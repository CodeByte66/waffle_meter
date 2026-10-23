using System.Text.Json;
using WaffleMeter.App.Core;
using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 전투시간 하한은 아티팩트가 선언하고(<c>minBattleDurationMs</c>) 미터가 따른다.
/// <para>배경: 하한이 미터에 20초로 박혀 있으면 서버가 모집단을 10초로 넓혀도 미터는 10~20초 전투를 계속
/// 숨기고, 반대로 서버가 좁히면 모집단에 없던 전투에 백분위를 붙인다. 밴드 규격(TierBandSpecFromArtifactTests)과
/// 같은 배선이고, 스키마 bump가 아니다 — 1·2·3 어느 문서에든 실린다.</para>
/// </summary>
public sealed class TierMinBattleDurationTests
{
    private const int BossCode = 2301601;

    private static readonly double[] Grid =
    [
        100, 99.5, 99, 98, 96, 93, 90, 85, 80, 75, 70, 65, 60, 55, 50, 45, 40, 35, 30, 25, 20, 15, 12.5, 10, 7.5,
        5, 3, 2, 1, 0.5, 0.1,
    ];

    /// <summary>R0 셀 하나(원정·살성·시너지 0·5인). 컷은 1,000부터 오름차순.</summary>
    private static object Row()
    {
        var encoded = new long[Grid.Length];
        long previous = 0;
        for (int i = 0; i < Grid.Length; i++)
        {
            long quantised = (100_000 + 10_000L * i) / 100;
            encoded[i] = quantised - previous;
            previous = quantised;
        }

        return new Dictionary<string, object>
        {
            ["r"] = 0, ["m"] = "dps", ["k"] = "원정", ["d"] = 11, ["v"] = 3, ["b"] = 1,
            ["j"] = "살성", ["s"] = 0, ["p"] = 5, ["n"] = 500, ["c"] = encoded,
        };
    }

    /// <summary>최소 문서 + <c>minBattleDurationMs</c>만 바꿔 끼운다. <paramref name="declared"/>가 null이면
    /// 키 자체를 뺀다(이 필드 이전 문서·디스크 캐시).</summary>
    private static TierArtifact Build(object? declared, int schemaVersion = 3)
    {
        var document = new Dictionary<string, object?>
        {
            ["schemaVersion"] = schemaVersion,
            ["artifactId"] = "min-duration-fixture",
            ["windowDays"] = 31,
            ["generatedAt"] = "2026-10-23T00:00:00.000Z",
            ["grid"] = Grid,
            ["jobs"] = new[] { "검성", "수호성", "살성", "궁성", "마도성", "정령성", "치유성", "호법성", "권성" },
            ["dungeons"] = new object[]
            {
                new { ord = 11, key = "expedition-fallen-deva-castle", name = "타락한 데바의 성", category = "원정" },
            },
            ["variants"] = new object[] { new { dungeonOrd = 11, ord = 3, label = "어려움" } },
            ["mobs"] = new Dictionary<string, int[]> { [BossCode.ToString()] = [11, 3, 1] },
            ["rows"] = new object[] { Row() },
        };

        if (declared is not null)
        {
            document["minBattleDurationMs"] = declared;
        }

        TierArtifact? artifact = TierArtifact.Parse(JsonSerializer.Serialize(document));
        Assert.NotNull(artifact);
        return artifact!;
    }

    private static TierCohort? CohortAt(TierArtifact artifact, long durationMs) =>
        TierLadder.CohortFor(artifact, BossCode, "살성", 900_000, durationMs, 0, 5, true);

    private static DpsReport Report(long durationMs)
    {
        var report = new DpsReport
        {
            Target = new MobInfo(1, new Mob(BossCode, "보스", Boss: true)),
            BattleStart = 1_000_000,
            BattleEnd = 1_000_000 + durationMs,
        };
        report.Contributors.Add(new User(7, "본인", 2003, JobClass.ASSASSIN, power: 900_000));
        report.Information[7] = new DpsInformation(200_000 * durationMs / 1000.0, 200_000, 200_000, 200_000);
        return report;
    }

    [Fact]
    public void An_artifact_without_the_field_keeps_the_20s_floor_it_was_built_with()
    {
        TierArtifact a = Build(null);

        Assert.Equal(20_000, a.MinBattleDurationMs);
        Assert.Equal(TierLadder.MinBattleDurationMs, a.MinBattleDurationMs);
        Assert.Null(CohortAt(a, 15_000));
        Assert.NotNull(CohortAt(a, 20_000));
    }

    [Fact]
    public void A_declared_10s_floor_admits_a_15s_fight()
    {
        TierArtifact a = Build(10_000);

        Assert.Equal(10_000, a.MinBattleDurationMs);
        Assert.NotNull(CohortAt(a, 15_000));
        Assert.NotNull(CohortAt(a, 10_000)); // 경계 포함
        Assert.Null(CohortAt(a, 9_999));
    }

    [Fact]
    public void A_declared_20s_floor_still_refuses_a_15s_fight()
    {
        TierArtifact a = Build(20_000);

        Assert.Null(CohortAt(a, 15_000));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_field_is_read_on_every_schema_not_gated_behind_a_new_one(int schemaVersion)
    {
        Assert.Equal(10_000, Build(10_000, schemaVersion).MinBattleDurationMs);
    }

    public static TheoryData<object> BrokenValues() => new()
    {
        0,
        -10_000,
        999,              // 범위(1,000..600,000) 바로 아래
        600_001,          // 범위 바로 위
        long.MaxValue,    // int32 밖
        10_000.5,         // 정수 아님
        "10000",          // 문자열
        true,
    };

    [Theory]
    [MemberData(nameof(BrokenValues))]
    public void A_broken_declaration_falls_back_to_20s_instead_of_trusting_it(object broken)
    {
        TierArtifact a = Build(broken);

        Assert.Equal(20_000, a.MinBattleDurationMs);
        Assert.Null(CohortAt(a, 15_000));
    }

    [Fact]
    public void The_range_edges_are_accepted()
    {
        Assert.Equal(1_000, Build(1_000).MinBattleDurationMs);
        Assert.Equal(600_000, Build(600_000).MinBattleDurationMs);
    }

    [Fact]
    public void The_evaluator_gates_on_the_declared_floor_too()
    {
        // TierEvaluator 는 CohortFor 이전에 자체 하한 검사를 한다 — 둘 중 하나만 바뀌면 10초 선언이 먹지 않는다.
        Assert.True(TierEvaluator.Evaluate(Report(15_000), Build(10_000)).ContainsKey(7));
        Assert.Empty(TierEvaluator.Evaluate(Report(15_000), Build(null)));
        Assert.Empty(TierEvaluator.Evaluate(Report(15_000), Build(20_000)));
    }
}
