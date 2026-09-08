using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// Spec for the weekly 성역 clear counters carried on the 0x610B/0x610C resource family.
/// <para>The byte fixtures are VERBATIM captures from
/// <c>packet-debug-logs/20260722-203343</c> (심연의 재련 : 루드라 run) — not hand-authored — because the whole
/// premise of the feature is that the game states these values and the meter only reads them.</para>
/// </summary>
public sealed class WeeklyContentParserTests
{
    private static byte[] Hex(string hex) =>
        hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

    /// <summary>The delta the server sent 0.236 s after 영겁의 루드라 died. Whole packet, as captured:
    /// <c>[0D length][0C 61 opcode][00 framing][00 mask][82 4A 5D 05 currency 90000002][03 trailer]</c>.
    /// bodyStart is 3 (past the length byte and the two opcode bytes).</summary>
    private const string RudraSpentDelta = "0D 0C 61 00 00 82 4A 5D 05 03";

    /// <summary>A slice of the 0x610B login snapshot around currency 90000002, as captured: the tail of the
    /// previous record, then <c>[04 mask][82 4A 5D 05 id][01 value]</c>, then the next record's start.</summary>
    private const string RudraAvailableSnapshotSlice = "04 04 82 4A 5D 05 01 04 83 4A 5D 05 04";

    [Fact]
    public void Reads_a_spent_counter_from_the_real_kill_delta()
    {
        WeeklyContentParse w = WeeklyContentParser.TryParse(Hex(RudraSpentDelta), 3, WeeklyContentKind.Rudra);

        Assert.True(w.Ok);
        Assert.Equal(0, w.Total);
    }

    /// <summary>The regression this parser exists to avoid. A spent counter is broadcast as a record with field
    /// mask 0x00 — no fields at all, because the game omits a pool that is zero. Both older parsers in this
    /// family fall through on a mask they don't list, which would drop precisely the 1 → 0 transition and leave
    /// a counter that never decrements.</summary>
    [Fact]
    public void Mask_zero_means_zero_not_unparseable()
    {
        byte[] packet = Hex(RudraSpentDelta);
        int mask = Array.IndexOf(packet, (byte)0x82) - 1;

        Assert.Equal(0x00, packet[mask]);
        Assert.True(WeeklyContentParser.TryParse(packet, 3, WeeklyContentKind.Rudra).Ok);
    }

    [Fact]
    public void Reads_an_available_counter_from_the_real_snapshot()
    {
        WeeklyContentParse w = WeeklyContentParser.TryParse(Hex(RudraAvailableSnapshotSlice), 0, WeeklyContentKind.Rudra);

        Assert.True(w.Ok);
        Assert.Equal(1, w.Base);
        Assert.Equal(0, w.Bonus);
        Assert.Equal(1, w.Total);
    }

    /// <summary>Each dungeon reads only its own currency. The snapshot carries all three back to back, and
    /// 90000003 (침식의 정화소's 도전 횟수 neighbour) sits immediately after 90000002 in the real bytes — an
    /// off-by-one in the scan would silently report one dungeon's count for another.</summary>
    [Fact]
    public void Does_not_confuse_neighbouring_currencies()
    {
        byte[] packet = Hex(RudraAvailableSnapshotSlice);

        Assert.True(WeeklyContentParser.TryParse(packet, 0, WeeklyContentKind.Rudra).Ok);
        Assert.False(WeeklyContentParser.TryParse(packet, 0, WeeklyContentKind.ErosionPurifier).Ok);
        Assert.False(WeeklyContentParser.TryParse(packet, 0, WeeklyContentKind.MuspelGrail).Ok);
        Assert.False(WeeklyContentParser.TryParse(packet, 0, WeeklyContentKind.FrozenLament).Ok);
    }

    [Theory]
    [InlineData(WeeklyContentKind.Rudra, 90_000_002u)]
    [InlineData(WeeklyContentKind.ErosionPurifier, 90_000_004u)]
    [InlineData(WeeklyContentKind.MuspelGrail, 90_000_006u)]
    // 비탄의 설원 (2026-09-09 패치): ContentsTicket 90000008 Contents_Ticket_FrozenLament_Clear, 주간 1회 수요일 충전.
    [InlineData(WeeklyContentKind.FrozenLament, 90_000_008u)]
    public void Maps_each_dungeon_to_its_observed_currency_id(WeeklyContentKind kind, uint expected) =>
        Assert.Equal(expected, WeeklyContentParser.CurrencyId(kind));

    /// <summary>The guard that replaces the old <c>_ =&gt; MuspelGrailId</c> catch-all. That arm meant a kind
    /// added to the enum without an id of its own read 무스펠의 성배's counter instead — a chip quietly showing
    /// another raid's number. Adding a dungeon must now either come with its currency id or fail right here.</summary>
    [Fact]
    public void Maps_every_declared_kind_to_a_distinct_currency_id()
    {
        uint[] ids = Enum.GetValues<WeeklyContentKind>().Select(WeeklyContentParser.CurrencyId).ToArray();

        Assert.DoesNotContain(0u, ids);                       // an unmapped kind reads nothing at all
        Assert.Equal(ids.Length, ids.Distinct().Count());     // ...and no two kinds read the same counter
    }

    /// <summary>The id must not be matched when it straddles the header — the mask byte would then be read from
    /// outside the body, i.e. from the opcode.</summary>
    [Fact]
    public void Ignores_a_currency_id_before_the_body()
    {
        byte[] packet = Hex(RudraSpentDelta);

        Assert.False(WeeklyContentParser.TryParse(packet, 5, WeeklyContentKind.Rudra).Ok);
    }

