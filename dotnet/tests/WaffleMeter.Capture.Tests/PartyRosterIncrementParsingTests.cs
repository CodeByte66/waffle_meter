using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// 0x971F(멤버 갱신) · 0x9622(멤버 제거) 파서 — <b>코퍼스에서 그대로 떠 온 프레임</b>으로 고정한다.
///
/// <para>🔑 왜 필요한가. 0x9702 스냅샷은 <b>부분 로스터</b>를 보내는 것이 정상이다(실측: 정원 10 방
/// 스냅샷 502건 중 완전한 것 290건, 57.8%). 빠진 멤버의 이름 바이트는 패킷에 <b>아예 없다</b> —
/// 파서 탓이 아니다(결손 519건 중 파싱 실패 5건, 1.0%). 그래서 증분을 받지 않으면 로스터는
/// 구조적으로 수시로 정원에 못 미치고, 그 상태에서 전투가 끝나면 10인 공대가 9로 업로드된다.</para>
///
/// <para>프레임 출처: <c>packet-debug-logs/20260709-205608</c> (07-01 패치 이후, 파티 5 / 성역 10 세대).</para>
/// </summary>
public sealed class PartyRosterIncrementParsingTests
{
    // 0x971F — 마이농(2003) 슬롯 3. [len][1F 97][mask 7E][slot 03][key u32][born u16][srv u16][len][name][tail]
    private const string MemberUpdateMaiNong =
        "43 1F 97 7E 03 E7 FF 02 00 00 00 D3 07 09 EB A7 88 EC 9D B4 EB 86 8D 24 00 00 00 32 00 00 00 " +
        "E5 10 00 00 03 D3 07 B8 0F 04 6A D1 07 00 00 00 00 00 01 02 01 00 00 00 C8 00 00 00 00 00 00 00 01";

    // 0x971F — 진설화(2001) 슬롯 8.
    private const string MemberUpdateJinSeolHwa =
        "43 1F 97 7E 08 1F B7 01 00 00 00 D1 07 09 EC A7 84 EC 84 A4 ED 99 94 24 00 00 00 32 00 00 00 " +
        "0E 12 00 00 03 D1 07 B8 0F 04 F4 FC 07 00 00 00 00 00 01 02 01 00 00 00 42 01 00 00 00 00 00 00 01";

    // 0x9622 — 제거. 본문이 로스터 key(3505 = 에이) 하나로 시작한다.
    private const string MemberRemoveEi =
        "17 22 96 B1 0D 00 00 00 00 D3 07 78 FA 07 00 1C 14 1C 00 03";

    private sealed class RecordingData : ICaptureGameData
    {
        public readonly List<(string Nickname, int Server, int Slot, int Key)> Updates = new();
        public readonly List<int> Removed = new();

        public void UpdatePartyMember(string nickname, int server, int slot, int key) =>
            Updates.Add((nickname, server, slot, key));

        public void RemovePartyMemberByKey(int key) => Removed.Add(key);

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

    private static byte[] Hex(string hex)
    {
        string[] t = hex.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var b = new byte[t.Length];
        for (int i = 0; i < t.Length; i++)
        {
            b[i] = Convert.ToByte(t[i], 16);
        }

        return b;
    }

    private static RecordingData Run(params string[] frames)
    {
        var data = new RecordingData();
        var proc = new StreamProcessor(new NullSink(), data, null);
        foreach (string f in frames)
        {
            proc.OnPacketReceived(Hex(f), 1000);
        }

        return data;
    }

    [Fact]
    public void A_member_update_carries_the_slot_and_the_roster_key()
    {
        RecordingData data = Run(MemberUpdateMaiNong);

        (string nickname, int server, int slot, int key) = Assert.Single(data.Updates);
        Assert.Equal("마이농", nickname);
        Assert.Equal(2003, server);
        Assert.Equal(3, slot);
        Assert.Equal(196_583, key);
    }

    /// <summary>두 번째 프레임도 같은 구조다 — 서버·슬롯·이름 길이가 다른 표본으로 한 번 더 고정한다.</summary>
    [Fact]
    public void A_second_update_frame_decodes_the_same_way()
    {
        RecordingData data = Run(MemberUpdateJinSeolHwa);

        (string nickname, int server, int slot, int key) = Assert.Single(data.Updates);
        Assert.Equal("진설화", nickname);
        Assert.Equal(2001, server);
        Assert.Equal(8, slot);
        Assert.Equal(112_415, key);
    }

    [Fact]
    public void A_removal_frame_yields_the_roster_key()
    {
        RecordingData data = Run(MemberRemoveEi);

        Assert.Equal(3505, Assert.Single(data.Removed));
        Assert.Empty(data.Updates);
    }

    /// <summary>⚠️ 제거는 갱신을 트리거하지 않는다(그 반대도). 두 경로가 섞이면 지운 사람이 되살아난다.</summary>
    [Fact]
    public void The_two_frames_do_not_bleed_into_each_other()
    {
        RecordingData data = Run(MemberUpdateMaiNong, MemberRemoveEi, MemberUpdateJinSeolHwa);

        Assert.Equal(2, data.Updates.Count);
        Assert.Equal(3505, Assert.Single(data.Removed));
    }

    /// <summary>잘린 프레임은 조용히 무시한다 — 절반만 읽은 멤버를 로스터에 넣는 것이 최악이다.</summary>
    [Theory]
    [InlineData("43 1F 97 7E")]
    [InlineData("43 1F 97 7E 03 E7 FF 02 00 00 00 D3 07 09 EB A7")]
    [InlineData("17 22 96 B1")]
    public void A_truncated_frame_changes_nothing(string frame)
    {
        RecordingData data = Run(frame);

        Assert.Empty(data.Updates);
        Assert.Empty(data.Removed);
    }
}
