using WaffleMeter.App.Core;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 미터 전체 크기(배율) 규칙. 여기서 고정하는 것은 단 하나 — <b>배율이 바뀌어도 논리 열 예산
/// (<c>Width ÷ 배율</c>)은 그대로다.</b>
///
/// <para>종전에는 배율이 루트 <c>LayoutTransform</c> 안에 있고 <c>Window.Width</c> 는 그 바깥이라,
/// 배율을 올리면 내부 논리 폭이 오히려 줄어 이름·태그·배지가 조용히 잘렸다. 사용자가 말한
/// "핸들을 끌면 미터 레이아웃은 그대로고 안의 요소들만 크기조절된다"가 이 비대칭이다.</para>
/// </summary>
public sealed class MeterScalePolicyTests
{
    // ── 불변식 ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(490.0, 100, 130)]
    [InlineData(490.0, 100, 75)]
    [InlineData(700.0, 130, 100)]
    [InlineData(360.0, 75, 130)]
    public void The_column_budget_survives_a_scale_change(double width, int from, int to)
    {
        // 🔑 이 저장소가 고치려는 결함 그 자체. 배율만 바꿨을 때 내부가 쓸 수 있는 폭이 변하면 안 된다.
        double budget = MeterScalePolicy.BaseWidth(width, from);

        double newWidth = MeterScalePolicy.WindowWidth(budget, to);

        Assert.Equal(budget, MeterScalePolicy.BaseWidth(newWidth, to), precision: 6);
    }

    [Fact]
    public void A_round_trip_through_the_budget_returns_the_same_window()
    {
        double budget = MeterScalePolicy.BaseWidth(637.0, 130);
        Assert.Equal(637.0, MeterScalePolicy.WindowWidth(budget, 130), precision: 6);
    }

    [Fact]
    public void Raising_the_scale_widens_the_window_rather_than_squeezing_the_content()
    {
        // 종전 동작: 폭은 490 그대로, 내부 논리 폭은 490/1.3 ≈ 377 로 줄어듦.
        double budget = MeterScalePolicy.BaseWidth(490.0, 100);

        Assert.Equal(490.0, budget, precision: 6);
        Assert.Equal(637.0, MeterScalePolicy.WindowWidth(budget, 130), precision: 6);
    }

    // ── 드래그 ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dragging_an_edge_moves_the_scale_in_proportion_to_the_width()
    {
        Assert.Equal(130, MeterScalePolicy.ScaleFromDrag(490.0, 100, 637.0));
        Assert.Equal(80, MeterScalePolicy.ScaleFromDrag(490.0, 100, 392.0));
    }

    [Fact]
    public void A_drag_starts_from_the_width_the_user_already_had()
    {
        // 상수 490 을 기준으로 잡으면 폭을 넓혀 둔 사용자가 가장자리를 잡는 순간 배율이 튄다.
        Assert.Equal(100, MeterScalePolicy.ScaleFromDrag(700.0, 100, 700.0));
        Assert.Equal(115, MeterScalePolicy.ScaleFromDrag(700.0, 115, 700.0));
    }

    [Fact]
    public void A_drag_that_moves_nothing_changes_nothing()
    {
        // 실측: 이동량 0인 가장자리 클릭도 리사이즈 제스처를 연다. 그걸로 배율이 움직이면 안 된다.
        Assert.Equal(115, MeterScalePolicy.ScaleFromDrag(563.0, 115, 563.0));
    }

    [Theory]
    [InlineData(101)]
    [InlineData(99)]
    [InlineData(102)]
    [InlineData(98)]
    public void The_scale_snaps_back_to_normal_near_a_hundred(int raw)
    {
        Assert.Equal(100, MeterScalePolicy.Detent(raw));
    }

    [Theory]
    [InlineData(103)]
    [InlineData(97)]
    public void But_the_detent_does_not_swallow_a_deliberate_nudge(int raw)
    {
        Assert.Equal(raw, MeterScalePolicy.Detent(raw));
    }

    [Fact]
    public void A_drag_cannot_leave_the_supported_range()
    {
        // ⚠️ 상한을 넓히면 TierPalette/NameFxPalette/TierSheen 세 파일의 설계 근거가 같이 낡는다.
        Assert.Equal(MeterScalePolicy.ScaleMax, MeterScalePolicy.ScaleFromDrag(490.0, 100, 4900.0));
        Assert.Equal(MeterScalePolicy.ScaleMin, MeterScalePolicy.ScaleFromDrag(490.0, 100, 49.0));
    }

