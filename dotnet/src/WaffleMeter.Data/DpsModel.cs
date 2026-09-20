using System.Text.Json.Serialization;
using WaffleMeter.Capture;

namespace WaffleMeter.Data;

/// <summary>Confidence of the source that set <see cref="User.Job"/>. A higher source overrides a lower
/// one and then can't be flipped back by a lower one; within a tier the FIRST write wins. OwnSkill ranks
/// ABOVE Authoritative on purpose: in AION2 damage skills are job-locked, so a player's OWN un-folded
/// damage packet is direct, live proof of their job — more reliable than the fragile byte-after-nickname
/// jobByte parse OR a short-name official lookup that can resolve a DIFFERENT same-name character. (Summon-
/// folded foreign skills never reach OwnSkill: the caller gates on actor == packet.ActorId.)</summary>
public enum JobProvenance
{
    None = 0,
    // The snapshot jobByte (ConvertFromCode) and the official pcId lookup — both external, both first-write-
    // wins relative to each other (the live snapshot byte is not overwritten by a later name lookup).
    Authoritative = 1,
    OwnSkill = 2, // the player's own un-folded job-locked damage skill — live ground truth, corrects the above
}

/// <summary>Player. Verbatim port of Kotlin <c>entity/User.kt</c>; equality/hash by id only
/// (so a contributor set is keyed by id).</summary>
public sealed class User
{
    public int Id { get; }
    public string? Nickname { get; set; }
    public int Server { get; set; }
    public JobClass? Job { get; set; }

    /// <summary>Confidence of the source that last set <see cref="Job"/>; see <see cref="JobProvenance"/>.</summary>
    public JobProvenance JobSource { get; set; }

    /// <summary>True once <see cref="Job"/> came from something stronger than a missing value — kept as a
    /// computed alias so existing callers read the same intent (the job shouldn't be re-filled blindly).</summary>
    public bool JobAuthoritative => JobSource >= JobProvenance.Authoritative;

    /// <summary>Set <see cref="Job"/> only if <paramref name="source"/> is STRICTLY higher-confidence than
    /// the current source (so equal/lower sources can't overwrite — first-write-wins within a tier, and a
    /// player's own job-locked skill (OwnSkill) corrects a wrong jobByte/official label). Returns whether it
    /// changed. Null jobs are ignored (a neutral/unknown skill code never clears a known job).</summary>
    public bool TrySetJob(JobClass? job, JobProvenance source)
    {
        if (job == null || source <= JobSource)
        {
            return false;
        }

        Job = job;
        JobSource = source;
        return true;
    }

    public bool IsExecutor { get; set; }
    public int Power { get; set; }

    public User(int id, string? nickname = null, int server = -1, JobClass? job = null, bool isExecutor = false, int power = 0)
    {
        Id = id;
        Nickname = nickname;
        Server = server;
        Job = job;
        IsExecutor = isExecutor;
        Power = power;
    }

    public override int GetHashCode() => Id;
    public override bool Equals(object? obj) => obj is User u && u.Id == Id;

    /// <summary>Kotlin data-class <c>copy()</c> — a mutable snapshot (used by the stats builder).</summary>
    public User Copy() => new(Id, Nickname, Server, Job, IsExecutor, Power) { JobSource = JobSource };
}

/// <summary>Per-player aggregate (Kotlin DpsInformation). Doubles, mutable.</summary>
public sealed class DpsInformation
{
    public double Amount { get; set; }
    public double Dps { get; set; }
    public double Contribution { get; set; }
    public double EntireContribution { get; set; }

    public DpsInformation() { }

    public DpsInformation(double amount, double dps, double contribution, double entireContribution)
    {
        Amount = amount;
        Dps = dps;
        Contribution = contribution;
        EntireContribution = entireContribution;
    }

    public void AddDamage(double damage) => Amount += damage;
}

/// <summary>Per-skill breakdown (Kotlin AnalyzedSkill). SkillCode is @Transient (not serialized).</summary>
public sealed class AnalyzedSkill
{
    public int SkillCode { get; init; }

