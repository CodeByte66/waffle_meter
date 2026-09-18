using WaffleMeter.App.Core;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

public class SkillCatalogTests
{
    [Fact]
    public void Maps_cover_all_skills_with_unique_codes()
    {
        Assert.Equal(184, SkillCatalog.Skills.Count); // 148 + 19 권성 (2026-07-01 신직업) + 17 주요 패시브
        Assert.Equal(SkillCatalog.Skills.Count, SkillCatalog.Skills.Select(s => s.Code).Distinct().Count());
        Assert.Equal(SkillCatalog.Skills.Count, SkillCatalog.DefaultVisibleCodes.Count);
    }

    [Fact]
    public void Every_job_has_tracked_skills_including_권성()
    {
        foreach (string job in SkillCatalog.JobPrefix.Keys)
        {
            Assert.True(SkillCatalog.Skills.Any(s => s.Job == job), $"no tracked skills for {job}");
        }

        // 권성 (the new job) must be populated with both normal + stigma skills.
        Assert.Equal(21, SkillCatalog.Skills.Count(s => s.Job == "권성")); // 19 액티브/스티그마 + 위세·폭주 증폭
        Assert.Contains(SkillCatalog.Skills, s => s.Job == "권성" && s.IsStigma);
        Assert.Contains(SkillCatalog.Skills, s => s.Job == "권성" && !s.IsStigma);
    }

    [Fact]
    public void Get_and_metadata_resolve()
    {
        SkillMeta? m = SkillCatalog.Get(15210000); // 마도성 불꽃 화살
        Assert.NotNull(m);
        Assert.Equal("불꽃 화살", m!.Name);
        Assert.Equal("마도성", m.Job);
        Assert.False(m.IsStigma);
        Assert.Equal("불꽃 화살", SkillCatalog.GetName(15210000));
        Assert.Null(SkillCatalog.GetName(99999999));
    }

    [Fact]
    public void Order_follows_source_order()
    {
        // 소스에 적힌 순서 그대로. 코드를 박아 두면 카탈로그를 손볼 때마다 같이 썩으므로(2026-08-23 에
        // 죽은 패시브 19개를 액티브로 교체하면서 실제로 그렇게 됐다) 카탈로그 자신에게서 뽑는다.
        Assert.True(SkillCatalog.Order(SkillCatalog.Skills[0].Code) < SkillCatalog.Order(SkillCatalog.Skills[1].Code));
        Assert.Equal(999, SkillCatalog.Order(99999999)); // unknown -> tail
    }

    [Theory]
    [InlineData(15210000, 15210000)] // exact match
    [InlineData(15210042, 15210000)] // sub-code -> floor base
    [InlineData(99999999, 99999999)] // unknown -> self
    public void Normalize_maps_to_base_or_self(int input, int expected)
        => Assert.Equal(expected, SkillCatalog.Normalize(input));

    [Fact]
    public void GroupedByJob_splits_normal_and_stigma()
    {
        Assert.Equal(9, SkillCatalog.GroupedByJob.Count);
        GroupedJobSkills sorc = SkillCatalog.GroupedByJob.First(g => g.Job == "마도성");
        Assert.Contains(15210000, sorc.NormalSkills);  // 불꽃 화살 (normal)
        Assert.Contains(15360000, sorc.StigmaSkills);   // 신성 폭발 (stigma)
        Assert.DoesNotContain(15210000, sorc.StigmaSkills);
    }

    [Fact]
    public void GroupedByJob_keeps_passives_out_of_the_other_two_groups()
    {
        GroupedJobSkills sorc = SkillCatalog.GroupedByJob.First(g => g.Job == "마도성");
        Assert.Contains(15740000, sorc.PassiveSkills);        // 불꽃의 로브
        Assert.DoesNotContain(15740000, sorc.NormalSkills);
        Assert.DoesNotContain(15740000, sorc.StigmaSkills);

        // 전 직업이 주요 패시브를 갖는다 — 한 직업만 비면 그 직업 신청자만 이 줄이 통째로 안 뜬다.
        Assert.All(SkillCatalog.GroupedByJob, g => Assert.NotEmpty(g.PassiveSkills));
        Assert.Equal(new[] { 19740000, 19750000 },
            SkillCatalog.GroupedByJob.First(g => g.Job == "권성").PassiveSkills);
    }

    /// <summary>
    /// 🔑 일반(액티브) 칸에는 패시브가 들어가면 안 된다. 조인 패널 뱃지의 액티브는 공식 홈 <b>장착</b>
    /// 정보에서만 오는데 그쪽은 패시브를 영원히 <c>equip:0</c> 으로 준다 — 일반으로 실린 패시브는 픽커에서
    /// 켜 놔도 절대 뜨지 않는 칸이 된다. 2026-08-23 라이브 108명 표본에서 전 직업에 그런 항목이 있었고
    /// 권성은 6개 중 4개였다.
    /// <para>이제 패시브는 <see cref="SkillMeta.IsPassive"/> 로 명시해 <b>미장착</b> 쪽에서 레벨을 읽는다.
    /// 그래서 규칙이 "패시브 대역이 없어야 한다"에서 "패시브 대역이면 반드시 IsPassive 여야 한다"로 바뀌었다.
    /// 클라 데이터에 category 가 없어 코드 대역으로 판정한다: 실측상 x71xxxx~x80xxxx 가 패시브 대역이다.
    /// 하한이 710000 인 것이 중요하다 — x70xxxx 의 `강습 *` 계열은 전 직업 공통 <b>스티그마 액티브</b>라
    /// 이 대역 밖이고, 700000 으로 잡으면 그것들이 통째로 걸린다.</para>
    /// </summary>
    [Fact]
    public void A_passive_band_code_is_marked_passive_never_left_as_an_active()
    {
        var misfiled = SkillCatalog.Skills
            .Where(s => !s.IsStigma && !s.IsPassive && s.Code % 1_000_000 >= 710_000)
            .Select(s => $"{s.Code} {s.Job} {s.Name}")
            .ToArray();

        Assert.Empty(misfiled);
    }

    /// <summary>역방향: 패시브로 표시한 것이 정말 패시브 대역이어야 한다. 액티브를 잘못 표시하면 장착
    /// 정보에 있는데도 미장착 쪽에서만 찾게 되어 조용히 사라진다.</summary>
    [Fact]
    public void Everything_marked_passive_really_is_in_the_passive_band()
    {
        Assert.NotEmpty(SkillCatalog.PassiveCodes);
        Assert.All(
            SkillCatalog.Skills.Where(s => s.IsPassive),
            s => Assert.InRange(s.Code % 1_000_000, 710_000, 809_999));

        // 패시브는 스티그마와 배타적이다 — 둘 다면 어느 묶음/어느 맵으로 갈지가 갈린다.
        Assert.DoesNotContain(SkillCatalog.Skills, s => s.IsPassive && s.IsStigma);
        Assert.Equal(SkillCatalog.PassiveCodes.Count, SkillCatalog.GroupedByJob.Sum(g => g.PassiveSkills.Count));
    }
}
