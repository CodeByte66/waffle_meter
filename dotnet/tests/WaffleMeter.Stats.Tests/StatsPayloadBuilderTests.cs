using WaffleMeter.Capture;
using WaffleMeter.Data;
using WaffleMeter.Stats;
using Xunit;

namespace WaffleMeter.Stats.Tests;

public sealed class StatsPayloadBuilderTests
{
    private static DataManager TwoPlayerParty(int mePower = 5000, int allyPower = 3000)
    {
        var dm = new DataManager();
        dm.SaveNickname(1, "Me", isExecutor: true, server: 3, jobByte: 5);    // GLADIATOR
        if (mePower > 0)
        {
            dm.SaveUserPower(1, mePower);
        }

        dm.SaveNickname(2, "Ally", isExecutor: false, server: 3, jobByte: 25); // SORCERER
        if (allyPower > 0)
        {
            dm.SaveUserPower(2, allyPower);
        }

        return dm;
    }

    private static DpsLog SampleLog(DataManager dm, bool boss = true)
    {
        User me = dm.User(1)!;
        User ally = dm.User(2)!;

        var report = new DpsReport
        {
            Contributors = new List<User> { me, ally },
            BattleStart = 1_000_000,
            BattleEnd = 1_030_000, // duration 30000
            Target = new MobInfo(100, new Mob(12345, "센터보스", boss), remainHp: 0, maxHp: 1_000_000),
            Information = new Dictionary<int, DpsInformation>
            {
                [1] = new DpsInformation(1_000_000, 50_000, 60.0, 40.0),
                [2] = new DpsInformation(600_000, 30_000, 40.0, 24.0),
            },
        };

        return new DpsLog
        {
            Report = report,
            SkillDetails = new Dictionary<int, Dictionary<string, AnalyzedSkill>>
            {
                [1] = new() { ["11020001"] = new AnalyzedSkill { SkillCode = 11020001, Name = "강타", DamageAmount = 1_000_000, Times = 100, CritTimes = 20, DoubleTimes = 10, PerfectTimes = 5, BackTimes = 3, ParryTimes = 2 } },
                [2] = new() { ["15210001"] = new AnalyzedSkill { SkillCode = 15210001, Name = "파이어", DamageAmount = 600_000, Times = 50, CritTimes = 10 } },
            },
            BuffRates = new Dictionary<int, List<OperatingData>>
            {
                [1] = new()
                {
                    new OperatingData(110200050, "글래디버프", null, null, 95.5, 1), // self (caster==owner, prefix match)
                    new OperatingData(150000010, "동료버프", null, null, 50.0, 2),  // party (caster 2 != owner 1)
                },
            },
            BossBuffRates = new List<OperatingData>
            {
                new OperatingData(990000000, "보스디버프", null, null, 80.0, 1),
            },
        };
    }

    private static StatsPayloadBuilder Builder(DataManager dm, bool isPublic = false) =>
        new(dm, publicCharacterProvider: () => isPublic, clock: () => 1_700_000_000_000);

    private static StatsUploadPayload BuildOk(DataManager dm, DpsLog log, bool isPublic = false)
    {
        BuildResult result = Builder(dm, isPublic).Build(log, "1.7.9", killConfirmed: true);
        return Assert.IsType<BuildResult.Payload>(result).Value;
    }

    [Fact]
    public void Builds_character_encounter_and_battle()
    {
        DataManager dm = TwoPlayerParty();
        StatsUploadPayload payload = BuildOk(dm, SampleLog(dm));

        Assert.Equal(6, payload.SchemaVersion);
        Assert.Equal("1.7.9", payload.ClientVersion);
        Assert.Equal(1_700_000_000_000, payload.UploadedAt);
        Assert.Equal(StatsIdentity.CharacterIdentityHash(3, "Me"), payload.Character.IdentityHash);
        Assert.Equal("검성", payload.Character.Job);
        Assert.Equal(5000, payload.Character.Power);
        Assert.False(payload.Character.Public);
        Assert.Equal(12345, payload.Encounter.MobCode);
        Assert.Equal("센터보스", payload.Encounter.BossName);
        Assert.Equal(30_000, payload.Battle.DurationMs);
        Assert.Equal(2, payload.Battle.PartySize);
        Assert.Equal(64, payload.BattleHash.Length);
    }

