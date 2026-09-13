using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using WaffleMeter.Data;

namespace WaffleMeter.App.Wpf;

/// <summary>View model for the combat-assist overlay: the local player's active buff slots, refreshed on a
/// timer from the data layer. Slots are reconciled in place (by code) so the icons don't flicker.</summary>
public sealed class BuffOverlayViewModel : INotifyPropertyChanged
{
    // Panel chrome shown only when the transparent-background option is OFF, so the window can be located
    // and dragged even with no active buffs. Frozen for cheap software rendering.
    private static readonly Brush PanelBg = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x18, 0x21)));
    private static readonly Brush PanelBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x78, 0x84, 0x9B)));

    public ObservableCollection<BuffSlotVM> Slots { get; } = new();

    private double _iconScale = 1.0;
    /// <summary>Uniform scale applied to each slot (icon + ring + text). The native design is the 40px icon,
    /// so scale = size/40 (32 → 0.8 = 80%, 40 → 1.0 = 100%, 80 → 2.0 = 200%). Set from the icon-size setting.</summary>
    public double IconScale { get => _iconScale; private set => Set(ref _iconScale, value); }

    /// <summary>Set the buff icon size in px; drives <see cref="IconScale"/> off the 40px native design.
    /// 이 범위는 <c>MeterSettings.BuffUiIconSize</c> 의 클램프와 반드시 같아야 한다 — 다르면 저장된 값과 화면이
    /// 조용히 어긋난다(종전 상한 72px 는 200%(80px)를 1.8배로 깎았을 것이다).</summary>
    public void SetIconSize(int px) => IconScale = Math.Clamp(px, 32, 80) / 40.0;

    private Brush _textBrush = Brushes.White;
    /// <summary>Countdown-text color (from the setting). White by default.</summary>
    public Brush TextBrush { get => _textBrush; private set => Set(ref _textBrush, value); }

    private string _textColorHex = "";
    /// <summary>Set the countdown-text color from a hex string; falls back to white on a bad value.</summary>
    public void SetTextColor(string hex)
    {
        if (_textColorHex == hex)
        {
            return;
        }

        _textColorHex = hex;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(hex) ? "#FFFFFF" : hex)!;
            var b = new SolidColorBrush(c);
            b.Freeze();
            TextBrush = b;
        }
        catch
        {
            TextBrush = Brushes.White;
        }
    }

    private bool _showBackground;
    /// <summary>When true, draw a panel background + border + placeholder so the (possibly empty) window is
    /// visible and draggable; when false the overlay is just floating icons on a transparent background.</summary>
    public bool ShowBackground
    {
        get => _showBackground;
        set
        {
            if (_showBackground == value)
            {
                return;
            }

            _showBackground = value;
            PanelBackground = value ? PanelBg : Brushes.Transparent;
            PanelBorderBrush = value ? PanelBorder : Brushes.Transparent;
            PanelBorderThickness = value ? new Thickness(1) : new Thickness(0);
            RecomputePlaceholder();
        }
    }

    private Brush _panelBackground = Brushes.Transparent;
    public Brush PanelBackground { get => _panelBackground; private set => Set(ref _panelBackground, value); }

    private Brush _panelBorderBrush = Brushes.Transparent;
    public Brush PanelBorderBrush { get => _panelBorderBrush; private set => Set(ref _panelBorderBrush, value); }

    private Thickness _panelBorderThickness;
    public Thickness PanelBorderThickness { get => _panelBorderThickness; private set => Set(ref _panelBorderThickness, value); }

    private Visibility _emptyVisibility = Visibility.Collapsed;
    /// <summary>Shown (placeholder) only when the background is on AND there are no active slots.</summary>
    public Visibility EmptyVisibility { get => _emptyVisibility; private set => Set(ref _emptyVisibility, value); }

    private void RecomputePlaceholder() => EmptyVisibility = _showBackground && Slots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>Replace the slot list from a fresh snapshot, reusing existing rows by code so only the
    /// countdown text + ring progress change on a normal tick.</summary>
    /// <param name="expiryWarnMs">남은 시간이 이 값 이하면 아이콘을 점멸시킨다. 0 = "버프 종료 3초 전 알림" 꺼짐.
    /// 판정은 500ms 틱마다 하므로 점멸 시작이 최대 한 틱 늦을 수 있다 — 3초 창에서는 눈에 띄지 않는다.</param>
    public void Update(IReadOnlyList<OwnerBuffView> buffs, bool grayOnCooldown, bool showLevel = true, long expiryWarnMs = 0)
    {
        // remove slots no longer present
        for (int i = Slots.Count - 1; i >= 0; i--)
        {
            if (!buffs.Any(b => b.Code == Slots[i].Code))
            {
                Slots.RemoveAt(i);
            }
        }

        // 넘어온 순서가 곧 표시 순서다(BuffOverlayOrder가 정한다). 예전에는 새 슬롯을 뒤에 붙이기만 해서
        // 화면 순서가 "처음 뜬 순서"로 고착됐고, 데이터 계층이 정렬해 넘겨도 아무 효과가 없었다.
        // 자리가 실제로 다를 때만 Move 한다 — 매 틱 무조건 옮기면 아이콘이 떨린다.
        for (int target = 0; target < buffs.Count; target++)
        {
            OwnerBuffView b = buffs[target];
            bool onCooldown = grayOnCooldown && b.OnCooldown; // only gray when the option is on
            // A maintained stance (폭주) has only a synthetic keep-alive, not a real countdown — draw it as a
            // plain "on" icon (no ring, no timer) by reporting an unknown duration.
            long dur = b.Indefinite ? 0 : b.DurationMs;

            int current = -1;
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].Code == b.Code)
                {
                    current = i;
                    break;
                }
            }

            // 무기한 유지 자세(폭주)는 만료가 합성 keep-alive 라 점멸시키면 매번 거짓 경고가 된다 — 음성 쪽이
            // 같은 이유로 이미 제외하고 있다.
            bool expiring = expiryWarnMs > 0 && !b.Indefinite && b.RemainingMs > 0 && b.RemainingMs <= expiryWarnMs;

            if (current < 0)
            {
                var slot = new BuffSlotVM(b.Code, b.Name, b.RemainingMs, dur, b.ByOther, onCooldown, b.Level, showLevel);
                slot.SetExpiring(expiring);
                Slots.Insert(Math.Min(target, Slots.Count), slot);
                continue;
            }

            Slots[current].SetRemaining(b.RemainingMs, dur);
            Slots[current].SetCooldown(onCooldown);
            Slots[current].SetExpiring(expiring);
            // 같은 슬롯을 다른 사람이 이어받으면(_ownerBuffs 가 base 코드로 키잉되므로) 레벨도 시전자도 바뀐다.
            Slots[current].SetLevel(b.Level, showLevel);
            Slots[current].SetByOther(b.ByOther);
            int destination = Math.Min(target, Slots.Count - 1);
            if (current != destination)
            {
                Slots.Move(current, destination);
            }
        }

        RecomputePlaceholder();

        // 점멸 타이머는 실제로 깜빡일 슬롯이 있을 때만 돈다. 0 이면 마스크를 투명으로 되돌리므로, 마지막
        // 깜빡임이 켜진 채 굳어 아이콘이 영영 어두워지는 일도 여기서 함께 막힌다.
        int flashing = 0;
        foreach (BuffSlotVM slot in Slots)
        {
            if (slot.ExpiryVisibility == Visibility.Visible)
            {
                flashing++;
            }
        }

        BuffExpiryFlash.SetDemand(flashing);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>One buff slot: icon + a live remaining-time countdown + a border ring that shrinks with the
