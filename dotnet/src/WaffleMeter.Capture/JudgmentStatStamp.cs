namespace WaffleMeter.Capture;

/// <summary>
/// The local player's judgment-relevant stats AS THEY WERE when one damage packet arrived.
///
/// <para><b>Why per hit.</b> These stats are not constants within a battle — a measured session moved 강타
/// (443) from 52.44% to 103.44%, 완벽 (442) from 53.08% to 78.08% as party buffs landed and fell off. A single
/// battle-level scalar (a login snapshot, or an average) therefore cannot say what the proc probability WAS for
/// any given hit, and averaging is not merely imprecise: it biases the estimate, because the hits are not
/// spread evenly across the stat's range. Stamping the value onto the packet at arrival is the only reading
/// that stays correct, and it costs a struct copy.</para>
///
/// <para><b>Why at arrival and not at accumulation.</b> The packet repository keeps the
/// <see cref="ParsedDamagePacket"/> object and the DPS calculator re-reads it on every replay (a cache reset
/// re-accumulates the whole window from sequence 0). A value read at accumulation time would therefore be the
/// stat sheet as of the LAST replay — in the worst case the sheet at battle end, applied retroactively to every
/// hit. Stamped at arrival, it survives any number of replays unchanged.</para>
///
/// <para><b>Absent is not zero.</b> Each field has a presence bit in <see cref="Mask"/>. The stat dictionary
/// arrives incrementally (0x364A deltas) with a full sheet (0x3649) only on character/zone load, so a meter
/// started mid-session legitimately has some of these and not others. Reading a missing 443 as 0% would report
/// "this player has no 강타" and drag any estimate built on it toward a resistance that does not exist.</para>
/// </summary>
/// <param name="Mask">Which fields are present — see the <c>Has*</c> constants.</param>
/// <param name="HardHitBp">강타 (stat id 443) in basis points. May be negative and may exceed 10,000.</param>
/// <param name="PerfectBp">완벽 (442) in basis points.</param>
/// <param name="Accuracy">추가 명중 (104), a flat rating.</param>
/// <param name="CriticalBp">기본 치명타 (128), a flat rating despite the name.</param>
/// <param name="CriticalIncBp">치명타 증가율 (429) in basis points.</param>
/// <param name="At">Capture-clock ms of the stat frame this was read from — how stale the reading was.</param>
public readonly record struct JudgmentStatStamp(
    int Mask,
    int HardHitBp,
    int PerfectBp,
    int Accuracy,
    int CriticalBp,
    int CriticalIncBp,
    long At)
{
    public const int HasHardHit = 1 << 0;
    public const int HasPerfect = 1 << 1;
    public const int HasAccuracy = 1 << 2;
    public const int HasCritical = 1 << 3;
    public const int HasCriticalInc = 1 << 4;

    /// <summary>True when at least one stat was readable. A packet that arrived before any stat frame carries
    /// mask 0 and must be excluded from every judgment denominator — not counted as a zero.</summary>
    public bool Stamped => Mask != 0;

    public bool Has(int flag) => (Mask & flag) != 0;

    /// <summary>인게임 스탯창의 <b>치명타</b>: <c>기본 치명타 × (1 + 치명타 증가율)</c>, rounded down to an
    /// integer rating. Composed here rather than at read time so the packet keeps both raw terms — the split
    /// matters if the composition rule is ever revised, and the stamp is the only surviving record of it.
    /// <para>Null unless BOTH terms were present: composing from 128 alone silently reports a rating ~1.6x too
    /// low, which reads as a much worse crit build than the player actually had.</para></summary>
    public int? CriticalRating =>
        Has(HasCritical) && Has(HasCriticalInc)
            ? (int)((long)CriticalBp * (10_000L + CriticalIncBp) / 10_000L)
            : null;
}
