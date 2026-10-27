using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 어비스 필드보스의 '사라진 보스' 규칙과 재진입 유지(수정 3). 스코프는 진행 중 전투의 보스가 어비스 필드보스
/// (<see cref="FieldBossCatalog.IsAbyss"/>)일 때뿐이다 — 각 규칙 옆에 던전·인스턴스 보스와 그 밖의 필드보스가
/// <b>종전과 똑같이</b> 동작한다는 대조 테스트를 둔다.
/// <para>배경(2026-10-27 감시자 카이라, 400명 군중전): 보스가 시야 밖으로 나가면(0x3642) 서버가 HP 를 끊고, 마지막
/// HP&gt;0 이 primary-lock 을 60초 붙잡아 다음 보스의 시작 토글이 거부된다(재시도 없음 = 미기록). 군중이 늘 무언가를
/// 때려서 '20초 무음' 조건이 충족되지 않아 옛 보스 화면이 고착되고, 존을 옮겨도 닫히지 않는다. 원거리 부활 뒤 돌아오면
/// 재진입 스폰이 HP 를 싣는데 읽지 않았다.</para>
/// </summary>
public sealed class BossReentryAndZoneLeaveTests
{
    private const int Kaira = 16497;
    private const int KairaCode = 2600089;      // 감시자 카이라 — 어비스 필드보스
    private const int Agro = 18800;
    private const int AgroCode = 2600068;       // 정령왕 아그로 — 어비스 필드보스
    private const int Field = 19900;
    private const int FieldCode = 2406034;      // 경계의 방랑자 파르곤 — 모르헤임 필드보스(어비스 아님)
    private const int Dungeon = 30001;
    private const int DungeonCode = 2311402;    // 나사라크 — 인스턴스 던전 보스
    private const int Gimmick = 30002;
    private const int GimmickCode = 2921427;    // 바크론의 가시속박 — boss=true 기믹
    private const int Add = 40001;
    private const int TrashCode = 2920115;
    private const int Self = 2851;
    private const int SelfAfterReload = 12208;

    private static (DataManager Dm, long[] Clock) Setup()
    {
        long[] now = { 1_000_000 };
        var dm = new DataManager { Clock = () => now[0] };
        dm.LoadMobs(new Dictionary<int, Mob>
        {
            [KairaCode] = new Mob(KairaCode, "감시자 카이라", Boss: true),
            [AgroCode] = new Mob(AgroCode, "정령왕 아그로", Boss: true),
            [FieldCode] = new Mob(FieldCode, "경계의 방랑자 파르곤", Boss: true),
            [DungeonCode] = new Mob(DungeonCode, "나사라크", Boss: true),
            [GimmickCode] = new Mob(GimmickCode, "바크론의 가시속박", Boss: true),
            [TrashCode] = new Mob(TrashCode, "잡몹", Boss: false),
        });
        dm.LoadContentTypes(new Dictionary<int, string> { [DungeonCode] = "expedition" });
        dm.SaveNickname(Self, "콘팡", isExecutor: true, server: 2003, jobByte: 32);
        dm.SaveMobId(Agro, AgroCode);
        dm.SaveMobId(Gimmick, GimmickCode);
        dm.SaveMobId(Add, TrashCode);
        return (dm, now);
    }

    /// <summary>보스를 등록·교전시키고 HP 를 보고해 '살아 있고 신선한' 전투로 만든다. 어비스 필드보스는 스폰 HP 도 싣는다.</summary>
    private static void Engage(DataManager dm, int id, int code, long hp, long max)
    {
        dm.SaveMobId(id, code);
        dm.SaveMobSpawnVitals(id, 1, max, max, inCombat: false);
        dm.StartBattle(id);
        dm.MobHp(id, hp);
    }

    private static void EngageKaira(DataManager dm) => Engage(dm, Kaira, KairaCode, 4_507_033_630, 14_580_000_000);

