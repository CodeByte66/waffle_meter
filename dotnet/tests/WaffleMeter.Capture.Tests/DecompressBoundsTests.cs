using K4os.Compression.LZ4;
using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// Regression spec for the LZ4 bundle header bounds in <c>StreamProcessor.DecompressPacket</c>.
///
/// <para>The originLength field is a raw wire u32 that used to be fed straight into
/// <c>new byte[originLength]</c>. One corrupted frame carrying 0xFFFFFFFF / 0x7FFFFFFF therefore tried to
/// allocate gigabytes and stalled the single consumer thread for seconds — and the resulting exception was
/// swallowed by the surrounding catch, so it left no trace at all.</para>
///
/// <para>The bound is measured, not guessed: across the two repository packet logs all 797 compressed frames
/// decoded, the largest originLength being 65,414 bytes (2026-07-27). The cap is 1 MiB = 16x that, and the
/// round-trip test below pins that a bundle at the measured maximum still passes.</para>
/// </summary>
public class DecompressBoundsTests
{
    private sealed class RecordingSink : IStreamProcessorSink
    {
        public readonly List<int> Dispatched = [];
        public readonly List<(string Stage, string Reason)> Errors = [];
        public int Compressed;

        public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) => Dispatched.Add(opcode);
        public void CompressedPacket(int len, bool extraFlag) => Compressed++;
        public void ParserError(string stage, string reason) => Errors.Add((stage, reason));
        public void UnknownOpcode(int opcode, bool extraFlag, int len) { }
        public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode) { }
        public void Meta(string type, params (string Key, object? Value)[] fields) { }
        public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
    }

    private const int DamageKey = 0x04 | (0x38 << 8); // 0x3804

    // Must match StreamProcessor.MaxLz4OriginLength. Hardcoded on purpose: if the constant is loosened back
    // toward "whatever the wire says", these expectations break loudly instead of silently.
    private const int MaxLz4OriginLength = 1 << 20;

    /// <summary>One inner frame: realLength = lengthVarintValue + 1 - 4, so value = realLength + 3.</summary>
    private static byte[] InnerFrame(int realLength)
    {
        var f = new byte[realLength];
        f[0] = (byte)(realLength + 3);
        f[1] = 0x04;
        f[2] = 0x38; // 0x3804
        return f;
    }

    /// <summary>[varint len=1][FF FF][originLength u32 LE][lz4 block]</summary>
    private static byte[] Bundle(byte[] restored, int declaredOriginLength)
    {
        var compressed = new byte[LZ4Codec.MaximumOutputSize(Math.Max(restored.Length, 1))];
        int clen = LZ4Codec.Encode(restored, 0, restored.Length, compressed, 0, compressed.Length);
        Assert.True(clen > 0);

        var outer = new List<byte> { 0x01, 0xFF, 0xFF };
        outer.Add((byte)(declaredOriginLength & 0xFF));
        outer.Add((byte)((declaredOriginLength >> 8) & 0xFF));
        outer.Add((byte)((declaredOriginLength >> 16) & 0xFF));
        outer.Add((byte)((declaredOriginLength >> 24) & 0xFF));
        outer.AddRange(compressed[..clen]);
        return [.. outer];
    }

    private static RecordingSink Feed(byte[] packet)
    {
        var sink = new RecordingSink();
        new StreamProcessor(sink).OnPacketReceived(packet, 0);
        return sink;
    }

    [Theory]
    [InlineData(unchecked((int)0xFFFFFFFF))] // -1 once ParseUInt32Le hands back a SIGNED int
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(MaxLz4OriginLength + 1)]
    [InlineData(int.MaxValue)]               // the 2.1GB allocation that froze the consumer thread
    public void Implausible_origin_length_is_rejected_without_allocating(int declaredOriginLength)
    {
        byte[] restored = InnerFrame(8);

        RecordingSink sink = Feed(Bundle(restored, declaredOriginLength));

        Assert.Equal(1, sink.Compressed);                          // the FF FF branch WAS taken...
        Assert.Empty(sink.Dispatched);                             // ...and nothing came out of it
        Assert.Equal(("decompress", "bad_origin_length"), Assert.Single(sink.Errors));
    }

    [Fact]
    public void Truncated_origin_length_field_is_rejected()
    {
        // [varint len=1][FF FF] and then only 3 of the 4 originLength bytes.
        byte[] packet = [0x01, 0xFF, 0xFF, 0x10, 0x00, 0x00];

        RecordingSink sink = Feed(packet);

        Assert.Equal(1, sink.Compressed);
        Assert.Equal(("decompress", "truncated_origin_length"), Assert.Single(sink.Errors));
    }

    [Fact]
    public void Partial_decode_is_rejected_instead_of_feeding_the_zero_tail_to_the_inner_loop()
    {
        // originLength overstates what the block actually yields. Before the return-value check the parser
        // kept the half-filled buffer and walked its trailing zeros: the length varint reads 0, so the inner
        // loop crawls one byte at a time over the padding and can frame garbage as a real packet.
        byte[] restored = InnerFrame(16);

        RecordingSink sink = Feed(Bundle(restored, restored.Length + 512));

        Assert.Equal(1, sink.Compressed);
        Assert.Empty(sink.Dispatched);
        Assert.Equal(("decompress", "lz4_short_decode"), Assert.Single(sink.Errors));
    }

    [Fact]
    public void Bundle_at_the_measured_real_world_maximum_still_round_trips()
    {
        // 65,414 B is the largest originLength in the corpus (2026-07-27, 427 compressed frames). The bound
        // must never be tightened below real traffic — that would silently drop 40-73% of damage packets,
        // which is the share that rides inside compressed frames.
        const int MeasuredMax = 65_414;
        const int InnerSize = 32;
        int expectedInner = MeasuredMax / InnerSize;

        var restored = new List<byte>();
        for (int i = 0; i < expectedInner; i++)
        {
            restored.AddRange(InnerFrame(InnerSize));
        }

        while (restored.Count < MeasuredMax)
        {
            restored.Add(0x00); // the inner loop skips zero-length framing bytes
        }

        RecordingSink sink = Feed(Bundle([.. restored], restored.Count));

        Assert.Equal(MeasuredMax, restored.Count);
        Assert.Equal(1, sink.Compressed);
        Assert.Empty(sink.Errors);
        Assert.Equal(expectedInner, sink.Dispatched.Count);
        Assert.All(sink.Dispatched, op => Assert.Equal(DamageKey, op));
    }
}
