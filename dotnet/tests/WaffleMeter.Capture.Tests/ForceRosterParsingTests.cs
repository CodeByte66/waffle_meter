using WaffleMeter.Capture;
using Xunit;
using F = WaffleMeter.Capture.Tests.ForceRosterFixtures;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// 포스(20인 공대) 계열 0x96xx 와 본인 파티 0x9200/0x920D 의 스키마 워크 — 전부 <b>실제 캡처 프레임</b>(20261027-205641)으로
/// 고정한다(<see cref="ForceRosterFixtures"/>, 계정 GUID 만 합성값). 기대값은 와이어에서 손으로 디코드한 값이고, 같은 공대의
/// 인게임 화면(2파티 Violette·쿨(=읓)·쉰·콘팡·순두부)과도 맞는다.
/// </summary>
public sealed class ForceRosterParsingTests
{
    private const int Server2003 = 2003;
    private const long Party1Key = 0x24135ED200148F37L; // 37 8F 14 00 D2 5E 13 24
    private const long Party2Key = 0x24135ED200149019L; // 19 90 14 00 D2 5E 13 24 — 콘팡의 서브파티
    private const long Party3Key = 0x24135ED200149092L;

    private static long Dbid(int server, long id) => ((long)server << 48) | id;

    /// <summary>opcode 2바이트 바로 뒤.</summary>
    private static int Body(byte[] p)
    {
        VarIntOutput len = PacketPrimitives.ReadVarInt(p);
        int flag = p[len.Length];
        return len.Length + (flag >= 0xF0 && flag < 0xFF ? 1 : 0) + 2;
    }

    [Fact]
    public void ForceInfo_snapshot_walks_to_the_last_byte_and_names_every_party_and_member()
    {
        byte[] p = F.Bytes(F.ForceInfo205829);
        Assert.True(RosterRecordParser.TryReadForceInfo(p, Body(p), out ForceSnapshot? force));

        Assert.Equal(20, force!.LimitMember);
        Assert.Equal(1, force.Reason); // Join
        Assert.Equal(Dbid(Server2003, 185206), force.LeaderDbid); // 콩령
        Assert.Equal(new[] { 1, 2 }, force.Parties.Select(x => x.Number));
        Assert.Equal(Party1Key, force.Parties[0].PartyKey);
        Assert.Equal(Party2Key, force.Parties[1].PartyKey);
        Assert.Equal(5, force.Parties[0].Members.Count);

        WireRosterMember[] own = force.Parties[1].Members.ToArray();
        Assert.Equal(new[] { "쉰", "Violette", "읓", "콘팡" }, own.Select(m => m.Nickname));
        Assert.Equal(new[] { 3, 1, 2, 4 }, own.Select(m => m.Slot));
        Assert.Equal(new[] { 5565, 0, 7423, 2851 }, own.Select(m => m.EntityUid)); // Violette = 다른 존 → 0
        Assert.Equal(new[] { 16, 28, 20, 16 }, own.Select(m => m.PcId));
        Assert.Equal(new[] { 854294L, 648911L, 1056174L, 883941L }, own.Select(m => m.CombatPower));
        Assert.All(own, m => Assert.Equal(Server2003, m.Server));
        Assert.Equal(Dbid(Server2003, 45996), own[3].Dbid);
    }

    [Fact]
    public void Bools_share_one_message_wide_bit_byte_across_records()
    {
        // 멤버마다 bool 이 둘(_live, _login)이라 넷마다 새 바이트를 읽는다. 9명 중 7번째(Violette, 다른 존)만 login=0 이다.
        // 레코드마다 바이트를 읽는다고 가정하면 다섯 번째 멤버에서 한 바이트씩 밀려 워크 자체가 실패한다.
        byte[] p = F.Bytes(F.ForceInfo205829);
        Assert.True(RosterRecordParser.TryReadForceInfo(p, Body(p), out ForceSnapshot? force));
        WireRosterMember[] all = force!.Parties.SelectMany(x => x.Members).ToArray();
        Assert.Equal(9, all.Length);
        Assert.Equal(new[] { true, true, true, true, true, true, false, true, true }, all.Select(m => m.Login));
    }

