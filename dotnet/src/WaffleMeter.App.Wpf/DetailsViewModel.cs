using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using WaffleMeter.App.Core;
using WaffleMeter.Data;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// View model for the detail window: fetches a player's skill breakdown + buff/debuff uptime via
/// <see cref="DetailModel"/> and exposes bindable, styled rows. <see cref="Refresh"/> is called on
/// each live report tick and preserves per-group expansion state.
/// </summary>
public sealed class DetailsViewModel : INotifyPropertyChanged
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    // Brighter emerald gradient (was a flat, dull #55c42a→#3a9e20) so the damage bar reads clean + vivid
    // on the dark panel; white bold damage text is overlaid for contrast.
    internal static readonly Brush SkillBar = Frozen(new LinearGradientBrush(C("#FF4ADE80"), C("#FF15A34A"), 0.0));
    // Uptime gauge: the bar is a left-to-right gradient (dark -> bright) so it reads as a lit gauge rather than
    // a painted block; the matching solid is used for the % text, where a gradient would smear across glyphs.
    private static readonly Brush GoodBar = Frozen(new LinearGradientBrush(C("#FF15803D"), C("#FF4ADE80"), 0.0));
    private static readonly Brush WarnBar = Frozen(new LinearGradientBrush(C("#FFB45309"), C("#FFFBBF24"), 0.0));
    private static readonly Brush BadBar = Frozen(new LinearGradientBrush(C("#FFB91C1C"), C("#FFF87171"), 0.0));
    private static readonly Brush GoodBuff = Frozen(new SolidColorBrush(C("#4ade80")));
    private static readonly Brush WarnBuff = Frozen(new SolidColorBrush(C("#fbbf24")));
    private static readonly Brush BadBuff = Frozen(new SolidColorBrush(C("#f87171")));

    private readonly int _uid;
    private readonly DpsCalculator _calc;
    private readonly string _fallbackName;

    /// <summary>「남이 준 버프」 체크박스의 상태를 담아 두는 곳. 상세창은 행을 클릭할 때마다 새로 만들어지므로
    /// VM 필드에만 두면 창을 닫는 순간 선택이 날아간다. null 이면(테스트·미배선) 그 창에서만 사는 값이 된다.</summary>
    private readonly MeterSettings? _settings;

    /// <summary>이 캐릭터의 티어 줄을 <b>표시 중인 리포트에서</b> 다시 뽑는 공급자. 값으로 스냅샷해 두지 않는
    /// 이유는 티어가 "렌더되는 리포트의 함수"여야 하기 때문이다 — 밀어 넣는 모델(SetTiers)은 기록 재생에서
    /// 옛 값을 그리던 회귀를 낳아 이미 한 번 걷어냈다. null 이면 티어 타일을 접는다(설정 꺼짐·미배선).</summary>
    private readonly Func<DpsReport, TierDetailLine>? _tierLineOf;

    // 같은 리포트로 Refresh 가 다시 와도 재평가하지 않는다. 티어 평가는 참가자마다 사전 조회 + 해시라
    // 미터가 이미 틱마다 한 번 돌리고 있고, 여기서 무조건 또 돌리면 그 비용이 두 배가 된다.
    private DpsReport? _tierReport;

    public DetailsViewModel(DpsReport report, int uid, DpsCalculator calc, string name, MeterColorTheme theme, string fontFamily,
        Func<DpsReport, TierDetailLine>? tierLineOf = null, MeterSettings? settings = null)
    {
        _uid = uid;
        _calc = calc;
        _fallbackName = name;
        _tierLineOf = tierLineOf;
        _settings = settings;
        _showPartyBuffs = settings?.DetailShowPartyBuffs ?? false;
        _timelineCooldownOnly = settings?.DetailTimelineCooldownOnly ?? false;
        FontFamily = fontFamily;
        // Theme-linked text colors (snapshot at open; the detail window is short-lived per row click).
        AmountBrush = ThemeBrush(theme.MeterStatAmount);
        ContributionBrush = ThemeBrush(theme.MeterStatPercent);
        DetailCombatTimeBrush = ThemeBrush(theme.CombatTimeColor);
        Refresh(report);
    }

    private string _title = string.Empty;

    /// <summary>"{player} 상세내역" — re-derived on every <see cref="Refresh"/> from the resolved report's
    /// roster, so it self-heals to the real nickname and never gets stuck showing a bare numeric uid.</summary>
    public string Title { get => _title; private set => Set(ref _title, value); }

    /// <summary>Selected UI font family (resolved to a FontFamily by FontFamilyConverter in XAML).</summary>
    public string FontFamily { get; }

    /// <summary>누적 피해량 color (theme meterStatAmount).</summary>
    public Brush AmountBrush { get; }

    /// <summary>피해량 기여도 color (theme meterStatPercent).</summary>
    public Brush ContributionBrush { get; }

    /// <summary>전투 시간 color (theme combatTimeColor).</summary>
    public Brush DetailCombatTimeBrush { get; }
    public ObservableCollection<SkillGroupVM> Skills { get; } = new();
    public ObservableCollection<BuffRowVM> Buffs { get; } = new();
    public ObservableCollection<BuffRowVM> Debuffs { get; } = new();

    private DpsGraphModel? _graph;

    /// <summary>Render-ready DPS-over-time graph for this player: per-second damage plus this player's buff
    /// timeline (icon lane), drawn on a plain Canvas by <see cref="DetailWindow"/>. Rebuilt every
    /// <see cref="Refresh"/>; null when there isn't enough to plot.</summary>
    public DpsGraphModel? Graph { get => _graph; private set => Set(ref _graph, value); }

    /// <summary>필터를 걸기 <b>전</b>의 버프 섹션 전체. 탭 활성화 판정(<see cref="HasBuffs"/>)과 체크박스 토글이
    /// 둘 다 이걸 본다 — 필터 결과로 판정하면 「남이 준 버프」밖에 없는 사람의 탭이 비활성화되고, 그러면 그 탭을
    /// 채워 줄 체크박스가 비활성화된 탭 안에 갇혀 영영 못 켠다.</summary>
    private IReadOnlyList<DetailBuffSection> _buffSections = [];

    private bool _showPartyBuffs;

    /// <summary>「남이 준 버프」 섹션을 표에 포함할지. 기본은 꺼짐 — 기본 화면이 답하는 질문은 "내가 뭘 유지했나"이고,
    /// 공대에서는 이 섹션만 수십 줄이라 켜 두면 내 버프가 묻힌다. 설정 파일(<c>detail.showPartyBuffs</c>)에
    /// 즉시 저장돼 다음에 연 상세창에도 그대로 이어진다.</summary>
    public bool ShowPartyBuffs
    {
        get => _showPartyBuffs;
        set
        {
            if (_showPartyBuffs == value)
            {
                return;
            }

            Set(ref _showPartyBuffs, value);
            if (_settings != null)
            {
                _settings.DetailShowPartyBuffs = value;
            }

            RebuildBuffs();
        }
    }

    private bool _hasPartyBuffs;

    /// <summary>이 전투에 남이 걸어 준 버프가 하나라도 있나. 없으면 체크박스를 접는다 — 켜도 아무 일이 안 일어나는
    /// 컨트롤은 고장으로 읽힌다(솔로 전투에서 늘 그렇다).</summary>
    public bool HasPartyBuffs { get => _hasPartyBuffs; private set => Set(ref _hasPartyBuffs, value); }

    /// <summary>스킬 시전 순서(간격 포함). 전투가 길면 수천 줄이 되므로 XAML 쪽에서 가상화한다.</summary>
    public ObservableCollection<SkillCastRowVM> TimelineCasts { get; } = new();

    /// <summary>스킬별 사용 간격 집계(횟수·평균·중앙값·최소·최대).</summary>
    public ObservableCollection<SkillGapRowVM> TimelineSkills { get; } = new();

    private string _timelineSummaryText = string.Empty;

    /// <summary>타임라인 탭 상단 한 줄 요약.</summary>
    public string TimelineSummaryText { get => _timelineSummaryText; private set => Set(ref _timelineSummaryText, value); }

    private bool _timelineCooldownOnly;

    /// <summary>타임라인에서 <b>쿨타임을 돌린 발동만</b> 볼지. 기본 꺼짐.
    /// <para>일부 스킬은 활성화 1회에 프레임을 둘 낸다(시전→적중으로 보이는 350ms 짝). 그걸 시간으로 접으면
    /// 임의의 임계값이 필요하고 그게 매크로 케이던스를 지운다 — 대신 서버가 준 사실 하나로 거른다.</para>
    /// <para>⚠️ 쿨 없는 스킬은 통째로 사라지므로 켤 때만 쓰는 렌즈다.</para></summary>
    public bool TimelineCooldownOnly
    {
        get => _timelineCooldownOnly;
        set
        {
            if (_timelineCooldownOnly == value)
            {
                return;
            }

            Set(ref _timelineCooldownOnly, value);
            if (_settings != null)
            {
                _settings.DetailTimelineCooldownOnly = value;
            }

            // 목록의 앞부분이 통째로 달라지므로 증분 append 를 쓰면 안 된다 — 시그니처를 무효화해
            // 다음 Refresh 가 전부 다시 만들게 한다.
            _timelineSignature = long.MinValue;
            TimelineCasts.Clear();
            _rebuildTimeline?.Invoke();
        }
    }

    /// <summary>토글이 즉시 반영되도록 마지막 Refresh 의 재계산을 담아 둔다(다음 틱을 기다리지 않는다).</summary>
    private Action? _rebuildTimeline;

    private bool _hasTimeline;

    /// <summary>시전이 2건 이상일 때만 "간격"이 존재한다. 그 아래면 탭을 비활성화한다.</summary>
    public bool HasTimeline { get => _hasTimeline; private set => Set(ref _hasTimeline, value); }

    // 라이브 전투에서는 Refresh 가 매 틱 돈다. 시전 목록은 오직 뒤로만 자라므로, 바뀐 게 없으면 아무것도 하지
    // 않고, 자라기만 했으면 꼬리만 붙인다 — 수천 줄을 매 틱 다시 만들면 그래프가 이미 피해 간 비용을 되밟는다.
    private long _timelineSignature = long.MinValue;

    private bool _hasCooldownStarts;

    /// <summary>이 전투에 쿨을 돌린 발동이 하나라도 있나. 없으면 토글을 감춘다 — 켜도 목록이 비기만 한다.</summary>
    public bool HasCooldownStarts { get => _hasCooldownStarts; private set => Set(ref _hasCooldownStarts, value); }

    private bool _hasGraph;

    /// <summary>True when the series has at least two seconds to draw a line; a shorter fight shows the
    /// "표시할 데이터가 없어요" note on the graph tab instead of a single dot.</summary>
    public bool HasGraph { get => _hasGraph; private set => Set(ref _hasGraph, value); }

    private string _totalDamageText = "0";
    public string TotalDamageText { get => _totalDamageText; private set => Set(ref _totalDamageText, value); }

    private string _dpsText = "0";
    public string DpsText { get => _dpsText; private set => Set(ref _dpsText, value); }


    private string _ndpsText = "-";
    /// <summary>버프를 걷어낸 초당 피해량. 계산 불가 시 "-".</summary>
    public string NdpsText { get => _ndpsText; private set => Set(ref _ndpsText, value); }

    private string _rdpsText = "-";
    /// <summary>nDPS + 내가 파티에 얹어준 몫. 계산 불가 시 "-".</summary>
    public string RdpsText { get => _rdpsText; private set => Set(ref _rdpsText, value); }

    private Visibility _metricsVisibility = Visibility.Collapsed;
    public Visibility MetricsVisibility { get => _metricsVisibility; private set => Set(ref _metricsVisibility, value); }

    private string _buffContributionText = string.Empty;
    /// <summary>"받은 버프 +N/s · 준 버프 +N/s (파티 피해량에 기여한 피해 N)" 한 줄 요약.</summary>
    public string BuffContributionText { get => _buffContributionText; private set => Set(ref _buffContributionText, value); }

    private Visibility _buffContributionVisibility = Visibility.Collapsed;
    public Visibility BuffContributionVisibility { get => _buffContributionVisibility; private set => Set(ref _buffContributionVisibility, value); }

    // ---- 티어 타일 (요약 격자의 14번째 칸) ----
    private string _tierLabelText = "이번 전투 등급";
    /// <summary>타일 제목. `누적 등급`(서버가 준 본인 성적) / `이번 전투 등급`(이 전투에서 계산한 값).</summary>
    public string TierLabelText { get => _tierLabelText; private set => Set(ref _tierLabelText, value); }

    private string _tierRankText = string.Empty;
    /// <summary>등급 이름 — `챌린저`.</summary>
    public string TierRankText { get => _tierRankText; private set => Set(ref _tierRankText, value); }

    private string _tierPercentText = string.Empty;
    /// <summary>이번 전투 백분위 — `상위 0.7%`. 표본이 없으면 빈 문자열이고 줄이 접힌다.</summary>
    public string TierPercentText { get => _tierPercentText; private set => Set(ref _tierPercentText, value); }

    private Visibility _tierPercentVisibility = Visibility.Collapsed;
    public Visibility TierPercentVisibility { get => _tierPercentVisibility; private set => Set(ref _tierPercentVisibility, value); }

    private string _tierBasisText = string.Empty;
    /// <summary>🔑 이 타일이 존재하는 이유. 백분위가 무엇과 비교한 값인지 — 미터 행에서는 폭이 모자라 툴팁에만
    /// 있는 문장이다.</summary>
    public string TierBasisText { get => _tierBasisText; private set => Set(ref _tierBasisText, value); }

    private Visibility _tierVisibility = Visibility.Collapsed;
    public Visibility TierVisibility { get => _tierVisibility; private set => Set(ref _tierVisibility, value); }

    private Visibility _tierBasisVisibility = Visibility.Collapsed;
    public Visibility TierBasisVisibility { get => _tierBasisVisibility; private set => Set(ref _tierBasisVisibility, value); }

    private void ApplyTier(TierDetailLine line)
    {
        TierVisibility = line.HasValue ? Visibility.Visible : Visibility.Collapsed;
        TierLabelText = line.HasValue ? line.Label : "이번 전투 등급";
        TierRankText = line.Rank;
        TierPercentText = line.Percent;
        TierPercentVisibility = line.Percent.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TierBasisText = line.Basis;
        TierBasisVisibility = line.Basis.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private string _hitsText = "0";
    public string HitsText { get => _hitsText; private set => Set(ref _hitsText, value); }
    private string _contributionText = "0.0%";
    public string ContributionText { get => _contributionText; private set => Set(ref _contributionText, value); }
    private string _critText = "0.0%";
    public string CritText { get => _critText; private set => Set(ref _critText, value); }
    private string _strongText = "0.0%";
    public string StrongText { get => _strongText; private set => Set(ref _strongText, value); }
    private string _perfectText = "0.0%";
    public string PerfectText { get => _perfectText; private set => Set(ref _perfectText, value); }
    private string _backText = "0.0%";
    public string BackText { get => _backText; private set => Set(ref _backText, value); }
    private string _frontText = "0.0%";
    public string FrontText { get => _frontText; private set => Set(ref _frontText, value); }
    private string _parryText = "0.0%";
    public string ParryText { get => _parryText; private set => Set(ref _parryText, value); }
    private string _combatTimeText = "0:00";
    public string CombatTimeText { get => _combatTimeText; private set => Set(ref _combatTimeText, value); }
    private bool _hasBuffs;
    public bool HasBuffs { get => _hasBuffs; private set => Set(ref _hasBuffs, value); }
    private bool _hasDebuffs;
    public bool HasDebuffs { get => _hasDebuffs; private set => Set(ref _hasDebuffs, value); }

    private bool _hasData = true;

    /// <summary>False when the resolved report has no row for this uid — the window then shows a "데이터 없음"
    /// placeholder and "-" stats instead of a misleading all-zero breakdown (and an honest title).</summary>
    public bool HasData
    {
        get => _hasData;
        private set
        {
            if (_hasData == value)
            {
                return;
            }

            _hasData = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasData)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoDataVisibility)));
        }
    }

    public Visibility NoDataVisibility => _hasData ? Visibility.Collapsed : Visibility.Visible;

    public void Refresh(DpsReport report)
    {
        // Re-derive the title + presence from the RESOLVED report each tick: the title self-heals to the
        // real nickname (or an honest "플레이어 {uid}" rather than a bare number), and a uid that isn't in
        // this report renders a "데이터 없음" state instead of a misleading all-zero breakdown.
        string? nickname = report.Contributors.FirstOrDefault(c => c.Id == _uid)?.Nickname;
        bool present = report.Information.ContainsKey(_uid) || report.Contributors.Any(c => c.Id == _uid);
        string display = !string.IsNullOrWhiteSpace(nickname) ? nickname!
            : !string.IsNullOrWhiteSpace(_fallbackName) && !int.TryParse(_fallbackName, out _) ? _fallbackName
            : $"플레이어 {_uid}";
        Title = $"{display} 상세내역";
        HasData = present;

        if (present && !ReferenceEquals(_tierReport, report))
        {
            _tierReport = report;
            ApplyTier(_tierLineOf?.Invoke(report) ?? TierDetailLine.None);
        }

        if (!present)
        {
            Skills.Clear();
            Buffs.Clear();
            Debuffs.Clear();
            _buffSections = [];
            HasBuffs = false;
            HasDebuffs = false;
            HasPartyBuffs = false;
            TimelineCasts.Clear();
            TimelineSkills.Clear();
            TimelineSummaryText = string.Empty;
            HasTimeline = false;
            HasCooldownStarts = false;
            _rebuildTimeline = null;
            _timelineSignature = long.MinValue;
            Graph = null;
            HasGraph = false;
            _graphSignature = long.MinValue;
            TotalDamageText = ContributionText = CritText = StrongText =
                PerfectText = BackText = FrontText = ParryText = CombatTimeText = "-";
            // nDPS/rDPS 도 함께 비운다. 안 그러면 "데이터 없음" 화면에 직전에 열어 둔 사람의 숫자가 남아
            // 그 사람 것으로 읽힌다 — 나머지 타일이 전부 "-" 인 만큼 더 눈에 띈다.
            NdpsText = RdpsText = "-";
            MetricsVisibility = Visibility.Collapsed;
            BuffContributionText = string.Empty;
            BuffContributionVisibility = Visibility.Collapsed;
            // 티어도 같은 이유로 비운다 — 안 그러면 "데이터 없음" 화면에 직전에 열어 둔 사람의 등급이 남는다.
            ApplyTier(TierDetailLine.None);
            _tierReport = null;
            return;
        }

        Dictionary<string, AnalyzedSkill> skills = _calc.BattleDetails(report, _uid);

        // Prefer the frozen buff-rate snapshot the battle was saved with (identical to the stats/web data).
        // The live buff repository is pruned once a battle is saved, so recomputing post-battle under-counts;
        // recompute only for the in-progress battle, where no snapshot exists yet and the repo is intact.
        bool hasSnapshot = report.BuffRates.Count > 0;
        List<OperatingData> own = hasSnapshot
            ? report.BuffRates.GetValueOrDefault(_uid) ?? new()
            : _calc.GetBuffOperatingRate(_uid, report.BattleStart, report.BattleEnd);
        List<OperatingData> boss = hasSnapshot
            ? report.BossBuffRates
            : report.Target != null
                ? _calc.GetBuffOperatingRate(report.Target.Id, report.BattleStart, report.BattleEnd)
                : new();

        User? user = report.Contributors.FirstOrDefault(c => c.Id == _uid);
        double contribution = report.Information.TryGetValue(_uid, out DpsInformation? info) ? info.Contribution : 0.0;
        long combatMs = Math.Max(report.BattleEnd - report.BattleStart, 0);

        // 회생의 계약 긴급 회복: 버프로 방송되지 않는 발동형이라 가동률(%)이 성립하지 않는다 — 발동 횟수로
        // 표시한다. 절대 시각으로 보관하므로 저장된 전투를 다시 열어도 같은 창이 집계된다.
        (int Count, int Code, string Name) heal = _calc.GetRevivalHealSummary(_uid, report.BattleStart, report.BattleEnd);
        DetailProcRow? proc = heal.Count > 0
            ? new DetailProcRow(heal.Code, heal.Name, heal.Count, "생명력 10% 이하에서 발동 (재발동 1분)")
            : null;

        // nDPS/rDPS: 저장 전투는 얼려 둔 값, 라이브는 지금 계산. 이 uid 몫이 없으면(버프 정보가 없어 계산이
        // 안 된 전투) null 로 남겨 타일을 감춘다 — 0을 그리면 "버프를 하나도 못 받았다"로 읽힌다.
        IReadOnlyDictionary<int, DpsMetricResult> metricsByUid = _calc.GetDpsMetrics(report);
        DetailMetrics? metrics = metricsByUid.TryGetValue(_uid, out DpsMetricResult m)
            ? new DetailMetrics(m.Ndps, m.Rdps, m.TakenBuffDps, m.GivenBuffDps, m.GrantedDamage)
            : null;

        // 남이 나에게 걸어 준 시너지 버프의 시전자 이름 — 스킬 표에서 "흡혈의 검 (밀피)" 처럼 붙는다. 코드만으로는
        // 내 것과 남의 것을 못 가르므로(검성 본인 타격도 같은 코드) 시전자가 나와 다른 행만 담는다.
        Dictionary<int, string>? grantedBy = null;
        foreach (OperatingData row in own)
        {
            int source = DataManager.BuffDisplayBase(row.Code);
            if (row.ActorId == _uid || !PartySynergyCatalog.IsMeasuredGrant(source)) continue;

            string granter = report.Contributors.FirstOrDefault(c => c.Id == row.ActorId)?.Nickname ?? string.Empty;
            if (granter.Length == 0) continue;

            grantedBy ??= new Dictionary<int, string>();
            grantedBy[source] = granter;
        }

        // 남이 걸어 준 버프의 시전자 이름. Contributors 는 피해를 낸 사람만 담으므로(딜 0 인 서포터는 없다)
        // 라이브 전투에서는 얼려 두기 전의 파티 스냅샷으로 보강한다. 저장 전투에는 스냅샷이 실리지 않아
        // Contributors 만 남고, 못 찾은 uid 는 DetailModel 이 "플레이어 {uid}" 로 떨어뜨린다.
        Dictionary<int, string> casterNames = new();
        foreach (User c in report.Contributors)
        {
            if (!string.IsNullOrWhiteSpace(c.Nickname))
            {
                casterNames[c.Id] = c.Nickname!;
            }
        }

        foreach (User member in report.PartySnapshot)
        {
            if (!string.IsNullOrWhiteSpace(member.Nickname))
            {
                casterNames.TryAdd(member.Id, member.Nickname!);
            }
        }

        DetailModel model = DetailModel.Compute(
            skills, own, boss, _uid, user?.Job, contribution, combatMs, proc, metrics, grantedBy, casterNames,
            _calc.ResolveSummonOwner);

        TotalDamageText = MeterFormat.FormatAmount(model.TotalDamage);
        DpsText = model.CombatMs > 0
            ? (model.TotalDamage / (model.CombatMs / 1000.0)).ToString("N0", Inv)
            : "0";
        // nDPS/rDPS 타일. 계산이 안 된 전투에서는 두 타일을 통째로 감춘다.
        NdpsText = model.Metrics is { } dm ? dm.Ndps.ToString("N0", Inv) : "-";
        RdpsText = model.Metrics is { } dm2 ? dm2.Rdps.ToString("N0", Inv) : "-";
        MetricsVisibility = model.Metrics != null ? Visibility.Visible : Visibility.Collapsed;
        BuffContributionText = model.Metrics is { } dm3
            ? $"받은 버프 +{dm3.TakenBuffDps:N0}/s · 준 버프 +{dm3.GivenBuffDps:N0}/s"
              + (dm3.GrantedDamage > 0 ? $" (파티 피해량에 기여한 피해 {MeterFormat.FormatAmount(dm3.GrantedDamage)})" : "")
            : string.Empty;
        BuffContributionVisibility =
            model.Metrics != null && BuffContributionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        HitsText = model.HitCount.ToString("N0", Inv);
        ContributionText = model.Contribution.ToString("F1", Inv) + "%";
        CritText = model.CritPct.ToString("F1", Inv) + "%";
        StrongText = model.StrongPct.ToString("F1", Inv) + "%";
        PerfectText = model.PerfectPct.ToString("F1", Inv) + "%";
        BackText = model.BackPct.ToString("F1", Inv) + "%";
        FrontText = model.FrontPct.ToString("F1", Inv) + "%";
        ParryText = model.ParryPct.ToString("F1", Inv) + "%";
        TimeSpan span = TimeSpan.FromMilliseconds(model.CombatMs);
        CombatTimeText = $"{(int)span.TotalMinutes}:{span.Seconds:D2}";

        ReconcileSkills(model.Skills, model.CombatMs);
        _buffSections = model.Buffs;
        RebuildBuffs();
        Rebuild(Debuffs, model.Debuffs);
        HasDebuffs = Debuffs.Count > 0;

        // DPS-over-time graph: per-second damage + this player's buff timeline. Prefer the frozen snapshot
        // (history replay / post-combat), fall back to the live accumulator for the in-progress battle —
        // the same snapshot-vs-live split as the buff rates above (the two are frozen together at save).
        long[] series = report.DpsSeries.Count > 0
            ? report.DpsSeries.GetValueOrDefault(_uid) ?? []
            : _calc.GetDpsSeries(_uid, report.BattleStart, report.BattleEnd);
        List<BuffTimeline> allTimelines = report.BuffIntervals.Count > 0
            ? report.BuffIntervals.GetValueOrDefault(_uid) ?? new()
            : _calc.GetBuffIntervals(_uid, report.BattleStart, report.BattleEnd);

        // Keep only the buffs that actually shape THIS player's damage: their own class(job) buffs — exactly the
        // "내 버프" set the buff-uptime tab shows (mirrors DetailModel.BuildOwnBuffs: self-cast + job-prefix match).
        // Consumables (주문서/음식/음료 → EffectiveJobPrefix 0) and other players' buffs are dropped, so a 100%-uptime
        // food/scroll no longer crowds out the real damage buffs. Falls back to "any self class buff" when the
        // player's job isn't recognized yet — still excludes consumables.
        int selfPrefix = user?.Job is { } jb ? JobClassInfo.BasicSkillCode(jb) / 1_000_000 : -1;
        List<BuffTimeline> timelines = allTimelines
            .Where(t => t.ActorId == _uid && (selfPrefix > 0 ? t.EffectiveJobPrefix == selfPrefix : t.JobPrefix != 0))
            .ToList();
        // Only rebuild (and so redraw) the graph when the underlying data actually changed — Refresh runs every
        // tick, but the per-second series only grows once a second and is frozen while idle/replaying history,
        // so this keeps the Canvas redraw off the hot path (the handoff's 라이브 리프레시 비용 caveat).
        long sig = GraphSignature(series, timelines);
        if (Graph is null || sig != _graphSignature)
        {
            _graphSignature = sig;
            Graph = BuildGraph(series, timelines, report.BattleStart);
        }

        HasGraph = series.Length >= 2;

        // 스킬 타임라인. 그래프와 같은 스냅샷-vs-라이브 분기다: 저장된 전투는 얼려 둔 목록을 쓴다
        // (시전 저장소는 저장 직후 잘리므로 다시 계산하면 기록 재생에서 탭이 통째로 빈다).
        IReadOnlyList<SkillCastRow> casts = report.SkillCasts.Count > 0
            ? report.SkillCasts.GetValueOrDefault(_uid) ?? []
            : _calc.GetSkillCasts(_uid, report.BattleStart, report.BattleEnd);

        long battleStart = report.BattleStart;
        _rebuildTimeline = () => RebuildTimeline(
            SkillTimelineModel.Compute(FilterCasts(casts), battleStart));

        long castSig = casts.Count == 0 ? 0L : casts.Count * 1_000_003L + casts[^1].TimestampMs;
        if (castSig != _timelineSignature)
        {
            _timelineSignature = castSig;
            _rebuildTimeline();
        }

        HasTimeline = casts.Count >= 2;
        HasCooldownStarts = casts.Any(c => c.StartsCooldown);
    }

    private IReadOnlyList<SkillCastRow> FilterCasts(IReadOnlyList<SkillCastRow> casts) =>
        _timelineCooldownOnly ? casts.Where(c => c.StartsCooldown).ToList() : casts;

    /// <summary>타임라인 두 표를 채운다. 시전 목록은 뒤로만 자라므로, 이미 그린 앞부분이 그대로면 꼬리만
    /// 붙인다 — 라이브 전투에서 매 틱 수천 줄을 다시 만들지 않기 위해서다.</summary>
    private void RebuildTimeline(SkillTimelineModel model)
    {
        bool canAppend = model.Casts.Count >= TimelineCasts.Count
            && TimelineCasts.Count > 0
            && TimelineCasts[0].Matches(model.Casts[0]);

        if (!canAppend)
        {
            TimelineCasts.Clear();
            foreach (SkillCastEntry entry in model.Casts)
            {
                TimelineCasts.Add(new SkillCastRowVM(entry));
            }
        }
        else
        {
            for (int i = TimelineCasts.Count; i < model.Casts.Count; i++)
            {
                TimelineCasts.Add(new SkillCastRowVM(model.Casts[i]));
            }
        }

        // 집계 행은 수십 개 수준이고 매 시전마다 값이 바뀌므로 통째로 다시 만든다.
        TimelineSkills.Clear();
        foreach (SkillCastSummaryRow row in model.Skills)
        {
            TimelineSkills.Add(new SkillGapRowVM(row));
        }

        TimelineSummaryText = model.Casts.Count == 0
            ? string.Empty
            : $"총 {model.Casts.Count:N0}회 시전 · 스킬 {model.Skills.Count:N0}종"
              + (model.MedianGapMs is long med ? $" · 시전 간격 중앙값 {med:N0}ms" : string.Empty)
              + (model.MeanGapMs is long avg ? $" (평균 {avg:N0}ms)" : string.Empty);
    }

    private long _graphSignature = long.MinValue;

    // A cheap content hash: total damage (grows on every hit, constant when frozen) + series length + buff/span
    // counts. Enough to notice any change worth redrawing without deep-comparing the model each tick.
    private static long GraphSignature(long[] series, List<BuffTimeline> timelines)
    {
        long sig = series.Length;
        long total = 0;
        foreach (long v in series) total += v;
        sig = sig * 1_000_003L + total;
        sig = sig * 1_000_003L + timelines.Count;
        long spans = 0;
        foreach (BuffTimeline t in timelines) spans += t.Spans.Count;
        return sig * 1_000_003L + spans;
    }

    // Distinct, well-separated hues so each buff lane / legend chip is told apart at a glance (cycled if a fight
    // somehow exceeds the count — capped at MaxGraphBuffs below, which is ≤ this length).
    private static readonly Brush[] GraphPalette =
    [
        Frozen(new SolidColorBrush(C("#FF38BDF8"))), // sky
        Frozen(new SolidColorBrush(C("#FFA78BFA"))), // violet
        Frozen(new SolidColorBrush(C("#FFFBBF24"))), // amber
        Frozen(new SolidColorBrush(C("#FF34D399"))), // emerald
        Frozen(new SolidColorBrush(C("#FFF472B6"))), // pink
        Frozen(new SolidColorBrush(C("#FF2DD4BF"))), // teal
        Frozen(new SolidColorBrush(C("#FFFB923C"))), // orange
        Frozen(new SolidColorBrush(C("#FF818CF8"))), // indigo
        Frozen(new SolidColorBrush(C("#FFA3E635"))), // lime
        Frozen(new SolidColorBrush(C("#FFFB7185"))), // rose
    ];

    private const int MaxGraphBuffs = 8;

    private static DpsGraphModel? BuildGraph(
        IReadOnlyList<long> perSecond, IReadOnlyList<BuffTimeline> timelines, long battleStart)
    {
        if (perSecond.Count == 0) return null;

        long peak = 0;
        foreach (long v in perSecond)
        {
            if (v > peak) peak = v;
        }

        // Highest-uptime buffs first, capped so the lane stack + legend stay readable (a player can carry a long
        // tail of low-value food/scroll/party buffs). The colour is assigned here so the XAML legend chip and the
        // hand-drawn lane share one source of truth.
        List<BuffTimeline> top = timelines
            .Where(t => t.Spans.Count > 0)
            .OrderByDescending(t => t.Spans.Sum(s => s.End - s.Start))
            .Take(MaxGraphBuffs)
            .ToList();

        var buffs = new List<DpsGraphBuff>(top.Count);
        for (int i = 0; i < top.Count; i++)
        {
            BuffTimeline t = top[i];
            var spans = new List<(double StartSec, double EndSec)>(t.Spans.Count);
            foreach ((long start, long end) in t.Spans)
            {
                spans.Add(((start - battleStart) / 1000.0, (end - battleStart) / 1000.0));
            }

            buffs.Add(new DpsGraphBuff(t.Name, JoinIcons.Skill(t.Code), GraphPalette[i % GraphPalette.Length], spans));
        }

        return new DpsGraphModel(perSecond, peak, buffs);
    }

    private void ReconcileSkills(IReadOnlyList<DetailSkillGroup> groups, long combatMs)
    {
        // Preserve expansion of chain groups (unique main code) across live ticks.
        Dictionary<int, bool> expanded = Skills.Where(g => g.HasChildren).ToDictionary(g => g.Code, g => g.IsExpanded);
        Skills.Clear();
        foreach (DetailSkillGroup g in groups)
        {
            var vm = new SkillGroupVM(g, combatMs);
            if (g.HasChildren && expanded.TryGetValue(g.Merged.Code, out bool ex))
            {
                vm.IsExpanded = ex;
            }

            Skills.Add(vm);
        }
    }

    /// <summary>버프 표를 현재 체크박스 상태로 다시 그린다. <see cref="HasBuffs"/>/<see cref="HasPartyBuffs"/>는
    /// <b>필터 전</b> 목록으로 판정한다 — 필터 결과로 탭을 껐다가는 그 탭을 채울 체크박스가 그 안에 갇힌다.</summary>
    private void RebuildBuffs()
    {
        Rebuild(
            Buffs,
            _showPartyBuffs ? _buffSections : _buffSections.Where(s => !s.FromOtherPlayers).ToList());
        HasBuffs = _buffSections.Any(s => s.Rows.Count > 0);
        HasPartyBuffs = _buffSections.Any(s => s.FromOtherPlayers && s.Rows.Count > 0);
    }

    // Sections are flattened into one table: the section label rides along as each row's subtitle
    // ("내 버프" / "그 외" / "남이 준 버프"), except that a 남이 준 버프 row shows its CASTER's name instead —
    // repeating one section label down forty raid rows says nothing, and "who gave me this" is the only new
    // fact that row carries. Boss debuffs come through with an empty label (one caster: this player).
    // Keeping the label per row lets the rows sort as one list, which is how the stats site presents them.
    private static void Rebuild(ObservableCollection<BuffRowVM> target, IReadOnlyList<DetailBuffSection> sections)
    {
        target.Clear();
        foreach (DetailBuffSection s in sections)
        {
            foreach (DetailBuffRow r in s.Rows)
            {
                target.Add(new BuffRowVM(r, r.CasterName ?? s.Label));
            }
        }
    }

    internal static Brush BuffBrush(double rate) => rate >= 80 ? GoodBuff : rate >= 50 ? WarnBuff : BadBuff;

    internal static Brush BuffBarBrush(double rate) => rate >= 80 ? GoodBar : rate >= 50 ? WarnBar : BadBar;

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    // Theme colors may be rgba(...) (ColorConverter can't parse that) -> use ColorString.
    private static Brush ThemeBrush(string value)
    {
        Color color = ColorString.TryParse(value, out ColorRgba c) ? Color.FromArgb(c.A, c.R, c.G, c.B) : Colors.White;
        return Frozen(new SolidColorBrush(color));
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

public sealed class SkillGroupVM : INotifyPropertyChanged
{
    public SkillGroupVM(DetailSkillGroup group, long combatMs)
    {
        Code = group.Merged.Code;
        HasChildren = group.HasChildren;
        Merged = new SkillRowVM(group.Merged, combatMs);
        Children = group.Children.Select(r => new SkillRowVM(r, combatMs)).ToList();
    }

    public int Code { get; }
    public bool HasChildren { get; }
    public SkillRowVM Merged { get; }
    public IReadOnlyList<SkillRowVM> Children { get; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SkillRowVM
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    // DetailModel appends this to a DoT row's name so the old single-column table could tell them apart.
    // The table now carries a 유형 badge, so the suffix would just repeat it.
    private const string DotSuffix = " - 지속";

    public SkillRowVM(DetailSkillRow row, long combatMs)
    {
        Name = row.IsDot && row.Name.EndsWith(DotSuffix, StringComparison.Ordinal)
            ? row.Name[..^DotSuffix.Length]
            : row.Name;
        IsDot = row.IsDot;
        TypeText = row.IsDot ? "지속" : "직접";
        IconSource = JoinIcons.Skill(row.Code);
        HitsText = row.Hits.ToString("N0", Inv);
        CritText = Pct(row.CritPct);
        StrongText = Pct(row.StrongPct);
        PerfectText = Pct(row.PerfectPct);
        BackText = Pct(row.BackPct);
        FrontText = Pct(row.FrontPct);
        ParryText = Pct(row.ParryPct);
        Spec = BuildSpec(row.Spec);
        HasSpec = row.Spec != null;
        DpsText = combatMs > 0 ? MeterFormat.FormatAmount(row.Damage / (combatMs / 1000.0)) : "-";
        AvgText = row.Hits > 0 ? MeterFormat.FormatAmount((double)row.Damage / row.Hits) : "-";
        DamageText = MeterFormat.FormatAmount(row.Damage);
        PercentText = (row.Ratio * 100).ToString("F1", Inv) + "%";
        BarRatio = row.Ratio;
        BarRest = 1.0 - row.Ratio;
        // 남의 효과로 들어온 피해면 시전자 이름표를 붙인다. 없으면 칩을 접는다 — 대부분의 행은 내 스킬이다.
        GrantedByText = row.GrantedBy is { Length: > 0 } g ? g : string.Empty;
        GrantedByVisibility = GrantedByText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>이 행의 피해를 만들어 준 파티원 이름(검성 흡혈의 검 착취 / 치유성 대지의 축복 추가 피해).
    /// 비어 있으면 내 스킬이다.</summary>
    public string GrantedByText { get; }

    public Visibility GrantedByVisibility { get; }

    public string Name { get; }
    public bool IsDot { get; }
    public string TypeText { get; }
    public ImageSource? IconSource { get; }
    public string HitsText { get; }
    public string CritText { get; }
    public string StrongText { get; }
    public string PerfectText { get; }
    public string BackText { get; }
    public string FrontText { get; }
    public string ParryText { get; }
    public string DpsText { get; }
    public string AvgText { get; }
    public string DamageText { get; }
    public string PercentText { get; }
    public double BarRatio { get; }
    public double BarRest { get; }
    public Brush BarBrush => DetailsViewModel.SkillBar;

    /// <summary>Five pips for the skill's specialization (특화) — active slots lit, inactive dim. Empty when
    /// the skill carries no specialization (basic attacks, DoT rows, non-player skills).</summary>
    public IReadOnlyList<SpecPipVM> Spec { get; }

    /// <summary>Whether this row has any specialization data (drives showing the pips vs a "-").</summary>
    public bool HasSpec { get; }

    public Visibility SpecVisibility => HasSpec ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoSpecVisibility => HasSpec ? Visibility.Collapsed : Visibility.Visible;

    private static IReadOnlyList<SpecPipVM> BuildSpec(IReadOnlyList<bool>? spec)
    {
        if (spec == null)
        {
            return Array.Empty<SpecPipVM>();
        }

        var pips = new SpecPipVM[spec.Count];
        for (int i = 0; i < spec.Count; i++)
        {
            pips[i] = new SpecPipVM(spec[i]);
        }

        return pips;
    }

    private static string Pct(int? value) => value.HasValue ? value.Value + "%" : "-";
}

/// <summary>One specialization pip: an active slot glows in the accent color, an inactive one is dim.</summary>
public sealed class SpecPipVM
{
    // Active = emerald (matches the skill damage bar / "output" accent); inactive = a faint gray dot.
    private static readonly Brush Active = Frozen(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));
    private static readonly Brush Inactive = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x9C, 0xA3, 0xAF)));

    public SpecPipVM(bool active) => Fill = active ? Active : Inactive;

    public Brush Fill { get; }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}