    [Fact]
    public void Participants_sorted_by_damage_with_uploader_flag()
    {
        DataManager dm = TwoPlayerParty();
        StatsUploadPayload payload = BuildOk(dm, SampleLog(dm));

        Assert.Equal(2, payload.Participants.Count);
        Assert.True(payload.Participants[0].IsUploader);   // Me, higher damage
        Assert.Equal(5000, payload.Participants[0].Power);
        Assert.False(payload.Participants[1].IsUploader);  // Ally
        Assert.Equal(3000, payload.Participants[1].Power);
    }

    [Fact]
    public void Own_result_and_skills_are_computed()
    {
        DataManager dm = TwoPlayerParty();
        StatsUploadPayload payload = BuildOk(dm, SampleLog(dm));

        Assert.Equal(1_000_000, payload.Result.TotalDamage);
        Assert.Equal(50_000, payload.Result.Dps);
        Assert.Equal(60.0, payload.Result.PartyContribution);
        Assert.Equal(100, payload.Result.HitCount);
        Assert.Equal(20.0, payload.Result.CritRate); // 20/100 * 100

        StatsSkillPayload skill = Assert.Single(payload.Skills);
        Assert.Equal(11020001, skill.SkillCode);
        Assert.Equal("direct", skill.DamageType);
        Assert.Equal(1_000_000, skill.Damage);
        Assert.Equal(100.0, skill.Share);
    }

    [Fact]
    public void Front_and_back_rates_divide_by_the_flag_bearing_hit_count()
    {
        // 후방/전방 are facing judgments carried only by flag-bearing (sw6) hits, so they divide by FlaggedTimes —
        // matching the meter's 후방/전방 detail tiles. Crit stays over every hit.
        DataManager dm = TwoPlayerParty();
        DpsLog log = SampleLog(dm);
        log.SkillDetails[1]["11020001"] = new AnalyzedSkill
        {
            SkillCode = 11020001, Name = "강타", DamageAmount = 1_000_000,
            Times = 100, FlaggedTimes = 40, FrontTimes = 24, BackTimes = 12, CritTimes = 20,
        };

        StatsUploadPayload payload = BuildOk(dm, log);

        Assert.Equal(60.0, payload.Result.FrontRate); // 24 / 40 flagged (NOT 24 / 100 hits)
        Assert.Equal(30.0, payload.Result.BackRate);  // 12 / 40 flagged (NOT 12 / 100 hits)
        Assert.Equal(20.0, payload.Result.CritRate);  // crit is per-hit: 20 / 100
    }

    [Fact]
    public void Uploader_dps_series_and_self_buff_intervals_come_from_the_frozen_snapshot()
    {
        DataManager dm = TwoPlayerParty();
        DpsLog log = SampleLog(dm); // BattleStart 1_000_000, BattleEnd 1_030_000 (30s), uploader = GLADIATOR(prefix 11)
        log.Report.DpsSeries = new Dictionary<int, long[]> { [1] = [100, 0, 200] };
        log.Report.BuffIntervals = new Dictionary<int, List<BuffTimeline>>
        {
            [1] =
            [
                new BuffTimeline(110200050, "글래디버프", 1, 11020000, 11, [(1_005_000L, 1_020_000L)]), // self class buff
                new BuffTimeline(22101051, "용기의 주문서", 1, 22101051, 0, [(1_000_000L, 1_030_000L)]), // consumable → drop
                new BuffTimeline(150000010, "동료버프", 2, 15000000, 15, [(1_000_000L, 1_030_000L)]),   // party → drop
            ],
        };

        StatsUploadPayload payload = BuildOk(dm, log);

        Assert.NotNull(payload.DpsSeries);
        Assert.Equal(1, payload.DpsSeries!.Step);
        Assert.Equal([100L, 0L, 200L], payload.DpsSeries.Damage);

        StatsSelfBuffIntervalPayload buff = Assert.Single(payload.SelfBuffIntervals!); // only the own-class buff survives
        Assert.Equal(11020000, buff.BaseCode);
        Assert.Equal("글래디버프", buff.Name);
        Assert.Equal([5, 20], buff.Spans); // whole-second offsets from BattleStart (5s..20s)
    }

