using WaffleMeter.Data;
using WaffleMeter.Services;
using WaffleMeter.Stats;
using Xunit;

namespace WaffleMeter.Stats.Tests;

public sealed class StatsConsentManagerTests : IDisposable
{
    private readonly string _tempAppData;
    private readonly PropertyHandler _props;
    private readonly DataManager _data = new();

    public StatsConsentManagerTests()
    {
        _tempAppData = Path.Combine(Path.GetTempPath(), "wm_consent_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempAppData);
        _props = new PropertyHandler(_tempAppData);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempAppData, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private StatsConsentManager Manager(StatsApiClient api) => new(
        _props,
        _data,
        api,
        ownCharacter: () => new StatsOwnCharacter(true, 1, "Hero", 3, "마도성", 5000),
        clock: () => 1_700_000_000_000);

    private static StatsApiClient ApiReturning(string body) =>
        new(() => "install-1", (_, _, _, _) => new StatsHttpResponse(200, body));

    private static StatsApiClient ApiFailing() =>
        new(() => "install-1", (_, _, _, _) => new StatsHttpResponse(500, "server_down"));

    // Records every request body the manager sends, so tests can assert exactly what went on the wire —
    // in particular whether the "public" key was sent true, sent false, or OMITTED (server-preserve).
    private static (StatsApiClient api, List<string?> bodies) RecordingApi(Func<string?, StatsHttpResponse> respond)
    {
        var bodies = new List<string?>();
        var api = new StatsApiClient(() => "install-1", (_, _, body, _) =>
        {
            bodies.Add(body);
            return respond(body);
        });
        return (api, bodies);
    }

    private void GiveExecutor() => _data.SaveNickname(1, "Hero", isExecutor: true, server: 3, jobByte: 0);

    [Fact]
    public void Declined_persists_locally_without_upload()
    {
        StatsConsentManager manager = Manager(ApiFailing());

        StatsConsentManager.Info info = manager.Set("declined", uploadEnabled: true, publicCharacter: true);

        Assert.Equal("declined", info.State);
        Assert.False(info.UploadEnabled);
        Assert.Equal("local_declined", info.SyncStatus);
        Assert.False(manager.IsUploadAllowed());

        // Persisted: a fresh manager reads the same.
        Assert.Equal("declined", Manager(ApiFailing()).GetInfo().State);
    }

    [Fact]
    public void Unknown_persists_locally()
    {
        StatsConsentManager manager = Manager(ApiFailing());
        StatsConsentManager.Info info = manager.Set("unknown", false, false);
        Assert.Equal("unknown", info.State);
        Assert.Equal("local_unknown", info.SyncStatus);
    }

    [Fact]
    public void Accept_without_executor_character_is_identity_missing()
    {
        // No executor user -> currentConsentCharacter null -> no HTTP, identity_missing.
        StatsConsentManager manager = Manager(ApiFailing());

        StatsConsentManager.Info info = manager.Set("accepted", uploadEnabled: true, publicCharacter: true);

        Assert.Equal("accepted", info.State);
        Assert.False(info.UploadEnabled);
        Assert.Equal("identity_missing", info.SyncStatus);
    }

    [Fact]
    public void Accept_applies_remote_and_enables_upload()
    {
        GiveExecutor();
        StatsApiClient api = ApiReturning(
            """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":true,"consentVersion":"2026-06-04","updatedAt":"2026-06-04T00:00:00Z"}""");
        StatsConsentManager manager = Manager(api);

        StatsConsentManager.Info info = manager.Set("accepted", uploadEnabled: true, publicCharacter: true);

        Assert.Equal("accepted", info.State);
        Assert.True(info.UploadEnabled);
        Assert.True(info.PublicCharacter);
        Assert.Equal("synced", info.SyncStatus);
        Assert.True(info.RemoteExists);
        Assert.True(manager.IsUploadAllowed());
    }

    [Fact]
    public void Accept_remote_failure_records_sync_failed_and_keeps_identity()
    {
        GiveExecutor();
        StatsConsentManager manager = Manager(ApiFailing());

        StatsConsentManager.Info info = manager.Set("accepted", uploadEnabled: true, publicCharacter: false);

        Assert.Equal("accepted", info.State);
        Assert.False(info.UploadEnabled); // not synced -> upload stays off
        Assert.Equal("sync_failed", info.SyncStatus);
        Assert.NotNull(info.IdentityHash);
        Assert.False(manager.IsUploadAllowed());
    }

    [Fact]
    public void Accept_remote_not_exists_does_not_enable_upload()
    {
        GiveExecutor();
        StatsApiClient api = ApiReturning(
            """{"ok":true,"identityHash":"h","exists":false,"consentState":"accepted","public":true}""");
        StatsConsentManager manager = Manager(api);

        StatsConsentManager.Info info = manager.Set("accepted", uploadEnabled: true, publicCharacter: true);

        Assert.False(info.UploadEnabled); // exists=false -> not accepted-effective
        Assert.False(info.PublicCharacter);
    }

    [Fact]
    public void RefreshFromServer_without_identity_is_identity_missing()
    {
        // No executor -> no identity hash.
        StatsConsentManager manager = Manager(ApiFailing());
        StatsConsentManager.Info info = manager.GetInfo(syncRemote: true);
        Assert.Equal("identity_missing", info.SyncStatus);
    }

    [Fact]
    public void Consent_is_remembered_per_character()
    {
        StatsApiClient accept = ApiReturning(
            """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":true,"consentVersion":"2026-06-04","updatedAt":"2026-06-04T00:00:00Z"}""");

        // Character A accepts.
        _data.SaveNickname(1, "Alice", isExecutor: true, server: 3, jobByte: 0);
        Manager(accept).Set("accepted", uploadEnabled: true, publicCharacter: true);
        Assert.True(Manager(accept).IsCurrentCharacterConsented());
        Assert.True(Manager(accept).IsUploadAllowed());

        // Switch to character B -> NOT A's accepted; an undecided character is unknown.
        _data.SaveNickname(2, "Bob", isExecutor: true, server: 3, jobByte: 0);
        Assert.Equal("unknown", Manager(accept).GetInfo().State);
        Assert.False(Manager(accept).IsCurrentCharacterConsented());
        Assert.False(Manager(accept).IsUploadAllowed());

        // B declines (remembered for B only).
        Manager(ApiFailing()).Set("declined", uploadEnabled: false, publicCharacter: false);
        Assert.Equal("declined", Manager(accept).GetInfo().State);

        // Switch back to A -> still accepted (no re-prompt), upload still allowed.
        _data.SaveNickname(1, "Alice", isExecutor: true, server: 3, jobByte: 0);
        Assert.Equal("accepted", Manager(accept).GetInfo().State);
        Assert.True(Manager(accept).IsCurrentCharacterConsented());
        Assert.True(Manager(accept).IsUploadAllowed());

        // The consented list has A but not B.
        IReadOnlyList<string> consented = Manager(accept).ConsentedCharacterHashes();
        Assert.Contains(StatsIdentity.CharacterIdentityHash(3, "Alice")!, consented);
        Assert.DoesNotContain(StatsIdentity.CharacterIdentityHash(3, "Bob")!, consented);
    }

    private static StatsApiClient AcceptApi(bool pub) => ApiReturning(
        $$"""{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":{{(pub ? "true" : "false")}},"consentVersion":"2026-06-04","updatedAt":"2026-06-04T00:00:00Z"}""");

    [Fact]
    public void ListCharacters_includes_name_server_job_metadata()
    {
        GiveExecutor(); // Hero, server 3
        Manager(AcceptApi(true)).Set("accepted", uploadEnabled: true, publicCharacter: true);

        StatsConsentManager.CharacterConsentInfo hero =
            Manager(AcceptApi(true)).ListCharacters().Single(c => c.Nickname == "Hero");

        Assert.Equal(3, hero.Server);
        Assert.Equal("마도성", hero.Job);          // from the own-character provider
        Assert.Equal("accepted", hero.State);
        Assert.True(hero.IsCurrent);
        Assert.True(hero.PublicCharacter);
        Assert.True(hero.CanSetPublic);
        Assert.Equal(StatsIdentity.CharacterIdentityHash(3, "Hero"), hero.IdentityHash);
    }

    [Fact]
    public void SetCharacterPublic_updates_a_non_current_character_only()
    {
        // Alice accepts public=true, then we switch to Bob (Alice is now non-current).
        _data.SaveNickname(1, "Alice", isExecutor: true, server: 3, jobByte: 0);
        Manager(AcceptApi(true)).Set("accepted", uploadEnabled: true, publicCharacter: true);
        string aliceHash = StatsIdentity.CharacterIdentityHash(3, "Alice")!;
        _data.SaveNickname(2, "Bob", isExecutor: true, server: 3, jobByte: 0);

        Manager(AcceptApi(false)).SetCharacterPublic(aliceHash, publicCharacter: false);

        StatsConsentManager.CharacterConsentInfo alice =
            Manager(AcceptApi(false)).ListCharacters().Single(c => c.IdentityHash == aliceHash);
        Assert.False(alice.PublicCharacter); // toggled private
        Assert.Equal("accepted", alice.State);
        Assert.False(alice.IsCurrent);
        // Bob's (current) consent is untouched / undecided.
        Assert.Equal("unknown", Manager(AcceptApi(false)).GetInfo().State);
    }

    [Fact]
    public void RevokeCharacter_revokes_a_non_current_character()
    {
        _data.SaveNickname(1, "Alice", isExecutor: true, server: 3, jobByte: 0);
        Manager(AcceptApi(true)).Set("accepted", uploadEnabled: true, publicCharacter: true);
        string aliceHash = StatsIdentity.CharacterIdentityHash(3, "Alice")!;
        _data.SaveNickname(2, "Bob", isExecutor: true, server: 3, jobByte: 0);

        StatsApiClient revoked = ApiReturning(
            """{"ok":true,"identityHash":"h","exists":false,"consentState":"revoked","consentVersion":"2026-06-04","updatedAt":"2026-06-05T00:00:00Z"}""");
        Manager(revoked).RevokeCharacter(aliceHash);

        Assert.DoesNotContain(aliceHash, Manager(revoked).ConsentedCharacterHashes());
        Assert.Equal("revoked", Manager(revoked).ListCharacters().Single(c => c.IdentityHash == aliceHash).State);
    }

    [Fact]
    public void Korean_nickname_round_trips_through_property_storage()
    {
        // EUC-KR settings encoding corrupts raw Korean; the default JSON serializer \uXXXX-escapes it to
        // ASCII so it survives. A fresh manager (re-reads settings.properties) must still see the name.
        _data.SaveNickname(1, "와플", isExecutor: true, server: 3, jobByte: 0);
        Manager(AcceptApi(false)).Set("accepted", uploadEnabled: true, publicCharacter: false);

        Assert.Contains(Manager(AcceptApi(false)).ListCharacters(), c => c.Nickname == "와플");
    }

    [Fact]
    public void ListCharacters_shows_the_current_character_name_live_when_its_record_has_none()
    {
        GiveExecutor(); // Hero recognized (uid 1, server 3)
        string hash = StatsIdentity.CharacterIdentityHash(3, "Hero")!;
        // A prior-session record with NO stored nickname (e.g. server-synced before the name was known).
        _props.SetProperty("statsConsentCharacters",
            "{\"" + hash + "\":{\"state\":\"accepted\",\"uploadEnabled\":true,\"publicCharacter\":false,\"updatedAt\":1}}");

        StatsConsentManager.CharacterConsentInfo c = Manager(ApiFailing()).ListCharacters().Single();

        Assert.Equal("Hero", c.Nickname); // resolved live from the executor, not "이름 없음 (이전 기록)"
        Assert.Equal(3, c.Server);
        Assert.True(c.IsCurrent);
    }

    [Fact]
    public void BackfillCurrentCharacterIdentity_persists_the_name_so_it_shows_when_not_current()
    {
        GiveExecutor(); // Hero current
        string hash = StatsIdentity.CharacterIdentityHash(3, "Hero")!;
        _props.SetProperty("statsConsentCharacters",
            "{\"" + hash + "\":{\"state\":\"accepted\",\"uploadEnabled\":true,\"publicCharacter\":false,\"updatedAt\":1}}");

        Manager(ApiFailing()).BackfillCurrentCharacterIdentity();

        // Switch away so Hero is no longer current -> its name must come from the PERSISTED record, not live.
        _data.SaveNickname(2, "Bob", isExecutor: true, server: 3, jobByte: 0);
        StatsConsentManager.CharacterConsentInfo hero =
            Manager(ApiFailing()).ListCharacters().Single(c => c.IdentityHash == hash);
        Assert.Equal("Hero", hero.Nickname);
        Assert.False(hero.IsCurrent);
    }

    [Fact]
    public void ListCharacters_hides_name_less_legacy_records()
    {
        // Legacy records with no stored nickname and no character recognized -> all hidden (no confusing rows).
        string h1 = StatsIdentity.CharacterIdentityHash(3, "Alice")!;
        string h2 = StatsIdentity.CharacterIdentityHash(3, "Bob")!;
        _props.SetProperty("statsConsentCharacters",
            "{\"" + h1 + "\":{\"state\":\"accepted\",\"uploadEnabled\":true,\"publicCharacter\":true,\"updatedAt\":1},"
            + "\"" + h2 + "\":{\"state\":\"accepted\",\"uploadEnabled\":true,\"publicCharacter\":false,\"updatedAt\":2}}");

        Assert.Empty(Manager(ApiFailing()).ListCharacters());
    }

    [Fact]
    public void ListCharacters_shows_only_named_characters_when_some_records_are_name_less()
    {
        GiveExecutor(); // Hero recognized -> live-named
        string heroHash = StatsIdentity.CharacterIdentityHash(3, "Hero")!;
        string ghostHash = StatsIdentity.CharacterIdentityHash(3, "Ghost")!;
        _props.SetProperty("statsConsentCharacters",
            "{\"" + heroHash + "\":{\"state\":\"accepted\",\"uploadEnabled\":true,\"publicCharacter\":false,\"updatedAt\":1},"
            + "\"" + ghostHash + "\":{\"state\":\"accepted\",\"uploadEnabled\":true,\"publicCharacter\":false,\"updatedAt\":2}}");

        IReadOnlyList<StatsConsentManager.CharacterConsentInfo> list = Manager(ApiFailing()).ListCharacters();

        StatsConsentManager.CharacterConsentInfo only = Assert.Single(list); // Hero (live-named); nameless Ghost hidden
        Assert.Equal("Hero", only.Nickname);
        Assert.True(only.IsCurrent);
    }

    [Fact]
    public void Accept_caches_grant_from_response()
    {
        GiveExecutor();
        StatsApiClient api = ApiReturning(
            """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":false,"granted":true,"consentVersion":"2026-06-04","updatedAt":"2026-06-04T00:00:00Z"}""");
        Manager(api).Set("accepted", uploadEnabled: true, publicCharacter: false);

        string hash = StatsIdentity.CharacterIdentityHash(3, "Hero")!;
        Assert.True(Manager(ApiFailing()).HasGrant(hash));
        Assert.True(Manager(ApiFailing()).ListCharacters().Single(c => c.IdentityHash == hash).Grant);
    }

    [Fact]
    public void MarkGranted_caches_grant_without_changing_state()
    {
        GiveExecutor();
        // AcceptApi(false) carries no "granted" field -> grant starts false.
        Manager(AcceptApi(false)).Set("accepted", uploadEnabled: true, publicCharacter: false);
        string hash = StatsIdentity.CharacterIdentityHash(3, "Hero")!;
        Assert.False(Manager(ApiFailing()).HasGrant(hash));

        Manager(ApiFailing()).MarkGranted(hash);

        StatsConsentManager.CharacterConsentInfo hero =
            Manager(ApiFailing()).ListCharacters().Single(c => c.IdentityHash == hash);
        Assert.True(hero.Grant);
        Assert.Equal("accepted", hero.State); // grant-only: consent state untouched
        Assert.True(Manager(ApiFailing()).HasGrant(hash));
    }

    [Fact]
    public void SetCharacterPublic_blocks_public_transition_without_grant()
    {
        // Alice accepts private, no grant. Switch to Bob so Alice is non-current.
        _data.SaveNickname(1, "Alice", isExecutor: true, server: 3, jobByte: 0);
        Manager(AcceptApi(false)).Set("accepted", uploadEnabled: true, publicCharacter: false);
        string aliceHash = StatsIdentity.CharacterIdentityHash(3, "Alice")!;
        _data.SaveNickname(2, "Bob", isExecutor: true, server: 3, jobByte: 0);

        // AcceptApi(true) WOULD make her public if the gate let the request through — it must not.
        Manager(AcceptApi(true)).SetCharacterPublic(aliceHash, publicCharacter: true);

        StatsConsentManager.CharacterConsentInfo alice =
            Manager(ApiFailing()).ListCharacters().Single(c => c.IdentityHash == aliceHash);
        Assert.False(alice.PublicCharacter); // gate blocked the public transition (no grant)
        Assert.False(alice.Grant);
    }

    [Fact]
    public void SetCharacterPublic_allows_public_transition_with_grant()
    {
        StatsApiClient grantingAccept = ApiReturning(
            """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":false,"granted":true,"consentVersion":"2026-06-04","updatedAt":"2026-06-04T00:00:00Z"}""");
        _data.SaveNickname(1, "Alice", isExecutor: true, server: 3, jobByte: 0);
        Manager(grantingAccept).Set("accepted", uploadEnabled: true, publicCharacter: false);
        string aliceHash = StatsIdentity.CharacterIdentityHash(3, "Alice")!;
        Assert.True(Manager(ApiFailing()).HasGrant(aliceHash));
        _data.SaveNickname(2, "Bob", isExecutor: true, server: 3, jobByte: 0);

        Manager(AcceptApi(true)).SetCharacterPublic(aliceHash, publicCharacter: true);

        StatsConsentManager.CharacterConsentInfo alice =
            Manager(ApiFailing()).ListCharacters().Single(c => c.IdentityHash == aliceHash);
        Assert.True(alice.PublicCharacter); // grant present -> public transition allowed
    }

    [Fact]
    public void Successful_public_toggle_clears_a_stale_ownership_notice()
    {
        StatsApiClient grantingAccept = ApiReturning(
            """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":false,"granted":true,"consentVersion":"2026-06-04","updatedAt":"2026-06-04T00:00:00Z"}""");
        _data.SaveNickname(1, "Alice", isExecutor: true, server: 3, jobByte: 0);
        Manager(grantingAccept).Set("accepted", uploadEnabled: true, publicCharacter: false);
        string aliceHash = StatsIdentity.CharacterIdentityHash(3, "Alice")!;
        _data.SaveNickname(2, "Bob", isExecutor: true, server: 3, jobByte: 0);

        // A prior blocked public attempt left the ownership notice in the global sync status.
        _props.SetProperty("statsConsentSyncStatus", StatsConsentManager.PublicRequiresOwnership);
        Assert.Equal(StatsConsentManager.PublicRequiresOwnership, Manager(ApiFailing()).GetInfo().SyncStatus);

        // A successful public toggle on the granted character must clear it (no stale notice).
        Manager(AcceptApi(true)).SetCharacterPublic(aliceHash, publicCharacter: true);
        Assert.Equal("synced", Manager(ApiFailing()).GetInfo().SyncStatus);
    }

    [Fact]
    public void Accept_public_requires_ownership_reaffirms_without_downgrading_public()
    {
        GiveExecutor(); // Hero, server 3, current — NO grant cached.
        // Refuse public=true (400). The omitted-public re-affirm returns the character still PUBLIC (another
        // owning install published it): the fix must NOT downgrade it — the second request omits "public".
        (StatsApiClient api, List<string?> bodies) = RecordingApi(body =>
            body != null && body.Contains("\"public\":true")
                ? new StatsHttpResponse(400, """{"ok":false,"error":{"code":"public_requires_ownership","message":"no grant"}}""")
                : new StatsHttpResponse(200, """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":true,"consentVersion":"2026-06-04","updatedAt":"2026-06-04T00:00:00Z"}"""));

        StatsConsentManager.Info info = Manager(api).Set("accepted", uploadEnabled: true, publicCharacter: true);

        // Two requests: the refused public:true attempt, then a re-affirm that OMITS public entirely.
        Assert.Equal(2, bodies.Count);
        Assert.Contains("\"public\":true", bodies[0]);
        Assert.DoesNotContain("\"public\"", bodies[1]); // omitted -> server preserves, never downgraded
        // Server kept it public, so no ownership nag and public stays true (matches the shared server row).
        Assert.Equal("accepted", info.State);
        Assert.True(info.PublicCharacter);
        Assert.Equal("synced", info.SyncStatus);
        Assert.True(info.UploadEnabled);
    }

    [Fact]
    public void Accept_public_requires_ownership_when_still_private_stamps_notice()
    {
        GiveExecutor();
        // Refuse public:true; the omitted-public re-affirm returns a still-PRIVATE character.
        var api = new StatsApiClient(() => "install-1", (_, _, body, _) =>
            body != null && body.Contains("\"public\":true")
                ? new StatsHttpResponse(400, """{"ok":false,"error":{"code":"public_requires_ownership","message":"no grant"}}""")
                : new StatsHttpResponse(200, """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":false,"consentVersion":"2026-06-04","updatedAt":"2026-06-04T00:00:00Z"}"""));

        StatsConsentManager.Info info = Manager(api).Set("accepted", uploadEnabled: true, publicCharacter: true);

        Assert.Equal("accepted", info.State);
        Assert.False(info.PublicCharacter); // never became public (no owning install has published it)
        Assert.Equal(StatsConsentManager.PublicRequiresOwnership, info.SyncStatus);
        Assert.True(info.UploadEnabled); // consent still landed; uploads stay on
    }

    [Fact]
    public void Public_intent_refused_for_ownership_auto_applies_on_the_first_upload_grant()
    {
        GiveExecutor(); // Hero, server 3, current, NO grant yet.
        string hash = StatsIdentity.CharacterIdentityHash(3, "Hero")!;

        // Accept + 공개 before owning the character: the server refuses public (public_requires_ownership), the
        // notice is stamped, and the intent is remembered (so the user need not re-toggle it later).
        var refuse = new StatsApiClient(() => "install-1", (_, _, body, _) =>
            body != null && body.Contains("\"public\":true")
                ? new StatsHttpResponse(400, """{"ok":false,"error":{"code":"public_requires_ownership","message":"no grant"}}""")
                : new StatsHttpResponse(200, """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":false,"consentVersion":"2026-06-04","updatedAt":"2026-06-04T00:00:00Z"}"""));
        Manager(refuse).Set("accepted", uploadEnabled: true, publicCharacter: true);
        Assert.False(Manager(ApiFailing()).ListCharacters().Single(c => c.IdentityHash == hash).PublicCharacter);
        Assert.Equal(StatsConsentManager.PublicRequiresOwnership, Manager(ApiFailing()).GetInfo().SyncStatus);

        // The FIRST upload earns the grant -> MarkGranted auto-applies the remembered public intent, so the user
        // never has to re-toggle "공개" after their first battle upload.
        var grantAndPublish = new StatsApiClient(() => "install-1", (_, _, _, _) =>
            new StatsHttpResponse(200, """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":true,"granted":true,"consentVersion":"2026-06-04","updatedAt":"2026-06-04T00:00:00Z"}"""));
        Manager(grantAndPublish).MarkGranted(hash, "test");

        StatsConsentManager.CharacterConsentInfo hero =
            Manager(ApiFailing()).ListCharacters().Single(c => c.IdentityHash == hash);
        Assert.True(hero.Grant);
        Assert.True(hero.PublicCharacter); // auto-public applied on the first grant — no manual re-toggle
    }

    [Fact]
    public void Accept_private_without_grant_omits_public_to_preserve_server_state()
    {
        GiveExecutor(); // Hero, current, NO grant.
        (StatsApiClient api, List<string?> bodies) = RecordingApi(_ =>
            new StatsHttpResponse(200, """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":false,"consentVersion":"2026-06-04","updatedAt":"2026-06-04T00:00:00Z"}"""));

        Manager(api).Set("accepted", uploadEnabled: true, publicCharacter: false);

        // A non-owning install accepting privately must NOT assert public=false (which would un-publish a
        // character an owning install made public). It omits the key so the server preserves it.
        string? body = Assert.Single(bodies);
        Assert.DoesNotContain("\"public\"", body);
    }

    [Fact]
    public void Accept_private_with_grant_sends_explicit_public_false()
    {
        GiveExecutor(); // Hero, current.
        // Earn a grant first, then a deliberate make-private from the OWNING install must send public:false.
        Manager(ApiReturning(
            """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":true,"granted":true,"consentVersion":"2026-06-04","updatedAt":"2026-06-04T00:00:00Z"}"""))
            .Set("accepted", uploadEnabled: true, publicCharacter: true);
        Assert.True(Manager(ApiFailing()).HasGrant(StatsIdentity.CharacterIdentityHash(3, "Hero")!));

        (StatsApiClient api, List<string?> bodies) = RecordingApi(_ =>
            new StatsHttpResponse(200, """{"ok":true,"identityHash":"h","exists":true,"consentState":"accepted","public":false,"consentVersion":"2026-06-04","updatedAt":"2026-06-04T00:00:00Z"}"""));

        Manager(api).Set("accepted", uploadEnabled: true, publicCharacter: false);

        string? body = Assert.Single(bodies);
        Assert.Contains("\"public\":false", body); // owning install may deliberately make it private
    }

    [Fact]
    public void Revoke_is_never_gated_even_when_sync_fails()
    {
        GiveExecutor();
        Manager(AcceptApi(false)).Set("accepted", uploadEnabled: true, publicCharacter: false);

        // Revoke must persist locally regardless of the server (fail-safe, offline-capable).
        StatsConsentManager.Info info = Manager(ApiFailing()).Set("revoked", false, false);

        Assert.Equal("revoked", info.State);
        Assert.False(info.UploadEnabled);
        Assert.False(Manager(ApiFailing()).IsUploadAllowed());
    }

    // ---- 캐릭터별 업로드가 조용히 막히는 상태의 자가 복구 (2026-08-22) ----
    //
    // 프로덕션 실측: 동의는 accepted 인데 업로더였던 적이 한 번도 없는 캐릭터가 1,290개였다. IsUploadAllowed 는
    // 캐릭터별이라, 재설치로 per-character 기록이 비면 그 캐릭터는 첫 게이트에서 영구히 걸리고 화면엔 아무 말도 없다.

    private static string StatusJson(string state, bool exists = true, bool publicCharacter = false, bool granted = false) =>
        $"{{\"ok\":true,\"identityHash\":\"h\",\"exists\":{(exists ? "true" : "false")},\"consentState\":\"{state}\"," +
        $"\"public\":{(publicCharacter ? "true" : "false")},\"consentVersion\":\"2026-06-04\"," +
        $"\"granted\":{(granted ? "true" : "false")}}}";

    [Fact]
    public void Server_accepted_repairs_a_character_this_install_never_decided()
    {
        GiveExecutor();
        StatsConsentManager manager = Manager(ApiReturning(StatusJson("accepted")));

        // 이 설치본에는 이 캐릭터의 기록이 없다 (재설치 직후 상태) -> 업로드가 막혀 있다.
        Assert.False(manager.IsUploadAllowed());

        manager.SyncCurrentCharacter("test");

        Assert.True(manager.IsUploadAllowed());
        Assert.Equal("accepted", manager.GetInfo().State);
    }

    [Fact]
    public void Server_accepted_repairs_a_dismissed_prompt_but_not_a_real_decline()
    {
        GiveExecutor();

        // 모달을 닫기만 한 경우: 결정이 아니므로 복구 대상이다.
        StatsConsentManager dismissed = Manager(ApiReturning(StatusJson("accepted")));
        dismissed.Set("declined", uploadEnabled: false, publicCharacter: false, "test", explicitChoice: false);
        Assert.False(dismissed.IsUploadAllowed());
        dismissed.SyncCurrentCharacter("test");
        Assert.True(dismissed.IsUploadAllowed());

        // 사용자가 실제로 '거부'를 누른 경우: 서버가 accepted 여도 건드리지 않는다.
        StatsConsentManager refused = Manager(ApiReturning(StatusJson("accepted")));
        refused.Set("declined", uploadEnabled: false, publicCharacter: false, "test");
        refused.SyncCurrentCharacter("test");
        Assert.False(refused.IsUploadAllowed());
        Assert.Equal("declined", refused.GetInfo().State);
    }

    [Fact]
    public void Remote_revoke_is_always_adopted_even_over_an_explicit_local_accept()
    {
        GiveExecutor();
        StatsConsentManager manager = Manager(ApiReturning(StatusJson("accepted")));
        manager.Set("accepted", uploadEnabled: true, publicCharacter: false, "test");
        Assert.True(manager.IsUploadAllowed());

        // 철회는 항상 따라간다 — 안전 방향은 '데이터가 덜 가는 쪽'이다.
        StatsConsentManager revoked = Manager(ApiReturning(StatusJson("revoked")));
        revoked.SyncCurrentCharacter("test");
        Assert.False(revoked.IsUploadAllowed());
    }

    [Fact]
    public void Block_reason_names_the_missing_grant_once_uploads_are_allowed()
    {
        GiveExecutor();
        StatsConsentManager manager = Manager(ApiReturning(StatusJson("accepted")));
        manager.SyncCurrentCharacter("test");

        Assert.True(manager.IsUploadAllowed());
        // 업로드는 열렸지만 이 설치본은 아직 이 캐릭터로 올린 적이 없다 -> 그 사실을 말해 준다.
        Assert.Contains("아직 업로드된 전투가 없어요", manager.CurrentUploadBlockReason());
    }

    // ---- 서버가 그 캐릭터를 모를 때 (exists:false) ----

    [Fact]
    public void Unknown_remote_reprompts_a_dismissed_decline_exactly_once()
    {
        GiveExecutor();
        StatsConsentManager manager = Manager(ApiReturning(StatusJson("unknown", exists: false)));

        // 모달을 X 로 닫아 declined 로 박혔던 예전 빌드의 기록.
        manager.Set("declined", uploadEnabled: false, publicCharacter: false, "test", explicitChoice: false);
        Assert.Equal("declined", manager.GetInfo().State);

        manager.SyncCurrentCharacter("test");
        Assert.Equal("unknown", manager.GetInfo().State); // 다시 물어볼 수 있는 상태로 돌아온다
        Assert.True(manager.NeedsConsentPrompt());

        // 두 번은 없다: 여기서 다시 거부하면 그걸로 끝이어야 한다.
        manager.Set("declined", uploadEnabled: false, publicCharacter: false, "test", explicitChoice: false);
        manager.SyncCurrentCharacter("test");
        Assert.Equal("declined", manager.GetInfo().State);
    }

    [Fact]
    public void Unknown_remote_never_touches_an_explicit_decline()
    {
        GiveExecutor();
        StatsConsentManager manager = Manager(ApiReturning(StatusJson("unknown", exists: false)));
        manager.Set("declined", uploadEnabled: false, publicCharacter: false, "test");

        manager.SyncCurrentCharacter("test");

        Assert.Equal("declined", manager.GetInfo().State);
        Assert.False(manager.NeedsConsentPrompt());
    }

    [Fact]
    public void Unknown_remote_does_not_reset_a_local_accept()
    {
        GiveExecutor();
        // 동의는 성공했지만(=로컬 accepted) 조회는 서버가 모른다고 답하는 경우.
        StatsConsentManager accepted = Manager(ApiReturning(StatusJson("accepted")));
        accepted.Set("accepted", uploadEnabled: true, publicCharacter: false, "test");
        Assert.True(accepted.IsUploadAllowed());

        StatsConsentManager blind = Manager(ApiReturning(StatusJson("unknown", exists: false)));
        blind.SyncCurrentCharacter("test");

        // exists:false 는 소식이 아니다 — 이걸 받아쓰면 매 세션 로컬 결정이 지워진다.
        Assert.Equal("accepted", blind.GetInfo().State);
        Assert.True(blind.IsUploadAllowed());
    }
    // ---- M-20: 전송 실패한 철회는 자동 동기화가 되돌리면 안 된다 (2026-09-18) ----
    //
    // 철회는 서버에 못 닿아도 로컬에는 남는다(프라이버시 fail-safe). 그러면 서버에는 accepted 가 남고, 다음
    // 접속의 자동 동기화가 "서버는 accepted 인데 로컬이 아니다"를 복구 대상으로 읽어 업로드를 재개시킨다 —
    // 화면에는 아무 표시도 없이. 전제는 로컬 레코드의 Explicit 이 false 라는 것: 아래 테스트들이 그 전제를
    // 그대로 만들어 둔다(사용자가 이 PC 에서 버튼을 눌러 본 적 있는 레코드는 예전 코드에서도 안전했다).

    /// <summary>매니저 API 로는 만들 수 없는 모양의 레코드(예: <c>explicit</c> 키 자체가 없는 구버전/서버복구
    /// 레코드)를 그대로 심는다. 키 이름은 <c>StatsConsentManager.KeyCharacters</c> 와 같아야 한다 — private 이라
    /// 여기서는 리터럴이고, 저쪽을 바꾸면 이 테스트들이 조용히 무의미해진다.</summary>
    private void SeedCharacterRecord(string identityHash, string recordJson) =>
        _props.SetProperty("statsConsentCharacters",
            """{"HASH":RECORD}""".Replace("HASH", identityHash).Replace("RECORD", recordJson));

    private const string ServerRepairedAccept =
        """{"state":"accepted","uploadEnabled":true,"publicCharacter":false,"consentVersion":"2026-06-04","updatedAt":1,"nickname":"Hero","server":3}""";

    [Fact]
    public void A_revoke_that_failed_to_send_is_not_undone_by_the_next_auto_sync()
    {
        GiveExecutor();
        string hash = StatsIdentity.CharacterIdentityHash(3, "Hero")!;
        SeedCharacterRecord(hash, ServerRepairedAccept); // 이 PC 에서 누른 적 없는 동의(explicit 키 없음)
        Assert.True(Manager(ApiFailing()).IsUploadAllowed());

        // 철회 버튼 -> 전송 실패 -> 로컬만 revoked.
        Assert.Equal("revoked", Manager(ApiFailing()).Set("revoked", false, false, "test").State);

        // 다음 접속의 자동 동기화: 서버에는 철회가 닿지 않았으므로 accepted 가 그대로 남아 있다.
        StatsConsentManager synced = Manager(ApiReturning(StatusJson("accepted")));
        synced.SyncCurrentCharacter("test");

        Assert.Equal("revoked", synced.GetInfo().State);
        Assert.False(synced.IsUploadAllowed()); // 되살아나면 사용자가 끈 업로드가 조용히 재개된다
    }

    [Fact]
    public void A_legacy_revoked_record_without_the_explicit_key_is_still_not_re_upgraded()
    {
        // 철회 시점에 이미 저장돼 있던 레코드는 나중에 고친 '철회에 explicit 을 찍는다'의 혜택을 못 받는다.
        // 그러니 revoked 라는 상태 자체가 가드여야 한다.
        GiveExecutor();
        string hash = StatsIdentity.CharacterIdentityHash(3, "Hero")!;
        SeedCharacterRecord(hash,
            """{"state":"revoked","uploadEnabled":false,"publicCharacter":false,"consentVersion":"2026-06-04","updatedAt":1,"nickname":"Hero","server":3}""");

        StatsConsentManager manager = Manager(ApiReturning(StatusJson("accepted")));
        manager.SyncCurrentCharacter("test");

        Assert.Equal("revoked", manager.GetInfo().State);
        Assert.False(manager.IsUploadAllowed());
    }

    [Fact]
    public void RevokeCharacter_survives_a_failed_send_when_that_character_comes_back()
    {
        // 목록에서 비-현재 캐릭터를 철회하는 경로(전송 실패). 이쪽은 실패해도 화면에 아무 신호가 없다.
        string aliceHash = StatsIdentity.CharacterIdentityHash(3, "Alice")!;
        SeedCharacterRecord(aliceHash,
            """{"state":"accepted","uploadEnabled":true,"publicCharacter":false,"consentVersion":"2026-06-04","updatedAt":1,"nickname":"Alice","server":3}""");
        _data.SaveNickname(2, "Bob", isExecutor: true, server: 3, jobByte: 0);

        Manager(ApiFailing()).RevokeCharacter(aliceHash);

        // Alice 로 접속하면 자동 동기화가 돈다 — 서버에는 여전히 accepted 가 남아 있다.
        _data.SaveNickname(1, "Alice", isExecutor: true, server: 3, jobByte: 0);
        StatsConsentManager back = Manager(ApiReturning(StatusJson("accepted")));
        back.SyncCurrentCharacter("test");

        Assert.Equal("revoked", back.GetInfo().State);
        Assert.False(back.IsUploadAllowed());
    }

    // ---- M-21: 자동 공개 적용이 실패해도 업로드를 빼앗지 않는다 (2026-09-18) ----
    //
    // MarkGranted 의 공개 자동화는 사용자가 누른 적 없는 동작이다. 그 전송이 실패했을 때 예전 코드는
    // uploadEnabled=false + publicCharacter=<요청값 true> + explicit=true 로 굳혀서, 목록엔 '공개 켜짐'으로
    // 그리면서 이후 전투를 전부 consent_not_allowed 로 막고 자가복구까지 껐다. 재시도는 없다(grant 당 1회).

    [Fact]
    public void A_failed_auto_public_apply_keeps_uploads_on_and_reports_itself()
    {
        GiveExecutor(); // Hero, grant 없음
        string hash = StatsIdentity.CharacterIdentityHash(3, "Hero")!;

        // 공개를 요청했지만 grant 가 없어 서버가 거절 -> 의사만 기억되고 업로드는 계속 허용된다.
        var refuse = new StatsApiClient(() => "install-1", (_, _, body, _) =>
            body != null && body.Contains("\"public\":true")
                ? new StatsHttpResponse(400, """{"ok":false,"error":{"code":"public_requires_ownership","message":"no grant"}}""")
                : new StatsHttpResponse(200, StatusJson("accepted")));
        Manager(refuse).Set("accepted", uploadEnabled: true, publicCharacter: true, "test");
        Assert.True(Manager(ApiFailing()).IsUploadAllowed());

        // 첫 업로드가 grant 를 얻어 자동 공개 적용이 도는데, 그 요청이 죽는다(502/타임아웃 계열).
        Manager(ApiFailing()).MarkGranted(hash, "test");

        StatsConsentManager after = Manager(ApiFailing());
        Assert.True(after.IsUploadAllowed()); // ① 누른 적 없는 동작의 실패가 동의를 취소할 수는 없다

        StatsConsentManager.CharacterConsentInfo hero = after.ListCharacters().Single(c => c.IdentityHash == hash);
        Assert.False(hero.PublicCharacter);   // ② 전송이 실패했으니 서버는 여전히 private — 요청값을 굳히면 안 된다
        Assert.True(hero.PublicApplyFailed);  // ③ 실패 사실은 UI 가 읽을 수 있는 형태로 남는다
        Assert.True(after.HasPublicApplyFailure(hash));
    }

    [Fact]
    public void A_failed_auto_public_apply_leaves_the_self_heal_intact()
    {
        // 서버 복구 경로가 만든 레코드(explicit 키 없음)에 공개 보류 의사만 얹혀 있고 업로드는 아직 꺼져 있다.
        // 자동 공개 적용의 실패가 이 레코드를 '사용자의 명시적 선택'으로 도장 찍으면 RefreshFromServer 의
        // 자가복구가 꺼져서, 그 캐릭터는 영구히 업로드를 못 한다.
        GiveExecutor();
        string hash = StatsIdentity.CharacterIdentityHash(3, "Hero")!;
        SeedCharacterRecord(hash,
            """{"state":"accepted","uploadEnabled":false,"publicCharacter":false,"consentVersion":"2026-06-04","updatedAt":1,"nickname":"Hero","server":3,"pendingPublic":true}""");

        Manager(ApiFailing()).MarkGranted(hash, "test");
        Assert.True(Manager(ApiFailing()).HasPublicApplyFailure(hash));

        StatsConsentManager healed = Manager(ApiReturning(StatusJson("accepted")));
        healed.SyncCurrentCharacter("test");

        Assert.True(healed.IsUploadAllowed());
    }

    [Fact]
    public void A_manual_public_toggle_clears_the_auto_apply_failure_notice()
    {
        // 수동 재시도가 유일한 재시도 경로다(MarkGranted 는 grant 당 한 번뿐). 성공하면 실패 표시는 사라진다.
        GiveExecutor();
        string hash = StatsIdentity.CharacterIdentityHash(3, "Hero")!;
        SeedCharacterRecord(hash,
            """{"state":"accepted","uploadEnabled":true,"publicCharacter":false,"consentVersion":"2026-06-04","updatedAt":1,"nickname":"Hero","server":3,"pendingPublic":true}""");
        Manager(ApiFailing()).MarkGranted(hash, "test");
        Assert.True(Manager(ApiFailing()).HasPublicApplyFailure(hash));

        Manager(AcceptApi(true)).SetCharacterPublic(hash, publicCharacter: true, "test");

        StatsConsentManager.CharacterConsentInfo hero =
            Manager(ApiFailing()).ListCharacters().Single(c => c.IdentityHash == hash);
        Assert.True(hero.PublicCharacter);
        Assert.False(hero.PublicApplyFailed);
    }
}
