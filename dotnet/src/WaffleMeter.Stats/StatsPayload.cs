using System.Text.Json.Serialization;

namespace WaffleMeter.Stats;

// Verbatim port of Kotlin stats.StatsPayload DTOs. Serialized with StatsJson (camelCase, nulls
// omitted). The literal "public" key keeps its name via JsonPropertyName.

public sealed record StatsOwnCharacter(
    bool Detected,
    int Id = 0,
    string? Nickname = null,
    int Server = -1,
    string? Job = null,
    int Power = 0);

public sealed record StatsUploadPayload(
    int SchemaVersion,
    string ClientVersion,
    string BattleHash,
    string IdentityHashVersion,
    string ConsentVersion,
    long UploadedAt,
    StatsCharacterPayload Character,
    StatsEncounterPayload Encounter,
    StatsBattlePayload Battle,
    StatsPartyCompositionPayload PartyComposition,
    IReadOnlyList<StatsParticipantPayload> Participants,
    StatsResultPayload Result,
    IReadOnlyList<StatsSkillPayload> Skills,
    IReadOnlyList<StatsBuffPayload> Buffs,
    IReadOnlyList<StatsBuffPayload> BossDebuffs,
    // The uploader's (self) combat-detail DPS graph source. Null (omitted) when the frozen snapshot is absent
    // (e.g. a pre-save/live report) — the web then hides the chart.
    // Since v6 EVERY participant carries its own series in Participants[].DpsSeries; this root field stays as the
    // uploader's copy so a web that only reads v5 keeps its chart. Same source and same downsampling as the
    // uploader's participant row, so the two never disagree.
    StatsDpsSeriesPayload? DpsSeries = null,
    IReadOnlyList<StatsSelfBuffIntervalPayload>? SelfBuffIntervals = null,
    // The uploader's judgment cross-tabs. Root-scoped, like SelfBuffIntervals and for the same reason: the
    // participant-fold path merges integer counters by hand and has no rule for a nested block, so a battle
    // where the uploader's uid split across a zone boundary would lose it silently. Null when the local player
    // dealt no measurable damage.
    StatsSelfJudgmentPayload? SelfJudgment = null);

/// <summary>
/// What the uploader's own hits observed, keyed by the stat they had AT THE MOMENT OF EACH HIT.
///
/// <para><b>Why this exists.</b> A boss's 강타 저항 / 막기 / 치명타 저항 are server-side: not on the wire, not in
/// the client. They are only inferable from "a player with a known stat observed this proc rate against this
/// boss". Observed rates alone cannot do it — the model is subtractive
/// (<c>관측 = clamp(내 스탯 − 대상 저항, 0, 1)</c>), so without the attacker's stat there are two unknowns and one
/// equation. The stat dictionary is broadcast for the LOCAL PLAYER ONLY, which is why this block covers the
/// uploader alone and why party rows carry nothing new.</para>
///
/// <para><b>Why histograms rather than a number.</b> The stats move inside a battle — a measured session ran
/// 강타 from 52.44% to 103.44% as buffs came and went — so a per-battle mean is both biased (hits are not
/// spread evenly across that range) and, being a mean, effectively the player's stat. A bucketed cross-tab is
/// unbiased for the estimator and coarser about the individual.</para>
/// </summary>
/// <param name="EligibleHits">Own direct hits that could carry a judgment at all (non-DoT, not folded in from a
/// summon, switch-type 5/6/7). The shared denominator; each axis's <c>n</c> is this minus hits that had no stat
/// reading yet, so <c>n / eligibleHits</c> is the coverage of the reading.</param>
/// <param name="StampAgeP50Ms">Median staleness of the stat reading behind a hit. Bounds a known bias: a buff
/// lands before the sheet reports it, so some hits carry a pre-buff stat, which pulls the estimate down.</param>
/// <param name="FreshHits">Hits whose stat reading was ≤500 ms old — a subset large enough to re-fit on and
/// measure that bias instead of assuming it.</param>
/// <param name="Trimmed">Which sections were shortened to fit the size budget, comma-separated. Without it
/// "this player had no such hits" and "we dropped the rows" are indistinguishable.</param>
public sealed record StatsSelfJudgmentPayload(
    int TargetMobCode,
    int EligibleHits,
    int StampAgeP50Ms,
    int FreshHits,
    StatsJudgmentAxisPayload Smite,
    StatsJudgmentAxisPayload Perfect,
    StatsJudgmentAccuracyPayload Accuracy,
    StatsJudgmentCritPayload Crit,
    StatsSelfJudgmentStatsPayload Stats,
    string? Trimmed = null);