    [Fact]
    public void Every_participant_carries_its_own_frozen_dps_series()
    {
        // v6의 본체: 그래프 소스를 업로더 한 명이 아니라 참가자 전원이 싣는다. 계산·동결은 원래부터 전원분으로
        // 돌고 있었고(DpsCalculator.BuildDpsSeries) payload가 업로더 것만 꺼내 쓰느라 나머지를 버리던 것이다.
        DataManager dm = TwoPlayerParty();
        DpsLog log = SampleLog(dm);
        log.Report.DpsSeries = new Dictionary<int, long[]>
        {
            [1] = [100, 0, 200], // 업로더
            [2] = [10, 20, 30],  // 동료
        };

        StatsUploadPayload payload = BuildOk(dm, log);

        StatsParticipantPayload me = payload.Participants.Single(p => p.IsUploader);
        StatsParticipantPayload ally = payload.Participants.Single(p => !p.IsUploader);

        Assert.Equal([100L, 0L, 200L], me.DpsSeries!.Damage);
        Assert.Equal(1, me.DpsSeries.Step);
        Assert.Equal([10L, 20L, 30L], ally.DpsSeries!.Damage); // 동료 것이 업로더 것으로 뭉개지지 않는다
        Assert.Equal(1, ally.DpsSeries.Step);

        // 최상위 dpsSeries는 v5까지만 읽는 웹을 위한 업로더 하위호환 필드 — 참가자 행과 값이 같아야 한다.
        Assert.Equal(me.DpsSeries.Damage, payload.DpsSeries!.Damage);
        Assert.Equal(me.DpsSeries.Step, payload.DpsSeries.Step);
    }

    [Fact]
    public void A_participant_without_a_frozen_series_omits_it_rather_than_sending_an_empty_one()
    {
        // 딜은 있는데 시계열이 없는 참가자는 실제로 생긴다(타임스탬프 없는 타격만 있었던 경우 등). 그 한 명 때문에
        // 전투 전체를 버리거나 빈 배열을 보내면 안 된다 — 빈 배열은 null과 달리 그대로 직렬화된다.
        DataManager dm = TwoPlayerParty();
        DpsLog log = SampleLog(dm);
        log.Report.DpsSeries = new Dictionary<int, long[]> { [1] = [100, 0, 200] }; // 동료 것만 없다

        StatsUploadPayload payload = BuildOk(dm, log);

        Assert.NotNull(payload.Participants.Single(p => p.IsUploader).DpsSeries);
        Assert.Null(payload.Participants.Single(p => !p.IsUploader).DpsSeries);
    }

