using WaffleMeter.App.Core;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

public sealed class DetailModelTests
{
    private static AnalyzedSkill Skill(int code, string name, int dmg, int hits, int crit = 0, int dot = 0, int dotTimes = 0, int back = 0, int flagged = 0, int front = 0, int rawCode = 0) =>
        new() { SkillCode = code, Name = name, DamageAmount = dmg, Times = hits, CritTimes = crit, DotDamageAmount = dot, DotTimes = dotTimes, BackTimes = back, FrontTimes = front, FlaggedTimes = flagged, RawSkillCode = rawCode };

    private static DetailModel Compute(
        Dictionary<string, AnalyzedSkill> skills,
        List<OperatingData>? own = null,
        List<OperatingData>? boss = null,
        JobClass? job = JobClass.GLADIATOR) =>
        DetailModel.Compute(skills, own ?? new(), boss ?? new(), uid: 1, job, contribution: 60.0, combatMs: 30000);

    [Fact]
    public void Skips_zero_damage_and_emits_dot_row()
    {
        var model = Compute(new Dictionary<string, AnalyzedSkill>
        {
            ["99999"] = Skill(99999, "강타", dmg: 1000, hits: 10, crit: 5, dot: 200, dotTimes: 4),
            ["88888"] = Skill(88888, "무피해", dmg: 0, hits: 3),
        });

        // A non-chain skill's direct + DOT rows are separate singleton groups (React sorts each by
        // damage); the zero-damage skill is dropped.
        Assert.Equal(2, model.Skills.Count);
        DetailSkillGroup dotGroup = Assert.Single(model.Skills, g => g.Merged.IsDot);
        Assert.Equal("강타 - 지속", dotGroup.Merged.Name);
        Assert.Null(dotGroup.Merged.CritPct);
        Assert.Equal(1200, model.TotalDamage); // 1000 direct + 200 dot
    }

    [Fact]
    public void Per_skill_and_total_pcts()
    {
        var model = Compute(new Dictionary<string, AnalyzedSkill>
        {
            ["99999"] = Skill(99999, "강타", dmg: 1000, hits: 10, crit: 5),
        });

        DetailSkillRow direct = model.Skills[0].Children.Single(r => !r.IsDot);
        Assert.Equal(50, direct.CritPct);   // 5/10*100
        Assert.Equal(50.0, model.CritPct);  // total one-decimal
    }

    [Fact]
    public void Chain_group_merges_main_and_child()
    {
        var model = Compute(new Dictionary<string, AnalyzedSkill>
        {
            ["11020000"] = Skill(11020000, "절단", dmg: 1000, hits: 10, crit: 4),
            ["11030000"] = Skill(11030000, "연계", dmg: 500, hits: 5, crit: 1),
            ["99999"] = Skill(99999, "기타", dmg: 300, hits: 3),
        });

        Assert.Equal(2, model.Skills.Count); // one chain group + one singleton
        DetailSkillGroup chain = model.Skills[0]; // sorted by merged dmg desc -> the 1500 group
        Assert.True(chain.HasChildren);
        Assert.Equal(1500, chain.Merged.Damage);
        Assert.Equal(15, chain.Merged.Hits);
        Assert.Equal(2, chain.Children.Count);

        DetailSkillGroup singleton = model.Skills[1];
        Assert.False(singleton.HasChildren);
        Assert.Equal(300, singleton.Merged.Damage);
    }

    [Fact]
    public void Dot_only_skill_still_emits_its_지속_row()
    {
        // 대지의 징벌's DoT normalizes to a different rank code than its direct hits, so it arrives as a
        // DoT-ONLY skill (DamageAmount 0, DotDamageAmount > 0). It must still show its "- 지속" row (the bug
        // was the old DamageAmount<=0 skip dropping it), with no 판정 (DoT has no crit/강타/백어택).
        var model = Compute(new Dictionary<string, AnalyzedSkill>
        {
            ["17400040"] = Skill(17400040, "대지의 징벌", dmg: 0, hits: 0, dot: 5000, dotTimes: 600),
        });

        DetailSkillGroup dot = Assert.Single(model.Skills);
        Assert.True(dot.Merged.IsDot);
        Assert.Equal("대지의 징벌 - 지속", dot.Merged.Name);
        Assert.Equal(600, dot.Merged.Hits);
        Assert.Equal(5000, dot.Merged.Damage);
        Assert.Null(dot.Merged.BackPct);
    }

