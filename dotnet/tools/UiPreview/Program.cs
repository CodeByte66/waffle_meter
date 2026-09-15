using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WaffleMeter.App.Core;
using WaffleMeter.App.Wpf;
using WaffleMeter.Capture;
using WaffleMeter.Data;
using WaffleMeter.Replay;
using WaffleMeter.Services;

namespace WaffleMeter.Tools.UiPreview;

/// <summary>
/// Renders the WPF overlay panels with sample data to PNG via RenderTargetBitmap — an offline UI check
/// (no game / capture host needed). Output dir is printed to stdout.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        string outDir = Path.Combine(Path.GetTempPath(), "waffle_ui_preview");
        Directory.CreateDirectory(outDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Shared overlay chrome (HeaderIconButton/HeaderCloseButton) — merged app-wide by App.xaml in the
        // real app; the preview host must merge it too so windows resolve the StaticResource styles.
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/WaffleMeter.App.Wpf;component/Themes/PanelChrome.xaml"),
        });

        VerifySettings();

        var props = new PropertyHandler();
        var settings = new MeterSettings(props);
        var theme = new MeterColorTheme(props);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Preload skin palettes once (re-sourcing a ResourceDictionary mid-run is flaky).
        var skins = new[] { "Dark", "Midnight", "Slate", "Light" }.ToDictionary(
            s => s,
            s => new ResourceDictionary { Source = new Uri($"pack://application:,,,/WaffleMeter.App.Wpf;component/Themes/Skin.{s}.xaml") });

        // 게이지 장식 비교 시트 — 5종을 같은 막대·같은 길이·세 위상으로. 실제 GaugeFxLayer 로 그린다.
        CaptureGaugeSheet(outDir, light: false, skins["Dark"]);
        CaptureGaugeSheet(outDir, light: true, skins["Light"]);

        foreach (string skin in skins.Keys)
        {
            ResourceDictionary palette = skins[skin];

            var join = new JoinRequestViewModel(settings);
            join.Reconcile(new List<JoinRequestUser>
            {
                new() { Requester = 1, Nickname = "쿵해쫑", Job = "마도성", Server = 1001, Power = 423359, ArrivedAt = now - 2000,
                    Skill = new Dictionary<int, int> { [15210000] = 5, [15740000] = 3, [15360000] = 2, [15400000] = 4 } },
                new() { Requester = 2, Nickname = "검사왕", Job = "검성", Server = 1002, Power = 512000, ArrivedAt = now - 9000 },
                new() { Requester = 3, Nickname = "빛의사제", Job = "치유성", Server = 2001, Power = 298400, ArrivedAt = now - 16500 },
            });
            Capture(() => new JoinRequestPanel { DataContext = join }, palette, Path.Combine(outDir, $"join_{skin}.png"));

            if (skin is "Dark" or "Light")
            {
                // 파티 신청 카드의 티어 칩. The live path fills these from a batched, rate-limited lookup; here
                // they are painted directly so the layout can be checked offline. The third applicant keeps no
                // tier on purpose — that is what a non-consenting (or too-new) character looks like.
                join.Rows[0].ApplyTier((1, "챌린저", 0.6));
                join.Rows[1].ApplyTier((4, "플래티넘", 24.8));
                Capture(() => new JoinRequestPanel { DataContext = join }, palette, Path.Combine(outDir, $"join_tier_{skin}.png"));
            }

            var history = new BattleHistoryViewModel(theme, settings);
            history.SetBattles(SampleBattles(now));
            Capture(() => new HistoryPanel { DataContext = history }, palette, Path.Combine(outDir, $"history_{skin}.png"));

            // 컨텐츠 관리: characters with their 오드 and their weekly 성역 clears. The sample deliberately
            // covers all three states a chip can be in — un-cleared, cleared, and a character with no record
            // this week at all (which reads as un-cleared).
            var content = new AetherPanelViewModel(settings);
            content.SetRows(SampleContentRows(now));
            Capture(() => new AetherPanel { DataContext = content }, palette, Path.Combine(outDir, $"content_{skin}.png"));

            string currentSkin = skin;
            var overlay = new OverlayViewModel("1.7.8", settings, theme, () => currentSkin == "Light") { Status = "캡처 중" };
            overlay.Update(SampleMeterReport(now));
            Capture(() => new OverlayWindow { DataContext = overlay }, palette, Path.Combine(outDir, $"meter_{skin}.png"));

                // 패치노트 팝업: render the REAL top section of RELEASE_NOTES.md through the same provider the
                // app uses, so what ships is exactly what was reviewed.
                string notesPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "RELEASE_NOTES.md");
                if (File.Exists(notesPath))
                {
                    string md = File.ReadAllText(notesPath);
                    int start = md.IndexOf("## ", StringComparison.Ordinal);
                    int next = md.IndexOf(Environment.NewLine + "---", StringComparison.Ordinal);
                    if (next < 0) { next = md.IndexOf("\n---", StringComparison.Ordinal); }
                    string section = start >= 0 && next > start ? md[start..next].Trim() : md[..Math.Min(2000, md.Length)];
                    Capture(() => new PatchNotesWindow("2.9.0", section, currentSkin == "Light"), palette, Path.Combine(outDir, $"patchnotes_{skin}.png"));
                    // The popup scrolls, so the first capture only ever shows the top. Render the LAST sections
                    // on their own too — that is where the wider tables live, and they are otherwise unreviewable.
                    int tail = section.IndexOf("## [변경]", StringComparison.Ordinal);
                    if (tail > 0)
                    {
                        Capture(() => new PatchNotesWindow("2.9.0", section[tail..], currentSkin == "Light"), palette, Path.Combine(outDir, $"patchnotes_tail_{skin}.png"));
                    }
                }

            if (skin is "Dark" or "Light")
            {
                // 던전 티어: every tier on one screen so the eight ring treatments can be compared at the real
                // badge size. Rank 1~3 carry the inner ring, 4~7 a single ring, 8 (아이언) stays unaccented, and
                // the footer carries the self "티어 · 상위 X.X%" chip. Light is captured too because a palette
                // tuned for the dark skins can vanish on #FAFCFF.
                // The resolver, not SetTiers: Update builds the rows and reads the tier map WHILE building, so a
                // map handed over afterwards lands one frame late and the capture came out with no tiers at all.
                var tierVm = new OverlayViewModel("1.7.8", settings, theme, () => currentSkin == "Light") { Status = "캡처 중" };
                tierVm.TierResolver = _ => SampleTiers();
                tierVm.Update(SampleTierReport(now));
                Capture(() => new OverlayWindow { DataContext = tierVm }, palette, Path.Combine(outDir, $"meter_tier_{skin}.png"));

                // …and the same rows with the feature off: must be pixel-identical to today's meter, including
                // the window height (a badge that grows a row re-opens the SizeToContent regression).
                var tierOffVm = new OverlayViewModel("1.7.8", settings, theme, () => currentSkin == "Light") { Status = "캡처 중" };
                tierOffVm.Update(SampleTierReport(now));
                Capture(() => new OverlayWindow { DataContext = tierOffVm }, palette, Path.Combine(outDir, $"meter_tier_off_{skin}.png"));

                // 닉네임 효과: one row per effect family so the warm/cold split and the still variants can be
                // compared at the real row size. Light matters — a palette tuned on the dark skins can vanish
                // on #FAFCFF, and the whole point is that the mark is legible.
                var fxVm = new OverlayViewModel("1.7.8", settings, theme, () => currentSkin == "Light") { Status = "캡처 중" };
                fxVm.SetNameFxRoster(SampleNameFxRoster(SampleMeterReport(now)));
                fxVm.Update(SampleMeterReport(now));
                Capture(() => new OverlayWindow { DataContext = fxVm }, palette, Path.Combine(outDir, $"meter_namefx_{skin}.png"));
            }

            if (skin == "Dark")
            {
                // gauge-form variants: "fill" (above), "bar" (thin bottom bar), "none".
                foreach (string bs in new[] { "bar", "none" })
                {
                    settings.BarStyle = bs;
                    var bv = new OverlayViewModel("1.7.8", settings, theme) { Status = "캡처 중" };
                    bv.Update(SampleMeterReport(now));
                    Capture(() => new OverlayWindow { DataContext = bv }, palette, Path.Combine(outDir, $"meter_gauge_{bs}_Dark.png"));
                }
                settings.BarStyle = "fill";

                // 레이아웃 3종 — 같은 리포트를 세 생김새로.
                string savedLayout = settings.MeterLayoutId;
                int savedRow = settings.RowHeight;
                foreach (var lay in WaffleMeter.App.Core.MeterLayout.All)
                {
                    settings.MeterLayoutId = lay.Id;
                    settings.RowHeight = lay.DefaultRowHeight;
                    var lv = new OverlayViewModel("1.7.8", settings, theme) { Status = "캡처 중" };
                    lv.SetRecognized(true, "콘팡");
                    lv.Update(SampleMeterReport(now));
                    Capture(() => new OverlayWindow { DataContext = lv }, palette, Path.Combine(outDir, $"layout_{lay.Id}_Dark.png"));
                }
                settings.MeterLayoutId = savedLayout;
                settings.RowHeight = savedRow;

                // font test: a visually-distinct BUNDLED font must actually reach the row text (item 3).
                string savedFont = settings.FontFamily;
                settings.FontFamily = "Tmoney RoundWind ExtraBold";
                var fv = new OverlayViewModel("1.7.8", settings, theme) { Status = "캡처 중" };
                fv.SetRecognized(true, "콘팡"); // also exercise the "캐릭터 인식됨" indicator (item 5)
                fv.Update(SampleMeterReport(now));
                Capture(() => new OverlayWindow { DataContext = fv }, palette, Path.Combine(outDir, "meter_font_Dark.png"));
                settings.FontFamily = savedFont;

                // idle case: durationMs>0 but 0 rows — must NOT stack placeholder + combat-timer pill.
                var idle = new OverlayViewModel("1.7.8", settings, theme) { Status = "캡처 헬퍼 시작 실패: NotFound" };
                idle.Update(new DpsReport { BattleStart = 0, BattleEnd = 5000 });
                Capture(() => new OverlayWindow { DataContext = idle }, palette, Path.Combine(outDir, "meter_idle_Dark.png"));

                // Detail window: one shot per tab (the sample report carries frozen skill/buff snapshots, so the
                // tables render exactly as they do for a saved battle).
                var calc = new WaffleMeter.Data.DpsCalculator(new WaffleMeter.Data.DataManager(), () => { });
                var details = new DetailsViewModel(SampleMeterReport(now), 1, calc, "콘팡", theme, settings.FontFamily);
                details.Refresh(SampleMeterReport(now));
                string[] tabs = ["skills", "buffs", "debuffs", "graph"];
                for (int t = 0; t < tabs.Length; t++)
                {
                    int index = t;
                    Capture(
                        () =>
                        {
                            var w = new DetailWindow { DataContext = details };
                            ((System.Windows.Controls.TabControl)w.FindName("Tabs")).SelectedIndex = index;
                            return w;
                        },
                        palette,
                        Path.Combine(outDir, $"detail_{tabs[index]}_Dark.png"));
                }

                var buffVm = new BuffOverlayViewModel();
                buffVm.SetTextColor("#FFD54A"); // amber text (verifies the color option)
                buffVm.Update(new List<WaffleMeter.Data.OwnerBuffView>
                {
                    // 폭주: an indefinite stance shown with a short refresh-based fallback duration (~6s).
                    new(19130000, "폭주", 5_400, 6_000, 5_400, false, true, false, true),
                    new(18290000, "회전격", 12_000, 30_000, 12_000, false, true, false, false),
                    new(11400000, "축복", 45_000, 60_000, 45_000, true, true, false, false),
                    new(13050000, "섬광베기", 6_000, 20_000, 6_000, false, true, true, false), // on cooldown → grayed
                }, grayOnCooldown: true);
                Capture(() => new BuffOverlayPanel(buffVm), palette, Path.Combine(outDir, "buffoverlay_Dark.png"));

                // 배율 하한 (32px = 80%)
                var buffSmallVm = new BuffOverlayViewModel();
                buffSmallVm.SetIconSize(32);
                buffSmallVm.Update(new List<WaffleMeter.Data.OwnerBuffView>
                {
                    new(18290000, "회전격", 12_000, 30_000, 12_000, false, true, false, false),
                    new(11400000, "축복", 45_000, 60_000, 45_000, true, true, false, false),
                    new(13050000, "섬광베기", 6_000, 20_000, 6_000, false, true, false, false),
                }, grayOnCooldown: false);
                Capture(() => new BuffOverlayPanel(buffSmallVm), palette, Path.Combine(outDir, "buffoverlay_small_Dark.png"));

                // 배율 상한 (80px = 200%) — 창 폭·간격·아이콘 확대 품질을 눈으로 확인하는 용도
                var buffLargeVm = new BuffOverlayViewModel();
                buffLargeVm.SetIconSize(80);
                buffLargeVm.Update(new List<WaffleMeter.Data.OwnerBuffView>
                {
                    new(18290000, "회전격", 12_000, 30_000, 12_000, false, true, false, false),
                    new(11400000, "축복", 45_000, 60_000, 45_000, true, true, false, false),
                    new(13050000, "섬광베기", 6_000, 20_000, 6_000, false, true, false, false),
                }, grayOnCooldown: false);
                Capture(() => new BuffOverlayPanel(buffLargeVm), palette, Path.Combine(outDir, "buffoverlay_large_Dark.png"));

                // 종료 3초 전 점멸. 마스크가 켜진 프레임을 잡는다(BuffExpiryFlash 는 demand>0 에서 On 으로
                // 시작한다) — 점멸이 "무엇인지 읽히되 확실히 어둡다"를 눈으로 확인하는 용도다. 쿨타임 회색과
                // 겹친 슬롯(섬광베기)도 같이 둬서 둘이 포개졌을 때를 본다.
                var buffExpiringVm = new BuffOverlayViewModel();
                buffExpiringVm.Update(new List<WaffleMeter.Data.OwnerBuffView>
                {
                    new(18290000, "회전격", 2_400, 30_000, 2_400, false, true, false, false),   // 임박 → 점멸
                    new(11400000, "축복", 45_000, 60_000, 45_000, true, true, false, false),    // 여유 → 평상
                    new(13050000, "섬광베기", 1_200, 20_000, 1_200, false, true, true, false),  // 임박 + 쿨타임 회색
                    new(19130000, "폭주", 5_400, 0, 5_400, false, true, false, true),           // 무기한 → 점멸 제외
                }, grayOnCooldown: true, showLevel: true, expiryWarnMs: 3_000, expiryMinDurationMs: 4_000);
                Capture(() => new BuffOverlayPanel(buffExpiringVm), palette, Path.Combine(outDir, "buffoverlay_expiring_Dark.png"));

                // opaque/findable mode (투명 배경 off) — background + border so an empty window is locatable
                var buffBgVm = new BuffOverlayViewModel { ShowBackground = true };
                buffBgVm.Update(new List<WaffleMeter.Data.OwnerBuffView>(), grayOnCooldown: false);
                Capture(() => new BuffOverlayPanel(buffBgVm), palette, Path.Combine(outDir, "buffoverlay_bg_Dark.png"));

                // (the per-job buff picker is now embedded in the settings 버프 알림 tab, not a standalone window)
                var bossPickerVm = new FieldBossPickerViewModel(settings);
                Capture(() => new FieldBossPickerWindow(bossPickerVm), palette, Path.Combine(outDir, "fieldbosspicker_Dark.png"));

                Capture(() => new CloseActionDialog(), palette, Path.Combine(outDir, "closedialog_Dark.png"));
                Capture(() => new StatsConsentModal("콘팡 · 마도성"), palette, Path.Combine(outDir, "consent_Dark.png"));

                var dl = new UpdateToastViewModel();
                dl.SetDownloading("1.8.0", 62);
                Capture(() => new UpdateToast { DataContext = dl }, palette, Path.Combine(outDir, "toast_downloading_Dark.png"));
                var rdy = new UpdateToastViewModel();
                rdy.SetReady("1.8.0");
                Capture(() => new UpdateToast { DataContext = rdy }, palette, Path.Combine(outDir, "toast_ready_Dark.png"));

                var skillVis = new SkillVisibility(props);
                Capture(() => new SkillSettingsFlyout { DataContext = new SkillSettingsViewModel(skillVis) }, palette, Path.Combine(outDir, "skillsettings_Dark.png"));

                // settings window (hotkeys tab) — verify the HotkeyCaptureBox now matches the dark style.
                string sdir = Path.Combine(Path.GetTempPath(), "waffle_settings_preview");
                Directory.CreateDirectory(sdir);
                var sp = new PropertyHandler(sdir);
                var ssvc = new MeterServices(sp);
                var ssettings = new MeterSettings(sp);
                var spresets = new BuffPresetManager(ssettings, _ => { }, _ => { }); // temp props; no store to update
                var svm = new SettingsViewModel(ssvc, ssettings, new MeterColorTheme(sp), new SkinManager(sp),
                    new OverlayController(new OverlayWindow(), sp), new HotkeyHandler(sp), spresets, new GameOptimizerService()) { SelectedNav = "battle" };
                Capture(() => new SettingsWindow(svm), palette, Path.Combine(outDir, "settings_replay_Dark.png"), fixedSize: true);

                // 미터 화면 탭: the 던전 티어 block (mode combo + two toggles + the 기준표 status line).
                // It sits well below the fold, so scroll the shared ScrollViewer to it before rendering —
                // otherwise the shot is just the top of the tab.
                svm.SelectedNav = "display";
                svm.RefreshTierStatus();
                Capture(
                    () =>
                    {
                        var w = new SettingsWindow(svm);
                        w.Loaded += (_, _) =>
                        {
                            if (w.FindName("ContentScroll") is System.Windows.Controls.ScrollViewer scroll
                                && w.FindName("TierSectionHeader") is FrameworkElement header)
                            {
                                scroll.UpdateLayout();
                                // BringIntoView parks the element at the nearest edge (the bottom here); push on
                                // by a viewport so the whole 던전 티어 block sits under the fold line.
                                header.BringIntoView();
                                scroll.UpdateLayout();
                                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + scroll.ViewportHeight - header.ActualHeight - 8);
                                scroll.UpdateLayout();
                            }
                        };
                        return w;
                    },
                    palette,
                    Path.Combine(outDir, "settings_tier_Dark.png"),
                    fixedSize: true);

                // 캐릭터 관리 tab: 오드 chips beside each character (populate the list directly — the preview has
                // no consent state to enumerate).
                svm.SelectedNav = "stats";
                svm.ConsentCharacters.Add(new ConsentCharacterRow
                {
                    Label = "콰과과 [지그하르트]", SubLabel = "궁성 · 공개", IsPublic = true, CanSetPublic = true, CanRevoke = true,
                    CurrentBadgeVisibility = System.Windows.Visibility.Visible, AetherText = "840(+120)",
                    AetherVisibility = System.Windows.Visibility.Visible,
                });
                svm.ConsentCharacters.Add(new ConsentCharacterRow
                {
                    Label = "띵보 [카이시넬]", SubLabel = "치유성 · 비공개 (익명 집계)", CanRevoke = true,
                    CurrentBadgeVisibility = System.Windows.Visibility.Collapsed, AetherText = "310",
                    AetherVisibility = System.Windows.Visibility.Visible,
                });
                svm.ConsentCharacters.Add(new ConsentCharacterRow
                {
                    Label = "마르틴 [네자칸]", SubLabel = "호법성 · 비공개 (익명 집계)", CanRevoke = true,
                    CurrentBadgeVisibility = System.Windows.Visibility.Collapsed,
                    AetherVisibility = System.Windows.Visibility.Collapsed, // never seen this character's 오드 yet
                });
                Capture(() => new SettingsWindow(svm), palette, Path.Combine(outDir, "settings_characters_Dark.png"), fixedSize: true);
            }
        }

        VerifySettingsTabs(skins, outDir);

        CaptureReplay(LoadRealOrSynthetic(now), Path.Combine(outDir, "replay.png"), "replay.png");
        CaptureReplay(SampleMapReplay(now), Path.Combine(outDir, "replay-map.png"), "replay-map.png");
        CaptureMechanics(outDir);

        Console.WriteLine(outDir);
        app.Shutdown();
    }

    /// <summary>
    /// Boss mechanics: with <c>WAFFLE_REPLAY_FILE</c> pointing at a real recording, freeze the replay on a
    /// mechanic — once while the floor telegraphs it and once as it lands — so the zones (circle / donut /
    /// cone / line, and the markers dropped on players) can be eyeballed against the dungeon map.
    /// </summary>
    private static void CaptureMechanics(string outDir)
    {
        string? file = Environment.GetEnvironmentVariable("WAFFLE_REPLAY_FILE");
        if (file is null || !File.Exists(file))
        {
            return;
        }

        ReplayRecording rec = ReplaySerializer.Deserialize(File.ReadAllText(file));
        ReplaySkillShapes shapes = ReplaySkillShapes.Load();
        Console.WriteLine($"  [info] mechanics preview from {Path.GetFileName(file)}: " +
                          $"{rec.Casts.Count} casts, catalog {shapes.SkillCount} skills");

        // Prefer the most interesting one on screen: the mechanic with the longest telegraph, breaking ties
        // toward a multi-target spread (several zones at once).
        ReplayCast? pick = rec.Casts
            .Where(c => shapes.For(c.SkillCode).Count > 0)
            .OrderByDescending(c => shapes.For(c.SkillCode).Max(z => z.NoticeMs))
            .ThenByDescending(c => c.Targets.Count)
            .FirstOrDefault();

        if (pick is null)
        {
            Console.WriteLine("  [skip] no drawable mechanic in this recording");
            return;
        }

        int notice = shapes.For(pick.SkillCode).Max(z => z.NoticeMs);
        Console.WriteLine($"  [info] skill {pick.SkillCode} at t={pick.TMs}ms notice={notice}ms " +
                          $"targets={pick.Targets.Count} hp={pick.HpFraction:P0}");

        CaptureReplay(rec, Path.Combine(outDir, "replay-mechanic-telegraph.png"),
            "replay-mechanic-telegraph.png", Math.Max(0, pick.TMs - notice / 2.0));
        CaptureReplay(rec, Path.Combine(outDir, "replay-mechanic-impact.png"),
            "replay-mechanic-impact.png", pick.TMs + 150);
    }

    /// <summary>Render the positional-replay window (paused mid-battle so paths/trails show) to PNG.</summary>
    private static void CaptureReplay(ReplayRecording rec, string path, string label, double? startMs = null)
    {
        ReplayWindow? win = null;
        try
        {
            win = new ReplayWindow(rec, autoPlay: false, startMs: startMs ?? rec.DurationMs * 0.5);
            win.Width = 940;
            win.Height = 660;
            win.Left = -10000;
            win.Top = -10000;
            win.Show();
            Drain(win.Dispatcher);

            var content = (FrameworkElement)win.Content;
            int w = (int)Math.Ceiling(content.ActualWidth);
            int h = (int)Math.Ceiling(content.ActualHeight);
            if (w <= 0 || h <= 0)
            {
                Console.WriteLine($"  [skip] {label} measured {w}x{h}");
                return;
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using FileStream fs = File.Create(path);
            encoder.Save(fs);
            Console.WriteLine($"  [ok]   {label} {w}x{h}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [fail] {label}: {ex.Message}");
        }
        finally
        {
            win?.Close();
        }
    }

    /// <summary>A synthetic battle anchored in a real dungeon's WORLD coordinates (무스펠의 성배, boss code
    /// 2301059) so the preview exercises the map-background projection: dots must land on the map art.</summary>
    private static ReplayRecording SampleMapReplay(long now)
    {
        string[] names = { "콘팡", "쌈", "강까", "노까", "빛의사제" };
        string[] jobs = { "권성", "검성", "궁성", "치유성", "호법성" };
        const int dur = 120_000;
        // A pull staged near the center of 무스펠 (world X ~18k, Y ~ -9k), spanning a few thousand units.
        const double baseX = 20000, baseY = -8000;
        var tracks = new List<ReplayTrack>();

        for (int k = 0; k < names.Length; k++)
        {
            var pts = new List<ReplayPoint>();
            double cx = baseX + (k * 700), cy = baseY + ((k % 2) * 900), r = 1200 + (k * 260);
            for (int t = 0; t <= dur; t += 400)
            {
                double a = (t / 1000.0) * (0.4 + (k * 0.05));
                float x = (float)(cx + (Math.Cos(a) * r) + (Math.Sin(t / 7000.0) * 500));
                float y = (float)(cy + (Math.Sin(a) * r) + (Math.Cos(t / 9000.0) * 500));
                float z = (float)(2000 + (200 * Math.Sin((t / 15000.0) + k)));
                pts.Add(new ReplayPoint(t, x, y, z));
            }

            tracks.Add(new ReplayTrack
            {
                Uid = 100 + k,
                Nickname = names[k],
                Server = 2003,
                Job = jobs[k],
                IsSelf = k == 0,
                PartySlot = k + 1,
                Points = pts,
                SourceOpcode = 0x371C,
                SourceOffset = 2,
            });
        }

        var boss = new List<ReplayPoint>();
        for (int t = 0; t <= dur; t += 800)
        {
            boss.Add(new ReplayPoint(t, (float)(baseX + (Math.Sin(t / 20000.0) * 700)), (float)(baseY + (Math.Cos(t / 20000.0) * 700)), 2000f));
        }

        tracks.Add(new ReplayTrack { Uid = 999, Nickname = "칼드릭스", IsTarget = true, Points = boss, SourceOpcode = 0x372F, SourceOffset = 1 });

        return new ReplayRecording
        {
            StartMs = now - dur,
            EndMs = now,
            BossDefeated = true,
            TargetName = "칼드릭스",
            TargetCode = 2301059, // 무스펠의 성배 -> matches the bundled map
            Tracks = tracks,
        };
    }

    /// <summary>Prefer the newest real persisted recording (shows true sampling gaps), else synthetic.</summary>
    private static ReplayRecording LoadRealOrSynthetic(long now)
    {
        try
        {
            string dir = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "waffle_meter.v1.4", "replays");
            if (Directory.Exists(dir))
            {
                FileInfo? f = new DirectoryInfo(dir).GetFiles("*.json").OrderByDescending(x => x.LastWriteTime).FirstOrDefault();
                if (f != null)
                {
                    Console.WriteLine($"  [info] replay.png using real recording {f.Name}");
                    return ReplaySerializer.Deserialize(File.ReadAllText(f.FullName));
                }
            }
        }
        catch
        {
            // fall back to synthetic
        }

        return SampleReplay(now);
    }

    /// <summary>A synthetic battle replay (5 players walking patterns + a drifting boss) for the preview.</summary>
    private static ReplayRecording SampleReplay(long now)
    {
        string[] names = { "콘팡", "쌈", "강까", "노까", "빛의사제" };
        string[] jobs = { "마도성", "검성", "궁성", "치유성", "호법성" };
        const int dur = 120_000;
        var tracks = new List<ReplayTrack>();

        for (int k = 0; k < names.Length; k++)
        {
            var pts = new List<ReplayPoint>();
            double cx = 200 + (k * 45), cy = 220 + ((k % 2) * 70), r = 60 + (k * 16);
            for (int t = 0; t <= dur; t += 500)
            {
                double a = (t / 1000.0) * (0.4 + (k * 0.05));
                float x = (float)(cx + (Math.Cos(a) * r) + (Math.Sin(t / 7000.0) * 30));
                float y = (float)(cy + (Math.Sin(a) * r) + (Math.Cos(t / 9000.0) * 30));
                float z = (float)(50 + (12 * Math.Sin((t / 15000.0) + k)));
                pts.Add(new ReplayPoint(t, x, y, z));
            }

            tracks.Add(new ReplayTrack
            {
                Uid = 100 + k,
                Nickname = names[k],
                Server = 2003,
                Job = jobs[k],
                IsSelf = k == 0,
                PartySlot = k + 1,
                Points = pts,
                SourceOpcode = 0x371C,
                SourceOffset = 2,
            });
        }

        var boss = new List<ReplayPoint>();
        for (int t = 0; t <= dur; t += 1000)
        {
            boss.Add(new ReplayPoint(t, (float)(280 + (Math.Sin(t / 20000.0) * 24)), (float)(260 + (Math.Cos(t / 20000.0) * 24)), 60f));
        }

        tracks.Add(new ReplayTrack { Uid = 999, Nickname = "크로메데", IsTarget = true, Points = boss, SourceOpcode = 0x372F, SourceOffset = 1 });

        return new ReplayRecording
        {
            StartMs = now - dur,
            EndMs = now,
            BossDefeated = true,
            TargetName = "크로메데의 심연",
            TargetCode = 500,
            Tracks = tracks,
        };
    }

    /// <summary>Drive every SettingsViewModel control/command against an ISOLATED settings file and assert
    /// each propagates. Prints a PASS/FAIL report — a one-cycle settings verification.</summary>
    private static void VerifySettings()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "waffle_settings_verify");
        Directory.CreateDirectory(tmp);
        foreach (string f in Directory.GetFiles(tmp))
        {
            File.Delete(f);
        }

        var props = new PropertyHandler(tmp);
        var services = new MeterServices(props);
        var settings = new MeterSettings(props);
        var theme = new MeterColorTheme(props);
        var skin = new SkinManager(props);
        var controller = new OverlayController(new OverlayWindow(), props);
        var hotkeys = new HotkeyHandler(props);
        var presets = new BuffPresetManager(settings, _ => { }, _ => { });
        var vm = new SettingsViewModel(services, settings, theme, skin, controller, hotkeys, presets, new GameOptimizerService());

        int pass = 0, fail = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "ok  " : "FAIL")}] {name}");
            if (ok)
            {
                pass++;
            }
            else
            {
                fail++;
            }
        }

        Console.WriteLine("=== settings verification cycle ===");

        vm.DisplayMode = "amount_percent"; Check("DisplayMode", settings.DisplayMode == "amount_percent" && props.GetProperty("displayMode") == "amount_percent");
        vm.DamageValueMode = "total"; Check("DamageValueMode", settings.UseTotalDamage);
        vm.ContributionMode = "entireContribution"; Check("ContributionMode", settings.UseEntireContribution);
        vm.NameDisplay = "me_only"; Check("NameDisplay", settings.NameDisplayMode == NameDisplay.MeOnly);
        vm.TargetInfoDisplayMode = "percent"; Check("TargetInfoDisplayMode", settings.TargetInfoDisplayMode == "percent");
        vm.BarStyle = "bar"; Check("BarStyle", settings.BarStyle == "bar" && props.GetProperty("barStyle") == "bar");
        vm.FontFamily = "Pretendard"; Check("FontFamily", settings.FontFamily == "Pretendard");

        // Font picker cards. The value that matters is FontCardViewModel.Value: it is the string in
        // settings.properties, and it is a font's internal family name, not a tidy label.
        Check($"FontCards built ({vm.FontCards.Count})", vm.FontCards.Count >= 15);
        Check("FontCards values are unique", vm.FontCards.Select(c => c.Value).Distinct(StringComparer.Ordinal).Count() == vm.FontCards.Count);
        Check("FontCards resolve a preview face", vm.FontCards.All(c => c.Preview is not null));
        Check("card selection mirrors the setting", vm.CardFontSelection == "Pretendard"
            && vm.FontCards.Single(c => c.Value == "Pretendard").IsSelected);

        vm.CardFontSelection = "Freesentation";
        Check("selecting a card writes through", settings.FontFamily == "Freesentation"
            && props.GetProperty("fontFamily") == "Freesentation");

        // THE regression this feature can introduce: two Selectors (cards + system dropdown) over one setting.
        // A Selector whose bound value is absent from ITS list coerces to null and writes that null back, which
        // would erase the font the other one just set. Picking a system font must leave the setting intact.
        vm.SystemFontSelection = "Segoe UI";
        Check("system font applies", settings.FontFamily == "Segoe UI");
        Check("card grid shows no selection for a system font", vm.CardFontSelection is null
            && vm.FontCards.All(c => !c.IsSelected));
        vm.CardFontSelection = null;   // the coercion WPF performs
        Check("null from the card grid does not erase the font", settings.FontFamily == "Segoe UI");
        vm.SystemFontSelection = null; // and the mirror case
        Check("null from the system dropdown does not erase the font", settings.FontFamily == "Segoe UI");
        Check("현재 글꼴 line names the system font", vm.CurrentFontStatus.Contains("Segoe UI") && vm.CurrentFontStatus.Contains("시스템"));

        vm.FontFamily = SettingsViewModel.DefaultFontFamily;
        Check("default font is a real card", vm.FontCards.Any(c => c.Value == SettingsViewModel.DefaultFontFamily && c.IsDefault));
        Check("system font list is populated", vm.SystemFontFamilies.Count > 20);

        // Every offered font must actually resolve. A "#name" miss does NOT come back empty — WPF returns the
        // DEFAULT family fully populated, so a broken option renders as Arial and looks like a design choice.
        // 'Freesentation' shipped exactly that way (the files register as 'Freesentation 4/6/7').
        string[] broken = vm.FontCards
            .Where(c => c.Value != "Malgun Gothic" && FontResolver.Classify(c.Value) != FontResolver.FontOrigin.Bundled)
            .Select(c => c.Value)
            .ToArray();
        Check($"every bundled card resolves to a bundled face{(broken.Length > 0 ? " — 실패: " + string.Join(", ", broken) : string.Empty)}", broken.Length == 0);
        Check("legacy 'Freesentation' maps to a real face",
            FontResolver.Resolve("Freesentation").FamilyNames.Values.Any(v => v.StartsWith("Freesentation", StringComparison.Ordinal)));
        Check("'맑은 고딕' resolves to the system face, not the bundled fallback",
            FontResolver.Classify("Malgun Gothic") == FontResolver.FontOrigin.System
            && FontResolver.Resolve("Malgun Gothic").FamilyNames.Values.Any(v => v.Contains("Malgun", StringComparison.OrdinalIgnoreCase)));
        Check("an unknown name falls through to the safe chain",
            FontResolver.Classify("ZZZ Not A Font") == FontResolver.FontOrigin.System);
        vm.RowHeight = 50; Check("RowHeight", settings.RowHeight == 50 && props.GetProperty("rowHeight") == "50");
        vm.MeterOpacity = 0.7; Check("MeterOpacity", Math.Abs(settings.MeterOpacity - 0.7) < 0.001);

        vm.IsMinimal = true; Check("IsMinimal", settings.IsMinimal);
        vm.ShowCombatTimerInMinimal = false; Check("ShowCombatTimerInMinimal", !settings.ShowCombatTimerInMinimal);
        vm.ShowTargetInfoInMinimal = false; Check("ShowTargetInfoInMinimal", !settings.ShowTargetInfoInMinimal);
        vm.MultiMonitorMode = true; Check("MultiMonitorMode", settings.MultiMonitorMode);
        vm.IsAutoHide = false; Check("IsAutoHide", !controller.IsAutoHide);
        vm.TaskbarMode = true; Check("TaskbarMode", settings.TaskbarMode && controller.TaskbarMode);

        vm.CloseAction = "tray"; Check("CloseAction", settings.CloseAction == "tray");
        vm.CaptureBackend = "npcap"; Check("CaptureBackend", settings.CaptureBackend == "npcap");
        vm.ServerIp = "10.0.0.0/8"; vm.ServerPort = "7777"; vm.SaveServer();
        Check("SaveServer", props.GetProperty("server.ip") == "10.0.0.0/8" && props.GetProperty("server.port") == "7777");

        vm.Skin = "light"; Check("Skin (light)", skin.Current == "light" && skin.IsLight);

        theme.UserBarFrom = "#FF112233"; Check("Theme color set", theme.UserBarFrom == "#FF112233");
        vm.ResetTheme(); Check("ResetTheme", theme.UserBarFrom != "#FF112233");

        bool updateAsked = false; vm.CheckUpdateRequested = () => updateAsked = true; vm.CheckForUpdate(); Check("CheckForUpdate", updateAsked);
        string? resetWhich = null; vm.ResetPositionRequested = w => resetWhich = w;
        vm.ResetMeterPosition(); Check("ResetMeterPosition", resetWhich == "meter");
        vm.ResetJoinPosition(); Check("ResetJoinPosition", resetWhich == "join");
        vm.ResetHistoryPosition(); Check("ResetHistoryPosition", resetWhich == "history");

        vm.ToggleLogging(); Check("Logging start", services.DebugLogger.IsRunning);
        vm.ToggleLogging(); Check("Logging stop", !services.DebugLogger.IsRunning);

        vm.PendingReset = new HotkeyCombo(0, 0x71); vm.Commit(); // F2
        Check("Hotkey commit", hotkeys.Reset?.VkCode == 0x71);
        vm.PendingReset = null; vm.Commit(); // 미지정 round-trip
        Check("Hotkey unassign", hotkeys.Reset is null);

        // 닉네임 효과. The property that must never regress: with the feature off the row is painted with the
        // SAME brush instance as before the feature existed, so "off" is pixel-identical rather than merely similar.
        var offVm = new OverlayViewModel("test", settings, theme, () => false);
        offVm.SetNameFxRoster(SampleNameFxRoster(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
        settings.NameFxMode = "off";
        offVm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Check("nameFx off: name keeps its own brush", offVm.Rows.All(r => ReferenceEquals(r.NameFillBrush, r.NameBrush)));

        settings.NameFxMode = "animated";
        offVm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Check("nameFx animated: granted rows get an effect brush",
            offVm.Rows.Any(r => !ReferenceEquals(r.NameFillBrush, r.NameBrush)));

        settings.NameFxShowSelf = false;
        settings.NameFxShowOthers = false;
        offVm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Check("nameFx self/others off: nobody is decorated", offVm.Rows.All(r => ReferenceEquals(r.NameFillBrush, r.NameBrush)));
        settings.NameFxShowSelf = true;
        settings.NameFxShowOthers = true;

        // 기능의 요점: 부여는 캐릭터에 붙고 명단은 모두가 같은 것을 받으므로, 같은 전투에 있는 다른
        // 미터 사용자에게도 그 사람이 고른 연출이 그대로 보인다. 위 검사는 "누군가 칠해진다"까지만
        // 보므로 내 행 하나만 칠해져도 통과한다 — 그건 이 요구를 전혀 증명하지 못한다.
        offVm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        RowViewModel[] others = offVm.Rows.Where(r => !r.IsUser).ToArray();
        Check($"남의 캐릭터도 그 사람 명단대로 칠해진다 ({others.Count(r => !ReferenceEquals(r.NameFillBrush, r.NameBrush))}/{others.Length}행)",
            others.Length > 0 && others.Any(r => !ReferenceEquals(r.NameFillBrush, r.NameBrush)));
        Check("남의 게이지 스킨도 그대로 보인다", offVm.Rows.Any(r => !r.IsUser && IsGaugeSkin(r.GaugeBrush)));

        // '적용 대상' 콤보. 보는 쪽 취향일 뿐이고 남에게 무엇이 보이는지는 바꾸지 않는다.
        vm.NameFxScope = "self";
        offVm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Check("'내 캐릭터만': 남의 행은 원래 색으로 돌아간다",
            offVm.Rows.Where(r => !r.IsUser).All(r => ReferenceEquals(r.NameFillBrush, r.NameBrush)));
        Check("'내 캐릭터만'이어도 내 행은 남는다",
            offVm.Rows.Where(r => r.IsUser).All(r => r.NameFillBrush is not null));

        vm.NameFxScope = "all";
        Check("적용 대상은 기존 두 키 위에 얹혀 있다",
            settings.NameFxShowSelf && settings.NameFxShowOthers && vm.NameFxScope == "all");
        offVm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Check("'모든 캐릭터'로 되돌리면 남의 행이 다시 칠해진다",
            offVm.Rows.Any(r => !r.IsUser && !ReferenceEquals(r.NameFillBrush, r.NameBrush)));

        settings.NameFxMode = "static";
        offVm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Check("nameFx static: effects survive but nothing is animated",
            offVm.Rows.Any(r => !ReferenceEquals(r.NameFillBrush, r.NameBrush)));

        // 랭커 게이지 스킨. The sample roster grants one only to ranker-family characters, so "a row's fill
        // changed" is also a check that the grant plumbing carries the optional field at all.
        settings.NameFxMode = "animated";
        settings.NameFxGauge = true;
        offVm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        bool IsGaugeSkin(System.Windows.Media.Brush b) =>
            NameFxPalette.GaugeSkins.Any(g => ReferenceEquals(NameFxPalette.GaugeBrush(g.Id, false), b));

        Check("gauge skin: a granted row's DPS bar uses the skin", offVm.Rows.Any(r => IsGaugeSkin(r.GaugeBrush)));

        // ...and the 3px accent rail must NOT. It has no visibility gate and is the only thing that tells your
        // own row and each job apart, so a gauge skin there would compress four stops into three pixels and
        // erase that signal — which is why the bar brush is a separate field from FillBrush at all.
        Check("gauge skin never reaches the accent rail", offVm.Rows.All(r => !IsGaugeSkin(r.FillBrush)));

        // 게이지 스킨의 계열 분리. 자격 경계라 "목록이 비지 않는다"로는 아무것도 증명되지 않는다 — 한쪽
        // 계열만 갖고도 통과하고, 실제로 게이지가 랭커 전용이던 시절의 코드는 후원자에게 빈 목록을 주면서
        // 랭커에게는 모든 스킨을 줬다. 양방향으로 못 박는다.
        string[] SupporterGaugeIds = NameFxPalette.GaugeSkins
            .Where(e => e.Kind == NameFxPalette.NameFxKind.Supporter).Select(e => e.Id).ToArray();
        string[] RankerGaugeIds = NameFxPalette.GaugeSkins
            .Where(e => e.Kind == NameFxPalette.NameFxKind.Ranker).Select(e => e.Id).ToArray();
        string[] GaugeIdsFor(string kind) => NameFxPalette.GaugeChoicesFor(kind).Select(e => e.Id).ToArray();

        Check($"후원자 게이지 스킨이 존재한다 ({SupporterGaugeIds.Length}종)", SupporterGaugeIds.Length > 0);
        Check("후원자에게는 후원자 게이지만 제시된다", GaugeIdsFor("supporter").SequenceEqual(SupporterGaugeIds));
        Check("랭커에게는 랭커 게이지만 제시된다", GaugeIdsFor("ranker").SequenceEqual(RankerGaugeIds));
        Check("both 는 두 계열을 모두 받는다",
            GaugeIdsFor("both").Length == SupporterGaugeIds.Length + RankerGaugeIds.Length);
        Check("자격이 없으면 게이지도 없다", GaugeIdsFor("none").Length == 0);

        // 게이지 id 와 닉네임 연출 id 는 서로의 슬롯에 들어가면 안 된다 — 게이지 id 가 닉네임 자리에
        // 앉으면 글자 하나에 막대 크기 그라디언트가 칠해진다(그래서 술어가 둘이다).
        Check("게이지 id 는 닉네임 연출로 통과하지 않는다",
            NameFxPalette.GaugeSkins.All(e => !NameFxPalette.IsKnownNameEffect(e.Id) && NameFxPalette.IsKnownGauge(e.Id)));
        Check("닉네임 연출 id 는 게이지로 통과하지 않는다",
            NameFxPalette.NameEffects.All(e => !NameFxPalette.IsKnownGauge(e.Id) && NameFxPalette.IsKnownNameEffect(e.Id)));

        // 스킨마다 다크·라이트 두 벌이 다 있어야 한다. 한쪽이 비면 그 스킨은 특정 스킨에서만 조용히
        // 기본 게이지로 되돌아간다 — 화면을 그 테마로 열어보기 전에는 드러나지 않는 종류의 결함이다.
        Check("모든 게이지 스킨이 다크·라이트 브러시를 갖는다",
            NameFxPalette.GaugeSkins.All(e =>
                NameFxPalette.GaugeBrush(e.Id, false) is not null && NameFxPalette.GaugeBrush(e.Id, true) is not null));

        // A skin at the stock 0.3 fill opacity reads as "the bar is a bit murky", not as a mark — that is the
        // state the first cut shipped in. Only skinned rows get the bump; everyone else stays at 0.3 exactly.
        Check("skinned rows raise the fill opacity",
            offVm.Rows.Where(r => IsGaugeSkin(r.GaugeBrush)).All(r => r.GaugeOpacity > 0.4));
        Check("un-skinned rows keep the stock fill opacity",
            offVm.Rows.Where(r => !IsGaugeSkin(r.GaugeBrush)).All(r => Math.Abs(r.GaugeOpacity - 0.3) < 0.001));

        settings.NameFxGauge = false;
        offVm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Check("gauge skin off: bars go back to the contribution/job colours",
            offVm.Rows.All(r => !IsGaugeSkin(r.GaugeBrush) && ReferenceEquals(r.GaugeBrush, r.FillBrush)));
        settings.NameFxGauge = true;

        Check("effect ids are unique", NameFxPalette.All.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() == NameFxPalette.All.Length);
        // '색상만' must keep the character's OWN effect, just without motion. Collapsing every ranker onto one
        // shared still entry is what made two of them indistinguishable in the picker.
        Check("every animated effect has a still form",
            NameFxPalette.All.Where(e => e.Animated).All(e => !NameFxPalette.StillVariant(e.Id, false).IsNone));
        Check("still form keeps the effect's identity",
            NameFxPalette.All.Where(e => e.Animated).All(e => NameFxPalette.StillVariant(e.Id, false).Id == e.Id));
        Check("still form does not animate",
            NameFxPalette.All.Where(e => e.Animated).All(e => !NameFxPalette.StillVariant(e.Id, false).Animated));
        Check("an already-still effect is its own still form",
            NameFxPalette.StillVariant("crisp", false).Id == "crisp");

        // The complaint that started this: two ranker marks that look the same are one mark. Compare every
        // ranker nickname pair as rendered pixels rather than trusting the hex values to look different.
        NameFxPalette.Effect[] rankerNames = NameFxPalette.NameEffects
            .Where(e => e.Kind == NameFxPalette.NameFxKind.Ranker).ToArray();
        for (int i = 0; i < rankerNames.Length; i++)
        {
            for (int j = i + 1; j < rankerNames.Length; j++)
            {
                double d = MeanChannelDistance(
                    PaintStrip(NameFxPalette.For(rankerNames[i].Id, false).NameFill, 0.0),
                    PaintStrip(NameFxPalette.For(rankerNames[j].Id, false).NameFill, 0.0));
                Check($"'{rankerNames[i].Id}' vs '{rankerNames[j].Id}' are visibly different ({d:F0}/255)", d >= 18);
            }
        }
        Check("unknown effect id draws nothing", NameFxPalette.For("nope", false).IsNone);

        vm.NameFxBrightnessPercent = 130; vm.CommitNameFxBrightness();
        vm.NameFxBrightnessPercent = 70; vm.CommitNameFxBrightness();
        vm.NameFxBrightnessPercent = 100; vm.CommitNameFxBrightness();
        Check("brightness round-trips", settings.NameFxBrightnessPercent == 100);
        vm.NameFxSpeedPercent = 200; Check("speed round-trips", settings.NameFxSpeedPercent == 200);
        Check("preview strip covers the nickname catalogue", vm.NameFxSamples.Count == NameFxPalette.NameEffects.Length);
        Check("gauge strip covers the gauge catalogue", vm.GaugeSkinSamples.Count == NameFxPalette.GaugeSkins.Length);
        // 게이지 카탈로그는 두 계열이 다 차 있어야 한다. "랭커 전용"이던 시절의 단정을 그대로 두면 후원자
        // 게이지를 추가하는 순간 빨개지는데, 그게 이 검사가 원한 사실은 아니다 — 원한 건 '계열이 실제로
        // 갈린다'이지 '한쪽만 있다'가 아니다.
        Check("게이지 카탈로그에 두 계열이 다 있다",
            NameFxPalette.GaugeSkins.Any(e => e.Kind == NameFxPalette.NameFxKind.Supporter)
            && NameFxPalette.GaugeSkins.Any(e => e.Kind == NameFxPalette.NameFxKind.Ranker));
        Check("게이지 견본에 계열 라벨이 붙는다",
            vm.GaugeSkinSamples.All(s => s.Kind is "후원자" or "랭커")
            && vm.GaugeSkinSamples.Any(s => s.Kind == "후원자")
            && vm.GaugeSkinSamples.Any(s => s.Kind == "랭커"));
        Check("gauge and nickname catalogues do not overlap",
            !NameFxPalette.NameEffects.Any(n => NameFxPalette.GaugeSkins.Any(g => g.Id == n.Id)));
        Check("a nickname effect id is not a gauge brush", NameFxPalette.GaugeBrush("syrup", false) is null);
        Check("a gauge id resolves to a brush", NameFxPalette.GaugeBrush("prism", false) is not null);

        // ⚠ The check that would have caught the shipped-frozen gauge. `Brush.Transform` applies in ABSOLUTE
        // (DIP) space no matter what MappingMode says; only `RelativeTransform` works in the 0~1 box space. A
        // relative-mapped brush translated by 1.0 through the wrong slot moves ONE PIXEL and renders as a still
        // gradient. Nothing about that fails, throws, or looks wrong in a single frame — so paint the brush at
        // two phases and demand the pixels differ.
        foreach (bool light in new[] { false, true })
        {
            foreach (NameFxPalette.Effect e in NameFxPalette.All.Where(x => x.Animated))
            {
                Brush b = NameFxSheen.BrushFor(e.Id, light);
                byte[] a = PaintStrip(b, 0.0);
                byte[] c = PaintStrip(b, 0.25);
                byte[] loop = PaintStrip(b, 1.0);
                Check($"'{e.Id}'{(light ? " (light)" : string.Empty)} actually travels",
                    !a.AsSpan().SequenceEqual(c));
                // 한 주기 뒤 원점으로 정확히 닫혀야 이음매가 안 보인다. 다만 비교는 렌더된 픽셀이라
                // 채널 1 정도의 반올림 차는 늘 난다 — 그건 이음매가 아니다. 값을 찍어 두어, 임계에
                // 걸렸을 때 '얼마나' 어긋났는지가 바로 보이게 한다.
                double seam = MeanChannelDistance(a, loop);
                Check($"'{e.Id}'{(light ? " (light)" : string.Empty)} loops seamlessly (이음매 {seam:F2}/255)",
                    seam < 1.0);
            }
        }

        NameFxSheen.SetPreviewPhase(0);

        // The preview strip needs its OWN claim on the sweep clock. Grants come from the server, so a user
        // deciding about this setting has no decorated row anywhere — row demand is zero and the timer is
        // stopped, which is exactly how the strip shipped frozen the first time.
        // 앞의 offVm.Update 들이 행 수요를 남겨 놓는다. 행 수요가 남아 있으면 클럭은 당연히 계속 도므로,
        // 미리보기 수요만 따로 보려면 먼저 비워야 한다.
        NameFxSheen.SetDemand(0, false, 100);
        vm.NameFxSpeedPercent = 100;
        vm.SelectedNav = "theme";
        vm.NameFxMode = "animated";
        Check("preview on the colour tab runs the sweep clock", NameFxSheen.IsRunning);
        vm.NameFxMode = "static";
        Check("'색상만' visibly stops the preview", !NameFxSheen.IsRunning);
        vm.NameFxMode = "animated";
        vm.SelectedNav = "display";
        Check("leaving the tab stops the preview", !NameFxSheen.IsRunning);
        vm.SelectedNav = "theme";
        Check("returning to the tab restarts it", NameFxSheen.IsRunning);
        vm.StopNameFxPreview();
        Check("closing the settings window releases the clock", !NameFxSheen.IsRunning);

        // ---- 설정 백업 · 공유 ----
        // The whole feature is "write a file, then make the running app agree with it". Both halves are checked
        // here rather than in the unit tests, because only the WPF side owns the reload hooks.
        var skills = new SkillVisibility(props);
        vm.BundleApplier = new SettingsBundleApplier(services, settings, theme, skin, controller, hotkeys, presets, skills);
        bool combat = false;
        vm.IsCombatActive = () => combat;

        settings.RowHeight = 41;
        skin.Apply("light");
        vm.ExportFull();
        string fullCode = vm.LastExportedCode;
        Check("export produces a code", fullCode.StartsWith("WM1.", StringComparison.Ordinal) && fullCode.Length > 40);
        Console.WriteLine($"        (전체 코드 {fullCode.Length}자, 설정 개수는 위 export 상태 기준)");

        vm.ImportText = fullCode;
        vm.PreviewPastedCode();
        Check("a code exported and re-read on the same machine changes nothing",
            vm.ImportChanges.Count == 0 && !vm.CanApplyImport);

        // The real journey: settings drift, then the code puts them back — including through the live models,
        // which is what "안 바뀌는데요" would be about.
        settings.RowHeight = 60;
        skin.Apply("dark");
        vm.ImportText = fullCode;
        vm.PreviewPastedCode();
        Check($"drifted settings show up as changes ({vm.ImportChanges.Count})", vm.ImportChanges.Count >= 2);
        Check("preview writes nothing", settings.RowHeight == 60 && skin.Current == "dark");

        combat = true;
        vm.PreviewPastedCode();
        Check("전투 중 적용 차단", !vm.CanApplyImport && vm.CombatBlockVisibility == Visibility.Visible);
        vm.ApplyPreviewedCode();
        Check("전투 중 적용 버튼은 아무것도 쓰지 않는다", settings.RowHeight == 60);
        combat = false;

        vm.PreviewPastedCode();
        vm.ApplyPreviewedCode();
        Check("import restores the stored value", props.GetProperty("rowHeight") == "41");
        Check("the live model catches up without a restart", settings.RowHeight == 41);
        Check("the skin catches up without a restart", skin.Current == "light" && skin.IsLight);
        Check("applying clears the pasted code and its preview",
            vm.ImportText.Length == 0 && vm.ImportPreviewVisibility == Visibility.Collapsed);
        Check("되돌리기 버튼이 나타난다", vm.UndoVisibility == Visibility.Visible);

        vm.UndoLastImport();
        Check("undo puts back what was there before the import", settings.RowHeight == 60 && skin.Current == "dark");

        // A code handed over in chat arrives wrapped in whatever the sender typed around it.
        vm.ImportText = "이거 내 세팅이야 " + fullCode + " 한번 써봐";
        vm.PreviewPastedCode();
        Check("a code pasted out of a chat message is still read",
            vm.ImportPreviewVisibility == Visibility.Visible);

        vm.ImportText = fullCode[..^3] + "aaa";
        vm.PreviewPastedCode();
        Check("a truncated code is refused, with a reason",
            vm.ImportPreviewVisibility == Visibility.Collapsed && vm.BundleStatus.Contains("손상"));
        Check("a refused code writes nothing", settings.RowHeight == 60);

        vm.ImportText = "그냥 아무 문장";
        vm.PreviewPastedCode();
        Check("text that holds no code says so", vm.BundleStatus.Contains("WM1."));

        // 디자인 코드가 알림 설정을 건드리면 "외형만 보여주려던" 사람이 남의 알람을 덮어쓴다.
        props.SetProperty("alarms.shugoEnabled", "true");
        vm.ExportDesign();
        string designCode = vm.LastExportedCode;
        props.SetProperty("alarms.shugoEnabled", "false");
        Check("디자인 코드는 알림을 실어 나르지 않는다",
            SettingsBundleCodec.TryDecode(designCode, out SettingsBundle designBundle, out _)
            && !designBundle.Data.ContainsKey("alarms.shugoEnabled"));

        vm.ImportText = designCode;
        vm.PreviewPastedCode();
        vm.ApplyPreviewedCode();
        Check("디자인 코드를 적용해도 알림은 그대로", props.GetProperty("alarms.shugoEnabled") == "false");

        // 파일 경로. 코드가 채팅 글자수 제한을 넘으면 클립보드로는 못 넘긴다.
        string codeFile = Path.Combine(tmp, "shared.wmset");
        File.WriteAllText(codeFile, "# 친구가 준 세팅" + Environment.NewLine + fullCode + Environment.NewLine);
        vm.LoadCodeFromText(File.ReadAllText(codeFile));
        Check("메모가 섞인 파일에서도 코드를 읽는다", vm.ImportPreviewVisibility == Visibility.Visible);
        Check("파일 이름 제안은 날짜가 붙는다", vm.SuggestedFileName(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero))
            == "waffle-settings-20260815.wmset");

        vm.ImportText = string.Empty;

        // 후원자 목록 상태선. 서버가 없어도 "받지 않았어요"로 말이 되어야 하고, 쿨다운에 먹힌 클릭은
        // 침묵하면 안 된다 — 6시간 주기라 죽은 버튼으로 보이는 게 바로 신고 대상이다.
        vm.RefreshNameFxStatus();
        Check($"후원자 목록 상태선이 비어 있지 않다 ({vm.NameFxStatus})", vm.NameFxStatus.Length > 0);
        Check("첫 갱신 요청은 통과한다", vm.RequestNameFxRefresh());
        vm.SetNameFxNotice("테스트 공지");
        vm.RefreshNameFxStatus();
        Check("공지는 2.5초 폴링에 지워지지 않는다", vm.NameFxStatus == "테스트 공지");
        Check("쿨다운 안의 재요청은 거절된다", !vm.RequestNameFxRefresh());

        // 내 연출 고르기. 선택지는 **서버가 명단의 k 로 내려보낸 자격**에서만 나온다 — 미터에
        // "누가 무슨 자격인가" 규칙을 두면 서버와 두 벌이 되고, 갈리는 순간 사용자는 고를 수 있는데
        // 저장이 거부되는 상태를 만난다.
        // 자격이 없어도 섹션은 남긴다 — 무엇이 있는지 보이지 않으면 "받으면 뭐가 생기는지" 를 알 수
        // 없다. 대신 고를 수 없게 하고, 왜 못 고르는지를 상태줄이 말한다.
        vm.RefreshMyNameFx();
        Check("자격이 없으면 고를 수 없다(숨지는 않는다)",
            vm.MyEffectChoices.Count == 0 && !vm.MyFxEnabled && vm.MyFxOpacity < 1.0);
        Check($"왜 못 고르는지 말해 준다 ({vm.MyFxStatus})", vm.MyFxStatus.Length > 0);

        // 고를 수 없는 상태에서 보내면 로컬에서 막는다. 서버까지 보내 401/403 을 받아 오는 건
        // 사용자에게 아무 도움이 안 되고, 이 하네스에는 네트워크가 없어 그대로 두면 예외가 난다.
        // 서버는 403 을 두 가지로 쓴다 — 소유 증명 실패와 자격 없음. 자격 유무 자체가 정보라
        // 서버가 일부러 같은 상태 코드로 주고, 본문 error 로만 갈린다. 상태 코드만 보면 회수된
        // 부여를 "전투를 올린 기록이 없다" 로 잘못 안내한다(명단이 최대 1시간 낡아 실제로 닿는다).
        Check("본문 error 코드를 읽는다",
            SettingsViewModel.ErrorCodeOf("""{"ok":false,"error":"not_entitled"}""") == "not_entitled"
            && SettingsViewModel.ErrorCodeOf("""{"ok":false,"error":"not_your_character"}""") == "not_your_character");
        Check("본문이 없거나 깨져도 안 터진다",
            SettingsViewModel.ErrorCodeOf(null) == string.Empty
            && SettingsViewModel.ErrorCodeOf("not json") == string.Empty
            && SettingsViewModel.ErrorCodeOf("{}") == string.Empty);

        string refusal = vm.SubmitMyNameFx("syrup", null);
        Check($"자격 없이 보내면 로컬에서 막는다 ({refusal})",
            refusal.Contains("자격", StringComparison.Ordinal) || refusal.Contains("인식", StringComparison.Ordinal));

        Check("후원자는 후원자 계열 4종만 고른다",
            NameFxPalette.ChoicesFor("supporter").Count == 4
            && NameFxPalette.ChoicesFor("supporter").All(e => e.Kind == NameFxPalette.NameFxKind.Supporter));
        Check("랭커는 랭커 계열 5종만 고른다",
            NameFxPalette.ChoicesFor("ranker").Count == 5
            && NameFxPalette.ChoicesFor("ranker").All(e => e.Kind == NameFxPalette.NameFxKind.Ranker));
        Check("'둘 다'는 아홉 개 전부 고른다", NameFxPalette.ChoicesFor("both").Count == 9);
        Check("모르는 자격은 아무것도 못 고른다", NameFxPalette.ChoicesFor("nope").Count == 0);

        // 게이지도 닉네임과 같은 자격 규칙을 따른다 — 자기 계열 것만 고를 수 있고 'both' 는 합집합이다.
        // ⚠️ 자격 판정과 선택 수락은 **서버**가 한다. 후원자 게이지는 통계웹이 supporter 자격에 대해
        // gaugeId 를 받아 주고 새 id 두 개를 알아야 실제로 저장된다 — 그 전까지 이 목록은 골라도
        // 거절당한다(SubmitChoice 는 서버 응답만 신뢰한다).
        VerifyGaugeFx(settings, offVm, Check);

        Check("게이지도 계열별로 갈린다",
            NameFxPalette.GaugeChoicesFor("supporter").All(e => e.Kind == NameFxPalette.NameFxKind.Supporter)
            && NameFxPalette.GaugeChoicesFor("ranker").All(e => e.Kind == NameFxPalette.NameFxKind.Ranker)
            && NameFxPalette.GaugeChoicesFor("both").Count
                == NameFxPalette.GaugeChoicesFor("supporter").Count + NameFxPalette.GaugeChoicesFor("ranker").Count);

        // 선택 목록에 게이지 id 가 섞이면 닉네임 자리에서 바 크기 그라디언트가 이름을 가로지른다.
        Check("닉네임 선택지에 게이지가 섞이지 않는다",
            NameFxPalette.ChoicesFor("both").All(e => !e.IsGauge));

        // Cancel 취소 계약. 여기 있는 setter 는 전부 즉시 파일에 쓰므로, Snapshot 에서 빠진 토글은
        // "저장 안 됨"이 아니라 "저장됐고 되돌릴 수 없음"이 된다.
        settings.TierShow = true; settings.TierEffects = "static"; settings.TierShowOthers = true;
        settings.TierShowSelfChip = true; settings.NameFxMode = "animated"; settings.ShowAetherStatus = true;

        // 창이 열릴 때 스냅샷을 뜬다 — 그 순간을 그대로 재현하려면 뷰모델을 새로 만드는 수밖에 없다.
        var cancelVm = new SettingsViewModel(services, settings, theme, skin, controller, hotkeys, presets, new GameOptimizerService());

        settings.TierShow = false; settings.TierEffects = "off"; settings.TierShowOthers = false;
        settings.TierShowSelfChip = false; settings.NameFxMode = "off"; settings.ShowAetherStatus = false;
        cancelVm.Revert();
        Check("취소가 티어 장식 4키를 되돌린다",
            settings.TierShow && settings.TierEffects == "static" && settings.TierShowOthers && settings.TierShowSelfChip);
        Check("취소가 닉네임 효과·오드 표시도 되돌린다", settings.NameFxMode == "animated" && settings.ShowAetherStatus);

        vm.ResetDefaults();
        Check("ResetDefaults", settings.DisplayMode == "dps_percent" && settings.RowHeight == 36 && skin.Current == "dark");

        bool consentOk = true;
        try { vm.ConsentAccepted = false; vm.ApplyConsent(); } catch { consentOk = false; }
        Check("ApplyConsent (no crash)", consentOk);

        Console.WriteLine($"=== settings: {pass} passed, {fail} failed ===");

        MeasureFontResolve(vm);
        MeasureOverlayFrame(settings, theme, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// The settings window's tab contract, and the screenshot baseline the IA re-shuffle gets diffed against.
    /// <para>The nav rail's <c>ListBoxItem.Tag</c> literals and the content panels' <c>ConverterParameter</c>
    /// literals are two separate string tables that must agree exactly. When they drift,
    /// <c>StringEqualsToVisibilityConverter</c> collapses EVERY panel and the right-hand side of the window
    /// goes blank — a failure neither the compiler nor a unit test catches, because both halves are XAML
    /// strings and the converter treats "no match" as a legitimate answer.</para>
    /// <para>So walk them: for each nav Tag, exactly one panel under <c>ContentScroll</c> must be visible.
    /// This is deliberately written against the CURRENT shape (parameter literals, no panel Tags) so it keeps
    /// working across the Phase 0 MultiBinding conversion — it only ever asks "what does the window show".</para>
    /// </summary>
    /// <summary>Every element under a root, so a check can ask "does this screen say X" without knowing where
    /// the text lives. Walking the tree beats naming each TextBlock — a check that needs an x:Name is a check
    /// nobody adds.</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (DependencyObject nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static void VerifySettingsTabs(IReadOnlyDictionary<string, ResourceDictionary> skins, string outDir)
    {
        Console.WriteLine("=== settings tab contract ===");

        string tmp = Path.Combine(Path.GetTempPath(), "waffle_settings_tabs");
        Directory.CreateDirectory(tmp);
        var props = new PropertyHandler(tmp);
        var settings = new MeterSettings(props);
        var presets = new BuffPresetManager(settings, _ => { }, _ => { });
        var vm = new SettingsViewModel(new MeterServices(props), settings, new MeterColorTheme(props),
            new SkinManager(props), new OverlayController(new OverlayWindow(), props), new HotkeyHandler(props),
            presets, new GameOptimizerService());

        int pass = 0, fail = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "ok  " : "FAIL")}] {name}");
            if (ok) { pass++; } else { fail++; }
        }

        // Only Dark and Light get shots: those two are the pair that actually disagree about contrast
        // (Skin.AccentSoft over the near-white Light RowBg is where selection states go invisible).
        // Midnight/Slate are Dark's palette with different hues and have never regressed independently.
        bool contractChecked = false;
        foreach (string skinName in new[] { "Dark", "Light" })
        {
            SettingsWindow? window = null;
            try
            {
                // 팔레트만 주입하면 VM 은 스킨이 바뀐 걸 모른다 — 닉네임 효과 미리보기가 라이트 화면에서도
                // 다크 변형을 그려, 라이트 색이 배경에 묻히는지 판단할 수 없었다(실제로 그렇게 오판했다).
                vm.Skin = skinName == "Light" ? "light" : "dark";
                window = new SettingsWindow(vm);
                window.Resources.MergedDictionaries.Insert(0, skins[skinName]);
                window.Left = -10000;
                window.Top = -10000;
                window.Show();
                Drain(window.Dispatcher);

                var nav = FindVisualChild<System.Windows.Controls.ListBox>(window);
                var scroll = window.FindName("ContentScroll") as System.Windows.Controls.ScrollViewer;
                var panelHost = scroll?.Content as System.Windows.Controls.Grid;

                if (nav is null || panelHost is null)
                {
                    Check($"{skinName}: nav ListBox + ContentScroll>Grid found", false);
                    continue;
                }

                List<string> navKeys = nav.Items
                    .OfType<System.Windows.Controls.ListBoxItem>()
                    .Select(i => i.Tag as string)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Select(t => t!)
                    .ToList();

                if (!contractChecked)
                {
                    // The font grid must not host its own ScrollViewer. WPF's ScrollViewer marks MouseWheel
                    // Handled whether or not it can actually scroll, so a nested one inside the shared
                    // ContentScroll means the settings window stops scrolling whenever the cursor is over the
                    // cards. ScrollBarVisibility="Disabled" does NOT fix it — only removing the scroll host does.
                    if (window.FindName("FontCardList") is DependencyObject cards)
                    {
                        // The WrapPanel proves the visual tree is actually built — without it a null
                        // ScrollViewer would mean "nothing realized yet", not "no scroll host".
                        bool realized = FindVisualChild<System.Windows.Controls.WrapPanel>(cards) is not null;
                        Check("font card grid is realized", realized);
                        Check("font card grid hosts no ScrollViewer (mouse wheel reaches the page)",
                            realized && FindVisualChild<System.Windows.Controls.ScrollViewer>(cards) is null);
                    }
                    else
                    {
                        Check("font card grid found", false);
                    }

                    Check($"nav has tags ({navKeys.Count})", navKeys.Count > 0);
                    Check("nav tags are unique", navKeys.Distinct(StringComparer.Ordinal).Count() == navKeys.Count);
                    Check($"panel count ({panelHost.Children.Count}) == nav count ({navKeys.Count})",
                        panelHost.Children.Count == navKeys.Count);
                    // The VM's hardcoded default must be a real tab, or the window opens on a blank right side.
                    Check($"default nav '{vm.SelectedNav}' is a real tab", navKeys.Contains(vm.SelectedNav, StringComparer.Ordinal));
                }

                foreach (string key in navKeys)
                {
                    vm.SelectedNav = key;
                    Drain(window.Dispatcher);
                    window.UpdateLayout();

                    int visible = panelHost.Children.OfType<UIElement>()
                        .Count(c => c.Visibility == Visibility.Visible);
                    if (!contractChecked)
                    {
                        Check($"tab '{key}': exactly 1 panel visible (got {visible})", visible == 1);
                    }

                    RenderToPng(window, Path.Combine(outDir, $"settings_tab_{key}_{skinName}.png"), fixedSize: true);

                    // 닉네임 효과 section sits at the bottom of the colour tab — capture the preview strip.
                    if (key == "theme"
                        && window.FindName("NameFxSectionHeader") is FrameworkElement fxHeader
                        && scroll is not null)
                    {
                        scroll.UpdateLayout();
                        fxHeader.BringIntoView();
                        scroll.UpdateLayout();
                        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + scroll.ViewportHeight - fxHeader.ActualHeight - 8);
                        scroll.UpdateLayout();
                        string shot = Path.Combine(outDir, $"settings_namefx_{skinName}.png");
                        RenderToPng(window, shot, fixedSize: true);

                        // Prove the strip actually moves. One frame cannot tell a running animation from a
                        // stopped one, and it WAS stopped: the sweep timer is demand-driven from the meter's
                        // row rebuild, so with no decorated row on screen the preview sat frozen — which is
                        // the one thing that preview exists to show.
                        string moved = Path.Combine(outDir, $"settings_namefx_moved_{skinName}.png");
                        NameFxSheen.SetPreviewPhase(0.35);
                        window.UpdateLayout();
                        RenderToPng(window, moved, fixedSize: true);
                        if (!contractChecked)
                        {
                            // 신청 경로. 부여는 서버가 하고 미터에는 신청 수단이 없으므로, 이 문구가
                            // 없으면 연출을 본 사람은 "나는 왜 없지"에서 멈춘다.
                            string[] texts = Descendants(window).OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text ?? string.Empty).ToArray();
                            Check("닉네임 효과 안내가 신청 경로(디스코드 DM)를 말한다",
                                texts.Any(t => t.Contains("디스코드", StringComparison.Ordinal) && t.Contains("DM", StringComparison.Ordinal)));
                            Check("닉네임 효과 안내가 랭커 자동 부여 기준을 말한다",
                                texts.Any(t => t.Contains("마스터 이상", StringComparison.Ordinal)));

                            Check("nameFx preview strip actually animates",
                                File.Exists(shot) && File.Exists(moved)
                                && !File.ReadAllBytes(shot).AsSpan().SequenceEqual(File.ReadAllBytes(moved)));
                        }

                        scroll.ScrollToTop();
                    }

                    // The font grid sits below the fold. Capture it in BOTH skins: Skin.AccentSoft over the
                    // near-white Light RowBg is exactly where a selection state goes invisible, and the whole
                    // point of the cards is that you can tell which one is picked.
                    if (key == "display"
                        && window.FindName("FontSectionHeader") is FrameworkElement fontHeader
                        && scroll is not null)
                    {
                        scroll.UpdateLayout();
                        fontHeader.BringIntoView();
                        scroll.UpdateLayout();
                        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + scroll.ViewportHeight - fontHeader.ActualHeight - 8);
                        scroll.UpdateLayout();
                        RenderToPng(window, Path.Combine(outDir, $"settings_font_{skinName}.png"), fixedSize: true);

                        // The card grid is taller than the viewport, so the other half of the feature — the
                        // system-font dropdown and the 현재 글꼴 preview row — needs its own frame.
                        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 360);
                        scroll.UpdateLayout();
                        RenderToPng(window, Path.Combine(outDir, $"settings_font_system_{skinName}.png"), fixedSize: true);
                        scroll.ScrollToTop();
                    }
                }

                // An unknown key must not blank the window. Today nothing guards this — the check is here so
                // the Phase 0 whitelist fallback has a test that fails BEFORE it is written, then passes after.
                if (!contractChecked)
                {
                    vm.SelectedNav = "no-such-tab";
                    Drain(window.Dispatcher);
                    window.UpdateLayout();
                    int visible = panelHost.Children.OfType<UIElement>().Count(c => c.Visibility == Visibility.Visible);
                    Check($"unknown nav key does not blank the window (visible={visible})", visible == 1);
                    vm.SelectedNav = navKeys[0];
                }

                contractChecked = true;
            }
            catch (Exception ex)
            {
                Check($"{skinName}: tab sweep", false);
                Console.WriteLine($"         {ex.Message}");
            }
            finally
            {
                window?.Close();
            }
        }

        Console.WriteLine($"=== settings tabs: {pass} passed, {fail} failed ===");
    }

    /// <summary>
    /// What it actually costs to resolve every selectable font, and what the overlay's hot path already pays.
    /// <para>The font-preview-card design was justified by "the dropdown loads 1 font, a grid of 15 cards loads
    /// 70 MB" — but <c>GlyphFallback.ForName</c> calls <c>FontResolver.Resolve</c> BEFORE its cache lookup, and
    /// that runs per row per tick. So the claim needs a number, not an argument.</para>
    /// </summary>
    /// <summary>
    /// What one overlay frame costs to rasterise, with and without the nickname/gauge effects.
    /// <para>This matters more than it looks. The meter repaints on its own report tick — 500 ms by default —
    /// but an effect animation drives the window at 30 fps, i.e. 15× the frame rate, on an
    /// <c>AllowsTransparency</c> layered window that has no GPU compositing path. This project already has an
    /// in-game frame-drop regression in its history (topmost re-assert storm), so "it feels fine" is not a
    /// number and the feature does not ship on one.</para>
    /// <para>Rasterisation is the half measurable offline; the layered-window surface upload is not, so the
    /// figure here is a floor, not the whole cost.</para>
    /// </summary>
    private static void MeasureOverlayFrame(MeterSettings settings, MeterColorTheme theme, long now)
    {
        Console.WriteLine("=== overlay frame cost ===");

        double Measure(string label, bool effectsOn)
        {
            settings.NameFxMode = effectsOn ? "animated" : "off";
            settings.NameFxGauge = effectsOn;
            var vm = new OverlayViewModel("bench", settings, theme, () => false);
            vm.SetNameFxRoster(SampleNameFxRoster(SampleMeterReport(now)));
            vm.Update(SampleMeterReport(now));

            var win = new OverlayWindow { DataContext = vm };
            win.Left = -10000;
            win.Top = -10000;
            win.Show();
            Drain(win.Dispatcher);
            var content = (FrameworkElement)win.Content;
            content.Measure(new Size(content.ActualWidth > 0 ? content.ActualWidth : 490, double.PositiveInfinity));
            content.Arrange(new Rect(0, 0, content.DesiredSize.Width, content.DesiredSize.Height));
            content.UpdateLayout();

            int w = Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Width));
            int h = Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Height));

            // One surface, reused. Allocating a 490x300 Pbgra32 per frame is ~588 KB straight to the LOH, and
            // the GC jitter that causes is larger than the 0.04 ms difference being measured.
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            for (int i = 0; i < 5; i++) // warm the glyph/geometry caches
            {
                rtb.Clear();
                rtb.Render(content);
            }

            const int Frames = 60;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < Frames; i++)
            {
                // Move the brushes like the animation would, so each frame really is different work.
                NameFxSheen.SetPreviewPhase(i / (double)Frames);
                content.UpdateLayout();
                rtb.Clear();
                rtb.Render(content);
            }

            sw.Stop();
            win.Close();
            double per = sw.Elapsed.TotalMilliseconds / Frames;
            Console.WriteLine($"  {label,-24} {per:F2} ms/frame  ({w}x{h})");
            return per;
        }

        double off = Measure("효과 없음", effectsOn: false);
        double on = Measure("닉네임+게이지 효과", effectsOn: true);
        // 진짜 변화는 프레임당 비용이 아니라 '몇 번 그리느냐'다. 미터는 원래 리포트 주기(기본 500ms)에만
        // 그리므로 초당 2프레임이고, 연출이 켜지면 30프레임이 된다.
        Console.WriteLine($"  꺼짐: 2 fps × {off:F2} ms = 코어 {off * 2 / 10:F2}%");
        Console.WriteLine($"  켜짐: 30 fps × {on:F2} ms = 코어 {on * 30 / 10:F1}%   ({on * 30 / (off * 2):F0}배)");
        settings.NameFxMode = "animated";
        settings.NameFxGauge = true;
    }

    private static void MeasureFontResolve(SettingsViewModel vm)
    {
        Console.WriteLine("=== font resolve cost ===");
        string[] names = vm.FontFamilies.Select(o => o.Value).ToArray();

        var cold = System.Diagnostics.Stopwatch.StartNew();
        foreach (string n in names)
        {
            FontResolver.Resolve(n);
        }

        cold.Stop();

        var warm = System.Diagnostics.Stopwatch.StartNew();
        foreach (string n in names)
        {
            FontResolver.Resolve(n);
        }

        warm.Stop();

        // The overlay's real shape: 10 rows rebuilt every tick, each asking for its nickname's font.
        string font = names[0];
        var rows = new[] { "콰과과", "띵보", "마르틴", "쿵해쫑", "검사왕", "빛의사제", "달빛나그네", "샤샤샥", "무명", "와플" };
        var hot = System.Diagnostics.Stopwatch.StartNew();
        for (int tick = 0; tick < 100; tick++)
        {
            foreach (string r in rows)
            {
                GlyphFallback.ForName(font, r);
            }
        }

        hot.Stop();

        Console.WriteLine($"  {names.Length} fonts — cold {cold.Elapsed.TotalMilliseconds:F1} ms, warm {warm.Elapsed.TotalMilliseconds:F1} ms");
        Console.WriteLine($"  overlay hot path — 100 ticks x 10 rows = {hot.Elapsed.TotalMilliseconds:F1} ms " +
                          $"({hot.Elapsed.TotalMilliseconds / 100:F2} ms per tick)");
    }

    /// <summary>
    /// Rasterise a brush across a bar-sized rectangle at a given sweep phase and return the pixels. Small and
    /// exact on purpose: this is the only way to tell "the animation moves the paint" from "the animation runs
    /// but the paint never changes", and those two look identical everywhere else.
    /// </summary>
    /// <summary>
    /// Every gauge skin's DECORATION over the real colour fill, at the same bar length and three phases.
    /// <para>This is the only view that answers the question the feature exists for. The meter capture cannot:
    /// each row there carries a different skin at a different bar length and one phase, so a motion difference
    /// reads as a length difference. It renders through the SHIPPING <see cref="GaugeFxLayer"/> at the real
    /// fill opacity with the DPS number on top, because "is this too loud" only means something with the number
    /// actually there — which is exactly what the reverted attempt got wrong.</para>
    /// </summary>
    private static void CaptureGaugeSheet(string outDir, bool light, ResourceDictionary palette)
    {
        NameFxPalette.Effect[] skins = NameFxPalette.GaugeSkins.ToArray();
        double[] phases = { 0.0, 2.6, 5.2 };
        const int LabelW = 128, BarW = 260, BarH = 30, RowH = 44, Pad = 10, HeadH = 26;
        int width = LabelW + ((BarW + Pad) * 3) + Pad;
        int height = HeadH + (RowH * skins.Length) + Pad;

        var rowBg = new SolidColorBrush(light ? Color.FromRgb(0xF1, 0xF5, 0xF9) : Color.FromRgb(0x1E, 0x29, 0x3B));
        var panelBg = new SolidColorBrush(light ? Color.FromRgb(0xFA, 0xFC, 0xFF) : Color.FromRgb(0x0F, 0x17, 0x2A));
        var fg = new SolidColorBrush(light ? Color.FromRgb(0x1E, 0x29, 0x3B) : Colors.White);
        var muted = new SolidColorBrush(light ? Color.FromRgb(0x64, 0x74, 0x8B) : Color.FromRgb(0x94, 0xA3, 0xB8));

        var canvas = new Canvas { Width = width, Height = height, Background = panelBg };
        var host = new Border { Width = width, Height = height, Child = canvas };
        host.Resources.MergedDictionaries.Insert(0, palette);

        for (int i = 0; i < skins.Length; i++)
        {
            double y = HeadH + (RowH * i);
            string kind = skins[i].Kind == NameFxPalette.NameFxKind.Ranker ? "랭커" : "후원자";
            canvas.Children.Add(Label(skins[i].Name, fg, 12, Pad, y + 3));
            canvas.Children.Add(Label(kind, muted, 9.5, Pad, y + 19));

            for (int p = 0; p < phases.Length; p++)
            {
                double x = LabelW + ((BarW + Pad) * p);
                if (i == 0)
                {
                    canvas.Children.Add(Label($"위상 {p}", muted, 11, x, 6));
                }

                var cell = new Grid { Width = BarW, Height = BarH, ClipToBounds = true };
                cell.Children.Add(new Border { Background = rowBg, CornerRadius = new CornerRadius(4) });
                cell.Children.Add(new Border
                {
                    Background = NameFxPalette.GaugeBrush(skins[i].Id, light),
                    Opacity = 0.58,
                    CornerRadius = new CornerRadius(4),
                });
                cell.Children.Add(new GaugeFxLayer
                {
                    SkinId = skins[i].Id,
                    IsFxEnabled = true,
                    ContentExclusionLeft = 0, // 시트에는 아이콘이 없으므로 보호 구간이 필요 없다
                    CornerRadius = 4,
                    Seed = i * 7,
                    PreviewSeconds = phases[p],
                });

                // 숫자는 장식 위에 — 실제 행과 같은 순서라야 가독성 판단이 성립한다.
                var dps = new TextBlock
                {
                    Text = "408,239/s",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    Foreground = fg,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                cell.Children.Add(dps);

                Canvas.SetLeft(cell, x);
                Canvas.SetTop(cell, y);
                canvas.Children.Add(cell);
            }
        }

        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        Drain(host.Dispatcher);

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        string path = Path.Combine(outDir, $"gauge_sheet_{(light ? "Light" : "Dark")}.png");
        using (FileStream fs = File.Create(path))
        {
            encoder.Save(fs);
        }

        Console.WriteLine($"  [ok]   {Path.GetFileName(path)} {width}x{height}");

        static TextBlock Label(string text, Brush brush, double size, double x, double y)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = size,
                Foreground = brush,
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            return tb;
        }
    }

    /// <summary>Render one decoration layer alone (no colour fill behind it) and return its pixels. Alone is the
    /// point: every check below is about what the DECORATION does, and over a fill a bug that draws nothing and
    /// a bug that draws everything both look like "a coloured bar".</summary>
    private static byte[] PaintGaugeFx(
        string? skinId, bool enabled, double seconds, double width = 240, double height = 30, double exclusionLeft = 0)
    {
        var layer = new GaugeFxLayer
        {
            SkinId = skinId,
            IsFxEnabled = enabled,
            PreviewSeconds = seconds,
            ContentExclusionLeft = exclusionLeft,
            CornerRadius = 4,
            Width = width,
            Height = height,
        };

        layer.Measure(new Size(width, height));
        layer.Arrange(new Rect(0, 0, width, height));
        layer.UpdateLayout();

        var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(layer);
        var px = new byte[(int)width * (int)height * 4];
        rtb.CopyPixels(px, (int)width * 4, 0);
        return px;
    }

    /// <summary>Total alpha in a horizontal band of a rendered layer. 0 means nothing was drawn there.</summary>
    private static long AlphaIn(byte[] px, int width, int height, int fromX, int toX)
    {
        long sum = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = Math.Max(0, fromX); x < Math.Min(width, toX); x++)
            {
                sum += px[((y * width) + x) * 4 + 3];
            }
        }

        return sum;
    }

    /// <summary>
    /// The decoration layer's contract. Everything here is about the layer ALONE — the colour fill it sits on is
    /// a separate Border and is not this element's business.
    /// </summary>
    private static void VerifyGaugeFx(MeterSettings settings, OverlayViewModel vm, Action<string, bool> Check)
    {
        const int W = 240, H = 30;
        string[] ids = NameFxPalette.GaugeSkins.Select(e => e.Id).ToArray();

        // 2) 스킨마다 실제로 무언가를 그린다. 이름만 등록되고 렌더러가 없는 스킨은 설정창에서는 멀쩡해
        //    보이고 미터에서만 아무 일도 안 일어난다.
        foreach (string id in ids)
        {
            byte[] on = PaintGaugeFx(id, enabled: true, seconds: 1.7);
            Check($"'{id}' 장식이 실제로 그려진다", AlphaIn(on, W, H, 0, W) > 0);
        }

        // 3) 세 위상이 픽셀로 달라야 한다. 정지한 효과는 한 프레임만 보면 동작하는 것과 구분되지 않는다.
        foreach (string id in ids)
        {
            byte[] a = PaintGaugeFx(id, enabled: true, seconds: 0.0);
            byte[] b = PaintGaugeFx(id, enabled: true, seconds: 2.6);
            byte[] c = PaintGaugeFx(id, enabled: true, seconds: 5.2);
            Check($"'{id}' 위상이 실제로 움직인다",
                !a.AsSpan().SequenceEqual(b) && !b.AsSpan().SequenceEqual(c));
        }

        // 4) 꺼진 상태·미상 id 는 **한 픽셀도** 그리지 않는다. 색 채움은 이 요소 밖이라 그대로 남는다.
        foreach (string id in ids)
        {
            Check($"'{id}' 장식 off 면 아무것도 안 그린다",
                AlphaIn(PaintGaugeFx(id, enabled: false, seconds: 1.7), W, H, 0, W) == 0);
        }

        Check("모르는 스킨 id 는 아무것도 안 그린다",
            AlphaIn(PaintGaugeFx("nope", enabled: true, seconds: 1.7), W, H, 0, W) == 0);
        Check("스킨 id 가 없으면 아무것도 안 그린다",
            AlphaIn(PaintGaugeFx(null, enabled: true, seconds: 1.7), W, H, 0, W) == 0);

        // 5) 🔴 아이콘 보호 구간. 반투명 테마에서 입자가 아이콘 뒤로 비치면 배지가 스킨마다 다른 색으로
        //    물든다 — 직업 아이콘은 스킨과 무관해야 한다는 것이 이 기능의 불변 조건이다.
        foreach (string id in ids)
        {
            long inside = 0;
            for (double t = 0; t < 9; t += 0.7)
            {
                inside += AlphaIn(PaintGaugeFx(id, true, t, W, H, exclusionLeft: 72), W, H, 0, 72);
            }

            Check($"'{id}' 아이콘 보호 구간(72 DIP)에 장식이 없다", inside == 0);
        }

        // 6) 짧은 막대에서 오른쪽으로 새지 않는다. 클립은 요소 크기 기준이므로 폭을 줄여 확인한다.
        foreach (double ratio in new[] { 0.16, 0.55, 1.0 })
        {
            int w = Math.Max(8, (int)(W * ratio));
            byte[] px = PaintGaugeFx("frost", true, 3.1, w, H);
            Check($"막대 {ratio:P0} 길이에서 장식이 경계를 넘지 않는다", px.Length == w * H * 4);
        }

        // 7) 보호 구간이 막대보다 길면(짧은 행) 아예 그리지 않는다 — 남는 폭이 없다.
        Check("막대가 보호 구간보다 짧으면 장식이 없다",
            AlphaIn(PaintGaugeFx("ember", true, 2.0, 60, H, exclusionLeft: 72), 60, H, 0, 60) == 0);

        // 8) 행 seed 가 다르면 그림도 달라야 한다 — 같은 스킨을 단 두 행이 완전히 동기화되면 안 된다.
        var l1 = new GaugeFxLayer { SkinId = "ember", IsFxEnabled = true, PreviewSeconds = 2.0, Seed = 0, Width = W, Height = H };
        var l2 = new GaugeFxLayer { SkinId = "ember", IsFxEnabled = true, PreviewSeconds = 2.0, Seed = 37, Width = W, Height = H };
        Check("행마다 seed 가 다르면 입자 배치도 다르다", !RenderLayer(l1, W, H).AsSpan().SequenceEqual(RenderLayer(l2, W, H)));

        // 9) 설정 모드별 게이트. 장식은 '움직임'이므로 색상만·저사양에서는 꺼지고 색 채움만 남는다.
        string savedMode = settings.NameFxMode;
        bool savedLow = settings.LowSpecMode;
        string savedBar = settings.BarStyle;

        settings.NameFxMode = "static";
        vm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Check("'색상만' 이면 장식이 꺼진다 (색 채움은 유지)",
            vm.Rows.All(r => !r.GaugeFxEnabled) && vm.Rows.Any(r => r.GaugeSkinId is not null));

        settings.NameFxMode = "animated";
        settings.LowSpecMode = true;
        vm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Check("저사양 모드면 장식이 꺼진다 (색 채움은 유지)",
            vm.Rows.All(r => !r.GaugeFxEnabled) && vm.Rows.Any(r => r.GaugeSkinId is not null));

        settings.LowSpecMode = false;
        settings.BarStyle = "none";
        vm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Check("게이지 형태가 '없음' 이면 장식이 꺼진다", vm.Rows.All(r => !r.GaugeFxEnabled));

        settings.BarStyle = "fill";
        settings.NameFxGauge = false;
        vm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Check("게이지 스킨 토글이 꺼지면 id 도 장식도 없다",
            vm.Rows.All(r => r.GaugeSkinId is null && !r.GaugeFxEnabled));

        settings.NameFxGauge = true;
        settings.NameFxMode = "off";
        vm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Check("연출이 꺼지면 id 도 장식도 없다",
            vm.Rows.All(r => r.GaugeSkinId is null && !r.GaugeFxEnabled));

        settings.NameFxMode = "animated";
        vm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Check("animated 로 돌아오면 부여된 행에 id 와 장식이 다시 붙는다",
            vm.Rows.Any(r => r.GaugeSkinId is not null && r.GaugeFxEnabled));

        // 10) id 는 실제로 해석된 스킨일 때만 붙는다 — 모르는 id 가 통과하면 색은 기본인데 장식만 뜬다.
        Check("id 가 붙은 행은 전부 카탈로그에 있는 스킨이다",
            vm.Rows.Where(r => r.GaugeSkinId is not null)
                   .All(r => NameFxPalette.IsKnownGauge(r.GaugeSkinId)));

        settings.NameFxMode = savedMode;
        settings.LowSpecMode = savedLow;
        settings.BarStyle = savedBar;
        vm.Update(SampleMeterReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private static byte[] RenderLayer(GaugeFxLayer layer, int w, int h)
    {
        layer.Measure(new Size(w, h));
        layer.Arrange(new Rect(0, 0, w, h));
        layer.UpdateLayout();
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(layer);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        return px;
    }

    private static byte[] PaintStrip(Brush brush, double phase)
    {
        NameFxSheen.SetPreviewPhase(phase);
        const int W = 300, H = 8;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(brush, null, new Rect(0, 0, W, H));
        }

        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[W * H * 4];
        rtb.CopyPixels(px, W * 4, 0);
        return px;
    }

    /// <summary>Mean per-channel distance between two rendered strips. "These two hex values look different to
    /// me" is not a measurement; two marks a user cannot tell apart are one mark.</summary>
    private static double MeanChannelDistance(byte[] a, byte[] b)
    {
        long sum = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return n == 0 ? 0 : sum / (double)n;
    }

    /// <summary>First descendant of the given type in the visual tree (breadth-first).</summary>
    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            DependencyObject node = queue.Dequeue();
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                if (child is T match)
                {
                    return match;
                }

                queue.Enqueue(child);
            }
        }

        return null;
    }

    private static void Capture(Func<Window> factory, ResourceDictionary palette, string path, bool fixedSize = false)
    {
        Window? window = null;
        try
        {
            window = factory();
            // Inject the skin into the WINDOW's resources (not Application.Resources — mutating the latter
            // trips net10 ThemeManager.SyncApplicationThemeMode). DynamicResource Skin.* resolves here.
            window.Resources.MergedDictionaries.Insert(0, palette);
            window.Left = -10000;
            window.Top = -10000;
            window.Show();

            // Drain the dispatcher so data binding + item realization complete, then measure explicitly
            // (SizeToContent's async sizing returns 0 for later windows).
            Drain(window.Dispatcher);

            RenderToPng(window, path, fixedSize);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [fail] {Path.GetFileName(path)}: {ex.Message}");
        }
        finally
        {
            window?.Close();
        }
    }

    /// <summary>
    /// Measure/arrange/encode an already-shown window. Split out of <see cref="Capture"/> so a caller that
    /// keeps ONE window alive across several states (the settings tab sweep) can render each state without
    /// paying window construction 12 times over.
    /// </summary>
    private static void RenderToPng(Window window, string path, bool fixedSize)
    {
        var content = (FrameworkElement)window.Content;
        double availableW = double.IsNaN(window.Width) ? content.ActualWidth : window.Width;
        content.Measure(new Size(availableW, double.PositiveInfinity));
        double measuredH = content.DesiredSize.Height > 0 ? content.DesiredSize.Height : content.ActualHeight;
        // fixedSize: arrange at the window's real height so overflow scrolls (shows the scrollbar).
        if (fixedSize && !double.IsNaN(window.Height) && window.Height > 0)
        {
            measuredH = window.Height;
        }

        content.Arrange(new Rect(0, 0, availableW, measuredH));
        content.UpdateLayout();

        int width = (int)Math.Ceiling(availableW);
        int height = (int)Math.Ceiling(measuredH);
        if (width <= 0 || height <= 0)
        {
            Console.WriteLine($"  [skip] {Path.GetFileName(path)} measured {width}x{height}");
            return;
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using FileStream fs = File.Create(path);
        encoder.Save(fs);
        Console.WriteLine($"  [ok]   {Path.GetFileName(path)} {width}x{height}");
    }

    /// <summary>Process all pending dispatcher work down to Background priority (waits for layout/binding).</summary>
    private static void Drain(Dispatcher dispatcher)
    {
        foreach (DispatcherPriority p in new[] { DispatcherPriority.Render, DispatcherPriority.Loaded, DispatcherPriority.Background })
        {
            dispatcher.Invoke(() => { }, p);
        }
    }

    private static List<(int Index, DpsReport Report)> SampleBattles(long now) => new()
    {
        Battle(0, "들판의 늑대", false, now - 600_000, now - 600_000 + 45_000, 120_000, 95_000),
        Battle(1, "발탄 군주", true, now - 300_000, now - 300_000 + 183_000, 8_200_000, 6_400_000, 3_100_000),
        Battle(2, "그림자 추적자", true, now - 120_000, now - 120_000 + 92_000, 4_200_000, 3_800_000),
    };

    /// <summary>컨텐츠 관리 rows built through the real <see cref="AetherRoster"/> and
    /// <see cref="WeeklyContentStore"/>, so the preview exercises the same staleness rule the app does — the
    /// last character's clears are stamped before the previous reset and must therefore show as un-cleared.</summary>
    private static IReadOnlyList<AetherRosterRow> SampleContentRows(long now)
    {
        var aether = AetherPerCharacterStore.Parse(null);
        aether.Upsert("h1", new AetherSnapshot(220, 635, now - 43_200_000, "콩팡", 1001));
        aether.Upsert("h2", new AetherSnapshot(15, 860, now - 36_000_000, "마이농", 1001));
        aether.Upsert("h3", new AetherSnapshot(520, 1_055, now - 57_600_000, "하아앙", 1001));
        aether.Upsert("h4", new AetherSnapshot(0, 1_805, now - 90_000_000, "헤로롱", 1001));
        // Most-recently-seen after the current character, so the shot actually contains the two states this
        // preview exists to check — an inherited row and the "기록 없음" row — rather than scrolling them off.
        aether.Upsert("h5", new AetherSnapshot(90, 240, now - 1_000, "필러싱", 1002));

        long thisWeek = Math.Max(now - 1, WeeklyContentReset.LastResetAtOrBefore(now) + 1);
        var weekly = WeeklyContentStore.Parse(null);
        weekly.Upsert("h1", "rud", 0, thisWeek);   // cleared
        weekly.Upsert("h1", "ero", 1, thisWeek);
        weekly.Upsert("h1", "mus", 1, thisWeek);
        weekly.Upsert("h2", "rud", 0, thisWeek);
        weekly.Upsert("h2", "ero", 0, thisWeek);
        weekly.Upsert("h2", "mus", 0, thisWeek);   // all three done
        weekly.Upsert("h3", "mus", 0, thisWeek);
        // h4 records nothing this week → every chip reads as the full grant

        var names = new[]
        {
            new AetherRosterName("h1", "콩팡", 1001, "궁성"),
            new AetherRosterName("h2", "마이농", 1001, "호법성"),
            new AetherRosterName("h3", "하아앙", 1001, "검성"),
            new AetherRosterName("h4", "헤로롱", 1001, "살성"),
            new AetherRosterName("h5", "필러싱", 1002, "수호성"),
        };

        // 어비스 회랑: the worst realistic case for layout — one character holding all six, one mid-visit with a
        // running clock, two that have reported nothing this cycle and so show their SERVER's corridors as full
        // (the 2026-08-20 case: a zeroed snapshot means "이 캐릭터는 안 갔다", not "우리가 못 뺏었다"), and one
        // on another server with no such donor, which is the only way the "기록 없음" line can still be reached.
        long thisCycle = Math.Max(now - 1, AbyssCorridorCycle.LastStartAtOrBefore(now) + 1);
        var corridors = AbyssCorridorStore.Parse(null);
        foreach (AbyssCorridorInfo corridor in AbyssCorridorCatalog.All)
        {
            corridors.Upsert("h1", corridor.TicketId, 130_000, thisCycle, markGranted: true);
        }

        corridors.Upsert("h1", 10_000_004, 0, thisCycle, markGranted: true);           // spent
        corridors.Upsert("h2", 10_000_002, 130_000, now - 55_000, markGranted: true, tickingSinceMs: now - 55_000);
        corridors.Upsert("h2", 10_000_005, 130_000, thisCycle, markGranted: true);
        corridors.MarkWitness("h1", thisCycle);
        corridors.MarkWitness("h2", thisCycle);
        corridors.MarkWitness("h3", thisCycle);   // logged in, reported nothing → inherits its server's six
        corridors.MarkWitness("h5", thisCycle);   // same, but nobody on 1002 has been watched → "기록 없음"
        // h4 has no witness at all and still inherits: never having been watched is not evidence either way

        return AetherRoster.Build(
            aether, names, currentHash: "h1", weekly: weekly, nowMs: now, corridors: corridors);
    }

    /// <summary>Eight rows, one per tier, so every ring treatment is visible in a single shot.</summary>
    private static DpsReport SampleTierReport(long now)
    {
        (int Uid, string Name, int Server, JobClass Job, int Power, long Damage, double Dps, double Share)[] rows =
        [
            (1, "콘팡", 1001, JobClass.SORCERER, 656_000, 59_300_000, 408_239, 24.1),
            (2, "쌈", 1001, JobClass.GLADIATOR, 663_400, 52_200_000, 359_394, 21.2),
            (3, "강까", 1001, JobClass.RANGER, 659_500, 44_300_000, 305_003, 18.0),
            (4, "띵보", 1002, JobClass.ASSASSIN, 641_200, 33_100_000, 227_915, 13.5),
            (5, "마르틴", 2001, JobClass.CHANTER, 598_400, 24_600_000, 169_374, 10.0),
            (6, "콰과과", 2003, JobClass.TEMPLAR, 574_900, 16_400_000, 112_912, 6.7),
            (7, "빛의사제", 1005, JobClass.CLERIC, 552_300, 9_800_000, 67_474, 4.0),
            (8, "느림보", 1007, JobClass.FIGHTER, 501_100, 6_300_000, 43_376, 2.5),
        ];

        var report = new DpsReport
        {
            BattleStart = now - 145_300,
            BattleEnd = now,
            Target = new MobInfo(999, new Mob(2301060, "칼드릭스", true), remainHp: 0, maxHp: 168_750_000),
            Contributors = [],
            Information = new Dictionary<int, DpsInformation>(),
        };

        foreach ((int uid, string name, int server, JobClass job, int power, long damage, double dps, double share) in rows)
        {
            report.Contributors.Add(new User(uid, name, server, job, isExecutor: uid == 1, power: power));
            report.Information[uid] = new DpsInformation(damage, dps, share, share);
        }

        return report;
    }

    /// <summary>Uid → tier. Row 1 is self and also carries a live battle percentile, so the "상위 X.X%" chip
    /// renders next to its 전투력 badge.</summary>
    private static Dictionary<int, RowTier> SampleTiers()
    {
        const string dungeon = "무스펠의 성배 · 어려움";
        // Self shows the longest basis the wording can produce (four grouped digits on both ends), so the
        // preview exercises the widest the chip's second line ever gets.
        const string band = "전투력 1,250k–1,300k 미만 기준";
        const string whole = "전체 전투력 기준";
        return new Dictionary<int, RowTier>
        {
            [1] = new RowTier(1, 0.7, dungeon, false, band),    // 챌린저
            [2] = new RowTier(2, 3.2, dungeon, false, band),    // 마스터
            [3] = new RowTier(3, 8.4, dungeon, false, whole),   // 다이아
            [4] = new RowTier(4, 22.6, dungeon, false, whole),  // 플래티넘
            [5] = new RowTier(5, 41.3, dungeon, false, whole),  // 골드
            [6] = new RowTier(6, 63.8, dungeon, false, whole),  // 실버
            [7] = new RowTier(7, 84.1, dungeon, false, whole),  // 브론즈
            // Career tier known but THIS fight's cohort shipped no distribution row — the chip collapses
            // rather than inventing a number, and carries no basis. Worth seeing in the preview.
            [8] = new RowTier(8, null, dungeon),  // 아이언, 표본 부족
        };
    }

    /// <summary>
    /// Grant every contributor in the sample report a different effect, so one screenshot covers the whole
    /// catalogue at real row size. Hashes are computed the same way the meter computes them — if
    /// <c>StatsIdentity</c> and the roster ever disagree, this preview goes blank and says so.
    /// </summary>
    private static NameFxRoster SampleNameFxRoster(DpsReport report)
    {
        var entries = new List<string>();
        int i = 0;
        foreach (User u in report.Contributors)
        {
            string? hash = WaffleMeter.Stats.StatsIdentity.CharacterIdentityHash(u.Server, u.Nickname);
            if (hash is null)
            {
                continue;
            }

            // stride 2: 연속 인덱스는 후원자 4종만 집어 랭커 게이지가 한 번도 안 걸린다.
            NameFxPalette.Effect e = NameFxPalette.NameEffects[(i * 2) % NameFxPalette.NameEffects.Length];
            string kind = e.Kind == NameFxPalette.NameFxKind.Ranker ? "ranker" : "supporter";
            // 게이지는 그 캐릭터 **자기 계열**에서 얹는다 — 부여 규칙 그대로다. 종전에는 랭커에게만
            // 얹었는데, 그러면 후원자 게이지가 어느 캡처에도 안 나와 0.45 불투명도 뒤에서 실제로 읽히는지를
            // 볼 방법이 없다(그게 이 스킨들의 유일한 판단 기준이다).
            NameFxPalette.Effect[] familyGauges = NameFxPalette.GaugeSkins
                .Where(g => g.Kind == e.Kind).ToArray();
            string quote = "\"";
            string gauge = familyGauges.Length > 0
                ? $",{quote}g{quote}:{quote}{familyGauges[i % familyGauges.Length].Id}{quote}"
                : string.Empty;
            i++;
            entries.Add($$"""{"h":"{{hash}}","e":"{{e.Id}}","k":"{{kind}}"{{gauge}}}""");
        }

        return NameFxRoster.Parse(
            $$"""{"schemaVersion":1,"entries":[{{string.Join(",", entries)}}]}""",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            NameFxPalette.IsKnownNameEffect,
            NameFxPalette.IsKnownGauge);
    }

    private static DpsReport SampleMeterReport(long now)
    {
        var report = new DpsReport
        {
            BattleStart = now - 145_300,
            BattleEnd = now,
            Target = new MobInfo(999, new Mob(500, "크로메데의 심연", true), remainHp: 0, maxHp: 168_750_000),
            Contributors = new List<User>
            {
                new(1, "콘팡", 1001, JobClass.SORCERER, isExecutor: true, power: 656_000),
                new(2, "쌈", 1001, JobClass.GLADIATOR, power: 663_400),
                new(3, "강까", 1001, JobClass.RANGER, power: 659_500),
                new(4, "노까", 1002, JobClass.CLERIC, power: 591_700),
            },
            Information = new Dictionary<int, DpsInformation>
            {
                [1] = new DpsInformation(59_300_000, 408_239, 35.1, 35.1),
                [2] = new DpsInformation(46_200_000, 318_077, 27.4, 27.4),
                [3] = new DpsInformation(36_300_000, 249_953, 21.5, 21.5),
                [4] = new DpsInformation(27_000_000, 185_861, 16.0, 16.0),
            },
            // A SAVED report's frozen snapshots — what the detail window renders in history replay. Without
            // these the skill table and both uptime tabs come out empty and the preview shows only chrome.
            SkillDetailsSnapshot = new Dictionary<int, Dictionary<string, AnalyzedSkill>> { [1] = SampleSkills() },
            BuffRates = new Dictionary<int, List<OperatingData>> { [1] = SampleBuffs() },
            BossBuffRates = SampleBossDebuffs(),
            // Frozen DPS-graph series + buff timeline for uid 1 — what the "DPS 그래프" tab renders in replay.
            // Total matches the skill-snapshot sum (≈15.4M, the tile's 총 피해) so the graph's scale agrees with
            // the DPS tile — in a real fight the series and the skill breakdown come from the same packets.
            DpsSeries = new Dictionary<int, long[]> { [1] = SampleDpsSeries(15_400_000, 145) },
            BuffIntervals = new Dictionary<int, List<BuffTimeline>> { [1] = SampleBuffIntervals(now - 145_300) },
        };
        return report;
    }

    // A shaped-but-deterministic per-second series (no RNG): a wavy baseline with the DPS lifting during the
    // 원소 강화 buff windows, so the preview shows the line rising where the icon lane is active.
    private static long[] SampleDpsSeries(long totalDamage, int seconds)
    {
        (int Start, int End)[] spikes = [(0, 40), (60, 100), (120, 145)];
        (int Start, int End)[] gaps = [(45, 58), (102, 108)]; // downtime → the DPS line breaks instead of hitting 0
        var series = new long[seconds];
        double baseline = totalDamage / (double)seconds;
        for (int i = 0; i < seconds; i++)
        {
            series[i] = (long)(baseline * (0.55 + 0.45 * Math.Sin(i * 0.33)));
            foreach ((int s, int e) in spikes)
            {
                if (i >= s && i < e) series[i] = (long)(series[i] * 1.8);
            }
        }

        foreach ((int s, int e) in gaps)
        {
            for (int i = s; i < e && i < seconds; i++) series[i] = 0; // no damage dealt this window
        }

        // Re-normalize so the series sums to the participant's total damage (keeps the y-axis honest).
        long sum = series.Sum();
        if (sum > 0)
        {
            for (int i = 0; i < seconds; i++) series[i] = series[i] * totalDamage / sum;
        }

        return series;
    }

    private static List<BuffTimeline> SampleBuffIntervals(long battleStart)
    {
        (long, long) Span(int a, int b) => (battleStart + a * 1000L, battleStart + b * 1000L);
        return
        [
            // Self (uid 1) 마도성(prefix 15) class buffs — these are what the graph keeps (딜 관련 버프).
            new(152100461, "원소 강화", 1, 15210000, 15, [Span(0, 40), Span(60, 100), Span(120, 145)]),
            new(153900471, "강화: 잔불", 1, 15390000, 15, [Span(10, 30), Span(70, 92)]),
            new(150500301, "냉기의 로브", 1, 15050000, 15, [Span(20, 46)]),
            new(153600201, "화염 각인", 1, 15360000, 15, [Span(5, 42), Span(62, 100)]),
            new(150800301, "정령 친화", 1, 15080000, 15, [Span(0, 145)]),
            // These must be FILTERED OUT: party buffs (actor 4) + consumables (prefix 0, 주문서).
            new(181600411, "질주의 진언", 4, 18160000, 18, [Span(0, 145)]),
            new(174000571, "대지의 축복", 4, 17400000, 17, [Span(5, 25), Span(55, 80), Span(110, 140)]),
            new(22101051, "용기의 주문서", 1, 22101051, 0, [Span(0, 145)]),
            new(22104021, "가호의 주문서", 1, 22104021, 0, [Span(0, 145)]),
        ];
    }

    private static Dictionary<string, AnalyzedSkill> SampleSkills()
    {
        // rawCode carries the specialization suffix ([slot][slot][slot][charge]); front = hits − back − a
        // few neutral, so 후방/전방 read like a real fight.
        AnalyzedSkill S(int code, string name, int dmg, int hits, int crit, int strong, int perfect, int back,
            int front, int rawCode, int dot = 0, int dotTimes = 0) => new()
        {
            SkillCode = code, Name = name, RawSkillCode = rawCode, DamageAmount = dmg, Times = hits, CritTimes = crit,
            DoubleTimes = strong, PerfectTimes = perfect, BackTimes = back, FrontTimes = front, FlaggedTimes = hits,
            DotDamageAmount = dot, DotTimes = dotTimes,
        };

        return new Dictionary<string, AnalyzedSkill>
        {
            ["15210000"] = S(15210000, "그리폰 화살", 2_500_000, 26, 22, 14, 19, 13, 11, 15210240),
            ["15220000"] = S(15220000, "속사", 2_200_000, 56, 41, 32, 27, 28, 24, 15221350),
            ["15230000"] = S(15230000, "송곳 화살", 1_900_000, 11, 8, 9, 9, 6, 4, 15230450),
            ["15240000"] = S(15240000, "광풍 화살", 1_600_000, 6, 6, 2, 5, 3, 2, 15240050),
            ["15250000"] = S(15250000, "폭발 화살", 1_400_000, 2, 2, 2, 1, 1, 1, 15252340),
            ["15260000"] = S(15260000, "지원 사격", 1_300_000, 26, 26, 14, 18, 13, 11, 15260008),
            ["15270000"] = S(15270000, "사냥꾼의 혼", 1_300_000, 14, 12, 7, 10, 7, 5, 15270120, dot: 420_000, dotTimes: 34),
            ["15280000"] = S(15280000, "질풍 화살", 1_100_000, 7, 6, 5, 3, 4, 3, 15280350),
            ["15290000"] = S(15290000, "파열 화살", 959_500, 4, 3, 1, 2, 2, 1, 15290000),
            ["15300000"] = S(15300000, "조준 화살", 739_400, 3, 1, 0, 1, 1, 1, 15300240),
        };
    }

    // uid 1 is 마도성 (job prefix 15): codes under 15xxxxxxx land in 내 버프, actor 4 (치유성) in 파티원 버프,
    // and the consumable codes in 그 외 — one row per section so the preview exercises all three subtitles.
    private static List<OperatingData> SampleBuffs() =>
    [
        new(152100461, "원소 강화", null, "공격력 증가", 93.2, 1, 15210000, 15),
        new(153900471, "강화: 잔불", null, "불 속성 피해 증가", 40.6, 1, 15390000, 15),
        new(150500301, "냉기의 로브", null, "받는 피해 감소", 20.5, 1, 15050000, 15),
        new(181600411, "질주의 진언", null, "이동 속도 증가", 100.0, 4, 18160000, 18),
        new(174000571, "대지의 축복", null, "생명력 회복", 96.0, 4, 17400000, 17),
        new(22101051, "용기의 주문서", null, "공격력 증가", 100.0, 1, 22101051),
        new(22104021, "가호의 주문서", null, "방어력 증가", 100.0, 1, 22104021),
    ];

    private static List<OperatingData> SampleBossDebuffs() =>
    [
        new(152800221, "불의 표식", null, "받는 불 속성 피해 증가", 88.6, 1, 15280000, 15),
        new(153200301, "지연 피해", null, "지속 피해", 65.0, 1, 15320000, 15),
        new(174000401, "대지의 징벌", null, "지속 피해", 96.0, 4, 17400000, 17),
        new(152000421, "빙결 폭발", null, "물 속성 지속 피해", 32.2, 1, 15200000, 15),
    ];

    private static (int, DpsReport) Battle(int idx, string mob, bool boss, long start, long end, params double[] amounts)
    {
        var info = new Dictionary<int, DpsInformation>();
        for (int i = 0; i < amounts.Length; i++)
        {
            info[i + 1] = new DpsInformation(amounts[i], 0, 0, 0);
        }

        return (idx, new DpsReport
        {
            BattleStart = start,
            BattleEnd = end,
            Target = new MobInfo(idx + 1, new Mob(100 + idx, mob, boss)),
            Information = info,
        });
    }
}
