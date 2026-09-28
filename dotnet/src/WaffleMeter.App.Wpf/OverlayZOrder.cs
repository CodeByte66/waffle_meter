using System.Runtime.InteropServices;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// Shared z-order logic for the topmost overlay surfaces (meter, panels, detail window), which all have to
/// answer the same question: has a foreign window climbed above us, and should we re-claim HWND_TOPMOST?
///
/// The subtlety this exists to encode: "a foreign window sits above us in z-order" is NOT the same as "a
/// foreign window covers us". explorer permanently keeps <c>ThumbnailDeviceHelperWnd</c> (1x1 px) and
/// <c>DummyDWMListenerWindow</c> (0x0 px) above every app window, both TOPMOST and IsWindowVisible, and both
/// living in a higher window band we can never climb over. Treating them as a bury made every overlay
/// re-issue SetWindowPos(HWND_TOPMOST) on every poll, forever — measured at ~4.8 z-order re-inserts per
/// second per window while the game was foreground. Each re-insert is a change outside the game's process,
/// which forces DWM to re-decide whether the game keeps its independent-flip / hardware-overlay-plane fast
/// path. That churn is invisible in our own CPU time and shows up as the game's frame drops.
/// </summary>
internal static class OverlayZOrder
{
    /// <summary>A foreign window narrower or shorter than this cannot meaningfully cover us; the shell's
    /// helper windows are 1x1 and 0x0.</summary>
    private const int MinOccluderSize = 8;

    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x8;
    private const int WsExTransparent = 0x20;
    private const int WsExLayered = 0x80000;
    private const uint GwHwndPrev = 3;
    private const uint GwOwner = 4;
    private const int DwmwaCloaked = 14;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopMost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    /// <summary>True while the HWND still carries WS_EX_TOPMOST. Losing it (a WPF owned-window / z-order
    /// shuffle) is the desync that used to leave the meter pinned behind a fullscreen game.</summary>
    public static bool HasTopmostBit(IntPtr handle) => (GetWindowLong(handle, GwlExStyle) & WsExTopmost) != 0;

    /// <summary>The first foreign window that is both above us in z-order AND actually overlaps us, or
    /// <see cref="IntPtr.Zero"/> when nothing covers us. Same-process windows (our tooltips, popups, sibling
    /// panels) are skipped, as are cloaked windows, anything too small to cover anything, and other apps'
    /// click-through overlays (see <see cref="IsClickThroughOverlay"/>).</summary>
    public static IntPtr FindOccluder(IntPtr handle)
    {
        if (!GetWindowRect(handle, out Rect self) || IsDegenerate(self))
        {
            return IntPtr.Zero;
        }

        // Cheap, in-process checks first. IsCloaked is a call into DWM, and this walk runs on every poll for
        // every presented overlay — the shell's helper windows must be rejected on geometry, not on an RPC.
        int ownPid = Environment.ProcessId;
        for (IntPtr above = GetWindow(handle, GwHwndPrev); above != IntPtr.Zero; above = GetWindow(above, GwHwndPrev))
        {
            GetWindowThreadProcessId(above, out uint pid);
            if ((int)pid == ownPid || !IsWindowVisible(above) || IsClickThroughOverlay(above))
            {
                continue;
            }

            if (!GetWindowRect(above, out Rect other) || IsDegenerate(other) || !Intersects(self, other))
            {
                continue;
            }

            if (IsCloaked(above))
            {
                continue; // in the z-order but not rendered — cannot cover us
            }

            return above;
        }

        return IntPtr.Zero;
    }

    /// <summary>Force HWND_TOPMOST without show/activation (NOACTIVATE — never steals the game's foreground).
    /// Re-topmosts any window OWNER first: an owned window cannot sit above a non-topmost owner. We keep
    /// ShowInTaskbar at its default so there is normally no owner and that step is a no-op.</summary>
    public static void ForceTopmost(IntPtr handle)
    {
        IntPtr owner = GetWindow(handle, GwOwner);
        if (owner != IntPtr.Zero)
        {
            SetWindowPos(owner, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }

        SetWindowPos(handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// Another app's in-game overlay: layered (per-pixel alpha) and click-through. These are drawn over the game
    /// for the same reason we are, and they keep themselves topmost by re-claiming the top whenever something
    /// rises above them. Discord's overlay is a full-screen <c>Discord Overlay</c> window of exactly this shape.
    /// Treating it as an occluder started a fight nobody can win: every poll we rose above it, it rose back
    /// 15–110ms later, and <see cref="TopmostReasserter"/> scored each round a win because it judges right after
    /// SetWindowPos — ~4 z-order swaps a second, each forcing DWM to recomposite, which users saw as flicker.
    /// Such a window is mostly transparent, so we lose nothing by sitting under it.
    /// </summary>
    private static bool IsClickThroughOverlay(IntPtr handle)
    {
        int exStyle = GetWindowLong(handle, GwlExStyle);
        return (exStyle & (WsExTransparent | WsExLayered)) == (WsExTransparent | WsExLayered);
    }

    // Cloaked = present in the z-order but not rendered (suspended UWP apps, virtual-desktop residents).
    private static bool IsCloaked(IntPtr handle) =>
        DwmGetWindowAttribute(handle, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool IsDegenerate(Rect r) =>
        r.Right - r.Left < MinOccluderSize || r.Bottom - r.Top < MinOccluderSize;

    private static bool Intersects(Rect a, Rect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
}

/// <summary>
/// Per-window state for "re-claim the top when something genuinely covers us". Beyond the occlusion test in
/// <see cref="OverlayZOrder"/>, this refuses to keep hammering an occluder it cannot beat: a window in a
/// higher band (the shell, and the taskbar if the user parks the overlay over it) stays above us no matter
/// how often we call SetWindowPos, and re-trying every poll is exactly the churn that costs the game frames.
/// After a few failed attempts against the same window we stand down until the situation changes.
/// </summary>
internal sealed class TopmostReasserter
{
    private const int MaxAttemptsPerOccluder = 3;

    private IntPtr _occluder;
    private int _attempts;

    public void ReassertIfBuried(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // Losing our own topmost bit is always worth fixing, and always winnable.
        if (!OverlayZOrder.HasTopmostBit(handle))
        {
            OverlayZOrder.ForceTopmost(handle);
            Reset();
            return;
        }

        IntPtr occluder = OverlayZOrder.FindOccluder(handle);
        if (occluder == IntPtr.Zero)
        {
            Reset(); // on top (or covered by nothing that matters) — the common path, no SetWindowPos
            return;
        }

        if (occluder != _occluder)
        {
            _occluder = occluder;
            _attempts = 0;
        }
        else if (_attempts >= MaxAttemptsPerOccluder)
        {
            return; // we already lost to this window three times; stop churning z-order over it
        }

        OverlayZOrder.ForceTopmost(handle);

        // Judge the attempt immediately, not on the next poll. A window that re-claims the top a moment
        // later (a game re-asserting its own topmost) must not be mistaken for one we cannot beat: we DID
        // rise above it, so the counter resets and we will happily fight it again. Only an occluder that is
        // still above us the instant after SetWindowPos returns — a higher window band — counts as a loss.
        if (OverlayZOrder.FindOccluder(handle) == occluder)
        {
            _attempts++;
        }
        else
        {
            Reset();
        }
    }

    /// <summary>Forget the standoff (call after an explicit re-claim, e.g. unfading).</summary>
    public void Reset()
    {
        _occluder = IntPtr.Zero;
        _attempts = 0;
    }
}
