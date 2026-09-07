using WaffleMeter.Capture;
using WaffleMeter.Data;
using WaffleMeter.Services;
using WaffleMeter.Stats;
using Xunit;

namespace WaffleMeter.Stats.Tests;

public sealed class StatsUploadQueueTests : IDisposable
{
    private readonly string _temp;
    private readonly PropertyHandler _props;
    private readonly DataManager _dm;

    public StatsUploadQueueTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "wm_queue_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _props = new PropertyHandler(_temp);
        _dm = new DataManager();
        _dm.SaveNickname(1, "Me", isExecutor: true, server: 3, jobByte: 5);
        _dm.SaveUserPower(1, 5000);
        _dm.SaveNickname(2, "Ally", isExecutor: false, server: 3, jobByte: 25);
        _dm.SaveUserPower(2, 3000);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static StatsApiClient AcceptingApi() => new(() => "install-1", (_, url, _, _) =>
        url.Contains("/api/v1/reports")
            ? new StatsHttpResponse(200, """{"ok":true,"reportId":"r1","duplicate":false}""")
            : new StatsHttpResponse(200, """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":false,"consentVersion":"2026-06-04"}"""));

    private StatsUploadQueue NewQueue(StatsApiClient api, bool accept)
    {
        var builder = new StatsPayloadBuilder(_dm, () => false);
        var consent = new StatsConsentManager(_props, _dm, api, () => builder.OwnCharacter());
        if (accept)
        {
            consent.Set("accepted", uploadEnabled: true, publicCharacter: false);
        }

        return new StatsUploadQueue(consent, builder, api, _dm, _props,
            dispatch: job => job(), killRecheckDelay: () => { }, clock: () => 1, retryDelay: _ => { });
    }

    private DpsLog DummyLog()
    {
        DpsLog log = BossLog(remainHp: 0, boss: false, mobCode: 2300229);
        log.Report.Target = new MobInfo(200, new Mob(2300229, "훈련용 허수아비", Boss: false, IsDummy: true), 0, 0);
        return log;
    }

    [Fact]
    public void A_dummy_run_is_ignored_without_touching_the_skip_diagnostics()
    {
        // 허수아비 런은 전투 기록의 '허수아비' 탭 때문에 저장되기 시작했고, 그래서 이 큐까지 온다. 업로드는
        // 물론 막혀야 하지만 "건너뜀" 카운터와 최근 사유까지 갱신하면, 연습 30번이 설정›통계의 진단 줄을
        // "건너뜀 +30 · 최근: 보스 전투가 아님"으로 덮어 사용자가 쫓던 진짜 사유를 지운다.
        using StatsUploadQueue queue = NewQueue(AcceptingApi(), accept: true);
        queue.OfferIfEligible(BossLog(0, boss: false));       // 진짜 스킵 — 사유가 남아야 한다
        string realReason = queue.Status().LastReason;
        int realSkips = queue.Status().Skipped;

        for (int i = 0; i < 5; i++)
        {
            queue.OfferIfEligible(DummyLog());
        }

        StatsUploadStatus status = queue.Status();
        Assert.Equal(0, status.Uploaded);            // 업로드는 여전히 차단
        Assert.Equal(realSkips, status.Skipped);     // 카운터가 안 움직였다
        Assert.Equal(realReason, status.LastReason); // 진짜 사유가 그대로 남았다
    }

    private DpsLog BossLog(int remainHp, bool boss = true, int mobCode = 12345)
    {
        User me = _dm.User(1)!;
        User ally = _dm.User(2)!;
        var report = new DpsReport
        {
            Contributors = new List<User> { me, ally },
            BattleStart = 1_000_000,
            BattleEnd = 1_030_000,
            Target = new MobInfo(100, new Mob(mobCode, "보스", boss), remainHp: remainHp, maxHp: 1_000_000),
            Information = new Dictionary<int, DpsInformation>
            {
                [1] = new DpsInformation(1_000_000, 50_000, 60.0, 40.0),
                [2] = new DpsInformation(600_000, 30_000, 40.0, 24.0),
            },
        };
        return new DpsLog
        {
            Report = report,
            SkillDetails = new Dictionary<int, Dictionary<string, AnalyzedSkill>>
            {
                [1] = new() { ["11020001"] = new AnalyzedSkill { SkillCode = 11020001, Name = "강타", DamageAmount = 1_000_000, Times = 100 } },
                [2] = new() { ["15210001"] = new AnalyzedSkill { SkillCode = 15210001, Name = "파이어", DamageAmount = 600_000, Times = 50 } },
            },
            BuffRates = new Dictionary<int, List<OperatingData>>(),
            BossBuffRates = new List<OperatingData>(),
        };
    }