    /// <summary>A representative FULL wire skill code (pre-normalization) for hits of this skill — its last
    /// four decimal digits carry the caster's specialization (특화), decoded by
    /// <c>SkillSpecialization.Decode</c>. Constant per actor per battle (a player does not respec mid-fight),
    /// so the last-seen raw code is representative. 0 when never set (e.g. rebuilt from an old snapshot).</summary>
    public int RawSkillCode { get; set; }

    public int DamageAmount { get; set; }
    public int DotDamageAmount { get; set; }
    public int DotTimes { get; set; }
    public int CritTimes { get; set; }
    public int Times { get; set; }
    public int BackTimes { get; set; }

    /// <summary>Front attacks (post-2026-07-01 position byte == 2). Mirror of <see cref="BackTimes"/>.</summary>
    public int FrontTimes { get; set; }

    public int PerfectTimes { get; set; }
    public int DoubleTimes { get; set; }
    public int ParryTimes { get; set; }
    public int ShardTimes { get; set; }
    public int MultiHitTimes { get; set; }

    /// <summary>Direct hits that carried a special-flag region (switch-type 6, region size ≥ 10). Only these
    /// hits can encode a back/강타/완벽/페리 판정 — switch-type-4 hits (heals/buffs/passives) have no flag byte,
    /// so back etc. are structurally unmeasurable on them. Used as the denominator for those rates so
    /// non-directional hits don't dilute them (crit stays over <see cref="Times"/> — it's a separate field
    /// present on every hit).</summary>
    public int FlaggedTimes { get; set; }

    /// <summary>피해 합계 중 <b>막기 판정이 굴러갈 수 있었던</b> 몫 — 플래그를 실은 직격(<see cref="FlaggedTimes"/>)
    /// 이면서 후방이 아닌 타격의 피해다.
    /// <para>왜 따로 세는가: 막기는 후방 공격에 <b>구조적으로 걸리지 않고</b>(실측 0/267,357, 클라 설명문도
    /// "뒤에서의 공격은 대상의 막기를 무시합니다"), 막힌 타격은 피해가 0.42~0.50배로 깎인다. 그래서 "명중을
    /// 올리면 DPS가 몇 % 오르나"의 분모는 총 피해가 아니라 <b>이 몫</b>이다. 실측 지분이 액터별 0.118~0.980
    /// (중앙 0.616)이라 총 피해로 나누면 후방 의존 직업에서 이득이 최대 8.5배 과대평가된다.</para></summary>
    public long EligibleDamage { get; set; }

    /// <summary>이 행에 접혀 들어온 <b>소환수</b> 타격 수와 그중 판정 관련 몫. <c>ResolveActor</c> 가 소환수
    /// 타격을 주인 uid 로 접기 때문에, 접힌 뒤에는 주인의 스탯으로 설명되지 않는 발동률이 주인의 분모에 섞인다
    /// — 실측 소환수 강타율이 주인과 pooled +8.68%p 어긋나고 flagged 비율도 0.521~0.967 로 따로 논다. 접기
    /// <b>전에</b> 세어 두지 않으면 사후 복원이 원리적으로 불가능하고, 이 편향은 직업과 상관돼 있어 "정령성은
    /// 강타 저항이 다르다" 류의 가짜 발견을 만든다.</summary>
    public int SummonTimes { get; set; }

    public int SummonFlaggedTimes { get; set; }

    public int SummonDoubleTimes { get; set; }

    public int SummonPerfectTimes { get; set; }

    public string? Name { get; set; }

