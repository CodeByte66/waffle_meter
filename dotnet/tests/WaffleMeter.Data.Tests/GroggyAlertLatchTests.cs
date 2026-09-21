using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 보스 무력화(그로기) 게이지가 <b>잔여 20%</b>로 내려오면 한 번 알린다. 게이지는 max 에서 0 으로 깎이고,
/// 바닥에서 그로기가 터진 뒤 만충으로 리필된다 — 리필이 곧 다음 사이클이므로 거기서 래치가 풀린다.
///
/// <para><b>왜 20%인가.</b> 원정/초월/성역 보스 실측(게이지형 하강 사이클 281건)에서 5음절 문구 기준 성공률이
/// 25% 86.5% / <b>20% 86.1%</b> / 15% 85.8% / 12% 76.5% / 10% 59.8% / 5% 6% 다. 절벽은 12%부터이고, 15%와
/// 20%는 성공률이 사실상 같지만 말이 끝난 뒤 남는 시간이 3.4초 vs 4.7초로 갈린다. 천장이 86%인 이유는
/// 임계값이 아니라 게이지가 3초에 빠지거나 중간에서 얼었다가 터지는 보스 세 마리 때문이라 더 올려도 안 는다.</para>
///
/// <para>🔴 <b>max 를 캐시하면 안 된다.</b> 같은 보스도 세션마다 티어가 다르고(실측 2250/3000/6000/7500)
/// 시련 '보스 강화' 어픽스로 전투 도중에 바뀐다. 바뀌는 프레임은 새 max 와 옛 cur 이 같이 실려 비율이
/// 1을 넘을 수 있는데, 그걸 리필로 읽으면 래치가 풀려 같은 사이클에 두 번 울린다.</para>
/// </summary>
public sealed class GroggyAlertLatchTests
{
    private const int Boss = 100;
    private const int OtherBoss = 200;
    private const int BossCode = 2311402;
    private const int OtherCode = 2311403;
    private const long Max = 3000;

    private static (DataManager Dm, List<int> Alerts) Fighting(int mobId = Boss)
    {
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.LoadMobs(new Dictionary<int, Mob>
        {
            [BossCode] = new Mob(BossCode, "나사라크", Boss: true),
            [OtherCode] = new Mob(OtherCode, "나트하라", Boss: true),
        });
        dm.SaveMobId(Boss, BossCode);
        dm.SaveMobId(OtherBoss, OtherCode);
        dm.StartBattle(mobId);

        var alerts = new List<int>();
        dm.GroggyImminent += () => alerts.Add(alerts.Count);
        return (dm, alerts);
    }

    /// <summary>비율(%)을 그 티어의 cur 로 바꿔 넣는다.</summary>
    private static void Gauge(DataManager dm, double ratio, int entity = Boss, long max = Max) =>
        dm.SaveGroggyGauge(entity, max, (long)Math.Round(max * ratio));

    [Fact]
    public void The_alert_fires_once_when_the_gauge_reaches_twenty_percent()
    {
        (DataManager dm, List<int> alerts) = Fighting();

        Gauge(dm, 1.00);
        Gauge(dm, 0.50);
        Gauge(dm, 0.25);
        Assert.Empty(alerts);

        Gauge(dm, 0.20);
        Assert.Single(alerts);
    }

    [Fact]
    public void It_does_not_repeat_as_the_gauge_keeps_falling()
    {
        (DataManager dm, List<int> alerts) = Fighting();

        Gauge(dm, 1.00);
        Gauge(dm, 0.18);
        Gauge(dm, 0.10);
        Gauge(dm, 0.03);
        Gauge(dm, 0.00);

        Assert.Single(alerts);
    }

    [Fact]
    public void A_refill_re_arms_it_for_the_next_cycle()
    {
        (DataManager dm, List<int> alerts) = Fighting();

        Gauge(dm, 1.00);
        Gauge(dm, 0.05);
        Assert.Single(alerts);

        Gauge(dm, 1.00);   // 그로기가 터지고 만충 복귀 = 다음 사이클
        Gauge(dm, 0.15);
        Assert.Equal(2, alerts.Count);
    }

    [Fact]
    public void A_gauge_tier_change_mid_fight_does_not_read_as_a_refill()
    {
        // 🔴 새 max 와 옛 cur 이 같이 실리는 전환 프레임. 비율만 보면 1.0 을 넘어 '리필'로 보이지만
        // 그 틱에 래치를 풀면 같은 하강에서 두 번 울린다.
        (DataManager dm, List<int> alerts) = Fighting();

        Gauge(dm, 1.00);
        dm.SaveGroggyGauge(Boss, Max, 450);        // 15% — 알림
        Assert.Single(alerts);

        dm.SaveGroggyGauge(Boss, 2250, 3000);      // max 티어 변경 + cur > max
        dm.SaveGroggyGauge(Boss, 2250, 300);       // 여전히 같은 하강(13%)
        Assert.Single(alerts);
    }

    [Fact]
    public void The_ratio_is_clamped_so_a_transition_frame_cannot_exceed_one()
    {
        (DataManager dm, _) = Fighting();

        dm.SaveGroggyGauge(Boss, 2250, 9999);

        Assert.Equal(1.0, dm.GroggyRatio());
    }

    [Fact]
    public void Only_the_boss_being_fought_is_watched()
    {
        // 한 세션에서 그로기를 방송하는 엔티티가 수십 개이고 잡몹·수정체가 섞인다. 현재 타깃만 본다.
        (DataManager dm, List<int> alerts) = Fighting();

        Gauge(dm, 1.00);
        Gauge(dm, 0.05, entity: OtherBoss);

        Assert.Empty(alerts);
        Assert.Equal(1.0, dm.GroggyRatio());
    }

    [Fact]
    public void Ending_the_battle_drops_the_latch_so_the_next_fight_starts_clean()
    {
        (DataManager dm, List<int> alerts) = Fighting();

        Gauge(dm, 1.00);
        Gauge(dm, 0.05);
        Assert.Single(alerts);

        dm.EndBattle(Boss);
        Assert.Equal(1.0, dm.GroggyRatio());

        dm.StartBattle(OtherBoss);
        Gauge(dm, 0.15, entity: OtherBoss);   // 만충을 못 본 채 시작해도 첫 알림은 나가야 한다
        Assert.Equal(2, alerts.Count);
    }

    [Fact]
    public void A_nonsense_frame_is_ignored()
    {
        (DataManager dm, List<int> alerts) = Fighting();

        dm.SaveGroggyGauge(Boss, 0, 0);
        dm.SaveGroggyGauge(0, Max, 100);

        Assert.Empty(alerts);
        Assert.Equal(1.0, dm.GroggyRatio());
    }
}
