using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 스키마 워크로 읽은 0x9200(본인 파티 전체 스냅샷)·0x920D(한 명 추가)의 데이터 계층 처리.
/// ① 다른 존에 있어 uid 가 0 인 파티원을 (닉네임, 서버)로 남긴다(종전엔 버려졌다 — 실측 Violette).
/// ② 스냅샷에서 빠진 사람의 프로필을 지운다(종전엔 제거 경로가 없어 5분 TTL 동안 프리뷰에 남았다 — 실측 21:02:31 Violette 이탈).
/// ③ 스냅샷의 파티 key 를 '내 파티' 표식으로 남긴다 — 20인 공대에서 본인 uid 를 모를 때 포스 안 내 파티를 고르는 근거.
/// </summary>
public sealed class DataManagerMemberProfileSnapshotTests
{
    private const long OwnKey = 0x24135ED200149019L;

    private static WireRosterMember M(string nick, int uid, long id) => new(1, uid, (2003L << 48) | id, nick, 16, 0, true);

    // 파서가 하는 순서 그대로: uid 있는 멤버는 SaveMemberProfile, 그다음 스냅샷 전체.
    private static void Feed(DataManager dm, params WireRosterMember[] members)
    {
        foreach (WireRosterMember m in members.Where(m => m.EntityUid > 0))
        {
            dm.SaveMemberProfile(m.EntityUid, m.Nickname, m.Server);
        }

        dm.SaveMemberProfileSnapshot(OwnKey, members);
    }

    [Fact]
    public void A_member_in_another_zone_is_kept_as_a_name_only_entry_until_it_returns_with_a_uid()
    {
        long clock = 1_000_000;
        var dm = new DataManager { Clock = () => clock };
        Feed(dm, M("쉰", 5565, 5014), M("Violette", 0, 5238), M("콘팡", 2851, 45996));
        Assert.Equal(new[] { ("Violette", 2003) }, dm.MemberProfileOffZone(300_000));

        clock += 1_000;
        Feed(dm, M("쉰", 5565, 5014), M("Violette", 7982, 5238), M("콘팡", 2851, 45996));
        Assert.Empty(dm.MemberProfileOffZone(300_000));
        Assert.Contains((7982, "Violette", 2003), dm.MemberProfileRoster(300_000));
    }

    [Fact]
    public void A_member_who_left_the_party_drops_out_on_the_next_snapshot()
    {
        long clock = 1_000_000;
        var dm = new DataManager { Clock = () => clock };
        Feed(dm, M("무적자", 9114, 287), M("Violette", 7982, 5238), M("읓", 7423, 33144), M("콘팡", 2851, 45996));
        clock += 8_000;
        Feed(dm, M("무적자", 9114, 287), M("읓", 7423, 33144), M("콘팡", 2851, 45996)); // Violette 이탈

        Assert.DoesNotContain(dm.MemberProfileRoster(300_000), p => p.Nickname == "Violette");
        Assert.Equal(3, dm.MemberProfileRoster(300_000).Count);
    }

    [Fact]
    public void A_stale_uid_of_someone_who_is_now_elsewhere_is_dropped_too()
    {
        long clock = 1_000_000;
        var dm = new DataManager { Clock = () => clock };
        Feed(dm, M("쉰", 5565, 5014), M("콘팡", 2851, 45996));
        clock += 1_000;
        Feed(dm, M("쉰", 0, 5014), M("콘팡", 12208, 45996)); // 존 이동: 쉰은 다른 존, 콘팡은 새 uid

        Assert.Equal(new[] { (12208, "콘팡", 2003) }, dm.MemberProfileRoster(300_000));
        Assert.Equal(new[] { ("쉰", 2003) }, dm.MemberProfileOffZone(300_000));
    }

    [Fact]
    public void PartyAddMember_without_a_uid_is_kept_by_name()
    {
        var dm = new DataManager { Clock = () => 1_000 };
        dm.AddMemberProfile(M("헐", 0, 44087));
        dm.AddMemberProfile(M("순두부", 3980, 112719)); // uid 있는 쪽은 SaveMemberProfile 이 받는다
        Assert.Equal(new[] { ("헐", 2003) }, dm.MemberProfileOffZone(300_000));
    }

    [Fact]
    public void The_snapshot_party_key_marks_my_party_while_fresh()
    {
        long clock = 1_000_000;
        var dm = new DataManager { Clock = () => clock };
        Assert.Equal(0, dm.OwnPartyKey(300_000));
        Feed(dm, M("콘팡", 2851, 45996));
        Assert.Equal(OwnKey, dm.OwnPartyKey(300_000));
        clock += 300_001;
        Assert.Equal(0, dm.OwnPartyKey(300_000));
    }

    [Fact]
    public void A_power_write_is_time_stamped_for_the_latest_wins_comparison()
    {
        long clock = 1_000_000;
        var dm = new DataManager { Clock = () => clock };
        dm.SaveNickname(5565, "쉰", isExecutor: false, server: 2003, jobByte: 16);
        Assert.Equal(0, dm.UserPowerObservedAtMs(5565));
        clock += 500;
        dm.SaveUserPower(5565, 854294);
        Assert.Equal(clock, dm.UserPowerObservedAtMs(5565));
        dm.SaveUserPower(4242, 854294); // 모르는 uid — 저장되지 않았으니 시각도 없다
        Assert.Equal(0, dm.UserPowerObservedAtMs(4242));
    }
}
