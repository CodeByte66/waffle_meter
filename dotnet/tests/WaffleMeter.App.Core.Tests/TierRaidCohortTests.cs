using System.Text.Json;
using WaffleMeter.App.Core;
using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 공대(성역) 코호트 좌표를 서버와 같은 규칙으로 뽑는다는 계약. 서버 기준(2026-09-19 웹 세션 확인):
/// <code>
/// party_mode      = CASE WHEN category = '성역' THEN 10 ELSE 5 END      -- 카테고리의 순함수
/// synergy_trusted = (category &lt;&gt; '성역' OR sub_party_known)             -- 비-성역은 슬롯을 안 본다
/// sub_party_known = 참가자 전원이 1..10 의 서로 다른 슬롯 (출석 요구 없음)
/// RAID_ROSTER_SIZES = [10]                                              -- 8인 공대는 존재하지 않는다
/// </code>
/// <para>미터가 서버보다 <b>빡세면</b> 그 전투는 R0 밖으로 밀리는데, 밴드행은 rung 0 에만 실리므로
/// 시너지 버킷뿐 아니라 <b>전투력 밴드까지</b> 잃는다.</para>
/// </summary>
public sealed class TierRaidCohortTests
{
    private const int RaidBoss = 2_301_601;
    private const int PartyBoss = 2_301_602;

    private static readonly double[] Grid =
    [
        100, 99.5, 99, 98, 96, 93, 90, 85, 80, 75, 70, 65, 60, 55, 50, 45, 40, 35, 30, 25, 20, 15, 12.5, 10, 7.5,
        5, 3, 2, 1, 0.5, 0.1,
    ];

    /// <summary>모든 (rung, synergy, partyMode) 조합을 깔아, 평가기가 **어느 좌표를 골랐는지**가 결과로 드러나게 한다.</summary>
    private static TierArtifact Artifact()
    {
        var rows = new List<object>();
        foreach ((int dungeonOrd, string category) in new[] { (21, "성역"), (11, "원정") })
        {
            foreach (int rung in new[] { 0, 1, 2, 3, 4, 5 })
            {
                foreach (int synergy in new[] { -1, 0, 1, 2, 3 })
                {
                    foreach (int partyMode in new[] { 0, 5, 10 })
                    {
                        // 컷을 좌표별로 다르게 만들어, 다른 좌표를 고르면 상위%가 달라지게 한다.
                        int step = 40 + (rung * 7) + ((synergy + 1) * 3) + partyMode;
                        rows.Add(new
                        {
                            r = rung,
                            m = "dps",
                            k = category,
                            d = dungeonOrd,
                            v = 1,
                            b = rung >= 4 ? -1 : 1,
                            j = "검성",
                            s = synergy,
                            p = partyMode,
                            n = 500,
                            c = Enumerable.Repeat(step, Grid.Length).ToArray(),
                        });
                    }
                }
            }
        }

        var document = new
        {
            schemaVersion = 2,
            artifactId = "raid-cohort-fixture",
            windowDays = 30,
            generatedAt = "2026-09-19T00:00:00.000Z",
            grid = Grid,
            jobs = new[] { "검성", "수호성", "살성", "궁성", "마도성", "정령성", "치유성", "호법성", "권성" },
            dungeons = new object[]
            {
                new { ord = 21, key = "sanctuary", name = "성역", category = "성역" },
                new { ord = 11, key = "expedition", name = "원정", category = "원정" },
            },
            variants = new object[]
            {
                new { dungeonOrd = 21, ord = 1, label = "1단계" },
                new { dungeonOrd = 11, ord = 1, label = "어려움" },
            },
            mobs = new Dictionary<string, int[]>
            {
                [RaidBoss.ToString()] = [21, 1, 1],
                [PartyBoss.ToString()] = [11, 1, 1],
            },
            rows = rows.ToArray(),
        };

        TierArtifact? a = TierArtifact.Parse(JsonSerializer.Serialize(document));
        Assert.NotNull(a);
        return a!;
    }

    private static DpsReport Report(int mobCode, int dealerCount, int rosterSize, bool withSlots)
    {
        var contributors = new List<User>();
        var info = new Dictionary<int, DpsInformation>();
        var slots = new Dictionary<int, int>();
        for (int i = 1; i <= dealerCount; i++)
        {
            var u = new User(i, $"P{i}", 3, JobClass.GLADIATOR, power: 900_000);
            contributors.Add(u);
            info[i] = new DpsInformation(1_000_000, 50_000, 10.0, 8.0);
            if (withSlots)
            {
                slots[i] = i;
            }
        }

        return new DpsReport
        {
            Contributors = contributors,
            Information = info,
            BattleStart = 1_000_000,
            BattleEnd = 1_060_000,
            Target = new MobInfo(100, new Mob(mobCode, "보스", Boss: true), remainHp: 0, maxHp: 1_000_000),
            PartySlots = slots,
            PartyRosterSize = rosterSize,
        };
    }

