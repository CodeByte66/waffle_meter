using WaffleMeter.Capture;

namespace WaffleMeter.Data;

/// <summary>
/// Verbatim port of Kotlin <c>DpsCalculator</c> (DpsCalculator.kt). Incremental, stateful: getDps()
/// is a battle state machine driven by DataManager's current-target/battle-revision and the
/// per-target packet ring buffer; it accumulates damage into cached per-player info + skill details,
/// and on a battle transition saves a frozen DpsLog (report + skill details + buff rates) to history.
///
/// Contributors keep insertion order with move-to-end on re-add (Kotlin LinkedHashSet semantics),
/// implemented here with a List + remove-by-id + append.
/// </summary>
public sealed class DpsCalculator
{
    private const long PreemptivePacketWindowMs = 1000L;

    private readonly DataManager _dm;
    private readonly Action? _streamResetCallback;

    private int _currentTarget;
    private long _currentBattleRevision;

    private DpsReport _recentData = new();
    private bool _recentDataSaved;
    private Dictionary<int, Dictionary<string, AnalyzedSkill>> _recentSkillDetails = new();
    private Dictionary<int, List<OperatingData>> _recentBuffRates = new();
    private List<OperatingData> _recentBossBuffRates = [];

    private long _lastProcessedSequence;
    private readonly Dictionary<int, DpsInformation> _cachedInfo = new();
    private readonly List<User> _cachedContributors = []; // ordered, move-to-end on re-add
    private long _cachedBattleEnd;
    private long _cachedBattleStart;
    private bool _isCachedBattleStartFake;
    private long _cachedBattleStartToggle; // the combat-enter toggle wall-clock at this battle's first damage
    private readonly Dictionary<int, Dictionary<string, AnalyzedSkill>> _cachedSkillDetails = new();
    // Per-second damage buckets (uid -> whole-second key (Timestamp/1000) -> damage that second), the raw
    // source for the combat-detail DPS graph. Keyed by ABSOLUTE second so it survives the reported BattleStart
    // being re-anchored (StartAnchor caps back-dating) — the dense per-report array is re-based at build time.
    private readonly Dictionary<int, Dictionary<long, long>> _cachedDpsBuckets = new();

    /// <summary><see cref="_cachedSkillDetails"/>·<see cref="_cachedDpsBuckets"/> 전용 게이트.
    /// <para><b>왜 필요한가.</b> 이 클래스는 캡처 소비자 스레드 전용이라는 계약(<c>MeterServices</c> 주석
    /// "the meter is not internally synchronized")으로 살아왔고, 리포트 틱은 블로킹
    /// <c>Dispatcher.Invoke</c> 펜스 안에서 돌아 실제로 안전하다. 그 계약을 깨는 경로가 딱 하나 있다 —
    /// 라이브 전투 중 미터 <b>행 클릭</b>: <c>new DetailsViewModel(...)</c> 생성자가 UI 스레드에서 곧바로
    /// <see cref="BattleDetails"/>/<see cref="GetDpsSeries"/> 를 부르는데 그건 펜스 밖이고, 그동안 파서는
    /// 계속 이 두 사전에 키를 꽂는다. 그러면 열거가 예외로 끝나고 게임 위에 오류 대화상자가 뜬다.
    /// <b>try/catch 로 덮는 것은 답이 아니다</b> — 증상만 숨고 상세창은 그대로 안 열린다.</para>
    /// <para>규칙: 이 두 사전은 <b>읽기·쓰기 모두</b> 이 락 안에서만 만진다. UI 쪽은 락 안에서 <b>복사</b>만
    /// 하고 나간다(소비자 스레드를 오래 잡지 않는다). <see cref="SkillCastRepository"/>·
    /// <see cref="UseBuffRepository"/>가 같은 이유로 같은 모양의 락을 갖는다.</para></summary>
    private readonly Lock _cacheGate = new();

    /// <summary>The uploader's own judgment cross-tabs for the battle in progress. Shares
    /// <see cref="_cachedSkillDetails"/>'s lifecycle exactly — see <see cref="SelfJudgmentAccumulator"/> for
    /// why any other clearing schedule is a silent bug.</summary>
    private readonly SelfJudgmentAccumulator _cachedJudgment = new();

    private static readonly string[] SummonDamageSkillPrefixes =
    [
        "불의 정령:",
        "물의 정령:",
        "바람의 정령:",
        "땅의 정령:",
        "고대의 정령:",
    ];

    public DpsCalculator(DataManager dm, Action? streamResetCallback = null)
    {
        _dm = dm;
        _streamResetCallback = streamResetCallback;
    }

    public DpsReport GetRecentData() => _recentData;

    private void ResetCache()
    {
        _lastProcessedSequence = 0L;
        _cachedInfo.Clear();
        _cachedContributors.Clear();
        lock (_cacheGate) // 구조적 변경 — 상세창 클릭이 같은 순간 열거하고 있을 수 있다(_cacheGate 주석)
        {
            _cachedSkillDetails.Clear();
            _cachedDpsBuckets.Clear();
        }

        _cachedJudgment.Clear();
        _cachedBattleEnd = 0L;
        _cachedBattleStart = 0L;
        _isCachedBattleStartFake = false;
        _cachedBattleStartToggle = 0L;
    }

    /// <summary>How far the reported battle start may sit BEFORE the combat-enter toggle. Pre-toggle openers
    /// are still counted (<see cref="ActivePacketCutoff"/> admits ~1 s of them); this only caps how far the
    /// clock is back-dated, which was the dominant source of the combat-time gap vs the in-game meter — our
    /// start could sit a full second before the toggle. Keep small: on a re-pull the first resumed damage is
    /// AFTER the toggle, so the floor never binds and the re-pull deflation fix is untouched.</summary>
    private const long StartBackdateLimitMs = 250L;

    /// <summary>The battle start to report: the first-damage timestamp, but never earlier than
    /// <see cref="StartBackdateLimitMs"/> before the combat-enter toggle. 0 when no damage yet.</summary>
    private long StartAnchor()
    {
        if (_cachedBattleStart == 0L)
        {
            return 0L;
        }

        long floor = _cachedBattleStartToggle > 0L ? _cachedBattleStartToggle - StartBackdateLimitMs : 0L;

        // The floor may only TRIM pre-toggle openers (the capture admits ~PreemptivePacketWindowMs of them),
        // never AMPUTATE the battle. On the end-of-battle full-ring rebuild the toggle snapshot can be a LATE
        // re-toggle (a corpse/phase restart overwrote CurrentBattleStart), and letting that floor bind collapsed a
        // whole fight's window to [lateToggle-250ms, lastHit] while Amount kept the full fight — the 191M-DPS
        // uploads of 2026-07-17. A legitimate opener floor sits at most ~750ms above the first counted hit, so a
        // floor beyond first-hit + the preemptive window can only mean the toggle does not belong to this window's
        // start: ignore it.
        if (floor > _cachedBattleStart + PreemptivePacketWindowMs)
        {
            return _cachedBattleStart;
        }

        return Math.Max(_cachedBattleStart, floor);
    }

    private bool IsSummonDamageSkill(int skillCode)
    {
        string? skillName = _dm.Skill(skillCode)?.Name;
        if (skillName == null) return false;
        return SummonDamageSkillPrefixes.Any(p => skillName.StartsWith(p, StringComparison.Ordinal));
    }

    private int? ResolveActor(ParsedDamagePacket packet, ICollection<User> candidates)
    {
        // Fold a summon's damage into its summoner — BUT never when the actor is itself a known player.
        // The summon map persists across battles (only HardReset flushes it) and entity ids get reused,
        // so a stale summonId->owner mapping could otherwise steal a FOREIGN player's skill (e.g. a 검성's
        // 흡혈의 검) into another player's breakdown. A real player is never a summon, so attribute a known
        // user's damage to that user directly. (Hardening over the original Kotlin's unconditional fold.)
        int? summoner = _dm.SummonerId(packet.ActorId);
        if (summoner != null && _dm.User(packet.ActorId) == null
            && (ContainsUid(candidates, summoner.Value) || IsFreshPartyMember(summoner.Value)))
        {
            // Bug 1: fold a summon into its owner ONLY when the owner is attributable to THIS fight — a current
            // damaging contributor, or a fresh (0x9702) party member. At a shared field boss the client also
            // receives non-party players' summons, whose owner id is a STALE known user (last seen in an earlier
            // scene); the old unconditional fold surfaced that stranger as a phantom low-DPS row. An un-attributable
            // owner leaves the summon unfolded → it drops as a mob-instance / stays a trace row the filter hides.
            return summoner;
        }

        if (IsSummonDamageSkill(packet.SkillCode))
        {
            List<int> elementalists = candidates
                .Where(u => u.Job == JobClass.ELEMENTALIST)
                .Select(u => u.Id)
                .Distinct()
                .ToList();
            return elementalists.Count == 1 ? elementalists[0] : null;
        }

        if (_dm.IsMobInstance(packet.ActorId)) return null;
        return packet.ActorId;
    }

