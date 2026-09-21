using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WaffleMeter.App.Wpf;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExTransparent = 0x00000020;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private const int WmActivate = 0x0006;
    private const int WmWindowPosChanged = 0x0047;

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private readonly TopmostReasserter _reasserter = new();
    private IntPtr _handle;
    private bool _clickThrough;
    private bool _taskbarMode;
    private bool _faded; // auto-hidden: Opacity 0 + forced click-through, but the window STAYS HWND_TOPMOST.
                         // "Hide" here is purely visual (opacity + hit-testing); z-order is never demoted, so
                         // returning to the game is a single Opacity flip with no reclaim race.
    public bool ClickThrough => _clickThrough;
    public bool TaskbarMode => _taskbarMode;

    /// <summary>Diagnostic-only: whether the overlay is currently auto-hide faded (Opacity 0). Read by
    /// <see cref="OverlayController"/>'s foreground-state trace. See overlay-autohide-unpark-on-return-rootcause.</summary>
    public bool DiagParked => _faded;

    /// <summary>Diagnostic-only: whether the HWND actually carries WS_EX_TOPMOST right now. This is the real
    /// symptom — a "presented" overlay (parked=false) with topmost=false is buried behind a fullscreen game.</summary>
    public bool DiagTopmost => _handle != IntPtr.Zero && OverlayZOrder.HasTopmostBit(_handle);

    /// <summary>Raised after a drag completes with the new Left/Top (App persists it).</summary>
    public event Action<double, double>? PositionChanged;

    /// <summary>Raised from the right-click menu (App opens the settings window / exits).</summary>
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    /// <summary>Header buttons (App handles): reset the meter / toggle taskbar (alt-tab) mode / open
    /// the battle-history panel.</summary>
    public event Action? ResetRequested;
    public event Action? TaskbarToggleRequested;
    public event Action? DummyTestToggleRequested;
    public event Action? HistoryRequested;
    public event Action? ThemeRequested;
    public event Action? JoinRequested;

    /// <summary>Footer 오드 badge clicked (App toggles the per-character 오드 panel).</summary>
    public event Action? AetherListRequested;

    /// <summary>Raise <see cref="AetherListRequested"/> from outside — the tray menu offers the same toggle,
    /// because the badge is hidden while 오드 표시 is off or before this session's first broadcast.</summary>
    public void RequestAetherList() => AetherListRequested?.Invoke();

    /// <summary>
    /// 전투 기록 / 설정을 바깥에서 요청한다 — 분리모드의 보스칸 창이 헤더를 대신해 쓴다.
    /// 핸들러(토글·위치 계산)는 App 에 하나뿐이므로 같은 이벤트로 흘려보낸다.
    /// </summary>
    public void RequestHistory() => HistoryRequested?.Invoke();

    public void RequestSettings() => SettingsRequested?.Invoke();

    /// <summary>Header update badge clicked (App shows the restart toast on demand).</summary>
    public event Action? UpdateRequested;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        // This window keeps ShowInTaskbar at its WPF default (true) — it must NOT be set to false in XAML.
        // WPF implements ShowInTaskbar=false by giving the window a hidden, NON-topmost OWNER window, and an
        // owned window cannot stay topmost above a borderless-fullscreen game — it gets pinned behind it
        // (the "overlay invisible after returning to the game" bug). The taskbar/Alt+Tab button is hidden
        // via WS_EX_TOOLWINDOW in SyncInputStyle instead.
        SyncInputStyle();
        // Re-assert the ex-style on focus/z-order changes so WPF can't strip TOOLWINDOW|NOACTIVATE
        // (the taskbar-flicker / focus-steal fix).
        HwndSource.FromHwnd(_handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmActivate or WmWindowPosChanged)
        {
            SyncInputStyle();
        }

        return IntPtr.Zero;
    }

    /// <summary>The single place the ex-style is asserted (TOOLWINDOW|LAYERED|NOACTIVATE &amp; ~APPWINDOW,
    /// toggling TRANSPARENT for click-through). No-op when unchanged.</summary>
    public void SyncInputStyle()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        int current = GetWindowLong(_handle, GwlExStyle);
        // Taskbar/Alt+Tab listing (APPWINDOW, activatable, no TOOLWINDOW/NOACTIVATE) applies ONLY while the
        // overlay is presented; a faded (auto-hidden) overlay falls back to the game-overlay style
        // (TOOLWINDOW|NOACTIVATE) so it leaves no blank taskbar/Alt+Tab entry. This keeps taskbar mode
        // (Option 2) orthogonal to auto-hide (Option 1): the listing follows Option 2, but only when the
        // overlay is actually on screen.
        bool taskbar = _taskbarMode && !_faded;
        int baseStyle = taskbar
            ? (current | WsExLayered | WsExAppWindow) & ~WsExToolWindow & ~WsExNoActivate
            : (current | WsExToolWindow | WsExLayered | WsExNoActivate) & ~WsExAppWindow;
        // While faded the window is invisible but stays HWND_TOPMOST, so it MUST be click-through (otherwise it
        // would eat clicks over its footprint); force WS_EX_TRANSPARENT whenever faded, regardless of the user's
        // click-through setting (which is restored on the next Present).
        bool transparent = _clickThrough || _faded;
        int next = transparent ? baseStyle | WsExTransparent : baseStyle & ~WsExTransparent;
        if (next == current)
        {
            return;
        }

        SetWindowLong(_handle, GwlExStyle, next);
        SetWindowPos(_handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    public void SetClickThrough(bool enable)
    {
        _clickThrough = enable;
        SyncInputStyle();
        NotifyClickThrough();
    }

    /// <summary>Push the current click-through state to the view model so the header lock badge tracks it.</summary>
    private void NotifyClickThrough()
    {
        if (DataContext is OverlayViewModel vm)
        {
            vm.SetClickThroughIndicator(_clickThrough);
        }
    }

    /// <summary>
    /// Toggle taskbar / alt-tab mode. Implemented purely via the ex-style
    /// (APPWINDOW↔TOOLWINDOW + NOACTIVATE) so the HWND is NOT recreated (WPF's ShowInTaskbar property
    /// would recreate it and break our handle/hook). A hide+show forces the shell to re-evaluate the
    /// taskbar button after the style change.
    /// </summary>
    public void SetTaskbarMode(bool enable)
    {
        _taskbarMode = enable;
        if (enable)
        {
            _clickThrough = false; // a taskbar/Alt+Tab window shouldn't pass input through to the game
        }

        // NOTE: visibility (Opacity) and topmost are owned by Present/Fade (driven by the auto-hide poll), so
        // taskbar mode stays orthogonal to auto-hide — we only flip the APPWINDOW/TOOLWINDOW ex-style bucket here.
        SyncInputStyle();

        if (_handle != IntPtr.Zero)
        {
            ShowWindow(_handle, SwHide);
            ShowWindow(_handle, SwShowNoActivate);
            SyncInputStyle(); // re-assert after the show
            if (!_faded)
            {
                ForceTopmost(); // SW_SHOWNOACTIVATE can drop us out of the topmost band — re-claim the top
                                // (user-paced action, not the per-tick poll, so an unconditional re-assert is fine)
            }
        }

        NotifyClickThrough(); // entering taskbar mode clears click-through — keep the badge in sync
    }

    /// <summary>Show the overlay fully: opaque, interactive (per the click-through setting), and HWND_TOPMOST.
    /// The overlay is topmost whenever it is alive — this never demotes. Idempotent: the
    /// 300ms auto-hide Poll calls this every tick, so SetWindowPos is issued ONLY on the transition out of the
    /// faded state (re-claiming the top of z-order once, in case the game re-topped while we were invisible).
    /// Steady-state ticks touch no z-order here, so hover tooltips don't flicker; the buried-only
    /// <see cref="ReassertTopmostIfBuried"/> is the per-tick guard for a genuine foreign bury.</summary>
    public void Present()
    {
        bool wasFaded = _faded;
        _faded = false; // presented: SyncInputStyle restores the click-through setting + taskbar/Alt+Tab ex-style
        Opacity = 1.0;
        Topmost = true;
        SyncInputStyle();
        if (_handle != IntPtr.Zero && wasFaded)
        {
            ReapplyTopmostIfBitLost(); // the HWND can lose WS_EX_TOPMOST while faded; if so, the `Topmost = true`
                                       // above is a WPF no-op (the property never changed) and the raw reclaim
                                       // below is silently reverted — toggle the property first to force WPF to
                                       // re-apply the native bit (else the overlay returns opaque but BURIED).
            ForceTopmost(); // returning from faded — re-claim the top of z-order once
        }
    }

    /// <summary>Auto-hide the overlay WITHOUT touching z-order: Opacity 0 and forced click-through (so clicks
    /// fall through the now-invisible surface), but the window STAYS HWND_TOPMOST. "Hide" is purely visual,
    /// never a demote / HWND_BOTTOM push, so returning to the game
    /// (<see cref="Present"/>) is a single Opacity flip with the window already on top: no reclaim race, which
    /// was the source of the intermittent "overlay gone even though AION2 is focused" reports. Idempotent.</summary>
    public void Fade()
    {
        if (_faded)
        {
            return; // already faded — keep it a true no-op on the steady auto-hide ticks
        }

        _faded = true; // auto-hidden: drop the taskbar/Alt+Tab ex-style so no blank entry is left
        Opacity = 0.0;
        // Topmost is intentionally NOT cleared and z-order is NOT changed — only opacity + hit-testing.
        SyncInputStyle(); // forces WS_EX_TRANSPARENT while faded so the invisible window passes clicks through
    }

    /// <summary>
    /// Re-claim HWND_TOPMOST when a FOREIGN topmost window — a borderless-fullscreen game re-asserting its
    /// own topmost (alt-enter, resolution/HDR transitions, game-owned popups) — has climbed above us. The
    /// 300ms poll calls this while the game is foreground. It reconciles "always above the game" with "no
    /// tooltip flicker": a true no-op on the common already-on-top path (no SetWindowPos, no recomposite),
    /// re-asserts WITHOUT SwpShowWindow only when actually buried, and keeps NOACTIVATE (never steals game
    /// foreground). No tooltip guard is needed: our own ToolTip/Popup is a SAME-PROCESS topmost HWND, which the
    /// foreign-window walk skips, so a re-assert never fires merely because our tooltip is up — only a genuine
    /// foreign bury triggers SetWindowPos. (The 6361280 tooltip-dismiss regression came from the OLD
    /// unconditional per-tick HWND_TOPMOST; this buried-only walk is not that. A tooltip-open guard here used to
    /// LATCH for the 20s ShowDuration and leave the overlay stuck behind the game after a header hover.)
    /// </summary>
    public void ReassertTopmostIfBuried()
    {
        if (_handle == IntPtr.Zero || _faded)
        {
            return; // faded = invisible; Present() re-claims the top of z-order on the unfade transition
        }

        ReapplyTopmostIfBitLost(); // recover a WPF-Topmost desync (bit lost while the DP still reads true) that
                                   // the raw buried-only reassert alone cannot fix; no-op unless the bit is lost
        _reasserter.ReassertIfBuried(_handle);
    }

    /// <summary>WPF Topmost-desync repair (mirrors <see cref="OverlayPanelWindow"/>, the fix that stopped the
    /// buff overlay falling behind the game): when the HWND has LOST WS_EX_TOPMOST while WPF's managed Topmost
    /// property still reads true, WPF will NOT re-apply the native bit (it sees no change) and a raw
    /// SetWindowPos(HWND_TOPMOST) is silently reverted — leaving the overlay opaque but buried behind a
    /// borderless-fullscreen game. Toggling the property false→true forces WPF to re-issue the native call.
    /// The two sets run synchronously with no message pump between them (Opacity is already 1), so there is no
    /// visible non-topmost frame; and it only fires when the bit is actually lost, so steady state does nothing.</summary>
    private void ReapplyTopmostIfBitLost()
    {
        if (_handle == IntPtr.Zero || OverlayZOrder.HasTopmostBit(_handle))
        {
            return;
        }

        Topmost = false;
        Topmost = true;
    }

    /// <summary>Unconditional re-claim of the top of the z-order (used on the parked → shown transition,
    /// where SW_SHOWNOACTIVATE can drop us out of the topmost band). The polled path goes through
    /// <see cref="ReassertTopmostIfBuried"/> instead, which only fires when something actually covers us.</summary>
    private void ForceTopmost()
    {
        OverlayZOrder.ForceTopmost(_handle);
        _reasserter.Reset();
    }

    private void OnDragHandle(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        double startLeft = Left, startTop = Top;
        DragMove();
        // 실제로 움직였을 때만 알린다. 이동 0인 클릭은 위치를 "새로 정한 것"이 아닌데, 미터는 이제 배율을
        // 끌어 키울 수 있어 일시적으로 화면 경계에 클램프돼 있을 수 있다 — 그 클램프된 좌표를 집(uiX/uiY)
        // 으로 저장해 버리면 사용자가 정한 자리가 클릭 한 번마다 조금씩 걸어간다.
        // OverlayPanelWindow 가 같은 사고를 같은 방식으로 이미 막고 있고, 미터 본체에만 없었다.
        if (Math.Abs(Left - startLeft) > 0.5 || Math.Abs(Top - startTop) > 0.5)
        {
            PositionChanged?.Invoke(Left, Top);
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnExit(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    // Header buttons: a reliable left-click path (the right-click ContextMenu is unreliable on a
    // WS_EX_NOACTIVATE window — the popup can't hold focus).
    private void OnSettingsButton(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnExitButton(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    private void OnResetButton(object sender, RoutedEventArgs e) => ResetRequested?.Invoke();

    private void OnTaskbarButton(object sender, RoutedEventArgs e) => TaskbarToggleRequested?.Invoke();

    private void OnDummyTestButton(object sender, RoutedEventArgs e) => DummyTestToggleRequested?.Invoke();

    private void OnHistoryButton(object sender, RoutedEventArgs e) => HistoryRequested?.Invoke();

    // The footer badge, not a header button: mark it handled so the click can't bubble on to anything else.
    private void OnAetherBadgeClick(object sender, MouseButtonEventArgs e)
    {
        AetherListRequested?.Invoke();
        e.Handled = true;
    }

    private void OnThemeButton(object sender, RoutedEventArgs e) => ThemeRequested?.Invoke();

    private void OnJoinButton(object sender, RoutedEventArgs e) => JoinRequested?.Invoke();

    private void OnUpdateButton(object sender, RoutedEventArgs e) => UpdateRequested?.Invoke();

    // OnRowClick 은 Controls/MeterRowsView.xaml.cs 로 옮겼다 — 행 마크업이 그 UserControl 로 갔고,
    // x:Class 가 있으므로 핸들러도 같은 파일에 있어야 XAML 이 찾는다.

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

}
