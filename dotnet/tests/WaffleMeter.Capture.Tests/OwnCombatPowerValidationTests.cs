using System.Text;
using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// Spec for the 0x3656 본인 전투력 파서의 fail-closed 게이트.
/// <para>이 파서는 <b>본인(executor) 전투력을 직접 갈아치우는 유일한 패킷 경로</b>다. 종전에는 검증이
/// "u32 하나를 읽을 만큼 길다 + 값이 1..1000만" 뿐이었는데, 상한 1000만은 실측 최댓값(979,329)의 10배가
/// 넘어 사실상 게이트가 아니었다. 캡처는 방향 제한이 없어 아웃바운드 암호문·루프백·압축 번들 내부
/// 리싱크 오차까지 같은 파이프라인에 들어오고, 프레임 뒤 2바이트가 우연히 <c>56 36</c>이면 그대로 이
/// 파서로 온다 — 0x3633 본인 로드가 같은 형태의 구멍으로 신원을 탈취당한 전례가 있다
/// (<see cref="OwnNicknameValidationTests"/>).</para>
/// <para>실측 사고(2026-08-17 02:24:49 롭스티노): 본인 전투력이 356,559 대신 <b>2,285,1xx</b>로 앉아 그
/// 전투의 저장본에 얼어붙었고, 400k 미만이라 뜨면 안 될 티어 배지까지 띄웠다.
/// ⚠️ <b>그 프레임 자체는 코퍼스에 없다</b> — 패킷 로깅이 그 전투가 끝나고 28초 뒤(02:25:54)에 시작됐다.
/// 근거는 소거법이다: 재진입 직후부터 0x3656(134프레임)·0x9702 로스터(42스냅샷)·공식 사이트가 전부
/// 356,559 / 340,370을 말했고 11.5시간 동안 100만 초과 표본이 0건인데, executor의 <c>User.Power</c>에
/// 쓸 수 있는 경로는 이 파서뿐이다. 다음 사람이 "코퍼스에서 직접 봤다"로 오해하지 않도록 적어 둔다.</para>
/// <para>정상 케이스는 코퍼스에서 그대로 떠 온 실프레임이다 — 과잉 차단은 즉시 "내 전투력이 안 뜬다"로
/// 돌아오므로 여기서 고정한다.</para>
/// </summary>
public sealed class OwnCombatPowerValidationTests
{
    private sealed class RecordingData : ICaptureGameData
    {
        public readonly List<(int Uid, int Power)> Powers = [];
        public readonly List<(int Uid, string Nickname, bool IsExecutor)> Names = [];

        public void SaveUserPower(int uid, int power) => Powers.Add((uid, power));

        public void SaveNickname(int uid, string nickname, bool isExecutor, int server, int jobByte) =>
            Names.Add((uid, nickname, isExecutor));

        public Mob? GetMob(int code) => null;
        public int? GetMobId(int instanceId) => null;
        public void SaveMobId(int instanceId, int mobCode) { }
        public bool SkillExists(long code) => false;
        public long CurrentEpoch() => 0;
        public void SaveDamage(ParsedDamagePacket pdp, long epoch) { }
        public void StartBattle(int target) { }
        public void EndBattle(int target) { }
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

    private const int ExecutorUid = 339;

