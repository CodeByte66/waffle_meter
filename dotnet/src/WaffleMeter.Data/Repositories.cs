using WaffleMeter.Capture;

namespace WaffleMeter.Data;

/// <summary>A read window over a target's packet ring buffer (Kotlin PacketRepository.PacketWindow).</summary>
public sealed record PacketWindow(
    IReadOnlyList<ParsedDamagePacket> Packets, long NextSequence, bool DroppedBeforeStart, int TotalSize);

/// <summary>
/// Verbatim port of Kotlin PacketRepository: per-target ring buffer of damage packets (cap 150k,
/// initial 1024, x2 growth, overwrite-oldest when full) + the current-target/battle-time state.
/// </summary>
public sealed class PacketRepository
{
    public const int MaxPacketsPerTarget = 150_000;
    private const int InitialBufferCapacity = 1_024;

    private sealed class RingBuffer(int maxCapacity)
    {
        private ParsedDamagePacket?[] _buffer = new ParsedDamagePacket?[Math.Min(InitialBufferCapacity, maxCapacity)];
        private int _start;
        private int _size;
        private long _totalAdded;

        public void Add(ParsedDamagePacket packet)
        {
            EnsureCapacityForAppend();
            if (_size < _buffer.Length)
            {
                _buffer[(_start + _size) % _buffer.Length] = packet;
                _size++;
            }
            else
            {
                _buffer[_start] = packet;
                _start = (_start + 1) % _buffer.Length;
            }

            _totalAdded++;
        }

        /// <summary>앞쪽(가장 오래된 쪽)에서 <paramref name="cutoff"/>보다 오래된 패킷을 떼어 낸다.
        /// 반환값은 "이제 비었는가". <c>_totalAdded</c>는 건드리지 않는다 — 시퀀스는 통짜 카운터라
        /// 여기서 줄이면 <see cref="WindowFrom"/>의 firstSequence 계산이 어긋나 읽는 쪽이 같은 패킷을
        /// 두 번 누적한다.</summary>
        public bool DropOlderThan(long cutoff)
        {
            while (_size > 0 && ElementAtOffset(0).Timestamp < cutoff)
            {
                _buffer[_start] = null;
                _start = (_start + 1) % _buffer.Length;
                _size--;
            }

            return _size == 0;
        }

        public List<ParsedDamagePacket> Snapshot()
        {
            var result = new List<ParsedDamagePacket>(_size);
            for (int i = 0; i < _size; i++)
            {
                result.Add(ElementAtOffset(i));
            }

            return result;
        }

        public PacketWindow WindowFrom(long sequence)
        {
            long firstSequence = _totalAdded - _size;
            long safeSequence = Math.Min(Math.Max(sequence, firstSequence), _totalAdded);
            int count = (int)(_totalAdded - safeSequence);
            var result = new List<ParsedDamagePacket>(count);
            int firstOffset = (int)(safeSequence - firstSequence);
            for (int i = 0; i < count; i++)
            {
                result.Add(ElementAtOffset(firstOffset + i));
            }

            return new PacketWindow(result, _totalAdded, sequence < firstSequence, _size);
        }

        private void EnsureCapacityForAppend()
        {
            if (_size < _buffer.Length || _buffer.Length >= maxCapacity)
            {
                return;
            }

            int newCapacity = Math.Min(_buffer.Length * 2, maxCapacity);
            var newBuffer = new ParsedDamagePacket?[newCapacity];
            for (int i = 0; i < _size; i++)
            {
                newBuffer[i] = ElementAtOffset(i);
            }

            _buffer = newBuffer;
            _start = 0;
        }

        private ParsedDamagePacket ElementAtOffset(int offset) => _buffer[(_start + offset) % _buffer.Length]!;
    }

    /// <summary>진행 중인 전투가 없을 때 링버퍼가 들고 있을 과거의 폭.
    /// <para>⚠️ 60초(<c>DataManager.PendingStartTtlMs</c>)보다 <b>반드시 커야 한다</b>. 시작 토글이 mobCode
    /// 미해결로 거부됐다가 스폰이 늦게 도착해 되살아나는 경로(<c>PromoteUnresolvedStart</c>)는 전투 시작을
    /// <b>원래 토글 시각</b>으로 back-date 하는데, 그 사이 파티 전체의 피해가 여기 남아 있어야 분자가
    /// 채워진다. 짧게 줄이면 "분모는 길고 분자는 빈 전투"가 저장·업로드된다.</para></summary>
    private const long IdleRetentionMs = 75_000L;

