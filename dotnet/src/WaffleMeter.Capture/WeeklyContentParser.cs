namespace WaffleMeter.Capture;

/// <summary>The 성역 raids whose weekly "최종 보스 처치 횟수" the game keeps per character.
/// <para>무스펠의 성배 has two difficulties (보통 620022 / 어려움 620021) but ONE counter — the client has a
/// single <c>Contents_Ticket_MusphelHolyGrail_Clear</c> currency, no per-difficulty suffix.</para></summary>
public enum WeeklyContentKind
{
    /// <summary>심연의 재련 : 루드라 — 최종 보스 영겁의 루드라 (mobCode 2301014).</summary>
    Rudra = 0,

    /// <summary>침식의 정화소 — 최종 보스 중합체 바고트 (mobCode 2301208).</summary>
    ErosionPurifier = 1,

    /// <summary>무스펠의 성배 — 최종 보스 칼드릭스 (보통 2301090 / 어려움 2301060, 횟수 공유).</summary>
    MuspelGrail = 2,

    /// <summary>비탄의 설원 — 최종 보스 델트라스 (쉬움 2301165 / 보통 2301145 / 어려움 2301115, 횟수 공유).
    /// 2026-09-09 패치 신규. 무스펠과 같은 이유로 한 항목이다: 클라 통화가
    /// <c>Contents_Ticket_FrozenLament_Clear</c>(ID 90000008) 하나뿐이고 난이도 접미사가 없다.</summary>
    FrozenLament = 3,
}

/// <summary>Outcome of decoding one weekly-content ticket record. Like aether, BOTH pools are authoritative:
/// a pool the record omits is ZERO, never "unchanged".</summary>
public readonly record struct WeeklyContentParse(bool Ok, int Base, int Bonus)
{
    public static readonly WeeklyContentParse None = default;

    /// <summary>Clears still available this week. 0 = this character has already killed the final boss.</summary>
    public int Total => Base + Bonus;
}

/// <summary>
/// Decodes the per-character weekly 성역 clear counters ("[성역] &lt;던전&gt; 최종 보스 처치 횟수") carried in the
/// SAME 0x610B/0x610C status family as 오드 and 슈고 열쇠. The game deducts one the instant the raid's FINAL
/// boss dies — measured at +0.13 s ~ +0.43 s after the kill across three capture sessions — so this is the
/// server's own answer, not something the meter has to infer from what it saw of the fight.
///
/// <para><b>Record layout.</b> What the aether parser documents as
/// <c>&lt;fieldMask&gt; &lt;resourceKey&gt; &lt;groupId(3)&gt; &lt;values…&gt;</c> is really
/// <c>&lt;fieldMask&gt; &lt;currencyId u32-LE&gt; &lt;values…&gt;</c>: 오드 is currency 60000001
/// (<c>01 87 93 03</c>) and the shugo key is currency 1 (<c>01 00 00 00</c>). Reading it as one id is what lets
/// these three be addressed by name. Verified by walking a 0x610B snapshot as 73 back-to-back records — the walk
/// consumes the body EXACTLY, and the 오드 record it recovers matches what the app logged for the same packet.</para>
///
/// <para><b>⚠️ Mask 0x00 is the signal we exist for.</b> The mask says which pools the record carries (0x04 =
/// first, 0x08 = second, 0x0C = both) and the game omits a pool that is zero — so a spent ticket arrives as a
/// record with NO fields at all. <see cref="AetherStatusParser"/> and <see cref="ShugoKeyParser"/> both fall
/// through on a mask they don't list, which for them is harmless (0 aether is not a state worth reporting) but
/// here would drop precisely the 1 → 0 transition this feature is about. Copying either of them verbatim
/// silently produces a counter that never decrements.</para>
///
/// Pure and allocation-free; the caller gates it behind the resource opcode so a coincidental byte run in an
/// unrelated packet can't false-match.
/// </summary>
public static class WeeklyContentParser
{
    // Currency ids, observed on the wire as u32-LE. The odd neighbours (90000001/3/5) are the matching
    // 도전 횟수 (entry) counters, which sat at 4 and never moved across the corpus — deliberately not read.
    private const uint RudraId = 90_000_002;
    private const uint ErosionPurifierId = 90_000_004;
    private const uint MuspelGrailId = 90_000_006;
    private const uint FrozenLamentId = 90_000_008;  // 2026-09-09 패치, ContentsTicket 90000008 (도전 횟수는 90000007)

