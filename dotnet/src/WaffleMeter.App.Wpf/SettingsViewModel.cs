using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using WaffleMeter.App.Core;
using WaffleMeter.Capture;
using WaffleMeter.Capture.Live;
using WaffleMeter.Data;
using WaffleMeter.Stats;

namespace WaffleMeter.App.Wpf;

/// <summary>One effect on the settings preview strip: what it is called, which family it belongs to, and the
/// brush it paints a nickname with. The brush is the SAME shared instance the meter row uses, so an animated
/// sample moves in step with the real thing instead of being a separate approximation.</summary>
public sealed record NameFxSampleViewModel(string Name, string Kind, System.Windows.Media.Brush Fill);

/// <summary>내가 고를 수 있는 연출 하나. <paramref name="IsCurrent"/> 는 지금 적용된 것.</summary>
public sealed record NameFxChoiceViewModel(
    string Id,
    string Name,
    System.Windows.Media.Brush Fill,
    bool IsCurrent);

/// <summary>
/// One gauge skin, drawn as a MOCK METER ROW rather than a colour swatch.
/// <para>A bare bar cannot answer the question this preview exists for. A gauge is the background that damage
/// numbers are read on top of, so "is this skin too loud" only means something with the numbers actually there,
/// at the real fill opacity, with the real proportions. A swatch made every skin look fine.</para>
/// <para>It is a facsimile, not the meter's own row template (that template lives inside
/// <c>OverlayWindow.xaml</c> and pulling it out would touch the meter's rendering — a file this project has
/// regressed on twice over row geometry). What it does mirror exactly are the parts that decide the answer:
/// the fill brush instance, its 0.3 opacity, and the 3 px accent rail keeping its own colour.</para>
/// </summary>
public sealed record GaugeSkinSampleViewModel(
    /// <summary>The catalogue id, passed through so the sample uses the SAME renderer as a real row. Inferring
    /// it from the display name would let the two drift the moment a name changed.</summary>
    string Id,
    string Name,
    /// <summary>후원자 / 랭커. 게이지는 닉네임 연출과 달리 계열이 모션으로 갈리지 않으므로(모든 게이지가
    /// 같은 주기를 쓴다) 어느 쪽 자격으로 받는 스킨인지는 이 라벨로만 알 수 있다.</summary>
    string Kind,
    System.Windows.Media.Brush Fill,
    System.Windows.Media.Brush RailBrush,
    System.Windows.Media.ImageSource? IconSource,
    string Rank,
    string Nickname,
    string ServerTag,
    string PowerText,
    string DpsText,
    string PercentText,
    double BarRatio,
    double BarRest,
    /// <summary>실제 행이 스킨을 받았을 때 쓰는 값과 같아야 한다 — 견본만 진하게(또는 옅게) 그리면
    /// 판단해야 할 축이 통째로 어긋난다.</summary>
    double GaugeOpacity);

/// <summary>A label/value choice for a settings ComboBox.</summary>
public sealed record SettingOption(string Label, string Value);

/// <summary>One row of the per-character consent management list (immutable; the collection is rebuilt on
/// change). The public toggle binds <c>IsPublic</c> one-way and routes the change through a Click handler.</summary>
public sealed class ConsentCharacterRow
{
    public string IdentityHash { get; init; } = "";
    public string Label { get; init; } = "";
    public string SubLabel { get; init; } = "";
    public bool IsPublic { get; init; }
    public bool CanSetPublic { get; init; }
    public bool CanRevoke { get; init; }
    public string PublicToggleTooltip { get; init; } = "";
    public Visibility CurrentBadgeVisibility { get; init; }

    /// <summary>The character's last-seen aether (오드) as "base(+bonus)", or empty when none is remembered.</summary>
    public string AetherText { get; init; } = "";

    /// <summary>Visible only when we have a remembered aether balance for this character.</summary>
    public Visibility AetherVisibility { get; init; } = Visibility.Collapsed;

    /// <summary>
    /// 공개 자동 적용이 실패해 서버는 비공개인데 사용자는 켠 적이 있는 상태. 종전에는 이 상태가 화면에
    /// 드러나지 않아 <b>"분명 켰는데 꺼져 있는" 체크박스</b>만 보였다(게다가 업로드까지 막혔다 — 그쪽은 이미 고쳤다).
    /// <para>재시도 버튼은 두지 않는다. 기존 공개 토글이 그대로 재시도 경로이고, 시도하는 순간 매니저가 이 표시를
    /// 지운다.</para>
    /// </summary>
    public bool PublicApplyFailed { get; init; }

    public Visibility PublicApplyFailedVisibility =>
        PublicApplyFailed ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>One row of the custom-alarm list (immutable; the collection is rebuilt on change). The enable
/// toggle binds one-way and routes the change through a Click handler.</summary>
public sealed class CustomAlarmRow
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string TimeText { get; init; } = "";
    public string DaysText { get; init; } = "";
    public bool Enabled { get; init; }
}

/// <summary>One buff-preset chip. <see cref="IsActive"/> is bound two-way to a RadioButton: checking one
/// selects that slot, and the view-model then clears the others (the chips deliberately carry no GroupName —
/// the bar is rendered on two tabs at once, and WPF would group all six buttons together).</summary>
public sealed class BuffPresetSlotViewModel : INotifyPropertyChanged
{
    private readonly Action<int> _select;
    private string _name;
    private bool _isActive;

    public BuffPresetSlotViewModel(int index, string name, bool isActive, Action<int> select)
    {
        Index = index;
        _name = name;
        _isActive = isActive;
        _select = select;
    }

    public int Index { get; }

