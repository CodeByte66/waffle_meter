using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>The cohort a single combat row belongs to, in artifact coordinates.
/// <para><paramref name="SynergyTrusted"/> is false for an 8/10-man raid whose sub-party slots were not fully
/// resolved: its synergy mask then describes the WHOLE raid, i.e. it claims synergies the player did not
/// actually receive. Those rows must skip the synergy-bucketed rungs (R0/R1) — the server excludes them from
/// the same rungs when it builds the distribution.</para></summary>
public readonly record struct TierCohort(
    int CategoryId,
    int DungeonOrd,
    int VariantOrd,
    int BossIndex,
    int JobId,
    int SynergyCount,
    int PartyMode,
    bool SynergyTrusted,
    // Schema-v2 combat-power band. A 400k character and a 900k one were being ranked against one another;
    // this is the axis that stops that. Defaults to the whole-cohort sentinel so a cohort built without it
    // behaves exactly as it did before v2.
    int PowerBand = TierArtifact.WholeCohortBand);

/// <summary>A resolved live percentile: which rung answered, and the rounded top-percent + tier it maps to.
/// <para><paramref name="PowerBand"/> is the band of the row that ACTUALLY answered — not the one asked for.
/// A v1 artifact, a v2 row with no <c>g</c>, and a v2 whole-cohort row all land on
/// <see cref="TierArtifact.WholeCohortBand"/>, which is exactly what each of them is.</para></summary>
public readonly record struct TierEvaluation(
    int Rung, int MetricId, double TopPercent, int TierRank, int PowerBand = TierArtifact.WholeCohortBand,
    int PowerBandSize = TierArtifact.DefaultPowerBandSize)
{
    public string TierName => TierLadder.TierNameOf(TierRank);

    /// <summary>"전체 전투력 기준" / "전투력 700k–750k 미만 기준" — what this percentile was measured against.
    /// <para>폭을 값으로 들고 다니는 이유: 이 평가는 특정 아티팩트가 낸 것이고, 라벨은 <b>그 아티팩트의</b>
    /// 밴드 폭으로 읽어야 맞다. 표시 시점에 상수를 다시 가져오면 아티팩트가 갱신된 뒤 라벨만 옛 폭으로 남는다.</para></summary>
    public string ComparisonBasis => TierLadder.FormatComparisonBasis(PowerBand, PowerBandSize);
}

/// <summary>
/// Pure client-side evaluation of the tier distribution artifact — no WPF, no I/O, fully unit-testable.
/// Every constant here is a wire contract with the stats web; changing one silently desynchronises the meter
/// from the server and produces a different "상위 X.X%" for the same fight.
/// </summary>
public static class TierLadder
{
    /// <summary>Tier names, index 0 = rank 1. <b>Rank 1 is the TOP (챌린저); rank 8 is 아이언.</b></summary>
    public static readonly string[] TierNames =
        ["챌린저", "마스터", "다이아", "플래티넘", "골드", "실버", "브론즈", "아이언"];

    /// <summary>Upper bounds of each tier in top-percent, inclusive: (0,1] 챌린저 … (90,∞) 아이언.</summary>
    private static readonly double[] Boundaries = [1, 5, 10, 30, 50, 70, 90];

    /// <summary>Displayed top-percent is clamped to this range so we never print the impossible "상위 0.0%"
    /// nor the insulting "상위 100.0%". Matches the server after commit 9f7cb67 (TS and SQL both).</summary>
    public const double MinTopPercent = 0.1;
    public const double MaxTopPercent = 99.9;

    /// <summary>Server-side sample gates. A row that fails these is not in the distribution's population, so
    /// claiming a percentile for it would be asserting membership of a cohort it was never part of.</summary>
    public const long MinBattleDurationMs = 20_000;
    public const int MinPower = 400_000;

    private const int MetricDps = 0;
    private const int MetricNdps = 1;

    /// <summary>Wire metric name → id. Unknown → -1.</summary>
    public static int MetricId(string? metric) => metric switch
    {
        "dps" => MetricDps,
        "ndps" => MetricNdps,
        _ => -1,
    };

    public static string TierNameOf(int rank) =>
        rank >= 1 && rank <= TierNames.Length ? TierNames[rank - 1] : string.Empty;