    [Fact]
    public void A_zone_move_snapshot_carries_four_parties_new_entity_uids_and_cross_server_members()
    {
        byte[] p = F.Bytes(F.ForceInfo210241);
        Assert.True(RosterRecordParser.TryReadForceInfo(p, Body(p), out ForceSnapshot? force));
        Assert.Equal(5, force!.Reason); // Reconnect — 존 이동 뒤 새 서버 연결(.103)로 온다
        Assert.Equal(new[] { 5, 3, 4, 2 }, force.Parties.Select(x => x.Members.Count));
        WireRosterMember self = force.Parties[1].Members.Single(m => m.Nickname == "콘팡");
        Assert.Equal(12208, self.EntityUid); // 2851 → 12208 : uid 는 존마다 바뀐다, dbid 는 그대로
        Assert.Equal(Dbid(Server2003, 45996), self.Dbid);
        // 캐릭터 서버는 dbid 상위 16비트다(born 은 전원 2003).
        Assert.Equal(2001, force.Parties[0].Members.Single(m => m.Nickname == "롤형").Server);
        Assert.Equal(2016, force.Parties[2].Members.Single(m => m.Nickname == "박태평").Server);
        Assert.Equal(2020, force.Parties[2].Members.Single(m => m.Nickname == "핀셔").Server);
    }

    [Fact]
    public void ForceAddMember_names_the_party_and_the_new_member()
    {
        byte[] p = F.Bytes(F.ForceAddMember205836);
        Assert.True(RosterRecordParser.TryReadForceAddMember(p, Body(p), out long partyKey, out WireRosterMember m));
        Assert.Equal(Party2Key, partyKey);
        Assert.Equal(("순두부", 5, 3980, 23, 824939L), (m.Nickname, m.Slot, m.EntityUid, m.PcId, m.CombatPower));
    }

    [Fact]
    public void ForceAddParty_carries_the_party_number_and_its_first_member()
    {
        byte[] p = F.Bytes(F.ForceAddParty205841);
        Assert.True(RosterRecordParser.TryReadForceAddParty(p, Body(p), out ForceParty? party));
        Assert.Equal((Party3Key, 3, 5), (party!.PartyKey, party.Number, party.LimitMember));
        Assert.Equal("둠벙", Assert.Single(party.Members).Nickname);
    }

    [Fact]
    public void ForceMoveMember_carries_both_touched_parties_in_full()
    {
        byte[] p = F.Bytes(F.ForceMoveMember205911);
        Assert.True(RosterRecordParser.TryReadForceMoveMember(p, Body(p), out IReadOnlyList<ForceParty> changes));
        Assert.Equal(new[] { 2, 3 }, changes.Select(x => x.Number));
        Assert.Equal(new[] { "무적자", "쉰", "Violette", "읓", "콘팡" }, changes[0].Members.Select(m => m.Nickname));
        Assert.Equal(new[] { "쉬었음", "둠벙", "순두부", "박태평", "핀셔" }, changes[1].Members.Select(m => m.Nickname));
        Assert.Equal(4, changes[1].Members.Single(m => m.Nickname == "순두부").Slot);
    }

    [Fact]
    public void ForceReconnectMember_fills_the_entity_uid_of_a_member_who_was_in_another_zone()
    {
        byte[] p = F.Bytes(F.ForceReconnect205829);
        Assert.True(RosterRecordParser.TryReadForceReconnectMember(p, Body(p), out WireRosterMember m));
        Assert.Equal(("Violette", 7982, Dbid(Server2003, 5238), 1024066L), (m.Nickname, m.EntityUid, m.Dbid, m.CombatPower));
    }

    [Fact]
    public void ForceRemoveMember_is_dbid_party_and_reason()
    {
        byte[] p = F.Bytes(F.ForceRemove210223);
        Assert.Equal(20, p.Length);
        Assert.True(RosterRecordParser.TryReadForceRemoveMember(p, Body(p), out ForceMemberRemoval r));
        Assert.Equal((Dbid(Server2003, 5014), Party2Key, 3), (r.MemberDbid, r.PartyKey, r.Reason)); // 쉰 · LeaveMember
    }

    [Fact]
    public void ForceUpdateMemberStatus_carries_job_and_combat_power_by_dbid()
    {
        byte[] p = F.Bytes(F.ForceStatus205829);
        Assert.True(RosterRecordParser.TryReadForceMemberStatus(p, Body(p), out ForceMemberStatus s));
        Assert.Equal((Dbid(Server2003, 5238), 28, 893426L), (s.Dbid, s.PcId, s.CombatPower));
    }

