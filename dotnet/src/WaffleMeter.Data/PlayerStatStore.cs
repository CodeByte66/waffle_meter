using WaffleMeter.Capture;

namespace WaffleMeter.Data;

/// <summary>The local player's stat sheet, as last broadcast by the server.</summary>
/// <param name="Values">stat id -&gt; raw signed value. Percent-ish ids are basis points — see
/// <see cref="PlayerStatIds.IsPercent"/>; the raw values are kept so an id we cannot name yet still survives
/// to the clipboard, where it can be compared against the in-game stat window later.</param>
/// <param name="UpdatedAt">Capture-clock ms of the most recent frame folded into this sheet.</param>
/// <param name="FullSnapshotSeen">
/// Whether a FULL sheet (0x3649) has been folded in, as opposed to only incremental updates. This matters for
/// honesty, not correctness: the server sends the full sheet on character load, and after that only what
/// changed. A meter started mid-session therefore holds a partial sheet, and telling the user "복사할 수
/// 있습니다" from a partial sheet would hand the calculator a form with holes in it that look like zeroes.
/// </param>
public sealed record PlayerStatSheet(
    IReadOnlyDictionary<int, int> Values,
    long UpdatedAt,
    bool FullSnapshotSeen)
{
    public int? Raw(int statId) => Values.TryGetValue(statId, out int v) ? v : null;

    /// <summary>A basis-point stat as a percentage (6613 → 66.13), or null when absent.</summary>
    public double? Percent(int statId) => Raw(statId) is { } v ? v / 100.0 : null;

    /// <summary>
    /// 인게임 스탯창의 <b>공격력</b>.
    /// <code>(기본 공격력 + 추가 공격력 + (무기 최소 + 무기 최대)/2) × (1 + 공격력 증가율)</code>
    /// <para>와이어에는 이 합계가 실리지 않는다 — 항을 따로 보내고 클라이언트가 합친다. 실측 대조(같은 시각
    /// 스탯창): <c>(3778 + 1083 + (973+1563)/2) × 2.2301 = 13,668.28</c> vs 스탯창 <b>13,668</b>.</para>
    /// <para>🔑 계산기에 넣어야 하는 값이 바로 이것이다. 항 하나(id 317)만 넣으면 3,778 이 되어 실제의 28%
    /// 수준이고, 계산기의 한계효율은 유한차분이라 분모가 작을수록 공격력 계열이 커 보인다 — 인게임에서는 결코
    /// 강타 1%를 못 넘는 공격력 옵션이 최상위로 뒤집힌다.</para>
    /// </summary>
    public double? AttackPower()
    {
        int? attack = Raw(PlayerStatIds.Attack);
        if (attack is null) return null;

        // 무기 최소/최대 중 하나만 와도 있는 쪽만 반영한다 — 없는 항을 0으로 두는 것이 합계를 0으로 만드는
        // 것보다 낫고, 실측상 둘은 항상 함께 온다.
        double weaponAverage =
            ((Raw(PlayerStatIds.MinimumAttack) ?? 0) + (Raw(PlayerStatIds.MaximumAttack) ?? 0)) / 2.0;
        double flat = attack.Value + (Raw(PlayerStatIds.AdditionalAttack) ?? 0) + weaponAverage;
        return flat * (1.0 + (Percent(PlayerStatIds.AttackIncreasePercent) ?? 0.0) / 100.0);
    }

    /// <summary>
    /// This sheet as the stat-shaped half of a <see cref="BuffGainContext"/> — the denominators an incoming
    /// buff's percentage points get divided by. The rate-shaped half (치명타/강타/완벽/전방 발동률) is NOT filled
    /// in here: those are measured per participant from their own damage packets, which is better evidence than
    /// any stat could be, and this sheet only ever describes the local player anyway.
    ///
    /// <para>Anything the sheet has not seen keeps <see cref="BuffGainContext.Default"/>'s value rather than
    /// falling to zero. A zero here does not mean "no bonus", it means "divide by 100", which is exactly the
    /// over-crediting this model exists to stop.</para>
    /// </summary>
    public BuffGainContext GainBaseline()
    {
        BuffGainContext ctx = BuffGainContext.Default;

        // 증폭 버킷은 데미지 공식과 같은 구성이다: 일반 + PvE + 보스. 무기 피해 증폭은 여기 들어가지 않는다 —
        // 그건 방어력 차감 앞에서 장비 유래 공격력에만 곱해지는 별도 항이다.
        double? amp = Percent(PlayerStatIds.DamageAmplifyPercent);
        double? pve = Percent(PlayerStatIds.PveDamageAmplifyPercent);
        double? boss = Percent(PlayerStatIds.BossDamageAmplifyPercent);
        if (amp is not null || pve is not null || boss is not null)
        {
            ctx = ctx with { AmpBucketPercent = (amp ?? 0.0) + (pve ?? 0.0) + (boss ?? 0.0) };
        }

        if (Percent(PlayerStatIds.AttackIncreasePercent) is { } inc)
        {
            ctx = ctx with { AttackIncreasePercent = inc };
        }

        if (Percent(PlayerStatIds.CriticalDamageAmplifyPercent) is { } critAmp)
        {
            ctx = ctx with { CritAmpPercent = critAmp };
        }

        if (Percent(PlayerStatIds.FrontDamageAmplifyPercent) is { } frontAmp)
        {
            ctx = ctx with { FrontAmpPercent = frontAmp };
        }

        // 완벽 보너스비 β: 완벽타는 `최대 공격력 + 0.32 × 구간폭`, 평타는 `공격력`이므로
        // β = (구간폭/2 + 0.32 × 구간폭) / 공격력 = 0.82 × 구간폭 / 공격력.
        // 증가율은 분자·분모에 똑같이 곱해져 약분되므로 원시 최소/최대와 평탄 합계로 계산한다.
        // 🔑 구간이 좁은 무기에서는 완벽이 거의 값을 못 갖는다 — 이 값이 상수일 수 없는 이유다.
        int? minAttack = Raw(PlayerStatIds.MinimumAttack);
        int? maxAttack = Raw(PlayerStatIds.MaximumAttack);
        int? attack = Raw(PlayerStatIds.Attack);
        if (minAttack is not null && maxAttack is not null && attack is not null)
        {
            double flat = attack.Value + (Raw(PlayerStatIds.AdditionalAttack) ?? 0)
                + (minAttack.Value + maxAttack.Value) / 2.0;
            double width = Math.Max(maxAttack.Value - minAttack.Value, 0);
            if (flat > 0.0)
            {
                ctx = ctx with { PerfectBonusRatio = 0.82 * width / flat };
            }
        }

        return ctx;
    }

    /// <summary>인게임 스탯창의 <b>방어력</b> = <c>(기본 방어력 + 방어구 방어력) × (1 + 방어력 증가율)</c>.
    /// 실측: <c>(10,666 + 16,393) × 1.24 = 33,553.16</c> vs 스탯창 <b>33,553</b>.</summary>
    public double? DefensePower()
    {
        int? defense = Raw(PlayerStatIds.Defense);
        if (defense is null) return null;

        double flat = defense.Value + (Raw(PlayerStatIds.ArmorDefense) ?? 0);
        return flat * (1.0 + (Percent(PlayerStatIds.DefenseIncreasePercent) ?? 0.0) / 100.0);
    }

    /// <summary>인게임 스탯창의 <b>명중</b> = <c>(기본 명중 + 무기 명중) × (1 + 명중 증가율)</c>.
    /// 실측: <c>(1,697 + 391) × 1.535 = 3,205.08</c> vs 스탯창 <b>3,205</b>.</summary>
    public double? AccuracyTotal()
    {
        int? accuracy = Raw(PlayerStatIds.Accuracy);
        if (accuracy is null) return null;

        double flat = accuracy.Value + (Raw(PlayerStatIds.WeaponAccuracy) ?? 0);
        return flat * (1.0 + (Percent(PlayerStatIds.AccuracyIncreasePercent) ?? 0.0) / 100.0);
    }

    /// <summary>
    /// 커뮤니티가 <b>'명중컷'</b>을 말할 때 쓰는 수치 = <c>스탯창 명중 + PvE 명중 + 막기 관통</c>.
    ///
    /// <para><see cref="AccuracyTotal"/>(스탯창 명중)만으로는 유저가 쓰는 어휘와 어긋난다 — 커뮤니티는 세 항을
    /// 더한 값을 기준으로 "이 던전은 명중 몇이면 안 막힌다"를 말한다. 통계웹이 발행하는 명중컷도 이 단위로
    /// 표시되므로, 미터가 같은 합을 보여 줘야 유저가 자기 화면과 발행값을 직접 비교할 수 있다.</para>
    ///
    /// <para>⚠️ 이건 <b>표시 단위</b>이지 물리 단위가 아니다. 패킷 실측에서 막기 발동률을 실제로 움직인 것은
    /// 추가 명중(104) 하나뿐이고, 나머지 항(무기 명중·명중 증가율·PvE 명중·막기 관통)은 한 캐릭터 안에서 상수라
    /// 효과의 유무를 로컬 데이터로는 가릴 수 없었다 — 컷 값이 그 상수를 통째로 흡수한다. 그래서 추정·저장은
    /// 원시 104 단위로 하고, 이 합은 화면에 보여 줄 때만 쓴다. (반증된 항은 철벽 관통 449 하나다.)</para>
    ///
    /// <para>⚠️ 버프·도핑이 반영된 값이다 — 스탯 사전은 버프 포함 최종치를 싣는다. 마을에서 본 무버프 값과
    /// 전투 중 값은 다르다.</para>
    /// </summary>
    public double? AccuracyCutTotal()
    {
        if (AccuracyTotal() is not { } accuracy) return null;
        return accuracy + (Raw(PlayerStatIds.PveAccuracy) ?? 0) + (Raw(PlayerStatIds.BlockPierce) ?? 0);
    }

    /// <summary>인게임 스탯창의 <b>치명타</b> = <c>기본 치명타 × (1 + 치명타 증가율)</c>. 명중·공격력과 달리
    /// 무기 몫이 따로 없다. 실측: <c>2,278 × 1.595 = 3,633.41</c> vs 스탯창 <b>3,633</b>.</summary>
    public double? CriticalTotal()
    {
        int? critical = Raw(PlayerStatIds.Critical);
        if (critical is null) return null;

        return critical.Value * (1.0 + (Percent(PlayerStatIds.CriticalIncreasePercent) ?? 0.0) / 100.0);
    }

    /// <summary>쿨타임 감소(%). 서버는 두 항목(기본 + 추가)에 나눠 "감소량"을 양수로 싣는다 — 사람이 읽는 쪽에서
    /// 자연스러운 음수로 뒤집어 돌려준다. 둘 다 없으면 null.</summary>
    public double? CooldownPercent()
    {
        int? baseValue = Raw(PlayerStatIds.CooldownBasePercent);
        int? bonus = Raw(PlayerStatIds.CooldownBonusPercent);
        if (baseValue is null && bonus is null) return null;
        return -((baseValue ?? 0) + (bonus ?? 0)) / 100.0;
    }
}

