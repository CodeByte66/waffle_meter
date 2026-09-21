using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 특화 표시의 전제: 코드 꼬리의 <b>의미가 스킬 종류마다 다르다</b>.
///
/// <para>일반 액티브는 꼬리가 플레이어의 빌드를 96.7% 정확히 싣는다. <b>스티그마는 아니다</b> — 티어마다
/// 별개의 데미지 컴포넌트가 있어서 꼬리는 *그 타격을 낸 컴포넌트의 티어*다. 실측으로 본인 스티그마 hit 의
/// 79.8%가 틀린 티어를 실었고, 11캐릭터 hit 기준으로는 42.8%만 맞았다. 한 스킬이 한 세션에 2~3행으로
/// 쪼개지기도 한다(생명의 권능 lvl20 이 티어 2·3·4 를 동시에 때린다).</para>
///
/// <para>그래서 스티그마는 <b>표시하지 않는다</b>. 값이 1~5 라 그럴듯해 보이는 게 함정이다.</para>
/// </summary>
public sealed class SpecCatalogTests
{
    private static IReadOnlyList<Skill> ShippedSkills()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "Assets", "json", "skills.json");
            if (File.Exists(candidate))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(candidate));
                return doc.RootElement.EnumerateArray()
                    .Select(e => new Skill(e.GetProperty("code").GetInt64(), e.GetProperty("name").GetString() ?? string.Empty))
                    .ToList();
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("skills.json not found above " + AppContext.BaseDirectory);
    }

    // ---- 꼬리 문법 ----

    [Theory]
    [InlineData(2, new[] { 2 })]
    [InlineData(24, new[] { 2, 4 })]
    [InlineData(245, new[] { 2, 4, 5 })]
    [InlineData(135, new[] { 1, 3, 5 })]
    public void A_valid_tail_decodes_to_ascending_slots(int tail, int[] expected)
    {
        Assert.Equal(expected, SpecCatalog.ParseTail(tail));
    }

    [Theory]
    [InlineData(0)]     // 꼬리 없음
    [InlineData(6)]     // 슬롯 6 은 없다
    [InlineData(20)]    // 0 자리
    [InlineData(22)]    // 중복
    [InlineData(42)]    // 내림차순
    [InlineData(1234)]  // 4자리
    public void An_invalid_tail_is_rejected(int tail)
    {
        Assert.Null(SpecCatalog.ParseTail(tail));
    }

    // ---- 종류 판별 ----

    [Fact]
    public void A_base_whose_variants_are_all_single_digit_is_stigma()
    {
        // 클라가 스티그마에는 다자리 조합 코드를 아예 만들지 않는다(실측 118/118 이 최대 1자리).
        var c = new SpecCatalog();
        c.Index([
            new Skill(17_400_000, "대지의 징벌"),
            new Skill(17_400_010, "대지의 징벌"),
            new Skill(17_400_050, "대지의 징벌"),
        ]);

        Assert.Equal(SpecKind.Stigma, c.KindOf(17_400_000));
    }

    [Fact]
    public void A_base_with_a_multi_digit_variant_is_a_normal_active()
    {
        var c = new SpecCatalog();
        c.Index([
            new Skill(17_350_000, "단죄"),
            new Skill(17_350_020, "단죄"),
            new Skill(17_352_450, "단죄"),
        ]);

        Assert.Equal(SpecKind.Normal, c.KindOf(17_350_000));
    }

    [Fact]
    public void A_code_outside_the_player_band_has_no_kind()
    {
        var c = new SpecCatalog();
        c.Index([new Skill(1_809_122, "보스 기술")]);

        Assert.Equal(SpecKind.None, c.KindOf(1_809_122));
    }

    [Fact]
    public void A_base_the_catalog_never_listed_is_promoted_by_what_the_wire_shows()
    {
        // skills.json 에 변형이 아예 없는 base 가 실재한다(실측 14200000 퇴보 베기, 13380000 암격).
        var c = new SpecCatalog();
        c.Index([new Skill(14_200_000, "퇴보 베기")]);
        Assert.Equal(SpecKind.None, c.KindOf(14_200_000));

        c.Observe(14_200_240);   // 2자리 꼬리를 봤다

        Assert.Equal(SpecKind.Normal, c.KindOf(14_200_000));
    }

    [Fact]
    public void Promotion_never_goes_backwards()
    {
        // 2자리 꼬리를 한 번이라도 봤으면 일반 액티브라는 증거다. 그 뒤 1자리만 계속 본다고 사라지지 않는다.
        var c = new SpecCatalog();
        c.Observe(14_200_240);
        Assert.Equal(SpecKind.Normal, c.KindOf(14_200_000));

        c.Observe(14_200_020);

        Assert.Equal(SpecKind.Normal, c.KindOf(14_200_000));
    }

    // ---- Decode ----

    [Fact]
    public void A_stigma_cast_never_reports_a_build()
    {
        // 🔴 값이 1~5 라 그럴듯해 보이는 게 함정이다. 그 숫자는 빌드가 아니라 컴포넌트 티어다.
        Assert.Null(SkillSpecialization.Decode(17_400_050, SpecKind.Stigma));
        Assert.Null(SkillSpecialization.Decode(13_310_040, SpecKind.Stigma));
    }

    [Fact]
    public void A_normal_cast_still_reports_its_slots()
    {
        bool[]? slots = SkillSpecialization.Decode(17_352_450, SpecKind.Normal);

        Assert.NotNull(slots);
        Assert.Equal([false, true, false, true, true], slots);   // 2·4·5
    }

    [Fact]
    public void An_unclassified_skill_falls_back_to_the_old_behaviour()
    {
        // 카탈로그가 안 실렸거나 분류에 실패한 경우다. 여기서 null 로 막으면 특화가 통째로 조용히
        // 사라진다 — 실측으로 문제가 확인된 건 스티그마 하나이므로 거기만 막는다.
        Assert.NotNull(SkillSpecialization.Decode(17_352_450, SpecKind.None));
    }

    // ---- 배포 카탈로그 ----

    [Fact]
    public void The_shipped_catalog_classifies_both_kinds()
    {
        // 인덱싱이 실제 자산에서 동작하는지 — 어느 한 종류도 0개면 판별식이 낡은 것이다.
        var c = new SpecCatalog();
        c.Index(ShippedSkills());

        var bases = ShippedSkills()
            .Select(s => SpecCatalog.BaseOf(s.Code))
            .Where(b => b != 0)
            .Distinct()
            .ToList();

        int stigma = bases.Count(b => c.KindOf(b) == SpecKind.Stigma);
        int normal = bases.Count(b => c.KindOf(b) == SpecKind.Normal);

        Assert.True(stigma > 0, "스티그마로 분류된 base 가 하나도 없다 — 판별식이 낡았다");
        Assert.True(normal > 0, "일반으로 분류된 base 가 하나도 없다 — 판별식이 낡았다");
    }
}
