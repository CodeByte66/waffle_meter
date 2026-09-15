using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// Shared base for secondary overlay panels (join requests, battle history). Provides the same
/// game-overlay windowing as <see cref="OverlayWindow"/> — WS_EX_NOACTIVATE|TOOLWINDOW so the panel
/// never steals focus/GPU from AION2 — plus drag-to-move, a close button, and <see cref="Present"/> /
/// <see cref="Park"/> visibility (park keeps the HWND + ex-style alive). Subclasses hook
/// <see cref="OnPresented"/> / <see cref="OnParked"/> (e.g. to run a countdown timer).
/// </summary>
public abstract class OverlayPanelWindow : Window, IReassertableOverlay
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExTransparent = 0x00000020; // click-through (input passes through to the window below)

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndTop = new(0);
    private static readonly IntPtr HwndNoTopMost = new(-2);
    private static readonly IntPtr HwndBottom = new(1);

    private const int WmActivate = 0x0006;
    private const int WmWindowPosChanged = 0x0047;

    private IntPtr _handle;
    private bool _dragging;
    private Point _surfacePress;     // 표면 드래그: 누른 지점 (임계값 판정용)
    private bool _surfaceArmed;      // 누른 뒤 아직 드래그로 승격되지 않은 상태
    private readonly TopmostReasserter _reasserter = new();
    private bool? _presentedTopMost; // last applied present state; null = parked -> ReassertTopmostIfBuried no-ops
    private bool _faded;             // auto-hidden (opacity 0) but STILL topmost — mirrors OverlayWindow.Fade
    private bool _clickThrough;      // user click-through: input passes through even while presented

    /// <summary>Raised after a drag completes with the new Left/Top (App persists it).</summary>
    public event Action<double, double>? PositionChanged;

    /// <summary>Raised when the user clicks ✕ (App parks the panel).</summary>
    public event Action? CloseRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        // Keep ShowInTaskbar at its WPF default (true) — do NOT set it false in XAML. WPF implements
        // ShowInTaskbar=false with a hidden, non-topmost OWNER window, and an owned window can't stay topmost
        // above a borderless-fullscreen game. We hide from the taskbar via WS_EX_TOOLWINDOW below instead.
        SyncInputStyle();
        HwndSource.FromHwnd(_handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Skip the re-assert while a drag is in flight: WM_WINDOWPOSCHANGED fires on every move tick, and
        // re-applying the frame style mid-drag forces a repaint that reads as a flicker. Re-assert once
        // when the drag ends (OnDragHandle).
        if ((msg is WmActivate or WmWindowPosChanged) && !_dragging)
        {
            SyncInputStyle();
        }

        return IntPtr.Zero;
    }

    /// <summary>Assert the overlay ex-style (TOOLWINDOW|LAYERED|NOACTIVATE &amp; ~APPWINDOW). No-op when unchanged.</summary>
    private void SyncInputStyle()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        int current = GetWindowLong(_handle, GwlExStyle);
        int baseStyle = (current | WsExToolWindow | WsExLayered | WsExNoActivate) & ~WsExAppWindow;
        // Click-through while the user enabled it, OR while faded (invisible-but-topmost must let clicks fall
        // through its footprint) — mirrors OverlayWindow.SyncInputStyle.
        bool transparent = _clickThrough || _faded;
        int next = transparent ? baseStyle | WsExTransparent : baseStyle & ~WsExTransparent;
        if (next == current)
        {
            return;
        }

        SetWindowLong(_handle, GwlExStyle, next);
        SetWindowPos(_handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    /// <summary>Apply the user's click-through setting (input passes through even while shown).</summary>
    public void SetClickThrough(bool enable)
    {
        if (_clickThrough == enable)
        {
            return;
        }

        _clickThrough = enable;
        SyncInputStyle();
    }

    /// <summary>Show the panel (topMost tracks the game). Mirrors OverlayWindow.Present: re-applies the WPF
    /// Topmost/Opacity EVERY call (cheap no-ops when unchanged) so WPF keeps maintaining HWND_TOPMOST — the
    /// old idempotent early-return skipped this, letting WPF's Topmost drift and the window fall behind the
    /// game. The SetWindowPos(SwpShowWindow) re-claim fires only on the parked/faded → present transition.</summary>
    public void Present(bool topMost)
    {
        bool needShow = _faded || _presentedTopMost != topMost; // transition out of faded/parked
        _faded = false;
        Topmost = topMost;
        Opacity = 1.0;
        _presentedTopMost = topMost; // arm ReassertTopmostIfBuried while shown
        SyncInputStyle();
        if (needShow && _handle != IntPtr.Zero)
        {
            SetWindowPos(_handle, topMost ? HwndTopMost : HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }

        if (needShow)
        {
            OnPresented();
        }
    }

    /// <summary>Auto-hide WITHOUT touching z-order: Opacity 0 + forced click-through, but STILL HWND_TOPMOST
    /// (mirrors OverlayWindow.Fade). Idempotent. Used for the buff overlay so it hides/returns in lockstep with
    /// the meter — no HWND_BOTTOM demote (which caused the reclaim-race "gone and won't come back").</summary>
    public void Fade()
    {
        if (_faded)
        {
            return; // already faded — steady-state no-op
        }

        _faded = true;
        Opacity = 0.0;
        // Topmost + z-order are intentionally left alone — only opacity + hit-testing (via SyncInputStyle).
        SyncInputStyle();
        OnParked();
    }

    /// <summary>Hide the panel (HWND + ex-style survive). Full hide: opacity 0 AND dropped from the topmost
    /// band (used when a panel is closed). The buff overlay uses <see cref="Fade"/> instead.</summary>
    public void Park()
    {
        OnParked();
        _faded = false;
        Opacity = 0.0;
        Topmost = false;
        _presentedTopMost = null; // parked -> ReassertTopmostIfBuried no-ops until the next Present
        if (_handle != IntPtr.Zero)
        {
            SetWindowPos(_handle, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            SetWindowPos(_handle, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }

        SyncInputStyle();
    }

    /// <summary>패널이 화면에서 내려가 있는가(Fade/Park 둘 다 참). <see cref="OverlayWindow.DiagParked"/> 와
    /// 같은 뜻이며, UI 분리모드에서 "지금 보이는 미터"가 본체가 아닐 때 토글이 볼 상태다.</summary>
    public bool DiagParked => _faded || Opacity == 0.0;

    /// <summary>True while a drag started from the panel's drag handle is in flight. Anything that repositions
    /// the panel on its own schedule has to sit out the drag — moving it mid-drag yanks it out from under the
    /// cursor (the same reason the topmost re-assert and the input-style sync below skip it).</summary>
    public bool IsDragging => _dragging;

    /// <summary>
    /// Re-claim HWND_TOPMOST when a FOREIGN topmost window (a borderless-fullscreen game re-asserting its own
    /// topmost on alt-tab return / alt-enter / a game-owned popup) has climbed above this panel. Mirrors
    /// <see cref="OverlayWindow.ReassertTopmostIfBuried"/>: the meter's 300ms poll drives it for every open
    /// panel while AION2 is foreground, so a panel left open across an alt-tab no longer stays buried behind
    /// the game. A true no-op on the common already-on-top path (no SetWindowPos, no recomposite); re-asserts
    /// WITHOUT SwpShowWindow only when actually buried; keeps NOACTIVATE (never steals the game's foreground);
    /// and is skipped while parked or mid-drag. No tooltip guard: our own tooltip/popup is a same-process
    /// topmost HWND the walk skips, so it never triggers a re-assert (see OverlayWindow for the full rationale).
    /// </summary>
    public void ReassertTopmostIfBuried()
    {
        if (_handle == IntPtr.Zero || _faded || _presentedTopMost != true || _dragging)
        {
            return; // parked/faded/dragging or not topmost-presented — nothing to re-claim
        }

        // WPF Topmost desync: the property reads true but the HWND lost WS_EX_TOPMOST, and a raw
        // SetWindowPos(HWND_TOPMOST) alone is silently reverted by WPF. Only when the bit is actually LOST,
        // toggle the property so WPF re-applies HWND_TOPMOST (synchronous → no visible flicker). Doing this
        // only on bit-loss (not on every "a foreign window is above" bury) avoids toggling every tick.
        if (!OverlayZOrder.HasTopmostBit(_handle))
        {
            Topmost = false;
            Topmost = true;
        }

        _reasserter.ReassertIfBuried(_handle);
    }

    /// <summary>Called after the panel is presented (subclass hook, e.g. start a timer).</summary>
    protected virtual void OnPresented() { }

    /// <summary>Called before the panel is parked (subclass hook, e.g. stop a timer).</summary>
    protected virtual void OnParked() { }

    protected void OnDragHandle(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            BeginDrag();
        }
    }

    /// <summary>
    /// 창 표면 전체를 드래그 핸들로 쓰되, <b>실제로 움직였을 때만</b> 드래그로 승격한다. 헤더가 없는 창
    /// (UI 분리모드의 보스칸·미터 행)이 이걸 쓴다.
    ///
    /// <para>🔑 <c>MouseLeftButtonDown</c> 에서 바로 <c>DragMove</c> 를 부르면 안 된다. DragMove 는 버튼이
    /// 떨어질 때까지 도는 모달 이동 루프라 뒤이을 <c>MouseLeftButtonUp</c> 을 통째로 삼킨다 — 미터 행은
    /// 클릭 판정을 Up 에서 하므로(<c>MeterRowsView.OnRowClick</c>) 행을 눌러도 상세가 안 열리게 된다.
    /// 그래서 Down 에선 좌표만 적어 두고, 4px 넘게 움직인 순간에만 승격한다.</para>
    ///
    /// <para>버블링 이벤트로 다는 것도 요점이다 — 버튼은 <c>MouseLeftButtonDown</c> 을 Handled 로 삼키므로
    /// 전투기록/설정 버튼 위에서는 여기까지 오지 않는다(누르고 살짝 흔들었다고 창이 끌려가지 않는다).</para>
    /// </summary>
    protected void OnDragSurfaceDown(object sender, MouseButtonEventArgs e)
    {
        _surfacePress = e.GetPosition(this);
        _surfaceArmed = true;
    }

    protected void OnDragSurfaceUp(object sender, MouseButtonEventArgs e) => _surfaceArmed = false;

    protected void OnDragSurfaceMove(object sender, MouseEventArgs e)
    {
        if (!_surfaceArmed)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _surfaceArmed = false; // 창 밖에서 버튼을 뗐다 — 다음 누름까지 무장 해제
            return;
        }

        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _surfacePress.X) < 4.0 && Math.Abs(now.Y - _surfacePress.Y) < 4.0)
        {
            return; // 아직 클릭일 수 있다
        }

        _surfaceArmed = false;
        BeginDrag();
    }

    private void BeginDrag()
    {
        double startLeft = Left, startTop = Top;
        _dragging = true;
        try
        {
            DragMove();
        }
        finally
        {
            _dragging = false;
        }

        SyncInputStyle(); // re-assert once now the drag has settled
        // 실제로 움직였을 때만 알린다. 이동 0인 클릭은 위치를 "새로 정한 것"이 아닌데, 버프 오버레이는
        // 창 전체가 드래그 핸들이고 폭이 자라 일시적으로 클램프돼 있을 수 있어서 — 그 좌표를 저장해
        // 버리면 사용자가 정한 자리가 클릭 한 번에 사라진다.
        if (Math.Abs(Left - startLeft) > 0.5 || Math.Abs(Top - startTop) > 0.5)
        {
            PositionChanged?.Invoke(Left, Top);
        }
    }

    protected void OnCloseButton(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

}