    public string Name
    {
        get => _name;
        internal set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            OnPropertyChanged();
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            OnPropertyChanged();
            if (value)
            {
                _select(Index);
            }
        }
    }

    /// <summary>Reflect the store's active slot without re-triggering a selection.</summary>
    internal void SyncActive(bool value)
    {
        if (_isActive == value)
        {
            return;
        }

        _isActive = value;
        OnPropertyChanged(nameof(IsActive));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Backs the tabbed settings window. Display/overlay settings apply live via <see cref="MeterSettings"/>
/// (the overlay reads them each tick); hotkeys are buffered and committed on Save; Cancel reverts the
/// live-applied settings from a snapshot. Stats consent + server config call the services directly.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly MeterServices _services;
    private readonly MeterSettings _settings;
    private readonly SkinManager _skin;
    private readonly OverlayController _controller;
    private readonly HotkeyHandler _hotkeys;
    private readonly Action _hotkeysIssuesChanged;
    private readonly BuffPresetManager _presets;
    private readonly GameOptimizerService _gameOpt;
    private readonly CooldownPresetManager? _cooldownPresets;
    // NOT readonly: an import re-takes it. Cancel restores the values captured when the window opened, so
    // after a 70-key import it would put back a mixture that neither the code nor any backup describes.
    private Snapshot _snapshot;

    public SettingsViewModel(MeterServices services, MeterSettings settings, MeterColorTheme theme, SkinManager skin, OverlayController controller, HotkeyHandler hotkeys, BuffPresetManager presets, GameOptimizerService gameOpt, CooldownPresetManager? cooldownPresets = null)
    {
        _services = services;
        _settings = settings;
        Theme = theme;
        _skin = skin;
        _controller = controller;
        _hotkeys = hotkeys;
        _hotkeysIssuesChanged = RefreshHotkeyWarnings;
        hotkeys.IssuesChanged += _hotkeysIssuesChanged;
        _presets = presets;
        _gameOpt = gameOpt;
        _cooldownPresets = cooldownPresets;
        _snapshot = Snapshot.Capture(settings, controller);
        RefreshGameOpt();

        // 미리보기는 '무엇이 바뀌었나'를 가리지 않는다 — 글꼴·투명도·티어·닉네임 효과·행 높이가 전부
        // 생김새를 바꾸는데, 세터마다 손으로 호출을 심으면 새 설정을 더할 때 하나씩 빠진다. 아직 안 만든
        // 미리보기는 그냥 통과하므로(RefreshLayoutPreview 의 null 가드) 평소엔 비용이 0 이다.
        _settingsChanged = (_, _) => RefreshLayoutPreview();
        settings.PropertyChanged += _settingsChanged;
        // 미리보기도 본체와 같은 스킨 전환 배선을 탄다(App.xaml.cs 의 skinManager.Changed += RefreshSkin).
        // 없으면 다크↔라이트를 바꿔도 미리보기 안의 DPS·퍼센트 색만 옛 스킨으로 남는다 — 스킨이 바뀌었는지
        // 확인하려고 여는 화면에서 그게 틀린 답을 준다.
        _skinChanged = () => _layoutPreview?.RefreshSkin();
        skin.Changed += _skinChanged;

        _pendingReset = hotkeys.Reset;
        _pendingVisibility = hotkeys.Visibility;
        _pendingClickThrough = hotkeys.ClickThrough;
        _pendingDummyToggle = hotkeys.DummyToggle;
        _pendingDummyReset = hotkeys.DummyReset;
        _pendingSplitUi = hotkeys.SplitUi;
        _pendingAetherList = hotkeys.AetherList;

        IReadOnlyList<string> presetNames = _presets.Names;
        for (int i = 0; i < BuffPresetManager.SlotCount; i++)
        {
            PresetSlots.Add(new BuffPresetSlotViewModel(i, presetNames[i], i == _presets.ActiveIndex, SelectPreset));
        }

        // 쿨타임 프리셋은 선택적이다 — UiPreview 하네스와 단위 테스트는 오버레이 배선 없이 이 뷰모델을
        // 만든다. null 이면 바를 통째로 접는다(HasCooldownPresets).
        if (_cooldownPresets is { } cd)
        {
            IReadOnlyList<string> cdNames = cd.Names;
            for (int i = 0; i < CooldownPresetManager.SlotCount; i++)
            {
                CooldownPresetSlots.Add(new BuffPresetSlotViewModel(i, cdNames[i], i == cd.ActiveIndex, SelectCooldownPreset));
            }
        }

        RebuildFontCards();
        RebuildNameFxSamples(_skin.IsLight);
        SyncNameFxPreview();
        Reload();
    }

    // ---- option lists (React SettingsPanel) ----
    public IReadOnlyList<SettingOption> DisplayModes { get; } = new[]
    {
        new SettingOption("DPS · 퍼센트", "dps_percent"),
        new SettingOption("누적 · DPS · 퍼센트", "amount_dps_percent"),
        new SettingOption("누적 · 퍼센트", "amount_percent"),
        new SettingOption("누적(전체) · DPS · 퍼센트", "amount_full_dps_percent"),
        new SettingOption("누적(전체) · 퍼센트", "amount_full_percent"),
    };

    /// <summary>허수아비 test run lengths (label, seconds-as-string for the ComboBox SelectedValue).</summary>
    public IReadOnlyList<SettingOption> DummyDurations { get; } = new[]
    {
        new SettingOption("30초", "30"),
        new SettingOption("1분", "60"),
        new SettingOption("1분 30초", "90"),
        new SettingOption("2분", "120"),
        new SettingOption("3분", "180"),
        new SettingOption("5분", "300"),
    };

    public IReadOnlyList<SettingOption> DamageValueModes { get; } = new[]
    {
        new SettingOption("DPS", "dps"),
        new SettingOption("누적 피해량", "total"),
    };

    public IReadOnlyList<SettingOption> RowDpsMetrics { get; } = new[]
    {
        new SettingOption("DPS (실제 피해)", "dps"),
        new SettingOption("nDPS (버프 제외)", "ndps"),
        new SettingOption("rDPS (버프 기여 포함)", "rdps"),
    };

    public IReadOnlyList<SettingOption> ContributionModes { get; } = new[]
    {
        new SettingOption("파티 기여도", "contribution"),
        new SettingOption("보스 체력 기여도", "entireContribution"),
    };

    public IReadOnlyList<SettingOption> NameDisplays { get; } = new[]
    {
        new SettingOption("모두 표기", "all"),
        new SettingOption("나만 표기 (방송용 익명)", "me_only"),
        new SettingOption("모두 숨김", "hidden"),
    };

    /// <summary>동봉된 음성 팩. 값은 폴더명이자 <c>alarms.ttsVoice</c>에 저장되는 문자열이다.</summary>
    public IReadOnlyList<SettingOption> TtsVoices { get; } = new[]
    {
        new SettingOption("와순이 (여성)", BakedVoicePack.Wasuni),
        new SettingOption("와붕이 (남성)", BakedVoicePack.Wabungi),
    };

    public IReadOnlyList<SettingOption> TargetInfoDisplayModes { get; } = new[]
    {
        new SettingOption("남은/최대 · 퍼센트", "hp_full_percent"),
        new SettingOption("남은/최대(축약) · 퍼센트", "hp_percent"),
        new SettingOption("남은 체력 · 퍼센트", "remain_full_percent"),
        new SettingOption("남은 체력(축약) · 퍼센트", "remain_percent"),
        new SettingOption("퍼센트만", "percent"),
    };

    public IReadOnlyList<SettingOption> BarStyles { get; } = new[]
    {
        new SettingOption("칸 채우기 (두꺼운 게이지)", "fill"),
        new SettingOption("얇은 바", "bar"),
        new SettingOption("표시 안 함", "none"),
    };

    /// <summary>레이아웃 3종. 목록 정본은 <see cref="MeterLayout.All"/> 하나다 — 여기에 리터럴로
    /// 다시 적으면 MeterSettings 의 허용값(같은 곳에서 가져온다)과 조용히 어긋난다.</summary>
    public IReadOnlyList<SettingOption> MeterLayouts { get; } =
        MeterLayout.All.Select(l => new SettingOption(l.Label, l.Id)).ToArray();

    public IReadOnlyList<SettingOption> TierEffectModes { get; } = new[]
    {
        new SettingOption("테두리 + 효과", "animated"),
        new SettingOption("테두리만", "static"),
        new SettingOption("표시 안 함", "off"),
    };

    public IReadOnlyList<SettingOption> BarColorModes { get; } = new[]
    {
        new SettingOption("본인 강조", "self"),
        new SettingOption("직업 강조", "job"),
    };

    // Bundled-or-fallback fonts (see Fonts/README.md). Each family ships a regular + a bolder weight, and
    // four families add an even heavier "(EX)" extra-bold. Each Value is the name FontFamilyConverter feeds
    // to WPF as ./Fonts/#<value>, which resolves to that exact weight's typeface — WPF matches it against the
    // font's Win32 family name (e.g. "NEXON Lv2 Gothic Bold") or its family+face (e.g. "Pretendard Bold",
    // whose Win32 family is the shared "Pretendard") — so the weight needs no separate FontWeight plumbing.
    // (EX) values verified per file via GlyphTypeface resolution. Malgun Gothic is always available (fallback).
    /// <summary>Shipped default. Must stay byte-identical to <c>MeterSettings</c>'s own default, or a fresh
    /// install shows no card selected.</summary>
    public const string DefaultFontFamily = "NEXON Lv2 Gothic Medium";

    private static readonly SettingOption[] BuiltInFonts =
    {
        new SettingOption("NEXON Lv2 Gothic (Bold, 기본)", "NEXON Lv2 Gothic Medium"),
        new SettingOption("NEXON Lv2 Gothic (EX)", "NEXON Lv2 Gothic Bold"),
        new SettingOption("NEXON Lv2 Gothic", "NEXON Lv2 Gothic"),
        new SettingOption("Pretendard (Bold)", "Pretendard SemiBold"),
        new SettingOption("Pretendard (EX)", "Pretendard Bold"),
        new SettingOption("Pretendard", "Pretendard"),
        new SettingOption("Spoqa Han Sans Neo (Bold)", "Spoqa Han Sans Neo Medium"),
        new SettingOption("Spoqa Han Sans Neo (EX)", "Spoqa Han Sans Neo Bold"),
        new SettingOption("Spoqa Han Sans Neo", "Spoqa Han Sans Neo"),
        new SettingOption("Freesentation (Bold)", "Freesentation 6 SemiBold"),
        new SettingOption("Freesentation (EX)", "Freesentation 7 Bold"),
        new SettingOption("Freesentation", "Freesentation"),
        new SettingOption("Tmoney Round Wind (Bold)", "Tmoney RoundWind ExtraBold"),
        new SettingOption("Tmoney Round Wind", "Tmoney RoundWind"),
        new SettingOption("맑은 고딕", "Malgun Gothic"),
    };

    /// <summary>The bundled fonts plus any the user has added (a .ttf/.otf in the fonts folder), so a custom
    /// font is selectable in the dropdown. Re-queried when <see cref="AddCustomFont"/> raises the change.</summary>
    public IReadOnlyList<SettingOption> FontFamilies
    {
        get
        {
            var list = new List<SettingOption>(BuiltInFonts);
            var seen = new HashSet<string>();
            foreach (SettingOption o in BuiltInFonts)
            {
                seen.Add(o.Value);
            }

            foreach (string name in FontResolver.EnumerateUserFontFamilies())
            {
                if (seen.Add(name))
                {
                    list.Add(new SettingOption(name + " (사용자)", name));
                }
            }

            return list;
        }
    }

    /// <summary>
    /// The font picker's cards — bundled fonts plus anything the user dropped into the fonts folder. Built once
    /// and mutated in place, because each card resolves its own <see cref="FontFamily"/> at construction and a
    /// getter that rebuilt the list would re-enumerate the fonts folder off disk on every binding refresh.
    /// </summary>
    public ObservableCollection<FontCardViewModel> FontCards { get; } = new();

    /// <summary>Installed system fonts. Not cards: a few hundred entries, and the bundled set is the curated one.
    /// The picked one still previews — the 현재 글꼴 row below the dropdown renders in whatever is applied.</summary>
    public IReadOnlyList<string> SystemFontFamilies => FontResolver.EnumerateSystemFontFamilies();

    private void RebuildFontCards()
    {
        FontCards.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SettingOption o in BuiltInFonts)
        {
            if (seen.Add(o.Value))
            {
                FontCards.Add(new FontCardViewModel(o.Label, o.Value, o.Value == DefaultFontFamily));
            }
        }

        foreach (string name in FontResolver.EnumerateUserFontFamilies())
        {
            if (seen.Add(name))
            {
                FontCards.Add(new FontCardViewModel(name, name, isDefault: false));
            }
        }

        SyncFontSelection();
    }

    private void SyncFontSelection()
    {
        string current = _settings.FontFamily;
        foreach (FontCardViewModel c in FontCards)
        {
            c.IsSelected = string.Equals(c.Value, current, StringComparison.Ordinal);
        }

        OnPropertyChanged(nameof(CardFontSelection));
        OnPropertyChanged(nameof(SystemFontSelection));
        OnPropertyChanged(nameof(CurrentFontPreview));
        OnPropertyChanged(nameof(CurrentFontSample));
        OnPropertyChanged(nameof(CurrentFontStatus));
    }

    private bool IsCardFont(string name) => FontCards.Any(c => string.Equals(c.Value, name, StringComparison.Ordinal));

    /// <summary>
    /// Card-grid selection. Deliberately NOT bound straight to <see cref="FontFamily"/>: the card list and the
    /// system dropdown are two <c>Selector</c>s over the same setting, and a Selector whose bound value is absent
    /// from ITS list coerces to null and writes that null back (SelectedValue is TwoWay by default). Routing each
    /// through its own property means "the other one owns the value" shows as an empty selection instead of
    /// wiping the setting.
    /// </summary>
    public string? CardFontSelection
    {
        get => IsCardFont(_settings.FontFamily) ? _settings.FontFamily : null;
        set
        {
            if (!string.IsNullOrEmpty(value))
            {
                FontFamily = value;
            }
        }
    }

    /// <summary>System-dropdown selection. Same null-coercion reasoning as <see cref="CardFontSelection"/>.</summary>
    public string? SystemFontSelection
    {
        get => IsCardFont(_settings.FontFamily) ? null : _settings.FontFamily;
        set
        {
            if (!string.IsNullOrEmpty(value))
            {
                FontFamily = value;
            }
        }
    }

    /// <summary>The applied font, resolved — so the 현재 글꼴 row previews a system pick too, not just cards.</summary>
    public System.Windows.Media.FontFamily CurrentFontPreview => FontResolver.Resolve(_settings.FontFamily);

    public string CurrentFontSample =>
        GlyphFallback.CanRender(_settings.FontFamily, "가") ? FontCardViewModel.Sample : "Waffle 1,234";

    public string CurrentFontStatus
    {
        get
        {
            string name = _settings.FontFamily;
            string where = FontResolver.Classify(name) switch
            {
                FontResolver.FontOrigin.Bundled => "번들 글꼴",
                FontResolver.FontOrigin.User => "사용자 추가 글꼴",
                _ => "시스템 글꼴",
            };
            string hangul = GlyphFallback.CanRender(name, "가") ? string.Empty : " · 한글 미지원(이름은 맑은 고딕으로 대체)";
            return $"현재 글꼴 — {name} ({where}){hangul}";
        }
    }

    /// <summary>Copy a user-picked font file into the fonts folder, add it to the picker, and select+apply it.
    /// Returns false if the file can't be read as a font (the caller shows a message). The font renders live via
    /// FontResolver — no restart needed — and persists (the folder is the store).</summary>
    public bool AddCustomFont(string sourcePath)
    {
        string? family = FontResolver.InstallUserFont(sourcePath);
        if (string.IsNullOrWhiteSpace(family))
        {
            return false;
        }

        // Re-adding a DIFFERENT file under a family name already asked about would otherwise keep serving the
        // memoised old face. Adding a genuinely new name is safe on its own, but this is the cheap side.
        GlyphFallback.InvalidateCache();
        RebuildFontCards();      // the grid now includes the new font...
        FontFamily = family;     // ...so selecting it lands on a real card, and applies it live
        OnPropertyChanged(nameof(FontFamilies));
        return true;
    }

    // ---- display tab (live) ----
    public string DisplayMode { get => _settings.DisplayMode; set { _settings.DisplayMode = value; OnPropertyChanged(); } }
    public string DamageValueMode { get => _settings.DamageValueMode; set { _settings.DamageValueMode = value; OnPropertyChanged(); } }

    /// <summary>미터 행의 초당 피해량 종류(DPS/nDPS/rDPS). 표시·정렬·게이지가 함께 움직인다.</summary>
    public string RowDpsMetric { get => _settings.RowDpsMetric; set { _settings.RowDpsMetric = value; OnPropertyChanged(); } }
    public string ContributionMode { get => _settings.ContributionMode; set { _settings.ContributionMode = value; OnPropertyChanged(); } }
    public string NameDisplay { get => _settings.NameDisplay; set { _settings.NameDisplay = value; OnPropertyChanged(); } }
    public string TtsVoice
    {
        get => _settings.TtsVoice;
        set
        {
            _settings.TtsVoice = value;
            TtsSpeech.SetVoicePack(new BakedVoicePack(AppContext.BaseDirectory, value));
            OnPropertyChanged();
        }
    }
    /// <summary>The applied meter font. The setter drops null/empty on purpose — see
    /// <see cref="CardFontSelection"/> for why two Selectors over one setting would otherwise erase it.</summary>
    public string FontFamily
    {
        get => _settings.FontFamily;
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            _settings.FontFamily = value;
            OnPropertyChanged();
            SyncFontSelection();
        }
    }
    public int RowHeight { get => _settings.RowHeight; set { _settings.RowHeight = value; OnPropertyChanged(); } }

    /// <summary>
    /// 보스칸 높이 배율(퍼센트). 100% 기준 높이가 레이아웃마다 달라서(전장 104 / 계기판 52 / 무대 70)
    /// 슬라이더 단위는 px 가 아니라 퍼센트다 — 대신 라벨이 지금 레이아웃에서의 실제 px 를 같이 말한다.
    /// </summary>
    public int BossSlotScalePercent
    {
        get => _settings.BossSlotScalePercent;
        set
        {
            _settings.BossSlotScalePercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BossSlotHeightHint));
        }
    }

    /// <summary>슬라이더 제목. 배율과 **지금 레이아웃에서의 실제 높이**를 함께 보여 준다.</summary>
    public string BossSlotHeightHint
    {
        get
        {
            MeterLayout layout = MeterLayout.For(_settings.MeterLayoutId);
            double px = MeterLayout.BossSlotHeight(layout.BossStyle, _settings.BossSlotScalePercent);
            return $"보스칸 높이 — {_settings.BossSlotScalePercent}% ({px:0}px)";
        }
    }

    /// <summary>미터 전체 크기 배율(퍼센트). 75~130 연속이고, 미터 창의 좌/우 가장자리를 끌어도 같은 값이
    /// 움직인다. 100% 근처는 디텐트로 붙는다 — 연속 슬라이더에서 "보통"이 안 잡히면 사용자는 영영 99%에
    /// 머문다.</summary>
    public int MeterScalePercent
    {
        get => _settings.MeterScalePercent;
        set
        {
            int p = MeterScalePolicy.Detent(MeterScalePolicy.ClampScale(value));
            if (p == _settings.MeterScalePercent)
            {
                return;
            }

            _settings.MeterScalePercent = p;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MeterScaleTitle));
            OnPropertyChanged(nameof(MeterScaleHint));
        }
    }

    public string MeterScaleTitle => $"미터 크기 — {_settings.MeterScalePercent}%";

    /// <summary>
    /// 지금 화면과 지금 미터가 실제로 어떤 관계인지 보고한다 — "현재 화면 1920×1080 · 미터 폭 490px (화면의 25.5%)".
    /// <para>종전에는 <c>{ get; } = BuildScaleHint()</c> 라 <b>생성자에서 한 번</b> 계산됐고, 물리 px 만 보고
    /// DPI 를 전혀 안 읽어 4K@150%(실효 2560×1440, 이미 넉넉한 환경) 사용자에게도 "권장 115~130%"라고
    /// <b>틀린 방향</b>으로 안내했다. 권장값을 지어내는 대신 사실만 말한다 — 사용자가 불만을 말한 단위가
    /// 곧 <b>화면 점유율</b>이기 때문이다.</para>
    /// </summary>
    public string MeterScaleHint => BuildScaleHint(_meterWidthProbe?.Invoke() ?? 0);

    /// <summary>현재 미터 창의 폭(DIP)을 읽어 오는 프로브. App 이 꽂아 준다(설정창은 미터를 모른다).</summary>
    private Func<double>? _meterWidthProbe;

    public void SetMeterWidthProbe(Func<double> probe)
    {
        _meterWidthProbe = probe;
        OnPropertyChanged(nameof(MeterScaleHint));
    }

    /// <summary>미터를 끌어 배율이 확정됐을 때 취소 기준선을 새 값으로 다시 잡고 화면을 갱신한다.</summary>
    public void RebaseMeterScale()
    {
        _snapshot = _snapshot with { MeterScalePercent = _settings.MeterScalePercent };
        OnPropertyChanged(nameof(MeterScalePercent));
        OnPropertyChanged(nameof(MeterScaleTitle));
        OnPropertyChanged(nameof(MeterScaleHint));
    }

    private static string BuildScaleHint(double meterWidthDip)
    {
        try
        {
            System.Windows.Forms.Screen? s = System.Windows.Forms.Screen.PrimaryScreen;
            if (s is null)
            {
                return string.Empty;
            }

            int w = s.Bounds.Width, h = s.Bounds.Height;
            if (meterWidthDip <= 0)
            {
                return $"현재 화면 {w}×{h}";
            }

            // 화면 폭은 물리 px, 미터 폭은 DIP 다. 같은 단위로 맞추지 않으면 고DPI 에서 점유율이 과소 보고된다.
            double dpi = 1.0;
            System.Windows.Window? any = System.Windows.Application.Current?.MainWindow;
            if (any is not null)
            {
                dpi = System.Windows.Media.VisualTreeHelper.GetDpi(any).DpiScaleX;
            }

            double pct = meterWidthDip * (dpi > 0 ? dpi : 1.0) / w * 100.0;
            return $"현재 화면 {w}×{h} · 미터 폭 {meterWidthDip:0}px (화면의 {pct:0.0}%)";
        }
        catch
        {
            return string.Empty;
        }
    }
    public int RefreshIntervalMs { get => _settings.RefreshIntervalMs; set { _settings.RefreshIntervalMs = value; OnPropertyChanged(); } }
    public int MaxVisibleRows { get => _settings.MaxVisibleRows; set { _settings.MaxVisibleRows = value; OnPropertyChanged(); } }

    public bool DimDeadRows { get => _settings.DimDeadRows; set { _settings.DimDeadRows = value; OnPropertyChanged(); } }
    public bool LowSpecMode
    {
        get => _settings.LowSpecMode;
        set { _settings.LowSpecMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(RefreshSliderEnabled)); }
    }

    /// <summary>The refresh-interval slider is disabled while low-spec mode pins the interval.</summary>
    public bool RefreshSliderEnabled => !_settings.LowSpecMode;
    public string TargetInfoDisplayMode { get => _settings.TargetInfoDisplayMode; set { _settings.TargetInfoDisplayMode = value; OnPropertyChanged(); } }

    // ---- game optimization tab (게임 최적화 · Engine.ini) ----
    private string _gpuText = string.Empty;
    /// <summary>감지된 그래픽카드 + VRAM 요약(감지 실패 시 안내 문구).</summary>
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }

    private string _gameOptTierText = string.Empty;
    /// <summary>VRAM으로 정해진 적용 프로필(티어) 요약.</summary>
    public string GameOptTierText { get => _gameOptTierText; private set => Set(ref _gameOptTierText, value); }

    private string _gameOptStatus = string.Empty;
    /// <summary>현재 Engine.ini에 우리 블록이 들어 있는지("적용됨"/"미적용").</summary>
    public string GameOptStatus { get => _gameOptStatus; private set => Set(ref _gameOptStatus, value); }

    private bool _gameRunning;
    /// <summary>아이온2 실행 중 — 적용/되돌리기 전에 종료하라는 경고를 XAML에서 BoolToVis로 표시.</summary>
    public bool GameRunning { get => _gameRunning; private set => Set(ref _gameRunning, value); }

    private EngineIniOptimizer.Tier _gameOptTier;

    /// <summary>GPU·적용 상태·게임 실행 여부를 다시 읽어 표시를 갱신(탭 열 때 + 적용/되돌리기 후).</summary>
    public void RefreshGameOpt()
    {
        GameOptimizerService.Gpu gpu = _gameOpt.DetectGpu();
        _gameOptTier = EngineIniOptimizer.TierForVram(gpu.VramBytes);
        if (gpu.VramBytes > 0)
        {
            double gb = gpu.VramBytes / (1024.0 * 1024 * 1024);
            GpuText = $"{gpu.Name} · VRAM {gb:0.#}GB";
        }
        else
        {
            GpuText = "그래픽카드 VRAM을 감지하지 못했습니다 — 가장 안전한 설정으로 적용됩니다.";
        }

        GameOptTierText = $"프로필 {_gameOptTier.Label} · 스트리밍 풀 {_gameOptTier.PoolMiB}MB";
        GameOptStatus = _gameOpt.IsApplied() ? "현재 상태: 적용됨" : "현재 상태: 미적용";
        GameRunning = _gameOpt.IsGameRunning();
    }

    /// <summary>감지된 프로필로 Engine.ini에 최적화를 적용/갱신한다(게임 재실행 후 반영).</summary>
    public void ApplyGameOpt()
    {
        try
        {
            _gameOpt.Apply(_gameOptTier);
        }
        catch
        {
            // 실패는 아래 RefreshGameOpt의 "미적용" 표시로 드러난다
        }

        RefreshGameOpt();
    }

    /// <summary>우리가 추가한 블록만 제거한다(사용자의 다른 Engine.ini 설정은 유지).</summary>
    public void RevertGameOpt()
    {
        try
        {
            _gameOpt.Revert();
        }
        catch
        {
            // no-op; 상태는 RefreshGameOpt가 반영
        }

        RefreshGameOpt();
    }
    public string BarStyle { get => _settings.BarStyle; set { _settings.BarStyle = value; OnPropertyChanged(); OnPropertyChanged(nameof(GaugeSkinApplicable)); } }

    /// <summary>
    /// 미터 레이아웃. 행 높이도 함께 그 레이아웃의 기본값으로 옮긴다 — 세 레이아웃은 행 높이까지
    /// 포함해서 하나의 생김새이고, 사용자가 바꾸고 싶으면 아래 슬라이더로 바로 되돌릴 수 있다.
    /// (RowHeight 는 Snapshot 에 이미 있어 '취소'로도 복구된다.)
    /// </summary>
    public string MeterLayoutId
    {
        get => _settings.MeterLayoutId;
        set
        {
            _settings.MeterLayoutId = value;
            _settings.RowHeight = MeterLayout.For(value).DefaultRowHeight;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RowHeight));
            // 배율은 레이아웃과 무관하게 유지하지만(퍼센트라 뜻이 그대로다) 그 배율이 만드는 실제 px 는
            // 기준 높이가 달라져 같이 움직인다. 라벨만 다시 읽으면 된다.
            OnPropertyChanged(nameof(BossSlotHeightHint));
            OnPropertyChanged(nameof(BarStyleEnabled));
            OnPropertyChanged(nameof(GaugeSkinApplicable));
            OnPropertyChanged(nameof(ServerTagEnabled));
            OnPropertyChanged(nameof(LayoutTraits));
            OnPropertyChanged(nameof(LayoutLocks));
            OnPropertyChanged(nameof(LayoutLocksVisibility));
        }
    }

    /// <summary>
    /// 계기판·무대는 '직업색 게이지가 곧 행'이라 게이지 형태를 '얇은 바'나 '표시 안 함'으로 두면
    /// 빈 리본이 된다. 저장값은 건드리지 않고 UI 만 잠근다 — 다른 레이아웃으로 돌아가면 사용자가
    /// 고른 값이 그대로 되살아난다.
    /// </summary>
    public bool BarStyleEnabled => !MeterLayout.For(_settings.MeterLayoutId).RequiresFillGauge;

    /// <summary>
    /// 계기판은 이름 뒤에 서버를 넣지 않는다 — 켜 둬도 레이아웃이 접으므로 토글을 잠그고 이유를 밝힌다.
    /// <para>이건 <see cref="BarStyleEnabled"/> 와 같은 종류의 잠금이라 목록도 같은 곳
    /// (<see cref="MeterLayout.LocksOf"/>)에서 나온다 — 여기와 목록이 따로 놀면 "잠긴다고 적혀 있는데
    /// 눌리는" 상태가 된다.</para>
    /// </summary>
    public bool ServerTagEnabled => MeterLayout.For(_settings.MeterLayoutId).ShowServerTag;

    /// <summary>이 레이아웃이 통째로 정하는 것들(설정에 항목 자체가 없다).</summary>
    public IReadOnlyList<MeterLayout.LayoutNote> LayoutTraits =>
        MeterLayout.TraitsOf(MeterLayout.For(_settings.MeterLayoutId));

    /// <summary>이 레이아웃에서 잠기는 설정. 없으면 목록째 접는다.</summary>
    public IReadOnlyList<MeterLayout.LayoutNote> LayoutLocks =>
        MeterLayout.LocksOf(MeterLayout.For(_settings.MeterLayoutId));

    public Visibility LayoutLocksVisibility =>
        LayoutLocks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private OverlayViewModel? _layoutPreview;
    private readonly System.ComponentModel.PropertyChangedEventHandler _settingsChanged;
    private readonly Action _skinChanged;

    /// <summary>
    /// 설정창이 닫힐 때 구독을 떼어낸다. 이 뷰모델은 창을 열 때마다 **새로 만들어지므로**, 떼지 않으면
    /// <see cref="MeterSettings"/> 와 <see cref="SkinManager"/> 가 죽은 뷰모델을 계속 붙들고 있다가
    /// 설정 하나 바뀔 때마다 그동안 열었던 모든 미리보기를 다시 그린다(창을 열수록 느려진다).
    /// </summary>
    // ── 단축키 경고 ────────────────────────────────────────────────────────────
    // 두 사유 모두 종전에는 완전히 무음이라 "설정엔 들어가 있는데 안 먹는다" 로만 보였다(3.1.0 제보).
    // 조치가 서로 다르므로 문구도 갈라 준다 — 은퇴는 **다시 지정**, 등록 실패는 **다른 조합으로 변경**.
    private static string WarningFor(HotkeyIssue issue) => issue switch
    {
        HotkeyIssue.Retired => "이전에 저장된 조합을 더 이상 쓸 수 없어 해제했습니다. 다시 지정해 주세요.",
        HotkeyIssue.RegisterFailed => "다른 프로그램이 이미 쓰는 조합이라 등록하지 못했습니다. 다른 조합으로 바꿔 주세요.",
        _ => string.Empty,
    };

    public string ResetHotkeyWarning => WarningFor(_hotkeys.ResetIssue);
    public string VisibilityHotkeyWarning => WarningFor(_hotkeys.VisibilityIssue);
    public string ClickThroughHotkeyWarning => WarningFor(_hotkeys.ClickThroughIssue);
    public string DummyToggleHotkeyWarning => WarningFor(_hotkeys.DummyToggleIssue);
    public string DummyResetHotkeyWarning => WarningFor(_hotkeys.DummyResetIssue);
    public string SplitUiHotkeyWarning => WarningFor(_hotkeys.SplitUiIssue);
    public string AetherListHotkeyWarning => WarningFor(_hotkeys.AetherListIssue);

    /// <summary>
    /// 경고 일곱 칸을 다시 읽는다. ⚠️ <see cref="HotkeyHandler.IssuesChanged"/> 는 <b>리스너 스레드</b>에서
    /// 올 수 있으므로 UI 스레드로 마셜한다. Application 이 없는 컨텍스트(단위 테스트·UiPreview)에서는
    /// 그 자리에서 바로 올린다.
    /// </summary>
    private void RefreshHotkeyWarnings()
    {
        Dispatcher? ui = Application.Current?.Dispatcher;
        if (ui != null && !ui.CheckAccess())
        {
            ui.BeginInvoke(RaiseHotkeyWarnings);
            return;
        }

        RaiseHotkeyWarnings();
    }

    private void RaiseHotkeyWarnings()
    {
        OnPropertyChanged(nameof(ResetHotkeyWarning));
        OnPropertyChanged(nameof(VisibilityHotkeyWarning));
        OnPropertyChanged(nameof(ClickThroughHotkeyWarning));
        OnPropertyChanged(nameof(DummyToggleHotkeyWarning));
        OnPropertyChanged(nameof(DummyResetHotkeyWarning));
        OnPropertyChanged(nameof(SplitUiHotkeyWarning));
        OnPropertyChanged(nameof(AetherListHotkeyWarning));
    }

    public void Detach()
    {
        _hotkeys.IssuesChanged -= _hotkeysIssuesChanged;
        _settings.PropertyChanged -= _settingsChanged;
        _skin.Changed -= _skinChanged;
        _layoutPreview?.Detach();
        _layoutPreview = null;
    }

    /// <summary>
    /// 레이아웃 미리보기. <b>진짜</b> <c>BossBarView</c>·<c>MeterRowsView</c> 를 표본 전투로 그린다 —
    /// 설정창용으로 따로 그린 그림은 레이아웃을 손볼 때마다 실물과 어긋나고, 어긋난 걸 볼 방법이 없다.
    ///
    /// <para>🔑 <c>preview: true</c> 가 핵심이다. 이 인스턴스가 공용 애니메이션 시계에 수요를 보고하면
    /// 본체 미터의 수요를 <b>대입으로 덮어</b> 설정창을 열어 둔 동안 연출이 간헐 정지한다
    /// (<see cref="OverlayViewModel"/> 의 <c>_preview</c> 주석).</para>
    ///
    /// <para>지연 생성이다 — 화면 탭을 한 번도 안 열면 표본 전투를 만들 일도 없다.</para>
    /// </summary>
    public OverlayViewModel LayoutPreview
    {
        get
        {
            if (_layoutPreview is null)
            {
                _layoutPreview = new OverlayViewModel(
                    _services.Version, _settings, Theme, () => _skin.IsLight,
                    _services.Data.Encounters, preview: true)
                {
                    TierResolver = _ => LayoutPreviewSample.Tiers,
                };
                _layoutPreview.SetRecognized(
                    true, LayoutPreviewSample.PreviewNickname, selfId: 1,
                    server: LayoutPreviewSample.PreviewServer, job: JobClass.SORCERER, power: 656_000);
                RefreshLayoutPreview();
            }

            return _layoutPreview;
        }
    }

    /// <summary>표본 전투를 다시 그린다. 이미 만들어진 뒤에만 도는 게 요점 — 설정 하나 바꿀 때마다
    /// 열지도 않은 미리보기를 만들 이유가 없다.</summary>
    private void RefreshLayoutPreview()
    {
        if (_layoutPreview is null)
        {
            return;
        }

        _layoutPreview.RefreshLayout();
        _layoutPreview.Update(LayoutPreviewSample.Report(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    /// <summary>
    /// UI 분리모드. 켜면 보스칸과 미터 행이 독립 창으로 떨어져 나가고 본체는 숨는다.
    /// 레이아웃과 직교하는 축이라 3×2 여섯 조합이 전부 성립한다.
    /// </summary>
    public bool SplitUiMode
    {
        get => _settings.SplitUiMode;
        set
        {
            _settings.SplitUiMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TaskbarModeEnabled)); // 분리모드에선 작업표시줄 모드가 잠긴다
        }
    }
    public bool IsMinimal { get => _settings.IsMinimal; set { _settings.IsMinimal = value; OnPropertyChanged(); } }
    public bool ShowCombatTimerInMinimal { get => _settings.ShowCombatTimerInMinimal; set { _settings.ShowCombatTimerInMinimal = value; OnPropertyChanged(); } }
    public bool ShowTargetInfoInMinimal { get => _settings.ShowTargetInfoInMinimal; set { _settings.ShowTargetInfoInMinimal = value; OnPropertyChanged(); } }
    public bool ShowServerTag { get => _settings.ShowServerTag; set { _settings.ShowServerTag = value; OnPropertyChanged(); } }

    public string TierEffects
    {
        get => _settings.TierEffects;
        set
        {
            _settings.TierEffects = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TierDetailEnabled));
        }
    }

    /// <summary>The per-row toggles only mean something while the decoration is on at all.</summary>
    public bool TierDetailEnabled => _settings.TierEffects != "off";

    public bool TierShowOthers { get => _settings.TierShowOthers; set { _settings.TierShowOthers = value; OnPropertyChanged(); } }

    public bool TierShowSelfChip { get => _settings.TierShowSelfChip; set { _settings.TierShowSelfChip = value; OnPropertyChanged(); } }

    // ---- 닉네임 효과 (후원자 · 랭커) ----

    public IReadOnlyList<SettingOption> NameFxModes { get; } = new[]
    {
        new SettingOption("끔", "off"),
        new SettingOption("색상만 (움직임 없음)", "static"),
        new SettingOption("애니메이션", "animated"),
    };

    public string NameFxMode
    {
        get => _settings.NameFxMode;
        set
        {
            _settings.NameFxMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NameFxDetailEnabled));
            OnPropertyChanged(nameof(NameFxAnimated));
            SyncNameFxPreview();
        }
    }

    // ---- 내 연출 고르기 ----

    private IReadOnlyList<NameFxChoiceViewModel> _myEffectChoices = Array.Empty<NameFxChoiceViewModel>();

    /// <summary>내 캐릭터가 고를 수 있는 닉네임 효과. 자격이 없으면 빈 목록이다.</summary>
    public IReadOnlyList<NameFxChoiceViewModel> MyEffectChoices
    {
        get => _myEffectChoices;
        private set => Set(ref _myEffectChoices, value);
    }

    private IReadOnlyList<NameFxChoiceViewModel> _myGaugeChoices = Array.Empty<NameFxChoiceViewModel>();

    /// <summary>랭커 자격이 있을 때만 채워진다.</summary>
    public IReadOnlyList<NameFxChoiceViewModel> MyGaugeChoices
    {
        get => _myGaugeChoices;
        private set => Set(ref _myGaugeChoices, value);
    }

    private string _myFxStatus = string.Empty;

    public string MyFxStatus { get => _myFxStatus; private set => Set(ref _myFxStatus, value); }

    private string _myFxNotice = string.Empty;

    /// <summary>
    /// 고르기 **바로 아래**에 붙는 줄: 방금 누른 결과, 또는 왜 지금은 안 바뀌는지.
    /// <para>왜 따로 있는가 — 결과 문구를 <see cref="NameFxStatus"/>(맨 아래 '후원자 목록 갱신' 줄)로 보내던 때는
    /// 칩과 문구 사이에 미리보기 카드 9장과 슬라이더 네 개가 끼어 있어, 누른 사람 화면에서는 사유가 스크롤 밖이었다.
    /// 실패가 조용해지면 남는 건 "눌리기만 하고 아무 일도 안 난다" 뿐이고, 그게 실제로 접수된 제보다.</para>
    /// </summary>
    public string MyFxNotice { get => _myFxNotice; private set { Set(ref _myFxNotice, value); OnPropertyChanged(nameof(MyFxNoticeVisibility)); } }

    public Visibility MyFxNoticeVisibility => MyFxNotice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>고르기 결과를 픽커 옆에 남긴다. 다음 <see cref="RefreshMyNameFx"/> 가 지우지 않도록 유지 플래그를 쓴다.</summary>
    public void SetMyFxNotice(string message) => MyFxNotice = message;

    /// <summary>자격이 없으면 못 고르게만 한다 — 섹션을 숨기면 "받으면 뭐가 생기는지" 를 알 수 없다.</summary>
    public bool MyFxEnabled => MyEffectChoices.Count > 0;

    /// <summary>못 고르는 상태라는 걸 눈으로도 알 수 있게. IsEnabled 만으로는 버튼 색이 크게 안 죽는다.</summary>
    public double MyFxOpacity => MyFxEnabled ? 1.0 : 0.45;

    public Visibility MyGaugeVisibility => MyGaugeChoices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 내 캐릭터의 자격을 읽어 선택지를 만든다.
    /// <para>자격 판정은 서버가 이미 내려 명단의 <c>k</c> 에 실어 보낸다 — 미터는 그 값만 읽는다.
    /// 여기에 "누가 무슨 자격인가" 규칙을 두면 서버와 두 벌이 되고, 갈리는 순간 사용자는 고를 수 있는데
    /// 저장이 거부되는 상태를 만난다.</para>
    /// </summary>
    public void RefreshMyNameFx(bool keepNotice = false)
    {
        StatsOwnCharacter own = _services.StatsBuilder.OwnCharacter();
        string? hash = own.Detected ? StatsIdentity.CharacterIdentityHash(own.Server, own.Nickname) : null;
        NameFxEntry? grant = hash is null ? null : _services.NameFx.Roster.Find(hash);
        _myFxHash = hash;
        _myFxRoster = _services.NameFx.Roster;

        if (grant is null)
        {
            // 고를 수 있는 게 없어도 목록은 비운 채로 두고 섹션은 남긴다. 왜 못 고르는지는 상태줄이
            // 말한다 — 인식 전인지, 자격이 없는지는 사용자가 할 일이 다르다.
            MyEffectChoices = Array.Empty<NameFxChoiceViewModel>();
            MyGaugeChoices = Array.Empty<NameFxChoiceViewModel>();
            MyFxStatus = own.Detected
                ? "지금 캐릭터에는 스킨 자격이 없습니다. 위 안내를 참고해 주세요."
                : "캐릭터를 인식하면 내 스킨을 고를 수 있습니다.";
            if (!keepNotice)
            {
                MyFxNotice = string.Empty;
            }

            RaiseMyNameFxState();
            return;
        }

        bool light = _skin.IsLight;
        MyEffectChoices = NameFxPalette.ChoicesFor(grant.Kind)
            .Select(e => new NameFxChoiceViewModel(e.Id, e.Name, NameFxPalette.For(e.Id, light).NameFill,
                string.Equals(e.Id, grant.EffectId, StringComparison.Ordinal)))
            .ToArray();
        MyGaugeChoices = NameFxPalette.GaugeChoicesFor(grant.Kind)
            .Select(e => new NameFxChoiceViewModel(e.Id, e.Name, NameFxPalette.For(e.Id, light).NameFill,
                string.Equals(e.Id, grant.GaugeId, StringComparison.Ordinal)))
            .ToArray();
        // dev 빌드에서는 '다른 사람에게도 보인다'가 사실이 아니다. 문구를 그대로 두면 서버에 반영된
        // 것으로 오해하게 된다.
        string reach = _services.NameFx.LocalChoiceOnly
            ? "고른 것은 이 PC 에만 적용됩니다 (dev 빌드 — 서버로 보내지 않음)."
            : "고르면 다른 사람에게도 그대로 보입니다.";
        MyFxStatus = grant.Kind switch
        {
            "both" => $"{own.Nickname} — 후원자 · 랭커. {reach}",
            "ranker" => $"{own.Nickname} — 랭커. {reach}",
            _ => $"{own.Nickname} — 후원자. {reach}",
        };

        // 🔑 자격(명단에 실렸는가)과 소유 증명(이 설치본이 그 캐릭터로 전투를 올린 적이 있는가)은 서로 다른
        // 사실이고, 서버는 둘 다 요구한다 — 명단은 아무 uid 나 실을 수 있으므로 소유 증명이 없으면 남의
        // 캐릭터 스킨을 바꿀 수 있게 된다. 그런데 랭커 자격은 **남이 올린 전투의 참가자** 기록에서도 나오기
        // 때문에, 자기 설치본으로는 한 번도 올린 적 없는 캐릭터가 명단에 실릴 수 있다. 그 캐릭터는 칩이 다
        // 보이는데 누르면 매번 403 이 되고, 사용자에게는 "이 캐릭터만 안 된다" 로만 보인다.
        // 그래서 누르기 **전에** 말해 준다. 캐시가 낡아 있을 수 있으니 막지는 않는다 — 판정은 서버가 한다.
        if (!keepNotice)
        {
            MyFxNotice = hash is not null && !_services.Consent.HasGrant(hash)
                ? "⚠ 이 기기에서 이 캐릭터로 전투를 업로드한 기록이 없어 지금은 바꿀 수 없어요. "
                  + "이 캐릭터로 던전 전투를 한 번 올리고 나면 바로 열립니다. (지금 적용된 스킨은 그대로 유지됩니다.)"
                : string.Empty;
        }

        RaiseMyNameFxState();
    }

    /// <summary>마지막으로 픽커를 그린 캐릭터와 명단. 둘 중 하나가 바뀌면 다시 그려야 한다 —
    /// 창을 연 뒤에 캐릭터가 인식되거나 명단이 도착하면 픽커가 죽은 채로 남아 있었다.</summary>
    private string? _myFxHash;

    private NameFxRoster? _myFxRoster;

    /// <summary>캐릭터나 명단이 바뀌었을 때만 픽커를 다시 만든다. 2.5초 폴링에 그냥 얹으면 고르는 도중에
    /// 목록이 갈리므로, 바뀐 순간에만 도는 것이 조건이다.</summary>
    public void RefreshMyNameFxIfStale()
    {
        StatsOwnCharacter own = _services.StatsBuilder.OwnCharacter();
        string? hash = own.Detected ? StatsIdentity.CharacterIdentityHash(own.Server, own.Nickname) : null;
        if (!string.Equals(hash, _myFxHash, StringComparison.Ordinal) || !ReferenceEquals(_services.NameFx.Roster, _myFxRoster))
        {
            RefreshMyNameFx();
        }
    }

    /// <summary>응답 본문의 <c>error</c> 코드. 못 읽으면 빈 문자열 — 그 경우 상태 코드만으로 판단한다.</summary>
    public static string ErrorCodeOf(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return string.Empty;
        }

        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(responseBody);
            return doc.RootElement.TryGetProperty("error", out System.Text.Json.JsonElement e)
                ? e.GetString() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>지금 적용된 닉네임 효과 id. 게이지만 바꿀 때 효과를 같이 보내야 해서 필요하다.</summary>
    public string? CurrentEffectId => MyEffectChoices.FirstOrDefault(c => c.IsCurrent)?.Id;

    private void RaiseMyNameFxState()
    {
        OnPropertyChanged(nameof(MyFxEnabled));
        OnPropertyChanged(nameof(MyFxOpacity));
        OnPropertyChanged(nameof(MyGaugeVisibility));
    }

    /// <summary>네트워크를 탄다. 호출부가 UI 스레드를 벗어나서 부른다.</summary>
    public string SubmitMyNameFx(string effectId, string? gaugeId)
    {
        StatsOwnCharacter own = _services.StatsBuilder.OwnCharacter();
        string? hash = own.Detected ? StatsIdentity.CharacterIdentityHash(own.Server, own.Nickname) : null;
        if (hash is null)
        {
            return "캐릭터가 인식되지 않았습니다.";
        }

        if (!MyFxEnabled)
        {
            // 버튼이 비활성이라 정상 경로로는 닿지 않지만, 자격 없는 요청을 서버까지 보내 401/403 을
            // 받아 오는 건 사용자에게 아무 도움이 안 된다.
            return "지금 캐릭터에는 스킨 자격이 없습니다.";
        }

        try
        {
            NameFxChoiceResponse response = _services.NameFx.SubmitChoice(hash, effectId, gaugeId, _services.Version);
            if (!response.Ok)
            {
                return "서버가 이 선택을 거절했습니다.";
            }

            return _services.NameFx.LocalChoiceOnly
                ? "적용했습니다. (dev 빌드 — 이 PC 에만 보이고 서버로 보내지 않습니다)"
                : "적용했습니다.";
        }
        catch (StatsApiException ex)
        {
            // ⚠ 상태 코드만 보면 안 된다. 403 이 두 가지다 — 소유 증명 실패(not_your_character)와
            // 자격 없음(not_entitled). 서버가 둘을 같은 코드로 주는 건 의도된 것이고(자격 유무 자체가
            // 정보라 구분해 주면 열거 오라클이 된다), 대신 본문의 error 로 갈린다.
            //
            // 이게 닿는 실제 경로: 명단은 최대 한 시간 낡는다. 그 사이 부여가 회수되면 미터는 아직
            // 선택 버튼을 보여 주고, 누르면 not_entitled 가 온다. 그때 "전투를 올린 기록이 없다" 고
            // 말하면 사실이 아닌 데다 사용자를 엉뚱한 곳으로 보낸다.
            string code = ErrorCodeOf(ex.ResponseBody);
            return (ex.StatusCode, code) switch
            {
                (401, _) => "설치 서명이 확인되지 않았습니다. 전투를 한 번 업로드한 뒤 다시 시도해 주세요.",
                (403, "not_your_character") =>
                    "이 기기에서 이 캐릭터로 전투를 업로드한 기록이 없어 바꿀 수 없어요. "
                    + "이 캐릭터로 던전 전투를 한 번 올리고 나면 바로 열립니다. (지금 적용된 스킨은 그대로 유지됩니다.)",
                (403, _) => "이 캐릭터에는 지금 스킨 자격이 없습니다. 목록을 새로고침해 주세요.",
                (400, _) => "지금 자격으로 고를 수 없는 스킨입니다. 목록을 새로고침해 주세요.",
                _ => "지금은 변경할 수 없습니다. 잠시 뒤 다시 시도해 주세요.",
            };
        }
        catch
        {
            return "지금은 변경할 수 없습니다. 잠시 뒤 다시 시도해 주세요.";
        }
    }

    public IReadOnlyList<SettingOption> NameFxScopes { get; } = new[]
    {
        new SettingOption("명단에 있는 모든 캐릭터", "all"),
        new SettingOption("내 캐릭터만", "self"),
    };

    /// <summary>
    /// 연출을 누구에게 그릴지.
    /// <para>부여는 캐릭터에 붙고 명단은 모두가 같은 것을 받으므로, 같은 전투에 있는 다른 미터
    /// 사용자에게도 그 사람이 고른 연출이 그대로 보인다. 이 설정은 <b>보는 쪽</b>의 취향일 뿐이고,
    /// 남에게 무엇이 보이는지는 바꾸지 않는다.</para>
    /// <para>기존 두 토글(<c>nameFx.showSelf</c>·<c>nameFx.showOthers</c>) 위에 얹는다. 새 키를
    /// 만들지 않는 이유는 설정 백업·취소 스냅샷·키 카탈로그가 전부 그 두 키를 이미 알고 있어서다 —
    /// 키를 하나 더 만들면 세 곳이 같이 늘어나고, 그 중 하나를 빠뜨리는 게 이 파일의 단골 결함이다.</para>
    /// </summary>
    public string NameFxScope
    {
        get => _settings.NameFxShowOthers ? "all" : "self";
        set
        {
            bool all = value != "self";
            // 내 행은 항상 켠다. "내 캐릭터만"에서 내 것마저 꺼지면 고를 이유가 없는 상태가 된다.
            _settings.NameFxShowSelf = true;
            _settings.NameFxShowOthers = all;
            OnPropertyChanged();
        }
    }

    /// <summary>The per-row toggles and the brightness slider only mean something while effects are on at all.</summary>
    public bool NameFxDetailEnabled => _settings.NameFxMode != "off";

    /// <summary>Speed only applies to the moving variants.</summary>
    public bool NameFxAnimated => _settings.NameFxMode == "animated";

    public bool NameFxShowSelf { get => _settings.NameFxShowSelf; set { _settings.NameFxShowSelf = value; OnPropertyChanged(); } }

    public bool NameFxShowOthers { get => _settings.NameFxShowOthers; set { _settings.NameFxShowOthers = value; OnPropertyChanged(); } }

    public int NameFxSpeedPercent
    {
        get => _settings.NameFxSpeedPercent;
        set { _settings.NameFxSpeedPercent = value; OnPropertyChanged(); SyncNameFxPreview(); }
    }

    /// <summary>
    /// Brightness. The setter only stores; rebuilding the shared brushes is <see cref="CommitNameFxBrightness"/>,
    /// called on drag-end — recolouring every stop on each slider tick would rebuild the palette dozens of times
    /// per drag AND rewrite the whole properties file each time.
    /// </summary>
    public int NameFxBrightnessPercent
    {
        get => _settings.NameFxBrightnessPercent;
        set { _settings.NameFxBrightnessPercent = value; OnPropertyChanged(); }
    }

    public void CommitNameFxBrightness() => NameFxSheen.Rebuild(_settings.NameFxBrightnessPercent);

    /// <summary>
    /// Keep the preview strip moving while it is on screen. The sweep timer is demand-driven from the meter's
    /// row rebuild, and a user deciding about this setting has no decorated row anywhere — so without a second
    /// demand source the strip renders frozen and the animation setting cannot be judged at all.
    /// <para>Gated on the colour tab being selected AND the mode being "animated", so the preview tells the
    /// truth: switching to "색상만" visibly stops it, which is exactly what that option does.</para>
    /// </summary>
    private void SyncNameFxPreview()
    {
        // Low-spec is checked HERE too, not only on the row path. The brushes are process-wide singletons, so a
        // preview that grabbed the clock would animate the meter's own rows as well — which is precisely what
        // low-spec mode exists to prevent.
        NameFxSheen.SetLowSpec(_settings.LowSpecMode);
        NameFxSheen.SetPreviewDemand(
            string.Equals(SelectedNav, "theme", StringComparison.Ordinal) && _settings.NameFxMode == "animated",
            _settings.NameFxSpeedPercent);
    }

    /// <summary>Drop the preview's claim on the sweep timer when the settings window closes.</summary>
    public void StopNameFxPreview() => NameFxSheen.SetPreviewDemand(false, _settings.NameFxSpeedPercent);

    /// <summary>
    /// Every catalogue effect, drawn on a sample nickname. Grants come from the server, so without this a user
    /// has no way to see what the setting even does — and the preview is also how someone decides whether the
    /// motion bothers them before turning it off.
    /// </summary>
    public IReadOnlyList<NameFxSampleViewModel> NameFxSamples { get; private set; } = Array.Empty<NameFxSampleViewModel>();

    /// <summary>DPS 게이지 스킨 미리보기(후원자·랭커 양쪽). 닉네임 연출과 목록을 나눈 이유는 칠하는 자리가
    /// 달라서다 — 하나는 글자, 하나는 행을 가로지르는 막대다.</summary>
    public IReadOnlyList<GaugeSkinSampleViewModel> GaugeSkinSamples { get; private set; } = Array.Empty<GaugeSkinSampleViewModel>();

    public bool NameFxGauge { get => _settings.NameFxGauge; set { _settings.NameFxGauge = value; OnPropertyChanged(); } }

    /// <summary>게이지 형태가 '없음'이면 칠할 막대가 존재하지 않는다 — 토글을 켜 봐야 아무 일도 일어나지
    /// 않으므로 비활성화하고 이유를 밝힌다.</summary>
    public bool GaugeSkinApplicable => _settings.BarStyle != "none";

    private void RebuildNameFxSamples(bool isLight)
    {
        NameFxSamples = NameFxPalette.NameEffects
            .Select(e => new NameFxSampleViewModel(
                e.Name,
                e.Kind == NameFxPalette.NameFxKind.Ranker ? "랭커" : "후원자",
                NameFxPalette.For(e.Id, isLight).NameFill))
            .ToArray();
        // 같은 행 내용에 스킨만 갈아 끼운다 — 비교해야 할 변수가 스킨 하나뿐이어야 한다.
        GaugeSkinSamples = NameFxPalette.GaugeSkins
            .Select(e => new GaugeSkinSampleViewModel(
                Id: e.Id,
                Name: e.Name,
                Kind: e.Kind == NameFxPalette.NameFxKind.Ranker ? "랭커" : "후원자",
                Fill: NameFxPalette.For(e.Id, isLight).NameFill,
                RailBrush: OverlayViewModel.RowGradient(Theme.UserBarFrom, Theme.UserBarTo),
                IconSource: JoinIcons.Job("마도성"),
                Rank: "1",
                Nickname: "와플장인",
                ServerTag: "[시엘]",
                PowerText: "656.0k",
                DpsText: "408,239/s",
                PercentText: "35.1%",
                BarRatio: 0.55,
                BarRest: 0.45,
                GaugeOpacity: 0.58))
            .ToArray();
        OnPropertyChanged(nameof(NameFxSamples));
        OnPropertyChanged(nameof(GaugeSkinSamples));
    }

    private string _tierStatus = string.Empty;

    /// <summary>One line under the refresh button: what we have and how old it is.</summary>
    public string TierStatus { get => _tierStatus; private set { _tierStatus = value; OnPropertyChanged(); } }

    /// <summary>Re-read the tier service state for display. Cheap — no network.</summary>
    public void RefreshTierStatus()
    {
        TierServiceStatus status = _services.Tier.Status();
        if (!status.HasArtifact)
        {
            TierStatus = status.Failures > 0
                ? $"티어 지표를 받지 못했어요 (실패 {status.Failures}회{FormatReason(status.LastError)})"
                : "티어 지표를 받지 못했어요.";
            return;
        }

        TimeSpan age = _services.Tier.Age;
        string freshness = age.TotalHours < 1
            ? "방금 갱신"
            : age.TotalDays < 1
                ? $"{(int)age.TotalHours}시간 전 기준"
                : $"{(int)age.TotalDays}일 전 기준";
        string stale = age.TotalDays >= 14 ? " · 오래된 기준이에요" : string.Empty;
        TierStatus = $"던전 {status.Dungeons}개 · 보스 {status.Mobs}종 · 구간 {status.Rows:N0}개 · {freshness}{stale}";
    }

    /// <summary>Settings' 새로고침 button. Returns false when the 60s cooldown swallowed it.</summary>
    public bool RequestTierRefresh() => _services.Tier.RequestManualRefresh();

    private string _nameFxStatus = string.Empty;

    /// <summary>One line under the 후원자 목록 갱신 button.</summary>
    public string NameFxStatus { get => _nameFxStatus; private set => Set(ref _nameFxStatus, value); }

    private long _nameFxNoticeUntilMs;

    /// <summary>Show a message about what the button just did, and hold it against the 2.5s status poll.
    /// Without the hold the poll overwrites it before it can be read, which is how a button ends up looking
    /// dead in exactly the case it is trying to explain.</summary>
    public void SetNameFxNotice(string message, int holdMs = 4000)
    {
        NameFxStatus = message;
        _nameFxNoticeUntilMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + holdMs;
    }

    /// <summary>Re-read the grant-list state for display. Cheap — no network.</summary>
    public void RefreshNameFxStatus()
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < _nameFxNoticeUntilMs)
        {
            return;
        }

        NameFxServiceStatus status = _services.NameFx.Status();
        if (status.UsingLocalFile)
        {
            NameFxStatus = $"로컬 파일에서 {status.Grants}명을 읽었습니다.";
            return;
        }

        if (!status.HasArtifact)
        {
            NameFxStatus = status.Failures > 0
                ? $"후원자 목록을 받지 못했어요 (실패 {status.Failures}회{FormatReason(status.LastError)})"
                : "아직 후원자 목록을 받지 않았어요. 미터를 켜고 잠시 뒤 자동으로 받아 옵니다.";
            return;
        }

        long age = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - status.FetchedAtMs);
        var span = TimeSpan.FromMilliseconds(age);
        string freshness = span.TotalHours < 1
            ? "방금 갱신"
            : span.TotalDays < 1
                ? $"{(int)span.TotalHours}시간 전 갱신"
                : $"{(int)span.TotalDays}일 전 갱신";
        NameFxStatus = $"연출이 적용된 캐릭터 {status.Grants}명 · {freshness}";
    }

    /// <summary>Settings' 후원자 목록 갱신 button. Returns false when the 60s cooldown swallowed it.</summary>
    public bool RequestNameFxRefresh() => _services.NameFx.RequestManualRefresh();

    private static string FormatReason(string? reason) => string.IsNullOrEmpty(reason) ? string.Empty : $", {reason}";
    public bool ShowAetherStatus { get => _settings.ShowAetherStatus; set { _settings.ShowAetherStatus = value; OnPropertyChanged(); } }
    public bool ShowLatencyIndicator { get => _settings.ShowLatencyIndicator; set { _settings.ShowLatencyIndicator = value; OnPropertyChanged(); } }
    public bool VrrCompatMode { get => _settings.VrrCompatMode; set { _settings.VrrCompatMode = value; OnPropertyChanged(); } }
    public bool ShowBuffUi { get => _settings.ShowBuffUi; set { _settings.ShowBuffUi = value; OnPropertyChanged(); } }
    public bool BuffUiTransparent { get => _settings.BuffUiTransparent; set { _settings.BuffUiTransparent = value; OnPropertyChanged(); } }
    /// <summary>버프 아이콘 배율(%). 설계 기준인 40px 을 100% 로 잡고 80~200% 로 조절한다. 저장은 px 그대로다 —
    /// buffUi.iconSize 는 디자인 공유코드·프리셋 blob 에 실려 구버전과 오가므로 단위를 바꿀 수 없다
    /// (<see cref="MeterSettings.BuffUiIconSize"/> 주석 참고). 슬라이더 눈금 5% = 2px 라 왕복이 무손실이고,
    /// 종전 "작게" 값 34px 은 정확히 85% 라 눈금 위에 그대로 착지한다(설정창을 여는 것만으로 재기록되지 않음).</summary>
    public int BuffIconScalePercent
    {
        get => Math.Clamp(_settings.BuffUiIconSize * 100 / BuffIconBasePx, 80, 200);
        set { _settings.BuffUiIconSize = Math.Clamp(value, 80, 200) * BuffIconBasePx / 100; OnPropertyChanged(); }
    }

    /// <summary>100% 로 정의된 아이콘 px — <see cref="BuffOverlayViewModel.SetIconSize"/> 가 나누는 값과 같다.</summary>
    private const int BuffIconBasePx = 40;

    /// <summary>Buff overlay countdown-text color (hex), bound to the color-swatch picker.</summary>
    public string BuffTextColor { get => _settings.BuffUiTextColor; set { _settings.BuffUiTextColor = value; OnPropertyChanged(); } }
    public bool BuffTtsOnStart { get => _settings.BuffTtsOnStart; set { _settings.BuffTtsOnStart = value; OnPropertyChanged(); } }
    public bool BuffTtsOnEnd { get => _settings.BuffTtsOnEnd; set { _settings.BuffTtsOnEnd = value; OnPropertyChanged(); } }
    public bool BuffEndWarning3s { get => _settings.BuffEndWarning3s; set { _settings.BuffEndWarning3s = value; OnPropertyChanged(); } }
    public bool BuffUiGrayOnCooldown { get => _settings.BuffUiGrayOnCooldown; set { _settings.BuffUiGrayOnCooldown = value; OnPropertyChanged(); } }

    // ---- 스킬 쿨타임 오버레이 ----
    public bool ShowCooldownUi { get => _settings.ShowCooldownUi; set { _settings.ShowCooldownUi = value; OnPropertyChanged(); } }
    public bool CooldownUiTransparent { get => _settings.CooldownUiTransparent; set { _settings.CooldownUiTransparent = value; OnPropertyChanged(); } }
    public string CooldownTextColor { get => _settings.CooldownUiTextColor; set { _settings.CooldownUiTextColor = value; OnPropertyChanged(); } }

    /// <summary>쿨타임 아이콘 배율(%). 버프 오버레이와 같은 규약 — 40px 을 100% 로 잡고 80~200%. 저장은 px.</summary>
    public int CooldownIconScalePercent
    {
        get => Math.Clamp(_settings.CooldownUiIconSize * 100 / BuffIconBasePx, 80, 200);
        set { _settings.CooldownUiIconSize = Math.Clamp(value, 80, 200) * BuffIconBasePx / 100; OnPropertyChanged(); }
    }

    /// <summary>한 줄에 놓을 최대 아이콘 수. 이 값이 창의 폭 상한이 되고, 폭 상한이 있어야만 WrapPanel 이 줄을
    /// 바꾼다 — 상한이 없으면 넘친 슬롯이 줄바꿈도 스크롤도 없이 조용히 사라진다.</summary>
    public int CooldownUiPerRow { get => _settings.CooldownUiPerRow; set { _settings.CooldownUiPerRow = value; OnPropertyChanged(); } }

    /// <summary>"스킬 고르기" 버튼 — 쿨타임 픽커 플라이아웃을 여닫는다. App 이 배선한다(창 위치와 수명을
    /// 아는 쪽이 App 이고, 다른 버튼들도 전부 이 모양이다).</summary>
    public Action? CooldownPickerRequested { get; set; }

    public void OpenCooldownPicker() => CooldownPickerRequested?.Invoke();

    /// <summary>버프 아이콘 우하단에 스킬 레벨 배지를 그린다(기본 켜짐). 레벨을 못 읽은 버프는 배지 없음.</summary>
    public bool BuffUiShowLevel { get => _settings.BuffUiShowLevel; set { _settings.BuffUiShowLevel = value; OnPropertyChanged(); } }
    public bool ShowOtherPlayerBuffs { get => _settings.ShowOtherPlayerBuffs; set { _settings.ShowOtherPlayerBuffs = value; OnPropertyChanged(); } }

    /// <summary>버프 오버레이 정렬 모드를 ComboBox의 SelectedIndex(0 적용순 / 1 남은시간순 / 2 이름순)로
    /// 노출한다. 맨 앞 고정한 버프는 이 모드와 무관하게 항상 앞에 온다.</summary>
    public int BuffUiSortModeIndex
    {
        get => _settings.BuffUiSortMode switch
        {
            BuffOverlayOrder.Remaining => 1,
            BuffOverlayOrder.Name => 2,
            _ => 0,
        };
        set
        {
            _settings.BuffUiSortMode = value switch
            {
                1 => BuffOverlayOrder.Remaining,
                2 => BuffOverlayOrder.Name,
                _ => BuffOverlayOrder.Applied,
            };
            OnPropertyChanged();
        }
    }

    private BuffPickerViewModel? _buffPicker;
    /// <summary>The per-job buff picker, embedded in the 버프 알림 settings tab. Built lazily and disposed when
    /// the window closes (see <see cref="DisposeBuffPicker"/>).</summary>
    public BuffPickerViewModel BuffPicker => _buffPicker ??= new BuffPickerViewModel(_services.Data, _settings);

    // ---- buff presets (three saved copies of the whole buff config; the active one IS the live settings) ----

    /// <summary>The preset chips, shown on both the 전투 보조 and 버프 알림 tabs.</summary>
    public ObservableCollection<BuffPresetSlotViewModel> PresetSlots { get; } = new();

    /// <summary>The active slot's name, edited inline. Blank falls back to "프리셋 N".</summary>
    public string ActivePresetName
    {
        get => _presets.ActiveName;
        set
        {
            _presets.RenameSlot(_presets.ActiveIndex, value);
            SyncPresetSlots();
            OnPropertyChanged();
        }
    }

    /// <summary>Apply a preset: push the slot into the live settings + the buff store, resync the picker, and
    /// re-announce every bound buff property. That last step is not optional — <c>MeterSettings</c> setters
    /// no-op on an unchanged value, so the manager's writes cannot be relied on to refresh these controls.</summary>
    public void SelectPreset(int index)
    {
        _presets.SelectSlot(index);
        _buffPicker?.Reload(); // else its stale cached sets would overwrite the preset on the next edit
        SyncPresetSlots();
        // 프리셋 전환은 취소 대상이 아니다(고르는 즉시 저장된다). 그러니 취소 스냅샷의 아이콘 크기 기준도
        // 새 슬롯 값으로 옮겨 둔다 — 안 그러면 전환 뒤 취소가 옛 슬롯의 px 를 되쓰고, BuffPresetManager 가
        // 그 값을 지금 활성인 슬롯에 캡처해 저장해 버려 방금 고른 프리셋의 크기가 영구히 사라진다.
        // (아이콘 크기는 스냅샷에 든 키 중 유일하게 프리셋이 소유하는 키다.)
        _snapshot = _snapshot with { BuffUiIconSize = _settings.BuffUiIconSize };

        OnPropertyChanged(nameof(BuffUiTransparent));
        OnPropertyChanged(nameof(BuffIconScalePercent));
        OnPropertyChanged(nameof(BuffTextColor));
        OnPropertyChanged(nameof(BuffTtsOnStart));
        OnPropertyChanged(nameof(BuffTtsOnEnd));
        OnPropertyChanged(nameof(BuffEndWarning3s));
        OnPropertyChanged(nameof(BuffUiGrayOnCooldown));
        OnPropertyChanged(nameof(BuffUiShowLevel));
        OnPropertyChanged(nameof(ShowOtherPlayerBuffs));
        OnPropertyChanged(nameof(ActivePresetName));
    }

    /// <summary>쿨타임 프리셋 칩. 슬롯 뷰모델은 버프 쪽과 같은 타입을 쓴다(인덱스·이름·활성만 담는
    /// 껍데기다). ⚠️ 바인딩 이름은 반드시 달라야 한다 — 같은 이름을 쓰면 두 바가 서로의 컬렉션을 그린다.</summary>
    public ObservableCollection<BuffPresetSlotViewModel> CooldownPresetSlots { get; } = new();

    /// <summary>쿨타임 프리셋 바를 그릴지. 오버레이 배선 없이 만들어진 뷰모델(UiPreview·테스트)에서는 false.</summary>
    public bool HasCooldownPresets => _cooldownPresets is not null;

    /// <summary>활성 쿨타임 슬롯의 이름, 인라인 편집. 빈 이름은 "프리셋 N" 으로 돌아간다.</summary>
    public string ActiveCooldownPresetName
    {
        get => _cooldownPresets?.ActiveName ?? string.Empty;
        set
        {
            if (_cooldownPresets is not { } cd)
            {
                return;
            }

            cd.RenameSlot(cd.ActiveIndex, value);
            SyncCooldownPresetSlots();
            OnPropertyChanged();
        }
    }

    /// <summary>쿨타임 프리셋 적용. 버프 쪽과 같은 세 가지 후속이 필요하다 — 픽커 칩 다시 읽기, 취소 스냅샷
    /// 이설, 바인딩 재방송(<c>MeterSettings</c> setter 는 같은 값이면 이벤트를 안 내므로 컨트롤이 안 갱신된다).</summary>
    public void SelectCooldownPreset(int index)
    {
        if (_cooldownPresets is not { } cd)
        {
            return;
        }

        cd.SelectSlot(index);
        SyncCooldownPresetSlots();

        // 🔴 프리셋 전환은 취소 대상이 아니다(고르는 즉시 저장된다). 그런데 이 세 키는 취소 스냅샷에 들어
        // 있으므로, 옮겨 두지 않으면 전환 뒤 취소가 옛 슬롯 값을 되쓰고 매니저가 그것을 지금 활성인 슬롯에
        // 캡처해 저장한다 — 방금 고른 프리셋의 세 값이 영구히 사라진다. 버프는 아이콘 크기 하나뿐이라
        // 한 줄이었지만 여기는 셋이다.
        _snapshot = _snapshot with
        {
            CooldownUiIconSize = _settings.CooldownUiIconSize,
            CooldownUiPerRow = _settings.CooldownUiPerRow,
            CooldownUiTextColor = _settings.CooldownUiTextColor,
        };

        OnPropertyChanged(nameof(CooldownUiTransparent));
        OnPropertyChanged(nameof(CooldownIconScalePercent));
        OnPropertyChanged(nameof(CooldownTextColor));
        OnPropertyChanged(nameof(CooldownUiPerRow));
        OnPropertyChanged(nameof(ActiveCooldownPresetName));
    }

    private void SyncCooldownPresetSlots()
    {
        if (_cooldownPresets is not { } cd)
        {
            return;
        }

        IReadOnlyList<string> names = cd.Names;
        foreach (BuffPresetSlotViewModel slot in CooldownPresetSlots)
        {
            slot.Name = names[slot.Index];
            slot.SyncActive(slot.Index == cd.ActiveIndex);
        }
    }

    private void SyncPresetSlots()
    {
        IReadOnlyList<string> names = _presets.Names;
        foreach (BuffPresetSlotViewModel slot in PresetSlots)
        {
            slot.Name = names[slot.Index];
            slot.SyncActive(slot.Index == _presets.ActiveIndex);
        }
    }

    /// <summary>Release the picker's catalog subscription when the settings window closes.</summary>
    public void DisposeBuffPicker() => _buffPicker?.Dispose();

    /// <summary>Wired by App: trigger an update check (results surface in the toast).</summary>
    public Action? CheckUpdateRequested { get; set; }
    public void CheckForUpdate() => CheckUpdateRequested?.Invoke();

    /// <summary>Wired by App: reset a panel position ("meter" / "join" / "history").</summary>
    public Action<string>? ResetPositionRequested { get; set; }
    public void ResetMeterPosition() => ResetPositionRequested?.Invoke("meter");
    public void ResetJoinPosition() => ResetPositionRequested?.Invoke("join");
    public void ResetHistoryPosition() => ResetPositionRequested?.Invoke("history");
    public void ResetAetherPosition() => ResetPositionRequested?.Invoke("aether");

    // ---- overlay tab (live) ----
    public double MeterOpacity { get => _settings.MeterOpacity; set { _settings.MeterOpacity = value; OnPropertyChanged(); } }

    /// <summary>
    /// 설정 원본 그 자체. 미터 헤더의 투명도 슬라이더가 <c>Settings.MeterOpacity</c> 로 직접 쓰기 때문에,
    /// 설정창 슬라이더도 <b>같은 경로</b>로 묶어야 두 화면이 한 값을 본다.
    ///
    /// <para>🔑 설정창만 <see cref="MeterOpacity"/> 래퍼를 쓰면 안 된다. 헤더에서 끌었을 때 래퍼는
    /// PropertyChanged 를 쏘지 않으므로, 설정창을 열어 둔 채 헤더를 움직이면 두 슬라이더가 서로 다른
    /// 위치를 가리킨다 — 그 상태에서 설정창 쪽을 건드리면 헤더에서 맞춰 둔 값이 조용히 되돌아간다.</para>
    /// </summary>
    public MeterSettings Settings => _settings;
    public bool MultiMonitorMode { get => _settings.MultiMonitorMode; set { _settings.MultiMonitorMode = value; OnPropertyChanged(); } }
    public bool ShowJoinPanel { get => _settings.ShowJoinPanel; set { _settings.ShowJoinPanel = value; OnPropertyChanged(); } }
    public bool ShowPreCombatRoster { get => _settings.ShowPreCombatRoster; set { _settings.ShowPreCombatRoster = value; OnPropertyChanged(); } }
    // (Light mode is now a skin — "light" in the Skin list — not a separate overlayTheme toggle.)

    // ---- alarms (live; persisted immediately, not part of the Cancel snapshot) ----
    public bool ShugoAlarmEnabled { get => _settings.ShugoAlarmEnabled; set { _settings.ShugoAlarmEnabled = value; OnPropertyChanged(); } }
    public bool ShugoLead10 { get => _settings.ShugoLead10; set { _settings.ShugoLead10 = value; OnPropertyChanged(); } }
    public bool ShugoLead5 { get => _settings.ShugoLead5; set { _settings.ShugoLead5 = value; OnPropertyChanged(); } }
    public bool ShugoLead1 { get => _settings.ShugoLead1; set { _settings.ShugoLead1 = value; OnPropertyChanged(); } }
    public bool ShugoLeadStart { get => _settings.ShugoLeadStart; set { _settings.ShugoLeadStart = value; OnPropertyChanged(); } }
    public bool AlarmSoundEnabled { get => _settings.AlarmSoundEnabled; set { _settings.AlarmSoundEnabled = value; OnPropertyChanged(); } }
    public bool TtsEnabled { get => _settings.TtsEnabled; set { _settings.TtsEnabled = value; OnPropertyChanged(); } }
    public double AlarmVolume { get => _settings.AlarmVolume; set { _settings.AlarmVolume = value; OnPropertyChanged(); } }

    /// <summary>Settings "소리 테스트" button: play the alarm chime at the current volume.</summary>
    public void TestAlarmSound() => AlarmSound.Play(_settings.AlarmVolume);

    /// <summary>Settings "음성 테스트" button: speak a sample line (falls back to the chime if TTS fails).</summary>
    /// <summary>The wording must be a line the shipped pack actually contains — this button exists to preview
    /// the chosen voice, and a near-miss (a full stop where the pack has a comma) would quietly demo the online
    /// fallback instead. <c>AlarmToastViewModel.SetShugo</c> is the source of this exact string.</summary>
    public void TestTts() => TtsSpeech.Speak("슈고 페스타, 5분 뒤 시작합니다", _settings.AlarmVolume);

    // ---- field-boss respawn reminder ----
    public bool FieldBossAlarmEnabled { get => _settings.FieldBossAlarmEnabled; set { _settings.FieldBossAlarmEnabled = value; OnPropertyChanged(); } }
    public bool FieldBossLead5 { get => _settings.FieldBossLead5; set { _settings.FieldBossLead5 = value; OnPropertyChanged(); } }
    public bool FieldBossLead10 { get => _settings.FieldBossLead10; set { _settings.FieldBossLead10 = value; OnPropertyChanged(); } }
    public bool FieldBossLead30 { get => _settings.FieldBossLead30; set { _settings.FieldBossLead30 = value; OnPropertyChanged(); } }
    public bool FieldBossAlarmMuteInCombat { get => _settings.FieldBossAlarmMuteInCombat; set { _settings.FieldBossAlarmMuteInCombat = value; OnPropertyChanged(); } }

    public bool GroggyAlarmEnabled { get => _settings.GroggyAlarmEnabled; set { _settings.GroggyAlarmEnabled = value; OnPropertyChanged(); } }

    public bool KairaAlarmEnabled { get => _settings.KairaAlarmEnabled; set { _settings.KairaAlarmEnabled = value; OnPropertyChanged(); } }
    public bool KairaLead10 { get => _settings.KairaLead10; set { _settings.KairaLead10 = value; OnPropertyChanged(); } }
    public bool KairaLead5 { get => _settings.KairaLead5; set { _settings.KairaLead5 = value; OnPropertyChanged(); } }
    public bool KairaLead1 { get => _settings.KairaLead1; set { _settings.KairaLead1 = value; OnPropertyChanged(); } }

    /// <summary>Build the field-boss alarm selection dialog, bound to the persisted disabled set.</summary>
    public FieldBossPickerWindow CreateFieldBossPicker() => new(new FieldBossPickerViewModel(_settings));

    // ---- custom alarms (CRUD list) ----
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();
    public IReadOnlyList<int> Minutes { get; } = Enumerable.Range(0, 60).ToList();

    public ObservableCollection<CustomAlarmRow> CustomAlarmRows { get; } = new();
    public bool HasCustomAlarms => CustomAlarmRows.Count > 0;

    private string _newAlarmTitle = "알람";
    public string NewAlarmTitle { get => _newAlarmTitle; set => Set(ref _newAlarmTitle, value); }
    private int _newAlarmHour = 12;
    public int NewAlarmHour { get => _newAlarmHour; set => Set(ref _newAlarmHour, value); }
    private int _newAlarmMinute;
    public int NewAlarmMinute { get => _newAlarmMinute; set => Set(ref _newAlarmMinute, value); }

    private bool _daySun, _dayMon, _dayTue, _dayWed, _dayThu, _dayFri, _daySat;
    public bool DaySun { get => _daySun; set => Set(ref _daySun, value); }
    public bool DayMon { get => _dayMon; set => Set(ref _dayMon, value); }
    public bool DayTue { get => _dayTue; set => Set(ref _dayTue, value); }
    public bool DayWed { get => _dayWed; set => Set(ref _dayWed, value); }
    public bool DayThu { get => _dayThu; set => Set(ref _dayThu, value); }
    public bool DayFri { get => _dayFri; set => Set(ref _dayFri, value); }
    public bool DaySat { get => _daySat; set => Set(ref _daySat, value); }

    /// <summary>Rebuild the displayed alarm rows from settings (call on open + after each change).</summary>
    public void RefreshCustomAlarms()
    {
        CustomAlarmRows.Clear();
        foreach (CustomAlarm a in _settings.CustomAlarms)
        {
            CustomAlarmRows.Add(ToRow(a));
        }

        OnPropertyChanged(nameof(HasCustomAlarms));
    }

    public void AddCustomAlarm()
    {
        var days = new List<int>();
        if (_daySun) days.Add(0);
        if (_dayMon) days.Add(1);
        if (_dayTue) days.Add(2);
        if (_dayWed) days.Add(3);
        if (_dayThu) days.Add(4);
        if (_dayFri) days.Add(5);
        if (_daySat) days.Add(6);

        var alarm = new CustomAlarm
        {
            Id = Guid.NewGuid().ToString("N"),
            Enabled = true,
            Title = string.IsNullOrWhiteSpace(NewAlarmTitle) ? "알람" : NewAlarmTitle.Trim(),
            Hour = Math.Clamp(NewAlarmHour, 0, 23),
            Minute = Math.Clamp(NewAlarmMinute, 0, 59),
            Days = days,
        };
        _settings.CustomAlarms = _settings.CustomAlarms.Append(alarm).ToList();
        RefreshCustomAlarms();
    }

    public void DeleteCustomAlarm(string id)
    {
        _settings.CustomAlarms = _settings.CustomAlarms.Where(a => a.Id != id).ToList();
        RefreshCustomAlarms();
    }

    public void SetCustomAlarmEnabled(string id, bool on)
    {
        _settings.CustomAlarms = _settings.CustomAlarms
            .Select(a => a.Id == id ? a with { Enabled = on } : a)
            .ToList();
        RefreshCustomAlarms();
    }

    private static readonly string[] DayLabels = { "일", "월", "화", "수", "목", "금", "토" };

    private static CustomAlarmRow ToRow(CustomAlarm a) => new()
    {
        Id = a.Id,
        Title = a.Title,
        TimeText = $"{a.Hour:00}:{a.Minute:00}",
        DaysText = FormatDays(a.Days),
        Enabled = a.Enabled,
    };

    private static string FormatDays(IReadOnlyList<int> days)
    {
        if (days.Count is 0 or 7)
        {
            return "매일";
        }

        return string.Join("·", days.OrderBy(d => d).Where(d => d is >= 0 and <= 6).Select(d => DayLabels[d]));
    }

    public bool IsAutoHide
    {
        get => _controller.IsAutoHide;
        set { _controller.SetAutoHide(value); OnPropertyChanged(); }
    }

    public bool KeepOverlayWhenMeterHidden
    {
        get => _controller.KeepOverlayWhenHidden;
        set { _controller.SetKeepOverlayWhenHidden(value); OnPropertyChanged(); }
    }

    /// <summary>Taskbar / alt-tab mode: the overlay becomes a normal window (shows in taskbar + alt-tab,
    /// auto-hide suspended). Applied live + persisted; the header also exposes this as a toggle.</summary>
    /// <summary>
    /// UI 분리모드에서는 작업표시줄/Alt+Tab 모드를 잠근다. 이 설정은 <b>본체 창</b>의 ex-style 만 바꾸는데
    /// 분리모드에선 그 창이 투명하게 내려가 있어, 켜면 눌러도 아무것도 안 뜨는 작업표시줄 항목이 생긴다
    /// (분리 창들은 <c>OverlayPanelWindow.SyncInputStyle</c> 이 항상 APPWINDOW 를 지우므로 목록에 못 올라간다).
    /// </summary>
    public bool TaskbarModeEnabled => !_settings.SplitUiMode;

    public bool TaskbarMode
    {
        get => _settings.TaskbarMode;
        set { _settings.TaskbarMode = value; _controller.SetTaskbarMode(value); OnPropertyChanged(); }
    }

    // ---- hotkey rebinding (buffered, committed on Save; null = 미지정/unassigned) ----
    //
    // 🔑 일곱 칸은 서로 **배타적**이다. 같은 조합을 둘이 들고 있으면 RegisterHotKey 는 먼저 등록되는
    //    쪽만 성공하고 나중 것은 조용히 실패한다 — 반환값을 보는 곳도 없고 UI 에 남는 단서도 없어서,
    //    사용자에겐 "설정엔 분명히 들어가 있는데 그 동작만 안 먹는다"로만 보인다. 그래서 세터마다
    //    방금 고른 조합을 쓰고 있던 **다른 칸을 비운다**(DropDuplicateHotkeys): 마지막에 고른 의도가
    //    이기고, 비워진 칸은 그 자리에서 '미지정'으로 바뀌어 사용자 눈에 바로 보인다.
    private HotkeyCombo? _pendingReset;
    public HotkeyCombo? PendingReset
    {
        get => _pendingReset;
        set { Set(ref _pendingReset, value); DropDuplicateHotkeys(value, nameof(PendingReset)); }
    }

    private HotkeyCombo? _pendingVisibility;
    public HotkeyCombo? PendingVisibility
    {
        get => _pendingVisibility;
        set { Set(ref _pendingVisibility, value); DropDuplicateHotkeys(value, nameof(PendingVisibility)); }
    }

    private HotkeyCombo? _pendingClickThrough;
    public HotkeyCombo? PendingClickThrough
    {
        get => _pendingClickThrough;
        set { Set(ref _pendingClickThrough, value); DropDuplicateHotkeys(value, nameof(PendingClickThrough)); }
    }

    private HotkeyCombo? _pendingDummyToggle;
    public HotkeyCombo? PendingDummyToggle
    {
        get => _pendingDummyToggle;
        set { Set(ref _pendingDummyToggle, value); DropDuplicateHotkeys(value, nameof(PendingDummyToggle)); }
    }

    private HotkeyCombo? _pendingDummyReset;
    public HotkeyCombo? PendingDummyReset
    {
        get => _pendingDummyReset;
        set { Set(ref _pendingDummyReset, value); DropDuplicateHotkeys(value, nameof(PendingDummyReset)); }
    }

    private HotkeyCombo? _pendingSplitUi;
    public HotkeyCombo? PendingSplitUi
    {
        get => _pendingSplitUi;
        set { Set(ref _pendingSplitUi, value); DropDuplicateHotkeys(value, nameof(PendingSplitUi)); }
    }

    private HotkeyCombo? _pendingAetherList;
    public HotkeyCombo? PendingAetherList
    {
        get => _pendingAetherList;
        set { Set(ref _pendingAetherList, value); DropDuplicateHotkeys(value, nameof(PendingAetherList)); }
    }

    /// <summary>
    /// 방금 지정한 조합을 쓰고 있던 <b>다른</b> 단축키 칸을 비운다. 충돌을 거절하는 게 아니라 먼저
    /// 쓰던 쪽을 놓아 주는 방향인 이유: 거절은 "왜 안 들어가지"가 되고, 그대로 두면 둘 중 하나가
    /// 조용히 죽는다. 비우면 화면에 '미지정'으로 남아 무슨 일이 일어났는지 그 자리에서 보인다.
    ///
    /// <para>🔑 <b>저장된 값을 읽어 오는 경로에서는 절대 돌지 않는다</b>(<see cref="SeedPendingHotkeys"/> 가
    /// 끄고 들어온다). 파일에 이미 같은 조합이 두 칸 들어 있는 상태 — 이 규칙이 생기기 전 빌드에서
    /// 만들어질 수 있다 — 에서 재시드가 그걸 '정리'하면, 하필 <b>등록에 성공하고 있던 칸</b>(id 가 작아
    /// 먼저 등록되는 쪽)이 지워지고 이어지는 저장이 그 손실을 굳힌다. 사용자는 아무것도 누르지 않았는데
    /// 멀쩡하던 단축키를 잃는다. 그런 파일은 예전과 똑같이 둘 다 보여 주고, 사용자가 둘 중 하나를
    /// 실제로 다시 지정할 때만 정리한다.</para>
    /// </summary>
    /// <summary>
    /// 저장된 일곱 조합을 편집 버퍼로 다시 읽어 온다(가져오기·취소). 중복 정리를 <b>끄고</b> 도는 것이
    /// 이 메서드의 존재 이유다 — <see cref="DropDuplicateHotkeys"/> 의 주석 참고.
    /// </summary>
    private void SeedPendingHotkeys()
    {
        _seedingHotkeys = true;
        try
        {
            PendingReset = _hotkeys.Reset;
            PendingVisibility = _hotkeys.Visibility;
            PendingClickThrough = _hotkeys.ClickThrough;
            PendingDummyToggle = _hotkeys.DummyToggle;
            PendingDummyReset = _hotkeys.DummyReset;
            PendingSplitUi = _hotkeys.SplitUi;
            PendingAetherList = _hotkeys.AetherList;
        }
        finally
        {
            _seedingHotkeys = false;
        }
    }

    private bool _seedingHotkeys;

    private void DropDuplicateHotkeys(HotkeyCombo? assigned, string owner)
    {
        if (assigned is null || _seedingHotkeys)
        {
            return; // 미지정은 겹칠 수 없다 — 그리고 아래 재귀의 바닥이다
        }

        // ⚠️ 단축키를 하나 더 만들면 이 표에도 한 줄. 빠뜨리면 그 칸만 중복 검사에서 빠져,
        //    하필 '조용히 실패하는' 쪽이 된다.
        (string Name, Func<HotkeyCombo?> Get, Action<HotkeyCombo?> Set)[] slots =
        [
            (nameof(PendingReset), () => PendingReset, v => PendingReset = v),
            (nameof(PendingVisibility), () => PendingVisibility, v => PendingVisibility = v),
            (nameof(PendingClickThrough), () => PendingClickThrough, v => PendingClickThrough = v),
            (nameof(PendingDummyToggle), () => PendingDummyToggle, v => PendingDummyToggle = v),
            (nameof(PendingDummyReset), () => PendingDummyReset, v => PendingDummyReset = v),
            (nameof(PendingSplitUi), () => PendingSplitUi, v => PendingSplitUi = v),
            (nameof(PendingAetherList), () => PendingAetherList, v => PendingAetherList = v),
        ];

        foreach ((string name, Func<HotkeyCombo?> get, Action<HotkeyCombo?> set) in slots)
        {
            if (name != owner && assigned.Equals(get()))
            {
                set(null); // 세터를 타므로 캡처 박스가 즉시 '미지정'으로 바뀐다
            }
        }
    }

    // ---- stats consent ----
    private bool _consentAccepted;
    public bool ConsentAccepted { get => _consentAccepted; set => Set(ref _consentAccepted, value); }
    private bool _uploadEnabled;
    public bool UploadEnabled { get => _uploadEnabled; set => Set(ref _uploadEnabled, value); }
    private bool _publicCharacter;
    public bool PublicCharacter { get => _publicCharacter; set => Set(ref _publicCharacter, value); }
    private bool _characterDetected;
    public bool CharacterDetected { get => _characterDetected; private set => Set(ref _characterDetected, value); }
    private string _consentStatus = string.Empty;
    public string ConsentStatus { get => _consentStatus; private set => Set(ref _consentStatus, value); }
    private string _consentNotice = string.Empty;
    /// <summary>Localized notice for the last consent action (e.g. a public transition refused for lack of
    /// ownership, rolled back to private). Empty when there is nothing to say.</summary>
    public string ConsentNotice
    {
        get => _consentNotice;
        private set { Set(ref _consentNotice, value); OnPropertyChanged(nameof(HasConsentNotice)); }
    }
    public bool HasConsentNotice => !string.IsNullOrEmpty(_consentNotice);

    public string UploadStatus
    {
        get
        {
            StatsUploadStatus s = _services.UploadQueue.Status();
            string counts = $"업로드 {s.Uploaded} · 대기 {s.Pending} · 건너뜀 {s.Skipped} · 실패 {s.Failed}";

            // 마지막 사유를 함께 보여준다. 큐는 사유를 코드까지 실어 만들어 두는데(예:
            // unsupported_encounter:2301059:영겁의 루드라) 지금까지 어디에도 표시되지 않아서, "안 올라가요" 제보를
            // 미동의·보스아님·카탈로그누락·전투력미해석 중 무엇인지 가를 방법이 없었다.
            string line = DescribeUploadReason(s.LastReason) is { } reason ? $"{counts}\n최근: {reason}" : counts;

            // 반복되는 사유는 따로 센다. '최근' 한 칸은 다음 전투가 덮어써서, 한 캐릭터의 전투를 매번 먹는 게이트가
            // 일회성과 구분되지 않았다 — 그 구분이 없으면 몇 달 동안 통계에서 빠져 있어도 아무도 모른다.
            if (s.SkipReasons is { Count: > 0 } reasons)
            {
                string breakdown = string.Join(" · ", reasons
                    .Where(r => r.Count > 1)
                    .Take(3)
                    .Select(r => $"{DescribeUploadReason(r.Reason) ?? r.Reason} {r.Count}회"));
                if (breakdown.Length > 0)
                {
                    line += $"\n건너뜀 사유: {breakdown}";
                }
            }

            return line;
        }
    }

    private string _uploadBlockNotice = string.Empty;

    /// <summary>지금 캐릭터의 전투가 왜 안 올라가는지. 없으면 빈 문자열.
    /// <para>업로드 여부는 <b>캐릭터별</b>인데(StatsConsentManager.LocalInfo) 그 사실이 화면 어디에도 없었다.
    /// 한 캐릭터만 꺼져 있으면 그 캐릭터는 통계에서 조용히 사라지고, 소유 증명이 안 생기니 공개 전환과 스킨
    /// 선택까지 같이 막힌다.</para></summary>
    public string UploadBlockNotice
    {
        get => _uploadBlockNotice;
        private set { Set(ref _uploadBlockNotice, value); OnPropertyChanged(nameof(HasUploadBlockNotice)); }
    }

    public bool HasUploadBlockNotice => UploadBlockNotice.Length > 0;

    /// <summary>업로드 큐/페이로드 빌더가 남긴 마지막 사유를 한국어 한 줄로. 모르는 사유는 원문 그대로 보여준다 —
    /// 새 사유가 생겼을 때 "" 로 삼켜 버리면 진단 가치가 사라진다.</summary>
    private static string? DescribeUploadReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        // 큐가 코드와 이름을 함께 싣는 형태: unsupported_encounter:<mobCode>:<보스 이름>
        if (reason.StartsWith("unsupported_encounter:", StringComparison.Ordinal))
        {
            string[] parts = reason.Split(':', 3);
            string boss = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : "이 보스";
            return $"{boss}는 아직 통계 대상 던전이 아닙니다";
        }

        if (reason.StartsWith("upload_failed:", StringComparison.Ordinal))
        {
            return $"전송 실패 — {reason["upload_failed:".Length..]}";
        }

        return reason switch
        {
            "uploaded" or "uploaded_duplicate" => "정상 업로드됨",
            // 이 게이트는 캐릭터별이다(StatsConsentManager.LocalInfo). "동의하지 않아서"라고만 하면 전체 동의를
            // 켜 둔 사람이 자기 얘기가 아니라고 읽고 지나간다 — 실제로는 그 캐릭터 하나만 막혀 있는 상태다.
            "consent_not_allowed" => "이 캐릭터의 업로드가 꺼져 있어 보내지 않았습니다 (업로드 설정은 캐릭터별입니다)",
            "unsigned_upload" => "서명 없이 업로드돼 이 캐릭터의 소유 증명이 생기지 않았습니다",
            "not_boss" or "not_uploadable_boss" => "보스 전투가 아닙니다",
            "estimated_boss" => "보스를 확정하지 못했습니다(미상 보스)",
            "not_kill" => "처치하지 못한 전투입니다",
            "duplicate" => "같은 전투가 이미 올라가 있습니다",
            "no_report_id" => "서버가 리포트 번호를 주지 않았습니다",
            "target_missing" => "대상 보스 정보가 없습니다",
            "executor_missing" or "own_character_missing" => "본인 캐릭터를 아직 인식하지 못했습니다",
            "own_nickname_missing" or "own_identity_missing" => "본인 닉네임을 확인하지 못했습니다",
            "own_result_missing" or "own_damage_empty" => "본인 딜 기록이 없는 전투입니다",
            "invalid_duration" => "전투 시간이 올바르지 않습니다",
            "own_power_unresolved" => "본인 전투력을 확인하지 못했습니다",
            "participant_power_unresolved" => "참가자 중 전투력을 확인하지 못한 사람이 있습니다",
            _ => reason,
        };
    }

    public void ApplyConsent()
    {
        string state = ConsentAccepted ? "accepted" : "declined";
        ApplyInfo(_services.Consent.Set(state, UploadEnabled, PublicCharacter, _services.Version));
    }

    public void RefreshConsentFromServer() => ApplyInfo(_services.Consent.GetInfo(syncRemote: true, _services.Version));

    /// <summary>Open the stats site to THIS character's own battle records ("내 캐릭터 통계 보기", Tier A:
    /// identityHash link — portable across reinstalls/other PCs, no nickname in the URL). No-op when no
    /// character is detected (the hash needs both a nickname and a server).</summary>
    public void OpenMyStats()
    {
        string? hash = _services.Consent.CurrentCharacterHash();
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = _services.StatsApi.CharacterReportUrl(hash), UseShellExecute = true });
    }

    /// <summary>통계 웹서비스 첫 화면 주소(설정창 하단 '통계 웹' 버튼).</summary>
    public string StatsWebUrl => _services.StatsApi.WebHomeUrl;

    // ---- 내 스탯 (0x364A/0x3649 스탯 사전) ----

    /// <summary>지금 잡혀 있는 본인 스탯 사전. 없으면 null.</summary>
    private PlayerStatSheet? Sheet => _services.Data.PlayerStats.Current;

    /// <summary>스탯을 하나라도 잡았는지. 두 버튼의 활성 조건.</summary>
    public bool HasStatSheet => Sheet != null;

    /// <summary>사용자에게 보여줄 상태 한 줄. "몇 개 잡았는지"보다 "믿고 써도 되는지"를 말한다.</summary>
    public string StatSheetStatus
    {
        get
        {
            PlayerStatSheet? sheet = Sheet;
            if (sheet == null)
            {
                return "아직 스탯 정보를 받지 못했습니다. 캐릭터 선택 화면으로 나갔다가 다시 접속하면 전체 스탯이 한 번에 들어옵니다.";
            }

            return sheet.FullSnapshotSeen
                ? $"{sheet.Values.Count}개 항목 · 전체 스냅샷을 받았습니다"
                : $"{sheet.Values.Count}개 항목 · 바뀐 항목만 받은 상태입니다. 캐릭터 선택 화면으로 나갔다가 "
                  + "다시 접속하시면 전체가 채워집니다.";
        }
    }

    /// <summary>잡힌 스탯을 사람이 읽는 순서로 묶은 것. 인게임 스탯창과 눈으로 대조하라고 두는 자리다 —
    /// 항목 이름은 패킷에 없어서 이 대조가 매칭을 "거의 확실"에서 "확인됨"으로 바꾸는 유일한 방법이다.</summary>
    public ObservableCollection<StatGroupVM> StatGroups { get; } = new();

    private void RebuildStatGroups()
    {
        StatGroups.Clear();
        if (Sheet is not { } sheet) return;

        foreach (StatSheetGroup group in StatSheetExport.Groups(sheet))
        {
            StatGroups.Add(new StatGroupVM(group));
        }
    }

    /// <summary>딥링크가 채우지 못하는 칸 안내(계산기에 없는 값이 아니라, 어떤 경로로도 못 얻는 값들).</summary>
    public string StatSheetUnfilled => Sheet is { } sheet
        ? "계산기에서 직접 채워야 하는 칸: " + string.Join(" · ", StatSheetExport.UnfilledFields(sheet))
        : string.Empty;

    /// <summary>미터가 실제 전투에서 잰 판정 빈도(치명타 적중률 · 전방/후방 타격률). 스탯창에 없는 값이라
    /// 계산기의 '전투 환경' 칸을 이걸로 채운다. 표시 중인 전투에 본인 행이 없으면 null.</summary>
    private MeasuredCombatRates? MeasuredRates()
    {
        DpsReport? report = _services.Calculator.GetRecentData();
        int uid = _services.Data.ExecutorId();
        if (report == null || uid <= 0) return null;

        Dictionary<string, AnalyzedSkill> skills = _services.Calculator.BattleDetails(report, uid);
        if (skills.Count == 0) return null;

        int hits = skills.Values.Sum(s => s.Times);
        int flagged = skills.Values.Sum(s => s.FlaggedTimes);
        if (hits == 0) return null;

        int crit = skills.Values.Sum(s => s.CritTimes);
        int back = skills.Values.Sum(s => s.BackTimes);
        int front = skills.Values.Sum(s => s.FrontTimes);
        bool preferBack = back >= front;
        int directional = preferBack ? back : front;

        return new MeasuredCombatRates(
            crit / (double)hits * 100.0,
            flagged > 0 ? directional / (double)flagged * 100.0 : 0.0,
            preferBack);
    }

    /// <summary>"내 스탯 복사" — 클립보드에 WAFFLE_STATS_V1 블록을 넣는다.</summary>
    public bool CopyStatSheet()
    {
        if (Sheet is not { } sheet) return false;

        string payload = StatSheetExport.BuildClipboard(
            sheet,
            _services.Data.User(_services.Data.ExecutorId())?.Job?.ClassName(),
            _services.Data.User(_services.Data.ExecutorId())?.Nickname,
            _services.Data.User(_services.Data.ExecutorId())?.Server ?? -1,
            sheet.UpdatedAt,
            MeasuredRates());

        try
        {
            Clipboard.SetText(payload);
            return true;
        }
        catch
        {
            // 클립보드는 다른 프로세스가 잡고 있으면 실패한다 — 조용히 실패시키고 호출부가 안내한다.
            return false;
        }
    }

    /// <summary>"계산기 열기" — 잡힌 스탯을 쿼리로 실어 계산기 페이지를 연다(웹 수정 없이 동작).</summary>
    public void OpenCalculator()
    {
        if (Sheet is not { } sheet) return;

        string url = StatSheetExport.BuildCalculatorUrl(
            sheet, MeasuredRates(), StatsApiClient.CalculatorPageUrl);
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    /// <summary>스탯 사전이 갱신됐을 때 화면을 새로 그리게 한다.</summary>
    public void NotifyStatSheetChanged()
    {
        RebuildStatGroups();
        OnPropertyChanged(nameof(HasStatSheet));
        OnPropertyChanged(nameof(StatSheetStatus));
        OnPropertyChanged(nameof(StatSheetUnfilled));
    }

    // ---- per-character consent management (the 내 캐릭터 관리 list) ----
    public ObservableCollection<ConsentCharacterRow> ConsentCharacters { get; } = new();
    public bool HasConsentCharacters => ConsentCharacters.Count > 0;

    /// <summary>Rebuild the management list from the locally-remembered consented characters (current
    /// character first). Local-only + UI-thread (no network); call on open and after each action.</summary>
    public void RefreshConsentCharacters()
    {
        ConsentCharacters.Clear();
        AetherPerCharacterStore aether = AetherPerCharacterStore.Parse(_settings.AetherPerCharacter);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (StatsConsentManager.CharacterConsentInfo c in _services.Consent.ListCharacters())
        {
            if (c.State != "accepted")
            {
                continue; // the management list = currently-consented characters
            }

            // Projected forward over the 자연회복 accrued since the reading was taken, exactly as the 컨텐츠 관리
            // list does — the two show the same characters and must not disagree.
            AetherSnapshot? snap = aether.Get(c.IdentityHash);
            string aetherText = string.Empty;
            if (snap is { } a)
            {
                (int aBase, int aBonus) = AetherRegen.Project(a.Base, a.Bonus, a.SavedAtMs, nowMs);
                aetherText = aBonus > 0
                    ? $"{aBase}(+{aBonus})"
                    : aBase.ToString(CultureInfo.InvariantCulture);
            }

            string label = !string.IsNullOrWhiteSpace(c.Nickname)
                ? (c.Server > 0 ? $"{c.Nickname} [{ServerNames.GetServerLabel(c.Server)}]" : c.Nickname!)
                : "이름 없음 (이전 기록)";
            string job = string.IsNullOrWhiteSpace(c.Job) ? string.Empty : c.Job! + " · ";

            // 공개 토글 게이트 (W18-UI): 일괄편집은 하나라도 접속 중일 때만(CharacterDetected) 활성화하고,
            // 개별 공개 토글은 CanSetPublic && Grant일 때만 활성. 현재 접속 캐릭터라도 grant가 없으면 이 토글은
            // 비활성이며, 상단 "캐릭터 공개" 체크박스(=Accept 경로, 서버가 최종 판정·실패 시 롤백)로 시도한다.
            // 비공개화·동의 철회는 게이트 없음. (CanSetPublic=false인 이전 기록 행은 목록에서 이미 숨겨짐.)
            bool canEditPublic = CharacterDetected && c.CanSetPublic && c.Grant;
            string tooltip = canEditPublic
                ? "공개하면 통계 사이트에 닉네임·서버가 표시됩니다."
                : !CharacterDetected
                    ? "캐릭터가 접속해 있어야 공개 설정을 바꿀 수 있어요."
                    : "이 기기에서 이 캐릭터로 전투를 업로드한 적이 있어야 공개로 전환할 수 있어요.";

            ConsentCharacters.Add(new ConsentCharacterRow
            {
                IdentityHash = c.IdentityHash,
                Label = label,
                SubLabel = job + (c.PublicCharacter
                    ? "공개"
                    : c.PublicApplyFailed ? "비공개 — 공개 적용 실패" : "비공개 (익명 집계)"),
                IsPublic = c.PublicCharacter,
                CanSetPublic = canEditPublic,
                CanRevoke = true, // 동의 철회는 항상 활성 (오프라인 포함)
                PublicToggleTooltip = tooltip,
                CurrentBadgeVisibility = c.IsCurrent ? Visibility.Visible : Visibility.Collapsed,
                AetherText = aetherText,
                AetherVisibility = aetherText.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
                PublicApplyFailed = c.PublicApplyFailed,
            });
        }

        OnPropertyChanged(nameof(HasConsentCharacters));
    }

    /// <summary>Change a character's public flag. Network call — run off the UI thread, then refresh.</summary>
    public void SetCharacterPublic(string identityHash, bool publicCharacter)
        => _services.Consent.SetCharacterPublic(identityHash, publicCharacter, _services.Version);

    /// <summary>Revoke a character's consent. Network call — run off the UI thread, then refresh.</summary>
    public void RevokeConsentCharacter(string identityHash)
        => _services.Consent.RevokeCharacter(identityHash, _services.Version);

    public void RefreshCharacterStatus()
    {
        bool detected = _services.StatsBuilder.OwnCharacter().Detected;
        bool changed = detected != CharacterDetected;
        CharacterDetected = detected;
        OnPropertyChanged(nameof(UploadStatus));
        UploadBlockNotice = _services.Consent.CurrentUploadBlockReason() ?? string.Empty;
        // Per-character public-toggle enablement depends on CharacterDetected; re-evaluate the rows when it flips.
        if (changed)
        {
            RefreshConsentCharacters();
        }
    }

    /// <summary>Re-read local consent state (no network) and rebuild the management list — call on the UI
    /// thread after any consent action so the rolled-back public flag + notice show.</summary>
    public void RefreshConsentState()
    {
        ApplyInfo(_services.Consent.GetInfo(syncRemote: false, _services.Version));
        RefreshConsentCharacters();
    }

    private void ApplyInfo(StatsConsentManager.Info info)
    {
        ConsentAccepted = info.State == "accepted";
        UploadEnabled = info.UploadEnabled;
        PublicCharacter = info.PublicCharacter;
        ConsentStatus = info.SyncError is { } error ? $"{info.State} · {info.SyncStatus} ({error})" : $"{info.State} · {info.SyncStatus}";
        ConsentNotice = NoticeFor(info.SyncStatus);
        OnPropertyChanged(nameof(UploadStatus));
    }

    // The manager surfaces server outcomes as ASCII status codes (Korean can't live in the EUC-KR settings
    // keys); localize the user-facing ones here.
    private static string NoticeFor(string syncStatus) => syncStatus == StatsConsentManager.PublicRequiresOwnership
        ? "이 기기에서 이 캐릭터로 전투를 업로드한 적이 있어야 공개로 전환할 수 있어요. (지금은 비공개로 동의되었습니다.)"
        : string.Empty;

    // ---- server ----
    private string _serverIp = string.Empty;
    public string ServerIp { get => _serverIp; set => Set(ref _serverIp, value); }
    private string _serverPort = string.Empty;
    public string ServerPort { get => _serverPort; set => Set(ref _serverPort, value); }

    public void SaveServer()
    {
        _services.Props.SetProperty("server.ip", ServerIp);
        _services.Props.SetProperty("server.port", ServerPort);
    }

    // ---- nav rail + footer ----

    /// <summary>
    /// The canonical tab keys, in nav-rail order. This is the SOURCE OF TRUTH — the XAML's
    /// <c>ListBoxItem.Tag</c> and each panel's <c>ConverterParameter</c> must agree with it, and
    /// <see cref="SettingsWindow"/> checks that at construction (see <c>VerifyNavContract</c>).
    /// </summary>
    public static readonly string[] NavKeys =
    {
        "display", "theme", "window", "buffs", "cooldown", "alarms",
        "battle", "hotkeys", "stats", "mystats", "gameopt", "advanced",
    };

    private string _selectedNav = NavKeys[0];

    /// <summary>
    /// Selected tab key. The setter ABSORBS null and unknown values instead of storing them, and that is
    /// load-bearing: the nav rail binds <c>Selector.SelectedValue</c>, which is TwoWay by default, so WPF
    /// writes <c>null</c> back the moment the bound value has no matching item — during init, and again
    /// whenever a tab is renamed or retired. A stored null makes
    /// <see cref="StringEqualsToVisibilityConverter"/> collapse EVERY panel and the right-hand side of the
    /// window goes blank with no error anywhere. Keeping the previous key is always better than that.
    /// </summary>
    public string SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is null || Array.IndexOf(NavKeys, value) < 0)
            {
                return;
            }

            Set(ref _selectedNav, value);
            SyncNameFxPreview(); // the strip only animates while its tab is on screen
        }
    }

    public string Version => _services.Version;

    // ---- advanced ----
    public IReadOnlyList<SettingOption> CloseActions { get; } = new[]
    {
        new SettingOption("종료 시 묻기", "ask"),
        new SettingOption("트레이로 최소화", "tray"),
        new SettingOption("프로그램 종료", "exit"),
    };
    public string CloseAction { get => _settings.CloseAction; set { _settings.CloseAction = value; OnPropertyChanged(); } }

    public IReadOnlyList<SettingOption> CaptureBackends { get; } = new[]
    {
        new SettingOption("WinDivert (기본)", "windivert"),
        new SettingOption("Npcap", "npcap"),
    };
    public string CaptureBackend { get => _settings.CaptureBackend; set { _settings.CaptureBackend = value; OnPropertyChanged(); } }

    /// <summary>Footer "기본값 복원": restore the display settings + theme to defaults.</summary>
    public void ResetDefaults()
    {
        DisplayMode = "dps_percent";
        DamageValueMode = "dps";
        RowDpsMetric = "dps";
        ContributionMode = "contribution";
        NameDisplay = "all";
        FontFamily = DefaultFontFamily;
        RowHeight = 36;
        BossSlotScalePercent = MeterLayout.BossScaleDefault;
        MeterScalePercent = MeterScalePolicy.ScaleDefault;
        MeterOpacity = 0.4;
        BarStyle = "fill";
        Skin = "dark";
        Theme.Reset();
    }

    // ---- skin (overall style preset) ----
    public IReadOnlyList<SkinManager.SkinOption> Skins => SkinManager.Skins;

    /// <summary>Active skin preset; applied + persisted live (swaps the Skin.* palette app-wide).</summary>
    public string Skin
    {
        get => _skin.Current;
        set
        {
            _skin.Apply(value);
            OnPropertyChanged();
            RebuildNameFxSamples(_skin.IsLight); // the preview strip carries its own palette per skin
        }
    }

    // ---- theme (color picker) ----
    /// <summary>The live color theme; the 테마 tab binds swatches/gradient rows directly to its
    /// properties (colors apply + persist immediately, like the React panel).</summary>
    public MeterColorTheme Theme { get; }

    /// <summary>Restore the default palette (writes DEFAULT_THEME back to the "theme" key).</summary>
    public void ResetTheme() => Theme.Reset();

    // ---- diagnostics (packet logging) ----
    public bool IsLoggingActive => _services.DebugLogger.IsRunning;

    public string LoggingButtonLabel => _services.DebugLogger.IsRunning ? "기록 중지" : "기록 시작";

    public string LoggingStatus => _services.DebugLogger.IsRunning
        ? $"기록 중 · 세그먼트 {_services.DebugLogger.CaptureCount} · {_services.DebugLogger.LineCount} 줄"
        : "중지됨";

    /// <summary>Start/stop a packet-debug-logs capture session (replayable corpus).</summary>
    public void ToggleLogging()
    {
        if (_services.DebugLogger.IsRunning)
        {
            _services.DebugLogger.Stop();
        }
        else
        {
            _services.DebugLogger.Start();
        }

        RefreshLogging();
        OnPropertyChanged(nameof(LoggingButtonLabel));
        OnPropertyChanged(nameof(IsLoggingActive));
    }

    /// <summary>Re-reads the live logging counters (polled while the window is open).</summary>
    public void RefreshLogging() => OnPropertyChanged(nameof(LoggingStatus));

    // ---- 설정 백업 · 공유 ----

    /// <summary>Injected by App once every collaborator exists. Null in the preview harness — the section then
    /// reports that it is unavailable rather than throwing.</summary>
    public SettingsBundleApplier? BundleApplier { get; set; }

    /// <summary>"is a fight happening right now". Import repaints every row, swaps the skin dictionary and
    /// re-registers five global hotkeys in one go — not something to do mid-pull.</summary>
    public Func<bool>? IsCombatActive { get; set; }

    private string _bundleStatus = string.Empty;

    /// <summary>One line under the buttons: what just happened.</summary>
    public string BundleStatus { get => _bundleStatus; private set => Set(ref _bundleStatus, value); }

    private string _lastExportedCode = string.Empty;

    /// <summary>The code that was just produced, shown in a read-only box. Clipboard writes can fail (another
    /// app holding the clipboard is common), so the code is always visible to copy by hand.</summary>
    public string LastExportedCode { get => _lastExportedCode; private set => Set(ref _lastExportedCode, value); }

    public Visibility UndoVisibility =>
        SettingsBackupStore.List(_services.Props.AppDirectory()).Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void ExportFull() => Export(SettingsProfile.Full, "전체 설정");

    public void ExportDesign() => Export(SettingsProfile.Design, "디자인");

    public void ExportAlarms() => Export(SettingsProfile.Alarms, "알림");

    private void Export(SettingsProfile profile, string label)
    {
        SettingsBundle bundle = SettingsBundleBuilder.Build(_services.Props, profile, _services.Version, DateTimeOffset.Now);
        string code = SettingsBundleCodec.Encode(bundle);
        LastExportedCode = code;

        bool copied = TryCopy(code);
        // 디스코드 한 메시지가 2,000자다. 그 선을 넘으면 붙여넣기가 잘려 나가고, 잘린 코드는 지문 검사에서
        // "손상됐다"로만 보인다 — 왜 잘렸는지는 받는 쪽이 알 길이 없으므로 보내는 쪽에서 미리 말해 준다.
        string tooLong = code.Length > 1800
            ? " 채팅에 붙여넣기엔 깁니다 — '파일로 저장'으로 넘기는 편이 안전해요."
            : string.Empty;
        BundleStatus = copied
            ? $"{label} 코드를 복사했습니다 — 설정 {bundle.Data.Count}개, {code.Length}자.{tooLong}"
            : $"{label} 코드를 만들었습니다 — 설정 {bundle.Data.Count}개. 클립보드 복사에 실패해 아래 상자에서 직접 복사해 주세요.";
    }

    private static bool TryCopy(string text)
    {
        try
        {
            // SetDataObject(copy: true) so the code survives this process exiting — a plain SetText leaves the
            // clipboard owned by us and the paste fails after the app closes.
            System.Windows.Clipboard.SetDataObject(text, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Read a code from the clipboard, if it holds one. Used to prefill the import box.</summary>
    public string? ClipboardCode()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText() ? SettingsBundleCodec.Extract(System.Windows.Clipboard.GetText()) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decode and diff, writing nothing. <paramref name="error"/> carries the reason on failure.</summary>
    public SettingsBundlePlan? PreviewImport(string? text, out string error)
    {
        error = string.Empty;
        if (!SettingsBundleCodec.TryDecode(text, out SettingsBundle bundle, out SettingsCodeError code))
        {
            error = code switch
            {
                SettingsCodeError.ChecksumMismatch =>
                    "코드가 손상됐습니다. 복사할 때 일부가 빠졌을 수 있어요 — 'WM1.' 부터 끝까지 전체를 다시 복사해 붙여넣어 주세요.",
                SettingsCodeError.FutureVersion =>
                    "더 새로운 버전에서 만든 코드입니다. 미터를 업데이트한 뒤 다시 시도해 주세요.",
                SettingsCodeError.NotFound =>
                    "설정 코드를 찾지 못했습니다. 'WM1.' 으로 시작하는 코드를 붙여넣어 주세요.",
                _ => "코드를 읽지 못했습니다. 올바른 설정 코드인지 확인해 주세요.",
            };
            return null;
        }

        return SettingsBundleBuilder.Plan(_services.Props, bundle);
    }

    /// <summary>Apply a previewed plan. Returns the user-facing result line.</summary>
    public string ApplyImport(SettingsBundlePlan plan)
    {
        if (BundleApplier is not { } applier)
        {
            return "이 빌드에서는 설정 가져오기를 쓸 수 없습니다.";
        }

        // 픽커는 이 뷰모델이 소유하므로 applier 가 직접 못 부른다. 이 한 줄이 없으면 가져온 hidden/voice/pinned
        // 선택이 화면에 안 보이다가, 사용자가 칩 하나를 만지는 순간 스테일 캐시가 통째로 덮어쓴다.
        applier.BuffPickerRefresh ??= () => _buffPicker?.Reload();

        SettingsImportResult result = applier.Apply(plan, _services.Version, DateTimeOffset.Now);

        // The window's own Cancel restores a 19-value snapshot taken when it opened, so after an import it would
        // put back a mixture no backup describes. Re-take it: Cancel now means "cancel what I did after this".
        _snapshot = Snapshot.Capture(_settings, _controller);
        SeedPendingHotkeys();

        Reload();
        // Reload() 는 동의·스탯·알람만 다시 읽는다. 보스칸 슬라이더는 **파생 라벨(px)** 까지 들고 있어서
        // 가만히 두면 값이 안 보이는 게 아니라 **틀린 px 를 말하고**, 그 상태에서 한 눈금만 건드리면
        // 가져온 값이 조용히 덮인다.
        OnPropertyChanged(nameof(BossSlotScalePercent));
        OnPropertyChanged(nameof(BossSlotHeightHint));
        RebuildFontCards();
        RebuildNameFxSamples(_skin.IsLight);
        SyncNameFxPreview();
        NameFxSheen.Rebuild(_settings.NameFxBrightnessPercent);
        OnPropertyChanged(nameof(UndoVisibility));

        BundleStatus = DescribeImport(result, plan);
        return BundleStatus;
    }

    /// <summary>
    /// 가져오기 결과를 사용자 문장으로. 🔑 <b>M-28 의 무게중심은 "복원 실패"가 아니라 "0건 복원해 놓고 성공
    /// 문구를 내는 것"이었다</b> — 그래서 완전 성공을 주장할 수 있는 건 <see cref="SettingsImportResult.Complete"/>
    /// 하나뿐이고, 나머지는 무엇이 덜 됐는지 말해야 한다.
    /// </summary>
    private static string DescribeImport(SettingsImportResult result, SettingsBundlePlan plan)
    {
        var parts = new List<string>
        {
            result.Changed == 0
                ? "바뀐 설정이 없습니다."
                : $"설정 {result.Changed}개를 적용했습니다.",
        };

        if (result.Removed > 0)
        {
            // 백업 복원에서만 나온다 — 공유 코드는 '설정한 적 없음'을 싣지 않는다.
            parts.Add($"그중 {result.Removed}개는 설정 안 함으로 되돌렸습니다.");
        }

        if (result.Skipped > 0)
        {
            parts.Add($"이 빌드가 모르는 항목 {result.Skipped}개는 건너뛰었습니다.");
        }

        if (result.Unwired.Count > 0)
        {
            // "가져왔는데 이번 세션에 안 먹는다" — 조용히 넘기면 사용자는 가져오기가 실패한 줄 모른다.
            parts.Add($"{string.Join(" · ", result.Unwired)}은(는) 이번 세션에 반영되지 않았습니다.");
        }

        parts.Add(result.BackupPath is null
            ? "⚠ 이전 설정 백업을 저장하지 못했습니다."
            : "적용 전 설정은 백업해 뒀습니다.");

        if (result.RestartHint)
        {
            parts.Add("일부 항목은 미터를 다시 켜야 적용됩니다.");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Put back the snapshot taken before the most recent import.
    /// <para>⚠️ 이것은 <b>한 번만 되는 되돌리기가 아니라 토글</b>이다 — 되돌리는 것도 <see cref="ApplyImport"/>를
    /// 타고, 그 경로가 적용 직전 상태를 다시 백업하기 때문이다. 그래서 두 번 누르면 원위치다. 동작 자체는
    /// 쓸모가 있어서(잘못 눌렀을 때 바로 복구된다) 그대로 두고, <b>문구만</b> 그 사실을 말하도록 고쳤다.
    /// 종전에는 두 번 다 "가져오기 직전 설정으로 되돌렸습니다"라고 해서, 두 번째 누름이 <b>되살리기</b>인데도
    /// 되돌리기라고 말했다.</para>
    /// </summary>
    public string UndoLastImport()
    {
        string? code = SettingsBackupStore.ReadNewest(_services.Props.AppDirectory());
        if (code is null || !SettingsBundleCodec.TryDecode(code, out SettingsBundle bundle, out _))
        {
            BundleStatus = "되돌릴 백업이 없습니다.";
            return BundleStatus;
        }

        SettingsBundlePlan plan = SettingsBundleBuilder.Plan(_services.Props, bundle);
        if (plan.Changes.Count == 0)
        {
            BundleStatus = "되돌릴 변경이 없습니다 — 이미 그 상태입니다.";
            return BundleStatus;
        }

        // ApplyImport 가 만든 문장을 **살린다.** 종전에는 그 위에 "되돌렸습니다"를 무조건 덮어써서, 건너뛴 키가
        // 있든 0건이 적용됐든 똑같이 성공을 말했다 — 그 한 줄이 M-28 의 증상 본체였다.
        string applied = ApplyImport(plan);
        string partial = plan.UnknownCount > 0
            ? $" ⚠ 이 백업에는 이 빌드가 모르는 항목 {plan.UnknownCount}개가 있어 부분 복원입니다."
            : string.Empty;
        BundleStatus = $"가장 최근 백업으로 되돌렸습니다. {applied}{partial} 다시 누르면 직전 상태로 돌아갑니다.";
        return BundleStatus;
    }

    private string _importText = string.Empty;

    /// <summary>What the user pasted. Not necessarily just a code — the parser cuts it out of surrounding chat.</summary>
    public string ImportText
    {
        get => _importText;
        set { Set(ref _importText, value); ClearPreview(); }
    }

    private SettingsBundlePlan? _plan;

    private IReadOnlyList<SettingsChange> _importChanges = Array.Empty<SettingsChange>();

    /// <summary>Exactly what would change, so the user agrees to a list rather than to the word "가져오기".</summary>
    public IReadOnlyList<SettingsChange> ImportChanges { get => _importChanges; private set => Set(ref _importChanges, value); }

    private string _importSummary = string.Empty;

    public string ImportSummary { get => _importSummary; private set => Set(ref _importSummary, value); }

    public Visibility ImportPreviewVisibility => _plan is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Blocked mid-fight: applying repaints every row, swaps the skin dictionary and re-registers five
    /// global hotkeys at once.</summary>
    public bool CanApplyImport => _plan is { HasWork: true } && IsCombatActive?.Invoke() != true;

    public string CombatBlockNotice =>
        IsCombatActive?.Invoke() == true ? "전투 중에는 적용할 수 없습니다. 전투가 끝난 뒤 눌러 주세요." : string.Empty;

    public Visibility CombatBlockVisibility =>
        IsCombatActive?.Invoke() == true ? Visibility.Visible : Visibility.Collapsed;

    private void ClearPreview()
    {
        _plan = null;
        ImportChanges = Array.Empty<SettingsChange>();
        ImportSummary = string.Empty;
        RaisePreviewState();
    }

    private void RaisePreviewState()
    {
        OnPropertyChanged(nameof(ImportPreviewVisibility));
        OnPropertyChanged(nameof(CanApplyImport));
        OnPropertyChanged(nameof(CombatBlockNotice));
        OnPropertyChanged(nameof(CombatBlockVisibility));
    }

    /// <summary>Decode + diff the pasted text. Writes nothing.</summary>
    public void PreviewPastedCode()
    {
        _plan = PreviewImport(ImportText, out string error);
        if (_plan is null)
        {
            ImportChanges = Array.Empty<SettingsChange>();
            ImportSummary = string.Empty;
            BundleStatus = error;
            RaisePreviewState();
            return;
        }

        ImportChanges = _plan.Changes;
        var bits = new List<string> { $"바뀌는 설정 {_plan.Changes.Count}개" };
        if (_plan.UnchangedCount > 0) { bits.Add($"이미 같음 {_plan.UnchangedCount}개"); }
        if (_plan.UnknownCount > 0) { bits.Add($"이 버전이 모르는 항목 {_plan.UnknownCount}개(무시)"); }
        if (_plan.MissingCount > 0) { bits.Add($"코드에 없어 그대로 두는 항목 {_plan.MissingCount}개"); }
        ImportSummary = string.Join(" · ", bits);
        BundleStatus = _plan.HasWork ? string.Empty : "이 코드는 지금 설정과 같습니다. 적용해도 바뀌는 게 없어요.";
        RaisePreviewState();
    }

    /// <summary>Apply the previewed plan.</summary>
    public void ApplyPreviewedCode()
    {
        if (_plan is null || !CanApplyImport)
        {
            return;
        }

        ApplyImport(_plan);
        ImportText = string.Empty; // also clears the preview
    }

    /// <summary>Default file name for "파일로 저장", so a folder of these is still readable a month later.</summary>
    public string SuggestedFileName(DateTimeOffset now) => $"waffle-settings-{now:yyyyMMdd}.wmset";

    /// <summary>Feed a code read from a file into the import box and diff it.</summary>
    public void LoadCodeFromText(string text)
    {
        ImportText = text;
        PreviewPastedCode();
    }

    /// <summary>Open the folder holding the pre-import snapshots.</summary>
    public void OpenBackupFolder()
    {
        string dir = SettingsBackupStore.Directory(_services.Props.AppDirectory());
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    /// <summary>Open the user-fonts folder. It IS the store — deleting a file there removes the card — so this
    /// doubles as the "remove a font I added" path without a separate delete UI.</summary>
    public void OpenFontsFolder()
    {
        string dir = FontResolver.UserFontsDir();
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    public void OpenLogFolder()
    {
        string dir = PacketDebugLogger.LogDirectory();
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    // ---- replay (BETA) ----

    /// <summary>Record a positional replay per battle. Live: the capture tap is gated on this, so turning it
    /// off stops recording immediately (and turning it on needs no restart).</summary>
    public bool RecordReplay
    {
        get => _settings.RecordReplay;
        set
        {
            if (_settings.RecordReplay == value)
            {
                return;
            }

            _settings.RecordReplay = value;
            _services.RecordReplay = value;
            OnPropertyChanged();
        }
    }

    // ---- 허수아비 테스트 ----

    /// <summary>허수아비 테스트 모드 on/off. Live (like <see cref="RecordReplay"/>, not part of the Cancel snapshot):
    /// writing the shared setting mirrors onto the capture gate at once and updates the header toggle's accent.</summary>
    public bool DummyTestMode
    {
        get => _settings.DummyTestMode;
        set
        {
            if (_settings.DummyTestMode == value)
            {
                return;
            }

            _settings.DummyTestMode = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Selected run length as the ComboBox's string value ("30".."300" seconds).</summary>
    public string DummyDurationValue
    {
        get => _settings.DummyDurationSec.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
                && seconds != _settings.DummyDurationSec)
            {
                _settings.DummyDurationSec = seconds;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>False in a build without the private replay engine — the panel says so instead of offering a
    /// toggle that could never do anything.</summary>
    public bool ReplayAvailable => _services.ReplayAvailable;

    public Visibility ReplayUnavailableVisibility => _services.ReplayAvailable ? Visibility.Collapsed : Visibility.Visible;

    public void OpenReplayFolder()
    {
        string dir = _services.ReplayDirectory;
        Directory.CreateDirectory(dir); // may not exist yet if nothing has been recorded
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    /// <summary>The replays folder, so the play dialog can open there by default.</summary>
    public string ReplayDirectory => _services.ReplayDirectory;

    /// <summary>Wired by the host (App) to a file picker + replay window (UI-thread work lives there, not in
    /// the VM). Raised by the "리플레이 재생" button.</summary>
    public Action? PlayReplayRequested;

    public void PlayReplay() => PlayReplayRequested?.Invoke();

    /// <summary>Wired by the host (App) to <c>MeterEngine.RequestDummyReset()</c>. Raised by the "허수아비 DPS
    /// 초기화" button — clears only the live dummy report for an immediate re-test (history is preserved).</summary>
    public Action? DummyResetRequested;

    public void ResetDummyDps() => DummyResetRequested?.Invoke();

    public void Reload()
    {
        ApplyInfo(_services.Consent.GetInfo(syncRemote: false, _services.Version));
        RefreshCharacterStatus();
        RefreshConsentCharacters();
        // 스탯 사전은 캡처 스레드가 갱신한다. 이벤트로 실시간 반영하면 디스패처를 타야 하는데, 이 화면은 열 때
        // 한 번 읽으면 충분하다 — 스탯은 전투 중에 초 단위로 바뀌는 값이 아니다.
        NotifyStatSheetChanged();
        RefreshCustomAlarms();
        CaptureConfig config = _services.BuildCaptureConfig();
        ServerIp = config.ServerIp;
        ServerPort = config.ServerPort;
    }

    /// <summary>Commit buffered hotkeys (Save).</summary>
    public void Commit()
    {
        _hotkeys.SetReset(PendingReset);
        _hotkeys.SetVisibility(PendingVisibility);
        _hotkeys.SetClickThrough(PendingClickThrough);
        _hotkeys.SetDummyToggle(PendingDummyToggle);
        _hotkeys.SetDummyReset(PendingDummyReset);
        _hotkeys.SetSplitUi(PendingSplitUi);
        _hotkeys.SetAetherList(PendingAetherList);
    }

    /// <summary>Revert live-applied settings + pending hotkeys (Cancel).</summary>
    public void Revert()
    {
        _snapshot.Apply(_settings, _controller);
        // Apply() writes the settings object directly, so nothing told the font grid its selection moved.
        OnPropertyChanged(nameof(FontFamily));
        SyncFontSelection();
        SyncNameFxPreview(); // Apply() wrote the settings object directly; the clock has to be told
        NameFxSheen.Rebuild(_settings.NameFxBrightnessPercent);
        SeedPendingHotkeys();
        Reload();
    }

    private sealed record Snapshot(
        string DisplayMode, string DamageValueMode, string RowDpsMetric, string ContributionMode, string NameDisplay,
        string FontFamily, int RowHeight, double MeterOpacity, bool MultiMonitor, string Theme, bool AutoHide,
        string TargetInfoDisplayMode, bool IsMinimal, bool ShowCombatTimerInMinimal, bool ShowTargetInfoInMinimal,
        bool ShowServerTag, string BarStyle, bool ShowJoinPanel, bool ShowPreCombatRoster, bool ShowAetherStatus,
        string NameFxMode, bool NameFxShowSelf, bool NameFxShowOthers, int NameFxSpeedPercent, int NameFxBrightnessPercent,
        bool NameFxGauge,
        bool TierShow, string TierEffects, bool TierShowOthers, bool TierShowSelfChip,
        int BuffUiIconSize, int CooldownUiIconSize, int CooldownUiPerRow, string CooldownUiTextColor,
        // ⚠️ 새 항목은 반드시 **맨 끝**에. 이 record 는 string/bool 이웃이 줄줄이라 중간에 끼우면
        // 컴파일이 통과하면서 값이 한 칸씩 밀린다. 빠뜨리면 "취소가 안 됨"이 아니라
        // **이미 저장됐고 되돌릴 수 없음**이 된다.
        string MeterLayoutId,
        bool SplitUiMode,
        int BossSlotScalePercent,
        int MeterScalePercent)
    {
        public static Snapshot Capture(MeterSettings s, OverlayController c) => new(
            s.DisplayMode, s.DamageValueMode, s.RowDpsMetric, s.ContributionMode, s.NameDisplay,
            s.FontFamily, s.RowHeight, s.MeterOpacity, s.MultiMonitorMode, s.OverlayTheme, c.IsAutoHide,
            s.TargetInfoDisplayMode, s.IsMinimal, s.ShowCombatTimerInMinimal, s.ShowTargetInfoInMinimal,
            s.ShowServerTag, s.BarStyle, s.ShowJoinPanel, s.ShowPreCombatRoster, s.ShowAetherStatus,
            s.NameFxMode, s.NameFxShowSelf, s.NameFxShowOthers, s.NameFxSpeedPercent, s.NameFxBrightnessPercent,
            s.NameFxGauge,
            s.TierShow, s.TierEffects, s.TierShowOthers, s.TierShowSelfChip,
            s.BuffUiIconSize, s.CooldownUiIconSize, s.CooldownUiPerRow, s.CooldownUiTextColor,
            s.MeterLayoutId,
            s.SplitUiMode,
            s.BossSlotScalePercent,
            s.MeterScalePercent);

        public void Apply(MeterSettings s, OverlayController c)
        {
            s.DisplayMode = DisplayMode;
            s.DamageValueMode = DamageValueMode;
            s.RowDpsMetric = RowDpsMetric;
            s.ContributionMode = ContributionMode;
            s.NameDisplay = NameDisplay;
            s.FontFamily = FontFamily;
            s.RowHeight = RowHeight;
            // 슬라이더(연속값)는 특히 여기 있어야 한다 — 세터가 즉시 파일에 쓰므로 빠뜨리면 '취소가
            // 안 됨'이 아니라 **이미 저장됐고 되돌릴 수 없음**이 되는데, 사용자는 원래 값을 기억 못 한다.
            s.BossSlotScalePercent = BossSlotScalePercent;
            // 미터 크기도 같은 이유로 필요하다. 5단 콤보였을 땐 "다시 고르면 끝"이었지만 연속 배율은
            // 원래 값을 사람이 기억하지 못한다. ⚠️ 미터를 직접 끌어 배율을 바꾼 경우에는 그게 사용자의
            // 확정 조작이므로 취소가 되돌리면 안 된다 — App 이 RebaseMeterScale 로 기준선을 옮긴다.
            s.MeterScalePercent = MeterScalePercent;
            s.MeterOpacity = MeterOpacity;
            s.MultiMonitorMode = MultiMonitor;
            s.OverlayTheme = Theme;
            c.SetAutoHide(AutoHide);
            s.TargetInfoDisplayMode = TargetInfoDisplayMode;
            s.IsMinimal = IsMinimal;
            s.ShowCombatTimerInMinimal = ShowCombatTimerInMinimal;
            s.ShowTargetInfoInMinimal = ShowTargetInfoInMinimal;
            s.ShowServerTag = ShowServerTag;
            s.BarStyle = BarStyle;
            s.ShowJoinPanel = ShowJoinPanel;
            s.ShowPreCombatRoster = ShowPreCombatRoster;
            // Every setter here writes through to the properties file immediately, so a toggle left out of this
            // record is not "unsaved on Cancel" — it is saved and unrevertable. 오드 표시 was missing, which made
            // turning it off and cancelling a one-way trip: the footer badge (and the shugo key badge it gates)
            // stayed hidden across restarts with no way back except finding the same toggle again.
            s.ShowAetherStatus = ShowAetherStatus;
            s.NameFxMode = NameFxMode;
            s.NameFxShowSelf = NameFxShowSelf;
            s.NameFxShowOthers = NameFxShowOthers;
            s.NameFxSpeedPercent = NameFxSpeedPercent;
            s.NameFxBrightnessPercent = NameFxBrightnessPercent;
            s.NameFxGauge = NameFxGauge;
            // 같은 이유로 티어 장식 4키도 여기 있어야 한다 — 빠져 있던 동안 색상·스킨 탭에서 티어 표시를
            // 껐다가 취소해도 꺼진 채로 남았다.
            s.TierShow = TierShow;
            s.TierEffects = TierEffects;
            s.TierShowOthers = TierShowOthers;
            s.TierShowSelfChip = TierShowSelfChip;
            // 버프 아이콘 크기도 같은 이유로 필요하다. 2지선다 콤보였을 땐 "다시 고르면 끝"이라 빠져 있어도
            // 티가 안 났지만, 연속 배율은 원래 값을 사람이 기억하지 못한다 — 취소해도 안 돌아오면
            // 슬라이더를 만져본 것만으로 되돌릴 수 없는 변경이 된다.
            s.BuffUiIconSize = BuffUiIconSize;
            // 쿨타임 오버레이의 연속값 3개도 같은 이유로 여기 있어야 한다 — 슬라이더와 색상은 사람이 원래
            // 값을 기억하지 못하므로, 스냅샷에서 빠지면 만져본 것만으로 되돌릴 수 없는 변경이 된다.
            s.CooldownUiIconSize = CooldownUiIconSize;
            s.CooldownUiPerRow = CooldownUiPerRow;
            s.CooldownUiTextColor = CooldownUiTextColor;
            s.MeterLayoutId = MeterLayoutId;
            // 세터가 곧바로 파일에 쓰고 App 이 창까지 갈라 놓기 때문에, 여기 없으면 '취소'가 안 되는 게 아니라
            // **이미 저장됐고 되돌릴 수 없는** 상태가 된다(이 record 맨 위 경고 그대로).
            s.SplitUiMode = SplitUiMode;
            NameFxSheen.Rebuild(NameFxBrightnessPercent);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>설정창 '내 스탯' 탭의 한 묶음. 두 칸(이름 · 값) 격자로 그려진다.</summary>
public sealed class StatGroupVM
{
    public StatGroupVM(StatSheetGroup group)
    {
        Title = group.Title;
        Rows = group.Rows;
    }

    public string Title { get; }

    public IReadOnlyList<StatSheetRow> Rows { get; }
}
