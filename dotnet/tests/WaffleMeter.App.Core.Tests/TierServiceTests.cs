using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WaffleMeter.App.Core;
using WaffleMeter.Services;
using WaffleMeter.Stats;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// Spec for fetching + caching the tier distribution artifact. The integrity check is the reason this class
/// exists: the artifact arrives gzip-encoded and its digest covers the COMPRESSED bytes, so a transport that
/// silently inflates would make verification impossible — a trap worth a permanent test.
/// </summary>
public sealed class TierServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "waffle_tier_tests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void Manual_refresh_makes_the_fetch_due_rather_than_only_waking_the_thread()
    {
        // 설정의 '티어 갱신' 버튼. 워커를 깨우기만 하면 루프가 `now >= _nextArtifactMs`에서 그대로 다시 자므로,
        // 갱신 주기 안에 누른 클릭은 성공을 보고하고 아무것도 안 받아 왔다. 후원자 명단(폴링 주기가 더 길다)에
        // 같은 코드를 복제할 참이라 여기서 못박아 둔다.
        byte[] gzip = GzipArtifact("abc0123456789def");
        var api = FakeApi(gzip, Sha256Hex(gzip), "abc0123456789def");
        var props = new PropertyHandler(_dir);
        long now = 1_000_000;

        using var service = new TierService(api, props, clock: () => now, startWorker: false);
        service.TryRefresh();
        Assert.True(service.NextArtifactCheckAtMs > now);

        now += 5_000;
        Assert.True(service.RequestManualRefresh());
        Assert.True(service.NextArtifactCheckAtMs <= now);
    }

    [Fact]
    public void Manual_refresh_inside_the_cooldown_is_refused_and_changes_nothing()
    {
        byte[] gzip = GzipArtifact("abc0123456789def");
        var api = FakeApi(gzip, Sha256Hex(gzip), "abc0123456789def");
        var props = new PropertyHandler(_dir);
        long now = 1_000_000;

        using var service = new TierService(api, props, clock: () => now, startWorker: false);
        Assert.True(service.RequestManualRefresh());

        service.TryRefresh();
        long due = service.NextArtifactCheckAtMs;

        now += 30_000; // 쿨다운 60초 안
        Assert.False(service.RequestManualRefresh());
        Assert.Equal(due, service.NextArtifactCheckAtMs);

        now += 40_000; // 쿨다운 밖
        Assert.True(service.RequestManualRefresh());
        Assert.True(service.NextArtifactCheckAtMs <= now);
    }

    [Fact]
    public void Downloads_verifies_and_caches_the_artifact()
    {
        byte[] gzip = GzipArtifact("abc0123456789def");
        var api = FakeApi(gzip, Sha256Hex(gzip), "abc0123456789def");
        var props = new PropertyHandler(_dir);

        using var service = new TierService(api, props, startWorker: false);
        Assert.Null(service.Artifact);

        service.TryRefresh();

        Assert.NotNull(service.Artifact);
        Assert.Equal("abc0123456789def", service.Artifact!.ArtifactId);
        Assert.Equal("abc0123456789def", props.GetProperty("tier.artifactId"));
        Assert.True(File.Exists(Path.Combine(props.AppDirectory(), "tier", "abc0123456789def.json.gz")));

        TierServiceStatus status = service.Status();
        Assert.True(status.HasArtifact);
        Assert.Equal(0, status.Failures);
        Assert.Null(status.LastError);
        // Dungeons and bosses are different counts — the settings line said "던전 41개" when 41 was the
        // mobCode total across 7 dungeons. Pin them apart so the label can't silently drift back.
        Assert.Equal(1, status.Dungeons);
        Assert.Equal(1, status.Mobs);
    }

    [Fact]
    public void Rejects_an_artifact_whose_digest_does_not_match_the_manifest()
    {
        byte[] gzip = GzipArtifact("abc0123456789def");
        // The manifest advertises a different digest — a corrupted or substituted body.
        var api = FakeApi(gzip, Sha256Hex(Encoding.UTF8.GetBytes("something else")), "abc0123456789def");

        using var service = new TierService(api, new PropertyHandler(_dir), startWorker: false);
        service.TryRefresh();

        Assert.Null(service.Artifact);
        Assert.Equal("sha256_mismatch", service.Status().LastError);
    }

    [Fact]
    public void Digest_is_taken_over_the_compressed_bytes_not_the_json()
    {
        // If a future change enables automatic decompression, the digest would be computed over the plaintext
        // and never match. Pin the direction: hashing the JSON must NOT satisfy the manifest.
        byte[] gzip = GzipArtifact("abc0123456789def");
        string plaintextDigest = Sha256Hex(Encoding.UTF8.GetBytes(ArtifactJson("abc0123456789def")));

        var api = FakeApi(gzip, plaintextDigest, "abc0123456789def");
        using var service = new TierService(api, new PropertyHandler(_dir), startWorker: false);
        service.TryRefresh();

        Assert.Null(service.Artifact);
        Assert.Equal("sha256_mismatch", service.Status().LastError);
    }

    [Fact]
    public void Skips_the_download_when_the_manifest_points_at_the_artifact_we_already_have()
    {
        byte[] gzip = GzipArtifact("abc0123456789def");
        int downloads = 0;
        var api = FakeApi(gzip, Sha256Hex(gzip), "abc0123456789def", onDownload: () => downloads++);
        var props = new PropertyHandler(_dir);

        using var service = new TierService(api, props, startWorker: false);
        service.TryRefresh();
        service.TryRefresh();
        service.TryRefresh();

        Assert.Equal(1, downloads); // the artifact is content-addressed; an unchanged id means nothing to fetch
    }

    [Fact]
    public void Refuses_a_schema_version_it_does_not_understand_and_keeps_the_cached_one()
    {
        byte[] good = GzipArtifact("abc0123456789def");
        var api = FakeApi(good, Sha256Hex(good), "abc0123456789def");
        var props = new PropertyHandler(_dir);
        using var service = new TierService(api, props, startWorker: false);
        service.TryRefresh();
        Assert.NotNull(service.Artifact);

        // Server moves to a shape this build does not know: keep serving the old ladder rather than guessing.
        // The number has to stay one version ABOVE what this build reads — v1/v2/v3 are all accepted, so using
        // any of them would assert the opposite of what this test is named for.
        api.ManifestSchemaVersion = TierArtifact.MaxSupportedSchemaVersion + 1;
        service.TryRefresh();

        Assert.NotNull(service.Artifact);
        Assert.Equal("abc0123456789def", service.Artifact!.ArtifactId);
        Assert.Equal($"unsupported_schema_{TierArtifact.MaxSupportedSchemaVersion + 1}", service.Status().LastError);
    }

    /// <summary>The rollout depends on this: the meter has to take a v2 manifest BEFORE the server starts
    /// sending one, or the flip stops tier updates for everyone who has not updated yet.</summary>
    [Fact]
    public void Accepts_a_v2_manifest()
    {
        byte[] good = GzipArtifact("v2artifact000001", schemaVersion: 2);
        var api = FakeApi(good, Sha256Hex(good), "v2artifact000001");
        api.ManifestSchemaVersion = 2;
        var props = new PropertyHandler(_dir);
        using var service = new TierService(api, props, startWorker: false);

        service.TryRefresh();

        Assert.NotNull(service.Artifact);
        Assert.Equal("v2artifact000001", service.Artifact!.ArtifactId);
        Assert.Null(service.Status().LastError);
    }

    [Fact]
    public void Survives_the_stats_backend_being_down()
    {
        var api = new FakeTierApi { ThrowOnManifest = new StatsApiException("boom", 503, null) };
        using var service = new TierService(api, new PropertyHandler(_dir), startWorker: false);

        service.TryRefresh(); // must not throw — an outage cannot surface in a combat overlay

        Assert.Null(service.Artifact);
        Assert.Equal(1, service.Status().Failures);
        Assert.Equal("http_503", service.Status().LastError);
    }

    [Fact]
    public void Restores_the_cached_artifact_on_the_next_start_without_a_network()
    {
        byte[] gzip = GzipArtifact("abc0123456789def");
        var props = new PropertyHandler(_dir);
        using (var first = new TierService(FakeApi(gzip, Sha256Hex(gzip), "abc0123456789def"), props, startWorker: false))
        {
            first.TryRefresh();
            Assert.NotNull(first.Artifact);
        }

        // Fresh process, backend unreachable: the ladder still comes up from disk.
        var offline = new FakeTierApi { ThrowOnManifest = new StatsApiException("offline", 0, null) };
        using var second = new TierService(offline, new PropertyHandler(_dir), startWorker: false);

        Assert.NotNull(second.Artifact);
        Assert.Equal("abc0123456789def", second.Artifact!.ArtifactId);
    }

    [Fact]
    public void Ignores_a_corrupt_cache_file_instead_of_crashing()
    {
        var props = new PropertyHandler(_dir);
        props.SetProperty("tier.artifactId", "abc0123456789def");
        Directory.CreateDirectory(Path.Combine(props.AppDirectory(), "tier"));
        File.WriteAllBytes(Path.Combine(props.AppDirectory(), "tier", "abc0123456789def.json.gz"), [0x1f, 0x8b, 0x00, 0x01, 0x02]);

        var offline = new FakeTierApi { ThrowOnManifest = new StatsApiException("offline", 0, null) };
        using var service = new TierService(offline, props, startWorker: false);

        Assert.Null(service.Artifact); // no exception, feature simply stays off
    }

    [Fact]
    public void Rate_limits_the_manual_refresh_button()
    {
        long now = 1_000_000;
        byte[] gzip = GzipArtifact("abc0123456789def");
        using var service = new TierService(FakeApi(gzip, Sha256Hex(gzip), "abc0123456789def"),
            new PropertyHandler(_dir), clock: () => now, startWorker: false);

        Assert.True(service.RequestManualRefresh());
        Assert.False(service.RequestManualRefresh()); // still inside the cooldown
        now += 61_000;
        Assert.True(service.RequestManualRefresh());
    }

    [Fact]
    public void Batches_career_lookups_and_caches_the_answers()
    {
        long now = 1_000_000;
        var api = new FakeTierApi();
        string a = Hash('a');
        string b = Hash('b');
        api.LookupAnswers[a] = new TierLookupResult(a, "ok", TierRank: 2, TierName: "마스터", TopPercent: 3.4, Job: "검성", Battles: 12, CohortSize: 800);

        using var service = new TierService(api, new PropertyHandler(_dir), clock: () => now, startWorker: false);
        service.RequestCareerTiers([a, b]);
        service.DrainLookupsForTest();

        Assert.Single(api.LookupBatches);
        Assert.Equal(2, api.LookupBatches[0].Count);

        Assert.Equal((2, "마스터", 3.4), service.CareerTier(a));
        Assert.Null(service.CareerTier(b)); // "none" is a real answer, just not a displayable one

        // Asking again inside the TTL must not produce a second request — neither for the hit nor the miss.
        service.RequestCareerTiers([a, b]);
        service.DrainLookupsForTest();
        Assert.Single(api.LookupBatches);
    }

    [Fact]
    public void Never_asks_about_more_than_the_server_accepts_in_one_request()
    {
        long now = 1_000_000;
        var api = new FakeTierApi();
        using var service = new TierService(api, new PropertyHandler(_dir), clock: () => now, startWorker: false);

        // A raid-sized flood of applicants: the server hard-caps a lookup at 12 hashes.
        service.RequestCareerTiers(Enumerable.Range(0, 30).Select(i => Hash((char)('a' + (i % 26)), i)));
        service.DrainLookupsForTest();

        Assert.Single(api.LookupBatches);
        Assert.Equal(12, api.LookupBatches[0].Count);
    }

    [Fact]
    public void Ignores_hashes_that_are_not_identity_hashes()
    {
        var api = new FakeTierApi();
        using var service = new TierService(api, new PropertyHandler(_dir), startWorker: false);

        service.RequestCareerTiers([null, "", "too-short", new string('Z', 64)]);
        service.DrainLookupsForTest();

        // "too-short"/null never queue; the 64-char one is queued (length is all we can check client-side).
        Assert.Single(api.LookupBatches);
        Assert.Single(api.LookupBatches[0]);
    }

    [Fact]
    public void Keeps_a_failed_lookup_queued_instead_of_forgetting_it()
    {
        long now = 1_000_000;
        var api = new FakeTierApi { ThrowOnLookup = new StatsApiException("rate limited", 429, null) };
        string a = Hash('a');
        using var service = new TierService(api, new PropertyHandler(_dir), clock: () => now, startWorker: false);

        service.RequestCareerTiers([a]);
        service.DrainLookupsForTest();
        Assert.Equal("lookup_http_429", service.Status().LastError);

        // The rate limit lifts: the same hash is retried without the panel having to ask again.
        api.ThrowOnLookup = null;
        api.LookupAnswers[a] = new TierLookupResult(a, "ok", TierRank: 5, TierName: "골드", TopPercent: 44.0);
        service.DrainLookupsForTest();

        Assert.Equal((5, "골드", 44.0), service.CareerTier(a));
    }

    [Fact]
    public void Forgets_a_tier_once_it_goes_stale()
    {
        long now = 1_000_000;
        var api = new FakeTierApi();
        string a = Hash('a');
        api.LookupAnswers[a] = new TierLookupResult(a, "ok", TierRank: 3, TierName: "다이아", TopPercent: 8.0);

        using var service = new TierService(api, new PropertyHandler(_dir), clock: () => now, startWorker: false);
        service.RequestCareerTiers([a]);
        service.DrainLookupsForTest();
        Assert.NotNull(service.CareerTier(a));

        now += (long)TimeSpan.FromMinutes(31).TotalMilliseconds; // past the 30 min reuse window
        Assert.Null(service.CareerTier(a));
    }

    private static string Hash(char fill, int salt = 0)
    {
        string body = new(fill, 64);
        return salt == 0 ? body : body[..(64 - 2)] + (salt % 100).ToString("00");
    }

    // ---- helpers -------------------------------------------------------------------------------------

    [Fact]
    public void A_successful_refresh_sweeps_every_older_artifact()
    {
        // LoadFromDisk only ever opens the file named by tier.artifactId, so anything else in the folder is
        // never read again — it is a stale copy of a distribution nobody will look at. Once the new one is
        // past its digest and parse checks, the rest can go.
        var props = new PropertyHandler(_dir);
        string cache = Path.Combine(props.AppDirectory(), "tier");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "old111111111111.json.gz"), GzipArtifact("old111111111111"));
        File.WriteAllBytes(Path.Combine(cache, "old222222222222.json.gz"), GzipArtifact("old222222222222"));
        File.WriteAllText(Path.Combine(cache, "notes.txt"), "unrelated"); // only *.json.gz is swept

        byte[] gzip = GzipArtifact("new333333333333");
        using var service = new TierService(FakeApi(gzip, Sha256Hex(gzip), "new333333333333"), props, startWorker: false);

        service.TryRefresh();

        Assert.Equal(
            new[] { "new333333333333.json.gz" },
            Directory.GetFiles(cache, "*.json.gz").Select(Path.GetFileName).OrderBy(n => n).ToArray());
        Assert.True(File.Exists(Path.Combine(cache, "notes.txt")));
    }

    [Fact]
    public void A_failed_refresh_keeps_the_cached_artifact_on_disk()
    {
        // The sweep must only run behind a verified download. A bad digest has to leave the working copy alone,
        // otherwise one corrupt response would take the meter's only distribution with it.
        byte[] good = GzipArtifact("good11111111111");
        var props = new PropertyHandler(_dir);
        using (var first = new TierService(FakeApi(good, Sha256Hex(good), "good11111111111"), props, startWorker: false))
        {
            first.TryRefresh();
        }

        string cache = Path.Combine(props.AppDirectory(), "tier");
        Assert.True(File.Exists(Path.Combine(cache, "good11111111111.json.gz")));

        byte[] corrupt = GzipArtifact("bad222222222222");
        using var service = new TierService(FakeApi(corrupt, Sha256Hex(good), "bad222222222222"), props, startWorker: false);

        service.TryRefresh(); // digest covers the OTHER artifact -> rejected

        Assert.True(File.Exists(Path.Combine(cache, "good11111111111.json.gz")));
        Assert.False(File.Exists(Path.Combine(cache, "bad222222222222.json.gz")));
        Assert.Equal("sha256_mismatch", service.Status().LastError);
    }

    private static FakeTierApi FakeApi(byte[] gzip, string sha256, string artifactId, Action? onDownload = null) => new()
    {
        ArtifactId = artifactId,
        Gzip = gzip,
        Sha256 = sha256,
        OnDownload = onDownload,
    };

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] GzipArtifact(string artifactId, int schemaVersion = 1)
    {
        byte[] raw = Encoding.UTF8.GetBytes(ArtifactJson(artifactId, schemaVersion));
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    private static string ArtifactJson(string artifactId, int schemaVersion = 1)
    {
        var grid = new List<double>();
        for (int i = 0; i < 31; i++)
        {
            grid.Add(100 - (i * 3.3));
        }

        var cuts = new List<long>();
        long previous = 0;
        for (int i = 0; i < 31; i++)
        {
            long quantised = 100 + i;
            cuts.Add(quantised - previous);
            previous = quantised;
        }

        return JsonSerializer.Serialize(new
        {
            schemaVersion,
            artifactId,
            windowDays = 30,
            generatedAt = "2026-08-03T04:00:11.000Z",
            grid,
            tierCuts = new[] { 1, 5, 10, 30, 50, 70, 90 },
            jobs = new[] { "검성", "수호성", "살성", "궁성", "마도성", "정령성", "치유성", "호법성", "권성" },
            dungeons = new object[] { new { ord = 19, key = "sanctuary-muspels-holy-grail", name = "무스펠의 성배", category = "성역" } },
            variants = new object[] { new { dungeonOrd = 19, ord = 2, label = "어려움" } },
            mobs = new Dictionary<string, int[]> { ["2301060"] = [19, 2, 2] },
            rows = new object[]
            {
                new { r = 0, m = "dps", k = "성역", d = 19, v = 2, b = 2, j = "검성", s = 3, p = 5, n = 900, c = cuts, g = -1 },
            },
        });
    }

    /// <summary>Canned manifest + bytes through the ITierApi seam — no sockets, no real transport.</summary>
    private sealed class FakeTierApi : ITierApi
    {
        public string ArtifactId { get; set; } = "abc0123456789def";
        public byte[] Gzip { get; set; } = [];
        public string Sha256 { get; set; } = string.Empty;
        public int ManifestSchemaVersion { get; set; } = 1;
        public Exception? ThrowOnManifest { get; set; }
        public Action? OnDownload { get; set; }

        public TierManifestResponse GetTierManifest()
        {
            if (ThrowOnManifest != null)
            {
                throw ThrowOnManifest;
            }

            return new TierManifestResponse(
                Ok: true,
                ArtifactId: ArtifactId,
                SchemaVersion: ManifestSchemaVersion,
                WindowDays: 30,
                Url: $"/api/v1/tiers/artifact/{ArtifactId}.json",
                ByteSize: Gzip.Length,
                Sha256: Sha256);
        }

        public StatsBinaryResponse GetTierArtifactGzip(string path)
        {
            OnDownload?.Invoke();
            return new StatsBinaryResponse(200, Gzip);
        }

        public List<IReadOnlyList<string>> LookupBatches { get; } = [];

        public Dictionary<string, TierLookupResult> LookupAnswers { get; } = new(StringComparer.Ordinal);

        public Exception? ThrowOnLookup { get; set; }

        public TierLookupResponse PostTierLookup(TierLookupRequest request, string clientVersion, string? installId = null)
        {
            if (ThrowOnLookup != null)
            {
                throw ThrowOnLookup;
            }

            LookupBatches.Add(request.IdentityHashes);
            var results = new List<TierLookupResult>();
            foreach (string hash in request.IdentityHashes)
            {
                results.Add(LookupAnswers.TryGetValue(hash, out TierLookupResult? answer)
                    ? answer
                    : new TierLookupResult(hash, "none"));
            }

            return new TierLookupResponse(true, "2026-08-03T05:00:00.000Z", results);
        }
    }
}
