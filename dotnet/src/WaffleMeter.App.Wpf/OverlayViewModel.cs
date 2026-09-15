using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Globalization;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using WaffleMeter.App.Core;
using WaffleMeter.Data;
using WaffleMeter.Stats;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// Overlay view model. <see cref="Update"/> runs on the dispatcher and reconciles the ranked row
/// list from the live DPS report, mirroring the React MeterList/MeterRow: sort by metric desc, top 10,
/// always append self, per-row progress ratio + fill color by contribution tier, masked name + power
/// badge. Row clicks raise <see cref="SelectionToggled"/> (App opens/closes the detail window).
/// </summary>
public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private readonly MeterSettings _settings;
    private readonly MeterColorTheme _theme;
    private readonly Func<bool> _isLight;

    // Supplies the difficulty/stage suffix on the target name. The same boss NAME appears in every difficulty
    // of a dungeon, so "바크론" alone can't say whether this was 탐험 or 시련.
    private readonly EncounterCatalog _encounters;

    // 시련 바크론's level, when the parser has pinned it. Every trial level shares the same boss codes, so
    // the catalogue alone can only ever say "시련" — this is what turns that into "시련 16단계".
    /// <summary>이 리포트에 동결된 시련 라벨. 시련이 아니거나 아무것도 못 본 전투는 null 이다.</summary>
    private static string? FrozenTrialLabel(DpsReport report) =>
        report.TrialDifficulty.IsTrial ? report.TrialDifficulty.Label : null;

    // Rebuilt from the theme whenever a color changes (MeterColorTheme.Changed); rows are records that
    // bake in the brush references, so a theme change re-runs Update on the last report.
    private Brush _userBar = null!, _normalBar = null!, _warningBar = null!, _errorBar = null!;
    private Dictionary<JobClass, Brush> _jobBars = new(); // per-job bar brushes (직업 강조 mode), rebuilt on theme change
    private Brush _nameDefault = null!, _nameServerA = null!, _nameServerB = null!;
    private Brush _amountBrush = null!, _dpsBrush = null!, _percentBrush = null!;
    private DpsReport? _lastReport;

    // Tier state, keyed by entity uid. Supplied by TierService (career tier from the upload response, live
    // battle percentile computed locally against the downloaded distribution) and read during the row rebuild.
    // Empty by default so a meter with the feature off — or with no artifact yet — renders exactly as before.
    private IReadOnlyDictionary<int, RowTier> _rowTiers = EmptyTiers;
    private static readonly Dictionary<int, RowTier> EmptyTiers = [];
    private static readonly Dictionary<int, DpsMetricResult> EmptyMetrics = [];

    // React isLightOverlay hardcoded stat colors — used when the active skin is "light" so values stay
    // readable on the light background (the user theme colors are tuned for dark).
    private static readonly Brush LightName = Frozen(Color.FromRgb(0x1E, 0x29, 0x3B));
    private static readonly Brush LightServerA = Frozen(Color.FromRgb(0x03, 0x69, 0xA1));
    private static readonly Brush LightServerB = Frozen(Color.FromRgb(0xA2, 0x1C, 0xAF));
    private static readonly Brush LightPower = Frozen(Color.FromRgb(0x8A, 0x5A, 0x00));
    private static readonly Brush LightDps = Frozen(Color.FromRgb(0x10, 0x20, 0x33));
    private static readonly Brush LightPercent = Frozen(Color.FromRgb(0x04, 0x78, 0x57));
    private static readonly Brush LightCombatTime = Frozen(Color.FromRgb(0x33, 0x41, 0x55));

    public OverlayViewModel(
        string version,
        MeterSettings settings,
        MeterColorTheme theme,
        Func<bool>? isLight = null,
        EncounterCatalog? encounters = null,
        bool preview = false)
    {
        _preview = preview;
        _settings = settings;
        Settings = settings;
        _theme = theme;
        Theme = theme;
        _isLight = isLight ?? (() => false);
        _encounters = encounters ?? EncounterCatalog.Empty;
        RebuildBrushes();
        // 핸들러를 필드에 붙잡아 둔다 — 설정창의 미리보기 VM 은 창을 열 때마다 새로 만들어지므로,
        // 떼어낼 수 없는 익명 구독이면 MeterColorTheme 이 죽은 VM 을 세션 내내 붙들고 리페인트한다.
        _themeChanged = (_, _) =>
        {
            RebuildBrushes();
            if (_lastReport is { } report)
            {
                Update(report); // repaint rows with the new colors
            }
        };
        theme.Changed += _themeChanged;
        _status = $"waffle_meter {version}";
    }

    /// <summary>
    /// 이 인스턴스가 설정창의 <b>미리보기</b>인가. 참이면 공용 애니메이션 시계에 수요를 보고하지 않는다.
    ///
    /// <para>🔑 <see cref="NameFxSheen.SetDemand"/>·<see cref="TierSheen.SetDemand"/> 는 카운트를 더하는 게
    /// 아니라 <b>대입</b>한다. 미리보기가 같이 보고하면 진짜 미터가 ~500ms 마다 밀어 넣는 수요를 서로
    /// 지워, 설정창을 열어 둔 동안 본체 연출이 간헐적으로 멈춘다. 설정창 쪽 시계는 별도 창구인
    /// <see cref="NameFxSheen.SetPreviewDemand"/> 가 이미 담당한다.</para>
    /// </summary>
    private readonly bool _preview;

    private readonly EventHandler _themeChanged;

    /// <summary>
    /// 이 뷰모델을 테마 이벤트에서 떼어낸다. 앱 수명 내내 하나인 본체 VM 은 부를 일이 없고, 설정창을 열 때마다
    /// 새로 생기는 미리보기 VM 이 쓴다 — 안 떼면 설정창을 열었다 닫을 때마다 죽은 VM 이 하나씩 쌓여
    /// 스킨을 한 번 바꿀 때마다 그 전부가 행을 다시 그린다.
    /// </summary>
    public void Detach() => _theme.Changed -= _themeChanged;

    /// <summary>
    /// Derives the per-row tier state FOR THE REPORT BEING RENDERED. Set once at startup.
    /// <para>🔑 The tier must be a function of the displayed report, not a value pushed in beside it. Pushing it
    /// separately meant a saved battle rendered whatever the live loop happened to leave behind: every boss of a
    /// run showed the same percentages (the last live evaluation, reused), and an older run showed none at all
    /// (its participants have different entity uids, so the stale map had no entry for them).</para>
    /// </summary>
    public Func<DpsReport, IReadOnlyDictionary<int, RowTier>>? TierResolver { get; set; }

    /// <summary>nDPS/rDPS for the report being shown. Injected the same way <see cref="TierResolver"/> is, so a
    /// live report and a history replay go through one function — a saved battle returns its frozen snapshot and
    /// a live one recomputes. Null (not wired) simply means the row metric selector has nothing to switch to and
    /// the rows stay on raw DPS.</summary>
    public Func<DpsReport, IReadOnlyDictionary<int, DpsMetricResult>>? MetricsResolver { get; set; }

    private NameFxRoster _nameFx = NameFxRoster.Empty;
    /// <summary>Grant lookups memoised on (server, nickname) — the hot loop must not hash per row per tick.
    /// Keyed on identity only, NOT on skin or mode, so switching either does not have to invalidate it.</summary>
    private readonly Dictionary<(int Server, string Nickname), NameFxEntry?> _nameFxGrantMemo = new();

    /// <summary>
    /// Install the supporter/ranker grant list. Replacing it clears the per-row memo — the memo keys on
    /// (server, nickname) rather than the identity hash so the hot loop never hashes, which means a new roster
    /// cannot be picked up any other way.
    /// </summary>
    public void SetNameFxRoster(NameFxRoster? roster)
    {
        _nameFx = roster ?? NameFxRoster.Empty;
        _nameFxGrantMemo.Clear();
    }

    /// <summary>The grant for a character, memoised on (server, nickname) so the hot loop never hashes.</summary>
    private NameFxEntry? ResolveNameFxGrant(int server, string? nickname)
    {
        if (server <= 0 || string.IsNullOrWhiteSpace(nickname))
        {
            return null; // placeholder rows (던전 강제 집계) have no identity to grant against
        }

        var key = (server, nickname);
        if (!_nameFxGrantMemo.TryGetValue(key, out NameFxEntry? cached))
        {
            cached = _nameFx.Find(StatsIdentity.CharacterIdentityHash(server, nickname));
            _nameFxGrantMemo[key] = cached;
        }

        // The memo caches the LOOKUP (which costs a SHA-256), not the verdict. Expiry is re-checked on every
        // read: a ranker lease is short, and a meter left open across it would otherwise keep the mark forever.
        if (cached is { ExpiresAtMs: > 0 } && cached.ExpiresAtMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            return null;
        }

        return cached;
    }

    private NameFxBadge ResolveNameFx(NameFxEntry? grant, bool isLight)
    {
        if (grant is null)
        {
            return NameFxBadge.None;
        }

        NameFxBadge badge = NameFxPalette.For(grant.EffectId, isLight);

        // "static" downgrades every effect to its family's still variant instead of switching them off, so the
        // mark survives for people who do not want moving pixels.
        return !badge.IsNone && _settings.NameFxMode == "static" && badge.Animated
            ? NameFxPalette.StillVariant(badge.Id, isLight)
            : badge;
    }

    /// <summary>Inject tier state directly. Only for the UI preview and unit tests — the app sets
    /// <see cref="TierResolver"/> instead so history and live can never disagree.</summary>
    public void SetTiers(IReadOnlyDictionary<int, RowTier>? byUid) => _rowTiers = byUid ?? EmptyTiers;

    /// <summary>Re-theme on a skin swap (light/dark stat colors) and repaint.</summary>
    public void RefreshSkin()
    {
        RebuildBrushes();
        if (_lastReport is { } report)
        {
            Update(report);
        }
    }

    /// <summary>Exposed for the overlay to bind opacity/font/etc. directly.</summary>
    public MeterSettings Settings { get; }

    /// <summary>Exposed so XAML can bind theme-driven chrome (e.g. combat-time color) directly.</summary>
    public MeterColorTheme Theme { get; }

    private Brush _combatTimeBrush = null!;
    public Brush CombatTimeBrush { get => _combatTimeBrush; private set => Set(ref _combatTimeBrush, value); }

    private Brush _bossBarBrush = null!;
    /// <summary>Target-info accent rail + HP fill gradient (React theme.bossBar).</summary>
    public Brush BossBarBrush { get => _bossBarBrush; private set => Set(ref _bossBarBrush, value); }

    // In-combat accent (React active dot/label #2dd4bf); standby reuses CombatTimeBrush.
    private static readonly Brush CombatActiveBrush = Frozen(Color.FromRgb(0x2D, 0xD4, 0xBF));

    private Brush _combatStatusBrush = CombatActiveBrush;
    public Brush CombatStatusBrush { get => _combatStatusBrush; private set => Set(ref _combatStatusBrush, value); }

    /// <summary>Boss icon for the target-info bar (bundled).</summary>
    public System.Windows.Media.ImageSource? BossIcon => JoinIcons.BossIcon;

    private void RebuildBrushes()
    {
        _userBar = ThemeGradient(_theme.UserBarFrom, _theme.UserBarTo);
        _normalBar = ThemeGradient(_theme.NormalBarFrom, _theme.NormalBarTo);
        _warningBar = ThemeGradient(_theme.WarningBarFrom, _theme.WarningBarTo);
        _errorBar = ThemeGradient(_theme.ErrorBarFrom, _theme.ErrorBarTo);
        _jobBars = new Dictionary<JobClass, Brush>();
        foreach (JobClass jc in Enum.GetValues<JobClass>())
        {
            _jobBars[jc] = ThemeSolid(_theme.JobBar(jc));
        }

        if (_isLight())
        {
            // Light skin: the user theme's stat colors (tuned for dark) are unreadable on the light bg,
            // so use React's isLightOverlay hardcodes.
            _nameDefault = LightName;
            _nameServerA = LightServerA;
            _nameServerB = LightServerB;
            _amountBrush = LightPower;
            _dpsBrush = LightDps;
            _percentBrush = LightPercent;
            CombatTimeBrush = LightCombatTime;
        }
        else
        {
            _nameDefault = ThemeSolid(_theme.ServerDefaultColor);
            _nameServerA = ThemeSolid(_theme.ServerAColor);
            _nameServerB = ThemeSolid(_theme.ServerBColor);
            _amountBrush = ThemeSolid(_theme.MeterStatAmount);  // power badge (React MeterRow.tsx:207)
            _dpsBrush = ThemeSolid(_theme.MeterStatDps);        // damage value (MeterRow.tsx:126)
            _percentBrush = ThemeSolid(_theme.MeterStatPercent); // percent (MeterRow.tsx:119/127)
            CombatTimeBrush = ThemeSolid(_theme.CombatTimeColor);
        }

        BossBarBrush = ThemeGradient(_theme.BossBarFrom, _theme.BossBarTo);
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public ObservableCollection<RowViewModel> Rows { get; } = new();

    /// <summary>Raised when a row is clicked (App toggles the detail window for that uid).</summary>
    public event Action<int>? SelectionToggled;

    public void ToggleSelection(int uid) => SelectionToggled?.Invoke(uid);

    private string _targetName = "-";
    public string TargetName { get => _targetName; private set => Set(ref _targetName, value); }

    private string _targetVariantText = string.Empty;

    /// <summary>난이도·단계 라벨만("어려움"). 보스칸이 칩으로 그린다.</summary>
    public string TargetVariantText { get => _targetVariantText; private set => Set(ref _targetVariantText, value); }

    private Visibility _targetVariantVisibility = Visibility.Collapsed;
    public Visibility TargetVariantVisibility { get => _targetVariantVisibility; private set => Set(ref _targetVariantVisibility, value); }

    private string _targetSubtitle = string.Empty;

    /// <summary>"던전 · 난이도" 한 줄. 무대 보스칸이 이름 아래에 쓴다.</summary>
    public string TargetSubtitle { get => _targetSubtitle; private set => Set(ref _targetSubtitle, value); }

    private Visibility _targetSubtitleVisibility = Visibility.Collapsed;
    public Visibility TargetSubtitleVisibility { get => _targetSubtitleVisibility; private set => Set(ref _targetSubtitleVisibility, value); }

    private string _targetHpAmountText = string.Empty;

    /// <summary>HP 수치만("1.28B / 2.71B") — 퍼센트 꼬리가 없다. 퍼센트를 따로 크게 그리는 레이아웃용.</summary>
    public string TargetHpAmountText { get => _targetHpAmountText; private set => Set(ref _targetHpAmountText, value); }

    private string _targetEtaText = string.Empty;

    /// <summary>처치까지 남은 시간("M:SS"). 남은 HP ÷ 현재 파티 DPS.</summary>
    public string TargetEtaText { get => _targetEtaText; private set => Set(ref _targetEtaText, value); }

    private Visibility _targetEtaVisibility = Visibility.Collapsed;
    public Visibility TargetEtaVisibility { get => _targetEtaVisibility; private set => Set(ref _targetEtaVisibility, value); }

    private string _targetHpPercentText = string.Empty;

    /// <summary>
    /// HP 퍼센트만("47.2%"). 계기판 레이아웃이 이 숫자를 29px 주인공으로 쓰기 때문에 사용자의
    /// <c>targetInfoDisplayMode</c> 와 무관해야 한다 — 그 설정이 hp_full_percent 면
    /// <see cref="TargetHpText"/> 에는 "1,234,567 / 2,345,678  52.3%" 가 들어가고, 그걸 29px 로
    /// 키우면 칸을 넘긴다. (percent 분기 자체는 FormatTargetHp 에 이미 있지만 설정에 종속된다.)
    /// </summary>
    public string TargetHpPercentText { get => _targetHpPercentText; private set => Set(ref _targetHpPercentText, value); }

    private string _targetHpText = string.Empty;
    public string TargetHpText { get => _targetHpText; private set => Set(ref _targetHpText, value); }

    private string _duration = "0.0s";
    public string Duration { get => _duration; private set => Set(ref _duration, value); }

    private string _status;
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _recognizedStatus = string.Empty;
    /// <summary>"· 콘팡 인식됨" shown beside the capture status once the own character is detected.</summary>
    public string RecognizedStatus { get => _recognizedStatus; private set => Set(ref _recognizedStatus, value); }

    private Visibility _recognizedVisibility = Visibility.Collapsed;
    public Visibility RecognizedVisibility { get => _recognizedVisibility; private set => Set(ref _recognizedVisibility, value); }

    private string _aetherText = string.Empty;
    /// <summary>The aether (오드) balance as "base(+bonus)" (bonus dropped when 0), shown beside the recognized
    /// character when enabled and a value exists.</summary>
    public string AetherText { get => _aetherText; private set => Set(ref _aetherText, value); }

    private Visibility _aetherVisibility = Visibility.Collapsed;
    public Visibility AetherVisibility { get => _aetherVisibility; private set => Set(ref _aetherVisibility, value); }

    private const string AetherToolTipBase =
        "오드 — 앞 숫자는 자연회복 오드, 괄호 안은 소모품 등으로 채운 추가 오드";
    private const string AetherToolTipClick = "\n클릭: 컨텐츠 관리 열기";

    private string _aetherToolTip = AetherToolTipBase + AetherToolTipClick;
    /// <summary>Badge tooltip. Says so when the number is CARRIED FORWARD rather than measured — the game only
    /// broadcasts the balance on its own schedule, so between sessions the meter projects the 자연회복 that
    /// accrued while it was closed. Showing an estimate is right; showing it as if it were a reading is not.</summary>
    public string AetherToolTip { get => _aetherToolTip; private set => Set(ref _aetherToolTip, value); }

    private string _shugoKeyText = string.Empty;
    /// <summary>Shugo-festa key balance as "base(+bonus)", shown in the footer's resource badges.</summary>
    public string ShugoKeyText { get => _shugoKeyText; private set => Set(ref _shugoKeyText, value); }

    private Visibility _shugoKeyVisibility = Visibility.Collapsed;
    public Visibility ShugoKeyVisibility { get => _shugoKeyVisibility; private set => Set(ref _shugoKeyVisibility, value); }

    /// <summary>Push the latest shugo-festa key balance. Hidden when the setting is off or no value exists.</summary>
    public void SetShugoKey(int baseVal, int bonus, bool hasValue)
    {
        if (!_settings.ShowAetherStatus || !hasValue)
        {
            ShugoKeyVisibility = Visibility.Collapsed;
            return;
        }

        ShugoKeyText = bonus > 0
            ? $"{baseVal:N0}(+{bonus:N0})"
            : baseVal.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        ShugoKeyVisibility = Visibility.Visible;
    }

    private string _pingText = string.Empty;
    /// <summary>Server latency badge ("NNms", or with a "(local)" suffix on a VPN/booster hop).</summary>
    public string PingText { get => _pingText; private set => Set(ref _pingText, value); }

    private Visibility _pingVisibility = Visibility.Collapsed;
    public Visibility PingVisibility { get => _pingVisibility; private set => Set(ref _pingVisibility, value); }

    /// <summary>Push the latest server latency (read each report tick). Hidden when the setting is off or no
    /// fresh sample exists.</summary>
    public void SetPing((double Ms, bool IsLocalHop)? ping)
    {
        if (!_settings.ShowLatencyIndicator || ping is not { } p)
        {
            PingVisibility = Visibility.Collapsed;
            return;
        }

        int ms = Math.Clamp((int)Math.Round(p.Ms), 0, 999);
        PingText = p.IsLocalHop ? $"{ms}ms (local)" : $"{ms}ms";
        PingVisibility = Visibility.Visible;
    }

    /// <summary>Push the latest aether balance (read from the data layer each report tick). Hidden when the
    /// setting is off or no value has been seen.</summary>
    /// <param name="estimated">True when the value was carried forward from a remembered reading rather than
    /// read off a live broadcast. Only the tooltip changes — the number itself is the best answer available, and
    /// dimming or annotating it would make the common case (a correct, if projected, balance) look broken.</param>
    public void SetAether(int baseVal, int bonus, bool hasValue, bool estimated = false)
    {
        if (!_settings.ShowAetherStatus || !hasValue)
        {
            AetherVisibility = Visibility.Collapsed;
            return;
        }

        AetherText = bonus > 0
            ? $"{baseVal:N0}(+{bonus:N0})"
            : baseVal.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        AetherToolTip = estimated
            ? AetherToolTipBase
                + "\n\n※ 마지막으로 확인한 값에 자연회복(3시간마다 +15)을 더한 추정치입니다."
                + "\n   게임에서 오드가 갱신되면 실제 값으로 바뀝니다."
                + AetherToolTipClick
            : AetherToolTipBase + AetherToolTipClick;
        AetherVisibility = Visibility.Visible;
    }

    // The recognized 본인 (executor) uid, mirrored LIVE from StatsBuilder.OwnCharacter().Id. The fallback
    // self signal for row coloring, used only when the report being shown carries no frozen executor id
    // (i.e. a live/in-progress report — see DpsReport.ExecutorId). A saved/history report self-identifies
    // its own player via report.ExecutorId, so it no longer depends on this transient live value (which the
    // history-replay path never refreshes). 0 = not recognized.
    private int _selfId;

    // The recognized executor's known identity (nickname/server/job/power), forwarded to OverlayRowBuilder for
    // lost-executor recovery: if 본인 re-instances and the new id's own-load packet never arrives, this names
    // and self-colors the bare row that would otherwise be hidden by the blank-row filter.
    private string? _selfNickname;
    private int _selfServer;
    private JobClass? _selfJob;
    private int _selfPower;

    private IReadOnlyList<User> _roster = [];

    /// <summary>App supplies the pre-combat party roster (recently-seen known players) each tick. It is
    /// merged as idle (0-DPS) rows in <see cref="Update"/> ONLY while there is no live combat data and the
    /// report is live (not a saved/history replay), so the combat row set, sort, and self-index are never
    /// touched once damage starts. Persisted in a field so it survives theme repaints (which re-run Update
    /// against the last report).</summary>
    public void SetRoster(IReadOnlyList<User> roster) => _roster = roster;

    // Feature 1: 라이브 idle 경로만 true — 파티(닉/서버)가 바뀌면 직전 전투 위로 로스터 프리뷰를 다시 띄운다.
    // 기록 재생 경로는 false로 둬(재생 전투가 라이브 로스터와 다르다고 비우면 안 됨). Update 시그니처를 안 늘려
    // theme 리페인트(마지막 리포트 재사용)에서도 마지막 라이브/기록 결정을 그대로 읽게 한다.
    private bool _allowRosterResurface;
    public void SetRosterResurface(bool enabled) => _allowRosterResurface = enabled;

    private IReadOnlyList<User> _authoritativeParty = [];
    private IReadOnlyList<(string Nickname, int Server, int Slot)> _authoritativePartyIdentities = [];
    private IReadOnlyList<(int Uid, string Nickname, int Server)> _memberProfiles = [];

    /// <summary>App supplies the AUTHORITATIVE (0x9702-only) party each tick — distinct from <see cref="_roster"/>,
    /// which also folds in recent boss-combat contributors (at a field boss those are the zerg). Used solely as
    /// the party-context guard for lost-executor recovery in <see cref="OverlayRowBuilder"/>: a NAMED damager not
    /// in this party marks a public zerg, where a strong stranger must not be relabeled 본인.
    /// <para><paramref name="identities"/> is the SAME snapshot before uid resolution (members never seen this
    /// session are dropped from <paramref name="party"/>). Set together in one call so the two can never drift
    /// apart between ticks.</para></summary>
    public void SetAuthoritativeParty(
        IReadOnlyList<User> party, IReadOnlyList<(string Nickname, int Server, int Slot)> identities,
        IReadOnlyList<(int Uid, string Nickname, int Server)> memberProfiles)
    {
        _authoritativeParty = party;
        _authoritativePartyIdentities = identities;
        _memberProfiles = memberProfiles;
    }

    /// <summary>App calls this each tick from StatsBuilder.OwnCharacter() so the indicator appears the
    /// moment the own character is recognized (and names it when known). <paramref name="selfId"/> is the
    /// recognized 본인 uid, used to keep the self row on the "내 캐릭터" color in 직업 강조 mode.</summary>
    public void SetRecognized(bool detected, string? nickname, int selfId = 0, int server = 0, JobClass? job = null, int power = 0)
    {
        _selfId = detected ? selfId : 0;
        _selfNickname = detected ? nickname : null;
        _selfServer = detected ? server : 0;
        _selfJob = detected ? job : null;
        _selfPower = detected ? power : 0;
        RecognizedVisibility = detected ? Visibility.Visible : Visibility.Collapsed;
        RecognizedStatus = !detected
            ? string.Empty
            : string.IsNullOrWhiteSpace(nickname) ? "· 캐릭터 인식됨" : $"· {nickname} 인식됨";
        RefreshIdleCard();
    }

    private string _selfNicknameText = string.Empty;

    /// <summary>대기화면(보스칸)에 띄우는 본인 닉네임.</summary>
    public string SelfNicknameText { get => _selfNicknameText; private set => Set(ref _selfNicknameText, value); }

    private string _selfMetaText = string.Empty;

    /// <summary>"권성 · 에페소" — 직업과 서버를 한 줄로.</summary>
    public string SelfMetaText { get => _selfMetaText; private set => Set(ref _selfMetaText, value); }

    private string _selfPowerText = string.Empty;

    /// <summary>본인 전투력("780.0k"). 값이 없으면 빈 문자열이라 칸이 조용히 비어 있다.</summary>
    public string SelfPowerText { get => _selfPowerText; private set => Set(ref _selfPowerText, value); }

    private Visibility _selfPowerVisibility = Visibility.Collapsed;
    public Visibility SelfPowerVisibility { get => _selfPowerVisibility; private set => Set(ref _selfPowerVisibility, value); }

    private ImageSource? _selfJobIcon;
    public ImageSource? SelfJobIcon { get => _selfJobIcon; private set => Set(ref _selfJobIcon, value); }

    private Brush _selfJobBrush = Brushes.Gray;
    public Brush SelfJobBrush { get => _selfJobBrush; private set => Set(ref _selfJobBrush, value); }

    private Visibility _idleCardVisibility = Visibility.Collapsed;

    /// <summary>
    /// 전투가 없을 때 보스칸 자리에 띄우는 본인 캐릭터 카드. 지금까지 대기 상태의 미터는 "전투 대기 중"
    /// 한 줄만 있는 빈 판이었다 — 그 자리를 인식된 캐릭터 정보로 채운다.
    /// </summary>
    public Visibility IdleCardVisibility { get => _idleCardVisibility; private set => Set(ref _idleCardVisibility, value); }

    private GridLength _bossRowHeight = GridLength.Auto;

    /// <summary>
    /// 보스칸이 차지할 행 높이. 전투 중에는 Auto(내용만큼)이고, <b>대기 중에는 Star</b> 라서 헤더와
    /// 푸터를 뺀 남은 공간을 대기 카드가 전부 쓴다 — 전투가 없으면 행 목록이 비어 그 공간이 놀기 때문이다.
    /// </summary>
    public GridLength BossRowHeight { get => _bossRowHeight; private set => Set(ref _bossRowHeight, value); }

    private GridLength _rowsRowHeight = new(1, GridUnitType.Star);

    /// <summary>
    /// 행 목록이 차지할 높이. 대기 중에는 Auto 다 — 행이 비어 있는데 Star 로 두면 보스 행(대기 카드)과
    /// 남은 공간을 **반씩 나눠 가져** 카드 아래에 빈 칸이 생긴다(실제로 그렇게 보였다).
    /// </summary>
    public GridLength RowsRowHeight { get => _rowsRowHeight; private set => Set(ref _rowsRowHeight, value); }

    private Visibility _bossSlotVisibility = Visibility.Collapsed;

    /// <summary>보스칸 자리가 무엇이든(타겟 정보 또는 대기 카드) 보여야 하는가.</summary>
    public Visibility BossSlotVisibility { get => _bossSlotVisibility; private set => Set(ref _bossSlotVisibility, value); }

    private void RefreshIdleCard()
    {
        bool idle = TargetInfoVisibility != Visibility.Visible;
        bool known = !string.IsNullOrWhiteSpace(_selfNickname);
        IdleCardVisibility = idle && known ? Visibility.Visible : Visibility.Collapsed;
        BossSlotVisibility = TargetInfoVisibility == Visibility.Visible || IdleCardVisibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        bool idleCard = IdleCardVisibility == Visibility.Visible;
        BossRowHeight = idleCard ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        RowsRowHeight = idleCard ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

        if (!known)
        {
            return;
        }

        string? job = _selfJob is { } j ? j.ClassName() : null;
        SelfNicknameText = _selfNickname ?? string.Empty;
        string server = _selfServer > 0 ? ServerNames.GetServerLabel(_selfServer) : string.Empty;
        // 카드가 뜨는 상황이 곧 '전투 대기 중'이라 상태를 여기 한 줄에 합친다 — 아래 플레이스홀더
        // 문구는 같은 말을 두 번 하게 되므로 카드가 있을 때 숨긴다.
        string who = string.IsNullOrEmpty(server) ? (job ?? string.Empty)
            : string.IsNullOrEmpty(job) ? server : $"{job} · {server}";
        SelfMetaText = string.IsNullOrEmpty(who) ? "전투 대기 중" : $"{who} · 전투 대기 중";
        SelfPowerText = _selfPower > 0 ? MeterFormat.FormatPower(_selfPower) : string.Empty;
        SelfPowerVisibility = _selfPower > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelfJobIcon = JoinIcons.Job(job);
        SelfJobBrush = JoinPanelPalette.For(job).Accent;
    }

    private Visibility _clickThroughVisibility = Visibility.Collapsed;
    /// <summary>Header lock badge: visible while click-through (input pass-through) is active, so the user
    /// can see the overlay is letting clicks fall through to the game (React Header LockKeyhole badge).</summary>
    public Visibility ClickThroughVisibility { get => _clickThroughVisibility; private set => Set(ref _clickThroughVisibility, value); }

    /// <summary>Driven by the overlay window whenever click-through toggles (hotkey, or taskbar-mode reset
    /// clearing it) so the header lock badge always reflects the real pass-through state.</summary>
    public void SetClickThroughIndicator(bool on) => ClickThroughVisibility = on ? Visibility.Visible : Visibility.Collapsed;

    private Visibility _updateReadyVisibility = Visibility.Collapsed;
    /// <summary>Header "update available" badge: shown once an update is downloaded and ready. Clicking it
    /// opens the restart toast on demand — there is no auto-popup, so the user updates when they choose.</summary>
    public Visibility UpdateReadyVisibility { get => _updateReadyVisibility; private set => Set(ref _updateReadyVisibility, value); }

    private string _updateTooltip = "업데이트";
    public string UpdateTooltip { get => _updateTooltip; private set => Set(ref _updateTooltip, value); }

    /// <summary>App calls this when an update has finished downloading; reveals the header update badge.</summary>
    public void SetUpdateReady(string version)
    {
        UpdateTooltip = $"업데이트 {version} — 클릭하면 적용(재시작)";
        UpdateReadyVisibility = Visibility.Visible;
    }

    private Visibility _placeholderVisibility = Visibility.Visible;
    public Visibility PlaceholderVisibility { get => _placeholderVisibility; private set => Set(ref _placeholderVisibility, value); }

    // ---- target-info bar (above the list) + combat-timer pill (below the list) ----
    private double _targetHpRatio;
    public double TargetHpRatio { get => _targetHpRatio; private set => Set(ref _targetHpRatio, value); }

    private double _targetHpRest = 1.0;
    public double TargetHpRest { get => _targetHpRest; private set => Set(ref _targetHpRest, value); }

    private Visibility _targetInfoVisibility = Visibility.Collapsed;
    public Visibility TargetInfoVisibility { get => _targetInfoVisibility; private set => Set(ref _targetInfoVisibility, value); }

    private MeterLayoutVisual _layout = MeterLayoutVisual.Default;

    /// <summary>
    /// 창 전체가 따르는 레이아웃 기하. 보스칸·행 목록·타이머가 전부 이걸 바인딩한다.
    /// 행은 자기 <c>L</c> 을 따로 들고 있는데(DataTemplate 안에서는 행이 DataContext 라 창 프로퍼티에
    /// 손이 안 닿는다), 둘은 매 <see cref="Update"/> 마다 같은 인스턴스로 맞춰진다.
    /// </summary>
    public MeterLayoutVisual Layout { get => _layout; private set => Set(ref _layout, value); }

    /// <summary>
    /// 설정의 레이아웃·행 높이를 즉시 기하로 반영한다.
    /// <para>⚠️ <see cref="Update"/> 안에서만 대입하면 <b>다음 리포트 틱까지 화면이 안 바뀌고</b>,
    /// 캡처 헬퍼가 안 붙은 상태에서는 리포트가 아예 안 오므로 영원히 안 바뀐다 — 레이아웃은 '고르는
    /// 즉시 생김새가 바뀌는' 설정이라 그 상태로는 "안 먹는다"는 제보가 먼저 온다. 그래서 설정 변경
    /// 알림에서도 직접 부른다(<c>RefreshSkin</c> 이 스킨 교체에서 하는 것과 같은 패턴).</para>
    /// </summary>
    private static readonly ConcurrentDictionary<Color, Brush> ShadedFills = new();

    /// <summary>
    /// 평면 단색 채움에 세로 음영을 입힌다. 무대처럼 카드 크롬이 없는 레이아웃에서는 27px 높이가
    /// 통째로 한 톤이라 게이지가 "칠해진 영역"으로 읽히는데, 위를 밝히고 아래를 어둡게 하면 같은
    /// 엘리먼트 수로 형태가 생긴다.
    /// <para>⚠️ 방향은 반드시 <b>세로</b>다. 가로로 주면 브러시가 RelativeToBoundingBox 라 한 타일이
    /// 곧 그 행의 기여도 폭이 되어, 짧은 바에는 그라디언트 머리만 남고 행마다 밝기가 달라진다.</para>
    /// <para>⚠️ 게이지 스킨이 붙은 행은 호출부에서 이미 걸러진다 — 팔레트가 자기 음영을 갖고 있다.</para>
    /// </summary>
    private static Brush ShadeFill(MeterLayoutVisual layout, Brush flat)
    {
        if (!layout.Spec.GaugeFillGradient || flat is not SolidColorBrush solid)
        {
            return flat;
        }

        return ShadedFills.GetOrAdd(solid.Color, static c =>
        {
            var g = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(0, 1),
            };
            g.GradientStops.Add(new GradientStop(Scale(c, 1.16), 0.0));
            g.GradientStops.Add(new GradientStop(c, 0.52));
            g.GradientStops.Add(new GradientStop(Scale(c, 0.86), 1.0));
            g.Freeze();
            return g;
        });
    }

    private static Color Scale(Color c, double k) => Color.FromArgb(
        c.A,
        (byte)Math.Clamp(c.R * k, 0, 255),
        (byte)Math.Clamp(c.G * k, 0, 255),
        (byte)Math.Clamp(c.B * k, 0, 255));

    public MeterLayoutVisual RefreshLayout()
    {
        MeterLayoutVisual visual = MeterLayoutVisual.For(_settings.MeterLayoutId, _settings.RowHeight);
        Layout = visual;
        return visual;
    }

    private Visibility _targetFailedVisibility = Visibility.Collapsed;
    public Visibility TargetFailedVisibility { get => _targetFailedVisibility; private set => Set(ref _targetFailedVisibility, value); }

    private Visibility _targetHpVisibility = Visibility.Collapsed;
    public Visibility TargetHpVisibility { get => _targetHpVisibility; private set => Set(ref _targetHpVisibility, value); }

    // Boss HP gauge form, driven by the same 게이지 형태 (BarStyle) setting as the meter rows: "fill" paints a
    // proportional cell fill behind the boss name, "bar" keeps the thin bottom HP bar, "none" hides both.
    private Visibility _targetBarFillVisibility = Visibility.Visible;
    /// <summary>Boss HP gauge as a proportional cell fill (게이지 형태 = "fill"), mirroring the meter rows.</summary>
    public Visibility TargetBarFillVisibility { get => _targetBarFillVisibility; private set => Set(ref _targetBarFillVisibility, value); }

    private Visibility _targetBottomBarVisibility = Visibility.Collapsed;
    /// <summary>Boss HP gauge as a thin bottom bar (게이지 형태 = "bar").</summary>
    public Visibility TargetBottomBarVisibility { get => _targetBottomBarVisibility; private set => Set(ref _targetBottomBarVisibility, value); }

    private Visibility _combatTimerVisibility = Visibility.Collapsed;
    public Visibility CombatTimerVisibility { get => _combatTimerVisibility; private set => Set(ref _combatTimerVisibility, value); }

    private string _combatStatusText = "대기 중";
    public string CombatStatusText { get => _combatStatusText; private set => Set(ref _combatStatusText, value); }

    /// <summary>The report the meter is CURRENTLY displaying — the live report, or a saved battle while
    /// replaying from history. The clickable rows are built from this exact report (<see cref="Update"/>
    /// keys rows by its Information), so the detail window must resolve a clicked uid against THIS report,
    /// not the app's live <c>_lastReport</c> (resolving against the live report while a saved battle is on
    /// screen is what produced the raw-uid title + all-zero breakdown).</summary>
    public DpsReport? CurrentReport => _lastReport;

    public void Update(DpsReport report)
    {
        _lastReport = report;
        string? mobName = report.Target?.Mob.Name;
        bool hasTarget = !string.IsNullOrEmpty(mobName);
        // 난이도를 칩으로 그리려면 이름과 라벨이 갈려 있어야 한다 — DisplayName 은 괄호로 합쳐서 준다.
        // (합쳐진 형태는 전투 기록·업로드가 계속 쓰므로 그쪽은 그대로 둔다.)
        if (hasTarget)
        {
            (string pName, string? pVariant, string? pDungeon) =
                // 🔑 리포트에 동결된 난이도를 쓴다. 예전엔 여기서 추적기를 **라이브로** 조회해서, 기록에서
                // 지난 시련 전투를 열면 지금 들어가 있는 시련의 단계가 찍혔다(라이브만 보면 늘 맞아 보인다).
                _encounters.DisplayParts(report.Target!.Mob.Code, mobName, FrozenTrialLabel(report));
            TargetName = pName;
            TargetVariantText = pVariant ?? string.Empty;
            TargetVariantVisibility = string.IsNullOrEmpty(pVariant) ? Visibility.Collapsed : Visibility.Visible;
            // 무대 서브라인 "던전 · 난이도". 페이즈는 미터에 데이터가 없어 넣지 않는다.
            TargetSubtitle = string.Join(" · ",
                new[] { pDungeon, pVariant }.Where(x => !string.IsNullOrWhiteSpace(x)));
            TargetSubtitleVisibility = TargetSubtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            TargetName = "타겟 인식 실패";
            TargetVariantText = string.Empty;
            TargetVariantVisibility = Visibility.Collapsed;
            TargetSubtitle = string.Empty;
            TargetSubtitleVisibility = Visibility.Collapsed;
        }

        TargetFailedVisibility = hasTarget ? Visibility.Collapsed : Visibility.Visible;
        if (report.Target is { MaxHp: > 0 } tgt)
        {
            double ratio = Math.Clamp((double)tgt.RemainHp / tgt.MaxHp, 0, 1);
            double pct = ratio * 100.0;
            TargetHpRatio = ratio;
            TargetHpRest = 1.0 - ratio;
            TargetHpText = FormatTargetHp(tgt.RemainHp, tgt.MaxHp, pct, _settings.TargetInfoDisplayMode);
            TargetHpPercentText = pct.ToString("F1", CultureInfo.InvariantCulture) + "%";
            TargetHpAmountText = FormatTargetHpAmount(tgt.RemainHp, tgt.MaxHp, _settings.TargetInfoDisplayMode);
            TargetHpVisibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;

            // 처치까지 = 남은 HP ÷ 지금 파티 DPS. 미터만 낼 수 있는 숫자다.
            // ⚠️ 보스 최대 HP 가 틀리면(포화·미측정) 이 값도 같이 틀린다 — 그래서 남은 HP 를 모르는
            //    상황에서는 아예 감춘다. 틀린 값을 자신 있게 보여주는 것보다 안 보여주는 게 낫다.
            double partyDps = report.Information.Values.Sum(i => i.Dps);
            bool etaSane = partyDps > 0 && tgt.RemainHp > 0;
            TargetEtaText = etaSane ? FormatDuration((long)(tgt.RemainHp / partyDps * 1000.0)) : string.Empty;
            TargetEtaVisibility = etaSane ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            TargetHpRatio = 0;
            TargetHpRest = 1.0;
            TargetHpText = string.Empty;
            TargetHpPercentText = string.Empty;
            TargetHpAmountText = string.Empty;
            TargetEtaText = string.Empty;
            TargetEtaVisibility = Visibility.Collapsed;
            TargetHpVisibility = Visibility.Collapsed;
        }

        long durationMs = Math.Max(report.BattleEnd - report.BattleStart, 0);
        Duration = FormatDuration(durationMs);
        // In combat = activity within the last ~1.5s (mirrors React isInCombat + 1s debounce).
        bool inCombat = report.Information.Count > 0
            && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - report.BattleEnd < 1500;
        CombatStatusText = inCombat ? "전투 중" : "대기 중";
        CombatStatusBrush = inCombat ? CombatActiveBrush : CombatTimeBrush;

        // metric = amount (total mode) or dps. Sort desc, take 10 (max raid = 5+5), always include self.
        bool total = _settings.UseTotalDamage;
        bool entire = _settings.UseEntireContribution;
        NameDisplay nameMode = _settings.NameDisplayMode;
        double rowHeight = _settings.RowHeight;
        // 레이아웃 기하는 창 단위로 한 번만 만든다((id, rowHeight) 캐시). 행은 이 값을 굽지 않고
        // RelativeSource 로 직접 본다.
        MeterLayoutVisual layoutVisual = RefreshLayout();
        string barStyle = _settings.BarStyle; // "fill" (cell fill) / "bar" (thin bottom bar) / "none"
        Visibility fillVis = barStyle == "fill" ? Visibility.Visible : Visibility.Collapsed;
        Visibility barVis = barStyle == "bar" ? Visibility.Visible : Visibility.Collapsed;
        // The boss HP gauge follows the same 게이지 형태 choice as the rows (fill / thin bar / none).
        TargetBarFillVisibility = fillVis;
        TargetBottomBarVisibility = barVis;
        double Metric(DpsInformation info) => total ? info.Amount : info.Dps;

        // 행에 찍을 초당 피해량의 종류. 총 피해량 모드에서는 언제나 Dps 로 떨어진다(정규화된 누적 피해량이라는
        // 물건은 없다 — MeterSettings.RowDpsMetricMode 주석 참조).
        RowDpsMetricMode metricMode = _settings.RowDpsMetricMode;
        IReadOnlyDictionary<int, DpsMetricResult> metrics =
            metricMode != RowDpsMetricMode.Dps && MetricsResolver is { } resolveMetrics
                ? resolveMetrics(report)
                : EmptyMetrics;

        // uid -> 표시/정렬에 쓸 값. 지표를 못 구한 행(버프 정보가 없어 계산이 안 된 참가자)은 raw DPS 로 남는다 —
        // 0 으로 떨어뜨리면 그 사람만 맨 아래로 밀려 "빠진 것처럼" 보인다.
        Dictionary<int, double>? metricOverride = null;
        if (metrics.Count > 0)
        {
            metricOverride = new Dictionary<int, double>(metrics.Count);
            foreach ((int uid, DpsMetricResult m) in metrics)
            {
                metricOverride[uid] = metricMode == RowDpsMetricMode.Rdps ? m.Rdps : m.Ndps;
            }
        }

        // ⚠️ 딜이 0인 행에는 절대 대체 지표를 붙이지 않는다. 전투 전 로스터 프리뷰는 <b>끝난 리포트 위에</b>
        // 빈 DpsInformation 으로 얹히는데(OverlayRowBuilder), 그 리포트에는 직전 전투의 nDPS/rDPS가 얼어 있다.
        // uid 만 보고 붙이면 아직 아무것도 안 한 대기 행에 지난 전투 숫자가 뜬다.
        double RowValue(int uid, DpsInformation info) =>
            info.Amount > 0 && metricOverride != null && metricOverride.TryGetValue(uid, out double v)
                ? v
                : Metric(info);

        // Row selection lives in the pure OverlayRowBuilder (App.Core, unit-tested): it drops bare/no-nickname
        // combat rows (a mid-join provisional actor that would otherwise show as a blank "broken" line — its DPS
        // still accumulates and the row appears once identity arrives), surfaces the pre-combat party roster
        // (incl. the App-injected self) only on the fresh report, always includes self, and reports whether any
        // DISPLAYABLE combat row exists via hasCombatRows (so an all-bare mid-join can't render a boss bar/timer
        // over the placeholder). Self-coloring uses the frozen report.ExecutorId, else the live _selfId.
        IReadOnlyList<OverlayRowBuilder.Row> display = OverlayRowBuilder.Build(
            report, _roster, _selfId, total, _settings.ShowPreCombatRoster, out bool hasCombatRows,
            topN: _settings.DisplayRowCap, // 사용자가 고른 인원의 1.5배 여유 (파티원·본인은 추가로 상한 면제)
            selfNickname: _selfNickname, selfServer: _selfServer, selfJob: _selfJob, selfPower: _selfPower,
            authoritativeParty: _authoritativeParty,
            metricOverride: metricOverride,
            // Opt-in "던전 강제 집계": only on a classified instanced (원정/초월/성역) boss (stamped live into the report).
            forceInstanceTracking: _settings.ForceInstanceTracking && report.TargetInstanced,
            rosterIdentities: _authoritativePartyIdentities,
            memberProfiles: _memberProfiles,
            allowRosterResurface: _allowRosterResurface);

        double topMetric = Math.Max(display.Count > 0 ? display.Max(e => RowValue(e.Uid, e.Info)) : 0.0, 1.0);

        // "off" is the hard kill switch: no ring, no chip, no timer — the row renders through the IsNone
        // trigger and is pixel-identical to a build without the feature.
        if (TierResolver is { } resolveTiers)
        {
            _rowTiers = resolveTiers(report) ?? EmptyTiers;
        }

        bool tierOn = _settings.TierShow && _settings.TierEffects != "off" && _rowTiers.Count > 0;
        bool isLightSkin = _isLight();
        int animatedTierRows = 0;
        bool nameFxOn = _settings.NameFxMode != "off" && _nameFx.Count > 0;
        int animatedNameRows = 0;
        TierBadge selfTier = TierBadge.None;
        RowTier selfRowTier = default;

        for (int i = 0; i < display.Count; i++)
        {
            OverlayRowBuilder.Row e = display[i];
            bool isUser = e.IsSelf;
            double contribution = e.Info.Contribution; // fill tier always uses party contribution
            int power = e.User?.Power ?? 0;
            int server = e.User?.Server ?? 0;
            // Restored bracketed server tag (dropped in the WPF migration). A SEPARATE row element (not folded
            // into Name) so it survives name masking + the Name MaxWidth ellipsis; collapsed when off/unknown.
            string serverTag = _settings.ShowServerTag ? ServerNames.GetServerLabel(server) : string.Empty;
            // 분자와 분모가 반드시 같은 지표여야 한다. topMetric 은 위에서 선택된 지표의 최댓값이므로 여기서
            // raw DPS 를 쓰면 서로 다른 물건을 나누게 되고, rDPS 1위(원딜보다 raw 가 낮은 서포터)가 가장 짧은
            // 막대를 다는 상태가 된다 — 이 기능이 보여주려는 그림과 정반대다.
            double ratio = Math.Clamp(RowValue(e.Uid, e.Info) / topMetric, 0.0, 1.0);
            double barRatio = ratio > 0 ? Math.Max(0.015, ratio) : 0.0; // React max(1.5%, ratio) so small bars stay visible
            string? jobName = e.User?.Job is JobClass jc ? jc.ClassName() : null;
            // 직업 강조 mode: this player's job bar (self keeps _userBar; unresolved job -> _normalBar).
            Brush jobBar = e.User?.Job is JobClass jcb && _jobBars.TryGetValue(jcb, out Brush? jbr) ? jbr : _normalBar;

            string displayName = MeterFormat.DisplayName(e.User?.Nickname, nameMode, isUser);

            // Tier decoration. Others only get the ring (their career tier is a server lookup gated on consent);
            // the "상위 X.X%" chip is self-only so it can never eat another row's 150px name column.
            TierBadge badge = TierBadge.None;
            TierRowInfo tierInfo = TierRowInfo.Empty;
            if (tierOn && _rowTiers.TryGetValue(e.Uid, out RowTier rt))
            {
                if (isUser || _settings.TierShowOthers)
                {
                    badge = TierPalette.For(rt.TierRank, isLightSkin);
                    if (badge.Animated)
                    {
                        animatedTierRows++;
                    }
                }

                tierInfo = TierRowInfo.Build(badge, rt);

                if (isUser)
                {
                    selfTier = badge;
                    selfRowTier = rt;
                }
            }

            // 후원자·랭커 닉네임 연출. 판정 키 (서버, 닉네임) 는 여기 이미 손 안에 있으므로 행 레코드에
            // identityHash 를 실을 필요가 없다. 매 틱 SHA-256 을 다시 돌지 않도록 소형 메모를 둔다 —
            // 로스터가 교체되면 통째로 비운다.
            Brush nameBrush = MeterFormat.ServerTier(server) switch
            {
                ServerColorTier.A => _nameServerA,
                ServerColorTier.B => _nameServerB,
                _ => _nameDefault,
            };
            NameFxBadge fx = NameFxBadge.None;
            Brush? gaugeSkin = null;
            string? gaugeSkinId = null;
            if (nameFxOn && (isUser ? _settings.NameFxShowSelf : _settings.NameFxShowOthers))
            {
                NameFxEntry? grant = ResolveNameFxGrant(server, e.User?.Nickname);
                fx = ResolveNameFx(grant, isLightSkin);
                if (fx.Animated)
                {
                    animatedNameRows++;
                }

                // 게이지 스킨. 계열(후원자/랭커) 판정은 명단이 권위라 여기서 다시 검사하지 않는다.
                // 애니메이션 여부는 전역 게이트를 그대로 따른다('색상만'이면 정지한 채 칠해진다).
                if (_settings.NameFxGauge && grant?.GaugeId is { Length: > 0 } gid)
                {
                    gaugeSkin = NameFxPalette.GaugeBrush(gid, isLightSkin);
                    if (gaugeSkin is not null)
                    {
                        animatedNameRows++;
                        // 브러시가 실제로 해석됐을 때만 id 를 행까지 보낸다 — 장식 레이어는 이 값만 보고
                        // 그리므로, 모르는 id 가 여기를 통과하면 색은 기본인데 장식만 뜨는 상태가 된다.
                        gaugeSkinId = gid;
                    }
                }
            }

            var row = new RowViewModel(
                Id: e.Uid,
                Rank: i + 1,
                Name: displayName,
                NameFontFamily: GlyphFallback.ForName(_settings.FontFamily, displayName),
                ServerTag: serverTag,
                ServerTagVisibility: serverTag.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
                PowerText: power > 0 ? MeterFormat.FormatPower(power) : string.Empty,
                PowerVisible: power > 0 ? Visibility.Visible : Visibility.Collapsed,
                DamageText: total
                    ? MeterFormat.FormatAmount(e.Info.Amount)
                    : MeterFormat.FormatDps(RowValue(e.Uid, e.Info)),
                PercentText: MeterFormat.FormatPercent(entire ? e.Info.EntireContribution : contribution),
                BarRatio: barRatio,
                BarRest: 1.0 - barRatio,
                FillBrush: _theme.BarColorMode == "job"
                    ? (isUser ? _userBar : jobBar)
                    : (isUser ? _userBar : contribution < 3 ? _errorBar : contribution < 5 ? _warningBar : _normalBar),
                NameBrush: nameBrush,
                PowerBrush: _amountBrush,
                DamageBrush: _dpsBrush,
                PercentBrush: _percentBrush,
                IsUser: isUser,
                RowHeight: rowHeight,
                IconSource: JoinIcons.Job(jobName),
                AccentOpacity: isUser ? 0.95 : 0.82,
                BarFillVisibility: fillVis,
                BottomBarVisibility: barVis,
                Tier: badge,
                TierInfo: tierInfo,
                NameFillBrush: fx.IsNone ? nameBrush : fx.NameFill,
                // 스킨 없는 행만 레이아웃이 정한 불투명도를 쓴다. 스킨이 붙은 행은 0.58 고정 —
                // 레이아웃이 이걸 덮으면 돈 내고 산 게이지 스킨이 희미해지고 입자만 둥둥 뜬다.
                GaugeOpacity: gaugeSkin is null ? layoutVisual.PlainFillOpacity : 0.58,
                GaugeBrush: gaugeSkin ?? ShadeFill(layoutVisual, _theme.BarColorMode == "job"
                    ? (isUser ? _userBar : jobBar)
                    : (isUser ? _userBar : contribution < 3 ? _errorBar : contribution < 5 ? _warningBar : _normalBar)),
                GaugeSkinId: gaugeSkinId,
                // 장식은 '움직임'이다. 색상만·저사양·게이지 형태 없음에서는 색 채움만 남고 장식은 그리지
                // 않는다 — 그 셋 모두 색을 지우는 설정이 아니기 때문이다.
                GaugeFxEnabled: gaugeSkinId is not null
                    && _settings.NameFxMode == "animated"
                    && !_settings.LowSpecMode
                    && _settings.BarStyle == "fill");

            if (i < Rows.Count)
            {
                Rows[i] = row;
            }
            else
            {
                Rows.Add(row);
            }
        }

        for (int i = Rows.Count - 1; i >= display.Count; i--)
        {
            Rows.RemoveAt(i);
        }

        // Idle CPU must be exactly zero when no animated tier is on screen, so the sheen timer is demand-driven.
        // LowSpecMode force-disables it — that property's doc comment has always claimed to "force-disable
        // display-only embellishments" while only pinning the refresh interval; this is the first thing it
        // actually switches off.
        if (!_preview) // 미리보기는 공용 시계의 수요를 대신 써 버린다 — _preview 주석 참고
        {
            TierSheen.SetDemand(animatedTierRows, _settings.TierEffects == "animated" && !_settings.LowSpecMode);
            NameFxSheen.SetLowSpec(_settings.LowSpecMode);
            NameFxSheen.SetDemand(
                animatedNameRows,
                _settings.NameFxMode == "animated" && !_settings.LowSpecMode,
                _settings.NameFxSpeedPercent);
        }
        ApplySelfTierChip(selfTier, selfRowTier);

        PlaceholderVisibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // React TargetInfo/CombatTimer show when players>0; compact mode can hide each (with overrides).
        // hasCombatRows is the count of DISPLAYABLE (named) combat rows from OverlayRowBuilder above — NOT
        // report.Information.Count — so the pre-combat idle roster (and an all-bare mid-join) never shows a
        // "타겟 인식 실패" bar or a 00:00 timer before a nameable fight is on screen.
        bool minimal = _settings.IsMinimal;
        TargetInfoVisibility = hasCombatRows && (!minimal || _settings.ShowTargetInfoInMinimal) ? Visibility.Visible : Visibility.Collapsed;
        // 타겟 가시성이 바뀌면 대기 카드가 들어갈지 말지도 같이 다시 판단해야 한다.
        RefreshIdleCard();
        CombatTimerVisibility = durationMs > 0 && hasCombatRows && (!minimal || _settings.ShowCombatTimerInMinimal)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private string _selfTierChipText = string.Empty;
    public string SelfTierChipText { get => _selfTierChipText; private set => Set(ref _selfTierChipText, value); }

    private string _selfTierChipToolTip = string.Empty;
    public string SelfTierChipToolTip { get => _selfTierChipToolTip; private set => Set(ref _selfTierChipToolTip, value); }

    private string _selfTierChipBasis = string.Empty;
    public string SelfTierChipBasis { get => _selfTierChipBasis; private set => Set(ref _selfTierChipBasis, value); }

    private Visibility _selfTierChipBasisVisibility = Visibility.Collapsed;
    public Visibility SelfTierChipBasisVisibility { get => _selfTierChipBasisVisibility; private set => Set(ref _selfTierChipBasisVisibility, value); }

    private Visibility _selfTierChipVisibility = Visibility.Collapsed;
    public Visibility SelfTierChipVisibility { get => _selfTierChipVisibility; private set => Set(ref _selfTierChipVisibility, value); }

    private Brush _selfTierChipBg = Brushes.Transparent;
    public Brush SelfTierChipBg { get => _selfTierChipBg; private set => Set(ref _selfTierChipBg, value); }

    private Brush _selfTierChipFg = Brushes.Transparent;
    public Brush SelfTierChipFg { get => _selfTierChipFg; private set => Set(ref _selfTierChipFg, value); }

    private Brush _selfTierChipRing = Brushes.Transparent;
    public Brush SelfTierChipRing { get => _selfTierChipRing; private set => Set(ref _selfTierChipRing, value); }

    /// <summary>Publish the footer chip: "챌린저 · 상위 0.7%" with the comparison basis on a second line. Hidden
    /// when the feature is off, when the fight's cohort had no shipped distribution row (표본 부족 — we never
    /// guess a number), or when the user turned the chip off. The tier NAME still shows on its own so a rank
    /// without a live percentile is not a blank chip.
    /// <para>The basis is rendered, not tucked into the ToolTip. Whether a percentile was measured against
    /// comparable gear or against everyone changes what it claims, and the reader most affected is the one who
    /// fell back to the whole cohort without asking to — they would never hover to find that out.</para></summary>
    private void ApplySelfTierChip(TierBadge badge, RowTier tier)
    {
        bool wanted = _settings.TierShow && _settings.TierShowSelfChip && !badge.IsNone;
        string percent = tier.BattleTopPercent is double p ? TierLadder.FormatTopPercent(p) : string.Empty;
        if (!wanted)
        {
            SelfTierChipVisibility = Visibility.Collapsed;
            return;
        }

        // No percentile, nothing to qualify — a lone "전체 전투력 기준" under a bare tier name would describe a
        // measurement that was never made.
        string basis = percent.Length > 0 ? tier.ComparisonBasis ?? string.Empty : string.Empty;

        SelfTierChipText = percent.Length > 0 ? $"{badge.Name} · {percent}" : badge.Name;
        SelfTierChipBasis = basis;
        SelfTierChipBasisVisibility = basis.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelfTierChipToolTip = tier.DungeonLabel is { Length: > 0 } label
            ? $"{label} · {(percent.Length > 0 ? percent : "이번 전투 표본 부족")}"
            : SelfTierChipText;
        SelfTierChipBg = badge.ChipBg;
        SelfTierChipFg = badge.ChipFg;
        SelfTierChipRing = badge.RankRing;
        SelfTierChipVisibility = Visibility.Visible;
    }

    /// <summary>Combat-time readout: under a minute keeps "12.3s"; from a minute up it converts to
    /// "6m 32s" (whole seconds, decimals truncated) so long fights read cleanly.</summary>
    private static string FormatDuration(long ms)
    {
        long totalSeconds = ms / 1000;
        if (totalSeconds < 60)
        {
            return $"{ms / 1000.0:F1}s";
        }

        return $"{totalSeconds / 60}m {totalSeconds % 60}s";
    }

    /// <summary>Boss HP readout per React targetInfoDisplayMode.</summary>
    /// <summary><see cref="FormatTargetHp"/> 와 같은 표시 형식 분기를 쓰되 퍼센트 꼬리를 뺀다.</summary>
    private static string FormatTargetHpAmount(long remain, long max, string mode) => mode switch
    {
        "percent" => string.Empty,
        "remain_percent" => MeterFormat.FormatAmount(remain),
        "remain_full_percent" => remain.ToString("N0"),
        "hp_percent" => $"{MeterFormat.FormatAmount(remain)} / {MeterFormat.FormatAmount(max)}",
        _ => $"{remain:N0} / {max:N0}",
    };

    private static string FormatTargetHp(long remain, long max, double pct, string mode) => mode switch
    {
        "percent" => $"{pct:F1}%",
        "remain_percent" => $"{MeterFormat.FormatAmount(remain)}  {pct:F1}%",
        "remain_full_percent" => $"{remain:N0}  {pct:F1}%",
        "hp_percent" => $"{MeterFormat.FormatAmount(remain)} / {MeterFormat.FormatAmount(max)}  {pct:F1}%",
        _ => $"{remain:N0} / {max:N0}  {pct:F1}%", // hp_full_percent
    };

    // angle 0 = left->right, matching the React `linear-gradient(to right, ...)` bars.
    /// <summary>The row-bar gradient for a theme colour pair. Public so the settings preview can paint its mock
    /// row with the SAME brush the meter uses — a preview that invents its own colours cannot be trusted to say
    /// what the meter will look like.</summary>
    public static Brush RowGradient(string from, string to) => ThemeGradient(from, to);

    private static Brush ThemeGradient(string from, string to)
    {
        var brush = new LinearGradientBrush(ToColor(from), ToColor(to), 0.0);
        brush.Freeze();
        return brush;
    }

    private static Brush ThemeSolid(string value)
    {
        var brush = new SolidColorBrush(ToColor(value));
        brush.Freeze();
        return brush;
    }

    // ColorString handles hex AND rgba(...) (ColorConverter.ConvertFromString cannot parse rgba()).
    private static Color ToColor(string value) =>
        ColorString.TryParse(value, out ColorRgba c) ? Color.FromArgb(c.A, c.R, c.G, c.B) : Colors.White;

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

public sealed record RowViewModel(
    int Id,
    int Rank,
    string Name,
    System.Windows.Media.FontFamily NameFontFamily,
    string ServerTag,
    Visibility ServerTagVisibility,
    string PowerText,
    Visibility PowerVisible,
    string DamageText,
    string PercentText,
    double BarRatio,
    double BarRest,
    Brush FillBrush,
    Brush NameBrush,
    Brush PowerBrush,
    Brush DamageBrush,
    Brush PercentBrush,
    bool IsUser,
    double RowHeight,
    System.Windows.Media.ImageSource? IconSource,
    double AccentOpacity,
    Visibility BarFillVisibility,
    Visibility BottomBarVisibility,
    TierBadge Tier,
    TierRowInfo TierInfo,
    /// <summary>What the nickname is actually painted with — <c>NameBrush</c> unless this character carries a
    /// supporter/ranker effect. Appended at the END on purpose: this record has same-typed neighbours, so a
    /// parameter inserted in the middle transposes silently without failing to compile.</summary>
    Brush NameFillBrush,
    /// <summary>The fill panel's opacity. 0.3 as it has always been, raised only for a row carrying a ranker
    /// gauge skin — at 0.3 a skin reads as "the bar is a bit murky" rather than as a mark. The numbers on top
    /// are near-white, and the skins keep their bright band narrow, so legibility survives the bump.</summary>
    double GaugeOpacity,
    /// <summary>What the DPS BAR is painted with. Equal to <see cref="FillBrush"/> unless this character has a
    /// ranker gauge skin. Kept separate on purpose: <c>FillBrush</c> also paints the 3 px accent rail, which has
    /// no visibility gate and is the only thing distinguishing your own row and each job — a gauge skin there
    /// would compress a four-stop gradient into three pixels and erase that signal.</summary>
    Brush GaugeBrush,
    /// <summary>The gauge skin this row actually resolved to, or null. Passed explicitly rather than inferred
    /// from <see cref="GaugeBrush"/> or a display name: the decoration layer keys off it, and every gate the
    /// grant had to pass (feature on, scope, 게이지 토글, known id) has already been applied by the time it is
    /// set.</summary>
    string? GaugeSkinId = null,
    /// <summary>Whether the decoration may animate on this row. Separate from having a skin, because 색상만
    /// (static), low-spec and <c>BarStyle != fill</c> all keep the colour fill and drop only the decoration.</summary>
    bool GaugeFxEnabled = false);

// 레이아웃 기하는 행에 굽지 않는다. 구웠더니 설정에서 레이아웃을 바꿔도 다음 Update(report) 까지
// 옛 기하가 남았고, 전투가 없으면 리포트가 안 와서 "대기 중" 화면의 행이 영영 안 바뀌었다.
// 지금은 행 템플릿이 창의 OverlayViewModel.Layout 을 RelativeSource 로 직접 본다 — 출처가 하나라
// 낡을 수가 없다.


/// <summary>Per-row tier text. Separate from <see cref="TierBadge"/> (a shared singleton) because these values
/// differ per row — keeping them in one record adds two positional parameters to RowViewModel instead of four,
/// which matters because that record has same-typed neighbours a transposition would not fail to compile.</summary>
public sealed record TierRowInfo(string PercentText, Visibility PercentVisibility, string ToolTip, bool HasToolTip)
{
    public static readonly TierRowInfo Empty = new(string.Empty, Visibility.Collapsed, string.Empty, false);

    public static TierRowInfo Build(TierBadge badge, RowTier tier)
    {
        if (badge.IsNone)
        {
            return Empty;
        }

        string percent = tier.BattleTopPercent is double p ? TierLadder.FormatTopPercent(p) : string.Empty;
        string dungeon = tier.DungeonLabel is { Length: > 0 } d ? $" · {d}" : string.Empty;
        // Each row is banded by ITS OWN combat power, so party members can be measured against different pools
        // than the player is. The row has no space for a second line (the 490px width is spent on
        // name/server/power), so here the basis rides the ToolTip — the player's own basis is on the footer
        // chip, rendered.
        string basis = percent.Length > 0 && tier.ComparisonBasis is { Length: > 0 } b ? $" ({b})" : string.Empty;
        string tip = percent.Length > 0
            ? $"{badge.Name}{dungeon} · 이번 전투 {percent}{basis}"
            : $"{badge.Name}{dungeon} · 이번 전투 표본 부족";

        return new TierRowInfo(
            percent,
            percent.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            tip,
            true);
    }
}
