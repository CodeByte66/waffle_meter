using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// Guards the SHIPPED skill classification (Assets/json/skill_class.json), which decides what the
/// 스킬 타임라인 tab counts as a skill the player actually pressed. Regenerate with
/// <c>dotnet/tools/skill-class-export.py</c> after a client patch.
/// <para>Getting this wrong is silent both ways: too much in <c>passive</c> erases real rotation rows,
/// too little puts auto-firing procs back on the list (실측 2026-08-31 코퍼스에서 시전 프레임의 20.1%).</para>
/// </summary>
public sealed class ShippedSkillClassTests
{
    private static string AssetsJsonDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "Assets", "json");
            if (File.Exists(Path.Combine(candidate, "skill_class.json")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Assets/json/skill_class.json not found above " + AppContext.BaseDirectory);
    }

    private static (List<int> Passive, List<int> ActiveOverrides) Shipped() =>
        ReferenceJson.LoadSkillClass(Path.Combine(AssetsJsonDir(), "skill_class.json"));

    private static DataManager Loaded()
    {
        var dm = new DataManager();
        (List<int> passive, List<int> overrides) = Shipped();
        dm.LoadSkillClass(passive, overrides);
        return dm;
    }

    [Fact]
    public void The_asset_holds_job_band_codes_only_and_the_two_sets_never_overlap()
    {
        (List<int> passive, List<int> overrides) = Shipped();

        Assert.NotEmpty(passive);
        Assert.NotEmpty(overrides);
        Assert.All(passive, c => Assert.InRange(c, 11_000_000, 19_999_999));
        Assert.All(overrides, c => Assert.InRange(c, 11_000_000, 19_999_999));
        Assert.Empty(passive.Intersect(overrides)); // 한 코드가 둘 다일 수는 없다
        Assert.Equal(passive.Count, passive.Distinct().Count());
        Assert.Equal(overrides.Count, overrides.Distinct().Count());
    }

    [Fact]
    public void Every_override_exists_because_its_base_is_passive()
    {
        // activeOverrides 의 존재 이유가 그거다 — base 가 패시브가 아니면 그 코드는 애초에 필요 없다.
        (List<int> passive, List<int> overrides) = Shipped();
        var passiveSet = passive.ToHashSet();

        Assert.All(overrides, c =>
        {
            Assert.NotEqual(0, c % 10_000);                        // base 자신이면 override 가 아니다
            Assert.Contains(c / 10_000 * 10_000, passiveSet);
        });
    }

    [Fact]
    public void Known_passive_procs_are_classified_as_passive()
    {
        // 실측(2026-08-31 5인 코퍼스)에서 직전 시전과 0ms 간격 비율이 각각 68.8% / 79.7% / 74.3% 였던 것들 —
        // 누른 게 아니라 저절로 터진 것이라는 행동 증거가 클라 분류와 일치한다.
        DataManager dm = Loaded();

        Assert.True(dm.IsPassiveSkill(11_800_010), "살기 파열");
        Assert.True(dm.IsPassiveSkill(14_800_007), "사냥꾼의 혼");
        Assert.True(dm.IsPassiveSkill(14_770_007), "속박의 눈");
        Assert.True(dm.IsPassiveSkill(14_720_007), "집중 포화");
        Assert.True(dm.IsPassiveSkill(11_780_007), "노련한 반격");
    }

    [Fact]
    public void Core_pressed_skills_stay_active()
    {
        // 대조군: 같은 실측에서 0ms 비율이 1.8~8.2% 였던 스킬들. 변종 코드(자기 타입은 System)도 base 로
        // 접혀 액티브로 남아야 한다.
        DataManager dm = Loaded();

        Assert.False(dm.IsPassiveSkill(11_010_000), "절단의 맹타");
        Assert.False(dm.IsPassiveSkill(11_010_047), "격파의 맹타 (변종)");
        Assert.False(dm.IsPassiveSkill(14_050_008), "송곳 화살 (변종)");
        Assert.False(dm.IsPassiveSkill(17_350_047), "단죄 (변종)");
    }

    [Fact]
    public void The_evade_family_survives_the_base_fold_trap()
    {
        // 🔑 각 직업 긴급 회피는 자기 타입이 Active 인데 base(직업 무기 장착)가 Passive 다. 그냥 접으면
        // 전 직업의 긴급 회피가 타임라인에서 통째로 사라진다 — 이름 해석이 가진 것과 같은 함정이다
        // (11000100 긴급 회피 → base 검성 무기 장착).
        DataManager dm = Loaded();

        Assert.True(dm.IsPassiveSkill(11_000_000), "검성 무기 장착 (base 는 패시브가 맞다)");
        foreach (int band in new[] { 11, 12, 13, 14, 15, 16, 17, 18, 19 })
        {
            int evade = (band * 1_000_000) + 100;
            Assert.False(dm.IsPassiveSkill(evade), $"{band} 긴급 회피");
        }
    }

    [Fact]
    public void An_unclassified_code_is_kept_rather_than_hidden()
    {
        // 모르는 것을 숨기지 않는다 — 새 패치의 신규 스킬이 자산 재생성 전까지 조용히 사라지면 안 된다.
        DataManager dm = Loaded();

        Assert.False(dm.IsPassiveSkill(19_990_000));
        Assert.False(dm.IsPassiveSkill(3_000_000)); // 기본 공격 대역 (밴드 밖)
    }

    [Fact]
    public void Without_the_asset_nothing_is_filtered()
    {
        // 옛 자산 번들(파일 없음)에서도 앱은 예전 동작 그대로여야 한다.
        var dm = new DataManager();

        Assert.False(dm.IsPassiveSkill(11_800_010));
    }
}