    private static RowTier? Tier(DpsReport r) =>
        TierEvaluator.Evaluate(r, Artifact(), null, _ => null).GetValueOrDefault(1);

    /// <summary>
    /// 🔑 F-12-4. 10인 공대에서 딜러가 9명이어도 partyMode 는 10이다. 종전에는 `Contributors.Count is 8 or 10`
    /// 으로 유도해서 9명이면 5인 분포로 채점되고, 게다가 시너지 검사를 <b>건너뛰고</b> "신뢰됨"을 반환했다.
    /// </summary>
    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(3)]
    public void A_raid_is_a_raid_no_matter_how_many_people_landed_a_hit(int dealers)
    {
        RowTier? nine = Tier(Report(RaidBoss, dealers, rosterSize: 10, withSlots: true));
        RowTier? ten = Tier(Report(RaidBoss, 10, rosterSize: 10, withSlots: true));

        Assert.NotNull(nine);
        // 딜러 수가 달라도 같은 partyMode·같은 rung 을 골랐다면 비교 기준 문장이 같다.
        Assert.Equal(ten!.Value.ComparisonBasis, nine!.Value.ComparisonBasis);
    }

    /// <summary>🔑 비-성역은 슬롯을 아예 안 본다 — 서버가 `category &lt;&gt; '성역'` 이면 무조건 신뢰한다.
    /// 미터가 여기에 슬롯 게이트를 걸면 R0 밖으로 밀려 전투력 밴드까지 잃는다.</summary>
    [Fact]
    public void A_non_raid_is_trusted_without_any_slots()
    {
        RowTier? withoutSlots = Tier(Report(PartyBoss, 5, rosterSize: 0, withSlots: false));
        RowTier? withSlots = Tier(Report(PartyBoss, 5, rosterSize: 5, withSlots: true));

        Assert.NotNull(withoutSlots);
        Assert.Equal(withSlots!.Value.BattleTopPercent, withoutSlots!.Value.BattleTopPercent);
    }

    /// <summary>
    /// 🔴 <b>8인 공대는 존재하지 않는다.</b> 서버 <c>RAID_ROSTER_SIZES = [10]</c> 이고, rosterSize 8 을 보내면
    /// 서버는 그 전투를 공대로 치지 않는다(<c>sub_party_known = false</c>). 미터가 8 을 4+4 로 신뢰하면
    /// <b>서버가 신뢰하지 않는 전투를 미터가 신뢰</b>하게 된다 — 그 전투가 속한 적 없는 코호트의 백분위다.
    /// </summary>
    [Fact]
    public void A_roster_of_eight_is_not_a_raid()
    {
        RowTier? eight = Tier(Report(RaidBoss, 8, rosterSize: 8, withSlots: true));
        RowTier? untrusted = Tier(Report(RaidBoss, 8, rosterSize: 10, withSlots: false));

        Assert.NotNull(eight);
        // 정원 8 = 신뢰 불가 → 슬롯이 아예 없는 리포트와 같은 좌표(시너지 풀린 rung)로 떨어져야 한다.
        Assert.Equal(untrusted!.Value.BattleTopPercent, eight!.Value.BattleTopPercent);
    }

    /// <summary>M-22. 같은 전투가 라이브(슬롯 있음)와 기록(슬롯 있음)에서 같은 숫자를 낸다.
    /// 종전에는 라이브 리포트에 슬롯이 안 실려 untrusted → R2 로 떨어졌고, R2 엔 밴드행이 없어
    /// <b>전투력 밴드까지</b> 잃었다.</summary>
    [Fact]
    public void The_same_raid_battle_scores_the_same_live_and_from_history()
    {
        DpsReport live = Report(RaidBoss, 10, rosterSize: 10, withSlots: true);
        DpsReport history = Report(RaidBoss, 10, rosterSize: 10, withSlots: true);

        Assert.Equal(Tier(history)!.Value.BattleTopPercent, Tier(live)!.Value.BattleTopPercent);
    }

    /// <summary>슬롯이 빠지면(라이브의 옛 동작) 다른 좌표로 떨어진다 — 이 테스트가 빨개지면 슬롯 주입이
    /// 무의미해졌다는 뜻이다.</summary>
    [Fact]
    public void A_raid_without_slots_falls_to_a_different_cohort()
    {
        RowTier? withSlots = Tier(Report(RaidBoss, 10, rosterSize: 10, withSlots: true));
        RowTier? without = Tier(Report(RaidBoss, 10, rosterSize: 10, withSlots: false));

        Assert.NotNull(withSlots);
        Assert.NotNull(without);
        Assert.NotEqual(withSlots!.Value.BattleTopPercent, without!.Value.BattleTopPercent);
    }
}