    [Fact]
    public void Skips_when_consent_not_allowed()
    {
        using StatsUploadQueue queue = NewQueue(AcceptingApi(), accept: false);
        queue.OfferIfEligible(BossLog(0));

        StatsUploadStatus status = queue.Status();
        Assert.Equal(0, status.Uploaded);
        Assert.Equal(1, status.Skipped);
        Assert.Equal("consent_not_allowed", status.LastReason);
    }

    [Fact]
    public void Skips_non_boss()
    {
        using StatsUploadQueue queue = NewQueue(AcceptingApi(), accept: true);
        queue.OfferIfEligible(BossLog(0, boss: false));

        Assert.Equal("not_boss", queue.Status().LastReason);
        Assert.Equal(0, queue.Status().Uploaded);
    }

    [Fact]
    public void Uploads_a_confirmed_boss_kill()
    {
        using StatsUploadQueue queue = NewQueue(AcceptingApi(), accept: true);
        queue.OfferIfEligible(BossLog(remainHp: 0)); // snapshot kill

        StatsUploadStatus status = queue.Status();
        Assert.Equal(1, status.Uploaded);
        Assert.Equal(0, status.Skipped);
        Assert.Equal(0, status.Failed);
        Assert.StartsWith("uploaded:", status.LastReason);
    }

    [Fact]
    public void Dedupes_repeat_battle_hash()
    {
        using StatsUploadQueue queue = NewQueue(AcceptingApi(), accept: true);
        DpsLog log = BossLog(remainHp: 0);

        queue.OfferIfEligible(log);
        queue.OfferIfEligible(log);

        StatsUploadStatus status = queue.Status();
        Assert.Equal(1, status.Uploaded);
        Assert.Equal(1, status.Skipped);
        Assert.Equal("duplicate", status.LastReason);
    }

    [Fact]
    public void Skips_when_kill_not_confirmed_after_recheck()
    {
        using StatsUploadQueue queue = NewQueue(AcceptingApi(), accept: true);
        queue.OfferIfEligible(BossLog(remainHp: 500_000)); // boss still alive, no mob-hp update

        StatsUploadStatus status = queue.Status();
        Assert.Equal(0, status.Uploaded);
        Assert.Equal("not_kill", status.LastReason);
    }

    [Fact]
    public void Confirms_kill_via_latest_mob_hp()
    {
        _dm.SaveMobHp(100, 0);
        _dm.SaveMobMaxHp(100, 1_000_000);

        using StatsUploadQueue queue = NewQueue(AcceptingApi(), accept: true);
        queue.OfferIfEligible(BossLog(remainHp: 500_000)); // snapshot not a kill, but latest hp is 0

        Assert.Equal(1, queue.Status().Uploaded);
    }

    [Fact]
    public void Successful_signed_upload_with_granted_caches_the_grant()
    {
        // Report response carries granted:true -> the queue must cache the uploader character's grant so the
        // public toggle unlocks without a separate consent round-trip (§2.2, W17 upload path).
        StatsApiClient api = new(() => "install-1", (_, url, _, _) =>
            url.Contains("/api/v1/reports")
                ? new StatsHttpResponse(200, """{"ok":true,"reportId":"r1","duplicate":false,"granted":true}""")
                : new StatsHttpResponse(200, """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":false,"consentVersion":"2026-06-04"}"""));
        var builder = new StatsPayloadBuilder(_dm, () => false);
        var consent = new StatsConsentManager(_props, _dm, api, () => builder.OwnCharacter());
        consent.Set("accepted", uploadEnabled: true, publicCharacter: false);
        using var queue = new StatsUploadQueue(consent, builder, api, _dm, _props,
            dispatch: job => job(), killRecheckDelay: () => { }, clock: () => 1, retryDelay: _ => { });

        queue.OfferIfEligible(BossLog(remainHp: 0));

        Assert.Equal(1, queue.Status().Uploaded);
        Assert.True(consent.HasGrant(StatsIdentity.CharacterIdentityHash(3, "Me")!));
    }

