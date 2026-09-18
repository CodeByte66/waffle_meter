namespace WaffleMeter.Data;

/// <summary>
/// Official character info (Kotlin <c>official.OfficialCharacterInfo</c>). Lives in the data layer
/// so <see cref="DataManager"/> can consume it without depending on the Services project; the actual
/// HTTP lookup (WaffleMeter.Services.OfficialCharacterLookup) implements
/// <see cref="IOfficialCharacterLookup"/> and is injected via <see cref="DataManager.OfficialLookup"/>.
/// </summary>
/// <param name="Skills">code → level for skills the character has EQUIPPED (<c>acquired &gt; 0 &amp;&amp; equip == 1</c>).
/// An active the character owns but has not slotted is deliberately absent — for an active, "not slotted" is
/// itself the answer the join panel wants to show.</param>
/// <param name="Unequipped">code → level for skills the character owns but the site reports as NOT equipped.
/// <para>🔑 This exists because <b>passives are reported as <c>equip:0</c> forever</b> (measured live
/// 2026-08-23 across 108 level-45+ characters). "Equipped" is not a concept that applies to them, so the
/// equip filter — correct for actives — silently erases every passive. Their level does ride along on the
/// same row, so keeping the unequipped half here lets a caller that knows which codes are passive
/// (<c>SkillCatalog</c>, which this layer cannot see) pick exactly those out and leave the rest alone.</para></param>
public sealed record OfficialCharacterInfo(
    string Nickname,
    int Server,
    JobClass? Job,
    int Power,
    IReadOnlyDictionary<int, int> Skills,
    IReadOnlyDictionary<int, int>? Unequipped = null)
{
    /// <summary>Never null, so callers need no defensive branch. Empty for an offline/replay lookup.</summary>
    public IReadOnlyDictionary<int, int> UnequippedOrEmpty => Unequipped ?? EmptyMap;

    private static readonly IReadOnlyDictionary<int, int> EmptyMap = new Dictionary<int, int>();
}

/// <summary>
/// Abstraction over the official-site character lookup, injected into <see cref="DataManager"/>.
/// Left null in offline/replay runs (no network), which keeps enrichment a no-op and the DPS golden
/// unchanged.
/// </summary>
public interface IOfficialCharacterLookup
{
    void LookupAsync(string? nickname, int server, JobClass? fallbackJob, Action<OfficialCharacterInfo> callback);

    OfficialCharacterInfo? LookupBlocking(string? nickname, int server, JobClass? fallbackJob);
}