    public AnalyzedSkill Copy() => new()
    {
        SkillCode = SkillCode,
        RawSkillCode = RawSkillCode,
        DamageAmount = DamageAmount,
        DotDamageAmount = DotDamageAmount,
        DotTimes = DotTimes,
        CritTimes = CritTimes,
        Times = Times,
        BackTimes = BackTimes,
        FrontTimes = FrontTimes,
        PerfectTimes = PerfectTimes,
        DoubleTimes = DoubleTimes,
        ParryTimes = ParryTimes,
        ShardTimes = ShardTimes,
        MultiHitTimes = MultiHitTimes,
        FlaggedTimes = FlaggedTimes,
        EligibleDamage = EligibleDamage,
        SummonTimes = SummonTimes,
        SummonFlaggedTimes = SummonFlaggedTimes,
        SummonDoubleTimes = SummonDoubleTimes,
        SummonPerfectTimes = SummonPerfectTimes,
        Name = Name,
    };
}

/// <summary>
/// Buff/debuff uptime entry (Kotlin OperatingData). One row per (base skill, name, caster) on one entity:
/// the rank/aspect variants a skill emits are merged upstream, so <see cref="OperatingRate"/> is the union of
/// every variant's applied intervals.
/// </summary>
/// <remarks>
/// Whether a row is a buff or a debuff is decided by WHO it landed on, not by buff.json's <c>type</c> field.
/// A player skill's debuff always goes on its target; nothing a player casts debuffs the caster. So a row in a
/// participant's list is a buff/self-state and a row in the boss's list is a debuff — and the datamined type is
/// not consulted, because it is demonstrably wrong (권성 폭주's codes are half-typed DeBuff while their text is a
/// pure buff, and 검성 살기 파열's DeBuff-typed codes land on the caster 100% of the time).
/// </remarks>
/// <param name="Code">A representative runtime code, preferred from buff.json so icon lookups resolve.</param>
/// <param name="BaseCode">The 8-digit base skill code the group collapsed to (see DataManager.BuffBaseCode).</param>
/// <param name="JobPrefix">
/// 11(검성)..19(권성) when the RAW runtime code sat in the 9-digit job-buff band, else 0. Derived from the raw
/// code — not from <see cref="Code"/> or <see cref="BaseCode"/>, because 8-digit mob/consumable codes like
/// 12000101(중독) would otherwise read as 수호성 and be mistaken for a self-buff.
/// </param>
/// <param name="Level">이 행이 모은 적용들 중 가장 높은 어노멀 레벨(0 = 모름). 시전자별로 이미 행이
/// 갈라져 있으므로 사실상 "그 시전자가 이 스킬을 몇 레벨로 갖고 있나"다. 전투 도중 랭크가 오르는 일은
/// 없으니 최댓값이 곧 대표값이고, 레벨을 못 읽은 적용(0)이 섞여도 끌어내리지 않는다.</param>
// ⚠ [method: JsonConstructor] — 아래 편의 생성자 때문에 공개 생성자가 둘이라, 없으면 System.Text.Json 이
// 어느 쪽을 써야 할지 몰라 역직렬화에서 터진다(저장된 전투 기록을 다시 읽는 경로).
[method: JsonConstructor]
public sealed record OperatingData(
    int Code,
    string Name,
    string? Summary,
    string? Effect,
    double OperatingRate,
    int ActorId,
    int BaseCode = 0,
    int JobPrefix = 0,
    int Level = 0)
{
    public OperatingData(int code, Buff? buff, double rate, int actorId)
        : this(code, buff?.Name ?? code.ToString(), buff?.Summary, buff?.Effect, rate, actorId) { }

    /// <summary>
    /// <see cref="JobPrefix"/>, or — for rows built without one — the prefix implied by a 9-digit job-buff
    /// <see cref="Code"/>. 0 means "not a job buff", which callers must read as "cannot be a self-buff".
    /// </summary>
    public int EffectiveJobPrefix => JobPrefix != 0
        ? JobPrefix
        : Code is >= 110_000_000 and <= 199_999_999 ? Code / 10_000_000 : 0;
}

