using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// Regression spec for the single-hit damage sanity cap (<c>MaxPlausibleDamage</c>) shared by the direct
/// (0x3804) and DoT (0x3805) paths.
///
/// <para>Two regressions are pinned here:</para>
/// <list type="number">
/// <item>the cap must not be REMOVED — a damage field of 0xFFFFFFFF parses to a ~2.1B varint and exhausts
/// the frame exactly, so nothing else rejects it; one such frame poisons a whole encounter's totals;</item>
/// <item>the cap must not be left at the old 10,000,000 — the measured single-hit peak grew 401,792
/// (2026-07-27) → 1,104,089 (2026-08-08) in six weeks, leaving only 9.1x headroom. Raised to 100,000,000.</item>
/// </list>
/// <para>Both parsers must read the SAME constant: a literal copied into one path and not the other is
/// exactly how they drifted before.</para>
/// </summary>
public class DamageSanityCapTests
{
    private sealed record DamageEvent(string Kind, int Damage, bool Saved, string? Reason);

    private sealed class DamageSink : IStreamProcessorSink
    {
        public readonly List<DamageEvent> Events = [];

        public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode)
            => Events.Add(new DamageEvent(kind, packet.Damage, saved, reason));

        public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) { }
        public void UnknownOpcode(int opcode, bool extraFlag, int len) { }
        public void CompressedPacket(int len, bool extraFlag) { }
        public void ParserError(string stage, string reason) { }
        public void Meta(string type, params (string Key, object? Value)[] fields) { }
        public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
    }

    /// <summary>LEB128, the encoding <see cref="PacketPrimitives.ReadVarInt"/> reads.</summary>
    private static IEnumerable<byte> VarInt(long value)
    {
        ulong v = (ulong)value;
        while (true)
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            if (v == 0)
            {
                yield return b;
                yield break;
            }

            yield return (byte)(b | 0x80);
        }
    }

    /// <summary>A minimal but structurally real 0x3804 direct-damage frame carrying <paramref name="damage"/>.</summary>
    private static byte[] DirectFrame(int damage)
    {
        var f = new List<byte>
        {
            // The leading varint is the declared length; the parser only uses its WIDTH. It must not be
            // 0x20 — ParsingDamage early-returns on packet[0] == 0x20 (the 29-byte companion/heal frames).
            0x30,
            0x04, 0x38,             // opcode 0x3804
            0x0A,                   // target = 10
            0x04,                   // switchVariable = 4 -> special-damage region is 8 bytes
            0x00,                   // flag
            0x14,                   // actor = 20 (!= target, so the actor == target branch is not taken)
            0x2C, 0x3B, 0x14, 0x01, // skill code u32 LE
            0x00,                   // the byte swallowed by `offset = temp + 5`
            0x02,                   // type
        };
        f.AddRange(new byte[8]);    // special region: all-zero -> no flags, no Restoration offset shift
        f.Add(0x00);                // "unknown" varint
        f.AddRange(VarInt(damage));
        f.Add(0x00);                // multi-hit trailer (the parser requires >= 1 byte past the damage varint)
        return [.. f];
    }

    /// <summary>A minimal but structurally real 0x3805 DoT frame carrying <paramref name="damage"/>.</summary>
    private static byte[] DotFrame(int damage)
    {
        var f = new List<byte>
        {
            0x30,
            0x05, 0x38,             // opcode 0x3805
            0x0A,                   // target = 10
            0x08,                   // DoT flag byte (consumed, not gated on)
            0x14,                   // actor = 20 (!= target)
            0x00,                   // unknown
            0x2C, 0x3B, 0x14, 0x01, // skill code u32 LE
        };
        f.AddRange(VarInt(damage));
        return [.. f];
    }

    private static DamageEvent Feed(byte[] frame)
    {
        var sink = new DamageSink();
        new StreamProcessor(sink).OnPacketReceived(frame, 0);
        return Assert.Single(sink.Events);
    }

    // 0xFFFFFFFF in the damage field parses to this as a varint — the sentinel the cap exists for.
    private const int SentinelDamage = 0x7FFFFFFF;

    [Theory]
    [InlineData(1)]
    [InlineData(401_792)]      // measured single-hit peak, 2026-07-27 session
    [InlineData(1_104_089)]    // measured single-hit peak, 2026-08-08 session
    [InlineData(9_999_999)]    // last value the OLD 10,000,000 cap admitted
    [InlineData(10_000_000)]   // the old cap itself: was dropped, must now be saved
    [InlineData(50_000_000)]
    [InlineData(99_999_999)]   // last value under the new cap
    public void Direct_damage_below_the_cap_is_saved(int damage)
    {
        DamageEvent e = Feed(DirectFrame(damage));
        Assert.Equal("direct", e.Kind);
        Assert.Equal(damage, e.Damage);
        Assert.True(e.Saved, $"{damage} must pass the sanity cap");
        Assert.Null(e.Reason);
    }

    [Theory]
    [InlineData(100_000_000)]      // the cap is inclusive-reject (>=)
    [InlineData(1_000_000_000)]
    [InlineData(SentinelDamage)]   // 0xFFFFFFFF garbage/sentinel frame — the reason the cap exists at all
    public void Direct_damage_at_or_above_the_cap_is_rejected(int damage)
    {
        DamageEvent e = Feed(DirectFrame(damage));
        Assert.Equal("direct", e.Kind);
        Assert.False(e.Saved);
        Assert.Equal("damage_guard", e.Reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1_104_089)]
    [InlineData(10_000_000)]
    [InlineData(99_999_999)]
    public void Dot_damage_below_the_cap_is_saved(int damage)
    {
        DamageEvent e = Feed(DotFrame(damage));
        Assert.Equal("dot", e.Kind);
        Assert.Equal(damage, e.Damage);
        Assert.True(e.Saved, $"{damage} must pass the sanity cap");
    }

    [Theory]
    [InlineData(100_000_000)]
    [InlineData(SentinelDamage)]
    public void Dot_damage_at_or_above_the_cap_is_rejected(int damage)
    {
        DamageEvent e = Feed(DotFrame(damage));
        Assert.Equal("dot", e.Kind);
        Assert.False(e.Saved);
        Assert.Equal("damage_guard", e.Reason);
    }

    [Fact]
    public void Direct_and_dot_reject_at_exactly_the_same_threshold()
    {
        // The two paths used to carry separate `>= 10000000` literals. If one is ever bumped alone this fails.
        foreach (int damage in new[] { 9_999_999, 10_000_000, 99_999_999, 100_000_000, 100_000_001, SentinelDamage })
        {
            Assert.Equal(Feed(DirectFrame(damage)).Saved, Feed(DotFrame(damage)).Saved);
        }
    }
}
