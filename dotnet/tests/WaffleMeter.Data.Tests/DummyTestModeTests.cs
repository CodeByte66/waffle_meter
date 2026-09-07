using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 허수아비 (training-dummy) test mode. A dummy hit is metered as a live battle ONLY while the mode is on; the
/// run is hard-cut at the chosen duration (later hits ignored, result frozen at exactly that window); after a
/// quiet gap the cut re-arms by itself so the next hit opens a SEPARATE run; each run lands in saved battle
/// history under its own row; and the dummy DPS reset clears just the live report while re-arming the cut.
/// Also locks in that <see cref="ReferenceJson.LoadMobs"/> reads the "isDummy" flag — without which the whole
/// gate is dead (every runtime Mob.IsDummy would be false).
/// </summary>
public sealed class DummyTestModeTests
{
    private const int DummyInstance = 200;
    private const int DummyCode = 2300229; // a shipped 훈련용 허수아비 code
    private const int BossInstance = 100;
    private const int BossCode = 2301008;
    private const int Dealer = 5001;

    private static (DataManager Dm, DpsCalculator Calc, long[] Clock) Setup()
    {
        long[] now = { 1_000_000 };
        var dm = new DataManager { Clock = () => now[0] };
        dm.LoadMobs(new Dictionary<int, Mob>
        {
            [DummyCode] = new Mob(DummyCode, "훈련용 허수아비", Boss: false, IsDummy: true),
            [BossCode] = new Mob(BossCode, "보스", Boss: true),
        });
        dm.SaveMobId(DummyInstance, DummyCode);
        dm.SaveMobId(BossInstance, BossCode);
        dm.SaveNickname(Dealer, "딜러", isExecutor: true, server: 2003, jobByte: 32);
        return (dm, new DpsCalculator(dm), now);
    }

    private static void HitDummy(DataManager dm, long timestamp, int damage) =>
        dm.SaveDamage(
            new ParsedDamagePacket { ActorId = Dealer, TargetId = DummyInstance, Damage = damage, Timestamp = timestamp },
            dm.CurrentEpoch());

    [Fact]
    public void Mode_off_a_dummy_hit_registers_no_combat()
    {
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.DummyTestMode = false;

        HitDummy(dm, clock[0] + 100, 5000);
        DpsReport report = calc.GetDps();

        Assert.True(dm.CurrentTarget() <= 0); // no live target (never started)
        Assert.Empty(report.Information);      // no DPS rows
    }

    [Fact]
    public void Mode_on_a_dummy_hit_is_metered_as_a_live_battle()
    {
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.DummyTestMode = true;

        HitDummy(dm, clock[0] + 100, 5000);
        HitDummy(dm, clock[0] + 1_100, 5000);
        DpsReport report = calc.GetDps();

        Assert.Equal(DummyInstance, dm.CurrentTarget());
        Assert.Equal(10_000.0, report.Information[Dealer].Amount, 3);
    }

    [Fact]
    public void Duration_hard_cut_on_hit_stops_counting_and_ignores_later_hits()
    {
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.DummyTestMode = true;
        dm.DummyDurationSec = 30;

        HitDummy(dm, clock[0], 4000); // opens the window at t=start
        calc.GetDps();
        clock[0] += 31_000;           // past the 30s cut
        HitDummy(dm, clock[0], 999_999); // this hit is past the cut → dropped, and it ends the run
        DpsReport afterCut = calc.GetDps();

        Assert.Equal(-1, dm.CurrentTarget());                        // battle ended by the cut
        Assert.Equal(4000.0, afterCut.Information[Dealer].Amount, 3); // the post-cut hit was NOT counted

        HitDummy(dm, clock[0] + 1_000, 888_888); // still ignored until a reset
        DpsReport still = calc.GetDps();
        Assert.Equal(-1, dm.CurrentTarget());
        Assert.Equal(4000.0, still.Information[Dealer].Amount, 3);
    }

    [Fact]
    public void Duration_hard_cut_fires_from_the_periodic_tick_without_a_hit()
    {
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.DummyTestMode = true;
        dm.DummyDurationSec = 30;

        HitDummy(dm, clock[0], 4000);
        calc.GetDps();
        Assert.Equal(DummyInstance, dm.CurrentTarget());

        clock[0] += 31_000; // no further hits — the tick must still cut
        calc.GetDps();
        Assert.Equal(-1, dm.CurrentTarget());
    }