/// <summary>스킬 시전 1회(0x3802). <see cref="TimestampMs"/>는 <b>절대</b> 캡처 시각이라 전투 창으로 잘라 쓴다
/// (초당 버킷이 절대 초를 쓰는 것과 같은 이유 — 리포트의 BattleStart 재앵커링에 흔들리지 않는다).</summary>
public sealed record SkillCast(int SkillCode, long TimestampMs, bool StartsCooldown = false);

/// <summary>표시용 시전 한 줄: 이름까지 붙인 <see cref="SkillCast"/>.
/// <para>이름을 Data 계층에서 붙이는 이유는, 정본이 <c>skills.json</c>(<c>DataManager.Skill</c>)뿐이고 표시
/// 계층은 <c>DpsCalculator</c>만 들고 있기 때문이다. 게다가 0x3802는 특화 접미가 붙은 <b>원본</b> 코드를 싣는데,
/// 무조건 base로 접으면 8종이 엉뚱한 이름이 된다(11010047 격파의 맹타 → 절단의 맹타, 11000100 긴급 회피 →
/// 검성 무기 장착 …). 그래서 원본 우선, 없을 때만 base 폴백이다.</para></summary>
/// <param name="StartsCooldown">이 발동이 쿨타임을 실제로 돌렸는가. 서버가 보낸 사실이며, 표시 계층은
/// 이걸 "확실히 나간 시전"의 표식으로만 쓴다 — 없다고 해서 안 나간 것은 아니다(쿨 없는 스킬이 다수다).</param>
public sealed record SkillCastRow(int Code, string Name, long TimestampMs, bool StartsCooldown = false);

/// <summary>Target/boss info (Kotlin MobInfo).</summary>
public sealed class MobInfo
{
    public int Id { get; }
    public Mob Mob { get; }
    // HP는 long이다 — 실측 최대가 27억대(델트라스)라 int로는 21.47억에서 포화해 게이지·기여도가 통째로 틀어진다.
    public long RemainHp { get; set; }
    public long MaxHp { get; set; }

    public MobInfo(int id, Mob mob, long remainHp = 0, long maxHp = 0)
    {
        Id = id;
        Mob = mob;
        RemainHp = remainHp;
        MaxHp = maxHp;
    }

    public MobInfo Copy() => new(Id, Mob, RemainHp, MaxHp);
}

/// <summary>Skill catalog entry (Kotlin Skill).</summary>
public sealed record Skill(long Code, string? Name);

/// <summary>Buff catalog entry (Kotlin Buff). buff.json's <c>type</c> field is deliberately not loaded — see
/// <see cref="OperatingData"/> for why it can't be trusted.</summary>
public sealed record Buff(int Code, string Name, string Summary, string Effect);

/// <summary>An applied buff/debuff interval (Kotlin UseBuff).</summary>
/// <param name="Level">어노멀 레벨(1~40, 0 = 모름) — 적용 패킷이 실어 보내는 시전자의 그 스킬 레벨.
/// <see cref="Capture.StreamProcessor"/>의 <c>ReadAbnormalLevel</c>이 자기검증으로 읽어 온 값이고, 실캡처
/// 8인 공대 기준 직업 버프 적용의 99.2%가 레벨을 달고 온다(나머지는 소모품/주문서라 직업 스킬 대역 밖).
/// <para>여기 담기 전까지 레벨은 라이브 오버레이 스토어에서만 살아남아 배타 버프 쌍 승자 판정에만 쓰였다.
/// 집계·상세·통계로 넘기려면 구간과 함께 보관해야 한다 — 시너지 버프의 효과량이 레벨 선형이라(예: 노련한
/// 반격 = 5.4% + 0.4%/레벨) 레벨 없이는 nDPS/rDPS가 랭크 스냅샷 근사에 머문다.</para></param>
/// <param name="Slot">그 대상의 버프 슬롯 번호(0 = 모름). 제거 브로드캐스트(0x382C)가 코드가 아니라
/// <b>슬롯</b>을 지목하므로, 이걸 들고 있어야 조기 해제가 왔을 때 정확히 그 인스턴스의 구간만 끊을 수 있다.
/// <para>종전에는 오버레이 사전에만 실리고 이 레코드에는 안 실렸다. 그래서 <b>오버레이는 즉시 지워지는데
/// 상세창 가동률은 선언 duration이 다 흐를 때까지 계속 세는</b> 비대칭이 있었다 — 같은 화면에서 한쪽은
/// 버프가 없다고 하고 다른 쪽은 아직 걸려 있다고 말한 셈이다. 그 값은 선형으로 nDPS/rDPS와 업로드
/// <c>OperatingRate</c>까지 따라간다(단일 원천이라 여기 하나를 고치면 셋이 같이 맞는다).</para>
/// <para>0은 fail-open이다 — 슬롯을 모르는 항목은 끊지 않고 기존 만료 로직에 맡긴다.</para></param>
public sealed record UseBuff(int SkillCode, long BuffStart, long BuffEnd, long Duration, int ActorId, int Level = 0, int Slot = 0);

