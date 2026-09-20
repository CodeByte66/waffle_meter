namespace WaffleMeter.Data;

/// <summary>
/// Ported from Kotlin BattleLogRepository: a bounded history of saved battles (cap 30 — the history panel
/// shows them newest-first in a scrollable list). A new log that matches an existing battle (same target id
/// + mob code, within a 120s gap) replaces it with the "preferred" of the two (higher damage, but keep the
/// existing one if the new is a longer idle-extended run with ~no extra damage). Order preserved (oldest first).
/// <para><b>허수아비 런은 두 규칙에서 모두 예외다.</b> ① 병합하지 않는다 — 허수아비는 인스턴스 id 도 몹 코드도
/// 고정이라 연속 측정이 전부 "같은 전투"로 판정돼 한 줄로 뭉개진다. 연습 3회는 기록 3줄이어야 한다.
/// ② 정원을 따로 센다 — 공유하면 허수아비 30번이 그날의 보스 전투 기록을 통째로 밀어낸다.</para>
/// <para><b>디스크는 이 목록의 거울이다.</b> <see cref="AttachStore"/> 로 붙여 두면 저장·병합·정원 초과·
/// 초기화가 그대로 파일에 반영돼, 앞으로는 앱을 다시 켜도 기록이 남는다. 붙이지 않으면 종전과 같은 메모리 전용이다.</para>
/// </summary>
public sealed class BattleLogRepository
{
    private const long SameBattleMergeWindowMs = 120_000L;
    private const long IdleExtensionGraceMs = 30_000L;
    /// <summary>Per-kind cap: 30 real battles AND 30 dummy runs, counted separately.</summary>
    private const int MaxSize = 30;

    /// <summary>한 칸 = 기록 한 건 + 그 건을 담은 파일 이름(<see cref="BattleHistoryStore"/>). 번호는 저장 순서대로
    /// 커지며, 정원 초과로 밀려난 번호는 다시 쓰지 않는다.</summary>
    private readonly record struct Entry(long Id, DpsLog Log);

    private readonly List<Entry> _storage = [];

    private BattleHistoryStore? _store;
    private long _nextId = 1;

    /// <summary>
    /// 디스크 영속화를 붙이고, 이미 남아 있는 기록을 메모리로 복원한다. 기동 때 한 번 부른다.
    /// <para>복원 직후 <b>지금 규칙으로</b> 정원을 다시 잰다 — 상한이 줄어든 빌드로 내려가거나,
    /// 종료 직전에 쓰기가 엇나가 파일이 남을 수 있다. 복원본을 먼저 정리해 두어야 화면과 파일이 같아진다.</para>
    /// </summary>
    public void AttachStore(BattleHistoryStore store)
    {
        _store = store;
        foreach ((long id, DpsLog log) in store.Load())
        {
            _storage.Add(new Entry(id, log));
            _nextId = Math.Max(_nextId, id + 1);
        }

        TrimToCap(dummy: false);
        TrimToCap(dummy: true);
    }

    public void Save(DpsLog data)
    {
        int existingIndex = _storage.FindLastIndex(it => IsSameBattle(it.Log, data));
        if (existingIndex >= 0)
        {
            Entry existing = _storage[existingIndex];
            DpsLog preferred = SelectPreferred(existing.Log, data);
            if (ReferenceEquals(preferred, existing.Log))
            {
                return; // 기존 것을 지키기로 했다 — 디스크에도 같은 것이 이미 있다.
            }

            _storage[existingIndex] = existing with { Log = preferred };
            _store?.Write(existing.Id, preferred);
            return;
        }

        // 정원은 종류별로 센다 — 허수아비 연습이 보스 전투 기록을 밀어내지 않도록. 밀어내는 것도
        // 같은 종류 중 가장 오래된 것 하나다.
        bool incomingIsDummy = IsDummy(data);
        TrimToCap(incomingIsDummy, MaxSize - 1);

        var entry = new Entry(_nextId++, data);
        _storage.Add(entry);
        _store?.Write(entry.Id, data);
    }

    /// <summary>한 종류의 기록을 <paramref name="keep"/>건까지 줄인다. 밀려난 건은 파일도 같이 사라진다.</summary>
    private void TrimToCap(bool dummy, int? keep = null)
    {
        int limit = keep ?? MaxSize;
        while (_storage.Count(e => IsDummy(e.Log) == dummy) > limit)
        {
            int oldest = _storage.FindIndex(e => IsDummy(e.Log) == dummy);
            if (oldest < 0)
            {
                break;
            }

            _store?.Delete(_storage[oldest].Id);
            _storage.RemoveAt(oldest);
        }
    }

    private static bool IsDummy(DpsLog log) => log.Report.Target?.Mob.IsDummy == true;

    public DpsLog? Get(int idx) => idx >= 0 && idx < _storage.Count ? _storage[idx].Log : null;

    public IReadOnlyList<DpsLog> GetAll() => _storage.Select(e => e.Log).ToList();

    public void Flush()
    {
        _storage.Clear();
        _store?.Clear();
    }

    private static bool IsSameBattle(DpsLog a, DpsLog b)
    {
        MobInfo? aTarget = a.Report.Target;
        MobInfo? bTarget = b.Report.Target;
        if (aTarget == null || bTarget == null)
        {
            return false;
        }

        if (aTarget.Id != bTarget.Id)
        {
            return false;
        }

        // 허수아비는 인스턴스 id 와 몹 코드가 고정이라 아래 120초 규칙에 전부 걸린다 — 연속 측정 3회가
        // 한 줄로 뭉개진다. 허수아비 런은 언제나 서로 다른 전투다.
        if (aTarget.Mob.IsDummy || bTarget.Mob.IsDummy)
        {
            return false;
        }

        if (aTarget.Mob.Code != bTarget.Mob.Code)
        {
            return false;
        }

        if (a.Report.BattleStart <= 0L || b.Report.BattleStart <= 0L)
        {
            return false;
        }

        long aEnd = a.Report.BattleEnd >= a.Report.BattleStart ? a.Report.BattleEnd : a.Report.BattleStart;
        long bEnd = b.Report.BattleEnd >= b.Report.BattleStart ? b.Report.BattleEnd : b.Report.BattleStart;
        long gap = aEnd < b.Report.BattleStart ? b.Report.BattleStart - aEnd
            : bEnd < a.Report.BattleStart ? a.Report.BattleStart - bEnd
            : 0L;

        return gap <= SameBattleMergeWindowMs;
    }

    private static DpsLog SelectPreferred(DpsLog existing, DpsLog next)
    {
        double existingDamage = TotalDamage(existing);
        double nextDamage = TotalDamage(next);
        long existingDuration = Duration(existing);
        long nextDuration = Duration(next);

        if (nextDamage <= existingDamage * 1.01 && nextDuration > existingDuration + IdleExtensionGraceMs)
        {
            return existing;
        }

        return nextDamage + 0.001 >= existingDamage ? next : existing;
    }

    private static double TotalDamage(DpsLog log) => log.Report.Information.Values.Sum(i => i.Amount);

    private static long Duration(DpsLog log) => Math.Max(log.Report.BattleEnd - log.Report.BattleStart, 0L);
}
