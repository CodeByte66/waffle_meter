using WaffleMeter.App.Core;
using WaffleMeter.Services;

namespace WaffleMeter.App.Wpf;

/// <summary>Outcome of an import, for the status line.</summary>
/// <param name="Applied">How many keys were written.</param>
/// <param name="BackupPath">Where the pre-import snapshot went, or null if it could not be written.</param>
/// <param name="RestartHint">True when something that only reads at startup was among the changes.</param>
public sealed record SettingsImportResult(int Applied, string? BackupPath, bool RestartHint)
{
    /// <summary>Keys deleted because the bundle recorded them as never-configured. Only a backup carries
    /// those, so this is 0 for every shared code. See <see cref="SettingsBundle.Absent"/>.</summary>
    public int Removed { get; init; }

    /// <summary>Keys the code carries that this build does not know, so nothing was done with them. A restore
    /// that skips keys is a PARTIAL restore and the status line must not call it a complete one.</summary>
    public int Skipped { get; init; }

    /// <summary>How many settings actually changed value (from the plan). Distinct from
    /// <see cref="Applied"/>, which counts writes including the ones that wrote the same value back.</summary>
    public int Changed { get; init; }

    /// <summary>Catch-up steps this applier was asked to run but has no wiring for — the owner never set the
    /// hook. Names, for the log; a non-empty list means "가져왔는데 그 항목은 이번 세션에 안 먹는다".</summary>
    public IReadOnlyList<string> Unwired { get; init; } = Array.Empty<string>();

    /// <summary>True when everything the code carried was understood and made live. The status line is allowed
    /// to claim success only for this — M-28 was, at bottom, "0건 복원해 놓고 성공 문구를 냈다".</summary>
    public bool Complete => Skipped == 0 && Unwired.Count == 0;
}

/// <summary>
/// Writes a decoded settings code into the live app.
/// <para><b>Raw in, then reload.</b> Values are written straight to the properties file and every model is then
/// told to re-read. Assigning the model properties instead would put the raw string in memory while the file
/// holds the same string — correct this session, different after a restart, because <c>GetProperty</c> re-decodes
/// on the way out. See <see cref="MeterSettings.Reload"/>.</para>
/// <para><b>One save.</b> The whole write happens inside <see cref="PropertyHandler.RunBatched"/>; otherwise
/// ~70 keys mean ~70 full-file rewrites.</para>
/// <para><b>The catch-up list is not maintained here.</b> It used to be — a hand-written sequence of
/// <c>Reload()</c> calls at the bottom of <see cref="Apply"/> — and three separate settings fell out of it
/// unnoticed (버프 픽커 M-14, 쿨타임 프리셋 순서 M-16, <c>replay.recordMovement</c> 게이트 M-26). Now every key
/// declares its <see cref="SettingsCatchUp"/> in <see cref="SettingsKeyCatalog"/>, <see cref="SettingsCatchUpPlan"/>
/// turns the imported keys into an ordered set of steps, and the constructor below refuses to exist if any step
/// has no action. Adding a setting therefore cannot silently skip its catch-up again.</para>
/// </summary>
public sealed class SettingsBundleApplier
{
    private readonly MeterServices _services;
    private readonly MeterSettings _settings;
    private readonly MeterColorTheme _theme;
    private readonly SkinManager _skin;
    private readonly OverlayController _controller;
    private readonly HotkeyHandler _hotkeys;
    private readonly BuffPresetManager _presets;
    private readonly SkillVisibility _skills;
    private readonly CooldownVisibility? _cooldownSkills;
    private readonly CooldownPresetManager? _cooldownPresets;

    private readonly Dictionary<SettingsCatchUp, Action<List<string>>> _catchUp;