    [Theory]
    [InlineData(900, 1, 900)]      // 상한 정확히(≈15분) — 접지 않는다
    [InlineData(901, 2, 450)]      // 한 칸 넘으면 2초 버킷, 마지막 홀수 초는 버린다
    [InlineData(1801, 3, 600)]     // 30분 초과 → 3초 버킷
    [InlineData(3600, 4, 900)]     // 1시간 → 4초 버킷 (예전엔 3,600 샘플 그대로 나가 웹 상한 3,600에 걸렸다)
    [InlineData(54_001, 60, 900)]  // 전투 창이 비정상적으로 길어져도 step은 웹 상한 60에서 멈춘다(400 = 전투 소실 회피)
    public void A_long_battle_is_folded_into_buckets_that_stay_under_the_sample_cap(int seconds, int expectedStep, int expectedSamples)
    {
        // 웹은 damage 배열을 3,600개로 제한한다. 그건 업로더 한 명 기준이라 16명 × 3,600이면 본문 1MB 한도에
        // 붙는다 — 그래서 참가자당 900샘플로 접는다. 웹 표시는 어차피 3초 버킷이라 시각적 손실은 없다.
        DataManager dm = TwoPlayerParty();
        DpsLog log = SampleLog(dm);
        long[] perSecond = Enumerable.Repeat(7L, seconds).ToArray();
        log.Report.DpsSeries = new Dictionary<int, long[]> { [1] = perSecond };

        StatsUploadPayload payload = BuildOk(dm, log);

        StatsDpsSeriesPayload series = payload.Participants.Single(p => p.IsUploader).DpsSeries!;
        Assert.Equal(expectedStep, series.Step);
        Assert.Equal(expectedSamples, series.Damage.Count);
        Assert.True(series.Damage.Count <= 900);
        Assert.True(series.Step <= 60);   // 웹 zod가 step에 건 상한
        // 최상위(v5 하위호환) 시계열도 같은 다운샘플을 거쳐야 한다 — 두 자리가 갈리면 웹이 어느 쪽을 믿을지 모른다.
        Assert.Equal(series.Step, payload.DpsSeries!.Step);
        Assert.Equal(series.Damage, payload.DpsSeries.Damage);
        // 버킷 값은 그 구간 피해의 '합'이다. 평균을 넣으면 웹이 합/(샘플수×step)으로 나누면서 표시 DPS가 1/N로 준다.
        Assert.All(series.Damage, v => Assert.Equal(7L * expectedStep, v));
        // 마지막 불완전 버킷은 버린다 — 잔여 초를 그대로 실으면 웹이 step초로 간주해 마지막 점이 과소 표시된다.
        Assert.Equal(7L * expectedStep * expectedSamples, series.Damage.Sum());
    }

    [Fact]
    public void Missing_dps_snapshot_omits_the_graph_fields()
    {
        // A report without the frozen snapshot (old/pre-freeze) must omit dpsSeries/selfBuffIntervals, not send empty.
        DataManager dm = TwoPlayerParty();
        StatsUploadPayload payload = BuildOk(dm, SampleLog(dm)); // SampleLog leaves DpsSeries/BuffIntervals empty

        Assert.Null(payload.DpsSeries);
        Assert.Null(payload.SelfBuffIntervals);
        Assert.All(payload.Participants, p => Assert.Null(p.DpsSeries)); // 참가자 행도 마찬가지로 생략
    }

    [Fact]
    public void Buffs_are_classified_self_and_party_with_actor_indexes()
    {
        DataManager dm = TwoPlayerParty();
        StatsUploadPayload payload = BuildOk(dm, SampleLog(dm));

        StatsBuffPayload self = payload.Buffs.Single(b => b.BuffCode == 110200050);
        Assert.Equal("self", self.Source);
        Assert.Equal("participant", self.Scope);
        Assert.Equal("buff", self.Category);
        Assert.Equal(95.5, self.OperatingRate);
        Assert.Equal(0, self.OwnerParticipantIndex);
        Assert.Equal(0, self.ActorParticipantIndex); // actor 1 -> participant 0

        StatsBuffPayload party = payload.Buffs.Single(b => b.BuffCode == 150000010);
        Assert.Equal("party", party.Source);
        Assert.Equal(1, party.ActorParticipantIndex); // actor 2 -> participant 1
    }

