using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// Parity spec for the six party join-request handlers. Every frame here is byte-exact from a capture:
/// JoinRequest from 20260604-175315 (requester=94890, job=26→마도성, nickname="쿵해쫑", server=1001,
/// power=423359), Cancel/Admit/Refuse/InstanceStart/ExitParty from 20260912-235429.
///
/// <para>Admit used to be pinned to a SYNTHETIC 7-byte frame that encoded the wrong offset — the real
/// 0x970B is 49~61 bytes and carries the requester after [flag][slot]. That synthetic test is what kept
/// "카드가 수락해도 안 사라진다" locked in place, so the frames below are deliberately verbatim.</para>
/// </summary>
public class JoinRequestParsingTests
{
    private sealed class RecordingJoinSink : IJoinRequestSink
    {
        public readonly List<(int Requester, string Nickname, int JobCode, int Server, int Power, long ArrivedAt)> Requests = [];
        public readonly List<int> Removed = [];
        public readonly List<int> Admitted = [];
        public int Refused;
        public int Cleared;

        public void OnJoinRequest(int requester, string nickname, int jobCode, int server, int power, long arrivedAt)
            => Requests.Add((requester, nickname, jobCode, server, power, arrivedAt));
        public void OnJoinRequestRemove(int requester, bool admit)
        {
            Removed.Add(requester);
            if (admit) Admitted.Add(requester);
        }
        public void OnRefuseJoinRequest() => Refused++;
        public void OnExitPartyUi() => Cleared++;
    }

