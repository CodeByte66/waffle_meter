using System.Linq;
using System.Text.Json;
using WaffleMeter.App.Core;
using WaffleMeter.Stats;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// Spec for saying which pool a percentile was measured against.
/// <para>The server answers the highest schema a client asked for that it currently publishes, so two players
/// in the same party can be measured differently — one reading a banded row, one reading the whole cohort.
/// The numbers alone cannot be told apart, so the basis has to be stated or they get compared when they were
/// never comparable.</para>
/// <para>🔑 Every string here is byte-identical to the web's <c>formatTierComparisonBasis</c>. Reading the two
/// screens side by side is the entire point, so a wording drift defeats the feature.</para>
/// </summary>
public sealed class TierComparisonBasisTests
{
    // ── wording ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_whole_cohort_says_so()
    {
        Assert.Equal("전체 전투력 기준", TierLadder.FormatComparisonBasis(TierArtifact.WholeCohortBand, TierArtifact.DefaultPowerBandSize));
    }

    /// <summary>The web formats through <c>Intl.NumberFormat("en-US", { maximumFractionDigits: 1 })</c> on
    /// thousands, so 1,250,000 renders "1,250k" — with the comma. ko-KR grouping would agree here, but the
    /// contract names en-US and this is what pins it.</summary>
    [Theory]
    [InlineData(400_000, "전투력 400k–450k 미만 기준")]
    [InlineData(700_000, "전투력 700k–750k 미만 기준")]
    [InlineData(1_250_000, "전투력 1,250k–1,300k 미만 기준")]
    [InlineData(950_000, "전투력 950k–1,000k 미만 기준")]
    public void A_band_names_its_range(int powerBand, string expected)
    {
        Assert.Equal(expected, TierLadder.FormatComparisonBasis(powerBand, TierArtifact.DefaultPowerBandSize));
    }

    /// <summary>The separator is an EN DASH (U+2013), not a hyphen — the web's template uses one.</summary>
    [Fact]
    public void The_separator_is_an_en_dash()
    {
        Assert.Contains('–', TierLadder.FormatComparisonBasis(700_000, TierArtifact.DefaultPowerBandSize));
        Assert.DoesNotContain('-', TierLadder.FormatComparisonBasis(700_000, TierArtifact.DefaultPowerBandSize));
    }

    // ── which basis, decided by the row that answered ────────────────────────────────────────────

    private static readonly double[] Grid =
    [
        100, 99.5, 99, 98, 96, 93, 90, 85, 80, 75, 70, 65, 60, 55, 50, 45, 40, 35, 30, 25, 20, 15, 12.5, 10, 7.5,
        5, 3, 2, 1, 0.5, 0.1,
    ];

    private static object Row(int? g) => new
    {
        r = 0, m = "dps", k = "원정", d = 11, v = 3, b = 1, j = "검성", s = 3, p = 5, n = 500, w = 1,
        c = Enumerable.Repeat(100, Grid.Length).ToArray(),
        g = g ?? int.MinValue,
    };

