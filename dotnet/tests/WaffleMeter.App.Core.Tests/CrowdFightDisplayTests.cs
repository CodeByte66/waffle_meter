using WaffleMeter.App.Core;
using WaffleMeter.Capture;
using WaffleMeter.Data;
using WaffleMeter.Services;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 어비스 필드보스 군중전 표시 — 10행 상한과 보스 HP 기울기 ETA. 던전·공대·다른 지역 필드보스는 종전 그대로여야
/// 한다(10인 공대는 1.5배 여유 상한이 있어야 파티원이 안 잘린다).
/// </summary>
public sealed class CrowdFightDisplayTests
{
    private const int Kaira = 2600089;          // 감시자 카이라 — 어비스 하층
    private const int Agro = 2600068;           // 정령왕 아그로 — 어비스 하층
    private const int Kutar = 2100040;          // 썩은 쿠타르 — 베르테론 필드보스
    private const int Nihog = 2101343;          // 니호그 — 엘테넨 필드보스
    private const int Nayatman = 2300154;       // 부화의 나야트만 — 원정 던전 보스
    private const int Rudra = 2301014;          // 영겁의 루드라 — 성역(10인 공대)
    private const int BossEntity = 16497;
    private const long KairaMaxHp = 14_580_000_000;

    private static DpsReport Fight(int mobCode, int damagers, long remainHp = 1_000, long maxHp = 2_000, int entity = BossEntity)
    {
        var report = new DpsReport
        {
            Target = new MobInfo(entity, new Mob(mobCode, "보스", Boss: true), remainHp, maxHp),
        };
        for (int uid = 1; uid <= damagers; uid++)
        {
            report.Contributors.Add(new User(uid, "p" + uid, 2003));
            report.Information[uid] = new DpsInformation(1000 - uid, 1000 - uid, 1, 1);
        }

        return report;
    }