public sealed class BuffRowVM
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public BuffRowVM(DetailBuffRow row, string subtitle)
    {
        Name = row.Name;
        Subtitle = subtitle;
        // A proc row reports a COUNT, not an uptime: show "N회" and draw no bar (a % bar would imply a
        // fraction of the fight, which this value is not).
        bool isProc = row.Count is not null;
        RateText = isProc ? row.Count!.Value.ToString("N0", Inv) + "회" : row.Rate.ToString("F1", Inv) + "%";
        BarRatio = isProc ? 0.0 : row.Rate / 100.0;
        BarRest = 1.0 - BarRatio;
        RateBrush = DetailsViewModel.BuffBrush(isProc ? 100.0 : row.Rate);
        BarBrush = DetailsViewModel.BuffBarBrush(row.Rate);
        Description = row.Description;
        IconSource = JoinIcons.Skill(row.Code); // buff/debuff share the skill-icon manifest
        // 시전자의 스킬 레벨. 0(모름)과 1은 칩을 접는다 — 0 은 소모품처럼 레벨 개념이 없는 버프이고,
        // 1 은 레벨이 오르지 않는 고정 효과 버프가 그대로 1 을 실어 보내는 값이라 "Lv.1"을 붙이면 레벨이
        // 낮은 것처럼 읽힌다. 오버레이 배지와 같은 규칙이다(BuffSlotVM.SetLevel).
        LevelText = row.Level > 1 ? "Lv." + row.Level.ToString(Inv) : string.Empty;
        LevelVisibility = row.Level > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"Lv.25" 형태의 시전자 스킬 레벨 칩(레벨을 모르면 빈 문자열).</summary>
    public string LevelText { get; }

    public Visibility LevelVisibility { get; }

    public string Name { get; }

    /// <summary>이 행의 부제. 보통은 섹션 이름("내 버프" / "그 외")이고, 「남이 준 버프」 행에서는 <b>시전자
    /// 이름</b>이다. 보스 디버프는 비어 있다 — 거기 모든 행의 시전자가 이 플레이어 하나라 부제가 매 줄 반복된다.</summary>
    public string Subtitle { get; }

    public Visibility SubtitleVisibility => Subtitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string RateText { get; }
    public double BarRatio { get; }
    public double BarRest { get; }
    public Brush RateBrush { get; }
    public Brush BarBrush { get; }
    public string Description { get; }
    public ImageSource? IconSource { get; }
}

