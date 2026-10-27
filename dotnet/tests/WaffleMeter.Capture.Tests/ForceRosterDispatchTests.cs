using WaffleMeter.Capture;
using Xunit;
using F = WaffleMeter.Capture.Tests.ForceRosterFixtures;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// 실제 프레임을 <see cref="StreamProcessor.OnPacketReceived"/> 에 흘려, 0x96xx·0x920D 가 <b>디스패치까지</b> 닿는지 고정한다.
/// 상수 + switch 만으로는 파서가 한 번도 안 불린다 — <c>OpcodeNames</c> 등록이 활성화 스위치이고, 중복 스트림에서는
/// <c>IdentityReplayOpcodes</c> 가 두 번째 스위치다(2026-09-19 0x971F 교훈). 0x9622 는 방 로스터(key)와 포스(dbid)를 같이 고친다.
/// </summary>
public sealed class ForceRosterDispatchTests
{
    private sealed class RecordingData : ICaptureGameData
    {
        public readonly List<string> Calls = [];
        public readonly List<(int Uid, string Nickname, int Server)> Profiles = [];
        public readonly List<(int Uid, string Nickname, int Server)> Binds = [];
        public readonly List<int> RoomRemovals = [];
        public IReadOnlyList<WireRosterMember>? Snapshot;
        public long SnapshotPartyKey;

        public void SaveForceInfo(ForceSnapshot force) => Calls.Add($"info:{force.Parties.Count}");
        public void AddForceMember(long partyKey, WireRosterMember member) => Calls.Add($"add:{member.Nickname}");
        public void AddForceParty(ForceParty party) => Calls.Add($"party:{party.Number}");
        public void MoveForceMembers(IReadOnlyList<ForceParty> changes) => Calls.Add($"move:{changes.Count}");
        public void ReconnectForceMember(WireRosterMember member) => Calls.Add($"reconnect:{member.Nickname}:{member.EntityUid}");
        public void RemoveForceMember(long memberDbid, long partyKey) => Calls.Add($"remove:{memberDbid & 0xFFFFFFFFFFFF}");
        public void UpdateForceMemberStatus(ForceMemberStatus status) => Calls.Add($"status:{status.CombatPower}");
        public void AddMemberProfile(WireRosterMember member) => Calls.Add($"profile-add:{member.Nickname}:{member.EntityUid}");
        public void RemovePartyMemberByKey(int key) => RoomRemovals.Add(key);
        public void SaveMemberProfile(int uid, string nickname, int server) => Profiles.Add((uid, nickname, server));
        public void TryBindExecutorByIdentity(int uid, string nickname, int server) => Binds.Add((uid, nickname, server));

        public void SaveMemberProfileSnapshot(long partyKey, IReadOnlyList<WireRosterMember> members)
        {
            SnapshotPartyKey = partyKey;
            Snapshot = members;
        }

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

