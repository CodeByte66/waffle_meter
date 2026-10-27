using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 처치 직후 유령 전투(2026-10-27 21:01:46 감시자 카이라, 로그 20261027-205641).
/// <para>실측 순서: 사망(0x8D04) +0ms / 종료 토글 +1 / <b>시체 시작 토글 +46</b> / HP 0 프레임 +46. 군중전에서는 HP 0
/// 프레임이 시체 토글보다 늦게 와서 HP==0 만 보던 시체 가드가 샜다. 그렇게 열린 전투가 유휴 종료될 때
/// <c>ResetCache</c> 뒤 링 전체를 하한 없이 다시 누적해 처치 전투의 피해를 한 번 더 세었고(35초짜리 중복 기록),
/// 저장 시점의 본인 uid(존 이동 뒤 12208)를 박아 self=MISSING 이 됐다.</para>
/// <para>여기 규칙 중 사망 표시·재누적 하한·전투 중 본인 고정은 <b>전 보스 공통</b>이고, '한 번도 안 열린 보스의
/// 사망 직후 3초' 가드만 어비스 필드보스 한정이다.</para>
/// </summary>
public sealed class GhostBattleAfterKillTests
{
    private const int Boss = 16497;
    private const int BossCode = 2600089;      // 감시자 카이라 (어비스 필드보스)
    private const int Other = 18800;
    private const int OtherCode = 2600068;     // 정령왕 아그로 (어비스 필드보스)
    private const int Dungeon = 30001;
    private const int DungeonCode = 2311402;   // 나사라크 (던전 보스)
    private const int Trash = 23177;
    private const int TrashCode = 2920115;
    private const int Self = 2851;
    private const int SelfAfterReload = 12208;

    private static (DataManager Dm, DpsCalculator Calc, long[] Clock, List<DpsLog> Saved) Setup()
    {
        long[] now = { 1_000_000 };
        var dm = new DataManager { Clock = () => now[0] };
        dm.LoadMobs(new Dictionary<int, Mob>
        {
            [BossCode] = new Mob(BossCode, "감시자 카이라", Boss: true),
            [OtherCode] = new Mob(OtherCode, "정령왕 아그로", Boss: true),
            [DungeonCode] = new Mob(DungeonCode, "나사라크", Boss: true),
            [TrashCode] = new Mob(TrashCode, "잡몹", Boss: false),
        });
        dm.SaveMobId(Boss, BossCode);
        dm.SaveMobId(Other, OtherCode);
        dm.SaveMobId(Dungeon, DungeonCode);
        dm.SaveMobId(Trash, TrashCode);
        dm.SaveNickname(Self, "콘팡", isExecutor: true, server: 2003, jobByte: 32);
        var calc = new DpsCalculator(dm);
        var saved = new List<DpsLog>();
        calc.OnBattleLogged = saved.Add;
        return (dm, calc, now, saved);
    }

    private static void Hit(DataManager dm, int target, int actor, long ts, int dmg) =>
        dm.SaveDamage(new ParsedDamagePacket { ActorId = actor, TargetId = target, Damage = dmg, Timestamp = ts }, dm.CurrentEpoch());

    [Fact]
    public void Kill_with_trailing_hp_frame_saves_one_kill_and_no_ghost()
    {
        (DataManager dm, DpsCalculator calc, long[] now, List<DpsLog> saved) = Setup();
        dm.MobHp(Boss, 14_000_000_000);
        dm.StartBattle(Boss);
        long t0 = now[0];
        for (int i = 0; i < 10; i++) Hit(dm, Boss, Self, t0 + i * 1_000, 1000);
        dm.MobHp(Boss, 5_833_693);
        now[0] = t0 + 9_500;
        calc.GetDps();

        now[0] = t0 + 10_000;
        dm.SaveEntityDeath(Boss, now[0]);   // 0x8D04
        dm.EndBattle(Boss);                 // 종료 토글, 로그상 +1ms
        now[0] += 46;
        dm.StartBattle(Boss);               // 시체 토글 — 삼켜져야 한다
        dm.MobHp(Boss, 0);                  // 뒤늦은 HP 0 프레임
        Assert.Equal(-1, dm.CurrentTarget());
        calc.GetDps();

        Assert.Single(saved);
        Assert.Equal(0, saved[0].Report.Target!.RemainHp);                  // 킬
        Assert.Equal(10_000, saved[0].Report.Information[Self].Amount, 3);   // 마지막 1초까지 다 들어 있다
    }

