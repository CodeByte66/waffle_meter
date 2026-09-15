using WaffleMeter.Capture;
using WaffleMeter.Data;
using WaffleMeter.Stats;
using Xunit;

namespace WaffleMeter.Stats.Tests;

/// <summary>
/// Spec for the encounter block of the upload payload: what it says about the dungeon a battle happened in,
/// and — the part that would break the server — that <c>bossName</c> stays the RAW mob name rather than the
/// difficulty-decorated one the meter's UI shows.
/// </summary>
public sealed class StatsPayloadEncounterTests
{
    private const string Catalog = """
    {
      "dungeons": [
        {
          "key": "expedition-bakron-floating-island", "category": "원정", "categoryOrd": 1,
          "name": "바크론의 공중섬", "variantType": "difficulty",
          "bosses": [{"index": 3, "name": "바크론"}],
          "variants": [
            {"label": "시련", "dungeonId": 600074, "difficulty": "시련", "stage": null,
             "mobs": [[2300582, 3]]}
          ]
        },
        {
          "key": "transcend-abyss-horn-cavern", "category": "초월", "categoryOrd": 2,
          "name": "심연의 뿔 암굴", "variantType": "stage",
          "bosses": [{"index": 1, "name": "어미잃은 변견 카푸"}],
          "variants": [
            {"label": "4단계", "dungeonId": 610033, "difficulty": null, "stage": "4",
             "mobs": [[2300544, 1]]}
          ]
        }
      ]
    }
    """;

    private static DataManager Party()
    {
        var dm = new DataManager();
        dm.SaveNickname(1, "Me", isExecutor: true, server: 3, jobByte: 5);
        dm.SaveUserPower(1, 500_000);
        dm.SaveNickname(2, "Ally", isExecutor: false, server: 3, jobByte: 25);
        dm.SaveUserPower(2, 400_000);
        dm.LoadEncounters(EncounterCatalog.Parse(Catalog));
        return dm;
    }

    private static DpsLog Log(DataManager dm, int mobCode, string mobName)
    {
        User me = dm.User(1)!;
        User ally = dm.User(2)!;
        return new DpsLog
        {
            Report = new DpsReport
            {
                Contributors = [me, ally],
                BattleStart = 1_000_000,
                BattleEnd = 1_030_000,
                Target = new MobInfo(100, new Mob(mobCode, mobName, true), remainHp: 0, maxHp: 1_000_000),
                // 실제 저장 경로(DataManager.SaveBattleLog)가 하는 일을 그대로 한다 — 시련 난이도는 리포트에
                // **동결**돼 오고, 페이로드는 추적기를 다시 조회하지 않는다. 여기서 빠뜨리면 이 하네스만
                // 옛 계약(라이브 조회)을 전제하게 된다.
                TrialDifficulty = dm.TrialDifficulty.Current,
                Information = new Dictionary<int, DpsInformation>
                {
                    [1] = new DpsInformation(1_000_000, 50_000, 60.0, 40.0),
                    [2] = new DpsInformation(600_000, 30_000, 40.0, 24.0),
                },
            },
            SkillDetails = new Dictionary<int, Dictionary<string, AnalyzedSkill>>
            {
                [1] = new() { ["11020001"] = new AnalyzedSkill { SkillCode = 11020001, Name = "강타", DamageAmount = 1_000_000, Times = 100 } },
            },
            BuffRates = new Dictionary<int, List<OperatingData>>(),
            BossBuffRates = [],
        };
    }

    private static StatsEncounterPayload Encounter(DataManager dm, int mobCode, string mobName)
    {
        var builder = new StatsPayloadBuilder(dm, () => false);
        BuildResult result = builder.Build(Log(dm, mobCode, mobName), "test", killConfirmed: true);
        return Assert.IsType<BuildResult.Payload>(result).Value.Encounter;
    }

    /// <summary>원정 variants ride as a difficulty; the stage field stays null.</summary>
    [Fact]
    public void Reports_the_dungeon_and_difficulty_for_an_expedition_boss()
    {
        StatsEncounterPayload encounter = Encounter(Party(), 2300582, "바크론");

        Assert.Equal(2300582, encounter.MobCode);
        Assert.Equal("바크론의 공중섬", encounter.DungeonName);
        Assert.Equal("원정", encounter.Category);
        Assert.Equal("시련", encounter.Difficulty);
        Assert.Null(encounter.Stage);
        Assert.Equal(3, encounter.BossIndex);
    }

    /// <summary>초월 variants ride as a numbered stage — as TEXT, because the server's schema types it that
    /// way next to difficulty and rejects a number.</summary>
    [Fact]
    public void Reports_a_numbered_stage_as_text_for_a_transcend_boss()
    {
        StatsEncounterPayload encounter = Encounter(Party(), 2300544, "어미잃은 변견 카푸");

        Assert.Equal("심연의 뿔 암굴", encounter.DungeonName);
        Assert.Equal("초월", encounter.Category);
        Assert.Null(encounter.Difficulty);
        Assert.Equal("4", encounter.Stage);
    }

