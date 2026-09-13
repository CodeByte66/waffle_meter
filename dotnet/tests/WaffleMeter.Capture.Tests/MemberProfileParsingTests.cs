using System.Text;
using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// Spec for the 0x9200 member-profile parser — the only broadcast that carries an entity uid in the SAME record
/// as the player's name, which is what lets the meter re-bind 본인 to a fresh entity id when the own-load packet
/// (0x3633) never arrives. The record geometry below was verified against a live capture (20260623-194127, a
/// 296-byte 0x9200 frame that arrived right after a 0x9702 roster in the same TCP segment) where it yielded
/// uid 15360 / '플러시' / server 2003.
/// <para>The structural checks matter as much as the offsets: in that session the self's name appeared ~40 times
/// across all captured bytes and EXACTLY ONE occurrence passed the 0x24 + 36-byte-ASCII-GUID + matching-server
/// validation. A false match here would move 본인 onto a stranger's entity id, so the negative cases below are
/// the point of this suite.</para>
/// </summary>
public sealed class MemberProfileParsingTests
{
    private sealed class RecordingData : ICaptureGameData
    {
        public readonly List<(int Uid, string Nickname, int Server)> Bound = [];

        public void TryBindExecutorByIdentity(int uid, string nickname, int server) =>
            Bound.Add((uid, nickname, server));

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
        // 파서가 "조용히 건너뛴 것"과 "터진 뒤 dispatch의 catch에 삼켜진 것"은 관찰 결과가 같다. 경계 가드를
        // 검증하려면 예외가 없었다는 것까지 단언해야 하므로 오류를 기록한다.
        public readonly List<string> Errors = [];

