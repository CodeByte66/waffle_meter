using WaffleMeter.App.Core;
using WaffleMeter.Services;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

public sealed class HotkeyHandlerTests : IDisposable
{
    private readonly string _temp;

    public HotkeyHandlerTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "wm_hotkey_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void Combo_round_trips_through_to_string_and_parse()
    {
        var combo = new HotkeyCombo(HotkeyHandler.ModControl, 0x52);
        Assert.Equal("modifiers=2,vkCode=82", combo.ToString());
        Assert.Equal(combo, HotkeyCombo.TryParse(combo.ToString()));
    }

    [Fact]
    public void Combo_parse_trims_whitespace()
    {
        Assert.Equal(new HotkeyCombo(2, 82), HotkeyCombo.TryParse("modifiers = 2 , vkCode = 82"));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("modifiers=2")]            // missing vkCode
    [InlineData("modifiers=x,vkCode=82")]  // non-numeric
    [InlineData("")]
    public void Combo_parse_returns_null_on_bad_input(string input)
    {
        Assert.Null(HotkeyCombo.TryParse(input));
    }

    [Fact]
    public void Defaults_are_ctrl_r_h_t()
    {
        var handler = new HotkeyHandler(new PropertyHandler(_temp));
        Assert.Equal(new HotkeyCombo(HotkeyHandler.ModControl, 0x52), handler.Reset);        // Ctrl+R
        Assert.Equal(new HotkeyCombo(HotkeyHandler.ModControl, 0x48), handler.Visibility);   // Ctrl+H
        Assert.Equal(new HotkeyCombo(HotkeyHandler.ModControl, 0x54), handler.ClickThrough); // Ctrl+T
    }

    [Fact]
    public void Set_persists_and_reloads()
    {
        var handler = new HotkeyHandler(new PropertyHandler(_temp));
        handler.SetReset(new HotkeyCombo(HotkeyHandler.ModAlt, 0x41)); // Alt+A — not started, so no listener

        var reopened = new HotkeyHandler(new PropertyHandler(_temp));
        Assert.Equal(new HotkeyCombo(HotkeyHandler.ModAlt, 0x41), reopened.Reset);
    }

    [Fact]
    public void Corrupt_property_falls_back_to_default()
    {
        var props = new PropertyHandler(_temp);
        props.SetProperty("hotkey", "not-a-combo");

        var handler = new HotkeyHandler(props);
        Assert.Equal(new HotkeyCombo(HotkeyHandler.ModControl, 0x52), handler.Reset);
    }

    [Fact]
    public void Unassigned_persists_and_reloads_as_null()
    {
        var handler = new HotkeyHandler(new PropertyHandler(_temp));
        handler.SetReset(null); // 미지정 — no global hotkey for reset

        var reopened = new HotkeyHandler(new PropertyHandler(_temp));
        Assert.Null(reopened.Reset);
        // the other two stay at their defaults (only reset was unassigned)
        Assert.Equal(new HotkeyCombo(HotkeyHandler.ModControl, 0x48), reopened.Visibility);
        Assert.Equal(new HotkeyCombo(HotkeyHandler.ModControl, 0x54), reopened.ClickThrough);
    }

    /// <summary>
    /// 나중에 붙은 네 단축키(허수아비 2개 · UI 분리모드 · 컨텐츠 관리)는 <b>조합 없이</b> 출고된다.
    /// RegisterHotKey 실패는 조용해서, 우리가 고른 조합이 인게임 키와 겹치면 "눌러도 아무 일이 없다"만
    /// 남고 원인을 짚을 방법이 없기 때문이다. 누가 <c>LoadOptional</c> 을 기본값 있는 <c>Load</c> 로
    /// 바꾸면 그 순간 출고 기본값이 생기는데, 그 회귀를 잡아 줄 곳이 여기뿐이다.
    /// </summary>
    [Fact]
    public void Later_hotkeys_ship_unassigned_and_round_trip()
    {
        var handler = new HotkeyHandler(new PropertyHandler(_temp));
        Assert.Null(handler.DummyToggle);
        Assert.Null(handler.DummyReset);
        Assert.Null(handler.SplitUi);
        Assert.Null(handler.AetherList);

        handler.SetAetherList(new HotkeyCombo(HotkeyHandler.ModControl | HotkeyHandler.ModShift, 0x4B));

        var reopened = new HotkeyHandler(new PropertyHandler(_temp));
        Assert.Equal(new HotkeyCombo(HotkeyHandler.ModControl | HotkeyHandler.ModShift, 0x4B), reopened.AetherList);
        Assert.Null(reopened.SplitUi); // 이웃 키를 건드리지 않는다

        reopened.SetAetherList(null);
        Assert.Null(new HotkeyHandler(new PropertyHandler(_temp)).AetherList);
    }

    [Fact]
    public void Unassigned_marker_does_not_fall_back_to_default()
    {
        // "none" is the explicit-unassigned marker: it must NOT be treated like a corrupt value and
        // revert to the default (that would make a hotkey impossible to turn off).
        var props = new PropertyHandler(_temp);
        props.SetProperty("hotkey", "none");

        var handler = new HotkeyHandler(props);
        Assert.Null(handler.Reset);
    }

    [Fact]
    public void RepeatGuard_fires_on_the_first_press()
    {
        Assert.True(HotkeyHandler.ShouldFire(hasPrevious: false, previousTick: 0, nowTick: 0));
    }

    [Theory]
    [InlineData(0)]                                       // simultaneous re-post / mechanical bounce
    [InlineData(20)]                                      // inside an OS auto-repeat burst (~33ms rate)
    [InlineData(HotkeyHandler.HotkeyRepeatSuppressMs - 1)] // just inside the suppress window
    public void RepeatGuard_suppresses_an_auto_repeat_within_the_window(long gapMs)
    {
        Assert.False(HotkeyHandler.ShouldFire(hasPrevious: true, previousTick: 1000, nowTick: 1000 + gapMs));
    }

    [Theory]
    [InlineData(HotkeyHandler.HotkeyRepeatSuppressMs)] // boundary — fires
    [InlineData(150)]                                  // a deliberate "hide then show" double-tap MUST fire
    [InlineData(400)]                                  // the old window length: was swallowed, now fires
    public void RepeatGuard_fires_a_deliberate_re_tap_past_the_window(long gapMs)
    {
        // The reported bug: pressing Ctrl+H to hide then quickly again to show had the second press
        // suppressed by an over-long (400ms) window, so the overlay stayed hidden. A real re-tap must fire.
        Assert.True(HotkeyHandler.ShouldFire(hasPrevious: true, previousTick: 1000, nowTick: 1000 + gapMs));
    }

    [Fact]
    public void RepeatGuard_window_stays_between_auto_repeat_and_a_deliberate_tap()
    {
        // Invariant that prevents the regression: the window must be LONGER than the fastest OS auto-repeat
        // interval (~33ms) yet SHORTER than a fast human re-tap (~100ms+), so it collapses a held burst
        // without ever eating the user's intended second press.
        Assert.InRange(HotkeyHandler.HotkeyRepeatSuppressMs, 34L, 100L);
    }
}
