using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using WaffleMeter.Services;

namespace WaffleMeter.App.Core;

/// <summary>
/// Persisted overlay/display settings — the .NET analogue of the React useSettingsStore saveProps
/// layer. Reads/writes the SAME PropertyHandler keys with the SAME string encoding (lowercase
/// "true"/"false" booleans, invariant numbers, raw enum strings) so settings round-trip with any
/// existing settings.properties. Raises INotifyPropertyChanged so the overlay updates live.
/// </summary>
public sealed class MeterSettings : INotifyPropertyChanged
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] DisplayModes = { "dps_percent", "amount_dps_percent", "amount_percent", "amount_full_dps_percent", "amount_full_percent" };
    private static readonly string[] DamageValueModes = { "dps", "total" };
    private static readonly string[] ContributionModes = { "contribution", "entireContribution" };
    private static readonly string[] NameDisplays = { "all", "me_only", "hidden" };
    private static readonly string[] RowDpsMetrics = { "dps", "ndps", "rdps" };
    private static readonly string[] Themes = { "dark", "light" };
    private static readonly string[] CloseActions = { "ask", "tray", "exit" };
    private static readonly string[] CaptureBackends = { "windivert", "npcap" };
    private static readonly string[] TargetInfoDisplayModes = { "hp_full_percent", "hp_percent", "remain_full_percent", "remain_percent", "percent" };
    private static readonly string[] BarStyles = { "fill", "bar", "none" };

    /// <summary>미터 레이아웃 3종. 허용값의 정본은 <see cref="MeterLayout.Ids"/> 하나다 — 여기에 따로
    /// 적으면 두 목록이 조용히 어긋난다(MeterLayoutTests 가 일치를 검사한다).</summary>
    private static readonly string[] MeterLayouts = MeterLayout.Ids;
    /// <summary>던전 티어 장식 강도. "off" = 현행 픽셀 그대로, "static" = 테두리만, "animated" = 상위 2티어 광택.</summary>
    private static readonly string[] TierEffectModes = { "off", "static", "animated" };

    /// <summary>Same three-step shape as <see cref="TierEffectModes"/>, so the two decorations read alike.</summary>
    private static readonly string[] NameFxModes = { "off", "static", "animated" };

    private readonly PropertyHandler _props;

    public MeterSettings(PropertyHandler props)
    {
        _props = props;
        Reload();
    }

    /// <summary>
    /// Re-read every key from the properties file. The constructor is the only other caller — this exists for
    /// the settings import, which writes the RAW stored values straight through
    /// <see cref="PropertyHandler.SetProperty"/> and then needs the model to pick them up.
    /// <para>Why the import cannot just assign the properties instead: <c>GetProperty</c> re-decodes on the way
    /// out (Latin-1 → EUC-KR), so the value a model holds and the value in the file are different strings for
    /// anything non-ASCII. Writing raw and re-reading keeps both sides in the representation each one expects;
    /// assigning raw through the setters would render correctly this session and change after a restart.</para>
    /// <para>Raises <c>PropertyChanged(null)</c> — WPF reads that as "every property", which is exactly right
    /// here and far safer than listing 70-odd names that a future key would silently miss.</para>
    /// </summary>
    // The constructor delegates to this, which the compiler will not follow — without the list every
    // field below reads as "maybe null after construction" and buries real warnings under 25 fake ones.
    [MemberNotNull(
        nameof(_aetherCharacterNames), nameof(_aetherLastValue), nameof(_aetherPerCharacter), nameof(_barStyle),
        nameof(_buffUiHidden), nameof(_buffUiObserved), nameof(_buffUiPinned), nameof(_buffUiPresets),
        nameof(_buffUiSortMode), nameof(_buffUiTextColor), nameof(_buffUiVoice), nameof(_captureBackend),
        nameof(_cooldownUiTextColor), nameof(_cooldownUiPresets),
        nameof(_closeAction), nameof(_contributionMode), nameof(_customAlarms), nameof(_damageValueMode),
        nameof(_displayMode), nameof(_fieldBossDisabled), nameof(_fontFamily), nameof(_nameDisplay),
        nameof(_meterLayoutId), nameof(_nameFxMode), nameof(_overlayTheme), nameof(_rowDpsMetric), nameof(_targetInfoDisplayMode),
        nameof(_tierEffects),
        nameof(_ttsVoice), nameof(_weeklyContentClears), nameof(_abyssCorridors), nameof(_abyssArtifacts))]
    public void Reload()
    {
        _displayMode = ReadEnum("displayMode", "dps_percent", DisplayModes);
        _damageValueMode = ReadEnum("damageValueMode", "dps", DamageValueModes);
        _rowDpsMetric = ReadEnum("rowDpsMetric", "dps", RowDpsMetrics);
        _contributionMode = ReadEnum("contributionMode", "contribution", ContributionModes);
        _nameDisplay = ReadEnum("nameDisplay", "all", NameDisplays);
        _overlayTheme = ReadEnum("overlayTheme", "dark", Themes);
        _closeAction = ReadEnum("closeAction", "ask", CloseActions);
        _fontFamily = _props.GetProperty("fontFamily") ?? "NEXON Lv2 Gothic Medium";
        _rowHeight = ReadInt("rowHeight", 36);
        _bossSlotScalePercent = ReadInt("bossSlotScale", MeterLayout.BossScaleDefault);
        _meterOpacity = ReadDouble("meterOpacity", 0.4);
        _isMinimal = ReadBool("isMinimal", false);
        _showCombatTimerInMinimal = ReadBool("showCombatTimerInMinimal", true);
        _showTargetInfoInMinimal = ReadBool("showTargetInfoInMinimal", true);
        _showServerTag = ReadBool("showServerTag", true);
        _tierShow = ReadBool("tier.show", true);
        _tierEffects = ReadEnum("tier.effects", "static", TierEffectModes);
        _tierShowOthers = ReadBool("tier.showOthers", true);
        _tierShowSelfChip = ReadBool("tier.showSelfChip", true);
        _nameFxMode = ReadEnum("nameFx.mode", "animated", NameFxModes);
        _nameFxShowSelf = ReadBool("nameFx.showSelf", true);
        _nameFxShowOthers = ReadBool("nameFx.showOthers", true);
        _nameFxSpeedPercent = ReadInt("nameFx.speedPercent", 100);
        _nameFxBrightnessPercent = ReadInt("nameFx.brightnessPercent", 100);
        _nameFxGauge = ReadBool("nameFx.gauge", true);
        _showJoinPanel = ReadBool("showJoinPanel", true);
        _showPreCombatRoster = ReadBool("showPreCombatRoster", true);
        _recordReplay = ReadBool("replay.recordMovement", false);
        _multiMonitorMode = ReadBool("multiMonitorMode", false);
        _taskbarMode = ReadBool("taskbarMode", false);
        _captureBackend = ReadEnum("captureBackend", "windivert", CaptureBackends);
        _targetInfoDisplayMode = ReadEnum("targetInfoDisplayMode", "hp_full_percent", TargetInfoDisplayModes);
        _barStyle = ReadEnum("barStyle", "fill", BarStyles);
        _meterLayoutId = ReadEnum("meterLayout", MeterLayout.DefaultId, MeterLayouts);
        _splitUiMode = ReadBool("splitUiMode", false);
        _shugoAlarmEnabled = ReadBool("alarms.shugoEnabled", false);
        _shugoLead10 = ReadBool("alarms.shugoLead10", false);
        _shugoLead5 = ReadBool("alarms.shugoLead5", true);
        _shugoLead1 = ReadBool("alarms.shugoLead1", false);
        _shugoLeadStart = ReadBool("alarms.shugoStart", true);
        _alarmSoundEnabled = ReadBool("alarms.soundEnabled", true);
        _ttsEnabled = ReadBool("alarms.ttsEnabled", false);
        _ttsVoice = ReadEnum("alarms.ttsVoice", BakedVoicePack.Wasuni, BakedVoicePack.All);
        _alarmVolume = ReadDouble("alarms.volume", 0.5);
        _customAlarms = CustomAlarmCodec.Decode(_props.GetProperty("alarms.custom")).ToList();
        _fieldBossAlarmEnabled = ReadBool("alarms.fieldBossEnabled", false);
        _fieldBossLead5 = ReadBool("alarms.fieldBossLead5", false);
        _fieldBossLead10 = ReadBool("alarms.fieldBossLead10", true);
        _fieldBossLead30 = ReadBool("alarms.fieldBossLead30", false);
        _fieldBossAlarmMuteInCombat = ReadBool("alarms.fieldBossMuteInCombat", false);
        _groggyAlarmEnabled = ReadBool("alarms.groggyEnabled", false);
        _kairaAlarmEnabled = ReadBool("alarms.kairaEnabled", false);
        _kairaLead10 = ReadBool("alarms.kairaLead10", true);
        _kairaLead5 = ReadBool("alarms.kairaLead5", false);
        _kairaLead1 = ReadBool("alarms.kairaLead1", false);
        _fieldBossDisabled = _props.GetProperty("alarms.fieldBossDisabled") ?? "";
        _refreshIntervalMs = ReadInt("refreshIntervalMs", 500);
        _maxVisibleRows = ReadInt("maxVisibleRows", 10);
        _lowSpecMode = ReadBool("lowSpecMode", false);
        _showAetherStatus = ReadBool("showAetherStatus", true);
        _showLatencyIndicator = ReadBool("showLatencyIndicator", false);
        _vrrCompatMode = ReadBool("vrrCompatMode", true);
        // gameOpt.includeAdvanced: 은퇴(2026-08-08) — 저장된 값은 읽지 않고 그대로 둔다(무해한 고아 키).
        _meterScalePercent = ReadInt("meterScalePercent", 100);
        _showBuffUi = ReadBool("buffUi.show", false);
        _buffUiIconSize = ReadInt("buffUi.iconSize", 40);
        _buffUiTextColor = _props.GetProperty("buffUi.textColor") ?? "#FFFFFF";
        _buffUiTransparent = ReadBool("buffUi.transparent", true);
        _buffTtsOnStart = ReadBool("buffUi.ttsOnStart", false);
        _buffTtsOnEnd = ReadBool("buffUi.ttsOnEnd", false);
        _buffEndWarning3s = ReadBool("buffUi.endWarning3s", false);
        _buffUiGrayOnCooldown = ReadBool("buffUi.grayOnCooldown", false);
        _buffUiShowLevel = ReadBool("buffUi.showLevel", true);
        _showOtherPlayerBuffs = ReadBool("buffUi.showOther", true);
        _detailShowPartyBuffs = ReadBool("detail.showPartyBuffs", false);
        _detailTimelineCooldownOnly = ReadBool("detail.timelineCooldownOnly", false);
        _buffUiHidden = _props.GetProperty("buffUi.hidden") ?? "";
        _buffUiObserved = _props.GetProperty("buffUi.observed") ?? "";
        _buffUiVoice = _props.GetProperty("buffUi.voice") ?? "";
        _buffUiSortMode = _props.GetProperty("buffUi.sortMode") ?? BuffOverlayOrder.Applied;
        _buffUiPinned = _props.GetProperty("buffUi.pinned") ?? "";
        _buffUiDefaultsApplied = ReadBool("buffUi.defaultsApplied", false);
        _buffUiPresets = _props.GetProperty("buffUi.presets") ?? "";
        _showCooldownUi = ReadBool("cooldownUi.show", false);
        _cooldownUiIconSize = ReadInt("cooldownUi.iconSize", 40);
        _cooldownUiTextColor = _props.GetProperty("cooldownUi.textColor") ?? "#FFFFFF";
        _cooldownUiTransparent = ReadBool("cooldownUi.transparent", true);
        _cooldownUiPerRow = ReadInt("cooldownUi.perRow", 8);
        _cooldownUiPresets = _props.GetProperty("cooldownUi.presets") ?? "";
        _aetherLastValue = _props.GetProperty("aether.lastValue") ?? "";
        _aetherPerCharacter = _props.GetProperty("aether.perCharacter") ?? "";
        _aetherCharacterNames = _props.GetProperty("aether.characterNames") ?? "";
        _weeklyContentClears = _props.GetProperty("content.weeklyClears") ?? "";
        _abyssCorridors = _props.GetProperty("content.abyssCorridors") ?? "";
        _abyssArtifacts = _props.GetProperty("content.abyssArtifacts") ?? "";
        _dummyTestMode = ReadBool("dummy.testMode", false);
        _dummyDurationSec = ReadInt("dummy.durationSeconds", 60);
        _patchNotesLastShownVersion = _props.GetProperty("patchNotes.lastShownVersion") ?? "";
        OnPropertyChanged(string.Empty);
    }

    private string _displayMode;
    public string DisplayMode { get => _displayMode; set => SetProp(ref _displayMode, "displayMode", value); }

    private string _damageValueMode;
    public string DamageValueMode { get => _damageValueMode; set => SetProp(ref _damageValueMode, "damageValueMode", value); }

    private string _rowDpsMetric;
    /// <summary>미터 행에 찍히는 초당 피해량의 종류: <c>dps</c>(실제 낸 피해, 기본) / <c>ndps</c>(남이 걸어준
    /// 버프 몫을 걷어낸 값) / <c>rdps</c>(nDPS + 내가 남에게 얹어준 몫). 표시 문자열뿐 아니라 <b>정렬과 게이지
    /// 길이도</b> 같이 따라간다 — 숫자만 바꾸고 순서를 그대로 두면 3등 행이 1등 숫자를 달고 있게 된다.
    /// <para>"총 피해량"(<see cref="DamageValueMode"/>) 모드와는 별개 축이다. 총량 모드에서는 이 선택이 적용되지
    /// 않는다 — 누적 피해량은 정의상 실제로 낸 피해이고, 거기에 정규화를 섞으면 두 축이 한 숫자에서 만난다.</para></summary>
    public string RowDpsMetric { get => _rowDpsMetric; set => SetProp(ref _rowDpsMetric, "rowDpsMetric", value); }

    private string _contributionMode;
    public string ContributionMode { get => _contributionMode; set => SetProp(ref _contributionMode, "contributionMode", value); }

    private string _nameDisplay;
    public string NameDisplay { get => _nameDisplay; set => SetProp(ref _nameDisplay, "nameDisplay", value); }

    private string _overlayTheme;
    public string OverlayTheme { get => _overlayTheme; set => SetProp(ref _overlayTheme, "overlayTheme", value); }

    private string _closeAction;
    public string CloseAction { get => _closeAction; set => SetProp(ref _closeAction, "closeAction", value); }

    private string _fontFamily;
    public string FontFamily { get => _fontFamily; set => SetProp(ref _fontFamily, "fontFamily", value); }

    private int _rowHeight;
    public int RowHeight { get => _rowHeight; set => SetInt(ref _rowHeight, "rowHeight", value); }

    private int _bossSlotScalePercent;
    /// <summary>보스칸(보스 HP 칸) 높이 배율(퍼센트, 기본 100). 칸 높이와 칸 안의 글자·게이지가 같은
    /// 비율로 함께 움직인다. px 가 아니라 퍼센트로 저장하는 이유: 100% 기준 높이가 레이아웃마다 다르고
    /// (전장 104 / 계기판 52 / 무대 70) px 로 저장하면 레이아웃을 바꾼 순간 같은 숫자가 다른 뜻이 된다
    /// — 계기판에서 104px 는 빈 판이고 전장에서 52px 는 잘린 판이다.
    /// getter 도 클램프하는 이유: 남의 공유코드는 SettingsBundleApplier 가 값 검증 없이 그대로 심는다.</summary>
    public int BossSlotScalePercent
    {
        get => Math.Clamp(_bossSlotScalePercent, MeterLayout.BossScaleMin, MeterLayout.BossScaleMax);
        set => SetInt(ref _bossSlotScalePercent, "bossSlotScale",
            Math.Clamp(value, MeterLayout.BossScaleMin, MeterLayout.BossScaleMax));
    }

    private double _meterOpacity;
    public double MeterOpacity { get => _meterOpacity; set => SetDouble(ref _meterOpacity, "meterOpacity", value); }

    private int _meterScalePercent;
    /// <summary>미터 전체 크기 배율(퍼센트, 기본 100). 오버레이 루트 LayoutTransform로 글자·행·여백까지
    /// 균일 확대/축소한다(해상도에 맞춘 체감 크기 조절). 폭은 사용자가 계속 드래그로 조절.</summary>
    public int MeterScalePercent { get => _meterScalePercent; set => SetInt(ref _meterScalePercent, "meterScalePercent", value); }

    private bool _isMinimal;
    public bool IsMinimal { get => _isMinimal; set => SetBool(ref _isMinimal, "isMinimal", value); }

    private bool _showCombatTimerInMinimal;
    public bool ShowCombatTimerInMinimal { get => _showCombatTimerInMinimal; set => SetBool(ref _showCombatTimerInMinimal, "showCombatTimerInMinimal", value); }

    private bool _showTargetInfoInMinimal;
    public bool ShowTargetInfoInMinimal { get => _showTargetInfoInMinimal; set => SetBool(ref _showTargetInfoInMinimal, "showTargetInfoInMinimal", value); }

    private bool _showServerTag;
    /// <summary>Show the abbreviated server label "[xx]" next to each player nickname in the meter rows.</summary>
    public bool ShowServerTag { get => _showServerTag; set => SetBool(ref _showServerTag, "showServerTag", value); }

    private bool _tierShow;
    /// <summary>Master switch for the 던전 티어 decoration (badge rings + 상위 X.X% chip).</summary>
    public bool TierShow { get => _tierShow; set => SetBool(ref _tierShow, "tier.show", value); }

    private string _tierEffects;
    /// <summary>"off" = no decoration at all (pixel-identical to pre-tier), "static" = rings only,
    /// "animated" = rings + the 챌린저/마스터 sheen. LowSpecMode pins this down to "static".</summary>
    public string TierEffects { get => _tierEffects; set => SetProp(ref _tierEffects, "tier.effects", value); }

    private bool _tierShowOthers;
    /// <summary>Decorate party members' rows too (ring + outline + their own 이번 전투 상위 X.X% chip). Off
    /// leaves everyone but you undecorated.</summary>
    public bool TierShowOthers { get => _tierShowOthers; set => SetBool(ref _tierShowOthers, "tier.showOthers", value); }

    private string _nameFxMode;
    /// <summary>후원자·랭커 닉네임 연출. "off" = 도입 전과 픽셀 동일, "static" = 움직임 없는 색만,
    /// "animated" = 흐르는 연출. LowSpecMode 는 이 값과 무관하게 강제로 끈다 — 게임 위에 뜨는 투명 창이라
    /// 움직이는 픽셀은 전부 CPU 합성이다.</summary>
    public string NameFxMode { get => _nameFxMode; set => SetProp(ref _nameFxMode, "nameFx.mode", value); }

    private bool _nameFxShowSelf;
    /// <summary>내 캐릭터 행에도 연출을 그린다.</summary>
    public bool NameFxShowSelf { get => _nameFxShowSelf; set => SetBool(ref _nameFxShowSelf, "nameFx.showSelf", value); }

    private bool _nameFxShowOthers;
    /// <summary>파티원 행에도 연출을 그린다. 끄면 내 것만 보인다.</summary>
    public bool NameFxShowOthers { get => _nameFxShowOthers; set => SetBool(ref _nameFxShowOthers, "nameFx.showOthers", value); }

    private int _nameFxSpeedPercent;
    /// <summary>50~200. 타이머 주기가 아니라 틱당 진행량을 바꾼다 — 주기를 줄이면 비용이 같이 늘지만
    /// 걸음을 키우는 건 공짜다.</summary>
    public int NameFxSpeedPercent
    {
        get => Math.Clamp(_nameFxSpeedPercent, 50, 200);
        set => SetInt(ref _nameFxSpeedPercent, "nameFx.speedPercent", Math.Clamp(value, 50, 200));
    }

    private int _nameFxBrightnessPercent;
    /// <summary>70~130. HSL 밝기로 적용한다(RGB 곱은 위쪽에서 색이 바랜다).</summary>
    public int NameFxBrightnessPercent
    {
        get => Math.Clamp(_nameFxBrightnessPercent, 70, 130);
        set => SetInt(ref _nameFxBrightnessPercent, "nameFx.brightnessPercent", Math.Clamp(value, 70, 130));
    }

    private bool _nameFxGauge;
    /// <summary>랭커 전용 DPS 게이지 스킨을 그린다. 부여 자체가 랭커에게만 나가므로 이 스위치는 "받았을 때
    /// 쓸 것인가"만 정한다. 끄면 게이지는 기존 기여도·직업 색으로 돌아간다.</summary>
    public bool NameFxGauge { get => _nameFxGauge; set => SetBool(ref _nameFxGauge, "nameFx.gauge", value); }

    private bool _tierShowSelfChip;
    /// <summary>Show the footer summary chip ("챌린저 · 상위 0.7%") next to the combat timer. The per-row
    /// percentile chips are governed by <see cref="TierShow"/>/<see cref="TierShowOthers"/> instead.</summary>
    public bool TierShowSelfChip { get => _tierShowSelfChip; set => SetBool(ref _tierShowSelfChip, "tier.showSelfChip", value); }

    private string _targetInfoDisplayMode;
    public string TargetInfoDisplayMode { get => _targetInfoDisplayMode; set => SetProp(ref _targetInfoDisplayMode, "targetInfoDisplayMode", value); }

    private string _barStyle;
    /// <summary>Damage gauge form: "fill" (proportional cell fill), "bar" (thin bottom bar), "none".</summary>
    public string BarStyle { get => _barStyle; set => SetProp(ref _barStyle, "barStyle", value); }

    private string _meterLayoutId;

    /// <summary>
    /// 미터 레이아웃(생김새). 분리모드와 **직교**한다 — 도킹이든 분리든 항상 적용된다.
    /// 기본값은 전장(현행 행과 수치가 같아, 아무것도 고르지 않은 사용자는 지금과 같은 화면을 본다).
    /// </summary>
    public string MeterLayoutId { get => _meterLayoutId; set => SetProp(ref _meterLayoutId, "meterLayout", value); }

    private bool _splitUiMode;

    /// <summary>
    /// UI 분리모드. 켜면 보스칸과 미터 행이 독립 창으로 떨어져 나가고 헤더·푸터가 사라진다.
    /// 레이아웃과 직교하므로 3×2 여섯 조합이 전부 성립해야 한다.
    /// </summary>
    public bool SplitUiMode { get => _splitUiMode; set => SetBool(ref _splitUiMode, "splitUiMode", value); }

    private bool _multiMonitorMode;
    public bool MultiMonitorMode { get => _multiMonitorMode; set => SetBool(ref _multiMonitorMode, "multiMonitorMode", value); }

    private bool _showJoinPanel;
    /// <summary>Auto-show the party join-request panel when a request arrives. Off = stay hidden; the
    /// header 파티 신청 button still opens it manually.</summary>
    public bool ShowJoinPanel { get => _showJoinPanel; set => SetBool(ref _showJoinPanel, "showJoinPanel", value); }

    private bool _recordReplay;

    /// <summary>Record a positional replay for every battle (BETA). Off = the movement tap never runs, so
    /// nothing is captured and nothing is written to disk. Reads/writes the same key the engine gate uses,
    /// so the toggle IS the feature switch.</summary>
    public bool RecordReplay { get => _recordReplay; set => SetBool(ref _recordReplay, "replay.recordMovement", value); }

    private bool _showPreCombatRoster;
    /// <summary>Show the party roster as idle (0-DPS) rows before combat starts, so party members appear on
    /// dungeon entry instead of the "전투 대기 중" placeholder. Only shown while there is no live combat
    /// data; the moment any damage lands the normal combat rows take over.</summary>
    public bool ShowPreCombatRoster { get => _showPreCombatRoster; set => SetBool(ref _showPreCombatRoster, "showPreCombatRoster", value); }

    private bool _taskbarMode;
    public bool TaskbarMode { get => _taskbarMode; set => SetBool(ref _taskbarMode, "taskbarMode", value); }

    private string _captureBackend;
    public string CaptureBackend { get => _captureBackend; set => SetProp(ref _captureBackend, "captureBackend", value); }

    // ---- alarms (슈고 페스타 reminder) ----
    private bool _shugoAlarmEnabled;
    /// <summary>Master toggle for the 슈고 페스타 (top-of-hour event) reminder alarm.</summary>
    public bool ShugoAlarmEnabled { get => _shugoAlarmEnabled; set => SetBool(ref _shugoAlarmEnabled, "alarms.shugoEnabled", value); }

    private bool _shugoLead10;
    public bool ShugoLead10 { get => _shugoLead10; set => SetBool(ref _shugoLead10, "alarms.shugoLead10", value); }

    private bool _shugoLead5;
    public bool ShugoLead5 { get => _shugoLead5; set => SetBool(ref _shugoLead5, "alarms.shugoLead5", value); }

    private bool _shugoLead1;
    public bool ShugoLead1 { get => _shugoLead1; set => SetBool(ref _shugoLead1, "alarms.shugoLead1", value); }

    private bool _shugoLeadStart;
    public bool ShugoLeadStart { get => _shugoLeadStart; set => SetBool(ref _shugoLeadStart, "alarms.shugoStart", value); }

    private bool _alarmSoundEnabled;
    public bool AlarmSoundEnabled { get => _alarmSoundEnabled; set => SetBool(ref _alarmSoundEnabled, "alarms.soundEnabled", value); }

    private bool _ttsEnabled;
    /// <summary>Speak alerts with an online Korean neural voice instead of (or before falling back to) the
    /// chime. Opt-in: the voice endpoint is unofficial and every failure degrades to the local sound.</summary>
    public bool TtsEnabled { get => _ttsEnabled; set => SetBool(ref _ttsEnabled, "alarms.ttsEnabled", value); }

    private string _ttsVoice;
    /// <summary>Which shipped voice pack reads the alerts. See <see cref="BakedVoicePack"/>.</summary>
    public string TtsVoice { get => _ttsVoice; set => SetProp(ref _ttsVoice, "alarms.ttsVoice", value); }

    private double _alarmVolume;
    public double AlarmVolume { get => _alarmVolume; set => SetDouble(ref _alarmVolume, "alarms.volume", value); }

    // ---- field-boss respawn reminder ----
    private bool _fieldBossAlarmEnabled;
    /// <summary>Master toggle for the field-boss respawn-timer reminder (driven by the 0x9101 broadcast).</summary>
    public bool FieldBossAlarmEnabled { get => _fieldBossAlarmEnabled; set => SetBool(ref _fieldBossAlarmEnabled, "alarms.fieldBossEnabled", value); }

    private bool _fieldBossLead5;
    public bool FieldBossLead5 { get => _fieldBossLead5; set => SetBool(ref _fieldBossLead5, "alarms.fieldBossLead5", value); }

    private bool _fieldBossLead10;
    public bool FieldBossLead10 { get => _fieldBossLead10; set => SetBool(ref _fieldBossLead10, "alarms.fieldBossLead10", value); }

    private bool _fieldBossLead30;
    public bool FieldBossLead30 { get => _fieldBossLead30; set => SetBool(ref _fieldBossLead30, "alarms.fieldBossLead30", value); }

    /// <summary>The enabled field-boss lead minutes (empty = none).</summary>
    public IReadOnlyCollection<int> FieldBossLeads
    {
        get
        {
            var s = new HashSet<int>();
            if (_fieldBossLead5) s.Add(5);
            if (_fieldBossLead10) s.Add(10);
            if (_fieldBossLead30) s.Add(30);
            return s;
        }
    }

    // ---- 감시자 카이라: its own clock-based reminder (see KairaAlarm for why it is not a picker row) ----
    private bool _kairaAlarmEnabled;
    /// <summary>Master toggle for the 감시자 카이라 출현 알림 (KST 0시 기준 4시간 격자).</summary>
    public bool KairaAlarmEnabled { get => _kairaAlarmEnabled; set => SetBool(ref _kairaAlarmEnabled, "alarms.kairaEnabled", value); }

    private bool _kairaLead10;
    public bool KairaLead10 { get => _kairaLead10; set => SetBool(ref _kairaLead10, "alarms.kairaLead10", value); }

    private bool _kairaLead5;
    public bool KairaLead5 { get => _kairaLead5; set => SetBool(ref _kairaLead5, "alarms.kairaLead5", value); }

    private bool _kairaLead1;
    public bool KairaLead1 { get => _kairaLead1; set => SetBool(ref _kairaLead1, "alarms.kairaLead1", value); }

    private bool _fieldBossAlarmMuteInCombat;
    /// <summary>Suppress the field-boss reminder while the meter is recording an active combat (so a dungeon
    /// fight isn't interrupted by a respawn toast). A suppressed alert can still fire once the fight ends if
    /// it's still inside its lead window.</summary>
    public bool FieldBossAlarmMuteInCombat { get => _fieldBossAlarmMuteInCombat; set => SetBool(ref _fieldBossAlarmMuteInCombat, "alarms.fieldBossMuteInCombat", value); }

    private bool _groggyAlarmEnabled;
    /// <summary>현재 타깃 보스의 무력화(그로기) 게이지가 <b>잔여 20%</b>로 내려오면 "그로기 임박"을 읽어 준다.
    /// 사이클당 한 번이고, 게이지가 만충으로 리필되면 다음 사이클에 다시 울린다.
    /// <para>기본 OFF. 켜기 전에 알아 둘 것 — 그로기 방송(0x E005)이 <b>모든 전투에 오지는 않는다</b>.
    /// 원정/초월/성역 전투 단위 커버리지가 92.9%이고, 제때 울리는 비율까지 곱하면 실제 체감은 약 76%다
    /// (원정 87% · 초월 72% · 성역 32%). 성역이 낮은 건 게이지가 거의 안 깎이고 바로 터지는 즉발 그로기가
    /// 그쪽에 몰려 있기 때문이라 임계값으로는 해결되지 않는다.</para></summary>
    public bool GroggyAlarmEnabled { get => _groggyAlarmEnabled; set => SetBool(ref _groggyAlarmEnabled, "alarms.groggyEnabled", value); }

    private string _fieldBossDisabled;
    /// <summary>Comma-separated boss codes the user unchecked in the boss picker; the reminder skips these.
    /// Empty = alert for every boss.</summary>
    public string FieldBossDisabled { get => _fieldBossDisabled; set => SetProp(ref _fieldBossDisabled, "alarms.fieldBossDisabled", value); }

    /// <summary>The disabled boss-code set (parsed from <see cref="FieldBossDisabled"/>).</summary>
    public HashSet<int> FieldBossDisabledCodes => ParseCodeSet(_fieldBossDisabled);

    private List<CustomAlarm> _customAlarms;
    /// <summary>User-defined recurring reminders. Persisted as one Base64(JSON) value (alarms.custom).</summary>
    public IReadOnlyList<CustomAlarm> CustomAlarms
    {
        get => _customAlarms;
        set
        {
            _customAlarms = value.ToList();
            _props.SetProperty("alarms.custom", CustomAlarmCodec.Encode(_customAlarms));
            OnPropertyChanged();
        }
    }

    // ---- display performance (refresh rate / row cap / low-spec) ----
    private int _refreshIntervalMs;
    /// <summary>How often (ms) the meter recomputes + repaints the report. Larger = less CPU/UI churn during
    /// combat, at the cost of coarser live DPS. Clamped to [100, 1000] where it is consumed.</summary>
    public int RefreshIntervalMs { get => _refreshIntervalMs; set => SetInt(ref _refreshIntervalMs, "refreshIntervalMs", value); }

    private int _maxVisibleRows;
    /// <summary>How many dealer rows the meter shows (1-10). Self is always shown even below the cap. All
    /// participants stay tracked; this only caps the display.</summary>
    public int MaxVisibleRows { get => _maxVisibleRows; set => SetInt(ref _maxVisibleRows, "maxVisibleRows", value); }

    private bool _lowSpecMode;
    /// <summary>Frame-drop relief: pins the refresh interval to a low-churn value and force-disables any
    /// display-only embellishments, prioritizing the game's frame rate. See <see cref="EffectiveRefreshIntervalMs"/>.</summary>
    public bool LowSpecMode { get => _lowSpecMode; set => SetBool(ref _lowSpecMode, "lowSpecMode", value); }

    /// <summary>The refresh interval actually applied: low-spec pins it to 500 ms (ignoring the slider);
    /// otherwise the slider value clamped to [100, 1000].</summary>
    public int EffectiveRefreshIntervalMs => _lowSpecMode ? 500 : Math.Clamp(_refreshIntervalMs, 100, 1000);

    /// <summary>The row cap actually applied, clamped to [1, 10].</summary>
    public int EffectiveMaxVisibleRows => Math.Clamp(_maxVisibleRows, 1, 10);

    /// <summary>실제 표시 상한. 사용자가 고른 인원의 <b>1.5배</b>다(10 → 15, 8 → 12).
    /// <para>설정값을 정원과 똑같이 두면 여유가 0이라, 낯선 사람·소환수 주인 팬텀·placeholder가 한 줄만 끼어도
    /// 실제 파티원 한 명이 잘려 나간다. 잘리는 대상이 순위 변동에 따라 바뀌어 "간헐적으로 한 명씩 안 보인다"로
    /// 체감된다. 사용자가 고른 값은 "보고 싶은 인원"이고 이 여유는 그걸 지키기 위한 완충이다.</para></summary>
    public int DisplayRowCap => (int)Math.Ceiling(EffectiveMaxVisibleRows * 1.5);

    private bool _showAetherStatus;
    /// <summary>Show the aether (오드) balance badge next to the recognized character.</summary>
    public bool ShowAetherStatus { get => _showAetherStatus; set => SetBool(ref _showAetherStatus, "showAetherStatus", value); }

    private bool _showLatencyIndicator;
    /// <summary>Show the passive server-latency (ping) badge next to the recognized character.</summary>
    public bool ShowLatencyIndicator { get => _showLatencyIndicator; set => SetBool(ref _showLatencyIndicator, "showLatencyIndicator", value); }

    private bool _vrrCompatMode;
    /// <summary>FreeSync/G-Sync compatibility: render the overlay in software (default) so a GPU-composited
    /// transparent window can't disturb a variable-refresh display. Read at startup (process-global) — a
    /// change needs an app restart.</summary>
    public bool VrrCompatMode { get => _vrrCompatMode; set => SetBool(ref _vrrCompatMode, "vrrCompatMode", value); }

    // ---- combat-assist overlay (live buff / cooldown slots) ----
    private bool _showBuffUi;
    /// <summary>Show the combat-assist overlay: a small window of the local player's active buff slots
    /// (remaining time), separate from the meter.</summary>
    public bool ShowBuffUi { get => _showBuffUi; set => SetBool(ref _showBuffUi, "buffUi.show", value); }

    private int _buffUiIconSize;
    /// <summary>Buff overlay icon size in px; drives a uniform scale of the whole slot. 40px = 100%(설계 기준),
    /// 설정의 배율 슬라이더 80~200% = 32~80px. 저장 단위는 px 로 고정한다 — 이 키는 디자인 공유코드
    /// (SettingsKeyCatalog)와 프리셋 blob(BuffPreset.IconSize)에 실려 구/신 빌드를 오가므로, 퍼센트로 바꾸면
    /// 구버전이 200을 200px 로, 신버전이 34를 34% 로 읽는 양방향 무음 오독이 된다. 퍼센트는 설정 뷰모델에만 있다.
    /// getter 도 클램프하는 이유: 남의 공유코드는 SettingsBundleApplier 가 값 검증 없이 그대로 심는다.</summary>
    public int BuffUiIconSize
    {
        get => Math.Clamp(_buffUiIconSize, 32, 80);
        set => SetInt(ref _buffUiIconSize, "buffUi.iconSize", Math.Clamp(value, 32, 80));
    }

    private string _buffUiTextColor;
    /// <summary>Buff overlay countdown-text color (hex). White by default; changeable since white can be hard
    /// to read against a bright game scene with the transparent background.</summary>
    public string BuffUiTextColor { get => _buffUiTextColor; set => SetProp(ref _buffUiTextColor, "buffUi.textColor", value); }

    private bool _buffUiTransparent;
    /// <summary>Transparent background (default): the overlay is just floating icons, invisible when empty.
    /// Turn OFF to show a panel background + border + placeholder so the window can be located and dragged
    /// even before any buff is active.</summary>
    public bool BuffUiTransparent { get => _buffUiTransparent; set => SetBool(ref _buffUiTransparent, "buffUi.transparent", value); }

    private bool _buffTtsOnStart;
    /// <summary>Speak "이름 온" when a tracked buff starts.</summary>
    public bool BuffTtsOnStart { get => _buffTtsOnStart; set => SetBool(ref _buffTtsOnStart, "buffUi.ttsOnStart", value); }

    private bool _buffTtsOnEnd;
    /// <summary>Speak "이름 오프" shortly before a tracked buff ends (a lead compensates for TTS latency).</summary>
    public bool BuffTtsOnEnd { get => _buffTtsOnEnd; set => SetBool(ref _buffTtsOnEnd, "buffUi.ttsOnEnd", value); }

    private bool _buffEndWarning3s;
    /// <summary>버프가 끝나기 <b>3초 전</b>에 미리 알린다 — 오버레이 아이콘이 점멸하고, 음성을 켠 버프라면
    /// "이름 오프 예정"을 읽어 준다.
    /// <para>켜면 <see cref="BuffTtsOnEnd"/>의 만료 직전 "이름 오프"를 <b>대체한다</b>(둘 다 발화하면 같은 버프를
    /// 2.8초 간격으로 두 번 말하게 된다). 끄면 종전 동작 그대로다.</para>
    /// <para>점멸은 음성 설정과 무관하게, 오버레이에 그려지는 버프면 모두 적용된다.</para></summary>
    public bool BuffEndWarning3s { get => _buffEndWarning3s; set => SetBool(ref _buffEndWarning3s, "buffUi.endWarning3s", value); }

    private bool _buffUiGrayOnCooldown;
    /// <summary>Gray out a buff overlay icon while the skill that grants it is still on cooldown (from the live
    /// 0x3847 cooldown snapshot), so you can see at a glance when it's re-castable. Off by default.</summary>
    public bool BuffUiGrayOnCooldown { get => _buffUiGrayOnCooldown; set => SetBool(ref _buffUiGrayOnCooldown, "buffUi.grayOnCooldown", value); }

    // ---- skill-cooldown overlay (a separate window from the buff overlay) ----
    private bool _showCooldownUi;
    /// <summary>스킬 쿨타임 전용 오버레이 창을 띄운다. 버프 오버레이와 별개의 창이고, 표시 목록은 이번 세션에
    /// 실제로 쓴(또는 서버가 쿨타임을 알려 온) 스킬로 스스로 채워진다 — 클라이언트가 로그인 시점 핫바 스냅샷을
    /// 보내지 않으므로 그것 말고는 정직한 목록이 없다. 기본 꺼짐.</summary>
    public bool ShowCooldownUi { get => _showCooldownUi; set => SetBool(ref _showCooldownUi, "cooldownUi.show", value); }

    private int _cooldownUiIconSize;
    /// <summary>쿨타임 오버레이 아이콘 크기(px). 버프 오버레이와 같은 규약 — 40px 이 100%(설계 기준)이고
    /// 32~80px 이 80~200% 다. <b>저장 단위는 px 로 고정</b>한다(퍼센트로 바꾸면 구·신 빌드가 서로를 조용히
    /// 오독한다). getter 도 클램프하는 이유는 공유코드가 값 검증 없이 심어질 수 있어서다.</summary>
    public int CooldownUiIconSize
    {
        get => Math.Clamp(_cooldownUiIconSize, 32, 80);
        set => SetInt(ref _cooldownUiIconSize, "cooldownUi.iconSize", Math.Clamp(value, 32, 80));
    }

    private string _cooldownUiTextColor;
    /// <summary>남은 시간 글씨 색상(hex). 기본 흰색.</summary>
    public string CooldownUiTextColor { get => _cooldownUiTextColor; set => SetProp(ref _cooldownUiTextColor, "cooldownUi.textColor", value); }

    private bool _cooldownUiTransparent;
    /// <summary>투명 배경(기본). 끄면 패널 배경 + 테두리 + 안내문이 생겨 목록이 비어 있을 때도 창을 찾아
    /// 드래그할 수 있다.</summary>
    public bool CooldownUiTransparent { get => _cooldownUiTransparent; set => SetBool(ref _cooldownUiTransparent, "cooldownUi.transparent", value); }

    private string _cooldownUiPresets;
    /// <summary>쿨타임 프리셋 3슬롯, Base64(JSON) 한 덩어리(<see cref="CooldownPresetCodec"/>). 한글 슬롯
    /// 이름이 설정 읽기의 EUC-KR 재디코드를 통과해야 해서 Base64 다.</summary>
    public string CooldownUiPresets { get => _cooldownUiPresets; set => SetProp(ref _cooldownUiPresets, "cooldownUi.presets", value); }

    private int _cooldownUiPerRow;
    /// <summary>한 줄에 놓을 최대 아이콘 수(4~16). 이 값이 창의 폭 상한을 정하고, 폭 상한이 있어야만
    /// WrapPanel 이 줄을 바꾼다 — 상한이 없으면 WPF 가 작업영역 폭에서 측정을 잘라 넘친 슬롯을 줄바꿈도
    /// 스크롤도 없이 버린다. 쿨타임 오버레이는 슬롯이 20~30개까지 가므로 버프 오버레이보다 훨씬 낮은
    /// 배율에서 그 함정에 닿는다.</summary>
    public int CooldownUiPerRow
    {
        get => Math.Clamp(_cooldownUiPerRow, 4, 16);
        set => SetInt(ref _cooldownUiPerRow, "cooldownUi.perRow", Math.Clamp(value, 4, 16));
    }

    private bool _buffUiShowLevel;
    /// <summary>버프 아이콘 우하단에 그 버프의 <b>스킬 레벨</b>(어노멀 레벨)을 작게 겹쳐 그린다. 레벨을 못 읽은
    /// 적용(0)은 배지를 아예 그리지 않는다 — "0"을 띄우면 1레벨과 구분이 안 된다. 기본 켜짐.</summary>
    public bool BuffUiShowLevel { get => _buffUiShowLevel; set => SetBool(ref _buffUiShowLevel, "buffUi.showLevel", value); }

    private bool _showOtherPlayerBuffs;
    /// <summary>Include buffs applied by other players (off = only the local player's own buffs).</summary>
    public bool ShowOtherPlayerBuffs { get => _showOtherPlayerBuffs; set => SetBool(ref _showOtherPlayerBuffs, "buffUi.showOther", value); }

    private bool _detailShowPartyBuffs;
    /// <summary>전투 상세 › 버프 업타임 탭에서 「남이 준 버프」 섹션을 함께 보여줄지. 기본 꺼짐.
    /// <para>⚠️ <see cref="ShowOtherPlayerBuffs"/>(<c>buffUi.showOther</c>)와 키를 공유하면 안 된다 — 그쪽은
    /// 버프 <i>오버레이</i>의 것이고 프리셋 3슬롯이 읽고 쓰는 값이라(<c>BuffPresetManager.PresetProps</c>),
    /// 프리셋을 바꾸는 순간 상세창 필터까지 같이 뒤집힌다. 같은 이유로 이 키를 그 목록에 넣지도 마라.</para></summary>
    public bool DetailShowPartyBuffs
    {
        get => _detailShowPartyBuffs;
        set => SetBool(ref _detailShowPartyBuffs, "detail.showPartyBuffs", value);
    }

    private bool _detailTimelineCooldownOnly;
    /// <summary>전투 상세 › 스킬 타임라인에서 <b>쿨타임을 돌린 발동만</b> 볼지. 기본 꺼짐.
    /// <para>왜 이 토글이 있나: 일부 스킬은 활성화 1회에 0x3802 프레임을 <b>둘</b> 낸다(실측 광풍 화살 77건 /
    /// 쿨 39건, 표적 화살 24 / 12 — <c>●○</c> 교대가 100%이고 사이 간격 중앙값 350ms, 시전→적중으로 보인다).
    /// 그걸 시간으로 접으면 임의의 임계값이 필요하고, 그 임계값이 매크로 케이던스를 통째로 지운다. 대신 서버가
    /// 준 사실 하나(쿨을 돌렸나)로 거르면 계수가 하나도 안 든다.</para>
    /// <para>⚠️ 쿨타임이 없는 스킬(속사·저격 등)은 이 필터에서 통째로 사라진다 — 그래서 기본은 꺼짐이다.</para></summary>
    public bool DetailTimelineCooldownOnly
    {
        get => _detailTimelineCooldownOnly;
        set => SetBool(ref _detailTimelineCooldownOnly, "detail.timelineCooldownOnly", value);
    }

    private string _buffUiHidden;
    /// <summary>Comma-separated base skill codes the user unchecked in the per-job buff picker; the overlay
    /// suppresses any live buff whose base code is in this set. Empty = show all (job) buffs.</summary>
    public string BuffUiHidden { get => _buffUiHidden; set => SetProp(ref _buffUiHidden, "buffUi.hidden", value); }

    private string _buffUiObserved;
    /// <summary>Comma-separated base skill codes ever seen on the local player / party — the accumulated
    /// catalog the per-job picker lists (so jobs stay populated across sessions).</summary>
    public string BuffUiObserved { get => _buffUiObserved; set => SetProp(ref _buffUiObserved, "buffUi.observed", value); }

    private string _buffUiVoice;
    /// <summary>Comma-separated base skill codes set to "오버레이+음성" in the per-job picker — these fire the
    /// start/end voice alert. A code is Off if in <see cref="BuffUiHidden"/>, Voice if here, else Overlay-only.</summary>
    public string BuffUiVoice { get => _buffUiVoice; set => SetProp(ref _buffUiVoice, "buffUi.voice", value); }

    /// <summary>The voice-enabled base-code set (parsed from <see cref="BuffUiVoice"/>).</summary>
    public HashSet<int> BuffUiVoiceCodes => ParseCodeSet(_buffUiVoice);

    private string _buffUiSortMode;
    /// <summary>버프 오버레이 정렬 모드 — <see cref="BuffOverlayOrder"/>의 applied(적용 순서, 기본) /
    /// remaining(남은 시간 순) / name(이름 순). 고정된 버프는 모드와 무관하게 항상 앞에 온다.</summary>
    public string BuffUiSortMode { get => _buffUiSortMode; set => SetProp(ref _buffUiSortMode, "buffUi.sortMode", value); }

    private string _buffUiPinned;
    /// <summary>맨 앞에 고정할 base 코드들 — 쉼표 구분이고 <b>순서가 곧 표시 순서</b>다(hidden/voice가 집합인
    /// 것과 달리 여기서는 순서에 의미가 있다). 고정되지 않은 버프는 그 뒤에 정렬 모드대로 붙는다.</summary>
    public string BuffUiPinned { get => _buffUiPinned; set => SetProp(ref _buffUiPinned, "buffUi.pinned", value); }

    /// <summary>고정 목록을 순서 그대로 파싱한 것(중복 제거).</summary>
    public List<int> BuffUiPinnedCodes => ParseCodeList(_buffUiPinned);

    private bool _buffUiDefaultsApplied;
    /// <summary>Set once the catalog's default-off toggle buffs have been merged into the hidden set, so the
    /// one-time default isn't re-applied over the user's later choices.</summary>
    public bool BuffUiDefaultsApplied { get => _buffUiDefaultsApplied; set => SetBool(ref _buffUiDefaultsApplied, "buffUi.defaultsApplied", value); }

    private string _buffUiPresets;
    /// <summary>The three buff-overlay preset slots + the active index, as one Base64(JSON) blob (see
    /// <see cref="BuffPresetCodec"/>). Base64 because slot names are user-typed Korean and every read of a
    /// settings value passes through the Latin-1 → EUC-KR re-decode, which would replace each Korean char
    /// with '?'. Owned by <see cref="BuffPresetManager"/>; not itself part of any preset.</summary>
    public string BuffUiPresets { get => _buffUiPresets; set => SetProp(ref _buffUiPresets, "buffUi.presets", value); }

    private string _aetherLastValue;
    /// <summary>The last observed aether balance as "base,bonus,unixMs" (base = 자연회복, bonus = 추가;
    /// the spendable total is their sum), persisted so it can be shown
    /// immediately on the next launch (the game only broadcasts the resource on its own schedule — zone load
    /// etc. — so without this the badge is blank for the first minutes). Restored only within a staleness
    /// window and cleared on a character switch. The shugo-festa key is deliberately NOT persisted: a stale
    /// key count would be misleading, so it always waits for a fresh broadcast.</summary>
    public string AetherLastValue { get => _aetherLastValue; set => SetProp(ref _aetherLastValue, "aether.lastValue", value); }

    private string _aetherPerCharacter;
    /// <summary>Each character's last-seen aether balance, keyed by stats identity hash (see
    /// <see cref="AetherPerCharacterStore"/>), so the character-management list can show every character's 오드,
    /// not only the one currently logged in. Unlike <see cref="AetherLastValue"/> this is NOT cleared on a
    /// character switch — that's the whole point: it accumulates each character's balance as they're played.</summary>
    public string AetherPerCharacter { get => _aetherPerCharacter; set => SetProp(ref _aetherPerCharacter, "aether.perCharacter", value); }

    private string _aetherCharacterNames;
    /// <summary>Each character's name for <see cref="AetherPerCharacter"/>, as <c>hash,server,nicknameB64</c>
    /// records. Kept in its OWN key rather than as extra fields on the balance record so that rolling back to a
    /// meter that predates the 오드 목록 loses nothing: an older build ignores an unknown settings key, but a
    /// balance record it can't parse would be dropped — and since every aether broadcast rewrites the whole
    /// blob, one packet under the old build would have made that loss permanent.</summary>
    public string AetherCharacterNames { get => _aetherCharacterNames; set => SetProp(ref _aetherCharacterNames, "aether.characterNames", value); }

    private string _weeklyContentClears;
    /// <summary>Each character's weekly 성역 clear counters as <c>hash,slug,remaining,savedAtMs</c> records (see
    /// <see cref="WeeklyContentStore"/>). Its OWN key rather than extra fields on <see cref="AetherPerCharacter"/>
    /// for the reason spelled out there: an older build drops a record it can't parse, and the aether blob is
    /// rewritten on every broadcast, so widening that record would cost every user their 오드 history the first
    /// time they rolled back.</summary>
    public string WeeklyContentClears
    {
        get => _weeklyContentClears;
        set => SetProp(ref _weeklyContentClears, "content.weeklyClears", value);
    }

    private string _abyssCorridors;
    /// <summary>Each character's 어비스 회랑 이용 시간 as
    /// <c>hash,ticketId,remainingMs,observedAtMs,grantedAtMs,tickingSinceMs</c> records (see
    /// <see cref="AbyssCorridorStore"/>). Its OWN key, never extra fields on
    /// <see cref="WeeklyContentClears"/> or <see cref="AetherPerCharacter"/>, for the reason spelled out on
    /// both: an older build DROPS a record it cannot parse, and those blobs are rewritten on every broadcast,
    /// so one rollback would make the loss permanent. An unknown key is merely ignored.</summary>
    public string AbyssCorridors
    {
        get => _abyssCorridors;
        set => SetProp(ref _abyssCorridors, "content.abyssCorridors", value);
    }

    private string _abyssArtifacts;
    /// <summary>The 어비스 아티팩트 점령 현황 per server, plus each character's 점령 개수, as
    /// <c>kind,key,zoneId,value,observedAtMs,cycleStartMs,cycleEndMs</c> records (see
    /// <see cref="AbyssArtifactStore"/>). Its OWN key for the same reason <see cref="AbyssCorridors"/> has one —
    /// and doubly so here, since this blob is what decides whether a corridor is shown at all.</summary>
    public string AbyssArtifacts
    {
        get => _abyssArtifacts;
        set => SetProp(ref _abyssArtifacts, "content.abyssArtifacts", value);
    }

    // ---- 허수아비 (training-dummy) test mode ----
    private bool _dummyTestMode;
    /// <summary>허수아비 테스트 모드: ON = hitting a training dummy is metered as a live battle; OFF = dummy hits
    /// register no combat. The single source of truth shared by the header toggle, the settings tab, and the
    /// hotkey; App mirrors it live onto the capture pipeline (DataManager.DummyTestMode).</summary>
    public bool DummyTestMode { get => _dummyTestMode; set => SetBool(ref _dummyTestMode, "dummy.testMode", value); }

    private int _dummyDurationSec;
    /// <summary>Dummy test run length in seconds (30/60/90/120/180/300). When it elapses the live battle is
    /// hard-cut so continued hits stop counting until the DPS is reset.</summary>
    public int DummyDurationSec { get => _dummyDurationSec; set => SetInt(ref _dummyDurationSec, "dummy.durationSeconds", value); }

    // Assigned in Reload(), which the constructor calls — the compiler cannot see through that, hence the seed.
    private string _patchNotesLastShownVersion = string.Empty;
    /// <summary>The base version (<c>X.Y.Z</c>) whose one-time post-update patch-note popup has already been
    /// shown. Empty = never (a fresh install / first run with this feature suppresses the very first popup, so it
    /// only ever fires on a real UPDATE). Set to the running base version once its popup fires, so it never
    /// re-pops for the same version.</summary>
    public string PatchNotesLastShownVersion
    {
        get => _patchNotesLastShownVersion;
        set => SetProp(ref _patchNotesLastShownVersion, "patchNotes.lastShownVersion", value);
    }

    /// <summary>Parse a CSV setting into a base-code set.</summary>
    public static HashSet<int> ParseCodeSet(string csv)
    {
        var set = new HashSet<int>();
        foreach (string part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, Inv, out int c))
            {
                set.Add(c);
            }
        }

        return set;
    }

    /// <summary>CSV 설정을 <b>순서를 유지한</b> 코드 목록으로 파싱한다(중복은 첫 등장만). 고정 목록처럼
    /// 순서 자체가 의미를 갖는 설정에 쓴다.</summary>
    public static List<int> ParseCodeList(string csv)
    {
        var list = new List<int>();
        var seen = new HashSet<int>();
        foreach (string part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, Inv, out int c) && seen.Add(c))
            {
                list.Add(c);
            }
        }

        return list;
    }

    /// <summary>Resolve the masking mode enum for the meter rows.</summary>
    public NameDisplay NameDisplayMode => _nameDisplay switch
    {
        "me_only" => Core.NameDisplay.MeOnly,
        "hidden" => Core.NameDisplay.Hidden,
        _ => Core.NameDisplay.All,
    };

    public bool UseTotalDamage => _damageValueMode == "total";

    /// <summary>Resolve the row metric enum. Total-damage mode wins: it has no normalized counterpart.</summary>
    public RowDpsMetricMode RowDpsMetricMode => UseTotalDamage
        ? Core.RowDpsMetricMode.Dps
        : _rowDpsMetric switch
        {
            "ndps" => Core.RowDpsMetricMode.Ndps,
            "rdps" => Core.RowDpsMetricMode.Rdps,
            _ => Core.RowDpsMetricMode.Dps,
        };
    public bool UseEntireContribution => _contributionMode == "entireContribution";

    private string ReadEnum(string key, string fallback, string[] allowed)
    {
        string? v = _props.GetProperty(key);
        return v != null && Array.IndexOf(allowed, v) >= 0 ? v : fallback;
    }

    private bool ReadBool(string key, bool fallback) => _props.GetProperty(key) switch
    {
        "true" => true,
        "false" => false,
        _ => fallback,
    };

    private int ReadInt(string key, int fallback) =>
        int.TryParse(_props.GetProperty(key), NumberStyles.Integer, Inv, out int v) ? v : fallback;

    private double ReadDouble(string key, double fallback)
    {
        string? raw = _props.GetProperty(key);
        return !string.IsNullOrEmpty(raw) && double.TryParse(raw, NumberStyles.Float, Inv, out double v) ? v : fallback;
    }

    private void SetProp(ref string field, string key, string value, [CallerMemberName] string? prop = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        _props.SetProperty(key, value);
        OnPropertyChanged(prop);
    }

    private void SetInt(ref int field, string key, int value, [CallerMemberName] string? prop = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        _props.SetProperty(key, value.ToString(Inv));
        OnPropertyChanged(prop);
    }

    private void SetDouble(ref double field, string key, double value, [CallerMemberName] string? prop = null)
    {
        if (field.Equals(value))
        {
            return;
        }

        field = value;
        _props.SetProperty(key, value.ToString(Inv));
        OnPropertyChanged(prop);
    }

    private void SetBool(ref bool field, string key, bool value, [CallerMemberName] string? prop = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        _props.SetProperty(key, value ? "true" : "false");
        OnPropertyChanged(prop);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>어떤 초당 피해량을 미터 행에 쓸지. <see cref="MeterSettings.RowDpsMetricMode"/> 참조.</summary>
public enum RowDpsMetricMode
{
    /// <summary>실제로 낸 피해 / 전투시간. 늘 기본값 — 다른 두 값은 모델 추정이 섞인다.</summary>
    Dps = 0,

    /// <summary>남이 걸어준 버프 몫을 나눠 걷어낸 값. "버프를 빼면 이 사람은 얼마인가".</summary>
    Ndps,

    /// <summary>nDPS + 내가 남에게 얹어준 몫. 서포터의 기여가 보이는 유일한 축.</summary>
    Rdps,
}