/// remaining time (a visual cooldown/duration helper).</summary>
public sealed class BuffSlotVM : INotifyPropertyChanged
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    // The ring is drawn on a fixed 46x46 canvas (matching the XAML slot) with absolute coordinates, so a
    // shrinking arc stays centered instead of drifting as its bounding box changes. Radius 21.5 frames the
    // 40px (radius 20) circular icon just outside its edge.
    private const double Canvas = 46;
    private const double Center = Canvas / 2; // 23
    private const double RingRadius = 21.5;

    public BuffSlotVM(
        int code, string name, long remainingMs, long durationMs, bool byOther, bool onCooldown,
        int level = 0, bool showLevel = true)
    {
        Code = code;
        Name = name;
        IconSource = JoinIcons.Skill(code);
        SetByOther(byOther);
        SetRemaining(remainingMs, durationMs);
        SetCooldown(onCooldown);
        SetLevel(level, showLevel);
    }

    public int Code { get; }
    public string Name { get; }
    public ImageSource? IconSource { get; }
    private bool _byOther;
    /// <summary>다른 사람이 걸어 준 버프인지(우상단 액센트 점). 슬롯은 base 코드로 키잉돼 시전자가 바뀌면
    /// 그대로 이어받으므로, 재사용 시 반드시 갱신해야 한다 — 안 그러면 레벨 배지는 새 시전자를, 점은 옛
    /// 시전자를 가리키는 자기모순 상태가 된다.</summary>
    public bool ByOther { get => _byOther; private set => Set(ref _byOther, value); }

    private double _iconOpacity = 1.0;
    /// <summary>Icon opacity — dimmed while the granting skill is on cooldown (the gray-out option).</summary>
    public double IconOpacity { get => _iconOpacity; private set => Set(ref _iconOpacity, value); }

    private Visibility _cooldownVeil = Visibility.Collapsed;
    /// <summary>A translucent gray veil over the icon while the skill is on cooldown.</summary>
    public Visibility CooldownVeil { get => _cooldownVeil; private set => Set(ref _cooldownVeil, value); }

    /// <summary>Set/clear the on-cooldown gray-out (icon dimmed + veiled).</summary>
    public void SetCooldown(bool onCooldown)
    {
        IconOpacity = onCooldown ? 0.4 : 1.0;
        CooldownVeil = onCooldown ? Visibility.Visible : Visibility.Collapsed;
    }

    private Visibility _expiryVisibility = Visibility.Collapsed;
    /// <summary>곧 끝나는 버프의 점멸 마스크. 칠하는 브러시는 <see cref="BuffExpiryFlash"/> 가 공유로 들고
    /// 두드리므로, 슬롯은 보일지 말지만 정한다.</summary>
    public Visibility ExpiryVisibility { get => _expiryVisibility; private set => Set(ref _expiryVisibility, value); }

    /// <summary>"버프 종료 3초 전 알림" 창에 들어왔는지.</summary>
    public void SetExpiring(bool expiring)
        => ExpiryVisibility = expiring ? Visibility.Visible : Visibility.Collapsed;

    private string _remainingText = string.Empty;
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }

    private string _levelText = string.Empty;
    /// <summary>아이콘 우하단 배지에 찍히는 스킬 레벨. 레벨을 모르거나(0) 옵션이 꺼져 있으면 빈 문자열이고,
    /// 그러면 <see cref="LevelVisibility"/>가 배지를 통째로 접는다.</summary>
    public string LevelText { get => _levelText; private set => Set(ref _levelText, value); }

    private Visibility _levelVisibility = Visibility.Collapsed;
    public Visibility LevelVisibility { get => _levelVisibility; private set => Set(ref _levelVisibility, value); }

    /// <summary>Update whether a party member (rather than the local player) applied this buff.</summary>
    public void SetByOther(bool byOther) => ByOther = byOther;

    /// <summary>
    /// Set the level badge.
    /// <para><b>0 과 1 은 둘 다 그리지 않는다.</b> 0 은 "모름"이고(소모품·주문서는 애초에 레벨이 없다),
    /// 1 은 레벨이 올라가지 않는 고정 효과 버프가 실어 보내는 값이다 — 광풍·표적 화살·바이젤의 권능·축복의
    /// 활처럼 실제로 레벨을 투자하는 스킬이 아닌 것들이 전부 1 로 온다. 그 줄에 "Lv.1"을 붙이면 레벨이 낮은
    /// 것처럼 읽혀 오히려 틀린 정보가 된다.</para>
    /// <para>표시에서만 뺀다 — 계산(<see cref="Data.PartySynergyCatalog"/>)과 통계 payload 에는 1 이 그대로
    /// 간다. 노련한 반격 1레벨 5.4% 처럼 1 이 진짜 의미를 갖는 자리가 있기 때문이다.</para>
    /// </summary>
    public void SetLevel(int level, bool show)
    {
        bool visible = show && level > 1;
        LevelText = visible ? level.ToString(Inv) : string.Empty;
        LevelVisibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private Geometry? _ring;
    /// <summary>The ring arc drawn around the icon; sweeps a shorter arc as the buff runs down, so the border
    /// visually "disappears" toward expiry. Null (no ring) when the duration is unknown.</summary>
    public Geometry? Ring { get => _ring; private set => Set(ref _ring, value); }

    public void SetRemaining(long remainingMs, long durationMs)
    {
        if (durationMs <= 0)
        {
            // Unknown / indefinite duration (a maintained stance like 폭주): show the icon only — no countdown
            // text, no ring — so it doesn't look like it is about to expire.
            RemainingText = string.Empty;
            Ring = null;
            return;
        }

        long s = Math.Max(0, remainingMs) / 1000;
        RemainingText = s >= 60 ? $"{s / 60}:{s % 60:D2}" : s.ToString(Inv) + "s";
        Ring = BuildRing(Math.Clamp((double)remainingMs / durationMs, 0, 1));
    }

    // A clockwise arc from 12 o'clock spanning 360°·progress, centered on the fixed canvas (so it frames the
    // circular icon). Progress 1 = full ring, →0 = no ring. Frozen for cheap software rendering.
    private static Geometry? BuildRing(double progress)
    {
        if (progress <= 0.001)
        {
            return null;
        }

        if (progress >= 0.999)
        {
            var full = new EllipseGeometry(new Point(Center, Center), RingRadius, RingRadius);
            full.Freeze();
            return full;
        }

        double sweep = 360.0 * progress;
        double a0 = -90 * Math.PI / 180.0;                 // start at 12 o'clock
        double a1 = (-90 + sweep) * Math.PI / 180.0;       // clockwise
        var start = new Point(Center + RingRadius * Math.Cos(a0), Center + RingRadius * Math.Sin(a0));
        var end = new Point(Center + RingRadius * Math.Cos(a1), Center + RingRadius * Math.Sin(a1));
        var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(end, new Size(RingRadius, RingRadius), 0, sweep > 180, SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