/// <summary>
/// Accumulates the character stat dictionary the server broadcasts (0x364A deltas / 0x3649 full snapshots)
/// for the LOCAL player only.
///
/// <para><b>Why it buffers.</b> The stat frames arrive BEFORE the packet that says which entity is us — the
/// own-load broadcast is a single easily-lost packet and, measured on a live capture, the stat sheet leads it
/// by about six seconds. Dropping everything that arrives while the executor is unknown would mean the sheet
/// is only ever complete on a lucky login. So frames for not-yet-identified entities are held, and replayed
/// once the executor is confirmed. The hold is bounded by count and age so a busy zone cannot grow it without
/// limit — every nearby player's incremental updates come through the same opcode.</para>
///
/// <para><b>What it does not do.</b> It never guesses which entity is the player. Only an entity the identity
/// layer confirms is promoted, so a stranger's stat frame can never become "your stats" — the failure mode
/// that a garbage-identity path caused before (see the executor-hijack fix).</para>
/// </summary>
public sealed class PlayerStatStore
{
    /// <summary>How many unidentified entities to hold stats for. A party/raid plus a few strangers; past this
    /// the oldest is dropped. Generous enough that the local player is never the one evicted in practice.</summary>
    private const int MaxPendingEntities = 16;

    /// <summary>How long an unidentified entity's stats are worth keeping. The measured lead is ~6 s; a minute
    /// covers a slow zone load without holding a whole session's strangers.</summary>
    private const long PendingTtlMs = 60_000L;