    [Fact]
    public void Same_name_split_codes_merge_into_one_row()
    {
        // 심판의 번개's direct cast (17041250) and field ticks (17040250) are two codes with the same name;
        // they merge into ONE row (mirrors the reference meter's single 심판의 번개 row).
        var model = Compute(new Dictionary<string, AnalyzedSkill>
        {
            ["17041250"] = Skill(17041250, "심판의 번개", dmg: 2000, hits: 20, crit: 10),
            ["17040250"] = Skill(17040250, "심판의 번개", dmg: 1600, hits: 15),
        });

        DetailSkillGroup merged = Assert.Single(model.Skills);
        Assert.True(merged.HasChildren);
        Assert.Equal("심판의 번개", merged.Merged.Name);
        Assert.Equal(3600, merged.Merged.Damage);
        Assert.Equal(35, merged.Merged.Hits);
        Assert.Equal(2, merged.Children.Count);
    }

    [Fact]
    public void Different_names_sharing_a_slot_stay_separate()
    {
        // 대지의 징벌 (17400000) and 대지의 축복 (17400058) share the 17_40 slot but are DISTINCT skills — the
        // name-merge must key on NAME, not base code, so they never fold together.
        var model = Compute(new Dictionary<string, AnalyzedSkill>
        {
            ["17400000"] = Skill(17400000, "대지의 징벌", dmg: 1000, hits: 40),
            ["17400058"] = Skill(17400058, "대지의 축복", dmg: 2000, hits: 300),
        });

        Assert.Equal(2, model.Skills.Count);
        Assert.Contains(model.Skills, g => g.Merged.Name == "대지의 징벌");
        Assert.Contains(model.Skills, g => g.Merged.Name == "대지의 축복");
        Assert.All(model.Skills, g => Assert.False(g.HasChildren));
    }

    [Fact]
    public void Back_rate_summary_divides_by_flag_bearing_hits_not_all_hits()
    {
        // An attack (40 flag-bearing hits, all back) + a heal (60 hits, none flag-bearing). The summary 백어택
        // rate must be over the 40 flag-bearing hits (100%), NOT all 100 hits (40%, diluted by the heal that
        // structurally can't back-attack). Crit stays over all hits (it's a per-hit field on every hit).
        var model = Compute(new Dictionary<string, AnalyzedSkill>
        {
            ["1"] = Skill(1, "단죄", dmg: 4000, hits: 40, crit: 20, back: 40, flagged: 40),
            ["2"] = Skill(2, "치유의 빛", dmg: 2000, hits: 60, crit: 0, back: 0, flagged: 0),
        });

        Assert.Equal(100.0, model.BackPct); // 40 / 40 flag-bearing (was 40.0 over all 100 hits)
        Assert.Equal(20.0, model.CritPct);  // 20 / 100 all hits (crit denominator unchanged)
    }

    [Fact]
    public void Front_and_back_rates_share_the_flag_bearing_denominator()
    {
        // 50 flag-bearing hits: 30 back, 15 front, 5 neither. Front and back are mutually exclusive and both
        // divide by the flag-bearing count — front% + back% + neither% = 100% of directional hits.
        var model = Compute(new Dictionary<string, AnalyzedSkill>
        {
            ["1"] = Skill(1, "가르기", dmg: 5000, hits: 50, back: 30, front: 15, flagged: 50),
        });

        Assert.Equal(60.0, model.BackPct);  // 30 / 50
        Assert.Equal(30.0, model.FrontPct); // 15 / 50

        DetailSkillRow row = model.Skills[0].Merged;
        Assert.Equal(60, row.BackPct);  // per-row uses the flag-bearing denominator too
        Assert.Equal(30, row.FrontPct);
    }

    [Fact]
    public void A_skill_row_carries_its_specialization_slots_decoded_from_the_raw_code()
    {
        // Raw 13040240 (base 13040000): drop the ones digit → 024 → specialization slots 2 and 4.
        var model = Compute(new Dictionary<string, AnalyzedSkill>
        {
            ["13040000"] = Skill(13040000, "기습", dmg: 3000, hits: 20, flagged: 20, rawCode: 13040240),
        });

        IReadOnlyList<bool>? spec = model.Skills[0].Merged.Spec;
        Assert.NotNull(spec);
        Assert.Equal(new[] { false, true, false, true, false }, spec); // slots 2 and 4

        // A DoT row never carries specialization.
        var dotModel = Compute(new Dictionary<string, AnalyzedSkill>
        {
            ["13040000"] = Skill(13040000, "기습", dmg: 0, hits: 0, dot: 500, dotTimes: 5, rawCode: 13040240),
        });
        Assert.Null(dotModel.Skills[0].Merged.Spec);
    }