/// <summary>One proc axis. <c>bins</c> rows are <c>[pct2, pos, n, o]</c> — pct2 is the 2-percentage-point
/// bucket floor of the driving stat, pos is the facing byte (0 none / 1 back / 2 front), n hits, o procs.
/// <para><c>sum(bins.n) == n</c> and <c>sum(bins.o) == hits</c> always hold; a violation means the meter
/// double-counted, which is otherwise symptomless (the ratio stays right, the interval silently narrows).</para>
/// <para><paramref name="LateBins"/> holds hits past 600 s of combat, empty in every fight measured (longest
/// 230.6 s). It exists because a client effect drops a target's 강타 저항 by 100%p after ten minutes, which
/// would change the answer mid-battle if long-form content ever ships.</para></summary>
public sealed record StatsJudgmentAxisPayload(
    int N,
    int Hits,
    IReadOnlyList<int[]> Bins,
    IReadOnlyList<int[]>? LateBins = null);

/// <summary>막기 axis. <c>bins</c> rows are <c>[acc25, pos, n, o]</c> (acc25 = 25-point bucket floor of 추가
/// 명중), o = hits the target blocked.
/// <para>Back hits cannot be blocked at all — 0 of 267,357 measured, and the client says so outright — so
/// <c>pos == 1</c> rows are expected to have o = 0 and the server drops them from the denominator. A non-zero
/// one is evidence the game's rules changed.</para>
/// <para><paramref name="BySkill"/> rows are <c>[rawSkillCode, n, o]</c>: some skills ignore block entirely
/// (the client marks 343 of them) and mixing those into the denominator understates the rate. The server learns
/// which from these counts rather than the meter shipping a catalog that goes stale. Two aggregate rows may
/// appear for trimmed entries: code 0 = trimmed rows that never blocked, code −1 = trimmed rows that did.</para>
/// </summary>
public sealed record StatsJudgmentAccuracyPayload(
    int N,
    int Hits,
    IReadOnlyList<int[]> Bins,
    IReadOnlyList<int[]> BySkill);

/// <summary>치명타 axis. <c>bins</c> rows are <c>[rating100, rawSkillCode, n, o]</c>, rating100 = the 100-point
/// bucket floor of <c>기본 치명타 × (1 + 치명타 증가율)</c>.
/// <para>The skill code is a bin key here and on no other axis, for two measured reasons: guaranteed-crit
/// skills are 13.6% of boss direct hits and would push the rate above the client's declared 80% cap, and below
/// that cap the crit rate genuinely differs by skill (chi2/df 2.96, versus 0.63 above it).</para>
/// <para><paramref name="Sw4"/> is <c>[n, o]</c> for switch-type-4 hits — the ones with no judgment region.
/// <b>Never merge it into the bins.</b> Two independent measurements of that population disagree with each
/// other (40.8% vs 63.0%) and both differ structurally from the ~80% the flagged hits sit at, which is evidence
/// of a different regime rather than noise. It ships so the decision to exclude it stays falsifiable.</para>
/// </summary>
public sealed record StatsJudgmentCritPayload(
    int N,
    int Hits,
    IReadOnlyList<int[]> Bins,
    IReadOnlyList<int> Sw4);

/// <summary>
/// Judgment-adjacent stats that do not move within a battle, sent once.
///
/// <para>These are the terms players add up when they say "명중컷", plus the crit scaling term. The meter
/// cannot tell whether they enter the game's block calculation: within one character they never change, so
/// their coefficient is unidentifiable locally and is absorbed into the cut. Only a population that varies them
/// can separate them, which is the sole reason they are sent. (The one term that WAS testable — 철벽 관통, id
/// 449 — was measured and rejected, chi2 50.9.)</para>
///
/// <para><paramref name="Src"/> carries more weight than it looks. 318/110/256 ride reliably only on a FULL
/// sheet (0x3649), which the game sends on character/zone load — a session where the meter started after the
/// game has them missing, and that absence is not random (it tracks how the player launches things). Weight on
/// this rather than reading an absent value as zero.</para>
/// </summary>
/// <param name="Src">"full", "delta", or "none".</param>
/// <param name="Mask">Presence bits, so "0" and "not captured" stay distinguishable.</param>
public sealed record StatsSelfJudgmentStatsPayload(
    string Src,
    int Mask,
    int Acc318,
    int Pve110,
    int AccInc427,
    int BlockPierce256,
    int CritInc429,
    int? BackCrit100 = null,
    int? FrontCrit591 = null);

