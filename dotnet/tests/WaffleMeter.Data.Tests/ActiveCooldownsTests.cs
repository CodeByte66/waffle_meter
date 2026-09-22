using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 스킬 쿨타임 오버레이가 데이터 계층에서 받는 행(<see cref="DataManager.ActiveCooldowns"/>)의 스펙.
/// <para>캐릭터가 인식되면 그 직업의 카탈로그가 <b>통째로</b> 깔린다 — 스킬을 한 번 써야 칸이 생기면 픽커가
/// 고장난 것처럼 보이기 때문이다. 아직 아무 정보가 없는 칸은 '미확인'이 아니라 <b>쓸 수 있음</b>으로 그린다:
/// 클라이언트가 접속 시점 스냅샷을 안 주므로 쿨 도는 중간에 켜면 실제로 모르지만, 그 창은 좁고 한 번 쓰면
/// 닫힌다. 직업을 아직 모르면 서버가 알려 온 스킬만 그린다.</para>
/// </summary>
public sealed class ActiveCooldownsTests
{
    private const int Me = 700;
    private const int Ally = 701;

    // 실제 배포 카탈로그를 쓴다 — 여기서 쓰는 코드가 카탈로그에서 사라지면 그것도 잡아야 할 회귀다.
    private const int BlessedBow = 14_220_000;      // 궁성 축복의 활
    private const int BlessedBowCast = 14_220_050;  // 그 스킬의 특화 변종(와이어가 실제로 싣는 코드)
    private const int Weisel = 14_310_000;          // 궁성 바이젤의 권능

