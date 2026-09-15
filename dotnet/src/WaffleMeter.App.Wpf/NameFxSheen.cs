using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// The moving part of the nickname effects and the ranker gauge skins.
/// <para><b>Why this is an animation and not a DispatcherTimer.</b> The first version nudged the gradient
/// origin from a 250 ms timer, the same shape <see cref="TierSheen"/> uses. That is fine for a ring sweeping
/// once every few seconds, but a highlight travelling along a nickname at 4 updates per second reads as a row
/// of steps, and raising the speed setting only makes the steps bigger.</para>
/// <para><b>Why this does not contradict "no Storyboards in this codebase".</b> That rule (see
/// <see cref="TierSheen"/>) is about animations living INSIDE a row DataTemplate: <c>OverlayViewModel.Update</c>
/// replaces every row each tick, so a template-owned Storyboard restarts every tick and stutters. These brushes
/// are process-wide singletons that no row owns, so the animation is started once and is never touched by a row
/// rebuild.</para>
/// <para><b>Cost control.</b> <c>Timeline.SetDesiredFrameRate</c> pins the clock to 30 fps; the animation only
/// exists while something on screen uses it; the meter being hidden or parked stops it outright
/// (<see cref="SetParked"/>); and low-spec mode refuses it (<see cref="SetLowSpec"/>). Every demand source
/// passes through the SAME gate in <see cref="Sync"/> — an earlier version gated only the row path, so the
/// settings preview could drive the meter's clock in low-spec mode.</para>
/// <para>⚠ <b>Units.</b> <c>Brush.Transform</c> applies in the painted shape's ABSOLUTE (DIP) space regardless
/// of <c>MappingMode</c>; only <c>Brush.RelativeTransform</c> works in the 0~1 box space. Mixing them is silent:
/// a relative-mapped brush translated 1.0 through <c>Transform</c> moves ONE PIXEL, not one tile, which is
/// exactly how the gauge skins shipped frozen in review. The period constants are named for their unit and each
/// track records which slot it animates so the two can never drift apart again.</para>
/// </summary>
public static class NameFxSheen
{
    /// <summary>Nickname tile width in DIP — absolute, so the band is the same physical size on every nickname.</summary>
    private const double NamePeriodPx = 90;

    /// <summary>
    /// Gauge tile as a FRACTION of the bar: one tile = one bar. An absolute tile looked right on the top row and
    /// wrong everywhere else — a bar at 16% is only ~65 px long, so it never got past the dark head of the
    /// gradient. Relative mapping gives every row the whole effect.
    /// </summary>
    private const double GaugePeriodRelative = 1.0;

    /// <summary>Seconds for one full tile at 100% speed. Slow enough to sit behind numbers you are reading.</summary>
    private const double NameSeconds = 2.5;

    private const double GaugeSeconds = 3.5;

    /// <summary>30 fps. 60 doubles the compositing work on an <c>AllowsTransparency</c> layered window for a
    /// difference that does not survive a moving game behind it.</summary>
    private const int FrameRate = 30;

    private sealed record Track(LinearGradientBrush Brush, TranslateTransform Transform, double Period, double Seconds);

    // ★ The only unfrozen brushes this feature owns. Shared, and animated in place.
    private static readonly Dictionary<(string Id, bool Light), Track> Tracks = Build();

    private static int _rowDemand;
    private static bool _previewDemand;
    /// <summary>
    /// 창별 표시 상태. 분리모드에서는 미터가 창 2~3개로 흩어지므로 "미터가 화면에 없다"를 전역
    /// 불린 하나로 잡을 수 없다 — 본체를 트레이로 내려도 분리 창이 떠 있으면 연출은 계속돼야 한다.
    /// <para>🔑 규칙은 '보이는 호스트가 0개면 정지'가 <b>아니라</b> '등록된 호스트가 1개 이상이고 그
    /// 전부가 숨김일 때만 정지'다. 그래야 호스트를 아무도 보고하지 않는 상태(하네스, 그리고 기동 후
    /// 첫 폴링 전)가 옛 <c>_parked == false</c> 와 같은 뜻이 된다.</para>
    /// <para>⚠️ 창이 닫히면 반드시 <see cref="RemoveHost"/> 를 불러야 한다. 닫힌 창을 visible 로
    /// 남겨두면 정지가 영영 안 걸려 유휴 CPU 회귀가 조용히 생긴다.</para>
    /// </summary>
    private static readonly Dictionary<object, bool> Hosts = new(ReferenceEqualityComparer.Instance);

