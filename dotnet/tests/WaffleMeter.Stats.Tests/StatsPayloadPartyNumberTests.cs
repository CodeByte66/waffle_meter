using WaffleMeter.Capture;
using WaffleMeter.Data;
using WaffleMeter.Stats;
using Xunit;

namespace WaffleMeter.Stats.Tests;

/// <summary>
/// Covers 공대 sub-party tagging on the upload payload. Each participant carries its raw roster slot; the
/// PARTY NUMBER is deliberately not derived here, because the sub-party boundary depends on the raid size
/// (4 for an 8-인 공대, 5 for a 10-인) and the site is the side that knows it. For a normal party the tags
/// stay null and are omitted on send.
/// </summary>
public sealed class StatsPayloadPartyNumberTests
{
    // Two damage-dealers (Me = executor, Ally) vs a boss; PartySlots is set per-test to model the roster.
    private static (DataManager Dm, DpsLog Log) Scene()
    {
        var dm = new DataManager();
        dm.SaveNickname(1, "Me", isExecutor: true, server: 3, jobByte: 5);
        dm.SaveUserPower(1, 5000);
        dm.SaveNickname(2, "Ally", isExecutor: false, server: 3, jobByte: 25);
        dm.SaveUserPower(2, 3000);

        var report = new DpsReport
        {
            Contributors = new List<User> { dm.User(1)!, dm.User(2)! },
            BattleStart = 1_000_000,
            BattleEnd = 1_030_000,
            Target = new MobInfo(100, new Mob(12345, "보스", true), remainHp: 0, maxHp: 1_000_000),
            Information = new Dictionary<int, DpsInformation>
            {
                [1] = new DpsInformation(1_000_000, 50_000, 60.0, 40.0),
                [2] = new DpsInformation(600_000, 30_000, 40.0, 24.0),
            },
        };
        var log = new DpsLog
        {
            Report = report,
            SkillDetails = new Dictionary<int, Dictionary<string, AnalyzedSkill>>
            {
                [1] = new() { ["11020001"] = new AnalyzedSkill { SkillCode = 11020001, Name = "강타", DamageAmount = 1_000_000, Times = 100 } },
                [2] = new() { ["15210001"] = new AnalyzedSkill { SkillCode = 15210001, Name = "파이어", DamageAmount = 600_000, Times = 50 } },
            },
        };
        return (dm, log);
    }

    private static StatsUploadPayload Build(DataManager dm, DpsLog log)
    {
        var builder = new StatsPayloadBuilder(dm, publicCharacterProvider: () => false, clock: () => 1_700_000_000_000);
        return Assert.IsType<BuildResult.Payload>(builder.Build(log, "2.0.0", killConfirmed: true)).Value;
    }

    [Fact]
    public void Raid_tags_the_roster_slot_and_leaves_the_party_number_to_the_site()
    {
        (DataManager dm, DpsLog log) = Scene();
        log.Report.PartyRosterSize = 8;
        log.Report.PartySlots = new Dictionary<int, int>
        {
            [1] = 2, [2] = 6, [10] = 1, [11] = 3, [12] = 4, [13] = 5, [14] = 7, [15] = 8,
        };

        StatsUploadPayload payload = Build(dm, log);

        StatsParticipantPayload me = payload.Participants.Single(p => p.IsUploader);
        StatsParticipantPayload ally = payload.Participants.Single(p => !p.IsUploader);
        Assert.Equal(2, me.PartySlot);
        Assert.Equal(6, ally.PartySlot);
        // Not derived: an 8-인 공대 splits 4+4, so slot 5 is party 2 — the old (slot-1)/5+1 said party 1 and
        // the site rejected the whole battle's sub-party split over that one disagreement.
        Assert.Null(me.PartyNumber);
        Assert.Null(ally.PartyNumber);
    }

    [Fact]
    public void Raid_is_decided_by_roster_size_not_by_who_dealt_damage()
    {
        // An 공대 where nobody in the second party landed a hit on this boss (split mechanics do exactly this).
        // The old test — "does any slot exceed 5" — read this as a normal party and dropped every tag.
        (DataManager dm, DpsLog log) = Scene();
        log.Report.PartyRosterSize = 10;
        log.Report.PartySlots = new Dictionary<int, int> { [1] = 2, [2] = 4 };

        StatsUploadPayload payload = Build(dm, log);

        Assert.Equal(2, payload.Participants.Single(p => p.IsUploader).PartySlot);
        Assert.Equal(4, payload.Participants.Single(p => !p.IsUploader).PartySlot);
    }

    [Fact]
    public void A_stale_high_slot_no_longer_turns_a_normal_party_into_a_raid()
    {
        // PartySlots can hold entries for uids that aren't in this battle (a roster member resolved to a
        // lingering uid). Judging "raid" off those values let one stale slot 6 tag a 5-인 party — which the
        // site then warns about and discards. The roster size can't be fooled that way.
        (DataManager dm, DpsLog log) = Scene();
        log.Report.PartyRosterSize = 5;
        log.Report.PartySlots = new Dictionary<int, int> { [1] = 1, [2] = 2, [99] = 6 };

        StatsUploadPayload payload = Build(dm, log);

        Assert.All(payload.Participants, p => Assert.Null(p.PartySlot));
        Assert.All(payload.Participants, p => Assert.Null(p.PartyNumber));
    }

