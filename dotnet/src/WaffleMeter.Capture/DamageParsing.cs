namespace WaffleMeter.Capture;

/// <summary>Result of <see cref="DamageParsing.TryParseMultiHit"/> (Kotlin MultiHitOutput).</summary>
public readonly record struct MultiHitOutput(int Time, int Damage, int NewOffset);

/// <summary>
/// Pure helpers used by the damage parser, ported verbatim from Kotlin <c>StreamProcessor</c>
/// (tryParseMultiHit 716-746, parseSpecialDamageFlags 824-865, normalizeDamageSkillCode 350-365).
/// CORRECTNESS-CRITICAL: a divergence here mis-counts damage / mis-attributes skills silently.
/// </summary>
public static class DamageParsing
{
    /// <summary>
    /// Reads a multi-hit run: a count byte then that many identical damage varints. If validation
    /// fails at any point, returns the offset UNCHANGED with time/damage 0 (Kotlin behavior).
    /// </summary>
    public static MultiHitOutput TryParseMultiHit(byte[] data, int offset)
    {
        int currentOffset = offset;
        int count = data[currentOffset] & 0xFF;
        currentOffset++;

        if (count == 0)
        {
            return new MultiHitOutput(0, 0, offset);
        }

        if (currentOffset >= data.Length)
        {
            return new MultiHitOutput(0, 0, offset);
        }

        VarIntOutput damageInfo = PacketPrimitives.ReadVarInt(data, currentOffset);
        if (damageInfo.Value == -1)
        {
            return new MultiHitOutput(0, 0, offset);
        }

        currentOffset += damageInfo.Length;

        if (damageInfo.Value == 0)
        {
            return new MultiHitOutput(0, 0, offset);
        }

        // The remaining (count - 1) varints must all equal the first.
        for (int k = 0; k < count - 1; k++)
        {
            if (currentOffset >= data.Length)
            {
                return new MultiHitOutput(0, 0, offset);
            }

            VarIntOutput next = PacketPrimitives.ReadVarInt(data, currentOffset);
            if (next.Value == -1)
            {
                return new MultiHitOutput(0, 0, offset);
            }

            currentOffset += next.Length;
            if (next.Value != damageInfo.Value)
            {
                return new MultiHitOutput(0, 0, offset);
            }
        }

        return new MultiHitOutput(count, damageInfo.Value, currentOffset);
    }

    /// <summary>
    /// Decodes the special-damage flag byte. size == 8 -> none; size >= 10 -> bit flags;
    /// any other size (e.g. 9) -> none. 0x80 (POWER_SHARD) is intentionally NOT decoded (commented
    /// out in Kotlin).
    /// </summary>
    public static List<SpecialDamage> ParseSpecialDamageFlags(byte[] packet)
    {
        var flags = new List<SpecialDamage>();

        if (packet.Length == 8)
        {
            return flags;
        }

        if (packet.Length >= 10)
        {
            // The 2026-07-01 patch rotated the special-flag byte RIGHT by 1 bit (BACK 0x01→0x80,
            // PERFECT 0x08→0x04, DOUBLE 0x10→0x08, …). Verified against pre/post captures: every observed
            // bit-combo maps by exactly a 1-bit circular rotation. Rotate LEFT by 1 to restore the original
            // bit layout so the flag masks below stay meaningful. (BACK had moved onto 0x80, whose decode is
            // commented out, which is why back-attacks read as 0% after the patch.)
            int raw = packet[0] & 0xFF;
            int flagByte = ((raw << 1) | (raw >> 7)) & 0xFF;
            if ((flagByte & 0x01) != 0) flags.Add(SpecialDamage.BACK);
            if ((flagByte & 0x02) != 0) flags.Add(SpecialDamage.UNKNOWN);
            if ((flagByte & 0x04) != 0) flags.Add(SpecialDamage.PARRY);
            if ((flagByte & 0x08) != 0) flags.Add(SpecialDamage.PERFECT);
            if ((flagByte & 0x10) != 0) flags.Add(SpecialDamage.DOUBLE);
            if ((flagByte & 0x20) != 0) flags.Add(SpecialDamage.ENDURE);
            if ((flagByte & 0x40) != 0) flags.Add(SpecialDamage.Restoration);
            // if ((flagByte & 0x80) != 0) flags.Add(SpecialDamage.POWER_SHARD);  // commented out in Kotlin
        }

        return flags;
    }

    /// <summary>
    /// Reads the attack-direction byte the 2026-07-01 patch added at region offset [2]: 1 = back (후방),
    /// 2 = front (전방), anything else (incl. the switch-type-4 8-byte resource form, which has no such
    /// byte) = 0 (neither / side / positionless). A single value, not a bitmask — front and back are
    /// mutually exclusive. Matches the client's own layout — other meters read this same byte.
    /// </summary>
    public static int ParsePosition(byte[] region) => ParsePosition(region, 2);

    /// <summary><paramref name="at"/> = 각도 바이트의 region 내 오프셋. 기본 2가 아니라 인자인 이유는
    /// 그 앞의 <c>_restoration_hp</c> varint 폭이 값에 따라 1~3바이트로 변하기 때문이다 — 흡혈이 붙은
    /// 타격에서 2로 고정해 읽으면 varint 중간 바이트를 각도로 읽는다. 실측(6세션 흡혈 프레임 120건):
    /// 고정 읽기는 6·13·5 같은 값을 내지만 폭을 반영해 읽으면 <b>120건 전부 {0,1,2}</b>로 떨어진다.</summary>
    public static int ParsePosition(byte[] region, int at)
    {
        if (region.Length < 10 || at < 0 || at >= region.Length)
        {
            return 0;
        }

        return region[at] switch
        {
            1 => 1,
            2 => 2,
            _ => 0,
        };
    }

    /// <summary>
    /// Picks the first skill-code candidate that exists in the catalog, else the fallback.
    /// Candidates (insertion-ordered, deduped — Kotlin linkedSetOf): for rawCode, rawCode/10,
    /// rawCode/100, each adds the code itself and its tens-rounded form; codes &lt;= 0 are skipped.
    /// </summary>
    public static int NormalizeDamageSkillCode(int rawCode, int fallback, Func<long, bool> skillExists)
    {
        var candidates = new List<int>();

        void Add(int code)
        {
            if (code <= 0)
            {
                return;
            }

            if (!candidates.Contains(code))
            {
                candidates.Add(code);
            }

            int rounded = (code / 10) * 10;
            if (!candidates.Contains(rounded))
            {
                candidates.Add(rounded);
            }
        }

        Add(rawCode);
        Add(rawCode / 10);
        Add(rawCode / 100);
        // Rank-variant collapse: skill codes are CC XX YYYY (class·skill·rank); the game sends the
        // equipped rank (e.g. 고결한 기운 17440050), but skills.json often only has the base 17440000.
        // The candidates above (÷10, ÷100) never reach the base, so an unmapped variant fell through as a
        // raw code (shown as a number, unmapped on the stats web). Floor to the base skill so it resolves
        // to the real name/icon. Added LAST so a variant that IS in skills.json still matches itself first.
        Add((rawCode / 10000) * 10000);

        foreach (int c in candidates)
        {
            if (skillExists(c))
            {
                return c;
            }
        }

        return fallback;
    }
}
