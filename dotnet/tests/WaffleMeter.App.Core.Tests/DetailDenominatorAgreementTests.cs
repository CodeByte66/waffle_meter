using WaffleMeter.App.Core;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 상세창 <b>위쪽 합계 타일</b>과 <b>아래쪽 스킬 행</b>은 같은 판정을 같은 기준으로 보여줘야 한다.
///
/// <para>서버는 아무것도 안 터진 평범한 타격에는 판정 플래그 바이트를 <b>아예 안 보낸다</b>(11바이트가
/// 아니라 8바이트로 온다). 그래서 "플래그를 실은 타격"만 세면 <b>실패한 시도가 분모에서 빠진다</b> —
/// 실측상 그 타격들은 판정 불명이 아니라 "강타도 완벽도 안 터진 타격"이다(같은 스킬·같은 크리 여부로
/// 통제했을 때 피해 중앙값이 그쪽과 5~15% 안에서 일치).</para>
///
/// <para>종전에는 강타·완벽·막기 셋만 타일이 flag-bearing 수로 나눠서, 같은 창 위아래가 같은 지표를 다르게
/// 보여줬다(실측 한 전투: 타일 강타 59.3% vs 스킬 행 가중평균 54.6%). 분모가 작은 타일이 <b>구조적으로
/// 항상 높다</b>. 스킬 행과 업로드 페이로드는 이미 전체 타격 기준이었으므로 셋 중 타일만 혼자 달랐다.</para>
///
/// <para>⚠️ <b>후방/전방은 예외다.</b> 방향은 플래그를 실은 타격에만 존재하는 판정이라, 비방향성 타격까지
/// 분모에 넣으면 인위적으로 낮아진다. 타일·행·웹 셋 다 flag-bearing 분모를 쓰며 그 셋은 이미 일치했다.
/// 이 테스트는 그 예외가 <b>유지되는지</b>도 같이 고정한다.</para>
/// </summary>
public sealed class DetailDenominatorAgreementTests
{
    /// <summary>10타 중 6타만 판정 플래그를 실었고, 그 6타 안에서 강타 3 · 완벽 3 · 막기 3 · 후방 3이 났다.</summary>
    private static AnalyzedSkill Mixed() => new()
    {
        SkillCode = 11010000,
        Name = "강타",
        DamageAmount = 1000,
        Times = 10,
        FlaggedTimes = 6,
        CritTimes = 5,
        DoubleTimes = 3,
        PerfectTimes = 3,
        ParryTimes = 3,
        BackTimes = 3,
    };

    private static DetailModel Compute(AnalyzedSkill s) =>
        DetailModel.Compute(
            new Dictionary<string, AnalyzedSkill> { [s.SkillCode.ToString()] = s },
            new List<OperatingData>(), new List<OperatingData>(), uid: 1, JobClass.GLADIATOR,
            contribution: 60.0, combatMs: 30000);

    [Fact]
    public void The_tile_counts_every_hit_for_strong_perfect_and_parry()
    {
        DetailModel model = Compute(Mixed());

        // 3/10 — 플래그 없는 4타(아무것도 안 터진 타격)도 분모에 든다. 종전 규칙이면 3/6 = 50.0 이었다.
        Assert.Equal(30.0, model.StrongPct);
        Assert.Equal(30.0, model.PerfectPct);
        Assert.Equal(30.0, model.ParryPct);
    }

    [Fact]
    public void The_tile_and_the_skill_row_now_agree()
    {
        DetailModel model = Compute(Mixed());
        DetailSkillRow row = model.Skills[0].Children.Single(r => !r.IsDot);

        Assert.Equal((int)model.StrongPct, row.StrongPct);
        Assert.Equal((int)model.PerfectPct, row.PerfectPct);
        Assert.Equal((int)model.ParryPct, row.ParryPct);
        Assert.Equal((int)model.CritPct, row.CritPct);
    }

    [Fact]
    public void Back_and_front_still_divide_by_the_flag_bearing_count()
    {
        // 방향 판정은 플래그를 실은 타격에만 존재한다 — 예외가 유지돼야 한다. 3/6 = 50%, 3/10 = 30% 가 아니다.
        DetailModel model = Compute(Mixed());
        DetailSkillRow row = model.Skills[0].Children.Single(r => !r.IsDot);

        Assert.Equal(50.0, model.BackPct);
        Assert.Equal(50, row.BackPct);
    }

    [Fact]
    public void With_every_hit_flagged_the_two_rules_coincide()
    {
        // 플래그가 전부 실린 전투에서는 어느 규칙이든 같은 값이 나와야 한다 — 회귀 시 이 테스트가 먼저 조용해진다.
        AnalyzedSkill s = Mixed();
        s.FlaggedTimes = s.Times;
        DetailModel model = Compute(s);

        Assert.Equal(30.0, model.StrongPct);
        Assert.Equal(30.0, model.BackPct);
    }
}