/// <summary>One combatant's per-second damage series. <see cref="Damage"/>[i] = damage dealt during the i-th
/// sample from battle start, and <see cref="Step"/> = seconds per sample — so sample i covers seconds
/// <c>[i*Step, (i+1)*Step)</c> and its value is that window's damage SUM (never an average: the web divides by
/// <c>samples * step</c> itself to get DPS).
/// <para><see cref="Step"/> is 1 for any battle short enough to send raw; longer ones are folded into Step-second
/// buckets so no single series exceeds the site's sample cap. A trailing partial bucket is dropped rather than
/// sent as if it were a full one, so the series can end up to <c>Step-1</c> seconds short of the battle.</para></summary>
public sealed record StatsDpsSeriesPayload(int Step, IReadOnlyList<long> Damage);

/// <summary>One of the uploader's own class(딜) buffs and when it was up. <see cref="Spans"/> is a flat
/// <c>[start, end, start, end, ...]</c> list of whole-second offsets from battle start (merged/clamped upstream).
/// Only the uploader's own-class buffs are sent — consumables (scrolls/food/drinks) and other players' buffs are
/// excluded (mirrors the meter's 내 버프 filter / <c>BuffSource == "self"</c>).</summary>
public sealed record StatsSelfBuffIntervalPayload(int BaseCode, string Name, IReadOnlyList<int> Spans);

public sealed record StatsCharacterPayload(
    string IdentityHash,
    string Nickname,
    int Server,
    string? Job,
    int Power,
    [property: JsonPropertyName("public")] bool Public);

/// <summary>Which encounter this battle was. <see cref="MobCode"/> is the authority — the server resolves the
/// dungeon, difficulty/stage and boss order from it, because a boss mobCode is unique per (dungeon, variant).
/// The rest is what the meter's own catalog made of that code: redundant when the two agree, and a readable
/// record of what the client believed when they don't.
/// <para><see cref="Stage"/> is a STRING ("1".."4"), not a number — the server's schema types it as nullable
/// text alongside <see cref="Difficulty"/>, and a numeric one is rejected outright.</para></summary>
public sealed record StatsEncounterPayload(
    int MobCode,
    string BossName,
    string? DungeonName = null,
    string? Category = null,
    string? Difficulty = null,
    string? Stage = null,
    int? BossIndex = null,
    StatsTrialDifficultyPayload? Trial = null);

/// <summary>
/// The 시련 난이도 for a trial run. Sent only for 시련: 바크론의 공중섬, where every level 4~16 shares one
/// dungeonId and one set of boss mobCodes — so unlike every other dungeon, the encounter identity alone does
/// NOT say which fight this was.
/// <para>The party sets four knobs, each 1~4, and the game shows their sum. Three are readable today, so
/// <see cref="Level"/> is null more often than not and <see cref="LevelMin"/>/<see cref="LevelMax"/> bound
/// it instead. <see cref="BossBuff"/> is the one that matters for a DPS percentile — it alone scales the
/// boss (max HP x1.0/1.3/1.7/2.2) — so it is worth bucketing on even while the total is uncertain.</para>
/// </summary>
public sealed record StatsTrialDifficultyPayload(
    int? Level,
    int LevelMin,
    int LevelMax,
    int? Timelimit,
    int? Rebirthlimit,
    int? BossBuff,
    int? SkillUpgrade);

/// <param name="PartySize">How many people DEALT DAMAGE. Load-bearing on the site (dedupe group key, cohort
/// axis, display) — its meaning must not change.</param>
/// <param name="RosterSize">How many people were in the party/raid, from the 0x9702 roster; null when no
/// roster was captured. A different question from <paramref name="PartySize"/>: a 10-인 공대 where two members
/// never touched this boss reports partySize 8 and rosterSize 10, and one where a summon lands its own rows
/// can report partySize 12. Sent so the site can tell a real 공대 from a field pull that merely had six
/// dealers — today it infers that from partySize alone, which mislabels a zerg as an "N인 공대". Additive and
/// omitted when null, so a site that does not read it yet is unaffected (its zod objects strip unknown keys).</param>
public sealed record StatsBattlePayload(
    long StartedAt,
    long EndedAt,
    long DurationMs,
    int PartySize,
    int? RosterSize = null);

public sealed record StatsPartyCompositionPayload(
    IReadOnlyDictionary<string, int> Jobs,
    StatsSynergyPayload Synergy);

public sealed record StatsSynergyPayload(
    bool HasGuardian,
    bool HasGladiator,
    bool HasChanter,
    bool HasCleric,
    int SynergyCount);

