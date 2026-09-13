using System.Text;
using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// Spec for the 0x3633 본인 로드 파서의 fail-closed 게이트. 이 파서는 미터에서 <b>유일하게 executor 포인터를
/// 직접 갈아치우는</b> 경로다(<c>SaveNickname(isExecutor: true)</c> → <c>SaveExecutorId</c>), 그런데 2026-07-30
/// 이전에는 uid·서버·닉네임을 하나도 검증하지 않는 유일한 신원 파서이기도 했다.
/// <para>캡처는 방향 제한이 없어 클라→서버 아웃바운드(암호문 = 사실상 난수)와 루프백까지 같은 파이프라인에
/// 들어온다. 길이 varint는 평문이라 난수 페이로드도 정상 프레이밍되고, 프레임 뒤 2바이트가 우연히
/// <c>33 36</c>이면 그대로 이 파서로 온다. 실제로 그렇게 들어온 프레임이 본인을 <c>"Q"</c>(실측 코퍼스)와
/// <c>"I"</c>/server 47200(2026-07-30 사고)로 둔갑시켜, 파티 로스터·오드·버프 초기화 + 통계 동의 모달 재출현 +
/// 업로드 차단까지 갔다.</para>
/// <para>아래 음성 케이스가 이 스위트의 요점이다. 동시에 <see cref="LegitimateOwnLoad_IsStillAccepted"/>와
/// <see cref="SingleKoreanCharacterNickname_IsStillAccepted"/>가 게이트를 과하게 조이지 않았음을 고정한다 —
/// 2026-07-01 패치가 레이아웃을 바꿔 본인 인식을 통째로 깨뜨린 전례가 있어, 여기서 과잉 차단은 즉시
/// "내 캐릭터를 인식 못함"으로 돌아온다.</para>
/// </summary>
public sealed class OwnNicknameValidationTests
{
    private sealed class RecordingData : ICaptureGameData
    {
        public readonly List<(int Uid, string Nickname, bool IsExecutor, int Server, int JobByte)> Saved = [];

        public void SaveNickname(int uid, string nickname, bool isExecutor, int server, int jobByte) =>
            Saved.Add((uid, nickname, isExecutor, server, jobByte));

        public Mob? GetMob(int code) => null;
        public int? GetMobId(int instanceId) => null;
        public void SaveMobId(int instanceId, int mobCode) { }
        public bool SkillExists(long code) => false;
        public long CurrentEpoch() => 0;
        public void SaveDamage(ParsedDamagePacket pdp, long epoch) { }
        public void StartBattle(int target) { }
        public void EndBattle(int target) { }
        public void SaveUserPower(int uid, int power) { }
        public void SaveSummon(int summonId, int ownerId) { }
        public void SaveMobHp(int instanceId, long hp) { }
        public void SaveUseBuff(int uid, int skillCode, long buffStart, long buffEnd, long duration, int actorId) { }
        public void RequestOfficialCharacterLookup(int uid) { }
        public void SavePartyRoster(IReadOnlyList<(string Nickname, int Server, int Slot)> members) { }
        public void SaveAetherStatus(int baseVal, int bonus) { }
        public void SaveShugoKey(int baseVal, int bonus) { }
        public void SaveFieldBossTimers(IReadOnlyList<(int Code, long TargetMs)> timers) { }
    }

    private sealed class RecordingSink : IStreamProcessorSink
    {
        public readonly List<string> Errors = [];

