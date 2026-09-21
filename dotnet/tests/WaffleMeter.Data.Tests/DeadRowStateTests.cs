using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 죽어 있는 동안 그 행을 흐리게 그리기 위한 상태. 시작은 0x8D04(사망 브로드캐스트)와 파티 HP 의 hp==0 중
/// 먼저 오는 쪽, 해제는 <b>"살아 있다"는 증거</b>뿐이다.
///
/// <para>🔴 <b>전체가 fail-open 이다.</b> 살아서 딜하는 사람을 회색으로 두면 옆 칸의 오르는 DPS 와 정면
/// 모순이라 미터 신뢰를 깎지만, 놓친 사망은 그냥 기능이 안 보일 뿐이다. 그래서 해제는 OR 이고, uid 존재
/// 게이트가 없고, 시간 타임아웃이 없고, 딜 한 방이면 무조건 풀린다.</para>
///
/// <para>실측 근거: 사망 창 41건 전부 해제 성공(부활석 제자리 부활 7건 포함), <c>live==0 ⟺ hp==0</c> 이
/// 41/41 로 한 번도 어긋나지 않음 — 즉 둘 중 어느 쪽이 권위인지 <b>증거가 없고</b>, 증거가 없으면 fail-open
/// 이 정한다.</para>
/// </summary>
public sealed class DeadRowStateTests
{
    private const int Me = 100;
    private const int Mate = 200;
    private const int Stranger = 999;
    private const int Boss = 500;
    private const int BossCode = 2311402;

    private static long _now;

    private static DataManager Party()
    {
        _now = 1_000_000;
        var dm = new DataManager { Clock = () => _now };
        dm.LoadMobs(new Dictionary<int, Mob> { [BossCode] = new Mob(BossCode, "나사라크", Boss: true) });
        dm.SaveMobId(Boss, BossCode);
        dm.SaveNickname(Me, "와플", isExecutor: true, server: 2003, jobByte: 29);
        dm.SaveNickname(Mate, "마이농", isExecutor: false, server: 2003, jobByte: 45);
        return dm;
    }

    private static void Damage(DataManager dm, int actor) =>
        dm.SaveDamage(
            new ParsedDamagePacket { ActorId = actor, TargetId = Boss, SkillCode = 1, Damage = 100 },
            dm.CurrentEpoch());

    [Fact]
    public void A_party_member_at_zero_hp_is_dead_and_comes_back_on_live()
    {
        DataManager dm = Party();

        dm.SaveMemberVitals(Mate, hp: 0, live: 0, arrivedAt: _now);
        Assert.True(dm.IsDead(Mate));

        dm.SaveMemberVitals(Mate, hp: 36575, live: 1, arrivedAt: _now + 4200);
        Assert.False(dm.IsDead(Mate));
    }

    [Fact]
    public void Either_signal_alone_is_enough_to_clear()
    {
        // 🔴 OR 이지 AND 가 아니다. 41창에서 둘이 어긋난 적이 없어 어느 쪽이 권위인지 증거가 없고,
        // 증거가 없으면 살았다는 쪽을 믿는다.
        DataManager a = Party();
        a.SaveMemberVitals(Mate, hp: 0, live: 0, arrivedAt: _now);
        a.SaveMemberVitals(Mate, hp: 0, live: 1, arrivedAt: _now + 1);   // live 만 살았다고 함
        Assert.False(a.IsDead(Mate));

        DataManager b = Party();
        b.SaveMemberVitals(Mate, hp: 0, live: 0, arrivedAt: _now);
        b.SaveMemberVitals(Mate, hp: 900, live: 0, arrivedAt: _now + 1); // hp 만 살았다고 함
        Assert.False(b.IsDead(Mate));
    }

    [Fact]
    public void Damage_from_a_greyed_row_clears_it_immediately()
    {
        // 제자리 부활(부활석) 7건 전부 0.x초 안에 첫 타격이 들어온다. 어떤 해제 신호를 놓쳐도 여기서 풀린다 —
        // "살아서 딜하는데 행은 회색" 을 구조적으로 불가능하게 만드는 줄이다.
        DataManager dm = Party();
        dm.SaveMemberVitals(Mate, hp: 0, live: 0, arrivedAt: _now);
        Assert.True(dm.IsDead(Mate));

        Damage(dm, Mate);

        Assert.False(dm.IsDead(Mate));
    }

