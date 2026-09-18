using System.Text.Json;
using WaffleMeter.App.Core;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 밴드 규격(폭·하한)은 아티팩트가 선언하고 미터가 따른다.
/// <para>배경: 폭이 양쪽에 상수로 박혀 있으면 서버가 그것을 바꾸는 순간 미터가 존재하지 않는 행 키를 조회하고,
/// <b>오류 없이</b> 전 사용자가 전체 기준으로 떨어진다. 그래서 폭은 바꿀 수 없는 값이었다. 폭은 표본이
/// 쌓이는 만큼 좁아질(50k → 20k) 예정이므로 이 배선이 그 전제다.</para>
/// </summary>
public sealed class TierBandSpecFromArtifactTests
{
    /// <summary>서버가 싣는 31칸 그리드(TierPowerBandTests와 동일 형태).</summary>
    private static readonly double[] Grid =
    [
        100, 99.5, 99, 98, 96, 93, 90, 85, 80, 75, 70, 65, 60, 55, 50, 45, 40, 35, 30, 25, 20, 15, 12.5, 10, 7.5,
        5, 3, 2, 1, 0.5, 0.1,
    ];

    private static object Row() => new Dictionary<string, object>
    {
        ["r"] = 0, ["m"] = "dps", ["k"] = "원정", ["d"] = 11, ["v"] = 3, ["b"] = 1,
        ["j"] = "검성", ["s"] = 3, ["p"] = 5, ["n"] = 500, ["w"] = 1,
        ["c"] = System.Linq.Enumerable.Repeat(1000, Grid.Length).ToArray(),
    };

    /// <summary>파서가 요구하는 최소 문서 + 밴드 규격만 바꿔 끼운다(TierPowerBandTests의 픽스처와 같은 형태).</summary>
    private static TierArtifact? Build(int? bandSize, int? bandFloor, int schemaVersion = 2)
    {
        var document = new Dictionary<string, object?>
        {
            ["schemaVersion"] = schemaVersion,
            ["artifactId"] = "band-spec-fixture",
            ["windowDays"] = 7,
            ["generatedAt"] = "2026-08-10T00:00:00.000Z",
            ["grid"] = Grid,
            ["jobs"] = new[] { "검성", "수호성", "살성", "궁성", "마도성", "정령성", "치유성", "호법성", "권성" },
            ["dungeons"] = new object[]
            {
                new { ord = 11, key = "expedition-fallen-deva-castle", name = "타락한 데바의 성", category = "원정" },
            },
            ["variants"] = new object[] { new { dungeonOrd = 11, ord = 3, label = "어려움" } },
            ["mobs"] = new Dictionary<string, int[]> { ["2301601"] = [11, 3, 1] },
            ["rows"] = new object[] { Row() },
        };

        if (bandSize is { } size)
        {
            document["powerBandSize"] = size;
        }

        if (bandFloor is { } floor)
        {
            document["powerBandFloor"] = floor;
        }

        return TierArtifact.Parse(JsonSerializer.Serialize(document));
    }

    [Fact]
    public void An_artifact_that_declares_the_spec_is_obeyed()
    {
        TierArtifact? a = Build(20_000, 300_000);

        Assert.NotNull(a);
        Assert.Equal(20_000, a!.PowerBandSize);
        Assert.Equal(300_000, a.PowerBandFloor);
        Assert.Equal(820_000, a.BandFor(835_000));   // 20k 격자
        Assert.Equal(300_000, a.BandFor(250_000));   // 하한 아래는 하한으로
    }