/// <summary>타임라인 한 줄: 시전 1회.</summary>
public sealed class SkillCastRowVM
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public SkillCastRowVM(SkillCastEntry entry)
    {
        OffsetMs = entry.OffsetMs;
        Code = entry.Code;
        TimeText = FormatOffset(entry.OffsetMs);
        Name = entry.Name;
        GapText = entry.GapFromPrevMs is long g ? g.ToString("N0", Inv) + "ms" : "-";
        IconSource = JoinIcons.Skill(entry.Code);
        CooldownMarkVisibility = entry.StartsCooldown ? Visibility.Visible : Visibility.Collapsed;
    }

    internal long OffsetMs { get; }

    internal int Code { get; }

    /// <summary>같은 시전을 가리키는 행인가 — 라이브 갱신에서 "앞부분은 그대로다"를 확인하는 데만 쓴다.</summary>
    internal bool Matches(SkillCastEntry entry) => entry.OffsetMs == OffsetMs && entry.Code == Code;

    /// <summary>전투 시작 기준 경과. 오프너는 음수라 부호를 그대로 보여 준다.</summary>
    public string TimeText { get; }

    public string Name { get; }

    /// <summary>직전 시전과의 간격. 첫 줄은 "-".</summary>
    public string GapText { get; }

    /// <summary>쿨타임을 돌린 발동에만 붙는 점. 서버가 보낸 사실이라 "확실히 나갔다"로 읽어도 된다 —
    /// 다만 점이 없다고 안 나간 것은 아니다(쿨 없는 스킬은 돌릴 쿨이 없다).</summary>
    public Visibility CooldownMarkVisibility { get; }

    public ImageSource? IconSource { get; }

    private static string FormatOffset(long ms)
    {
        string sign = ms < 0 ? "-" : string.Empty;
        long abs = Math.Abs(ms);
        long minutes = abs / 60_000;
        double seconds = (abs % 60_000) / 1000.0;
        return $"{sign}{minutes}:{seconds.ToString("00.0", Inv)}";
    }
}