    [Fact]
    public void A_dummy_run_is_saved_to_battle_history()
    {
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.DummyTestMode = true;
        dm.DummyDurationSec = 30;

        HitDummy(dm, clock[0], 4000);
        calc.GetDps();
        clock[0] += 31_000;
        calc.GetDps(); // cut → end transition

        DpsLog? saved = dm.BattleLog(0);
        Assert.NotNull(saved);
        Assert.True(saved!.Report.Target?.Mob.IsDummy); // 기록 패널의 '허수아비' 탭이 이 플래그로 갈린다
    }

    [Fact]
    public void The_frozen_dummy_run_lasts_exactly_the_configured_window()
    {
        // 첫 타격부터 설정 시간까지가 곧 측정 창이다. 예전에는 얼려 둔 리포트가 "첫 타격 → 마지막 타격"으로
        // 다시 잡혀(RefreshRecentReportFromCache) 30초 런이 29.x초로 굳었고, DPS 분모도 그만큼 짧아져
        // 같은 조건으로 두 번 돌린 결과를 비교할 수 없었다.
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.DummyTestMode = true;
        dm.DummyDurationSec = 30;

        HitDummy(dm, clock[0], 4000);
        calc.GetDps();
        HitDummy(dm, clock[0] + 20_000, 4000); // 마지막 타격은 창이 끝나기 10초 전
        clock[0] += 31_000;
        calc.GetDps(); // 틱이 컷을 발화

        DpsLog saved = Assert.IsType<DpsLog>(dm.BattleLog(0));
        Assert.Equal(30_000, saved.Report.BattleEnd - saved.Report.BattleStart);
    }

    [Fact]
    public void The_cut_re_arms_after_a_quiet_gap_so_the_next_hit_is_a_new_run()
    {
        // 요구: 설정 시간이 지나면 전투 종료 처리하고, 다음 타격은 <b>별도의</b> 허수아비 전투로 잡는다.
        // 정숙 구간이 필요한 이유는 만료 순간에도 대개 계속 때리고 있기 때문이다 — 0초면 방금 결과를 읽을 틈이 없다.
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.DummyTestMode = true;
        dm.DummyDurationSec = 30;

        HitDummy(dm, clock[0], 4000);
        calc.GetDps();
        clock[0] += 31_000;
        HitDummy(dm, clock[0], 999_999); // past the cut → dropped, ends the run
        calc.GetDps();
        Assert.Equal(-1, dm.CurrentTarget());

        // 아직 조용하지 않다 — 계속 때리는 손은 새 전투를 열지 못한다.
        clock[0] += 2_000;
        HitDummy(dm, clock[0], 888_888);
        calc.GetDps();
        Assert.Equal(-1, dm.CurrentTarget());

        // 5초 넘게 쉬면 스스로 재무장한다 (초기화 버튼을 누르지 않았다).
        clock[0] += 5_001;
        calc.GetDps();                       // 이 틱에서 래치가 풀린다
        HitDummy(dm, clock[0] + 10, 7_000);
        DpsReport second = calc.GetDps();

        Assert.Equal(DummyInstance, dm.CurrentTarget());
        Assert.Equal(7_000.0, second.Information[Dealer].Amount, 3); // 이전 런의 피해가 섞이지 않았다
    }

    [Fact]
    public void Two_back_to_back_dummy_runs_produce_two_history_rows()
    {
        // 허수아비는 인스턴스 id 도 몹 코드도 고정이라 기본 병합 규칙(같은 대상 + 120초 이내)에 전부 걸린다.
        // 그 예외가 없으면 연속 측정 3회가 기록 한 줄로 뭉개진다.
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.DummyTestMode = true;
        dm.DummyDurationSec = 30;

        for (int run = 0; run < 2; run++)
        {
            HitDummy(dm, clock[0], 4000 + run);
            calc.GetDps();
            clock[0] += 31_000;
            calc.GetDps();      // cut
            clock[0] += 6_000;  // 정숙 구간 → 재무장
            calc.GetDps();
        }

        Assert.NotNull(dm.BattleLog(0));
        Assert.NotNull(dm.BattleLog(1)); // 두 줄 — 병합되지 않았다
    }

