using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WaffleMeter.Services;
using WaffleMeter.Stats;

namespace WaffleMeter.App.Core;

/// <summary>Diagnostics for the settings screen: what we have and how the last fetch went.</summary>
public sealed record NameFxServiceStatus(
    bool HasArtifact,
    string? ArtifactId,
    long FetchedAtMs,
    int Grants,
    int Failures,
    string? LastError,
    bool UsingLocalFile);

/// <summary>
/// Owns the supporter/ranker grant list: fetches it rarely, verifies it, caches it on disk, and hands the parsed
/// roster to whoever renders a nickname.
/// <para><b>Shaped like <see cref="TierService"/>, minus the parts of it that were wrong.</b> Content-addressed
/// artifacts, digest over the compressed bytes, dedicated background thread, failures swallowed into a counter.
/// What is deliberately NOT copied: a manual refresh that only wakes the thread (it now makes the fetch due —
/// with a 6-hour cadence a no-op button would mean "후원했는데 왜 안 떠요"), and a fixed retry cadence (a
/// transient outage would otherwise mean a half-day blank).</para>
/// <para><b>Push, not pull.</b> The tier artifact is read on demand by the calculator, so a swapped document is
/// picked up for free. A grant list is not: the overlay memoises grants per (server, nickname) and only
/// <c>SetNameFxRoster</c> clears that memo. Hence <see cref="Changed"/> — without something marshalling it onto
/// the UI thread, a successful download changes nothing on screen until the next launch, with no error anywhere.</para>
/// </summary>
public sealed class NameFxService : IDisposable
{
    /// <summary>How often the manifest is checked. Matched to the server's hourly rebuild: the grant list is
    /// republished at the end of every tier refresh, so a new ranker is picked up within the hour rather than
    /// waiting out a longer window for no reason.
    /// <para>Hourly is affordable because the document is content-addressed and small. An unchanged list keeps
    /// its artifact id, so the poll costs one manifest request and no download — which is what most hours are.
    /// The tier artifact is different only because publishing it makes every client re-fetch ~230 KB.</para></summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    /// <summary>Let capture, the update check and the tier fetch finish before adding another socket. Later than
    /// <see cref="TierService"/>'s delay for that reason: a decoration is the least urgent thing at startup.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    /// <summary>Manual refresh cooldown, so a bored click cannot become a poll loop.</summary>
    private static readonly TimeSpan ManualCooldown = TimeSpan.FromSeconds(60);

    /// <summary>First retry after a failure, doubling up to <see cref="RefreshInterval"/>. The tier service
    /// retries only on its full cadence; at six hours that would turn one 503 into a half-day of nothing.</summary>
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(2);

    private const string KeyArtifactId = "namefx.artifactId";
    private const string KeyFetchedAt = "namefx.fetchedAtMs";

    /// <summary>Cached artifacts to keep. One is enough to run; a couple more make a rollback cheap.</summary>
    private const int KeepCachedArtifacts = 3;

    private readonly INameFxApi _api;
    private readonly PropertyHandler _props;
    private readonly Func<long> _clock;
    private readonly Func<string, bool> _isKnownEffect;
    private readonly Func<string, bool> _isKnownGauge;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Thread? _worker;
    private volatile bool _stopped;

    private NameFxRoster _roster = NameFxRoster.Empty;
    private string? _artifactId;
    private long _fetchedAtMs;
    private int _failures;
    private string? _lastError;
    private long _lastManualMs;
    private long _nextArtifactMs;
    private bool _usingLocalFile;

    /// <summary>The server published a document shape this build cannot read, so the poll is parked.
    /// <para>Written on the worker thread, cleared from the UI thread by the manual refresh — hence volatile.</para></summary>
    private volatile bool _schemaTooNew;

    /// <summary>Raised whenever the roster is replaced, including the load from disk in the constructor.
    /// <para>⚠ Raised on the worker thread. The only consumer that matters lives on the UI thread and clears a
    /// non-concurrent dictionary, so whoever subscribes must marshal.</para></summary>
    public event Action<NameFxRoster>? Changed;

