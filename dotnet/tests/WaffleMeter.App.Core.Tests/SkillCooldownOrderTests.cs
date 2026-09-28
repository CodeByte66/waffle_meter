using WaffleMeter.App.Core;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 스킬 쿨타임 오버레이의 표시 순서 — 기본(직업 → 일반 → 스티그마 → 카탈로그 순서) 위에 사용자 배치가 얹힌다.
/// 배치는 base 코드 목록 하나로 저장되고, 숨긴 스킬의 자리는 사용자가 보이는 스킬끼리 순서를 바꿔도 남는다.
/// </summary>
public sealed class SkillCooldownOrderTests
{
    // 궁성(14). 카탈로그 순서만 보면 일반·스티그마가 섞인다: 11(일반) < 12(스티그마) < 13(일반) < 14(스티그마).
    private const int N1 = 14_110_000, S1 = 14_120_000, N2 = 14_130_000, S2 = 14_140_000;
    private const int Other = 11_050_000; // 검성 — 다른 직업의 배치가 섞여 있어도 서로 안 건드려야 한다

    private static SkillCooldownView Row(int code, int order, bool stigma) =>
        new(code, code, $"스킬{code}", 0, 0, true, code / 1_000_000, order, stigma);

    private static readonly SkillCooldownView[] Rows =
    [
        Row(S2, 3, stigma: true), Row(N1, 0, stigma: false), Row(S1, 1, stigma: true), Row(N2, 2, stigma: false),
    ];

    private static CooldownSkillInfo Info(int code, int order, bool stigma) =>
        new(code, code / 1_000_000, $"스킬{code}", 1000, code, 0, order, stigma);

    private static readonly CooldownSkillInfo[] Catalog =
    [
        Info(N1, 0, false), Info(S1, 1, true), Info(N2, 2, false), Info(S2, 3, true),
    ];

    private static int[] Codes(IEnumerable<SkillCooldownView> rows) => rows.Select(r => r.GroupId).ToArray();

    [Fact]
    public void Default_puts_normal_skills_before_stigmas()
    {
        Assert.Equal(new[] { N1, N2, S1, S2 }, Codes(SkillCooldownOrder.Sort(Rows, [])));
    }

    [Fact]
    public void A_saved_arrangement_wins_over_the_default()
    {
        Assert.Equal(new[] { S2, N1, S1, N2 }, Codes(SkillCooldownOrder.Sort(Rows, [S2, N1, S1, N2])));
    }

    [Fact]
    public void Skills_missing_from_the_arrangement_follow_it_in_default_order()
    {
        // 배치해 둔 뒤 패치로 생긴 스킬 — 맨 뒤에 기본 순서로 붙는다. 사라진 스킬의 코드(99…)는 그냥 무시된다.
        Assert.Equal(new[] { S1, N1, N2, S2 }, Codes(SkillCooldownOrder.Sort(Rows, [S1, 14_990_000, N1])));
    }

    [Fact]
    public void Preview_order_matches_the_overlay_order()
    {
        // 미리보기(카탈로그 기준)와 오버레이(행 기준)가 다른 순서를 그리면 드래그 결과가 화면마다 달라진다.
        int[] order = [N2, S2];
        Assert.Equal(Codes(SkillCooldownOrder.Sort(Rows, order)), SkillCooldownOrder.JobOrder(Catalog, order));
        Assert.Equal(Codes(SkillCooldownOrder.Sort(Rows, [])), SkillCooldownOrder.JobOrder(Catalog, []));
    }

    [Fact]
    public void Rearranging_the_visible_skills_keeps_a_hidden_skill_in_its_place()
    {
        // S1 은 숨김 상태라 미리보기에 없다. 보이는 셋의 순서를 뒤집어도 S1 은 세 번째 자리를 지킨다 —
        // 다시 켰을 때 맨 뒤가 아니라 원래 자리로 돌아와야 한다.
        List<int> jobOrder = SkillCooldownOrder.JobOrder(Catalog, []); // N1, N2, S1, S2
        List<int> stored = SkillCooldownOrder.Rearrange([Other], 14, jobOrder, [S2, N2, N1]);

        Assert.Equal(new[] { Other, S2, N2, S1, N1 }, stored);
    }

    [Fact]
    public void Rearranging_rewrites_only_that_job_and_drops_its_stale_codes()
    {
        List<int> stored = SkillCooldownOrder.Rearrange(
            [14_990_000, Other, N2], 14, SkillCooldownOrder.JobOrder(Catalog, [N2]), [N1, N2, S1, S2]);

        Assert.Equal(new[] { Other, N1, N2, S1, S2 }, stored); // 없어진 14990000 은 정리, 검성은 그대로
    }

    [Fact]
    public void Reset_clears_only_that_jobs_arrangement()
    {
        Assert.Equal(new[] { Other }, SkillCooldownOrder.WithoutJob([N2, Other, S1], 14));
    }

    [Theory]
    [InlineData(null, new int[0])]
    [InlineData("", new int[0])]
    [InlineData("14130000, 14110000,14130000", new[] { 14_130_000, 14_110_000 })] // 중복은 처음 자리만
    [InlineData("[14110000,14120000]", new[] { 14_110_000, 14_120_000 })]          // 대괄호도 벗긴다
    [InlineData("abc,-5,14110000", new[] { 14_110_000 })]
    public void Parse_is_forgiving(string? raw, int[] expected)
    {
        Assert.Equal(expected, SkillCooldownOrder.Parse(raw));
    }

    [Fact]
    public void Format_round_trips()
    {
        Assert.Equal(new[] { N2, S1 }, SkillCooldownOrder.Parse(SkillCooldownOrder.Format([N2, S1])));
    }
}
