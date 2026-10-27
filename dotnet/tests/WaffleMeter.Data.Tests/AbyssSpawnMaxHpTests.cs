using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 어비스 필드보스의 최대 HP 는 스폰(0x3641)이 정본이다.
/// <para>2026-10-27 21:00 감시자 카이라: 0x8D00 은 statId 7(최대 HP)을 한 번도 싣지 않았고, 10e9 상한에 걸려 첫 33.5초
/// 프레임이 버려진 뒤 처음 받아들인 현재 HP 99.98억이 '관측 최대'로 래치됐다(단조 증가라 영영 안 고쳐진다). 진짜 최대
/// 145.8억 대비 % 가 1.458배 부풀었다(실제 30.2% → 화면 44.1%).</para>
/// </summary>
public sealed class AbyssSpawnMaxHpTests
{
    private const int Kaira = 16497;
    private const int KairaCode = 2600089;

    private static DataManager Dm()
    {
        var dm = new DataManager { Clock = () => 1_000_000 };
        dm.LoadMobs(new Dictionary<int, Mob>
        {
            [KairaCode] = new Mob(KairaCode, "감시자 카이라", Boss: true),
            [2600068] = new Mob(2600068, "정령왕 아그로", Boss: false), // 표의 어비스 필드보스지만 비보스로 둔 변형
            [2311402] = new Mob(2311402, "나사라크", Boss: true),       // 던전 보스
        });
        return dm;
    }

    [Fact]
    public void A_spawn_max_replaces_a_latched_current_hp_and_then_bounds_the_current_hp()
    {
        DataManager dm = Dm();
        dm.SaveMobId(Kaira, KairaCode);
        dm.SaveMobHp(Kaira, 9_998_359_555);              // 실측: 처음 받아들인 0x8D00 이 최대로 래치됐다
        Assert.Equal(9_998_359_555, dm.MobMaxHp(Kaira));

        dm.SaveMobSpawnVitals(Kaira, live: 1, hp: 14_580_000_000, maxHp: 14_580_000_000, inCombat: false);
        Assert.Equal(14_580_000_000, dm.MobMaxHp(Kaira));

        dm.SaveMobHp(Kaira, 15_000_000_000);             // 권위 최대보다 큰 현재 HP — 그 프레임은 버린다
        Assert.Equal(14_580_000_000, dm.MobHp(Kaira));
        Assert.Equal(14_580_000_000, dm.MobMaxHp(Kaira));

        dm.SaveMobHp(Kaira, 4_405_013_627);              // 정상 프레임은 그대로, 최대는 안 움직인다
        Assert.Equal(4_405_013_627, dm.MobHp(Kaira));
        Assert.Equal(14_580_000_000, dm.MobMaxHp(Kaira));
    }

    [Fact]
    public void Without_a_spawn_max_the_current_hp_latch_is_unchanged()
    {
        DataManager dm = Dm();
        dm.SaveMobId(Kaira, KairaCode);
        dm.SaveMobHp(Kaira, 9_000_000_000);
        dm.SaveMobHp(Kaira, 9_500_000_000);              // 권위값이 없으면 종전대로 관측 최대를 래치
        Assert.Equal(9_500_000_000, dm.MobMaxHp(Kaira));
    }

    [Fact]
    public void A_non_boss_or_non_abyss_spawn_stores_no_hp()
    {
        DataManager dm = Dm();
        dm.SaveMobId(500, 2600068);
        dm.SaveMobSpawnVitals(500, 1, 5_000_000_000, 5_000_000_000, false);
        Assert.Null(dm.MobHp(500));
        Assert.Null(dm.MobMaxHp(500));

        dm.SaveMobId(600, 2311402);
        dm.SaveMobSpawnVitals(600, 1, 50_000_000, 50_000_000, false);
        Assert.Null(dm.MobHp(600));
        Assert.Null(dm.MobMaxHp(600));
    }

    [Fact]
    public void The_spawn_max_survives_a_same_code_re_registration_but_not_an_id_recycle()
    {
        DataManager dm = Dm();
        dm.SaveMobId(Kaira, KairaCode);
        dm.SaveMobSpawnVitals(Kaira, 1, 14_580_000_000, 14_580_000_000, false);

        dm.SaveMobId(Kaira, KairaCode);                  // 재진입 스폰이 같은 코드를 다시 등록
        dm.SaveMobHp(Kaira, 15_000_000_000);
        Assert.Equal(14_580_000_000, dm.MobMaxHp(Kaira));

        dm.SaveMobId(Kaira, 2311402);                    // 엔티티 id 재발급 — 권위값도 함께 버린다
        dm.SaveMobHp(Kaira, 20_000_000);
        Assert.Equal(20_000_000, dm.MobMaxHp(Kaira));
    }
}
