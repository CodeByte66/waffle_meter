using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WaffleMeter.App.Core;
using WaffleMeter.Services;
using WaffleMeter.Stats;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// Spec for the supporter/ranker grant channel. Most of these exist because the tier service — the thing this is
/// modelled on — gets them wrong, and copying it wholesale would have shipped the same defects here.
/// </summary>
public sealed class NameFxServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "waffle_namefx_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static bool KnownEffect(string id) => id is "syrup" or "goldleaf";

    private static bool KnownGauge(string id) => id is "prism" or "ember";

    [Fact]
    public void Downloads_verifies_and_caches_the_roster()
    {
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[{"h":"AAAA","e":"syrup","k":"supporter"}]}""");
        var api = FakeApi(gzip, Sha256Hex(gzip), "cafe0123");
        var props = new PropertyHandler(_dir);

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, startWorker: false);
        Assert.Equal(0, service.Roster.Count);

        service.TryRefresh();

        Assert.Equal(1, service.Roster.Count);
        Assert.Equal("syrup", service.Roster.Find("AAAA")!.EffectId);
        Assert.Equal("cafe0123", props.GetProperty("namefx.artifactId"));
        Assert.True(File.Exists(Path.Combine(props.AppDirectory(), "namefx", "cafe0123.json.gz")));

        NameFxServiceStatus status = service.Status();
        Assert.True(status.HasArtifact);
        Assert.Equal(1, status.Grants);
        Assert.Equal(0, status.Failures);
        Assert.False(status.UsingLocalFile);
    }

    [Fact]
    public void A_roster_that_arrives_corrupted_is_refused_and_nothing_is_cached()
    {
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[{"h":"AAAA","e":"syrup","k":"supporter"}]}""");
        var api = FakeApi(gzip, Sha256Hex(Encoding.UTF8.GetBytes("something else")), "cafe0123");
        var props = new PropertyHandler(_dir);

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, startWorker: false);
        service.TryRefresh();

        Assert.Equal(0, service.Roster.Count);
        Assert.Equal("sha256_mismatch", service.Status().LastError);
        Assert.False(Directory.Exists(Path.Combine(props.AppDirectory(), "namefx")));
    }

    [Fact]
    public void A_newer_document_version_is_refused_rather_than_guessed_at()
    {
        byte[] gzip = GzipRoster("""{"schemaVersion":2,"entries":[]}""");
        var api = FakeApi(gzip, Sha256Hex(gzip), "cafe0123", schemaVersion: 2);
        var props = new PropertyHandler(_dir);

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, startWorker: false);
        service.TryRefresh();

        Assert.Equal("unsupported_schema_2", service.Status().LastError);
        Assert.Null(props.GetProperty("namefx.artifactId"));
    }

    /// <summary>
    /// 🔑 판정 38. 상위 스키마는 되돌아오지 않는 상태다 — 재시도해도 같은 매니페스트를 같은 이유로 버린다.
    /// 종전에는 <c>Fail()</c> 경로라 백오프가 정상 주기(1시간)에서 멈췄고, 결과가 "영원히 한 시간마다
    /// 한 번씩 헛요청"이었다.
    /// </summary>
    [Fact]
    public void A_document_this_build_cannot_read_parks_the_poll_instead_of_retrying_forever()
    {
        byte[] gzip = GzipRoster("""{"schemaVersion":2,"entries":[]}""");
        var api = FakeApi(gzip, Sha256Hex(gzip), "cafe0123", schemaVersion: 2);
        var props = new PropertyHandler(_dir);
        long now = 1_000_000;

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, clock: () => now, startWorker: false);
        service.TryRefresh();

        Assert.Equal(1, api.ManifestCalls);
        // 다음 확인 시각이 정상 주기 안에 있으면, 워커는 한 시간 뒤 같은 짓을 또 한다.
        Assert.True(
            service.NextArtifactCheckAtMs - now > (long)TimeSpan.FromDays(365).TotalMilliseconds,
            $"폴링이 접히지 않았다: {service.NextArtifactCheckAtMs - now}ms 뒤 재시도 예정");
    }

    /// <summary>탈출구는 수동 갱신 하나 — 서버가 롤백하면 버튼 한 번으로 다시 붙는다.</summary>
    [Fact]
    public void The_manual_refresh_is_the_one_way_out_of_a_parked_poll()
    {
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[{"h":"AAAA","e":"syrup","k":"supporter"}]}""");
        var api = FakeApi(gzip, Sha256Hex(gzip), "cafe0123", schemaVersion: 2);
        var props = new PropertyHandler(_dir);
        long now = 1_000_000;

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, clock: () => now, startWorker: false);
        service.TryRefresh();
        Assert.Equal(0, service.Roster.Count);

        now += 61_000; // 쿨다운 밖
        api.SchemaVersion = 1; // 서버 롤백
        Assert.True(service.RequestManualRefresh());
        Assert.True(service.NextArtifactCheckAtMs <= now, "수동 갱신이 접힌 폴링을 풀지 못했다");

        service.TryRefresh();
        Assert.Equal(1, service.Roster.Count);
        Assert.Null(service.Status().LastError);
    }

    [Fact]
    public void An_unchanged_artifact_costs_no_download()
    {
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[{"h":"AAAA","e":"syrup","k":"supporter"}]}""");
        int downloads = 0;
        var api = FakeApi(gzip, Sha256Hex(gzip), "cafe0123", onDownload: () => downloads++);
        var props = new PropertyHandler(_dir);

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, startWorker: false);
        service.TryRefresh();
        service.TryRefresh();
        service.TryRefresh();

        Assert.Equal(1, downloads);
        Assert.Equal(1, service.Roster.Count);
    }

    [Fact]
    public void The_poll_matches_the_servers_hourly_rebuild()
    {
        // 서버는 티어 갱신(매시) 끝에서 명단을 다시 발행한다. 폴링이 그보다 굵으면 새 랭커가
        // 붙기까지 이유 없이 기다리게 된다. 매시가 감당되는 건 문서가 콘텐츠 주소라서다 —
        // 안 바뀐 시각에는 매니페스트 한 번이고 다운로드가 없다.
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[]}""");
        var props = new PropertyHandler(_dir);
        long now = 1_000_000;

        using var service = new NameFxService(FakeApi(gzip, Sha256Hex(gzip), "cafe0123"), props, KnownEffect, KnownGauge,
            clock: () => now, startWorker: false);
        service.TryRefresh();

        long untilNext = service.NextArtifactCheckAtMs - now;
        Assert.Equal((long)TimeSpan.FromHours(1).TotalMilliseconds, untilNext);
    }

    [Fact]
    public void A_failure_retries_sooner_than_the_normal_cadence()
    {
        // The tier service has no backoff: a 503 there costs a full interval, and the retry is the only way back.
        var api = new ThrowingApi();
        var props = new PropertyHandler(_dir);
        long now = 1_000_000;

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, clock: () => now, startWorker: false);
        service.TryRefresh();

        long firstRetryIn = service.NextArtifactCheckAtMs - now;
        Assert.InRange(firstRetryIn, 1, (long)TimeSpan.FromMinutes(5).TotalMilliseconds);

        service.TryRefresh();
        long secondRetryIn = service.NextArtifactCheckAtMs - now;
        Assert.True(secondRetryIn > firstRetryIn, $"백오프가 늘지 않았다: {firstRetryIn} → {secondRetryIn}");

        for (int i = 0; i < 20; i++)
        {
            service.TryRefresh();
        }

        // ...but never past the normal cadence, or a long outage turns into a permanently dead channel.
        Assert.True(service.NextArtifactCheckAtMs - now <= (long)TimeSpan.FromHours(1).TotalMilliseconds);
    }

    [Fact]
    public void Manual_refresh_makes_the_fetch_due_rather_than_only_waking_the_thread()
    {
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[]}""");
        var api = FakeApi(gzip, Sha256Hex(gzip), "cafe0123");
        var props = new PropertyHandler(_dir);
        long now = 1_000_000;

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, clock: () => now, startWorker: false);
        service.TryRefresh();
        Assert.True(service.NextArtifactCheckAtMs > now);

        now += 61_000; // 쿨다운 밖
        Assert.True(service.RequestManualRefresh());
        Assert.True(service.NextArtifactCheckAtMs <= now);
    }

    [Fact]
    public void The_cached_artifact_comes_back_without_a_network_call()
    {
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[{"h":"AAAA","e":"syrup","k":"supporter"}]}""");
        var props = new PropertyHandler(_dir);
        using (var first = new NameFxService(FakeApi(gzip, Sha256Hex(gzip), "cafe0123"), props, KnownEffect, KnownGauge, startWorker: false))
        {
            first.TryRefresh();
        }

        var offline = new ThrowingApi();
        using var restarted = new NameFxService(offline, new PropertyHandler(_dir), KnownEffect, KnownGauge, startWorker: false);

        Assert.Equal(1, restarted.Roster.Count);
        Assert.Equal(0, offline.Calls);
    }

    [Fact]
    public void The_plain_local_file_still_works_when_no_artifact_has_been_fetched()
    {
        // 서버 채널이 붙기 전부터 있던 개발/데모 경로. 서버가 없는 빌드에서도 화면이 나오게 남겨 둔다.
        var props = new PropertyHandler(_dir);
        Directory.CreateDirectory(Path.Combine(props.AppDirectory(), "namefx"));
        File.WriteAllText(
            NameFxRoster.FilePath(props.AppDirectory()),
            """{"schemaVersion":1,"entries":[{"h":"BBBB","e":"goldleaf","k":"ranker","g":"prism"}]}""");

        using var service = new NameFxService(new ThrowingApi(), props, KnownEffect, KnownGauge, startWorker: false);

        Assert.Equal(1, service.Roster.Count);
        Assert.True(service.Status().UsingLocalFile);
    }

    [Fact]
    public void Sweeping_old_artifacts_does_not_eat_the_plain_local_file()
    {
        // 캐시 스윕은 같은 폴더에 사는 supporters.json 을 지우면 안 된다. 티어 쪽 스윕을 그대로 베끼면
        // 확장자만 보고 지우므로, 여기서 확장자 범위를 못박아 둔다.
        var props = new PropertyHandler(_dir);
        Directory.CreateDirectory(Path.Combine(props.AppDirectory(), "namefx"));
        string plain = NameFxRoster.FilePath(props.AppDirectory());
        File.WriteAllText(plain, """{"schemaVersion":1,"entries":[]}""");

        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[]}""");
        using var service = new NameFxService(FakeApi(gzip, Sha256Hex(gzip), "cafe0123"), props, KnownEffect, KnownGauge, startWorker: false);
        service.TryRefresh();

        Assert.True(File.Exists(plain));
    }

    [Fact]
    public void Replacing_the_roster_announces_it()
    {
        // 이 이벤트가 유일한 반영 경로다. 오버레이가 부여를 (서버, 닉네임)으로 메모하고 있어서, 다운로드가
        // 성공해도 아무도 SetNameFxRoster 를 부르지 않으면 화면은 다음 실행까지 그대로다 — 로그도 없이.
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[{"h":"AAAA","e":"syrup","k":"supporter"}]}""");
        var props = new PropertyHandler(_dir);
        var seen = new List<int>();

        using var service = new NameFxService(FakeApi(gzip, Sha256Hex(gzip), "cafe0123"), props, KnownEffect, KnownGauge, startWorker: false);
        service.Changed += r => seen.Add(r.Count);
        service.TryRefresh();

        Assert.Equal(new[] { 1 }, seen);
    }

    [Fact]
    public void An_effect_id_this_build_cannot_draw_is_dropped()
    {
        byte[] gzip = GzipRoster("""
            {"schemaVersion":1,"entries":[
              {"h":"AAAA","e":"from_a_newer_build","k":"supporter"},
              {"h":"BBBB","e":"goldleaf","k":"ranker","g":"unknown_gauge"}]}
            """);
        var props = new PropertyHandler(_dir);

        using var service = new NameFxService(FakeApi(gzip, Sha256Hex(gzip), "cafe0123"), props, KnownEffect, KnownGauge, startWorker: false);
        service.TryRefresh();

        Assert.Null(service.Roster.Find("AAAA"));
        // 모르는 게이지 하나가 닉네임 효과까지 데리고 사라지면 안 된다.
        Assert.Equal("goldleaf", service.Roster.Find("BBBB")!.EffectId);
        Assert.Null(service.Roster.Find("BBBB")!.GaugeId);
    }

    [Fact]
    public void Choosing_an_effect_shows_up_on_my_own_row_at_once()
    {
        // 서버는 다음 발행에서야 명단을 바꾼다. 그때까지 아무 일도 안 일어나면, 고른 사람은 자기
        // 화면에서 변화를 못 본다. 그래서 응답이 성공이면 로컬 명단을 그 자리에서 갈아 끼운다.
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[{"h":"AAAA","e":"syrup","k":"both"}]}""");
        var api = FakeApi(gzip, Sha256Hex(gzip), "cafe0123");
        var props = new PropertyHandler(_dir);

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, startWorker: false);
        service.TryRefresh();
        Assert.Equal("syrup", service.Roster.Find("AAAA")!.EffectId);

        NameFxChoiceResponse response = service.SubmitChoice("AAAA", "goldleaf", null, "test");

        Assert.True(response.Ok);
        Assert.Equal("goldleaf", service.Roster.Find("AAAA")!.EffectId);
        Assert.Equal("AAAA", api.LastChoice!.IdentityHash);
    }

    [Fact]
    public void A_local_patch_cannot_invent_a_grant_the_server_never_made()
    {
        // 명단에 없는 해시를 로컬에서 끼워 넣을 수 있으면, 자격 없는 사람이 자기 화면에서만
        // 연출을 켜고 "되는데 남에게는 안 보인다"고 신고하게 된다. 부여가 없으면 그대로 둔다.
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[{"h":"AAAA","e":"syrup","k":"supporter"}]}""");
        var props = new PropertyHandler(_dir);

        using var service = new NameFxService(FakeApi(gzip, Sha256Hex(gzip), "cafe0123"), props, KnownEffect, KnownGauge, startWorker: false);
        service.TryRefresh();

        service.SubmitChoice("BBBB", "goldleaf", null, "test");

        Assert.Null(service.Roster.Find("BBBB"));
        Assert.Equal(1, service.Roster.Count);
    }

    [Fact]
    public void A_refused_choice_leaves_the_roster_alone()
    {
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[{"h":"AAAA","e":"syrup","k":"supporter"}]}""");
        var props = new PropertyHandler(_dir);
        var api = new RefusingApi(gzip, Sha256Hex(gzip), "cafe0123");

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, startWorker: false);
        service.TryRefresh();

        Assert.False(service.SubmitChoice("AAAA", "goldleaf", null, "test").Ok);
        Assert.Equal("syrup", service.Roster.Find("AAAA")!.EffectId);
    }

    private sealed class RefusingApi(byte[] gzip, string sha, string artifactId) : INameFxApi
    {
        public NameFxManifestResponse GetNameFxManifest() =>
            new(true, artifactId, 1, $"/api/v1/supporters/artifact/{artifactId}.json", gzip.Length, sha);

        public StatsBinaryResponse GetNameFxArtifactGzip(string path) => new(200, gzip);

        public NameFxChoiceResponse PostNameFxChoice(NameFxChoiceRequest request, string clientVersion, string? installId = null) =>
            new(false);
    }

    private static byte[] GzipRoster(string json)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            gz.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// dev 빌드의 로컬 전용 선택. 서버 절반이 배포되기 전에도 스킨을 눈으로 볼 수 있어야 하는데, 그렇다고
    /// 클라이언트가 스스로에게 표식을 줄 수 있게 되면 안 된다 — 그 경계를 못박는다.
    /// </summary>
    [Fact]
    public void A_dev_build_applies_a_pick_locally_and_never_sends_it()
    {
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[{"h":"AAAA","e":"syrup","k":"both","g":"prism"}]}""");
        var api = FakeApi(gzip, Sha256Hex(gzip), "cafe0123");
        var props = new PropertyHandler(_dir);

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, startWorker: false)
        {
            LocalChoiceOnly = true,
        };

        service.TryRefresh();

        Assert.True(service.SubmitChoice("AAAA", "goldleaf", "ember", "test").Ok);
        Assert.Null(api.LastChoice); // 서버로 나가지 않았다
        Assert.Equal("goldleaf", service.Roster.Find("AAAA")!.EffectId);
        Assert.Equal("ember", service.Roster.Find("AAAA")!.GaugeId);
    }

    /// <summary>서버 갱신이 로컬 선택을 되돌리면 안 된다 — 매시 새로고침 때 조용히 원복되면 사용자에게는
    /// 설정이 안 먹는 것으로 보인다.</summary>
    [Fact]
    public void A_local_pick_survives_a_server_refresh()
    {
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[{"h":"AAAA","e":"syrup","k":"both","g":"prism"}]}""");
        var api = FakeApi(gzip, Sha256Hex(gzip), "cafe0123");
        var props = new PropertyHandler(_dir);

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, startWorker: false)
        {
            LocalChoiceOnly = true,
        };

        service.TryRefresh();
        service.SubmitChoice("AAAA", "goldleaf", "ember", "test");

        props.SetProperty("namefx.artifactId", string.Empty); // 다음 fetch 가 실제로 돌게 한다
        service.TryRefresh();

        Assert.Equal("goldleaf", service.Roster.Find("AAAA")!.EffectId);
        Assert.Equal("ember", service.Roster.Find("AAAA")!.GaugeId);
    }

    /// <summary>🔴 로컬 선택으로 **자격을 만들어낼 수는 없다.** 서버가 부여한 적 없는 캐릭터는 로컬에서
    /// 골라도 명단에 들어가지 않는다 — 안 그러면 클라이언트가 스스로에게 표식을 줄 수 있다.</summary>
    [Fact]
    public void A_local_pick_cannot_invent_a_grant()
    {
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[{"h":"AAAA","e":"syrup","k":"supporter"}]}""");
        var api = FakeApi(gzip, Sha256Hex(gzip), "cafe0123");
        var props = new PropertyHandler(_dir);

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, startWorker: false)
        {
            LocalChoiceOnly = true,
        };

        service.TryRefresh();
        service.SubmitChoice("ZZZZ", "goldleaf", "ember", "test");

        Assert.Null(service.Roster.Find("ZZZZ"));
        Assert.Equal(1, service.Roster.Count);
    }

    /// <summary>기본값(릴리스 빌드)에서는 종전 그대로 서버가 권위다.</summary>
    [Fact]
    public void A_release_build_still_sends_the_pick()
    {
        byte[] gzip = GzipRoster("""{"schemaVersion":1,"entries":[{"h":"AAAA","e":"syrup","k":"both"}]}""");
        var api = FakeApi(gzip, Sha256Hex(gzip), "cafe0123");
        var props = new PropertyHandler(_dir);

        using var service = new NameFxService(api, props, KnownEffect, KnownGauge, startWorker: false);
        service.TryRefresh();

        Assert.False(service.LocalChoiceOnly);
        Assert.True(service.SubmitChoice("AAAA", "goldleaf", null, "test").Ok);
        Assert.Equal("goldleaf", api.LastChoice!.EffectId);
    }

    private static FakeNameFxApi FakeApi(byte[] gzip, string sha, string artifactId, int schemaVersion = 1, Action? onDownload = null) =>
        new(gzip, sha, artifactId, schemaVersion, onDownload);

    private sealed class FakeNameFxApi(byte[] gzip, string sha, string artifactId, int schemaVersion, Action? onDownload) : INameFxApi
    {
        /// <summary>Settable so a test can act out a server rollback (schema up, then back down).</summary>
        public int SchemaVersion { get; set; } = schemaVersion;

        /// <summary>Manifest requests served. The parked-poll contract is about this number not growing.</summary>
        public int ManifestCalls { get; private set; }

        public NameFxManifestResponse GetNameFxManifest()
        {
            ManifestCalls++;
            return new(true, artifactId, SchemaVersion, $"/api/v1/supporters/artifact/{artifactId}.json", gzip.Length, sha);
        }

        public StatsBinaryResponse GetNameFxArtifactGzip(string path)
        {
            onDownload?.Invoke();
            return new StatsBinaryResponse(200, gzip);
        }

        public NameFxChoiceRequest? LastChoice { get; private set; }

        public NameFxChoiceResponse PostNameFxChoice(NameFxChoiceRequest request, string clientVersion, string? installId = null)
        {
            LastChoice = request;
            return new NameFxChoiceResponse(true, "both", request.EffectId, request.GaugeId);
        }
    }

    private sealed class ThrowingApi : INameFxApi
    {
        public int Calls { get; private set; }

        public NameFxManifestResponse GetNameFxManifest()
        {
            Calls++;
            throw new StatsApiException("namefx_manifest_http_503", 503, null);
        }

        public StatsBinaryResponse GetNameFxArtifactGzip(string path)
        {
            Calls++;
            throw new StatsApiException("namefx_artifact_http_503", 503, null);
        }

        public NameFxChoiceResponse PostNameFxChoice(NameFxChoiceRequest request, string clientVersion, string? installId = null)
        {
            Calls++;
            throw new StatsApiException("namefx_choice_http_503", 503, null);
        }
    }
}