    /// <summary>군중이 근처 쫄을 때린다 — '어디에도 딜이 없다' 조건을 계속 깨는 400명 필드.</summary>
    private static void CrowdHitsAdds(DataManager dm, long[] now, long seconds)
    {
        for (long i = 0; i < seconds; i++)
        {
            now[0] += 1_000;
            dm.SaveDamage(new ParsedDamagePacket { ActorId = 9001, TargetId = Add, Damage = 1, Timestamp = now[0] }, dm.CurrentEpoch());
        }
    }

    private static void ZoneReload(DataManager dm) =>
        dm.SaveNickname(SelfAfterReload, "콘팡", isExecutor: true, server: 2003, jobByte: 32);

    // ---- 시야 밖 → primary-lock 양보 ----

    [Fact]
    public void OpenWorld_out_of_sight_boss_yields_to_next_boss_immediately()
    {
        (DataManager dm, long[] now) = Setup();
        EngageKaira(dm);
        dm.SaveMobDisappear(Kaira);
        CrowdHitsAdds(dm, now, 10);
        dm.StartBattle(Agro);               // 10초 < 60초지만 카이라는 시야 밖이다

        Assert.Equal(Agro, dm.CurrentTarget());
    }

    [Fact]
    public void A_non_abyss_field_boss_out_of_sight_still_holds_the_lock()
    {
        (DataManager dm, long[] now) = Setup();
        Engage(dm, Field, FieldCode, 50_000_000, 90_000_000);
        dm.SaveMobDisappear(Field);
        CrowdHitsAdds(dm, now, 10);
        dm.StartBattle(Agro);

        Assert.Equal(Field, dm.CurrentTarget());
    }

    [Fact]
    public void Instanced_boss_despawn_keeps_gimmick_protection()
    {
        // 칼드릭스형 무음 페이즈(27.8초) 동안 boss=true 기믹이 토글해도 전투를 못 뺏는다 — 0x3642 가 와도 그대로.
        (DataManager dm, long[] now) = Setup();
        Engage(dm, Dungeon, DungeonCode, 50_000_000, 90_000_000);
        dm.SaveMobDisappear(Dungeon);
        CrowdHitsAdds(dm, now, 14);
        dm.StartBattle(Gimmick);
        Assert.Equal(Dungeon, dm.CurrentTarget());

        CrowdHitsAdds(dm, now, 56);         // 보스 무음 70초, 쫄 딜은 계속 — 종전 규칙대로 유지
        dm.TickBossBattleIdle();
        Assert.Equal(Dungeon, dm.CurrentTarget());
    }

    // ---- 시야 밖 유지 시간 ----

    [Fact]
    public void Out_of_sight_longer_than_the_hold_closes_at_the_last_activity_even_while_the_crowd_fights()
    {
        (DataManager dm, long[] now) = Setup();
        EngageKaira(dm);
        long last = now[0];
        dm.SaveMobDisappear(Kaira);
        CrowdHitsAdds(dm, now, 59);
        dm.TickBossBattleIdle();
        Assert.Equal(Kaira, dm.CurrentTarget());   // 아직 유지 시간 안

        CrowdHitsAdds(dm, now, 2);
        dm.TickBossBattleIdle();
        Assert.True(dm.CurrentTarget() <= 0);
        Assert.Equal(last, dm.CurrentBattleEnd());
    }

    [Fact]
    public void A_non_abyss_boss_out_of_sight_with_a_busy_crowd_keeps_the_old_rule()
    {
        (DataManager dm, long[] now) = Setup();
        Engage(dm, Field, FieldCode, 50_000_000, 90_000_000);
        dm.SaveMobDisappear(Field);
        CrowdHitsAdds(dm, now, 90);
        dm.TickBossBattleIdle();

        Assert.Equal(Field, dm.CurrentTarget());   // 종전: 어디선가 딜이 계속되면 닫지 않는다
    }