    [Fact]
    public void Records_failure_when_report_upload_throws()
    {
        StatsApiClient api = new(() => "install-1", (_, url, _, _) =>
            url.Contains("/api/v1/reports")
                ? new StatsHttpResponse(500, "server_down")
                : new StatsHttpResponse(200, """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":false}"""));

        using StatsUploadQueue queue = NewQueue(api, accept: true);
        queue.OfferIfEligible(BossLog(remainHp: 0));

        StatsUploadStatus status = queue.Status();
        Assert.Equal(0, status.Uploaded);
        Assert.Equal(1, status.Failed);
        Assert.StartsWith("upload_failed:", status.LastReason);
    }

    // ── 지원 던전 게이트 ──────────────────────────────────────────────────────────────────────────
    // 웹은 카탈로그에 없는 mobCode를 400 unsupported_encounter로 거절하고 미터엔 재시도가 없다 — 그 전투는
    // 그대로 사라진다. 필드보스처럼 애초에 집계 대상이 아닌 전투로 그 실패를 만들 이유가 없다.

    private const string TwoDungeonCatalog = """
    {
      "dungeons": [
        {
          "key": "expedition-bakron-floating-island", "category": "원정", "categoryOrd": 1,
          "name": "바크론의 공중섬", "variantType": "difficulty",
          "bosses": [{"index": 1, "name": "바크론"}],
          "variants": [{"label": "시련", "dungeonId": 600074, "mobs": [[2300582, 1]]}]
        }
      ]
    }
    """;

    [Fact]
    public void Skips_a_boss_the_stats_web_has_no_catalog_for()
    {
        _dm.LoadEncounters(EncounterCatalog.Parse(TwoDungeonCatalog));
        using StatsUploadQueue queue = NewQueue(AcceptingApi(), accept: true);

        queue.OfferIfEligible(BossLog(remainHp: 0, mobCode: 2600068)); // 정령왕 아그로 — 어비스 필드보스

        StatsUploadStatus status = queue.Status();
        Assert.Equal(0, status.Uploaded);
        Assert.Equal(1, status.Skipped);
        // 사유에 코드와 이름이 실린다 — 이 게이트가 잘못 걸렸을 때 드러나는 유일한 경로다.
        Assert.Equal("unsupported_encounter:2600068:보스", status.LastReason);
    }

    [Fact]
    public void Uploads_a_boss_that_is_in_the_catalog()
    {
        _dm.LoadEncounters(EncounterCatalog.Parse(TwoDungeonCatalog));
        using StatsUploadQueue queue = NewQueue(AcceptingApi(), accept: true);

        queue.OfferIfEligible(BossLog(remainHp: 0, mobCode: 2300582)); // 시련 바크론

        StatsUploadStatus status = queue.Status();
        Assert.Equal(1, status.Uploaded);
        Assert.Equal(0, status.Skipped);
    }

    /// <summary>Fail-open: an install whose catalog asset is missing must keep uploading exactly as before,
    /// not go silent.</summary>
    [Fact]
    public void Uploads_everything_when_no_catalog_is_loaded()
    {
        using StatsUploadQueue queue = NewQueue(AcceptingApi(), accept: true);

        queue.OfferIfEligible(BossLog(remainHp: 0, mobCode: 2600068));

        Assert.Equal(1, queue.Status().Uploaded);
    }

    /// <summary>The gate runs before the kill re-check, so an unsupported encounter never costs the 4s wait.</summary>
    [Fact]
    public void The_catalog_gate_precedes_the_kill_recheck()
    {
        _dm.LoadEncounters(EncounterCatalog.Parse(TwoDungeonCatalog));
        bool waited = false;
        var builder = new StatsPayloadBuilder(_dm, () => false);
        StatsApiClient api = AcceptingApi();
        var consent = new StatsConsentManager(_props, _dm, api, () => builder.OwnCharacter());
        consent.Set("accepted", uploadEnabled: true, publicCharacter: false);
        using var queue = new StatsUploadQueue(consent, builder, api, _dm, _props,
            dispatch: job => job(), killRecheckDelay: () => waited = true, clock: () => 1, retryDelay: _ => { });

        queue.OfferIfEligible(BossLog(remainHp: 500_000, mobCode: 2600068)); // 미확정 킬 + 미지원 보스

        Assert.False(waited);
        Assert.StartsWith("unsupported_encounter:", queue.Status().LastReason);
    }
}