    [Fact]
    public void The_death_broadcast_also_marks()
    {
        // 0x8D04 는 파티 HP 보다 최대 1초 빠르다. 다만 AoI 사각에서 통째로 빠지기도 한다(재현율 13/14)
        // — 그래서 두 신호를 합친다.
        DataManager dm = Party();

        dm.SaveEntityDeath(Mate, _now);

        Assert.True(dm.IsDead(Mate));
    }

    [Fact]
    public void An_unknown_entity_is_never_marked_but_is_always_cleared()
    {
        // 0x8D04 의 99% 가 몹이다. 마킹은 아는 유저에게만.
        DataManager dm = Party();
        dm.SaveEntityDeath(Stranger, _now);
        Assert.False(dm.IsDead(Stranger));

        // 🔴 반대로 해제에는 게이트가 없어야 한다. UserRepository 가 캐릭터당 uid 를 3개로 제한해 축출하므로,
        // 같은 캐릭터가 네 번째로 재인스턴스되면 옛 uid 의 Exist() 가 false 로 떨어진다 — 그때 해제를
        // 버리면 그 행은 영구 회색이다. 모르는 uid 를 지우는 건 무해하다.
        dm.SaveMemberVitals(Mate, hp: 0, live: 0, arrivedAt: _now);
        Assert.True(dm.IsDead(Mate));
        dm.SaveMemberVitals(Mate, hp: 1, live: 1, arrivedAt: _now + 1);
        Assert.False(dm.IsDead(Mate));
    }

    [Fact]
    public void The_executor_is_tracked_through_its_own_hp_path()
    {
        // 본인은 파티 HP 브로드캐스트에 **안 실린다** — 0x8D00 statId 0 이 유일한 경로다.
        DataManager dm = Party();

        dm.ObserveEntityHp(Me, currentHp: 0);
        Assert.True(dm.IsDead(Me));

        dm.ObserveEntityHp(Me, currentHp: 36575);
        Assert.False(dm.IsDead(Me));
    }

    [Fact]
    public void A_party_member_is_not_judged_by_the_self_hp_path()
    {
        // ⚠️ 이 경로를 파티원으로 넓히면 안 된다. 0x8D00 은 파티원 HP 도 싣지만 AoI 희소성 때문에
        // hp>0 복귀가 40~64초 늦는 사례가 실측으로 있다(진실 14.70초 ↔ 이 경로 78.50초).
        DataManager dm = Party();

        dm.ObserveEntityHp(Mate, currentHp: 0);

        Assert.False(dm.IsDead(Mate));
    }

    [Fact]
    public void Ending_the_battle_clears_everyone()
    {
        // 시간 타임아웃을 일부러 안 둔다(직전 코퍼스에 203~235초짜리 사망 구간이 있다). 대신 전투 경계가
        // 유일한 안전망이다 — 사망 직후 존 전환이 끼면 그 키의 HP 브로드캐스트가 영영 안 온다.
        DataManager dm = Party();
        dm.StartBattle(Boss);
        dm.SaveMemberVitals(Mate, hp: 0, live: 0, arrivedAt: _now);
        dm.ObserveEntityHp(Me, currentHp: 0);
        Assert.True(dm.IsDead(Mate));
        Assert.True(dm.IsDead(Me));

        dm.EndBattle(Boss);

        Assert.False(dm.IsDead(Mate));
        Assert.False(dm.IsDead(Me));
    }

    [Fact]
    public void Repeated_frames_are_idempotent()
    {
        // 공대에서는 같은 키가 0x921B 와 0x962B 양쪽에 실려 온다.
        DataManager dm = Party();

        dm.SaveMemberVitals(Mate, hp: 0, live: 0, arrivedAt: _now);
        dm.SaveMemberVitals(Mate, hp: 0, live: 0, arrivedAt: _now + 50);
        Assert.True(dm.IsDead(Mate));

        dm.SaveMemberVitals(Mate, hp: 500, live: 1, arrivedAt: _now + 4200);
        dm.SaveMemberVitals(Mate, hp: 500, live: 1, arrivedAt: _now + 4250);
        Assert.False(dm.IsDead(Mate));
    }

    [Fact]
    public void An_out_of_range_live_byte_does_not_decide_on_its_own()
    {
        // live 가 {0,1} 밖이면 그 필드는 못 믿는다 — hp 가 판정한다.
        DataManager dm = Party();

        dm.SaveMemberVitals(Mate, hp: 900, live: 7, arrivedAt: _now);
        Assert.False(dm.IsDead(Mate));

        dm.SaveMemberVitals(Mate, hp: 0, live: 7, arrivedAt: _now + 1);
        Assert.True(dm.IsDead(Mate));
    }
}