    private static bool _parked => Hosts.Count > 0 && !Hosts.ContainsValue(true);
    private static bool _lowSpec;
    private static int _speedPercent = 100;
    private static bool _running;

    /// <summary>The shared live brush for an animated effect or gauge skin. Same instance every call.
    /// UI thread only — these are unfrozen <see cref="Freezable"/>s owned by the dispatcher that first touched
    /// them.</summary>
    public static Brush BrushFor(string id, bool isLight) => Tracks[(id, isLight)].Brush;

    /// <summary>Is the sweep actually running? Observable because "nothing moves" has several very different
    /// causes — no demand, parked, low-spec, or a demand source nobody wired up — and only the last is a bug.</summary>
    public static bool IsRunning => _running;

    /// <summary>The user's motion speed, 50~200. Read by <see cref="GaugeFxClock"/> so the decoration layer and
    /// the nickname sheen move at one setting rather than two.</summary>
    public static int SpeedPercent => _speedPercent;

    /// <summary>
    /// Report how many animated rows this frame drew. Zero (or the feature off) releases the animation, so a
    /// meter sitting in town costs nothing.
    /// </summary>
    public static void SetDemand(int count, bool enabled, int speedPercent)
    {
        _rowDemand = enabled ? Math.Max(0, count) : 0;
        Sync(speedPercent);
    }

    /// <summary>
    /// The settings preview strip is a SECOND, independent demand source. Without it the strip sits frozen:
    /// grants come from the server, so a user deciding whether to keep the animation on has no decorated row on
    /// screen, row demand is zero, and the clock everything depends on is stopped.
    /// </summary>
    public static void SetPreviewDemand(bool on, int speedPercent)
    {
        _previewDemand = on;
        Sync(speedPercent);
    }

    /// <summary>
    /// Latch for "the meter is not on screen". A one-shot <c>SetDemand(0)</c> from the hide path only lasted
    /// until the next report tick, because <c>OverlayViewModel.Update</c> keeps running while hidden and
    /// reports demand again ~500 ms later. The clock has to stay down until the window comes back.
    /// </summary>
    public static void SetParked(bool parked) => SetHostVisible(MainMeterHost, !parked);

    /// <summary>본체 미터를 가리키는 호스트 키. <see cref="SetParked"/> 하위호환용.</summary>
    public static readonly object MainMeterHost = new();

    /// <summary>이 창이 화면에 있는지 보고한다. 분리 창도 자기 몫을 각자 보고한다.</summary>
    public static void SetHostVisible(object host, bool visible)
    {
        Hosts[host] = visible;
        Sync(_speedPercent);
    }

    /// <summary>창이 닫혔다. 빼지 않으면 그 창이 영원히 '보이는 중'으로 남아 정지가 안 걸린다.</summary>
    public static void RemoveHost(object host)
    {
        if (Hosts.Remove(host))
        {
            Sync(_speedPercent);
        }
    }

    /// <summary>Low-spec mode refuses the animation outright, for every demand source at once.</summary>
    public static void SetLowSpec(bool lowSpec)
    {
        _lowSpec = lowSpec;
        Sync(_speedPercent);
    }

