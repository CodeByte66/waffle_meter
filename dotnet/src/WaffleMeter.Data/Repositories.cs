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

    private readonly Dictionary<int, RingBuffer> _storage = new();
    private int _currentTarget;
    private long _currentBattleStart;
    private long _currentBattleEnd;

    public void Save(ParsedDamagePacket pdp)
    {
        if (!_storage.TryGetValue(pdp.TargetId, out RingBuffer? ring))
        {
            ring = new RingBuffer(MaxPacketsPerTarget);
            _storage[pdp.TargetId] = ring;
        }

        ring.Add(pdp);
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

/// <summary>Kotlin UseBuffRepository: actor/target -> applied buff intervals.</summary>
public sealed class UseBuffRepository
{
    private readonly Dictionary<int, List<UseBuff>> _storage = new();

    public void Save(int id, UseBuff useBuff)
    {
        if (!_storage.TryGetValue(id, out List<UseBuff>? list))
        {
            list = [];
            _storage[id] = list;
        }

        list.Add(useBuff);
    }

    public List<UseBuff> FindOverlapping(int id, long timestamp1, long timestamp2)
    {
        if (!_storage.TryGetValue(id, out List<UseBuff>? list))
        {
            return [];
        }

        return list.Where(b => b.BuffStart <= timestamp2 && b.BuffEnd >= timestamp1).ToList();
    }

    public void PruneBefore(long timestamp)
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

    public void Flush() => _storage.Clear();
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