    private static T WithSettings<T>(int maxVisibleRows, Func<MeterSettings, T> body)
    {
        string temp = Path.Combine(Path.GetTempPath(), "wm_crowd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var s = new MeterSettings(new PropertyHandler(temp)) { MaxVisibleRows = maxVisibleRows };
            return body(s);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    // ---- 판정 ----

    [Fact]
    public void Only_an_abyss_field_boss_counts_as_the_crowd_fight()
    {
        Assert.True(CrowdFightPolicy.IsAbyssFieldBoss(Fight(Kaira, 3)));
        Assert.True(CrowdFightPolicy.IsAbyssFieldBoss(Fight(Agro, 3)));
        Assert.False(CrowdFightPolicy.IsAbyssFieldBoss(Fight(Kutar, 40)));    // 다른 지역 필드보스
        Assert.False(CrowdFightPolicy.IsAbyssFieldBoss(Fight(Nihog, 40)));
        Assert.False(CrowdFightPolicy.IsAbyssFieldBoss(Fight(Nayatman, 40))); // 던전
        Assert.False(CrowdFightPolicy.IsAbyssFieldBoss(Fight(Rudra, 40)));    // 공대
        // 접두가 같은(2600xxx) 미등록 코드는 어비스 필드보스가 아니다 — 판정은 표, 접두 규칙이 아니다.
        Assert.False(CrowdFightPolicy.IsAbyssFieldBoss(Fight(2600078, 40)));

        DpsReport noTarget = Fight(Kaira, 30);
        noTarget.Target = null;
        Assert.False(CrowdFightPolicy.IsAbyssFieldBoss(noTarget));
    }

    // ---- 행 상한 ----

    [Fact]
    public void An_abyss_field_boss_caps_rows_at_the_chosen_count()
    {
        Assert.Equal(10, WithSettings(10, s => CrowdFightPolicy.RowCap(Fight(Kaira, 30), s))); // 종전 15
        Assert.Equal(6, WithSettings(6, s => CrowdFightPolicy.RowCap(Fight(Kaira, 30), s)));   // 종전 9
    }

    [Fact]
    public void Other_fights_keep_the_buffered_cap()
    {
        foreach (int code in new[] { Kutar, Nihog, Nayatman, Rudra })
        {
            Assert.Equal(15, WithSettings(10, s => CrowdFightPolicy.RowCap(Fight(code, 30), s)));
            Assert.Equal(9, WithSettings(6, s => CrowdFightPolicy.RowCap(Fight(code, 30), s)));
        }

        DpsReport noTarget = Fight(Kaira, 30);
        noTarget.Target = null;
        Assert.Equal(15, WithSettings(10, s => CrowdFightPolicy.RowCap(noTarget, s)));
    }

    [Fact]
    public void The_abyss_cap_still_keeps_self_and_party_members_beyond_ten()
    {
        DpsReport r = Fight(Kaira, 30);
        var party = new List<User> { new(29, "p29", 2003), new(30, "p30", 2003) };
        IReadOnlyList<OverlayRowBuilder.Row> rows = OverlayRowBuilder.Build(
            r, roster: [], liveSelfId: 28, useTotalDamage: true, showPreCombatRoster: false, out _,
            topN: WithSettings(10, s => CrowdFightPolicy.RowCap(r, s)), authoritativeParty: party);

        Assert.Equal(13, rows.Count); // 상위 10 + 본인(28) + 파티원 2
        Assert.Contains(rows, x => x.Uid == 28 && x.IsSelf);
        Assert.Contains(rows, x => x.Uid == 29);
        Assert.Contains(rows, x => x.Uid == 30);
        Assert.DoesNotContain(rows, x => x.Uid == 11); // 11위 낯선 사람은 잘린다
    }

    [Fact]
    public void A_ten_man_raid_without_a_confirmed_roster_keeps_all_its_rows()
    {
        // 로스터가 아직 안 온 10인 공대 + 위에 낀 낯선 사람·팬텀 3줄. 여유 상한(15)이라 10명 전원이 보여야 한다.
        DpsReport r = Fight(Rudra, 13);
        IReadOnlyList<OverlayRowBuilder.Row> rows = OverlayRowBuilder.Build(
            r, roster: [], liveSelfId: 13, useTotalDamage: true, showPreCombatRoster: false, out _,
            topN: WithSettings(10, s => CrowdFightPolicy.RowCap(r, s)));

        Assert.Equal(13, rows.Count);
    }

    // ---- 처치까지: 어비스 필드보스 = HP 기울기 ----

    private static long? Feed(KillEtaEstimator est, int mobCode, long hpStart, long perSecond, int ticks,
        long tickMs = 500, long t0 = 0, long maxHp = KairaMaxHp, int entity = BossEntity)
    {
        long? eta = null;
        for (int k = 0; k <= ticks; k++)
        {
            long hp = hpStart - perSecond * k * tickMs / 1000;
            eta = est.Estimate(Fight(mobCode, 5, hp, maxHp, entity), liveFeed: true, nowMs: t0 + k * tickMs);
        }

        return eta;
    }

    [Fact]
    public void Abyss_eta_follows_the_boss_hp_slope_not_the_party_dps()
    {
        // 10초 동안 10k/s 로 900k 까지 — 기울기 ETA 90초. 보이는 파티 DPS(약 5k)로 나누면 180초가 나온다.
        Assert.Equal(90_000, Feed(new KillEtaEstimator(), Kaira, 1_000_000, 10_000, 20));
    }

    [Fact]
    public void Abyss_eta_is_hidden_until_the_samples_spread_far_enough()
    {
        Assert.Null(Feed(new KillEtaEstimator(), Kaira, 1_000_000, 10_000, 15)); // 7.5초 < MinSpanMs
    }

    [Fact]
    public void Abyss_eta_is_hidden_with_too_few_samples()
    {
        // 9초에 걸친 표본 2개 — 퍼짐은 충분해도 두 점으로는 숫자를 내지 않는다.
        var est = new KillEtaEstimator();
        Assert.Null(est.Estimate(Fight(Kaira, 5, 1_000_000, KairaMaxHp), true, 0));
        Assert.Null(est.Estimate(Fight(Kaira, 5, 910_000, KairaMaxHp), true, 9_000));
    }

    [Fact]
    public void Abyss_eta_is_hidden_once_hp_has_been_static_for_five_seconds()
    {
        var est = new KillEtaEstimator();
        Feed(est, Kaira, 1_000_000, 10_000, 20); // 마지막 HP 900k @ 10초
        Assert.NotNull(est.Estimate(Fight(Kaira, 5, 900_000, KairaMaxHp), true, 10_000 + BossHpSlope.StaleMs));
        Assert.Null(est.Estimate(Fight(Kaira, 5, 900_000, KairaMaxHp), true, 10_001 + BossHpSlope.StaleMs)); // 시야 밖·무적
    }

    [Fact]
    public void An_hp_jump_up_restarts_the_slope()
    {
        var est = new KillEtaEstimator();
        Feed(est, Kaira, 1_000_000, 10_000, 20, maxHp: 2_000_000); // 0~10초, 900k 까지
        // 만피 리셋 — 직후에는 근거가 없다.
        Assert.Null(est.Estimate(Fight(Kaira, 5, 2_000_000, 2_000_000), true, 10_500));
        // 새 곡선 50k/s 로 9초: 리셋 뒤 표본만으로 (2.0M − 450k) ÷ 50k/s = 31초.
        long? eta = Feed(est, Kaira, 2_000_000, 50_000, 18, t0: 10_500, maxHp: 2_000_000);
        Assert.Equal(31_000, eta);
    }

    [Fact]
    public void A_target_change_restarts_the_slope()
    {
        var est = new KillEtaEstimator();
        Feed(est, Kaira, 1_000_000, 10_000, 20);
        Assert.Null(est.Estimate(Fight(Kaira, 5, 800_000, KairaMaxHp, entity: 99), true, 10_500)); // 다른 엔티티

        var byCode = new KillEtaEstimator();
        Feed(byCode, Kaira, 1_000_000, 10_000, 20);
        Assert.Null(byCode.Estimate(Fight(Agro, 5, 800_000, KairaMaxHp), true, 10_500)); // 같은 id 에 다른 보스
    }

    [Fact]
    public void Abyss_eta_bridges_a_sight_gap_with_the_anchor_sample()
    {
        // 창(20초)보다 긴 22초 시야 공백 뒤에는 창 안 표본이 공백 뒤 것뿐이다. 창 앞 닻 표본(10초)이 남아 있어야
        // 공백 뒤 두 번째 HP 에서 바로 숫자가 나온다 — 닻이 없으면 8초를 다시 기다리며 숫자가 꺼진다.
        var est = new KillEtaEstimator();
        Feed(est, Kaira, 5_000_000, 100_000, 20); // 0~10초 연소, 4.0M 까지
        Assert.Null(est.Estimate(Fight(Kaira, 5, 1_800_000, KairaMaxHp), true, 32_000)); // 공백 뒤 첫 HP — 표본 2개
        long? eta = est.Estimate(Fight(Kaira, 5, 1_750_000, KairaMaxHp), true, 32_500);
        Assert.NotNull(eta);
        Assert.InRange(eta!.Value, 16_000, 19_000); // 1.75M / 100k/s = 17.5초
    }

    [Fact]
    public void A_saved_battle_replay_hides_the_abyss_eta_and_leaves_the_live_samples_alone()
    {
        var est = new KillEtaEstimator();
        Feed(est, Kaira, 1_000_000, 10_000, 20); // 라이브 0~10초

        // 기록에서 연 저장 전투(다른 엔티티의 카이라, 던전 보스) — 어비스면 숨기고, 던전이면 종전 공식.
        Assert.Null(est.Estimate(Fight(Kaira, 5, 300_000, KairaMaxHp, entity: 777), liveFeed: false, nowMs: 10_200));
        DpsReport savedDungeon = Fight(Nayatman, 3, remainHp: 900_000, maxHp: 2_000_000);
        Assert.Equal(KillEtaEstimator.PartyDpsEta(savedDungeon), est.Estimate(savedDungeon, false, 10_300));

        // 라이브로 돌아오면 끊기지 않고 이어서 잰다(재생이 표본을 비웠다면 여기서 null).
        Assert.Equal(89_500, est.Estimate(Fight(Kaira, 5, 895_000, KairaMaxHp), true, 10_500));
    }

    // ---- 처치까지: 그 밖의 전투 = 종전 공식 ----

    [Fact]
    public void Other_fights_keep_the_party_dps_eta()
    {
        foreach (int code in new[] { Kutar, Nihog, Nayatman, Rudra })
        {
            var est = new KillEtaEstimator();
            DpsReport r = Fight(code, 3, remainHp: 900_000, maxHp: 2_000_000); // 파티 DPS 999+998+997 = 2994
            Assert.Equal((long)(900_000 / 2994.0 * 1000.0), est.Estimate(r, liveFeed: true, nowMs: 0));
            Assert.Equal((long)(900_000 / 2994.0 * 1000.0), est.Estimate(r, liveFeed: false, nowMs: 0));

            DpsReport idle = Fight(code, 0, remainHp: 900_000, maxHp: 2_000_000); // 파티 DPS 0 → 숨김
            Assert.Null(est.Estimate(idle, true, 0));
        }
    }
}