    [Fact]
    public void A_battle_saved_before_the_roster_size_existed_falls_back_to_the_slot_inference()
    {
        // Re-uploading history saved by an older build: PartyRosterSize deserializes to 0, so the old
        // "any slot above 5" inference still applies rather than silently dropping the tags.
        (DataManager dm, DpsLog log) = Scene();
        log.Report.PartyRosterSize = 0;
        log.Report.PartySlots = new Dictionary<int, int> { [1] = 2, [2] = 6 };

        StatsUploadPayload payload = Build(dm, log);

        Assert.Equal(2, payload.Participants.Single(p => p.IsUploader).PartySlot);
        Assert.Equal(6, payload.Participants.Single(p => !p.IsUploader).PartySlot);
    }

    [Fact]
    public void Non_raid_party_leaves_party_tags_null()
    {
        (DataManager dm, DpsLog log) = Scene();
        log.Report.PartyRosterSize = 5;
        log.Report.PartySlots = new Dictionary<int, int> { [1] = 1, [2] = 2 };

        StatsUploadPayload payload = Build(dm, log);

        Assert.All(payload.Participants, p => Assert.Null(p.PartyNumber));
        Assert.All(payload.Participants, p => Assert.Null(p.PartySlot));
    }

    [Fact]
    public void Roster_combat_power_rescues_a_battle_the_lookup_would_have_dropped()
    {
        // A participant with no combat power fails the whole upload (the site's schema requires a positive
        // number for every participant), and the only source that used to cover the gap was a synchronous
        // lookup against the official site — which returns nothing at all for a private profile, and caches a
        // failure for ten minutes when the site hiccups. The 0x9702 roster carries the same number over the
        // wire and the parser already stores it.
        var dm = new DataManager { Clock = () => 1_000_000 };
        dm.SaveNickname(1, "Me", isExecutor: true, server: 3, jobByte: 5);
        dm.SaveUserPower(1, 5000);
        dm.SaveNickname(2, "Ally", isExecutor: false, server: 3, jobByte: 25);
        // Ally's power never arrived on a packet: no 0x3645 snapshot, no lookup.
        dm.SavePartyRoster(new List<(string, int, int)> { ("Me", 3, 1), ("Ally", 3, 2) });
        dm.SavePartyRosterJobPower(new List<(string, int, int, int)> { ("Ally", 3, 25, 3300) });

        var report = new DpsReport
        {
            Contributors = new List<User> { dm.User(1)!, dm.User(2)! },
            BattleStart = 1_000_000,
            BattleEnd = 1_030_000,
            Target = new MobInfo(100, new Mob(12345, "보스", true), remainHp: 0, maxHp: 1_000_000),
            Information = new Dictionary<int, DpsInformation>
            {
                [1] = new DpsInformation(1_000_000, 50_000, 60.0, 40.0),
                [2] = new DpsInformation(600_000, 30_000, 40.0, 24.0),
            },
        };
        var log = new DpsLog
        {
            Report = report,
            SkillDetails = new Dictionary<int, Dictionary<string, AnalyzedSkill>>
            {
                [1] = new() { ["11020001"] = new AnalyzedSkill { SkillCode = 11020001, Name = "강타", DamageAmount = 1_000_000, Times = 100 } },
                [2] = new() { ["15210001"] = new AnalyzedSkill { SkillCode = 15210001, Name = "파이어", DamageAmount = 600_000, Times = 50 } },
            },
        };

        StatsUploadPayload payload = Build(dm, log);

        Assert.Equal(3300, payload.Participants.Single(p => !p.IsUploader).Power);
    }

    [Fact]
    public void A_stale_roster_snapshot_does_not_supply_combat_power()
    {
        // Fail-safe direction: past the freshness window the roster is somebody else's party, so the fallback
        // stays out of it rather than tagging the participant with a stale number. 그 결과는 이제 "전투 폐기"가
        // 아니라 "그 참가자만 null" 이다 — 값을 모르는 것과 값이 0 인 것은 다르고, 모른다고 전투를 버리지 않는다.
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SaveNickname(1, "Me", isExecutor: true, server: 3, jobByte: 5);
        dm.SaveUserPower(1, 5000);
        dm.SaveNickname(2, "Ally", isExecutor: false, server: 3, jobByte: 25);
        dm.SavePartyRoster(new List<(string, int, int)> { ("Me", 3, 1), ("Ally", 3, 2) });
        dm.SavePartyRosterJobPower(new List<(string, int, int, int)> { ("Ally", 3, 25, 3300) });

        now += 31L * 60 * 1000; // past the 30-minute window

        var report = new DpsReport
        {
            Contributors = new List<User> { dm.User(1)!, dm.User(2)! },
            BattleStart = now,
            BattleEnd = now + 30_000,
            Target = new MobInfo(100, new Mob(12345, "보스", true), remainHp: 0, maxHp: 1_000_000),
            Information = new Dictionary<int, DpsInformation>
            {
                [1] = new DpsInformation(1_000_000, 50_000, 60.0, 40.0),
                [2] = new DpsInformation(600_000, 30_000, 40.0, 24.0),
            },
        };
        var log = new DpsLog { Report = report, SkillDetails = new Dictionary<int, Dictionary<string, AnalyzedSkill>>() };

        var builder = new StatsPayloadBuilder(dm, publicCharacterProvider: () => false, clock: () => 1_700_000_000_000);
        BuildResult result = builder.Build(log, "2.0.0", killConfirmed: true);

        StatsUploadPayload payload = Assert.IsType<BuildResult.Payload>(result).Value;
        Assert.Null(payload.Participants.Single(p => !p.IsUploader).Power); // 낡은 스냅샷 값이 새어 들어오지 않는다
    }

    [Fact]
    public void No_roster_leaves_party_tags_null()
    {
        (DataManager dm, DpsLog log) = Scene(); // PartySlots empty (no 0x9702 captured)

        StatsUploadPayload payload = Build(dm, log);

        Assert.All(payload.Participants, p => Assert.Null(p.PartyNumber));
    }
}
