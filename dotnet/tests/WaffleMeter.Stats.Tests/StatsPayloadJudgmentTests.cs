using WaffleMeter.Capture;
using WaffleMeter.Data;
using WaffleMeter.Stats;
using Xunit;

namespace WaffleMeter.Stats.Tests;

/// <summary>
/// Locks how the judgment observations reach the wire: raw counters on the uploader's row only, and the
/// cross-tab block at the payload root.
///
/// <para>The scope rule is not a size optimization. Those integers only mean something when paired with the
/// stat that produced them, and the game broadcasts the stat dictionary for the local player alone — a party
/// member's counters would be an observation with no matching covariate, which is exactly the shape of data
/// that invites a wrong answer rather than no answer.</para>
/// </summary>
public sealed class StatsPayloadJudgmentTests
{
    private static DataManager TwoPlayerParty()
    {
        var dm = new DataManager();
        dm.SaveNickname(1, "Me", isExecutor: true, server: 3, jobByte: 5);
        dm.SaveUserPower(1, 5000);
        dm.SaveNickname(2, "Ally", isExecutor: false, server: 3, jobByte: 25);
        dm.SaveUserPower(2, 3000);
        return dm;
    }

    private static DpsLog SampleLog(DataManager dm, SelfJudgmentSnapshot? judgment = null)
    {
        User me = dm.User(1)!;
        User ally = dm.User(2)!;

        var report = new DpsReport
        {
            Contributors = new List<User> { me, ally },
            BattleStart = 1_000_000,
            BattleEnd = 1_030_000,
            Target = new MobInfo(100, new Mob(12345, "센터보스", true), remainHp: 0, maxHp: 1_000_000),
            Information = new Dictionary<int, DpsInformation>
            {
                [1] = new DpsInformation(1_000_000, 50_000, 60.0, 40.0),
                [2] = new DpsInformation(600_000, 30_000, 40.0, 24.0),
            },
            SelfJudgment = judgment,
        };

        return new DpsLog
        {
            Report = report,
            SkillDetails = new Dictionary<int, Dictionary<string, AnalyzedSkill>>
            {
                [1] = new()
                {
                    ["11020001"] = new AnalyzedSkill
                    {
                        SkillCode = 11020001, Name = "가르기", DamageAmount = 1_000_000,
                        Times = 100, FlaggedTimes = 90, CritTimes = 72, DoubleTimes = 55, PerfectTimes = 60,
                        BackTimes = 30, ParryTimes = 4, EligibleDamage = 700_000,
                        SummonTimes = 8, SummonFlaggedTimes = 7, SummonDoubleTimes = 3, SummonPerfectTimes = 4,
                    },
                },
                [2] = new()
                {
                    ["15210001"] = new AnalyzedSkill
                    {
                        SkillCode = 15210001, Name = "파이어", DamageAmount = 600_000,
                        Times = 50, FlaggedTimes = 45, CritTimes = 30, DoubleTimes = 20,
                    },
                },
            },
            BuffRates = new Dictionary<int, List<OperatingData>>(),
            BossBuffRates = new List<OperatingData>(),
        };
    }

    private static StatsUploadPayload BuildOk(DataManager dm, DpsLog log)
    {
        BuildResult result = new StatsPayloadBuilder(dm, () => false, () => 1_700_000_000_000)
            .Build(log, "2.14.0", killConfirmed: true);
        return Assert.IsType<BuildResult.Payload>(result).Value;
    }

    private static SelfJudgmentSnapshot SampleJudgment()
    {
        var acc = new SelfJudgmentAccumulator();
        for (int i = 0; i < 40; i++)
        {
            acc.Accumulate(new ParsedDamagePacket
            {
                ActorId = 1,
                TargetId = 100,
                Damage = 1_000,
                SkillCode = 11_020_001,
                RawSkillCode = 11_020_001,
                Type = i % 5 == 0 ? 3 : 2,
                SwitchVariable = 0x36,
                Position = 2,
                Timestamp = 1_000_000 + i,
                Specials = i % 2 == 0 ? [SpecialDamage.DOUBLE] : [],
                JudgmentStats = new JudgmentStatStamp(
                    JudgmentStatStamp.HasHardHit | JudgmentStatStamp.HasPerfect | JudgmentStatStamp.HasAccuracy
                    | JudgmentStatStamp.HasCritical | JudgmentStatStamp.HasCriticalInc,
                    HardHitBp: 8_284, PerfectBp: 5_308, Accuracy: 1_807,
                    CriticalBp: 2_278, CriticalIncBp: 5_950, At: 1_000_000 + i),
            }, battleStart: 1_000_000);
        }

        var sheet = new PlayerStatSheet(
            new Dictionary<int, int>
            {
                [PlayerStatIds.WeaponAccuracy] = 391,
                [PlayerStatIds.PveAccuracy] = 320,
                [PlayerStatIds.AccuracyIncreasePercent] = 5_350,
                [PlayerStatIds.BlockPierce] = 210,
                [PlayerStatIds.CriticalIncreasePercent] = 5_950,
            },
            UpdatedAt: 1L,
            FullSnapshotSeen: true);

        return acc.Build(12345, sheet);
    }

    [Fact]
    public void UploaderRowCarriesRawCounters()
    {
        DataManager dm = TwoPlayerParty();
        StatsUploadPayload payload = BuildOk(dm, SampleLog(dm));

        StatsResultPayload own = payload.Result;
        Assert.Equal(90, own.FlaggedHits);
        Assert.Equal(55, own.SmiteHits);
        Assert.Equal(60, own.PerfectHits);
        Assert.Equal(72, own.CritHits);
        Assert.Equal(4, own.ParryHits);
        Assert.Equal(30, own.BackTimes);
        Assert.Equal(700_000L, own.EligibleDamage);
        Assert.Equal(7, own.SummonFlaggedHits);
        Assert.Equal(3, own.SummonSmiteHits);
        Assert.Equal(4, own.SummonPerfectHits);
    }

