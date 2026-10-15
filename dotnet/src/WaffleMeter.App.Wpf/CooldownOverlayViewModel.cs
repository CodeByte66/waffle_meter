using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using WaffleMeter.App.Core;
using WaffleMeter.Data;

namespace WaffleMeter.App.Wpf;

/// <summary>View model for the skill-cooldown overlay: one square slot per skill of the recognised character's
/// job, with a dark pie that wipes away as the cooldown runs out. Slots are reconciled in place (by
/// shared-cooldown group) so icons never jump under the cursor.
/// <para>Visually deliberately unlike the buff overlay — square icons and a filling/emptying pie, against that
/// overlay's circular icons and outline ring — because the two windows sit side by side and mean different
/// things: one says "this is active on me", the other "this is ready to press".</para></summary>
public sealed class CooldownOverlayViewModel : INotifyPropertyChanged
{
    private static readonly Brush PanelBg = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x18, 0x21)));
    private static readonly Brush PanelBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x78, 0x84, 0x9B)));

    public ObservableCollection<CooldownSlotVM> Slots { get; } = new();

    private double _iconScale = 1.0;
    /// <summary>Uniform scale for the whole slot. The native design is the 42px icon on a 46px cell, driven off
    /// the same 40px = 100% convention the buff overlay uses so both sliders mean the same thing.</summary>
    public double IconScale { get => _iconScale; private set => Set(ref _iconScale, value); }

    /// <summary>Set the icon size in px. The clamp must match <c>MeterSettings.CooldownUiIconSize</c> — if the
    /// two disagree the saved value and the screen drift apart with nothing to show for it.</summary>
    public void SetIconSize(int px) => IconScale = Math.Clamp(px, 32, 80) / 40.0;

    private Brush _textBrush = Brushes.White;
    /// <summary>Countdown-text color (from the setting).</summary>
    public Brush TextBrush { get => _textBrush; private set => Set(ref _textBrush, value); }

    private string _textColorHex = string.Empty;
    /// <summary>Set the countdown-text color from a hex or <c>rgba()</c> string; falls back to white on an empty
    /// or bad value.</summary>
    public void SetTextColor(string hex)
    {
        if (_textColorHex == hex)
        {
            return;
        }

        _textColorHex = hex;
        // 버프 오버레이와 같은 결함·같은 해법: 피커의 rgba(...) 를 ColorConverter 가 못 읽어 흰색으로 떨어졌다.
        if (ColorString.TryParse(hex, out ColorRgba c))
        {
            var b = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            b.Freeze();
            TextBrush = b;
        }
        else
        {
            TextBrush = Brushes.White;
        }
    }

    private bool _showBackground;
    /// <summary>When true, draw a panel background + border + placeholder so the (possibly empty) window can be
    /// found and dragged before any skill has been used.</summary>
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
    /// <summary>Placeholder shown only with the background on and no slots — i.e. before the character has been
    /// recognised, or with every skill unticked in the picker. Once the job is known the grid fills on its own,
    /// so this is a short-lived state rather than the normal one.</summary>
    public Visibility EmptyVisibility { get => _emptyVisibility; private set => Set(ref _emptyVisibility, value); }

    private void RecomputePlaceholder() => EmptyVisibility = _showBackground && Slots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>Replace the slot list from a fresh snapshot, reusing rows by group id so a normal tick only
    /// changes the pie and the countdown text. Rows are moved only when their position actually differs —
    /// unconditional moves make the icons shiver.</summary>
    public void Update(IReadOnlyList<SkillCooldownView> rows)
    {
        for (int i = Slots.Count - 1; i >= 0; i--)
        {
            if (!rows.Any(r => r.GroupId == Slots[i].GroupId))
            {
                Slots.RemoveAt(i);
            }
        }

        for (int target = 0; target < rows.Count; target++)
        {
            SkillCooldownView r = rows[target];

            int current = -1;
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].GroupId == r.GroupId)
                {
                    current = i;
                    break;
                }
            }

            if (current < 0)
            {
                Slots.Insert(Math.Min(target, Slots.Count), new CooldownSlotVM(r));
                continue;
            }

            Slots[current].Update(r);
            int destination = Math.Min(target, Slots.Count - 1);
            if (current != destination)
            {
                Slots.Move(current, destination);
            }
        }

        RecomputePlaceholder();
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