    private readonly object _gate = new();
    private readonly Dictionary<int, int> _values = new();
    private readonly Dictionary<int, (Dictionary<int, int> Values, long At, bool Full)> _pending = new();

    private int _ownerId;
    private long _updatedAt;
    private bool _fullSeen;

    /// <summary>Raised when the local player's sheet changed, so a settings screen can refresh.</summary>
    public event Action? Changed;

    /// <summary>The local player's sheet, or null when nothing has been captured for them yet.</summary>
    public PlayerStatSheet? Current
    {
        get
        {
            lock (_gate)
            {
                return _values.Count == 0
                    ? null
                    : new PlayerStatSheet(new Dictionary<int, int>(_values), _updatedAt, _fullSeen);
            }
        }
    }

    /// <summary>
    /// The four judgment stats (강타 443 · 완벽 442 · 추가 명중 104 · 치명타 128/429) as one value-typed
    /// reading, for stamping onto a damage packet on the capture consumer thread.
    ///
    /// <para><b>Why not <see cref="Current"/>.</b> That getter deep-copies the whole dictionary (~85-112
    /// entries, 1.5-2 KB) inside the lock on every call. Stamping every own hit through it would put
    /// 20-40 KB/s of gen0 churn on the damage path for data we throw away immediately. This reads five entries
    /// and allocates nothing.</para>
    ///
    /// <para>Returns <c>default</c> (mask 0 = not stamped) when nothing has been captured yet. Missing
    /// individual stats leave their presence bit clear rather than reporting 0 — see
    /// <see cref="JudgmentStatStamp"/>.</para>
    /// </summary>
    public JudgmentStatStamp JudgmentStats()
    {
        lock (_gate)
        {
            if (_values.Count == 0) return default;

            int mask = 0;
            if (_values.TryGetValue(PlayerStatIds.HardHitPercent, out int hardHit)) mask |= JudgmentStatStamp.HasHardHit;
            if (_values.TryGetValue(PlayerStatIds.PerfectPercent, out int perfect)) mask |= JudgmentStatStamp.HasPerfect;
            if (_values.TryGetValue(PlayerStatIds.Accuracy, out int accuracy)) mask |= JudgmentStatStamp.HasAccuracy;
            if (_values.TryGetValue(PlayerStatIds.Critical, out int critical)) mask |= JudgmentStatStamp.HasCritical;
            if (_values.TryGetValue(PlayerStatIds.CriticalIncreasePercent, out int criticalInc)) mask |= JudgmentStatStamp.HasCriticalInc;

            return new JudgmentStatStamp(mask, hardHit, perfect, accuracy, critical, criticalInc, _updatedAt);
        }
    }