    /// <summary>Round to one decimal and clamp — the ONLY way a top-percent may reach a screen or a tier map.</summary>
    public static double RoundTopPercent(double value)
    {
        double rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return Math.Min(MaxTopPercent, Math.Max(MinTopPercent, rounded));
    }

    /// <summary>Map a top-percent to a tier rank (1..8).
    /// <para>🔑 The value is rounded FIRST and the boundary test runs on the rounded number. Reversing this
    /// prints "상위 5.0%" next to a 다이아 badge, because 5.04 displays as 5.0 but tests as &gt; 5.</para></summary>
    public static int TierRankOf(double topPercent)
    {
        double p = RoundTopPercent(topPercent);
        for (int i = 0; i < Boundaries.Length; i++)
        {
            if (p <= Boundaries[i])
            {
                return i + 1;
            }
        }

        return TierNames.Length;
    }

    /// <summary>
    /// Which pool a percentile was measured against, worded exactly as the stats site words it.
    /// <para>"상위 3%" against comparable gear and "상위 3%" against everyone are different claims, and the
    /// number alone cannot tell them apart. Two players in the same party can now be measured differently —
    /// one on 2.9.1 reading a banded row, one on an older build reading the whole cohort — so without this
    /// they would compare figures that were never comparable.</para>
    /// <para>🔑 Byte-identical to the web's <c>formatTierComparisonBasis</c>. The whole point is that the two
    /// screens can be read side by side, so a wording drift defeats the feature. The number format is the
    /// web's <c>formatPower</c>: below a million, thousands with one optional decimal and a <c>k</c> ("950k");
    /// from a million up, millions with up to three decimals, trailing zeros dropped, and an <c>M</c>
    /// ("1.25M", "1.3M", "1M") — the same unit as the "1.250M" power badge beside it. The M branch is new
    /// (2026-10-20) and has to exist on the web too, or the two screens part ways above a million.
    /// The separator is an EN DASH.</para>
    /// </summary>
    /// <param name="powerBandSize">이 백분위를 낸 아티팩트가 선언한 밴드 폭. 라벨의 위 끝은 이 값으로만
    /// 정해진다 — 상수로 두면 서버가 폭을 바꾼 날 "800k–850k"라고 적어 놓고 실제로는 800k–820k 분포를
    /// 보여주게 된다(숫자가 틀린 게 아니라 <b>문장이</b> 틀린다).</param>
    public static string FormatComparisonBasis(int powerBand, int powerBandSize) =>
        powerBand < 0
            ? "전체 전투력 기준"
            : $"전투력 {FormatBandPower(powerBand)}–{FormatBandPower(powerBand + powerBandSize)} 미만 기준";

    private static string FormatBandPower(int power) =>
        power >= 1_000_000
            ? (power / 1_000_000.0).ToString("#,0.###", System.Globalization.CultureInfo.InvariantCulture) + "M"
            : (power / 1000.0).ToString("#,0.#", System.Globalization.CultureInfo.InvariantCulture) + "k";

    /// <summary>"상위 12.3%" — the single display format, so every surface agrees.</summary>
    public static string FormatTopPercent(double topPercent) =>
        $"상위 {RoundTopPercent(topPercent).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}%";