    private sealed class Sink : IStreamProcessorSink
    {
        public readonly List<string> Errors = [];
        public readonly List<string> Unknown = [];
        public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) { }
        public void UnknownOpcode(int opcode, bool extraFlag, int len) => Unknown.Add(opcode.ToString("X4"));
        public void CompressedPacket(int len, bool extraFlag) { }
        public void ParserError(string stage, string reason) => Errors.Add($"{stage}:{reason}");
        public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode) { }
        public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
        public void Meta(string type, params (string Key, object? Value)[] fields) { }
    }

    private static (RecordingData Data, Sink Sink) Feed(bool identityOnly, params string[] hex)
    {
        var data = new RecordingData();
        var sink = new Sink();
        var sp = new StreamProcessor(sink, data, null);
        foreach (string h in hex)
        {
            sp.OnPacketReceived(F.Bytes(h), 1000, identityOnly);
        }

        Assert.Empty(sink.Errors);
        Assert.Empty(sink.Unknown);
        return (data, sink);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // 존 이동 뒤 ForceInfo 가 억제된 두 번째 연결로 오는 경우 — 멱등이라 재생 대상이다
    public void Every_force_opcode_reaches_its_handler_including_on_a_dup_suppressed_stream(bool identityOnly)
    {
        (RecordingData d, _) = Feed(identityOnly,
            F.ForceInfo205829, F.ForceReconnect205829, F.ForceStatus205829, F.ForceAddMember205836,
            F.ForceAddParty205841, F.ForceMoveMember205911, F.ForceRemove210223, F.PartyAddMember205836);

        Assert.Equal(new[]
        {
            "info:2", "reconnect:Violette:7982", "status:893426", "add:순두부", "party:3", "move:2", "remove:5014",
            "profile-add:순두부:3980",
        }, d.Calls);
    }

    [Fact]
    public void ForceRemoveMember_also_removes_from_the_room_roster_by_the_low_dbid_key_as_before()
    {
        (RecordingData d, _) = Feed(false, F.ForceRemove210223);
        Assert.Equal(new[] { 5014 }, d.RoomRemovals); // 종전 0x9622 경로 — 10인 방 로스터 key = dbid 하위 32비트
        Assert.Contains("remove:5014", d.Calls);
    }

    [Fact]
    public void PartyInfo_hands_over_every_member_and_binds_only_the_ones_with_an_entity_uid()
    {
        (RecordingData d, _) = Feed(false, F.PartyInfo205829);
        var withUid = new[] { (5565, "쉰", 2003), (7423, "읓", 2003), (2851, "콘팡", 2003) };
        Assert.Equal(withUid, d.Profiles);
        Assert.Equal(withUid, d.Binds);
        Assert.NotNull(d.Snapshot);
        Assert.Equal(new[] { "쉰", "Violette", "읓", "콘팡" }, d.Snapshot!.Select(m => m.Nickname));
        Assert.Equal(0x24135ED200149019L, d.SnapshotPartyKey);
    }

    [Fact]
    public void A_cross_server_party_member_is_no_longer_dropped()
    {
        // 0x9200 의 쉰 레코드를 다른 서버(2016) 캐릭터로 바꾼다 — dbid 상위 16비트만. born 서버는 2003 그대로다.
        // 종전 바이트스캔은 born == dbid 서버를 구조 검증으로 써서 이런 멤버(실측 20인 포스의 박태평·핀셔·롤형)를 버렸다.
        byte[] p = F.Bytes(F.PartyInfo205829);
        int dbidHigh = IndexOfDbidServer(p, "쉰");
        p[dbidHigh] = 0xE0; // 2016 = 0x07E0
        p[dbidHigh + 1] = 0x07;
        var data = new RecordingData();
        new StreamProcessor(new Sink(), data, null).OnPacketReceived(p, 1000);
        Assert.Contains((5565, "쉰", 2016), data.Profiles);
    }

    [Fact]
    public void An_old_client_PartyInfo_still_reads_through_the_byte_scan_without_a_snapshot()
    {
        (RecordingData d, _) = Feed(false, F.PartyInfoOldClient0727);
        Assert.NotEmpty(d.Profiles); // 종전 경로 그대로
        Assert.Null(d.Snapshot);     // 떠난 멤버 정리·uid 0 보존은 스키마 워크가 성공한 프레임에서만
    }

    [Fact]
    public void The_new_opcodes_count_as_game_frames()
    {
        // OpcodeNames 에 넣으면 LooksLikeGamePacket 도 넓어진다 — 의도된 결과임을 고정해 둔다(주석 참조).
        Assert.True(StreamProcessor.LooksLikeGamePacket(F.Bytes(F.ForceInfo205829)));
        Assert.True(StreamProcessor.LooksLikeGamePacket(F.Bytes(F.ForceStatus205829)));
        Assert.True(StreamProcessor.LooksLikeGamePacket(F.Bytes(F.PartyAddMember205836)));
    }

    /// <summary>이름 바이트 앞의 [dbid i64][nameLen u8] 에서 dbid 상위 2바이트(서버) 위치.</summary>
    private static int IndexOfDbidServer(byte[] p, string name)
    {
        byte[] nm = System.Text.Encoding.UTF8.GetBytes(name);
        for (int i = 9; i + nm.Length <= p.Length; i++)
        {
            if (p[i - 1] == nm.Length && p.AsSpan(i, nm.Length).SequenceEqual(nm))
            {
                return i - 3;
            }
        }

        throw new InvalidOperationException(name);
    }
}
