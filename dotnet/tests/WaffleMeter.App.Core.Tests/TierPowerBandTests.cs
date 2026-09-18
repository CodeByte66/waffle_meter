using System.Collections.Generic;
using System.Text.Json;
using WaffleMeter.App.Core;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// Spec for artifact schema v2 — the combat-power band axis — and for reading v1 and v2 side by side.
/// <para>The version check is exact-match, so a build that understands only one schema stops taking tier
/// updates the moment the other is live. Both have to work at once or the rollout has a dead window in it,
/// and these tests are what stop a later edit from quietly closing one of them.</para>
/// </summary>
public sealed class TierPowerBandTests
{
    private static readonly double[] Grid =
    [
        100, 99.5, 99, 98, 96, 93, 90, 85, 80, 75, 70, 65, 60, 55, 50, 45, 40, 35, 30, 25, 20, 15, 12.5, 10, 7.5,
        5, 3, 2, 1, 0.5, 0.1,
    ];

    /// <summary>31 equal deltas. The server transports cuts as /100-quantized deltas, so a delta of
    /// <c>step</c> decodes to cuts of <c>step*100, step*200, …</c> — i.e. step 100 spans 10k…310k and step
    /// 1,000 spans 100k…3.1M. A dps of 150,000 therefore lands mid-range in the first and near the bottom of
    /// the second, which is what makes the two fixtures tell each other apart.</summary>
    private static int[] Deltas(int step) => [.. System.Linq.Enumerable.Repeat(step, Grid.Length)];

    private static object Row(int rung, string job, int synergy, int partyMode, int step, int? g)
    {
        var row = new Dictionary<string, object>
        {
            ["r"] = rung,
            ["m"] = "dps",
            ["k"] = "원정",
            ["d"] = 11,
            ["v"] = 3,
            ["b"] = 1,
            ["j"] = job,
            ["s"] = synergy,
            ["p"] = partyMode,
            ["n"] = 500,
            ["w"] = 1,
            ["c"] = Deltas(step),
        };

        if (g.HasValue)
        {
            row["g"] = g.Value;
        }

        return row;
    }

    private static TierArtifact Build(int schemaVersion, params object[] rows)
    {
        var document = new
        {
            schemaVersion,
            artifactId = "band-fixture",
            windowDays = 7,
            generatedAt = "2026-08-05T04:00:00.000Z",
            grid = Grid,
            jobs = new[] { "검성", "수호성", "살성", "궁성", "마도성", "정령성", "치유성", "호법성", "권성" },
            dungeons = new object[]
            {
                new { ord = 11, key = "expedition-fallen-deva-castle", name = "타락한 데바의 성", category = "원정" },
            },
            variants = new object[] { new { dungeonOrd = 11, ord = 3, label = "어려움" } },
            mobs = new Dictionary<string, int[]> { ["2301601"] = [11, 3, 1] },
            rows,
        };

        TierArtifact? artifact = TierArtifact.Parse(JsonSerializer.Serialize(document));
        Assert.NotNull(artifact);
        return artifact!;
    }

    private static TierCohort? CohortFor(TierArtifact a, int power) =>
        TierLadder.CohortFor(a, 2301601, "검성", power, durationMs: 60_000, synergyCount: 3, partyMode: 5, synergyTrusted: true);