public sealed record StatsParticipantPayload(
    string? IdentityHash,
    bool IsUploader,
    string? Job,
    int Power,
    StatsResultPayload Result,
    IReadOnlyList<StatsSkillPayload> Skills,
    IReadOnlyList<StatsBuffPayload> Buffs,
    // 10-인 공대(5+5) sub-party: PartyNumber 1 = uploader's party (slots 1-5), 2 = the other party (slots 6-10);
    // PartySlot is the raw 1-10 roster slot. Both null for a non-raid (5-인 이하) or an unmatched participant.
    int? PartyNumber = null,
    int? PartySlot = null,
    // v6: THIS participant's per-second damage series — what makes the site's combat-detail DPS graph drawable for
    // every party member instead of the uploader alone. Null (omitted, never an empty array) when the frozen
    // snapshot has nothing for them: a pre-freeze report, or damage that never reached a second bucket. Every
    // participant's series in one battle covers the same window, so they line up index-for-index.
    StatsDpsSeriesPayload? DpsSeries = null,
    // The meter's own nDPS/rDPS for this participant. The site computes the same two numbers at ingest, so this
    // is not a replacement — it is the LEVEL-AWARE version of them. The site prices a support buff from a fixed
    // snapshot table with no room for the caster's skill level (its own source: "the payload has no skill level
    // with which to make this rDPS approximation exact"), which reads 불패의 진언 at level 25 as its level-1
    // value, has no row for 질풍의 권능's rank-5 code, and none for 흡혈의 검 at all. The meter reads the level
    // off the wire and prices those from it, and it can measure 흡혈의 검/대지의 축복's shared damage directly —
    // those land as real damage packets on each party member under the granting class's skill code.
    // Null when the battle had no computable metrics (no buff data, or a zero-length window).
    StatsDpsMetricsPayload? Metrics = null);

/// <summary>One participant's buff-normalized rates, rounded to whole damage-per-second.</summary>
/// <param name="Ndps">DPS with the buffs OTHER people supplied divided out, and another class's shared damage
/// removed.</param>
/// <param name="Rdps"><paramref name="Ndps"/> plus what this participant enabled in everyone else.</param>
/// <param name="GrantedDamage">Raw damage this participant's effects dealt ON OTHER participants' meters —
/// the measured (not estimated) half of the rDPS credit.</param>
public sealed record StatsDpsMetricsPayload(long Ndps, long Rdps, long GrantedDamage);

public sealed record StatsResultPayload(
    long TotalDamage,
    long Dps,
    double PartyContribution,
    double BossHpContribution,
    int HitCount,
    double CritRate,
    double StrongRate,
    double PerfectRate,
    double BackRate,
    // Front/back are the two mutually-exclusive facing judgments; both divide by the flag-bearing hit count
    // (FlaggedTimes), matching the meter's 후방/전방 detail tiles. Optional on the wire (older schema versions
    // omitted it); the web treats an absent frontRate as "no data" (renders "-", never 0%).
    double FrontRate,
    double ParryRate,
    double BossBlockRate,
    // ---- raw judgment counters, UPLOADER ROW ONLY (null on every party member's row) ----
    // The rates above are all the meter has ever sent, and two things are wrong with them for statistical use.
    // (1) StrongRate/PerfectRate/CritRate/ParryRate divide by directHits, but 강타/완벽/막기 판정 only exist on
    // flag-bearing hits — the flagged share is a per-character build property (measured 0.717 to 0.997), so the
    // dilution cannot be corrected after the fact. (2) A rate rounded to one decimal cannot recover its
    // numerator when the denominator is unknown, and ParryRate in particular censors exactly the boundary the
    // 명중컷 estimate needs (a real 0.04% reads as 0.0).
    // These integers make both recoverable without changing the meaning of any existing field.
    //
    // FlaggedHits: direct hits that carried a judgment region (switch-type 5/6/7) — the true denominator for
    // 강타/완벽/막기. ⚠️ Always sent, even as 0: it is also the only reliable marker that a row came from a
    // judgment-aware meter (clientVersion is not per-row and does not survive report merges).
    int? FlaggedHits = null,
    int? SmiteHits = null,
    int? PerfectHits = null,
    int? CritHits = null,
    int? ParryHits = null,
    //
    // BackTimes: back hits. Block is structurally impossible from behind (0 of 267,357 measured), so the 막기
    // denominator is flaggedHits − backTimes. Using flaggedHits alone is off by up to 24.9x — an order
    // of magnitude worse than the 강타 denominator problem.
    int? BackTimes = null,
    //
    // EligibleDamage: damage dealt by hits that block could have applied to (flag-bearing and not from behind).
    // The denominator for "how much DPS would more 명중 buy" — measured share of total damage runs 0.118 to
    // 0.980 by character, so using total damage overstates the gain by up to 8.5x for back-loaded classes.
    long? EligibleDamage = null,
    //
    // Summon*: the summon-attributed share of the counters above. ResolveActor folds a summon's hits onto its
    // owner, and a summon's proc rate differs from its owner's by a pooled +8.68%p; once folded it cannot be
    // separated again. Counted before the fold so the server can subtract it from both numerator and
    // denominator — the bias correlates with class, so leaving it in manufactures class-shaped resistances.
    int? SummonFlaggedHits = null,
    int? SummonSmiteHits = null,
    int? SummonPerfectHits = null);

