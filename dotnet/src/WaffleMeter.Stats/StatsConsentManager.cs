using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WaffleMeter.Data;
using WaffleMeter.Services;

namespace WaffleMeter.Stats;

/// <summary>
/// Verbatim port of Kotlin <c>stats.StatsConsentManager</c>: the consent state machine. Persists
/// state to settings (via <see cref="PropertyHandler"/>) and reconciles with the backend
/// (<see cref="StatsApiClient"/>). Dependencies are injected (props, data, api, own-character
/// provider, clock) so it is testable; the own-character provider is supplied by the payload builder
/// in the live app.
/// </summary>
public sealed class StatsConsentManager
{
    public const string ConsentVersion = "2026-06-04";

    private const string KeyState = "statsConsentState";
    private const string KeyUploadEnabled = "statsUploadEnabled";
    private const string KeyPublicCharacter = "statsPublicCharacter";
    private const string KeyConsentVersion = "statsConsentVersion";
    private const string KeyUpdatedAt = "statsConsentUpdatedAt";
    private const string KeyIdentityHash = "statsConsentIdentityHash";
    private const string KeyRemoteExists = "statsConsentRemoteExists";
    private const string KeySyncStatus = "statsConsentSyncStatus";
    private const string KeySyncError = "statsConsentSyncError";
    private const string KeyServerUpdatedAt = "statsConsentServerUpdatedAt";
    private const string KeyLastSeenAt = "statsConsentLastSeenAt";
    // Per-character consent memory (req 2): identityHash -> remembered decision, so switching
    // characters never re-prompts an already-decided one. Session-level sync metadata (syncStatus
    // etc.) stays in the global keys above. The global state/identity keys are kept as a migration /
    // no-character fallback.
    private const string KeyCharacters = "statsConsentCharacters";

    // Interned sentinel: SaveLocal's identityHash defaults to "the current character's hash"; an
    // explicit value (including null, from a remote response) overrides it.
    private const string KeepCurrentIdentity = "￿__use_current_identity__";

    public enum State
    {
        unknown,
        accepted,
        declined,
        revoked,
    }

    public sealed record Info(
        string State,
        bool UploadEnabled,
        bool PublicCharacter,
        string ConsentVersion,
        long UpdatedAt,
        string? IdentityHash = null,
        bool RemoteExists = false,
        string SyncStatus = "local",
        string? SyncError = null,
        string? ServerUpdatedAt = null,
        string? LastSeenAt = null);

    private readonly PropertyHandler _props;
    private readonly DataManager _data;
    private readonly StatsApiClient _api;
    private readonly Func<StatsOwnCharacter> _ownCharacter;
    private readonly Func<long> _clock;