    /// <summary>The 시련 difficulty block rides along for a trial boss — that is the whole reason it exists,
    /// since the mobCode cannot express which of the 4~16 levels was run.</summary>
    [Fact]
    public void Attaches_the_trial_difficulty_block_to_a_trial_boss()
    {
        DataManager dm = Party();
        dm.SaveTrialAffix(TrialAffixGroup.BossBuff, 4, arrivedAt: 0);
        dm.SaveTrialAffix(TrialAffixGroup.BakronSkillUpgrade, 4, arrivedAt: 0);

        StatsEncounterPayload encounter = Encounter(dm, 2300582, "바크론");

        Assert.NotNull(encounter.Trial);
        Assert.Equal(4, encounter.Trial!.BossBuff);
    }

    /// <summary>...and never to anything else. The knobs are read from abnormals only the trial broadcasts, but
    /// they had no expiry until 2026-08-11 and outlived the run — so a 초월 battle fought after a trial uploaded
    /// that trial's difficulty attached to it, the upload-side twin of the "(시련 13~16단계)" label bug. The
    /// tracker now clears on leaving; this keeps the payload right even if it ever leaks again.</summary>
    [Fact]
    public void Never_attaches_a_trial_difficulty_block_to_another_dungeon()
    {
        DataManager dm = Party();
        dm.SaveTrialAffix(TrialAffixGroup.BossBuff, 4, arrivedAt: 0);
        dm.SaveTrialAffix(TrialAffixGroup.BakronSkillUpgrade, 4, arrivedAt: 0);
        Assert.True(dm.TrialDifficulty.Current.IsTrial); // the tracker is still holding the trial's knobs

        StatsEncounterPayload encounter = Encounter(dm, 2300544, "어미잃은 변견 카푸"); // 초월 4단계

        Assert.Null(encounter.Trial);
    }

    /// <summary>The decorated name is a UI concern only. The server falls back to matching on bossName, so
    /// sending "바크론 (시련)" would break that fallback for anything the mobCode doesn't resolve.</summary>
    [Fact]
    public void Boss_name_stays_raw_never_the_difficulty_decorated_one()
    {
        DataManager dm = Party();

        StatsEncounterPayload encounter = Encounter(dm, 2300582, "바크론");

        Assert.Equal("바크론", encounter.BossName);
        Assert.DoesNotContain("시련", encounter.BossName);
        // ...even though that is exactly what the meter puts on screen for this fight.
        Assert.Equal("바크론 (시련)", dm.Encounters.DisplayName(2300582, "바크론"));
    }

    /// <summary>An uncatalogued boss still uploads with just its code and name (the upload gate decides whether
    /// it goes at all — the builder must not invent dungeon fields for it).</summary>
    [Fact]
    public void Leaves_the_descriptive_fields_null_for_an_uncatalogued_boss()
    {
        StatsEncounterPayload encounter = Encounter(Party(), 2600068, "정령왕 아그로");

        Assert.Equal(2600068, encounter.MobCode);
        Assert.Equal("정령왕 아그로", encounter.BossName);
        Assert.Null(encounter.DungeonName);
        Assert.Null(encounter.Category);
        Assert.Null(encounter.Difficulty);
        Assert.Null(encounter.Stage);
        Assert.Null(encounter.BossIndex);
    }

    /// <summary>
    /// 동결이 라이브 추적기를 이긴다. 업로드는 스풀에 며칠 묵었다 재시도될 수 있고, 그때 추적기는 이미 다른
    /// 시련(또는 빈 값)을 들고 있다 — 그 값을 실어 보내면 서버에 남아 되돌릴 수 없다.
    /// </summary>
    [Fact]
    public void The_frozen_difficulty_wins_over_whatever_the_tracker_holds_now()
    {
        DataManager dm = Party();
        dm.SaveTrialAffix(TrialAffixGroup.BossBuff, 4, arrivedAt: 0);
        dm.SaveTrialAffix(TrialAffixGroup.BakronSkillUpgrade, 4, arrivedAt: 0);
        DpsLog log = Log(dm, 2300582, "바크론"); // 이 시점의 난이도가 리포트에 동결된다

        // 이후 추적기가 완전히 다른 상태가 된다 — 다음 시련에 들어갔거나, 던전을 나가 비워졌거나.
        dm.TrialDifficulty.Reset();
        dm.SaveTrialAffix(TrialAffixGroup.BossBuff, 1, arrivedAt: 0);

        var builder = new StatsPayloadBuilder(dm, () => false);
        StatsEncounterPayload encounter =
            Assert.IsType<BuildResult.Payload>(builder.Build(log, "test", killConfirmed: true)).Value.Encounter;

        Assert.NotNull(encounter.Trial);
        Assert.Equal(4, encounter.Trial!.BossBuff);
    }
}