    public NameFxService(
        INameFxApi api,
        PropertyHandler props,
        Func<string, bool> isKnownEffect,
        Func<string, bool> isKnownGauge,
        Func<long>? clock = null,
        bool startWorker = true)
    {
        _api = api;
        _props = props;
        _isKnownEffect = isKnownEffect;
        _isKnownGauge = isKnownGauge;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        LoadFromDisk();

        if (startWorker)
        {
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "namefx-artifact" };
            _worker.Start();
        }
    }

    /// <summary>The current grant list. Never null — an absent or unreadable document means "nobody has an
    /// effect", which is the only sane failure mode for a decoration.</summary>
    public NameFxRoster Roster => _roster;

    /// <summary>When the worker will next consider fetching. Exposed so the manual path is observable — waking
    /// the thread is not the same thing as making the fetch due, and only one of those is testable.</summary>
    public long NextArtifactCheckAtMs => Interlocked.Read(ref _nextArtifactMs);

    public NameFxServiceStatus Status() =>
        new(_artifactId != null, _artifactId, _fetchedAtMs, _roster.Count, _failures, _lastError, _usingLocalFile);

    /// <summary>
    /// Send this character's chosen effect to the server, and apply it locally at once.
    /// <para>The local apply is not optimism for its own sake: the artifact only reaches other people on the
    /// next publish, and without it the person who just picked would watch nothing happen on their own row.
    /// The server is still the authority — the next fetch overwrites whatever is patched here, which is what
    /// makes a rejected choice self-correct.</para>
    /// <para>⚠ Blocking network call. The settings window must not call this on the UI thread.</para>
    /// </summary>
    public NameFxChoiceResponse SubmitChoice(string identityHash, string effectId, string? gaugeId, string clientVersion)
    {
        if (LocalChoiceOnly)
        {
            _localPicks[identityHash] = (effectId, gaugeId);
            Publish(_roster);
            return new NameFxChoiceResponse(
                Ok: true, Kind: _roster.Find(identityHash)?.Kind, EffectId: effectId, GaugeId: gaugeId,
                Published: false);
        }

        NameFxChoiceResponse response = _api.PostNameFxChoice(
            new NameFxChoiceRequest(identityHash, effectId, gaugeId), clientVersion);

        if (response.Ok)
        {
            Publish(_roster.With(identityHash, response.EffectId ?? effectId, response.GaugeId));
        }

        return response;
    }

    /// <summary>
    /// Apply a pick to THIS INSTALL only, never sending it. Dev builds set this.
    ///
    /// <para><b>Why it exists.</b> The server is the authority on what a character wears, and it must stay that
    /// way — otherwise a client could give itself any mark. But that also means a skin cannot be looked at
    /// until the server side of it is deployed, and this repo ships the meter half first often enough that
    /// "the picker works but nothing happens" is a normal state to be stuck in while developing.</para>
    ///
    /// <para><b>What it deliberately does NOT do.</b> It cannot invent a grant:
    /// <see cref="NameFxRoster.With"/> returns the roster unchanged for a character the server never granted,
    /// so a local pick can only ever re-arrange marks this install was already entitled to. And nothing is
    /// published — other players see whatever the server last said.</para>
    /// </summary>
    public bool LocalChoiceOnly { get; set; }

    /// <summary>Local picks by identity hash, re-applied after every fetch. Without this the next hourly
    /// refresh would silently revert the pick, which reads as the setting not sticking.</summary>
    private readonly Dictionary<string, (string EffectId, string? GaugeId)> _localPicks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Settings' 후원자 목록 갱신 button. Rate-limited; returns false when the cooldown blocks it.</summary>
    public bool RequestManualRefresh()
    {
        long now = _clock();
        if (now - _lastManualMs < ManualCooldown.TotalMilliseconds)
        {
            return false;
        }

        _lastManualMs = now;
        // 상위 스키마로 접어둔 폴링을 한 번 풀어준다 — 서버가 롤백했을 수 있고, 그 확인 비용은 요청 1회다.
        // 여전히 상위면 아래에서 곧바로 다시 접힌다.
        _schemaTooNew = false;
        Interlocked.Exchange(ref _nextArtifactMs, 0);
        _wake.Set();
        return true;
    }

    public void Dispose()
    {
        _stopped = true;
        _wake.Set();
        _wake.Dispose();
    }

    private void WorkerLoop()
    {
        if (_wake.Wait(StartupDelay) && _stopped)
        {
            return;
        }

        while (!_stopped)
        {
            _wake.Reset();

            if (!_schemaTooNew && _clock() >= Interlocked.Read(ref _nextArtifactMs))
            {
                TryRefresh(); // re-arms the schedule itself
            }

            long waitMs = Math.Clamp(Interlocked.Read(ref _nextArtifactMs) - _clock(), 0, (long)RefreshInterval.TotalMilliseconds);
            if (_wake.Wait((int)waitMs) && _stopped)
            {
                return;
            }
        }
    }

    /// <summary>One fetch cycle. Failures are swallowed on purpose — a decoration outage must never surface as an
    /// error in a combat overlay; the counter is exposed through <see cref="Status"/> for the settings screen.</summary>
    internal void TryRefresh()
    {
        // Armed before the attempt, so "a fetch happened" and "the next one is due" cannot drift apart. A failure
        // pulls it back in below.
        Interlocked.Exchange(ref _nextArtifactMs, _clock() + (long)RefreshInterval.TotalMilliseconds);

        try
        {
            NameFxManifestResponse manifest = _api.GetNameFxManifest();
            if (manifest.SchemaVersion > NameFxRoster.MaxSchemaVersion)
            {
                // A newer document shape: keep serving the cached one rather than guessing at its meaning.
                //
                // 🔑 판정 38. 이건 <b>일시적 실패가 아니다</b>. 서버가 문서 모양을 올린 것이고 그 번호는
                // 내려오지 않는다. 종전에는 Fail() 로 떨어뜨렸는데, Fail 의 백오프는 정상 주기에서 멈추므로
                // 결과가 "한 시간마다 영원히 같은 매니페스트를 받아 같은 이유로 버린다"였다 — 고칠 방법은
                // 미터 업데이트뿐이고 그건 어차피 프로세스 재시작을 뜻한다. 그래서 폴링 자체를 접는다.
                // 캐시된 로스터는 계속 그린다(장식이 사라지는 것보다 옛 목록이 낫다).
                // 탈출구는 설정의 수동 갱신 버튼 하나 — 그것만 이 중단을 풀어준다.
                _schemaTooNew = true;
                _failures++;
                _lastError = $"unsupported_schema_{manifest.SchemaVersion}";
                Interlocked.Exchange(ref _nextArtifactMs, long.MaxValue);
                return;
            }

            if (string.Equals(_artifactId, manifest.ArtifactId, StringComparison.Ordinal))
            {
                Succeed(); // unchanged — no download
                return;
            }

            StatsBinaryResponse response = _api.GetNameFxArtifactGzip(manifest.Url);
            if (!VerifyDigest(response.Body, manifest.Sha256))
            {
                Fail("sha256_mismatch");
                return;
            }

            string json = Inflate(response.Body);
            NameFxRoster parsed = NameFxRoster.Parse(json, _clock(), _isKnownEffect, _isKnownGauge);

            // Write the cache only after the document parsed. A file that cannot be read is worse than no file:
            // it survives restarts and there is nothing on screen to explain it.
            WriteCache(manifest.ArtifactId, response.Body);
            _artifactId = manifest.ArtifactId;
            _usingLocalFile = false;
            _props.SetProperty(KeyArtifactId, manifest.ArtifactId);
            Publish(parsed);
            Succeed();
            SweepCache(manifest.ArtifactId);
        }
        catch (Exception ex)
        {
            Fail(ex is StatsApiException api ? api.Message : ex.GetType().Name);
        }
    }

    private void Succeed()
    {
        _fetchedAtMs = _clock();
        _props.SetProperty(KeyFetchedAt, _fetchedAtMs.ToString(CultureInfo.InvariantCulture));
        _failures = 0;
        _lastError = null;
    }

    private void Fail(string reason)
    {
        _failures++;
        _lastError = reason;

        // Exponential backoff, capped at the normal cadence. Without this a single 503 costs six hours.
        double backoff = Math.Min(
            FirstBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(_failures - 1, 8)),
            RefreshInterval.TotalMilliseconds);
        Interlocked.Exchange(ref _nextArtifactMs, _clock() + (long)backoff);
    }

    private void Publish(NameFxRoster roster)
    {
        // Local picks are re-applied on the way out rather than stored in the roster, so a server refresh
        // cannot drop them and they never leak into anything that gets written back or sent.
        foreach ((string hash, (string effectId, string? gaugeId)) in _localPicks)
        {
            roster = roster.With(hash, effectId, gaugeId);
        }

        _roster = roster;
        Changed?.Invoke(roster);
    }

    /// <summary>
    /// Restore the last good roster without touching the network.
    /// <para>Two sources, in order: the cached server artifact, then the plain
    /// <c>namefx\supporters.json</c>. The plain file is the development/demo path that predates this service —
    /// keeping it means a build with no server to talk to still renders, and it costs one <c>File.Exists</c>.</para>
    /// </summary>
    private void LoadFromDisk()
    {
        string? id = _props.GetProperty(KeyArtifactId);
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                string path = CachePath(id!);
                if (File.Exists(path))
                {
                    NameFxRoster cached = NameFxRoster.Parse(
                        Inflate(File.ReadAllBytes(path)), _clock(), _isKnownEffect, _isKnownGauge);
                    _artifactId = id;
                    _fetchedAtMs = long.TryParse(_props.GetProperty(KeyFetchedAt), NumberStyles.Integer, CultureInfo.InvariantCulture, out long at) ? at : 0;
                    Publish(cached);
                    return;
                }
            }
            catch
            {
                // fall through to the plain file
            }
        }

        NameFxRoster local = NameFxRoster.Load(_props.AppDirectory(), _clock(), _isKnownEffect, _isKnownGauge);
        if (local.Count > 0)
        {
            _usingLocalFile = true;
            Publish(local);
        }
    }

    private string CacheDir() => Path.Combine(_props.AppDirectory(), "namefx");

    private string CachePath(string artifactId) => Path.Combine(CacheDir(), artifactId + ".json.gz");

    private void WriteCache(string artifactId, byte[] gzip)
    {
        Directory.CreateDirectory(CacheDir());
        File.WriteAllBytes(CachePath(artifactId), gzip);
    }

    /// <summary>Drop superseded artifacts. Scoped to <c>*.json.gz</c>, which is why the plain
    /// <c>supporters.json</c> that shares this folder survives — a blanket delete would eat it.</summary>
    private void SweepCache(string keepId)
    {
        try
        {
            foreach (FileInfo f in new DirectoryInfo(CacheDir())
                         .GetFiles("*.json.gz")
                         .Where(f => !f.Name.StartsWith(keepId, StringComparison.Ordinal))
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(KeepCachedArtifacts - 1))
            {
                f.Delete();
            }
        }
        catch
        {
            // housekeeping must never fail a refresh
        }
    }

    /// <summary>Unwrap the envelope if there is one, then inflate.
    /// <para>Plain gzip is still accepted so a cache written before the envelope existed keeps working —
    /// otherwise every install would lose its roster on upgrade and sit blank until the next poll.</para></summary>
    private static string Inflate(byte[] payload)
    {
        byte[] gzip = NameFxEnvelope.IsSealed(payload) ? NameFxEnvelope.Open(payload) : payload;
        using var input = new MemoryStream(gzip);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>🔑 The digest is over the COMPRESSED bytes exactly as received — see
    /// <c>StatsApiClient.GetTierArtifactGzip</c> for why the server refuses to declare an encoding.</summary>
    private static bool VerifyDigest(byte[] gzip, string? expectedHex)
    {
        if (string.IsNullOrEmpty(expectedHex) || gzip.Length == 0)
        {
            return false;
        }

        return string.Equals(
            Convert.ToHexStringLower(SHA256.HashData(gzip)),
            expectedHex!.Trim().ToLowerInvariant(),
            StringComparison.Ordinal);
    }
}