    /// <summary>진행 중인 전투 창의 시작보다 이만큼 앞은 정리에서 뺀다. <c>DpsCalculator</c>의
    /// <c>PreemptivePacketWindowMs</c>와 같은 값이어야 오프너가 조회에는 잡히는데 정리에는 지워지는
    /// 어긋남이 안 생긴다(시전 저장소의 <c>PreemptiveCastWindowMs</c>와 같은 쌍).</summary>
    private const long OpenerWindowMs = 1_000L;

    /// <summary>몇 건마다 보존 정리를 돌릴지. 패킷마다 전 타깃을 훑으면 파서 경로에 비용이 붙는다.</summary>
    private const int SweepEvery = 2_048;

    private readonly Dictionary<int, RingBuffer> _storage = new();
    private int _currentTarget;
    private long _currentBattleStart;
    private long _currentBattleEnd;
    private int _sinceSweep;

    public void Save(ParsedDamagePacket pdp)
    {
        if (!_storage.TryGetValue(pdp.TargetId, out RingBuffer? ring))
        {
            ring = new RingBuffer(MaxPacketsPerTarget);
            _storage[pdp.TargetId] = ring;
        }

        ring.Add(pdp);

        // 유휴 중에도 잡몹 피해는 계속 들어오고, 그걸 치우는 자리는 여기뿐이다. 예전에는 대기 <b>틱마다</b>
        // DpsCalculator 가 FlushPacket() 으로 전 타깃 링버퍼를 통째로 비워서 이게 GC 노릇을 했는데, 그 통짜
        // 비우기가 다음 전투의 <b>오프너</b>(교전 토글보다 먼저 들어간 타격)까지 같이 지웠다 — ActivePacketCutoff
        // 가 admit 해도 이미 버려져 돌아오지 않는다. 그래서 정리는 시간 기준으로만 한다.
        // ⚠️ 이 스윕을 없애고 대기 틱 통짜 비우기로 되돌리면 오프너 누락(battle-lifecycle#3)이 재발한다.
        if (++_sinceSweep >= SweepEvery)
        {
            _sinceSweep = 0;
            PruneOlderThan(pdp.Timestamp - IdleRetentionMs);
        }
    }

    /// <summary><paramref name="cutoff"/>보다 오래된 패킷을 버리고, 비워진 타깃은 사전에서도 지운다
    /// (링 개수 자체가 필드에서 몇 시간 도는 동안 무제한으로 늘어나는 것을 막는다).
    /// <para>열려 있는 전투 창은 절대 건드리지 않는다 — 진행 중인 전투의 앞부분을 지우면 캐시를 0부터 다시
    /// 누적하는 읽기 쪽이 이미 잘린 창을 보게 된다. 그래서 cutoff 는 <c>CurrentBattleStart - </c>
    /// <see cref="OpenerWindowMs"/> 를 넘지 못한다.</para></summary>
    public void PruneOlderThan(long cutoff)
    {
        if (_currentBattleStart > 0L)
        {
            long floor = _currentBattleStart - OpenerWindowMs;
            if (cutoff > floor)
            {
                cutoff = floor;
            }
        }

        if (cutoff <= 0L)
        {
            return;
        }

        List<int>? emptied = null;
        foreach (KeyValuePair<int, RingBuffer> kv in _storage)
        {
            if (kv.Value.DropOlderThan(cutoff))
            {
                (emptied ??= []).Add(kv.Key);
            }
        }

        if (emptied == null)
        {
            return;
        }

        foreach (int key in emptied)
        {
            _storage.Remove(key);
        }
    }

    public List<ParsedDamagePacket>? Get(int id) => _storage.TryGetValue(id, out RingBuffer? r) ? r.Snapshot() : null;

    public PacketWindow GetWindow(int id, long sequence) =>
        _storage.TryGetValue(id, out RingBuffer? r) ? r.WindowFrom(sequence) : new PacketWindow([], sequence, false, 0);

    public bool Exist(int id) => _storage.ContainsKey(id);