/// <summary>
/// One buff/debuff's merged applied intervals on a single entity, for the combat-detail DPS-graph timeline
/// lane. Same grouping as <see cref="OperatingData"/> (base skill + name + caster) but keeps the merged
/// <see cref="Spans"/> instead of collapsing them to a single rate — the graph draws a band per span and an
/// icon so you can read which buffs were up when the DPS spiked. <see cref="Code"/> is the representative
/// display code (feed it to the icon resolver). Spans are absolute capture-clock ms, already window-clamped
/// and unioned via <see cref="BuffUptime.MergeIntervals"/>.
/// </summary>
public sealed record BuffTimeline(
    int Code,
    string Name,
    int ActorId,
    int BaseCode,
    int JobPrefix,
    IReadOnlyList<(long Start, long End)> Spans,
    int Level = 0)
{
    /// <summary><see cref="JobPrefix"/>, or the prefix implied by a 9-digit job-buff <see cref="Code"/> when the
    /// raw code carried none — mirrors <see cref="OperatingData.EffectiveJobPrefix"/> so the DPS graph's
    /// class-buff (내 버프) filter matches the buff-uptime tab. 0 = not a class buff (e.g. a consumable).</summary>
    public int EffectiveJobPrefix => JobPrefix != 0
        ? JobPrefix
        : Code is >= 110_000_000 and <= 199_999_999 ? Code / 10_000_000 : 0;
}

/// <summary>One raw packet kept for replay (Kotlin RawPacket).</summary>
public sealed record RawPacket(byte[] Data, long Timestamp);

/// <summary>
/// A DPS report for one battle (Kotlin DpsReport). Contributors keep insertion order (the Kotlin
/// MutableSet is a LinkedHashSet; the move-to-end-on-readd behavior is handled where it is mutated).
/// fakeTimeFlag and packets are @Transient (not serialized).
/// </summary>
/// <summary>얼려 둔 0x9702 로스터 항목. 원시 스냅샷에는 uid가 없어 (닉네임, 서버, 슬롯)뿐이다.
/// 튜플이 아니라 클래스인 이유 = 저장된 전투와 함께 직렬화되기 때문(ValueTuple은 필드라 JSON에 안 실린다).</summary>
public sealed class RosterMember
{
    public string Nickname { get; set; } = string.Empty;
    public int Server { get; set; }
    public int Slot { get; set; }
}

public sealed class DpsReport
{
    public List<User> Contributors { get; set; } = [];
    public long BattleStart { get; set; }
    public long BattleEnd { get; set; }
    public Dictionary<int, DpsInformation> Information { get; set; } = new();
    public MobInfo? Target { get; set; }