    /// <summary>The whole 512-byte 0x610B login snapshot, verbatim from the same session — 73 records of every
    /// resource the character holds, the three weekly counters among them. This is the packet the panel is
    /// populated from on login, so it is worth asserting against the real thing rather than a slice: the ids sit
    /// six-in-a-row at the very end (90000001..90000006, alternating 도전 횟수 4 / 처치 횟수 1), which is exactly
    /// where a scan that keyed on too few bytes would cross-match.</summary>
    private const string LoginSnapshot =
        "82 04 0B 61 49 04 01 00 00 00 0C 03 03 00 00 00 30 8C FF 02 00 00 00 00 00 F3 6F 06 00 00 00 00 04 " +
        "04 00 00 00 02 00 06 00 00 00 04 07 00 00 00 20 04 08 00 00 00 07 03 09 00 00 00 00 0B 01 03 00 00 " +
        "00 00 20 4F E5 00 00 00 00 00 04 0A 00 00 00 0E 04 0B 00 00 00 02 04 0C 00 00 00 0E 01 0D 00 00 00 " +
        "00 0B 01 03 00 00 00 00 04 0E 00 00 00 0E 04 0F 00 00 00 0E 00 65 00 00 00 04 66 00 00 00 07 04 67 " +
        "00 00 00 07 01 C9 00 00 00 80 85 80 01 00 00 00 00 01 CA 00 00 00 80 85 80 01 00 00 00 00 01 CB 00 " +
        "00 00 80 85 80 01 00 00 00 00 01 CC 00 00 00 80 85 80 01 00 00 00 00 01 CD 00 00 00 80 85 80 01 00 " +
        "00 00 00 01 CE 00 00 00 80 85 80 01 00 00 00 00 00 81 96 98 00 00 82 96 98 00 00 83 96 98 00 00 84 " +
        "96 98 00 00 85 96 98 00 00 86 96 98 00 00 87 96 98 00 00 88 96 98 00 00 89 96 98 00 00 8A 96 98 00 " +
        "00 8B 96 98 00 00 8C 96 98 00 08 01 87 93 03 C1 0F 0C 02 87 93 03 0E 06 04 03 87 93 03 0E 04 04 87 " +
        "93 03 0E 04 05 87 93 03 0E 0C 06 87 93 03 15 0A 0C 07 87 93 03 23 05 04 08 87 93 03 1C 04 09 87 93 " +
        "03 0A 04 0A 87 93 03 07 04 65 87 93 03 0E 04 C9 87 93 03 07 04 4D 8B 93 03 07 04 B1 8B 93 03 0E 04 " +
        "35 8F 93 03 07 04 99 8F 93 03 0E 04 1D 93 93 03 07 04 81 93 93 03 0E 04 05 97 93 03 07 04 69 97 93 " +
        "03 07 04 B5 9B 93 03 04 04 9D 9F 93 03 04 0C BD A2 93 03 07 01 04 21 A3 93 03 0E 04 8D AA 93 03 07 " +
        "04 F1 AA 93 03 0E 04 45 B6 93 03 07 04 A9 B6 93 03 07 04 2D BA 93 03 07 04 15 BE 93 03 07 04 FD C1 " +
        "93 03 07 04 81 1D 2C 04 03 00 01 B4 C4 04 04 81 4A 5D 05 04 04 82 4A 5D 05 01 04 83 4A 5D 05 04 04 " +
        "84 4A 5D 05 01 04 85 4A 5D 05 04 04 86 4A 5D 05 01";

    /// <summary>⚠️ Three kinds, not every kind, and it must stay that way: this fixture is a 2026-07-22 capture,
    /// and 비탄의 설원 (currency 90000008) did not exist until the 2026-09-09 patch. Adding
    /// <see cref="WeeklyContentKind.FrozenLament"/> here would assert that a byte run absent from the recorded
    /// packet is present — a red test that says nothing about the parser. Replace the fixture with a post-patch
    /// snapshot to extend it.</summary>
    [Theory]
    [InlineData(WeeklyContentKind.Rudra)]
    [InlineData(WeeklyContentKind.ErosionPurifier)]
    [InlineData(WeeklyContentKind.MuspelGrail)]
    public void Reads_every_dungeon_out_of_the_real_login_snapshot(WeeklyContentKind kind)
    {
        // bodyStart 4 = past the two-byte varint length and the 0x610B opcode, the same offset StreamProcessor
        // hands the parser. The character had cleared none of the three that week.
        WeeklyContentParse w = WeeklyContentParser.TryParse(Hex(LoginSnapshot), 4, kind);

        Assert.True(w.Ok);
        Assert.Equal(1, w.Total);
    }

    /// <summary>The 오드 record in the same snapshot decodes to what the app logged for this packet (base 0,
    /// bonus 1985). Reading a foreign resource with this parser's own grammar is the cross-check that the
    /// record layout — <c>[mask][currencyId u32-LE][values]</c> — is right, not just self-consistent.</summary>
    [Fact]
    public void Shares_its_record_grammar_with_the_aether_parser()
    {
        AetherParse a = AetherStatusParser.TryParse(Hex(LoginSnapshot), 4);

        Assert.True(a.Ok);
        Assert.Equal(0, a.Base);
        Assert.Equal(1985, a.Bonus);
    }

    [Fact]
    public void Reports_nothing_for_an_unrelated_packet() =>
        Assert.False(WeeklyContentParser.TryParse(Hex("01 02 03 04 05 06 07 08"), 0, WeeklyContentKind.Rudra).Ok);

    [Fact]
    public void Survives_a_truncated_record()
    {
        // mask says "carries a value" but the packet ends right after the id
        Assert.False(WeeklyContentParser.TryParse(Hex("04 82 4A 5D 05"), 0, WeeklyContentKind.Rudra).Ok);
    }
}
