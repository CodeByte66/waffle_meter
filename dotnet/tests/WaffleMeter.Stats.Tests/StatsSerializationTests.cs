using WaffleMeter.Stats;
using Xunit;

namespace WaffleMeter.Stats.Tests;

public sealed class StatsSerializationTests
{
    [Fact]
    public void Character_payload_uses_public_key_camelcase_and_omits_null_job()
    {
        var payload = new StatsCharacterPayload(
            IdentityHash: "abc",
            Nickname: "Hero",
            Server: 3,
            Job: null,
            Power: 1000,
            Public: true);

        string json = StatsJson.Serialize(payload);

        Assert.Contains("\"identityHash\":\"abc\"", json);
        Assert.Contains("\"public\":true", json);
        Assert.DoesNotContain("\"Public\"", json);
        Assert.DoesNotContain("\"job\"", json); // null omitted (explicitNulls=false)
    }

    [Fact]
    public void Encounter_payload_omits_null_optionals_but_keeps_set_ones()
    {
        string omitted = StatsJson.Serialize(new StatsEncounterPayload(MobCode: 5, BossName: "Boss"));
        Assert.DoesNotContain("dungeonName", omitted);
        Assert.DoesNotContain("stage", omitted);

        // stage is TEXT on the wire, not a number — the server's schema types it as nullable text next to
        // difficulty, and a numeric one is rejected outright.
        string set = StatsJson.Serialize(new StatsEncounterPayload(5, "Boss", DungeonName: "Abyss", Stage: "2"));
        Assert.Contains("\"dungeonName\":\"Abyss\"", set);
        Assert.Contains("\"stage\":\"2\"", set);
    }

    [Fact]
    public void Own_character_writes_non_null_defaults()
    {
        // encodeDefaults=true: detected:false and the numeric defaults are written; only nulls drop.
        string json = StatsJson.Serialize(new StatsOwnCharacter(Detected: false));
        Assert.Contains("\"detected\":false", json);
        Assert.Contains("\"server\":-1", json);
        Assert.DoesNotContain("nickname", json); // null
    }

    [Fact]
    public void Consent_status_response_reads_public_key_and_ignores_unknown()
    {
        const string serverJson = """
            {"ok":true,"identityHash":"h","exists":true,"consentState":"accepted",
             "public":true,"consentVersion":"2026-06-04","unexpectedField":42}
            """;

        ConsentStatusResponse response = StatsJson.Deserialize<ConsentStatusResponse>(serverJson);

        Assert.True(response.Ok);
        Assert.True(response.Exists);
        Assert.Equal("accepted", response.ConsentState);
        Assert.True(response.PublicCharacter);
        Assert.Equal("2026-06-04", response.ConsentVersion);
        Assert.Null(response.LastSeenAt); // missing -> default null
    }

    [Fact]
    public void Consent_event_character_serializes_public_key()
    {
        var character = new ConsentEventCharacter(
            IdentityHash: "h",
            Nickname: "Hero",
            Server: 3,
            PublicCharacter: false,
            Job: "검성",
            Power: 1234);

        string json = StatsJson.Serialize(character);

        Assert.Contains("\"public\":false", json);
        Assert.Contains("\"job\":\"검성\"", json);
        Assert.Contains("\"power\":1234", json);
    }

    [Fact]
    public void Result_payload_serializes_front_rate_next_to_back_rate()
    {
        var result = new StatsResultPayload(
            TotalDamage: 1000, Dps: 50, PartyContribution: 10, BossHpContribution: 5, HitCount: 100,
            CritRate: 20, StrongRate: 10, PerfectRate: 5, BackRate: 30, FrontRate: 46.9, ParryRate: 0, BossBlockRate: 0);

        string json = StatsJson.Serialize(result);

        Assert.Contains("\"backRate\":30", json);
        Assert.Contains("\"frontRate\":46.9", json);
    }

    [Fact]
    public void Dps_graph_dtos_serialize_the_web_contract_field_names()
    {
        // These camelCase names ARE the web/zod contract (dpsSeries {step, damage}; selfBuffInterval {baseCode, name, spans}).
        string series = StatsJson.Serialize(new StatsDpsSeriesPayload(Step: 1, Damage: [100L, 0L, 200L]));
        Assert.Contains("\"step\":1", series);
        Assert.Contains("\"damage\":[100,0,200]", series);

        string interval = StatsJson.Serialize(new StatsSelfBuffIntervalPayload(BaseCode: 15210000, Name: "원소 강화", Spans: [5, 20]));
        Assert.Contains("\"baseCode\":15210000", interval);
        Assert.Contains("\"name\":\"원소 강화\"", interval);
        Assert.Contains("\"spans\":[5,20]", interval);
    }