    /// <summary>
    /// 이 전투가 치러진 시련 난이도. 관측한 어픽스 4축을 <b>전투에 동결</b>한 값이다.
    ///
    /// <para>🔑 왜 리포트가 들고 있어야 하나: 난이도는 던전 입장 때 한 번 정해지고 <see cref="Target"/> 의
    /// 몹 코드로는 되짚을 수 없다(시련은 모든 단계가 같은 맵·같은 보스 코드를 쓴다 — 그래서 애초에 와이어에서
    /// 읽는다). 렌더 시점에 추적기를 조회하면 <b>지금</b> 들어가 있는 시련의 난이도를 어제 전투 위에 찍게 된다.
    /// 라이브만 보면 늘 맞는 것처럼 보이고, 기록을 열어야 드러난다.</para>
    ///
    /// <para>기본값(전 축 null)은 "시련이 아니거나 아무것도 못 봤다"이고 <see cref="Data.TrialDifficulty.IsTrial"/>
    /// 가 false 라, 라벨도 업로드 페이로드도 알아서 빈다.</para>
    /// </summary>
    public TrialDifficulty TrialDifficulty { get; set; }

    public bool FakeTimeFlag { get; set; }
    public List<ParsedDamagePacket>? Packets { get; set; }

    /// <summary>Frozen buff-uptime snapshot (uid -&gt; rates), populated when the battle is saved so the
    /// detail window shows the SAME rates the stats payload/web uses. The live buff repository is pruned
    /// after a battle is saved (<see cref="UseBuffRepository.PruneBefore"/>), so recomputing post-battle
    /// would under-count; this stays empty while the battle is in progress (the detail recomputes live
    /// against the intact repo then).</summary>
    public Dictionary<int, List<OperatingData>> BuffRates { get; set; } = new();

    /// <summary>Frozen boss-debuff-uptime snapshot, populated alongside <see cref="BuffRates"/>.</summary>
    public List<OperatingData> BossBuffRates { get; set; } = [];

    /// <summary>Frozen judgment cross-tabs for the LOCAL player (see <see cref="SelfJudgmentSnapshot"/>),
    /// populated when the battle is saved. Frozen for the same reason <see cref="SkillDetailsSnapshot"/> is: a
    /// saved report carries <see cref="Packets"/>=null and the live accumulator is cleared when the next battle
    /// starts, so nothing could rebuild it afterwards. Null while the battle is in progress and on any battle
    /// where the local player dealt no measurable damage.</summary>
    public SelfJudgmentSnapshot? SelfJudgment { get; set; }

    /// <summary>Frozen per-second damage series (uid -&gt; dense <c>long[]</c>, index = whole-second offset from
    /// <see cref="BattleStart"/>, value = damage dealt in that second), the source for the combat-detail DPS
    /// graph. Frozen at save time because a SAVED report carries <see cref="Packets"/>=null and the live
    /// per-second accumulator is cleared when the next battle starts — without this a history-replayed graph
    /// would be empty (same bug class as <see cref="SkillDetailsSnapshot"/>). Stays empty for the in-progress
    /// report, where the detail rebuilds the series live from <see cref="DpsCalculator.GetDpsSeries"/>.</summary>
    public Dictionary<int, long[]> DpsSeries { get; set; } = new();

    /// <summary>Frozen per-recipient buff/debuff timeline (uid -&gt; merged applied spans), the icon-lane source
    /// for the DPS graph. Populated alongside <see cref="BuffRates"/> and, like it, frozen BEFORE the live buff
    /// repository is pruned (<see cref="UseBuffRepository.PruneBefore"/>) so a post-battle or history-replayed
    /// graph keeps the intervals. Empty while the battle is in progress (the detail recomputes live via
    /// <see cref="DpsCalculator.GetBuffIntervals"/> against the intact repo).</summary>
    public Dictionary<int, List<BuffTimeline>> BuffIntervals { get; set; } = new();

    /// <summary>얼려 둔 시전 타임라인(uid -> 시각 오름차순). 저장 리포트는 <c>Packets = null</c> 이고 시전
    /// 저장소는 저장 직후 잘리므로, 여기 얼려 두지 않으면 <b>기록 재생에서 타임라인 탭이 통째로 빈다</b> —
    /// <see cref="SkillDetailsSnapshot"/>·<see cref="DpsSeries"/>가 정확히 같은 사유로 추가된 필드다.
    /// 진행 중 전투에서는 비어 있고, 그때 상세창은 계산기로 라이브 조회한다.</summary>
    public Dictionary<int, List<SkillCastRow>> SkillCasts { get; set; } = new();