    public void Flush()
    {
        _currentTarget = 0;
        _currentBattleStart = 0;
        _currentBattleEnd = 0;
        _storage.Clear();
    }

    public int CurrentTarget() => _currentTarget;

    public int CurrentTarget(int targetId)
    {
        int past = _currentTarget;
        _currentTarget = targetId;
        return past;
    }

    public void FlushBattleTime()
    {
        _currentBattleStart = 0;
        _currentBattleEnd = 0;
    }

    public long CurrentBattleStart() => _currentBattleStart;
    public long CurrentBattleEnd() => _currentBattleEnd;

    public void SaveCurrentBattleStart(long time)
    {
        _currentBattleStart = time;
        _currentBattleEnd = 0;
    }

    public void SaveCurrentBattleEnd(long time) => _currentBattleEnd = time;
}

/// <summary>Kotlin MobIdRepository: instanceId -> (mobCode, maxHp).</summary>
public sealed class MobIdRepository
{
    public sealed class MobInstance(int code, long maxHp = 0)
    {
        public int Code { get; } = code;
        // long이다 — 실측 최대 HP가 27억대(델트라스)라 int로는 21.47억에서 포화한다.
        public long MaxHp { get; set; } = maxHp;
    }

    private readonly Dictionary<int, MobInstance> _storage = new();

    public void Save(int key, int code)
    {
        long maxHp = _storage.TryGetValue(key, out MobInstance? existing) && existing.Code == code ? existing.MaxHp : 0;
        _storage[key] = new MobInstance(code, maxHp);
    }

    public bool SaveMaxHp(int key, long maxHp)
    {
        if (!_storage.TryGetValue(key, out MobInstance? instance))
        {
            return false;
        }

        if (maxHp > instance.MaxHp)
        {
            instance.MaxHp = maxHp;
        }

        return true;
    }

    public MobInstance? Get(int id) => _storage.TryGetValue(id, out MobInstance? m) ? m : null;
    public bool Exist(int id) => _storage.ContainsKey(id);
    public void Flush() => _storage.Clear();
}

/// <summary>Kotlin MobHpRepository: instanceId -> remaining HP.</summary>
public sealed class MobHpRepository
{
    private readonly Dictionary<int, long> _storage = new();
    public long? Get(int key) => _storage.TryGetValue(key, out long v) ? v : null;
    public void Set(int key, long value) => _storage[key] = value;
    public void Flush() => _storage.Clear();
}

/// <summary>Kotlin SummonRepository: summonId -> owner.</summary>
public sealed class SummonRepository
{
    private readonly Dictionary<int, int> _storage = new();
    public void Save(int summonId, int summonerId) => _storage[summonId] = summonerId;
    public int? Get(int summonId) => _storage.TryGetValue(summonId, out int v) ? v : null;
    public IReadOnlyDictionary<int, int> GetAll() => _storage;
    public void Flush() => _storage.Clear();
}

/// <summary>Kotlin UseBuffRepository: actor/target -> applied buff intervals.
/// <para><b>락이 있는 이유.</b> <see cref="SkillCastRepository"/>와 정확히 같다 — 쓰기는 캡처 소비자
/// 스레드 전용이지만 읽기는 아니다. 미터 행을 클릭하면 <c>new DetailsViewModel(...)</c> 생성자가
/// <b>UI 스레드에서</b> 곧바로 Refresh 를 돌리고(App.ToggleDetail), 그 경로는 리포트 틱의 블로킹
/// <c>Dispatcher.Invoke</c> 펜스 <b>밖</b>이라 파서가 그동안 계속 돈다. 목록 append 는 배열을 재할당할 수
/// 있고 <see cref="PruneBefore"/>/<see cref="Flush"/>는 Dictionary 를 구조적으로 바꾸므로, 그때 읽고 있으면
/// 열거가 예외로 끝난다("라이브 전투 중 행 클릭 → waffle_meter 오류" 대화상자의 가장 흔한 원인).
/// 락을 빼고 try/catch 로 덮으면 증상만 숨고 상세창은 그대로 안 열린다.</para></summary>
public sealed class UseBuffRepository
{
    private readonly Dictionary<int, List<UseBuff>> _storage = new();
    private readonly Lock _gate = new();

    public void Save(int id, UseBuff useBuff)
    {
        lock (_gate)
        {
            if (!_storage.TryGetValue(id, out List<UseBuff>? list))
            {
                list = [];
                _storage[id] = list;
            }

            list.Add(useBuff);
        }
    }