        public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) { }
        public void UnknownOpcode(int opcode, bool extraFlag, int len) { }
        public void CompressedPacket(int len, bool extraFlag) { }
        public void ParserError(string stage, string reason) => Errors.Add($"{stage}:{reason}");
        public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode) { }
        public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
        public void Meta(string type, params (string Key, object? Value)[] fields) { }
    }

    private static (RecordingData Data, RecordingSink Sink) Feed(byte[] frame)
    {
        var data = new RecordingData();
        var sink = new RecordingSink();
        new StreamProcessor(sink, data).OnPacketReceived(frame, 0);
        return (data, sink);
    }

    private static void WriteVarInt(List<byte> to, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            to.Add((byte)(v | 0x80));
            v >>= 7;
        }

        to.Add((byte)v);
    }

    /// <summary>본인 로드 프레임을 실측 레이아웃대로 조립한다:
    /// <c>[len varint][0x33][0x36][uid varint][5B 고정 프리픽스][nameLen varint][name UTF-8][server u16 LE][job u8]</c>.
    /// 프리픽스 바이트는 0x60(=96)으로 채운다 — probe 루프가 varint로 읽었을 때 96 &gt; 71이라 이름 후보로
    /// 오인되지 않으므로, 2026-07-01 패치 이후의 "0x07 스플리터 없는" 레이아웃을 그대로 흉내낸다.</summary>
    private static byte[] OwnLoadFrame(int uid, string nickname, int server, int job, int prefixBytes = 5)
    {
        var body = new List<byte> { 0x33, 0x36 };
        WriteVarInt(body, uid);
        for (int i = 0; i < prefixBytes; i++)
        {
            body.Add(0x60);
        }

        byte[] name = Encoding.UTF8.GetBytes(nickname);
        WriteVarInt(body, name.Length);
        body.AddRange(name);
        body.Add((byte)(server & 0xFF));
        body.Add((byte)((server >> 8) & 0xFF));
        body.Add((byte)job);

        // realLength = lengthValue + lengthLength - 4 (StreamAssembler), so a 1-byte length varint
        // carrying (bodyLength + 3) frames a body of exactly bodyLength.
        var frame = new List<byte>();
        WriteVarInt(frame, body.Count + 3);
        frame.AddRange(body);
        return frame.ToArray();
    }

    /// <summary>2026-07-28 코퍼스에서 실제로 관측된 아웃바운드 프레임. 수정 전에는
    /// <c>uid=106900 / nickname="Q" / server=-1</c>로 파싱돼 본인으로 등록됐다. 이 프레임이 다시 통과하면
    /// 같은 사고가 재발한다.</summary>
    [Fact]
    public void RealCapturedGarbageFrame_IsRejected()
    {
        byte[] frame = Convert.FromHexString("0E333694C306D0600151" + "0C");

        (RecordingData data, _) = Feed(frame);

        Assert.Empty(data.Saved);
    }

    /// <summary>2026-07-30 사고의 재구성: 위 프레임에 2바이트(<c>60 B8</c>)만 더 붙으면 server 자리까지 채워져
    /// <c>nickname="I" / server=47200</c>이 본인으로 등록된다. 그 (47200, "I") 조합이 통계 신원 해시
    /// <c>d044f870…</c>를 만들어 동의 모달을 다시 띄우고 업로드를 막았다.</summary>
    [Fact]
    public void ReconstructedIncidentFrame_IsRejected()
    {
        byte[] frame = Convert.FromHexString("10333694C306D06001" + "49" + "60B8" + "0C");

        (RecordingData data, _) = Feed(frame);

        Assert.Empty(data.Saved);
    }

    [Fact]
    public void LegitimateOwnLoad_IsStillAccepted()
    {
        byte[] frame = OwnLoadFrame(uid: 9549, nickname: "콘팡", server: 2003, job: 16);

        (RecordingData data, RecordingSink sink) = Feed(frame);

        (int uid, string nickname, bool isExecutor, int server, int jobByte) = Assert.Single(data.Saved);
        Assert.Equal(9549, uid);
        Assert.Equal("콘팡", nickname);
        Assert.True(isExecutor);
        Assert.Equal(2003, server);
        Assert.Equal(16, jobByte);
        Assert.Empty(sink.Errors);
    }

    /// <summary>코퍼스 실측 최대 정상 본인 uid는 15510이다. 상한(16383) 바로 아래는 통과해야 한다.</summary>
    [Fact]
    public void UidJustBelowTheCap_IsAccepted()
    {
        byte[] frame = OwnLoadFrame(uid: 16383, nickname: "플러시", server: 2003, job: 32);

        Assert.Single(Feed(frame).Data.Saved);
    }

    /// <summary>오프셋을 잘못 잡은 varint는 예외 없이 엔티티 id 공간을 벗어난다(실측 106900).</summary>
    [Fact]
    public void UidAboveTheCap_IsRejected()
    {
        byte[] frame = OwnLoadFrame(uid: 106900, nickname: "콘팡", server: 2003, job: 16);

        Assert.Empty(Feed(frame).Data.Saved);
    }

    /// <summary>길이 1 = 정의상 ASCII 한 글자. 실측 오탐("I", "Q")이 전부 이 형태였다.</summary>
    [Fact]
    public void SingleAsciiCharacterNickname_IsRejected()
    {
        byte[] frame = OwnLoadFrame(uid: 9549, nickname: "I", server: 2003, job: 16);

        Assert.Empty(Feed(frame).Data.Saved);
    }

    /// <summary>과잉 차단 방지: 1글자 한글 닉은 UTF-8 3바이트라 길이 하한에 걸리지 않아야 한다.</summary>
    [Fact]
    public void SingleKoreanCharacterNickname_IsStillAccepted()
    {
        byte[] frame = OwnLoadFrame(uid: 9549, nickname: "쭌", server: 2003, job: 16);

        (int _, string nickname, bool _, int server, int _) = Assert.Single(Feed(frame).Data.Saved);
        Assert.Equal("쭌", nickname);
        Assert.Equal(2003, server);
    }

    /// <summary>사고의 server 값. 이름 뒤 2바이트를 무검증으로 읽던 것이 린치핀이었다.</summary>
    [Theory]
    [InlineData(47200)]  // 0xB860 — 2026-07-30 사고의 실제 값
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1022)]
    [InlineData(2022)]
    [InlineData(65535)]
    public void ServerOutsideTheKnownRanges_IsRejected(int server)
    {
        byte[] frame = OwnLoadFrame(uid: 9549, nickname: "콘팡", server: server, job: 16);

        Assert.Empty(Feed(frame).Data.Saved);
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(1021)]
    [InlineData(2001)]
    [InlineData(2003)]
    [InlineData(2021)]
    public void ServerInsideTheKnownRanges_IsAccepted(int server)
    {
        byte[] frame = OwnLoadFrame(uid: 9549, nickname: "콘팡", server: server, job: 16);

        Assert.Single(Feed(frame).Data.Saved);
    }

    /// <summary>서버가 필드를 옮기면(2026-07-01 패치 전례) 이 게이트가 조용히 본인 인식을 막게 된다. 그때
    /// "이름은 찾았는데 그 뒤가 유효 서버가 아니다"라는 흔적이 남아야 다음 패치에서 즉시 보인다.</summary>
    [Fact]
    public void NameFoundButNoValidServer_LeavesABreadcrumb()
    {
        byte[] frame = OwnLoadFrame(uid: 9549, nickname: "콘팡", server: 47200, job: 16);

        (RecordingData data, RecordingSink sink) = Feed(frame);

        Assert.Empty(data.Saved);
        Assert.Contains(sink.Errors, e => e.StartsWith("own_nickname:", StringComparison.Ordinal));
    }

    /// <summary>이름 뒤가 잘려 서버를 읽을 수 없는 프레임. 종전에는 server=-1로 본인 등록이 됐고, 그 경로가
    /// 실측 오탐("Q", server=-1)이 executor를 차지한 통로였다. 코퍼스 6종 실측에서 정상 본인 파스 125건은
    /// 전부 유효 서버를 실어 왔으므로(server=-1 정상 사례 0건) 여기서 잃는 정상 케이스는 없다.</summary>
    [Fact]
    public void FrameTruncatedBeforeTheServerField_IsRejected()
    {
        byte[] full = OwnLoadFrame(uid: 9549, nickname: "콘팡", server: 2003, job: 16);
        byte[] truncated = full[..^3]; // server u16 + job 제거

        Assert.Empty(Feed(truncated).Data.Saved);
    }
}