    [Fact]
    public void Boss_debuffs_have_boss_scope_and_no_source()
    {
        DataManager dm = TwoPlayerParty();
        StatsUploadPayload payload = BuildOk(dm, SampleLog(dm));

        StatsBuffPayload debuff = Assert.Single(payload.BossDebuffs);
        Assert.Equal(990000000, debuff.BuffCode);
        Assert.Equal("boss", debuff.Scope);
        Assert.Equal("debuff", debuff.Category);
        Assert.Null(debuff.Source);
        Assert.Equal(0, debuff.ActorParticipantIndex);
    }

    [Fact]
    public void Buffs_carry_the_base_code_the_rank_variants_collapsed_to()
    {
        DataManager dm = TwoPlayerParty();
        DpsLog log = SampleLog(dm);
        log.BuffRates[1] =
        [
            new OperatingData(118000071, "살기 파열", null, null, 99.8, 1, 11800000, 11),
        ];

        StatsUploadPayload payload = BuildOk(dm, log);

        StatsBuffPayload buff = Assert.Single(payload.Buffs);
        Assert.Equal(118000071, buff.BuffCode); // raw runtime code — the web's rDPS looks buff values up by it
        Assert.Equal(11800000, buff.BaseCode);
    }

    [Fact]
    public void Category_stays_target_derived()
    {
        // "buff" for a player target, "debuff" for the boss. That IS the taxonomy: a player skill's debuff lands
        // on its target, never on the caster. The stats web also pins participant rows with z.literal("buff"),
        // so any other value rejects the whole upload.
        DataManager dm = TwoPlayerParty();
        StatsUploadPayload payload = BuildOk(dm, SampleLog(dm));

        Assert.All(payload.Buffs, b => Assert.Equal("buff", b.Category));
        Assert.All(payload.BossDebuffs, b => Assert.Equal("debuff", b.Category));
        // 스키마 이력 (이 레포에서 어떤 필드가 어느 버전에 들어갔는지 적어두는 유일한 자리):
        //   v5 = frontRate + dpsSeries(업로더) + selfBuffIntervals
        //   v6 = participants[].dpsSeries — 파티원 전원의 DPS 추이 그래프
        // ⚠️ 웹 zod의 리터럴 유니온이 이 번호를 받아들여야 한다. 웹 배포 전에 미터를 내면 400 invalid_schema로
        //    전투가 영구 소실된다(미터에 4xx 재시도 없음).
        Assert.Equal(6, payload.SchemaVersion);
    }

    [Fact]
    public void A_self_buff_that_fell_back_to_its_base_code_is_still_classified_self()
    {
        // Regression: BuffSource used to read the job prefix off buffCode. The fallback path emits the 8-digit
        // base (11800000 / 10_000_000 == 1), so the caster's own buff was labelled "other" — and the stats web
        // drops every non-scroll "other" row, which is how these vanished from the web but not the meter.
        DataManager dm = TwoPlayerParty();
        DpsLog log = SampleLog(dm);
        log.BuffRates[1] =
        [
            new OperatingData(11800000, "살기 파열", null, null, 60.0, 1, 11800000, 11),
        ];

        StatsUploadPayload payload = BuildOk(dm, log);

        Assert.Equal("self", Assert.Single(payload.Buffs).Source);
    }

    [Fact]
    public void An_eight_digit_mob_debuff_on_a_player_is_never_that_players_self_buff()
    {
        // 15003201(중독) is a mob effect whose 8-digit code leads with 15 — the 마도성 prefix. The ally IS 마도성,
        // so deriving the prefix from the code (or from BaseCode, which is the code itself here) would report the
        // mob's poison as her own class buff. Only the raw 9-digit job band may grant a prefix.
        DataManager dm = TwoPlayerParty();
        DpsLog log = SampleLog(dm);
        log.BuffRates[2] =
        [
            new OperatingData(15003201, "중독", null, null, 30.0, 2, 15003201, JobPrefix: 0),
        ];

        StatsUploadPayload payload = BuildOk(dm, log);

        StatsParticipantPayload ally = payload.Participants.Single(p => !p.IsUploader);
        Assert.Equal("마도성", ally.Job);

        StatsBuffPayload row = Assert.Single(ally.Buffs);
        Assert.Equal("other", row.Source);
        Assert.Equal(15003201, row.BaseCode);
    }