    public SettingsBundleApplier(
        MeterServices services,
        MeterSettings settings,
        MeterColorTheme theme,
        SkinManager skin,
        OverlayController controller,
        HotkeyHandler hotkeys,
        BuffPresetManager presets,
        SkillVisibility skills,
        CooldownVisibility? cooldownSkills = null,
        CooldownPresetManager? cooldownPresets = null)
    {
        _services = services;
        _settings = settings;
        _theme = theme;
        _skin = skin;
        _controller = controller;
        _hotkeys = hotkeys;
        _presets = presets;
        _skills = skills;
        _cooldownSkills = cooldownSkills;
        _cooldownPresets = cooldownPresets;

        _catchUp = new Dictionary<SettingsCatchUp, Action<List<string>>>
        {
            // 언제나 첫 번째다(enum 값 0). 아래 동작들이 전부 갱신된 MeterSettings 를 읽는다.
            [SettingsCatchUp.Settings] = _ => _settings.Reload(),
            [SettingsCatchUp.Theme] = _ => _theme.Reload(),
            [SettingsCatchUp.Skin] = _ => _skin.Apply(_services.Props.GetProperty("skin") ?? _skin.Current),
            [SettingsCatchUp.OverlayWindow] = _ =>
            {
                PropertyHandler props = _services.Props;
                _controller.SetAutoHide(props.GetProperty("isAutoHide") != "false");
                _controller.SetKeepOverlayWhenHidden(props.GetProperty("keepOverlayWhenMeterHidden") == "true");
            },
            [SettingsCatchUp.Hotkeys] = _ => _hotkeys.Reload(),
            [SettingsCatchUp.BuffSelection] = unwired =>
            {
                _presets.Reload();
                // 픽커 VM 은 설정 창(SettingsViewModel)이 소유해서 여기서 직접 닿지 않는다. 안 깨우면 가져온
                // 선택이 화면에 안 보이고, 사용자가 칩 하나를 만지는 순간 스테일 캐시가 통째로 덮어쓴다(M-14).
                if (BuffPickerRefresh is { } refresh)
                {
                    refresh();
                }
                else
                {
                    unwired.Add("버프 픽커");
                }
            },
            [SettingsCatchUp.JoinSkillPicker] = _ => _skills.Reload(),
            [SettingsCatchUp.CooldownSelection] = _ =>
            {
                // 🔑 순서가 전부다. _cooldownSkills.Reload() 를 먼저 부르면 그 Changed 가 _applying 밖에서
                // 터지고, CooldownPresetManager 가 **아직 안 읽은 옛 _set** 을 cooldownUi.presets 로 되쓴다
                // — 가져온 슬롯 2·3 의 내용과 세 슬롯 이름, 활성 인덱스가 그 자리에서 사라진다(M-16).
                // 매니저의 Reload 는 Load→Apply→SetRawHidden 으로 픽커까지 제자리 갱신하므로, 매니저가 있으면
                // 픽커를 따로 부를 필요가 없다(부르면 위의 되쓰기가 그대로 재연된다).
                if (_cooldownPresets is { } manager)
                {
                    manager.Reload();
                }
                else
                {
                    _cooldownSkills?.Reload();
                }
            },
            // 가져온 설정의 음성 팩을 즉시 반영한다 — 안 하면 재시작 전까지 옛 목소리가 계속 나온다.
            [SettingsCatchUp.VoicePack] = _ =>
                TtsSpeech.SetVoicePack(new BakedVoicePack(AppContext.BaseDirectory, _settings.TtsVoice)),
            // 실효 스위치는 이 volatile 필드다. 파일과 MeterSettings 만 바꾸면 세션 내내 녹화가 안 되고(또는
            // 계속 되고), 체크박스를 다시 눌러도 세터가 조기 반환해 아무 일도 안 일어난다(M-26).
            [SettingsCatchUp.ReplayGate] = _ => _services.RecordReplay = _settings.RecordReplay,
            // DataManager 는 private volatile 필드를 게이트로 직접 읽고 props 폴백이 없다. App.xaml.cs 의
            // 미러링 핸들러는 이름으로만 비교하는데 Reload 는 빈 이름을 쏘므로 임포트 경로에서 안 걸린다(M-29).
            [SettingsCatchUp.DummyGate] = _ =>
            {
                _services.Data.DummyTestMode = _settings.DummyTestMode;
                _services.Data.DummyDurationSec = _settings.DummyDurationSec;
            },
            [SettingsCatchUp.RefreshInterval] = unwired =>
            {
                // MeterEngine 은 App 이 소유한다. 훅이 없으면 갱신 주기·저사양 모드는 재시작 전까지 옛 값이다.
                if (RefreshIntervalChanged is { } changed)
                {
                    changed();
                }
                else
                {
                    unwired.Add("갱신 주기");
                }
            },
        };

        // 새 SettingsCatchUp 값을 만들고 여기 배선을 빠뜨리면 설정 창을 여는 순간 터진다. 조용히
        // "가져왔는데 안 바뀐다"로 나가는 것보다 낫다 — M-14/16/26 은 전부 그 조용한 쪽이었다.
        string[] unmapped = Enum.GetValues<SettingsCatchUp>()
            .Where(t => !_catchUp.ContainsKey(t))
            .Select(t => t.ToString())
            .ToArray();
        if (unmapped.Length > 0)
        {
            throw new InvalidOperationException(
                "SettingsBundleApplier: 따라잡기 동작이 없는 SettingsCatchUp — " + string.Join(", ", unmapped));
        }
    }

