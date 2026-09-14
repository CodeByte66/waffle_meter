using System.Text;
using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// 가상 0x9200 멤버 프로필 패킷을 실제 StreamProcessor에 흘려, 각 파티원의 (uid, 이름, 서버)가 표시-계층 보조
/// 로스터로 흘러가는지(SaveMemberProfile 호출) 검증한다. 이 보조 로스터가 0x9702가 입장 버스트에서 유실됐을
/// 때의 로스터 폴백이자, 타인 닉(0x3645) 유실로 무명이 된 파티원 전투행을 uid로 곧장 명명하는 소스다.
/// 레이아웃은 <see cref="MemberProfileParsingTests"/>와 동일(실 캡처 20260623-194127로 검증). 데이터 계층의
/// TTL·상한 동작은 DataManagerMemberProfileTests가 따로 고정한다.
/// </summary>
public sealed class MemberProfileRosterEndToEndTests
{
    private const string Guid36 = "A1B2C3D4-E5F6-4788-99AA-BBCCDDEEFF00";

    private sealed class RecordingData : ICaptureGameData
    {
        public readonly List<(int Uid, string Nickname, int Server)> Profiles = [];
        public void SaveMemberProfile(int uid, string nickname, int server) => Profiles.Add((uid, nickname, server));

        public void SavePartyRoster(IReadOnlyList<(string Nickname, int Server, int Slot)> members) { }
        public void SaveAetherStatus(int baseVal, int bonus) { }
        public void SaveShugoKey(int baseVal, int bonus) { }
        public void SaveFieldBossTimers(IReadOnlyList<(int Code, long TargetMs)> timers) { }
        public Mob? GetMob(int code) => null;
        public int? GetMobId(int instanceId) => null;
        public void SaveMobId(int instanceId, int mobCode) { }
        public bool SkillExists(long code) => false;
        public long CurrentEpoch() => 0;
        public void SaveDamage(ParsedDamagePacket pdp, long epoch) { }
        public void StartBattle(int target) { }
        public void EndBattle(int target) { }
        public void SaveNickname(int uid, string nickname, bool isExecutor, int server, int jobByte) { }
        public void SaveUserPower(int uid, int power) { }
        public void SaveSummon(int summonId, int ownerId) { }
        public void SaveMobHp(int instanceId, long hp) { }
        public void SaveUseBuff(int uid, int skillCode, long buffStart, long buffEnd, long duration, int actorId) { }
        public void RequestOfficialCharacterLookup(int uid) { }
    }

    private sealed class NullSink : IStreamProcessorSink
    {
        public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) { }
        public void UnknownOpcode(int opcode, bool extraFlag, int len) { }
        public void CompressedPacket(int len, bool extraFlag) { }
        public void ParserError(string stage, string reason) { }
        public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode) { }
        public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
        public void Meta(string type, params (string Key, object? Value)[] fields) { }
    }

    private static void AddProfile(List<byte> body, int uid, string name, int server)
    {
        byte[] nm = Encoding.UTF8.GetBytes(name);
        body.Add((byte)(uid & 0xFF));
        body.Add((byte)((uid >> 8) & 0xFF));
        body.Add((byte)((uid >> 16) & 0xFF));
        body.Add((byte)((uid >> 24) & 0xFF));
        body.Add((byte)(server & 0xFF));
        body.Add((byte)((server >> 8) & 0xFF));
        body.Add(0x81);
        body.Add(0xD3);
        body.Add(0x24); // GUID length marker
        body.AddRange(Encoding.ASCII.GetBytes(Guid36));
        for (int i = 0; i < 6; i++)
        {
            body.Add((byte)(0x27 + i)); // 6-byte handle
        }

        body.Add((byte)(server & 0xFF));
        body.Add((byte)((server >> 8) & 0xFF));
        body.Add((byte)nm.Length);
        body.AddRange(nm);
    }

    private static byte[] Packet(List<byte> body)
    {
        var packet = new byte[body.Count + 1];
        packet[0] = (byte)Math.Min(body.Count, 255);
        body.CopyTo(packet, 1);
        return packet;
    }

    [Fact]
    public void Every_member_in_a_0x9200_frame_flows_to_the_display_roster_with_its_uid()
    {
        var body = new List<byte> { 0x00, 0x92 };
        AddProfile(body, uid: 8611, name: "엽록소", server: 1014);
        AddProfile(body, uid: 1558, name: "달잔", server: 1006);

        var data = new RecordingData();
        new StreamProcessor(new NullSink(), data).OnPacketReceived(Packet(body), 1000);

        Assert.Equal([(8611, "엽록소", 1014), (1558, "달잔", 1006)], data.Profiles);
    }

    [Fact]
    public void A_structurally_invalid_record_reaches_neither_binding_nor_the_roster()
    {
        // GUID 마커가 어긋나면 레코드 자체가 거부되므로 보조 로스터에도 들어가지 않는다(무명 낯선 행 오염 방지).
        var body = new List<byte> { 0x00, 0x92, 0xD3, 0x07, 0x09 };
        body.AddRange(Encoding.UTF8.GetBytes("엽록소"));

        var data = new RecordingData();
        new StreamProcessor(new NullSink(), data).OnPacketReceived(Packet(body), 1000);

        Assert.Empty(data.Profiles);
    }
}
