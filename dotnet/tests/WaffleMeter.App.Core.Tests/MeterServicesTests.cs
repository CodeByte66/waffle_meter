using WaffleMeter.App.Core;
using WaffleMeter.Capture;
using WaffleMeter.Capture.Corpus;
using WaffleMeter.Capture.Live;
using WaffleMeter.Data;
using WaffleMeter.Services;
using WaffleMeter.Stats;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

public sealed class MeterServicesTests : IDisposable
{
    private readonly string _temp;
    private readonly MeterServices _services;

    public MeterServicesTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "wm_appcore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _services = new MeterServices(new PropertyHandler(_temp));
    }

    public void Dispose()
    {
        _services.UploadQueue.Dispose();
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void Wires_the_full_object_graph()
    {
        Assert.NotNull(_services.Data);
        Assert.NotNull(_services.Calculator);
        Assert.NotNull(_services.OfficialLookup);
        Assert.Same(_services.OfficialLookup, GetInjectedLookup(_services));
        Assert.NotNull(_services.Consent);
        Assert.NotNull(_services.StatsBuilder);
        Assert.NotNull(_services.UploadQueue);
        Assert.NotNull(_services.Calculator.OnBattleLogged); // Data -> Stats edge wired
        // Version comes from the build (assembly InformationalVersion), not a persisted property;
        // the injectable appVersion path is the deterministic one to assert.
        Assert.False(string.IsNullOrWhiteSpace(_services.Version));
        Assert.Equal("9.9.9-test", new MeterServices(new PropertyHandler(_temp), appVersion: "9.9.9-test").Version);

        static IOfficialCharacterLookup? GetInjectedLookup(MeterServices s) => s.Data.OfficialLookup;
    }

    [Fact]
    public void Battle_log_is_routed_to_the_upload_queue()
    {
        // Consent is unknown by default -> upload not allowed -> the queue skips with that reason.
        // This proves OnBattleLogged actually reaches the queue (no network needed).
        var log = new DpsLog { Report = new DpsReport() };

        _services.Calculator.OnBattleLogged!(log);

        StatsUploadStatus status = _services.UploadQueue.Status();
        Assert.Equal(1, status.Skipped);
        Assert.Equal("consent_not_allowed", status.LastReason);
        Assert.Equal(0, status.Uploaded);
    }

    [Fact]
    public void Build_capture_config_uses_settings_defaults()
    {
        CaptureConfig config = _services.BuildCaptureConfig();
        Assert.Equal("206.127.156.0/24", config.ServerIp);
        Assert.Equal("13328", config.ServerPort);
    }

    [Fact]
    public void Feed_logs_each_segment_as_a_capture_line_with_the_right_fields()
    {
        // Proves the MeterServices -> DebugLogger.Capture wiring passes the segment's ip/seq/data/at
        // (not just that the logger formats correctly). Inject a logger pointed at a temp dir so the
        // test never touches the real %APPDATA% packet-debug-logs.
        string logDir = Path.Combine(_temp, "pdl");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(new CapturedSegment(42, new byte[] { 0x0A, 0x0B }, 12345, "9.9.9.9", 1, "8.8.8.8", 2));
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Contains(
            "{\"type\":\"capture\",\"at\":12345,\"ip\":\"9.9.9.9\",\"seq\":42,\"len\":2,\"head\":\"0A 0B\",\"data\":\"Cgs=\"}",
            lines);
    }

    [Fact]
    public void Dual_capture_of_the_game_stream_is_counted_once()
    {
        // The VPN dual-capture shape: the SAME game packet (opcode 0x3804) arrives under two different
        // 4-tuples (distinct src ports). The second connection is a duplicate of the game stream and must
        // be suppressed — otherwise every damage event would be dispatched (and SaveDamage'd) twice → ~2x.
        string logDir = Path.Combine(_temp, "pdl_dual");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1000, srcPort: 5000));
        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1000, srcPort: 5001));
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Equal(1, lines.Count(l => l.Contains("\"type\":\"dispatch\"")));               // counted once
        Assert.Equal(1, lines.Count(l => l.Contains("\"type\":\"dup_game_stream_dropped\""))); // breadcrumb fired
    }

    [Fact]
    public void Single_game_stream_is_never_suppressed()
    {
        // Non-VPN users have ONE game connection: both packets on it dispatch, nothing is dropped.
        string logDir = Path.Combine(_temp, "pdl_single");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1000, srcPort: 5000));
        services.Value.Feed(ClockedGameSegment(seq: 16, at: 1001, srcPort: 5000)); // same 4-tuple, next contiguous seq
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Equal(2, lines.Count(l => l.Contains("\"type\":\"dispatch\"")));
        Assert.DoesNotContain(lines, l => l.Contains("dup_game_stream_dropped"));
    }

    [Fact]
    public void Quiet_primary_fails_over_to_the_surviving_game_stream()
    {
        // Real reconnect / proxy port change: the primary goes quiet, so after the handover window the
        // other game stream takes over and IS dispatched (no permanent blackout).
        string logDir = Path.Combine(_temp, "pdl_failover");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1000, srcPort: 5000));        // A claims primary → dispatched
        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1000, srcPort: 5001));        // B concurrent → suppressed
        services.Value.Feed(ClockedGameSegment(seq: 16, at: 7001, srcPort: 5001));        // > handover later → B takes over
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Equal(2, lines.Count(l => l.Contains("\"type\":\"dispatch\""))); // A's first + B's failover packet
    }

    [Fact]
    public void Dedup_is_disabled_when_the_property_is_off()
    {
        // Escape hatch: capture.dedupeGameStreams=false restores the old (double-counting) behavior.
        var props = new PropertyHandler(_temp);
        props.SetProperty("capture.dedupeGameStreams", "false");
        string logDir = Path.Combine(_temp, "pdl_off");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(props, debugLogger: logger));

        logger.Start();
        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1000, srcPort: 5000));
        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1000, srcPort: 5001));
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Equal(2, lines.Count(l => l.Contains("\"type\":\"dispatch\""))); // both dispatched (lock off)
        Assert.DoesNotContain(lines, l => l.Contains("dup_game_stream_dropped"));
    }

    [Fact]
    public void A_suppressed_stream_still_replays_the_party_roster_but_not_damage()
    {
        // The SECOND game connection (VPN mirror / a separate party server link) is suppressed to stop damage
        // double-counting — but it frequently carries the party roster / member profiles / identity, which are
        // IDEMPOTENT. Those MUST still be replayed from the suppressed stream (a 10-인 공대's roster rode the
        // suppressed connection and was lost). Damage on the suppressed stream stays dropped.
        string logDir = Path.Combine(_temp, "pdl_identity_replay");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1000, srcPort: 5000));   // A claims primary (damage) → dispatched
        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1000, srcPort: 5001));   // B suppressed (damage) → dropped
        services.Value.Feed(RosterSegment(seq: 16, at: 1001, srcPort: 5001)); // B suppressed (roster) → STILL replayed
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Equal(1, lines.Count(l => l.Contains("\"opcodeHex\":\"3804\""))); // damage: primary only (suppressed dropped)
        Assert.Equal(1, lines.Count(l => l.Contains("\"opcodeHex\":\"9702\""))); // roster: replayed from the suppressed stream
    }

    [Fact]
    public void A_loopback_text_false_positive_never_holds_primary_and_game_damage_is_counted()
    {
        // 10-16 실측 재현: NAVER 스트리밍 커넥터의 루프백 HTTP 요청이 User-Agent 의 ".36 " 에서 프레이밍돼
        // 0x3633 으로 읽히고(isGame), 게임보다 먼저 primary 를 잡아 요청마다(1Hz) 갱신했다. 진짜 게임 스트림은
        // 세션 내내 identityOnly 로 묶여 데미지가 0/91 이었다. 서버 시계 자격이 없는 스트림은 primary 를 못 쥔다.
        string logDir = Path.Combine(_temp, "pdl_ua_hijack");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        long uaSeq = 0, gameSeq = 0;
        int damage = 0;
        for (long at = 0; at <= 20_000; at += 100)
        {
            if (at % 1000 == 0)
            {
                services.Value.Feed(UaFrameSegment(uaSeq, at));
                uaSeq += UaFrame.Length;
            }

            if (at >= 300 && at % 500 == 300)
            {
                services.Value.Feed(ClockedGameSegment(gameSeq, at, srcPort: 5000));
                gameSeq += 16;
                damage++;
            }
        }

        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Equal(damage, lines.Count(l => l.Contains("\"opcodeHex\":\"3804\"")));
        Assert.DoesNotContain(lines, l => l.Contains("\"type\":\"primary_claim\"") && l.Contains("127.0.0.1"));
        Assert.Contains(lines, l => l.Contains("\"type\":\"primary_claim_skipped_text\"") && l.Contains("127.0.0.1"));
    }

    [Fact]
    public void A_chance_hit_during_an_idle_gap_cannot_take_primary_from_the_game()
    {
        // 마을 대기: 진짜 S2C 도 isGame 공백이 10초를 넘는다(실측 최장 10.75초, 0x3600 은 isGame 이 아니다).
        // 종전엔 그 틈에 노이즈·역방향의 우연한 isGame 이 primary 를 잡고, 돌아온 게임 데미지를 억제했다.
        // 자격 없는 도전자는 그 틈에 임시 primary 만 잡고, 게임이 돌아오는 즉시 넘겨준다.
        string logDir = Path.Combine(_temp, "pdl_idle_gap");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1000, srcPort: 5000));
        long seq = 16, noiseSeq = 0;
        for (long at = 1500; at < 9500; at += 500)
        {
            services.Value.Feed(new CapturedSegment(seq, MapFrame(at), at, "10.0.0.1", 5000, "10.0.0.2", 13328));
            seq += 11;
            if (at % 2000 == 1000)
            {
                // 역방향(클라→서버)의 우연한 0x3804 — 3000/5000/7000/9000
                services.Value.Feed(new CapturedSegment(noiseSeq, DamageFrame, at, "10.0.0.2", 13328, "10.0.0.1", 5000));
                noiseSeq += DamageFrame.Length;
            }
        }

        services.Value.Feed(new CapturedSegment(seq, DamageFrame, 9500, "10.0.0.1", 5000, "10.0.0.2", 13328));
        logger.Stop();

        string[] lines = ReadLog(logDir);
        // 게임 1000 + (3000·5000 은 살아 있는 primary 에 억제) + 7000·9000 임시 primary + 게임 9500 = 4. 종전 코드는 3.
        Assert.Equal(4, lines.Count(l => l.Contains("\"opcodeHex\":\"3804\"")));
        Assert.Contains(lines, l => l.Contains("\"reason\":\"preempt\"") && l.Contains("\"key\":\"10.0.0.1:5000-"));
    }

    [Fact]
    public void The_credential_frame_claims_primary_when_a_game_packet_was_framed_first()
    {
        // 0808 실측 순서: 첫 isGame 이 0x3600 보다 152ms 먼저 왔다. 그 사이에도 VPN 미러는 한 번만 세야 한다
        // (종전 락이 막던 구간) — 첫 isGame 이 임시 primary 를 잡고, 자격 프레임이 그걸 정식으로 올린다.
        string logDir = Path.Combine(_temp, "pdl_credential_late");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(GameSegment(seq: 0, at: 1000, srcPort: 5000));
        services.Value.Feed(GameSegment(seq: 0, at: 1001, srcPort: 5001)); // VPN 미러 — 둘 다 아직 자격 없음
        services.Value.Feed(new CapturedSegment(5, MapFrame(1150), 1150, "10.0.0.1", 5000, "10.0.0.2", 13328));
        services.Value.Feed(new CapturedSegment(5, MapFrame(1150), 1151, "10.0.0.1", 5001, "10.0.0.2", 13328));
        services.Value.Feed(new CapturedSegment(16, DamageFrame, 1200, "10.0.0.1", 5000, "10.0.0.2", 13328));
        services.Value.Feed(new CapturedSegment(16, DamageFrame, 1201, "10.0.0.1", 5001, "10.0.0.2", 13328));
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Equal(2, lines.Count(l => l.Contains("\"opcodeHex\":\"3804\""))); // 미러는 한 번도 안 셌다
        Assert.Contains(lines, l => l.Contains("\"reason\":\"provisional\"") && l.Contains(":5000-"));
        Assert.Contains(lines, l => l.Contains("\"reason\":\"credential\"") && l.Contains(":5000-"));
        Assert.Single(lines, l => l.Contains("\"type\":\"dup_game_stream_dropped\""));
    }

    [Fact]
    public void Server_clock_frames_alone_do_not_keep_a_stale_primary_alive()
    {
        // 재접속: 옛 연결이 0x3600 만 계속 흘리는 동안 새 연결이 데미지를 싣는다. 0x3600 으로 primary 를 갱신하면
        // 새 연결이 영영 억제된다 — 갱신은 isGame 으로만 하고, 자격은 '쥘 수 있느냐'만 정한다.
        string logDir = Path.Combine(_temp, "pdl_stale_clock");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1000, srcPort: 5000));
        long seq = 16;
        for (long at = 1500; at <= 7000; at += 500)
        {
            services.Value.Feed(new CapturedSegment(seq, MapFrame(at), at, "10.0.0.1", 5000, "10.0.0.2", 13328));
            seq += 11;
        }

        services.Value.Feed(ClockedGameSegment(seq: 0, at: 6500, srcPort: 5001));
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Equal(2, lines.Count(l => l.Contains("\"opcodeHex\":\"3804\"")));
        Assert.Contains(lines, l => l.Contains("\"reason\":\"handover\"") && l.Contains(":5001-"));
    }

    [Fact]
    public void Without_a_server_clock_the_lock_still_dedupes_and_yields_to_the_first_credential()
    {
        // 0x3600 이 아직 안 왔거나 패치로 깨진 경우: 임시 primary 가 종전처럼 VPN 이중 집계를 막는다.
        // 자격 있는 스트림이 나타나면 임시 primary 가 살아 있어도 즉시 넘겨준다.
        string logDir = Path.Combine(_temp, "pdl_drought");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        for (int i = 0; i < 60; i++)
        {
            services.Value.Feed(GameSegment(seq: i * 5, at: 1000 + i, srcPort: 5000));
            services.Value.Feed(GameSegment(seq: i * 5, at: 1000 + i, srcPort: 5001)); // 미러
        }

        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1100, srcPort: 5002));
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Contains(lines, l => l.Contains("\"reason\":\"provisional\"") && l.Contains(":5000-"));
        Assert.Contains(lines, l => l.Contains("\"type\":\"dup_game_stream_dropped\"") && l.Contains(":5001-"));
        Assert.Contains(lines, l => l.Contains("\"reason\":\"preempt\"") && l.Contains(":5002-"));
        // A 60 + 미러 0 + C 1
        Assert.Equal(61, lines.Count(l => l.Contains("\"opcodeHex\":\"3804\"")));
    }

    [Fact]
    public void Plain_text_never_takes_the_lock_even_provisionally()
    {
        string logDir = Path.Combine(_temp, "pdl_drought_text");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        for (int i = 0; i < 60; i++)
        {
            services.Value.Feed(UaFrameSegment(i * UaFrame.Length, 1000 + i * 1000));
        }

        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.DoesNotContain(lines, l => l.Contains("\"type\":\"primary_claim\""));
        Assert.Single(lines, l => l.Contains("\"type\":\"primary_claim_skipped_text\""));
    }

    [Fact]
    public void An_http_request_connection_never_earns_a_game_signal()
    {
        // 커넥터 요청 연결은 첫 세그먼트가 HTTP 요청 헤드다. 그 연결은 평문이 우연히 만든 opcode 로 게임 신호를
        // 얻지 못한다(얻으면 노이즈 가드에서 영구 면제된다).
        string logDir = Path.Combine(_temp, "pdl_http_mark");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(new CapturedSegment(0, HttpHead, 0, "127.0.0.1", 49638, "127.0.0.1", 17080));
        for (int i = 0; i < 30; i++)
        {
            services.Value.Feed(new CapturedSegment(HttpHead.Length + i * DamageFrame.Length, DamageFrame, 1000 + i,
                "127.0.0.1", 49638, "127.0.0.1", 17080));
        }

        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Single(lines, l => l.Contains("\"type\":\"nongame_conn\""));
        Assert.DoesNotContain(lines, l => l.Contains("\"type\":\"game_signal_first\""));
    }

    [Fact]
    public void A_marked_connection_loses_its_game_signal_and_the_noise_guard_can_exclude_it()
    {
        // 종전엔 우연한 isGame 한 번이 영구 면제였다. 반대 방향이 HTTP 요청으로 연결을 드러내면 그 면제를 거두고,
        // 기존 노이즈 가드(50프레임 · 2MB)가 영상 방향을 걸러낸다.
        string logDir = Path.Combine(_temp, "pdl_http_reset");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        long seq = 0;
        for (int i = 0; i < 60; i++)
        {
            services.Value.Feed(new CapturedSegment(seq, DamageFrame, 1000 + i, "127.0.0.1", 17080, "127.0.0.1", 49638));
            seq += DamageFrame.Length;
        }

        services.Value.Feed(new CapturedSegment(0, HttpHead, 2000, "127.0.0.1", 49638, "127.0.0.1", 17080));
        services.Value.Feed(new CapturedSegment(seq, new byte[2_000_000], 2001, "127.0.0.1", 17080, "127.0.0.1", 49638));
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Contains(lines, l => l.Contains("\"type\":\"nongame_signal_reset\"") && l.Contains("\"gameSignal\":60"));
        Assert.Contains(lines, l => l.Contains("\"type\":\"conn_excluded\"") && l.Contains("127.0.0.1:17080-"));
    }

    [Fact]
    public void A_connect_tunnel_carrying_the_game_is_never_marked()
    {
        // 부스터·프록시는 CONNECT 로 터널을 열고 그 연결에 게임을 싣는다 — 거부 목록에 올라 영영 표시되지 않는다.
        string logDir = Path.Combine(_temp, "pdl_http_connect");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        byte[] connect = System.Text.Encoding.ASCII.GetBytes(
            "CONNECT 206.127.156.103:13328 HTTP/1.1\r\nHost: 206.127.156.103:13328\r\n\r\n");
        services.Value.Feed(new CapturedSegment(0, connect, 900, "10.0.0.2", 5000, "10.0.0.1", 8080));
        // 터널 안에서 평범한 요청 헤드가 다시 지나가도(프록시가 넘겨 주는 HTTP) 이 연결은 이미 거부 목록이다.
        services.Value.Feed(new CapturedSegment(connect.Length, HttpHead, 950, "10.0.0.2", 5000, "10.0.0.1", 8080));
        for (int i = 0; i < 5; i++)
        {
            services.Value.Feed(new CapturedSegment(i * 16, [.. MapFrame(1000 + i), .. DamageFrame], 1000 + i,
                "10.0.0.1", 8080, "10.0.0.2", 5000));
        }

        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Equal(5, lines.Count(l => l.Contains("\"opcodeHex\":\"3804\"")));
        Assert.DoesNotContain(lines, l => l.Contains("\"type\":\"nongame_conn\""));
    }

    [Fact]
    public void A_request_head_after_the_server_clock_never_marks_the_game_connection()
    {
        // 미터를 세션 도중에 켜면 S2C 가 먼저 0x3600 자격을 얻는다. 그 뒤 C2S 에 요청 헤드가 실려도(요청마다
        // 헤드를 붙이는 HTTP 터널) 그 연결은 이미 게임으로 확인됐다 — C2S 가 표시돼 게임 신호를 잃으면
        // 노이즈 가드가 게임의 C2S 를 헬퍼에서 끊을 수 있다.
        string logDir = Path.Combine(_temp, "pdl_http_after_clock");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(new CapturedSegment(0, [.. MapFrame(1000), .. DamageFrame], 1000, "10.0.0.1", 8080, "10.0.0.2", 5000));
        services.Value.Feed(new CapturedSegment(0, HttpHead, 1100, "10.0.0.2", 5000, "10.0.0.1", 8080));
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.DoesNotContain(lines, l => l.Contains("\"type\":\"nongame_conn\""));
    }

    [Fact]
    public void A_marked_tunnel_behind_a_stale_primary_loses_nothing_and_takes_the_lock_once_clocked()
    {
        // 평범한 GET 으로 여는 HTTP 터널 위의 게임이 재접속했다. 옛 연결(자격 primary)은 5초 넘게 조용하다.
        // 새 연결의 첫 0x3600 이전 프레임은 표시돼 있어도 풀 처리돼야 하고(살아 있는 primary 가 없다),
        // 0x3600 이 표시를 거둔 뒤의 프레임은 정상적으로 게임 신호를 얻어 primary 를 넘겨받아야 한다.
        string logDir = Path.Combine(_temp, "pdl_http_revoke");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1000, srcPort: 6000)); // 옛 연결, 이후 침묵
        services.Value.Feed(new CapturedSegment(0, HttpHead, 7000, "10.0.0.2", 5000, "10.0.0.1", 8080));
        services.Value.Feed(new CapturedSegment(0, [.. DamageFrame, .. MapFrame(7100), .. DamageFrame], 7100,
            "10.0.0.1", 8080, "10.0.0.2", 5000));
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Single(lines, l => l.Contains("\"type\":\"nongame_conn\""));
        Assert.Single(lines, l => l.Contains("\"type\":\"nongame_revoked\""));
        Assert.Equal(3, lines.Count(l => l.Contains("\"opcodeHex\":\"3804\""))); // 옛 1 + 새 2(0x3600 앞뒤)
        Assert.Contains(lines, l => l.Contains("\"type\":\"primary_claim\"") && l.Contains("\"key\":\"10.0.0.1:8080-"));
    }

    [Fact]
    public void A_stalled_stream_self_heals_and_resumes_dispatching()
    {
        // The reported failure: with a NAVER Live Streaming Connector session running, the meter stopped
        // showing combat and only 초기화 brought it back — every time. That button is the ONLY caller of
        // FlushAllStreams, and an active stream never hits the 30 s idle eviction, so a stream that latches on a
        // head-of-line gap emits NOTHING until 2MB more arrives on that same connection (8-28 minutes at a
        // measured game-stream rate). Self-heal runs the same recovery automatically, scoped to that stream.
        string logDir = Path.Combine(_temp, "pdl_selfheal");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(GameSegment(seq: 0, at: 1000, srcPort: 5000));    // dispatched; next expected = 5
        services.Value.Feed(GameSegment(seq: 100, at: 1001, srcPort: 5000));  // gap -> held, stall opens
        services.Value.Feed(GameSegment(seq: 200, at: 5001, srcPort: 5000));  // 4 s of silence -> self-heal
        services.Value.Feed(GameSegment(seq: 900, at: 5002, srcPort: 5000));  // re-synced -> dispatched again
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Equal(1, lines.Count(l => l.Contains("\"type\":\"stream_self_heal\"")));
        Assert.Contains(lines, l => l.Contains("\"reason\":\"aligner_gap\""));
        Assert.Equal(2, lines.Count(l => l.Contains("\"opcodeHex\":\"3804\""))); // pre-stall + post-heal
    }

    [Fact]
    public void An_ordinary_reorder_is_not_self_healed()
    {
        // Out-of-order delivery inside the window is the aligner doing its job — healing there would throw away
        // packets that were about to arrive. Only a gap that stays open past the threshold is a capture loss.
        string logDir = Path.Combine(_temp, "pdl_reorder");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(GameSegment(seq: 0, at: 1000, srcPort: 5000));   // next expected = 5
        services.Value.Feed(GameSegment(seq: 10, at: 1001, srcPort: 5000));  // arrives early -> held
        services.Value.Feed(GameSegment(seq: 5, at: 1500, srcPort: 5000));   // fills it 0.5 s later -> both drain
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.DoesNotContain(lines, l => l.Contains("stream_self_heal"));
        Assert.Equal(3, lines.Count(l => l.Contains("\"opcodeHex\":\"3804\""))); // nothing was thrown away
    }

    [Fact]
    public void Self_heal_is_disabled_when_the_property_is_zero()
    {
        // Escape hatch on a capture-path behavior change: capture.selfHealGapMs=0 restores the old behavior,
        // where a latched stream only recovered when the user pressed 초기화.
        var props = new PropertyHandler(_temp);
        props.SetProperty("capture.selfHealGapMs", "0");
        string logDir = Path.Combine(_temp, "pdl_heal_off");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(props, debugLogger: logger));

        logger.Start();
        services.Value.Feed(GameSegment(seq: 0, at: 1000, srcPort: 5000));
        services.Value.Feed(GameSegment(seq: 100, at: 1001, srcPort: 5000));
        services.Value.Feed(GameSegment(seq: 200, at: 5001, srcPort: 5000));
        services.Value.Feed(GameSegment(seq: 900, at: 5002, srcPort: 5000));
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.DoesNotContain(lines, l => l.Contains("stream_self_heal"));
        Assert.Equal(1, lines.Count(l => l.Contains("\"opcodeHex\":\"3804\""))); // stays latched after the gap
    }

    [Fact]
    public void A_noise_stream_stuck_on_a_bogus_frame_length_is_cut_loose()
    {
        // High-entropy video/P2P bytes make the framer read a false multi-MB realLength. It then emits nothing,
        // so the noise guard's MinNoisePackets gate can NEVER fire on it, and it grows toward the 32MB
        // accumulator cap (a 64MB array that never shrinks) — with one such stream per peer direction.
        string logDir = Path.Combine(_temp, "pdl_framer_noise");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        FeedBogusFrame(services.Value, srcPort: 5100, withGameSignalFirst: false);
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Contains(lines, l => l.Contains("\"reason\":\"framer_hold\""));
    }

    [Fact]
    public void A_large_frame_on_a_proven_game_stream_is_never_cut_short()
    {
        // The safety half of the same rule: a dungeon/boss-room snapshot frame legitimately spans megabytes —
        // that is exactly why the accumulator cap was raised to 32MB (c683495, 바크론 보스 인식). A connection
        // that has proven itself a game stream is exempt, so self-heal can never re-open that regression.
        string logDir = Path.Combine(_temp, "pdl_framer_game");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        FeedBogusFrame(services.Value, srcPort: 5200, withGameSignalFirst: true);
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.DoesNotContain(lines, l => l.Contains("stream_self_heal"));
    }

    [Fact]
    public void An_idle_game_connection_carrying_only_the_server_clock_is_never_excluded()
    {
        // 마을 대기: 진짜 S2C 가 0x3600 만 실어 오는 동안은 게임 신호가 0 이다(실측 186초·42KB). 그대로 2MB 에
        // 닿으면(≈2.4시간 AFK) 노이즈 가드가 게임 연결을 헬퍼에서 끊었다. 서버 시계 자격이 그걸 막는다.
        string logDir = Path.Combine(_temp, "pdl_idle_clock");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        byte[] clocks = new byte[11 * 200_000]; // 2.2MB = MapFrame 200,000개
        for (int i = 0; i < 200_000; i++) MapFrame(1000 + i).CopyTo(clocks, i * 11);
        services.Value.Feed(new CapturedSegment(0, clocks, 1000, "10.0.0.1", 5000, "10.0.0.2", 13328));
        services.Value.Feed(new CapturedSegment(clocks.Length, DamageFrame, 2000, "10.0.0.1", 5000, "10.0.0.2", 13328));
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.DoesNotContain(lines, l => l.Contains("\"type\":\"conn_excluded\""));
        Assert.Single(lines, l => l.Contains("\"opcodeHex\":\"3804\"")); // 대기 뒤의 전투가 그대로 잡힌다
    }

    [Fact]
    public void The_client_to_server_direction_of_a_clocked_connection_is_never_excluded()
    {
        // C2S 는 0x3600 을 싣지 않고 게임 opcode 도 우연히만 맞는다. 같은 연결의 S2C 가 자격을 얻었으면 보호된다.
        string logDir = Path.Combine(_temp, "pdl_c2s_protected");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(ClockedGameSegment(seq: 0, at: 1000, srcPort: 5000));
        FeedUnknownFrames(services.Value, "10.0.0.2", 13328, "10.0.0.1", 5000, megabytes: 2.1);
        logger.Stop();

        Assert.DoesNotContain(ReadLog(logDir), l => l.Contains("\"type\":\"conn_excluded\""));
    }

    [Fact]
    public void A_lone_chance_hit_no_longer_exempts_a_noise_stream_for_life()
    {
        // 무거운 P2P 연결은 판정 전에 우연히 게임 opcode 를 한 번 맞히곤 한다(연결당 2~3%). 예전엔 그 한 번이
        // 영구 면제였다(실측 37MB 피어가 그렇게 남았다). 이제는 서버 시계나 세 번 이상의 신호가 있어야 한다.
        string logDir = Path.Combine(_temp, "pdl_lone_hit");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        services.Value.Feed(new CapturedSegment(0, DamageFrame, 900, "10.9.9.9", 6880, "10.0.0.2", 50000));
        FeedUnknownFrames(services.Value, "10.9.9.9", 6880, "10.0.0.2", 50000, megabytes: 2.1, seq: DamageFrame.Length);
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.Contains(lines, l => l.Contains("\"type\":\"game_signal_first\"") && l.Contains("10.9.9.9"));
        Assert.Contains(lines, l => l.Contains("\"type\":\"conn_excluded\"") && l.Contains("10.9.9.9"));
    }

    [Fact]
    public void Text_frames_never_count_as_a_game_signal()
    {
        // 요청 헤드를 못 본 평문 연결(미터보다 먼저 열린 연결, HTTP/2 등)도 우연한 opcode 로 면제를 얻지 못한다.
        string logDir = Path.Combine(_temp, "pdl_text_signal");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        for (int i = 0; i < 10; i++)
        {
            services.Value.Feed(new CapturedSegment(i * UaFrame.Length, UaFrame, 900 + i, "127.0.0.1", 17080, "127.0.0.1", 49700));
        }

        FeedUnknownFrames(services.Value, "127.0.0.1", 17080, "127.0.0.1", 49700, megabytes: 2.1, seq: 10 * UaFrame.Length);
        logger.Stop();

        string[] lines = ReadLog(logDir);
        Assert.DoesNotContain(lines, l => l.Contains("\"type\":\"game_signal_first\""));
        Assert.Contains(lines, l => l.Contains("\"type\":\"conn_excluded\""));
    }

    [Theory]
    [InlineData(3, false)] // 서버 시계를 못 본 진짜 게임 경로(시계 없는 미러·릴레이 등)는 신호 3회로 보호된다
    [InlineData(2, true)]  // 우연한 hit 두 번으로는 면제되지 않는다
    public void Without_a_server_clock_three_game_signals_protect_a_stream_and_two_do_not(int hits, bool excluded)
    {
        string logDir = Path.Combine(_temp, $"pdl_hits_{hits}");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        for (int i = 0; i < hits; i++)
        {
            services.Value.Feed(new CapturedSegment(i * DamageFrame.Length, DamageFrame, 900 + i, "10.0.0.1", 5300, "10.0.0.2", 13328));
        }

        FeedUnknownFrames(services.Value, "10.0.0.1", 5300, "10.0.0.2", 13328, megabytes: 2.1, seq: hits * DamageFrame.Length);
        logger.Stop();

        Assert.Equal(excluded, ReadLog(logDir).Any(l => l.Contains("\"type\":\"conn_excluded\"")));
    }

    [Fact]
    public void A_large_frame_after_three_game_signals_is_never_cut_short_even_without_a_clock()
    {
        // 재접속 직후 0x3600 보다 먼저 거대한 스냅샷 프레임이 오는 경우 — 게임 신호 3회면 framer_hold 가 자르지 않는다.
        string logDir = Path.Combine(_temp, "pdl_framer_hits");
        var logger = new PacketDebugLogger(logDir);
        using var services = new ServicesScope(new MeterServices(new PropertyHandler(_temp), debugLogger: logger));

        logger.Start();
        for (int i = 0; i < 3; i++)
        {
            services.Value.Feed(new CapturedSegment(i * DamageFrame.Length, DamageFrame, 900 + i, "10.0.0.1", 5400, "10.0.0.2", 13328));
        }

        long seq = 3 * DamageFrame.Length;
        byte[] header = { 0x80, 0x87, 0xA7, 0x0E }; // realLength 30,000,000
        services.Value.Feed(new CapturedSegment(seq, header, 1000, "10.0.0.1", 5400, "10.0.0.2", 13328));
        seq += header.Length;
        for (int i = 0; i < 5; i++)
        {
            services.Value.Feed(new CapturedSegment(seq, new byte[1_000_000], 1001 + i, "10.0.0.1", 5400, "10.0.0.2", 13328));
            seq += 1_000_000;
        }

        logger.Stop();

        Assert.DoesNotContain(ReadLog(logDir), l => l.Contains("stream_self_heal"));
    }

    [Fact]
    public void Startup_purge_drops_an_impossible_identitys_artifact_counts_but_keeps_server_ownership()
    {
        // 기동 시 위생 정리는 동의 레코드에서 지운 해시를 캐릭터별 스토어 전부에서 치운다 — content.abyssArtifacts
        // 만 빠져 있었다. 그 'c' 행(점령 개수)이 브로드캐스트의 어느 슬롯이 우리 편인지를 정하므로, 남겨 두면
        // 존재할 수 없는 캐릭터가 계속 편을 고른다. 서버 점령 현황('o' 행)은 서버 것이라 남아야 하고, 다른
        // 캐릭터의 행도 그대로여야 한다. 패널 ✕(App.xaml.cs RemoveRequested)와 같은 처리다.
        const string garbageHash = "d044f8703709a3fb402e82271849f2998d2fd5ef9c01c450c088dfae5f446b70"; // "I" / 47200
        const string realHash = "0291206ee0f826ce22f3340aebcb21702c31cba93df21876011ded2de2734e23";
        const int server = 2003;
        const int lower = AbyssArtifactBuffCatalog.LowerZoneId;
        const int middle = AbyssArtifactBuffCatalog.MiddleZoneId;
        const long cycleStart = 1_787_750_154_000; // the real 2026-08-28 window
        const long cycleEnd = 1_788_008_700_000;
        const long now = 1_787_928_471_518;

        string dir = Path.Combine(_temp, "purge_artifacts");
        Directory.CreateDirectory(dir);
        var props = new PropertyHandler(dir);
        props.SetProperty("statsConsentCharacters",
            "{" + ConsentRecord(realHash, "콘팡", server) + "," + ConsentRecord(garbageHash, "I", 47200) + "}");
        string lowerOwnership = $"o,{server},{lower},212,{now - 1000},{cycleStart},{cycleEnd}";
        string middleOwnership = $"o,{server},{middle},212,{now - 1000},{cycleStart},{cycleEnd}";
        string realCount = $"c,{realHash},{lower},2,{now},0,0";
        props.SetProperty("content.abyssArtifacts", string.Join(';',
            lowerOwnership,
            middleOwnership,
            realCount,
            $"c,{garbageHash},{lower},1,{now + 1},0,0",
            $"c,{garbageHash},{middle},1,{now + 1},0,0"));

        using var services = new ServicesScope(new MeterServices(props));

        string[] records = props.GetProperty("content.abyssArtifacts")!.Split(';');
        // 필드 수가 형식 판별자다 — 구 빌드는 읽지 못하는 레코드를 버리므로 7필드 모양은 절대 바뀌면 안 된다.
        Assert.All(records, record => Assert.Equal(7, record.Split(',').Length));
        Assert.DoesNotContain(records, record => record.Contains(garbageHash, StringComparison.Ordinal));
        Assert.Contains(lowerOwnership, records);
        Assert.Contains(middleOwnership, records);
        Assert.Contains(realCount, records);
        Assert.Equal(3, records.Length);

        AbyssArtifactStore reloaded = AbyssArtifactStore.Parse(props.GetProperty("content.abyssArtifacts"));
        Assert.True(reloaded.HasOwnership(server, now));
        Assert.Equal(2, reloaded.SideFor(server, realHash, now));
        Assert.Null(reloaded.SideFor(server, garbageHash, now));
    }

    private static string ConsentRecord(string hash, string nickname, int server) =>
        $"\"{hash}\":{{\"state\":\"accepted\",\"uploadEnabled\":true,\"publicCharacter\":true," +
        $"\"consentVersion\":\"2026-06-04\",\"updatedAt\":1," +
        $"\"nickname\":\"{nickname}\",\"server\":{server}," +
        $"\"job\":null,\"grant\":false,\"pendingPublic\":false}}";

    // Declares a 30MB frame and then feeds 5MB of contiguous body, so the framer sits on a multi-MB buffer
    // without ever completing a packet. withGameSignalFirst prefixes one real game segment (server clock +
    // damage), which is the only thing separating the two cases above.
    private static void FeedBogusFrame(MeterServices services, int srcPort, bool withGameSignalFirst)
    {
        long seq = 0;
        long at = 1000;
        if (withGameSignalFirst)
        {
            services.Feed(ClockedGameSegment(seq: 0, at: at, srcPort: srcPort)); // 서버 시계 = 확정된 게임 연결
            seq = 16;
        }

        // LEB128 for 30,000,000 -> realLength = value + varint.length - 4 = 30,000,000. Under the 32MB cap, so
        // the accumulator keeps growing instead of force-resetting.
        byte[] header = { 0x80, 0x87, 0xA7, 0x0E };
        services.Feed(new CapturedSegment(seq, header, at, "10.0.0.1", srcPort, "10.0.0.2", 13328));
        seq += header.Length;

        for (int i = 0; i < 5; i++)
        {
            var body = new byte[1_000_000];
            services.Feed(new CapturedSegment(seq, body, ++at, "10.0.0.1", srcPort, "10.0.0.2", 13328));
            seq += body.Length;
        }
    }

    // A length-prefixed frame whose opcode is 0x3804 (Damage) — passes StreamProcessor.LooksLikeGamePacket
    // and dispatches as a game packet. realLength = varint.value(8) + varint.length(1) - 4 = 5 = frame size.
    private static CapturedSegment GameSegment(long seq, long at, int srcPort) =>
        new(seq, new byte[] { 0x08, 0x04, 0x38, 0x00, 0x00 }, at, "10.0.0.1", srcPort, "10.0.0.2", 13328);

    // Same framing but opcode 0x9702 (party roster) — an idempotent identity/roster opcode that must survive
    // dup-suppression. (Body is empty, so the parser finds no members and no-ops — we assert only the dispatch.)
    private static CapturedSegment RosterSegment(long seq, long at, int srcPort) =>
        new(seq, new byte[] { 0x08, 0x02, 0x97, 0x00, 0x00 }, at, "10.0.0.1", srcPort, "10.0.0.2", 13328);

    private static readonly byte[] DamageFrame = [0x08, 0x04, 0x38, 0x00, 0x00];

    // 0x3600 MapFrame_NT: [0E][00 36][서버 epoch ms Int64 LE] = 11바이트 (1016 실프레임 모양). primary 자격.
    private static byte[] MapFrame(long at)
    {
        byte[] frame = new byte[11];
        frame[0] = 0x0E;
        frame[1] = 0x00;
        frame[2] = 0x36;
        BitConverter.TryWriteBytes(frame.AsSpan(3), 1_792_083_904_000 + at);
        return frame;
    }

    // 진짜 게임 S2C 세그먼트: 서버 시계(자격) + 데미지 0x3804. 16바이트라 다음 seq 는 +16.
    private static CapturedSegment ClockedGameSegment(long seq, long at, int srcPort) =>
        new(seq, [.. MapFrame(at), .. DamageFrame], at, "10.0.0.1", srcPort, "10.0.0.2", 13328);

    // 10-16 하이재킹 프레임: User-Agent "...537.36 (KHTML, like Gecko)..." 이 '.' 에서 잘린 것.
    // 0x2E = 길이 46 → realLength 43, 그 뒤 "36" = 0x3633(OwnNickname) → LooksLikeGamePacket true.
    private static readonly byte[] UaFrame =
        System.Text.Encoding.ASCII.GetBytes(".36 (KHTML, like Gecko) Chrome/141.0.0.0 Sa"); // 43바이트

    private static CapturedSegment UaFrameSegment(long seq, long at) =>
        new(seq, UaFrame, at, "127.0.0.1", 49638, "127.0.0.1", 17080);

    // 정확히 68바이트인 HTTP 요청 헤드: 'G'(0x47=71) 를 길이로 읽으면 realLength = 71 + 1 - 4 = 68 이라 프레임 하나로
    // 떨어지고, 같은 방향의 다음 세그먼트가 프레임 경계에서 시작한다.
    private static readonly byte[] HttpHead =
        System.Text.Encoding.ASCII.GetBytes("GET /" + new string('a', 27) + " HTTP/1.1\r\nHost: localhost:17080\r\n\r\n");

    // 게임 opcode 가 아닌(0x9999) 1000바이트 프레임을 megabytes 만큼 한 세그먼트에 담아 보낸다. 노이즈 가드가
    // 판정할 수 있는 모양(프레임 50개 이상 · 2MB 이상)이다. realLength = 1002 + 2 - 4 = 1000.
    private static void FeedUnknownFrames(MeterServices services, string srcIp, int srcPort, string dstIp, int dstPort,
        double megabytes, long seq = 0)
    {
        int count = (int)(megabytes * 1_000_000 / 1000);
        byte[] payload = new byte[count * 1000];
        for (int i = 0; i < count; i++)
        {
            payload[i * 1000] = 0xEA;
            payload[i * 1000 + 1] = 0x07;
            payload[i * 1000 + 2] = 0x99;
            payload[i * 1000 + 3] = 0x99;
        }

        services.Feed(new CapturedSegment(seq, payload, 5000, srcIp, srcPort, dstIp, dstPort));
    }

    private static string[] ReadLog(string logDir) =>
        CaptureCorpusReader.ReadLines(Directory.GetFiles(logDir, "*.jsonl.gz").Single())
            .Where(l => l.Length > 0)
            .ToArray();

    // Disposes the extra MeterServices' upload-queue thread without leaking it past the test.
    private sealed class ServicesScope(MeterServices value) : IDisposable
    {
        public MeterServices Value { get; } = value;

        public void Dispose() => Value.UploadQueue.Dispose();
    }
}
