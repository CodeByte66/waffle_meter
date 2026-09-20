using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WaffleMeter.App.Core;

namespace WaffleMeter.App.Wpf.Controls;

/// <summary>
/// HSV/alpha color picker, a port of the React <c>ColorPicker.tsx</c>: saturation/value square, hue +
/// alpha tracks, HEX/RGBA text + format toggle, and a 16-swatch preset grid. The two-way bindable
/// <see cref="Color"/> string round-trips through <see cref="ColorString"/> (hex or rgba()).
/// </summary>
public partial class ColorPickerControl : UserControl
{
    private static readonly string[] PresetList =
    {
        "#ff4d4d", "#ff8c42", "#ffd166", "#06d6a0", "#55c42a", "#3a9e20", "#4ecdc4", "#45b7d1",
        "#6c63ff", "#a855f7", "#f3a5ff", "#ff6b9d", "#ffffff", "#95ddff", "#ffc837", "#e8960a",
    };

    private double _h, _s, _v, _a = 1.0; // hue 0-360, sat 0-1, value 0-1, alpha 0-1
    private bool _hexPreferred = true;
    private bool _applying;     // suppress re-entrant emit while applying an external Color
    private bool _editingText;  // suppress text refresh while the user types

    // ---- drag deferral (M-30) ----
    // A track/square drag is in flight. While it is, the picker updates its own preview on every mouse-move
    // but does NOT push the value out through <see cref="Color"/>.
    private bool _dragging;
    private bool _pendingEmit;      // a deferred move is waiting to be published
    private string? _pendingFormat; // the last move's format hint (alpha < 1 forces rgba)