    [Fact]
    public void Death_mark_alone_latches_the_kill()
    {
        // HP 0 프레임이 끝내 안 와도 사망 브로드캐스트가 곧 HP 0 이다.
        (DataManager dm, DpsCalculator calc, long[] now, List<DpsLog> saved) = Setup();
        dm.MobHp(Dungeon, 90_000_000);
        dm.StartBattle(Dungeon);
        Hit(dm, Dungeon, Self, now[0] + 100, 1000);
        calc.GetDps();
        dm.MobHp(Dungeon, 47_008_281);
        dm.SaveEntityDeath(Dungeon, now[0]);
        dm.EndBattle(Dungeon);
        calc.GetDps();

        Assert.Single(saved);
        Assert.Equal(0, saved[0].Report.Target!.RemainHp);
    }

    [Fact]
    public void A_boss_that_dies_and_comes_back_with_hp_restarts_on_its_first_hp_frame()
    {
        // 같은 엔티티의 페이즈 '사망' — 사망 표시가 정당한 재시작을 얼리면 안 된다(pending 재생이 회복시킨다).
        (DataManager dm, _, long[] now, _) = Setup();
        dm.MobHp(Dungeon, 90_000_000);
        dm.StartBattle(Dungeon);
        dm.SaveEntityDeath(Dungeon, now[0]);
        dm.EndBattle(Dungeon);
        now[0] += 500;
        dm.StartBattle(Dungeon);
        Assert.Equal(-1, dm.CurrentTarget());
        now[0] += 300;
        dm.MobHp(Dungeon, 80_000_000);
        Assert.Equal(Dungeon, dm.CurrentTarget());
    }

    [Fact]
    public void Trash_and_player_deaths_do_not_write_boss_hp()
    {
        (DataManager dm, _, long[] now, _) = Setup();
        dm.SaveEntityDeath(Trash, now[0]);
        dm.SaveEntityDeath(Self, now[0]);
        Assert.Null(dm.MobHp(Trash));
        Assert.Null(dm.MobHp(Self));
    }

    [Fact]
    public void A_reused_player_uid_that_dies_does_not_kill_the_old_boss_mapping()
    {
        // 보스였던 엔티티 id 가 지금은 플레이어로 알려져 있다 — 죽은 건 그 플레이어다.
        (DataManager dm, _, long[] now, _) = Setup();
        dm.MobHp(Other, 5_000_000_000);
        dm.SaveNickname(Other, "남", isExecutor: false, server: 2003, jobByte: 32);
        dm.SaveEntityDeath(Other, now[0]);
        Assert.Equal(5_000_000_000, dm.MobHp(Other));
    }

    [Fact]
    public void A_same_target_restart_does_not_re_count_the_previous_battle()
    {
        (DataManager dm, DpsCalculator calc, long[] now, List<DpsLog> saved) = Setup();
        dm.MobHp(Dungeon, 90_000_000);
        dm.StartBattle(Dungeon);
        long t0 = now[0];
        for (int i = 0; i < 5; i++) Hit(dm, Dungeon, Self, t0 + i * 1_000, 1000);
        now[0] = t0 + 5_000;
        calc.GetDps();
        dm.EndBattle(Dungeon);              // 리쉬, HP>0, 재풀 전에 GetDps 가 안 돌았다
        now[0] = t0 + 9_000;                // 3초 창 밖 → 같은 id 의 진짜 재시작
        dm.StartBattle(Dungeon);
        calc.GetDps();                      // 1판은 battleRestartedWithSameTarget 로 저장
        for (int i = 0; i < 3; i++) Hit(dm, Dungeon, Self, now[0] + 100 + i * 1_000, 500);
        now[0] += 4_000;
        calc.GetDps();
        dm.EndBattle(Dungeon);
        calc.GetDps();

        Assert.Equal(2, saved.Count);
        Assert.Equal(5_000, saved[0].Report.Information[Self].Amount, 3);
        Assert.Equal(1_500, saved[1].Report.Information[Self].Amount, 3); // 수정 전: 6,500 (1판을 다시 셌다)
    }