    [Fact]
    public void Participant_rows_carry_their_own_dps_series_under_the_same_wire_key()
    {
        // v6 계약: 시계열이 participants[] 안에도 들어간다. 웹 zod가 participants 요소에 dpsSeries를 선언하기 전에는
        // unknown 키라 조용히 strip된다(400도 안 난다) — 그래서 키 이름이 정확히 일치해야 한다.
        var result = new StatsResultPayload(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        string withSeries = StatsJson.Serialize(new StatsParticipantPayload(
            IdentityHash: "abc", IsUploader: false, Job: "마도성", Power: 3000,
            Result: result, Skills: [], Buffs: [],
            DpsSeries: new StatsDpsSeriesPayload(Step: 2, Damage: [10L, 20L])));

        Assert.Contains("\"dpsSeries\":{\"step\":2,\"damage\":[10,20]}", withSeries);

        // 시계열이 없는 참가자는 빈 배열이 아니라 키 자체가 빠진다(StatsJson은 null만 생략한다).
        string without = StatsJson.Serialize(new StatsParticipantPayload(
            IdentityHash: "abc", IsUploader: false, Job: "마도성", Power: 3000,
            Result: result, Skills: [], Buffs: []));

        Assert.DoesNotContain("dpsSeries", without);
    }

    /// <summary>Locks the wire names and shape of the judgment block. The server writes its validator against
    /// these exact keys, and a rename here would strip the block silently — the schema has no strict mode, so
    /// an unknown key is dropped without an error at either end.</summary>
    [Fact]
    public void Self_judgment_block_uses_expected_wire_names_and_array_rows()
    {
        var payload = new StatsSelfJudgmentPayload(
            TargetMobCode: 2301721,
            EligibleHits: 120,
            StampAgeP50Ms: 400,
            FreshHits: 30,
            Smite: new StatsJudgmentAxisPayload(120, 66, [[82, 2, 120, 66]]),
            Perfect: new StatsJudgmentAxisPayload(120, 90, [[52, 2, 120, 90]]),
            Accuracy: new StatsJudgmentAccuracyPayload(120, 4, [[1800, 2, 120, 4]], [[11020001, 120, 4]]),
            Crit: new StatsJudgmentCritPayload(120, 96, [[3600, 11020001, 120, 96]], [40, 25]),
            Stats: new StatsSelfJudgmentStatsPayload("full", 31, 391, 320, 5350, 210, 5950));

        string json = StatsJson.Serialize(payload);

        Assert.Contains("\"targetMobCode\":2301721", json);
        Assert.Contains("\"eligibleHits\":120", json);
        Assert.Contains("\"stampAgeP50Ms\":400", json);
        Assert.Contains("\"smite\":{\"n\":120,\"hits\":66,\"bins\":[[82,2,120,66]]}", json);
        Assert.Contains("\"bySkill\":[[11020001,120,4]]", json);
        Assert.Contains("\"sw4\":[40,25]", json);
        Assert.Contains("\"blockPierce256\":210", json);
        Assert.Contains("\"src\":\"full\"", json);
        // lateBins is null here and must be omitted, not sent as an empty array.
        Assert.DoesNotContain("lateBins", json);
    }

    /// <summary>flaggedHits is the row-level marker that a judgment-aware meter wrote the row, so it must
    /// serialize even at 0 — while the other counters stay omitted on a party member's row.</summary>
    [Fact]
    public void Result_payload_keeps_zero_flagged_hits_and_omits_absent_counters()
    {
        string uploader = StatsJson.Serialize(new StatsResultPayload(
            1, 2, 3.0, 4.0, 5, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 0.0,
            FlaggedHits: 0, SmiteHits: 0, EligibleDamage: 0L));
        Assert.Contains("\"flaggedHits\":0", uploader);
        Assert.Contains("\"eligibleDamage\":0", uploader);

        string party = StatsJson.Serialize(new StatsResultPayload(
            1, 2, 3.0, 4.0, 5, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 0.0));
        Assert.DoesNotContain("flaggedHits", party);
        Assert.DoesNotContain("summonSmiteHits", party);
    }
}