    /// <summary>Frozen nDPS/rDPS per contributor, computed at save time from the frozen buff rates + skill
    /// snapshot (see <see cref="DpsCalculator.GetDpsMetrics"/>). Frozen for the same reason
    /// <see cref="BuffRates"/> is: the live buff repository is pruned right after a battle is saved, so a
    /// history-replayed battle could not recompute these — it would report every buff at 0% uptime and hand
    /// back nDPS == DPS, which reads as "nobody buffed you" rather than as "unknown".
    /// <para>Empty while the battle is in progress; the detail/overlay then recomputes live.</para></summary>
    public Dictionary<int, DpsMetricResult> DpsMetrics { get; set; } = new();

    /// <summary>Frozen per-actor skill-breakdown snapshot (uid -&gt; skillCode -&gt; analyzed skill),
    /// populated when the battle is saved. A SAVED report carries <see cref="Packets"/>=null, so
    /// <see cref="DpsCalculator.BattleDetails"/> could otherwise only rebuild from packets and would return
    /// an EMPTY skill table for any history-replayed battle (which also zeroed 누적 피해량 + every hit-rate%,
    /// since the detail's summary derives from the skill rows). Preferred by BattleDetails when non-empty;
    /// stays empty for the in-progress/live report, where BattleDetails uses the live cache instead.</summary>
    public Dictionary<int, Dictionary<string, AnalyzedSkill>> SkillDetailsSnapshot { get; set; } = new();

    /// <summary>이 전투를 함께한 파티(0x9702). uid까지 해석된 쪽과 원시 스냅샷을 <b>전투 종료 시점에</b> 얼려
    /// 둔다.
    /// <para>왜 필요한가: 표시 계층의 이름 복구(무명 행에 파티원 이름을 붙이는 경로)는 파티 문맥이 있어야
    /// 동작하는데, 리포트만 얼고 파티 문맥은 <b>지금</b>의 것을 쓰고 있었다. 그래서 전투가 끝나고 파티를
    /// 나가거나 로스터가 만료되면(TTL 5분) 같은 전투를 기록에서 다시 열었을 때 복구가 통째로 죽고, 라이브에서
    /// 보이던 참가자 한 명이 도로 사라졌다(실측: 5명 → 4명, 보이는 비중 합 89.7%). 얼려 두면 기록 재생이
    /// 현재 파티 상태에 의존하지 않는다.</para>
    /// <para>비어 있으면 진행 중/라이브 리포트라는 뜻이고, 그때는 호출자가 넘기는 라이브 로스터를 쓴다.</para></summary>
    public List<User> PartySnapshot { get; set; } = [];

    /// <summary>uid 해석 없이 그대로 얼린 0x9702 스냅샷. <see cref="PartySnapshot"/>의 상위집합으로,
    /// "이번 세션에 한 번도 못 본 파티원"이 여기에만 있다.</summary>
    public List<RosterMember> PartyIdentitiesSnapshot { get; set; } = [];

    /// <summary>표시 중인 이 리포트가 <b>끝난 전투</b>인지. 전투 대상이 없는 상태(<c>CurrentTarget == -1</c>)에서
    /// 내보내는 리포트에만 켜진다 — 진행 중 리포트는 매 틱 새 <see cref="DpsReport"/>로 만들어지므로 언제나 false다.
    /// <para>왜 별도 플래그인가: 표시 계층의 "파티 구성이 바뀌면 직전 전투 대신 로스터 프리뷰를 다시 띄운다"가
    /// <see cref="PartyIdentitiesSnapshot"/>가 비었는지로 <b>"전투가 끝났는지"</b>를 대신 판정하고 있었다. 그런데
    /// 파티 없이 혼자 잡은 전투는 스냅샷이 <b>정당하게</b> 비므로, 그 뒤 파티에 들어가도 프리뷰가 영영 뜨지 못했다
    /// (실측: 솔로 필드보스 → 파티 가입 후에도 직전 솔로 전투가 계속 표시되고, 실제 전투가 시작돼야 파티원이 보임).
    /// "파티가 있었나"(스냅샷)와 "전투가 끝났나"(이 플래그)는 서로 다른 질문이다 — 섞지 말 것.</para></summary>
    public bool BattleFinished { get; set; }