        public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) { }
        public void UnknownOpcode(int opcode, bool extraFlag, int len) { }
        public void CompressedPacket(int len, bool extraFlag) { }
        public void ParserError(string stage, string reason) => Errors.Add($"{stage}:{reason}");
        public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode) { }
        public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
        public void Meta(string type, params (string Key, object? Value)[] fields) { }
    }

    // 형식만 같은 합성 GUID. 실제 캡처의 값은 계정/캐릭터 식별자라 공개 저장소에 넣지 않는다 —
    // 파서가 보는 것은 "0x24 + 8-4-4-4-12 형태의 ASCII" 뿐이므로 검증력은 동일하다.
    private const string Guid36 = "A1B2C3D4-E5F6-4788-99AA-BBCCDDEEFF00";

    /// <summary>One member record, exactly as observed on the wire:
    /// [uid u32 LE][server u16 LE][?? u16][0x24][36B ASCII GUID][6B handle][server u16 LE][nameLen u8][name].</summary>
    private static void AddProfile(
        List<byte> body, int uid, string name, int server,
        byte guidMarker = 0x24, string guid = Guid36, int? leadingServer = null)
    {
        byte[] nm = Encoding.UTF8.GetBytes(name);
        int lead = leadingServer ?? server;

        body.Add((byte)(uid & 0xFF));
        body.Add((byte)((uid >> 8) & 0xFF));
        body.Add((byte)((uid >> 16) & 0xFF));
        body.Add((byte)((uid >> 24) & 0xFF));
        body.Add((byte)(lead & 0xFF));
        body.Add((byte)((lead >> 8) & 0xFF));
        body.Add(0x81);                                  // unidentified u16
        body.Add(0xD3);
        body.Add(guidMarker);                            // 0x24 = the 36-byte GUID string's length marker
        body.AddRange(Encoding.ASCII.GetBytes(guid));
        for (int i = 0; i < 6; i++)
        {
            body.Add((byte)(0x27 + i));                  // 6-byte handle
        }

        body.Add((byte)(server & 0xFF));
        body.Add((byte)((server >> 8) & 0xFF));
        body.Add((byte)nm.Length);
        body.AddRange(nm);
    }

    private static byte[] Packet(List<byte> body)
    {
        var packet = new byte[body.Count + 1];
        packet[0] = (byte)Math.Min(body.Count, 255); // 1-byte length varint (only the prefix LENGTH matters here)
        body.CopyTo(packet, 1);
        return packet;
    }

    private static NullSink _lastSink = new();

    private static RecordingData Run(List<byte> body)
    {
        var data = new RecordingData();
        _lastSink = new NullSink();
        new StreamProcessor(_lastSink, data, null).OnPacketReceived(Packet(body), 1000);
        return data;
    }

    [Fact]
    public void A_member_profile_yields_the_entity_uid_with_the_name_and_server()
    {
        var body = new List<byte> { 0x00, 0x92 };
        AddProfile(body, uid: 15360, name: "플러시", server: 2003);

        Assert.Equal([(15360, "플러시", 2003)], Run(body).Bound);
    }

    [Fact]
    public void Every_record_in_a_multi_member_frame_is_reported()
    {
        // 파서는 본인을 고르지 않는다 — 신원 일치 판정은 executor를 아는 데이터 계층의 몫이다.
        var body = new List<byte> { 0x00, 0x92 };
        AddProfile(body, uid: 15360, name: "플러시", server: 2003);
        AddProfile(body, uid: 4857, name: "Wildz", server: 1014);

        Assert.Equal([(15360, "플러시", 2003), (4857, "Wildz", 1014)], Run(body).Bound);
    }

    [Fact]
    public void A_missing_guid_length_marker_rejects_the_record()
    {
        var body = new List<byte> { 0x00, 0x92 };
        AddProfile(body, uid: 15360, name: "플러시", server: 2003, guidMarker: 0x23);

        Assert.Empty(Run(body).Bound);
    }

    [Fact]
    public void A_non_guid_blob_rejects_the_record()
    {
        // 길이만 36이고 내용이 GUID가 아니면 우연히 걸린 바이트 런이다.
        var body = new List<byte> { 0x00, 0x92 };
        AddProfile(body, uid: 15360, name: "플러시", server: 2003, guid: new string('Z', 36));

        Assert.Empty(Run(body).Bound);
    }

    [Fact]
    public void A_mismatched_leading_server_rejects_the_record()
    {
        // 같은 레코드 안의 두 server 필드가 어긋나면 레코드 경계를 잘못 잡은 것이다.
        var body = new List<byte> { 0x00, 0x92 };
        AddProfile(body, uid: 15360, name: "플러시", server: 2003, leadingServer: 1014);

        Assert.Empty(Run(body).Bound);
    }

    [Fact]
    public void A_record_too_close_to_the_packet_start_is_ignored_rather_than_read_out_of_bounds()
    {
        // uid 필드가 패킷 앞을 벗어나는 위치. 이 하한 가드 하나가 음수 인덱스 네 곳(-51/-47/-43/-42)을 지키므로,
        // "결과가 비었다"만으로는 부족하다 — 예외가 없었다는 것과, 같은 패킷 뒤쪽의 정상 레코드가 살아남는다는
        // 것까지 확인해야 가드가 실제로 '건너뛰기'로 동작함이 증명된다.
        var body = new List<byte> { 0x00, 0x92, 0xD3, 0x07, 0x09 };
        body.AddRange(Encoding.UTF8.GetBytes("플러시"));

        RecordingData decoyOnly = Run(body);
        Assert.Empty(decoyOnly.Bound);
        Assert.Empty(_lastSink.Errors);

        AddProfile(body, uid: 15360, name: "플러시", server: 2003);
        RecordingData withRealRecord = Run(body);
        Assert.Equal([(15360, "플러시", 2003)], withRealRecord.Bound);
        Assert.Empty(_lastSink.Errors);
    }

    [Fact]
    public void An_out_of_range_server_rejects_the_record()
    {
        var body = new List<byte> { 0x00, 0x92 };
        AddProfile(body, uid: 15360, name: "플러시", server: 3999);

        Assert.Empty(Run(body).Bound);
    }

    [Fact]
    public void Another_opcode_never_reaches_the_member_profile_parser()
    {
        var body = new List<byte> { 0x02, 0x97 };
        AddProfile(body, uid: 15360, name: "플러시", server: 2003);

        Assert.Empty(Run(body).Bound);
    }
}