/// <summary>One cooldown slot: a square skill icon, a dark pie covering the part of the cooldown still to
/// run, and the remaining seconds over it.</summary>
public sealed class CooldownSlotVM : INotifyPropertyChanged
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // The pie is drawn on the icon's own 42x42 box in absolute coordinates. Its radius reaches past the
    // corners (half-diagonal is 29.7) so a square icon is covered corner to corner; the XAML clips it back to
    // the rounded square. A radius that only reached the edge midpoint would leave four bright corners.
    private const double Box = 42;
    private const double Center = Box / 2;
    private const double PieRadius = 31;

    // 파이 두 가지. 진한 쪽 = 못 누름(쿨 / 충전 0회), 옅은 쪽 = 누를 수 있지만 다음 1회가 차오르는 중(충전형).
    // 옅은 쪽을 진한 쪽과 헷갈리지 않을 만큼 떨어뜨리되, 밝은 아이콘 위에서도 진행이 보일 만큼은 남긴다.
    private static readonly Brush CooldownFill = Freeze(new SolidColorBrush(Color.FromArgb(0xB2, 0, 0, 0)));
    private static readonly Brush RechargeFill = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public CooldownSlotVM(SkillCooldownView row)
    {
        GroupId = row.GroupId;
        Name = row.Name;
        IconSource = JoinIcons.Skill(row.DisplayCode);
        Update(row);
    }

    public int GroupId { get; }

    private string _name = string.Empty;
    /// <summary>Tooltip text. Kept updatable because a shared-cooldown group can report under either of its
    /// skills' codes.</summary>
    public string Name { get => _name; private set => Set(ref _name, value); }

    public ImageSource? IconSource { get; }

    private string _remainingText = string.Empty;
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }

    private Visibility _textVisibility = Visibility.Collapsed;
    /// <summary>The countdown is drawn only while the skill is actually cooling — a "0" on every ready slot is
    /// noise on a window that is mostly ready slots.</summary>
    public Visibility TextVisibility { get => _textVisibility; private set => Set(ref _textVisibility, value); }

    private Geometry? _wipe;
    /// <summary>The dark pie over the icon: a clockwise wedge from 12 o'clock spanning the fraction of the
    /// cooldown still to run, so it sweeps away to nothing as the skill becomes usable. Null when ready.
    /// 충전형이 1회 이상 남은 채 다음 1회를 채우는 중이면 같은 쐐기를 <see cref="WipeFill"/> 의 옅은 색으로 그린다.</summary>
    public Geometry? Wipe { get => _wipe; private set => Set(ref _wipe, value); }

    private Brush _wipeFill = CooldownFill;
    /// <summary>파이 색. 진함 = 못 누름, 옅음 = 누를 수 있고 재충전 중. 얼린 브러시 둘 중 하나라 바인딩 비용만 든다.</summary>
    public Brush WipeFill { get => _wipeFill; private set => Set(ref _wipeFill, value); }

    private string _chargeText = string.Empty;
    /// <summary>충전형 스킬의 남은 횟수(우하단 숫자).</summary>
    public string ChargeText { get => _chargeText; private set => Set(ref _chargeText, value); }

    private Visibility _chargeVisibility = Visibility.Collapsed;
    /// <summary>와이어가 충전형이라고 말한 스킬에만 보인다 — 가득 찬 상태(예: 2)도 보여 준다. 숫자가 늘 같은
    /// 자리에 있어야 "이 칸은 충전형"으로 읽힌다.</summary>
    public Visibility ChargeVisibility { get => _chargeVisibility; private set => Set(ref _chargeVisibility, value); }

    private double _readyOpacity = 1.0;
    /// <summary>Slight lift for a ready skill so a full row of them still reads as "all up" at a glance without
    /// spending an alpha-composited surface per icon.</summary>
    public double ReadyOpacity { get => _readyOpacity; private set => Set(ref _readyOpacity, value); }

    public void Update(SkillCooldownView row)
    {
        Name = row.Name;

        bool charge = row.Charges >= 0;
        ChargeText = charge ? row.Charges.ToString(Inv) : string.Empty;
        ChargeVisibility = charge ? Visibility.Visible : Visibility.Collapsed;

        if (row.IsReady || row.RemainingMs <= 0)
        {
            RemainingText = string.Empty;
            TextVisibility = Visibility.Collapsed;
            ReadyOpacity = 1.0;

            // 충전형이 1회 이상 남았는데 다음 1회가 차오르는 중: 옅은 파이로 진행만 보인다. 가운데 숫자는 띄우지
            // 않는다 — 이 창에서 가운데 숫자는 "지금 못 누른다"는 표식이고, 이 칸은 누를 수 있다.
            if (charge && row.RemainingMs > 0 && row.TotalMs > 0)
            {
                WipeFill = RechargeFill;
                Wipe = BuildWipe(Math.Clamp((double)row.RemainingMs / row.TotalMs, 0, 1));
            }
            else
            {
                Wipe = null;
            }

            return;
        }

        long s = (row.RemainingMs + 999) / 1000; // round up: "1s" must not read as ready
        RemainingText = s >= 60 ? $"{s / 60}:{s % 60:D2}" : s.ToString(Inv);
        TextVisibility = Visibility.Visible;
        ReadyOpacity = 0.85;
        WipeFill = CooldownFill;

        // The denominator can be unknown (a correction arrived before any cast of this skill was seen). Draw a
        // full wipe then — "on cooldown, share unknown" — rather than guessing a total and animating a lie.
        double progress = row.TotalMs > 0 ? Math.Clamp((double)row.RemainingMs / row.TotalMs, 0, 1) : 1;
        Wipe = BuildWipe(progress);
    }

    // A clockwise pie wedge from 12 o'clock spanning 360°·progress. Progress 1 = the whole icon covered,
    // →0 = nothing left. Frozen: the panel re-renders in software, so an unfrozen geometry per slot per tick is
    // the one cost worth removing.
    private static Geometry? BuildWipe(double progress)
    {
        if (progress <= 0.002)
        {
            return null;
        }

        if (progress >= 0.998)
        {
            var full = new EllipseGeometry(new Point(Center, Center), PieRadius, PieRadius);
            full.Freeze();
            return full;
        }

        double sweep = 360.0 * progress;
        double a0 = -90 * Math.PI / 180.0;
        double a1 = (-90 + sweep) * Math.PI / 180.0;
        var start = new Point(Center + PieRadius * Math.Cos(a0), Center + PieRadius * Math.Sin(a0));
        var end = new Point(Center + PieRadius * Math.Cos(a1), Center + PieRadius * Math.Sin(a1));

        var fig = new PathFigure { StartPoint = new Point(Center, Center), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(start, false));
        fig.Segments.Add(new ArcSegment(end, new Size(PieRadius, PieRadius), 0, sweep > 180, SweepDirection.Clockwise, false));
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
