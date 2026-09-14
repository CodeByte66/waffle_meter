using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 전멸(wipe)로 끝난 전투의 기록에 보스 체력이 만피로 박히던 결함.
///
/// <para><b>무엇이 일어났나.</b> 파티가 전멸하면 보스가 제자리로 돌아가며 만피를 한 번 방송한다. 실측
/// (2026-08-31, 바실루스 entity 23280)에서 그 0x8D00 프레임은 <b>종료 토글보다 1.495초 먼저</b> 왔다 —
/// 25.4%(90,199,903)까지 깎아 놓고 진 전투가 기록에 354,439,800/354,439,800 = <b>100%</b>로 남았다.
/// <c>replay-diag</c> 전수에서 죽지 않고 끝난 비포화 전투 601건 중 194건(32%)이 이 모양이었고, 리플레이의
/// 시전 시점 HP와 대조하면 실제로는 17~22% 였다.</para>
///
/// <para><b>왜 "종료 순간에 얼린다"로는 못 막나.</b> 리드 타임이 1.5초라 500ms 리포트 틱이 두세 번 이미
/// 라이브 리포트에 만피를 써 넣은 뒤에 종료 토글이 온다. 그래서 기록값은 전투 <b>도중에</b> 확정돼야 한다 —
/// 그게 <see cref="DataManager.BattleLowMobHp"/>(전투 중 관측된 최저 잔여 HP)다.</para>
/// </summary>
public sealed class WipeBossHpFreezeTests
{
    private const int Instance = 100;
    private const int BossCode = 2301723; // 바실루스
    private const int Dealer = 5001;
    private const long FullHp = 354_439_800L;

    private static (DataManager Dm, DpsCalculator Calc, long[] Clock) Boss()
    {
        long[] now = { 1_000_000 };
        var dm = new DataManager { Clock = () => now[0] };
        dm.LoadMobs(new Dictionary<int, Mob> { [BossCode] = new Mob(BossCode, "바실루스", Boss: true) });
        dm.SaveMobId(Instance, BossCode);
        dm.SaveNickname(Dealer, "딜러", isExecutor: true, server: 2003, jobByte: 32);
        return (dm, new DpsCalculator(dm), now);
    }

    private static void Hit(DataManager dm, long timestamp, int damage) =>
        dm.SaveDamage(
            new ParsedDamagePacket { ActorId = Dealer, TargetId = Instance, Damage = damage, Timestamp = timestamp },
            dm.CurrentEpoch());

    [Fact]
    public void A_wipe_records_the_hp_we_pushed_the_boss_to_not_the_reset_broadcast()
    {
        (DataManager dm, DpsCalculator calc, long[] clock) = Boss();

        dm.SaveMobMaxHp(Instance, FullHp);
        dm.MobHp(Instance, FullHp);
        dm.StartBattle(Instance);
        Hit(dm, clock[0] + 1_000, 1_000_000);
        clock[0] += 2_000;
        dm.MobHp(Instance, 90_199_903L); // 25.4% 까지 깎았다
        calc.GetDps();                   // 라이브 틱

        // 전멸. 보스가 제자리로 돌아가며 만피를 방송하고, 종료 토글은 1.5초 뒤에야 온다.
        clock[0] += 1_000;
        dm.MobHp(Instance, FullHp);
        calc.GetDps();                   // 그 사이의 라이브 틱 — 종전에는 여기서 리포트가 이미 오염됐다
        clock[0] += 1_495;
        dm.EndBattle(Instance);

        DpsReport ended = calc.GetDps();

        Assert.NotNull(ended.Target);
        Assert.Equal(90_199_903L, ended.Target!.RemainHp);
        Assert.Equal(FullHp, ended.Target.MaxHp);
        Assert.True(ended.BattleFinished);
    }

    [Fact]
    public void Quitting_right_after_a_wipe_still_records_the_low_water_mark()
    {
        // 저장 경로는 셋인데(타깃 전환·전투 종료·종료 드레인) 전이 지점에만 얼리면 세 번째가 샌다 —
        // 유저가 전멸 직후 미터를 닫아 소비자 루프가 종료 전이를 돌기 전에 빠져나가는 경로다. 하필 이
        // 기능이 없애려던 바로 그 장면이라, 얼리는 자리를 SaveRecentBattleLog 한 곳으로 모았다.
        (DataManager dm, DpsCalculator calc, long[] clock) = Boss();
        var logged = new List<DpsLog>();
        calc.OnBattleLogged = logged.Add;

        dm.SaveMobMaxHp(Instance, FullHp);
        dm.MobHp(Instance, FullHp);
        dm.StartBattle(Instance);
        Hit(dm, clock[0] + 1_000, 1_000_000);
        clock[0] += 2_000;
        dm.MobHp(Instance, 90_199_903L);
        calc.GetDps();

        clock[0] += 1_000;
        dm.MobHp(Instance, FullHp); // 전멸 리셋 방송
        calc.GetDps();
        clock[0] += 1_000;

        calc.ResetDataStorage(); // 종료 토글이 오기도 전에 미터를 닫았다

        DpsLog log = Assert.Single(logged);
        Assert.Equal(90_199_903L, log.Report.Target!.RemainHp);
    }

