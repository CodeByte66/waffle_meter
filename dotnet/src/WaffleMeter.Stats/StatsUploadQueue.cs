using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using WaffleMeter.Data;
using WaffleMeter.Services;

namespace WaffleMeter.Stats;

/// <summary>
/// Verbatim port of Kotlin <c>stats.StatsUploadQueue</c>: consent-gated, boss-only, kill-confirmed
/// upload of finished battles on a single background worker, with battle-hash de-duplication and
/// running counters. Wired to <see cref="DpsCalculator.OnBattleLogged"/> in the live app.
///
/// The work dispatcher and the kill-recheck delay are injected so the queue can run synchronously
/// (and instantly) under test; by default it owns a daemon thread and waits 4s before re-checking a
/// not-yet-confirmed kill (the boss may die just after the report is cut).
/// </summary>
public sealed class StatsUploadQueue : IDisposable
{
    private readonly StatsConsentManager _consent;
    private readonly StatsPayloadBuilder _builder;
    private readonly StatsApiClient _api;
    private readonly DataManager _data;
    private readonly PropertyHandler _props;
    private readonly Action<Action> _dispatch;
    private readonly Action _killRecheckDelay;
    private readonly Func<long> _clock;

    private readonly HashSet<string> _uploadedHashes = new();
    private readonly object _hashLock = new();

    private int _pending;
    private int _uploaded;
    private int _skipped;
    private int _failed;
    private long _lastUpdatedAt;
    private volatile string _clientVersion = "dev";
    private volatile string? _lastPath;
    private volatile string? _lastReason;

    private readonly BlockingCollection<Action>? _queue;
    private readonly Thread? _worker;

    /// <summary>Total tries per battle, initial attempt included. Two waits (1 s + 2 s) on top of the read
    /// timeouts the single attempt already cost.</summary>
    private const int MaxUploadAttempts = 3;
    private const int BaseRetryDelayMs = 1_000;

    /// <summary>Ceiling on a server-supplied <c>Retry-After</c>. A rate limit measured in minutes must not
    /// park the upload worker for minutes.</summary>
    private const int MaxRetryAfterMs = 10_000;

    /// <summary>How long to stop retrying after a battle exhausted its attempts. Bounds the damage of a real
    /// outage: without it every queued battle would pay the full retry budget in turn.</summary>
    private const int RetryPauseMs = 60_000;

    private readonly Action<int> _retryDelay;
    private long _retryPausedUntilMs;