    /// <summary>본인 로드(0x3633)를 먼저 먹여 executor를 세운 뒤 전투력 프레임을 먹인다 — 0x3656은
    /// executor가 없으면 아무것도 하지 않으므로, 이 순서가 없으면 모든 케이스가 "통과했는데 저장 안 됨"으로
    /// 뭉개져 음성/양성을 구분하지 못한다.</summary>
    private static (RecordingData Data, RecordingSink Sink) Feed(byte[] powerFrame)
    {
        var data = new RecordingData();
        var sink = new RecordingSink();
        var processor = new StreamProcessor(sink, data);
        processor.OnPacketReceived(OwnLoadFrame(ExecutorUid, "하아앙", 2003, 8), 0);
        processor.OnPacketReceived(powerFrame, 0);
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

    /// <summary>0x3633 본인 로드 프레임 (OwnNicknameValidationTests와 동일한 조립).</summary>
    private static byte[] OwnLoadFrame(int uid, string nickname, int server, int job)
    {
        var body = new List<byte> { 0x33, 0x36 };
        WriteVarInt(body, uid);
        for (int i = 0; i < 5; i++)
        {
            body.Add(0x60);
        }

        byte[] name = Encoding.UTF8.GetBytes(nickname);
        WriteVarInt(body, name.Length);
        body.AddRange(name);
        body.Add((byte)(server & 0xFF));
        body.Add((byte)((server >> 8) & 0xFF));
        body.Add((byte)job);

        var frame = new List<byte>();
        WriteVarInt(frame, body.Count + 3);
        frame.AddRange(body);
        return frame.ToArray();
    }

    /// <summary>0x3656 프레임을 실측 레이아웃대로 조립한다:
    /// <c>[len varint][0x56][0x36][u32 LE 현재][00 00 00 00][u32 LE 둘째][00 00 00 00]</c> = 19바이트.
    /// 길이 varint 값 22는 코퍼스 실프레임과 바이트 단위로 같다(<c>16 5636 …</c>).</summary>
    private static byte[] PowerFrame(long current, long second, byte pad = 0x00)
    {
        var body = new List<byte> { 0x56, 0x36 };
        body.AddRange(BitConverter.GetBytes((uint)current));
        body.AddRange([pad, pad, pad, pad]);
        body.AddRange(BitConverter.GetBytes((uint)second));
        body.AddRange([pad, pad, pad, pad]);

        var frame = new List<byte>();
        WriteVarInt(frame, body.Count + 4);
        frame.AddRange(body);
        return frame.ToArray();
    }

    /// <summary>2026-08-17 코퍼스에서 그대로 떠 온 실프레임(02:26:11.287). 하아앙의 실제 전투력
    /// 356,559 + 둘째 필드 357,335. 이게 막히면 본인 전투력이 통째로 안 뜬다.</summary>
    [Fact]
    public void RealCapturedFrame_IsAccepted()
    {
        byte[] frame = Convert.FromHexString("165636CF70050000000000D773050000000000");

        (RecordingData data, RecordingSink sink) = Feed(frame);

        Assert.Equal((ExecutorUid, 356559), Assert.Single(data.Powers));
        Assert.Empty(sink.Errors);
    }

    /// <summary>같은 캐릭터의 로드 진행 중 프레임(02:28:06.381) — 현재값이 둘째 필드(최고 전투력)보다
    /// 한참 작다. 로드 시퀀스는 195,607 → 290,293 → 356,559처럼 올라온다.
    /// <para>둘째 필드는 실측 108프레임 전부에서 <c>최고 ≥ 현재</c>였지만(현재값이 최고를 넘는 순간
    /// 같이 올라간다), 그 대소를 게이트로 쓰지는 않는다 — 서버가 두 필드를 한 프레임 어긋나게 보내는
    /// 날 정상 전투력이 통째로 막히기 때문이다. 게이트는 값의 대소가 아니라 <b>모양</b>만 본다.</para></summary>
    [Fact]
    public void PartialLoadFrame_IsAccepted()
    {
        byte[] frame = Convert.FromHexString("16563617FC020000000000D773050000000000");

        Assert.Equal((ExecutorUid, 195607), Assert.Single(Feed(frame).Data.Powers));
    }

    /// <summary>사고 재현. 2,285,100은 종전 상한(1000만) 아래라 그대로 통과했고, 본인 배지·티어 구간·
    /// 통계 업로드까지 오염시켰다.</summary>
    [Fact]
    public void IncidentValue_IsRejected()
    {
        byte[] frame = PowerFrame(2_285_100, 2_285_100);

        (RecordingData data, RecordingSink sink) = Feed(frame);

        Assert.Empty(data.Powers);
        Assert.Contains(sink.Errors, e => e == "own_combat_power:implausible value");
    }

    /// <summary>상한 경계: 정확히 상한은 통과, 한 칸 위는 거절.</summary>
    [Theory]
    [InlineData(CombatPower.Max, true)]
    [InlineData(CombatPower.Max + 1, false)]
    [InlineData(979_329, true)]   // 2026-08-17 코퍼스 실측 최댓값(낯선 유저 포함 577 표본)
    [InlineData(0, false)]
    public void CeilingBoundary(long value, bool accepted)
    {
        byte[] frame = PowerFrame(value, 979_329);

        Assert.Equal(accepted ? 1 : 0, Feed(frame).Data.Powers.Count);
    }

    /// <summary>둘째 필드가 말이 안 되면 그 프레임 전체가 0x3656이 아니다 — 현재값이 그럴듯해도 버린다.</summary>
    [Fact]
    public void SecondFieldOutOfRange_IsRejected()
    {
        byte[] frame = PowerFrame(356_559, 9_000_000);

        Assert.Empty(Feed(frame).Data.Powers);
    }

    /// <summary>린치핀: 두 u32 사이·뒤의 0 패딩 8바이트. 우연히 <c>56 36</c>으로 시작한 난수 페이로드가
    /// 이걸 전부 맞출 확률은 사실상 0이라, 값 상한보다 이쪽이 실제 방어선이다.</summary>
    [Fact]
    public void NonZeroPadding_IsRejected()
    {
        byte[] frame = PowerFrame(356_559, 357_335, pad: 0x7F);

        (RecordingData data, RecordingSink sink) = Feed(frame);

        Assert.Empty(data.Powers);
        Assert.Contains(sink.Errors, e => e == "own_combat_power:padding mismatch");
    }

    /// <summary>2026-08-17 코퍼스에는 0x3656으로 dispatch된 길이 11·5 프레임이 4건 있었다(정상은 전부 19).
    /// 종전 파서는 길이 11 프레임에서도 u32를 읽어 버렸고 — 값 범위에 걸린 덕에 우연히 살았을 뿐이다.</summary>
    [Theory]
    [InlineData(11)]
    [InlineData(7)]
    [InlineData(5)]
    public void ShortBody_IsRejected(int length)
    {
        byte[] frame = PowerFrame(356_559, 357_335)[..length];

        (RecordingData data, RecordingSink sink) = Feed(frame);

        Assert.Empty(data.Powers);
        Assert.Contains(sink.Errors, e => e.StartsWith("own_combat_power:", StringComparison.Ordinal));
    }

    /// <summary>본인 로드가 아직 안 온 상태(executor 미확정)에서는 아무것도 저장하지 않는다.</summary>
    [Fact]
    public void WithoutExecutor_NothingIsSaved()
    {
        var data = new RecordingData();
        new StreamProcessor(new RecordingSink(), data)
            .OnPacketReceived(Convert.FromHexString("165636CF70050000000000D773050000000000"), 0);

        Assert.Empty(data.Powers);
    }

    // ---- 0x3645 스냅샷 스캔은 본인 행을 건드리지 않는다 ----

    /// <summary>0x3645 '주변 남' 스냅샷 프레임:
    /// <c>[len][0x45][0x36][uid][varint][varint][filler][nickLen][name][job][server u16][legionLen][legion]</c>
    /// + 꼬리에 <c>F4 CB 1F</c> 마커 + 8바이트 + <c>[u32 전투력][u32 0]</c>. 전투력은 고정 오프셋이 아니라
    /// 마커+11부터의 슬라이딩 스캔으로 읽힌다(<c>ParseSnapshotPower</c>).</summary>
    private static byte[] SnapshotFrame(int uid, string nickname, int server, long scannedPower)
    {
        var body = new List<byte> { 0x45, 0x36 };
        WriteVarInt(body, uid);
        WriteVarInt(body, 0);          // unknownInfo1
        WriteVarInt(body, 0);          // unknownInfo2
        body.Add(0x00);                // 파서가 무조건 건너뛰는 1바이트

        byte[] name = Encoding.UTF8.GetBytes(nickname);
        WriteVarInt(body, name.Length);
        body.AddRange(name);
        body.Add(16);                                       // job
        body.Add((byte)(server & 0xFF));
        body.Add((byte)((server >> 8) & 0xFF));
        WriteVarInt(body, 4);
        body.AddRange(Encoding.UTF8.GetBytes("AAAA"));       // 군단명(숫자가 아니어야 server가 채택된다)

        body.AddRange([0xF4, 0xCB, 0x1F]);                   // PowerMarker
        body.AddRange(new byte[8]);                          // 마커+11 = 전투력 u32 시작
        body.AddRange(BitConverter.GetBytes((uint)scannedPower));
        body.AddRange(new byte[4]);                          // 스캔이 요구하는 "뒤 u32 == 0"

        var frame = new List<byte>();
        WriteVarInt(frame, body.Count + 4);
        frame.AddRange(body);
        return frame.ToArray();
    }

    /// <summary>대조군: 남의 스냅샷 전투력은 그대로 저장된다(가드가 과하게 조여지지 않았음을 고정).</summary>
    [Fact]
    public void SnapshotPower_ForAnotherPlayer_IsStored()
    {
        var data = new RecordingData();
        var processor = new StreamProcessor(new RecordingSink(), data);
        processor.OnPacketReceived(OwnLoadFrame(ExecutorUid, "하아앙", 2003, 8), 0);

        processor.OnPacketReceived(SnapshotFrame(1833, "라떼몬", 2003, 354_483), 0);

        Assert.Equal((1833, 354_483), Assert.Single(data.Powers));
    }

    /// <summary>0x3645는 '주변 남' 브로드캐스트라 실측 11.5시간 / 540스냅샷에서 executor uid를 지목한 적이
    /// 없다. 그래도 파서엔 self 제외가 없었고, 이 경로의 전투력은 세 소스 중 가장 무른 슬라이딩 스캔이다 —
    /// <see cref="CombatPower"/> 상한만으로는 [40만, 상한] 구간의 오독이 그대로 통과해 0x3656이 앉힌
    /// 진값을 덮는다. 본인은 전용·고정 오프셋 소스(0x3656)를 이미 갖고 있으므로 이 스캔값을 쓰지 않는다.</summary>
    [Fact]
    public void SnapshotPower_DoesNotOverwriteTheExecutor()
    {
        var data = new RecordingData();
        var processor = new StreamProcessor(new RecordingSink(), data);
        processor.OnPacketReceived(OwnLoadFrame(ExecutorUid, "하아앙", 2003, 8), 0);
        processor.OnPacketReceived(PowerFrame(356_559, 357_335), 0);       // 0x3656 진값

        processor.OnPacketReceived(SnapshotFrame(ExecutorUid, "하아앙", 2003, 1_900_000), 0);

        Assert.Equal((ExecutorUid, 356_559), Assert.Single(data.Powers));  // 스캔값 미반영
        Assert.Contains(data.Names, n => n.Uid == ExecutorUid && !n.IsExecutor); // 닉/서버 갱신은 그대로
    }
}