    [Fact]
    public void A_dummy_run_does_not_prune_the_buff_repository()
    {
        // 허수아비 런이 버프 저장소를 자르면, 연습 뒤에 이어지는 <b>진짜</b> 전투의 가동률·nDPS/rDPS 가
        // 미리 걸어 둔 장기 버프를 못 본다. 연습 30번이면 30번 잘린다.
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.SaveUseBuff(Dealer, 110200500, clock[0] - 10_000, clock[0] + 600_000, 610_000, Dealer);
        dm.DummyTestMode = true;
        dm.DummyDurationSec = 30;

        HitDummy(dm, clock[0], 4000);
        calc.GetDps();
        clock[0] += 31_000;
        calc.GetDps(); // cut → save

        Assert.NotEmpty(dm.BattleBuff(Dealer, clock[0], clock[0] + 1_000));
    }

    [Fact]
    public void A_boss_battle_after_a_dummy_run_does_not_inherit_the_dummy_window()
    {
        // 고정 종료가 그 창을 넘어 살아남으면 다음 보스 전투의 종료 스탬프로 새어 나간다(191M 오염 계열).
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.DummyTestMode = true;
        dm.DummyDurationSec = 30;

        HitDummy(dm, clock[0], 4000);
        calc.GetDps();
        clock[0] += 31_000;
        calc.GetDps(); // cut → 종료 전이. FlushPacket 이 같은 틱에서 고정 종료를 이미 내려놓는다.
        Assert.Equal(0, dm.DummyFixedBattleEnd);

        clock[0] += 10_000;
        dm.MobHp(BossInstance, 5000);
        dm.StartBattle(BossInstance);
        Assert.Equal(0, dm.DummyFixedBattleEnd);

        dm.SaveDamage(
            new ParsedDamagePacket { ActorId = Dealer, TargetId = BossInstance, Damage = 3000, Timestamp = clock[0] + 500 },
            dm.CurrentEpoch());
        calc.GetDps();
        clock[0] += 2_000;
        dm.EndBattle(BossInstance);
        calc.GetDps();

        DpsLog boss = Assert.IsType<DpsLog>(dm.BattleLog(1)); // [0] = the dummy run
        Assert.False(boss.Report.Target?.Mob.IsDummy);
        Assert.NotEqual(30_000, boss.Report.BattleEnd - boss.Report.BattleStart);
    }

    [Fact]
    public void A_boss_battle_is_still_saved_to_history()
    {
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.MobHp(BossInstance, 5000);
        dm.StartBattle(BossInstance);
        dm.SaveDamage(
            new ParsedDamagePacket { ActorId = Dealer, TargetId = BossInstance, Damage = 3000, Timestamp = clock[0] + 500 },
            dm.CurrentEpoch());
        calc.GetDps();
        clock[0] += 2_000;
        dm.EndBattle(BossInstance);
        calc.GetDps(); // end transition → saved

        Assert.NotNull(dm.BattleLog(0)); // the dummy save-skip must not affect bosses
    }

    [Fact]
    public void Reset_clears_the_live_dummy_report_and_re_arms_the_cut()
    {
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.DummyTestMode = true;
        dm.DummyDurationSec = 30;

        HitDummy(dm, clock[0], 4000);
        calc.GetDps();
        clock[0] += 31_000;
        calc.GetDps(); // cut (cutoff latched, report frozen at 4000)

        calc.ResetDummyBattle(); // 허수아비 DPS 초기화
        Assert.Empty(calc.GetDps().Information); // live report cleared

        HitDummy(dm, clock[0] + 100, 7000); // a fresh hit opens a new window (the cut was re-armed)
        DpsReport retest = calc.GetDps();
        Assert.Equal(DummyInstance, dm.CurrentTarget());
        Assert.Equal(7000.0, retest.Information[Dealer].Amount, 3);
    }

    [Fact]
    public void Mode_off_mid_run_ends_the_dummy_battle_on_the_next_tick()
    {
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.DummyTestMode = true;
        HitDummy(dm, clock[0], 4000);
        calc.GetDps();
        Assert.Equal(DummyInstance, dm.CurrentTarget());

        dm.DummyTestMode = false;
        calc.GetDps(); // TickDummyBattle ends the run
        Assert.Equal(-1, dm.CurrentTarget());
    }