    public List<UseBuff> FindOverlapping(int id, long timestamp1, long timestamp2)
    {
        lock (_gate)
        {
            if (!_storage.TryGetValue(id, out List<UseBuff>? list))
            {
                return [];
            }

            return list.Where(b => b.BuffStart <= timestamp2 && b.BuffEnd >= timestamp1).ToList();
        }
    }

    /// <summary>
    /// 조기 해제(0x382C)를 반영해 <b>아직 열려 있는</b> 구간의 끝을 <paramref name="at"/>으로 끊는다.
    /// <para>이 저장소에는 여태 구간을 <b>줄이는</b> 연산이 없었다 — <see cref="Save"/>가 박아 넣은
    /// <c>BuffEnd = 적용시각 + 선언 duration</c>이 끝까지 불변이었고, <see cref="PruneBefore"/>는 이미 끝난
    /// 항목을 통째로 지울 뿐 잘라내지 않는다. 그래서 서버가 예상 만료보다 먼저 끊어도 가동률은 선언값대로
    /// 계속 셌다(0x382C로 끝난 인스턴스의 57.6%가 1초 이상 일찍 끊긴다는 실측).</para>
    /// <para>조건 셋이 전부 필요하다: <c>Slot != 0</c>(모르는 건 fail-open), <c>BuffStart &lt; at</c>(같은 슬롯에
    /// 방금 새로 걸린 구간을 0길이로 깎지 않는다), <c>BuffEnd &gt; at</c>(이미 끝난 것을 되살리거나 늘리지 않는다).
    /// 셋 중 하나라도 빼면 재적용 체인에서 잘못된 구간을 건드린다.</para>
    /// </summary>
    public void TruncateOpenSlots(int id, IReadOnlyList<int> slots, long at)
    {
        lock (_gate)
        {
            if (!_storage.TryGetValue(id, out List<UseBuff>? list))
            {
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                UseBuff b = list[i];
                if (b.Slot != 0 && b.BuffStart < at && b.BuffEnd > at && slots.Contains(b.Slot))
                {
                    // Duration은 일부러 그대로 둔다 — "서버가 선언한 길이"는 그 자체로 기록이고, 가동률 경로는
                    // BuffStart/BuffEnd만 읽는다(CoveredMs·MergeIntervals·FindOverlapping 전수 확인).
                    list[i] = b with { BuffEnd = at };
                }
            }
        }
    }

    public void PruneBefore(long timestamp)
    {
        lock (_gate)
        {
            foreach (int key in _storage.Keys.ToList())
            {
                List<UseBuff> buffs = _storage[key];
                buffs.RemoveAll(b => b.BuffEnd < timestamp);
                if (buffs.Count == 0)
                {
                    _storage.Remove(key);
                }
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _storage.Clear();
        }
    }
}

/// <summary>
/// 액터별 스킬 시전 이력(0x3802). <see cref="UseBuffRepository"/>와 같은 모양이되, 상한이 있다.
/// <para>상한에 닿으면 <b>가장 오래된 것을 덮어쓴다</b>(<c>PacketRepository</c>의 링버퍼와 같은 규칙).
/// "가득 차면 새 것을 버린다"로 만들면, 전투 밖에서 상한만큼 쌓인 뒤 정작 다음 전투가 통째로 기록되지 않는다 —
/// 정리(<see cref="PruneBefore"/>)는 전투가 저장될 때만 돌기 때문이다.</para>
/// <para><b>락이 있는 이유.</b> 쓰기는 소비자 스레드 전용이지만 읽기는 아니다 — 미터 행을 클릭하면
/// <c>new DetailsViewModel(...)</c> 의 생성자가 <b>UI 스레드에서</b> 곧바로 Refresh 를 돌린다(App.ToggleDetail).
/// 그 경로는 리포트 틱의 블로킹 <c>Dispatcher.Invoke</c> 밖이라 파서가 그동안 계속 돈다. 목록 append 는 배열을
/// 재할당할 수 있고 정리는 Dictionary 를 구조적으로 바꾸므로, 그때 읽고 있으면 예외가 난다.</para>
/// </summary>
public sealed class SkillCastRepository
{
    /// <summary>한 액터가 들고 있을 수 있는 최대 시전 수. 레코드가 16바이트라 액터당 320KB 상한이고,
    /// 20분 전투에서 초당 1시전이어도 1,200건이라 넉넉하다.</summary>
    public const int MaxCastsPerActor = 20_000;