    private const byte MaskNone = 0x00;   // no fields → both pools zero (= already cleared this week)
    private const byte MaskBase = 0x04;   // 기본 수량 only
    private const byte MaskBonus = 0x08;  // 추가 수량 only
    private const byte MaskBoth = 0x0C;   // both, 기본 first

    /// <summary>Sanity bound on one pool. The base grant is 1/week; the cap only has to reject a coincidental
    /// byte run, so it is loose enough to survive the game handing out bonus 처치권.</summary>
    private const int MaxTickets = 64;

    /// <summary>The wire currency id for a dungeon. 0 = no id is wired up for this kind, which
    /// <see cref="TryParse"/> refuses to scan for.
    /// <para><b>⚠️ No catch-all arm, on purpose.</b> This used to end in <c>_ =&gt; MuspelGrailId</c>, so any kind
    /// added to the enum without an id here would quietly read 무스펠의 성배's counter — the new raid's chip would
    /// show another raid's number, with nothing on screen to give it away. Every kind now gets its own arm and an
    /// unmapped one reads NOTHING; <c>Maps_every_declared_kind_to_a_distinct_currency_id</c> turns the omission
    /// into a red test instead of a silent misread.</para>
    /// <para>0 rather than a throw because the whole opcode dispatch shares one try/catch
    /// (<c>StreamProcessor.OnPacketReceived</c>): an exception raised here would also swallow the 어비스 회랑 parse
    /// that runs after us on the SAME packet, turning one missing id into two broken features.</para></summary>
    public static uint CurrencyId(WeeklyContentKind kind) => kind switch
    {
        WeeklyContentKind.Rudra => RudraId,
        WeeklyContentKind.ErosionPurifier => ErosionPurifierId,
        WeeklyContentKind.MuspelGrail => MuspelGrailId,
        WeeklyContentKind.FrozenLament => FrozenLamentId,
        _ => 0,
    };

    /// <summary>Scan <paramref name="packet"/> from <paramref name="bodyStart"/> for one dungeon's ticket
    /// record. A 0x610B snapshot carries every one of them (call once per kind); a 0x610C delta carries one.</summary>
    public static WeeklyContentParse TryParse(byte[] packet, int bodyStart, WeeklyContentKind kind)
    {
        uint id = CurrencyId(kind);
        if (id == 0)
        {
            // An unmapped kind. Scanning for 00 00 00 00 would false-match on the padding every snapshot is full
            // of and invent a count, which is worse than reporting nothing.
            return WeeklyContentParse.None;
        }

        Span<byte> needle = stackalloc byte[4];
        needle[0] = (byte)id;
        needle[1] = (byte)(id >> 8);
        needle[2] = (byte)(id >> 16);
        needle[3] = (byte)(id >> 24);

        int from = Math.Max(0, bodyStart);
        for (int g = IndexOf(packet, from + 1, needle); g >= 0; g = IndexOf(packet, g + 1, needle))
        {
            if (g - 1 < from)
            {
                continue; // the id straddles the header — its mask byte would be outside the body
            }

            int o = g + needle.Length;
            byte mask = packet[g - 1];
            if (mask == MaskNone)
            {
                return new WeeklyContentParse(true, 0, 0); // spent: the record carries no fields
            }

            if (mask == MaskBoth)
            {
                if (TryReadPool(packet, o, out int b, out int next) && TryReadPool(packet, next, out int bonus, out _))
                {
                    return new WeeklyContentParse(true, b, bonus);
                }
            }
            else if (mask == MaskBase && TryReadPool(packet, o, out int baseOnly, out _))
            {
                return new WeeklyContentParse(true, baseOnly, 0);
            }
            else if (mask == MaskBonus && TryReadPool(packet, o, out int bonusOnly, out _))
            {
                return new WeeklyContentParse(true, 0, bonusOnly);
            }

            // an unknown mask (or an out-of-range value) — keep scanning; a later record may still be ours
        }

        return WeeklyContentParse.None;
    }

    private static bool TryReadPool(byte[] packet, int at, out int value, out int next)
    {
        VarIntOutput v = PacketPrimitives.ReadVarInt(packet, at);
        value = v.Value;
        next = at + v.Length;
        return v.Length > 0 && v.Value >= 0 && v.Value <= MaxTickets;
    }

    private static int IndexOf(byte[] hay, int start, ReadOnlySpan<byte> needle)
    {
        int last = hay.Length - needle.Length;
        for (int i = Math.Max(0, start); i <= last; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j])
            {
                j++;
            }

            if (j == needle.Length)
            {
                return i;
            }
        }

        return -1;
    }
}