    // ---- 존 이탈 ----

    [Fact]
    public void Zone_reload_closes_the_old_battle_even_while_trash_is_being_hit()
    {
        (DataManager dm, long[] now) = Setup();
        EngageKaira(dm);
        long last = now[0];
        now[0] += 2_000;
        ZoneReload(dm);
        CrowdHitsAdds(dm, now, 2);
        dm.TickBossBattleIdle();

        Assert.True(dm.CurrentTarget() <= 0);
        Assert.Equal(last, dm.CurrentBattleEnd());
    }

    [Fact]
    public void Teleporting_into_a_dungeon_releases_the_stale_boss()
    {
        (DataManager dm, long[] now) = Setup();
        EngageKaira(dm);
        now[0] += 5_000;
        ZoneReload(dm);
        CrowdHitsAdds(dm, now, 4);          // 던전 쫄
        dm.SaveMobId(Dungeon, DungeonCode);
        dm.StartBattle(Dungeon);            // 카이라 마지막 신호 9초 뒤

        Assert.Equal(Dungeon, dm.CurrentTarget());
    }

    [Fact]
    public void A_zone_reload_outside_the_abyss_changes_nothing()
    {
        (DataManager dm, long[] now) = Setup();
        Engage(dm, Dungeon, DungeonCode, 50_000_000, 90_000_000);
        now[0] += 2_000;
        ZoneReload(dm);
        CrowdHitsAdds(dm, now, 8);
        dm.TickBossBattleIdle();
        dm.StartBattle(Gimmick);
        Assert.Equal(Dungeon, dm.CurrentTarget());

        CrowdHitsAdds(dm, now, 60);
        dm.TickBossBattleIdle();
        Assert.Equal(Dungeon, dm.CurrentTarget());
    }

    [Fact]
    public void A_rebind_while_the_boss_still_reports_does_not_close()
    {
        (DataManager dm, long[] now) = Setup();
        EngageKaira(dm);
        ZoneReload(dm);
        for (int i = 0; i < 10; i++)
        {
            now[0] += 1_000;
            dm.MobHp(Kaira, 4_000_000_000 - i);
            dm.TickBossBattleIdle();
        }

        Assert.Equal(Kaira, dm.CurrentTarget());

        // 재바인드 '뒤에도' 보스가 보고했으니 그 로드는 이 전투와 무관하다 — 이후 잠깐(5초) 조용해도 존 이탈이 아니다.
        CrowdHitsAdds(dm, now, 5);
        dm.TickBossBattleIdle();
        Assert.Equal(Kaira, dm.CurrentTarget());
    }

    [Fact]
    public void Same_uid_own_load_resend_far_revive_does_not_close()
    {
        // 2026-10-27 21:01:16 본인 사망 → 원거리 부활: 같은 uid 로 0x3633 이 다시 온다. 존 이동이 아니다.
        (DataManager dm, long[] now) = Setup();
        EngageKaira(dm);
        now[0] += 13_000;
        dm.SaveNickname(Self, "콘팡", isExecutor: true, server: 2003, jobByte: 32);
        now[0] += 5_000;
        dm.TickBossBattleIdle();

        Assert.Equal(Kaira, dm.CurrentTarget());
    }

    // ---- 엔티티 id 재발급 ----

    [Fact]
    public void Recycled_entity_id_closes_battle()
    {
        (DataManager dm, long[] now) = Setup();
        EngageKaira(dm);
        long last = now[0];
        now[0] += 5_000;
        ZoneReload(dm);
        dm.SaveMobId(Kaira, TrashCode);     // 새 존이 카이라의 엔티티 id 를 쫄에게 줬다

        Assert.True(dm.CurrentTarget() <= 0);
        Assert.Equal(last, dm.CurrentBattleEnd());
    }