    /// <summary>버프 픽커(설정 창 소유)를 다시 읽게 하는 훅. 설정하지 않으면 버프 선택을 담은 코드를
    /// 가져와도 픽커가 옛 캐시를 계속 들고 있다가 다음 칩 토글에서 그것을 되쓴다(M-14).</summary>
    public Action? BuffPickerRefresh { get; set; }

    /// <summary><c>MeterEngine.ReportIntervalMs</c> 를 다시 밀어 넣는 훅(App 소유). 설정하지 않으면
    /// 갱신 주기·저사양 모드가 재시작 전까지 안 먹는다(M-29).</summary>
    public Action? RefreshIntervalChanged { get; set; }

    /// <summary>Keys nothing re-reads at runtime. Changing one is honest about needing a restart rather than
    /// silently doing nothing — "가져왔는데 안 바뀐다" is the complaint that produces.</summary>
    private static readonly string[] RestartOnly = { "captureBackend", "vrrCompatMode" };

    public SettingsImportResult Apply(SettingsBundlePlan plan, string appVersion, DateTimeOffset now)
    {
        PropertyHandler props = _services.Props;

        // Before anything is written. The settings window's Cancel restores 19 values captured when the window
        // opened, so it cannot undo this — the snapshot is the only way back.
        string? backup = SettingsBackupStore.Save(props, appVersion, now);

        int applied = 0, skipped = 0;
        var touched = new List<string>();
        props.RunBatched(() =>
        {
            foreach ((string key, string value) in plan.Bundle.Data)
            {
                if (!SettingsKeyCatalog.IsKnown(key))
                {
                    skipped++; // a key from a newer build, or one we retracted
                    continue;
                }

                props.SetProperty(key, value);
                touched.Add(key);
                applied++;
            }

            // 백업만 담는 "이 키는 손댄 적이 없었다" 표시를 되돌린다. 기본값을 써 넣는 것과는 다르다 —
            // 써 넣은 기본값은 그 기본값이 바뀌는 날 사용자의 선택으로 둔갑하고, "한 번도 안 건드림"을 보는
            // 게이트도 속인다. 그래서 복원은 삭제다(M-28 / 판정31).
            foreach (string key in plan.Clear)
            {
                props.RemoveProperty(key);
                touched.Add(key);
            }
        });

        // 무엇을 깨울지는 카탈로그가 정한다. 여기서 손으로 나열하지 않는 것이 이 클래스의 요점이다.
        var unwired = new List<string>();
        foreach (SettingsCatchUp step in SettingsCatchUpPlan.For(touched))
        {
            _catchUp[step](unwired);
        }

        bool restart = plan.Bundle.Data.Keys.Any(k => RestartOnly.Contains(k, StringComparer.Ordinal));
        return new SettingsImportResult(applied, backup, restart)
        {
            Removed = plan.Clear.Count,
            Skipped = skipped,
            Changed = plan.Changes.Count,
            Unwired = unwired,
        };
    }
}