    [Fact]
    public void Party_composition_counts_jobs_and_synergy()
    {
        DataManager dm = TwoPlayerParty();
        StatsUploadPayload payload = BuildOk(dm, SampleLog(dm));

        Assert.Equal(1, payload.PartyComposition.Jobs["검성"]);
        Assert.Equal(1, payload.PartyComposition.Jobs["마도성"]);
        Assert.True(payload.PartyComposition.Synergy.HasGladiator);
        Assert.False(payload.PartyComposition.Synergy.HasCleric);
        Assert.Equal(1, payload.PartyComposition.Synergy.SynergyCount); // only GLADIATOR is a synergy job
    }

    [Theory]
    [InlineData(false, true, "not_uploadable_boss")] // not a boss
    [InlineData(true, false, "not_kill")]            // kill not confirmed
    public void Skips_when_not_uploadable(bool boss, bool killConfirmed, string expected)
    {
        DataManager dm = TwoPlayerParty();
        BuildResult result = Builder(dm).Build(SampleLog(dm, boss), "1.7.9", killConfirmed);
        Assert.Equal(expected, Assert.IsType<BuildResult.Skip>(result).Reason);
    }

    [Fact]
    public void Skips_when_own_power_unresolved()
    {
        DataManager dm = TwoPlayerParty(mePower: 0);
        BuildResult result = Builder(dm).Build(SampleLog(dm), "1.7.9", killConfirmed: true);
        Assert.Equal("own_power_unresolved", Assert.IsType<BuildResult.Skip>(result).Reason);
    }

    /// <summary>
    /// 종전에는 참가자 한 명의 전투력을 못 읽으면 <b>전투를 통째로 버렸다</b>(`participant_power_unresolved`).
    /// 이제는 그 참가자의 <c>Power</c> 만 null 로 나가고 전투는 산다 — 서버가 NULL 을 파티 편차에서 무시하고
    /// <c>power &gt;= 400000</c> 게이트로 그 사람만 배제한다(2026-09-18 웹 `7a7b23e`).
    /// <para>🔑 참가자를 배열에서 <b>빼는</b> 것으로 대신하면 안 된다 — 편차는 자격자가 아니라 참가자 전원으로
    /// 계산하고, 성역에서는 <c>sub_party_known</c> 이 깨져 R0/R1 자격을 잃는다. 그래서 "행은 남기고 값만 null".</para>
    /// </summary>
    [Fact]
    public void An_unresolved_participant_power_no_longer_discards_the_battle()
    {
        DataManager dm = TwoPlayerParty(allyPower: 0);

        BuildResult result = Builder(dm).Build(SampleLog(dm), "1.7.9", killConfirmed: true);

        StatsUploadPayload payload = Assert.IsType<BuildResult.Payload>(result).Value;
        Assert.Equal(2, payload.Participants.Count);                       // 행은 그대로 둘 다 있다
        Assert.Null(payload.Participants.Single(p => !p.IsUploader).Power); // 못 읽은 쪽만 null
        Assert.NotNull(payload.Participants.Single(p => p.IsUploader).Power);
    }

    [Fact]
    public void OwnCharacter_reports_executor_snapshot()
    {
        DataManager dm = TwoPlayerParty();
        StatsOwnCharacter own = Builder(dm).OwnCharacter();

        Assert.True(own.Detected);
        Assert.Equal(1, own.Id);
        Assert.Equal("Me", own.Nickname);
        Assert.Equal("검성", own.Job);
        Assert.Equal(5000, own.Power);
    }
}
