using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WaffleMeter.App.Core;
using WaffleMeter.Capture;
using WaffleMeter.Capture.Live;
using WaffleMeter.Data;
using WaffleMeter.Services;
using WaffleMeter.Stats;

namespace WaffleMeter.App.Wpf;

public partial class App : Application
{
    private MeterEngine? _engine;
    private MeterSettings? _settings;
    private MeterColorTheme? _theme;
    private SkinManager? _skin;
    private UpdateService? _updateService;
    private HotkeyHandler? _hotkeys;
    private OverlayController? _controller;
    private TrayIconController? _tray;
    private OverlayWindow? _overlayWindow;
    // 사용자가 이번 실행에서 세로 핸들로 미터 높이를 고정했는가. 일부러 저장하지 않는다 — 앱을 다시 켜면
    // 행 수 자동 맞춤으로 돌아온다(v2.8.0까지의 거동).
    private bool _meterHeightManual;
    private DpsReport? _lastReport;
    private DetailWindow? _detailWindow;
    private DetailsViewModel? _detailViewModel;
    private int _detailUid;
    private JoinRequestPanel? _joinPanel;
    private JoinRequestViewModel? _joinViewModel;
    private bool _joinPanelPositioned;
    private bool _joinUserDismissed;                         // user closed the panel — suppress auto-show…
    private readonly HashSet<int> _joinDismissedIds = new(); // …until a requester NOT in this set applies (option a)
    private SkillSettingsFlyout? _skillFlyout;
    private bool _skillFlyoutVisible;
    private SettingsWindow? _settingsWindow; // single instance; the ⚙ button toggles it (open/close), not stacks
    private ReplayWindow? _replayWindow; // single instance; the tray item toggles it (open/close)
    private HistoryPanel? _historyPanel;
    private BattleHistoryViewModel? _historyViewModel;
    private bool _historyPanelPositioned;
    private bool _historyPanelVisible;
    /// <summary>The supported-encounter catalog, held here so windows built outside the startup scope (the
    /// replay player) can label a boss with its difficulty too. Empty until the catalogs load.</summary>
    private WaffleMeter.Data.EncounterCatalog _encounters = WaffleMeter.Data.EncounterCatalog.Empty;
    private AetherPanel? _aetherPanel;
    private AetherPanelViewModel? _aetherViewModel;
    private bool _aetherPanelPositioned;
    private bool _aetherPanelVisible;
    /// <summary>The 컨텐츠 관리 panel's shipped size, kept so "위치 초기화" can restore it.</summary>
    private (double W, double H) _aetherPanelDefaultSize;

    /// <summary>Weekly 성역 counters from a 0x610B dump, held until the identity they belong to is established.
    /// See <see cref="OnWeeklyContentBroadcast"/> for why filing them on arrival is wrong.</summary>
    private readonly Dictionary<WeeklyContentKind, (int Remaining, long AtMs)> _weeklyContentPending = new();

    /// <summary>어비스 회랑 이용 시간 from a 0x610B dump, held under the same rule as the weekly counters — the
    /// dump names no character, and filing it early writes one character's corridors onto another's row.</summary>
    private readonly Dictionary<int, (long RemainingMs, long AtMs)> _abyssCorridorPending = new();

    // 어비스 아티팩트 점령 현황 frames whose SERVER is not known yet. The 0xE307 login broadcast lands about
    // eight seconds before the packet that names the character (measured 2026-08-28: 23:47:43.491 against
    // 23:47:51.529), and the store is keyed by server, so it waits exactly like the 0x610B dump does.
    private readonly Dictionary<int,
        (long CycleStartMs, long CycleEndMs, IReadOnlyList<AbyssArtifactHolding> Holdings, long AtMs)>
        _abyssArtifactPending = new();

    /// <summary>The corridor map the character is currently standing in, or 0.
    /// <para>Entering one starts the clock and leaving stops it — leaving is the only chance to turn an early
    /// exit into a real number, because the server says nothing more until the budget is gone.</para>
    /// <para>The MAP is what drives this, not the ticket broadcast. A ticket arriving with time on it means one
    /// of two different things — the character walked in, or 점령전 just stocked it — and only the map tells
    /// them apart. Driving it from the map also covers the case the server never reports at all: walking back
    /// into a corridor that still has time on it, where there is no broadcast to react to.</para></summary>
    private int _corridorInsideMapId;

    /// <summary>The character whose corridor clock is running, or null. Held rather than re-read at stop time:
    /// a character switch is exactly when the clock must be stopped, and by then
    /// <c>CurrentCharacterHash()</c> already names the INCOMING character — stopping against that would leave
    /// the outgoing one burning forever while crediting the new one with a corridor it never entered.</summary>
    private string? _corridorClockHash;

    /// <summary>Last time an open panel's corridor times were re-rendered. The clock is a projection, so only a
    /// redraw moves it, but the report loop ticks far faster than the one second a "m:ss" readout can show.</summary>
    private long _corridorRefreshedAtMs;
    private bool _viewingHistory;
    private long _historyBaselineBattleStart;
    // Pre-combat party preview: the roster = recent boss-combat contributors (the party). Combat is the only
    // reliable party signal — a 0x3645 nickname snapshot fires for EVERY nearby player, so in town that lists
    // strangers. A member fades after this long with no combat (leaving the party / lingering in town).
    private const long PreCombatPartyTtlMs = 300_000; // 5 min
    private readonly Dictionary<int, long> _partyLastCombatMs = new();
    private readonly HashSet<string> _consentPrompted = new();
    private bool _consentDialogOpen;
    private int _lastConsentBackfillId; // executor uid whose name was last persisted into its consent record
    private string? _lastConsentSyncHash; // identity whose consent was last re-read from the server (once per character)

    /// <summary>identityHash → career tier rank, as the server reported it. Written on the upload worker thread
    /// (the receipt carries the uploader's tier) and read on the UI thread every report tick, hence concurrent.
    /// A character absent here still gets a 이번 전투 등급 computed locally.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _careerTiers = new(StringComparer.Ordinal);
    private UpdateToast? _updateToast;
    private UpdateToastViewModel? _updateToastVm;
    private AlarmToast? _alarmToast;
    private AlarmToastViewModel? _alarmToastVm;
    private AlarmController? _alarms;
    private volatile bool _combatActive; // recent damage activity — gates the "mute field-boss alarm in combat" option
    private BuffOverlayPanel? _buffOverlay;
    /// <summary>사용자가 정한 버프 오버레이 위치("집"). 이 창만 SizeToContent 라 폭이 스스로 자라는데,
    /// 클램프로 밀린 좌표를 저장해 버리면 세션 내내 왼쪽으로 밀려나기만 한다. 저장값은 여기 두고 실제
    /// 위치는 매번 여기서 다시 계산한다 — 넓어지면 화면 안으로 끌려오고, 다시 좁아지면 제자리로 돌아온다.</summary>
    private Point? _buffOverlayHome;
    private BuffOverlayViewModel? _buffOverlayVm;
    private BuffPresetManager? _buffPresets;
    private System.Windows.Threading.DispatcherTimer? _buffTimer;

    private CooldownOverlayPanel? _cooldownOverlay;
    /// <summary>사용자가 정한 쿨타임 오버레이 위치("집"). 버프 오버레이와 같은 이유로 따로 둔다 — 이 창도
    /// SizeToContent 라 스킬이 하나씩 학습될 때마다 폭이 자라고, 클램프로 밀린 좌표를 저장하면 세션 내내
    /// 왼쪽으로 밀려나기만 한다.</summary>
    private Point? _cooldownOverlayHome;
    private CooldownOverlayViewModel? _cooldownOverlayVm;
    private System.Windows.Threading.DispatcherTimer? _cooldownTimer;
    private CooldownVisibility? _cooldownVisibility;
    private CooldownPickerViewModel? _cooldownPickerVm;
    private CooldownPickerFlyout? _cooldownFlyout;
    private bool _cooldownFlyoutVisible;
    private CooldownPresetManager? _cooldownPresets;

    /// <summary>Auto-reset event the FIRST instance owns (set by <see cref="Program"/>); a later launch
    /// opens it by name and signals it instead of spawning a colliding UI. We wait on it and surface the
    /// overlay (un-hide from tray) so relaunching the shortcut brings the running instance back.</summary>
    public EventWaitHandle? SingleInstanceShowSignal { get; set; }