    [Fact]
    public void A_garbage_code_from_a_moving_spawn_does_not_close_the_battle()
    {
        // 이동 중 스폰은 마커 스캔이 카탈로그 밖 코드를 읽는다(실측 11건, 예: 1018881) — 그걸로 닫으면 안 된다.
        (DataManager dm, _) = Setup();
        EngageKaira(dm);
        dm.SaveMobId(Kaira, 1_018_881);

        Assert.Equal(Kaira, dm.CurrentTarget());
    }

    [Fact]
    public void A_recycled_id_outside_the_abyss_behaves_as_before()
    {
        (DataManager dm, _) = Setup();
        Engage(dm, Dungeon, DungeonCode, 50_000_000, 90_000_000);
        dm.SaveMobId(Dungeon, TrashCode);

        Assert.Equal(Dungeon, dm.CurrentTarget());
    }

    // ---- 재진입 스폰 (수정 3) ----

    [Fact]
    public void Reentry_spawn_with_hp_keeps_the_return_trip_in_one_battle()
    {
        (DataManager dm, long[] now) = Setup();
        EngageKaira(dm);
        long rev = dm.CurrentBattleRevision();
        dm.SaveMobDisappear(Kaira);
        CrowdHitsAdds(dm, now, 55);         // 원거리 부활 후 달려오는 동안
        dm.SaveMobSpawnVitals(Kaira, 1, 2_391_135_608, 14_580_000_000, inCombat: true); // 다시 시야에 들어왔다
        CrowdHitsAdds(dm, now, 10);         // 시야 이탈 65초 — 유지 시간(60초)을 넘겼지만 이미 돌아왔다
        dm.TickBossBattleIdle();

        Assert.Equal(Kaira, dm.CurrentTarget());
        Assert.Equal(rev, dm.CurrentBattleRevision());
        Assert.Equal(2_391_135_608, dm.MobHp(Kaira));
    }

    [Fact]
    public void A_corpse_entering_sight_does_not_re_arm_the_lock_and_is_not_a_kill()
    {
        (DataManager dm, long[] now) = Setup();
        EngageKaira(dm);
        long rev = dm.CurrentBattleRevision();
        long last = now[0];
        dm.SaveMobDisappear(Kaira);
        CrowdHitsAdds(dm, now, 15);         // 시야 밖에서 죽었다(본 적 없는 처치)
        dm.SaveMobSpawnVitals(Kaira, 0, 0, 14_580_000_000, inCombat: false); // 시체로 다시 시야에

        Assert.True(dm.CurrentTarget() <= 0);
        Assert.Equal(last, dm.CurrentBattleEnd());
        Assert.Equal(4_507_033_630, dm.BattleLowMobHp(Kaira, rev)); // 킬로 승격되지 않았다

        dm.StartBattle(Agro);
        Assert.Equal(Agro, dm.CurrentTarget());
    }

    [Fact]
    public void A_full_hp_respawn_of_the_current_boss_closes_instead_of_merging()
    {
        (DataManager dm, long[] now) = Setup();
        EngageKaira(dm);
        long last = now[0];
        now[0] += 20_000;
        dm.SaveMobSpawnVitals(Kaira, 1, 14_580_000_000, 14_580_000_000, inCombat: false);

        Assert.True(dm.CurrentTarget() <= 0);
        Assert.Equal(last, dm.CurrentBattleEnd());
    }

    [Fact]
    public void A_spawn_of_a_non_abyss_boss_changes_nothing()
    {
        (DataManager dm, long[] now) = Setup();
        Engage(dm, Dungeon, DungeonCode, 50_000_000, 90_000_000);
        now[0] += 5_000;
        dm.SaveMobSpawnVitals(Dungeon, 0, 0, 90_000_000, inCombat: false);
        dm.SaveMobSpawnVitals(Dungeon, 1, 90_000_000, 90_000_000, inCombat: false);

        Assert.Equal(Dungeon, dm.CurrentTarget());
        Assert.Equal(50_000_000, dm.MobHp(Dungeon));
    }
}