    private static TierArtifact Build(int schemaVersion, params object[] rows)
    {
        // A v1 row carries no 'g' at all; emitting the sentinel would not reproduce that.
        object[] shaped = rows.Select(r => (object)(schemaVersion == 1
            ? new { r = 0, m = "dps", k = "원정", d = 11, v = 3, b = 1, j = "검성", s = 3, p = 5, n = 500, w = 1,
                    c = Enumerable.Repeat(100, Grid.Length).ToArray() }
            : r)).ToArray();

        TierArtifact? artifact = TierArtifact.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion,
            artifactId = "basis-fixture",
            windowDays = 7,
            generatedAt = "2026-08-06T04:00:00.000Z",
            grid = Grid,
            jobs = new[] { "검성", "수호성", "살성", "궁성", "마도성", "정령성", "치유성", "호법성", "권성" },
            dungeons = new object[]
            {
                new { ord = 11, key = "expedition-fallen-deva-castle", name = "타락한 데바의 성", category = "원정" },
            },
            variants = new object[] { new { dungeonOrd = 11, ord = 3, label = "어려움" } },
            mobs = new System.Collections.Generic.Dictionary<string, int[]> { ["2301721"] = [11, 3, 1] },
            rows = shaped,
        }));

        Assert.NotNull(artifact);
        return artifact!;
    }

    private static TierEvaluation Evaluate(TierArtifact artifact, int power)
    {
        TierCohort? cohort = TierLadder.CohortFor(
            artifact, 2301721, "검성", power, durationMs: 60_000,
            synergyCount: 3, partyMode: 5, synergyTrusted: true);
        Assert.NotNull(cohort);

        TierEvaluation? result = TierLadder.Evaluate(artifact, cohort!.Value, dps: 150_000);
        Assert.NotNull(result);
        return result!.Value;
    }

    /// <summary>A v1 artifact has no band axis at all, so everything it answers is the whole cohort. This is
    /// the normal path today — the meter asks for 2 and the server still publishes 1.</summary>
    [Fact]
    public void A_v1_artifact_reports_the_whole_cohort()
    {
        TierEvaluation result = Evaluate(Build(1, Row(null)), power: 700_000);

        Assert.Equal(TierArtifact.WholeCohortBand, result.PowerBand);
        Assert.Equal("전체 전투력 기준", result.ComparisonBasis);
    }

    /// <summary>A v2 whole-cohort row means the band was too thin and the server fell back — which is exactly
    /// what the reader needs told.</summary>
    [Fact]
    public void A_v2_whole_cohort_row_reports_the_whole_cohort()
    {
        TierEvaluation result = Evaluate(Build(2, Row(TierArtifact.WholeCohortBand)), power: 700_000);

        Assert.Equal("전체 전투력 기준", result.ComparisonBasis);
    }

    [Fact]
    public void A_v2_banded_row_reports_its_band()
    {
        TierEvaluation result = Evaluate(
            Build(2, Row(TierArtifact.WholeCohortBand), Row(700_000)), power: 720_000);

        Assert.Equal(700_000, result.PowerBand);
        Assert.Equal("전투력 700k–750k 미만 기준", result.ComparisonBasis);
    }

    /// <summary>🔑 The basis follows the row that ACTUALLY answered, not the one asked for. This character's
    /// band has no row, so the whole-cohort row answers and the label must say so.</summary>
    [Fact]
    public void A_fallback_reports_the_whole_cohort_not_the_requested_band()
    {
        TierEvaluation result = Evaluate(
            Build(2, Row(TierArtifact.WholeCohortBand), Row(700_000)), power: 1_250_000);

        Assert.Equal(TierArtifact.WholeCohortBand, result.PowerBand);
        Assert.Equal("전체 전투력 기준", result.ComparisonBasis);
    }

    // ── the requested schema, on the wire ────────────────────────────────────────────────────────

    private static string ManifestUrl(int readableSchemaVersion)
    {
        string? seen = null;
        var api = new StatsApiClient(
            () => "install-1",
            (_, url, _, _) =>
            {
                seen = url;
                return new StatsHttpResponse(
                    200,
                    """{"ok":true,"artifactId":"a1","url":"/t/a1.json.gz","sha256":"x","bytes":1,"schemaVersion":1}""");
            },
            readableSchemaVersion: readableSchemaVersion);

        api.GetTierManifest();
        Assert.NotNull(seen);
        return seen!;
    }

    /// <summary>🔑 A QUERY PARAMETER, not a header. The manifest is CDN-cached with <c>s-maxage=60</c>; a body
    /// that varied by header would serve one client's v2 document straight back to a v1-only client, and
    /// <c>Vary</c> on a custom header fails silently the moment anything in the path drops it.</summary>
    [Fact]
    public void The_manifest_request_asks_for_the_highest_schema_this_build_reads()
    {
        Assert.Equal(3, TierArtifact.MaxSupportedSchemaVersion);
        Assert.Equal(TierArtifact.SupportedSchemaVersions.Max(), TierArtifact.MaxSupportedSchemaVersion);

        string url = ManifestUrl(TierArtifact.MaxSupportedSchemaVersion);

        Assert.Contains("/api/v1/tiers/manifest?schema=3", url);
    }

    /// <summary>The client holds no schema literal of its own — it sends what the composition root derived.
    /// Pinning a number inside the HTTP layer is exactly the drift this guards.</summary>
    [Theory]
    [InlineData(1, "?schema=1")]
    [InlineData(2, "?schema=2")]
    [InlineData(7, "?schema=7")]
    public void The_client_sends_the_version_it_was_given(int version, string expected)
    {
        Assert.EndsWith(expected, ManifestUrl(version));
    }
}
