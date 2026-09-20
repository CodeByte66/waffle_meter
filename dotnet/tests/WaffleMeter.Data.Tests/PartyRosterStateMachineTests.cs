using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 로스터를 <b>스냅샷 하나</b>가 아니라 <b>상태</b>로 들고 있다는 계약.
///
/// <para>🔑 왜. 0x9702 스냅샷은 <b>부분 로스터</b>를 보내는 것이 정상이다 — 코퍼스 실측(2026-07-09 이후):
/// 정원 10 방의 스냅샷 502건 중 완전한 것은 290건(57.8%)이고, 같은 슬롯이 사라졌다 되돌아오는 것도
/// 확인됐다(슬롯 5·7 각 3회). 빠진 멤버의 이름 바이트는 패킷에 <b>아예 없다</b> — 파서 탓이 아니라
/// 서버가 그만큼만 보낸 것이다(결손 519건 중 파싱 실패는 5건, 1.0%).</para>
///
/// <para>종전처럼 스냅샷으로 로스터를 통째 교체하면, 하필 부분 스냅샷 직후 전투가 끝난 경우
/// <c>PartyRosterSize</c> 가 9로 나가고 서버의 <c>RAID_ROSTER_SIZES = [10]</c> 공대 판별에서 탈락한다.
/// 웹 실측(2026-09-19): 성역 30일 비신뢰 2,503건 중 <b>1,385건(55.3%)</b>이 이 사유이고, 그중
/// roster=9 가 1,340건이다.</para>
/// </summary>
public sealed class PartyRosterStateMachineTests
{
    private static DataManager Fresh(long now) => new() { Clock = () => now };