    [Fact]
    public void An_artifact_without_the_spec_keeps_the_values_it_was_built_with()
    {
        // 이 필드가 생기기 전에 발행된 아티팩트 — 그때 쓰던 50k/400k가 곧 정답이다.
        TierArtifact? a = Build(null, null);

        Assert.NotNull(a);
        Assert.Equal(TierArtifact.DefaultPowerBandSize, a!.PowerBandSize);
        Assert.Equal(TierArtifact.DefaultPowerBandFloor, a.PowerBandFloor);
        Assert.Equal(850_000, a.BandFor(875_000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50_000)]
    public void A_broken_spec_falls_back_instead_of_dividing_by_zero(int broken)
    {
        TierArtifact? a = Build(broken, null);

        Assert.NotNull(a);
        Assert.Equal(TierArtifact.DefaultPowerBandSize, a!.PowerBandSize);
        Assert.Equal(850_000, a.BandFor(875_000));
    }

    [Fact]
    public void The_comparison_label_reads_the_declared_width_not_a_constant()
    {
        // 🔑 폭이 바뀌었는데 라벨이 상수를 쓰면 "800k–850k 기준"이라 적어 놓고 실제로는 800k–820k 분포를
        // 보여준다. 숫자가 아니라 문장이 틀리는 종류의 버그라 화면만 봐서는 못 잡는다.
        Assert.Equal("전투력 800k–820k 미만 기준", TierLadder.FormatComparisonBasis(800_000, 20_000));
        Assert.Equal("전투력 800k–850k 미만 기준", TierLadder.FormatComparisonBasis(800_000, 50_000));
        Assert.Equal("전체 전투력 기준", TierLadder.FormatComparisonBasis(TierArtifact.WholeCohortBand, 20_000));
    }

    [Fact]
    public void An_evaluation_carries_the_width_that_produced_it()
    {
        // 표시 시점에 상수를 다시 가져오면, 아티팩트가 갱신된 뒤 라벨만 옛 폭으로 남는다.
        var evaluation = new TierEvaluation(0, 0, 3.2, 2, PowerBand: 600_000, PowerBandSize: 20_000);

        Assert.Equal("전투력 600k–620k 미만 기준", evaluation.ComparisonBasis);
    }

    /// <summary>
    /// v3 = v2와 문서 형태가 같고 <c>powerBandSize</c>만 25k로 좁힌 것. 파서에 버전 분기가 없다는 게 요점이다.
    /// </summary>
    [Fact]
    public void A_v3_artifact_is_parsed_by_the_same_code_and_bands_at_the_width_it_declares()
    {
        TierArtifact? a = Build(25_000, 400_000, schemaVersion: 3);

        Assert.NotNull(a);
        Assert.Equal(25_000, a!.PowerBandSize);
        Assert.Equal(400_000, a.PowerBandFloor);
        Assert.Equal(425_000, a.BandFor(440_000));
        Assert.Equal("전투력 425k–450k 미만 기준", TierLadder.FormatComparisonBasis(425_000, a.PowerBandSize));
    }

    /// <summary>
    /// 🔑 v3라는 버전 번호가 존재하는 이유 — <b>청중을 가르는 것</b> 하나뿐이다.
    /// <para>2.10.0 미만 빌드는 <c>powerBandSize</c> 선언을 무시하고 50k 격자로 행 키를 만든다. 그런데 50k의
    /// 배수는 전부 25k의 배수이기도 해서, 그 키는 25k 아티팩트에서 <b>조회에 실패하지 않는다</b> — 한 칸 아래의
    /// 멀쩡한 밴드에 붙는다. 미스가 안 나니 전체 코호트 폴백도 안 걸리고, 라벨도 "전체 기준"으로 바뀌지 않으며,
    /// 그 사용자는 자기보다 약한 모집단과 비교된 상위%를 아무 신호 없이 받는다. 폭을 좁히면서 그 빌드에게는
    /// 계속 v2(50k)를 주는 것이 이 사고를 막는 유일한 수단이고, 그래서 서버가 두 버전을 함께 발행한다.</para>
    /// </summary>
    [Fact]
    public void An_old_builds_50k_key_still_resolves_in_a_25k_grid_which_is_why_v2_must_keep_being_published()
    {
        TierArtifact? v3 = Build(25_000, 400_000, schemaVersion: 3);
        Assert.NotNull(v3);

        // 전투력 440k. 구버전은 폭을 못 읽으므로 언제나 50k 격자로 키를 만든다.
        int oldKey = TierArtifact.BandFor(440_000, TierArtifact.DefaultPowerBandSize, TierArtifact.DefaultPowerBandFloor);
        int newKey = v3!.BandFor(440_000);

        Assert.Equal(400_000, oldKey);
        Assert.Equal(425_000, newKey);
        Assert.NotEqual(newKey, oldKey);

        // 그리고 그 옛 키는 25k 격자에서 '없는 밴드'가 아니라 410k 캐릭터가 속하는 실재 밴드다.
        // 조회가 빗나가 주지 않으므로 폴백이 구제해 줄 기회 자체가 없다.
        Assert.Equal(oldKey, v3.BandFor(410_000));
    }
}
