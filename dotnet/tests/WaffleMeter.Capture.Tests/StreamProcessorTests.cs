using K4os.Compression.LZ4;
using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// Parity spec for the L3a dispatch + decompression core of <see cref="StreamProcessor"/>.
/// Covers opcode routing (known/unknown), the 3-byte skip, and a compressed-bundle round trip
/// (K4os encode -> our decode -> inner re-framing -> dispatch).
/// </summary>
public class StreamProcessorTests
{
    private sealed class RecordingSink : IStreamProcessorSink
    {
        public readonly List<int> Dispatched = [];
        public int Unknown;
        public int Compressed;
        public int ParserErrors;

        public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) => Dispatched.Add(opcode);
        public void UnknownOpcode(int opcode, bool extraFlag, int len) => Unknown++;
        public void CompressedPacket(int len, bool extraFlag) => Compressed++;
        public void ParserError(string stage, string reason) => ParserErrors++;
        public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode) { }
        public void Meta(string type, params (string Key, object? Value)[] fields) { }
        public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
    }

    private const int DamageKey = 0x04 | (0x38 << 8); // 0x3804 = "Damage" (known)
    private const int DoTKey = 0x05 | (0x38 << 8);    // 0x3805 = "DoT" (known)
    private const int UnknownKey = 0x03 | (0x36 << 8); // 0x3603 (seen as unknown in real corpus)
    private const int PartyRosterKey = 0x02 | (0x97 << 8); // 0x9702 — replayed even on a suppressed stream

    [Fact]
    public void Skips_three_byte_packet()
    {
        var sink = new RecordingSink();
        new StreamProcessor(sink).OnPacketReceived(new byte[] { 0x06, 0x00, 0x36 }, 0);
        Assert.Empty(sink.Dispatched);
        Assert.Equal(0, sink.Unknown);
    }

    [Fact]
    public void Routes_known_opcode_without_marking_unknown()
    {
        var sink = new RecordingSink();
        // [varint len=1][0x04][0x38][..] -> opcode 0x3804
        new StreamProcessor(sink).OnPacketReceived(new byte[] { 0x01, 0x04, 0x38, 0x00, 0x00 }, 0);
        Assert.Equal(new[] { DamageKey }, sink.Dispatched);
        Assert.Equal(0, sink.Unknown);
    }

    [Fact]
    public void Routes_unknown_opcode_and_flags_it()
    {
        var sink = new RecordingSink();
        new StreamProcessor(sink).OnPacketReceived(new byte[] { 0x01, 0x03, 0x36, 0x00, 0x00 }, 0);
        Assert.Equal(new[] { UnknownKey }, sink.Dispatched);
        Assert.Equal(1, sink.Unknown);
    }

    [Fact]
    public void Decompresses_ff_ff_bundle_and_dispatches_inner_packets()
    {
        // Two inner frames, each: [varint len][0x04][0x38][marker...] with realLength = value + 1 - 4.
        // For a 1-byte varint (length 1): value = realLength + 3.
        static byte[] InnerFrame(int realLength)
        {
            var f = new byte[realLength];
            f[0] = (byte)(realLength + 3); // varint value so realLength = value + 1 - 4
            f[1] = 0x04;                   // opcode low
            f[2] = 0x38;                   // opcode high  -> 0x3804
            return f;
        }

        byte[] restored = [.. InnerFrame(5), .. InnerFrame(6)];

        // LZ4 BLOCK encode the inner stream (what the server would have compressed).
        var compressed = new byte[LZ4Codec.MaximumOutputSize(restored.Length)];
        int clen = LZ4Codec.Encode(restored, 0, restored.Length, compressed, 0, compressed.Length);
        Assert.True(clen > 0);

        // Outer packet: [varint len=0x01][FF FF][originLength u32 LE][compressed...]
        var outer = new List<byte> { 0x01, 0xFF, 0xFF };
        int n = restored.Length;
        outer.Add((byte)(n & 0xFF));
        outer.Add((byte)((n >> 8) & 0xFF));
        outer.Add((byte)((n >> 16) & 0xFF));
        outer.Add((byte)((n >> 24) & 0xFF));
        outer.AddRange(compressed[..clen]);

        var sink = new RecordingSink();
        new StreamProcessor(sink).OnPacketReceived(outer.ToArray(), 0);

        Assert.Equal(1, sink.Compressed);
        Assert.Equal(new[] { DamageKey, DamageKey }, sink.Dispatched); // both inner frames dispatched
        Assert.Equal(0, sink.Unknown);
        Assert.Equal(0, sink.ParserErrors);
    }

    [Fact]
    public void Identity_only_applies_inside_a_compressed_bundle_too()
    {
        // The dup-suppressed second game stream runs with identityOnly:true so ONLY idempotent identity/roster
        // opcodes are replayed and the single-game-stream damage lock holds. But the FF FF branch is taken
        // BEFORE that gate, and it used to re-enter OnPacketReceived without the flag — so everything inside a
        // compressed bundle was processed in full. Measured on four real corpora: 40-73% of damage packets ride
        // inside compressed frames, so a VPN dual-capture user got that share of their damage counted twice.
        static byte[] InnerFrame(int realLength, byte opcodeLow, byte opcodeHigh)
        {
            var f = new byte[realLength];
            f[0] = (byte)(realLength + 3);
            f[1] = opcodeLow;
            f[2] = opcodeHigh;
            return f;
        }

        // One damage frame (must be dropped) + one party-roster frame (must survive — that is the whole point
        // of the identity replay: the roster often rides the suppressed connection).
        byte[] restored = [.. InnerFrame(5, 0x04, 0x38), .. InnerFrame(6, 0x02, 0x97)];

        var compressed = new byte[LZ4Codec.MaximumOutputSize(restored.Length)];
        int clen = LZ4Codec.Encode(restored, 0, restored.Length, compressed, 0, compressed.Length);
        var outer = new List<byte> { 0x01, 0xFF, 0xFF };
        int n = restored.Length;
        outer.Add((byte)(n & 0xFF));
        outer.Add((byte)((n >> 8) & 0xFF));
        outer.Add((byte)((n >> 16) & 0xFF));
        outer.Add((byte)((n >> 24) & 0xFF));
        outer.AddRange(compressed[..clen]);
        byte[] bundle = outer.ToArray();

        var full = new RecordingSink();
        new StreamProcessor(full).OnPacketReceived(bundle, 0);
        Assert.Equal(new[] { DamageKey, PartyRosterKey }, full.Dispatched); // normal stream: both

        var identity = new RecordingSink();
        new StreamProcessor(identity).OnPacketReceived(bundle, 0, identityOnly: true);
        Assert.Equal(1, identity.Compressed);                     // the bundle WAS opened...
        Assert.Equal(new[] { PartyRosterKey }, identity.Dispatched); // ...but only the roster came out
    }

    [Fact]
    public void Truncated_dot_packet_is_swallowed_not_thrown()
    {
        // Regression for the consumer-thread crash: content-based capture lets non-game / TCP-truncated
        // data frame as a known opcode. A DoT packet ending before its 4-byte skill code made
        // ParseDoTPacket -> ParseUInt32Le throw ("패킷 길이가 필요길이보다 짧음"); that propagated out of
        // the opcode dispatch, through StreamAssembler -> MeterServices.Feed -> MeterEngine.ConsumeLoop,
        // and killed the single consumer thread (the whole app terminated). The dispatch must now
        // swallow it as a parser error instead of throwing.
        //
        // Layout: [varint len=6][0x05][0x38]=DoT [target=1][bitflag 0x02][actor=3][unk=4][skillcode truncated]
        byte[] truncated = { 0x06, 0x05, 0x38, 0x01, 0x02, 0x03, 0x04, 0x00 };

        var sink = new RecordingSink();
        var processor = new StreamProcessor(sink);

        Exception? ex = Record.Exception(() => processor.OnPacketReceived(truncated, 0));

        Assert.Null(ex);                            // must NOT throw (this is the crash)
        Assert.Contains(DoTKey, sink.Dispatched);   // it WAS routed to the DoT handler
        Assert.Equal(1, sink.ParserErrors);         // and the parse failure was recorded, not fatal
    }

    // 1016 실프레임 그대로: [0E][00 36][epoch ms LE] — primary 락 자격.
    private static readonly byte[] RealMapFrame = [0x0E, 0x00, 0x36, 0xE5, 0x43, 0x86, 0x40, 0xA1, 0x01, 0x00, 0x00];

    [Fact]
    public void IsServerClockFrame_accepts_real_mapframes()
    {
        Assert.True(StreamProcessor.IsServerClockFrame(RealMapFrame));
        Assert.True(StreamProcessor.IsServerClockFrame(
            [0x0F, 0xF0, 0x00, 0x36, 0xE5, 0x43, 0x86, 0x40, 0xA1, 0x01, 0x00, 0x00])); // extraFlag 12B
    }

    [Theory]
    [InlineData(new byte[] { 0x0E, 0x00, 0x36, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })] // epoch 0
    [InlineData(new byte[] { 0x0E, 0x00, 0x36, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40 })] // 2^62
    [InlineData(new byte[] { 0x0E, 0x00, 0x36, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x03, 0x00, 0x00 })] // 2100년 이후
    [InlineData(new byte[] { 0x0E, 0x00, 0x36, 0xE5, 0x43, 0x86, 0x40, 0xA1, 0x01, 0x00 })]       // 10B 절단
    [InlineData(new byte[] { 0x0F, 0x00, 0x36, 0xE5, 0x43, 0x86, 0x40, 0xA1, 0x01, 0x00, 0x00, 0x00 })] // 플래그 없는 12B
    [InlineData(new byte[] { 0x0E, 0x33, 0x36, 0xE5, 0x43, 0x86, 0x40, 0xA1, 0x01, 0x00, 0x00 })] // 0x3633
    [InlineData(new byte[] { 0x08, 0x04, 0x38, 0x00, 0x00 })]                                      // 0x3804
    public void IsServerClockFrame_rejects_lookalikes(byte[] frame) =>
        Assert.False(StreamProcessor.IsServerClockFrame(frame));

    [Fact]
    public void IsServerClockFrame_rejects_the_user_agent_hijack_frame()
    {
        byte[] ua = System.Text.Encoding.ASCII.GetBytes(".36 (KHTML, like Gecko) Chrome/141.0.0.0 Sa");
        Assert.True(StreamProcessor.LooksLikeGamePacket(ua)); // 이것이 10-16 하이재킹의 출발점이다
        Assert.False(StreamProcessor.IsServerClockFrame(ua));
    }

    [Fact]
    public void LooksLikeText_separates_plain_text_from_game_frames()
    {
        Assert.True(StreamProcessor.LooksLikeText(
            System.Text.Encoding.ASCII.GetBytes(".36 (KHTML, like Gecko) Chrome/141.0.0.0 Sa")));
        Assert.False(StreamProcessor.LooksLikeText(RealMapFrame));
        Assert.False(StreamProcessor.LooksLikeText([0x08, 0x04, 0x38, 0x00, 0x00])); // 8바이트 미만은 판단 안 함

        // 경계: 8바이트 중 출력 가능 6개(=3/4)는 텍스트, 5개는 아니다.
        Assert.True(StreamProcessor.LooksLikeText([0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x00, 0x00]));
        Assert.False(StreamProcessor.LooksLikeText([0x41, 0x41, 0x41, 0x41, 0x41, 0x00, 0x00, 0x00]));
    }
}
