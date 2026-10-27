using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 포스(20인 공대) 로스터 상태기 <see cref="ForceRoster"/> 와 그 DataManager 배선. 시나리오는 실측 20261027-205641 의 순서를
/// 그대로 따른다: 스냅샷(2파티 9명) → 재접속(Violette uid 채움) → 추가(순두부) → 파티 추가(3파티 둠벙) → 이동(순두부 ↔ 무적자)
/// → 제거(쉰). 사람은 dbid 로 지목한다 — 엔티티 uid 는 존마다 바뀌고 다른 존이면 0 이다.
/// </summary>
public sealed class ForceRosterStateMachineTests
{
    private const long P1 = 0x24135ED200148F37L, P2 = 0x24135ED200149019L, P3 = 0x24135ED200149092L;

    private static long Dbid(long id, int server = 2003) => ((long)server << 48) | id;

    private static WireRosterMember M(string nick, long id, int slot, int uid, int pc = 16, long power = 0, int server = 2003) =>
        new(slot, uid, Dbid(id, server), nick, pc, power, Login: true);

    private static ForceSnapshot Snapshot(params ForceParty[] parties) => new(0x24135ED2000224C0L, Dbid(185206), 20, parties, 1);

    private static ForceParty Party(long key, int number, params WireRosterMember[] members) => new(key, number, 5, members);

    private static readonly WireRosterMember Swin = M("쉰", 5014, 3, 5565, power: 854294);
    private static readonly WireRosterMember VioletteAway = M("Violette", 5238, 1, 0, pc: 28, power: 648911);
    private static readonly WireRosterMember Eut = M("읓", 33144, 2, 7423, pc: 20, power: 1056174);
    private static readonly WireRosterMember Konpang = M("콘팡", 45996, 4, 2851, power: 883941);
    private static readonly WireRosterMember Sundubu = M("순두부", 112719, 5, 3980, pc: 23, power: 824939);
    private static readonly WireRosterMember Mujeok = M("무적자", 287, 5, 9114, pc: 36, power: 882255);
    private static readonly WireRosterMember Dumbung = M("둠벙", 32606, 1, 12625, power: 850901);

    private static ForceRoster Seeded(out long now)
    {
        now = 1_000_000;
        var f = new ForceRoster();
        f.Apply(Snapshot(
            Party(P1, 1, M("콩령", 185206, 1, 14527, pc: 24, power: 1030271)),
            Party(P2, 2, Swin, VioletteAway, Eut, Konpang)), now);
        return f;
    }

    private static string[] Names(ForceRosterParty p) => p.Members.Select(m => m.Nickname).ToArray();

    [Fact]
    public void A_snapshot_lays_out_parties_by_number_and_members_by_slot()
    {
        ForceRosterView v = Seeded(out _).View()!;
        Assert.Equal(20, v.LimitMember);
        Assert.Equal(new[] { 1, 2 }, v.Parties.Select(p => p.Number));
        Assert.Equal(new[] { "Violette", "읓", "쉰", "콘팡" }, Names(v.Parties[1]));
    }

    [Fact]
    public void The_full_observed_sequence_ends_with_the_servers_own_party_lists()
    {
        ForceRoster f = Seeded(out long now);
        f.Reconnect(VioletteAway with { EntityUid = 7982, CombatPower = 1024066 }, now += 100);
        f.AddMember(P2, Sundubu, now += 7_000);
        f.AddParty(Party(P3, 3, Dumbung), now += 5_000);
        Assert.Equal(new[] { "Violette", "읓", "쉰", "콘팡", "순두부" }, Names(f.View()!.Parties[1]));

        // 이동: 실린 두 파티가 이동 후 전체 목록이다 — 순두부는 3파티 4번, 무적자가 2파티 5번.
        f.Move(new[]
        {
            Party(P2, 2, Mujeok, Swin, VioletteAway with { EntityUid = 7982 }, Eut, Konpang),
            Party(P3, 3, Dumbung, Sundubu with { Slot = 4 }),
        }, now += 30_000);
        ForceRosterView v = f.View()!;
        Assert.Equal(new[] { "Violette", "읓", "쉰", "콘팡", "무적자" }, Names(v.Parties[1]));
        Assert.Equal(new[] { "둠벙", "순두부" }, Names(v.Parties[2]));

        f.Remove(Dbid(5014), now += 1_000); // 쉰 LeaveMember
        Assert.Equal(new[] { "Violette", "읓", "콘팡", "무적자" }, Names(f.View()!.Parties[1]));
        f.Remove(Dbid(5014), now += 1_000); // 같은 제거가 한 번 더 와도(중복 스트림) 무해
        Assert.Equal(4, f.View()!.Parties[1].Members.Count);
    }