    private sealed class CountingSink : IStreamProcessorSink
    {
        public int ParserErrors;
        public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) { }
        public void UnknownOpcode(int opcode, bool extraFlag, int len) { }
        public void CompressedPacket(int len, bool extraFlag) { }
        public void ParserError(string stage, string reason) => ParserErrors++;
        public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode) { }
        public void Meta(string type, params (string Key, object? Value)[] fields) { }
        public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
    }

    // Corpus-verified first JoinRequest frame (post-assembler, extraFlag=false). 59 bytes.
    private static readonly byte[] GoldenJoinRequest =
    [
        0x3e, 0x07, 0x97, 0x1e, 0x23, 0x02, 0x00, 0xaa, 0x72, 0x01, 0x00, 0x00, 0x00, 0xe9,
        0x03, 0x1a, 0x00, 0x00, 0x00, 0x2d, 0x00, 0x00, 0x00, 0xec, 0x0f, 0x00, 0x00, 0x09,
        0xec, 0xbf, 0xb5, 0xed, 0x95, 0xb4, 0xec, 0xab, 0x91, 0xe9, 0x03, 0x00, 0x00, 0x00,
        0x00, 0xbf, 0x75, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x6d, 0x95, 0xd8, 0x91, 0x9e,
        0x01, 0x00, 0x00,
    ];

    private static (RecordingJoinSink Join, CountingSink Diag, StreamProcessor Proc) NewProcessor()
    {
        var join = new RecordingJoinSink();
        var diag = new CountingSink();
        return (join, diag, new StreamProcessor(diag, null, join));
    }

    [Fact]
    public void ParseJoinRequest_extracts_all_fields()
    {
        var (join, _, proc) = NewProcessor();
        proc.OnPacketReceived(GoldenJoinRequest, 1717_000_000);

        Assert.Single(join.Requests);
        var r = join.Requests[0];
        Assert.Equal(94890, r.Requester);
        Assert.Equal("쿵해쫑", r.Nickname);
        Assert.Equal(26, r.JobCode);   // JobClass.ConvertFromCode(26) == SORCERER (마도성)
        Assert.Equal(1001, r.Server);
        Assert.Equal(423359, r.Power);
        Assert.Equal(1717_000_000, r.ArrivedAt);
    }

    // 실 취소 프레임(20260912-235429, +2404.208s): uid 13227 / 서버 2003. 같은 세션의 0x9707 신청과 일치.
    private static readonly byte[] GoldenCancel =
    [
        0x0e, 0x25, 0x97, 0xab, 0x33, 0x00, 0x00, 0x00, 0x00, 0xd3, 0x07,
    ];

    // 실 수락 프레임(같은 세션, +726.109s): [flag 0x0C][slot 0x04][uid 109885][00 00][서버 1001][len 9]"릴리아".
    private static readonly byte[] GoldenAdmit =
    [
        0x37, 0x0b, 0x97, 0x0c, 0x04, 0x3d, 0xad, 0x01, 0x00, 0x00, 0x00, 0xe9, 0x03, 0x09,
        0xeb, 0xa6, 0xb4, 0xeb, 0xa6, 0xac, 0xec, 0x95, 0x84, 0x12, 0x00, 0x00, 0x00,
    ];

    [Fact]
    public void ParseCancelJoin_reads_the_requester_right_after_the_opcode()
    {
        var (join, _, proc) = NewProcessor();
        proc.OnPacketReceived(GoldenCancel, 0);
        Assert.Equal([13227], join.Removed);
        Assert.Empty(join.Admitted); // 취소는 0x9709 를 동반하지 않는다
    }

    [Fact]
    public void ParseAdmitJoin_reads_the_requester_after_the_flag_and_slot()
    {
        var (join, _, proc) = NewProcessor();
        proc.OnPacketReceived(GoldenAdmit, 0);
        Assert.Equal([109885], join.Removed);
        Assert.Equal([109885], join.Admitted);
    }

    [Fact]
    public void ParseAdmitJoin_accepts_the_member_enumeration_variant_too()
    {
        // 같은 opcode 로 이미 파티에 있는 멤버를 같은 ms 에 2~3발씩 다시 싣는다(flag 0x0E). 실을 열거하지
        // 않는 이유: 2026-06 코퍼스에는 flag 0x3A 도 있었다. 대기 카드가 없는 uid 라 제거는 무해한 no-op.
        var (join, _, proc) = NewProcessor();
        proc.OnPacketReceived(
            [0x34, 0x0b, 0x97, 0x0e, 0x02, 0x14, 0x04, 0x00, 0x00, 0x00, 0x00, 0xd3, 0x07, 0x06,
             0xeb, 0xaa, 0xbd, 0xeb, 0xaa, 0xbd, 0x20, 0x00, 0x00, 0x00], 0);
        Assert.Equal([1044], join.Removed);
    }

    // ⚠️ 아래 노이즈 프레임은 전부 **디스패처를 통과해 핸들러까지 도달하는** 모양이어야 의미가 있다.
    // OnPacketReceived 는 opcodeKey 를 `packet[1] | packet[2]<<8` 로 잡으므로(extraFlag 는 packet[1] 이
    // 0xF0~0xFE 일 때만), `0e 58 09 97 …` 같은 프레임은 0x0958 로 읽혀 UnknownOpcode 에서 끝난다 — 게이트를
    // 통째로 지워도 통과하는 vacuous 테스트가 된다. 처음 넣었던 11행 중 6행이 실제로 그랬다.
    [Theory]
    // 코퍼스 8세션에서 실제로 디스패치된 비게임 LAN 스트림의 난수 프레임(길이 11 = uid+pad+server 에 못 미침).
    [InlineData(new byte[] { 0x0e, 0x0b, 0x97, 0x66, 0xd7, 0x8e, 0x51, 0x4e, 0x25, 0xe7, 0xe2 })]
    [InlineData(new byte[] { 0x0e, 0x0b, 0x97, 0x85, 0xdd, 0x69, 0x7c, 0xb1, 0x25, 0x2e, 0xfb })]
    // 길이는 통과하지만 uid 가 음수이거나 그 뒤가 00 00 이 아닌 것들 — uid·패드 검사가 잡는다
    // (실측 2026-08-07 / 09-12).
    [InlineData(new byte[] { 0x11, 0x25, 0x97, 0xe6, 0xd5, 0x45, 0xb3, 0x92, 0x67, 0x91, 0x4f, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x31, 0x25, 0x97, 0xb1, 0xe5, 0x95, 0x1e, 0x16, 0x39, 0x6e, 0xb6, 0xc2, 0xb5, 0x09, 0x92, 0x68, 0xec, 0x63,
                             0x04, 0xdb, 0xc6, 0x1e, 0xc6, 0xa7, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                             0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    public void Noise_frames_shaped_like_a_remove_are_rejected(byte[] frame)
    {
        var (join, _, proc) = NewProcessor();
        proc.OnPacketReceived(frame, 0);
        Assert.Empty(join.Removed);
    }

    [Theory]
    // 진짜 0x9709/0x9718/0x971D 는 예외 없이 5바이트다. 이 길이들은 코퍼스 8세션에서 노이즈로만 관측됐다.
    [InlineData(new byte[] { 0x0e, 0x09, 0x97, 0x06, 0xff, 0x73, 0xc2, 0x42, 0xbb, 0x38, 0x2f })]
    [InlineData(new byte[] { 0x0e, 0x09, 0x97, 0xc1, 0x3a, 0x6a, 0x6d, 0x08, 0x03, 0x81, 0xa3 })]
    public void Noise_frames_shaped_like_a_resolve_are_rejected(byte[] frame)
    {
        var (join, _, proc) = NewProcessor();
        proc.OnPacketReceived(frame, 0);
        Assert.Equal(0, join.Refused);
    }

    [Theory]
    [InlineData(new byte[] { 0x0a, 0x18, 0x97, 0x79, 0x0c, 0x84, 0x6b })]
    [InlineData(new byte[] { 0x0e, 0x18, 0x97, 0x93, 0xd6, 0xff, 0x76, 0x47, 0xd7, 0x61, 0x04 })]
    [InlineData(new byte[] { 0x0e, 0x1d, 0x97, 0x15, 0x50, 0x0c, 0x20, 0x32, 0x8f, 0xe4, 0x7e })]
    [InlineData(new byte[] { 0x0e, 0x1d, 0x97, 0x4e, 0x10, 0x56, 0x93, 0x47, 0xb1, 0xc1, 0x71 })]
    public void Noise_frames_shaped_like_a_clear_are_rejected(byte[] frame)
    {
        var (join, _, proc) = NewProcessor();
        proc.OnPacketReceived(frame, 0);
        Assert.Equal(0, join.Cleared);
    }

    [Fact]
    public void A_remove_from_a_server_newer_than_the_shipped_list_is_still_honored()
    {
        // 서버 화이트리스트를 제거 경로에 그대로 쓰면 증설이 곧 버그 배포다 — 신규 서버 유저의 신청은
        // 카드로 뜨는데(추가 경로엔 서버 검사가 없다) 수락해도 안 지워진다. 상한을 열어 둔 이유.
        var (join, _, proc) = NewProcessor();
        proc.OnPacketReceived(
            [0x37, 0x0b, 0x97, 0x0c, 0x04, 0x3d, 0xad, 0x01, 0x00, 0x00, 0x00, 0x06, 0x04, 0x09, // 서버 1030 = 현행 목록 밖
             0xeb, 0xa6, 0xb4, 0xeb, 0xa6, 0xac, 0xec, 0x95, 0x84, 0x12, 0x00, 0x00, 0x00], 0);
        Assert.Equal([109885], join.Removed);
    }

    [Fact]
    public void ParseRefuseJoin_emits_refuse()
    {
        var (join, _, proc) = NewProcessor();
        proc.OnPacketReceived([0x08, 0x09, 0x97, 0x00, 0x00], 0); // real corpus shape
        Assert.Equal(1, join.Refused);
    }

    [Fact]
    public void ParseRefuseJoin_accepts_the_non_zero_trailing_byte_variant()
    {
        // 실 프레임 17건 중 1건이 08 09 97 00 3C 였다 — 바이트 동등 게이트였다면 놓쳤을 것이다.
        var (join, _, proc) = NewProcessor();
        proc.OnPacketReceived([0x08, 0x09, 0x97, 0x00, 0x3c], 0);
        Assert.Equal(1, join.Refused);
    }

    [Fact]
    public void ParseInstanceStart_clears_all()
    {
        var (join, _, proc) = NewProcessor();
        proc.OnPacketReceived([0x08, 0x18, 0x97, 0x00, 0x00], 0);
        Assert.Equal(1, join.Cleared);
    }

    [Fact]
    public void ParseExitParty_clears_all()
    {
        var (join, _, proc) = NewProcessor();
        proc.OnPacketReceived([0x08, 0x1D, 0x97, 0x00, 0x00], 0);
        Assert.Equal(1, join.Cleared);
    }

    [Fact]
    public void Garbage_nickname_join_request_is_rejected()
    {
        var (join, diag, proc) = NewProcessor();
        // The golden frame with its 9 name bytes (indices 28..36, "쿵해쫑") overwritten with invalid UTF-8
        // (0xFF is never a legal UTF-8 byte). This is the phantom "party 신청" with a mojibake name a
        // mis-assembled 0x9707 frame produced while idle — it must be dropped, not shown as an applicant.
        byte[] garbage = (byte[])GoldenJoinRequest.Clone();
        for (int i = 28; i <= 36; i++) garbage[i] = 0xFF;

        proc.OnPacketReceived(garbage, 1717_000_000);

        Assert.Empty(join.Requests);        // no phantom card
        Assert.Equal(1, diag.ParserErrors); // rejected at the trust boundary
    }

    [Fact]
    public void Truncated_join_request_is_swallowed_not_thrown()
    {
        var (join, diag, proc) = NewProcessor();
        // Golden frame cut to 43 bytes — the power u32 read runs off the end.
        byte[] truncated = GoldenJoinRequest[..43];

        Exception? ex = Record.Exception(() => proc.OnPacketReceived(truncated, 0));

        Assert.Null(ex);                  // must not crash the consumer thread
        Assert.Empty(join.Requests);      // no partial request emitted
        Assert.Equal(1, diag.ParserErrors);
    }
}