    /// <summary>The 본인(executor) uid, frozen into a SAVED report at save time (like <see cref="BuffRates"/>
    /// and <see cref="SkillDetailsSnapshot"/>). A saved report's per-row <see cref="User.IsExecutor"/> is
    /// frozen by <c>DataManager.CopyUser</c> — usually <c>false</c>, since a battle is often saved before the
    /// own character is recognized — so nothing in the report itself would otherwise mark the local player,
    /// and a history replay's "내 캐릭터" color (직업 강조 mode) would leak back to the job color. This lets a
    /// saved battle self-identify its executor regardless of the LIVE recognition state at view time. 0 = a
    /// live/in-progress report (the overlay then uses the live recognized uid / per-row IsExecutor instead).</summary>
    public int ExecutorId { get; set; }

    /// <summary>True when the current target is a classified instanced (원정/초월/성역) boss. Set live in
    /// <c>DpsCalculator.GetDps</c>. Read by the roster rescue as positive proof that this is a party scene --
    /// instanced content has no outsiders, so a nameless row in one cannot be a stranger.</summary>
    public bool TargetInstanced { get; set; }

    /// <summary>Frozen party/raid sub-group slots (uid -&gt; slot 1-8 from the 0x9702 roster), populated at
    /// save time like <see cref="ExecutorId"/>. Lets the stats upload tag each participant's sub-party for an
    /// 8-인 공대 — slots 1-4 = party 1, 5-8 = party 2; empty for a non-raid / unmatched battle. Frozen because
    /// the live roster is replaced on party change, so a delayed upload stays faithful to the battle.</summary>
    public Dictionary<int, int> PartySlots { get; set; } = new();

    /// <summary>Frozen size of the 0x9702 roster at save time — how many people were in the party/raid, which
    /// is NOT the same as how many of them dealt damage. 8 or 10 means a two-sub-party 공대; anything else is a
    /// single party. Frozen alongside <see cref="PartySlots"/> from the same snapshot.
    /// <para>The upload used to infer "is this a raid" from <c>PartySlots.Values.Any(s =&gt; s &gt; 5)</c>, which
    /// really asks "did any SECOND-party member get a slot" — so an 공대 whose party-2 members dealt no damage,
    /// or whose uploader sat in party 1, was silently treated as a normal party. 0 on battles saved before this
    /// field existed, which selects the old inference as a fallback.</para></summary>
    public int PartyRosterSize { get; set; }

    public bool IsEmpty() => Information.Count == 0;

    public void CompareBattleTime(long time)
    {
        if (BattleStart == 0L)
        {
            BattleStart = time;
            FakeTimeFlag = true;
        }

        if (BattleStart > time && FakeTimeFlag)
        {
            BattleStart = time;
        }

        if (BattleEnd < time)
        {
            BattleEnd = time;
        }
    }
}

/// <summary>A saved battle log (Kotlin DpsLog).</summary>
public sealed class DpsLog
{
    public required DpsReport Report { get; init; }
    public Dictionary<int, int> SummonMap { get; init; } = new();
    public List<RawPacket> Packets { get; init; } = [];
    public Dictionary<int, Dictionary<string, AnalyzedSkill>> SkillDetails { get; init; } = new();
    public Dictionary<int, List<OperatingData>> BuffRates { get; init; } = new();
    public List<OperatingData> BossBuffRates { get; init; } = [];
}