    [Fact]
    public void A_reconnect_fills_the_entity_uid_of_a_member_who_was_in_another_zone()
    {
        ForceRoster f = Seeded(out long now);
        Assert.Equal(0, f.View()!.Parties[1].Members.Single(m => m.Nickname == "Violette").EntityUid);
        f.Reconnect(VioletteAway with { EntityUid = 7982 }, now + 100);
        Assert.Equal(7982, f.View()!.Parties[1].Members.Single(m => m.Nickname == "Violette").EntityUid);
    }

    [Fact]
    public void A_reconnect_of_someone_the_roster_does_not_hold_invents_nobody()
    {
        ForceRoster f = Seeded(out long now);
        f.Reconnect(Sundubu, now + 100); // 파티를 모르는 사람 — 만들지 않는다
        Assert.DoesNotContain(f.View()!.Parties.SelectMany(p => p.Members), m => m.Nickname == "순두부");
    }

    [Fact]
    public void Members_are_keyed_by_dbid_so_a_new_zone_uid_updates_the_same_person()
    {
        ForceRoster f = Seeded(out long now);
        f.Apply(Snapshot(Party(P2, 2, Swin, Konpang with { EntityUid = 12208 })), now + 1_000);
        ForceRosterMember self = f.View()!.Parties.Single().Members.Single(m => m.Nickname == "콘팡");
        Assert.Equal(12208, self.EntityUid);
        Assert.Single(f.View()!.Parties.SelectMany(p => p.Members), m => m.Dbid == Dbid(45996));
    }

    [Fact]
    public void An_entity_uid_belongs_to_one_member_at_a_time()
    {
        // 존이 바뀌면 같은 uid 가 다른 사람에게 다시 붙는다. 낡은 쪽은 '다른 존(0)'으로 내려야 조인이 엉뚱한 User 를 안 고른다.
        ForceRoster f = Seeded(out long now);
        f.Reconnect(VioletteAway with { EntityUid = 2851 }, now + 100); // 콘팡이 들고 있던 uid
        ForceRosterParty p = f.View()!.Parties[1];
        Assert.Equal(2851, p.Members.Single(m => m.Nickname == "Violette").EntityUid);
        Assert.Equal(0, p.Members.Single(m => m.Nickname == "콘팡").EntityUid);
    }

    [Fact]
    public void Combat_power_is_the_latest_value_received_with_its_arrival_time()
    {
        ForceRoster f = Seeded(out long now);
        f.UpdateStatus(new ForceMemberStatus(Dbid(5238), 28, 893426), now + 50);
        Assert.Equal((893426L, now + 50), PowerOf(f, "Violette"));
        f.Reconnect(VioletteAway with { EntityUid = 7982, CombatPower = 1024066 }, now + 100);
        Assert.Equal((1024066L, now + 100), PowerOf(f, "Violette"));
        f.UpdateStatus(new ForceMemberStatus(Dbid(5238), 28, 0), now + 200); // 전투력 없는 상태는 값을 지우지 않는다
        Assert.Equal((1024066L, now + 100), PowerOf(f, "Violette"));
    }

    [Fact]
    public void A_status_for_an_unknown_dbid_creates_nobody()
    {
        var f = new ForceRoster();
        f.UpdateStatus(new ForceMemberStatus(Dbid(5238), 28, 893426), 1);
        Assert.Null(f.View());
    }

    private static (long Power, long At) PowerOf(ForceRoster f, string nick)
    {
        ForceRosterMember m = f.View()!.Parties.SelectMany(p => p.Members).Single(x => x.Nickname == nick);
        return (m.Power, m.PowerAtMs);
    }

    [Fact]
    public void DataManager_serves_the_force_roster_only_while_fresh_and_drops_it_on_reset()
    {
        long clock = 1_000_000;
        var dm = new DataManager { Clock = () => clock };
        dm.SaveForceInfo(Snapshot(Party(P2, 2, Swin, Konpang)));
        Assert.NotNull(dm.ForceRosterView(300_000));
        clock += 299_000;
        dm.UpdateForceMemberStatus(new ForceMemberStatus(Dbid(5014), 16, 854294)); // 상태 패킷도 신선도를 갱신한다
        clock += 299_000;
        Assert.NotNull(dm.ForceRosterView(300_000));
        clock += 2_000;
        Assert.Null(dm.ForceRosterView(300_000));

        dm.SaveForceInfo(Snapshot(Party(P2, 2, Swin)));
        dm.ResetBattleRecords(); // 0x9702 방 로스터와 같은 규칙 — 초기화 뒤 낡은 공대가 다시 뜨지 않는다
        Assert.Null(dm.ForceRosterView(300_000));
    }

    [Fact]
    public void A_ForceRemoveMember_through_DataManager_removes_by_dbid()
    {
        var dm = new DataManager { Clock = () => 1_000 };
        dm.SaveForceInfo(Snapshot(Party(P2, 2, Swin, Konpang)));
        dm.RemoveForceMember(Dbid(5014), P2);
        Assert.Equal(new[] { "콘팡" }, Names(dm.ForceRosterView(300_000)!.Parties.Single()));
    }
}
