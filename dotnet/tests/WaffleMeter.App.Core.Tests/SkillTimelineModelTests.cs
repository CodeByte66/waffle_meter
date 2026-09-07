using WaffleMeter.App.Core;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 스킬 타임라인 탭의 계산 규칙. 두 가지를 동시에 답해야 한다 — "시전 사이사이 몇 ms 였나"(순서 목록의
/// 직전 간격)와 "스킬별로 평균 몇 ms 간격이었나"(같은 스킬의 연속 시전 간격).
/// </summary>
public sealed class SkillTimelineModelTests
{
    private const long Start = 1_000_000L;

    private static SkillCastRow Cast(int code, string name, long atMs) => new(code, name, Start + atMs);

    [Fact]
    public void Empty_input_produces_the_empty_model()
    {
        SkillTimelineModel model = SkillTimelineModel.Compute([], Start);

        Assert.Empty(model.Casts);
        Assert.Empty(model.Skills);
        Assert.Null(model.MedianGapMs);
        Assert.Null(model.MeanGapMs);
    }

    [Fact]
    public void Each_cast_carries_the_gap_from_the_previous_cast_and_the_first_carries_none()
    {
        SkillTimelineModel model = SkillTimelineModel.Compute(
            [Cast(11010000, "절단의 맹타", 0), Cast(11020000, "예리한 일격", 1_200), Cast(11010000, "절단의 맹타", 1_500)],
            Start);

        Assert.Equal([null, 1_200L, 300L], model.Casts.Select(c => c.GapFromPrevMs).ToArray());
        Assert.Equal([0L, 1_200L, 1_500L], model.Casts.Select(c => c.OffsetMs).ToArray());
    }

    [Fact]
    public void Input_is_sorted_so_an_out_of_order_list_still_reads_forward()
    {
        SkillTimelineModel model = SkillTimelineModel.Compute(
            [Cast(11020000, "예리한 일격", 900), Cast(11010000, "절단의 맹타", 100)],
            Start);

        Assert.Equal(["절단의 맹타", "예리한 일격"], model.Casts.Select(c => c.Name).ToArray());
        Assert.Equal(800L, model.Casts[1].GapFromPrevMs);
    }

    [Fact]
    public void An_opener_before_the_battle_anchor_keeps_a_negative_offset()
    {
        // 전투 시작 앵커는 첫 피해보다 앞으로 당겨지지 않으므로(StartAnchor), 피해가 나기 전에 쓴 버프·이동기는
        // 상시 그보다 앞선다. 0으로 접으면 첫 몇 줄이 같은 시각에 겹쳐 보인다.
        SkillTimelineModel model = SkillTimelineModel.Compute(
            [Cast(11380000, "근성", -400), Cast(11010000, "절단의 맹타", 200)],
            Start);

        Assert.Equal(-400L, model.Casts[0].OffsetMs);
        Assert.Equal(600L, model.Casts[1].GapFromPrevMs);
    }

    [Fact]
    public void Per_skill_gaps_measure_the_SAME_skill_back_to_back()
    {
        // 전역 간격(1,000 / 500 / 1,000)과 스킬별 간격(A: 1,500 / B: -)은 서로 다른 수다.
        SkillTimelineModel model = SkillTimelineModel.Compute(
            [
                Cast(11010000, "A", 0),
                Cast(11020000, "B", 1_000),
                Cast(11010000, "A", 1_500),
                Cast(11030000, "C", 2_500),
            ],
            Start);

        SkillCastSummaryRow a = model.Skills.Single(r => r.Name == "A");
        Assert.Equal(2, a.Count);
        Assert.Equal(1_500L, a.MeanGapMs);
        Assert.Equal(1_500L, a.MinGapMs);
        Assert.Equal(1_500L, a.MaxGapMs);

        SkillCastSummaryRow b = model.Skills.Single(r => r.Name == "B");
        Assert.Equal(1, b.Count);
        Assert.Null(b.MeanGapMs); // 한 번만 쓴 스킬은 잴 간격 자체가 없다
    }

    [Fact]
    public void Skills_are_grouped_by_NAME_so_specialization_suffixes_do_not_split_a_row()
    {
        // 0x3802 는 특화 접미가 붙은 원본 코드를 싣는다 — 코드로 묶으면 같은 스킬이 특화 조합마다 흩어진다.
        SkillTimelineModel model = SkillTimelineModel.Compute(
            [Cast(11010047, "절단의 맹타", 0), Cast(11010000, "절단의 맹타", 2_000)],
            Start);

        SkillCastSummaryRow row = Assert.Single(model.Skills);
        Assert.Equal(2, row.Count);
        Assert.Equal(2_000L, row.MeanGapMs);
    }

    [Fact]
    public void Skills_are_ordered_by_use_count()
    {
        SkillTimelineModel model = SkillTimelineModel.Compute(
            [Cast(11020000, "드문", 0), Cast(11010000, "잦은", 100), Cast(11010000, "잦은", 200), Cast(11010000, "잦은", 300)],
            Start);

        Assert.Equal(["잦은", "드문"], model.Skills.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void The_cooldown_start_fact_reaches_both_the_row_and_the_per_skill_count()
    {
        SkillTimelineModel model = SkillTimelineModel.Compute(
            [
                new SkillCastRow(11010000, "A", Start, StartsCooldown: true),
                new SkillCastRow(11010000, "A", Start + 2_000, StartsCooldown: false),
                new SkillCastRow(11010000, "A", Start + 4_000, StartsCooldown: true),
                new SkillCastRow(11020000, "B", Start + 5_000, StartsCooldown: false),
            ],
            Start);

        Assert.Equal([true, false, true, false], model.Casts.Select(c => c.StartsCooldown).ToArray());
        Assert.Equal(2, model.Skills.Single(r => r.Name == "A").CooldownStartCount);
        // 쿨이 없는 스킬은 0 으로 남는다 — 0 이 "안 나갔다"는 뜻이 아니다.
        Assert.Equal(0, model.Skills.Single(r => r.Name == "B").CooldownStartCount);
    }

    [Fact]
    public void Median_handles_both_an_odd_and_an_even_sample()
    {
        // 간격 3개(홀수): 100 / 200 / 900 -> 중앙값 200, 평균 400.
        SkillTimelineModel odd = SkillTimelineModel.Compute(
            [Cast(1, "s", 0), Cast(2, "t", 100), Cast(3, "u", 300), Cast(4, "v", 1_200)],
            Start);
        Assert.Equal(200L, odd.MedianGapMs);
        Assert.Equal(400L, odd.MeanGapMs);

        // 간격 2개(짝수): 100 / 300 -> 중앙값 200.
        SkillTimelineModel even = SkillTimelineModel.Compute(
            [Cast(1, "s", 0), Cast(2, "t", 100), Cast(3, "u", 400)],
            Start);
        Assert.Equal(200L, even.MedianGapMs);
    }

    [Fact]
    public void The_median_survives_the_simultaneous_proc_cluster_that_wrecks_the_mean()
    {
        // 실측(5인 파티 코퍼스, 액터 1인 3,044 간격): 인접 시전 간격의 <b>36%가 50ms 미만</b>이고 그 전부가
        // 서로 다른 스킬이다 — 한 입력에 여러 스킬이 함께 나가는 묶음이다. 그 비율을 흉내 낸다:
        // 실제 리듬은 1초인데, 20비트 중 7번은 10ms 뒤에 동반 시전이 하나 더 붙는다.
        var casts = new List<SkillCastRow>();
        for (int beat = 0; beat < 20; beat++)
        {
            long at = beat * 1_000L;
            casts.Add(Cast(11010000 + beat, "주력" + beat, at));
            if (beat % 3 == 1)
            {
                casts.Add(Cast(11020000 + beat, "동반" + beat, at + 10));
            }
        }

        SkillTimelineModel model = SkillTimelineModel.Compute(casts, Start);

        // 26개 간격 = 10ms 7개(27%) + 990ms 6개 + 1,000ms 13개. 짝수 표본이라 중앙값은 990과 1,000의 평균.
        Assert.Equal(995L, model.MedianGapMs);            // 중앙값은 실제 리듬(1초) 위에 앉는다
        Assert.InRange(model.MeanGapMs!.Value, 700, 760); // 평균은 어느 실제 간격에도 해당하지 않는 값이 된다
    }
}