    /// <summary>
    /// Invert the quantile ladder: where does <paramref name="value"/> sit in this cohort?
    /// <para>The grid is descending top-percent and the cuts ascend in metric value, so a larger value maps to a
    /// SMALLER top-percent. Interpolation is linear in percent space between the two bracketing anchors, matching
    /// the contract's "이진탐색 + 선형 보간".</para>
    /// <para>Equal neighbouring cuts are legal (a low-sample cohort collapses quantiles into one histogram bin);
    /// the zero-width span is treated as t=0 rather than dividing by zero.</para>
    /// </summary>
    public static double? TopPercentIn(double[] grid, long[] cuts, double value)
    {
        if (grid.Length < 2 || cuts.Length != grid.Length || double.IsNaN(value))
        {
            return null;
        }

        if (value <= cuts[0])
        {
            return grid[0];
        }

        if (value >= cuts[^1])
        {
            return grid[^1];
        }

        // Invariant: cuts[lo] <= value < cuts[hi], hi = lo + 1 on exit.
        int lo = 0;
        int hi = cuts.Length - 1;
        while (hi - lo > 1)
        {
            int mid = lo + ((hi - lo) / 2);
            if (cuts[mid] <= value)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        long span = cuts[lo + 1] - cuts[lo];
        double t = span <= 0 ? 0.0 : (value - cuts[lo]) / span;
        return grid[lo] + ((grid[lo + 1] - grid[lo]) * t);
    }

    /// <summary>
    /// Walk R0 → R6 and answer with the FIRST rung that has a shipped row. Rows below the server's sample floor
    /// are simply absent, so "present == qualified" and there is nothing to re-check here.
    /// <para>Every rung is compared on raw <c>dps</c> — see <see cref="RungOrder"/> for why the ladder no longer
    /// carries a second metric, and for the fairness trade-off that came with it.</para>
    /// </summary>
    /// <returns>null when no rung matched — the caller must render "표본 부족", never a guessed number.</returns>
    public static TierEvaluation? Evaluate(TierArtifact artifact, TierCohort cohort, double dps)
    {
        if (artifact == null || cohort.CategoryId < 0 || cohort.JobId < 0 || dps <= 0)
        {
            return null;
        }

        foreach (int rung in RungOrder)
        {
            // Synergy-bucketed rungs need a trustworthy mask; an untrusted raid mask would place the row in a
            // cohort it never belonged to, so they are skipped exactly as the server skips them when aggregating.
            if (KeepsSynergy(rung) && !cohort.SynergyTrusted)
            {
                continue;
            }

            // Within a rung, prefer the row for this character's combat-power band and fall back to the
            // whole-cohort row. The fallback is INSIDE the rung loop on purpose: rung order is cohort
            // specificity (boss → job → synergy), and a banded R6 row would otherwise beat an exact R0 one —
            // trading the right cohort for the right power range, which is the worse half of the deal. The
            // server ships a whole-cohort row alongside every banded one, so this picks the same rung it
            // always did and only sharpens which distribution that rung answers with.
            int band = cohort.PowerBand;
            long[]? cuts = artifact.Cuts(KeyFor(rung, MetricDps, cohort));
            if (cuts == null)
            {
                band = TierArtifact.WholeCohortBand;
                cuts = artifact.Cuts(KeyFor(rung, MetricDps, cohort with { PowerBand = band }));
            }

            if (cuts == null)
            {
                continue;
            }

            double? raw = TopPercentIn(artifact.Grid, cuts, dps);
            if (raw is not double percent)
            {
                continue;
            }

            double rounded = RoundTopPercent(percent);
            return new TierEvaluation(rung, MetricDps, rounded, TierRankOf(rounded), band, artifact.PowerBandSize);
        }

        return null;
    }

    /// <summary>The row coordinate a given rung looks up. Sentinels: -1 removes an axis, party mode uses 0.
    /// Category and job are carried through every rung untouched.</summary>
    /// <summary>
    /// The order the ladder is walked, most specific first.
    /// <para>🔑 EVERY rung is <c>dps</c>. The lower rungs used to ship as <c>ndps</c> — a metric the meter
    /// cannot compute, since normalising away received buffs needs the server's buff-attribution model — so the
    /// meter could only ever read R0/R1 and rendered nothing whenever the exact (boss × job × synergy) cell was
    /// missing. Measured against the live artifact on 2026-08-03: 43.5% of cells reachable, 56.5% present but
    /// unreadable, 0% genuinely sampleless. The web now emits the whole ladder in raw dps
    /// (<c>dps_trusted</c> for R0/R1, <c>dps_pooled</c> for R2~R6), both labelled <c>dps</c> on the wire.</para>
    /// <para>The trade-off that comes with it: R2~R6 pool synergy WITHOUT normalising, so on a fallback rung a
    /// player in a high-synergy party is compared against a pool that also holds low-synergy parties. The
    /// fallback was always an approximation; this makes it a slightly generous one.</para>
    /// </summary>
    /// <para>🔑 R6 is deliberately NOT walked. It drops <c>DungeonOrd</c>, so it is "every dungeon in this
    /// category, by job" — answering a fight in one dungeon with other dungeons' records. A dungeon whose own
    /// samples are thin is exactly where that fires, and exactly where the number is least defensible: the
    /// 시련 tier of 바크론의 공중섬 has a boss with 2.2x the HP of its 탐험 tier, so pooling it with the rest
    /// of 원정 is not an approximation, it is a different fight. R5 is the last rung that still keys on the
    /// dungeon, so it is the floor. Below it, <see cref="Evaluate"/> returns null and the caller renders
    /// "표본 부족" — which is the contract, and the honest answer.</para>
    private static readonly int[] RungOrder = [0, 1, 2, 3, 4, 5];

    /// <summary>R0/R1 bucket by synergy and are aggregated only from synergy-trusted rows (the web's
    /// <c>dps_trusted</c> branch), so an untrusted raid mask must not reach them. R2~R6 pool synergy and carry
    /// every row, so they stay available.</summary>
    private static bool KeepsSynergy(int rung) => rung <= 1;

    internal static TierRowKey KeyFor(int rung, int metricId, TierCohort c) => rung switch
    {
        // R0: everything explicit.
        0 => new TierRowKey(0, metricId, c.CategoryId, c.DungeonOrd, c.VariantOrd, c.BossIndex, c.JobId, c.SynergyCount, c.PartyMode, c.PowerBand),
        // R1: drop party mode (synergy matters more than raid size).
        1 => new TierRowKey(1, metricId, c.CategoryId, c.DungeonOrd, c.VariantOrd, c.BossIndex, c.JobId, c.SynergyCount, 0, c.PowerBand),
        // R2: pool synergy (safe now that the metric is normalised), keep party mode.
        2 => new TierRowKey(2, metricId, c.CategoryId, c.DungeonOrd, c.VariantOrd, c.BossIndex, c.JobId, -1, c.PartyMode, c.PowerBand),
        3 => new TierRowKey(3, metricId, c.CategoryId, c.DungeonOrd, c.VariantOrd, c.BossIndex, c.JobId, -1, 0, c.PowerBand),
        4 => new TierRowKey(4, metricId, c.CategoryId, c.DungeonOrd, c.VariantOrd, -1, c.JobId, -1, 0, c.PowerBand),
        5 => new TierRowKey(5, metricId, c.CategoryId, c.DungeonOrd, -1, -1, c.JobId, -1, 0, c.PowerBand),
        // R6 drops the dungeon entirely. The server still ships those rows and this still describes them, but
        // RungOrder no longer walks it — see the note there.
        _ => new TierRowKey(6, metricId, c.CategoryId, -1, -1, -1, c.JobId, -1, 0, c.PowerBand),
    };

    /// <summary>Build a cohort from a finished/live battle. Returns null when the fight is outside the
    /// distribution's population (unmapped boss, unranked job, sub-20s, sub-400k power) — the caller then shows
    /// nothing rather than a percentile of a cohort this fight was never part of.</summary>
    public static TierCohort? CohortFor(
        TierArtifact artifact,
        int mobCode,
        string? job,
        int power,
        long durationMs,
        int synergyCount,
        int partyMode,
        bool synergyTrusted,
        TrialDifficulty trial = default)
    {
        if (artifact == null || durationMs < MinBattleDurationMs || power < MinPower)
        {
            return null;
        }

        // 시련's difficulties share their boss codes, so its coordinate cannot come from the mob map — the
        // artifact's trial gates supply it, and only when the affixes read as the difficulty they name.
        if (artifact.Placement(mobCode, trial) is not TierMobPlacement placement)
        {
            return null; // fail-closed: an unmapped mobCode gets no tier, ever.
        }

        int jobId = artifact.JobId(job);
        int categoryId = artifact.CategoryIdForDungeon(placement.DungeonOrd);
        if (jobId < 0 || categoryId < 0)
        {
            return null;
        }

        if (partyMode != 5 && partyMode != 10)
        {
            return null;
        }

        return new TierCohort(
            categoryId,
            placement.DungeonOrd,
            placement.VariantOrd,
            placement.BossIndex,
            jobId,
            Math.Clamp(synergyCount, 0, 3),
            partyMode,
            synergyTrusted,
            // The same power this battle reports in its upload. The server bands by the span's max, so a
            // character who gained power mid-span can land one band off — the whole-cohort fallback covers it.
            artifact.BandFor(power));
    }
}
