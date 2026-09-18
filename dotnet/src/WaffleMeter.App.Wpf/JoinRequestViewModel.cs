using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WaffleMeter.App.Core;
using WaffleMeter.Data;

namespace WaffleMeter.App.Wpf;

/// <summary>One skill badge on a join card: icon + "name LvN" + job-tinted pill colors.</summary>
public sealed record JoinSkillBadge(System.Windows.Media.ImageSource? Icon, string Label, Brush Background, Brush BorderBrush);

/// <summary>
/// View model for the party join-request panel (port of React JoinRequestPanel). <see cref="Reconcile"/>
/// (on store Changed) rebuilds the row list newest-first; <see cref="Tick"/> (a 250ms UI timer) drives
/// each row's 20s countdown bar and evicts a row when it reaches 0 (web per-row setTimeout parity).
/// All methods run on the UI thread.
/// </summary>
public sealed class JoinRequestViewModel : INotifyPropertyChanged
{
    private readonly TierService? _tier;

    /// <param name="tier">Career-tier source for applicants. Optional so the UI preview and unit tests can
    /// build a panel without a network stack; when absent the cards simply carry no tier.</param>
    public JoinRequestViewModel(
        MeterSettings settings,
        IEnumerable<int>? visibleCodes = null,
        TierService? tier = null)
    {
        Settings = settings;
        _visibleCodes = visibleCodes != null ? new HashSet<int>(visibleCodes) : new HashSet<int>(SkillCatalog.DefaultVisibleCodes);
        _tier = tier;
    }

    /// <summary>
    /// Ask the tier service about everyone currently on screen, then paint whatever it already knows.
    /// <para>Requesting is idempotent and rate-limited inside the service (batched, 12 at a time, at most one
    /// request a minute), so calling it on every roster change and every tick costs nothing extra. Reading is a
    /// pure cache hit, which is why the panel can just re-read on its existing heartbeat instead of needing the
    /// worker to marshal results back onto the UI thread.</para>
    /// </summary>
    private void RefreshTiers()
    {
        if (_tier == null || Rows.Count == 0 || !Settings.TierShow || Settings.TierEffects == "off")
        {
            return;
        }

        _tier.RequestCareerTiers(Rows.Select(r => r.IdentityHash));

        foreach (JoinRequestRowViewModel row in Rows)
        {
            row.ApplyTier(_tier.CareerTier(row.IdentityHash));
        }
    }

    /// <summary>Exposed so the panel can bind the user's overlay font.</summary>
    public MeterSettings Settings { get; }

    private HashSet<int> _visibleCodes;

    /// <summary>Replace the visible-skill set (skill-settings flyout). Clears rows so the next reconcile
    /// rebuilds each card's badges against the new set.</summary>
    public void SetVisibleCodes(IEnumerable<int> codes)
    {
        _visibleCodes = new HashSet<int>(codes);
        Rows.Clear();
    }

    public ObservableCollection<JoinRequestRowViewModel> Rows { get; } = new();

    /// <summary>Raised when the list goes from empty to non-empty (web auto-open on 0→≥1).</summary>
    public event Action? RequestPresent;

    private int _lastCount;

    private string _countText = "0건";
    public string CountText { get => _countText; private set => Set(ref _countText, value); }

    private Visibility _emptyVisibility = Visibility.Visible;
    public Visibility EmptyVisibility { get => _emptyVisibility; private set => Set(ref _emptyVisibility, value); }

    /// <summary>Reconcile the rows against a newest-first snapshot, preserving existing row objects so
    /// in-flight countdown bars stay smooth.</summary>
    public void Reconcile(IReadOnlyList<JoinRequestUser> snapshot)
    {
        // Drop rows no longer present.
        var present = new HashSet<int>(snapshot.Select(s => s.Requester));
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!present.Contains(Rows[i].Id))
            {
                Rows.RemoveAt(i);
            }
        }

        // Insert/reorder to match the snapshot order (newest first).
        for (int i = 0; i < snapshot.Count; i++)
        {
            JoinRequestUser u = snapshot[i];
            int existing = IndexOfId(u.Requester);
            if (existing < 0)
            {
                Rows.Insert(i, new JoinRequestRowViewModel(u, _visibleCodes));
                continue;
            }

            // The official-API enrichment re-Adds the SAME requester with Skill/Power/Job filled in (the
            // initial join packet carries no skills). Rebuild the row in place when its source changed so the
            // badges/power refresh — without this they stay EMPTY until something else forces a rebuild (the
            // reported "no badges until I toggle a skill" bug). ArrivedAt is carried across the re-Add, so the
            // rebuilt row's countdown stays continuous; an unchanged source just reorders (row object kept).
            if (Rows[existing].SourceChanged(u))
            {
                Rows[existing] = new JoinRequestRowViewModel(u, _visibleCodes);
            }

            if (existing != i)
            {
                Rows.Move(existing, i);
            }
        }

        RefreshTiers();
        UpdateCount();
        if (_lastCount == 0 && Rows.Count > 0)
        {
            RequestPresent?.Invoke();
        }

        _lastCount = Rows.Count;
        Tick(); // paint timers for any newly inserted rows immediately
    }

    /// <summary>Clear all rows (instance start / party exit).</summary>
    public void Clear()
    {
        Rows.Clear();
        _lastCount = 0;
        UpdateCount();
    }

    /// <summary>250ms heartbeat: refresh each row's bar; evict rows past 20s.</summary>
    public void Tick()
    {
        if (Rows.Count == 0)
        {
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long latest = Rows.Max(r => r.ArrivedAt);
        long effectiveNow = Math.Max(now, latest); // web effectiveNow guard (no negative bars)

        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (Rows[i].UpdateTimer(effectiveNow) <= 0)
            {
                Rows.RemoveAt(i);
            }
        }

        RefreshTiers(); // a lookup that landed since the last beat paints here

        if (Rows.Count != _lastCount)
        {
            _lastCount = Rows.Count;
            UpdateCount();
        }
    }

    private void UpdateCount()
    {
        CountText = $"{Rows.Count}건";
        EmptyVisibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private int IndexOfId(int id)
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].Id == id)
            {
                return i;
            }
        }

        return -1;
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

