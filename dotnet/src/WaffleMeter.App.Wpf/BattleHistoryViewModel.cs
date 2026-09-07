using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WaffleMeter.App.Core;
using WaffleMeter.Data;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// View model for the battle-history panel (port of React HistoryPanel + useHistory). Rows are built
/// from a saved-battle snapshot captured on the consumer thread; each row keeps its frozen DpsReport so
/// a click can replay it without re-reading the data layer. Newest first. UI-thread only.
/// </summary>
public sealed class BattleHistoryViewModel : INotifyPropertyChanged
{
    private readonly MeterColorTheme _theme;
    private readonly EncounterCatalog _encounters;
    private List<(int Index, DpsReport Report)> _battles = [];

    public BattleHistoryViewModel(MeterColorTheme theme, MeterSettings settings, EncounterCatalog? encounters = null)
    {
        _theme = theme;
        Settings = settings;
        _encounters = encounters ?? EncounterCatalog.Empty;
        theme.Changed += (_, _) => Rebuild();
    }

    /// <summary>Exposed so the panel can bind the user's overlay font.</summary>
    public MeterSettings Settings { get; }

    public ObservableCollection<BattleHistoryRowViewModel> Rows { get; } = new();

    private BattleHistory.Tab _tab = BattleHistory.Tab.Battle;

    /// <summary>보고 있는 탭. 허수아비 런은 진짜 전투와 성격이 달라(같은 대상, 고정 길이, 반복) 섞어 두면
    /// 둘 다 읽기 어렵다 — 그래서 목록 자체를 가른다. 기본은 전투.</summary>
    public BattleHistory.Tab SelectedTab
    {
        get => _tab;
        set
        {
            if (_tab == value)
            {
                return;
            }

            _tab = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTab)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBattleTab)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDummyTab)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EmptyText)));
            Rebuild();
        }
    }

    /// <summary>탭 알약의 IsChecked 바인딩. RadioButton 은 해제될 때도 false 를 밀어 넣으므로 참일 때만 반응한다.</summary>
    public bool IsBattleTab
    {
        get => _tab == BattleHistory.Tab.Battle;
        set
        {
            if (value)
            {
                SelectedTab = BattleHistory.Tab.Battle;
            }
        }
    }

    public bool IsDummyTab
    {
        get => _tab == BattleHistory.Tab.Dummy;
        set
        {
            if (value)
            {
                SelectedTab = BattleHistory.Tab.Dummy;
            }
        }
    }

    /// <summary>빈 목록 안내. 탭마다 다르게 말해야 "기록이 통째로 없다"로 오해하지 않는다.</summary>
    public string EmptyText => _tab == BattleHistory.Tab.Dummy
        ? "허수아비 측정 기록이 없습니다"
        : "전투 기록이 없습니다";

    /// <summary>Raised when a row is clicked (App shows that battle in the overlay).</summary>
    public event Action<DpsReport>? BattleSelected;

    public void SelectBattle(DpsReport report) => BattleSelected?.Invoke(report);

    /// <summary>Raised when a row's ▶ is clicked (App opens the positional replay for that battle).</summary>
    public event Action<DpsReport>? ReplayRequested;

    public void RequestReplay(DpsReport report) => ReplayRequested?.Invoke(report);

    /// <summary>Does this battle have a replay to play? Set by App (it owns the engine + the saved-recording
    /// folder). Null = the replay feature is off / unavailable, and no row shows a ▶.</summary>
    public Func<DpsReport, bool>? HasReplay { get; set; }

    private Visibility _emptyVisibility = Visibility.Visible;
    public Visibility EmptyVisibility { get => _emptyVisibility; private set => Set(ref _emptyVisibility, value); }

    // 리플레이 존재 여부는 행마다 파일 시스템을 한 번씩 두드린다(App 이 FindRecording 을 걸어 준다).
    // Rebuild 는 이제 탭을 누를 때마다 돌므로, 같은 스냅샷 안에서는 그 답을 재사용한다.
    private readonly Dictionary<int, bool> _replayProbe = new();

    /// <summary>Replace the row list from a fresh snapshot (marshalled from the consumer thread).</summary>
    public void SetBattles(List<(int Index, DpsReport Report)> battles)
    {
        _battles = battles;
        _replayProbe.Clear();
        Rebuild();
    }

    private void Rebuild()
    {
        var reportByIndex = _battles.ToDictionary(b => b.Index, b => b.Report);
        Color from = ToColor(_theme.BossBarFrom);
        Color to = ToColor(_theme.BossBarTo);
        Brush durationBrush = FrozenSolid(ToColor(_theme.BossRightValue));

        Rows.Clear();
        foreach (BattleHistoryItem item in BattleHistory.Filter(BattleHistory.Build(_battles, _encounters), _tab))
        {
            if (!reportByIndex.TryGetValue(item.Index, out DpsReport? report))
            {
                continue;
            }

            if (!_replayProbe.TryGetValue(item.Index, out bool hasReplay))
            {
                hasReplay = HasReplay?.Invoke(report) == true;
                _replayProbe[item.Index] = hasReplay;
            }

            Rows.Add(new BattleHistoryRowViewModel(item, report, from, to, durationBrush, hasReplay));
        }

        EmptyVisibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Color ToColor(string value) =>
        ColorString.TryParse(value, out ColorRgba c) ? Color.FromArgb(c.A, c.R, c.G, c.B) : Colors.White;

    private static Brush FrozenSolid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
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

/// <summary>One saved-battle row. Carries the frozen report for replay on click.</summary>
public sealed class BattleHistoryRowViewModel
{
    public BattleHistoryRowViewModel(
        BattleHistoryItem item, DpsReport report, Color barFrom, Color barTo, Brush durationBrush, bool hasReplay)
    {
        Report = report;
        ReplayVisibility = hasReplay ? Visibility.Visible : Visibility.Collapsed;
        IsDummy = item.IsDummy;
        MobName = item.MobName;
        DateTimeText = FormatDateTime(item.BattleStartMs);
        DurationText = MeterFormat.FormatBattleTime(item.BattleTimeMs);
        IsBoss = item.IsBoss;
        BossIcon = JoinIcons.BossIcon;
        IconOpacity = item.IsBoss ? 1.0 : 0.4;
        DurationBrush = durationBrush;

        var gradient = new LinearGradientBrush(barFrom, barTo, 0.0) { Opacity = item.IsBoss ? 0.8 : 0.2 };
        gradient.Freeze();
        BackgroundBrush = gradient;
    }

    public DpsReport Report { get; }

    /// <summary>허수아비 측정 런. 지금은 탭이 이미 갈라 주므로 행 자체는 같은 모양으로 그린다.</summary>
    public bool IsDummy { get; }

    public string MobName { get; }
    public string DateTimeText { get; }
    public string DurationText { get; }
    public bool IsBoss { get; }
    public BitmapImage? BossIcon { get; }
    public double IconOpacity { get; }
    public Brush BackgroundBrush { get; }
    public Brush DurationBrush { get; }

    /// <summary>The ▶ shows only when this battle actually has a recording to open.</summary>
    public Visibility ReplayVisibility { get; }

    private static string FormatDateTime(long ms)
    {
        if (ms <= 0)
        {
            return string.Empty;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
