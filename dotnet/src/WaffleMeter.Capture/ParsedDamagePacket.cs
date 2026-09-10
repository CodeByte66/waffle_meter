namespace WaffleMeter.Capture;

/// <summary>
/// Parsed damage/DoT event — the capture layer's output for one attack, mirroring Kotlin
/// <c>entity/ParsedDamagePacket.kt</c>. (In the final layering this is the DTO that the data/DPS
/// layer consumes; it lives in Capture for now and may move to Domain in Phase 1.)
/// </summary>
public sealed class ParsedDamagePacket
{
    public int ActorId { get; set; }
    public int TargetId { get; set; }
    public int Flag { get; set; }
    public int Damage { get; set; }
    public int SkillCode { get; set; }

    /// <summary>The full u32 skill code as it arrived on the wire, BEFORE
    /// <see cref="DamageParsing.NormalizeDamageSkillCode"/> collapsed it to a catalog code. Its last four
    /// decimal digits carry the caster's skill specialization (특화): dropping the ones digit, each of the
    /// remaining tens/hundreds/thousands digits (1..5) names one active specialization slot. Kept so the
    /// detail panel can surface which specializations a skill was cast with.</summary>
    public int RawSkillCode { get; set; }

    public int Type { get; set; }
    public int Unknown { get; set; }
    public int SwitchVariable { get; set; }
    public int Loop { get; set; }

    /// <summary>Attack direction relative to the target, from the position byte the 2026-07-01 patch added
    /// to the special-damage region (region offset [2]): 1 = back (후방), 2 = front (전방), 0 = neither
    /// (side / positionless / no directional data). Mutually exclusive by construction — it is a single
    /// value, not a bitmask. Absent (0) on switch-type-4 resource hits, which have no region byte.</summary>
    public int Position { get; set; }

    public long Timestamp { get; set; }
    public IReadOnlyList<SpecialDamage> Specials { get; set; } = [];
    public bool Dot { get; set; }

    /// <summary>The local player's judgment stats as of this packet's arrival, stamped by
    /// <c>DataManager.SaveDamage</c> for the executor's OWN hits only (every other actor's sheet is
    /// unobtainable — the stat dictionary is broadcast for the local player alone). Default (mask 0) on every
    /// other packet. See <see cref="JudgmentStatStamp"/> for why this is stamped here rather than read at
    /// accumulation time.</summary>
    public JudgmentStatStamp JudgmentStats { get; set; }

    /// <summary>Kotlin: <c>isCrit() = type == 3</c>.</summary>
    public bool IsCrit => Type == 3;

    /// <summary>A back attack (post-2026-07-01 position byte == 1). Replaces the old special-flag heuristic
    /// (raw bit 0x80), which measured an unrelated ~45%-incidence proc, not hit direction.</summary>
    public bool IsBack => Position == 1;

    /// <summary>A front attack (post-2026-07-01 position byte == 2).</summary>
    public bool IsFront => Position == 2;
}
