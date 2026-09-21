using System.Collections.Generic;
using System.Text.Json;
using WaffleMeter.App.Core;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// Spec for the artifact's trial gates — how a client reaches a variant no mobCode can point at.
/// <para>시련's levels 4~16 share three boss mobCodes, so its top-difficulty variant cannot live in
/// <c>mobs</c>. The server therefore keeps those codes OUT of that map and publishes a gate instead: the
/// codes, the coordinate to use, and the affix values that must hold. Every direction of this is fail-closed
/// on purpose, and these tests are what keep it that way.</para>
/// </summary>
public sealed class TierTrialGateTests
{
    private static readonly double[] Grid =
    [
        100, 99.5, 99, 98, 96, 93, 90, 85, 80, 75, 70, 65, 60, 55, 50, 45, 40, 35, 30, 25, 20, 15, 12.5, 10, 7.5,
        5, 3, 2, 1, 0.5, 0.1,
    ];

    private const int TrialBoss = 2300582;   // 바크론, shared by every trial level

    private static TierArtifact Build(object? trialGates)
    {
        var document = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 2,
            ["artifactId"] = "gate-fixture",
            ["windowDays"] = 7,
            ["generatedAt"] = "2026-08-06T04:00:00.000Z",
            ["grid"] = Grid,
            ["jobs"] = new[] { "검성", "수호성", "살성", "궁성", "마도성", "정령성", "치유성", "호법성", "권성" },
            ["dungeons"] = new object[]
            {
                new { ord = 5, key = "expedition-bakron-floating-island", name = "바크론의 공중섬", category = "원정" },
            },
            ["variants"] = new object[]
            {
                new { dungeonOrd = 5, ord = 9, label = "시련 13~16단계" },
                new { dungeonOrd = 5, ord = 10, label = "시련 16단계" },
            },
            // The trial codes are deliberately absent here — that is the whole point of the gate.
            ["mobs"] = new Dictionary<string, int[]> { ["2300812"] = [5, 2, 3] },
            ["rows"] = new object[]
            {
                new
                {
                    r = 0, m = "dps", k = "원정", d = 5, v = 9, b = 3, j = "검성", s = 3, p = 5, n = 300, w = 1,
                    c = System.Linq.Enumerable.Repeat(100, Grid.Length).ToArray(), g = -1,
                },
            },
        };

        if (trialGates != null)
        {
            document["trialGates"] = trialGates;
        }

