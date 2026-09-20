using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WaffleMeter.App.Wpf;

public partial class DetailWindow : Window, IReassertableOverlay
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    // 🔑 WS_EX_APPWINDOW. WPF 는 ShowInTaskbar 기본값(true) 때문에 HWND 를 만들 때 이걸 세우는데,
    // 셸 규칙상 APPWINDOW 가 TOOLWINDOW 를 **이긴다**("Forces a top-level window onto the taskbar when the
    // window is visible"). 그래서 TOOLWINDOW 만 OR 하면 창이 작업표시줄·Alt+Tab 목록에 그대로 남는다 —
    // 실제로 '상세내역' 이 Alt+Tab 에 뜬다는 제보가 이것이었다(2026-09-19). 반드시 같이 지워야 한다.
    private const int WsExAppWindow = 0x00040000;

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private readonly TopmostReasserter _reasserter = new();
    private IntPtr _handle;
    private bool _dragging;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    public DetailWindow()
    {
        InitializeComponent();
        // The DPS graph is hand-drawn on GraphCanvas (software render, no external chart lib — the ReplayWindow
        // map is the approved precedent). Redraw when the canvas is (re)sized — including the first time its tab
        // is selected and it gets a non-zero size — and when the view model publishes a new Graph.
        GraphCanvas.SizeChanged += (_, _) => DrawGraph();
        GraphCanvas.MouseMove += GraphCanvas_MouseMove;
        GraphCanvas.MouseLeave += (_, _) => ClearHover();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        // ShowInTaskbar stays at its WPF default (true) — must NOT be false in XAML, or WPF attaches a hidden
        // non-topmost OWNER window that pins this window behind a fullscreen game. WS_EX_TOOLWINDOW (below)
        // keeps it off the taskbar instead.
        //
        // ⚠️ 그런데 TOOLWINDOW 를 OR 하는 것만으로는 부족하다 — WPF 가 세워 둔 WS_EX_APPWINDOW 를 **같이
        // 지워야** 한다. 셸에서는 APPWINDOW 가 TOOLWINDOW 를 이기므로, 안 지우면 창이 Alt+Tab 에 남는다.
        // 이 창은 NOACTIVATE 라 Alt+Tab 으로 골라도 포커스를 못 받는 반쪽 항목이 된다. 오버레이·패널은
        // 전부 `& ~APPWINDOW` 를 하고 있었고(OverlayPanelWindow.SyncInputStyle, OverlayWindow) 여기만 빠져 있었다.
        int exStyle = GetWindowLong(_handle, GwlExStyle);
        int next = (exStyle | WsExNoActivate | WsExToolWindow) & ~WsExAppWindow;
        if (next != exStyle)
        {
            SetWindowLong(_handle, GwlExStyle, next);
            // 스타일만 바꾸면 셸이 목록을 다시 평가하지 않는다. 여기선 창이 아직 안 보일 때(OnSourceInitialized)
            // 고치므로 hide+show 까지는 필요 없지만, FRAMECHANGED 로 비-클라이언트 영역 재계산은 시켜 둔다.
            SetWindowPos(_handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
    }

    /// <summary>
    /// Re-claim HWND_TOPMOST when a FOREIGN topmost window (borderless-fullscreen AION2 re-asserting its own
    /// topmost on alt-tab return) has climbed above this detail window. Driven by the meter's 300ms poll while
    /// the game is foreground, exactly like the meter and the other panels, so a detail window left open across
    /// an alt-tab no longer stays buried behind the game. A true no-op on the already-on-top path; re-asserts
    /// WITHOUT show/activation only when actually buried (NOACTIVATE — never steals the game's foreground); and
    /// is skipped while hidden/closed or mid-drag. No tooltip guard: our own tooltip/popup is a same-process
    /// topmost HWND the walk skips, so it never triggers a re-assert (see OverlayWindow for the rationale).
    /// </summary>
    public void ReassertTopmostIfBuried()
    {
        if (_handle == IntPtr.Zero || !IsVisible || _dragging)
        {
            return;
        }

        _reasserter.ReassertIfBuried(_handle);
    }

    private void OnDragHandle(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            _dragging = true;
            try
            {
                DragMove();
            }
            finally
            {
                _dragging = false;
            }
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ── DPS graph (4th tab) ────────────────────────────────────────────────────────────────────────────────
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Size Inf = new(double.PositiveInfinity, double.PositiveInfinity);

    private static readonly Brush LineBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF))); // ValueCyan
    private static readonly Brush AreaFill = Frozen(new SolidColorBrush(Color.FromArgb(0x2E, 0x2D, 0xD4, 0xBF)));
    private static readonly Brush GridMinorBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush GridMajorBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush AxisTextBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xBC, 0xCB, 0xD5, 0xE1)));
    private static readonly Brush LaneTrackBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush CrosshairBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TipBackBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0x0F, 0x17, 0x2A)));
    private static readonly Brush TipBorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x88, 0x2D, 0xD4, 0xBF)));
    private static readonly Brush AvgLineBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFB, 0xBF, 0x24))); // amber

    private const double LaneRowH = 16.0;
    private const double PadT = 6.0, PadB = 18.0, PadR = 12.0;
    private const double LeftGutter = 48.0; // holds the y-axis DPS value labels

    private INotifyPropertyChanged? _boundVm;
    private GraphLayout? _layout;                       // last-drawn mapping, so hover reads without re-laying-out
    private readonly List<UIElement> _hoverElements = [];

    /// <summary>The pixel↔data mapping of the last DrawGraph, captured so mouse-hover can place the crosshair and
    /// read the DPS at the hovered 3-second bucket without recomputing the whole layout.</summary>
    private sealed record GraphLayout(
        double PlotLeft, double PlotRight, double PlotTop, double PlotBottom, double TopY,
        int Seconds, double Peak, double AvgDps,
        IReadOnlyList<double> BucketCenterSec, IReadOnlyList<double> BucketDps);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm != null)
        {
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _boundVm = e.NewValue as INotifyPropertyChanged;
        if (_boundVm != null)
        {
            _boundVm.PropertyChanged += OnViewModelPropertyChanged;
        }

        DrawGraph();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Empty name = "everything changed"; otherwise only the Graph property drives a redraw.
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(DetailsViewModel.Graph))
        {
            DrawGraph();
        }
    }

    private void DrawGraph()
    {
        if (GraphCanvas is null)
        {
            return;
        }

        GraphCanvas.Children.Clear();
        _hoverElements.Clear();
        _layout = null;

        if (DataContext is not DetailsViewModel vm || vm.Graph is not { } g || g.PerSecond.Count < 2)
        {
            return; // tab is disabled via HasGraph in this state; nothing to draw
        }

        double w = GraphCanvas.ActualWidth;
        double h = GraphCanvas.ActualHeight;
        if (w < 80 || h < 70)
        {
            return; // not yet arranged (e.g. tab never shown) — SizeChanged will call back when it is
        }

        int buffRows = g.Buffs.Count; // already capped + colour-assigned by the view model
        double plotLeft = LeftGutter;
        double plotRight = w - PadR;
        double plotBottom = h - PadB;
        double plotW = plotRight - plotLeft;

        // Fit the buff-lane stack + chart into the height we have: when many buffs meet a short window, shrink the
        // lane rows (down to a floor) so the chart always keeps a usable height instead of collapsing to nothing.
        double laneGap = buffRows > 0 ? 6.0 : 0.0;
        const double minPlotH = 70.0;
        double avail = plotBottom - PadT;
        double laneRowH = LaneRowH;
        double laneH = buffRows * laneRowH;
        if (buffRows > 0 && laneH + laneGap + minPlotH > avail)
        {
            laneRowH = Math.Max(5.0, (avail - laneGap - minPlotH) / buffRows);
            laneH = buffRows * laneRowH;
        }

        double plotTop = PadT + laneH + laneGap;
        double plotH = plotBottom - plotTop;
        if (plotW < 40 || plotH < 24)
        {
            return; // too small to be a chart; leave blank rather than draw garbage
        }

        int n = g.PerSecond.Count; // domain = [0, n] seconds

        // Aggregate to 3-second buckets (value = average DPS over the window): smoother than raw per-second, and
        // reads as "the DPS around this moment". A bucket with no damage is a 0 gap, used to break the line.
        const int secPerBucket = 3;
        int bucketCount = (n + secPerBucket - 1) / secPerBucket;
        var bucketDps = new double[bucketCount];
        var bucketCenter = new double[bucketCount];
        long grand = 0;
        double peak = 1.0;
        for (int b = 0; b < bucketCount; b++)
        {
            int s0 = b * secPerBucket;
            int s1 = Math.Min(s0 + secPerBucket, n);
            long sum = 0;
            for (int i = s0; i < s1; i++) sum += g.PerSecond[i];
            grand += sum;
            double v = s1 > s0 ? (double)sum / (s1 - s0) : 0.0;
            bucketDps[b] = v;
            bucketCenter[b] = (s0 + s1) / 2.0;
            if (v > peak) peak = v;
        }

        double avgDps = n > 0 ? (double)grand / n : 0.0; // == the meter's DPS (total damage ÷ duration)

        double XOf(double sec) => plotLeft + Math.Clamp(sec, 0, n) / n * plotW;
        double YOf(double v) => plotBottom - v / peak * plotH;

        // ── Time (x) grid: labelled major lines at a "nice" second step, spanning the buff lanes AND the plot so
        //    the two share one time ruler; faint minor lines at each 3-second bucket when they'd be legible. ──
        int majorStep = NiceSeconds(n / Math.Max(plotW / 64.0, 1.0));
        if (plotW / Math.Max(bucketCount, 1) >= 9.0)
        {
            for (int b = 1; b < bucketCount; b++)
            {
                double xm = XOf(b * secPerBucket);
                GraphCanvas.Children.Add(MakeLine(xm, plotTop, xm, plotBottom, GridMinorBrush));
            }
        }

        for (int s = 0; s <= n; s += majorStep)
        {
            double x = XOf(s);
            GraphCanvas.Children.Add(MakeLine(x, PadT, x, plotBottom, GridMajorBrush));
            TextBlock t = AxisLabel(Mmss(s));
            t.Measure(Inf);
            Canvas.SetLeft(t, Math.Clamp(x - t.DesiredSize.Width / 2, 0, w - t.DesiredSize.Width));
            Canvas.SetTop(t, plotBottom + 3);
            GraphCanvas.Children.Add(t);
        }

        // ── Value (y) grid: baseline / mid / peak, DPS value labelled in the left gutter. ──
        foreach (double frac in new[] { 0.0, 0.5, 1.0 })
        {
            double y = plotBottom - frac * plotH;
            GraphCanvas.Children.Add(MakeLine(plotLeft, y, plotRight, y, frac == 0.0 ? GridMajorBrush : GridMinorBrush));
            TextBlock t = AxisLabel(Compact((long)Math.Round(peak * frac)));
            t.Measure(Inf);
            Canvas.SetLeft(t, plotLeft - 5 - t.DesiredSize.Width);
            Canvas.SetTop(t, Math.Clamp(y - t.DesiredSize.Height / 2, PadT, plotBottom));
            GraphCanvas.Children.Add(t);
        }

        // ── Average-DPS reference line (= the number the meter shows). The curve below is INSTANTANEOUS DPS and
        //    swings around this flat average, which is why the end-of-fight value differs from the meter's overall
        //    DPS — one is "right now", the other is total damage ÷ the whole duration. ──
        if (avgDps > 0 && avgDps <= peak)
        {
            double ay = YOf(avgDps);
            Line avgLine = MakeLine(plotLeft, ay, plotRight, ay, AvgLineBrush);
            avgLine.StrokeDashArray = [4, 3];
            GraphCanvas.Children.Add(avgLine);
            TextBlock t = AxisLabel($"평균 {Compact((long)Math.Round(avgDps))}/s");
            t.Foreground = AvgLineBrush;
            t.Measure(Inf);
            Canvas.SetLeft(t, plotRight - t.DesiredSize.Width - 2);
            Canvas.SetTop(t, Math.Clamp(ay - t.DesiredSize.Height - 1, plotTop, plotBottom - t.DesiredSize.Height));
            GraphCanvas.Children.Add(t);
        }

        // ── DPS curve: smooth (Catmull-Rom) segments over runs of active buckets, BROKEN where DPS is 0 (no
        //    damage) so downtime shows as a gap instead of the line diving to the floor and climbing back. ──
        int run = 0;
        while (run < bucketCount)
        {
            if (bucketDps[run] <= 0.0)
            {
                run++;
                continue;
            }

            int start = run;
            while (run < bucketCount && bucketDps[run] > 0.0) run++;
            var pts = new List<Point>();
            for (int b = start; b < run; b++) pts.Add(new Point(XOf(bucketCenter[b]), YOf(bucketDps[b])));

            if (pts.Count == 1)
            {
                var dot = new Ellipse { Width = 5, Height = 5, Fill = LineBrush, IsHitTestVisible = false };
                Canvas.SetLeft(dot, pts[0].X - 2.5);
                Canvas.SetTop(dot, pts[0].Y - 2.5);
                GraphCanvas.Children.Add(dot);
                continue;
            }

            GraphCanvas.Children.Add(SmoothAreaPath(pts, plotBottom));
            GraphCanvas.Children.Add(SmoothLinePath(pts));
        }

        // ── Buff lanes (one row per class buff): a faint full-width track shows the row's extent, and the solid
        //    bands on top are the uptime. The lane colour matches the legend chip above the chart. ──
        for (int r = 0; r < buffRows; r++)
        {
            DpsGraphBuff buff = g.Buffs[r];
            double rowY = PadT + r * laneRowH;
            double bandH = Math.Max(3.0, laneRowH - 5.0);

            var track = new Rectangle { Width = plotW, Height = bandH, RadiusX = 2, RadiusY = 2, Fill = LaneTrackBrush, IsHitTestVisible = false };
            Canvas.SetLeft(track, plotLeft);
            Canvas.SetTop(track, rowY);
            GraphCanvas.Children.Add(track);

            foreach ((double startSec, double endSec) in buff.Spans)
            {
                double x0 = XOf(startSec);
                double bw = Math.Max(2.0, XOf(endSec) - x0);
                var rect = new Rectangle { Width = bw, Height = bandH, RadiusX = 2, RadiusY = 2, Fill = buff.LaneBrush, IsHitTestVisible = false };
                Canvas.SetLeft(rect, x0);
                Canvas.SetTop(rect, rowY);
                GraphCanvas.Children.Add(rect);
            }
        }

        _layout = new GraphLayout(plotLeft, plotRight, plotTop, plotBottom, PadT, n, peak, avgDps, bucketCenter, bucketDps);
    }

    // Catmull-Rom control points → a smooth open stroke through the run's points.
    private static System.Windows.Shapes.Path SmoothLinePath(IReadOnlyList<Point> pts)
    {
        var fig = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
        foreach (BezierSegment s in SmoothBeziers(pts)) fig.Segments.Add(s);
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return new System.Windows.Shapes.Path
        {
            Data = geo,
            Stroke = LineBrush,
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
    }

    // The same smooth curve, closed down to the baseline for a translucent area fill.
    private static System.Windows.Shapes.Path SmoothAreaPath(IReadOnlyList<Point> pts, double baseline)
    {
        var fig = new PathFigure { StartPoint = new Point(pts[0].X, baseline), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(pts[0], true));
        foreach (BezierSegment s in SmoothBeziers(pts)) fig.Segments.Add(s);
        fig.Segments.Add(new LineSegment(new Point(pts[^1].X, baseline), true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return new System.Windows.Shapes.Path { Data = geo, Fill = AreaFill, IsHitTestVisible = false };
    }

    private static List<BezierSegment> SmoothBeziers(IReadOnlyList<Point> p)
    {
        var segs = new List<BezierSegment>(p.Count - 1);
        for (int k = 0; k < p.Count - 1; k++)
        {
            Point p0 = p[Math.Max(k - 1, 0)];
            Point p1 = p[k];
            Point p2 = p[k + 1];
            Point p3 = p[Math.Min(k + 2, p.Count - 1)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
            segs.Add(new BezierSegment(c1, c2, p2, true));
        }

        return segs;
    }

    // ── Hover: crosshair at the hovered second + a tooltip reading that second's DPS ─────────────────────────
    private void GraphCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        ClearHover();
        if (_layout is not { } lay)
        {
            return;
        }

        Point p = e.GetPosition(GraphCanvas);
        if (p.X < lay.PlotLeft || p.X > lay.PlotRight || lay.BucketDps.Count == 0)
        {
            return;
        }

        double plotW = lay.PlotRight - lay.PlotLeft;
        double plotH = lay.PlotBottom - lay.PlotTop;
        double mSec = (p.X - lay.PlotLeft) / plotW * lay.Seconds;
        int bi = Math.Clamp((int)(mSec / 3.0), 0, lay.BucketDps.Count - 1);
        double val = lay.BucketDps[bi];
        double x = lay.PlotLeft + Math.Clamp(lay.BucketCenterSec[bi], 0, lay.Seconds) / lay.Seconds * plotW;

        // Vertical crosshair through the buff lanes AND the plot — reads which buffs were up + the DPS at this moment.
        Hover(MakeLine(x, lay.TopY, x, lay.PlotBottom, CrosshairBrush));

        double dotY = lay.PlotBottom;
        if (val > 0)
        {
            dotY = lay.PlotBottom - val / (lay.Peak > 0 ? lay.Peak : 1) * plotH;
            var dot = new Ellipse { Width = 8, Height = 8, Fill = LineBrush, Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
            Canvas.SetLeft(dot, x - 4);
            Canvas.SetTop(dot, dotY - 4);
            Hover(dot);
        }

        int tSec = (int)Math.Round(lay.BucketCenterSec[bi]);
        var text = new TextBlock { Text = $"{Mmss(tSec)}   {val.ToString("N0", Inv)}/s", Foreground = Brushes.White, FontSize = 11, IsHitTestVisible = false };
        var tip = new Border
        {
            Background = TipBackBrush,
            BorderBrush = TipBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3),
            Child = text,
            IsHitTestVisible = false,
        };
        tip.Measure(Inf);
        double tw = tip.DesiredSize.Width, th = tip.DesiredSize.Height;
        Canvas.SetLeft(tip, Math.Clamp(x + 10, lay.PlotLeft, lay.PlotRight - tw));
        Canvas.SetTop(tip, Math.Clamp((val > 0 ? dotY : lay.PlotTop) - th - 8, lay.TopY, lay.PlotBottom - th));
        Hover(tip);
    }

    private void Hover(UIElement el)
    {
        _hoverElements.Add(el);
        GraphCanvas.Children.Add(el);
    }

    private void ClearHover()
    {
        if (GraphCanvas is null)
        {
            return;
        }

        foreach (UIElement el in _hoverElements)
        {
            GraphCanvas.Children.Remove(el);
        }

        _hoverElements.Clear();
    }

    private static Line MakeLine(double x1, double y1, double x2, double y2, Brush stroke) => new()
    {
        X1 = x1,
        Y1 = y1,
        X2 = x2,
        Y2 = y2,
        Stroke = stroke,
        StrokeThickness = 1,
        IsHitTestVisible = false,
    };

    private static TextBlock AxisLabel(string text) => new()
    {
        Text = text,
        Foreground = AxisTextBrush,
        FontSize = 10,
        IsHitTestVisible = false,
    };

    // A "nice" whole-second step ≥ the raw target, so the time labels land on readable boundaries.
    private static int NiceSeconds(double raw)
    {
        int[] steps = [1, 2, 5, 10, 15, 20, 30, 60, 120, 300, 600, 900];
        foreach (int s in steps)
        {
            if (s >= raw)
            {
                return s;
            }
        }

        return steps[^1];
    }

    private static string Mmss(int seconds)
    {
        int m = seconds / 60;
        int s = seconds % 60;
        return $"{m}:{s:D2}";
    }

    private static string Compact(long v)
    {
        double d = v;
        return v >= 1_000_000_000 ? (d / 1_000_000_000).ToString("0.#", Inv) + "B"
            : v >= 1_000_000 ? (d / 1_000_000).ToString("0.#", Inv) + "M"
            : v >= 1_000 ? (d / 1_000).ToString("0.#", Inv) + "K"
            : v.ToString(Inv);
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}