    [Fact]
    public void A_repull_that_opens_before_the_previous_attempt_is_saved_keeps_its_own_low_water_mark()
    {
        // 최저치를 '전투를 열 때' 지우면, 직전 판의 저장(리포트 틱)보다 새 판의 개시가 빠른 순간 그 저장이
        // 새 판의 만피를 읽어 간다. 그래서 최저치는 전투 리비전으로 무효화한다.
        (DataManager dm, DpsCalculator calc, long[] clock) = Boss();
        var logged = new List<DpsLog>();
        calc.OnBattleLogged = logged.Add;

        dm.SaveMobMaxHp(Instance, FullHp);
        dm.MobHp(Instance, FullHp);
        dm.StartBattle(Instance);
        Hit(dm, clock[0] + 1_000, 1_000_000);
        clock[0] += 2_000;
        dm.MobHp(Instance, 90_199_903L);
        calc.GetDps();

        // 전멸 → 종료 → 리포트 틱이 돌기 전에 같은 보스로 재교전이 열린다.
        clock[0] += 1_000;
        dm.EndBattle(Instance);
        dm.MobHp(Instance, FullHp);
        dm.StartBattle(Instance);

        calc.GetDps(); // 이 틱이 직전 판을 저장한다

        DpsLog log = Assert.Single(logged);
        Assert.Equal(90_199_903L, log.Report.Target!.RemainHp);
    }

    [Fact]
    public void A_kill_still_records_zero()
    {
        (DataManager dm, DpsCalculator calc, long[] clock) = Boss();

        dm.SaveMobMaxHp(Instance, FullHp);
        dm.MobHp(Instance, FullHp);
        dm.StartBattle(Instance);
        Hit(dm, clock[0] + 1_000, 1_000_000);
        clock[0] += 2_000;
        calc.GetDps();          // 전투 중 라이브 틱(실제 미터는 300~500ms마다 돈다)
        dm.MobHp(Instance, 0);
        dm.EndBattle(Instance);

        DpsReport ended = calc.GetDps();

        Assert.Equal(0L, ended.Target!.RemainHp);
    }

    [Fact]
    public void A_repull_does_not_inherit_the_previous_attempt_low_water_mark()
    {
        (DataManager dm, DpsCalculator calc, long[] clock) = Boss();

        dm.SaveMobMaxHp(Instance, FullHp);
        dm.MobHp(Instance, FullHp);
        dm.StartBattle(Instance);
        Hit(dm, clock[0] + 1_000, 1_000_000);
        clock[0] += 2_000;
        calc.GetDps();
        dm.MobHp(Instance, 10_000_000L); // 1차 시도는 거의 잡을 뻔했다
        dm.EndBattle(Instance);
        calc.GetDps();

        // 재도전: 보스가 만피로 부활하고, 이번엔 초반에 전멸한다.
        clock[0] += 100_000;
        dm.MobHp(Instance, FullHp);
        dm.StartBattle(Instance);
        Hit(dm, clock[0] + 1_000, 1_000_000);
        clock[0] += 2_000;
        calc.GetDps();
        dm.MobHp(Instance, 300_000_000L);
        dm.EndBattle(Instance);

        DpsReport ended = calc.GetDps();

        Assert.Equal(300_000_000L, ended.Target!.RemainHp);
    }

    [Fact]
    public void Hp_above_int_max_survives_the_record_path()
    {
        // 델트라스 계열(27억대)이 int.MaxValue 에 붙던 갈래. 파서의 포화는 걷어냈고, 데이터·기록 계층이
        // long 이어야 그 값이 끝까지 살아남는다.
        (DataManager dm, DpsCalculator calc, long[] clock) = Boss();
        const long designMax = 2_720_000_000L;

        dm.SaveMobMaxHp(Instance, designMax);
        dm.MobHp(Instance, designMax);
        dm.StartBattle(Instance);
        Hit(dm, clock[0] + 1_000, 1_000_000);
        clock[0] += 2_000;
        calc.GetDps();
        dm.MobHp(Instance, 2_500_000_000L);
        dm.EndBattle(Instance);

        DpsReport ended = calc.GetDps();

        Assert.Equal(2_500_000_000L, ended.Target!.RemainHp);
        Assert.Equal(designMax, ended.Target.MaxHp);
    }
}
