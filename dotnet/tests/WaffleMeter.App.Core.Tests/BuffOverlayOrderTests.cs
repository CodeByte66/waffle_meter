using WaffleMeter.App.Core;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 버프 오버레이 표시 순서 — 전역 정렬 모드로 줄을 세우고, "맨 앞 고정"한 버프를 그 앞으로 끌어온다.
/// 기본이 적용 순서인 이유는 남은 시간으로 정렬하면 타이머가 흐를 때마다 아이콘이 자리를 바꾸기 때문.
/// </summary>
public sealed class BuffOverlayOrderTests
{
    private static OwnerBuffView Buff(int code, string name, long remaining, long duration, long end) =>
        new(code, name, remaining, duration, end, ByOther: false, Overlay: true, OnCooldown: false, Indefinite: false);

    // 적용 시각 = end - duration. A가 가장 먼저, C가 가장 나중에 걸렸다.
    private static readonly OwnerBuffView A = Buff(11110000, "가", remaining: 5_000, duration: 30_000, end: 1_030_000);
    private static readonly OwnerBuffView B = Buff(12220000, "나", remaining: 20_000, duration: 30_000, end: 1_045_000);
    private static readonly OwnerBuffView C = Buff(13330000, "다", remaining: 12_000, duration: 10_000, end: 1_050_000);

    private static readonly OwnerBuffView[] All = { C, A, B }; // 입력 순서는 무의미해야 한다

    private static int[] Order(string? mode, params int[] pinned) =>
        BuffOverlayOrder.Sort(All, mode, pinned).Select(b => b.Code).ToArray();

    [Fact]
    public void Applied_order_is_the_default_and_is_stable_over_time()
    {
        // A(1,000,000) → B(1,015,000) → C(1,040,000) 순으로 걸렸다.
        Assert.Equal(new[] { 11110000, 12220000, 13330000 }, Order(null));
        Assert.Equal(new[] { 11110000, 12220000, 13330000 }, Order(BuffOverlayOrder.Applied));
    }

    [Fact]
    public void A_held_toggle_sorts_by_when_it_was_switched_on_not_by_its_last_pulse()
    {
        // 보호의 빛: 1초마다 펄스라 end - duration 은 늘 "방금"(1,049,000)이다. 켠 시각(1,010,000)으로 세워야
        // A(1,000,000)와 B(1,015,000) 사이에 머문다 — 안 그러면 펄스마다 맨 뒤로 끌려간다.
        var toggle = new OwnerBuffView(17410000, "보호의 빛", 4_000, 1_250, 1_050_250, ByOther: true, Overlay: true,
            OnCooldown: false, Indefinite: false, Toggle: true, HeldSinceMs: 1_010_000);

        int[] order = BuffOverlayOrder.Sort(new[] { C, toggle, A, B }, BuffOverlayOrder.Applied, null)
            .Select(b => b.Code).ToArray();

        Assert.Equal(new[] { 11110000, 17410000, 12220000, 13330000 }, order);
    }

    [Fact]
    public void Remaining_order_puts_the_longest_first()
    {
        Assert.Equal(new[] { 12220000, 13330000, 11110000 }, Order(BuffOverlayOrder.Remaining));
    }

    [Fact]
    public void Name_order_is_ordinal()
    {
        Assert.Equal(new[] { 11110000, 12220000, 13330000 }, Order(BuffOverlayOrder.Name));
    }

    [Fact]
    public void Pinned_buffs_come_first_in_their_pin_order()
    {
        // 고정 = [C, B] → 그 둘이 이 순서로 앞에 오고, 나머지(A)는 모드 순서대로 뒤에 붙는다.
        Assert.Equal(new[] { 13330000, 12220000, 11110000 }, Order(BuffOverlayOrder.Applied, 13330000, 12220000));
    }

    [Fact]
    public void Unpinned_keep_the_mode_order_behind_the_pins()
    {
        // 고정 = [C]. 나머지는 남은 시간 순(B 20s → A 5s).
        Assert.Equal(new[] { 13330000, 12220000, 11110000 }, Order(BuffOverlayOrder.Remaining, 13330000));
    }

    [Fact]
    public void A_pin_for_a_buff_that_is_not_up_is_ignored()
    {
        Assert.Equal(new[] { 11110000, 12220000, 13330000 }, Order(BuffOverlayOrder.Applied, 19990000));
    }

    [Fact]
    public void TogglePin_appends_then_removes()
    {
        List<int> once = BuffOverlayOrder.TogglePin(new[] { 111 }, 222);
        Assert.Equal(new[] { 111, 222 }, once);
        Assert.Equal(new[] { 111 }, BuffOverlayOrder.TogglePin(once, 222));
    }

    [Fact]
    public void Move_shifts_within_the_pin_list_and_clamps_at_the_ends()
    {
        int[] pins = { 1, 2, 3 };
        Assert.Equal(new[] { 2, 1, 3 }, BuffOverlayOrder.Move(pins, 2, up: true));
        Assert.Equal(new[] { 1, 3, 2 }, BuffOverlayOrder.Move(pins, 2, up: false));
        Assert.Equal(new[] { 1, 2, 3 }, BuffOverlayOrder.Move(pins, 1, up: true));   // 이미 맨 앞
        Assert.Equal(new[] { 1, 2, 3 }, BuffOverlayOrder.Move(pins, 3, up: false));  // 이미 맨 뒤
        Assert.Equal(new[] { 1, 2, 3 }, BuffOverlayOrder.Move(pins, 99, up: true));  // 목록에 없음
    }
}