    /// <summary>Fold one captured frame in. <paramref name="entityId"/> 0 means the frame carried no entity —
    /// a full snapshot, which is the local player's by construction, but only usable once we know who that is.</summary>
    public void Accept(int entityId, IReadOnlyList<(int Stat, int Value)> stats, bool fullSnapshot, long arrivedAt)
    {
        if (stats.Count == 0) return;

        bool changed = false;
        lock (_gate)
        {
            // A full snapshot with no entity id belongs to whoever we currently believe we are. If that is not
            // known yet, park it under a reserved key so it is replayed on confirmation like any other.
            int key = entityId != 0 ? entityId : _ownerId;

            if (_ownerId != 0 && key == _ownerId)
            {
                Apply(stats, fullSnapshot, arrivedAt);
                changed = true;
            }
            else
            {
                Park(entityId, stats, fullSnapshot, arrivedAt);
            }
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Tell the store who the local player is. Anything parked for that entity — plus any entity-less full
    /// snapshot — is replayed, oldest bucket first so the newer one wins.
    /// <para><paramref name="resetSheet"/> must be true ONLY when a DIFFERENT CHARACTER took over, never merely
    /// because the uid changed: the local player is re-registered under a fresh uid on every zone/instance
    /// load, and clearing on uid would wipe the sheet at every loading screen — including the full snapshot
    /// that arrives during exactly that load, which is the only time the game sends one.</para>
    /// </summary>
    public void SetOwner(int ownerId, bool resetSheet)
    {
        if (ownerId <= 0) return;

        bool changed = false;
        lock (_gate)
        {
            if (resetSheet && _values.Count > 0)
            {
                _values.Clear();
                _fullSeen = false;
                _updatedAt = 0;
                changed = true;
            }

            _ownerId = ownerId;

            // Replay by ARRIVAL ORDER, not by a fixed bucket order: both the entity-less full snapshot (key 0)
            // and this uid's deltas are filled during the same pre-identity window, and whichever came last is
            // the truth. Applying a stale delta after a newer full snapshot would resurrect stats the snapshot
            // had just dropped.
            List<int> keys = new[] { 0, ownerId }
                .Where(k => _pending.ContainsKey(k))
                .OrderBy(k => _pending[k].At)
                .ToList();

            foreach (int key in keys)
            {
                if (!_pending.Remove(key, out (Dictionary<int, int> Values, long At, bool Full) held)) continue;

                Apply(held.Values.Select(kv => (kv.Key, kv.Value)).ToList(), held.Full, held.At);
                changed = true;
            }

            Prune(_updatedAt);
        }

        if (changed) Changed?.Invoke();
    }

    private void Apply(IReadOnlyList<(int Stat, int Value)> stats, bool fullSnapshot, long arrivedAt)
    {
        // A full snapshot REPLACES: it is the whole sheet, so a stat that disappeared (an unequipped item's
        // bonus) has to disappear here too. A delta merges.
        if (fullSnapshot)
        {
            _values.Clear();
            _fullSeen = true;
        }

        foreach ((int stat, int value) in stats)
        {
            _values[stat] = value;
        }

        if (arrivedAt > _updatedAt) _updatedAt = arrivedAt;
    }

    private void Park(int entityId, IReadOnlyList<(int Stat, int Value)> stats, bool fullSnapshot, long arrivedAt)
    {
        if (!_pending.TryGetValue(entityId, out (Dictionary<int, int> Values, long At, bool Full) held))
        {
            held = (new Dictionary<int, int>(), arrivedAt, false);
        }

        if (fullSnapshot) held.Values.Clear();
        foreach ((int stat, int value) in stats)
        {
            held.Values[stat] = value;
        }

        _pending[entityId] = (held.Values, arrivedAt, held.Full || fullSnapshot);
        Prune(arrivedAt);
    }

    private void Prune(long now)
    {
        foreach (int key in _pending
                     .Where(kv => now - kv.Value.At > PendingTtlMs)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            _pending.Remove(key);
        }

        // 본인 후보 두 자리(엔티티 없는 전체 스냅샷 key 0, 그리고 현재 주인 후보)는 절대 먼저 버리지 않는다.
        // 주변 플레이어들의 변경분은 프레임마다 At을 갱신해 항상 "최신"이 되므로, 순수 오래된 순 축출은
        // 구조적으로 한 번만 오는 전체 스냅샷을 먼저 버린다 — 세션에서 유일한 그 프레임을.
        while (_pending.Count > MaxPendingEntities)
        {
            int? oldest = _pending
                .Where(kv => kv.Key != 0 && kv.Key != _ownerId)
                .OrderBy(kv => kv.Value.At)
                .Select(kv => (int?)kv.Key)
                .FirstOrDefault();

            if (oldest is null) break; // 남은 게 보호 대상뿐이면 더 줄이지 않는다
            _pending.Remove(oldest.Value);
        }
    }
}