/// <summary>
/// One join-request card. Static fields (name/power/icon/job colors) are set from the packet; the
/// countdown fields (<see cref="BarRatio"/>, <see cref="FillBrush"/>, <see cref="RemainingText"/>) are
/// recomputed on each <see cref="UpdateTimer"/> tick.
/// </summary>
public sealed class JoinRequestRowViewModel : INotifyPropertyChanged
{
    private const double TotalSec = 20.0;

    // Timer-bar fills by remaining fraction (web gradients), frozen + shared.
    private static readonly Brush TealFill = Gradient("#FF2DD4BF", "#FF0F766E");
    private static readonly Brush AmberFill = Gradient("#FFF59E0B", "#FFB45309");
    private static readonly Brush RoseFill = Gradient("#FFFB7185", "#FFBE123C");

    private readonly JoinRequestUser _source;

    public JoinRequestRowViewModel(JoinRequestUser u, ISet<int> visibleCodes)
    {
        _source = u;
        Id = u.Requester;
        ArrivedAt = u.ArrivedAt;

        string label = ServerNames.GetServerLabel(u.Server);
        Nickname = label.Length > 0 ? $"{u.Nickname}[{label}]" : u.Nickname;
        // The anonymous key the stats web joins on. Null for a garbled/serverless request — those simply
        // never get a tier rather than being asked about with a bogus hash.
        IdentityHash = WaffleMeter.Stats.StatsIdentity.CharacterIdentityHash(u.Server, u.Nickname);

        PowerText = u.Power > 0 ? MeterFormat.FormatPower(u.Power) : "-";
        JobIcon = JoinIcons.Job(u.Job);

        JobColors colors = JoinPanelPalette.For(u.Job);
        CardBrush = colors.Card;
        BorderBrush = colors.Border;
        AccentBrush = colors.Accent;

        BuildBadges(u.Skill, visibleCodes, colors,
            out List<JoinSkillBadge> normal, out List<JoinSkillBadge> stigma, out List<JoinSkillBadge> passive);
        NormalBadges = normal;
        StigmaBadges = stigma;
        PassiveBadges = passive;
        NormalBadgesVisibility = normal.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StigmaBadgesVisibility = stigma.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PassiveBadgesVisibility = passive.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Anonymous stats identity for this applicant, or null when nickname/server are unusable.</summary>
    public string? IdentityHash { get; }

    private TierBadge _tier = TierBadge.None;

    /// <summary>The applicant's career tier colours, or <see cref="TierBadge.None"/> while unknown.</summary>
    public TierBadge Tier { get => _tier; private set => Set(ref _tier, value); }

    private string _tierText = string.Empty;
    public string TierText { get => _tierText; private set => Set(ref _tierText, value); }

    private Visibility _tierVisibility = Visibility.Collapsed;

    /// <summary>Collapsed until a tier is known — an applicant who has not consented, or has too few battles,
    /// shows no chip at all rather than an empty or "unranked" one.</summary>
    public Visibility TierVisibility { get => _tierVisibility; private set => Set(ref _tierVisibility, value); }

    private string _tierToolTip = string.Empty;
    public string TierToolTip { get => _tierToolTip; private set => Set(ref _tierToolTip, value); }

    /// <summary>Paint (or clear) this applicant's tier. Idempotent — the panel calls it on every heartbeat.</summary>
    /// <summary>The dark-card palette is used in EVERY skin on purpose: the applicant card is a fixed dark
    /// fill, and the light palette's saturated-dark chip text would sink into it. The dark variants are already
    /// the shape we want here — a very faint tinted backing under bright text.</summary>
    public void ApplyTier((int Rank, string Name, double TopPercent)? tier)
    {
        if (tier is not (int rank, string name, double topPercent))
        {
            Tier = TierBadge.None;
            TierVisibility = Visibility.Collapsed;
            return;
        }

        Tier = TierPalette.For(rank, isLight: false);
        // 미터 푸터와 같은 문구("챌린저 · 상위 0.7%"). 등급만으로는 밴드가 넓어(플래티넘 하나가 10~30%) 신청자
        // 비교가 안 되고, 백분위만으로는 색이 무슨 등급인지 안 읽힌다 — 둘을 같이 보여준다.
        TierText = $"{name} · {TierLadder.FormatTopPercent(topPercent)}";
        TierToolTip = $"{TierText} — 와플미터에 공개된 캐릭터의 최근 성적";
        TierVisibility = Visibility.Visible;
    }

    /// <summary>True if the underlying request changed in a way that affects the card — the official-API
    /// enrichment fills Skill/Power/Job AFTER the initial empty-skill packet — so the row must be rebuilt.</summary>
    public bool SourceChanged(JoinRequestUser u) =>
        u.Power != _source.Power
        || u.Job != _source.Job
        || u.Server != _source.Server
        || !SameSkills(u.Skill, _source.Skill);

    private static bool SameSkills(IReadOnlyDictionary<int, int> a, IReadOnlyDictionary<int, int> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (KeyValuePair<int, int> kv in a)
        {
            if (!b.TryGetValue(kv.Key, out int v) || v != kv.Value)
            {
                return false;
            }
        }

        return true;
    }

    public int Id { get; }
    public long ArrivedAt { get; }
    public string Nickname { get; }
    public string PowerText { get; }
    public BitmapImage? JobIcon { get; }
    public Brush CardBrush { get; }
    public Brush BorderBrush { get; }
    public Brush AccentBrush { get; }
    public IReadOnlyList<JoinSkillBadge> NormalBadges { get; }
    public IReadOnlyList<JoinSkillBadge> StigmaBadges { get; }

    /// <summary>주요 패시브. 액티브/스티그마와 줄을 나눈다 — 모집자가 보려는 숫자가 이쪽이라, 배지 20개
    /// 사이에 섞어 놓으면 따로 낼 이유가 없어진다.</summary>
    public IReadOnlyList<JoinSkillBadge> PassiveBadges { get; }
    public Visibility NormalBadgesVisibility { get; }
    public Visibility StigmaBadgesVisibility { get; }
    public Visibility PassiveBadgesVisibility { get; }

    // From the requester's skills: normalize → keep only visible → dedupe (max lv) → sort by catalog
    // order → split 일반/스티그마/패시브 (port of JoinRequestPanel badgeMap + SkillBadges).
    private static void BuildBadges(IReadOnlyDictionary<int, int> skill, ISet<int> visibleCodes, JobColors colors,
        out List<JoinSkillBadge> normal, out List<JoinSkillBadge> stigma, out List<JoinSkillBadge> passive)
    {
        normal = new List<JoinSkillBadge>();
        stigma = new List<JoinSkillBadge>();
        passive = new List<JoinSkillBadge>();
        if (skill.Count == 0)
        {
            return;
        }

        var merged = new Dictionary<int, (string Name, int Lv, bool Stigma, bool Passive)>();
        foreach ((int rawCode, int lv) in skill)
        {
            int code = SkillCatalog.Normalize(rawCode);
            if (!visibleCodes.Contains(code))
            {
                continue;
            }

            SkillMeta? meta = SkillCatalog.Get(code);
            string name = meta?.Name ?? rawCode.ToString();
            bool isStigma = meta?.IsStigma ?? false;
            bool isPassive = meta?.IsPassive ?? false;
            int level = merged.TryGetValue(code, out var prev) ? Math.Max(prev.Lv, lv) : lv;
            merged[code] = (name, level, isStigma, isPassive);
        }

        foreach ((int code, var b) in merged.OrderBy(kv => SkillCatalog.Order(kv.Key)))
        {
            var badge = new JoinSkillBadge(JoinIcons.Skill(code), $"{b.Name} Lv{b.Lv}", colors.BadgeBg, colors.Border);
            (b.Passive ? passive : b.Stigma ? stigma : normal).Add(badge);
        }
    }

    private double _barRatio = 1.0;
    public double BarRatio { get => _barRatio; private set => Set(ref _barRatio, value); }

    private double _barRest;
    public double BarRest { get => _barRest; private set => Set(ref _barRest, value); }

    private Brush _fillBrush = TealFill;
    public Brush FillBrush { get => _fillBrush; private set => Set(ref _fillBrush, value); }

    private string _remainingText = "20s";
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }

    /// <summary>Recompute the bar from the shared effective-now; returns the remaining seconds.</summary>
    public double UpdateTimer(long effectiveNowMs)
    {
        double remaining = Math.Max(0, TotalSec - (effectiveNowMs - ArrivedAt) / 1000.0);
        double pct = remaining / TotalSec;
        BarRatio = pct;
        BarRest = 1.0 - pct;
        FillBrush = pct > 0.5 ? TealFill : pct > 0.25 ? AmberFill : RoseFill;
        RemainingText = $"{Math.Ceiling(remaining):0}s";
        return remaining;
    }

    private static Brush Gradient(string from, string to)
    {
        var brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(from),
            (Color)ColorConverter.ConvertFromString(to),
            0.0); // left -> right
        brush.Freeze();
        return brush;
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