    // ── schema acceptance ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_supported_schema_is_accepted(int schemaVersion)
    {
        Assert.True(TierArtifact.IsSupportedSchemaVersion(schemaVersion));
        Assert.NotNull(Build(schemaVersion, Row(0, "검성", 3, 5, step: 100, g: null)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void An_unknown_schema_is_still_refused(int schemaVersion)
    {
        Assert.False(TierArtifact.IsSupportedSchemaVersion(schemaVersion));

        string json = JsonSerializer.Serialize(new { schemaVersion, artifactId = "x", grid = Grid, jobs = new[] { "검성" } });
        Assert.Null(TierArtifact.Parse(json));
    }

    // ── band arithmetic ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(400_000, 400_000)]
    [InlineData(449_999, 400_000)]
    [InlineData(450_000, 450_000)]
    [InlineData(899_999, 850_000)]
    [InlineData(900_000, 900_000)]
    [InlineData(100_000, 400_000)]   // below the floor shares the lowest band
    public void Band_is_the_floor_of_the_fifty_thousand_step(int power, int expected)
    {
        Assert.Equal(expected, TierArtifact.BandFor(power, TierArtifact.DefaultPowerBandSize, TierArtifact.DefaultPowerBandFloor));
    }

    [Fact]
    public void A_cohort_carries_the_band_for_its_power()
    {
        TierArtifact artifact = Build(2, Row(0, "검성", 3, 5, step: 100, g: 450_000));

        Assert.Equal(450_000, CohortFor(artifact, 470_000)!.Value.PowerBand);
    }

    // ── v1 compatibility ─────────────────────────────────────────────────────────────────────────

    /// <summary>A v1 row has no <c>g</c>, and that IS the whole-cohort row — so a v1 artifact must evaluate
    /// exactly as it did before v2 existed, for a character of any power.</summary>
    [Theory]
    [InlineData(420_000)]
    [InlineData(900_000)]
    public void A_v1_artifact_evaluates_as_it_always_did(int power)
    {
        TierArtifact artifact = Build(1, Row(0, "검성", 3, 5, step: 100, g: null));

        TierEvaluation? result = TierLadder.Evaluate(artifact, CohortFor(artifact, power)!.Value, dps: 150_000);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Value.Rung);
    }

    [Fact]
    public void A_row_without_g_parses_as_the_whole_cohort_row()
    {
        TierArtifact artifact = Build(1, Row(0, "검성", 3, 5, step: 100, g: null));

        var wholeCohort = new TierRowKey(0, 0, artifact.CategoryId("원정"), 11, 3, 1, artifact.JobId("검성"), 3, 5, TierArtifact.WholeCohortBand);
        Assert.NotNull(artifact.Cuts(wholeCohort));
    }

    // ── band lookup + fallback ───────────────────────────────────────────────────────────────────

    /// <summary>Given both, the band row wins — that is the entire point of v2.</summary>
    [Fact]
    public void The_band_row_is_preferred_over_the_whole_cohort_row()
    {
        TierArtifact artifact = Build(2,
            Row(0, "검성", 3, 5, step: 100, g: TierArtifact.WholeCohortBand),
            Row(0, "검성", 3, 5, step: 1_000, g: 450_000));

        // 150,000 is the midpoint of the whole-cohort row's range but near the bottom of the band row's, so
        // the two cannot report the same percent.
        TierEvaluation? banded = TierLadder.Evaluate(artifact, CohortFor(artifact, 460_000)!.Value, dps: 150_000);
        TierEvaluation? whole = TierLadder.Evaluate(artifact, CohortFor(artifact, 800_000)!.Value, dps: 150_000);

        Assert.NotNull(banded);
        Assert.NotNull(whole);
        Assert.NotEqual(whole!.Value.TopPercent, banded!.Value.TopPercent);
    }

    [Fact]
    public void A_power_with_no_band_row_falls_back_to_the_whole_cohort_row()
    {
        TierArtifact artifact = Build(2,
            Row(0, "검성", 3, 5, step: 100, g: TierArtifact.WholeCohortBand),
            Row(0, "검성", 3, 5, step: 1_000, g: 450_000));

        // 800k has no band row shipped; the whole-cohort row answers instead of nothing.
        TierEvaluation? result = TierLadder.Evaluate(artifact, CohortFor(artifact, 800_000)!.Value, dps: 150_000);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Value.Rung);
    }

    [Fact]
    public void No_row_at_all_still_means_no_tier()
    {
        TierArtifact artifact = Build(2, Row(0, "수호성", 3, 5, step: 100, g: 450_000));

        Assert.Null(TierLadder.Evaluate(artifact, CohortFor(artifact, 460_000)!.Value, dps: 150_000));
    }

    /// <summary>🔑 The fallback lives INSIDE the rung loop. An exact-cohort row at R0 must beat a
    /// power-matched row at R1 — swapping that order trades the right cohort for the right power range,
    /// which is the worse half of the deal.</summary>
    [Fact]
    public void A_whole_cohort_row_on_a_better_rung_beats_a_band_row_on_a_worse_one()
    {
        TierArtifact artifact = Build(2,
            Row(0, "검성", 3, 5, step: 100, g: TierArtifact.WholeCohortBand),   // R0, whole cohort
            Row(1, "검성", 3, 0, step: 1_000, g: 450_000));                     // R1, this character's band

        TierEvaluation? result = TierLadder.Evaluate(artifact, CohortFor(artifact, 460_000)!.Value, dps: 150_000);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Value.Rung);
    }

    /// <summary>The server ships a whole-cohort row beside every banded one, so rung selection has to come out
    /// exactly as it did before v2 — only the distribution behind it sharpens.</summary>
    [Fact]
    public void Rung_selection_is_unchanged_when_only_a_lower_rung_exists()
    {
        TierArtifact artifact = Build(2,
            Row(3, "검성", -1, 0, step: 100, g: TierArtifact.WholeCohortBand),
            Row(3, "검성", -1, 0, step: 1_000, g: 450_000));

        TierEvaluation? result = TierLadder.Evaluate(artifact, CohortFor(artifact, 460_000)!.Value, dps: 150_000);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Value.Rung);
    }
}