    /// <summary>0x971F — 스냅샷에 안 실린 슬롯을 증분이 메운다.</summary>
    [Fact]
    public void An_increment_fills_a_slot_the_snapshot_left_out()
    {
        DataManager dm = Fresh(1_000_000);
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2) }, partyId: 77);

        dm.UpdatePartyMember("C", 1, 3, key: 4242);

        IReadOnlyList<(string Nickname, int Server, int Slot)> raw = dm.PartyRosterIdentities(300_000);
        Assert.Equal(3, raw.Count);
        Assert.Contains(("C", 1, 3), raw);
    }

    /// <summary>실린 슬롯이 권위다 — 표본 85건에서 슬롯이 달라진 사례가 0건이라 그대로 덮어쓴다.</summary>
    [Fact]
    public void An_increment_overwrites_the_slot_it_names()
    {
        DataManager dm = Fresh(1_000_000);
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2) }, partyId: 77);

        dm.UpdatePartyMember("Z", 1, 2, key: 9); // 슬롯 2가 다른 사람으로 바뀌었다

        IReadOnlyList<(string Nickname, int Server, int Slot)> raw = dm.PartyRosterIdentities(300_000);
        Assert.Equal(2, raw.Count);
        Assert.Contains(("Z", 1, 2), raw);
        Assert.DoesNotContain(("B", 1, 2), raw);
    }

    /// <summary>
    /// 0x9622 — 제거는 <b>key</b> 로만 온다. 스냅샷이 실어 준 key 로 사람을 지목해 지운다.
    /// <para>⚠️ 이름으로 지우면 동명이인·잘린 닉에서 엉뚱한 사람이 빠진다.</para>
    /// </summary>
    [Fact]
    public void A_removal_drops_the_member_that_key_belongs_to()
    {
        DataManager dm = Fresh(1_000_000);
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2) }, partyId: 77);
        dm.SavePartyRosterKeys(new List<(string, int, int)> { ("A", 1, 111), ("B", 1, 222) });

        dm.RemovePartyMemberByKey(222);

        IReadOnlyList<(string Nickname, int Server, int Slot)> raw = dm.PartyRosterIdentities(300_000);
        Assert.Single(raw);
        Assert.Contains(("A", 1, 1), raw);
    }

    [Fact]
    public void A_removal_for_an_unknown_key_changes_nothing()
    {
        DataManager dm = Fresh(1_000_000);
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2) }, partyId: 77);
        dm.SavePartyRosterKeys(new List<(string, int, int)> { ("A", 1, 111), ("B", 1, 222) });

        dm.RemovePartyMemberByKey(999);

        Assert.Equal(2, dm.PartyRosterIdentities(300_000).Count);
    }

    /// <summary>🔑 슬롯이 키라서 정원을 <b>구조적으로</b> 못 넘는다. key 로 쌓으면 떠난 멤버가 안 지워져
    /// 10인 방이 11~13명이 된다(실측 초과 23건) — 9명보다 13명이 더 나쁘다.</summary>
    [Fact]
    public void The_roster_can_never_exceed_the_slots_it_has_seen()
    {
        DataManager dm = Fresh(1_000_000);
        for (int i = 0; i < 40; i++)
        {
            // 같은 슬롯 1..10 에 계속 다른 사람이 들어온다
            dm.UpdatePartyMember($"P{i}", 1, (i % 10) + 1, key: 1000 + i);
        }

        Assert.Equal(10, dm.PartyRosterIdentities(300_000).Count);
    }

    /// <summary>
    /// 🔑 <c>PartyRosterSize</c> = 방 정원(<c>_limit_member</c>), 파싱된 멤버 수가 아니다.
    /// 통계웹 스키마 주석이 이 필드를 처음부터 "로스터 정원"으로 정의한다.
    /// </summary>
    [Fact]
    public void The_roster_size_is_the_room_capacity_not_the_member_count()
    {
        DataManager dm = Fresh(1_000_000);
        dm.SavePartyRosterCapacity(10);
        dm.SavePartyRoster(
            new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2), ("C", 1, 3) }, partyId: 77); // 9명 중 3명만 실림

        (_, int size) = dm.FreshPartySlots(new List<User>());

        Assert.Equal(10, size); // 3 이 아니다
    }

    /// <summary>정원을 모르면(옛 캡처 로그·테스트 경로) 멤버 수로 폴백한다.</summary>
    [Fact]
    public void Without_a_capacity_the_member_count_is_used()
    {
        DataManager dm = Fresh(1_000_000);
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2) }, partyId: 77);

        (_, int size) = dm.FreshPartySlots(new List<User>());

        Assert.Equal(2, size);
    }

    /// <summary>말이 안 되는 정원은 받지 않는다 — 20은 포스 최대, 그 위는 파싱이 어긋난 것이다.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(255)]
    public void An_implausible_capacity_is_ignored(int bogus)
    {
        DataManager dm = Fresh(1_000_000);
        dm.SavePartyRosterCapacity(bogus);
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2) }, partyId: 77);

        (_, int size) = dm.FreshPartySlots(new List<User>());

        Assert.Equal(2, size); // 폴백
    }

    /// <summary>파티가 실제로 교체되면(파티 id 변경) 상태를 버린다 — 그게 '부분 스냅샷'과 구별되는 유일한 신호다.</summary>
    [Fact]
    public void A_new_party_id_resets_the_state()
    {
        DataManager dm = Fresh(1_000_000);
        dm.SavePartyRoster(
            new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2), ("C", 1, 3) }, partyId: 100);

        dm.SavePartyRoster(new List<(string, int, int)> { ("X", 1, 1) }, partyId: 200);

        IReadOnlyList<(string Nickname, int Server, int Slot)> raw = dm.PartyRosterIdentities(300_000);
        Assert.Single(raw);
        Assert.Contains(("X", 1, 1), raw);
    }

    /// <summary>
    /// 슬롯 충돌이 감지된 스냅샷은 파서가 전 멤버의 슬롯을 0 으로 만들어 보낸다. 그런 스냅샷은
    /// 놓을 자리가 없으니 통째로 무시하고 직전 상태를 지킨다 — 확실히 틀린 배치보다 낫다.
    /// </summary>
    [Fact]
    public void A_snapshot_with_no_usable_slots_leaves_the_state_alone()
    {
        DataManager dm = Fresh(1_000_000);
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2) }, partyId: 77);

        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 0), ("B", 1, 0), ("C", 1, 0) }, partyId: 77);

        IReadOnlyList<(string Nickname, int Server, int Slot)> raw = dm.PartyRosterIdentities(300_000);
        Assert.Equal(2, raw.Count);
        Assert.Contains(("A", 1, 1), raw);
        Assert.DoesNotContain(("C", 1, 0), raw);
    }

    /// <summary>초기화는 로스터 상태를 통째로 버린다 — 정원도 같이 잊는다.</summary>
    [Fact]
    public void A_reset_forgets_the_capacity_too()
    {
        DataManager dm = Fresh(1_000_000);
        dm.SavePartyRosterCapacity(10);
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1) }, partyId: 77);

        dm.ResetBattleRecords();
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2) }, partyId: 77);

        (_, int size) = dm.FreshPartySlots(new List<User>());
        Assert.Equal(2, size); // 정원이 아니라 멤버 수로 폴백
    }
}