    public StatsUploadQueue(
        StatsConsentManager consent,
        StatsPayloadBuilder builder,
        StatsApiClient api,
        DataManager data,
        PropertyHandler props,
        Action<Action>? dispatch = null,
        Action? killRecheckDelay = null,
        Func<long>? clock = null,
        Action<int>? retryDelay = null)
    {
        _retryDelay = retryDelay ?? Thread.Sleep;
        _consent = consent;
        _builder = builder;
        _api = api;
        _data = data;
        _props = props;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _killRecheckDelay = killRecheckDelay ?? (() => Thread.Sleep(4_000));

        // 이전 세션의 카운터를 이어받는다 — 이게 없으면 "얼마나 자주 나는가"를 영영 못 잰다.
        LoadCounts();

        if (dispatch != null)
        {
            _dispatch = dispatch;
        }
        else
        {
            _queue = new BlockingCollection<Action>();
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "stats-upload-queue" };
            _worker.Start();
            _dispatch = job =>
            {
                if (!_queue.IsAddingCompleted)
                {
                    _queue.Add(job);
                }
            };
        }
    }

    private void WorkerLoop()
    {
        foreach (Action job in _queue!.GetConsumingEnumerable())
        {
            try
            {
                job();
            }
            catch
            {
                // a single upload failure must not kill the worker
            }
        }
    }

    public void Configure(string version) => _clientVersion = version;

    /// <summary>Raised on the upload worker thread when the server returned the uploader character's career
    /// tier alongside the receipt: <c>(identityHash, tier)</c>. Handlers must be thread-safe.</summary>
    public Action<string, TierSnapshotDto>? TierReceived { get; set; }

    public void OfferIfEligible(DpsLog log)
    {
        if (!_consent.IsUploadAllowed())
        {
            MarkSkipped("consent_not_allowed");
            return;
        }

        MobInfo? target = log.Report.Target;

        // 허수아비 런은 <b>조용히</b> 무시한다 — 카운터도, 최근 스킵 사유도 건드리지 않는다.
        // 스킵 진단은 "올라갔어야 할 전투가 왜 안 올라갔나"에 답하려고 있는 화면인데(설정›통계의 한 줄),
        // 허수아비는 애초에 후보가 아니다. 아래 not_boss 로 흘려보내면 연습 30번이 그 줄을
        // "건너뜀 +30 · 최근: 보스 전투가 아님"으로 덮어 사용자가 쫓던 진짜 사유를 지운다.
        // (허수아비 런이 여기까지 오는 것은 전투 기록의 '허수아비' 탭을 만들며 저장을 푼 뒤부터다.)
        if (target?.Mob.IsDummy == true)
        {
            return;
        }

        if (target == null || !target.Mob.Boss)
        {
            MarkSkipped("not_boss");
            return;
        }

        // '미상 보스'(스폰 유실로 HP 휴리스틱 승격된 미등록 보스)는 코드가 추정이라 통계 웹에 올리지 않는다 —
        // 로컬 DPS 집계만 되살리는 것이 목적이다.
        if (target.Mob.Code == DataManager.UnknownBossMobCode)
        {
            MarkSkipped("estimated_boss");
            return;
        }

        // 통계 웹이 집계하는 던전(원정/초월/성역)의 보스가 아니면 보내지 않는다. 서버는 카탈로그에 없는 mobCode를
        // 400 unsupported_encounter로 거절하는데 미터엔 재시도 경로가 없어서, 그렇게 거절된 전투는 그대로 사라진다
        // — 필드보스처럼 애초에 집계 대상이 아닌 전투로 그 실패를 만들 이유가 없다. 카탈로그가 없으면(자산 누락)
        // IsSupported가 전부 true라 예전 동작 그대로다.
        if (!_data.Encounters.IsSupported(target.Mob.Code))
        {
            // 코드와 이름을 사유에 싣는다 — 이 게이트가 잘못 걸렸을 때(웹이 새 던전을 등록했는데 동봉
            // 카탈로그가 아직 옛것) 드러나는 유일한 경로가 여기고, 코드 없는 고정 문자열은 "안 올라간다"는
            // 제보를 미동의·보스아님·미상보스와 구분해주지 못한다.
            //
            // ⚠️ **이 스킵이 쌓인다고 해서 카탈로그가 뒤처진 것은 아니다.** 게임 데이터가 boss=true 로
            // 표시하지만 통계 웹이 **일부러 인카운터에서 뺀** 몹이 있다 — 길목에 서 있는 기어체크류다.
            // 확인된 예: **염화의 수호검**(무스펠의 성배). 한 판에 두 번 교전되므로 스킵이 꾸준히 찍힌다.
            //   어려움 2301055 · 2301067 (와이어 실측: 2301059/2301060 과 같은 인스턴스에서 스폰)
            //   보통   2301085 · 2301097 (덤프 대칭 추정, 실측 없음)
            // 무스펠 수호상(2921274) 같은 292xxxx 기믹 대역도 마찬가지로 대상이 아니다.
            //
            // 🔑 클라 덤프로는 이 판단을 **확정도 반증도 못 한다** — 그 코드들은 단계/스크립트 스폰이라
            // `MapData.SpawnInfoList` 에 애초에 안 실린다(무스펠 두 맵에서 빠진 오프셋이 …55/…62/…67 로
            // 동일). 2026-09-20 에 이걸 모르고 덤프를 한 바퀴 뒤졌다. 다음에 같은 스킵을 보면
            // **덤프를 뒤지기 전에 "이거 일부러 뺀 건가"를 웹/오너에게 먼저 물어라.**
            MarkSkipped($"unsupported_encounter:{target.Mob.Code}:{target.Mob.Name}");
            return;
        }

        if (IsKillConfirmed(log))
        {
            Enqueue(log, killConfirmed: true);
            return;
        }

        _dispatch(() =>
        {
            Interlocked.Increment(ref _pending);
            try
            {
                _killRecheckDelay();
                if (IsKillConfirmed(log))
                {
                    UploadPayload(log, killConfirmed: true);
                }
                else
                {
                    MarkSkipped("not_kill");
                }
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        });
    }

    public StatsUploadStatus Status() => new(
        Enabled: _consent.IsUploadAllowed(),
        Pending: Volatile.Read(ref _pending),
        Uploaded: Volatile.Read(ref _uploaded),
        Skipped: Volatile.Read(ref _skipped),
        Failed: Volatile.Read(ref _failed),
        LastPath: _lastPath,
        LastReason: _lastReason,
        LastUpdatedAt: Interlocked.Read(ref _lastUpdatedAt),
        SkipReasons: _skipCounts
            .Select(kv => new StatsSkipCount(kv.Key, kv.Value))
            .OrderByDescending(r => r.Count)
            .ToArray());

    public string OpenFolder()
    {
        string dir = Path.Combine(_props.AppDirectory(), "stats-upload");
        Directory.CreateDirectory(dir);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch
        {
            // best effort
        }

        return dir;
    }

    private void Enqueue(DpsLog log, bool killConfirmed)
    {
        _dispatch(() =>
        {
            Interlocked.Increment(ref _pending);
            try
            {
                UploadPayload(log, killConfirmed);
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        });
    }

    private void UploadPayload(DpsLog log, bool killConfirmed)
    {
        switch (_builder.Build(log, _clientVersion, killConfirmed))
        {
            case BuildResult.Skip skip:
                MarkSkipped(skip.Reason);
                break;
            case BuildResult.Payload built:
                StatsUploadPayload payload = built.Value;
                lock (_hashLock)
                {
                    if (_uploadedHashes.Contains(payload.BattleHash))
                    {
                        MarkSkipped("duplicate");
                        return;
                    }
                }

                try
                {
                    ReportUploadResponse response = PostWithRetry(payload);
                    lock (_hashLock)
                    {
                        _uploadedHashes.Add(payload.BattleHash);
                    }

                    // The signed upload earned/confirmed this install's grant for the uploader character —
                    // cache it so the "공개" toggle unlocks without waiting for a consent round-trip (§2.2).
                    if (response.Granted)
                    {
                        _consent.MarkGranted(payload.Character.IdentityHash, _clientVersion);
                    }
                    else
                    {
                        // The battle landed but earned no ownership. The only way that happens is the server
                        // treating the write as unsigned (warn mode accepts it and skips grantCharacter), and
                        // nothing used to record it — so an install whose signing had quietly broken kept
                        // uploading forever while 공개 전환 and 스킨 선택 stayed locked, with no counter anywhere
                        // that would have shown it.
                        _skipCounts.AddOrUpdate("unsigned_upload", 1, (_, n) => n + 1);
                        SaveCounts();
                    }

                    // 던전 티어는 업로드 응답에 얹혀 온다 — 본인 등급을 얻는 데 요청이 0회 더 든다는 뜻이다.
                    // 서버가 줄 게 없으면 키 자체가 빠지므로 구버전 서버에서도 그냥 null이다.
                    if (response.Tier is { } tier && payload.Character.IdentityHash is { Length: > 0 } hash)
                    {
                        TierReceived?.Invoke(hash, tier);
                    }

                    Interlocked.Increment(ref _uploaded);
                    string reason = response.Duplicate ? "uploaded_duplicate" : "uploaded";
                    UpdateLast(_api.ReportEndpoint(), $"{reason}:{response.ReportId ?? "no_report_id"}");
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref _failed);
                    UpdateLast(_api.ReportEndpoint(), $"upload_failed:{Summarize(e)}");
                }

                break;
        }
    }

    /// <summary>
    /// Upload, retrying a transient failure a couple of times.
    /// <para>Before this, one 502 or one network blip was a battle gone for good — the queue caught the
    /// exception, bumped a counter and dropped the payload. Observed twice in two days: six uploads lost on
    /// 2026-08-04 when nginx marked both upstreams down over slow responses, two more on 08-05 to the same
    /// cause. Re-sending is safe because the server keys on <c>battle_hash</c>, and this side only records the
    /// hash as uploaded after a success.</para>
    /// <para>⚠️ This runs on the single upload worker, so its sleeping delays every battle queued behind it.
    /// That is why the budget is small (two waits, ~3 s) and why a run of failures opens
    /// <see cref="_retryPausedUntilMs"/>: during a real outage the first battle pays for the retries and the
    /// rest fail fast instead of the queue growing a minute per battle.</para>
    /// </summary>
    private ReportUploadResponse PostWithRetry(StatsUploadPayload payload)
    {
        bool paused = _clock() < Interlocked.Read(ref _retryPausedUntilMs);
        int attempts = paused ? 1 : MaxUploadAttempts;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                ReportUploadResponse response = _api.PostReport(payload, _clientVersion);
                Interlocked.Exchange(ref _retryPausedUntilMs, 0); // the server is answering again
                return response;
            }
            catch (Exception e) when (IsRetryable(e) && attempt < attempts)
            {
                _retryDelay(RetryDelayMs(attempt, (e as StatsApiException)?.RetryAfterSeconds));
            }
            catch (Exception e) when (IsRetryable(e))
            {
                // Out of attempts (or already paused). Stop paying the retry cost for a while — see the pause
                // note above. Rethrown so the caller records the failure exactly as it always did.
                Interlocked.Exchange(ref _retryPausedUntilMs, _clock() + RetryPauseMs);
                throw;
            }

            // Anything else is a verdict on this request (400/401/409/…): it propagates on the first attempt.
        }
    }

    /// <summary>Whether re-sending could plausibly succeed. A transport fault never arrives as
    /// <see cref="StatsApiException"/> — it is an <c>HttpRequestException</c> or a timeout's
    /// <c>TaskCanceledException</c>, which is the very case retrying exists for.</summary>
    private static bool IsRetryable(Exception e) =>
        e is not StatsApiException api || api.IsTransient;

    /// <summary>Backoff for the given attempt, honouring the server's <c>Retry-After</c> when it sent one.
    /// The header wins because it is the server saying how long it needs, but it is capped so a large value
    /// cannot stall the worker.</summary>
    private static int RetryDelayMs(int attempt, int? retryAfterSeconds)
    {
        if (retryAfterSeconds is > 0 and int seconds)
        {
            return Math.Min(seconds * 1_000, MaxRetryAfterMs);
        }

        return BaseRetryDelayMs << (attempt - 1); // 1s, 2s
    }

    private bool IsKillConfirmed(DpsLog log)
    {
        MobInfo? target = log.Report.Target;
        if (target == null)
        {
            return false;
        }

        bool snapshotKill = target.MaxHp > 0 && target.RemainHp <= 0;
        long? latestHp = _data.MobHp(target.Id);
        long latestMaxHp = _data.MobMaxHp(target.Id) ?? target.MaxHp;
        bool latestKill = latestMaxHp > 0 && latestHp == 0;
        return snapshotKill || latestKill;
    }

    private void MarkSkipped(string reason)
    {
        Interlocked.Increment(ref _skipped);
        // Count by reason, not just "last one wins". A gate that eats every battle for one character looks
        // exactly like a one-off in a single-slot field, and that is how a character can sit out of statistics
        // for months with nothing on screen that says so.
        _skipCounts.AddOrUpdate(SkipKey(reason), 1, (_, n) => n + 1);
        UpdateLast(null, reason);
        SaveCounts(); // 전투당 한 번꼴이라 작은 파일 한 번 쓰는 비용이면 충분하다

    }

    /// <summary>Group key for the counter. Reasons that carry data (<c>unsupported_encounter:&lt;code&gt;:&lt;name&gt;</c>)
    /// collapse to their prefix so one recurring cause reads as one row instead of a hundred.</summary>
    private static string SkipKey(string reason)
    {
        int colon = reason.IndexOf(':');
        return colon > 0 ? reason[..colon] : reason;
    }

    /// <summary>Skip reason -> count for this session. Concurrent because battles are offered from the report
    /// thread while the settings screen reads it on the UI thread.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _skipCounts = new(StringComparer.Ordinal);

    /// <summary>이 카운터가 처음 세기 시작한 시각(ms). 비율을 내려면 분모가 있어야 한다.</summary>
    private long _countsSince;

    private string CountsPath() => Path.Combine(_props.AppDirectory(), "stats-upload", "skip-counts.json");

    /// <summary>
    /// 스킵 카운터를 디스크에 남긴다.
    /// <para>🔑 종전에는 메모리 전용이라 앱을 끄면 0 으로 돌아갔다. 그래서 <b>"내 전투가 왜 안 올라갔지"를 사후에
    /// 확인할 방법이 없었다</b> — 설정 화면에서 이번 세션 값을 눈으로 읽는 것 말고는 어디에도 안 남았고, 서버
    /// 쪽은 애초에 도달하지 못한 전투라 볼 수가 없다(양쪽 다 못 재는 상태였다).</para>
    /// <para>settings.properties 에 넣지 않는 이유: 그 파일은 <c>SetProperty</c> 마다 <b>전량 재작성</b>되고
    /// EUC-KR 계열 디코딩을 거친다. 이 카운터는 전투당 한 번꼴로 움직이므로 작은 전용 파일이 맞다.</para>
    /// <para>실패는 삼킨다 — 진단 보조 기능이 업로드를 막으면 안 된다.</para>
    /// </summary>
    private void SaveCounts()
    {
        try
        {
            string path = CountsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var doc = new
            {
                since = Interlocked.Read(ref _countsSince),
                updatedAt = _clock(),
                uploaded = Volatile.Read(ref _uploaded),
                skipped = Volatile.Read(ref _skipped),
                failed = Volatile.Read(ref _failed),
                reasons = _skipCounts.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            };

            // temp + move: 쓰는 도중에 프로세스가 죽어도 반쪽 파일이 남지 않는다.
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(doc));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // 진단이 본업을 막지 않는다.
        }
    }

    private void LoadCounts()
    {
        Interlocked.Exchange(ref _countsSince, _clock());
        try
        {
            string path = CountsPath();
            if (!File.Exists(path))
            {
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("since", out JsonElement since) && since.TryGetInt64(out long sinceMs) && sinceMs > 0)
            {
                Interlocked.Exchange(ref _countsSince, sinceMs);
            }

            if (root.TryGetProperty("uploaded", out JsonElement up) && up.TryGetInt32(out int upN))
            {
                Volatile.Write(ref _uploaded, upN);
            }

            if (root.TryGetProperty("skipped", out JsonElement sk) && sk.TryGetInt32(out int skN))
            {
                Volatile.Write(ref _skipped, skN);
            }

            if (root.TryGetProperty("failed", out JsonElement fa) && fa.TryGetInt32(out int faN))
            {
                Volatile.Write(ref _failed, faN);
            }

            if (root.TryGetProperty("reasons", out JsonElement reasons) && reasons.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty r in reasons.EnumerateObject())
                {
                    if (r.Value.TryGetInt32(out int n) && n > 0)
                    {
                        _skipCounts[r.Name] = n;
                    }
                }
            }
        }
        catch
        {
            // 손상된 카운터 파일이 기동을 막지 않는다 — 0 부터 다시 센다.
        }
    }

    private void UpdateLast(string? path, string reason)
    {
        _lastPath = path;
        _lastReason = reason;
        Interlocked.Exchange(ref _lastUpdatedAt, _clock());
    }

    private static string Summarize(Exception e)
    {
        string? message = e.Message;
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message.Length > 160 ? message[..160] : message;
        }

        return e.GetType().Name;
    }

    public void Dispose()
    {
        _queue?.CompleteAdding();
        _worker?.Join(2000);
        _queue?.Dispose();
    }
}
