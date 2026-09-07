namespace WaffleMeter.Data;

/// <summary>
/// Ported from Kotlin BattleLogRepository: a bounded history of saved battles (cap 30 — the history panel
/// shows them newest-first in a scrollable list). A new log that matches an existing battle (same target id
/// + mob code, within a 120s gap) replaces it with the "preferred" of the two (higher damage, but keep the
/// existing one if the new is a longer idle-extended run with ~no extra damage). Order preserved (oldest first).
/// <para><b>허수아비 런은 두 규칙에서 모두 예외다.</b> ① 병합하지 않는다 — 허수아비는 인스턴스 id 도 몹 코드도
/// 고정이라 연속 측정이 전부 "같은 전투"로 판정돼 한 줄로 뭉개진다. 연습 3회는 기록 3줄이어야 한다.
/// ② 정원을 따로 센다 — 공유하면 허수아비 30번이 그날의 보스 전투 기록을 통째로 밀어낸다.</para>
/// </summary>
public sealed class BattleLogRepository
{
    private const long SameBattleMergeWindowMs = 120_000L;
    private const long IdleExtensionGraceMs = 30_000L;
    /// <summary>Per-kind cap: 30 real battles AND 30 dummy runs, counted separately.</summary>
    private const int MaxSize = 30;

    private readonly List<DpsLog> _storage = [];

    public void Save(DpsLog data)
    {
        int existingIndex = _storage.FindLastIndex(it => IsSameBattle(it, data));
        if (existingIndex >= 0)
        {
            _storage[existingIndex] = SelectPreferred(_storage[existingIndex], data);
            return;
        }

        // 정원은 종류별로 센다 — 허수아비 연습이 보스 전투 기록을 밀어내지 않도록. 밀어내는 것도
        // 같은 종류 중 가장 오래된 것 하나다.
        bool incomingIsDummy = IsDummy(data);
        while (_storage.Count(l => IsDummy(l) == incomingIsDummy) >= MaxSize)
        {
            int oldest = _storage.FindIndex(l => IsDummy(l) == incomingIsDummy);
            if (oldest < 0)
            {
                break;
            }

            _storage.RemoveAt(oldest);
        }

        _storage.Add(data);
    }

    private static bool IsDummy(DpsLog log) => log.Report.Target?.Mob.IsDummy == true;

    public DpsLog? Get(int idx) => idx >= 0 && idx < _storage.Count ? _storage[idx] : null;

    public IReadOnlyList<DpsLog> GetAll() => _storage;

    public void Flush() => _storage.Clear();

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