    /// <summary>The party member's row must stay exactly as it was. Their counters exist in the meter, but
    /// their stat sheet does not exist anywhere, so shipping the counters alone would add observations that
    /// cannot be interpreted — and would widen what the meter collects about people who never saw a consent
    /// screen.</summary>
    [Fact]
    public void PartyMemberRowsCarryNoJudgmentCounters()
    {
        DataManager dm = TwoPlayerParty();
        StatsUploadPayload payload = BuildOk(dm, SampleLog(dm));

        StatsParticipantPayload ally = payload.Participants.Single(p => !p.IsUploader);
        Assert.Null(ally.Result.FlaggedHits);
        Assert.Null(ally.Result.SmiteHits);
        Assert.Null(ally.Result.PerfectHits);
        Assert.Null(ally.Result.CritHits);
        Assert.Null(ally.Result.ParryHits);
        Assert.Null(ally.Result.BackTimes);
        Assert.Null(ally.Result.EligibleDamage);
        Assert.Null(ally.Result.SummonFlaggedHits);

        // …while the uploader's participant row matches the root Result exactly.
        StatsParticipantPayload own = payload.Participants.Single(p => p.IsUploader);
        Assert.Equal(payload.Result.FlaggedHits, own.Result.FlaggedHits);
        Assert.Equal(payload.Result.EligibleDamage, own.Result.EligibleDamage);
    }

    /// <summary>flaggedHits doubles as the row-level marker that a judgment-aware meter wrote the row —
    /// clientVersion cannot serve, because a merged report keeps the first uploader's version string while its
    /// participant rows get repainted by later payloads. So it ships even when it is 0.</summary>
    [Fact]
    public void FlaggedHitsIsSentEvenWhenZero()
    {
        DataManager dm = TwoPlayerParty();
        DpsLog log = SampleLog(dm);
        foreach (AnalyzedSkill skill in log.SkillDetails[1].Values) skill.FlaggedTimes = 0;

        StatsUploadPayload payload = BuildOk(dm, log);

        Assert.Equal(0, payload.Result.FlaggedHits);
        Assert.NotNull(payload.Result.FlaggedHits);
    }

    [Fact]
    public void SelfJudgmentBlockIsEmittedAtRootWithConsistentTotals()
    {
        DataManager dm = TwoPlayerParty();
        StatsUploadPayload payload = BuildOk(dm, SampleLog(dm, SampleJudgment()));

        StatsSelfJudgmentPayload judgment = Assert.IsType<StatsSelfJudgmentPayload>(payload.SelfJudgment);
        Assert.Equal(12345, judgment.TargetMobCode);
        Assert.Equal(40, judgment.EligibleHits);
        Assert.Equal(40, judgment.Smite.N);
        Assert.Equal(20, judgment.Smite.Hits);
        Assert.Equal(judgment.Smite.N, judgment.Smite.Bins.Sum(b => b[2]));
        Assert.Equal(judgment.Smite.Hits, judgment.Smite.Bins.Sum(b => b[3]));
        Assert.Equal(8, judgment.Crit.Hits);

        // 2,278 × 1.595 = 3,633 → 3,600 bucket. Matches PlayerStatStore.CriticalTotal, which was checked
        // against the in-game stat window.
        Assert.Equal(3_600, judgment.Crit.Bins.Single()[0]);

        // 강타 82.84% → the 82 bucket (2%p wide, floored).
        Assert.Equal(82, judgment.Smite.Bins.Single()[0]);

        Assert.Equal("full", judgment.Stats.Src);
        Assert.Equal(391, judgment.Stats.Acc318);
        Assert.Equal(320, judgment.Stats.Pve110);
        Assert.Equal(210, judgment.Stats.BlockPierce256);
    }

    /// <summary>An empty late table is omitted rather than sent as [] — the server must be able to tell "no
    /// ten-minute hits" from "this meter does not populate that field".</summary>
    [Fact]
    public void EmptyLateBinsAreOmitted()
    {
        DataManager dm = TwoPlayerParty();
        StatsUploadPayload payload = BuildOk(dm, SampleLog(dm, SampleJudgment()));

        Assert.Null(payload.SelfJudgment!.Smite.LateBins);
    }

    /// <summary>A battle with no own damage carries no block at all — an empty cross-tab would look like a
    /// player who attacked 0 times, which is not what happened and not what should be aggregated.</summary>
    [Fact]
    public void SelfJudgmentIsOmittedWhenNothingWasObserved()
    {
        DataManager dm = TwoPlayerParty();
        StatsUploadPayload payload = BuildOk(dm, SampleLog(dm, judgment: null));

        Assert.Null(payload.SelfJudgment);
    }

    /// <summary>The schema number must NOT move for this change. The web pins it to a literal union and the
    /// meter does not retry a 4xx, so shipping an unrecognized number destroys those battles permanently;
    /// optional fields, by contrast, are dropped harmlessly by a server that does not know them yet.</summary>
    [Fact]
    public void SchemaVersionStaysAtSix()
    {
        DataManager dm = TwoPlayerParty();
        Assert.Equal(6, BuildOk(dm, SampleLog(dm, SampleJudgment())).SchemaVersion);
    }
}