    [Fact]
    public void PartyInfo_walk_keeps_the_member_in_another_zone_and_shares_the_force_party_key()
    {
        byte[] p = F.Bytes(F.PartyInfo205829);
        Assert.True(RosterRecordParser.TryReadPartyInfo(p, Body(p), out PartySnapshot? party));
        Assert.Equal(Party2Key, party!.PartyKey); // 0x9200 의 key = 포스 안 본인 서브파티 key
        Assert.Equal(new[] { "쉰", "Violette", "읓", "콘팡" }, party.Members.Select(m => m.Nickname));
        Assert.Equal(0, party.Members[1].EntityUid);
    }

    [Fact]
    public void PartyAddMember_is_one_party_member_record()
    {
        byte[] p = F.Bytes(F.PartyAddMember205836);
        Assert.True(RosterRecordParser.TryReadPartyAddMember(p, Body(p), out WireRosterMember m));
        Assert.Equal(("순두부", 3980, 23), (m.Nickname, m.EntityUid, m.PcId));
    }

    [Fact]
    public void An_old_client_PartyInfo_without_the_latency_field_does_not_walk()
    {
        byte[] p = F.Bytes(F.PartyInfoOldClient0727);
        Assert.False(RosterRecordParser.TryReadPartyInfo(p, Body(p), out _));
    }

    public static TheoryData<string> AllFixtures() => new()
    {
        nameof(F.ForceInfo205829), nameof(F.ForceInfo210241), nameof(F.ForceAddMember205836), nameof(F.ForceAddParty205841),
        nameof(F.ForceMoveMember205911), nameof(F.ForceReconnect205829), nameof(F.ForceRemove210223),
        nameof(F.ForceStatus205829), nameof(F.PartyInfo205829), nameof(F.PartyAddMember205836),
    };

    private static byte[] Fixture(string name) =>
        F.Bytes((string)typeof(F).GetField(name)!.GetValue(null)!);

    private static bool TryAny(string name, byte[] p)
    {
        int b = Body(p);
        return name switch
        {
            nameof(F.ForceInfo205829) or nameof(F.ForceInfo210241) => RosterRecordParser.TryReadForceInfo(p, b, out _),
            nameof(F.ForceAddMember205836) => RosterRecordParser.TryReadForceAddMember(p, b, out _, out _),
            nameof(F.ForceAddParty205841) => RosterRecordParser.TryReadForceAddParty(p, b, out _),
            nameof(F.ForceMoveMember205911) => RosterRecordParser.TryReadForceMoveMember(p, b, out _),
            nameof(F.ForceReconnect205829) => RosterRecordParser.TryReadForceReconnectMember(p, b, out _),
            nameof(F.ForceRemove210223) => RosterRecordParser.TryReadForceRemoveMember(p, b, out _),
            nameof(F.ForceStatus205829) => RosterRecordParser.TryReadForceMemberStatus(p, b, out _),
            nameof(F.PartyInfo205829) => RosterRecordParser.TryReadPartyInfo(p, b, out _),
            nameof(F.PartyAddMember205836) => RosterRecordParser.TryReadPartyAddMember(p, b, out _),
            _ => throw new ArgumentException(name),
        };
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Every_fixture_walks_exactly_and_fails_closed_when_cut_or_extended(string name)
    {
        byte[] p = Fixture(name);
        Assert.True(TryAny(name, p));
        // 잘린 프레임: 모든 길이에서 실패(예외 없이). 덧붙은 바이트: 정확 소진이 아니므로 실패.
        for (int cut = Body(p); cut < p.Length; cut++)
        {
            Assert.False(TryAny(name, p[..cut]), $"{name} cut at {cut}");
        }

        Assert.False(TryAny(name, [.. p, 0x00]));
    }

    [Fact]
    public void An_unobserved_member_mask_is_refused_rather_than_guessed()
    {
        byte[] p = F.Bytes(F.ForceAddMember205836);
        int maskAt = Body(p) + 8; // [_party_key i64] 바로 뒤가 멤버 마스크
        Assert.Equal(0x03, p[maskAt]);
        p[maskAt] = 0x07;
        Assert.False(RosterRecordParser.TryReadForceAddMember(p, Body(p), out _, out _));
    }

    [Fact]
    public void An_unobserved_ForceInfo_header_mask_is_refused()
    {
        byte[] p = F.Bytes(F.ForceInfo205829);
        Assert.Equal(0x00, p[Body(p)]);
        p[Body(p)] = 0x01; // _target_marker_list 가 실렸을지 모른다 — 폭을 모르니 추측하지 않는다
        Assert.False(RosterRecordParser.TryReadForceInfo(p, Body(p), out _));
    }
}