    /// <summary>액터 하나를 얼마나 오래 들고 있을지. 정리(<see cref="PruneBefore"/>)는 전투가 <b>저장될 때만</b>
    /// 도는데, 필드에서 몇 시간을 돌아다니는 동안에는 저장되는 전투가 하나도 없을 수 있다. 그동안 이 저장소는
    /// 지나가는 모든 플레이어의 시전을 액터 키로 쌓는다 — 액터당 상한은 있어도 <b>액터 수</b>에는 없다.
    /// 그래서 시간 기준으로도 잘라 낸다. ⚠️ 10분은 <b>긴 전투보다 짧다</b>(공대·시련은 그 이상 간다) — 그래서
    /// <see cref="Save"/>의 <c>keepFromMs</c>가 열린 전투 창을 정리에서 빼 준다. 이 둘은 한 쌍이다.</summary>
    private const long RetentionMs = 10 * 60 * 1000L;

    /// <summary>몇 건마다 위 보존 정리를 돌릴지. 매 시전마다 전 액터를 훑으면 파서 경로에 비용이 붙는다.</summary>
    private const int SweepEvery = 4096;

    private readonly Dictionary<int, List<SkillCast>> _storage = new();
    private readonly Lock _gate = new();
    private int _sinceSweep;

    /// <param name="keepFromMs">이 시각 이후는 보존 정리가 절대 건드리지 않는다 — 열려 있는 전투 창의 시작을
    /// 넘긴다. 이게 없으면 10분을 넘기는 전투(공대·시련)에서 정리가 <b>진행 중인 그 전투의 앞부분</b>을 지워,
    /// 얼려 둔 타임라인이 이미 잘린 채로 저장된다. 열린 전투가 없으면 <see cref="long.MaxValue"/>.</param>
    public void Save(int actorId, SkillCast cast, long keepFromMs = long.MaxValue)
    {
        lock (_gate)
        {
            if (!_storage.TryGetValue(actorId, out List<SkillCast>? list))
            {
                list = [];
                _storage[actorId] = list;
            }

            if (list.Count >= MaxCastsPerActor)
            {
                list.RemoveAt(0); // overwrite-oldest
            }

            list.Add(cast);

            if (++_sinceSweep >= SweepEvery)
            {
                _sinceSweep = 0;
                PruneBefore(Math.Min(cast.TimestampMs - RetentionMs, keepFromMs));
            }
        }
    }

    /// <summary>[start, end] 창에 든 시전들, 저장 순서 그대로(= 시각 오름차순).</summary>
    public List<SkillCast> FindInWindow(int actorId, long start, long end)
    {
        lock (_gate)
        {
            if (!_storage.TryGetValue(actorId, out List<SkillCast>? list))
            {
                return [];
            }

            return list.Where(c => c.TimestampMs >= start && c.TimestampMs <= end).ToList();
        }
    }

    public void PruneBefore(long timestamp)
    {
        lock (_gate)
        {
            foreach (int key in _storage.Keys.ToList())
            {
                List<SkillCast> casts = _storage[key];
                casts.RemoveAll(c => c.TimestampMs < timestamp);
                if (casts.Count == 0)
                {
                    _storage.Remove(key);
                }
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _storage.Clear();
        }
    }
}

/// <summary>Skill catalog (Kotlin SkillRepository): code -> Skill (with name).</summary>
public sealed class SkillRepository
{
    private readonly Dictionary<long, Skill> _storage = new();
    public void Save(long key, Skill value) => _storage[key] = value;
    public Skill? Get(long key) => _storage.TryGetValue(key, out Skill? s) ? s : null;
    public bool Exist(long key) => _storage.ContainsKey(key);
}

/// <summary>Buff catalog (Kotlin BuffRepository): code -> Buff.</summary>
public sealed class BuffRepository
{
    private readonly Dictionary<int, Buff> _storage = new();
    public void Save(Buff value) => _storage[value.Code] = value;
    public Buff? Get(int code) => _storage.TryGetValue(code, out Buff? b) ? b : null;
}