        TierArtifact? artifact = TierArtifact.Parse(JsonSerializer.Serialize(document));
        Assert.NotNull(artifact);
        return artifact!;
    }

    private static object Gate(Dictionary<string, int> axes) => new[]
    {
        new
        {
            dungeonOrd = 5,
            variantOrd = 9,
            mobs = new Dictionary<string, int> { ["2300580"] = 1, ["2300581"] = 2, ["2300582"] = 3 },
            axes,
        },
    };

    private static object TopDifficultyGate() =>
        Gate(new Dictionary<string, int> { ["timelimit"] = 4, ["bossBuff"] = 4, ["skillUpgrade"] = 4 });

    /// <summary>The shape the server actually publishes once 부활 제한 became readable: the four-axis gate
    /// FIRST, the three-axis gate still behind it. Both point at the same dungeon and boss.</summary>
    private static object Top16ThenTopGates() => new[]
    {
        new
        {
            dungeonOrd = 5,
            variantOrd = 10,
            mobs = new Dictionary<string, int> { ["2300580"] = 1, ["2300581"] = 2, ["2300582"] = 3 },
            axes = new Dictionary<string, int>
            {
                ["timelimit"] = 4, ["rebirthlimit"] = 4, ["bossBuff"] = 4, ["skillUpgrade"] = 4,
            },
        },
        new
        {
            dungeonOrd = 5,
            variantOrd = 9,
            mobs = new Dictionary<string, int> { ["2300580"] = 1, ["2300581"] = 2, ["2300582"] = 3 },
            axes = new Dictionary<string, int> { ["timelimit"] = 4, ["bossBuff"] = 4, ["skillUpgrade"] = 4 },
        },
    };

    private static TrialDifficulty Knobs(int? timelimit, int? bossBuff, int? skillUpgrade) =>
        new(timelimit, Rebirthlimit: null, bossBuff, skillUpgrade);

    // ── the gate applies ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_run_matching_every_axis_gets_the_gates_coordinate()
    {
        TierArtifact artifact = Build(TopDifficultyGate());

        TierMobPlacement? placement = artifact.Placement(TrialBoss, Knobs(4, 4, 4));

        Assert.NotNull(placement);
        Assert.Equal(new TierMobPlacement(5, 9, 3), placement!.Value);
    }

    /// <summary>부활 제한 has no packet carrier, so the server leaves it out of the axes. Requiring it would
    /// shut the gate on every real upload.</summary>
    [Fact]
    public void An_axis_the_gate_does_not_list_is_not_required()
    {
        TierArtifact artifact = Build(TopDifficultyGate());

        Assert.NotNull(artifact.Placement(TrialBoss, Knobs(4, 4, 4)));   // rebirthlimit stays null throughout
    }

    /// <summary>The mob map still wins when it has an answer — only the trial needs the gate.</summary>
    [Fact]
    public void An_ordinary_boss_still_resolves_from_the_mob_map()
    {
        TierArtifact artifact = Build(TopDifficultyGate());

        Assert.Equal(new TierMobPlacement(5, 2, 3), artifact.Placement(2300812, default)!.Value);
    }

    // ── fail-closed ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(4, 4, 3)]
    [InlineData(4, 1, 4)]
    [InlineData(1, 4, 4)]
    public void A_run_below_the_gates_difficulty_gets_nothing(int timelimit, int bossBuff, int skillUpgrade)
    {
        TierArtifact artifact = Build(TopDifficultyGate());

        Assert.Null(artifact.Placement(TrialBoss, Knobs(timelimit, bossBuff, skillUpgrade)));
    }

    /// <summary>An affix the meter never read is not equal to anything the gate asks for.</summary>
    [Fact]
    public void An_unread_affix_closes_the_gate()
    {
        TierArtifact artifact = Build(TopDifficultyGate());

        Assert.Null(artifact.Placement(TrialBoss, Knobs(null, 4, 4)));
        Assert.Null(artifact.Placement(TrialBoss, default));
    }

    /// <summary>An artifact built before the gate existed yields no tier rather than a guessed one.</summary>
    [Fact]
    public void An_artifact_without_gates_yields_nothing_for_the_trial()
    {
        TierArtifact artifact = Build(trialGates: null);

        Assert.Null(artifact.Placement(TrialBoss, Knobs(4, 4, 4)));
    }

    /// <summary>🔑 A newer server must not be able to half-apply a gate to an older meter. An axis name this
    /// build does not know can never match, so the whole gate stays shut.</summary>
    [Fact]
    public void An_axis_name_this_build_does_not_know_closes_the_gate()
    {
        TierArtifact artifact = Build(Gate(new Dictionary<string, int>
        {
            ["timelimit"] = 4,
            ["bossBuff"] = 4,
            ["skillUpgrade"] = 4,
            ["someFutureKnob"] = 2,
        }));

        Assert.Null(artifact.Placement(TrialBoss, Knobs(4, 4, 4)));
    }

    [Fact]
    public void A_gate_listing_no_axes_never_matches()
    {
        TierArtifact artifact = Build(Gate([]));

        Assert.Null(artifact.Placement(TrialBoss, Knobs(4, 4, 4)));
    }

    [Fact]
    public void A_gate_that_does_not_cover_this_boss_is_skipped()
    {
        TierArtifact artifact = Build(TopDifficultyGate());

        Assert.Null(artifact.Placement(2600068, Knobs(4, 4, 4)));   // 정령왕 아그로 — a field boss
    }

    // ── two gates, in order ───────────────────────────────────────────────────

    /// <summary>🔑 The server publishes the narrower gate first and the wider one behind it, so order is
    /// load-bearing: first match wins and the array order in the JSON is the server's ranking. Reordering or
    /// sorting the gates on parse would file every 16단계 run under the wider variant instead.</summary>
    [Fact]
    public void The_first_gate_that_matches_wins_and_json_order_is_the_ranking()
    {
        TierArtifact artifact = Build(Top16ThenTopGates());

        TierMobPlacement? placement = artifact.Placement(TrialBoss, new TrialDifficulty(4, 4, 4, 4));

        Assert.Equal(10, placement!.Value.VariantOrd);   // the four-axis gate, not the three-axis one behind it
    }

    /// <summary>🔴 The reason the two gates coexist at all. A build that cannot read 부활 제한 holds null
    /// there, which shuts gate [0] — and then gate [1] still catches the run. Collapsing the two into one
    /// four-axis gate would drop those uploads on the floor instead.</summary>
    [Fact]
    public void A_meter_that_cannot_read_the_fourth_axis_falls_through_to_the_wider_gate()
    {
        TierArtifact artifact = Build(Top16ThenTopGates());

        TierMobPlacement? placement = artifact.Placement(TrialBoss, Knobs(4, 4, 4));   // rebirthlimit null

        Assert.Equal(9, placement!.Value.VariantOrd);
    }

    /// <summary>A run that is 13단계 by the fourth axis is still top by the three damage axes, so it lands on
    /// the wider gate — not on nothing, and not on 16단계.</summary>
    [Fact]
    public void A_run_short_on_the_fourth_axis_lands_on_the_wider_gate()
    {
        TierArtifact artifact = Build(Top16ThenTopGates());

        TierMobPlacement? placement = artifact.Placement(TrialBoss, new TrialDifficulty(4, 1, 4, 4));

        Assert.Equal(9, placement!.Value.VariantOrd);
    }

    // ── end to end ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_gate_carries_a_top_difficulty_run_all_the_way_to_a_percentile()
    {
        TierArtifact artifact = Build(TopDifficultyGate());

        TierCohort? cohort = TierLadder.CohortFor(
            artifact, TrialBoss, "검성", power: 500_000, durationMs: 60_000,
            synergyCount: 3, partyMode: 5, synergyTrusted: true, trial: Knobs(4, 4, 4));

        Assert.NotNull(cohort);
        Assert.NotNull(TierLadder.Evaluate(artifact, cohort!.Value, dps: 150_000));
    }

    [Fact]
    public void A_lower_difficulty_run_produces_no_cohort_at_all()
    {
        TierArtifact artifact = Build(TopDifficultyGate());

        Assert.Null(TierLadder.CohortFor(
            artifact, TrialBoss, "검성", power: 500_000, durationMs: 60_000,
            synergyCount: 3, partyMode: 5, synergyTrusted: true, trial: Knobs(1, 1, 1)));
    }
}