/// <summary>스킬 한 종류의 사용 간격 집계 한 줄.</summary>
public sealed class SkillGapRowVM
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public SkillGapRowVM(SkillCastSummaryRow row)
    {
        Name = row.Name;
        CountText = row.Count.ToString("N0", Inv);
        // 쿨을 돌린 발동이 하나도 없으면 칸을 비운다 — 0 을 찍으면 "0번 나갔다"로 읽히는데, 쿨타임이 없는
        // 스킬에는 애초에 돌릴 쿨이 없다는 뜻일 뿐이다.
        CooldownCountText = row.CooldownStartCount > 0 ? row.CooldownStartCount.ToString("N0", Inv) : "-";
        MeanText = Ms(row.MeanGapMs);
        MedianText = Ms(row.MedianGapMs);
        MinText = Ms(row.MinGapMs);
        MaxText = Ms(row.MaxGapMs);
        IconSource = JoinIcons.Skill(row.Code);
    }

    public string Name { get; }
    public string CountText { get; }

    /// <summary>그중 쿨타임을 돌린 발동 수. 쿨타임이 있는 스킬이면 이 수가 "진짜 나간 횟수"다.</summary>
    public string CooldownCountText { get; }

    /// <summary>같은 스킬의 연속 시전 사이 평균 간격. 한 번만 쓴 스킬은 "-".</summary>
    public string MeanText { get; }

    public string MedianText { get; }
    public string MinText { get; }
    public string MaxText { get; }
    public ImageSource? IconSource { get; }

    private static string Ms(long? value) => value is long v ? v.ToString("N0", Inv) + "ms" : "-";
}

/// <summary>Render-ready DPS-over-time graph the detail window hand-draws on a Canvas. <see cref="PerSecond"/>
/// is dense damage-per-whole-second from the battle start (index = second offset); <see cref="PeakPerSecond"/>
/// is its max for y-axis scaling/label; <see cref="Buffs"/> is the icon lane.</summary>
public sealed record DpsGraphModel(
    IReadOnlyList<long> PerSecond,
    long PeakPerSecond,
    IReadOnlyList<DpsGraphBuff> Buffs);

/// <summary>One buff on the graph: its name + icon + assigned lane colour (shared by the legend chip and the
/// hand-drawn Gantt lane), and the second-offset spans (from battle start) it was active for.</summary>
public sealed record DpsGraphBuff(
    string Name,
    ImageSource? Icon,
    Brush LaneBrush,
    IReadOnlyList<(double StartSec, double EndSec)> Spans);