    private static bool ContainsUid(ICollection<User> candidates, int uid)
    {
        foreach (User c in candidates)
        {
            if (c.Id == uid)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="uid"/> resolves to a member of the current (0x9702) party roster —
    /// by the same (nickname, server) identity the roster carries. Used to keep a summon fold attributable even
    /// when the owner has not yet dealt DIRECT damage (so is not yet a contributor).</summary>
    private bool IsFreshPartyMember(int uid)
    {
        User? u = _dm.User(uid);
        if (u?.Nickname is not { Length: > 0 })
        {
            return false;
        }

        foreach ((string nick, int server) in _dm.PartyMemberIdentities(RosterRecoveryWindowMs))
        {
            if (server == u.Server && string.Equals(nick, u.Nickname, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>산 <see cref="_cachedSkillDetails"/>의 <b>구조만</b> 복사한 스냅샷(안쪽 <see cref="AnalyzedSkill"/>
    /// 는 공유 참조). 열거 안전성이 목적이므로 이 정도면 충분하다 — 값이 한 틱 찢어지는 것은 다음 틱에 덮이지만
    /// 열거 중 예외는 상세창이 아예 안 열린다. 값까지 얼려야 하는 저장 경로는 <see cref="CloneSkillDetails"/>.</summary>
    private Dictionary<int, Dictionary<string, AnalyzedSkill>> SnapshotSkillDetails()
    {
        lock (_cacheGate)
        {
            var copy = new Dictionary<int, Dictionary<string, AnalyzedSkill>>(_cachedSkillDetails.Count);
            foreach (KeyValuePair<int, Dictionary<string, AnalyzedSkill>> kv in _cachedSkillDetails)
            {
                copy[kv.Key] = new Dictionary<string, AnalyzedSkill>(kv.Value);
            }

            return copy;
        }
    }

    private static Dictionary<int, Dictionary<string, AnalyzedSkill>> CloneSkillDetails(
        Dictionary<int, Dictionary<string, AnalyzedSkill>> source)
    {
        var result = new Dictionary<int, Dictionary<string, AnalyzedSkill>>();
        foreach (KeyValuePair<int, Dictionary<string, AnalyzedSkill>> kv in source)
        {
            var inner = new Dictionary<string, AnalyzedSkill>();
            foreach (KeyValuePair<string, AnalyzedSkill> sk in kv.Value)
            {
                inner[sk.Key] = sk.Value.Copy();
            }

            result[kv.Key] = inner;
        }

        return result;
    }

    private void AccumulateSkillDetail(
        Dictionary<int, Dictionary<string, AnalyzedSkill>> target, ParsedDamagePacket packet, int actor,
        bool folded)
    {
        string skillCode = packet.SkillCode.ToString();
        if (!target.TryGetValue(actor, out Dictionary<string, AnalyzedSkill>? actorSkills))
        {
            actorSkills = new Dictionary<string, AnalyzedSkill>();
            target[actor] = actorSkills;
        }

        if (!actorSkills.ContainsKey(skillCode))
        {
            Skill? skill = _dm.Skill(packet.SkillCode);
            var analyzed = new AnalyzedSkill { SkillCode = packet.SkillCode, Name = skill?.Name ?? skillCode };
            actorSkills[skillCode] = analyzed;
        }

        AnalyzedSkill analyzedSkill = actorSkills[skillCode];
        if (packet.Dot)
        {
            analyzedSkill.DotTimes++;
            analyzedSkill.DotDamageAmount += packet.Damage;
        }
        else
        {
            analyzedSkill.Times++;
            analyzedSkill.DamageAmount += packet.Damage;
            // The full wire code carries the caster's specialization (특화) in its last four digits; keep a
            // representative one (constant per actor/battle) for the detail panel to decode.
            if (packet.RawSkillCode != 0) analyzedSkill.RawSkillCode = packet.RawSkillCode;
            // A special-flag region exists only for switch-type 5/6/7 (region size ≥ 10); switch-type 4 hits
            // (heals/buffs/passives) have no flag byte, so back/전방/강타/완벽/페리 are unmeasurable on them.
            // Count the flag-bearing hits so those rates use them as the denominator instead of every hit.
            bool flagged = (packet.SwitchVariable & 0x0F) is 5 or 6 or 7;
            if (flagged) analyzedSkill.FlaggedTimes++;
            // 막기가 굴러갈 수 있었던 피해. 후방 타격은 구조적으로 막히지 않으므로(실측 0/267,357) 분모에서
            // 빠져야 한다 — 이 몫이 "명중을 올리면 DPS가 얼마나 오르나"의 분모다.
            if (flagged && !packet.IsBack) analyzedSkill.EligibleDamage += packet.Damage;
            if (packet.IsCrit) analyzedSkill.CritTimes++;
            // Attack direction comes from the position byte the 07-01 patch added (IsBack/IsFront), NOT the
            // special-flag byte: its raw bit 0x80 (which the old back count used) is a ~45%-incidence proc
            // statistically independent of facing — measured across 151,937 hits — so it mis-counted back
            // attacks. Front and back are mutually exclusive.
            if (packet.IsBack) analyzedSkill.BackTimes++;
            if (packet.IsFront) analyzedSkill.FrontTimes++;
            if (packet.Specials.Contains(SpecialDamage.PARRY)) analyzedSkill.ParryTimes++;
            if (packet.Specials.Contains(SpecialDamage.DOUBLE)) analyzedSkill.DoubleTimes++;
            if (packet.Specials.Contains(SpecialDamage.PERFECT)) analyzedSkill.PerfectTimes++;
            if (packet.Specials.Contains(SpecialDamage.POWER_SHARD)) analyzedSkill.ShardTimes++;
            if (packet.Loop != 0) analyzedSkill.MultiHitTimes++;

            // 소환수 몫을 접기 전에 따로 센다. ResolveActor 가 이미 주인 uid 로 접어 넣었으므로 여기서 세지
            // 않으면 사후 분리가 원리적으로 불가능하다 — 소환수 강타율이 주인과 +8.68%p 어긋나고, 그 편향이
            // 직업과 상관돼 있어 "정령성/치유성은 저항이 다르다"는 가짜 발견을 만든다.
            if (folded)
            {
                analyzedSkill.SummonTimes++;
                if (flagged) analyzedSkill.SummonFlaggedTimes++;
                if (packet.Specials.Contains(SpecialDamage.DOUBLE)) analyzedSkill.SummonDoubleTimes++;
                if (packet.Specials.Contains(SpecialDamage.PERFECT)) analyzedSkill.SummonPerfectTimes++;
            }
        }
    }

    private void AccumulatePacket(ParsedDamagePacket packet)
    {
        int? actorN = ResolveActor(packet, _cachedContributors);
        if (actorN == null) return;
        int actor = actorN.Value;
        User? user = _dm.User(actor);
        if (user == null)
        {
            // 난입(mid-join) / late identity: the executor's own-nickname (0x3633) — the only packet that
            // registers "this uid is me" — can arrive AFTER the first boss fight, so the executor's own
            // damage (often the top dealer) was being silently dropped, losing its DPS row entirely. Instead
            // of dropping, provisionally register an un-folded actor that is dealing a JOB-LOCKED damage skill
            // (direct evidence it is a player — AION2 damage skills are job-locked), so the row shows with the
            // correct DPS now and is enriched (nickname/server/job/isExecutor) IN PLACE when 0x3633/0x3645
            // arrives. The un-folded + job-locked-skill gate keeps non-player entities (NPC allies, unmapped
            // pets) out; summons/mobs are already folded or dropped by ResolveActor.
            if (actor != packet.ActorId || JobClassInfo.ConvertFromSkill(packet.SkillCode) is null)
            {
                return;
            }

            user = _dm.EnsureUser(actor);
        }

        _cachedContributors.RemoveAll(u => u.Id == user.Id);
        _cachedContributors.Add(user);

        // Infer job from the skill code ONLY for the actor's OWN (un-folded) packet. When ResolveActor
        // folded this packet onto another uid (a summon owner, or the lone elementalist), the skill code
        // belongs to a DIFFERENT player, so inferring from it would mislabel the resolved user — the
        // reported wrong-class-icon bug (e.g. a CLERIC/CHANTER summon's 17xx/18xx code folded onto a
        // TEMPLAR). The fold guard (actor == packet.ActorId) keeps a folded foreign code from ever reaching
        // here. An OWN job-locked skill is the highest-confidence source, so it CORRECTS a wrong jobByte /
        // official label (e.g. a 살성's 13xxxxxx skills overriding a mis-read 궁성 jobByte), not just fills
        // a missing job — see User.TrySetJob / JobProvenance.
        if (actor == packet.ActorId && JobClassInfo.ConvertFromSkill(packet.SkillCode) is { } inferredJob)
        {
            user.TrySetJob(inferredJob, JobProvenance.OwnSkill);
        }

        if (!string.IsNullOrWhiteSpace(user.Nickname) && user.Server > 0 && user.Power <= 0)
        {
            _dm.RequestOfficialCharacterLookup(user.Id);
        }

        if (!_cachedInfo.TryGetValue(user.Id, out DpsInformation? info))
        {
            info = new DpsInformation();
            _cachedInfo[user.Id] = info;
        }

        info.AddDamage(packet.Damage);
        // 접힘 판정은 여기서만 할 수 있다. ResolveActor 는 순수 함수(부작용을 넣으면 재생 안전성 추론이
        // 무너진다)이고, 그 안에서 세면 바로 아래 user == null 분기가 통째로 버리는 패킷까지 세어 과다 차감된다.
        bool folded = actor != packet.ActorId;
        lock (_cacheGate) // 새 액터/스킬 키 삽입 = 구조적 변경 (_cacheGate 주석)
        {
            AccumulateSkillDetail(_cachedSkillDetails, packet, user.Id, folded);
        }

        // 판정 교차표는 업로더 본인의 접히지 않은 타격만 받는다 — 스탯 스탬프는 본인 시트이므로, 소환수 타격을
        // 넣으면 한 주체의 발동률을 다른 주체의 스탯과 짝지어 버린다.
        if (!folded && user.Id == _dm.ExecutorId())
        {
            _cachedJudgment.Accumulate(packet, _cachedBattleStart);
        }

        long ts = packet.Timestamp;

        // Bucket this hit into its absolute whole-second for the DPS graph. Absolute (not BattleStart-relative)
        // so it is immune to the reported start being re-anchored below; the dense array is re-based at build.
        if (ts > 0L)
        {
            // 초당 새 키가 꽂히는 사전이라, 라이브 중 DPS 그래프를 여는 클릭과 정확히 부딪친다 (_cacheGate 주석).
            lock (_cacheGate)
            {
                if (!_cachedDpsBuckets.TryGetValue(user.Id, out Dictionary<long, long>? buckets))
                {
                    buckets = new Dictionary<long, long>();
                    _cachedDpsBuckets[user.Id] = buckets;
                }

                long second = ts / 1000L;
                buckets[second] = buckets.GetValueOrDefault(second) + packet.Damage;
            }
        }

        if (_cachedBattleStart == 0L)
        {
            _cachedBattleStart = ts;
            _isCachedBattleStartFake = true;
            // Snapshot the combat-enter toggle for this battle (reliable here — the first damage is only
            // accumulated during live processing of the started battle). Used to cap start back-dating.
            _cachedBattleStartToggle = _dm.CurrentBattleStart();
        }

        if (_isCachedBattleStartFake && _cachedBattleStart > ts) _cachedBattleStart = ts;
        if (ts > _cachedBattleEnd) _cachedBattleEnd = ts;
    }

    private long ActivePacketCutoff()
    {
        long start = _dm.CurrentBattleStart();
        return start > PreemptivePacketWindowMs ? start - PreemptivePacketWindowMs : 0L;
    }

    private void ProcessPendingPacketsBefore(int targetId, long before)
    {
        if (targetId <= 0) return;
        PacketWindow window = _dm.BattleDataSince(targetId, _lastProcessedSequence);
        if (window.DroppedBeforeStart)
        {
            ResetCache();
        }

        foreach (ParsedDamagePacket p in window.Packets)
        {
            if (before <= 0L || p.Timestamp < before)
            {
                AccumulatePacket(p);
            }
        }

        _lastProcessedSequence = window.NextSequence;
    }

    private void RefreshRecentReportFromCache(int targetId, MobInfo? fixedTarget)
    {
        PurgeResolvedNonPlayers();
        TryRecoverExecutorFromRoster();
        long battleStart = _cachedBattleStart > 0L ? StartAnchor() : _recentData.BattleStart;
        long battleEnd = Math.Max(_cachedBattleEnd > 0L ? _cachedBattleEnd : _recentData.BattleEnd, battleStart);

        MobInfo? targetInfo;
        if (fixedTarget != null)
        {
            targetInfo = new MobInfo(fixedTarget.Id, fixedTarget.Mob, fixedTarget.RemainHp, fixedTarget.MaxHp);
        }
        else
        {
            int? mobCode = _dm.GetMobId(targetId);
            Mob? mob = mobCode != null ? _dm.Mob(mobCode.Value) : null;
            targetInfo = mob != null
                // 라이브 재구성도 기록용 최저치를 쓴다 — 안 그러면 기여도 분모(아래 fixedMax)와 저장 값이
                // 서로 다른 HP 를 보게 된다.
                ? new MobInfo(
                    targetId,
                    mob,
                    _dm.BattleLowMobHp(targetId, _currentBattleRevision) ?? _dm.MobHp(targetId) ?? 0,
                    _dm.MobMaxHp(targetId) ?? 0)
                : null;
        }

        // 허수아비 런은 측정 창이 곧 결과다. 여기서 "마지막 데미지 패킷"으로 끝을 다시 잡으면 1:00 짜리
        // 런이 59.4초로 굳고(마지막 몇 백 ms 는 그냥 안 때린 시간이다) DPS 분모도 그만큼 짧아져,
        // 같은 조건으로 두 번 돌린 결과를 서로 비교할 수 없게 된다. 컷이 찍어 둔 고정 종료가 있으면 그걸 쓴다.
        // ⚠️ 세 가지를 전부 확인한 뒤에만 덮어쓴다:
        //   ① 이 리포트가 정말 허수아비 전투인가 — 아니면 보스 전투의 종료 시각이 허수아비 값으로 덮인다.
        //   ② 스탬프가 시작보다 뒤인가.
        //   ③ 스탬프가 <b>집계된 마지막 피해보다 뒤인가</b> — 앞이면 분자는 전체인데 분모만 짧아져 DPS 가 부푼다.
        // 보스 전투에서는 ①에서 이미 걸린다.
        long dummyFixedEnd = _dm.DummyFixedBattleEnd;
        if (targetInfo?.Mob.IsDummy == true && dummyFixedEnd > battleStart && dummyFixedEnd >= _cachedBattleEnd)
        {
            battleEnd = dummyFixedEnd;
        }

        (Dictionary<int, int> cachedSlots, int cachedRosterSize) = _dm.FreshPartySlots(_cachedInfo.Keys
            .Select(uid => _dm.User(uid))
            .Where(u => u != null)
            .Select(u => u!)
            .ToList());

        var report = new DpsReport
        {
            Contributors = FreezeContributors(),
            BattleStart = battleStart,
            BattleEnd = battleEnd,
            Packets = null,
            Target = targetInfo,
            // 🔑 슬롯·정원을 **라이브 리포트에도** 싣는다. 티어 resolver 는 라이브·기록 공용이라, 저장본에만
            // 실으면 같은 전투가 두 숫자를 낸다: 라이브는 PartySlots 가 비어 IsSynergyTrusted 가 false → R2 로
            // 떨어지고, 기록은 trusted → R0 를 쓴다. 그리고 **밴드행은 rung 0 에만 실리므로** 라이브는 시너지
            // 버킷뿐 아니라 전투력 밴드까지 통째로 잃는다(900k 캐릭터가 전 전투력 풀과 비교된다).
            // 게이트는 FreshPartySlots 안에 있다 — 저장 경로와 같은 시계를 봐야 새 불일치가 안 생긴다.
            PartySlots = cachedSlots,
            PartyRosterSize = cachedRosterSize,
            // 이 전투의 시련 난이도를 지금 박아 둔다 — 나중에 몹 코드로는 되짚을 수 없다(DpsReport 주석).
            TrialDifficulty = _dm.TrialDifficulty.Current,
            // ⚠️ 이 줄을 빼면 <b>보스가 죽는 바로 그 틱</b>에 로스터로 구제한 파티원 행이 사라진다.
            // 이 리포트가 _recentData 를 교체하는데 TargetInstanced 의 대입은 라이브 경로 한 곳뿐이라,
            // 여기서 이월하지 않으면 기본값 false 로 떨어진다 — 그런데 그 플래그가 표시 계층의
            // '여기는 파티 씬이다' 증거(인스턴스에는 외부인이 없다)라, 꺼지면 구제가 닫히고 사용자가
            // 결과를 읽는 바로 그 순간 행이 줄고 비중 합이 100%에 못 미친다. 몹 코드를 알면 다시 묻고,
            // 모르면(타깃 인식 실패) 직전 값을 그대로 들고 간다.
            TargetInstanced = targetInfo != null
                ? _dm.IsInstancedBoss(targetInfo.Mob.Code)
                : _recentData.TargetInstanced,
            ExecutorId = _dm.ExecutorId(), // freeze 본인 uid so the post-combat idle ("대기 중") view self-colors the
                                           // own row in 직업 강조 mode — this is the report returned in the idle state,
                                           // so without it self-id falls to the transient _selfId and reverts to job color
        };

        double totalDamage = _cachedInfo.Values.Sum(i => i.Amount);
        long duration = report.BattleEnd - report.BattleStart;
        double? fixedMax = targetInfo != null && targetInfo.MaxHp > 0 ? (double?)targetInfo.MaxHp : null;
        double mobMaxHp = fixedMax ?? _dm.MobMaxHp(targetId) ?? 0.0;

        foreach (KeyValuePair<int, DpsInformation> kv in _cachedInfo)
        {
            report.Information[kv.Key] = new DpsInformation(
                kv.Value.Amount,
                duration > 0 ? kv.Value.Amount / duration * 1000 : 0.0,
                totalDamage > 0 ? kv.Value.Amount / totalDamage * 100 : 0.0,
                mobMaxHp > 0 ? kv.Value.Amount / mobMaxHp * 100 : 0.0);
        }

        _recentData = report;
    }

    public DpsReport GetLiveReport()
    {
        int storageTarget = _dm.CurrentTarget();
        if (storageTarget == -1) return _recentData;
        if (!_recentData.IsEmpty() && _recentData.Target?.Id == storageTarget) return _recentData;
        return new DpsReport
        {
            BattleStart = _dm.CurrentBattleStart(),
            BattleEnd = _dm.CurrentBattleEnd(),
            Packets = _dm.BattleData(storageTarget),
        };
    }

    /// <summary>이 전투의 보스 HP를 기록용으로 확정한다 — 라이브 값이 아니라 <b>전투 중 관측된 최저 잔여 HP</b>다.
    /// <para>전멸하면 보스가 제자리로 돌아가며 만피를 한 번 방송하는데, 실측상 그 프레임이 <b>종료 토글보다
    /// 1.5초 먼저</b> 온다. 그래서 라이브 값을 읽으면 25% 남기고 진 전투가 기록에 100%로 박히고, 종료 직후
    /// 대기 화면의 게이지까지 만피로 그려진다(이 리포트가 <c>BattleFinished</c>로 계속 나가므로). 최저치는
    /// 정상 전투에서 마지막 타격 직후의 잔여 HP와 같으므로(피해는 줄어들기만 한다) 평시 동작은 안 바뀐다.</para>
    /// <para>최대 HP는 단조 증가라 되돌아갈 일이 없어 그대로 라이브에서 읽는다.</para></summary>
    /// <para><b>저장 직전에 한 번 더 부르는 이유.</b> 저장 경로는 셋이고(타깃 전환·전투 종료·종료 드레인)
    /// 셋 다 <see cref="SaveRecentBattleLog"/> 를 지난다. 전이 지점에만 꽂으면 세 번째 — 유저가 전멸
    /// <b>직후</b> 미터를 닫아 소비자 루프가 종료 전이를 돌기 전에 빠져나가는 경로 — 가 그대로 새고, 그게
    /// 하필 이 수정이 없애려던 바로 그 장면이다.</para>
    private void FreezeTargetHp(int targetId)
    {
        if (_recentData.Target is not { } target)
        {
            return;
        }

        target.RemainHp = _dm.BattleLowMobHp(targetId, _currentBattleRevision) ?? _dm.MobHp(targetId) ?? target.RemainHp;
        target.MaxHp = _dm.MobMaxHp(targetId) ?? target.MaxHp;
    }

    public DpsReport GetDps()
    {
        _dm.TickDummyBattle(); // enforce the dummy duration hard-cut / idle-end / mode-off before reading state
        // 죽지도 종료 토글도 없이 조용해진 보스(버스에서 흔하다)를 닫는다. 아래 상태 읽기보다 먼저 돌아야 이 틱에서
        // 곧바로 종료 전이(_currentTarget == -1)를 타고 리포트가 풀린다.
        _dm.TickBossBattleIdle();
        int storageTarget = _dm.CurrentTarget();
        long storageBattleRevision = _dm.CurrentBattleRevision();
        int previousTarget = _currentTarget;
        bool targetChanged = storageTarget != previousTarget;
        bool battleRestartedWithSameTarget =
            storageTarget > 0 && previousTarget > 0 && storageBattleRevision != _currentBattleRevision;
        bool isNewBattleEnd = storageTarget == -1 && storageTarget != previousTarget;

        // 허수아비 런도 저장한다(전투 기록의 '허수아비' 탭이 이걸 읽는다). 예전에는 여기와 아래 종료 전이,
        // 둘 다 !prevTargetDummy 로 막고 있었다.
        if ((targetChanged || battleRestartedWithSameTarget)
            && storageTarget != -1 && previousTarget > 0 && !_recentData.IsEmpty())
        {
            ProcessPendingPacketsBefore(previousTarget, ActivePacketCutoff());
            FreezeTargetHp(previousTarget); // Refresh 보다 먼저 — 기여도 분모(fixedMax)가 얼린 MaxHp 를 보게 한다
            RefreshRecentReportFromCache(previousTarget, _recentData.Target);
            SaveRecentBattleLog();
            _recentDataSaved = true;
        }

        if (targetChanged || battleRestartedWithSameTarget)
        {
            ResetCache();
        }

        _currentTarget = storageTarget;
        _currentBattleRevision = storageBattleRevision;

        if (_currentTarget == -1)
        {
            long battleEnd = _dm.CurrentBattleEnd();
            if (isNewBattleEnd)
            {
                _recentData.BattleEnd = battleEnd;
                if (previousTarget > 0)
                {
                    ProcessPendingPacketsBefore(previousTarget, 0L);
                    FreezeTargetHp(previousTarget);
                    RefreshRecentReportFromCache(previousTarget, _recentData.Target);
                }

                // 통짜 비우기는 <b>전투가 끝난 그 한 틱</b>에만 돈다. 예전에는 이 줄이 이 블록 <b>밖</b>에
                // 있어서 대기 중 매 틱(기본 500ms) 전 타깃의 링버퍼를 비웠고, 그래서 다음 보스에 넣은
                // 오프너(교전 토글보다 먼저 들어간 타격)가 토글이 오기 전에 이미 지워져 누적 피해·기여도·
                // DPS 분자에서 빠졌다 — ActivePacketCutoff 가 admit 해도 돌아올 것이 없었다. 더 큰 갈래는
                // PromoteUnresolvedStart(TTL 60초) 대기 구간으로, 그 내내 CurrentTarget()==-1 이라 매 틱
                // 플러시가 돌아 토글~스폰 사이 파티 전체 피해가 소각되고 시작 시각만 back-date 돼
                // "분모는 길고 분자는 빈 전투"가 저장·업로드됐다.
                // 유휴 중 GC 는 PacketRepository.Save 의 시간 기준 스윕(IdleRetentionMs)이 대신 맡는다 —
                // ⚠️ 이 줄을 블록 밖으로 되돌리면 그 두 가지가 같이 재발한다.
                _dm.FlushPacket();

                if (!_recentData.IsEmpty())
                {
                    SaveRecentBattleLog();
                    _recentDataSaved = true;
                }
            }

            // 대기 상태에서 계속 내보내는 이 리포트는 더 이상 진행 중이 아니다. 표시 계층이 "직전 전투 위로
            // 로스터 프리뷰를 다시 띄울지"를 판정하는 신호이며(OverlayRowBuilder Feature 1), 파티 스냅샷 유무로
            // 대신 판정하면 솔로 전투(스냅샷이 정당하게 빔)에서 깨진다. 저장 여부와 무관하게 켠다 — 허수아비처럼
            // 저장을 건너뛰는 전투도 "끝난 전투"인 건 같다. 진행 중 리포트는 매 틱 새 DpsReport라 여기로 새지 않는다.
            _recentData.BattleFinished = true;
            return _recentData;
        }

        if (_currentTarget == 0)
        {
            return new DpsReport();
        }

        List<ParsedDamagePacket>? reportPackets;
        if (_currentTarget > 0)
        {
            PacketWindow window = _dm.BattleDataSince(_currentTarget, _lastProcessedSequence);
            if (window.DroppedBeforeStart)
            {
                ResetCache();
            }

            long packetCutoff = ActivePacketCutoff();
            foreach (ParsedDamagePacket p in window.Packets)
            {
                if (p.Timestamp >= packetCutoff)
                {
                    AccumulatePacket(p);
                }
            }

            _lastProcessedSequence = window.NextSequence;
            reportPackets = null;
        }
        else
        {
            List<ParsedDamagePacket>? data = _dm.BattleData(_currentTarget);
            int currentCount = data?.Count ?? 0;
            if (currentCount < _lastProcessedSequence)
            {
                ResetCache();
            }

            int fromIndex = Math.Clamp((int)_lastProcessedSequence, 0, currentCount);
            if (data != null && currentCount > fromIndex)
            {
                for (int i = fromIndex; i < currentCount; i++)
                {
                    AccumulatePacket(data[i]);
                }

                _lastProcessedSequence = currentCount;
            }

            reportPackets = data;
        }

        PurgeResolvedNonPlayers();
        TryRecoverExecutorFromRoster();

        long dmStart = _dm.CurrentBattleStart();
        long dmEnd = _dm.CurrentBattleEnd();
        List<User> liveContributors = CopyContributors();
        (Dictionary<int, int> liveSlots, int liveRosterSize) = _dm.FreshPartySlots(liveContributors);
        var report = new DpsReport
        {
            Contributors = liveContributors,
            // 🔑 슬롯·정원을 **라이브 리포트에도** 싣는다. 티어 resolver 는 라이브·기록 공용이라, 저장본에만
            // 실으면 같은 전투가 두 숫자를 낸다: 라이브는 PartySlots 가 비어 IsSynergyTrusted 가 false → R2 로
            // 떨어지고, 기록은 trusted → R0 를 쓴다. 그리고 **밴드행은 rung 0 에만 실리므로** 라이브는 시너지
            // 버킷뿐 아니라 전투력 밴드까지 통째로 잃는다(900k 캐릭터가 전 전투력 풀과 비교된다).
            // 게이트는 FreshPartySlots 안에 있다 — 저장 경로와 같은 시계를 봐야 새 불일치가 안 생긴다.
            PartySlots = liveSlots,
            PartyRosterSize = liveRosterSize,
            // Duration basis MUST match the FINAL/saved report (RefreshRecentReportFromCache, which uses
            // _cachedBattleStart only): once any damage exists, count from the first DAMAGE timestamp
            // (_cachedBattleStart), falling back to the battle-start toggle wall-clock (CurrentBattleStart)
            // only before the first hit. This previously took Math.Min(dmStart, _cachedBattleStart); on a
            // re-pull (notably after a 전멸) the start toggle fires at re-aggro/run-back WELL BEFORE damage
            // resumes, so Math.Min picked the earlier toggle and the live duration absorbed the whole
            // pre-damage gap — deflating the in-progress DPS until the battle ended and the toggle start was
            // dropped (the "전투 중엔 낮게 표시되다 끝나면 정상" report). The end report is unchanged.
            // StartAnchor() additionally caps back-dating to 250ms before the toggle (openers still counted).
            BattleStart = _cachedBattleStart != 0L ? StartAnchor() : dmStart,
            BattleEnd = Math.Max(dmEnd, _cachedBattleEnd),
            Packets = reportPackets,
            // 이 전투의 시련 난이도를 지금 박아 둔다 — 나중에 몹 코드로는 되짚을 수 없다(DpsReport 주석).
            TrialDifficulty = _dm.TrialDifficulty.Current,
            ExecutorId = _dm.ExecutorId(), // carry 본인 uid into the live report so self-coloring survives the
                                           // transition into the post-combat idle state (matches the saved snapshot)
        };

        if (_currentTarget > 0)
        {
            int? mobCode = _dm.GetMobId(_currentTarget);
            // An engaged-but-UNRECOGNIZED target (mobCode never registered, or a code not in the catalog — e.g.
            // the intermittent first-boss miss) must NOT build a MobInfo with a null Mob: that null-derefs later
            // as Target.Mob.Name in OverlayViewModel.Update and, dispatched via Dispatcher.Invoke, crashes the
            // consumer thread (bypassing DispatcherUnhandledException). Mirror the null-safe path in GetDps
            // (~L296): leave Target null so the overlay shows "타겟 인식 실패" instead of crashing.
            Mob? mob = mobCode != null ? _dm.Mob(mobCode.Value) : null;
            if (mob != null)
            {
                report.Target = new MobInfo(_currentTarget, mob)
                {
                    RemainHp = _dm.MobHp(_currentTarget) ?? 0,
                    MaxHp = _dm.MobMaxHp(_currentTarget) ?? 0,
                };
                report.TargetInstanced = _dm.IsInstancedBoss(mobCode!.Value); // party-scene proof for the roster rescue
            }
        }

        double totalDamage = _cachedInfo.Values.Sum(i => i.Amount);
        long duration = report.BattleEnd - report.BattleStart;
        double mobMaxHp = _dm.MobMaxHp(_currentTarget) ?? 0.0;
        foreach (KeyValuePair<int, DpsInformation> kv in _cachedInfo)
        {
            report.Information[kv.Key] = new DpsInformation(
                kv.Value.Amount,
                duration > 0 ? kv.Value.Amount / duration * 1000 : 0.0,
                totalDamage > 0 ? kv.Value.Amount / totalDamage * 100 : 0.0,
                mobMaxHp > 0 ? kv.Value.Amount / mobMaxHp * 100 : 0.0);
        }

        if (_dm.IsCurrentTargetDummy())
        {
            int executorId = _dm.ExecutorId();
            if (executorId != 0 && !report.Contributors.Any(u => u.IsExecutor))
            {
                return _recentData;
            }
        }

        _recentData = report;
        _recentSkillDetails = new Dictionary<int, Dictionary<string, AnalyzedSkill>>();
        _recentBuffRates = new Dictionary<int, List<OperatingData>>();
        _recentBossBuffRates = [];
        _recentDataSaved = false;
        return report;
    }

    /// <summary>How old the party roster may be and still identify us (the same window the replay scopes by).</summary>
    private const long RosterRecoveryWindowMs = 30 * 60 * 1000L;

    /// <summary>
    /// Recover "which uid is me" when the own-nickname packet (0x3633) never arrived.
    /// <para>
    /// 0x3633 is the ONLY packet that says "this uid is you", and the server sends it on a character/zone
    /// load — so a meter started (or restarted) while you are already inside a dungeon never sees it. Your
    /// damage still lands (AccumulatePacket registers a nickname-less provisional row), but nothing knows it
    /// is yours: no self colour, no self row name, and the replay drops you, because scoping a recording to
    /// the party works by name and yours is blank.
    /// </para>
    /// <para>
    /// The party roster (0x9702) DOES include us. So when exactly one roster identity is unaccounted for —
    /// nobody in the repository carries that name — and exactly one damaging contributor is nameless, the two
    /// are each other. Both "exactly one"s are the guard: a party member who has not damaged (or not been
    /// named) yet leaves two loose ends, and we wait rather than guess.
    /// </para>
    /// <para>
    /// ⚠️ 두 "exactly one"만으로는 부족하다. 미청구 이름이 본인인 것은 확실하지만(executor==0 이면 본인 닉을
    /// 아무 uid도 들고 있지 않다) <b>무명 하나가 본인인지</b>는 확실하지 않다 — 본인이 아직 딜을 안 넣은
    /// 창(전투 시작 직후 수백 ms~수 초)에서는 남의 행이 그 자리를 차지한다. 그래서 표시층과 같은 가드
    /// 셋(소환수/몹 배제 · 최소 딜 비중 · 외부인 게이트)을 아래에 함께 건다. 자세한 이유는 각 가드의 주석과
    /// <see cref="SelfRecoveryGuards"/>.
    /// </para>
    /// </summary>
    private void TryRecoverExecutorFromRoster()
    {
        if (_dm.ExecutorId() != 0 || _cachedContributors.Count == 0)
        {
            return;
        }

        IReadOnlyList<(string Nickname, int Server)> roster = _dm.PartyMemberIdentities(RosterRecoveryWindowMs);
        if (roster.Count == 0)
        {
            return;
        }

        List<(string Nickname, int Server)> unclaimed = roster
            .Where(r => _dm.FindUserByNicknameAndServer(r.Nickname, r.Server) is null)
            .ToList();

        List<User> nameless = _cachedContributors
            .Where(u => string.IsNullOrWhiteSpace(u.Nickname))
            .ToList();

        if (unclaimed.Count != 1 || nameless.Count != 1)
        {
            return;
        }

        User candidate = nameless[0];

        // ── 아래 세 가드는 표시층(OverlayRowBuilder 의 lost-executor 복구)이 이미 갖고 있던 것을 그대로
        // 옮겨 온 것이다. 두 층이 같은 질문에 다른 답을 낼 이유가 없고, 특히 이쪽은 표시 전용 복사본이 아니라
        // SaveNickname(isExecutor: true) 로 신원 저장소에 <b>영구 기록</b>한다 — 틀리면 자기색·버프 오버레이
        // 게이트·업로드의 본인 성적이 전부 그 uid를 따라가고, 그 행은 PurgeResolvedNonPlayers 에서도 영구
        // 제외된다. 2차 방어선인 SaveNickname 의 executor 게이트는 uid 상한뿐이라 아무것도 막지 못한다.

        // ① 소환수/몹은 절대 본인이 아니다(identity-roster#4). 지금은 두 호출자 모두 바로 앞에서
        //    PurgeResolvedNonPlayers 를 돌리므로 겹치는 방어선이지만, 그 순서가 바뀌면 남의 소환수 딜이
        //    내 성적으로 표시·업로드되고 그다음 전투부터는 own_result_missing 으로 조용히 스킵된다.
        if (_dm.SummonerId(candidate.Id) != null || _dm.IsMobInstance(candidate.Id))
        {
            return;
        }

        // ② 큰 딜러여야 한다 — 표시층과 같은 문턱(SelfRecoveryGuards.MinDamageShare), 같은 지표(RAW 피해량).
        double topAmount = 0.0;
        double candidateAmount = 0.0;
        foreach (KeyValuePair<int, DpsInformation> kv in _cachedInfo)
        {
            if (kv.Value.Amount > topAmount)
            {
                topAmount = kv.Value.Amount;
            }

            if (kv.Key == candidate.Id)
            {
                candidateAmount = kv.Value.Amount;
            }
        }

        if (topAmount <= 0.0 || candidateAmount < topAmount * SelfRecoveryGuards.MinDamageShare)
        {
            return;
        }

        // ③ 외부인 게이트. 파티에 없는 <b>이름 달린</b> 딜러가 있으면 여기는 공개 씬(필드보스 zerg)이고,
        //    "미청구 이름 하나 ↔ 무명 하나"는 본인의 증거가 아니다 — 본인은 아직 딜을 안 넣은 구경꾼일 수 있고,
        //    그때 이 경로는 낯선 사람 uid를 본인으로 확정한다(identity-roster~S2). 로스터가 있다는 것은 이미
        //    확인했으므로(위 roster.Count) 파티 문맥 미상으로 게이트가 꺼지는 경우는 없다.
        var party = new List<(int Uid, string? Nickname, int Server)>();
        foreach (User m in _dm.PartyRoster(RosterRecoveryWindowMs))
        {
            party.Add((m.Id, m.Nickname, m.Server));
        }

        foreach ((string nickname, int server) in roster)
        {
            party.Add((0, nickname, server)); // uid 미상 — (닉,서버) 대조에만 쓰인다
        }

        var damagers = new List<(int Uid, string? Nickname, int Server)>(_cachedContributors.Count);
        foreach (User u in _cachedContributors)
        {
            damagers.Add((u.Id, u.Nickname, u.Server));
        }

        if (SelfRecoveryGuards.HasNamedOutsider(damagers, party, candidate.Id))
        {
            return;
        }

        // jobByte 0 = "unknown": the job is already being inferred from our own job-locked damage skills,
        // which outrank a snapshot byte anyway.
        _dm.SaveNickname(candidate.Id, unclaimed[0].Nickname, isExecutor: true, unclaimed[0].Server, jobByte: 0);
    }

    private List<User> CopyContributors() => [.. _cachedContributors];

    // A summon whose owner-map (0x3641 summon_map) or mob registration lands AFTER it has already dealt damage
    // gets provisionally registered as a player by AccumulatePacket: its damage skill is job-locked (a summon
    // casts its owner's class skill), it is un-folded (no summon→owner mapping yet), and it is not yet a known
    // mob instance, so ResolveActor returns the raw entity id and the job-locked-skill gate lets EnsureUser
    // create a nickname-less row. Once the game reveals that entity as a summon (SummonerId) or mob
    // (IsMobInstance), retract the phantom so it never lingers as a non-party contributor. Only nickname-less
    // provisional rows are eligible — a real player (0x3633/0x3645 nickname) and the executor are never a
    // summon/mob, so they are never removed. Root cause of the 그리오사 "파티원 아닌 사람" phantom: a healer-summon
    // (0x3641 tagged 07 02 01, missed by the fixed 07 02 06 owner scan — see StreamProcessor) that also raced
    // its own spawn packet.
    private void PurgeResolvedNonPlayers()
    {
        for (int i = _cachedContributors.Count - 1; i >= 0; i--)
        {
            User u = _cachedContributors[i];
            if (!string.IsNullOrWhiteSpace(u.Nickname) || u.IsExecutor) continue;
            if (_dm.SummonerId(u.Id) == null && !_dm.IsMobInstance(u.Id)) continue;
            _cachedContributors.RemoveAt(i);
            _cachedInfo.Remove(u.Id);
            lock (_cacheGate)
            {
                _cachedSkillDetails.Remove(u.Id);
            }
        }
    }

    // Deep-copy the contributors so a FINISHED/standby battle's displayed identity (nickname/server/job/power)
    // is FROZEN at battle end — mirroring SaveBattleLog's CopyUser freeze. Without this, the standby report
    // (built by RefreshRecentReportFromCache and returned by GetDps in the idle state) shares the live
    // repository User objects; when the game later reissues a RETIRED entity uid to a DIFFERENT nearby player
    // (minutes after the fight) and DataManager.SaveNickname mutates that User in place, the still-displayed
    // finished-battle row repaints to the wrong player. The IN-COMBAT report (CopyContributors on the
    // live-target path) deliberately keeps the shared refs so identity correction still flows during the fight.
    private List<User> FreezeContributors() => [.. _cachedContributors.Select(u => u.Copy())];

    private Dictionary<int, Dictionary<string, AnalyzedSkill>> BuildSkillDetails(DpsReport data)
    {
        var analyzedByActor = new Dictionary<int, Dictionary<string, AnalyzedSkill>>();
        var contributorIds = data.Contributors.Select(u => u.Id).ToHashSet();
        if (data.Packets != null)
        {
            foreach (ParsedDamagePacket p in data.Packets)
            {
                int? realActor = ResolveActor(p, data.Contributors);
                if (realActor == null || !contributorIds.Contains(realActor.Value)) continue;
                AccumulateSkillDetail(analyzedByActor, p, realActor.Value, folded: realActor.Value != p.ActorId);
            }
        }

        return analyzedByActor;
    }

    public Dictionary<string, AnalyzedSkill> BattleDetails(DpsReport? data, int uid)
    {
        if (data == null) return new Dictionary<string, AnalyzedSkill>();

        // A SAVED/history-replayed report carries a frozen per-actor skill snapshot (and Packets==null), so
        // prefer it — otherwise the rebuild-from-packets path below would return an empty table for every
        // replayed battle. Live/in-progress reports leave this empty and fall through to the cache/packets.
        if (data.SkillDetailsSnapshot.Count > 0)
        {
            Dictionary<string, AnalyzedSkill> snapshot = data.SkillDetailsSnapshot.GetValueOrDefault(uid) ?? new();
            return snapshot.ToDictionary(kv => kv.Key, kv => kv.Value.Copy());
        }

        if (ReferenceEquals(data, _recentData) && data.Packets == null)
        {
            // ⚠️ 이 호출은 UI 스레드에서 온다(행 클릭 → DetailsViewModel 생성자). 파서가 같은 순간 이 사전에
            // 키를 꽂으므로 락 밖에서 열거하면 예외가 난다 — _cacheGate 주석 참조. 락 안에서는 복사만 한다.
            lock (_cacheGate)
            {
                Dictionary<string, AnalyzedSkill> details =
                    _cachedSkillDetails.GetValueOrDefault(uid) ?? _recentSkillDetails.GetValueOrDefault(uid) ?? new();
                return details.ToDictionary(kv => kv.Key, kv => kv.Value.Copy());
            }
        }

        Dictionary<string, AnalyzedSkill> built = BuildSkillDetails(data).GetValueOrDefault(uid) ?? new();
        return new Dictionary<string, AnalyzedSkill>(built);
    }

    /// <param name="Catalogued">True when the runtime code itself is in buff.json (so its code resolves an icon
    /// and a buff-value); false when only the skill NAME could be recovered and <see cref="Code"/> is the base.</param>
    private readonly record struct BuffDisplay(int Code, string Name, string? Summary, string? Effect, bool Catalogued);

    private static bool IsPlaceholderBuff(Buff buff) => DataManager.IsPlaceholderBuffName(buff.Name);

    private int? NormalizeBuffSkillCode(int code)
    {
        var candidates = new List<int>();

        void Add(int value)
        {
            if (value <= 0) return;
            if (!candidates.Contains(value)) candidates.Add(value);
            int rounded = (value / 10) * 10;
            if (!candidates.Contains(rounded)) candidates.Add(rounded);
        }

        Add(code);
        // Job-buff codes 11xxxxxxx(검성)..19xxxxxxx(권성, 2026-07-01 패치). Upper bound raised from
        // 190_999_999 to 199_999_999 so 권성 buff codes floor to their base skill code (else 권성
        // buffs at higher ranks fail to resolve a name and get dropped from the buff/debuff view).
        if (code is >= 110_000_000 and <= 199_999_999)
        {
            Add((code / 100_000) * 10_000);
            Add((code / 10_000) * 1_000);
        }

        Add(code / 10);
        Add(code / 100);

        foreach (int c in candidates)
        {
            if (_dm.Skill(c) != null) return c;
        }

        return null;
    }

    private BuffDisplay? ResolveBuffDisplay(int code)
    {
        Buff? buff = _dm.Buff(code);
        if (buff != null)
        {
            if (IsPlaceholderBuff(buff)) return null;
            return new BuffDisplay(code, buff.Name, buff.Summary, buff.Effect, Catalogued: true);
        }

        int? skillCode = NormalizeBuffSkillCode(code);
        if (skillCode == null) return null;
        Skill? skill = _dm.Skill(skillCode.Value);
        if (skill == null) return null;
        string? name = skill.Name;
        if (DataManager.IsPlaceholderBuffName(name)) return null;
        return new BuffDisplay(skillCode.Value, name!, null, null, Catalogued: false);
    }

    /// <summary>
    /// Buff/debuff uptime for one entity, one row per (base skill, name, caster).
    /// </summary>
    /// <remarks>
    /// A skill fires several runtime codes at once — the buff it puts on the caster and the debuff it puts on the
    /// target, each with its own rank variants. The ENTITY already separates buff from debuff (a player's list is
    /// what landed on them; the boss's list is what landed on it), so this collapses everything one skill did to
    /// one entity into a single row. Grouping is deliberately NOT by:
    /// <list type="bullet">
    /// <item>runtime code — 격노 폭발 puts 113900071 + 113900072 on its target, so each rank became its own row.</item>
    /// <item>name alone — 지연 피해 is five unrelated skills across five bases (궁성 14170000, 마도성 15050000/15320000,
    /// 정령성 16300000/16330000) that merely share a display name.</item>
    /// <item>base alone — one base carries differently-named effects (16300000 holds 4원소, 피해 내성 감소, 지연 피해).</item>
    /// </list>
    /// The rate is the union of every member interval, not a sum or a max — applications overlap.
    /// </remarks>
    /// <summary>회생의 계약 긴급 회복의 창 내 발동 횟수(+표시용 base 코드/이름). 가동률 %가 성립하지 않는
    /// 발동형이라 상세 창이 "N회"로 그린다 — 통계 웹에는 나가지 않는다.</summary>
    public (int Count, int Code, string Name) GetRevivalHealSummary(int uid, long start, long end) =>
        _dm.RevivalHealSummary(uid, start, end);

    public List<OperatingData> GetBuffOperatingRate(int uid, long start, long end)
    {
        long totalDuration = end - start;
        if (totalDuration <= 0) return [];

        var groups = _dm.BattleBuff(uid, start, end)
            .Where(b => !_dm.IsBuffBlacklisted(b.SkillCode))
            .Select(useBuff => (Display: ResolveBuffDisplay(useBuff.SkillCode), UseBuff: useBuff))
            .Where(x => x.Display != null)
            .Select(x => (D: x.Display!.Value, U: x.UseBuff, Base: DataManager.BuffBaseCode(x.UseBuff.SkillCode)))
            .GroupBy(x => (x.Base, x.D.Name, x.U.ActorId));

        var result = new List<OperatingData>();
        foreach (var group in groups)
        {
            (int baseCode, string name, int actorId) = group.Key;
            long covered = BuffUptime.CoveredMs(group.Select(x => (x.U.BuffStart, x.U.BuffEnd)), start, end);
            double rate = (double)covered / totalDuration * 100.0;

            BuffDisplay display = RepresentativeOf(group, start, end);
            // The raw packet code, not the display code: the fallback path collapses a 9-digit job buff to its
            // 8-digit base, which would then read as "not a job buff" and misclassify a self-buff as 그 외.
            int jobPrefix = group
                .Select(x => DataManager.IsJobBuffCode(x.U.SkillCode) ? x.U.SkillCode / 10_000_000 : 0)
                .FirstOrDefault(p => p != 0);

            result.Add(new OperatingData(
                display.Code, name, display.Summary, display.Effect, rate, actorId, baseCode, jobPrefix,
                GroupLevel(group)));
        }

        return result;
    }

    /// <summary>이 그룹이 대표할 어노멀 레벨: 구성 적용들의 최댓값(레벨을 못 읽은 0은 무시). 그룹 키에 이미
    /// 시전자가 들어 있으므로 한 그룹은 한 사람의 한 스킬이고, 전투 중 스킬 레벨이 바뀌지 않으니 최댓값이
    /// 곧 그 사람의 레벨이다. 전부 0이면 0(모름)을 돌려 상위 계산이 레벨 기반 계수를 건너뛴다.</summary>
    private static int GroupLevel(IEnumerable<(BuffDisplay D, UseBuff U, int Base)> group)
    {
        int level = 0;
        foreach ((BuffDisplay _, UseBuff u, int _) in group)
        {
            if (u.Level > level) level = u.Level;
        }

        return level;
    }

    /// <summary>The member that best stands for the merged group: catalogued codes first (they resolve an icon and
    /// a stats-web buff value), then the longest-running one, then the lowest code so the choice is stable.</summary>
    private static BuffDisplay RepresentativeOf(
        IEnumerable<(BuffDisplay D, UseBuff U, int Base)> group, long start, long end) =>
        group
            .GroupBy(x => x.D.Code)
            .OrderByDescending(g => g.First().D.Catalogued)
            .ThenByDescending(g => BuffUptime.CoveredMs(g.Select(x => (x.U.BuffStart, x.U.BuffEnd)), start, end))
            .ThenBy(g => g.Key)
            .First()
            .First().D;

    /// <summary>The buff/debuff timeline for one entity: the same grouping as
    /// <see cref="GetBuffOperatingRate"/> (base skill + name + caster, blacklist- and placeholder-filtered), but
    /// each group keeps its MERGED applied spans instead of a single rate — the icon-lane source for the DPS
    /// graph. Blacklist/placeholder handling is kept identical to <see cref="GetBuffOperatingRate"/> so the lane
    /// never shows an icon the uptime tab wouldn't.</summary>
    public List<BuffTimeline> GetBuffIntervals(int uid, long start, long end)
    {
        if (end <= start) return [];

        var groups = _dm.BattleBuff(uid, start, end)
            .Where(b => !_dm.IsBuffBlacklisted(b.SkillCode))
            .Select(useBuff => (Display: ResolveBuffDisplay(useBuff.SkillCode), UseBuff: useBuff))
            .Where(x => x.Display != null)
            .Select(x => (D: x.Display!.Value, U: x.UseBuff, Base: DataManager.BuffBaseCode(x.UseBuff.SkillCode)))
            .GroupBy(x => (x.Base, x.D.Name, x.U.ActorId));

        var result = new List<BuffTimeline>();
        foreach (var group in groups)
        {
            (int baseCode, string name, int actorId) = group.Key;
            List<(long Start, long End)> spans =
                BuffUptime.MergeIntervals(group.Select(x => (x.U.BuffStart, x.U.BuffEnd)), start, end);
            if (spans.Count == 0) continue;

            BuffDisplay display = RepresentativeOf(group, start, end);
            // Same raw-code job prefix as GetBuffOperatingRate: the display code may have collapsed a 9-digit
            // job buff to its 8-digit base, which would read as "not a job buff".
            int jobPrefix = group
                .Select(x => DataManager.IsJobBuffCode(x.U.SkillCode) ? x.U.SkillCode / 10_000_000 : 0)
                .FirstOrDefault(p => p != 0);

            result.Add(new BuffTimeline(display.Code, name, actorId, baseCode, jobPrefix, spans, GroupLevel(group)));
        }

        return result;
    }

    /// <summary>The per-second damage series for one entity over <c>[start, end]</c>: a dense <c>long[]</c>
    /// indexed by whole-second offset from <paramref name="start"/> (element i = damage in the i-th second),
    /// re-based from the absolute-second accumulator. Buckets outside the window fold into the nearest edge so
    /// the series always sums to the entity's total damage. Empty when the window is non-positive or the entity
    /// dealt no damage.</summary>
    public long[] GetDpsSeries(int uid, long start, long end)
    {
        if (end <= start) return [];

        long startSecond = start / 1000L;
        long endSecond = end / 1000L;
        int length = (int)(endSecond - startSecond) + 1;
        if (length <= 0) return [];

        // ⚠️ UI 스레드(행 클릭 → 상세창 그래프)에서도 들어온다. 파서가 매 초 새 키를 꽂는 사전이라
        // 락 없이 열거하면 예외가 난다 — _cacheGate 주석 참조.
        var series = new long[length];
        lock (_cacheGate)
        {
            if (!_cachedDpsBuckets.TryGetValue(uid, out Dictionary<long, long>? buckets) || buckets.Count == 0)
            {
                return [];
            }

            foreach (KeyValuePair<long, long> bucket in buckets)
            {
                int idx = (int)Math.Clamp(bucket.Key - startSecond, 0, length - 1);
                series[idx] += bucket.Value;
            }
        }

        return series;
    }

    /// <summary>[start, end] 창의 이 플레이어 스킬 시전들, 시각 오름차순(이름 포함). 저장된 전투는
    /// <see cref="DpsReport.SkillCasts"/>를 대신 읽는다.
    /// <para>창을 <see cref="PreemptivePacketWindowMs"/>만큼 앞으로 넓힌다 — 피해 파이프라인이
    /// <see cref="ActivePacketCutoff"/>로 같은 여유를 주는 것과 같은 이유다. 전투 시작 앵커는 첫 피해보다
    /// 앞으로 당겨지지 않으므로(<see cref="StartAnchor"/>), 오프너 시전은 <b>상시</b> BattleStart 보다 앞선다.
    /// 그 여유가 없으면 모든 전투의 첫 스킬이 목록에서 빠진다.</para></summary>
    /// <summary>소환수 엔티티면 주인 uid, 아니면 그대로. 상세창의 버프 분류가 피해 경로와 같은 규칙을 쓰도록
    /// 내보낸다 — 표시 계층은 <see cref="DataManager"/>를 들고 있지 않다.</summary>
    public int ResolveSummonOwner(int actorId) => _dm.ResolveSummonOwner(actorId);

    public IReadOnlyList<SkillCastRow> GetSkillCasts(int uid, long start, long end)
    {
        if (end <= start) return [];
        long from = start > PreemptivePacketWindowMs ? start - PreemptivePacketWindowMs : 0L;
        return _dm.BattleSkillCasts(uid, from, end);
    }

    private Dictionary<int, List<SkillCastRow>> BuildSkillCasts(DpsReport data)
    {
        var result = new Dictionary<int, List<SkillCastRow>>();
        if (data.BattleEnd <= data.BattleStart) return result;
        foreach (User user in data.Contributors)
        {
            IReadOnlyList<SkillCastRow> casts = GetSkillCasts(user.Id, data.BattleStart, data.BattleEnd);
            if (casts.Count > 0) result[user.Id] = casts.ToList();
        }

        return result;
    }

    private Dictionary<int, long[]> BuildDpsSeries(DpsReport data)
    {
        var result = new Dictionary<int, long[]>();
        if (data.BattleEnd <= data.BattleStart) return result;
        foreach (User user in data.Contributors)
        {
            long[] series = GetDpsSeries(user.Id, data.BattleStart, data.BattleEnd);
            if (series.Length > 0) result[user.Id] = series;
        }

        return result;
    }

    private Dictionary<int, List<BuffTimeline>> BuildBuffIntervals(DpsReport data)
    {
        var result = new Dictionary<int, List<BuffTimeline>>();
        if (data.BattleEnd <= data.BattleStart) return result;
        foreach (User user in data.Contributors)
        {
            result[user.Id] = GetBuffIntervals(user.Id, data.BattleStart, data.BattleEnd);
        }

        return result;
    }

    private Dictionary<int, List<OperatingData>> BuildBuffRates(DpsReport data)
    {
        if (data.BattleEnd <= data.BattleStart) return new();
        var result = new Dictionary<int, List<OperatingData>>();
        foreach (User user in data.Contributors)
        {
            result[user.Id] = GetBuffOperatingRate(user.Id, data.BattleStart, data.BattleEnd);
        }

        return result;
    }

    private List<OperatingData> BuildBossBuffRates(DpsReport data)
    {
        int? targetId = data.Target?.Id;
        if (targetId == null) return [];
        if (data.BattleEnd <= data.BattleStart) return [];
        return GetBuffOperatingRate(targetId.Value, data.BattleStart, data.BattleEnd);
    }

    /// <summary>
    /// nDPS/rDPS for every contributor in <paramref name="data"/>. Prefers the frozen snapshot on a SAVED
    /// report and otherwise recomputes live, the same way the buff tabs and the DPS graph do — a saved report
    /// carries no packets and its buff repository has been pruned, so a live recompute there would silently
    /// under-count.
    /// </summary>
    public Dictionary<int, DpsMetricResult> GetDpsMetrics(DpsReport data)
    {
        if (data.DpsMetrics.Count > 0)
        {
            return data.DpsMetrics;
        }

        return BuildDpsMetrics(data, null);
    }

    private Dictionary<int, DpsMetricResult> BuildDpsMetrics(
        DpsReport data, Dictionary<int, Dictionary<string, AnalyzedSkill>>? skillsOverride)
    {
        long durationMs = data.BattleEnd - data.BattleStart;
        if (durationMs <= 0 || data.Contributors.Count == 0)
        {
            return new Dictionary<int, DpsMetricResult>();
        }

        bool frozen = data.BuffRates.Count > 0;
        // ⚠️ 이 메서드는 UI 스레드에서도 들어온다(GetDpsMetrics ← 오버레이/상세창). 아래 계산은 길고 그 내내
        // 파서는 _cachedSkillDetails 에 새 액터/스킬 키를 꽂으므로, 산 사전을 그대로 들고 가면 열거 중
        // 예외가 난다. 락 안에서 <b>구조 스냅샷</b>만 떠서 나간다 — _cacheGate 주석 참조.
        Dictionary<int, Dictionary<string, AnalyzedSkill>> skills =
            skillsOverride is { Count: > 0 } ? skillsOverride
            : data.SkillDetailsSnapshot.Count > 0 ? data.SkillDetailsSnapshot
            : SnapshotSkillDetails() is { Count: > 0 } cachedSkills ? cachedSkills
            : BuildSkillDetails(data);

        // 버킷 기준선의 스탯 절반. 미터가 스탯시트를 갖는 사람은 본인뿐이라 파티원에게도 같은 값을 쓴다 —
        // 게임이 남의 스탯창을 방송하지 않기 때문이다. 이 모델에서 가장 약한 전제이고, BuffGainContext 주석에
        // 그렇게 적어 뒀다. 그래도 종전(버킷이 빈 것처럼 100으로 나누기)보다는 훨씬 낫고, 틀리는 방향이 한쪽으로
        // 정해져 있다(더 잘 갖춘 파티원일수록 실제 버킷이 커서 살짝 과대평가된다).
        BuffGainContext statBaseline = _dm.PlayerStats.Current?.GainBaseline() ?? BuffGainContext.Default;

        var participants = new List<MetricParticipantInput>(data.Contributors.Count);
        foreach (User user in data.Contributors)
        {
            List<OperatingData> rows = frozen
                ? data.BuffRates.GetValueOrDefault(user.Id) ?? []
                : GetBuffOperatingRate(user.Id, data.BattleStart, data.BattleEnd);

            // 배타 쌍을 정직하게 값매김하려면 "겹쳤는가"를 알아야 하고, 그건 가동률이 아니라 구간이 답한다.
            // 같은 그룹 키로 만들어진 타임라인이라 (표시 base, 시전자)로 1:1 대응한다.
            List<BuffTimeline> lanes = frozen
                ? data.BuffIntervals.GetValueOrDefault(user.Id) ?? []
                : GetBuffIntervals(user.Id, data.BattleStart, data.BattleEnd);
            var spans = new Dictionary<(int, int), IReadOnlyList<(long Start, long End)>>();
            foreach (BuffTimeline lane in lanes)
            {
                spans[(DataManager.BuffDisplayBase(lane.Code), lane.ActorId)] = lane.Spans;
            }

            List<MetricBuffInput> buffs = rows.Select(r => ToMetricBuff(r, bossScope: false, spans)).ToList();
            DpsInformation info = data.Information.GetValueOrDefault(user.Id) ?? new DpsInformation();

            participants.Add(new MetricParticipantInput(
                user.Id,
                info.Dps,
                info.Amount,
                buffs,
                GrantedDamage(skills.GetValueOrDefault(user.Id)),
                user.Job is { } job ? JobClassInfo.BasicSkillCode(job) / 1_000_000 : 0,
                WithMeasuredRates(statBaseline, skills.GetValueOrDefault(user.Id))));
        }

        List<OperatingData> bossRows = frozen
            ? data.BossBuffRates
            : data.Target is { } target
                ? GetBuffOperatingRate(target.Id, data.BattleStart, data.BattleEnd)
                : [];

        return DpsMetrics.Compute(
            participants,
            bossRows.Select(r => ToMetricBuff(r, bossScope: true)).ToList(),
            _dm.BuffValues,
            durationMs / 1000.0);
    }

    /// <summary>
    /// Overlay THIS participant's own 판정 비율 onto the shared stat baseline.
    ///
    /// <para>강타·완벽·치명타 buffs add percentage points of PROC RATE, and what a point is worth depends
    /// entirely on the rate it lands on top of — <c>Δp/(1+p)</c> for 강타, and nothing at all once the rate is
    /// at 100%. Unlike the stat half, this does not have to be assumed: every one of these judgements is a flag
    /// on that player's own damage packets, so the rate is measured on exactly the person the buff was applied
    /// to.</para>
    ///
    /// <para>Denominators follow the same rule the combat detail uses: 치명타 is a field on every hit so it goes
    /// over <see cref="AnalyzedSkill.Times"/>, while 강타/완벽/방향 판정 only exist on flagged direct hits and so
    /// go over <see cref="AnalyzedSkill.FlaggedTimes"/>. A rate with no sample keeps the baseline's value rather
    /// than becoming 0 — "no evidence" is not "never procs".</para>
    /// </summary>
    private static BuffGainContext WithMeasuredRates(
        BuffGainContext baseline, Dictionary<string, AnalyzedSkill>? skills)
    {
        if (skills is null || skills.Count == 0)
        {
            return baseline;
        }

        long times = 0, crits = 0, flagged = 0, smites = 0, perfects = 0, fronts = 0;
        foreach (AnalyzedSkill skill in skills.Values)
        {
            times += skill.Times;
            crits += skill.CritTimes;
            flagged += skill.FlaggedTimes;
            smites += skill.DoubleTimes;
            perfects += skill.PerfectTimes;
            fronts += skill.FrontTimes;
        }

        BuffGainContext ctx = baseline;
        if (times > 0)
        {
            ctx = ctx with { CritRate = Math.Clamp(crits / (double)times, 0.0, 1.0) };
        }

        if (flagged > 0)
        {
            ctx = ctx with
            {
                SmiteRate = Math.Clamp(smites / (double)flagged, 0.0, 1.0),
                PerfectRate = Math.Clamp(perfects / (double)flagged, 0.0, 1.0),
                FrontRate = Math.Clamp(fronts / (double)flagged, 0.0, 1.0),
            };
        }

        return ctx;
    }

    private static MetricBuffInput ToMetricBuff(
        OperatingData row,
        bool bossScope,
        IReadOnlyDictionary<(int Base, int Actor), IReadOnlyList<(long Start, long End)>>? spans = null) => new(
        row.Code,
        // The display base, not OperatingData.BaseCode: 대지의 축복 collapses to 17400000 there, which is
        // 대지의 징벌's base — the same key the healer's own DoT uses. The display base keeps them apart, and
        // it is what both the synergy catalog and the exclusive-pair table are written against.
        DataManager.BuffDisplayBase(row.Code),
        row.ActorId,
        row.OperatingRate,
        row.Level,
        bossScope,
        spans is not null && spans.TryGetValue((DataManager.BuffDisplayBase(row.Code), row.ActorId), out var s)
            ? s
            : null);

    /// <summary>Damage on THIS player's meter that another class's effect dealt through them — 검성 흡혈의 검's
    /// 착취 and 치유성 대지의 축복's 추가 피해, which arrive as ordinary damage packets under the granting class's
    /// skill code. Only counted when the matching buff on this player came from SOMEONE ELSE: the granting
    /// class's own hits carry the same code and are their own damage.</summary>
    private static Dictionary<int, long> GrantedDamage(Dictionary<string, AnalyzedSkill>? skills)
    {
        var granted = new Dictionary<int, long>();
        if (skills == null || skills.Count == 0)
        {
            return granted;
        }

        foreach (AnalyzedSkill skill in skills.Values)
        {
            int source = PartySynergyCatalog.GrantedDamageSource(skill.SkillCode);
            if (source == 0) continue;

            // 여기서는 거르지 않는다. 누구 몫인지는 DpsMetrics.SplitGrant 가 (버프 시전자 → 없으면 직업) 으로
            // 가른다.
            //
            // 종전에는 "이 사람에게 그 버프 행이 있나"를 게이트로 썼는데, 실측(2026-09-01)에서 파티원 전원이
            // 대지의 축복 추가 피해를 맞는데도 버프 행은 시전자에게만 있는 전투가 나왔다(호법성의 질풍의 권능이
            // 적용을 막은 파티). 그 게이트가 피해를 수집 단계에서 통째로 버려, 치유성의 넘긴 피해가 0이 됐다.
            granted[source] = granted.GetValueOrDefault(source) + skill.DamageAmount + skill.DotDamageAmount;
        }

        return granted;
    }

    /// <summary>
    /// Invoked with the frozen DpsLog each time a battle is saved (Kotlin called StatsUploadQueue
    /// here directly). Left null in replay/headless-without-upload so the DPS golden is unaffected;
    /// the live app wires it to the stats upload queue.
    /// </summary>
    public Action<DpsLog>? OnBattleLogged { get; set; }

    private void SaveRecentBattleLog()
    {
        // 얼리는 자리는 여기 하나다. 저장 경로가 셋이라(타깃 전환·전투 종료·종료 드레인) 전이 지점에만 꽂으면
        // 반드시 하나가 샌다 — 실제로 '전멸 직후 미터를 닫는' 경로가 그렇게 새고 있었다.
        if (_recentData.Target is { } saving)
        {
            FreezeTargetHp(saving.Id);
        }

        Dictionary<int, Dictionary<string, AnalyzedSkill>>? liveSkills;
        lock (_cacheGate) // 규칙: 이 사전은 읽기도 락 안에서 (_cacheGate 주석). BuildSkillDetails 는 락 밖으로 뺀다.
        {
            liveSkills = _cachedSkillDetails.Count > 0 ? CloneSkillDetails(_cachedSkillDetails) : null;
        }

        Dictionary<int, Dictionary<string, AnalyzedSkill>> skillDetails =
            liveSkills ?? BuildSkillDetails(_recentData);
        Dictionary<int, List<OperatingData>> buffRates = BuildBuffRates(_recentData);
        List<OperatingData> bossBuffRates = BuildBossBuffRates(_recentData);
        // Built here, BEFORE the SaveBattleLog call below prunes the buff repository (DataManager.PruneBefore):
        // GetBuffIntervals reads that same repo, so a post-prune snapshot would under-count. GetDpsSeries reads
        // the still-live _cachedDpsBuckets (cleared only when the next battle starts).
        Dictionary<int, long[]> dpsSeries = BuildDpsSeries(_recentData);
        Dictionary<int, List<BuffTimeline>> buffIntervals = BuildBuffIntervals(_recentData);
        // 시전 타임라인도 같은 이유로 여기서 — SaveBattleLog 가 시전 저장소도 함께 자른다.
        Dictionary<int, List<SkillCastRow>> skillCasts = BuildSkillCasts(_recentData);

        _recentSkillDetails = skillDetails;
        _recentBuffRates = buffRates;
        _recentBossBuffRates = bossBuffRates;

        // Freeze the rates onto the live report too, so the post-battle detail (which holds this same
        // _recentData) reads the snapshot instead of recomputing against the now-pruned buff repository.
        _recentData.BuffRates = buffRates;
        _recentData.BossBuffRates = bossBuffRates;
        _recentData.DpsSeries = dpsSeries;
        _recentData.BuffIntervals = buffIntervals;
        _recentData.SkillCasts = skillCasts;
        // 판정 교차표도 같은 이유로 여기서 얼린다 — 저장 리포트는 Packets = null 이고 누적기는 다음 전투가
        // 시작될 때 비워지므로, 여기서 안 얼리면 업로드 시점에 만들 방법이 원리적으로 없다.
        _recentData.SelfJudgment = _cachedJudgment.Build(
            _recentData.Target?.Mob.Code ?? 0, _dm.PlayerStats.Current);
        // Freeze AFTER the rates above are on the report: BuildDpsMetrics reads THOSE (plus the skill table
        // passed in) rather than the buff repository, which SaveBattleLog is about to prune. The skill table
        // is handed over explicitly instead of being parked on _recentData.SkillDetailsSnapshot — that field
        // means "this is a saved report" to BattleDetails, and setting it on the live report would change
        // which source the post-battle detail reads from.
        _recentData.DpsMetrics = BuildDpsMetrics(_recentData, skillDetails);

        // 이 전투의 파티 문맥도 함께 얼린다. 표시 계층의 이름 복구는 파티가 있어야 동작하는데, 종전에는
        // 리포트만 얼고 파티는 "지금"의 것을 봤다 — 전투 후 파티를 나가면(또는 로스터 TTL이 만료되면) 같은
        // 전투를 기록에서 다시 열었을 때 복구가 죽어 참가자 한 명이 사라졌다. User는 반드시 딥카피한다:
        // 저장소의 산 객체를 그대로 들고 있으면 나중에 그 uid가 다른 플레이어에게 재발급될 때 얼린 스냅샷까지
        // 남의 이름으로 리페인트된다(전투 후 신원 둔갑과 같은 계열).
        _recentData.PartySnapshot = _dm.PartyRoster(RosterRecoveryWindowMs).Select(u => u.Copy()).ToList();
        _recentData.PartyIdentitiesSnapshot = _dm.PartyRosterIdentities(RosterRecoveryWindowMs)
            .Select(m => new RosterMember { Nickname = m.Nickname, Server = m.Server, Slot = m.Slot })
            .ToList();

        DpsLog log = _dm.SaveBattleLog(_recentData, skillDetails, buffRates, bossBuffRates);
        OnBattleLogged?.Invoke(log);
        _recentData.Packets = null;
    }

    public void ResetDataStorage()
    {
        if (!_recentData.IsEmpty() && !_recentDataSaved)
        {
            SaveRecentBattleLog();
            _recentDataSaved = true;
        }

        _dm.FlushPacket();
        _currentTarget = -1;
        _currentBattleRevision = _dm.CurrentBattleRevision();
        _recentData = new DpsReport();
        _recentSkillDetails = new();
        _recentBuffRates = new();
        _recentBossBuffRates = [];
        _recentDataSaved = false;
        ResetCache();
    }

    /// <summary>허수아비 DPS 초기화: clear ONLY the live dummy report so the next hit re-tests immediately. Unlike
    /// <see cref="ResetDataStorage"/> it never saves the current run (a dummy run is not history) and unlike the
    /// soft reset (<see cref="DataManager.ResetBattleRecords"/>) it does NOT flush saved history, recognized
    /// users, the mob map, or the party roster — only the in-flight packets + live report are dropped, and the
    /// duration cutoff latch is re-armed. Runs on the consumer thread (via MeterEngine.RequestDummyReset).</summary>
    public void ResetDummyBattle()
    {
        _dm.FlushPacket();          // drop in-flight packets, set CurrentTarget = -1 (FlushPacket does NOT bump revision)
        _dm.ResetDummyCutoff();     // re-arm: the next dummy hit opens a fresh window
        _currentTarget = -1;
        _currentBattleRevision = _dm.CurrentBattleRevision(); // stay in lockstep (do NOT zero — a mismatch reads as a restart)
        _recentData = new DpsReport();
        _recentSkillDetails = new();
        _recentBuffRates = new();
        _recentBossBuffRates = [];
        _recentDataSaved = false;
        ResetCache();
    }

    public void HardReset()
    {
        _dm.HardReset();
        _streamResetCallback?.Invoke();
        _currentTarget = -1;
        _currentBattleRevision = 0;
        _recentData = new DpsReport();
        _recentSkillDetails = new();
        _recentBuffRates = new();
        _recentBossBuffRates = [];
        _recentDataSaved = false;
        ResetCache();
    }

    /// <summary>
    /// Soft reset for the user "초기화" button: clears the live report + cached battle and the saved history
    /// (via <see cref="DataManager.ResetBattleRecords"/>) but KEEPS recognized characters/executor, the
    /// mob-instance map, and the party roster, so combat info still appears on the next pull inside a dungeon
    /// with no zone reload. Mirrors <see cref="HardReset"/> exactly except it does not wipe users. The
    /// in-progress battle is intentionally NOT saved (the reset is a deliberate full-ledger wipe).
    /// </summary>
    public void ResetKeepingCharacters()
    {
        _dm.ResetBattleRecords();
        _streamResetCallback?.Invoke();
        _currentTarget = -1;
        _currentBattleRevision = 0; // lockstep with DataManager._battleRevision = 0 (else GetDps mis-detects a restart)
        _recentData = new DpsReport();
        _recentSkillDetails = new();
        _recentBuffRates = new();
        _recentBossBuffRates = [];
        _recentDataSaved = false;
        ResetCache();
    }
}

/// <summary>
/// "이 무명 전투행이 본인인가"를 묻는 <b>두 층이 공유하는</b> 가드. 데이터층(<see cref="DpsCalculator"/>의
/// 로스터 1:1 복구)과 표시층(<c>OverlayRowBuilder</c>의 lost-executor 복구)은 같은 질문을 서로 다른 입력으로
/// 푸는데, 종전에는 문턱과 외부인 판정이 <b>표시층에만</b> 있었다 — 그래서 화면에서는 거부된 조합이
/// 저장소에는 <c>SaveNickname(isExecutor: true)</c>로 영구히 기록됐다(identity-roster~S2). 두 층이 같은 답을
/// 내도록 판정을 여기 한 곳에 둔다.
/// <para>App.Core 가 아니라 Data 에 사는 이유: App.Core → Data 단방향 참조라 반대 방향은 순환이 된다.</para>
/// </summary>
public static class SelfRecoveryGuards
{
    /// <summary>본인 후보가 1등 딜러 대비 최소로 차지해야 하는 <b>RAW 피해량</b> 비중. 스치듯 딜을 넣은
    /// 펫/NPC/지나가던 엔티티가 "무명 하나"가 되는 순간을 본인으로 확정하지 않기 위한 문턱이다.
    /// ⚠️ nDPS/rDPS 같은 대체 지표로 재지 마라 — 모델 추정이 섞인 값이라 되돌리기 어려운 신원 판단의
    /// 근거로 쓸 수 없다.</summary>
    public const double MinDamageShare = 0.2;

    /// <summary>파티에 없는 <b>이름 달린</b> 딜러가 이 전투에 있는가.
    /// <para>본인이 재인스턴스되는 곳은 던전이고 거기 있는 플레이어는 파티뿐이다. 이름이 확인된 비파티
    /// 딜러가 있다는 것은 여기가 공개 씬(필드보스 zerg)이라는 증거이고, 그때 "무명 하나 = 본인"은 성립하지
    /// 않는다 — 본인은 아직 딜을 안 넣은 구경꾼일 수 있다. 이 가드를 빼면 낯선 사람이 본인으로 칠해진다.</para>
    /// <para>무명 행은 애초에 판단 근거가 아니므로 세지 않는다. <paramref name="party"/>의 uid 0 항목은
    /// "이름만 아는 파티원"(RAW 0x9702 로스터)이라 uid 대조에서는 빠지고 (닉,서버) 대조에만 쓰인다.</para></summary>
    /// <param name="exemptUids">판정에서 빼는 uid — 본인 후보와 이미 인식된 본인.</param>
    public static bool HasNamedOutsider(
        IReadOnlyList<(int Uid, string? Nickname, int Server)> damagers,
        IReadOnlyList<(int Uid, string? Nickname, int Server)> party,
        params int[] exemptUids)
    {
        var partyUids = new HashSet<int>();
        var partyIdentities = new HashSet<(string, int)>();
        foreach ((int uid, string? nickname, int server) in party)
        {
            if (uid != 0)
            {
                partyUids.Add(uid);
            }

            if (!string.IsNullOrWhiteSpace(nickname))
            {
                partyIdentities.Add((nickname!, server));
            }
        }

        foreach ((int uid, string? nickname, int server) in damagers)
        {
            if (string.IsNullOrWhiteSpace(nickname)
                || Array.IndexOf(exemptUids, uid) >= 0
                || partyUids.Contains(uid)
                || partyIdentities.Contains((nickname!, server)))
            {
                continue;
            }

            return true;
        }

        return false;
    }
}
