using System.Globalization;
using WaffleMeter.Capture;
using WaffleMeter.Capture.Live;
using WaffleMeter.Data;
using WaffleMeter.Replay;
using WaffleMeter.Services;
using WaffleMeter.Stats;

namespace WaffleMeter.App.Core;

/// <summary>
/// Composition root: builds and wires the entire ported backend object graph — config, the capture
/// pipeline (aligner -&gt; assembler -&gt; stream processor), the data + DPS layer, the official
/// lookup, and the stats consent/builder/queue — and resolves their dependency cycles. The WPF UI
/// (and <see cref="MeterEngine"/>) bind to the components exposed here. <see cref="Feed"/> processes
/// one captured segment; <see cref="GetReport"/> reads the live DPS report. Both must be called from
/// a single owner thread (the meter is not internally synchronized), mirroring the Kotlin consumer.
/// </summary>
public sealed class MeterServices
{
    public PropertyHandler Props { get; }
    public DataManager Data { get; }
    public DpsCalculator Calculator { get; }

    /// <summary>Movement/positional replay engine, or null unless <c>replay.recordMovement=true</c> AND the
    /// private engine DLL is present (discovered at runtime — see <see cref="ReplayEngineLoader"/>). Records
    /// per-battle position timelines for the WCL-style replay. A PARALLEL tap on the assembled packet stream
    /// — fully decoupled from and unable to regress the parity-critical DPS path. Off by default. See
    /// docs/replay-feature-plan.md.</summary>
    public IReplayEngine? Movement { get; }

    /// <summary>Whether battles are being recorded for replay (BETA). Toggled live from settings — the
    /// engine is always loaded when its DLL is present, and this gates the capture tap and the per-battle
    /// build. Turning it off stops recording at once (and drops what was buffered); it never touches DPS.
    /// Written on the UI thread, read on the capture-consumer thread.</summary>
    public volatile bool RecordReplay;

    /// <summary>Live replay availability for the UI: the private engine DLL shipped with this build.</summary>
    public bool ReplayAvailable => Movement != null;

    /// <summary>Where recordings are written (the folder the settings panel opens).</summary>
    public string ReplayDirectory => Path.Combine(Props.AppDirectory(), "replays");
    public OfficialCharacterLookup OfficialLookup { get; }
    public StatsApiClient StatsApi { get; }
    public StatsConsentManager Consent { get; }
    public StatsPayloadBuilder StatsBuilder { get; }
    public StatsUploadQueue UploadQueue { get; }

    /// <summary>던전 티어 분포 기준표(로컬 캐시 + 주기 갱신). 전투 중에는 절대 네트워크를 타지 않는다.</summary>
    public TierService Tier { get; }

    /// <summary>후원자·랭커 닉네임 연출 명단(로컬 캐시 + 매시 갱신). 티어와 별도 채널이라 후원이 반영되는
    /// 시점이 분포 재계산에 묶이지 않는다.</summary>
    public NameFxService NameFx { get; }
    public string Version { get; }

    /// <summary>Pending party-join requests (Kotlin PacketEvent.JoinRequest family). The WPF layer
    /// subscribes to <see cref="JoinRequestStore.Changed"/> and renders the join panel.</summary>
    public JoinRequestStore JoinRequests { get; }

    /// <summary>Raised (on the consumer thread) with a fresh saved-battle snapshot whenever the history
    /// changes — a battle is saved or the meter is reset. The history panel caches the latest snapshot.
    /// Fires on the owner thread; the WPF layer marshals it.</summary>
    public event Action<List<(int Index, DpsReport Report)>>? BattleListChanged;

    /// <summary>Snapshot the saved-battle list and notify subscribers. MUST be called on the consumer
    /// (owner) thread — it reads the repository the parser writes to.</summary>
    public void NotifyBattleListChanged() => BattleListChanged?.Invoke(Data.RecentBattleList());

    /// <summary>Diagnostic packet-debug-logs writer (off by default). Doubles as the stream processor
    /// sink + capture/assembled hooks, so the app can record a replayable corpus without the Kotlin
    /// dev build. Toggle with <c>DebugLogger.Start()/Stop()</c>.</summary>
    public PacketDebugLogger DebugLogger { get; }

