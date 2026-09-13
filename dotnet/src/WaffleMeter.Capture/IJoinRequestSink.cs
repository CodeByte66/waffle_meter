namespace WaffleMeter.Capture;

/// <summary>
/// App-facing channel for party join-request events (Kotlin PacketEvent.JoinRequest / JoinRequestRemove
/// / RefuseJoinRequest / ExitPartyUI). Kept separate from <see cref="IStreamProcessorSink"/> (which is
/// for diagnostics) and uses primitives only, so <c>WaffleMeter.Capture</c> stays free of a dependency
/// on the data/domain layer — App.Core resolves the job name and builds the JoinRequestUser.
/// </summary>
public interface IJoinRequestSink
{
    /// <summary>A join request arrived (or was refreshed). Add/replace by <paramref name="requester"/>.</summary>
    void OnJoinRequest(int requester, string nickname, int jobCode, int server, int power, long arrivedAt);

    /// <summary>The request was cancelled by the applicant (<paramref name="admit"/> false) or admitted by
    /// the leader (true) — remove it. An admit also answers the id-less 0x9709 that arrived with it, which is
    /// why the two cases are distinguished.</summary>
    void OnJoinRequestRemove(int requester, bool admit);

    /// <summary>0x9709 — one pending request was resolved, with no id saying which. Fires on an ACCEPT as well
    /// as a refusal, so the store waits briefly for a matching admit before falling back to "drop the oldest".</summary>
    void OnRefuseJoinRequest();

    /// <summary>Instance start or party exit — clear all pending requests.</summary>
    void OnExitPartyUi();
}

/// <summary>No-op sink (default when the app does not wire join handling, e.g. replay/tests).</summary>
public sealed class NullJoinRequestSink : IJoinRequestSink
{
    public static readonly NullJoinRequestSink Instance = new();
    public void OnJoinRequest(int requester, string nickname, int jobCode, int server, int power, long arrivedAt) { }
    public void OnJoinRequestRemove(int requester, bool admit) { }
    public void OnRefuseJoinRequest() { }
    public void OnExitPartyUi() { }
}
