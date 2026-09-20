using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 공유 쿨타임 그룹(<c>GroupCoolTimeId</c>)에 두 스킬이 매달린 칸들의 스펙. 권성(job 19)에만 있고 배포
/// 카탈로그 기준 7쌍이다.
/// <para>화면 계약: <b>스킬마다 칸을 하나씩</b> 낸다(픽커 칩이 BaseCode 기준이라 칸을 합치면 한쪽 칩이 아무
/// 칸도 못 켜게 된다). 대신 <b>같은 그룹이면 쿨 상태를 공유</b>한다 — 한쪽을 쓰면 인게임에서 둘 다 쿨이
/// 도니까.</para>
/// <para>고정하는 회귀(M-15): 프리필이 <c>BaseCode</c>로 칸을 만드는데 보고는 <b>그룹 키</b>로 들어와,
/// 그룹 대표가 아닌 7칸이 쿨 도는 내내 "준비됨"으로 매 틱 다시 깔렸다. 짝 칸은 정상으로 파이가 도니 정보
/// 부재가 아니라 <b>중복 칸이 거짓말을 하는</b> 형태였다.</para>
/// </summary>
public sealed class SharedCooldownGroupTests
{
    private const int Me = 700;
    private const int FighterJobByte = 45; // ConvertFromCode 45~48 = FIGHTER(권성), skill band 19

    private const int AscentPokju = 19_150_000; // 승천타[폭주] — 이 쌍의 그룹 대표
    private const int Ascent = 19_160_000;      // 승천타 — gct가 19150000을 가리키는 쪽
    private const int BurstFistPokju = 19_190_000; // 폭렬권[폭주] — 그룹 대표
    private const int BurstFist = 19_200_000;      // 폭렬권
    private const int BurstFistVariant = 19_200_027; // 자기 자신을 가리키는 gctOverride 변종

    // 실제 배포 카탈로그를 쓴다 — 여기서 쓰는 쌍이 카탈로그에서 사라지면 그것도 잡아야 할 회귀다.
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

    private static DataManager WithFighter(long t0)
    {
        var dm = new DataManager { Clock = () => t0 };
        dm.LoadCooldownCatalog(ShippedCatalog());
        dm.SaveNickname(Me, "본인", isExecutor: true, server: 3, jobByte: FighterJobByte);
        return dm;
    }

    [Fact]
    public void Casting_one_skill_of_a_shared_group_cools_both_slots()
    {
        long t0 = 1_000_000;
        DataManager dm = WithFighter(t0);

        dm.SaveCooldown(Ascent, 60_000, t0, actorId: Me, fromCast: true);

        IReadOnlyList<SkillCooldownView> rows = dm.ActiveCooldowns(t0 + 10_000);
        SkillCooldownView cast = Assert.Single(rows, r => r.GroupId == Ascent);
        SkillCooldownView partner = Assert.Single(rows, r => r.GroupId == AscentPokju);

        Assert.False(cast.IsReady);
        Assert.False(partner.IsReady); // ← 회귀 지점: 여기가 true 로 돌아가면 그 칸이 다시 거짓말한다
        Assert.Equal(50_000, partner.RemainingMs);
        Assert.Equal(60_000, partner.TotalMs);
        Assert.Equal(cast.RemainingMs, partner.RemainingMs);
    }

    [Fact]
    public void A_shared_group_still_draws_one_slot_per_skill()
    {
        // 칸을 하나로 합치면 사라진 스킬의 픽커 칩이 아무 칸도 못 켜게 된다 — 그래서 칸은 둘로 유지한다.
        long t0 = 1_000_000;
        DataManager dm = WithFighter(t0);
        int before = dm.ActiveCooldowns(t0).Count;

        dm.SaveCooldown(Ascent, 60_000, t0, actorId: Me, fromCast: true);

        IReadOnlyList<SkillCooldownView> rows = dm.ActiveCooldowns(t0 + 10_000);
        Assert.Equal(before, rows.Count); // 시전이 칸을 늘리지도 줄이지도 않는다
        Assert.Contains(rows, r => r.GroupId == Ascent);
        Assert.Contains(rows, r => r.GroupId == AscentPokju);
        Assert.Equal(Ascent, Assert.Single(rows, r => r.GroupId == Ascent).DisplayCode); // 각 칸은 자기 아이콘
    }