    [Fact]
    public void Rebuild_lower_bound_is_the_previous_saved_end_of_the_same_entity()
    {
        // 유령 전투와 같은 모양: 같은 엔티티로 다시 열린 전투가 유휴 종료로 닫히며 링 전체를 다시 누적한다.
        (DataManager dm, DpsCalculator calc, long[] now, List<DpsLog> saved) = Setup();
        dm.MobHp(Dungeon, 90_000_000);
        dm.StartBattle(Dungeon);
        long t0 = now[0];
        for (int i = 0; i < 5; i++) Hit(dm, Dungeon, Self, t0 + i * 1_000, 1000); // 1판, 마지막 타격 t0+4000
        now[0] = t0 + 4_100;
        calc.GetDps();
        dm.EndBattle(Dungeon);
        dm.StartBattle(Dungeon);            // HP>0 이라 곧바로 재시작(링은 비워지지 않았다)
        now[0] = t0 + 4_200;
        calc.GetDps();                      // 1판 저장 → 하한 = t0+4000+1
        Assert.Single(saved);
        Assert.Equal(t0 + 4_000, saved[0].Report.BattleEnd);

        Hit(dm, Dungeon, Self, t0 + 5_000, 700);
        now[0] = t0 + 5_100;
        DpsReport live = calc.GetDps();
        // 라이브도 같은 하한: 시작 1초 전까지 받아 주는 선행 창이 1판의 마지막 타격(t0+4000)을 다시 집지 않는다.
        Assert.Equal(700, live.Information[Self].Amount, 3);
        now[0] = t0 + 5_000 + 61_000;       // 보스 무음 60초 + 어디에도 딜 없음 20초 → 유휴 종료
        calc.GetDps();                      // ResetCache + 링 전체 재누적

        Assert.Equal(2, saved.Count);
        Assert.Equal(700, saved[1].Report.Information[Self].Amount, 3); // 하한이 없으면 5,700
    }

    [Fact]
    public void Idle_closed_battle_keeps_the_in_battle_self_after_a_zone_reload()
    {
        // 일반 규칙이라 던전 보스로 본다(어비스의 존 이탈 규칙과 무관하게).
        (DataManager dm, DpsCalculator calc, long[] now, List<DpsLog> saved) = Setup();
        dm.MobHp(Dungeon, 90_000_000);
        dm.StartBattle(Dungeon);
        Hit(dm, Dungeon, Self, now[0] + 100, 1000);
        calc.GetDps();
        now[0] += 70_000;
        dm.SaveNickname(SelfAfterReload, "콘팡", isExecutor: true, server: 2003, jobByte: 32); // 재인스턴스
        now[0] += 15_000;
        calc.GetDps();                      // 유휴 종료 → 저장

        Assert.Single(saved);
        Assert.Equal(Self, saved[0].Report.ExecutorId); // 수정 전: 12208 → self=MISSING
    }

    [Fact]
    public void Refused_boss_kill_toggle_never_opens_a_ghost()
    {
        // 한 번도 열린 적 없는 어비스 필드보스(잠김에 거부됐던 B)가 죽고, 34ms 뒤 시체 토글 0→1 이 온다.
        // 시체 가드는 '끝난 전투'가 있어야 걸리므로 이 토글은 그대로 유령 전투를 열었다.
        (DataManager dm, _, long[] now, _) = Setup();
        dm.MobHp(Other, 3_000_000_000);
        dm.SaveEntityDeath(Other, now[0]);
        dm.EndBattle(Other);                // 현 타깃이 아니라 무시된다
        now[0] += 34;
        dm.StartBattle(Other);

        Assert.True(dm.CurrentTarget() <= 0);
    }

    [Fact]
    public void The_never_opened_kill_guard_expires_after_three_seconds()
    {
        // ⚠️ 3초 제한이 없으면 같은 엔티티의 정상 재풀까지 막힌다.
        (DataManager dm, _, long[] now, _) = Setup();
        dm.MobHp(Other, 3_000_000_000);
        dm.SaveEntityDeath(Other, now[0]);
        now[0] += 3_500;
        dm.StartBattle(Other);

        Assert.Equal(Other, dm.CurrentTarget());
    }

    [Fact]
    public void Outside_the_abyss_the_never_opened_kill_toggle_behaves_as_before()
    {
        // 스코프 고정: 이 가드는 어비스 필드보스 한정이다(오너 결정). 던전 보스는 종전과 같다.
        (DataManager dm, _, long[] now, _) = Setup();
        dm.MobHp(Dungeon, 90_000_000);
        dm.SaveEntityDeath(Dungeon, now[0]);
        now[0] += 34;
        dm.StartBattle(Dungeon);

        Assert.Equal(Dungeon, dm.CurrentTarget());
    }
}