    [Fact]
    public void Own_buffs_are_split_by_who_applied_them()
    {
        var own = new List<OperatingData>
        {
            new(110200500, "내버프", null, null, 95.5, 1),     // actor==uid, prefix 11 == GLADIATOR -> 내 버프
            new(150000000, "타직업버프", null, null, 40.0, 1),  // actor==uid, prefix 15 (소모품/타직업) -> 그 외
            new(110200500, "파티버프", null, null, 50.0, 2),    // actor 2 != uid -> 남이 준 버프
        };

        DetailModel model = Compute(new(), own: own, job: JobClass.GLADIATOR);

        // 남이 준 버프는 언제나 맨 뒤다 — 중간에 끼우면 몇 줄 안 되는 "그 외"(소모품)가 공대에서 수십 줄짜리
        // 이 섹션 아래로 밀려 사라진다.
        Assert.Equal(new[] { "내 버프", "그 외", "남이 준 버프" }, model.Buffs.Select(s => s.Label).ToArray());
        Assert.Equal(95.5, model.Buffs.Single(s => s.Label == "내 버프").Rows[0].Rate);
        Assert.Equal(
            new[] { false, false, true },
            model.Buffs.Select(s => s.FromOtherPlayers).ToArray());
    }

    [Fact]
    public void A_buff_from_another_player_carries_that_players_name()
    {
        var own = new List<OperatingData>
        {
            new(110200500, "내버프", null, null, 95.5, 1),
            new(117000100, "축복", null, null, 88.0, 7),
            new(117000200, "이름모를자의버프", null, null, 12.0, 9),
        };

        DetailModel model = DetailModel.Compute(
            new Dictionary<string, AnalyzedSkill>(), own, new List<OperatingData>(),
            uid: 1, JobClass.GLADIATOR, contribution: 60.0, combatMs: 30000,
            casterNames: new Dictionary<int, string> { [7] = "밀피" });

        DetailBuffSection granted = model.Buffs.Single(s => s.FromOtherPlayers);
        Assert.Equal("밀피", granted.Rows.Single(r => r.Name == "축복").CasterName);
        // 이름을 못 찾아도 행을 버리지 않는다 — 버리면 저장된 전투에서 피해를 안 낸 서포터가 통째로 사라진다.
        Assert.Equal("플레이어 9", granted.Rows.Single(r => r.Name == "이름모를자의버프").CasterName);
        // 내가 올린 버프에는 이름표가 없다(그 행에서 시전자는 새 정보가 아니다).
        Assert.Null(model.Buffs.Single(s => s.Label == "내 버프").Rows[0].CasterName);
    }

    [Fact]
    public void A_summons_buff_on_its_owner_is_not_someone_elses()
    {
        // 소환수 엔티티 id 는 Contributors 에도 파티 스냅샷에도 절대 없다(피해가 주인에게 접히므로).
        // 접지 않고 「남이 준 버프」로 보내면, 혼자 사냥한 정령성/치유성에게도 그 섹션이 생기고
        // 행 부제가 "플레이어 4713261" 같은 원시 엔티티 id 로 뜬다.
        var own = new List<OperatingData>
        {
            new(110200500, "내버프", null, null, 95.5, 1),
            new(163000003, "소환수버프", null, null, 60.0, 9001), // actor = 소환수 엔티티
        };

        DetailModel model = DetailModel.Compute(
            new Dictionary<string, AnalyzedSkill>(), own, new List<OperatingData>(),
            uid: 1, JobClass.GLADIATOR, contribution: 60.0, combatMs: 30000,
            resolveActor: id => id == 9001 ? 1 : id);

        Assert.DoesNotContain(model.Buffs, s => s.FromOtherPlayers); // 솔로에는 체크박스가 뜨지 않는다
        Assert.Contains(model.Buffs.SelectMany(s => s.Rows), r => r.Name == "소환수버프");
        Assert.All(model.Buffs.SelectMany(s => s.Rows), r => Assert.Null(r.CasterName));
    }

    [Fact]
    public void No_other_player_buffs_means_no_granted_section_at_all()
    {
        // 솔로 전투: 빈 섹션을 만들면 표시 계층이 "남이 준 버프 보기" 체크박스를 켜 두게 되고, 켜도 아무 일이
        // 안 일어나는 컨트롤은 고장으로 읽힌다.
        var own = new List<OperatingData> { new(110200500, "내버프", null, null, 95.5, 1) };

        DetailModel model = Compute(new(), own: own, job: JobClass.GLADIATOR);

        Assert.DoesNotContain(model.Buffs, s => s.FromOtherPlayers);
    }