    private static CooldownCatalog ShippedCatalog()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "Assets", "json", "cooldown_catalog.json");
            if (File.Exists(candidate))
            {
                return CooldownCatalog.Load(candidate);
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("cooldown_catalog.json not found above " + AppContext.BaseDirectory);
    }

    private static DataManager WithSelf(long t0)
    {
        var dm = new DataManager { Clock = () => t0 };
        dm.LoadCooldownCatalog(ShippedCatalog());
        dm.SaveNickname(Me, "본인", isExecutor: true, server: 3, jobByte: 0);
        return dm;
    }

    /// <summary>궁성(RANGER) 로 인식되는 job 바이트. ConvertFromCode 13~16 이 RANGER 다.</summary>
    private const int RangerJobByte = 13;

    private static DataManager WithSelfJob(long t0, int jobByte)
    {
        var dm = new DataManager { Clock = () => t0 };
        dm.LoadCooldownCatalog(ShippedCatalog());
        dm.SaveNickname(Me, "본인", isExecutor: true, server: 3, jobByte: jobByte);
        return dm;
    }

    [Fact]
    public void A_recognised_job_lays_out_its_whole_catalogue_before_anything_is_cast()
    {
        // 픽커에서 체크해 둔 스킬이 "한 번 눌러야" 나타나면 픽커가 고장난 것처럼 보인다.
        long t0 = 1_000_000;
        DataManager dm = WithSelfJob(t0, RangerJobByte);

        IReadOnlyList<SkillCooldownView> rows = dm.ActiveCooldowns(t0);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(14, r.Job));
        Assert.Contains(rows, r => r.GroupId == BlessedBow);
        Assert.Contains(rows, r => r.GroupId == Weisel);
    }

    [Fact]
    public void An_untouched_skill_reads_as_ready_with_no_ring()
    {
        // 아직 아무 정보가 없는 칸은 '미확인'이 아니라 '쓸 수 있음'으로 그린다. 오버레이를 쿨 도는 중간에
        // 켜는 일이 드물고, 한 번 쓰면 그 순간부터 실제 값이 들어온다.
        long t0 = 1_000_000;
        DataManager dm = WithSelfJob(t0, RangerJobByte);

        SkillCooldownView row = Assert.Single(dm.ActiveCooldowns(t0), r => r.GroupId == BlessedBow);
        Assert.True(row.IsReady);
        Assert.Equal(0, row.RemainingMs);
        Assert.Equal(0, row.TotalMs); // 클라 테이블 값으로 채우면 쿨감이 빠진 거짓 분모가 된다
        Assert.Equal(BlessedBow, row.DisplayCode);
    }

    [Fact]
    public void A_live_cooldown_replaces_the_prefilled_row_rather_than_adding_a_second_one()
    {
        long t0 = 1_000_000;
        DataManager dm = WithSelfJob(t0, RangerJobByte);
        int before = dm.ActiveCooldowns(t0).Count;

        dm.SaveCooldown(BlessedBowCast, 80_000, t0, actorId: Me, fromCast: true);

        IReadOnlyList<SkillCooldownView> rows = dm.ActiveCooldowns(t0 + 20_000);
        Assert.Equal(before, rows.Count);
        SkillCooldownView row = Assert.Single(rows, r => r.GroupId == BlessedBow);
        Assert.False(row.IsReady);
        Assert.Equal(80_000, row.TotalMs);
    }

    [Fact]
    public void Only_the_recognised_jobs_skills_are_prefilled()
    {
        long t0 = 1_000_000;
        DataManager dm = WithSelfJob(t0, RangerJobByte);

        Assert.DoesNotContain(dm.ActiveCooldowns(t0), r => r.Job != 14);
    }

    [Fact]
    public void A_reported_skill_from_another_band_survives_a_wrong_job_byte()
    {
        // 직업 바이트가 틀리게 와도, 라이브 데이터가 있는 스킬까지 지워 버리면 안 된다.
        long t0 = 1_000_000;
        DataManager dm = WithSelfJob(t0, RangerJobByte);
        dm.SaveCooldown(11_120_000, 10_000, t0, actorId: 0); // 검성 피의 흡수

        Assert.Contains(dm.ActiveCooldowns(t0 + 1_000), r => r.GroupId == 11_120_000);
    }

    [Fact]
    public void With_no_recognised_job_only_reported_skills_are_drawn()
    {
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);
        Assert.Empty(dm.ActiveCooldowns(t0));

        dm.SaveCooldown(BlessedBowCast, 80_000, t0, actorId: Me, fromCast: true);

        SkillCooldownView row = Assert.Single(dm.ActiveCooldowns(t0 + 1_000));
        Assert.Equal(BlessedBow, row.GroupId);
        Assert.Equal("축복의 활", row.Name);
        Assert.Equal(BlessedBowCast, row.DisplayCode);
    }

    [Fact]
    public void The_cast_frame_supplies_the_ring_denominator()
    {
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);
        dm.SaveCooldown(BlessedBowCast, 80_000, t0, actorId: Me, fromCast: true);

        SkillCooldownView row = Assert.Single(dm.ActiveCooldowns(t0 + 20_000));
        Assert.Equal(80_000, row.TotalMs);
        Assert.Equal(60_000, row.RemainingMs);
        Assert.False(row.IsReady);
    }

    [Fact]
    public void A_correction_never_shrinks_the_denominator()
    {
        // 0x3847 은 '남은 시간'만 싣는다. 그걸 총량으로 삼으면 링의 분모가 매 틱 줄어들어 60초 쿨이 3초처럼
        // 보인다 — 오버레이가 조용히 거짓말하는 가장 쉬운 길이다.
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);
        dm.SaveCooldown(BlessedBowCast, 80_000, t0, actorId: Me, fromCast: true);
        dm.SaveCooldown(BlessedBow, 30_000, t0 + 50_000, actorId: 0);

        SkillCooldownView row = Assert.Single(dm.ActiveCooldowns(t0 + 50_000));
        Assert.Equal(80_000, row.TotalMs);
        Assert.Equal(30_000, row.RemainingMs);
    }

    [Fact]
    public void A_ready_skill_stays_on_the_list_as_a_ready_row()
    {
        // 준비된 스킬이 목록에서 사라지면 아이콘 자리가 계속 바뀌어 근육기억으로 읽을 수 없다.
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);
        dm.SaveCooldown(BlessedBowCast, 5_000, t0, actorId: Me, fromCast: true);

        SkillCooldownView row = Assert.Single(dm.ActiveCooldowns(t0 + 6_000));
        Assert.True(row.IsReady);
        Assert.Equal(0, row.RemainingMs);
    }

    [Fact]
    public void A_cast_inside_the_grace_window_reads_as_ready()
    {
        // 시전 값은 잠정이다 — 서버가 곧바로 초기화하고 0x3847 로 알려 오는 경우가 흔해서, 그 250~350ms 를
        // 쿨로 칠하면 연타 스킬에서 상시 깜빡인다.
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);
        dm.SaveCooldown(BlessedBowCast, 80_000, t0, actorId: Me, fromCast: true);

        Assert.True(Assert.Single(dm.ActiveCooldowns(t0 + 100)).IsReady);
        Assert.False(Assert.Single(dm.ActiveCooldowns(t0 + 500)).IsReady);
    }

    [Fact]
    public void Another_players_cast_is_not_my_cooldown()
    {
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);
        dm.SaveCooldown(BlessedBowCast, 80_000, t0, actorId: Ally, fromCast: true);

        Assert.Empty(dm.ActiveCooldowns(t0 + 1_000));
    }

    [Fact]
    public void Casts_that_arrive_before_the_executor_is_known_are_replayed_not_dropped()
    {
        // 실측: 한 세션에서 본인 시전 811건 중 224건(27.6%)이 본인 닉네임 패킷보다 먼저 도착했다.
        long t0 = 1_000_000;
        var dm = new DataManager { Clock = () => t0 };
        dm.LoadCooldownCatalog(ShippedCatalog());

        dm.SaveCooldown(BlessedBowCast, 80_000, t0, actorId: Me, fromCast: true);
        Assert.Empty(dm.ActiveCooldowns(t0 + 1_000)); // executor 미확정 — 아직 내 것인지 모른다

        dm.Clock = () => t0 + 2_000;
        dm.SaveNickname(Me, "본인", isExecutor: true, server: 3, jobByte: 0);

        SkillCooldownView row = Assert.Single(dm.ActiveCooldowns(t0 + 2_000));
        Assert.Equal(BlessedBow, row.GroupId);
        Assert.Equal(78_000, row.RemainingMs);
    }

    [Fact]
    public void A_staged_cast_that_already_expired_is_not_resurrected()
    {
        long t0 = 1_000_000;
        var dm = new DataManager { Clock = () => t0 };
        dm.LoadCooldownCatalog(ShippedCatalog());

        dm.SaveCooldown(BlessedBowCast, 5_000, t0, actorId: Me, fromCast: true);

        dm.Clock = () => t0 + 60_000;
        dm.SaveNickname(Me, "본인", isExecutor: true, server: 3, jobByte: 0);

        Assert.Empty(dm.ActiveCooldowns(t0 + 60_000));
    }

    [Fact]
    public void Rows_are_ordered_by_job_then_by_the_catalog_order()
    {
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);
        dm.SaveCooldown(Weisel, 40_000, t0, actorId: 0);
        dm.SaveCooldown(BlessedBow, 80_000, t0, actorId: 0);

        IReadOnlyList<SkillCooldownView> rows = dm.ActiveCooldowns(t0 + 1_000);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Order < rows[1].Order);
        Assert.Equal(BlessedBow, rows[0].GroupId); // 14220000 < 14310000
    }

    [Fact]
    public void Skills_outside_the_catalog_are_not_drawn()
    {
        // 대시(1101) 같은 공용 코드는 0x3847 이 실제로 실어 보내지만 이름도 아이콘도 없다 — 번호만 뜬 슬롯을
        // 그리느니 빼는 편이 낫다.
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);
        dm.SaveCooldown(1101, 3_000, t0, actorId: 0);

        Assert.Empty(dm.ActiveCooldowns(t0 + 1_000));
    }

    /// <summary>궁성 = 밴드 14, 마도성 = 밴드 15. jobByte 는 다른 공간이다(ConvertFromCode: 궁성 13~16, 마도성 25~28).</summary>
    private const int SorcererJobByte = 25;
    private const int SharedUid = 702;

    /// <summary>
    /// 🔴 <b>남이 쓰던 uid 를 본인이 물려받으면, 그 남의 시전이 내 쿨타임 줄로 넘어오면 안 된다.</b>
    ///
    /// <para>제보: "스킬쿨타임이 마도인데 궁성스킬이 나옵니다". 직업 인식은 멀쩡했다 — 코퍼스 실측에서
    /// jobByte↔실제 직업 불일치는 0종이었고 마도성은 26·28을 달고 와 정상 판정된다. 범인은 <b>보류 시전</b>이다.</para>
    ///
    /// <para>엔티티 id 는 풀 사이에 재사용된다(<c>DpsCalculator.ResolveActor</c> 가 그 성질에 기대고 있다).
    /// executor 가 아직 0 인 창에서는 <c>SaveCooldown</c> 이 <b>누구의</b> 시전이든 그 uid 앞으로 스테이징하고
    /// (본인 시전이 0x3633 보다 먼저 오는 비율이 실측 27.6% 라 필요한 장치다), 나중에 그 uid 가 executor 로
    /// 확정되면 통째로 재생한다. 그 사이에 uid 주인이 바뀌면 <b>남의 쿨이 내 것이 된다.</b></para>
    ///
    /// <para>닉네임이 바뀌는 순간 이걸 무효화하는 분기가 <c>DataManager.SaveNickname</c> 에 이미 있는데,
    /// 거기서 지우는 건 <c>_pendingSelfBuffs</c> 뿐이었다 — 쿨타임 쌍둥이(v2.12.2)가 나중에 생기면서 그 자리가
    /// 같이 갱신되지 않았다. 그래서 버프는 막히고 쿨타임만 샜다.</para>
    /// </summary>
    [Fact]
    public void A_stranger_who_had_my_uid_first_does_not_leave_their_cooldowns_on_my_overlay()
    {
        long t0 = 1_000_000;
        var dm = new DataManager { Clock = () => t0 };
        dm.LoadCooldownCatalog(ShippedCatalog());

        // 궁성 파티원이 그 uid 를 쓰는 동안 시전한다. 본인이 아직 미확정이라 '본인 후보'로 스테이징된다.
        dm.SaveNickname(SharedUid, "궁성파티원", isExecutor: false, server: 3, jobByte: RangerJobByte);
        dm.SaveCooldown(BlessedBowCast, 80_000, t0, actorId: SharedUid, fromCast: true);

        // 같은 uid 를 본인(마도성)이 물려받는다 — 닉네임이 바뀌므로 미터도 점유자 교체를 안다.
        dm.Clock = () => t0 + 2_000;
        dm.SaveNickname(SharedUid, "마도본인", isExecutor: true, server: 3, jobByte: SorcererJobByte);

        IReadOnlyList<SkillCooldownView> rows = dm.ActiveCooldowns(t0 + 2_000);

        Assert.All(rows, r => Assert.Equal(15, r.Job));   // 마도성 칸만 있어야 한다
        Assert.DoesNotContain(rows, r => r.GroupId == BlessedBow);
    }

    /// <summary>같은 사람이 같은 uid 를 계속 쓰는 정상 경로는 그대로여야 한다 — 위 수정이 보류 재생 자체를
    /// 죽이면 본인 시전의 27.6% 가 사라진다.</summary>
    [Fact]
    public void The_same_character_keeping_its_uid_still_gets_its_staged_casts_back()
    {
        long t0 = 1_000_000;
        var dm = new DataManager { Clock = () => t0 };
        dm.LoadCooldownCatalog(ShippedCatalog());

        dm.SaveNickname(SharedUid, "본인", isExecutor: false, server: 3, jobByte: RangerJobByte);
        dm.SaveCooldown(BlessedBowCast, 80_000, t0, actorId: SharedUid, fromCast: true);

        dm.Clock = () => t0 + 2_000;
        dm.SaveNickname(SharedUid, "본인", isExecutor: true, server: 3, jobByte: RangerJobByte);

        SkillCooldownView row = Assert.Single(
            dm.ActiveCooldowns(t0 + 2_000).Where(r => r.GroupId == BlessedBow && !r.IsReady));
        Assert.Equal(78_000, row.RemainingMs);
    }
}
