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

    /// <summary>
    /// 🔑 The left/right codes are the whole point. WPF's <c>Key</c> enum has no generic Ctrl/Shift/Alt, so a
    /// real keypress comes back from <c>KeyInterop.VirtualKeyFromKey</c> as VK_LCONTROL (0xA2), never
    /// VK_CONTROL (0x11). A guard listing only the generic codes matched nothing a user could press.
    /// </summary>
    [Theory]
    [InlineData(0x10)] // VK_SHIFT     — generic; a hand-edited file or an old Kotlin combo can hold these
    [InlineData(0x11)] // VK_CONTROL
    [InlineData(0x12)] // VK_MENU
    [InlineData(0xA0)] // VK_LSHIFT    — these are what WPF actually reports
    [InlineData(0xA1)] // VK_RSHIFT
    [InlineData(0xA2)] // VK_LCONTROL  ← the one behind "CTRL + VK_162"
    [InlineData(0xA3)] // VK_RCONTROL
    [InlineData(0xA4)] // VK_LMENU
    [InlineData(0xA5)] // VK_RMENU
    [InlineData(0x5B)] // VK_LWIN
    [InlineData(0x5C)] // VK_RWIN
    public void A_modifier_is_not_usable_as_a_combos_main_key(int vk)
    {
        Assert.True(HotkeyCombo.IsPureModifierVk(vk));
        Assert.Null(HotkeyCombo.TryParse($"modifiers=2,vkCode={vk}"));
    }

    [Theory]
    [InlineData(0x52)] // R  — the reset default
    [InlineData(0x48)] // H
    [InlineData(0x70)] // F1
    [InlineData(0x60)] // NUMPAD 0
    [InlineData(0x1B)] // ESC
    [InlineData(0x5A)] // Z — adjacent to VK_LWIN (0x5B); the range must not swallow it
    [InlineData(0x5D)] // VK_APPS — the other side of the Win pair
    [InlineData(0x9F)] // adjacent to VK_LSHIFT (0xA0)
    [InlineData(0xA6)] // adjacent to VK_RMENU (0xA5)
    public void An_ordinary_key_is_still_a_valid_main_key(int vk)
    {
        Assert.False(HotkeyCombo.IsPureModifierVk(vk));
        Assert.Equal(new HotkeyCombo(2, vk), HotkeyCombo.TryParse($"modifiers=2,vkCode={vk}"));
    }

    /// <summary>
    /// Installs that already hit the bug have "modifiers=2,vkCode=162" on disk, and RegisterHotKey accepts it —
    /// the action then fires on every bare Ctrl press. Retiring it at load is what makes those installs
    /// recover: a defaulted action returns to its default, an opt-in one returns to 미지정.
    /// </summary>
    [Fact]
    public void A_stored_modifier_only_combo_is_retired_on_load()
    {
        var props = new PropertyHandler(_temp);
        props.SetProperty("hotkey", "modifiers=2,vkCode=162");        // has a default
        props.SetProperty("aetherListHotkey", "modifiers=2,vkCode=162"); // ships unassigned

        var handler = new HotkeyHandler(props);

        Assert.Equal(new HotkeyCombo(HotkeyHandler.ModControl, 0x52), handler.Reset); // back to Ctrl+R
        Assert.Null(handler.AetherList);                                              // back to 미지정
    }

    [Fact]
    public void RepeatGuard_window_stays_between_auto_repeat_and_a_deliberate_tap()
    {
        // Invariant that prevents the regression: the window must be LONGER than the fastest OS auto-repeat
        // interval (~33ms) yet SHORTER than a fast human re-tap (~100ms+), so it collapses a held burst
        // without ever eating the user's intended second press.
        Assert.InRange(HotkeyHandler.HotkeyRepeatSuppressMs, 34L, 100L);
    }

    /// <summary>
    /// 🔑 옛 빌드가 써 둔 <b>수식키 단독</b> 조합(예: CTRL + VK_LCONTROL)은 3.1.0 이상에서 거부된다.
    /// 종전에는 그 칸이 <b>조용히</b> 미지정이 돼서, 업데이트한 사용자는 이유도 모른 채 단축키를 잃었다
    /// ("컨텐츠관리 팝업창이 여전히 안 되는데" — 3.1.0 실사용자 제보, 2026-09-19).
    /// 이제 그 사유가 설정 화면에 뜬다.
    /// </summary>
    [Fact]
    public void A_retired_combo_is_reported_so_the_settings_screen_can_say_why()
    {
        var props = new PropertyHandler(_temp);
        props.SetProperty("aetherListHotkey", "modifiers=2,vkCode=162"); // CTRL + VK_LCONTROL — 옛 쓰레기

        var handler = new HotkeyHandler(props);

        Assert.Null(handler.AetherList);                                  // 해제된 것은 맞고
        Assert.Equal(HotkeyIssue.Retired, handler.AetherListIssue);       // 이유가 남는다
    }

    /// <summary>기본값으로 되돌아가는 칸도 '사용자가 고른 값은 사라졌다'를 알려야 한다 —
    /// 안 그러면 "내가 지정한 게 아닌 조합이 걸려 있다"를 설명할 방법이 없다.</summary>
    [Fact]
    public void A_retired_combo_is_reported_even_when_a_default_takes_over()
    {
        var props = new PropertyHandler(_temp);
        props.SetProperty("hotkey", "modifiers=2,vkCode=162");

        var handler = new HotkeyHandler(props);

        Assert.Equal(new HotkeyCombo(HotkeyHandler.ModControl, 0x52), handler.Reset); // Ctrl+R 로 폴백
        Assert.Equal(HotkeyIssue.Retired, handler.ResetIssue);                        // 그래도 알린다
    }

    [Fact]
    public void A_healthy_combo_reports_no_issue()
    {
        var props = new PropertyHandler(_temp);
        props.SetProperty("aetherListHotkey", "modifiers=2,vkCode=122"); // Ctrl+F11

        var handler = new HotkeyHandler(props);

        Assert.Equal(new HotkeyCombo(HotkeyHandler.ModControl, 0x7A), handler.AetherList);
        Assert.Equal(HotkeyIssue.None, handler.AetherListIssue);
    }

    /// <summary>미지정(사용자가 ✕ 로 지운 칸)은 문제가 아니다 — 경고를 띄우면 안 된다.</summary>
    [Fact]
    public void An_explicitly_unassigned_hotkey_is_not_an_issue()
    {
        var props = new PropertyHandler(_temp);
        props.SetProperty("aetherListHotkey", "none");

        var handler = new HotkeyHandler(props);

        Assert.Null(handler.AetherList);
        Assert.Equal(HotkeyIssue.None, handler.AetherListIssue);
    }

    /// <summary>한 번도 지정한 적 없는 칸도 문제가 아니다.</summary>
    [Fact]
    public void A_never_set_hotkey_is_not_an_issue()
    {
        var handler = new HotkeyHandler(new PropertyHandler(_temp));

        Assert.Null(handler.AetherList);
        Assert.Equal(HotkeyIssue.None, handler.AetherListIssue);
    }

    /// <summary>은퇴 사유는 사용자가 다시 지정하면 사라진다.</summary>
    [Fact]
    public void Reassigning_clears_the_retired_warning()
    {
        var props = new PropertyHandler(_temp);
        props.SetProperty("aetherListHotkey", "modifiers=2,vkCode=162");
        var handler = new HotkeyHandler(props);
        Assert.Equal(HotkeyIssue.Retired, handler.AetherListIssue);

        handler.SetAetherList(new HotkeyCombo(HotkeyHandler.ModControl, 0x7A)); // 리스너 미기동 → 등록 없음

        Assert.Equal(HotkeyIssue.None, handler.AetherListIssue);
    }
}