    [Fact]
    public void Before_any_cast_both_slots_read_as_ready()
    {
        long t0 = 1_000_000;
        DataManager dm = WithFighter(t0);

        IReadOnlyList<SkillCooldownView> rows = dm.ActiveCooldowns(t0);
        Assert.True(Assert.Single(rows, r => r.GroupId == Ascent).IsReady);
        Assert.True(Assert.Single(rows, r => r.GroupId == AscentPokju).IsReady);
    }

    [Fact]
    public void A_variant_that_folds_onto_the_non_representative_half_still_cools_both()
    {
        // 자기 자신을 가리키는 gctOverride 변종 5개(19080001·19200027·1910210/220/230)는 접힘을 거쳐 그룹
        // 비대표 base 로 착지한다. 정규화가 없으면 같은 공유 쿨이 시전 코드에 따라 두 키로 갈라져, 짝 칸
        // 하나만 쿨이 돈다(M-15의 반대 방향 증상).
        long t0 = 1_000_000;
        DataManager dm = WithFighter(t0);

        dm.SaveCooldown(BurstFistVariant, 45_000, t0, actorId: Me, fromCast: true);

        IReadOnlyList<SkillCooldownView> rows = dm.ActiveCooldowns(t0 + 5_000);
        Assert.False(Assert.Single(rows, r => r.GroupId == BurstFist).IsReady);
        Assert.False(Assert.Single(rows, r => r.GroupId == BurstFistPokju).IsReady);
    }

    [Fact]
    public void Every_shared_pair_in_the_shipped_catalog_shares_its_cooldown()
    {
        // 쌍 목록을 카탈로그에서 뽑는다 — 패치로 쌍이 늘어도 이 테스트가 같이 커진다.
        CooldownCatalog catalog = ShippedCatalog();
        List<CooldownSkillInfo> members = catalog.Skills
            .Where(s => s.GroupId != s.BaseCode && s.Job == 19)
            .OrderBy(s => s.BaseCode)
            .ToList();
        Assert.Equal(7, members.Count); // 배포 카탈로그 실측치. 달라지면 위 상수도 함께 본다.

        long t0 = 1_000_000;
        DataManager dm = WithFighter(t0);
        foreach (CooldownSkillInfo member in members)
        {
            dm.SaveCooldown(member.BaseCode, 30_000, t0, actorId: Me, fromCast: true);
        }

        IReadOnlyList<SkillCooldownView> rows = dm.ActiveCooldowns(t0 + 5_000);
        foreach (CooldownSkillInfo member in members)
        {
            Assert.False(Assert.Single(rows, r => r.GroupId == member.BaseCode).IsReady);
            Assert.False(Assert.Single(rows, r => r.GroupId == member.GroupId).IsReady);
        }
    }

    [Fact]
    public void A_skill_outside_the_group_is_untouched_by_the_cast()
    {
        // 상태 공유가 그룹 밖으로 새면 오버레이 전체가 한꺼번에 회색이 된다.
        long t0 = 1_000_000;
        DataManager dm = WithFighter(t0);

        dm.SaveCooldown(Ascent, 60_000, t0, actorId: Me, fromCast: true);

        IReadOnlyList<SkillCooldownView> rows = dm.ActiveCooldowns(t0 + 10_000);
        Assert.All(
            rows.Where(r => r.GroupId != Ascent && r.GroupId != AscentPokju),
            r => Assert.True(r.IsReady));
    }
}