    // Per-connection stream demux (Kotlin Main.kt after dev d00c850): the game can flow over a local
    // proxy on loopback with dynamic ports where multiple connections share a srcPort, so streams are
    // keyed by the full 4-tuple; each owns its own aligner+assembler over ONE shared StreamProcessor.
    private const long IdleMs = 30_000;
    private const long EvictEvery = 1000;
    // P2P/streaming noise guard: a directional connection that has pushed this many bytes WITHOUT ever
    // yielding a recognizable game packet is noise (NAVER Live P2P, downloads, OBS…). We stop processing
    // it AND ask the elevated helper to drop it at capture, so a flood can't starve the game's
    // high-frequency damage stream. Content-based (never IP/port targeted) → loopback/booster game paths,
    // which DO yield game packets, are always kept (they earn GameSignal within the first few KB).
    private const long NoiseVolumeBytes = 2_000_000;
    // Require some FRAMED packets too, so a stalled aligner (which accumulates raw bytes but emits no
    // assembled packets — more likely under the very flood this fights) can never be misread as noise.
    private const int MinNoisePackets = 50;
    private const int MaxExcludedKeys = 16384;
    // Stream-scoped self-heal. Until now the ONLY escape from a latched aligner/framer stall was the user
    // pressing 초기화 — FlushAllStreams is invoked from nowhere else (DpsCalculator.HardReset /
    // ResetKeepingCharacters), and an ACTIVE stream never hits the 30 s idle eviction because LastSeen is
    // refreshed by every segment. Measured against a NAVER Live Streaming Connector session (its local proxy on
    // 127.0.0.1:17080 carries the whole video over loopback TCP, so 25 of its 27 connections land in our
    // content-based capture): the meter stopped showing combat and 초기화 restored it instantly, every time.
    // These two thresholds re-run exactly what that button does, scoped to the one stream that is stuck.
    //
    // (A) The aligner has held a head-of-line gap this long while segments keep arriving. SNIFF never
    // re-observes a segment WE dropped (the client received it, so the server never retransmits), and 3 s is
    // past Windows' minimum RTO plus a backoff, so a genuine network loss has already been repaired by then.
    // The aligner's own 2MB escape needs 8-28 minutes at a real game connection's rate (measured 1.2-3.8 KB/s).
    // Tunable, and 0 turns the whole self-heal off: capture.selfHealGapMs. Below the RTO floor this would start
    // cutting gaps that a retransmit was about to fill, so keep it comfortably past a couple of backoffs.
    private const long DefaultSelfHealGapMs = 3_000;
    private readonly long _selfHealGapMs;
    // (B) A stream with no game signal is sitting on a multi-MB framer buffer: a false realLength read out of
    // high-entropy video/P2P bytes. It emits nothing, so it can never satisfy the noise guard's
    // MinNoisePackets gate, and it grows toward PacketAccumulator's 32MB cap (a 64MB array that never shrinks).
    // GameSignal > 0 streams are EXEMPT, so the large zone/boss snapshot frames that raised that cap
    // (c683495, 바크론 보스 인식) are untouched — they ride a connection that earned its game signal in the
    // first few KB. The grace bytes keep a freshly-reconnected game stream out of this until it has had ample
    // room to earn that signal.
    private const long NoiseFramerHoldBytes = 2_000_000;
    private const long NoiseFramerGraceBytes = 4_000_000;
    // Single-game-stream lock (dual-capture defense): a VPN/accelerator can expose the SAME plaintext
    // game bytes under TWO 4-tuples (dual tunnel, loopback relay, mid-session port rebind). Each StreamKey
    // owns its own aligner, so TCP-seq dedup can't collapse them and BOTH would feed the shared processor —
    // every damage event counted twice (~2x DPS, uniform across all rows/classes). Only the PRIMARY game
    // stream is fed; a concurrent duplicate is dropped. If the primary emits no game packet for this long,
    // the next game stream fails over (real reconnect / proxy port change). A lone game stream is always
    // primary, so single-stream (non-VPN) users are byte-for-byte unaffected.
    private const long GameStreamHandoverMs = 5_000;
    private readonly StreamProcessor _processor;
    private readonly Dictionary<string, StreamState> _streams = new();
    private readonly HashSet<string> _excludedKeys = new();
    private readonly bool _dedupeGameStreams;
    // volatile: the ping handler (pipe read thread) compares against this; the consumer thread writes it.
    private volatile string? _primaryGameKey;
    // The most recent stream that carried a game packet, maintained regardless of the dedupe toggle. The
    // ping matcher uses this (falling back from _primaryGameKey) so server latency still works when
    // capture.dedupeGameStreams=false leaves _primaryGameKey unset.
    private volatile string? _lastGameStreamKey;
    private long _primaryGameAt;
    private long _processed;

    // Passive server-latency (ping), matched to the primary game stream. Written by the pipe read thread,
    // read by the UI thread; the values are a display convenience so a rare torn read self-corrects.
    private double _latestPingMs;
    private long _lastPingAtMs;
    private volatile bool _pingLoopback;

    /// <summary>Accept a passive RTT sample from the capture helper (any thread). Kept only when its inbound
    /// connection matches the primary game stream, so non-game connections' latency is ignored.</summary>
    public void AcceptPing(ConnKey key, double ms, bool isLoopback)
    {
        // Match the game stream direction-INDEPENDENTLY (compare the unordered endpoint pair). The RTT can be
        // resolved on either direction of the connection — always the inbound one on a normal link, but the
        // synthetic-direction loopback path (VPN/booster) can resolve on the client→server direction, whose
        // 4-tuple is the reverse of the elected inbound game-stream key. A canonical compare accepts both, so a
        // booster user's ping stops being silently rejected; it still can't match a DIFFERENT connection.
        string? gameKey = _primaryGameKey ?? _lastGameStreamKey;
        if (gameKey == null || Canonicalize(streamKeyOf(key)) != Canonicalize(gameKey))
        {
            return;
        }

        static string streamKeyOf(ConnKey k) => $"{Dotted(k.SrcIp)}:{k.SrcPort}-{Dotted(k.DstIp)}:{k.DstPort}";

        _latestPingMs = ms;
        _lastPingAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _pingLoopback = isLoopback;
    }

