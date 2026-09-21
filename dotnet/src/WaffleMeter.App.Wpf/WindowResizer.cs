using System.Windows;
using System.Windows.Interop;
using WaffleMeter.App.Core;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// Adds edge/corner resize to a borderless (WindowStyle=None, AllowsTransparency) window by answering
/// WM_NCHITTEST with the matching HT* code when the cursor is within <c>margin</c> DIPs of an edge —
/// so dragging the top/bottom/left/right/corners resizes. The window must be ResizeMode=CanResize;
/// MinWidth/MinHeight bound the shrink.
/// <para><paramref name="onResizeEnd"/> reports the finished gesture (which handle was grabbed + the height
/// before/after) once the modal resize loop exits. The meter needs it because WPF turns
/// <c>SizeToContent</c> off the moment ANY resize starts — see <see cref="WindowResizePolicy"/> — so the
/// caller has to decide whether to switch height auto-fit back on.</para>
/// </summary>
public static class WindowResizer
{
    private const int WmNcHitTest = 0x0084, WmSysCommand = 0x0112, WmExitSizeMove = 0x0232;
    private const int ScSize = 0xF000, ScMask = 0xFFF0;

    /// <summary>한 번의 크기 조절 제스처가 끝난 시점. <see cref="HitCode"/>가
    /// <see cref="WindowResizePolicy.HtUnknown"/>이면 어느 핸들에서 시작됐는지 알 수 없다는 뜻이다.</summary>
    public readonly record struct ResizeEnd(int HitCode, double HeightBefore, double HeightAfter);

    /// <summary>한 번의 크기 조절 제스처가 <b>시작된</b> 시점 — 잡은 핸들과 그 순간의 창 크기.
    /// <para>미터는 좌/우 가장자리 드래그를 <b>배율</b>로 읽는데, 그 비율의 분모가 이 시작 폭이다. 상수
    /// 기본폭을 분모로 쓰면 폭을 이미 조절해 둔 사용자가 가장자리를 잡는 순간 배율이 튄다.</para></summary>
    public readonly record struct ResizeStart(int HitCode, double Width, double Height);

    public static void Attach(Window window, double margin = 6, Action<ResizeEnd>? onResizeEnd = null,
        Action<ResizeStart>? onResizeStart = null)
    {
        var hook = new Hook(window, margin, onResizeEnd, onResizeStart);

        // The window may already be shown (Attach is called after Show), in which case SourceInitialized
        // has fired — add the hook now; otherwise wait for it.
        if (PresentationSource.FromVisual(window) is HwndSource existing)
        {
            existing.AddHook(hook.WndProc);
        }
        else
        {
            window.SourceInitialized += (_, _) =>
            {
                if (PresentationSource.FromVisual(window) is HwndSource source)
                {
                    source.AddHook(hook.WndProc);
                }
            };
        }
    }

    /// <summary>Per-window state (the grabbed handle + the height the gesture started at), so the five
    /// windows sharing this class can't read each other's drag.</summary>
    private sealed class Hook(Window window, double margin, Action<ResizeEnd>? onResizeEnd,
        Action<ResizeStart>? onResizeStart)
    {
        private int _lastHit = WindowResizePolicy.HtUnknown;
        private bool _sizing;
        private double _heightAtStart;

        public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WmNcHitTest:
                    return HitTest(lParam, ref handled);

                // WPF가 바로 이 메시지에서 SizeToContent를 끈다(실측) — 제스처 시작 높이를 여기서 잡아야
                // 자동 맞춤이 꺼지기 전 높이와 비교할 수 있다. 제목표시줄 드래그 이동은 SC_MOVE라 안 걸린다.
                case WmSysCommand when ((int)wParam & ScMask) == ScSize:
                    _sizing = true;
                    _heightAtStart = window.ActualHeight;
                    onResizeStart?.Invoke(new ResizeStart(_lastHit, window.ActualWidth, _heightAtStart));
                    return IntPtr.Zero;

                case WmExitSizeMove when _sizing:
                    _sizing = false;
                    int hit = _lastHit;
                    _lastHit = WindowResizePolicy.HtUnknown; // 다음 제스처가 이 코드를 물려받지 않게
                    onResizeEnd?.Invoke(new ResizeEnd(hit, _heightAtStart, window.ActualHeight));
                    return IntPtr.Zero;

                default:
                    return IntPtr.Zero;
            }
        }

        private IntPtr HitTest(IntPtr lParam, ref bool handled)
        {
            // ToInt64: 커서가 주 모니터 위/왼쪽 모니터에 있으면 스크린 좌표가 음수이고, x64에서 그 lParam이
            // zero-extend 되어 오면 ToInt32()가 OverflowException을 던진다. 하위 32비트만 쓰므로 결과는 같다.
            long lp = lParam.ToInt64();
            var screenPoint = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)); // physical px
            Point p = window.PointFromScreen(screenPoint);                                   // DIPs from window top-left
            int code = WindowResizePolicy.Resolve(p.X, p.Y, window.ActualWidth, window.ActualHeight, margin);
            if (code == WindowResizePolicy.HtClient)
            {
                return IntPtr.Zero; // let the normal client hit-testing (drag handle, buttons) run
            }

            if (!_sizing)
            {
                _lastHit = code; // 잡은 핸들 = 크기 조절이 시작되기 직전의 히트 코드 (마우스·터치 공통 경로)
            }

            handled = true;
            return (IntPtr)code;
        }
    }
}