    private static void Sync(int speedPercent)
    {
        bool want = !_parked && !_lowSpec && (_rowDemand > 0 || _previewDemand);

        // Speed is only meaningful while running. Updating it on the way DOWN would let the park path
        // (which has no user setting to hand) overwrite the user's choice with a default.
        if (want)
        {
            int speed = Math.Clamp(speedPercent, 50, 200);
            if (_running && speed == _speedPercent)
            {
                return; // already running at this speed; restarting would visibly jump the phase
            }

            _speedPercent = speed;
            foreach (Track t in Tracks.Values)
            {
                var anim = new DoubleAnimation
                {
                    From = 0,
                    To = t.Period,
                    Duration = TimeSpan.FromSeconds(t.Seconds * 100.0 / speed),
                    RepeatBehavior = RepeatBehavior.Forever,
                };
                Timeline.SetDesiredFrameRate(anim, FrameRate);
                anim.Freeze();
                t.Transform.BeginAnimation(TranslateTransform.XProperty, anim);
            }

            _running = true;
            GaugeFxClock.OnGateChanged();
            return;
        }

        if (!_running)
        {
            return;
        }

        foreach (Track t in Tracks.Values)
        {
            t.Transform.BeginAnimation(TranslateTransform.XProperty, null);
            t.Transform.X = 0;
        }

        _running = false;
        GaugeFxClock.OnGateChanged();
    }

    /// <summary>
    /// Park the sweep at a fixed phase, for the offline preview harness only. Rendering to a bitmap has no
    /// animation clock, so without this a captured frame cannot show that the transform reaches the pixels —
    /// and a stopped animation looks exactly like a working one in a single frame.
    /// <para>Restores the demand-derived state on the way out, so <see cref="IsRunning"/> never reports a
    /// fourth "posed" state that no demand flag explains.</para>
    /// </summary>
    public static void SetPreviewPhase(double fraction)
    {
        foreach (Track t in Tracks.Values)
        {
            t.Transform.BeginAnimation(TranslateTransform.XProperty, null);
            t.Transform.X = t.Period * fraction;
        }

        _running = false;
    }

    /// <summary>Recolour the shared brushes for a new brightness. Called on a settings change, never per frame —
    /// rebuilding stops is the expensive half and the slider commits on drag-end.
    /// <para>Note this covers the gauge skins too: one brightness control, both surfaces.</para></summary>
    public static void Rebuild(int brightnessPercent)
    {
        double factor = Math.Clamp(brightnessPercent, 70, 130) / 100.0;
        foreach (((string id, bool light), Track t) in Tracks)
        {
            NameFxPalette.Effect? e = NameFxPalette.Find(id);
            if (e is null)
            {
                continue;
            }

            t.Brush.GradientStops.Clear();
            NameFxPalette.AddStops(t.Brush, e, light);
            foreach (GradientStop s in t.Brush.GradientStops)
            {
                s.Color = NameFxPalette.Scale(s.Color, factor);
            }
        }
    }

    private static Dictionary<(string, bool), Track> Build()
    {
        var map = new Dictionary<(string, bool), Track>();
        foreach (NameFxPalette.Effect e in NameFxPalette.All.Where(x => x.Animated))
        {
            double period = e.IsGauge ? GaugePeriodRelative : NamePeriodPx;
            double seconds = e.IsGauge ? GaugeSeconds : NameSeconds;
            foreach (bool light in new[] { false, true })
            {
                var transform = new TranslateTransform();
                var b = new LinearGradientBrush
                {
                    // Repeat is what makes a translation equal a seamless flow: the gradient tiles every
                    // `period`, so sliding by exactly one period lands on an identical pattern.
                    MappingMode = e.IsGauge ? BrushMappingMode.RelativeToBoundingBox : BrushMappingMode.Absolute,
                    SpreadMethod = GradientSpreadMethod.Repeat,
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(period, 0),
                };
                NameFxPalette.AddStops(b, e, light);

                // ⚠ The slot MUST match the mapping — see the type remarks. Relative mapping needs
                // RelativeTransform; absolute mapping needs Transform. Getting this wrong does not fail, it
                // just quietly moves the brush by a pixel and looks like a still gradient.
                if (e.IsGauge)
                {
                    b.RelativeTransform = transform;
                }
                else
                {
                    b.Transform = transform;
                }

                map[(e.Id, light)] = new Track(b, transform, period, seconds); // NOT frozen — animated in place.
            }
        }

        return map;
    }
}