    /// <summary>The current server latency for display, or null when none is fresh (older than 10 s).
    /// <c>IsLocalHop</c> = the measured hop is a VPN/booster relay, not the real server.</summary>
    public (double Ms, bool IsLocalHop)? CurrentPing()
    {
        long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastPingAtMs;
        return _lastPingAtMs != 0 && age <= 10_000 ? (_latestPingMs, _pingLoopback) : null;
    }

    private static string Dotted(uint ip) => $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";

    /// <summary>A direction-independent form of an "ep-ep" stream key: the two endpoints sorted, so the two
    /// directions of one connection compare equal (and different connections still don't). Public for testing.</summary>
    public static string Canonicalize(string streamKey)
    {
        int dash = streamKey.IndexOf('-');
        if (dash < 0)
        {
            return streamKey;
        }

        string a = streamKey[..dash];
        string b = streamKey[(dash + 1)..];
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    /// <summary>Raised (consumer thread) when a connection is classified as high-volume non-game noise,
    /// so the capture helper can drop it at the source. <see cref="MeterEngine"/> forwards it to the
    /// backend (the pipe client relays it to the elevated helper).</summary>
    public event Action<ConnKey>? ConnectionExcludeRequested;

    private sealed class StreamState(PacketAlignmenter aligner, StreamAssembler assembler)
    {
        public PacketAlignmenter Aligner { get; } = aligner;
        public StreamAssembler Assembler { get; } = assembler;
        public long LastSeen { get; set; }
        public long Bytes { get; set; }              // raw payload volume seen on this directional connection
        public int EmittedPackets { get; set; }      // assembled packets the framer emitted (stall guard)
        public int GameSignal { get; set; }          // assembled packets that look like game packets (>0 => protected)
        public bool SuppressedDuplicate { get; set; } // a concurrent duplicate of the primary game stream — drop its packets
        public long SelfHeals { get; set; }          // stream-scoped stall recoveries (diagnostic)
    }

    // Stall recoveries and gap-skips that belong to streams which have since been evicted or excluded. Without
    // these the totals DROP when a stream disappears (AlignerGapSkips only sums live streams), which is exactly
    // what the field log showed — `gapSkip/5s=-2 cum=0` — erasing the loss indicator at the very moment peer
    // churn is highest. Consumer-thread only, same as _streams.
    private long _gapSkipsRetired;
    private long _selfHealsRetired;

    /// <summary>Re-admit every excluded connection (called from a user reset, on the consumer thread) so a
    /// misclassification recovers without an app relaunch. The helper's source-side drop set is cleared
    /// separately by <see cref="MeterEngine"/>.</summary>
    public void ClearExclusions() => _excludedKeys.Clear();

    public MeterServices(
        PropertyHandler props,
        StatsApiClient.RequestFunc? statsTransport = null,
        OfficialCharacterLookup? officialLookup = null,
        PacketDebugLogger? debugLogger = null,
        string? appVersion = null,
        NameFxCatalogue? nameFxCatalogue = null)
    {
        Props = props;
        // From the build (entry-assembly InformationalVersion = WaffleVersion), not a persisted
        // property — the old Kotlin value lingers in settings.properties. appVersion lets tests/CLI inject.
        Version = VersionConfig.Resolve(appVersion).Version;

        // Dual-capture defense (default on): collapse a game stream that a VPN/accelerator mirrors onto two
        // 4-tuples down to one, so damage isn't double-counted. Escape hatch: capture.dedupeGameStreams=false.
        _dedupeGameStreams = props.GetProperty("capture.dedupeGameStreams", "true") != "false";

        // Stream-scoped stall recovery (default on). Escape hatch: capture.selfHealGapMs=0 restores the old
        // behavior, where the only way out of a latched stream was the user pressing 초기화.
        _selfHealGapMs = long.TryParse(props.GetProperty("capture.selfHealGapMs", ""), out long gapMs) && gapMs >= 0
            ? gapMs
            : DefaultSelfHealGapMs;

        OfficialLookup = officialLookup ?? new OfficialCharacterLookup();
        Data = new DataManager { OfficialLookup = OfficialLookup };
        // 전투 기록을 디스크에 남긴다 — 종전에는 메모리 리스트 하나라, 리플레이 파일은 남는데 그 전투의
        // 기록 행은 없는 상태가 재시작마다 나왔다. 실패는 스토어가 삼킨다(영속화는 편의지 집계의 전제가 아니다).
        Data.EnableBattleHistoryPersistence(Path.Combine(props.AppDirectory(), "battle-history"));

        // Pipeline (single consumer owns these; the calculator's flush resets framing + ordering of
        // every live stream). The debug logger is the processor sink so a diagnostic session captures
        // dispatch/damage/meta/etc.; it is an inert no-op until DebugLogger.Start() is called.
        DebugLogger = debugLogger ?? new PacketDebugLogger();
        JoinRequests = new JoinRequestStore();
        _processor = new StreamProcessor(DebugLogger, Data, new JoinRequestSinkAdapter(JoinRequests, Data));
        Calculator = new DpsCalculator(Data, FlushAllStreams);

        // Movement/positional replay (BETA, default OFF): a parallel tap that records per-battle position
        // timelines. Never on the DPS path; resolves entity ids via Data for non-contributor (support) movers.
        // The engine is a private, runtime-loaded DLL — absent in an open-source build, in which case
        // TryLoad returns null and replay stays unavailable.
        //
        // The engine is created whenever the DLL is there and RECORDING is gated at the tap instead, so the
        // settings toggle takes effect immediately rather than on the next launch. An idle engine costs
        // nothing: Scan() is simply never called.
        Movement = ReplayEngineLoader.TryLoad()
            ?.Create(new DataManagerIdentitySource(Data), Path.Combine(props.AppDirectory(), "replays"));
        RecordReplay = props.GetProperty("replay.recordMovement", "false") == "true";
        if (RecordReplay)
        {
            // Startup marker so a "replay missing" report distinguishes flag-off from engine-DLL-absent.
            ReplayDiag.Note(props, Movement != null ? "engine loaded" : "engine DLL MISSING — replay unavailable");
        }

        // Stats stack. Break the consent <-> builder cycle with a deferred reference. The install key signs
        // every write (reports / consent events) from the first run per §2.1/§2.5 — the server takes signed
        // writes in warn mode and gates public transitions on the resulting grant.
        StatsApi = new StatsApiClient(
            () => StatsInstall.InstallId(props), statsTransport, new StatsInstallKey(props),
            // Derived, never a literal: supporting a future schema means editing the set and nothing else.
            readableSchemaVersion: TierArtifact.MaxSupportedSchemaVersion);
        StatsConsentManager consent = null!;
        StatsBuilder = new StatsPayloadBuilder(Data, () => consent.GetInfo().PublicCharacter);
        consent = new StatsConsentManager(props, Data, StatsApi, () => StatsBuilder.OwnCharacter());
        Consent = consent;

        // 기동 시 1회 위생 정리. 신원 파서가 뚫려 저장된 "존재할 수 없는 캐릭터"의 동의 레코드와, 같은 해시로
        // 남은 오드 기록을 함께 치운다 — 파서 게이트(StreamProcessor.SearchOwnNickname)는 새 오염만 막으므로
        // 이미 굳은 레코드는 여기서만 사라진다. MeterSettings는 이 뒤에 만들어지므로(App.xaml.cs) 정리된
        // aether.perCharacter를 읽는다. 실측 오염: nickname="I" / server=47200 (2026-07-30).
        IReadOnlyList<string> purgedCharacters = consent.PurgeImpossibleCharacters();
        if (purgedCharacters.Count > 0)
        {
            AetherPerCharacterStore aether = AetherPerCharacterStore.Parse(
                props.GetProperty("aether.perCharacter"), props.GetProperty("aether.characterNames"));
            if (aether.RemoveAll(purgedCharacters))
            {
                props.SetProperty("aether.perCharacter", aether.Serialize());
                props.SetProperty("aether.characterNames", aether.SerializeNames()); // 오염된 신원의 이름도 함께
            }

            // 주간 성역 클리어 기록도 같은 해시로 남아 있다 — 오드와 함께 치우지 않으면 존재할 수 없는 캐릭터가
            // 컨텐츠 관리 목록에 계속 뜬다.
            WeeklyContentStore weekly = WeeklyContentStore.Parse(props.GetProperty("content.weeklyClears"));
            if (weekly.RemoveAll(purgedCharacters))
            {
                props.SetProperty("content.weeklyClears", weekly.Serialize());
            }

            // 어비스 회랑 기록도 같은 해시를 쓴다 — 같이 치운다.
            AbyssCorridorStore corridors = AbyssCorridorStore.Parse(props.GetProperty("content.abyssCorridors"));
            if (corridors.RemoveAll(purgedCharacters))
            {
                props.SetProperty("content.abyssCorridors", corridors.Serialize());
            }
        }

        UploadQueue = new StatsUploadQueue(consent, StatsBuilder, StatsApi, Data, props);
        UploadQueue.Configure(Version);

        // 던전 티어 기준표. 자체 백그라운드 스레드에서 12시간마다 한 번만 받고, 전투 중에는 어떤 요청도
        // 하지 않는다 — 라이브 '상위 X.X%'는 받아둔 분포로 로컬 계산한다.
        Tier = new TierService(StatsApi, props, clientVersion: Version);

        // 닉네임 연출 명단. 효과 id 카탈로그는 브러시를 들고 있어 WPF 쪽에 산다. Core 가 렌더를 참조하는
        // 방향으로 뒤집지 않으려고 판정 함수를 주입받고, 없으면 아무 id 도 모르는 것으로 둔다 —
        // 그리지 못하는 효과를 받아 두는 것보다 안 받는 편이 낫다.
        NameFxCatalogue catalogue = nameFxCatalogue ?? NameFxCatalogue.None;
        NameFx = new NameFxService(StatsApi, props, catalogue.IsKnownEffect, catalogue.IsKnownGauge)
        {
            // dev 빌드는 스킨 선택을 서버로 보내지 않고 이 PC 에만 적용한다. 미터 절반이 먼저 나가는 일이
            // 잦아서, 웹이 배포되기 전까지 '고를 수는 있는데 아무 일도 안 일어나는' 상태가 계속되기 때문이다.
            // 릴리스는 `-p:WaffleVersion=x.y.z` 로 순수 semver 를 박으므로 여기 걸리지 않는다.
            LocalChoiceOnly = Version.Contains("-dev", StringComparison.OrdinalIgnoreCase),
        };

        // The only Data -> Stats edge: a saved battle log is offered to the upload queue. Also refresh
        // the history-panel snapshot (both run on the consumer thread inside the save).
        Calculator.OnBattleLogged = log =>
        {
            // Build the position replay FIRST so its durable file is on disk before anything that could
            // block: NotifyBattleListChanged marshals to the UI thread, which during app-shutdown is
            // itself waiting on this (consumer) thread — writing the replay first means the artifact
            // survives even if that notify can't complete. Isolated in try/catch because the replay engine
            // is an optional private module and must never break the parity-critical save/upload path.
            // 허수아비 런은 리플레이를 굽지 않는다. 제자리에 선 표적을 때리는 60초짜리 이동 기록은 볼 것이
            // 없는데, 연습 30번이면 replay-*.json 이 30개 쌓인다(허수아비가 기록에 남기 시작하면서 생긴
            // 새 경로다 — 예전에는 저장 자체가 막혀 여기까지 오지 않았다).
            if (RecordReplay && Movement is { } replay && log.Report.Target?.Mob.IsDummy != true)
            {
                try
                {
                    // kills AND wipes/직전 전투, scoped to the party/raid roster (empty roster = self+boss
                    // only, so a shared field boss never records bystanders); the diag line live-verifies
                    // the open questions (wipe fire, roster scoping, AoI coverage, self density).
                    IReadOnlyList<(string Nickname, int Server)> roster = Data.PartyMemberIdentities(30 * 60 * 1000L);
                    ReplayRecording rec = replay.OnBattleLogged(log, roster);
                    ReplayDiag.Log(props, log.Report, rec, roster.Count);

                    // OnBattleLogged just wrote replay-{startMs}.json into ReplayDirectory and the engine
                    // never prunes, so bound the folder to the most recent recordings here. Best-effort and
                    // self-contained (Prune swallows its own IO errors), so it can't disturb the save path.
                    ReplayRetention.Prune(ReplayDirectory);
                }
                catch
                {
                    // a replay failure must never disturb the DPS save/upload path or the consumer thread
                }
            }

            LogRaidSlotBinding(log);
            UploadQueue.OfferIfEligible(log); // 허수아비 런은 큐가 조용히 무시한다 (진단 카운터도 안 건드린다)
            NotifyBattleListChanged();
        };
    }

    /// <summary>One line per saved 공대 battle recording how the sub-party slots came out, because the stats
    /// site is all-or-nothing about them and its "N인 공대 시너지 구분 이전 지표" label says only that something
    /// went wrong, never what. This separates the three possibilities without a packet capture: the roster never
    /// arrived (<c>roster=0</c>), the slot bytes did not parse (<c>slotted</c> below <c>roster</c>), or the slots
    /// parsed but did not reach the battle's uids (<c>bound</c> below <c>participants</c>) — and in that last
    /// case it names who was missed. Consumer-thread only; never throws.</summary>
    private void LogRaidSlotBinding(DpsLog log)
    {
        try
        {
            DpsReport r = log.Report;
            if (r.PartyRosterSize is not (8 or 10))
            {
                return;
            }

            IReadOnlyList<(string Nickname, int Server, int Slot)> roster = Data.PartyRosterIdentities(30 * 60 * 1000L);
            List<User> participants = r.Contributors.Where(u => r.Information.ContainsKey(u.Id)).ToList();
            List<string> unslotted = participants
                .Where(u => !r.PartySlots.ContainsKey(u.Id))
                .Select(u => string.IsNullOrWhiteSpace(u.Nickname) ? $"uid{u.Id}" : u.Nickname)
                .ToList();

            BuffDiag.Write(string.Format(
                CultureInfo.InvariantCulture,
                "[raid] boss={0} roster={1} slotted={2} participants={3} bound={4} slots={5}{6}",
                r.Target?.Mob.Name ?? "?",
                r.PartyRosterSize,
                roster.Count(m => m.Slot > 0),
                participants.Count,
                r.PartySlots.Count,
                string.Join("/", r.PartySlots.Values.OrderBy(v => v)),
                unslotted.Count > 0 ? " missing=" + string.Join(",", unslotted) : ""));
        }
        catch
        {
            // diagnostics must never disturb the save path
        }
    }

    /// <summary>Loads the reference catalogs (mobs/skills/buffs/blacklist) from a json directory.</summary>
    public void LoadCatalogs(string jsonDir)
    {
        Data.LoadMobs(ReferenceJson.LoadMobs(Path.Combine(jsonDir, "mobs.json")));
        Data.LoadSkills(ReferenceJson.LoadSkills(Path.Combine(jsonDir, "skills.json")));

        // Instanced-content (원정/초월/성역) boss classification. Its one consumer is the roster rescue's
        // party-scene proof (<c>OverlayRowBuilder</c>: an instance has no outsiders, so a bare row there is a
        // party member). Optional: an older asset bundle without the file classifies no boss, which only costs
        // that rescue tier -- nothing else reads it.
        string contentTypes = Path.Combine(jsonDir, "content-types.json");
        if (File.Exists(contentTypes))
        {
            Data.LoadContentTypes(ReferenceJson.LoadContentTypes(contentTypes));
        }

        // Supported-encounter catalog (mobCode -> dungeon/difficulty/boss). Optional in the same way: without it
        // EncounterCatalog.Empty gates nothing and boss names keep their bare form.
        string encounters = Path.Combine(jsonDir, "encounters.json");
        if (File.Exists(encounters))
        {
            Data.LoadEncounters(EncounterCatalog.Load(encounters));
        }

        foreach (string buffFile in new[] { "buff.json", "buff_custom.json" })
        {
            string path = Path.Combine(jsonDir, buffFile);
            if (File.Exists(path))
            {
                Data.LoadBuffs(ReferenceJson.LoadBuffs(path));
            }
        }

        // 스킬 분류(액티브/패시브). 스킬 타임라인 탭이 패시브 프록을 빼는 데 쓴다. 없으면 아무 것도
        // 걸러지지 않으므로 옛 자산 번들에서도 그냥 예전 동작이 된다.
        string skillClass = Path.Combine(jsonDir, "skill_class.json");
        if (File.Exists(skillClass))
        {
            (var passive, var activeOverrides) = ReferenceJson.LoadSkillClass(skillClass);
            Data.LoadSkillClass(passive, activeOverrides);
        }

        string blacklist = Path.Combine(jsonDir, "buff_blacklist.json");
        if (File.Exists(blacklist))
        {
            Data.LoadBuffBlacklist(ReferenceJson.LoadBuffBlacklist(blacklist));
        }

        string buffValues = Path.Combine(jsonDir, "buff_values.json");
        if (File.Exists(buffValues))
        {
            Data.LoadBuffValues(ReferenceJson.LoadBuffValues(buffValues));
        }

        string buffNames = Path.Combine(jsonDir, "buff_names.json");
        if (File.Exists(buffNames))
        {
            Data.LoadBuffNames(ReferenceJson.LoadBuffNames(buffNames));
        }

        string buffCatalog = Path.Combine(jsonDir, "buff_catalog.json");
        if (File.Exists(buffCatalog))
        {
            (var catalog, var defaultOff) = ReferenceJson.LoadBuffCatalog(buffCatalog);
            Data.LoadBuffCatalog(catalog, defaultOff);
        }

        // Skill-cooldown catalog (names + shared-cooldown groups + order) for the cooldown overlay. Optional in
        // the same way as the others: without it group ids fall back to the plain fold — the buff overlay's gray
        // veil keeps working exactly as before and the cooldown overlay simply has no rows it can name.
        string cooldowns = Path.Combine(jsonDir, "cooldown_catalog.json");
        if (File.Exists(cooldowns))
        {
            Data.LoadCooldownCatalog(CooldownCatalog.Load(cooldowns));
        }
    }

    /// <summary>Diagnostic: total permanent-gap skips across the live streams — a capture-loss indicator for
    /// the buff-tracking diagnosis. Called on the consumer thread (same thread that mutates <c>_streams</c>).</summary>
    public long AlignerGapSkips()
    {
        long total = _gapSkipsRetired;
        foreach (StreamState s in _streams.Values)
        {
            total += s.Aligner.GapSkips;
        }

        return total;
    }

    /// <summary>Capture-stall diagnostics for buff-diag: how many streams are live, how many are stuck in the
    /// noise guard's blind spot (enough volume to be noise but not enough emitted frames to be classified),
    /// the self-heal / framer-reset tallies, and the WORST current aligner stall. Consumer-thread only.
    /// <c>StallMs</c> is 0 when nothing is stalled. This is what tells apart "packets never arrived" from
    /// "packets arrived and the app latched" — the distinction 초기화 recovering the meter already proved.</summary>
    public (int Streams, int GuardBlindSpot, long SelfHeals, long FramerResets, long StallMs, long StallHeldBytes, bool StallIsGame, bool StallIsPrimary) CaptureDiagSnapshot(long nowMs)
    {
        int blindSpot = 0;
        long selfHeals = _selfHealsRetired;
        long framerResets = 0;
        long worstMs = 0, worstHeld = 0;
        bool worstIsGame = false, worstIsPrimary = false;

        foreach ((string key, StreamState s) in _streams)
        {
            selfHeals += s.SelfHeals;
            framerResets += s.Assembler.ForcedResets;
            if (s.GameSignal == 0 && s.Bytes >= NoiseVolumeBytes && s.EmittedPackets < MinNoisePackets)
            {
                blindSpot++;
            }

            if (s.Aligner.GapOpenAtMs is not long open)
            {
                continue;
            }

            long stalled = nowMs - open;
            if (stalled > worstMs)
            {
                worstMs = stalled;
                worstHeld = s.Aligner.HeldBytes;
                worstIsGame = s.GameSignal > 0;
                worstIsPrimary = _primaryGameKey == key;
            }
        }

        return (_streams.Count, blindSpot, selfHeals, framerResets, worstMs, worstHeld, worstIsGame, worstIsPrimary);
    }

    /// <summary>Feeds one captured segment through its per-connection stream (Kotlin Main.kt consumer).</summary>
    public void Feed(CapturedSegment segment)
    {
        // Known noise (P2P/streaming flood): already classified — drop locally. The elevated helper also
        // drops it at the source, so these segments stop arriving shortly after the exclusion is sent.
        if (_excludedKeys.Contains(segment.StreamKey))
        {
            return;
        }

        // L0: log the raw segment (pre-alignment) for a diagnostic session — replayable corpus input.
        DebugLogger.Capture(segment.SrcIp, segment.Seq, segment.Payload, segment.ArrivedAtMs);

        if (!_streams.TryGetValue(segment.StreamKey, out StreamState? state))
        {
            string streamKey = segment.StreamKey; // captured for the closure (the key this state is stored under)
            StreamState? created = null;
            var assembler = new StreamAssembler((packet, at) =>
            {
                DebugLogger.Assembled(packet, at); // L1: reassembled application packet
                bool isGame = StreamProcessor.LooksLikeGamePacket(packet);
                if (created is not null)
                {
                    created.EmittedPackets++;
                    if (isGame)
                    {
                        if (created.GameSignal == 0)
                        {
                            // The 0->1 transition, breadcrumbed ONCE. This is the line that tells a real game
                            // connection from a false positive: LooksLikeGamePacket is a structural check
                            // (27 opcode keys + FF FF, p≈4.3e-4 per framed packet), so high-entropy noise
                            // eventually earns a game signal too — and that grants PERMANENT exemption from the
                            // noise guard (the GameSignal==0 test below never decays). A game stream earns this
                            // within the first few KB; a false positive shows up tens of MB in, so the byte
                            // count alone separates them.
                            DebugLogger.Meta("game_signal_first",
                                ("key", streamKey), ("bytes", created.Bytes), ("emitted", created.EmittedPackets));
                        }

                        created.GameSignal++; // content signal: this connection carries the game stream — protect it
                        _lastGameStreamKey = streamKey; // for the ping matcher (independent of the dedupe toggle)
                    }
                }

                // Single-game-stream lock: claim primary for the first/live game stream; suppress a
                // concurrent SECOND game stream (a VPN/accelerator mirroring the same plaintext bytes onto
                // another 4-tuple) so its damage isn't double-counted. Fail over to it only if the primary
                // has gone quiet for GameStreamHandoverMs (real reconnect / proxy port change). A lone game
                // stream always satisfies the first branch, so non-VPN users are unaffected.
                if (_dedupeGameStreams && isGame && created is not null)
                {
                    if (_primaryGameKey is null || _primaryGameKey == streamKey
                        || at - _primaryGameAt > GameStreamHandoverMs)
                    {
                        _primaryGameKey = streamKey;
                        _primaryGameAt = at;
                        created.SuppressedDuplicate = false;
                    }
                    else
                    {
                        if (!created.SuppressedDuplicate)
                        {
                            // Breadcrumb the 0->1 transition only (not every packet): a debug session now
                            // shows whether dual-capture is happening WITHOUT disabling the VPN.
                            DebugLogger.Meta("dup_game_stream_dropped",
                                ("key", streamKey), ("primary", _primaryGameKey));
                        }

                        created.SuppressedDuplicate = true;
                    }
                }

                if (created is { SuppressedDuplicate: true })
                {
                    // Damage/buff stay suppressed (single-stream lock → no VPN double-count), but the SECOND
                    // game connection frequently carries the party roster / member profiles / identity — which
                    // are idempotent — so replay ONLY those. Fixes a 10-인 공대 whose roster packets rode the
                    // suppressed connection and never reached the parser (empty/partial pre-combat roster).
                    _processor.OnPacketReceived(packet, at, identityOnly: true);
                    return;
                }

                if (RecordReplay)
                {
                    Movement?.Scan(packet, at); // parallel positional-replay tap (BETA; off = never called)
                }
                _processor.OnPacketReceived(packet, at);
            });
            created = new StreamState(new PacketAlignmenter(), assembler);
            state = created;
            _streams[segment.StreamKey] = state;
        }

        state.LastSeen = segment.ArrivedAtMs;
        state.Bytes += segment.Payload.Length;
        foreach (AlignedChunk chunk in state.Aligner.Feed(segment.Seq, segment.Payload, segment.ArrivedAtMs))
        {
            state.Assembler.ProcessChunk(chunk.Data, chunk.ArrivedAt);
        }

        SelfHealIfStalled(segment.StreamKey, state, segment.ArrivedAtMs);

        // Classify AFTER processing (so this segment's packets count first): a connection that has pushed
        // a lot of bytes AND emitted enough framed packets, none of which look like the game, is noise.
        // The game stream earns GameSignal within the first few KB, so it is protected long before this.
        if (state.GameSignal == 0 && state.EmittedPackets >= MinNoisePackets && state.Bytes >= NoiseVolumeBytes)
        {
            // Drop the stream state now, and breadcrumb it so a wrongful exclusion is diagnosable in a debug
            // session instead of a silent blackout. ⚠️ The durable part is _excludedKeys below, which is capped:
            // past the cap this becomes a counter reset rather than a drop (the next segment rebuilds the state
            // and it has to re-earn NoiseVolumeBytes all over again). See the backlog note on making it LRU.
            RetireStream(segment.StreamKey, state);
            if (_primaryGameKey == segment.StreamKey) _primaryGameKey = null; // free the lock if (defensively) it was primary
            DebugLogger.Meta("conn_excluded",
                ("key", segment.StreamKey), ("bytes", state.Bytes), ("packets", state.EmittedPackets));

            // Tell the helper to drop it at the source too (capped to bound the set under peer churn).
            if (_excludedKeys.Count < MaxExcludedKeys)
            {
                _excludedKeys.Add(segment.StreamKey);
                if (ConnKey.TryFrom(segment, out ConnKey key))
                {
                    ConnectionExcludeRequested?.Invoke(key);
                }
            }

            return;
        }

        // Sampled idle eviction (every 1000th packet), clocked off the incoming packet like Kotlin.
        if (++_processed % EvictEvery == 0)
        {
            long cutoff = segment.ArrivedAtMs - IdleMs;
            foreach ((string key, StreamState idle) in _streams.Where(kv => kv.Value.LastSeen < cutoff).ToList())
            {
                RetireStream(key, idle);
                if (_primaryGameKey == key) _primaryGameKey = null; // primary went idle — let the next game stream claim it
            }
        }
    }

    /// <summary>Drops a stream, carrying its loss counters into the retired totals first so the cumulative
    /// figures never move backwards. Consumer-thread only.</summary>
    private void RetireStream(string key, StreamState state)
    {
        _gapSkipsRetired += state.Aligner.GapSkips;
        _selfHealsRetired += state.SelfHeals;
        _streams.Remove(key);
    }

    /// <summary>Re-runs, for ONE stream, exactly what the user's 초기화 button runs for all of them — but only
    /// when that stream is provably stuck. Two triggers, both narrow:
    /// <para>(A) the aligner has held a head-of-line gap for <see cref="DefaultSelfHealGapMs"/> while segments keep
    /// arriving. Its own escape hatch is 2MB of FURTHER traffic on the same connection, which at a measured
    /// game-stream rate of 1.2-3.8 KB/s is 8-28 minutes of a completely silent meter: no damage, no buffs, no
    /// battle toggle, no boss HP. The framer is flushed with it because a discarded gap has already broken the
    /// frame boundary.</para>
    /// <para>(B) a stream that has never yielded a game packet is holding a multi-MB framer buffer — a false
    /// realLength read out of high-entropy bytes. It emits nothing, so the noise guard's MinNoisePackets gate
    /// can never fire on it, and it grows toward the 32MB accumulator cap. Streams WITH a game signal are
    /// exempt, so the large snapshot frames that cap exists for are never cut.</para>
    /// Either way the suppression flag and (if this stream held it) the primary-game lock are released: a stream
    /// that cannot emit must not keep another one suppressed. Consumer-thread only; never throws.</summary>
    private void SelfHealIfStalled(string streamKey, StreamState state, long nowMs)
    {
        if (_selfHealGapMs <= 0)
        {
            return;
        }

        bool alignerStalled = state.Aligner.GapOpenAtMs is long openedAt && nowMs - openedAt >= _selfHealGapMs;
        bool framerStuck = state.GameSignal == 0
            && state.Bytes >= NoiseFramerGraceBytes
            && state.Assembler.BufferedBytes >= NoiseFramerHoldBytes;

        if (!alignerStalled && !framerStuck)
        {
            return;
        }

        DebugLogger.Meta("stream_self_heal",
            ("key", streamKey),
            ("reason", alignerStalled ? "aligner_gap" : "framer_hold"),
            ("stalledMs", alignerStalled ? nowMs - state.Aligner.GapOpenAtMs!.Value : 0),
            ("heldBytes", state.Aligner.HeldBytes),
            ("bufferedBytes", state.Assembler.BufferedBytes),
            ("pendingLength", state.Assembler.PendingRealLength),
            ("gameSignal", state.GameSignal));

        if (alignerStalled)
        {
            state.Aligner.Reset();
        }

        state.Assembler.Flush();
        state.SuppressedDuplicate = false;
        state.SelfHeals++;
        if (_primaryGameKey == streamKey)
        {
            _primaryGameKey = null; // a stalled primary must not go on suppressing the others
        }
    }

    private void FlushAllStreams()
    {
        foreach (StreamState state in _streams.Values)
        {
            try
            {
                state.Assembler.Flush();
                state.Aligner.Reset();
                state.SuppressedDuplicate = false;
            }
            catch
            {
                // one stream's reset failure must not abort the rest
            }
        }

        _primaryGameKey = null; // a user reset re-selects the primary game stream from scratch
        Movement?.Reset(); // drop buffered movement + stored replays on a user reset
    }

    /// <summary>The live DPS report (must be called on the same thread as <see cref="Feed"/>).</summary>
    public DpsReport GetReport() => Calculator.GetDps();

    /// <summary>Builds capture config from settings (server.ip/port/timeout/maxSnapshotSize).</summary>
    public CaptureConfig BuildCaptureConfig() => CaptureConfig.FromProperties(Props.GetProperty);
}