    [Fact]
    public void Everything_on_the_player_lands_in_a_buff_section()
    {
        // A player skill's debuff goes on its target, never on its caster, so a row in a player's list is always
        // a buff or a self-state. 살기 파열's three codes all land on the 검성 and merge upstream into one row.
        var own = new List<OperatingData>
        {
            new(118000071, "살기 파열", null, null, 99.8, 1, 11800000, 11),
            new(13270000, "맹수의 송곳니", null, null, 23.6, 1, 13270000, 13),
        };

        DetailModel model = Compute(new(), own: own, job: JobClass.GLADIATOR);

        Assert.DoesNotContain(model.Buffs, s => s.Label == "받은 디버프");
        Assert.Equal(99.8, model.Buffs.Single(s => s.Label == "내 버프").Rows[0].Rate);
        Assert.Equal(23.6, model.Buffs.Single(s => s.Label == "그 외").Rows[0].Rate); // 살성 code on a 검성
    }

    [Fact]
    public void Proc_row_is_pinned_to_the_bottom_of_내_버프_and_reports_a_count()
    {
        // 회생의 계약 긴급 회복은 가동률(%)이 성립하지 않는 발동형 — 정렬에 끼지 않고 직업 버프 섹션의
        // 맨 아래에 고정되며, 도핑류가 모이는 "그 외"에는 절대 들어가지 않는다.
        var own = new List<OperatingData>
        {
            new(110200500, "낮은 가동률", null, null, 20.0, 1),
            new(110200600, "높은 가동률", null, null, 90.0, 1),
            new(150000000, "도핑", null, null, 100.0, 1),
        };
        var proc = new DetailProcRow(14790000, "회생의 계약", 3, "생명력 10% 이하에서 발동");

        DetailModel model = DetailModel.Compute(
            new Dictionary<string, AnalyzedSkill>(), own, new List<OperatingData>(),
            uid: 1, JobClass.GLADIATOR, contribution: 60.0, combatMs: 30000, proc);

        DetailBuffSection mine = model.Buffs.Single(s => s.Label == "내 버프");
        Assert.Equal(new[] { "높은 가동률", "낮은 가동률", "회생의 계약" }, mine.Rows.Select(r => r.Name).ToArray());
        Assert.Equal(3, mine.Rows[^1].Count);
        Assert.Null(mine.Rows[0].Count); // 일반 행은 여전히 %
        Assert.DoesNotContain(model.Buffs.Single(s => s.Label == "그 외").Rows, r => r.Count != null);
    }

    [Fact]
    public void Proc_row_alone_still_opens_the_내_버프_section()
    {
        DetailModel model = DetailModel.Compute(
            new Dictionary<string, AnalyzedSkill>(), new List<OperatingData>(), new List<OperatingData>(),
            uid: 1, JobClass.GLADIATOR, contribution: 60.0, combatMs: 30000,
            new DetailProcRow(14790000, "회생의 계약", 1, ""));

        DetailBuffSection mine = Assert.Single(model.Buffs);
        Assert.Equal("내 버프", mine.Label);
        Assert.Equal(1, Assert.Single(mine.Rows).Count);
    }

    [Fact]
    public void A_self_buff_that_fell_back_to_its_base_code_stays_in_내_버프()
    {
        // The fallback path reports the 8-digit base as Code; 11800000 / 10_000_000 == 1 would land it in 그 외.
        var own = new List<OperatingData>
        {
            new(11800000, "살기 파열", null, null, 60.0, 1, 11800000, 11),
        };

        DetailModel model = Compute(new(), own: own, job: JobClass.GLADIATOR);

        Assert.Equal("내 버프", Assert.Single(model.Buffs).Label);
    }

    [Fact]
    public void Boss_debuffs_keep_only_the_ones_this_player_applied()
    {
        var boss = new List<OperatingData>
        {
            new(990000000, "내가건디버프", null, null, 80.0, 1),
            new(990000001, "남이건디버프", null, null, 60.0, 2),
        };

        DetailModel model = Compute(new(), boss: boss);

        // One unlabelled section: every row shares this player as the caster, so a subtitle would just repeat.
        DetailBuffSection section = Assert.Single(model.Debuffs);
        Assert.Equal(string.Empty, section.Label);
        Assert.Equal("내가건디버프", Assert.Single(section.Rows).Name);
    }
}