    public ColorPickerControl()
    {
        InitializeComponent();
        Presets.ItemsSource = PresetList;
        Loaded += (_, _) => ApplyColorString(Color);
    }

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ColorPickerControl),
        new FrameworkPropertyMetadata("#ffffff", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColorChanged));

    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (ColorPickerControl)d;
        if (c._applying)
        {
            return; // our own emit — don't re-parse
        }

        c.ApplyColorString((string)e.NewValue);
    }

    private void ApplyColorString(string? value)
    {
        if (!ColorString.TryParse(value, out ColorRgba c))
        {
            c = new ColorRgba(255, 255, 255, 255);
        }

        (_h, _s, _v) = RgbToHsv(c.R, c.G, c.B);
        _a = c.A / 255.0;
        _hexPreferred = (value ?? string.Empty).TrimStart().StartsWith('#');
        RefreshAll(updateText: true);
    }

    // ---- emit ----

    private void Emit(string? formatHint = null)
    {
        if (_dragging)
        {
            // 🔑 M-30. 바인딩 대상(테마·버프 색)은 세터에서 곧바로 settings.properties **파일 전체**를 다시
            // 쓴다(PropertyHandler.SetProperty -> Save). 그래서 종전에는 채도 사각형을 1초 끄는 동안
            // 마우스무브 수십~수백 번이 그대로 동기 디스크 쓰기 수십~수백 번이 됐다 — UI 스레드에서.
            // 팝업 안의 미리보기(칩·커서·그라디언트)는 지금처럼 매 프레임 갱신하고, 값은 마우스를 뗄 때
            // 한 번만 내보낸다. 최종 색은 같고 쓰기는 드래그당 1회다.
            _pendingEmit = true;
            _pendingFormat = formatHint;
            RefreshAll(updateText: false);
            return;
        }

        (byte r, byte g, byte b) = HsvToRgb(_h, _s, _v);
        string fmt = formatHint ?? (_a < 1.0 ? "rgba" : _hexPreferred ? "hex" : "rgba");
        string outStr = ColorString.Serialize(r, g, b, _a, preferHex: fmt == "hex");

        _applying = true;
        Color = outStr;
        _applying = false;
        RefreshAll(updateText: false);
    }

    // ---- drag start / end ----

    /// <summary>
    /// Ends a deferred drag and publishes the value once.
    /// <para>⚠ Wired to <c>LostMouseCapture</c>, not to mouse-up — <c>ReleaseMouseCapture()</c> raises it too,
    /// so this is the <b>single</b> exit path. Capture can also be taken away from us (Alt-Tab, the popup
    /// closing, a modal opening); with mouse-up as the only exit, that would strand the drag's last color in
    /// memory and it would never be saved.</para>
    /// </summary>
    private void OnDragEnded(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (!_pendingEmit)
        {
            return;
        }

        _pendingEmit = false;
        string? fmt = _pendingFormat;
        _pendingFormat = null;
        Emit(fmt);
    }

    /// <summary>
    /// ⚠ Fail-open: <c>CaptureMouse()</c> returns false when something else already holds capture. Deferring
    /// in that case would leave <c>_dragging</c> stuck true forever — the picker would silently stop saving,
    /// with no symptom until restart. Without capture we simply emit per move, exactly as before.
    /// </summary>
    private static bool BeginDrag(UIElement surface) => surface.CaptureMouse();

    // ---- saturation / value square ----

    private void OnSvDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = BeginDrag(SvSquare);
        SetSvFrom(e.GetPosition(SvSquare));
    }

    private void OnSvMove(object sender, MouseEventArgs e)
    {
        if (SvSquare.IsMouseCaptured)
        {
            SetSvFrom(e.GetPosition(SvSquare));
        }
    }

    private void OnSvUp(object sender, MouseButtonEventArgs e) => SvSquare.ReleaseMouseCapture();

    private void SetSvFrom(Point p)
    {
        double w = SvSquare.ActualWidth, h = SvSquare.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        _s = Math.Clamp(p.X / w, 0, 1);
        _v = Math.Clamp(1 - p.Y / h, 0, 1);
        Emit();
    }

    // ---- hue / alpha tracks ----

    private void OnHueDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = BeginDrag(HueTrack);
        SetHueFrom(e.GetPosition(HueTrack).X);
    }

    private void OnHueMove(object sender, MouseEventArgs e)
    {
        if (HueTrack.IsMouseCaptured)
        {
            SetHueFrom(e.GetPosition(HueTrack).X);
        }
    }

    private void SetHueFrom(double x)
    {
        if (HueTrack.ActualWidth <= 0)
        {
            return;
        }

        _h = Math.Clamp(x / HueTrack.ActualWidth, 0, 1) * 360;
        Emit();
    }

    private void OnAlphaDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = BeginDrag(AlphaTrack);
        SetAlphaFrom(e.GetPosition(AlphaTrack).X);
    }

    private void OnAlphaMove(object sender, MouseEventArgs e)
    {
        if (AlphaTrack.IsMouseCaptured)
        {
            SetAlphaFrom(e.GetPosition(AlphaTrack).X);
        }
    }

    private void SetAlphaFrom(double x)
    {
        if (AlphaTrack.ActualWidth <= 0)
        {
            return;
        }

        // React alpha slider is 0-100 step 1 -> snap to whole percent.
        double pct = Math.Round(Math.Clamp(x / AlphaTrack.ActualWidth, 0, 1) * 100);
        _a = pct / 100.0;
        Emit(_a < 1.0 ? "rgba" : null);
    }

    private void OnTrackUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    // ---- format toggle / text / presets ----

    private void OnHexFormat(object sender, RoutedEventArgs e)
    {
        _hexPreferred = true;
        Emit("hex");
    }

    private void OnRgbaFormat(object sender, RoutedEventArgs e)
    {
        _hexPreferred = false;
        Emit("rgba");
    }

    private void OnTextInputChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        string raw = ColorInput.Text;
        if (!ColorString.TryParse(raw, out ColorRgba c))
        {
            return; // ignore partial/invalid input until it parses
        }

        (_h, _s, _v) = RgbToHsv(c.R, c.G, c.B);
        _a = c.A / 255.0;
        _hexPreferred = raw.TrimStart().StartsWith('#');
        _editingText = true;
        Emit(_hexPreferred ? "hex" : "rgba");
        _editingText = false;
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string hex || !ColorString.TryParse(hex, out ColorRgba c))
        {
            return;
        }

        (_h, _s, _v) = RgbToHsv(c.R, c.G, c.B);
        _a = 1.0;
        _hexPreferred = true;
        Emit("hex");
    }

    // ---- refresh ----

    private void OnSquareSizeChanged(object sender, SizeChangedEventArgs e) => RefreshCursors();

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => RefreshCursors();

    private void RefreshAll(bool updateText)
    {
        (byte r, byte g, byte b) = HsvToRgb(_h, _s, _v);
        (byte hr, byte hg, byte hb) = HsvToRgb(_h, 1, 1);

        HueRect.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(hr, hg, hb));
        CurrentChip.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)Math.Round(_a * 255), r, g, b));
        TextChip.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        AlphaGradient.Background = new LinearGradientBrush(
            System.Windows.Media.Color.FromArgb(0, r, g, b),
            System.Windows.Media.Color.FromArgb(255, r, g, b),
            new Point(0, 0.5), new Point(1, 0.5));

        HueLabel.Text = $"{Math.Round(_h)}°";
        AlphaLabel.Text = $"{Math.Round(_a * 100)}%";
        HexToggle.IsChecked = _hexPreferred;
        RgbaToggle.IsChecked = !_hexPreferred;

        if (updateText && !_editingText)
        {
            ColorInput.Text = _hexPreferred ? ColorString.Hex(r, g, b) : ColorString.Css(r, g, b, _a);
        }

        RefreshCursors();
    }

    private void RefreshCursors()
    {
        double sw = SvSquare.ActualWidth, sh = SvSquare.ActualHeight;
        if (sw > 0 && sh > 0)
        {
            Canvas.SetLeft(SvCursor, _s * sw - 7);
            Canvas.SetTop(SvCursor, (1 - _v) * sh - 7);
        }

        if (HueTrack.ActualWidth > 0)
        {
            Canvas.SetLeft(HueThumb, _h / 360 * HueTrack.ActualWidth - 6);
        }

        if (AlphaTrack.ActualWidth > 0)
        {
            Canvas.SetLeft(AlphaThumb, _a * AlphaTrack.ActualWidth - 6);
        }
    }

    // ---- HSV <-> RGB (color-utils.ts) ----

    private static (double H, double S, double V) RgbToHsv(byte rb, byte gb, byte bb)
    {
        double r = rb / 255.0, g = gb / 255.0, b = bb / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d != 0)
        {
            if (max == r) { h = (g - b) / d + (g < b ? 6 : 0); }
            else if (max == g) { h = (b - r) / d + 2; }
            else { h = (r - g) / d + 4; }
            h *= 60;
        }

        double s = max == 0 ? 0 : d / max;
        return (h, s, max);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        double hh = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(hh / 60 % 2 - 1)), m = v - c;
        double r = 0, g = 0, b = 0;
        if (hh < 60) { r = c; g = x; }
        else if (hh < 120) { r = x; g = c; }
        else if (hh < 180) { g = c; b = x; }
        else if (hh < 240) { g = x; b = c; }
        else if (hh < 300) { r = x; b = c; }
        else { r = c; b = x; }

        return (ToByte((r + m) * 255), ToByte((g + m) * 255), ToByte((b + m) * 255));
    }

    private static byte ToByte(double n) => (byte)Math.Clamp((int)Math.Round(n, MidpointRounding.AwayFromZero), 0, 255);
}