    [Fact]
    public void Shortening_the_measurement_time_mid_run_never_inflates_the_result()
    {
        // 컷 시각은 창을 열 때 잠근 길이로 계산한다. 살아 있는 설정을 매번 다시 읽으면, 런 도중 측정 시간을
        // 줄였을 때 컷이 <b>이미 지나간</b> 시각을 종료로 찍는다 — 분자(누적 피해)는 그대로인데 분모만 짧아져
        // DPS 가 통째로 부풀고, 그 값이 허수아비 기록에 그대로 남는다.
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.DummyTestMode = true;
        dm.DummyDurationSec = 300;
        long start = clock[0];

        // 소비자 시계도 패킷과 함께 흘려야 한다 — 안 그러면 5초 유휴 자동 종료가 먼저 걸려 이 스펙이
        // 검증하려는 상황(런이 아직 살아 있는데 설정만 줄어든 상태)에 도달하지 못한다.
        for (int i = 0; i < 60; i++)
        {
            clock[0] = start + (i * 1_000L);
            HitDummy(dm, clock[0], 1_000);
            calc.GetDps();
        }

        dm.DummyDurationSec = 30; // 런 도중 '30초'로 변경
        clock[0] += 500;
        DpsReport after = calc.GetDps();

        // 이 런은 300초 창으로 열렸다 — 60초 시점에는 아직 컷이 아니다.
        Assert.Equal(DummyInstance, dm.CurrentTarget());
        Assert.Equal(60_000.0, after.Information[Dealer].Amount, 3);
        long duration = after.BattleEnd - after.BattleStart;
        Assert.True(duration >= 59_000, $"창이 과거로 잘렸다: {duration}ms");
    }

    [Fact]
    public void A_dummy_classified_hit_during_a_live_boss_battle_never_touches_that_battle()
    {
        // 허수아비 모드는 저장되는 설정이라 보스를 잡는 동안에도 켜져 있을 수 있다. 그때 허수아비로 분류된
        // 프레임 하나(인스턴스 id 재사용·옆에 선 허수아비)가 보스 전투의 시작 시각을 기준으로 컷을 계산하면,
        // 그 보스 전투가 강제 종료되고 종료 시각이 "보스 시작 + 측정 시간"으로 굳어 DPS 가 몇 배로 부푼 채
        // 기록되고 업로드 후보가 된다.
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.DummyTestMode = true;
        dm.DummyDurationSec = 30;

        dm.MobHp(BossInstance, 5_000);
        dm.StartBattle(BossInstance);
        long start = clock[0];
        // 소비자 시계도 함께 흘린다 — 안 그러면 조용해진 보스를 닫는 유휴 종료가 먼저 걸린다.
        for (int i = 0; i < 100; i++)
        {
            clock[0] = start + (i * 1_000L);
            dm.SaveDamage(
                new ParsedDamagePacket
                {
                    ActorId = Dealer, TargetId = BossInstance, Damage = 1_000, Timestamp = clock[0],
                },
                dm.CurrentEpoch());
            calc.GetDps();
        }

        Assert.Equal(BossInstance, dm.CurrentTarget());

        HitDummy(dm, clock[0] + 10, 1_000); // 보스전 도중 허수아비 프레임 한 건
        DpsReport after = calc.GetDps();

        Assert.Equal(BossInstance, dm.CurrentTarget());   // 보스 전투가 살아 있다
        Assert.Equal(0, dm.DummyFixedBattleEnd);          // 고정 종료가 찍히지 않았다
        Assert.Equal(100_000.0, after.Information[Dealer].Amount, 3);
        Assert.True(after.BattleEnd - after.BattleStart >= 99_000,
            $"보스 전투 창이 허수아비 길이로 잘렸다: {after.BattleEnd - after.BattleStart}ms");
    }

    [Fact]
    public void LoadMobs_reads_the_isDummy_flag()
    {
        string path = Path.Combine(Path.GetTempPath(), "wm_mobs_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(
            path,
            """[{"code":2300229,"name":"훈련용 허수아비","boss":false,"isDummy":true},{"code":2301008,"name":"보스","boss":true}]""");
        try
        {
            Dictionary<int, Mob> mobs = ReferenceJson.LoadMobs(path);
            Assert.True(mobs[2300229].IsDummy);  // the flag round-trips
            Assert.False(mobs[2301008].IsDummy); // absent flag defaults false
        }
        finally
        {
            File.Delete(path);
        }
    }
}