    [Fact]
    public void A_degenerate_start_width_is_not_a_division_by_zero()
    {
        Assert.Equal(115, MeterScalePolicy.ScaleFromDrag(0.0, 115, 500.0));
        Assert.Equal(115, MeterScalePolicy.ScaleFromDrag(500.0, 115, 0.0));
    }

    // ── 핸들 배정 ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WindowResizePolicy.HtLeft)]
    [InlineData(WindowResizePolicy.HtRight)]
    public void The_side_edges_scale_the_whole_meter(int hit)
    {
        Assert.Equal(MeterScalePolicy.Gesture.Scale, MeterScalePolicy.MeaningOf(hit));
    }

    [Theory]
    [InlineData(WindowResizePolicy.HtTop)]
    [InlineData(WindowResizePolicy.HtBottom)]
    [InlineData(WindowResizePolicy.HtTopLeft)]
    [InlineData(WindowResizePolicy.HtTopRight)]
    [InlineData(WindowResizePolicy.HtBottomLeft)]
    [InlineData(WindowResizePolicy.HtBottomRight)]
    [InlineData(WindowResizePolicy.HtUnknown)]
    public void Everything_else_keeps_todays_meaning(int hit)
    {
        // ⚠️ 모서리를 여기로 데려오지 마라. 모서리는 폭과 높이를 직접 맞추는 유일한 수단이고,
        // 기능을 없애 규칙을 지키려 한 것이 v2.8.1 의 실패 형태다.
        Assert.Equal(MeterScalePolicy.Gesture.Free, MeterScalePolicy.MeaningOf(hit));
    }

    [Fact]
    public void A_scale_gesture_can_never_pin_the_height()
    {
        // 🔑 이게 WindowResizePolicy 를 한 줄도 안 고치고 끝나는 이유다. 높이 고정 래치는 한 번 켜지면
        // 드래그로는 안 풀리는 sticky 라, 배율 제스처가 그걸 켤 수 있으면 안 된다.
        foreach (int hit in new[] { WindowResizePolicy.HtLeft, WindowResizePolicy.HtRight })
        {
            Assert.Equal(MeterScalePolicy.Gesture.Scale, MeterScalePolicy.MeaningOf(hit));
            Assert.False(WindowResizePolicy.IsManualAfterDrag(hit, heightBefore: 300, heightAfter: 900));
        }
    }

    // ── 하한 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_minimum_width_follows_the_scale()
    {
        // 따라가지 않으면 130% 에서 320 까지 좁혀져 오른쪽이 잘리고, 75% 에서는 쓸데없이 넓다.
        Assert.Equal(320.0, MeterScalePolicy.MinWindowWidth(100), precision: 6);
        Assert.Equal(416.0, MeterScalePolicy.MinWindowWidth(130), precision: 6);
        Assert.Equal(240.0, MeterScalePolicy.MinWindowWidth(75), precision: 6);
    }

    [Fact]
    public void A_window_can_never_be_built_narrower_than_its_floor()
    {
        Assert.Equal(MeterScalePolicy.MinWindowWidth(130), MeterScalePolicy.WindowWidth(10.0, 130), precision: 6);
    }

    [Fact]
    public void An_out_of_range_scale_is_clamped_everywhere_it_is_read()
    {
        // 남의 공유코드는 검증 없이 심긴다 — 계산 함수들도 스스로 잘라야 한다.
        Assert.Equal(MeterScalePolicy.ScaleMax, MeterScalePolicy.ClampScale(5000));
        Assert.Equal(MeterScalePolicy.ScaleMin, MeterScalePolicy.ClampScale(-3));
        Assert.Equal(MeterScalePolicy.MinWindowWidth(MeterScalePolicy.ScaleMax), MeterScalePolicy.MinWindowWidth(5000), precision: 6);
        Assert.Equal(MeterScalePolicy.BaseWidth(490.0, MeterScalePolicy.ScaleMin), MeterScalePolicy.BaseWidth(490.0, 0), precision: 6);
    }
}