    public App()
    {
        // Surface UI-thread exceptions instead of hard-crashing, so a faulty window/binding is
        // diagnosable (and the app survives). Logs next to the exe too.
        DispatcherUnhandledException += (_, args) =>
        {
            TryLogCrash(args.Exception);
            System.Windows.MessageBox.Show(args.Exception.ToString(), "waffle_meter 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => TryLogCrash(args.ExceptionObject as Exception);
    }

    private static void TryLogCrash(Exception? ex)
    {
        if (ex == null)
        {
            return;
        }

        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), $"{DateTime.Now:o}\n{ex}\n\n");
        }
        catch
        {
            // best effort
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Velopack lifecycle hooks run earlier, in Program.Main (before this App is constructed).
        base.OnStartup(e);

        var props = new PropertyHandler();
        // Overlay render mode. Default = software (no GPU compositing): keeps the overlay off the game's
        // GPU path (with WS_EX_NOACTIVATE it also never steals foreground) AND is the friendliest to
        // variable-refresh-rate (FreeSync/G-Sync) displays, where a GPU-composited transparent overlay can
        // break the game's flip/independent-present path and cause stutter. A user whose setup does better
        // with GPU rendering can turn the compat mode off. Process-global + read once → needs a restart.
        bool vrrCompat = props.GetProperty("vrrCompatMode") != "false";
        RenderOptions.ProcessRenderMode = vrrCompat ? RenderMode.SoftwareOnly : RenderMode.Default;

        // Text stays on WPF's default TextFormattingMode.Ideal. Display mode would grid-fit the glyphs and
        // visibly sharpen the 11-13 px UI text, but it also quantizes glyph advances to whole pixels, so every
        // bundled font collapses onto the same horizontal rhythm — measured, the four bundled families grow
        // 13% more alike, and moving one font from Ideal to Display shifts it about as far as swapping it for
        // a different family. Keeping the typeface's own metrics is the deliberate choice here.
        //
        // Nor is there a way to soften the antialiasing instead: TextRenderingMode.Aliased and
        // TextHintingMode.Fixed are both silently ignored under Ideal (verified — pixel-identical output),
        // and Animated hinting only smears further. The one lever that sharpens without touching the
        // typeface is snapping the text origin to a whole pixel, i.e. UseLayoutRounding on the meter
        // overlay (OverlayWindow.xaml) — it cut the overlay's partially-covered glyph pixels from 33% to
        // 29%. The settings window's origins already land on whole pixels, so it gains nothing there.

        var services = new MeterServices(
            props,
            nameFxCatalogue: new NameFxCatalogue(NameFxPalette.IsKnownNameEffect, NameFxPalette.IsKnownGauge));
        TryLoadCatalogs(services);
        _encounters = services.Data.Encounters;

        // Apply the persisted skin (palette) into Application.Resources before any window is built.
        _skin = new SkinManager(services.Props);
        _skin.ApplyInitial();

        _settings = new MeterSettings(services.Props);
        _theme = new MeterColorTheme(services.Props);
        SkinManager skinManager = _skin;
        var viewModel = new OverlayViewModel(
            services.Version, _settings, _theme, () => skinManager.IsLight, services.Data.Encounters,
            // Evaluated per report tick: the trial's difficulty arrives a few seconds after zone-in, so it is
            // not known when the target line is first drawn.
            () => services.Data.TrialDifficulty.Current is { IsTrial: true } t ? t.Label : null);
        skinManager.Changed += viewModel.RefreshSkin; // re-theme stat colors on light/dark swap
        var window = new OverlayWindow { DataContext = viewModel };
        LoadPosition(services.Props, window);
        // The meter auto-sizes its HEIGHT to the row count (SizeToContent=Height) so no scrollbar appears;
        // only WIDTH is user-resizable + persisted.
        // The upload receipt carries the uploader character's career tier — the only place a standing enters the
        // meter, and it costs no extra request.
        services.UploadQueue.TierReceived = (hash, tier) => _careerTiers[hash] = tier.TierRank;

        // 던전 티어는 표시 중인 리포트에서 파생시킨다 — 라이브든 저장 전투든 같은 함수를 탄다. 순수 로컬 계산이라
        // (받아둔 분포에 그 전투의 dps를 대입할 뿐) 네트워크를 타지 않는다.
        //
        // 저장 전투에는 커리어 티어를 얹지 않는다. 커리어 티어는 "지금 이 캐릭터의 성적"이라, 지난 전투 화면에
        // 오늘의 등급을 섞으면 칩이 '실버 · 상위 12.3%'처럼 서로 다른 시점을 한 줄에 붙여 말하게 된다. 기록 화면은
        // 전부 그 전투 기준이다.
        viewModel.TierResolver = report => TierEvaluator.Evaluate(
            report,
            services.Tier.Artifact,
            _viewingHistory ? null : _careerTiers,
            u => StatsIdentity.CharacterIdentityHash(u.Server, u.Nickname),
            // 시련은 난이도가 mobCode에 안 실려서 아티팩트의 몹 맵으로는 좌표가 안 나온다. 어픽스로 읽은
            // 값을 넘겨주면 아티팩트의 trial gate가 "이 난이도가 맞을 때만" 좌표를 내준다.
            services.Data.TrialDifficulty.Current);

        // nDPS/rDPS 도 같은 방식으로 표시 중인 리포트에서 파생시킨다. 저장 전투는 저장 시점에 얼려 둔 값을
        // 그대로 돌려주고(버프 저장소가 이미 비워졌으므로 재계산이 불가능하다), 라이브는 지금 값을 다시 센다.
        viewModel.MetricsResolver = report => services.Calculator.GetDpsMetrics(report);

        // 후원자·랭커 닉네임 연출 명단. 파일이 없으면 아무도 연출을 갖지 않는다 — 서버 배포 채널이 붙기
        // 전까지가 그 상태다. 공개 repo 에 동봉하지 않는 이유는 부여를 철회해도 git 히스토리에서는 회수할
        // 수 없기 때문이다.
        //
        // 저장 전투 재생에서도 그대로 뜬다 — 바로 위 커리어 티어와는 반대 결정이고, 의도한 것이다. 티어는
        // '오늘의 성적'이라 지난 전투 화면에 섞으면 서로 다른 시점을 한 줄에 붙여 말하게 되지만, 연출은
        // 시점이 아니라 '이 사람이 후원자/랭커다'라는 신원 표식이라 어제 전투에서도 같은 사실이다.
        viewModel.SetNameFxRoster(services.NameFx.Roster);

        // 서비스가 새 명단을 받아 와도 이 배선이 없으면 화면은 다음 실행까지 안 바뀐다 — 오버레이가 부여를
        // (서버, 닉네임)으로 메모하고 있어서 SetNameFxRoster 만이 그 메모를 비우기 때문이다. 실패도 로그도
        // 남지 않는 종류의 무동작이라 여기에 적어 둔다. Changed 는 워커 스레드에서 온다.
        services.NameFx.Changed += roster => Dispatcher.BeginInvoke(() => viewModel.SetNameFxRoster(roster));
        NameFxSheen.Rebuild(_settings.NameFxBrightnessPercent);

        MigrateMeterWidthForTierChip(services.Props);
        LoadWindowWidth(services.Props, "meterWidth", window);
        window.Show();
        _overlayWindow = window;
        AttachScreenClamp(window);
        ClampWhenLoaded(window); // pull a stale/off-screen restored position back onto a live monitor
        // 미터는 높이를 저장하지 않는다(widthOnly) — 대신 드래그가 끝날 때마다 자동 맞춤을 되살릴지 판단한다.
        // WPF는 크기 조절이 시작되면 방향과 무관하게 SizeToContent를 꺼버리므로(WindowResizePolicy 참조),
        // 폭만 조절한 드래그였다면 여기서 다시 켜줘야 인원 수에 따라 높이가 계속 따라온다.
        AttachResize(window, services.Props, "meterWidth", "meterHeight", widthOnly: true, onResizeEnd: e =>
        {
            _meterHeightManual = WindowResizePolicy.NextManual(
                _meterHeightManual, e.HitCode, e.HeightBefore, e.HeightAfter);
            if (!_meterHeightManual)
            {
                window.SizeToContent = SizeToContent.Height;
            }
        });
        // ③ 수동으로 높이를 고정한 뒤에도 파티 인원(행 수)이 바뀌면 자동 맞춤으로 복귀시킨다. 세로 핸들은
        // 그대로 두되, 맞춰둔 높이가 인원 변화로 어차피 안 맞게 되는 순간엔 자동 추종이 낫다는 사용자 요구.
        // Rows는 증분 동기화(값 교체는 Rows[i]=, 인원 변화만 Add/RemoveAt)라 Count 변화가 곧 인원 변화다.
        // CollectionChanged는 보고 갱신과 함께 UI 스레드에서 발화하므로 여기서 SizeToContent를 만져도 안전하다.
        int meterRowCount = viewModel.Rows.Count;
        viewModel.Rows.CollectionChanged += (_, _) =>
        {
            int now = viewModel.Rows.Count;
            if (WindowResizePolicy.ShouldReautoFit(_meterHeightManual, meterRowCount, now))
            {
                _meterHeightManual = false;
                window.SizeToContent = SizeToContent.Height; // 새 행 수에 맞춰 다시 자동 높이
            }

            meterRowCount = now;
        };
        // Snap all windows back onto a monitor the moment multi-monitor movement is turned off.
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MeterSettings.MultiMonitorMode) && !_settings.MultiMonitorMode)
            {
                Dispatcher.BeginInvoke(ClampAllWindows);
            }
        };

        // Re-clamp every window onto a live monitor when the display topology changes (a monitor
        // unplugged / resolution or arrangement change can otherwise strand a window off the desktop).
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Auto-hide / park-present + tray (Kotlin BrowserApp behavior).
        _controller = new OverlayController(window, services.Props);
        _controller.Start();
        if (_settings.TaskbarMode)
        {
            _controller.SetTaskbarMode(true); // restore persisted taskbar/alt-tab mode
        }
        _tray = new TrayIconController(window, _controller, () => Dispatcher.Invoke(ExitApp),
            services.Movement != null ? () => OpenReplay(services, window) : null,
            DevPacketLogReplay.IsAvailable(VersionConfig.Resolve().Version) ? () => LoadPacketLog(services) : null,
            // Resolved at click time: WireAetherPanel subscribes later in this same startup.
            window.RequestAetherList);
        window.PositionChanged += (left, top) => SavePosition(services.Props, left, top);

        // Single-instance: surface this (running) instance when a later launch signals us, so relaunching
        // the shortcut un-hides the overlay instead of spawning a second UI that would collide on the pipe.
        StartSingleInstanceListener();

        // Global hotkeys (Ctrl+R reset / Ctrl+H visibility / Ctrl+T click-through). Callbacks fire on
        // the listener thread, so marshal window ops to the dispatcher.
        OverlayController controller = _controller;
        _hotkeys = new HotkeyHandler(services.Props)
        {
            OnReset = () => { _viewingHistory = false; _engine?.RequestReset(); }, // clears saved battles + live data, keeps recognized characters (consumer thread)
            OnVisibility = () => Dispatcher.Invoke(controller.ToggleVisibility),
            OnClickThrough = () => Dispatcher.Invoke(() =>
            {
                window.SetClickThrough(!window.ClickThrough);
                _buffOverlay?.SetClickThrough(window.ClickThrough); // buff overlay follows the meter at once
            }),
            // 허수아비 mode toggle marshals to the UI thread (it raises PropertyChanged that WPF bindings read);
            // the reset only flips a volatile flag on the engine, so it's fine straight off the listener thread.
            OnDummyToggle = () => Dispatcher.Invoke(() => _settings.DummyTestMode = !_settings.DummyTestMode),
            OnDummyReset = () => _engine?.RequestDummyReset(),
        };
        _hotkeys.Start();

        // Right-click overlay -> 설정 / 종료.
        HotkeyHandler hotkeys = _hotkeys;
        MeterSettings settings = _settings;
        MeterColorTheme theme = _theme;
        SkinManager skin = _skin;
        window.SettingsRequested += () =>
        {
            // Toggle like the other panels (전투 기록 / 파티 신청): a second press on the ⚙ button closes the
            // open window instead of stacking another one. The window nulls the field on close (✕ / Esc / Alt+F4),
            // so the next press reopens a fresh instance.
            if (_settingsWindow != null)
            {
                _settingsWindow.Close();
                return;
            }

            // _buffPresets is assigned later in OnStartup, well before the overlay exists to raise this.
            var svm = new SettingsViewModel(services, settings, theme, skin, controller, hotkeys, _buffPresets!, new GameOptimizerService(), _cooldownPresets);
            if (_skillVisibility is { } skills)
            {
                svm.BundleApplier = new SettingsBundleApplier(services, settings, theme, skin, controller, hotkeys, _buffPresets!, skills, _cooldownVisibility, _cooldownPresets);
            }

            // 전투가 도는 중에 70키를 밀면 전 행 리페인트 + 스킨 사전 교체 + 전역 핫키 재등록이 한꺼번에
            // 일어난다. 끝나고 하면 된다.
            svm.IsCombatActive = () => _combatActive;
            svm.CheckUpdateRequested = () => _ = _updateService?.CheckAndDownloadAsync(msg => Dispatcher.Invoke(() => viewModel.Status = msg));
            svm.ResetPositionRequested = which => ResetPanelPosition(which, services, window);
            svm.PlayReplayRequested = () => PlayReplayFromPicker(services, window);
            svm.DummyResetRequested = () => _engine?.RequestDummyReset(); // 허수아비 DPS 초기화 button (settings tab)
            svm.CooldownPickerRequested = () => ToggleCooldownPicker(services);
            var settingsWindow = new SettingsWindow(svm) { Owner = window };
            LoadWindowSize(services.Props, "settingsWidth", "settingsHeight", settingsWindow);
            settingsWindow.SizeChanged += (_, _) =>
            {
                services.Props.SetProperty("settingsWidth", settingsWindow.ActualWidth.ToString("0", CultureInfo.InvariantCulture));
                services.Props.SetProperty("settingsHeight", settingsWindow.ActualHeight.ToString("0", CultureInfo.InvariantCulture));
            };
            settingsWindow.Closed += (_, _) =>
            {
                if (ReferenceEquals(_settingsWindow, settingsWindow))
                {
                    _settingsWindow = null;
                }
            };
            _settingsWindow = settingsWindow;
            settingsWindow.Show();
        };
        window.ExitRequested += () =>
        {
            // Honor the CloseAction setting (React closeAction): exit / tray-hide / ask-once.
            string action = settings.CloseAction;
            if (action == "tray") { controller.HideToTray(); return; }
            if (action == "exit") { ExitApp(); return; }

            var dlg = new CloseActionDialog { Owner = window };
            dlg.ShowDialog();
            if (dlg.Choice == CloseActionDialog.CloseChoice.Cancel) { return; }
            settings.CloseAction = dlg.Choice == CloseActionDialog.CloseChoice.Tray ? "tray" : "exit"; // remember the choice
            if (dlg.Choice == CloseActionDialog.CloseChoice.Tray) { controller.HideToTray(); }
            else { ExitApp(); }
        };
        window.ResetRequested += () => { _viewingHistory = false; _engine?.RequestReset(); };
        window.ThemeRequested += () => skin.Cycle(); // 테마 버튼: cycle dark → midnight → slate
        window.TaskbarToggleRequested += () =>
        {
            bool next = !controller.TaskbarMode;
            settings.TaskbarMode = next;
            controller.SetTaskbarMode(next);
        };
        // 허수아비 테스트 header button: flip the one shared setting (mirrored live onto the capture gate above);
        // the header icon's accent state follows via a DataTrigger bound to Settings.DummyTestMode.
        window.DummyTestToggleRequested += () => settings.DummyTestMode = !settings.DummyTestMode;

        // Row click -> open/close the detail window for that player.
        viewModel.SelectionToggled += uid => ToggleDetail(uid, services, window, viewModel);

        // Party join-request panel (Kotlin JoinRequest family -> React JoinRequestPanel).
        WireJoinPanel(services, window);

        // Battle-history panel (React HistoryPanel): the 기록 header button toggles it.
        WireHistoryPanel(services, window, viewModel);

        // 오드 목록 panel: the footer 오드 badge toggles it.
        WireAetherPanel(services, window);

        // Capture runs in the elevated CaptureHost; the UI connects over the pipe (no admin here).
        // EnsureServing (below) already launches the helper, absorbs any UAC prompt, and WAITS for the
        // pipe to appear before we connect — so by connect time a healthy helper accepts in milliseconds.
        // The connect budget is therefore modest: a longer wait would only prolong the failure when the
        // single serve-once pipe is already OCCUPIED by another (e.g. pre-guard/old-build) instance.
        // captureBackend setting: "windivert" (default, embedded) or "npcap" (needs Npcap installed).
        string backend = services.Props.GetProperty("captureBackend") ?? "windivert";
        _engine = new MeterEngine(services, new NamedPipeCaptureClient(backend, connectTimeoutMs: 10_000));
        // Frame-drop relief: apply the persisted refresh interval, and keep it in sync live. Low-spec mode
        // pins it (EffectiveRefreshIntervalMs); the slider is otherwise honored.
        MeterEngine engine = _engine;
        _engine.ReportIntervalMs = _settings.EffectiveRefreshIntervalMs;
        // 허수아비 test mode: seed the capture pipeline from the persisted setting, then mirror live changes (the
        // header toggle / settings tab / hotkey all write MeterSettings — one source of truth) onto the gate.
        services.Data.DummyTestMode = _settings.DummyTestMode;
        services.Data.DummyDurationSec = _settings.DummyDurationSec;
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MeterSettings.RefreshIntervalMs) or nameof(MeterSettings.LowSpecMode))
            {
                engine.ReportIntervalMs = _settings.EffectiveRefreshIntervalMs;
            }
            else if (e.PropertyName == nameof(MeterSettings.DummyTestMode))
            {
                services.Data.DummyTestMode = _settings.DummyTestMode;
            }
            else if (e.PropertyName == nameof(MeterSettings.DummyDurationSec))
            {
                services.Data.DummyDurationSec = _settings.DummyDurationSec;
            }
        };
        _engine.ReportUpdated += report => Dispatcher.Invoke(() =>
        {
            _lastReport = report;
            // Combat-active = recent damage; a few seconds of grace covers the gaps between hits in a fight.
            // Set from the LIVE report before any early-return (history replay) so the field-boss "mute in
            // combat" gate always reflects real combat, not the displayed (frozen) battle.
            _combatActive = report.Information.Count > 0
                && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - report.BattleEnd < 5000;

            // The footer's resource badges (오드 / 슈고 열쇠 / ping) describe the LIVE session, not the battle on
            // screen, so they are pushed before the history early-return below. Leaving them after it froze all
            // three the moment a saved battle was opened — and since _viewingHistory only clears when a NEW
            // battle starts or on reset, a badge that happened to be hidden then stayed hidden indefinitely.
            (int aBase, int aBonus, int _, bool aHas) = services.Data.CurrentAether;
            (long aAtMs, bool _, bool aLive) = services.Data.AetherOrigin;
            if (aHas && !aLive)
            {
                // Restored, not measured: carry it over the 자연회복 accrued since it was taken. Projected HERE,
                // every tick, from the stored raw reading — so the badge keeps up with a long session instead of
                // freezing on the estimate made at launch, and agrees with the two lists (which do the same).
                // Because the stored value is never the projected one, re-projecting cannot compound.
                (aBase, aBonus) = AetherRegen.Project(
                    aBase, aBonus, aAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            viewModel.SetAether(aBase, aBonus, aHas, estimated: !aLive);
            (int sBase, int sBonus, int _, bool sHas) = services.Data.CurrentShugoKey;
            viewModel.SetShugoKey(sBase, sBonus, sHas);
            viewModel.SetPing(services.CurrentPing());

            // Filing a held balance dump is live bookkeeping too — it must not stall behind the history
            // early-return below, or a zone-in while a saved battle is open would never reach the store.
            FlushPendingAether(services);

            // While viewing a saved battle, hold the overlay until a NEW battle begins (React resets the
            // selected history when isInCombat); the open detail follows the SAME displayed battle (below).
            if (_viewingHistory)
            {
                if (report.BattleStart > _historyBaselineBattleStart)
                {
                    _viewingHistory = false;
                }
                else
                {
                    // Still replaying a saved battle: the overlay stays frozen on it, so refresh the open
                    // detail against the SAME displayed (saved) report. Refreshing with the LIVE `report`
                    // here is what made a detail opened on a history row blank out into a raw-uid title +
                    // all-zero stats once the live battle moved on.
                    _detailViewModel?.Refresh(viewModel.CurrentReport ?? report);
                    return;
                }
            }

            // Pre-combat party preview: remember everyone dealing damage to the boss with me (the party) and
            // feed them as the idle roster, so a fresh dungeon entry shows the party — not every nearby player
            // (a nickname snapshot fires for all nearby players, which in town is strangers). OverlayViewModel
            // only merges this while idle, so combat rows are untouched. Only live reports reach here (history
            // replay returns above).
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (int combatUid in report.Information.Keys)
            {
                _partyLastCombatMs[combatUid] = nowMs;
            }
            int execUid = services.Data.ExecutorId();
            // Authoritative party from the 0x9702 roster packet (fires on party formation, so the party
            // shows on dungeon entry BEFORE any combat), unioned with recent boss-combat contributors as a
            // fallback (covers a party seen only in combat / before its roster snapshot arrives). Dedup by
            // uid, executor first then power desc.
            var rosterById = new Dictionary<int, User>();
            // The 0x9702-only party (no combat-contributor fallback): the party-context guard for lost-executor
            // recovery needs the AUTHORITATIVE party, because at a field boss the fallback below would fold the
            // zerg into the display roster and defeat the guard.
            List<User> authoritativeParty = services.Data.PartyRoster(PreCombatPartyTtlMs).ToList();
            foreach (User member in authoritativeParty)
            {
                rosterById[member.Id] = member;
            }

            // 현재 파티(최신 0x9702 스냅샷)의 신원 집합. 아래 0x9200 프로필·최근 전투 기여자 폴백은 5분 TTL이라
            // 파티를 떠난 이전 파티원을 대기 프리뷰에 계속 누적한다(실측: 파티 교체가 잦은 던전에서 게임 파티는
            // 5명인데 프리뷰엔 떠난 멤버까지 6~9명 쌓임). 0x9702 로스터가 있을 땐 현재 파티에 있는 멤버(닉+서버)만
            // 폴백으로 추가하고, 로스터가 없으면(필드보스·입장 버스트로 로스터 미도착) 종전대로 폴백을 그대로 쓴다.
            // 본인은 파티 패킷이 흔히 제외하므로 뒤에서 별도 주입(execUid 예외).
            var currentPartyIds = new HashSet<(string, int)>(
                services.Data.PartyRosterIdentities(PreCombatPartyTtlMs).Select(m => (m.Nickname, m.Server)));
            bool filterToCurrentParty = currentPartyIds.Count > 0;

            // 0x9200 멤버 프로필(uid 동반)도 프리뷰 로스터에 넣는다 — 0x9702 로스터 스냅샷이 입장 버스트에서
            // 유실돼도(실측: 세션당 1회뿐이거나 아예 0회) 파티가 미리 뜨게 하는 이중 소스.
            foreach ((int mpUid, string mpNick, int mpServer) in services.Data.MemberProfileRoster(PreCombatPartyTtlMs))
            {
                if (!rosterById.ContainsKey(mpUid) && !string.IsNullOrWhiteSpace(mpNick)
                    && (!filterToCurrentParty || currentPartyIds.Contains((mpNick, mpServer))))
                {
                    rosterById[mpUid] = new User(mpUid, mpNick, mpServer);
                }
            }
            foreach (KeyValuePair<int, long> kv in _partyLastCombatMs)
            {
                if (nowMs - kv.Value > PreCombatPartyTtlMs || rosterById.ContainsKey(kv.Key))
                {
                    continue;
                }

                User? u = services.Data.User(kv.Key);
                if (u != null && !string.IsNullOrWhiteSpace(u.Nickname)
                    && (!filterToCurrentParty || u.Id == execUid || currentPartyIds.Contains((u.Nickname!, u.Server))))
                {
                    rosterById[kv.Key] = u;
                }
            }

            // Did a real party source (0x9702 roster / recent combat) report anyone? If not, the only thing in the
            // preview is the self-injection below — a purely solo preview we suppress (see the solo filter). In a
            // dungeon the 0x9702 packet fires (even a party-of-1), so this is true there and self shows.
            bool hasPartySource = rosterById.Count > 0;

            // Pin the recognized 본인 so the local player shows in the pre-combat preview even when the 0x9702
            // roster omits self (party packets often exclude the local player) or its name+server hasn't matched
            // a uid yet. Dedup by uid: if self already arrived via 0x9702 or combat, keep that object (it may
            // carry a better server/power). Self sorts first via the executor-first OrderBy below.
            if (execUid != 0 && !rosterById.ContainsKey(execUid))
            {
                User? self = services.Data.User(execUid);
                if (self != null && !string.IsNullOrWhiteSpace(self.Nickname))
                {
                    rosterById[execUid] = self;
                }
            }

            List<User> partyRoster = rosterById.Values
                .OrderByDescending(u => u.Id == execUid)
                .ThenByDescending(u => u.Power)
                .ToList();
            // Dedup by character identity (nickname+server): 본인 can persist under an OLD uid (e.g. before a
            // town→dungeon re-instance, kept since reset preserves users) AND the current executor uid — both
            // same nickname+server — so the 0x9702 name-match + self-inject would list 본인 twice. Keep the first;
            // the executor sorts first, so 본인 keeps its executor uid (self-coloring). Also collapses any other
            // same-character-different-uid duplicate across the roster sources.
            var seenIdentity = new HashSet<(string, int)>();
            partyRoster = partyRoster
                .Where(u => string.IsNullOrWhiteSpace(u.Nickname) || seenIdentity.Add((u.Nickname!, u.Server)))
                .ToList();
            // Suppress a PURELY self-injected solo preview (no party source) — e.g. in town right after a reset,
            // where the party roster was cleared but self is still recognized. In a dungeon, 0x9702 fires (even a
            // party-of-1), so hasPartySource is true and self still shows while waiting for members to join.
            if (!hasPartySource && partyRoster.Count == 1 && partyRoster[0].Id == execUid)
            {
                partyRoster.Clear();
            }

            // 0x9702 로스터가 이름은 실어 왔지만 그 멤버의 uid가 이번 세션에 아직 해석되지 않았으면(공간상 먼
            // 2파티원은 0x3645 신원 패킷이 희박하다) PartyRoster()가 그 멤버를 버려 프리뷰에서 사라진다 —
            // 실측: 10인 공대 입장 시 2파티원 3명이 빠진 채 7명만 뜸. 전투 전 프리뷰는 파티 전원을 보여줘야
            // 하므로, 아직 안 뜬 raw 0x9702 이름을 placeholder 행으로 채운다. 합성 음수 uid라 실제 uid·본인과
            // 충돌하지 않고, 전투가 시작되면(Information.Count>0) 프리뷰가 통째로 버려지므로 무해하다.
            if (partyRoster.Count > 0 || hasPartySource)
            {
                var shownIdentities = new HashSet<(string, int)>(
                    partyRoster.Where(u => !string.IsNullOrWhiteSpace(u.Nickname)).Select(u => (u.Nickname!, u.Server)));
                int placeholderId = -1;
                foreach ((string rNick, int rServer, int _) in services.Data.PartyRosterIdentities(PreCombatPartyTtlMs))
                {
                    if (!string.IsNullOrWhiteSpace(rNick) && shownIdentities.Add((rNick, rServer)))
                    {
                        partyRoster.Add(new User(placeholderId--, rNick, rServer));
                    }
                }
            }

            // 0x9702 로스터는 직업·전투력도 실어 오는데, 프리뷰 행이 그 멤버의 0x3645/0x3633을 아직 못 받았으면
            // 직업 아이콘·전투력이 빈다(실측: 근접 아닌 파티원). 로스터가 가진 job/power로 '빈 값만' 채운다 —
            // display-only, 본인·이미 채워진 행은 건드리지 않고, repo User 오염을 막으려 Copy에 쓴다.
            var rosterJp = new Dictionary<(string, int), (JobClass? Job, int Power)>();
            foreach ((string jpNick, int jpServer, int jpJobCode, int jpPower) in services.Data.PartyRosterJobPower(PreCombatPartyTtlMs))
            {
                if (!string.IsNullOrWhiteSpace(jpNick))
                {
                    rosterJp[(jpNick, jpServer)] = (JobClassInfo.ConvertFromCode(jpJobCode), jpPower);
                }
            }

            if (rosterJp.Count > 0)
            {
                for (int i = 0; i < partyRoster.Count; i++)
                {
                    User u = partyRoster[i];
                    if (u.Id == execUid || string.IsNullOrWhiteSpace(u.Nickname) || (u.Job != null && u.Power > 0))
                    {
                        continue;
                    }

                    if (!rosterJp.TryGetValue((u.Nickname!, u.Server), out (JobClass? Job, int Power) jp))
                    {
                        continue;
                    }

                    User c = u.Copy();
                    if (c.Job == null && jp.Job != null)
                    {
                        c.Job = jp.Job;
                        c.JobSource = JobProvenance.Authoritative;
                    }

                    if (c.Power <= 0 && jp.Power > 0)
                    {
                        c.Power = jp.Power;
                    }

                    partyRoster[i] = c;
                }
            }

            // 0x9702-only; the party-context guard for self-recovery. The RAW snapshot rides along because
            // PartyRoster() above drops every member whose uid this session has never seen — and that dropped
            // member is usually the owner of a nameless row. SAME TTL as the resolved list: pulling the raw one
            // on a longer window would open a band where only the raw roster is "fresh", silently widening the
            // recovery gate.
            viewModel.SetAuthoritativeParty(
                authoritativeParty, services.Data.PartyRosterIdentities(PreCombatPartyTtlMs),
                services.Data.MemberProfileRoster(PreCombatPartyTtlMs));
            viewModel.SetRoster(partyRoster);
            viewModel.SetRosterResurface(true); // Feature 1: 라이브 idle 경로 — 파티(닉/서버) 변경 시 로스터 프리뷰 재노출 허용
            viewModel.Update(report);
            _detailViewModel?.Refresh(report); // live-refresh the open detail window
            StatsOwnCharacter own = services.StatsBuilder.OwnCharacter();
            // Pass the executor's known job (from its User) so the VM can recover 본인 when it re-instances and
            // its new id's own-load packet (0x3633) is missing — see OverlayRowBuilder lost-executor recovery.
            JobClass? ownJob = own.Detected ? services.Data.User(own.Id)?.Job : null;
            viewModel.SetRecognized(own.Detected, own.Nickname, own.Id, own.Server, ownJob, own.Power);
            // Persist the connected character's display name into its consent record (local only) once per
            // recognized character, so the '내 캐릭터 관리' list shows the real name instead of "이름 없음".
            // 🔑 Ask the server what IT knows about this character, once per identity.
            //
            // Keyed on the identity HASH, not the entity uid: a zone load hands the same character a fresh uid,
            // and re-asking on every load would be a GET per loading screen. Deliberately NOT hung off
            // ExecutorChanged either — that only fires on a SWITCH (it needs a previous executor), so the most
            // common session of all, "open the meter and play one character", would never have synced.
            //
            // Why it has to happen at all: until this existed the remote consent sync had exactly one caller,
            // the settings window's 서버 동기화 button. IsUploadAllowed() is per character, so a character with
            // no local record on this install — every character after a reinstall — sat blocked at the first
            // gate for the life of the install, with nothing on screen saying so. Measured on production
            // 2026-08-22: 1,287 characters were consented server-side and had never once uploaded, and at least
            // 268 of them belong to installs still in daily use.
            //
            // Off the UI thread: blocking HTTP, and this lands right at a loading screen.
            if (own.Detected && StatsIdentity.CharacterIdentityHash(own.Server, own.Nickname) is { } ownHash
                && !string.Equals(ownHash, _lastConsentSyncHash, StringComparison.Ordinal))
            {
                _lastConsentSyncHash = ownHash;
                System.Threading.Tasks.Task.Run(() => services.Consent.SyncCurrentCharacter(services.Version));
            }

            if (own.Detected && own.Id != _lastConsentBackfillId)
            {
                _lastConsentBackfillId = own.Id;
                services.Consent.BackfillCurrentCharacterIdentity();
                // Now that we know WHO this is, the badge can fall back to what this character last held —
                // which is the moment the user expects a recognized character to come with its 오드.
                ReseedAetherFromStore(services);
            }

            MaybePromptConsent(services, window);
            FlushPendingWeeklyContent(services);
            FlushPendingAbyssCorridors(services);
            FlushPendingAbyssArtifacts(services);
            TickAbyssCorridor(services);
        });
        _engine.CaptureError += message => Dispatcher.Invoke(() => viewModel.Status = CaptureErrorMessage(message));
        // A reset clears the data-layer party roster; also drop the UI-side recent-combat party tracker so a stale
        // party (e.g. after returning to town) doesn't re-preview on reset. Fires before the cleared report, so
        // there's no one-frame flash of the old party.
        _engine.ResetCompleted += () => Dispatcher.Invoke(() => _partyLastCombatMs.Clear());
        // A character switch (a DIFFERENT character connects) likewise drops the UI-side recent-combat tracker so
        // the previous character doesn't linger as a stale 0/s idle preview row under the new one (the data layer
        // drops its 0x9702 roster snapshot in lockstep). Mirrors the ResetCompleted ordering — queued from the
        // consumer thread before the next idle report, so there's no one-frame flash.
        _engine.ExecutorChanged += () => Dispatcher.Invoke(() =>
        {
            _partyLastCombatMs.Clear();
            // The identity that just arrived is, by construction, the one a held balance dump was waiting for:
            // the dump precedes its naming packet, which is this very event. File it now rather than let it sit
            // until the next report tick.
            FlushPendingAether(services);
            // If the switch DID drop a balance (one too old to be the incoming character's login dump), show
            // what the incoming character last held rather than nothing until the game next speaks.
            ReseedAetherFromStore(services);
            // A different character is connecting, so the one that was in a 어비스 회랑 has certainly left it.
            // Without this its clock keeps burning against a corridor it is no longer standing in, and its row
            // goes on claiming "지금 입장 중" while the user plays someone else.
            StopAbyssCorridorClock(services, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _corridorInsideMapId = 0;
        });

        viewModel.Status = "캡처 헬퍼 시작 중…";
        // Launch + connect entirely off the UI thread. EnsureServing registers/triggers the elevated helper
        // and WAITS for its pipe to actually appear: schtasks /run reports success when the task is merely
        // triggered, so a VPN/booster or AV that silently blocks the (unsigned, elevated) helper would
        // otherwise surface only as a 30s pipe-connect timeout. If the no-prompt task yields no pipe,
        // EnsureServing escalates to a user-approved runas (harder to block) before reporting Blocked.
        Task.Run(() =>
        {
            CaptureHostLaunch launch = CaptureHostLauncher.EnsureServing();
            if (launch is CaptureHostLaunch.Declined or CaptureHostLaunch.NotFound
                or CaptureHostLaunch.Failed or CaptureHostLaunch.Blocked)
            {
                Dispatcher.Invoke(() => viewModel.Status = CaptureLaunchMessage(launch));
                return;
            }

            Dispatcher.Invoke(() => viewModel.Status = "캡처 헬퍼 연결 중…");
            try
            {
                _engine.Start();
                Dispatcher.Invoke(() => viewModel.Status = "캡처 중");
            }
            catch (Exception ex)
            {
                // A helper pipe was already being served before we launched (AlreadyRunning) yet we still
                // can't connect → another waffle_meter is almost certainly occupying the single serve-once
                // helper. Surface that actionable cause instead of a raw connect error (covers an old,
                // pre-single-instance-guard build still running, or a cross-session instance).
                bool occupiedByOther = launch == CaptureHostLaunch.AlreadyRunning
                    && ex.Message.Contains("occupied", StringComparison.OrdinalIgnoreCase);
                string status = occupiedByOther
                    ? "다른 waffle_meter가 이미 실행 중인 것 같아요. 트레이의 기존 창을 쓰거나 종료한 뒤 다시 시작해 주세요."
                    : $"캡처 시작 실패 ({ex.Message})";
                Dispatcher.Invoke(() => viewModel.Status = status);
            }
        });

        // Background auto-update check (no-op for dev / non-Velopack installs) — surfaced via the toast.
        _updateToastVm = new UpdateToastViewModel();
        _updateToast = new UpdateToast { DataContext = _updateToastVm };
        _updateToast.Show();
        _updateToast.Park();
        _controller?.RegisterOverlay(_updateToast);
        _updateToast.CloseRequested += () => _updateToast.Park();

        // One-time post-update patch-note popup: the first launch after updating to a NEW version shows that
        // version's notes once. Deferred to ApplicationIdle so it appears after the overlay has settled; the
        // method records the version (a fresh install / first run with this feature is recorded silently, not
        // shown) and never throws into startup.
        Dispatcher.BeginInvoke(new Action(() => MaybeShowPatchNotes(services.Version)),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // The shipped voice pack backs every built-in alert line; the online voice is only the fallback for
        // custom alarms and anything a newer patch added after this pack was rendered.
        TtsSpeech.SetVoicePack(new BakedVoicePack(AppContext.BaseDirectory, _settings!.TtsVoice));

        // 슈고 페스타 (top-of-hour event) reminder: a transient toast + an app-scoped clock that fires it.
        _alarmToastVm = new AlarmToastViewModel();
        _alarmToast = new AlarmToast { DataContext = _alarmToastVm };
        _alarmToast.Show();
        _alarmToast.Park();
        _controller?.RegisterOverlay(_alarmToast);
        _alarmToast.CloseRequested += () => _alarmToast.Park();
        _alarms = new AlarmController(
            _settings,
            lead => Dispatcher.Invoke(() => ShowShugoAlarm(lead)),
            alarm => Dispatcher.Invoke(() => ShowCustomAlarm(alarm)),
            fieldBossTimers: () => services.Data.CurrentFieldBossTimers,
            onFieldBoss: due => Dispatcher.Invoke(() => ShowFieldBossAlarm(due)),
            combatActive: () => _combatActive,
            onKaira: (lead, spawn) => Dispatcher.Invoke(() => ShowKairaAlarm(lead, spawn)));
        _alarms.Start();

        // Per-job buff picker: seed the observed catalog + hidden selection from persisted settings, and
        // persist the growing catalog back as new buffs are seen.
        services.Data.SeedObservedBuffBases(MeterSettings.ParseCodeSet(_settings.BuffUiObserved));
        HashSet<int> hidden = MeterSettings.ParseCodeSet(_settings.BuffUiHidden);
        if (!_settings.BuffUiDefaultsApplied)
        {
            // First run: hide the catalog's toggle/aura buffs (질주의 진언 / 불패의 진언 등) by default — they
            // stay on indefinitely, so they're noise in the overlay until the user opts them back in.
            foreach (int c in services.Data.DefaultOffBuffBases())
            {
                hidden.Add(c);
            }

            _settings.BuffUiHidden = string.Join(",", hidden);
            _settings.BuffUiDefaultsApplied = true;
        }

        services.Data.SetHiddenBuffBases(hidden);
        services.Data.SetVoiceBuffBases(MeterSettings.ParseCodeSet(_settings.BuffUiVoice)); // 음성만/오버레이+음성 buffs the store must keep
        services.Data.BuffCatalogChanged += () => Dispatcher.BeginInvoke(() =>
            _settings.BuffUiObserved = string.Join(",", services.Data.ObservedBuffBases()));

        // Buff presets: three saved copies of the whole buff config, the active one mirroring the live
        // settings. Built strictly AFTER the default-off merge above — seeded any earlier, slot 1 would
        // capture a hidden set the merge is about to change, and diverge from the running config.
        _buffPresets = new BuffPresetManager(_settings, services.Data.SetHiddenBuffBases, services.Data.SetVoiceBuffBases);

        // Aether badge: restore the last value so it shows immediately (the game only broadcasts the resource
        // on its own schedule — zone load etc. — so without this it's blank for the first minutes), carried
        // forward over the 자연회복 that accrued while the meter was closed. Restore BEFORE wiring the persister;
        // the persister ignores restores anyway (their arrival stamp is 0), but the order keeps that a belt AND
        // braces. The shugo-festa key is deliberately not persisted — nothing accrues it on a timer, so a stale
        // key count would just be wrong, and it waits for a broadcast.
        RestoreAetherFromSettings(services);
        services.Data.AetherStatusChanged += () => Dispatcher.BeginInvoke(() =>
        {
            PersistAether(services);
            if (_aetherPanelVisible)
            {
                RefreshAetherRoster(services); // keep an open 컨텐츠 관리 in step with the live balance
            }
        });

        // Weekly 성역 clears ride the same 0x610x packets: a full snapshot on login/zone-in and a delta within
        // half a second of a final boss dying. Persisted per character exactly like the 오드 balance, and
        // ALWAYS (not only while the panel is open) — the point is to know about characters you aren't looking
        // at. PersistWeeklyContent refreshes the panel itself when the value actually changed.
        services.Data.WeeklyContentChanged += (kind, remaining, atMs, fromSnapshot) =>
            Dispatcher.BeginInvoke(() => OnWeeklyContentBroadcast(services, kind, remaining, atMs, fromSnapshot));

        // 어비스 회랑 이용 시간 rides the very same packets, one currency id per corridor. Unlike the counters
        // beside it this is a CLOCK: the server states it on entry and again at zero, and nothing between, so
        // the instance-map feed below is what tells the meter when to run it and when to stop.
        services.Data.AbyssCorridorChanged += (ticketId, remainingMs, atMs, fromSnapshot) =>
            Dispatcher.BeginInvoke(() => OnAbyssCorridorBroadcast(services, ticketId, remainingMs, atMs, fromSnapshot));
        services.Data.AbyssArtifactsChanged += (zoneId, cycleStartMs, cycleEndMs, holdings, atMs) =>
            Dispatcher.BeginInvoke(() =>
                OnAbyssArtifactsBroadcast(services, zoneId, cycleStartMs, cycleEndMs, holdings, atMs));
        services.Data.AbyssArtifactCountChanged += (zoneId, count, atMs) =>
            Dispatcher.BeginInvoke(() => OnAbyssArtifactCount(services, zoneId, count, atMs));
        services.Data.InstanceMapChanged += (mapId, atMs) =>
            Dispatcher.BeginInvoke(() => OnInstanceMapChanged(services, mapId, atMs));

        // Combat-assist overlay: the local player's active buff slots, refreshed twice a second.
        _buffOverlayVm = new BuffOverlayViewModel();
        _buffOverlay = new BuffOverlayPanel(_buffOverlayVm);
        LoadPanelPosition(services.Props, _buffOverlay, "buffOverlayX", "buffOverlayY");
        _buffOverlayHome = new Point(_buffOverlay.Left, _buffOverlay.Top);
        ClampWhenLoaded(_buffOverlay);
        // 다른 창들은 폭이 고정이라 위치만 지키면 됐지만, 이 창은 버프가 붙을 때마다 넓어진다. 오른쪽 끝에
        // 세워둔 사용자는 시작 시점(슬롯 0개, 폭 ~64px)의 클램프를 통과하고도 첫 전투에서 창이 거의 통째로
        // 화면 밖으로 나가 버린다 — 그러면 몸통이 화면 밖이라 드래그로 되돌릴 수조차 없다.
        _buffOverlay.SizeChanged += (_, _) => ReflowBuffOverlay();
        _buffOverlay.DpiChanged += (_, _) => ReflowBuffOverlay(); // 모니터 배율이 바뀌면 폭 상한도 달라진다
        _buffOverlay.Show();
        _buffOverlay.Park();
        _controller?.RegisterOverlay(_buffOverlay);
        // Present/park the buff overlay in exact lockstep with the meter (gated by the toggle) so it never
        // disappears on its own — when the toggle is on it is always shown whenever the meter is.
        _controller?.SetCompanion(_buffOverlay, () => _settings?.ShowBuffUi == true);
        _buffOverlay.CloseRequested += () => { _settings.ShowBuffUi = false; };
        _buffOverlay.PositionChanged += (left, top) =>
        {
            // 드래그로 새로 정한 자리가 곧 새 "집". 저장은 클램프 전 좌표 그대로 둔다 — 표시 위치는 언제나
            // 집에서 다시 계산되므로, 화면 밖에 놓인 집은 "가능한 한 그 방향 끝"이라는 뜻이 되어 무해하다.
            _buffOverlayHome = new Point(left, top);
            services.Props.SetProperty("buffOverlayX", left.ToString("0", CultureInfo.InvariantCulture));
            services.Props.SetProperty("buffOverlayY", top.ToString("0", CultureInfo.InvariantCulture));
            ReflowBuffOverlay(); // 화면 밖에 떨어뜨렸으면 바로 끌어온다
        };
        MeterServices svc = services;
        _buffTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _buffTimer.Tick += (_, _) => RefreshBuffOverlay(svc);
        _buffTimer.Start();

        // Skill-cooldown overlay: a SECOND window, wired the same way as the buff overlay but with its own
        // toggle and its own home. It is not registered as the controller's companion — that slot is a single
        // field and taking it would silently unwire the buff overlay. Instead it reads CompanionBaseShown (the
        // same meter-presence decision the poll publishes) and reconciles itself on its own tick, which is how
        // the buff overlay's visibility actually works in practice anyway.
        _cooldownOverlayVm = new CooldownOverlayViewModel();
        _cooldownOverlay = new CooldownOverlayPanel(_cooldownOverlayVm);
        LoadPanelPosition(services.Props, _cooldownOverlay, "cooldownOverlayX", "cooldownOverlayY");
        _cooldownOverlayHome = new Point(_cooldownOverlay.Left, _cooldownOverlay.Top);
        ClampWhenLoaded(_cooldownOverlay);
        _cooldownOverlay.SizeChanged += (_, _) => ReflowCooldownOverlay();
        _cooldownOverlay.DpiChanged += (_, _) => ReflowCooldownOverlay();
        _cooldownOverlay.Show();
        _cooldownOverlay.Park();
        _controller?.RegisterOverlay(_cooldownOverlay);
        _cooldownOverlay.CloseRequested += () => { _settings.ShowCooldownUi = false; };
        _cooldownOverlay.PositionChanged += (left, top) =>
        {
            _cooldownOverlayHome = new Point(left, top);
            services.Props.SetProperty("cooldownOverlayX", left.ToString("0", CultureInfo.InvariantCulture));
            services.Props.SetProperty("cooldownOverlayY", top.ToString("0", CultureInfo.InvariantCulture));
            ReflowCooldownOverlay();
        };
        // 250ms: a cooldown is read in seconds, so four steps a second is smooth enough, and the measured cost
        // of a 32-slot repaint at this rate is ~1.7% of a core. A 24fps shared clock would be ~10%.
        _cooldownTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _cooldownTimer.Tick += (_, _) => RefreshCooldownOverlay(svc);
        _cooldownTimer.Start();

        // 표시할 스킬 픽커. 참가요청 배지 픽커와 창(SkillSettingsFlyout)과 행 클래스는 공유하고 카탈로그와
        // 저장 키만 다르다. ⚠️ 선택 집합은 반드시 별도 인스턴스여야 한다 — SkillVisibility/CooldownVisibility
        // 는 집합을 참조로 넘기므로 하나를 공유하면 배지 토글이 쿨타임 표시를 함께 바꾼다.
        _cooldownVisibility = new CooldownVisibility(services.Props, services.Data.CooldownCatalog);
        _cooldownPickerVm = new CooldownPickerViewModel(services.Data.CooldownCatalog, _cooldownVisibility);
        _cooldownFlyout = new CooldownPickerFlyout { DataContext = _cooldownPickerVm };
        LoadWindowSize(services.Props, "cooldownPickerWidth", "cooldownPickerHeight", _cooldownFlyout);
        _cooldownFlyout.Show();
        _cooldownFlyout.Park();
        _controller?.RegisterOverlay(_cooldownFlyout);
        AttachScreenClamp(_cooldownFlyout);
        AttachResize(_cooldownFlyout, services.Props, "cooldownPickerWidth", "cooldownPickerHeight");
        _cooldownFlyout.CloseRequested += () => { _cooldownFlyoutVisible = false; _cooldownFlyout.Park(); };
        // 토글 즉시 반영 — 250ms 를 기다리면 체크가 안 먹은 것처럼 보인다.
        _cooldownPickerVm.Changed += () => RefreshCooldownOverlay(svc);
        // 반대 방향: 설정 가져오기가 집합을 통째로 갈아끼웠을 때 칩을 다시 읽는다.
        _cooldownVisibility.Changed += () => { _cooldownPickerVm.Refresh(); RefreshCooldownOverlay(svc); };
        // 프리셋 매니저는 CooldownVisibility 뒤에 만들어야 한다 — 활성 슬롯을 라이브 선택으로 치유하려면
        // 그 선택이 이미 파일에서 읽혀 있어야 한다.
        _cooldownPresets = new CooldownPresetManager(_settings, _cooldownVisibility);

        _updateService = new UpdateService(prerelease: false);
        UpdateService updateService = _updateService;
        // Free the single-instance guard the instant an update-restart commits, so Velopack's relaunched
        // process acquires the mutex as "first" instead of racing this (exiting) process's handle.
        updateService.BeforeRestart = Program.ReleaseSingleInstance;
        _updateToast.RestartRequested += () => updateService.ApplyAndRestart();
        _updateService.StageChanged += (stage, info, percent) => Dispatcher.Invoke(() =>
        {
            switch (stage)
            {
                case UpdateService.UpdateStage.Downloading: _updateToastVm.SetDownloading(info, percent); break;
                case UpdateService.UpdateStage.Ready: _updateToastVm.SetReady(info); viewModel.SetUpdateReady(info); break;
                case UpdateService.UpdateStage.Failed: _updateToastVm.SetFailed(info); break;
            }

            // No auto-popup: the download runs silently and surfaces as the header "업데이트" badge on the
            // meter (UpdateReadyVisibility). The toast is shown only on demand when the user clicks the badge.
        });

        // User clicks the meter's update badge -> show the restart toast (bottom-right) so they apply when
        // they choose (the toast's 지금 재시작 -> UpdateService.ApplyAndRestart).
        window.UpdateRequested += () =>
        {
            Rect wa = SystemParameters.WorkArea;
            _updateToast.Left = wa.Right - _updateToast.Width - 16;
            _updateToast.Top = wa.Bottom - 130;
            _updateToast.Present(true);
        };
        _ = _updateService.CheckAndDownloadAsync(msg => Dispatcher.Invoke(() => viewModel.Status = msg));
    }

    private static string CaptureLaunchMessage(CaptureHostLaunch launch) => launch switch
    {
        CaptureHostLaunch.Blocked =>
            $"캡처 헬퍼('{CaptureHostLauncher.HostExeName}')가 차단된 것 같습니다. VPN·게임 가속기나 보안 프로그램이 " +
            "헬퍼 실행을 막고 있을 수 있어요. 보안 프로그램 허용 목록에 이 파일을 추가하거나 잠시 끄고 다시 시작해 주세요.",
        CaptureHostLaunch.Declined => "권한 상승(UAC)이 취소되어 캡처를 시작할 수 없습니다. 앱을 다시 시작하면 재시도합니다.",
        CaptureHostLaunch.NotFound => "캡처 헬퍼 파일을 찾을 수 없습니다. 앱을 재설치해 주세요.",
        _ => "캡처 헬퍼 시작에 실패했습니다. 잠시 후 다시 시도해 주세요.",
    };

    // The pipe wait only proves the helper PROCESS started — the WinDivert driver opens later, after the
    // client connects. So a booster/AV that allows the process but blocks the .sys surfaces here (not as
    // a launch failure). Re-route driver-load errors through the same actionable booster guidance.
    private static string CaptureErrorMessage(string raw)
    {
        if (raw.Contains("WinDivert", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("driver", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("드라이버", StringComparison.Ordinal))
        {
            return "캡처 드라이버 로드에 실패했습니다. VPN·게임 가속기나 보안 프로그램이 드라이버를 막고 있을 수 있어요. " +
                   $"허용 목록에 추가하거나 잠시 끄고 다시 시작해 주세요. (원본: {raw})";
        }

        return raw;
    }

    /// <summary>Background waiter for the single-instance "show" signal. A later launch (see
    /// <see cref="Program"/>) opens the named event and Set()s it; we un-hide the overlay from the tray so
    /// the user gets the running instance back instead of a second, colliding one. Best-effort: any handle
    /// error (e.g. on shutdown) just ends the loop.</summary>
    private void StartSingleInstanceListener()
    {
        EventWaitHandle? signal = SingleInstanceShowSignal;
        if (signal is null)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    signal.WaitOne();
                }
                catch
                {
                    break; // handle disposed / abandoned on shutdown
                }

                Dispatcher.Invoke(() => _controller?.ShowFromTray());
            }
        })
        {
            IsBackground = true,
            Name = "single-instance-listener",
        };
        thread.Start();
    }

    // Open the positional replay for the last battle (the 직전 전투). Toggle: a second invocation closes it
    // so the next reopens with the latest recording. Only reachable when replay.recordMovement=true.
    /// <summary>
    /// Dev builds only (tray → "[개발] 패킷 로그 불러오기"). Replays a recorded packet-debug corpus through the
    /// live pipeline so its battles show up in the history/detail windows without running a dungeon.
    /// Replaying wipes the meter's live battle state, so it asks first.
    /// </summary>
    private void LoadPacketLog(WaffleMeter.App.Core.MeterServices services)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "패킷 로그 불러오기 (개발용)",
            Filter = "패킷 디버그 로그 (*.jsonl;*.jsonl.gz)|*.jsonl;*.jsonl.gz|모든 파일 (*.*)|*.*",
            InitialDirectory = Directory.Exists(DevPacketLogReplay.DefaultLogDirectory())
                ? DevPacketLogReplay.DefaultLogDirectory()
                : null,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        MessageBoxResult confirm = MessageBox.Show(
            $"{Path.GetFileName(dialog.FileName)}\n\n" +
            "이 로그를 미터에 재생합니다. 현재 전투 상태는 초기화되고, 재생된 전투는 통계 사이트로 업로드되지 않습니다.\n" +
            "게임이 켜져 있으면 실시간 패킷과 섞일 수 있으니 꺼두는 것이 좋습니다.\n\n계속할까요?",
            "패킷 로그 불러오기 (개발용)",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        string path = dialog.FileName;
        Task.Run(() =>
        {
            try
            {
                int battles = DevPacketLogReplay.Run(services, path);
                Dispatcher.BeginInvoke(() =>
                {
                    services.NotifyBattleListChanged();
                    MessageBox.Show($"전투 {battles}건을 불러왔습니다.\n히스토리에서 열어보세요.",
                        "패킷 로그 불러오기 (개발용)", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => MessageBox.Show(
                    "재생 실패: " + ex.Message, "패킷 로그 불러오기 (개발용)",
                    MessageBoxButton.OK, MessageBoxImage.Error));
            }
        });
    }

    /// <summary>Let the user pick a saved replay .json and play it (the "리플레이 재생" button). Opens the
    /// replays folder by default; reuses the single-instance replay window like every other open path.</summary>
    private void PlayReplayFromPicker(WaffleMeter.App.Core.MeterServices services, Window? owner)
    {
        string dir = services.ReplayDirectory;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
        }
        catch
        {
            // a missing/unwritable folder just means the dialog opens wherever it can
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "리플레이 파일 선택",
            Filter = "리플레이 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
            InitialDirectory = System.IO.Directory.Exists(dir) ? dir : null,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        WaffleMeter.Replay.ReplayRecording rec;
        try
        {
            rec = WaffleMeter.Replay.ReplaySerializer.Deserialize(System.IO.File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"이 파일은 리플레이로 열 수 없어요.\n\n{ex.Message}", "리플레이 재생",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (rec.PointCount == 0)
        {
            MessageBox.Show(owner, "이 리플레이에는 표시할 이동 기록이 없어요.", "리플레이 재생",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShowReplayWindow(rec, owner);
    }

    private void OpenReplay(WaffleMeter.App.Core.MeterServices services, Window owner)
    {
        if (_replayWindow != null)
        {
            _replayWindow.Close();
            return;
        }

        // Prefer the live last battle; after a restart that is empty, so fall back to the newest saved
        // recording on disk (history replay survives restart).
        WaffleMeter.Replay.ReplayRecording? rec = services.Movement?.LastRecording;
        if (rec is null || rec.PointCount == 0)
        {
            rec = TryLoadNewestSavedReplay(services);
        }

        if (rec is null || rec.PointCount == 0)
        {
            return; // no recorded battle with movement yet
        }

        ShowReplayWindow(rec, owner);
    }

    /// <summary>The recording for ONE saved battle: the live engine's copy if this session made it, else the
    /// file it wrote (recordings survive a restart). Null when that battle has no positions — which is what
    /// hides the ▶ on a history row.</summary>
    private static WaffleMeter.Replay.ReplayRecording? FindRecording(
        WaffleMeter.App.Core.MeterServices services, DpsReport report)
    {
        if (report.BattleStart <= 0)
        {
            return null;
        }

        if (services.Movement is { } engine
            && engine.TryGetForBattle(report.BattleStart, out WaffleMeter.Replay.ReplayRecording? live)
            && live is { PointCount: > 0 })
        {
            return live;
        }

        try
        {
            string path = System.IO.Path.Combine(services.ReplayDirectory, $"replay-{report.BattleStart}.json");
            if (!System.IO.File.Exists(path))
            {
                return null;
            }

            WaffleMeter.Replay.ReplayRecording saved =
                WaffleMeter.Replay.ReplaySerializer.Deserialize(System.IO.File.ReadAllText(path));
            return saved.PointCount > 0 ? saved : null;
        }
        catch
        {
            return null; // a corrupt/half-written file just means "no replay for this battle"
        }
    }

    // One replay window at a time: opening another battle's replay replaces the one on screen.
    private void ShowReplayWindow(WaffleMeter.Replay.ReplayRecording rec, Window? owner)
    {
        _replayWindow?.Close();

        var win = new ReplayWindow(rec, _encounters);
        if (owner != null && owner.IsLoaded)
        {
            win.Owner = owner;
        }

        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_replayWindow, win))
            {
                _replayWindow = null;
            }
        };
        _replayWindow = win;
        win.Show();
    }

    private static WaffleMeter.Replay.ReplayRecording? TryLoadNewestSavedReplay(WaffleMeter.App.Core.MeterServices services)
    {
        try
        {
            string dir = System.IO.Path.Combine(services.Props.AppDirectory(), "replays");
            if (!System.IO.Directory.Exists(dir))
            {
                return null;
            }

            System.IO.FileInfo? f = new System.IO.DirectoryInfo(dir)
                .GetFiles("replay-*.json")
                .OrderByDescending(x => x.LastWriteTime)
                .FirstOrDefault();
            return f is null ? null : WaffleMeter.Replay.ReplaySerializer.Deserialize(System.IO.File.ReadAllText(f.FullName));
        }
        catch
        {
            return null;
        }
    }

    private void ExitApp()
    {
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    private static void LoadPosition(PropertyHandler props, Window window)
    {
        string? x = props.GetProperty("uiX") ?? props.GetProperty("windowX");
        string? y = props.GetProperty("uiY") ?? props.GetProperty("windowY");
        if (double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double left) &&
            double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out double top))
        {
            window.Left = left;
            window.Top = top;
        }
    }

    private static void SavePosition(PropertyHandler props, double left, double top)
    {
        props.SetProperty("uiX", left.ToString("0", CultureInfo.InvariantCulture));
        props.SetProperty("uiY", top.ToString("0", CultureInfo.InvariantCulture));
    }

    private void ToggleDetail(int uid, MeterServices services, Window owner, OverlayViewModel meterVm)
    {
        // Resolve the clicked player against the report the OVERLAY IS CURRENTLY SHOWING (the live battle,
        // or a saved battle while replaying from history) — NOT the live _lastReport. A row clicked while
        // a saved battle is on screen carries a uid from that saved battle; resolving it against the live
        // report (a different, possibly unrelated battle) is what produced the "15485 상세내역" raw-uid
        // title, all-zero stats/skills, and the meter-vs-detail combat-time mismatch.
        DpsReport? source = meterVm.CurrentReport ?? _lastReport;
        if (source == null)
        {
            return;
        }

        if (_detailWindow != null && _detailUid == uid)
        {
            _detailWindow.Close(); // re-click same row -> close (toggle)
            return;
        }

        _detailWindow?.Close();

        string name = source.Contributors.FirstOrDefault(c => c.Id == uid)?.Nickname ?? uid.ToString();

        // 티어 줄은 미터가 쓰는 것과 **같은** resolver 로 뽑는다. 상세창이 자체 Evaluate 를 구성하면 인자가
        // 갈라져(기록 재생에서 커리어 티어를 빼는 결정, 시련 어픽스 게이트) 미터와 다른 숫자를 낼 수 있다.
        // 게이트는 마스터 두 개만 본다 — tier.showOthers 는 "미터 행", tier.showSelfChip 은 "미터 아래
        // 전투 시간 옆"을 문구가 명시적으로 지목하므로 상세창에 재사용하면 두 설명이 거짓이 된다.
        Func<DpsReport, TierDetailLine> tierLineOf = rep =>
        {
            if (_settings is not { TierShow: true } s || string.Equals(s.TierEffects, "off", StringComparison.Ordinal))
            {
                return TierDetailLine.None;
            }

            if (meterVm.TierResolver is not { } resolve || !resolve(rep).TryGetValue(uid, out RowTier row))
            {
                return TierDetailLine.None; // 전투력 미확보·미지원 보스 등 — 애초에 모집단 밖이다
            }

            TierBadge badge = TierPalette.For(row.TierRank, _skin?.IsLight ?? false);
            return TierDetail.Build(row, badge.IsNone ? null : badge.Name);
        };

        _detailViewModel = new DetailsViewModel(
            source, uid, services.Calculator, name, _theme!, _settings!.FontFamily, tierLineOf: tierLineOf,
            settings: _settings);
        _detailUid = uid;
        _detailWindow = new DetailWindow { DataContext = _detailViewModel };
        _detailWindow.Closed += (s, _) =>
        {
            if (s is IReassertableOverlay overlay)
            {
                _controller?.UnregisterOverlay(overlay); // recreated per row -> drop the dead-HWND reference
            }

            _detailWindow = null;
            _detailViewModel = null;
            _detailUid = 0;
        };
        LoadWindowSize(services.Props, "detailWidth", "detailHeight", _detailWindow);
        PlaceDetailWindow(owner, _detailWindow); // right of the meter, flipping left if it would clip off-screen
        _detailWindow.Show();
        _controller?.RegisterOverlay(_detailWindow); // poll re-claims its topmost on alt-tab return to the game
        AttachScreenClamp(_detailWindow);
        AttachResize(_detailWindow, services.Props, "detailWidth", "detailHeight");
    }

    /// <summary>Place the detail window beside the meter: to its RIGHT by default, flipped to the LEFT
    /// when the right side would run off the monitor (the reported "opens off-screen" bug when the meter
    /// sits at the right edge). Clamped to the owner's monitor so it's always fully visible.</summary>
    private static void PlaceDetailWindow(Window owner, Window detail)
    {
        const double gap = 8;
        double w = detail.Width, h = detail.Height;

        IntPtr hwnd = new WindowInteropHelper(owner).Handle;
        System.Drawing.Rectangle b = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds; // physical px
        DpiScale dpi = VisualTreeHelper.GetDpi(owner);
        double left = b.Left / dpi.DpiScaleX, right = b.Right / dpi.DpiScaleX;
        double top = b.Top / dpi.DpiScaleY, bottom = b.Bottom / dpi.DpiScaleY;

        double rightPos = owner.Left + owner.ActualWidth + gap;
        double leftPos = owner.Left - w - gap;
        double x = rightPos + w <= right ? rightPos          // fits on the right
                 : leftPos >= left ? leftPos                 // else flip to the left
                 : Math.Max(left, right - w);                // neither side fits: clamp inside
        detail.Left = x;
        detail.Top = Math.Min(owner.Top, Math.Max(top, bottom - h));
    }

    private SkillVisibility? _skillVisibility;

    private void WireJoinPanel(MeterServices services, OverlayWindow overlay)
    {
        // 필드로 두는 이유: 설정 임포트가 이 인스턴스를 Reload 해야 하고, JoinRequestViewModel 과
        // SkillSettingsViewModel 이 같은 HashSet 을 참조로 들고 있다.
        _skillVisibility = new SkillVisibility(services.Props);
        _joinViewModel = new JoinRequestViewModel(
            _settings!, _skillVisibility.Codes, services.Tier);
        _joinPanel = new JoinRequestPanel { DataContext = _joinViewModel };
        MigrateJoinPanelWidthForTierChip(services.Props);
        LoadWindowSize(services.Props, "joinPanelWidth", "joinPanelHeight", _joinPanel);

        // Build the HWND + assert the overlay ex-style, then park (hidden) until a request arrives.
        _joinPanel.Show();
        _joinPanel.Park();
        _controller?.RegisterOverlay(_joinPanel); // poll re-claims its topmost on alt-tab return to the game
        AttachScreenClamp(_joinPanel);
        AttachResize(_joinPanel, services.Props, "joinPanelWidth", "joinPanelHeight");

        // Restore a persisted position; otherwise dock under the meter overlay on first present.
        if (LoadPanelPosition(services.Props, _joinPanel, "joinPanelX", "joinPanelY"))
        {
            _joinPanelPositioned = true;
        }

        ClampWhenLoaded(_joinPanel); // a persisted off-screen panel position should restore reachable

        _joinPanel.PositionChanged += (left, top) =>
        {
            _joinPanelPositioned = true;
            services.Props.SetProperty("joinPanelX", left.ToString("0", CultureInfo.InvariantCulture));
            services.Props.SetProperty("joinPanelY", top.ToString("0", CultureInfo.InvariantCulture));
        };
        _joinPanel.CloseRequested += () =>
        {
            // Explicit close (✕): remember the requests showing now and stay closed until a genuinely NEW
            // requester applies. Clearing the VM resets its count to 0 so an enrichment re-Add of the SAME
            // request (which lands ~hundreds of ms later) can no longer re-fire the empty->non-empty auto-show
            // that made close look like it "didn't work".
            _joinDismissedIds.Clear();
            foreach (var s in services.JoinRequests.Snapshot())
            {
                _joinDismissedIds.Add(s.Requester);
            }

            _joinUserDismissed = true;
            _joinViewModel.Clear();
            _joinPanel.Park();
        };

        void PresentJoinPanel()
        {
            if (!_joinPanelPositioned)
            {
                _joinPanel.Left = overlay.Left;
                _joinPanel.Top = overlay.Top + overlay.ActualHeight + 8;
            }

            _joinPanel.Present(true);
        }

        // Auto-open on the empty -> non-empty transition (web isOpen behavior), unless the user turned off
        // auto-show (the header 파티 신청 button still opens it manually).
        _joinViewModel.RequestPresent += () =>
        {
            if (_settings!.ShowJoinPanel && !_joinUserDismissed)
            {
                PresentJoinPanel();
            }
        };

        // 계정/파티 신청 header button: toggle the panel manually (Opacity tracks park/present).
        overlay.JoinRequested += () =>
        {
            if (_joinPanel.Opacity > 0)
            {
                _joinPanel.Park();
            }
            else
            {
                _joinUserDismissed = false; // manual open overrides a prior dismissal
                _joinViewModel.Reconcile(services.JoinRequests.Snapshot()); // re-show currently-live requests
                PresentJoinPanel();
            }
        };

        // 짝이 될 0x970B 가 끝내 오지 않은 0x9709(= 진짜 거절)를 여기서 해소한다. 저장소에는 시계가 없고,
        // 이 하트비트는 카드가 화면에 있을 수 있는 동안에는 언제나 돌고 있다. 실제로 지워지면 저장소가
        // Changed 를 올려 아래 핸들러가 다시 그린다.
        _joinPanel.Heartbeat += () => services.JoinRequests.FlushResolved();

        // Store events fire on the meter-consumer thread; marshal to the UI.
        services.JoinRequests.Changed += () => Dispatcher.Invoke(() =>
        {
            var snapshot = services.JoinRequests.Snapshot();
            if (_joinUserDismissed)
            {
                foreach (var s in snapshot)
                {
                    if (!_joinDismissedIds.Contains(s.Requester))
                    {
                        _joinUserDismissed = false; // a brand-new requester re-arms auto-show (option a)
                        break;
                    }
                }
            }

            _joinViewModel.Reconcile(snapshot);
        });
        services.JoinRequests.Cleared += () => Dispatcher.Invoke(() =>
        {
            _joinUserDismissed = false; // party exit / instance start resets the dismissal
            _joinDismissedIds.Clear();
            _joinViewModel.Clear();
            _joinPanel.Park();
        });

        // Skill-settings flyout (visibleSkillCodes filter). The ⚙ button toggles it; changes re-render badges.
        var skillVm = new SkillSettingsViewModel(_skillVisibility);
        _skillFlyout = new SkillSettingsFlyout { DataContext = skillVm };
        LoadWindowSize(services.Props, "skillFlyoutWidth", "skillFlyoutHeight", _skillFlyout);
        _skillFlyout.Show();
        _skillFlyout.Park();
        _controller?.RegisterOverlay(_skillFlyout);
        AttachScreenClamp(_skillFlyout);
        AttachResize(_skillFlyout, services.Props, "skillFlyoutWidth", "skillFlyoutHeight");
        _skillFlyout.CloseRequested += () => { _skillFlyoutVisible = false; _skillFlyout.Park(); };
        skillVm.Changed += () =>
        {
            _joinViewModel.SetVisibleCodes(_skillVisibility.Codes);
            _joinViewModel.Reconcile(services.JoinRequests.Snapshot()); // rebuild rows so badges honor the new set
        };

        // The other direction: a settings import replaces the set wholesale (SettingsBundleApplier -> Reload).
        // Without this the imported list only took effect after a restart — the chips still drew the old state,
        // and touching one wrote that stale state straight back over what was just imported.
        _skillVisibility.Changed += () =>
        {
            skillVm.Refresh();
            _joinViewModel.SetVisibleCodes(_skillVisibility.Codes);
            _joinViewModel.Reconcile(services.JoinRequests.Snapshot());
        };
        _joinPanel.SettingsRequested += () =>
        {
            if (_skillFlyoutVisible)
            {
                _skillFlyoutVisible = false;
                _skillFlyout.Park();
                return;
            }

            _skillFlyout.Left = _joinPanel.Left + _joinPanel.Width + 8;
            _skillFlyout.Top = _joinPanel.Top;
            _skillFlyoutVisible = true;
            _skillFlyout.Present(true);
        };
    }

    private void WireHistoryPanel(MeterServices services, OverlayWindow overlay, OverlayViewModel meterViewModel)
    {
        _historyViewModel = new BattleHistoryViewModel(_theme!, _settings!, services.Data.Encounters);
        _historyPanel = new HistoryPanel { DataContext = _historyViewModel };
        LoadWindowSize(services.Props, "historyPanelWidth", "historyPanelHeight", _historyPanel);
        _historyPanel.Show();
        _historyPanel.Park();
        _controller?.RegisterOverlay(_historyPanel);
        AttachScreenClamp(_historyPanel);
        AttachResize(_historyPanel, services.Props, "historyPanelWidth", "historyPanelHeight");

        if (LoadPanelPosition(services.Props, _historyPanel, "historyPanelX", "historyPanelY"))
        {
            _historyPanelPositioned = true;
        }

        ClampWhenLoaded(_historyPanel); // a persisted off-screen panel position should restore reachable

        _historyPanel.PositionChanged += (left, top) =>
        {
            _historyPanelPositioned = true;
            services.Props.SetProperty("historyPanelX", left.ToString("0", CultureInfo.InvariantCulture));
            services.Props.SetProperty("historyPanelY", top.ToString("0", CultureInfo.InvariantCulture));
        };
        _historyPanel.CloseRequested += () =>
        {
            _historyPanelVisible = false;
            _historyPanel.Park();
        };

        // Saved-battle snapshots arrive on the consumer thread; cache them on the UI thread. BeginInvoke
        // (not Invoke) so the consumer never blocks on the UI thread — during app shutdown the UI thread is
        // itself joining the consumer, and a synchronous Invoke there would mutually deadlock (and stall the
        // shutdown save). A history-panel refresh is not latency-critical; if the dispatcher is already
        // shutting down the post simply doesn't run.
        services.BattleListChanged += battles => Dispatcher.BeginInvoke(() => _historyViewModel.SetBattles(battles));

        // Clicking a saved battle replays it in the meter until the next live battle starts.
        _historyViewModel.BattleSelected += report =>
        {
            _viewingHistory = true;
            _historyBaselineBattleStart = _lastReport?.BattleStart ?? 0;
            meterViewModel.SetRosterResurface(false); // Feature 1: 기록 재생은 라이브 로스터 불일치로 절대 비우지 않는다
            meterViewModel.Update(report);
        };

        // ▶ on a row: the positional replay for THAT battle (the tray entry only opens the last one). The
        // button is only rendered for battles that actually have a recording.
        _historyViewModel.HasReplay = report => FindRecording(services, report) is not null;
        _historyViewModel.ReplayRequested += report =>
        {
            if (FindRecording(services, report) is { } rec)
            {
                ShowReplayWindow(rec, _historyPanel);
            }
        };

        // The 기록 header button toggles the panel.
        overlay.HistoryRequested += () =>
        {
            if (_historyPanelVisible)
            {
                _historyPanelVisible = false;
                _historyPanel.Park();
                return;
            }

            if (!_historyPanelPositioned)
            {
                _historyPanel.Left = overlay.Left + overlay.ActualWidth + 8;
                _historyPanel.Top = overlay.Top;
            }

            _historyPanelVisible = true;
            _historyPanel.Present(true);
        };
    }

    /// <summary>The 오드 목록 panel: every character this install has seen and the 오드 it last held. Opened
    /// from the meter's footer 오드 badge. Rows are rebuilt on open (and while it is on screen) because the
    /// packet only ever carries the ACTIVE character's balance — the rest of the list is remembered state.</summary>
    private void WireAetherPanel(MeterServices services, OverlayWindow overlay)
    {
        _aetherViewModel = new AetherPanelViewModel(_settings!);
        _aetherPanel = new AetherPanel { DataContext = _aetherViewModel };
        // Capture the shipped size BEFORE any saved one is applied, so "위치 초기화" can put it back. The panel
        // grew when the weekly-content chips arrived, and a user who had ever dragged its edge keeps the old,
        // narrower width forever — with nothing in the UI able to undo it.
        _aetherPanelDefaultSize = (_aetherPanel.Width, _aetherPanel.Height);
        LoadWindowSize(services.Props, "aetherPanelWidth", "aetherPanelHeight", _aetherPanel);
        _aetherPanel.Show();
        _aetherPanel.Park();
        _controller?.RegisterOverlay(_aetherPanel);
        AttachScreenClamp(_aetherPanel);
        AttachResize(_aetherPanel, services.Props, "aetherPanelWidth", "aetherPanelHeight");

        if (LoadPanelPosition(services.Props, _aetherPanel, "aetherPanelX", "aetherPanelY"))
        {
            _aetherPanelPositioned = true;
        }

        ClampWhenLoaded(_aetherPanel);

        _aetherPanel.PositionChanged += (left, top) =>
        {
            _aetherPanelPositioned = true;
            services.Props.SetProperty("aetherPanelX", left.ToString("0", CultureInfo.InvariantCulture));
            services.Props.SetProperty("aetherPanelY", top.ToString("0", CultureInfo.InvariantCulture));
        };
        _aetherPanel.CloseRequested += () =>
        {
            _aetherPanelVisible = false;
            _aetherPanel.Park();
        };

        // ✕ on a row forgets that character. The store's key is a hash of (server, nickname), so a rename
        // leaves the old character behind as a row that can never update — this is the only way to clear it.
        // Removing the character that's currently logged in is allowed; its next broadcast simply re-adds it.
        _aetherViewModel.RemoveRequested += hash =>
        {
            AetherPerCharacterStore store = AetherPerCharacterStore.Parse(
                _settings!.AetherPerCharacter, _settings.AetherCharacterNames);
            if (store.RemoveAll([hash]))
            {
                _settings.AetherPerCharacter = store.Serialize();
                _settings.AetherCharacterNames = store.SerializeNames();
            }

            // The row is gone from the list, so its weekly-clear records must go too — otherwise a re-detected
            // character would inherit the clears of the one the user just forgot.
            WeeklyContentStore weekly = WeeklyContentStore.Parse(_settings.WeeklyContentClears);
            if (weekly.RemoveAll([hash]))
            {
                _settings.WeeklyContentClears = weekly.Serialize();
            }

            // Same reasoning for the corridor clocks — a forgotten character must leave nothing behind in any
            // store, or a re-detected one inherits records it never earned.
            AbyssCorridorStore corridors = AbyssCorridorStore.Parse(_settings.AbyssCorridors);
            if (corridors.RemoveAll([hash]))
            {
                _settings.AbyssCorridors = corridors.Serialize();
            }

            // The 점령 개수 reading is per character too, and it is what picks which side of the broadcast is
            // ours — leaving one behind would let a forgotten character keep deciding that. The server-wide
            // ownership rows stay: they describe the abyss, not this character.
            AbyssArtifactStore artifacts = AbyssArtifactStore.Parse(_settings.AbyssArtifacts);
            if (artifacts.RemoveAll([hash]))
            {
                _settings.AbyssArtifacts = artifacts.Serialize();
            }

            RefreshAetherRoster(services);
        };

        // Clicking a weekly chip flips it. The counter is normally the server's, but the meter only hears the
        // broadcast while it is running — a raid cleared with the meter closed reads as un-cleared until that
        // character next logs in, and this is the way out. Stamped with 'now' so it expires at the same weekly
        // reset a real observation would.
        _aetherViewModel.WeeklyToggleRequested += (hash, slug) =>
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            WeeklyContentStore weekly = WeeklyContentStore.Parse(_settings!.WeeklyContentClears);
            int shown = weekly.Remaining(hash, slug, nowMs) ?? WeeklyContentCatalog.WeeklyGrant;
            if (weekly.Upsert(hash, slug, shown > 0 ? 0 : WeeklyContentCatalog.WeeklyGrant, nowMs))
            {
                _settings.WeeklyContentClears = weekly.Serialize();
            }

            RefreshAetherRoster(services);
        };

        overlay.AetherListRequested += () =>
        {
            if (_aetherPanelVisible)
            {
                _aetherPanelVisible = false;
                _aetherPanel.Park();
                return;
            }

            if (!_aetherPanelPositioned)
            {
                // Offset from the history panel's dock spot: both are topmost, so identical defaults would
                // stack this exactly on top of an open 전투 기록 and read as that panel having changed.
                _aetherPanel.Left = overlay.Left + overlay.ActualWidth + 8;
                _aetherPanel.Top = overlay.Top + 40;
            }

            RefreshAetherRoster(services);
            _aetherPanelVisible = true;
            _aetherPanel.Present(true);
        };
    }

    /// <summary>Rebuild the 컨텐츠 관리 rows from the persisted stores. Cheap (a few dozen records parsed from two
    /// settings strings), so it simply re-reads instead of maintaining an incremental cache.</summary>
    private void RefreshAetherRoster(MeterServices services)
    {
        if (_aetherViewModel is null)
        {
            return;
        }

        _aetherViewModel.SetRows(BuildAetherRows(services));
    }

    /// <summary>The 컨텐츠 관리 rows as the persisted stores currently describe them.</summary>
    private IReadOnlyList<AetherRosterRow> BuildAetherRows(MeterServices services)
    {
        var names = services.Consent.ListCharacters()
            .Select(c => new AetherRosterName(c.IdentityHash, c.Nickname, c.Server, c.Job))
            .ToList();

        return AetherRoster.Build(
            AetherPerCharacterStore.Parse(_settings!.AetherPerCharacter, _settings.AetherCharacterNames),
            names,
            services.Consent.CurrentCharacterHash(),
            WeeklyContentStore.Parse(_settings.WeeklyContentClears),
            nowMs: 0,
            corridors: AbyssCorridorStore.Parse(_settings.AbyssCorridors),
            artifacts: AbyssArtifactStore.Parse(_settings.AbyssArtifacts));
    }

    /// <summary>Persist one weekly 성역 counter under the character that broadcast it. Runs on the UI thread
    /// (marshalled from the packet consumer) because it writes settings, which the panel then re-reads.
    /// <para>Keyed by the same stats identity hash as the 오드 record — and dropped when that hash isn't known
    /// yet, because a counter filed under the wrong character is worse than a missing one.</para></summary>
    /// <summary>
    /// A weekly counter arrived. Deciding WHOSE it is, is the whole job here.
    /// <para>The 0x610B dump that lands on login/zone-in beats the own-load packet naming the character by
    /// about four seconds — measured on 14 of 14 zone-ins in the capture corpus, with no counter-example. So
    /// "file it under whoever the executor is right now" is wrong precisely when it matters: on a character
    /// switch it writes the INCOMING character's counters onto the OUTGOING character's record, and a weekly
    /// clear is a week-long claim, not a number that refreshes on its own. A dump is therefore held until an
    /// identity has been established at or after it arrived — that identity is, by construction, the one the
    /// dump describes.</para>
    /// <para>A 0x610C delta needs none of that: it only fires when a counter actually changes, which means the
    /// character has been in the zone fighting, so the identity settled long ago. Holding it would just delay
    /// the 1/1 → 0/1 the user is watching for.</para>
    /// </summary>
    private void OnWeeklyContentBroadcast(
        MeterServices services, WeeklyContentKind kind, int remaining, long atMs, bool fromSnapshot)
    {
        if (fromSnapshot)
        {
            _weeklyContentPending[kind] = (remaining, atMs);
            FlushPendingWeeklyContent(services);
            return;
        }

        PersistWeeklyContent(services, kind, remaining);
    }

    /// <summary>Write one counter under the character currently identified. Callers must already have decided
    /// that the counter belongs to that character.</summary>
    private void PersistWeeklyContent(MeterServices services, WeeklyContentKind kind, int remaining)
    {
        string? hash = services.Consent.CurrentCharacterHash();
        if (_settings is null || string.IsNullOrEmpty(hash))
        {
            return;
        }

        WeeklyContentStore store = WeeklyContentStore.Parse(_settings.WeeklyContentClears);
        if (store.Upsert(
                hash,
                WeeklyContentCatalog.ByKind(kind).Slug,
                remaining,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
        {
            _settings.WeeklyContentClears = store.Serialize();
            RefreshAetherRoster(services);
        }
    }

    /// <summary>File any held dump values whose owning identity has since been established. Called both when a
    /// dump arrives (the identity is usually already known — a zone-in on the same character) and from the
    /// report loop (which is what catches the login/switch case, where the naming packet is still in flight).
    /// A value is dropped from the pending set as soon as it is filed, so a later switch can never re-file the
    /// previous character's numbers onto the new one.</summary>
    private void FlushPendingWeeklyContent(MeterServices services)
    {
        if (_weeklyContentPending.Count == 0 || string.IsNullOrEmpty(services.Consent.CurrentCharacterHash()))
        {
            return;
        }

        long identityAtMs = services.Data.ExecutorIdentityAtMs;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (WeeklyContentKind kind in _weeklyContentPending.Keys.ToList())
        {
            (int remaining, long atMs) = _weeklyContentPending[kind];
            if (!WeeklyContentOwnership.CanFile(atMs, identityAtMs, nowMs))
            {
                continue; // still waiting for the identity this dump belongs to
            }

            _weeklyContentPending.Remove(kind);
            PersistWeeklyContent(services, kind, remaining);
        }
    }

    /// <summary>
    /// A 어비스 회랑 이용 시간 arrived. Two things have to be decided: whose it is, and whether the clock runs.
    /// <para><b>Whose.</b> Identical to the weekly counters — a 0x610B dump names no character and lands about
    /// four seconds before the packet that does, so it waits (measured 4.0~4.6 s on 5 of 5 login snapshots; the
    /// one apparent "corridor recharged itself overnight" in the corpus turned out to be two characters).</para>
    /// <para><b>Running.</b> A 0x610C delta with time on it means one of two things — the character walked into
    /// the corridor, or 점령전 just handed out a fresh allocation. Only the first should start a countdown, and
    /// the instance-map packet is what tells them apart, so the value is banked immediately and the clock waits
    /// for <see cref="OnInstanceMapChanged"/> to confirm.</para>
    /// </summary>
    private void OnAbyssCorridorBroadcast(
        MeterServices services, int ticketId, long remainingMs, long atMs, bool fromSnapshot)
    {
        if (fromSnapshot)
        {
            _abyssCorridorPending[ticketId] = (remainingMs, atMs);
            FlushPendingAbyssCorridors(services);
            return;
        }

        // A delta only fires when a corridor's clock actually moved, which means the character has been in the
        // world long enough for its identity to have settled. Unlike a dump, a delta zero is trustworthy: the
        // server only sends it when the budget actually ran out, so it is filed as-is.
        //
        // Neither reading says anything about whether the side HOLDS the corridor — that question is settled by
        // OnInstanceMapChanged, and only there. A positive value here is time this character has banked, which
        // may well have been granted at a 점령전 two occupations ago and never spent.
        //
        // The clock is NOT started here even when the value is full: an allocation handed out at 점령전 looks
        // identical on the wire to walking in, and only the map can tell them apart. It is stopped here on a
        // zero, though — that IS the corridor running out, and it arrives ~13.6 s before the map change that
        // ends the visit.
        bool insideThisCorridor = AbyssCorridorCatalog.ByMapId(_corridorInsideMapId)?.TicketId == ticketId;
        PersistAbyssCorridor(
            services, ticketId, remainingMs, atMs,
            markGranted: remainingMs > 0,
            tickingSinceMs: remainingMs <= 0 ? 0 : insideThisCorridor ? atMs : null);

        if (insideThisCorridor)
        {
            _corridorClockHash = remainingMs <= 0 ? null : services.Consent.CurrentCharacterHash();
        }
    }

    /// <summary>
    /// 어비스 아티팩트 점령 현황 arrived (0xE305 / 0xE307). This is what decides which 회랑 the panel shows at
    /// all — see <see cref="AbyssArtifactStore"/>.
    /// <para>It is filed against a SERVER, and the frame names none: the login broadcast beats the packet that
    /// identifies the character by about eight seconds, so it is held until an identity has been established at
    /// or after it arrived — by construction the character the broadcast was sent to. Same rule, same helper as
    /// the weekly counters and the corridor dump.</para>
    /// </summary>
    private void OnAbyssArtifactsBroadcast(
        MeterServices services,
        int zoneId,
        long cycleStartMs,
        long cycleEndMs,
        IReadOnlyList<AbyssArtifactHolding> holdings,
        long atMs)
    {
        _abyssArtifactPending[zoneId] = (cycleStartMs, cycleEndMs, holdings, atMs);
        FlushPendingAbyssArtifacts(services);
    }

    /// <summary>File any 점령 현황 whose server has since become knowable. Called on arrival (usually enough —
    /// a world-map open happens long after login) and from the report loop, which is what catches the login
    /// case.</summary>
    private void FlushPendingAbyssArtifacts(MeterServices services)
    {
        if (_abyssArtifactPending.Count == 0 || _settings is null)
        {
            return;
        }

        int server = services.Data.User(services.Data.ExecutorId())?.Server ?? 0;
        if (server <= 0)
        {
            return;
        }

        long identityAtMs = services.Data.ExecutorIdentityAtMs;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool changed = false;

        AbyssArtifactStore store = AbyssArtifactStore.Parse(_settings.AbyssArtifacts);
        foreach (int zoneId in _abyssArtifactPending.Keys.ToList())
        {
            (long cycleStartMs, long cycleEndMs, IReadOnlyList<AbyssArtifactHolding> holdings, long atMs) =
                _abyssArtifactPending[zoneId];
            if (!WeeklyContentOwnership.CanFile(atMs, identityAtMs, nowMs))
            {
                continue; // still waiting for the identity whose server this describes
            }

            _abyssArtifactPending.Remove(zoneId);
            changed |= store.UpsertOwnership(server, zoneId, cycleStartMs, cycleEndMs, holdings, atMs);
        }

        if (changed)
        {
            _settings.AbyssArtifacts = store.Serialize();
            RefreshAetherRoster(services);
        }
    }

    /// <summary>The 아티팩트 점령 개수 abnormal, already gated to the own character by the parser. It is the only
    /// thing that says which of the broadcast's two slots is ours, so it is filed under the character that wore
    /// it — the roster then hands the answer to that character's server siblings.</summary>
    private void OnAbyssArtifactCount(MeterServices services, int zoneId, int count, long atMs)
    {
        string? hash = services.Consent.CurrentCharacterHash();
        if (_settings is null || string.IsNullOrEmpty(hash))
        {
            return;
        }

        AbyssArtifactStore store = AbyssArtifactStore.Parse(_settings.AbyssArtifacts);
        if (store.UpsertCount(hash, zoneId, count, atMs))
        {
            _settings.AbyssArtifacts = store.Serialize();
            RefreshAetherRoster(services);
        }
    }

    /// <summary>The character loaded into a map. Entering a corridor confirms a pending ticket and starts its
    /// clock; loading anywhere else stops whatever was running, which is the ONLY moment an early exit can be
    /// turned into a number — the server broadcasts nothing more until the budget is gone.</summary>
    private void OnInstanceMapChanged(MeterServices services, int mapId, long atMs)
    {
        if (mapId == _corridorInsideMapId)
        {
            return; // a re-send of the map we are already standing in: nothing began and nothing ended
        }

        // Any real zone change ends whatever corridor was running — including walking straight from one
        // corridor into the next, where there is no outdoor map in between and the first clock would
        // otherwise keep draining a corridor the character has already left.
        StopAbyssCorridorClock(services, atMs);
        _corridorInsideMapId = 0;

        if (AbyssCorridorCatalog.ByMapId(mapId) is not { } corridor)
        {
            return;
        }

        _corridorInsideMapId = mapId;
        EnterAbyssCorridor(services, corridor.TicketId, atMs);
    }

    /// <summary>The character just loaded a corridor's instance map. Two things are written: the proof that its
    /// side holds that artifact, and the clock for this visit.
    ///
    /// <para><b>The entry IS the evidence.</b> The game only opens the portal while the side holds the artifact,
    /// so getting in is the one observation that settles the question. The ticket cannot: 이용 시간 is a stock
    /// the character keeps, and time granted at one 점령전 and never spent is still reported after the artifact
    /// changes hands — 2026-08-23, 콘팡 was told 유황나무 held time three and three-quarter hours after the
    /// 점령전 that lost it, portal already closed. Everything the panel shows now hangs off this stamp.</para>
    ///
    /// <para><b>The clock starts from whatever is banked</b> rather than from a broadcast. The server states the
    /// budget only when it changes, so walking back into a corridor that still has time on it produces NO ticket
    /// packet at all — a clock that waited for one would leave that visit's time frozen on screen while it
    /// silently drained. With nothing banked from this cycle the full grant is assumed, which getting in has
    /// just proved was there.</para></summary>
    private void EnterAbyssCorridor(MeterServices services, int ticketId, long atMs)
    {
        string? hash = services.Consent.CurrentCharacterHash();
        if (_settings is null || string.IsNullOrEmpty(hash))
        {
            return;
        }

        AbyssCorridorStore store = AbyssCorridorStore.Parse(_settings.AbyssCorridors);
        bool changed = store.MarkEntered(hash, ticketId, atMs);

        // A reading from a PREVIOUS cycle is not a starting point — it describes an allocation that has since
        // been re-granted, spent or lost. Reading() answers null for one, and the full grant takes over.
        long remaining = store.Reading(hash, ticketId, atMs) is { } banked and > 0
            ? banked
            : AbyssCorridorCatalog.FullGrantMs;

        changed |= store.Upsert(hash, ticketId, remaining, atMs, markGranted: false, tickingSinceMs: atMs);
        _corridorClockHash = hash;

        if (changed)
        {
            _settings.AbyssCorridors = store.Serialize();
            RefreshAetherRoster(services);
        }
    }

    /// <summary>Write one corridor reading under the character currently identified.</summary>
    private void PersistAbyssCorridor(
        MeterServices services, int ticketId, long remainingMs, long atMs, bool markGranted, long? tickingSinceMs)
    {
        string? hash = services.Consent.CurrentCharacterHash();
        if (_settings is null || string.IsNullOrEmpty(hash))
        {
            return;
        }

        AbyssCorridorStore store = AbyssCorridorStore.Parse(_settings.AbyssCorridors);
        if (store.Upsert(hash, ticketId, remainingMs, atMs, markGranted, tickingSinceMs))
        {
            _settings.AbyssCorridors = store.Serialize();
            RefreshAetherRoster(services);
        }
    }

    /// <summary>Freeze the running corridor clock at <paramref name="atMs"/>, for the character that started
    /// it — see <see cref="_corridorClockHash"/> for why that is not the same as whoever is current.</summary>
    private void StopAbyssCorridorClock(MeterServices services, long atMs)
    {
        if (_settings is null || _corridorClockHash is not { Length: > 0 } hash)
        {
            return;
        }

        _corridorClockHash = null;
        AbyssCorridorStore store = AbyssCorridorStore.Parse(_settings.AbyssCorridors);
        if (store.StopTicking(hash, atMs))
        {
            _settings.AbyssCorridors = store.Serialize();
            RefreshAetherRoster(services);
        }
    }

    /// <summary>File any held dump values whose owning identity has since been established — the corridor twin
    /// of <see cref="FlushPendingWeeklyContent"/>.
    /// <para><b>A zero from a dump is never filed.</b> It is the most overloaded value on this wire — 미점령,
    /// 소진, 타종족 and 미방문 all send it — and the fourth of those is not rare: a character standing in town
    /// reports zero on ALL twelve corridors whatever it holds, because the ticket only materialises once it goes
    /// to the abyss (measured 2026-08-21 on 헤로롱, whose twelve town zeros became real values on zone-in). So a
    /// dump zero filed over a real reading would turn a corridor with 1:10 left into 0:00 for the rest of the
    /// cycle, every time the player logged in from anywhere else. A corridor only reaches zero here the two ways
    /// that mean it: the 0x610C expiry the server sends when the budget runs out, and the clock this app runs
    /// while the character is standing in it.</para></summary>
    private void FlushPendingAbyssCorridors(MeterServices services)
    {
        string? hash = services.Consent.CurrentCharacterHash();
        if (_abyssCorridorPending.Count == 0 || _settings is null || string.IsNullOrEmpty(hash))
        {
            return;
        }

        long identityAtMs = services.Data.ExecutorIdentityAtMs;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        AbyssCorridorStore store = AbyssCorridorStore.Parse(_settings.AbyssCorridors);
        bool changed = false;
        long witnessAtMs = 0;

        foreach (int ticketId in _abyssCorridorPending.Keys.ToList())
        {
            (long remainingMs, long atMs) = _abyssCorridorPending[ticketId];
            if (!WeeklyContentOwnership.CanFile(atMs, identityAtMs, nowMs))
            {
                continue; // still waiting for the identity this dump belongs to
            }

            _abyssCorridorPending.Remove(ticketId);
            witnessAtMs = Math.Max(witnessAtMs, atMs);

            if (remainingMs > 0)
            {
                changed |= store.Upsert(hash, ticketId, remainingMs, atMs, markGranted: true);
            }
        }

        // The dump lists every corridor, so having seen one is having seen them all — and that is what lets the
        // panel say "어비스 회랑 기록 없음" instead of staying silent about a character it simply has not watched.
        if (witnessAtMs > 0)
        {
            changed |= store.MarkWitness(hash, witnessAtMs);
        }

        if (changed)
        {
            _settings.AbyssCorridors = store.Serialize();
            RefreshAetherRoster(services);
        }
    }

    /// <summary>Report-loop upkeep: move an open panel's corridor clocks. The displayed time is a projection,
    /// so only a redraw advances it.
    /// <para>Updates the existing chips in place instead of rebuilding the list. Rebuilding once a second for
    /// the whole 130 seconds of a visit would reset the scroll position, drop hover state and cancel any tooltip
    /// the user was reading — for a readout that only changes one digit.</para></summary>
    private void TickAbyssCorridor(MeterServices services)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!_aetherPanelVisible
            || _aetherViewModel is null
            || _corridorInsideMapId == 0
            || nowMs - _corridorRefreshedAtMs < 1_000)
        {
            return;
        }

        _corridorRefreshedAtMs = nowMs;
        _aetherViewModel.UpdateCorridorTimes(BuildAetherRows(services));
    }

    /// <summary>Show the stats-consent modal once per detected character that has no decision yet
    /// (React StatsConsentModal). Runs on the UI thread from the report loop; remembers prompted hashes
    /// so it never re-pops in the same session.</summary>
    private void MaybePromptConsent(MeterServices services, Window owner)
    {
        if (_consentDialogOpen || !services.Consent.NeedsConsentPrompt())
        {
            return;
        }

        string? hash = services.Consent.CurrentCharacterHash();
        if (hash == null || !_consentPrompted.Add(hash))
        {
            return;
        }

        StatsOwnCharacter own = services.StatsBuilder.OwnCharacter();
        string label = !string.IsNullOrEmpty(own.Nickname)
            ? own.Nickname + (string.IsNullOrEmpty(own.Job) ? string.Empty : $" · {own.Job}")
            : "내 캐릭터";

        _consentDialogOpen = true;
        try
        {
            var dlg = new StatsConsentModal(label) { Owner = owner };
            dlg.ShowDialog();
            if (!dlg.Answered)
            {
                // Dismissed, not answered — record nothing. Writing "declined" here is what silently retired a
                // character from statistics forever; see StatsConsentModal for the full shape of that failure.
                // The session-level _consentPrompted guard still stops it re-popping in this session.
                return;
            }

            if (dlg.Accepted)
            {
                services.Consent.Set("accepted", uploadEnabled: true, publicCharacter: dlg.PublicCharacter, services.Version);
            }
            else
            {
                services.Consent.Set("declined", uploadEnabled: false, publicCharacter: false, services.Version);
            }
        }
        finally
        {
            _consentDialogOpen = false;
        }
    }

    /// <summary>Settings "위치 초기화": clear a panel's saved position and re-dock it now.</summary>
    private void ResetPanelPosition(string which, MeterServices services, OverlayWindow overlay)
    {
        switch (which)
        {
            case "meter":
                services.Props.SetProperty("uiX", string.Empty);
                services.Props.SetProperty("uiY", string.Empty);
                services.Props.SetProperty("windowX", string.Empty);
                services.Props.SetProperty("windowY", string.Empty);
                overlay.Left = 40;
                overlay.Top = 40;
                break;
            case "join":
                services.Props.SetProperty("joinPanelX", string.Empty);
                services.Props.SetProperty("joinPanelY", string.Empty);
                _joinPanelPositioned = false;
                if (_joinPanel is { } jp && jp.Opacity > 0)
                {
                    jp.Left = overlay.Left;
                    jp.Top = overlay.Top + overlay.ActualHeight + 8;
                }

                break;
            case "history":
                services.Props.SetProperty("historyPanelX", string.Empty);
                services.Props.SetProperty("historyPanelY", string.Empty);
                _historyPanelPositioned = false;
                if (_historyPanel is { } hp && _historyPanelVisible)
                {
                    hp.Left = overlay.Left + overlay.ActualWidth + 8;
                    hp.Top = overlay.Top;
                }

                break;
            case "aether":
                services.Props.SetProperty("aetherPanelX", string.Empty);
                services.Props.SetProperty("aetherPanelY", string.Empty);
                _aetherPanelPositioned = false;
                if (_aetherPanel is { } ap)
                {
                    // Size too, not just position: the panel grew for the weekly-content chips, and anyone who
                    // had ever resized it is stuck with the old width otherwise. Assigning re-saves through
                    // AttachResize's SizeChanged, so the shipped size is what the next launch restores.
                    if (_aetherPanelDefaultSize.W > 0)
                    {
                        ap.Width = _aetherPanelDefaultSize.W;
                        ap.Height = _aetherPanelDefaultSize.H;
                    }

                    if (_aetherPanelVisible)
                    {
                        ap.Left = overlay.Left + overlay.ActualWidth + 8;
                        ap.Top = overlay.Top + 40;
                    }
                }

                break;
        }
    }

    /// <summary>Confine a window to its monitor while multi-monitor movement is off (off-screen guard).</summary>
    private void AttachScreenClamp(Window w)
    {
        w.LocationChanged += (_, _) => ScreenClamp.Apply(w, _settings?.MultiMonitorMode ?? false);
    }

    /// <summary>One-shot off-screen reconciliation after a window is shown. LoadPosition/LoadPanelPosition
    /// assign Left/Top before the HWND and layout exist, so a persisted position naming a monitor that no
    /// longer exists (undocked/disconnected) or lying outside the virtual desktop would otherwise restore
    /// the window invisibly. Dispatched at Loaded priority so ActualWidth/Height are valid when it runs.</summary>
    private void ClampWhenLoaded(Window w) =>
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => ScreenClamp.Apply(w, _settings?.MultiMonitorMode ?? false)));

    /// <summary>Display topology changed (monitor unplugged / resolution / arrangement) — pull every
    /// window back onto a live monitor. Fires off the UI thread, so marshal the clamp to the dispatcher.</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ClampAllWindows);

    /// <summary>Re-clamp every meter window (called when multi-monitor is turned off).</summary>
    private void ClampAllWindows()
    {
        bool allow = _settings?.MultiMonitorMode ?? false;
        foreach (Window? w in new Window?[] { _overlayWindow, _joinPanel, _historyPanel, _aetherPanel, _skillFlyout, _cooldownFlyout, _detailWindow })
        {
            if (w != null)
            {
                ScreenClamp.Apply(w, allow);
            }
        }

        ReflowBuffOverlay();     // 폭 상한까지 다시 잡아야 하는 두 창은 자기 경로로 간다
        ReflowCooldownOverlay();
    }

    /// <summary>버프 오버레이의 폭 상한과 실제 위치를 "집"(사용자가 정한 좌표)에서 다시 계산한다. 이 창만
    /// SizeToContent 라 슬롯 수 × 아이콘 배율만큼 폭이 스스로 자라므로 두 가지가 필요하다.
    /// ① 폭 상한 — 없으면 WPF 가 작업영역 폭에서 측정을 잘라 넘친 슬롯을 줄바꿈도 스크롤도 없이 버린다
    /// (ItemsPanel 의 WrapPanel 도 상한이 있어야 줄을 바꾼다).
    /// ② 화면 안 복귀 — 넓어졌을 땐 끌어오고 다시 좁아졌을 땐 집으로 되돌린다.
    /// 두 계산 모두 기준 모니터를 <b>집 좌표</b>에서 고른다. 창 사각형으로 고르면 (a) 폭이 자라 두 모니터에
    /// 걸치는 순간 "많이 겹친 쪽"인 이웃 모니터가 뽑혀 창이 게임 화면 밖으로 밀려나고, (b) 폭 → 모니터 →
    /// 상한 → 폭 되먹임이 닫혀 작업영역이 다른 듀얼에서 진동한다.
    /// 클램프된 좌표를 집으로 저장하지 않는 것도 요점 — 저장하면 창이 커질 때마다 집이 왼쪽으로 옮겨가
    /// 사용자가 정한 자리가 세션 안에서 조금씩 사라진다.</summary>
    private void ReflowBuffOverlay() => ReflowOverlay(_buffOverlay, _buffOverlayHome, null);

    /// <summary>쿨타임 오버레이의 리플로우. 버프 오버레이와 다른 점은 폭 상한을 화면이 아니라 <b>사용자가 정한
    /// "한 줄 최대 개수"</b>가 정한다는 것뿐이다 — 이 창은 슬롯이 20~30개까지 가므로 화면 폭까지 늘어나게 두면
    /// 한 줄짜리 띠가 모니터를 가로지른다. 슬롯 하나의 폭은 셀 46 + 좌우 여백 1+1 = 48 DIP(배율 1 기준).
    /// ⚠️ 이 값은 CooldownOverlayPanel.xaml 의 셀 크기·여백과 반드시 같아야 한다 — 상한이 실제보다 좁으면
    /// 한 줄에 예상보다 적게 들어가고, 넓으면 WrapPanel 이 줄을 안 바꿔 넘친 슬롯이 조용히 사라진다.</summary>
    private void ReflowCooldownOverlay()
    {
        double scale = Math.Clamp(_settings?.CooldownUiIconSize ?? 40, 32, 80) / 40.0;
        int perRow = _settings?.CooldownUiPerRow ?? 8;
        ReflowOverlay(_cooldownOverlay, _cooldownOverlayHome, (perRow * 48.0 * scale) + 10);
    }

    /// <summary>SizeToContent 오버레이(버프·쿨타임)의 폭 상한과 실제 위치를 "집"(사용자가 정한 좌표)에서 다시
    /// 계산한다. 이 창들만 폭이 스스로 자라므로 두 가지가 필요하다.
    /// ① 폭 상한 — 없으면 WPF 가 작업영역 폭에서 측정을 잘라 넘친 슬롯을 줄바꿈도 스크롤도 없이 버린다
    /// (ItemsPanel 의 WrapPanel 도 상한이 있어야 줄을 바꾼다).
    /// ② 화면 안 복귀 — 넓어졌을 땐 끌어오고 다시 좁아졌을 땐 집으로 되돌린다.
    /// 두 계산 모두 기준 모니터를 <b>집 좌표</b>에서 고른다. 창 사각형으로 고르면 (a) 폭이 자라 두 모니터에
    /// 걸치는 순간 "많이 겹친 쪽"인 이웃 모니터가 뽑혀 창이 게임 화면 밖으로 밀려나고, (b) 폭 → 모니터 →
    /// 상한 → 폭 되먹임이 닫혀 작업영역이 다른 듀얼에서 진동한다.
    /// 클램프된 좌표를 집으로 저장하지 않는 것도 요점 — 저장하면 창이 커질 때마다 집이 왼쪽으로 옮겨가
    /// 사용자가 정한 자리가 세션 안에서 조금씩 사라진다.
    /// <para><paramref name="preferredMaxWidth"/> 는 화면 상한보다 좁게 두고 싶을 때만 준다(쿨타임 오버레이의
    /// 한 줄 개수). 화면 상한은 언제나 함께 걸린다.</para></summary>
    private void ReflowOverlay(OverlayPanelWindow? window, Point? homePoint, double? preferredMaxWidth)
    {
        // 드래그 중에는 손대지 않는다 — 오버레이는 계속 갱신되니 옮기는 도중 슬롯 하나가 사라져 창이 줄어들 수
        // 있고, 그때 집으로 되돌리면 잡고 있던 창이 커서 밑에서 빠져나간다. 드래그가 끝나면 PositionChanged 가
        // 새 집을 알려주며 곧바로 다시 맞춘다.
        if (window is null || homePoint is null || window.IsDragging)
        {
            return;
        }

        Point home = homePoint.Value;
        // 작업영역보다 살짝 좁게 — WPF 자체 측정 캡이 작업영역 폭 + 20px 근처라 그 아래에 머물러야 한다.
        double max = Math.Max(120, ScreenClamp.WorkAreaWidth(window, home) - 16);
        if (preferredMaxWidth is { } want)
        {
            max = Math.Min(max, Math.Max(120, want));
        }

        if (Math.Abs(window.MaxWidth - max) > 0.5)
        {
            window.MaxWidth = max;
        }

        window.Left = home.X;
        window.Top = home.Y;
        ScreenClamp.Apply(window, _settings?.MultiMonitorMode ?? false, home);
    }

    /// <summary>
    /// One-time widen for the per-row "상위 X.X%" chip. The meter's default grew 420 → 490 to make room; a user
    /// who never dragged the edge has exactly the old default saved, so bumping only that exact value widens
    /// them without touching anyone who chose their own width. Runs once (guarded by its own settings key) so a
    /// user who later shrinks back to 420 on purpose is never re-widened.
    /// </summary>
    private static void MigrateMeterWidthForTierChip(PropertyHandler props) =>
        MigrateDefaultWidth(props, "meterWidthTierChipMigrated", "meterWidth", 420.0, 490.0);

    private static void MigrateJoinPanelWidthForTierChip(PropertyHandler props) =>
        MigrateDefaultWidth(props, "joinPanelWidthTierChipMigrated", "joinPanelWidth", 300.0, 350.0);

    /// <summary>Carry a widened default onto users who still sit at the OLD default. A width is only persisted
    /// once the user drags the window, so someone who never touched it would otherwise keep the old size forever
    /// and see the new chip crowd the nickname out. A width the user actually chose (anything but the old
    /// default) is left alone — the run-once flag makes sure we never second-guess it twice.</summary>
    private static void MigrateDefaultWidth(
        PropertyHandler props, string doneKey, string widthKey, double oldDefault, double newDefault)
    {
        if (props.GetProperty(doneKey) == "true")
        {
            return;
        }

        props.SetProperty(doneKey, "true");
        if (double.TryParse(props.GetProperty(widthKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
            && Math.Abs(w - oldDefault) < 0.5)
        {
            props.SetProperty(widthKey, newDefault.ToString("0", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Apply a persisted manual size (no-op if unset/invalid).</summary>
    private static void LoadWindowSize(PropertyHandler props, string wKey, string hKey, Window window)
    {
        if (double.TryParse(props.GetProperty(wKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && w >= window.MinWidth &&
            double.TryParse(props.GetProperty(hKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && h >= window.MinHeight)
        {
            window.Width = w;
            window.Height = h;
        }
    }

    /// <summary>Apply only a persisted WIDTH (for the meter, whose height auto-sizes to its content).</summary>
    private static void LoadWindowWidth(PropertyHandler props, string wKey, Window window)
    {
        if (double.TryParse(props.GetProperty(wKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && w >= window.MinWidth)
        {
            window.Width = w;
        }
    }

    /// <summary>Attach edge resize + persist the new size on resize. When <paramref name="widthOnly"/>,
    /// only the width is persisted (the meter's height is content-driven and deliberately not saved, so a
    /// restart always comes back auto-fitted). <paramref name="onResizeEnd"/> fires once per finished
    /// gesture — the meter uses it to switch height auto-fit back on.</summary>
    private void AttachResize(Window window, PropertyHandler props, string wKey, string hKey,
        bool widthOnly = false, Action<WindowResizer.ResizeEnd>? onResizeEnd = null)
    {
        // 전방향(상/하/좌/우 + 네 모서리) 리사이즈 — 모든 창 공통. v2.8.1은 미터에서만 세로/대각 핸들을
        // 막았는데, 그래봐야 SizeToContent는 폭 드래그에도 꺼지므로(실측) 자동 높이는 못 지키면서
        // 사용자가 높이를 맞출 수단만 사라졌다.
        WindowResizer.Attach(window, onResizeEnd: onResizeEnd);
        // 마지막으로 저장한 값과 다를 때만 기록한다. 미터는 이제 전투 중 행 수 변동마다 높이가 바뀌며 SizeChanged가
        // 자주 발화하는데, 매번 (변화 없는) 폭까지 SetProperty하면 euc-kr 프로퍼티 파일 전체를 동기 재기록해
        // 장시간 느려짐을 유발한다(longrun-slowdown 계열). 실제 값이 바뀔 때만 저장한다.
        string? lastW = null, lastH = null;
        window.SizeChanged += (_, _) =>
        {
            string wv = window.ActualWidth.ToString("0", CultureInfo.InvariantCulture);
            if (wv != lastW)
            {
                lastW = wv;
                props.SetProperty(wKey, wv);
            }

            if (!widthOnly)
            {
                string hv = window.ActualHeight.ToString("0", CultureInfo.InvariantCulture);
                if (hv != lastH)
                {
                    lastH = hv;
                    props.SetProperty(hKey, hv);
                }
            }
        };
    }

    private static bool LoadPanelPosition(PropertyHandler props, Window panel, string xKey, string yKey)
    {
        if (double.TryParse(props.GetProperty(xKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double left) &&
            double.TryParse(props.GetProperty(yKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double top))
        {
            panel.Left = left;
            panel.Top = top;
            return true;
        }

        return false;
    }

    private static void TryLoadCatalogs(MeterServices services)
    {
        string jsonDir = Path.Combine(AppContext.BaseDirectory, "json");
        if (!Directory.Exists(jsonDir))
        {
            return;
        }

        try
        {
            services.LoadCatalogs(jsonDir);
        }
        catch
        {
            // run with empty catalogs; the overlay still shows
        }
    }

    // How old a cached balance may be and still be worth showing. Was 12 h, on the reasoning that "values change
    // between sessions" — true, but the change is now MODELLED rather than skipped: 자연회복 accrues on the
    // server whether or not anyone is logged in, so AetherRegen carries the reading forward. Deliberately the
    // SAME window as the projection: a reading we will not carry forward is one we cannot vouch for at all, and
    // showing it flat would be the very mismatch this is meant to remove.
    private const long AetherRestoreMaxAgeMs = AetherRegen.MaxProjectionMs;

    /// <summary>The 0x610B balance dump held until the identity it belongs to is established — the dump beats the
    /// packet that NAMES the character by ~4 s, so filing it on arrival writes the incoming character's 오드 onto
    /// the outgoing character's record. Exactly the hold <see cref="_weeklyContentPending"/> applies to the
    /// counters that ride the same packet.</summary>
    private (int Base, int Bonus, long AtMs)? _aetherPending;

    /// <summary>Seed the aether balance from the persisted "base,bonus,unixMs" value, projected forward over the
    /// 자연회복 that accrued while the meter was closed. Never overrides a live value (RestoreAetherStatus is
    /// onlyIfEmpty).
    /// <para>The pre-2026-07-30 format had a fourth field (a separately-stored total) — those values were
    /// written while the parser mis-read the single-pool packet, so their 자연회복/추가 split is wrong and the
    /// field-count check below drops them. The badge then simply waits for the next live broadcast.</para></summary>
    private void RestoreAetherFromSettings(MeterServices services)
    {
        string[] parts = _settings!.AetherLastValue.Split(',');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out int b) || !int.TryParse(parts[1], out int bonus)
            || !long.TryParse(parts[2], out long savedAtMs))
        {
            return;
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long ageMs = nowMs - savedAtMs;
        if (ageMs < 0 || ageMs > AetherRestoreMaxAgeMs)
        {
            return; // a clock that has gone backwards, or older than we are willing to vouch for
        }

        // Stored RAW, with the time it was taken — the 자연회복 projection is applied where it is displayed, so
        // the badge keeps up as the session runs instead of freezing on the estimate made at launch.
        services.Data.RestoreAetherStatus(b, bonus, savedAtMs);
    }

    /// <summary>Show what this character last held when nothing live has arrived yet. The badge's only gate is
    /// "has a value ever been seen", and the game speaks on its own schedule — so without this, a character that
    /// is recognized and whose balance we ALREADY KNOW (the 컨텐츠 관리 list is showing it) still renders a blank
    /// footer until the next zone-in. Deliberately a fallback: <c>onlyIfEmpty</c> means a live reading always
    /// wins, and the value is projected forward over the 자연회복 accrued since it was recorded.</summary>
    private void ReseedAetherFromStore(MeterServices services)
    {
        if (_settings is null)
        {
            return;
        }

        // A LIVE reading wins and stops here. A restored one does not: the launch-time cache is a single global
        // value, so it can easily be the character the user played last night rather than the one on screen now
        // — and this character's own record, once we know who they are, is strictly the better answer.
        if (services.Data.AetherOrigin.IsLive && services.Data.CurrentAether.HasValue)
        {
            return;
        }

        string? hash = services.Consent.CurrentCharacterHash();
        if (string.IsNullOrEmpty(hash))
        {
            return; // we don't know who this is yet — a balance under the wrong character is worse than none
        }

        AetherSnapshot? remembered = AetherPerCharacterStore
            .Parse(_settings.AetherPerCharacter, _settings.AetherCharacterNames)
            .Get(hash);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (remembered is not { } snapshot
            || snapshot.SavedAtMs <= 0
            || nowMs - snapshot.SavedAtMs > AetherRestoreMaxAgeMs)
        {
            // Nothing remembered for THIS character — so whatever the launch-time cache put on screen belongs to
            // some other one, and leaving it there is worse than an empty badge: the tooltip would vouch for a
            // stranger's balance. (Widening that cache from 12 h to 7 days is what made this reachable often.)
            services.Data.DropRestoredAether();
            return;
        }

        // Raw, with its own timestamp: projected where it is displayed, never baked into the stored value.
        services.Data.RestoreAetherStatus(
            snapshot.Base, snapshot.Bonus, snapshot.SavedAtMs, onlyIfEmpty: false);
    }

    /// <summary>Persist the current aether value so the next launch can restore it, and remember it under the
    /// character it belongs to.
    /// <para>The record is stamped with when the value was OBSERVED, not when this ran — the offline 자연회복
    /// projection measures elapsed time from that stamp, so re-stamping a value we merely re-displayed would
    /// quietly reset the clock and lose the accrual. A restore has no observation time (arrival stamp 0) and is
    /// therefore skipped entirely.</para></summary>
    private void PersistAether(MeterServices services)
    {
        (int b, int bonus, int _, bool has) = services.Data.CurrentAether;
        (long atMs, bool fromSnapshot, bool isLive) = services.Data.AetherOrigin;

        if (!has)
        {
            // A real character switch: the cached value is the previous character's, so drop it rather than
            // restore it under someone else next launch. The per-character record stays — that IS the memory.
            _settings!.AetherLastValue = string.Empty;
            _aetherPending = null;
            return;
        }

        if (!isLive || atMs <= 0)
        {
            return; // a restore, not an observation
        }

        _settings!.AetherLastValue = string.Join(',',
            b.ToString(CultureInfo.InvariantCulture),
            bonus.ToString(CultureInfo.InvariantCulture),
            atMs.ToString(CultureInfo.InvariantCulture));

        if (fromSnapshot)
        {
            // The login/zone-in dump: hold it until an identity established at or after it says whose it is.
            _aetherPending = (b, bonus, atMs);
            FlushPendingAether(services);
            return;
        }

        // A 0x610C change notice supersedes any dump still waiting: it is newer AND it is unambiguous about its
        // owner (a balance only changes while its character is logged in and playing). Left in place, the held
        // dump would come off hold up to 30 s later and write its older numbers back over this one — losing, for
        // instance, the +40 from a 오드 회복 소모품 used just after a zone-in.
        _aetherPending = null;
        UpsertAetherForCurrentCharacter(services, b, bonus, atMs);
    }

    /// <summary>File a held balance dump once the identity it belongs to has been established. Called both when
    /// the dump arrives (usually a zone-in on the same character, where the identity is already known) and from
    /// the report loop, which is what catches the login/switch case with the naming packet still in flight.</summary>
    private void FlushPendingAether(MeterServices services)
    {
        if (_aetherPending is not { } pending || string.IsNullOrEmpty(services.Consent.CurrentCharacterHash()))
        {
            return;
        }

        // Same rule, same packet family: see WeeklyContentOwnership for the measurement behind it.
        if (!WeeklyContentOwnership.CanFile(
                pending.AtMs, services.Data.ExecutorIdentityAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
        {
            return;
        }

        _aetherPending = null;
        UpsertAetherForCurrentCharacter(services, pending.Base, pending.Bonus, pending.AtMs);
    }

    /// <summary>Remember a balance under the character currently identified, so the 컨텐츠 관리 list can show every
    /// character's 오드 — not just the active one. Callers must already have decided it belongs to that
    /// character.</summary>
    private void UpsertAetherForCurrentCharacter(MeterServices services, int b, int bonus, long atMs)
    {
        string? hash = services.Consent.CurrentCharacterHash();
        if (_settings is null || string.IsNullOrEmpty(hash))
        {
            return;
        }

        AetherPerCharacterStore store = AetherPerCharacterStore.Parse(
            _settings.AetherPerCharacter, _settings.AetherCharacterNames);

        // Never let an older reading overwrite a newer one. Readings do not arrive in order — a dump can be held
        // for its owner while a change notice files immediately — and the record's timestamp is what the offline
        // projection measures from, so going backwards here would both show a stale balance and mis-date it.
        if (store.Get(hash) is { } existing && existing.SavedAtMs > atMs)
        {
            return;
        }

        // Record the name alongside the balance. The key is a one-way hash, so the 오드 목록 can only name a
        // character from a record like this one or from a consent entry — and a character the user never gave a
        // consent decision for has no consent entry at all.
        User? self = services.Data.User(services.Data.ExecutorId());
        if (store.Upsert(hash, new AetherSnapshot(b, bonus, atMs, self?.Nickname, self?.Server ?? 0)))
        {
            _settings.AetherPerCharacter = store.Serialize();
            _settings.AetherCharacterNames = store.SerializeNames();
        }
    }

    /// <summary>Show the 슈고 페스타 reminder toast (docked under the meter) + play the alarm chime.</summary>
    /// <summary>One-time post-update patch-note popup. Shows the running version's RELEASE_NOTES section exactly
    /// once after an UPDATE: compares the running base version to the last-shown one persisted in settings. A
    /// fresh install / first run with this feature (no last-shown version yet) is recorded SILENTLY — it is not
    /// an update. Records the version before showing so a failure never re-pops, and never throws into startup.</summary>
    private void MaybeShowPatchNotes(string version)
    {
        try
        {
            string baseVer = PatchNotesProvider.BaseVersion(version);
            if (baseVer.Length == 0 || _settings is not { } settings)
            {
                return;
            }

            string last = settings.PatchNotesLastShownVersion;
            if (string.IsNullOrEmpty(last))
            {
                settings.PatchNotesLastShownVersion = baseVer; // fresh install: record, do NOT pop
                return;
            }

            if (last == baseVer)
            {
                return; // already shown for this version
            }

            settings.PatchNotesLastShownVersion = baseVer; // record BEFORE showing so a failure never re-pops
            string? notes = PatchNotesProvider.SectionForVersion(LoadEmbeddedReleaseNotes(), baseVer);
            if (string.IsNullOrWhiteSpace(notes))
            {
                return; // no section for this version (e.g. a hotfix with no entry) — skip silently
            }

            new PatchNotesWindow(baseVer, notes, _skin?.IsLight == true).Show();
        }
        catch
        {
            // a "what's new" popup must never disturb startup
        }
    }

    /// <summary>The bundled RELEASE_NOTES.md text (embedded resource), or "" if unavailable.</summary>
    private static string LoadEmbeddedReleaseNotes()
    {
        try
        {
            using Stream? stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("RELEASE_NOTES.md");
            if (stream == null)
            {
                return "";
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    private void ShowShugoAlarm(int lead)
    {
        if (_alarmToast is null || _alarmToastVm is null)
        {
            return;
        }

        _alarmToastVm.SetShugo(lead);
        if (_overlayWindow is { } w)
        {
            _alarmToast.Left = w.Left;
            _alarmToast.Top = w.Top + w.ActualHeight + 8;
        }

        _alarmToast.Present(true);
        PlayAlert(_alarmToastVm.SpokenText);
    }

    /// <summary>Show the 감시자 카이라 4-hour-grid reminder toast + alert sound/voice. Unlike the respawn-timer
    /// alerts this is not gated on being in the abyss — the reminder exists to get you there in time.</summary>
    private void ShowKairaAlarm(int lead, DateTime spawn)
    {
        if (_alarmToast is null || _alarmToastVm is null)
        {
            return;
        }

        _alarmToastVm.SetKaira(lead, spawn);
        if (_overlayWindow is { } w)
        {
            _alarmToast.Left = w.Left;
            _alarmToast.Top = w.Top + w.ActualHeight + 8;
        }

        _alarmToast.Present(true);
        PlayAlert(_alarmToastVm.SpokenText);
    }

    /// <summary>Show a field-boss respawn reminder toast (docked under the meter) + alert sound/voice.</summary>
    private void ShowFieldBossAlarm(FieldBossAlarm.Due due)
    {
        if (_alarmToast is null || _alarmToastVm is null)
        {
            return;
        }

        DateTime respawn = DateTimeOffset.FromUnixTimeMilliseconds(due.TargetMs).LocalDateTime;
        _alarmToastVm.SetFieldBoss(FieldBossCatalog.Name(due.Code), due.LeadMinutes, respawn);
        if (_overlayWindow is { } w)
        {
            _alarmToast.Left = w.Left;
            _alarmToast.Top = w.Top + w.ActualHeight + 8;
        }

        _alarmToast.Present(true);
        PlayAlert(_alarmToastVm.SpokenText);
    }

    /// <summary>
    /// How long before a buff actually expires the "오프" voice should start. Pre-rendered packs play from
    /// disk, so this is now purely a preference — it used to be inflated to ~0.8s to hide the online
    /// synthesiser's round trip.
    /// </summary>
    private const long BuffEndTtsLeadMs = 200;

    /// <summary>
    /// How early the refresh loop starts LOOKING for an expiring buff. This is deliberately NOT the same
    /// number as the lead: the check only sees whatever <see cref="_buffTimer"/> happens to sample, so the
    /// window it scans has to be wider than that timer's 500 ms interval or ticks step straight over it. At a
    /// 200 ms window roughly 3 in 5 end-warnings would simply never fire, and silently — which is exactly how
    /// this would have regressed if the lead had just been turned down.
    /// </summary>
    private const long BuffEndTtsScanMs = 700;

    /// <summary>"버프 종료 3초 전 알림"의 리드. 점멸이 시작되는 시점이자, 음성이 나가는 시점이다.</summary>
    private const long BuffEndWarnLeadMs = 3_000;

    /// <summary>버프 슬롯을 새로 그리는 주기(<see cref="_buffTimer"/>). 스캔 창은 반드시 이보다 넓어야
    /// 틱이 경고를 건너뛰지 않는다.</summary>
    private const long BuffTickMs = 500;

    /// <summary>3초 리드를 적용할 수 있는 최소 지속시간. 리드보다 1초는 길어야 "온"과 경고가 겹치지 않는다.
    /// <b>이보다 짧은 버프는 침묵시키지 않고 종전 경로(만료 직전 "오프")로 되돌린다</b> — 안 그러면 이 옵션을
    /// 켠 것만으로 짧은 버프의 종료 알림이 조용히 사라진다.</summary>
    private const long BuffEndWarnMinDurationMs = BuffEndWarnLeadMs + 1_000;
    private readonly HashSet<int> _buffStartAnnounced = new(); // base codes we've spoken "온" for (cleared when they end)
    private readonly Dictionary<int, long> _buffEndAnnouncedFor = new(); // base code -> the End(ms) already "오프"-warned; a re-cast extends End and re-arms
    /// <summary>Queued-but-unspoken end warnings, with the End(ms) each was queued against, so a re-cast can
    /// cancel one before it lies about a buff that is still up.</summary>
    private readonly Dictionary<int, (System.Windows.Threading.DispatcherTimer Timer, long EndMs)> _buffEndPending = new();
    private long _lastBuffClearRevision; // 마지막으로 본 DataManager.OwnerBuffClearRevision (사망 클리어 감지)

    // Refresh the combat-assist overlay each tick: pull the local player's active buffs, fire the start/end
    // voice alerts, update the slot content, AND reconcile the window's visibility. This 500ms timer always
    // runs (unlike the controller poll, which skips the companion during the startup grace — the cause of the
    // buff overlay staying hidden until settings was opened), so it is the reliable visibility driver. It keys
    // off the controller's CompanionShown — the SAME decision the poll acts on — so Present/Fade never disagree
    // (keying off MeterShown made the two fight while the meter was hidden with "오버레이 유지" on → flicker).
    private void RefreshBuffOverlay(MeterServices services)
    {
        if (_buffOverlayVm is null || _settings is null)
        {
            return;
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        IReadOnlyList<WaffleMeter.Data.OwnerBuffView> buffs = services.Data.ActiveOwnerBuffs(nowMs);
        if (!_settings.ShowOtherPlayerBuffs)
        {
            buffs = buffs.Where(b => !b.ByOther).ToList();
        }

        // 사망으로 버프가 통째로 비워진 틱에서는 종료 음성을 내지 않는다. 클리어된 버프는 스냅샷에서 사라져
        // 보통은 "오프" 조건(남은시간 800ms 이하)에 닿지도 않지만, 스냅샷을 뜬 직후 클리어가 들어오는
        // 서브초 레이스에서는 잔여 버프가 한 번 외칠 수 있다. 알림 상태도 함께 비워 부활 후 재시전 때
        // "온"이 정상적으로 다시 나오게 한다.
        long clearRevision = services.Data.OwnerBuffClearRevision;
        if (clearRevision != _lastBuffClearRevision)
        {
            _lastBuffClearRevision = clearRevision;
            _buffStartAnnounced.Clear();
            _buffEndAnnouncedFor.Clear();
            // 예약된 종료 경고도 함께 버린다. 안 그러면 사망으로 이미 사라진 버프를 두고 "오프"가 뒤늦게
            // 나간다.
            CancelPendingBuffEndAlerts();
        }
        else
        {
            // The announce list includes 음성만 (voice-only) buffs; the overlay draws only Overlay==true ones.
            AnnounceBuffTransitions(buffs);
        }

        _buffOverlayVm.ShowBackground = !_settings.BuffUiTransparent;
        _buffOverlayVm.SetIconSize(_settings.BuffUiIconSize);
        _buffOverlayVm.SetTextColor(_settings.BuffUiTextColor);
        // 표시 순서: 전역 정렬 모드로 줄을 세우고, 사용자가 "맨 앞 고정"한 버프를 그 앞으로 끌어온다.
        List<WaffleMeter.Data.OwnerBuffView> drawn = BuffOverlayOrder.Sort(
            buffs.Where(b => b.Overlay).ToList(), _settings.BuffUiSortMode, _settings.BuffUiPinnedCodes);
        _buffOverlayVm.Update(
            drawn,
            _settings.BuffUiGrayOnCooldown,
            _settings.BuffUiShowLevel,
            _settings.BuffEndWarning3s ? BuffEndWarnLeadMs : 0,
            BuffEndWarnMinDurationMs);

        // Visibility: mirror the controller's companion decision (CompanionShown already folds in ShowBuffUi,
        // the meter's on-screen state, and the "메터 숨겨도 오버레이 유지" toggle). Mirror the meter's click-through
        // when shown, and re-claim topmost each tick so a borderless-fullscreen game can't strand it behind.
        if (_buffOverlay is not null)
        {
            bool show = _settings.ShowBuffUi && (_controller?.CompanionShown ?? true);
            if (show)
            {
                _buffOverlay.SetClickThrough(_controller?.MeterClickThrough ?? false);
                _buffOverlay.Present(true);
                _buffOverlay.ReassertTopmostIfBuried();
            }
            else
            {
                _buffOverlay.Fade();
                // 숨겨진 창을 위해 점멸 타이머를 돌릴 이유가 없다. TierSheen 과 같은 규약이다.
                BuffExpiryFlash.SetDemand(0);
            }
        }
    }

    /// <summary>설정창의 "스킬 고르기" 버튼. 참가요청 픽커와 같은 토글 동작이고, 위치만 설정창 옆으로 잡는다
    /// (설정창이 없으면 미터 옆). 창은 Park 상태로 미리 만들어 두므로 여는 데 지연이 없다.</summary>
    private void ToggleCooldownPicker(MeterServices services)
    {
        if (_cooldownFlyout is null)
        {
            return;
        }

        if (_cooldownFlyoutVisible)
        {
            _cooldownFlyoutVisible = false;
            _cooldownFlyout.Park();
            return;
        }

        Window? anchor = _settingsWindow ?? (Window?)_overlayWindow;
        if (anchor is not null)
        {
            _cooldownFlyout.Left = anchor.Left + anchor.Width + 8;
            _cooldownFlyout.Top = anchor.Top;
        }

        // 창을 열 때마다 직업을 다시 읽는다 — 생성 시점에는 대개 아직 캐릭터를 모르고, 캐릭터를 바꿔도
        // 다음에 열 때 맞는 직업이 걸려야 한다.
        if (_cooldownPickerVm is { } picker)
        {
            picker.OwnJobBand = services.Data.User(services.Data.ExecutorId())?.Job?.SkillBand() ?? 0;
        }

        _cooldownFlyoutVisible = true;
        _cooldownFlyout.Present(true);
    }

    // Refresh the skill-cooldown overlay. Same two-layer shape as the buff overlay — the controller poll
    // publishes the meter's presence, this tick applies our own toggle and reconciles Present/Fade — but the
    // toggle gate comes FIRST here: with the overlay off there is nothing to compute, and the data-layer call
    // takes a lock the capture thread also wants.
    private void RefreshCooldownOverlay(MeterServices services)
    {
        if (_cooldownOverlayVm is null || _settings is null)
        {
            return;
        }

        if (!_settings.ShowCooldownUi)
        {
            _cooldownOverlay?.Fade();
            return;
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        IReadOnlyList<WaffleMeter.Data.SkillCooldownView> rows = services.Data.ActiveCooldowns(nowMs);
        if (_cooldownVisibility is { } picked)
        {
            rows = rows.Where(r => picked.IsVisible(r.GroupId)).ToList();
        }

        _cooldownOverlayVm.ShowBackground = !_settings.CooldownUiTransparent;
        _cooldownOverlayVm.SetIconSize(_settings.CooldownUiIconSize);
        _cooldownOverlayVm.SetTextColor(_settings.CooldownUiTextColor);
        _cooldownOverlayVm.Update(rows);

        if (_cooldownOverlay is not null)
        {
            if (_controller?.CompanionBaseShown ?? true)
            {
                _cooldownOverlay.SetClickThrough(_controller?.MeterClickThrough ?? false);
                _cooldownOverlay.Present(true);
                _cooldownOverlay.ReassertTopmostIfBuried();
            }
            else
            {
                _cooldownOverlay.Fade();
            }
        }
    }

    // Speak "이름 온" when a buff set to "오버레이+음성" / "음성만" starts and "이름 오프" just before it ends (each
    // once, gated by the global start/end toggles). Per-buff voice is chosen in the 버프 알림 tab; independent of
    // the visual overlay so voice can be used on its own (음성만). Codes here are already BASE codes, so the same
    // buff re-cast by another player is one entry — it takes over the earlier one WITHOUT a second start voice,
    // and re-arms the end alert off the refreshed expiry. Alerts are queued durably so a burst of simultaneous
    // buffs is spoken in sequence rather than the later ones being dropped.
    private void AnnounceBuffTransitions(IReadOnlyList<WaffleMeter.Data.OwnerBuffView> buffs)
    {
        if (_settings is not { } s || (!s.BuffTtsOnStart && !s.BuffTtsOnEnd))
        {
            _buffStartAnnounced.Clear();
            _buffEndAnnouncedFor.Clear();
            CancelPendingBuffEndAlerts(); // 음성을 끈 뒤에 예약분이 뒤늦게 말하지 않도록
            return;
        }

        HashSet<int> voiceCodes = s.BuffUiVoiceCodes; // base codes set to 오버레이+음성 or 음성만
        foreach (WaffleMeter.Data.OwnerBuffView b in buffs)
        {
            if (!voiceCodes.Contains(b.Code))
            {
                continue; // overlay-only (or off) buff — no voice
            }

            // Start once per buff. A same-buff re-cast (base already announced) does NOT re-announce — the later
            // cast silently takes over the earlier one.
            if (s.BuffTtsOnStart && _buffStartAnnounced.Add(b.Code))
            {
                TtsSpeech.Speak($"{b.Name} 온", s.AlarmVolume, durable: true, chimeFallback: false);
            }

            // Pre-warn the end once inside the lead window (skip very short buffs so it doesn't double up with
            // the start). Keyed on the End(ms): a re-cast that extends the buff gives a new End and re-arms this,
            // so the end alert fires off the REFRESHED duration. A maintained stance (폭주) is skipped entirely:
            // its expiry is a synthetic keep-alive, not a real end, so pre-warning it spoke a false "오프" every
            // time a held re-broadcast gap elapsed while the stance was still up.
            // "3초 전 알림"이 켜져 있으면 그쪽이 만료 직전 "오프"를 <b>대체한다</b> — 둘 다 내면 같은 버프를
            // 2.8초 간격으로 두 번 말한다. 문구도 달라야 한다: 3초 전에 "오프"라고 하면 이미 끝난 것으로 들린다.
            // 판정은 버프마다 따로 한다 — 4초도 안 되는 버프에 3초 리드는 성립하지 않으므로 그런 버프만
            // 종전 경로로 되돌린다(옵션을 켠 대가로 짧은 버프가 조용해지면 그게 더 나쁜 회귀다).
            bool warnEarly = s.BuffEndWarning3s && b.DurationMs > BuffEndWarnMinDurationMs;
            long lead = warnEarly ? BuffEndWarnLeadMs : BuffEndTtsLeadMs;
            long scan = warnEarly ? BuffEndWarnLeadMs + BuffTickMs : BuffEndTtsScanMs;
            long minDuration = warnEarly ? BuffEndWarnMinDurationMs : BuffEndTtsScanMs * 2;

            if (s.BuffTtsOnEnd && !b.Indefinite && b.DurationMs > minDuration && b.RemainingMs > 0 && b.RemainingMs <= scan
                && (!_buffEndAnnouncedFor.TryGetValue(b.Code, out long warnedEnd) || warnedEnd != b.EndMs))
            {
                _buffEndAnnouncedFor[b.Code] = b.EndMs;
                string text = warnEarly ? $"{b.Name} 오프 예정" : $"{b.Name} 오프";
                // Claimed on the tick that spotted it, but spoken at the lead — the tick lands anywhere in the
                // scan window, so speaking immediately would put the voice up to half a second early.
                SpeakBuffEnd(b.Code, b.EndMs, text, s.AlarmVolume, b.RemainingMs - lead);
            }

            // A re-cast inside the scan window moves End out, so the warning we already queued is now about an
            // expiry that is not going to happen — drop it. The claim above is keyed on End, so the refreshed
            // buff re-arms by itself and warns again off the new one.
            if (_buffEndPending.TryGetValue(b.Code, out (System.Windows.Threading.DispatcherTimer Timer, long EndMs) pending)
                && pending.EndMs != b.EndMs)
            {
                pending.Timer.Stop();
                _buffEndPending.Remove(b.Code);
            }
        }

        // Codes no longer active can announce again next time they appear.
        var current = buffs.Select(b => b.Code).ToHashSet();
        _buffStartAnnounced.RemoveWhere(c => !current.Contains(c));
        foreach (int c in _buffEndAnnouncedFor.Keys.Where(c => !current.Contains(c)).ToList())
        {
            _buffEndAnnouncedFor.Remove(c);
        }
    }

    /// <summary>예약해 둔 종료 경고를 전부 취소한다 — 사망 클리어, 음성 옵션 해제처럼 "그 버프가 더는
    /// 우리 관심사가 아니다"가 된 순간. 안 그러면 이미 없는 버프를 두고 뒤늦게 말한다.</summary>
    private void CancelPendingBuffEndAlerts()
    {
        foreach ((System.Windows.Threading.DispatcherTimer Timer, long EndMs) queued in _buffEndPending.Values)
        {
            queued.Timer.Stop();
        }

        _buffEndPending.Clear();
    }

    /// <summary>Sound an alert: speak it with TTS if enabled (which falls back to the chime on failure),
    /// otherwise play the chime when the sound setting is on.</summary>
    /// <summary>
    /// Speak a buff's end warning after <paramref name="delayMs"/>, so it lands at the intended lead rather
    /// than whenever the 500 ms refresh tick noticed. A one-shot dispatcher timer rather than a delay inside
    /// <c>TtsSpeech</c>: that queue is drained by a single worker, so parking a request in it would hold up
    /// every other alert behind it.
    /// </summary>
    private void SpeakBuffEnd(int code, long endMs, string text, double volume, long delayMs)
    {
        if (delayMs <= 0)
        {
            TtsSpeech.Speak(text, volume, durable: true, chimeFallback: false);
            return;
        }

        if (_buffEndPending.Remove(code, out (System.Windows.Threading.DispatcherTimer Timer, long EndMs) prev))
        {
            prev.Timer.Stop(); // one pending warning per buff
        }

        var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(delayMs),
        };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _buffEndPending.Remove(code);
            TtsSpeech.Speak(text, volume, durable: true, chimeFallback: false);
        };
        _buffEndPending[code] = (t, endMs);
        t.Start();
    }

    private void PlayAlert(string spokenText)
    {
        if (_settings is not { } s)
        {
            return;
        }

        if (s.TtsEnabled)
        {
            TtsSpeech.Speak(spokenText, s.AlarmVolume);
        }
        else if (s.AlarmSoundEnabled)
        {
            AlarmSound.Play(s.AlarmVolume);
        }
    }

    /// <summary>Show a user custom-alarm toast (docked under the meter) + play the alarm chime.</summary>
    private void ShowCustomAlarm(CustomAlarm alarm)
    {
        if (_alarmToast is null || _alarmToastVm is null)
        {
            return;
        }

        _alarmToastVm.SetCustom(alarm.Title);
        if (_overlayWindow is { } w)
        {
            _alarmToast.Left = w.Left;
            _alarmToast.Top = w.Top + w.ActualHeight + 8;
        }

        _alarmToast.Present(true);
        PlayAlert(_alarmToastVm.SpokenText);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _controller?.Stop(); // unhook the foreground WinEvent + stop the poll
        _alarms?.Stop();
        _buffPresets?.Dispose();
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _engine?.Services.DebugLogger.Stop(); // finalize the gzip trailer if a packet-log session is running
        _engine?.Dispose();
        TtsSpeech.Shutdown(); // the voice players keep the clip they last played open
        base.OnExit(e);
    }
}