    public StatsConsentManager(
        PropertyHandler props,
        DataManager data,
        StatsApiClient api,
        Func<StatsOwnCharacter> ownCharacter,
        Func<long>? clock = null)
    {
        _props = props;
        _data = data;
        _api = api;
        _ownCharacter = ownCharacter;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public Info GetInfo(bool syncRemote = false, string clientVersion = "dev") =>
        syncRemote ? RefreshFromServer(clientVersion) : LocalInfo();

    /// <param name="explicitChoice">The user pressed a button for this character on this install. Default true —
    /// every caller of <see cref="Set"/> is a UI action. The consent dialog passes <c>false</c> when it was
    /// dismissed rather than answered; see <see cref="CharConsent.Explicit"/> for why that matters.</param>
    public Info Set(string state, bool uploadEnabled, bool publicCharacter, string clientVersion = "dev",
        bool explicitChoice = true)
    {
        State next = TryState(state) ?? State.unknown;
        switch (next)
        {
            case State.accepted:
                return Accept(uploadEnabled, publicCharacter, clientVersion, explicitChoice);
            case State.revoked:
                return Revoke(clientVersion);
            case State.declined:
                SaveLocal(State.declined, uploadEnabled: false, publicCharacter: false, syncStatus: "local_declined",
                    explicitChoice: explicitChoice);
                return LocalInfo();
            default:
                SaveLocal(State.unknown, uploadEnabled: false, publicCharacter: false, syncStatus: "local_unknown",
                    explicitChoice: explicitChoice);
                return LocalInfo();
        }
    }

    public bool IsUploadAllowed()
    {
        Info current = LocalInfo();
        return current.State == State.accepted.ToString() && current.UploadEnabled;
    }

    private Info RefreshFromServer(string clientVersion)
    {
        string? identityHash = CurrentIdentityHash();
        if (identityHash == null)
        {
            RememberSync("identity_missing", null);
            return LocalInfo();
        }

        try
        {
            ConsentStatusResponse response = _api.GetConsentStatus(identityHash);
            LoadCharacters().TryGetValue(identityHash, out CharConsent? local);
            State remoteState = TryState(response.ConsentState) ?? State.unknown;
            bool remoteAccepted = remoteState == State.accepted && response.Exists;

            // ⚠ The server knowing NOTHING about this character is not news, and must not be written down as if
            // it were. This sync now runs unprompted on every character, so letting an "exists:false / unknown"
            // answer through to ApplyRemote would reset that character's local record to unknown EVERY session —
            // re-prompting someone who genuinely declined, over and over. Keep what we have.
            if (!response.Exists || remoteState == State.unknown)
            {
                // The one exception, and it is a one-shot: a local `declined` that nobody explicitly chose.
                // Builds before 2026-08-23 wrote that for a dialog closed with the X button, and a declined
                // character is never asked again — so the only people this reopens the door for are the ones who
                // never closed it. Marked on the record so it happens exactly once per character, ever.
                if (local is { Explicit: false, Reprompted: false } && TryState(local.State) == State.declined)
                {
                    ClearForOneReprompt(identityHash);
                    RememberSync("reprompt_once", null);
                    return LocalInfo();
                }

                RememberSync("synced", null);
                return LocalInfo();
            }

            // 철회는 '고쳐 줄' 대상이 아니다 — Explicit 플래그와 무관하게. 철회 이벤트 전송이 실패하면 서버에는
            // accepted 가 남고 로컬만 revoked 가 된다(Revoke 의 catch = 프라이버시 fail-safe). 그 상태에서 아래
            // 복구 경로가 "서버는 accepted 인데 로컬이 아니다"만 보고 되살리면 사용자가 끈 업로드가 다음 접속에
            // 조용히 재개된다. Explicit 게이트만으로는 못 막는다 — 2026-08-23 이전에 쓰인 레코드에는 explicit 키
            // 자체가 없어 false 로 역직렬화되고, 서버 복구 경로가 만든 레코드도 마찬가지다. 로컬 revoked 를 다시
            // 켜는 길은 설정에서의 명시적 재동의 하나뿐이다.
            // ⚠ 이 가드를 지우면 '철회했는데 다음 접속에 업로드가 되살아난다'가 그대로 재발한다.
            if (remoteAccepted && TryState(local?.State) == State.revoked)
            {
                RememberSync("synced_local_revoked", null);
                return LocalInfo();
            }

            // 🔑 The repair. A character the server already knows as accepted, whose local record on THIS
            // install says otherwise without the user ever having said so, is a stuck install — and the cost of
            // leaving it stuck is invisible and total: IsUploadAllowed() is per-character (see LocalInfo), so
            // every battle on that character is dropped at the first gate, no 소유 증명 is ever earned, and
            // 공개 전환 and 스킨 선택 stay locked with nothing on screen to explain it. Measured on production
            // 2026-08-22: 1,290 characters were consented server-side and had never once been an uploader.
            //
            // An EXPLICIT local opt-out is a different thing and is left alone — the user turned this character
            // off on this PC and that decision outranks a stale server row.
            //
            // ⚠ Only an UPGRADE is gated this way. A remote revoke/decline is always adopted: the fail-safe
            // direction is less data, never more.
            if (remoteAccepted && local is { Explicit: true } && (TryState(local.State) != State.accepted || !local.UploadEnabled))
            {
                RememberSync("synced_local_choice", null);
                return LocalInfo();
            }

            // Uploads default ON for anything that is not an explicit local preference — that is what makes the
            // stuck character heal on the next character switch rather than needing a settings visit nobody knows to make.
            bool requestedUpload = local is { Explicit: true, State: nameof(State.accepted) } ? local.UploadEnabled : true;
            return ApplyRemote(response, requestedUpload);
        }
        catch (Exception e)
        {
            RememberSync("sync_failed", Summarize(e));
            return LocalInfo();
        }
    }

    /// <summary>
    /// Re-read the CURRENT character's consent from the server. Call it when a different character connects.
    /// <para>⚠ Blocking HTTP — never on the UI thread. Every failure is swallowed into the sync status, because
    /// this runs unprompted and a decoration-grade sync must not surface as an error mid-combat.</para>
    /// <para>Before this existed the only caller of the remote sync was the settings window's 서버 동기화 button,
    /// so a character whose local record was missing or wrong stayed that way for as long as the install lived.</para>
    /// </summary>
    public void SyncCurrentCharacter(string clientVersion)
    {
        try
        {
            RefreshFromServer(clientVersion);
        }
        catch
        {
            // RefreshFromServer already swallows API failures; this is belt-and-braces for the caller's thread.
        }
    }

    /// <summary>Why the current character's battles are (not) being uploaded — for the 통계 탭 to say it out loud.
    /// <para><c>null</c> = nothing to report (uploading normally, or no character detected yet).</para></summary>
    public string? CurrentUploadBlockReason()
    {
        if (!_ownCharacter().Detected)
        {
            return null;
        }

        Info info = LocalInfo();
        if (info.State != State.accepted.ToString())
        {
            return info.State == State.declined.ToString() || info.State == State.revoked.ToString()
                ? "이 캐릭터는 통계 업로드를 사용하지 않도록 되어 있어요. 아래에서 다시 켤 수 있습니다."
                : "이 캐릭터는 아직 통계 동의를 하지 않았어요.";
        }

        if (!info.UploadEnabled)
        {
            return "이 캐릭터만 업로드가 꺼져 있어요. (업로드 설정은 캐릭터별입니다)";
        }

        string? hash = CurrentIdentityHash();
        if (hash != null && !HasGrant(hash))
        {
            return "이 캐릭터로는 이 PC 에서 아직 업로드된 전투가 없어요. 던전 전투가 한 번 올라가면 공개 전환과 스킨 선택이 열립니다.";
        }

        return null;
    }

    /// <param name="automatic">The APP issued this accept, not a button press — today only the 공개 자동화
    /// re-accept that <see cref="MarkGranted"/> fires when the first upload earns the grant. It changes what a
    /// failed send is allowed to cost the user; see the catch blocks and <see cref="SaveAutomaticSendFailure"/>.</param>
    private Info Accept(bool uploadEnabled, bool publicCharacter, string clientVersion, bool? explicitChoice = true,
        bool automatic = false)
    {
        // An automatic re-accept is not a decision, so it must leave the Explicit flag exactly as the user left
        // it (null = untouched, the same thing a server echo passes). Stamping true would tell
        // RefreshFromServer's repair path "the user chose this on this PC" and switch the self-heal OFF for a
        // record the app wrote; stamping false would erase a real decision the user did make.
        explicitChoice = automatic ? null : explicitChoice;

        // A character's shared public flag may only be CHANGED (turned ON or OFF) by an install that OWNS
        // the character (holds its grant). A non-owning install (e.g. a PC방/재설치 install with no grant)
        // must NOT touch the flag: it sends the accept with `public` OMITTED so the server PRESERVES whatever
        // it has, and therefore can never downgrade a character another install legitimately made public.
        // (Privacy fail-safe is unaffected: 철회/revoke is always allowed and needs no grant.)
        string? currentHash = CurrentIdentityHash();
        bool owns = currentHash != null && HasGrant(currentHash);
        bool? publicIntent = publicCharacter ? true : (owns ? false : (bool?)null);

        ConsentEventCharacter? character = CurrentConsentCharacter(publicIntent);
        if (character == null)
        {
            SaveLocal(State.accepted, uploadEnabled: false, publicCharacter: publicCharacter, syncStatus: "identity_missing",
                explicitChoice: explicitChoice);
            return LocalInfo();
        }

        try
        {
            ConsentStatusResponse response = _api.PostConsentEvent(
                new ConsentEventRequest(State.accepted.ToString(), ConsentVersion, Character: character),
                clientVersion);
            return ApplyRemote(response, requestedUpload: uploadEnabled, explicitChoice: explicitChoice);
        }
        catch (Exception e) when (publicCharacter && IsPublicOwnershipRejection(e))
        {
            // Public refused (no grant). Re-affirm consent WITHOUT asserting public (omit → server preserves),
            // never downgrading a shared row, then surface the ownership notice. Replaces the old destructive
            // "re-accept as private" rollback that overwrote public=false and un-published the character.
            return AcceptPublicUnmanaged(character, uploadEnabled, clientVersion, explicitChoice, automatic);
        }
        catch (Exception e)
        {
            if (automatic)
            {
                return SaveAutomaticSendFailure(character.IdentityHash, e);
            }

            SaveLocal(
                State.accepted,
                uploadEnabled: false,
                publicCharacter: publicCharacter,
                syncStatus: "sync_failed",
                identityHash: character.IdentityHash,
                syncError: Summarize(e),
                explicitChoice: explicitChoice);
            return LocalInfo();
        }
    }

    /// <summary>Fallback when accept+public was refused for lack of grant: re-affirm consent with the public
    /// flag OMITTED (public=null) so the server PRESERVES the character's existing public state — never
    /// downgrading a row an owning install already made public. Reflect the server's real state locally, and
    /// only stamp the public_requires_ownership notice when the character is genuinely still private (there is
    /// nothing to warn about if another owning install has already made it public).</summary>
    private Info AcceptPublicUnmanaged(ConsentEventCharacter character, bool uploadEnabled, string clientVersion,
        bool? explicitChoice = true, bool automatic = false)
    {
        ConsentEventCharacter consentOnly = character with { PublicCharacter = null };
        bool serverPublic;
        try
        {
            ConsentStatusResponse response = _api.PostConsentEvent(
                new ConsentEventRequest(State.accepted.ToString(), ConsentVersion, Character: consentOnly), clientVersion);
            ApplyRemote(response, requestedUpload: uploadEnabled, explicitChoice: explicitChoice);
            serverPublic = (TryState(response.ConsentState) ?? State.unknown) == State.accepted
                && response.Exists && response.PublicCharacter;
        }
        catch (Exception e)
        {
            if (automatic)
            {
                return SaveAutomaticSendFailure(consentOnly.IdentityHash, e);
            }

            SaveLocal(State.accepted, uploadEnabled: false, publicCharacter: false,
                syncStatus: "sync_failed", identityHash: consentOnly.IdentityHash, syncError: Summarize(e),
                explicitChoice: explicitChoice);
            return LocalInfo();
        }

        if (!serverPublic)
        {
            RememberSync(PublicRequiresOwnership, null);
            SetPendingPublic(character.IdentityHash, true); // 공개 자동화: apply on the first upload that grants ownership
        }
        return LocalInfo();
    }

    /// <summary>A consent send the USER never asked for (공개 자동화) failed. Freeze as little as possible.
    /// <para>What this replaced cost them everything at once: the old catch wrote <c>uploadEnabled=false</c> +
    /// <c>publicCharacter=(요청값 true)</c> + <c>explicit=true</c>, so ① the 캐릭터 목록 drew 공개 ON while the
    /// server stayed private, ② <see cref="IsUploadAllowed"/> went false and every later battle died at the
    /// first gate with <c>consent_not_allowed</c>, and ③ the Explicit stamp told <see cref="RefreshFromServer"/>
    /// this was the user's own opt-out, switching off the self-heal that would have undone ②.
    /// <see cref="MarkGranted"/> returns early once granted, so nothing ever retried it — permanent, silent,
    /// and set off by an action nobody performed.</para>
    /// <para>So: keep the upload permission the user already granted, keep the last KNOWN public state (a send
    /// that failed applied nothing — the character is still private), leave Explicit alone so the self-heal
    /// survives, and record the failed auto-apply so the list can say so and the user can re-apply 공개 by hand.</para>
    /// <para>⚠ 되돌려서 여기서 uploadEnabled: false 로 굳히면 그 캐릭터의 업로드가 영구히 막히는 결함이 그대로
    /// 재발한다 — 사용자는 아무 버튼도 누른 적이 없는데.</para>
    /// </summary>
    private Info SaveAutomaticSendFailure(string identityHash, Exception e)
    {
        Info before = LocalInfo();
        SaveLocal(
            State.accepted,
            uploadEnabled: before.UploadEnabled,
            publicCharacter: before.PublicCharacter,
            syncStatus: "sync_failed",
            identityHash: identityHash,
            syncError: Summarize(e),
            explicitChoice: null);
        MarkPublicApplyFailed(identityHash, true);
        return LocalInfo();
    }

    private Info Revoke(string clientVersion)
    {
        string? identityHash = CurrentIdentityHash() ?? NonBlank(_props.GetProperty(KeyIdentityHash));
        if (identityHash == null)
        {
            SaveLocal(State.revoked, uploadEnabled: false, publicCharacter: false, syncStatus: "identity_missing",
                explicitChoice: true);
            return LocalInfo();
        }

        try
        {
            ConsentStatusResponse response = _api.PostConsentEvent(
                new ConsentEventRequest(State.revoked.ToString(), ConsentVersion, IdentityHash: identityHash),
                clientVersion);
            return ApplyRemote(response, requestedUpload: false, explicitChoice: true);
        }
        catch (Exception e)
        {
            // ⚠ explicitChoice: true 는 장식이 아니다. 철회는 사용자가 버튼을 눌러야만 오는 경로이므로, 전송이
            // 실패해도 '사용자가 눌렀다'는 사실은 로컬에 남아야 한다. 이 인자가 빠져 있던 동안 레코드는
            // Explicit=false 인 채 revoked 가 됐고, 서버에는 accepted 가 남아 다음 접속의 자동 동기화가 그대로
            // 되살려 업로드를 재개시켰다 — 되돌아간 사실을 알리는 표시도 없이.
            SaveLocal(
                State.revoked,
                uploadEnabled: false,
                publicCharacter: false,
                syncStatus: "sync_failed",
                identityHash: identityHash,
                syncError: Summarize(e),
                explicitChoice: true);
            return LocalInfo();
        }
    }

    private Info ApplyRemote(ConsentStatusResponse response, bool requestedUpload, bool? explicitChoice = null)
    {
        State remoteState = TryState(response.ConsentState) ?? State.unknown;
        bool accepted = remoteState == State.accepted && response.Exists;
        bool publicCharacter = accepted && response.PublicCharacter;
        SaveLocal(
            remoteState,
            uploadEnabled: accepted && requestedUpload,
            publicCharacter: publicCharacter,
            syncStatus: "synced",
            consentVersion: response.ConsentVersion ?? ConsentVersion,
            updatedAt: ParseRemoteTime(response.UpdatedAt) ?? _clock(),
            identityHash: response.IdentityHash,
            remoteExists: response.Exists,
            syncError: null,
            serverUpdatedAt: response.UpdatedAt,
            lastSeenAt: response.LastSeenAt,
            grant: response.Granted,
            explicitChoice: explicitChoice);
        return LocalInfo();
    }

    private ConsentEventCharacter? CurrentConsentCharacter(bool? publicCharacter)
    {
        User? user = _data.User(_data.ExecutorId());
        string? nickname = NonBlank(user?.Nickname);
        if (user == null || nickname == null)
        {
            return null;
        }

        string? identityHash = StatsIdentity.CharacterIdentityHash(user.Server, nickname);
        if (identityHash == null)
        {
            return null;
        }

        StatsOwnCharacter resolved = _ownCharacter();
        return new ConsentEventCharacter(
            identityHash,
            nickname,
            user.Server,
            publicCharacter,
            Job: resolved.Job ?? user.Job?.ClassName(),
            Power: resolved.Power);
    }

    private string? CurrentIdentityHash()
    {
        User? user = _data.User(_data.ExecutorId());
        return user == null ? null : StatsIdentity.CharacterIdentityHash(user.Server, user.Nickname);
    }

    /// <summary>True if the CURRENT character has an accepted consent on record (live or remembered).</summary>
    public bool IsCurrentCharacterConsented() => LocalInfo().State == State.accepted.ToString();

    /// <summary>True when an own character is detected but has no consent decision yet — the UI should
    /// show the consent modal (React: modal opens when state is unknown for the detected character).</summary>
    public bool NeedsConsentPrompt() => _ownCharacter().Detected && LocalInfo().State == State.unknown.ToString();

    /// <summary>Identity hash of the current character (lets the UI prompt once per character).</summary>
    public string? CurrentCharacterHash() => CurrentIdentityHash();

    /// <summary>Identity hashes of all locally-remembered consented characters (the consented list).</summary>
    public IReadOnlyList<string> ConsentedCharacterHashes() =>
        LoadCharacters().Where(kv => TryState(kv.Value.State) == State.accepted).Select(kv => kv.Key).ToList();

    /// <summary>One locally-remembered character's consent, for the per-character management UI.</summary>
    public sealed record CharacterConsentInfo(
        string IdentityHash, string? Nickname, int Server, string? Job, string State,
        bool UploadEnabled, bool PublicCharacter, long UpdatedAt, bool IsCurrent, bool CanSetPublic,
        // True once this install holds the server-side grant for this character (cached from an upload/accept
        // response). The "공개" toggle needs grant; see W18 (Grant || IsCurrent allows the attempt).
        bool Grant = false,
        // True when the app's own 공개 자동화 apply failed to reach the server. PublicCharacter is false and
        // correct (the transition never landed) and uploads are unaffected — the UI should say the automatic
        // apply failed and let the user re-apply 공개 by hand, because nothing retries it.
        bool PublicApplyFailed = false);

    /// <summary>All locally-remembered characters and their consent (the management list), current character
    /// first then most-recently-updated. <c>CanSetPublic</c> is false for a legacy entry that has no stored
    /// nickname/server and isn't the current character — its accept event can't be rebuilt, so its public
    /// flag can't be changed (it can still be revoked, which needs only the hash).</summary>
    public IReadOnlyList<CharacterConsentInfo> ListCharacters()
    {
        string? current = CurrentIdentityHash();
        // The CURRENT character's display name may be missing from its stored entry (synced in a prior session,
        // or saved before its nickname was recognized). Resolve it LIVE from the executor so "내 캐릭터 관리"
        // shows the real name instead of "이름 없음" right away (BackfillCurrentCharacterIdentity persists it).
        User? liveSelf = current != null ? _data.User(_data.ExecutorId()) : null;
        string? liveNick = NonBlank(liveSelf?.Nickname);
        return LoadCharacters()
            .Select(kv =>
            {
                CharConsent c = kv.Value;
                bool isCurrent = kv.Key == current;
                string? nickname = c.Nickname;
                int server = c.Server;
                string? job = c.Job;
                if (isCurrent && liveNick != null)
                {
                    nickname = liveNick;
                    if (liveSelf!.Server > 0) server = liveSelf.Server;
                    job ??= NonBlank(_ownCharacter().Job) ?? liveSelf.Job?.ClassName();
                }

                bool named = !string.IsNullOrWhiteSpace(nickname) && server > 0;
                return new CharacterConsentInfo(
                    kv.Key, nickname, server, job, c.State, c.UploadEnabled, c.PublicCharacter,
                    c.UpdatedAt, isCurrent, CanSetPublic: isCurrent || named, Grant: c.Grant,
                    PublicApplyFailed: c.PublicApplyFailed);
            })
            // Hide name-less legacy records (consented in a prior session before names were stored): a list of
            // "이름 없음 (이전 기록)" rows is confusing. The consent record stays (uploads honor the prior decision);
            // the character reappears here — named — the moment it reconnects (BackfillCurrentCharacterIdentity).
            .Where(c => !string.IsNullOrWhiteSpace(c.Nickname))
            .OrderByDescending(c => c.IsCurrent)
            .ThenByDescending(c => c.UpdatedAt)
            .ToList();
    }

    /// <summary>Fill in the CURRENT character's display name (nickname/server/job) on its stored consent record
    /// from the live executor — local only, never uploaded — so the management list shows the real name even
    /// after the character disconnects. Idempotent: no-op when nothing changed or the character has no record.</summary>
    public void BackfillCurrentCharacterIdentity()
    {
        User? u = _data.User(_data.ExecutorId());
        string? nickname = NonBlank(u?.Nickname);
        if (u == null || nickname == null)
        {
            return;
        }

        string? hash = StatsIdentity.CharacterIdentityHash(u.Server, nickname);
        if (hash == null)
        {
            return;
        }

        Dictionary<string, CharConsent> map = LoadCharacters();
        if (!map.TryGetValue(hash, out CharConsent? entry))
        {
            return; // only label characters that already have a consent record (don't create one here)
        }

        string? job = NonBlank(_ownCharacter().Job) ?? (u.Job is { } jc ? jc.ClassName() : null);
        bool changed = entry.Nickname != nickname
            || (u.Server > 0 && entry.Server != u.Server)
            || (job != null && entry.Job != job);
        if (!changed)
        {
            return;
        }

        entry.Nickname = nickname;
        if (u.Server > 0) entry.Server = u.Server;
        if (job != null) entry.Job = job;
        map[hash] = entry;
        _props.SetProperty(KeyCharacters, JsonSerializer.Serialize(map));
    }

    /// <summary>Set a SPECIFIC character's public flag. The current character routes through the normal
    /// accept path; a non-current character re-issues an accept event from its stored identity (name+server)
    /// and updates only its own remembered entry. No-op if the character isn't accepted or can't be rebuilt.</summary>
    public void SetCharacterPublic(string identityHash, bool publicCharacter, string clientVersion = "dev")
        => SetCharacterPublicCore(identityHash, publicCharacter, clientVersion, automatic: false);

    /// <param name="automatic">True only for the 공개 자동화 apply that <see cref="MarkGranted"/> fires: nobody
    /// pressed anything, so a failure here may not cost them their uploads (see
    /// <see cref="SaveAutomaticSendFailure"/>) and must be reported on the character record instead of silently.</param>
    private void SetCharacterPublicCore(string identityHash, bool publicCharacter, string clientVersion, bool automatic)
    {
        if (identityHash == CurrentIdentityHash())
        {
            // The automatic path has no UI gate in front of it (the manual toggle only exists on an accepted
            // row), so it checks the one thing the UI would have: a character that is no longer accepted — a
            // remote revoke adopted while an upload was still in flight — must not be re-accepted by a late
            // grant. Fail-safe direction is always 덜 공개.
            if (automatic && LocalInfo().State != State.accepted.ToString())
            {
                SetPendingPublic(identityHash, false);
                return;
            }

            // A fresh attempt supersedes the previous one's verdict; re-stamped below if this one fails too.
            MarkPublicApplyFailed(identityHash, false);
            Accept(LocalInfo().UploadEnabled, publicCharacter, clientVersion, automatic: automatic);
            return;
        }

        Dictionary<string, CharConsent> map = LoadCharacters();
        if (!map.TryGetValue(identityHash, out CharConsent? entry)
            || TryState(entry.State) != State.accepted
            || string.IsNullOrWhiteSpace(entry.Nickname)
            || entry.Server <= 0)
        {
            return;
        }

        // W18 gate (§2.4): turning a NON-current character public needs this install's grant. Making it
        // private is always allowed (privacy reduction). Block the ungranted public attempt + inform.
        if (publicCharacter && !entry.Grant)
        {
            RememberSync(PublicRequiresOwnership, null);
            SetPendingPublic(identityHash, true); // 공개 자동화: apply on the first upload that grants ownership
            return;
        }

        MarkPublicApplyFailed(identityHash, false); // a fresh attempt supersedes the previous verdict
        var character = new ConsentEventCharacter(identityHash, entry.Nickname!, entry.Server, publicCharacter, entry.Job, 0);
        try
        {
            ConsentStatusResponse response = _api.PostConsentEvent(
                new ConsentEventRequest(State.accepted.ToString(), ConsentVersion, Character: character), clientVersion);
            bool accepted = (TryState(response.ConsentState) ?? State.unknown) == State.accepted && response.Exists;
            UpsertCharacter(identityHash, accepted ? State.accepted : State.declined, entry.UploadEnabled,
                accepted && response.PublicCharacter, response.ConsentVersion ?? ConsentVersion,
                ParseRemoteTime(response.UpdatedAt) ?? _clock(), entry.Nickname, entry.Server, entry.Job,
                grant: response.Granted);
            SetPendingPublic(identityHash, false); // the user's explicit toggle supersedes any pending auto-public intent
            RememberSync("synced", null); // clear any stale public_requires_ownership notice from a prior action
        }
        catch (Exception e) when (publicCharacter && IsPublicOwnershipRejection(e))
        {
            // Grant cache was stale; the server refused. Roll the toggle back to private + inform.
            UpsertCharacter(identityHash, State.accepted, entry.UploadEnabled, publicCharacter: false,
                entry.ConsentVersion ?? ConsentVersion, _clock(), entry.Nickname, entry.Server, entry.Job);
            RememberSync(PublicRequiresOwnership, null);
        }
        catch
        {
            // leave the entry unchanged on a generic failed sync; the list re-reads the old state
            if (automatic)
            {
                // Nobody asked for this one, so silence is the wrong answer: the record carries the failure so
                // the list can show 공개 as off (it is — the send never landed) and offer a manual retry.
                MarkPublicApplyFailed(identityHash, true);
            }
        }
    }

    /// <summary>Revoke a SPECIFIC character's consent. The current character routes through the normal
    /// revoke path; a non-current character posts a revoke event (hash only) and marks its remembered entry
    /// revoked — even on a failed sync, so future uploads stop locally regardless.</summary>
    public void RevokeCharacter(string identityHash, string clientVersion = "dev")
    {
        SetPendingPublic(identityHash, false); // revoking cancels any remembered auto-public intent
        MarkPublicApplyFailed(identityHash, false); // ...and the '자동 공개 적용 실패' 알림도 함께
        if (identityHash == CurrentIdentityHash())
        {
            Revoke(clientVersion);
            return;
        }

        LoadCharacters().TryGetValue(identityHash, out CharConsent? entry);
        try
        {
            ConsentStatusResponse response = _api.PostConsentEvent(
                new ConsentEventRequest(State.revoked.ToString(), ConsentVersion, IdentityHash: identityHash), clientVersion);
            UpsertCharacter(identityHash, State.revoked, false, false,
                response.ConsentVersion ?? ConsentVersion, ParseRemoteTime(response.UpdatedAt) ?? _clock(),
                entry?.Nickname, entry?.Server ?? 0, entry?.Job, explicitChoice: true);
        }
        catch
        {
            // ⚠ explicitChoice: true — 전송 실패와 무관하게 '사용자가 눌렀다'를 남긴다. 없으면 서버에 남은
            // accepted 를 RefreshFromServer 가 복구 대상으로 읽어 이 캐릭터의 업로드를 되살린다.
            UpsertCharacter(identityHash, State.revoked, false, false, ConsentVersion, _clock(),
                entry?.Nickname, entry?.Server ?? 0, entry?.Job, explicitChoice: true);
        }
    }

    private Info LocalInfo()
    {
        // Session-level sync metadata is always global (it describes the last sync op, not a character).
        bool remoteExists = ParseBoolStrict(_props.GetProperty(KeyRemoteExists)) ?? false;
        string syncStatus = _props.GetProperty(KeySyncStatus) ?? "local";
        string? syncError = NonBlank(_props.GetProperty(KeySyncError));
        string? serverUpdatedAt = NonBlank(_props.GetProperty(KeyServerUpdatedAt));
        string? lastSeenAt = NonBlank(_props.GetProperty(KeyLastSeenAt));

        string? currentHash = CurrentIdentityHash();
        Dictionary<string, CharConsent> map = LoadCharacters();

        // 1) Remembered decision for the current character -> use it (no re-prompt).
        if (currentHash != null && map.TryGetValue(currentHash, out CharConsent? entry))
        {
            State st = TryState(entry.State) ?? State.unknown;
            return new Info(
                st.ToString(),
                st == State.accepted && entry.UploadEnabled,
                st == State.accepted && entry.PublicCharacter,
                entry.ConsentVersion ?? ConsentVersion,
                entry.UpdatedAt,
                currentHash,
                remoteExists, syncStatus, syncError, serverUpdatedAt, lastSeenAt);
        }

        // 2) Migration / no-character fallback: use the legacy global keys only when they belong to the
        //    current character (a pre-feature consent) or no character is detected yet.
        string? globalIdentity = NonBlank(_props.GetProperty(KeyIdentityHash));
        if (currentHash == null || globalIdentity == currentHash)
        {
            return GlobalInfo(remoteExists, syncStatus, syncError, serverUpdatedAt, lastSeenAt);
        }

        // 3) A detected character with no decision on record -> unknown (eligible to be prompted).
        return new Info(State.unknown.ToString(), false, false, ConsentVersion, 0, currentHash,
            remoteExists, syncStatus, syncError, serverUpdatedAt, lastSeenAt);
    }

    private Info GlobalInfo(bool remoteExists, string syncStatus, string? syncError, string? serverUpdatedAt, string? lastSeenAt)
    {
        State state = TryState(_props.GetProperty(KeyState)) ?? State.unknown;
        bool uploadEnabled = ParseBoolStrict(_props.GetProperty(KeyUploadEnabled)) ?? false;
        bool publicCharacter = ParseBoolStrict(_props.GetProperty(KeyPublicCharacter)) ?? false;
        string consentVersion = _props.GetProperty(KeyConsentVersion) ?? ConsentVersion;
        long updatedAt = long.TryParse(_props.GetProperty(KeyUpdatedAt), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0L;
        string? identityHash = NonBlank(_props.GetProperty(KeyIdentityHash));
        return new Info(
            state.ToString(),
            state == State.accepted && uploadEnabled,
            state == State.accepted && publicCharacter,
            consentVersion,
            updatedAt,
            identityHash,
            remoteExists,
            syncStatus,
            syncError,
            serverUpdatedAt,
            lastSeenAt);
    }

    /// <summary>물리적으로 존재할 수 없는 서버 번호. 하드 화이트리스트가 <b>아니라</b> 느슨한 상·하한이다 —
    /// 운영 중 서버가 증설돼도 정상 사용자를 막지 않으면서(실제 범위는 1001-1021 / 2001-2021), 오프셋 오독이
    /// 만드는 값은 확실히 걸러낸다. 2026-07-30 사고의 실측값 47200은 최댓값의 23배다.</summary>
    private static bool IsImpossibleServer(int server) => server is < 1000 or > 3000;

    /// <summary>기동 시 1회 위생 정리: 존재할 수 없는 신원으로 저장된 동의 레코드를 지우고, 지워진 해시 목록을
    /// 돌려준다(호출자가 같은 해시로 남은 오드 기록도 함께 치울 수 있게).
    /// <para>2026-07-30: 0x3633 파서가 아웃바운드 난수 프레임을 본인으로 오인해 <c>nickname="I" / server=47200</c>
    /// 을 심었고, 그 신원으로 동의가 올라갔다가 서버에 거절당하면서(HTTP 400) 로컬에 accepted로 굳었다. 파서
    /// 게이트(<c>SearchOwnNickname</c>)는 <b>새</b> 오염만 막으므로, 이미 저장된 레코드는 여기서 치운다.</para>
    /// <para>판정은 <b>서버 번호만</b> 본다. 닉네임 길이/문자로 지우면 실존 캐릭터를 날릴 위험이 있고, 서버
    /// 번호는 오독 시 예외 없이 범위를 크게 벗어나 오탐이 사실상 없다. <c>Server == 0</c>인 레코드는 손대지
    /// 않는다 — 이름/서버가 저장되기 전 세션에서 동의한 <b>정상</b>인 구 레코드이고, 업로드는 그 결정을
    /// 존중해야 한다(<see cref="ListCharacters"/> 주석 참조).</para></summary>
    public IReadOnlyList<string> PurgeImpossibleCharacters()
    {
        Dictionary<string, CharConsent> map = LoadCharacters();
        List<string> purged = map
            .Where(kv => kv.Value.Server > 0 && IsImpossibleServer(kv.Value.Server))
            .Select(kv => kv.Key)
            .ToList();
        if (purged.Count == 0)
        {
            return Array.Empty<string>();
        }

        foreach (string hash in purged)
        {
            map.Remove(hash);
        }

        _props.SetProperty(KeyCharacters, JsonSerializer.Serialize(map));

        // 전역 키가 같은 신원을 가리키고 있으면 함께 비운다. 안 그러면 마이그레이션 폴백(LocalInfo 2번 분기)이
        // 방금 지운 신원의 상태를 계속 되살린다. 거기 남은 sync 상태/오류도 그 신원의 것이므로 같이 지운다
        // (실측 잔존값: syncStatus=sync_failed / "HTTP 400 invalid_schema").
        string? globalIdentity = NonBlank(_props.GetProperty(KeyIdentityHash));
        if (globalIdentity != null && purged.Contains(globalIdentity, StringComparer.Ordinal))
        {
            _props.SetProperty(KeyIdentityHash, string.Empty);
            _props.SetProperty(KeyState, State.unknown.ToString());
            _props.SetProperty(KeyUploadEnabled, BoolStr(false));
            _props.SetProperty(KeyPublicCharacter, BoolStr(false));
            _props.SetProperty(KeySyncStatus, "local");
            _props.SetProperty(KeySyncError, string.Empty);
        }

        return purged;
    }

    private Dictionary<string, CharConsent> LoadCharacters()
    {
        string? raw = _props.GetProperty(KeyCharacters);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, CharConsent>>(raw) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void UpsertCharacter(string identityHash, State state, bool uploadEnabled, bool publicCharacter,
        string version, long updatedAt, string? nickname = null, int server = 0, string? job = null, bool grant = false,
        bool? explicitChoice = null)
    {
        Dictionary<string, CharConsent> map = LoadCharacters();
        CharConsent entry = map.TryGetValue(identityHash, out CharConsent? existing) ? existing : new CharConsent();
        if (explicitChoice is { } chosen)
        {
            entry.Explicit = chosen; // only a real button press flips this; a server echo passes null
        }

        entry.State = state.ToString();
        entry.UploadEnabled = state == State.accepted && uploadEnabled;
        entry.PublicCharacter = state == State.accepted && publicCharacter;
        entry.ConsentVersion = version;
        entry.UpdatedAt = updatedAt;
        // Preserve previously-stored identity metadata when this call doesn't supply it (e.g. a hash-only
        // revoke of a non-current character).
        if (!string.IsNullOrWhiteSpace(nickname)) entry.Nickname = nickname;
        if (server > 0) entry.Server = server;
        if (!string.IsNullOrWhiteSpace(job)) entry.Job = job;
        if (grant) entry.Grant = true; // monotone: a response can confer grant, never withdraw it
        map[identityHash] = entry;
        _props.SetProperty(KeyCharacters, JsonSerializer.Serialize(map));
    }

    /// <summary>Mark a character as granted (server confirmed this install owns it). Called after a successful
    /// signed upload whose response carries <c>granted</c>. Grant-only: never changes consent state, and only
    /// ever sets the flag true (the server grant set is monotone). No-op if already granted.</summary>
    public void MarkGranted(string identityHash, string clientVersion = "dev")
    {
        if (string.IsNullOrWhiteSpace(identityHash))
        {
            return;
        }

        Dictionary<string, CharConsent> map = LoadCharacters();
        CharConsent entry = map.TryGetValue(identityHash, out CharConsent? existing) ? existing : new CharConsent();
        if (entry.Grant)
        {
            return; // already granted — avoid a redundant settings write from the upload thread
        }

        entry.Grant = true;
        map[identityHash] = entry;
        _props.SetProperty(KeyCharacters, JsonSerializer.Serialize(map));

        // 공개 자동화: the user consented to "공개" before this install owned the character (no uploaded battle
        // yet), so the server refused it and we remembered the intent. This FIRST grant unlocks it — apply it
        // now so they don't have to re-toggle public after their first upload. SetCharacterPublic clears the
        // pending flag on success and no-ops if the character isn't in an acceptable state.
        if (entry.PendingPublic)
        {
            // automatic: true — 여기서 전송이 실패해도 사용자가 켜 둔 업로드를 끄지 않는다(SaveAutomaticSendFailure).
            SetCharacterPublicCore(identityHash, publicCharacter: true, clientVersion, automatic: true);
        }
    }

    /// <summary>Remember (or clear) that the user wants this character public but couldn't yet (no grant).
    /// Persisted on the character record so the first upload's grant can auto-apply public (공개 자동화).</summary>
    /// <summary>Put a character back to <c>unknown</c> so the consent dialog asks once more, and stamp the record
    /// so it can never be asked again this way. Local only — nothing is sent, and no other field is touched.</summary>
    private void ClearForOneReprompt(string identityHash)
    {
        Dictionary<string, CharConsent> map = LoadCharacters();
        if (!map.TryGetValue(identityHash, out CharConsent? entry))
        {
            return;
        }

        entry.Reprompted = true;
        entry.State = State.unknown.ToString();
        entry.UploadEnabled = false;
        entry.PublicCharacter = false;
        entry.UpdatedAt = _clock();
        map[identityHash] = entry;
        _props.SetProperty(KeyCharacters, JsonSerializer.Serialize(map));
    }

    /// <summary>Record (or clear) "the app's own 공개 적용이 실패했다" on a character record, for the management
    /// list to surface. Local only; never sent. See <see cref="CharConsent.PublicApplyFailed"/>.</summary>
    private void MarkPublicApplyFailed(string identityHash, bool failed)
    {
        if (string.IsNullOrWhiteSpace(identityHash))
        {
            return;
        }

        Dictionary<string, CharConsent> map = LoadCharacters();
        if (!map.TryGetValue(identityHash, out CharConsent? entry))
        {
            return; // no record = nothing to annotate (and never create one just to carry a notice)
        }

        if (entry.PublicApplyFailed == failed)
        {
            return;
        }

        entry.PublicApplyFailed = failed;
        map[identityHash] = entry;
        _props.SetProperty(KeyCharacters, JsonSerializer.Serialize(map));
    }

    private void SetPendingPublic(string identityHash, bool pending)
    {
        if (string.IsNullOrWhiteSpace(identityHash))
        {
            return;
        }

        Dictionary<string, CharConsent> map = LoadCharacters();
        if (!map.TryGetValue(identityHash, out CharConsent? entry))
        {
            if (!pending)
            {
                return;
            }

            entry = new CharConsent();
            map[identityHash] = entry;
        }

        if (entry.PendingPublic == pending)
        {
            return;
        }

        entry.PendingPublic = pending;
        map[identityHash] = entry;
        _props.SetProperty(KeyCharacters, JsonSerializer.Serialize(map));
    }

    /// <summary>True when the app's own 공개 자동화 apply for this character failed to reach the server and the
    /// user has not re-tried since. The 캐릭터 목록 uses it to explain why 공개 is off after they turned it on.</summary>
    public bool HasPublicApplyFailure(string identityHash) =>
        !string.IsNullOrWhiteSpace(identityHash)
        && LoadCharacters().TryGetValue(identityHash, out CharConsent? failedEntry)
        && failedEntry.PublicApplyFailed;

    /// <summary>True when this install holds the server-side grant for <paramref name="identityHash"/> (cached).
    /// Used by the public-transition gate (§2.4) and the management UI.</summary>
    public bool HasGrant(string identityHash) =>
        !string.IsNullOrWhiteSpace(identityHash)
        && LoadCharacters().TryGetValue(identityHash, out CharConsent? entry)
        && entry.Grant;

    private sealed class CharConsent
    {
        [JsonPropertyName("state")] public string State { get; set; } = "unknown";
        [JsonPropertyName("uploadEnabled")] public bool UploadEnabled { get; set; }
        [JsonPropertyName("publicCharacter")] public bool PublicCharacter { get; set; }
        [JsonPropertyName("consentVersion")] public string? ConsentVersion { get; set; }
        [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
        // Display metadata for the per-character management UI (so it can label characters by name, not the
        // raw hash). MUST stay on the DEFAULT serializer so Korean is \uXXXX-escaped to ASCII and survives
        // the EUC-KR settings encoding — StatsJson's relaxed escaping would corrupt raw Korean here.
        [JsonPropertyName("nickname")] public string? Nickname { get; set; }
        [JsonPropertyName("server")] public int Server { get; set; }
        [JsonPropertyName("job")] public string? Job { get; set; }
        // Character grant (§2.2): true once this install uploaded/accepted with this character so the server
        // granted ownership — gates the "공개" toggle. Cache only; the server is the authority. Grant is a
        // monotone TOFU set on the server, so locally we only ever set it true, never clear it.
        [JsonPropertyName("grant")] public bool Grant { get; set; }

        // 공개 자동화: the user asked for "공개" while this install had no grant yet (no uploaded battle), so the
        // server refused it. Remembered here so the first upload that earns the grant auto-applies public instead
        // of making them re-toggle it. Cleared once public is applied, the user turns it off, or revokes.
        [JsonPropertyName("pendingPublic")] public bool PendingPublic { get; set; }

        // 공개 자동화 the app tried and could not deliver (the send failed for a reason other than ownership).
        // The character is NOT public and uploads are untouched; this exists so the 캐릭터 목록 can say the
        // automatic apply failed instead of leaving the user staring at an unchecked box they did check.
        // Cleared by the next attempt or by a revoke. Nothing retries it on its own — MarkGranted fires once
        // per grant — so the retry is the user's own toggle.
        [JsonPropertyName("publicApplyFailed")] public bool PublicApplyFailed { get; set; }

        /// <summary>
        /// The user actually pressed a button for THIS character on THIS install (동의 / 거부 / 철회 / 토글).
        ///
        /// <para>Everything else — a dialog dismissed with the window close button, a state written to keep the
        /// record shaped, a server echo — is not a decision. The distinction exists because it decides what
        /// <see cref="RefreshFromServer"/> is allowed to repair: a character the server already knows as
        /// <c>accepted</c> whose local record says otherwise WITHOUT this flag is a stuck install, not a choice,
        /// and leaving it stuck costs that character every upload, its 소유 증명, and with it 공개 전환 and
        /// 스킨 선택 — silently and forever.</para>
        ///
        /// <para>Absent in records written before this field existed, which deserializes to false. That is the
        /// right default: those records predate the ability to say "the user meant it", so they are repairable.</para>
        /// </summary>
        [JsonPropertyName("explicit")] public bool Explicit { get; set; }

        /// <summary>
        /// This character already used up its one re-ask. See <see cref="StatsConsentManager.RefreshFromServer"/>.
        /// <para>Once, and only once: builds before 2026-08-23 recorded a dismissed dialog as a decline, and a
        /// declined character is never prompted again (<see cref="NeedsConsentPrompt"/> only fires on unknown).
        /// Anyone stranded that way has no way back that they would ever find. One prompt reopens the door; a
        /// second one would be nagging someone who has now said no twice.</para>
        /// </summary>
        [JsonPropertyName("reprompted")] public bool Reprompted { get; set; }
    }

    private void SaveLocal(
        State state,
        bool uploadEnabled,
        bool publicCharacter,
        string syncStatus,
        string? consentVersion = null,
        long? updatedAt = null,
        string? identityHash = KeepCurrentIdentity,
        bool remoteExists = false,
        string? syncError = null,
        string? serverUpdatedAt = null,
        string? lastSeenAt = null,
        bool grant = false,
        bool? explicitChoice = null)
    {
        string? resolvedIdentity = ReferenceEquals(identityHash, KeepCurrentIdentity) ? CurrentIdentityHash() : identityHash;
        string version = consentVersion ?? ConsentVersion;
        long updated = updatedAt ?? _clock();

        _props.SetProperty(KeyState, state.ToString());
        _props.SetProperty(KeyUploadEnabled, BoolStr(state == State.accepted && uploadEnabled));
        _props.SetProperty(KeyPublicCharacter, BoolStr(state == State.accepted && publicCharacter));
        _props.SetProperty(KeyConsentVersion, version);
        _props.SetProperty(KeyUpdatedAt, updated.ToString(CultureInfo.InvariantCulture));
        _props.SetProperty(KeyIdentityHash, resolvedIdentity ?? string.Empty);
        _props.SetProperty(KeyRemoteExists, BoolStr(remoteExists));
        _props.SetProperty(KeySyncStatus, syncStatus);
        _props.SetProperty(KeySyncError, syncError ?? string.Empty);
        _props.SetProperty(KeyServerUpdatedAt, serverUpdatedAt ?? string.Empty);
        _props.SetProperty(KeyLastSeenAt, lastSeenAt ?? string.Empty);

        // Remember this decision against the CURRENT character (req 2), keyed by the locally-computed
        // identity hash — which is exactly what LocalInfo looks up (the server may echo a different/
        // stubbed hash into the global key, but the per-character map must match the local lookup). When
        // no character is detected, only the global keys are written.
        string? mapKey = CurrentIdentityHash();
        if (mapKey != null)
        {
            // Stamp the current character's display identity (name/server/job) so the management UI can
            // label it; resolve from the live executor (and the own-character provider for job).
            User? u = _data.User(_data.ExecutorId());
            string? job = NonBlank(_ownCharacter().Job) ?? (u?.Job is { } jc ? jc.ClassName() : null);
            UpsertCharacter(mapKey, state, uploadEnabled, publicCharacter, version, updated,
                NonBlank(u?.Nickname), u?.Server ?? 0, job, grant, explicitChoice);
        }
    }

    private void RememberSync(string status, string? error)
    {
        _props.SetProperty(KeySyncStatus, status);
        _props.SetProperty(KeySyncError, error ?? string.Empty);
    }

    private static long? ParseRemoteTime(string? value)
    {
        if (value == null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }

    // Kotlin Boolean.toString() / toBooleanStrictOrNull() are lowercase-exact; match that so files
    // round-trip with the Kotlin app (C# bool.ToString() would write "True").
    private static string BoolStr(bool value) => value ? "true" : "false";

    private static bool? ParseBoolStrict(string? value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Server error code (§2.4) returned when a public transition lacks the required grant. Surfaced
    /// to the UI as a sync status so it can roll the toggle back + show a localized notice. ASCII-only — never
    /// store a Korean message in the global sync keys (the EUC-KR settings re-decode would corrupt it).</summary>
    public const string PublicRequiresOwnership = "public_requires_ownership";

    private static bool IsPublicOwnershipRejection(Exception e) =>
        (e is StatsApiException sae && sae.ResponseBody?.Contains(PublicRequiresOwnership, StringComparison.Ordinal) == true)
        || (e.Message?.Contains(PublicRequiresOwnership, StringComparison.Ordinal) == true);

    private static State? TryState(string? value)
    {
        if (value != null && Enum.TryParse(value, ignoreCase: false, out State state) && Enum.IsDefined(state))
        {
            return state;
        }

        return null;
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
}