/// <param name="BackRate">후방 타격률 %. Divides by the FLAG-BEARING hit count, not every hit — a direction is
/// only measurable on hits that carried a special-flag region, and mixing the others in reads as an
/// artificially low rate. Same denominator the meter's own detail table uses, so the two agree.</param>
/// <param name="FrontRate">전방 타격률 %, same denominator as <paramref name="BackRate"/>. Front and back are
/// mutually exclusive by construction (one position byte, not a bitmask).</param>
/// <param name="ParryRate">막기(페리) 발동률 %, over every direct hit.</param>
/// <param name="Specialization">
/// Which of the five 특화 slots this skill was cast with, as the ACTIVE slot numbers (1..5) — e.g. <c>[2, 4]</c>.
/// Null when the skill carries none: only player skills (8-digit codes in the 11M..19.99M band) do, so basic
/// attacks, 테오스톤 오브, mob skills and DoT rows have no build. The game sends no specialization field — it is
/// baked into the skill code's last four digits — so this is the meter's decode of it, and the only way the
/// site can show the same five pips the meter draws.
/// </param>
public sealed record StatsSkillPayload(
    int SkillCode,
    string SkillName,
    string DamageType,
    long Damage,
    int HitCount,
    double CritRate,
    double StrongRate,
    double PerfectRate,
    double Share,
    double? BackRate = null,
    double? FrontRate = null,
    double? ParryRate = null,
    IReadOnlyList<int>? Specialization = null);

/// <param name="Category">
/// Target-derived: "buff" for a player target, "debuff" for the boss. That IS the correct taxonomy — a player
/// skill's debuff always lands on its target, never on the caster — so there is nothing better to send. The
/// datamined per-code type is not shipped because it is wrong often enough to be dangerous.
/// </param>
/// <param name="BaseCode">The 8-digit base skill code this row's rank/aspect variants collapsed to.</param>
/// <param name="Level">
/// The CASTER's skill level for this buff — the 어노멀 레벨 (1..40) the apply packet carries, or null when the
/// wire did not give one (consumables/scrolls have no level, and the tail self-validation can decline).
/// <para>Why it matters: a support buff's magnitude is linear in this level (노련한 반격 = 5.4% + 0.4%/level,
/// 불패의 진언 = 10.5% + 0.5%/level), so uptime alone cannot say how much a buffer contributed. The site's own
/// rDPS model says as much in <c>dps-metrics.ts</c> — "the payload has no skill level with which to make this
/// rDPS approximation exact" — and it currently credits a level-25 불패의 진언 with its level-1 value.</para>
/// </param>
public sealed record StatsBuffPayload(
    int BuffCode,
    string BuffName,
    double OperatingRate,
    string Scope,
    string Category,
    string? Source = null,
    string? ActorIdentityHash = null,
    int? OwnerParticipantIndex = null,
    int? ActorParticipantIndex = null,
    int? BaseCode = null,
    int? Level = null);

public sealed record StatsUploadStatus(
    bool Enabled,
    int Pending,
    int Uploaded,
    int Skipped,
    int Failed,
    string? LastPath = null,
    string? LastReason = null,
    long LastUpdatedAt = 0L,
    /// <summary>Skip reasons and how many battles each one ate this session, most frequent first.
    /// <para>Exists because <see cref="LastReason"/> is a single slot the next battle overwrites: a gate that
    /// drops EVERY battle for one character (a per-character consent that was never re-decided, an unresolved
    /// combat power) looked identical to a one-off, and the count is the only thing that separates them.</para></summary>
    IReadOnlyList<StatsSkipCount>? SkipReasons = null);

/// <summary>One skip reason and its running count. <paramref name="Reason"/> is the raw code — the settings
/// screen localizes it, and an unknown code must survive to the screen rather than be swallowed.</summary>
public sealed record StatsSkipCount(string Reason, int Count);
