using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// Parity spec for <see cref="PacketPrimitives.ReadVarInt"/> and the LE readers, encoding the
/// documented behavior of Kotlin StreamProcessor.readVarInt / parseUInt*le (incl. the -1 sentinels
/// and signed-int results). See docs/phase-0-parity-harness.md §7.
/// </summary>
public class PacketPrimitivesTests
{
    [Fact]
    public void ReadVarInt_single_byte()
    {
        VarIntOutput r = PacketPrimitives.ReadVarInt(new byte[] { 0x0D });
        Assert.Equal(13, r.Value);
        Assert.Equal(1, r.Length);
    }

    [Fact]
    public void ReadVarInt_two_bytes_little_endian_base128()
    {
        // 0x80 0x01 => (0 << 0) | (1 << 7) = 128, length 2
        VarIntOutput r = PacketPrimitives.ReadVarInt(new byte[] { 0x80, 0x01 });
        Assert.Equal(128, r.Value);
        Assert.Equal(2, r.Length);
    }

    [Fact]
    public void ReadVarInt_honors_offset()
    {
        VarIntOutput r = PacketPrimitives.ReadVarInt(new byte[] { 0xFF, 0xFF, 0x7F }, offset: 2);
        Assert.Equal(0x7F, r.Value);
        Assert.Equal(1, r.Length);
    }

    [Fact]
    public void ReadVarInt_out_of_bounds_returns_minus_one()
    {
        // continuation bit set but no following byte -> OOB sentinel (the split-varint case)
        VarIntOutput r = PacketPrimitives.ReadVarInt(new byte[] { 0x80 });
        Assert.Equal(-1, r.Value);
        Assert.Equal(-1, r.Length);
    }

    [Fact]
    public void ReadVarInt_overflow_past_32_bits_returns_minus_one()
    {
        // five continuation bytes push shift to 35 (>= 32) before terminating -> overflow sentinel
        VarIntOutput r = PacketPrimitives.ReadVarInt(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 });
        Assert.Equal(-1, r.Value);
        Assert.Equal(-1, r.Length);
    }

    [Fact]
    public void ParseUInt16Le_and_UInt32Le()
    {
        Assert.Equal(0x0201, PacketPrimitives.ParseUInt16Le(new byte[] { 0x01, 0x02 }));
        Assert.Equal(0x04030201, PacketPrimitives.ParseUInt32Le(new byte[] { 0x01, 0x02, 0x03, 0x04 }));
    }

    [Fact]
    public void ParseUInt32Le_is_signed_like_kotlin()
    {
        // high bit set -> negative int (Kotlin parseUInt32le returns a signed Int)
        Assert.Equal(unchecked((int)0xFFFFFFFF), PacketPrimitives.ParseUInt32Le(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));
    }

    [Fact]
    public void ReadUInt32LeAsLong_is_unsigned()
    {
        Assert.Equal(0xFFFFFFFFL, PacketPrimitives.ReadUInt32LeAsLong(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));
    }

    [Fact]
    public void ReadUInt64Le_full_range()
    {
        Assert.Equal(
            unchecked((long)0x8877665544332211),
            PacketPrimitives.ReadUInt64Le(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 }));
    }

    [Fact]
    public void ToHex_uppercase_space_separated()
    {
        Assert.Equal("00 0F A1 FF", PacketPrimitives.ToHex(new byte[] { 0x00, 0x0F, 0xA1, 0xFF }));
    }

    [Fact]
    public void ReadVarUInt64_reads_past_32_bits()
    {
        // 실측 0x3641 감시자 카이라 최대 HP: 80 CA A4 A8 36 = 14,580,000,000 (ReadVarInt 는 32비트로 잘라 엉뚱한 값을 낸다).
        VarLongOutput r = PacketPrimitives.ReadVarUInt64(new byte[] { 0xAA, 0x80, 0xCA, 0xA4, 0xA8, 0x36 }, offset: 1);
        Assert.Equal(14_580_000_000L, r.Value);
        Assert.Equal(5, r.Length);
        Assert.NotEqual(14_580_000_000L, PacketPrimitives.ReadVarInt(new byte[] { 0x80, 0xCA, 0xA4, 0xA8, 0x36 }).Value);
    }

    [Fact]
    public void ReadVarUInt64_single_byte_zero_is_canonical()
    {
        Assert.Equal(new VarLongOutput(0, 1), PacketPrimitives.ReadVarUInt64(new byte[] { 0x00 }));
    }

    [Fact]
    public void ReadVarUInt64_rejects_a_non_canonical_trailing_zero_byte()
    {
        Assert.Equal(new VarLongOutput(-1, -1), PacketPrimitives.ReadVarUInt64(new byte[] { 0x80, 0x00 }));
    }

    [Fact]
    public void ReadVarUInt64_rejects_out_of_bounds_and_values_past_long_max()
    {
        Assert.Equal(-1, PacketPrimitives.ReadVarUInt64(new byte[] { 0x80, 0x80 }).Length);

        // long.MaxValue 는 받고, 2^63 은 거부한다.
        byte[] max = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F };
        Assert.Equal(new VarLongOutput(long.MaxValue, 9), PacketPrimitives.ReadVarUInt64(max));
        byte[] twoTo63 = { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 };
        Assert.Equal(-1, PacketPrimitives.ReadVarUInt64(twoTo63).Length);

        // 11바이트째까지 이어지는 varint 는 10바이트 상한에서 끊긴다.
        byte[] tooLong = Enumerable.Repeat((byte)0x80, 10).Append((byte)0x01).ToArray();
        Assert.Equal(-1, PacketPrimitives.ReadVarUInt64(tooLong).Length);
    }
}
