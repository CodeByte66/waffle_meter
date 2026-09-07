using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 스킬 시전 타임라인(0x3802)의 데이터 계층. 잠그는 것: 이름 해석(원본 우선, base 폴백), 소환수 시전을
/// 주인에게 접기(+엔티티 id 재사용 방어), 창 자르기와 보존 정리, <b>저장 리포트에 얼려 두기</b>(빠지면 기록
/// 재생에서 탭이 통째로 빈다), 그리고 <b>"이 사람이 누른 것"만 남기는 필터</b>.
/// <para>마지막 것이 왜 필요한지는 실제 코퍼스가 알려 줬다 — 0x3802 는 시전만 싣지 않는다. 근거는
/// <c>DataManager.IsOwnCast</c> 주석에 있다.</para>
/// </summary>
public sealed class SkillCastTimelineTests
{
    private const int BossInstance = 100;
    private const int BossCode = 2301008;
    private const int Dealer = 5001;   // 정령성(밴드 16) — 소환수를 쓰는 직업이라 접기 규칙까지 한 픽스처로 본다
    private const int Summon = 6001;
    private const int ElementalistJobByte = 21;

    private static (DataManager Dm, DpsCalculator Calc, long[] Clock) Setup()
    {
        long[] now = { 1_000_000 };
        var dm = new DataManager { Clock = () => now[0] };
        dm.LoadMobs(new Dictionary<int, Mob> { [BossCode] = new Mob(BossCode, "보스", Boss: true) });
        dm.LoadSkills(
        [
            new Skill(16010000, "냉기 충격"),
            new Skill(16010047, "격파의 냉기"),
            new Skill(16020000, "진공 폭발"),
            new Skill(16030000, "대지 진동"),
            new Skill(11340000, "흡혈의 검"),
            new Skill(11010000, "절단의 맹타"),
        ]);
        dm.SaveMobId(BossInstance, BossCode);
        dm.SaveNickname(Dealer, "딜러", isExecutor: true, server: 2003, jobByte: ElementalistJobByte);
        return (dm, new DpsCalculator(dm), now);
    }

    private static void Hit(DataManager dm, long timestamp, int damage) =>
        dm.SaveDamage(
            new ParsedDamagePacket { ActorId = Dealer, TargetId = BossInstance, Damage = damage, Timestamp = timestamp },
            dm.CurrentEpoch());

    [Fact]
    public void An_exact_code_wins_over_its_folded_base()
    {
        // 0x3802 는 특화 접미가 붙은 원본 코드를 싣는다. 무조건 base 로 접으면 8종이 엉뚱한 이름이 된다 —
        // 실측 사례가 11010047 격파의 맹타 → 절단의 맹타 다.
        (DataManager dm, _, long[] clock) = Setup();
        dm.SaveSkillCast(Dealer, 16010047, clock[0]);
        dm.SaveSkillCast(Dealer, 16020240, clock[0] + 100); // 카탈로그에 없는 특화 변종 → base 폴백

        List<SkillCastRow> casts = dm.BattleSkillCasts(Dealer, clock[0] - 1, clock[0] + 1_000);

        Assert.Equal("격파의 냉기", casts[0].Name);
        Assert.Equal("진공 폭발", casts[1].Name); // 16020240 -> 16020000
    }

    [Fact]
    public void An_unknown_code_falls_back_to_its_number_rather_than_a_wrong_name()
    {
        (DataManager dm, _, long[] clock) = Setup();
        dm.SaveSkillCast(Dealer, 16_990_000, clock[0]); // 내 밴드지만 카탈로그에 없는 코드

        Assert.Equal("16990000", Assert.Single(dm.BattleSkillCasts(Dealer, clock[0] - 1, clock[0] + 1)).Name);
    }

    [Fact]
    public void A_summons_casts_fold_onto_its_owner()
    {
        // 피해 경로가 ResolveActor 로 하는 것과 같은 규칙. 접지 않으면 정령성 상세에서 소환수 스킬이
        // 통째로 사라진다 — 그 엔티티 id 는 참가자 목록에 없기 때문이다.
        (DataManager dm, _, long[] clock) = Setup();
        dm.SaveSummon(Summon, Dealer);
        dm.SaveSkillCast(Dealer, 16010000, clock[0]);
        dm.SaveSkillCast(Summon, 16030000, clock[0] + 500);

        List<SkillCastRow> casts = dm.BattleSkillCasts(Dealer, clock[0] - 1, clock[0] + 1_000);

        Assert.Equal(["냉기 충격", "대지 진동"], casts.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void A_reused_summon_id_cannot_drag_another_players_casts_into_this_timeline()
    {
        // 소환수 맵은 전투를 넘어 살아남고(비우는 건 HardReset 뿐) 엔티티 id 는 서버가 재발급한다.
        // 낡은 summonId→owner 항목이 그 id 를 물려받은 실제 플레이어의 시전을 옛 주인에게 끌어오면
        // 시전 수와 간격 통계가 두 사람 것으로 섞인다. (같은 밴드 코드를 써서, 막는 것이 밴드 필터가 아니라
        // 재사용 가드임을 분명히 한다.)
        (DataManager dm, _, long[] clock) = Setup();
        dm.SaveSummon(Summon, Dealer);
        dm.SaveSkillCast(Dealer, 16010000, clock[0]);
        dm.SaveSkillCast(Summon, 16030000, clock[0] + 100);
        Assert.Equal(2, dm.BattleSkillCasts(Dealer, clock[0] - 1, clock[0] + 1_000).Count); // 아직은 진짜 소환수

        dm.SaveNickname(Summon, "남", isExecutor: false, server: 2003, jobByte: ElementalistJobByte);
        dm.SaveSkillCast(Summon, 16020000, clock[0] + 200);

        List<SkillCastRow> casts = dm.BattleSkillCasts(Dealer, clock[0] - 1, clock[0] + 1_000);
        Assert.Equal(["냉기 충격"], casts.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void A_party_grant_proc_is_not_counted_as_this_players_cast()
    {
        // 🔑 실제 코퍼스 재생 실측(5인 파티, 직업 밴드 44,178 프레임): 검성의 흡혈의 검 착취(11340028)가
        // <b>수혜자를 actor 로</b> 파티원 전원에게 온다 — 궁성 751회, 마도성 508회, 치유성 587회. 검성 본인만
        // 밴드 밖 프레임이 0건이었다. 걸러내지 않으면 "궁성이 흡혈의 검을 158번 썼다"는 유령 행이 생긴다.
        (DataManager dm, _, long[] clock) = Setup();
        dm.SaveSkillCast(Dealer, 16010000, clock[0]);
        dm.SaveSkillCast(Dealer, 11340028, clock[0] + 50);  // 남의 직업 밴드 + grant 코드
        dm.SaveSkillCast(Dealer, 11010000, clock[0] + 100); // 남의 직업 밴드

        List<SkillCastRow> casts = dm.BattleSkillCasts(Dealer, clock[0] - 1, clock[0] + 1_000);

        Assert.Equal(["냉기 충격"], casts.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void The_grant_proc_is_dropped_from_the_casters_own_list_too()
    {
        // 프록은 시전자에게도 온다. 시전자에게는 자기 밴드라 밴드 필터로는 안 걸린다 — 그래서 grant 코드를
        // 따로 뺀다. 대가로 검성은 흡혈의 검을 "누른" 기록을 잃지만(20초에 한 번), 수백 줄의 유령 행보다 낫다.
        (DataManager dm, _, long[] clock) = Setup();
        const int Gladiator = 7001;
        dm.SaveNickname(Gladiator, "검성", isExecutor: false, server: 2003, jobByte: 5);
        dm.SaveSkillCast(Gladiator, 11010000, clock[0]);
        dm.SaveSkillCast(Gladiator, 11340028, clock[0] + 50);

        List<SkillCastRow> casts = dm.BattleSkillCasts(Gladiator, clock[0] - 1, clock[0] + 1_000);

        Assert.Equal(["절단의 맹타"], casts.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void A_passive_proc_is_not_a_cast()
    {
        // 사용자 요구: 타임라인은 "누른 스킬"만 — 패시브/자동 발동은 뺀다. 클라 Skill.dat 의 SkillType 이
        // 정본이고, 실측에서 그 판정이 행동으로도 확증됐다(패시브 판정의 72.8%가 직전 시전과 0ms).
        (DataManager dm, _, long[] clock) = Setup();
        dm.LoadSkillClass(passive: [16_070_000], activeOverrides: []);
        dm.SaveSkillCast(Dealer, 16010000, clock[0]);
        dm.SaveSkillCast(Dealer, 16_070_007, clock[0]);      // 변종 코드 → base 가 패시브
        dm.SaveSkillCast(Dealer, 16020000, clock[0] + 500);

        List<SkillCastRow> casts = dm.BattleSkillCasts(Dealer, clock[0] - 1, clock[0] + 1_000);

        Assert.Equal(["냉기 충격", "진공 폭발"], casts.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void An_active_whose_base_is_passive_survives()
    {
        // 긴급 회피 계열: 자기 타입은 Active 인데 base(무기 장착)가 Passive 다. override 가 없으면 사라진다.
        (DataManager dm, _, long[] clock) = Setup();
        dm.LoadSkillClass(passive: [16_000_000], activeOverrides: [16_000_100]);
        dm.SaveSkillCast(Dealer, 16_000_100, clock[0]);

        Assert.Single(dm.BattleSkillCasts(Dealer, clock[0] - 1, clock[0] + 1_000));
    }

    [Fact]
    public void One_cast_that_emits_several_frames_in_the_same_millisecond_counts_once()
    {
        // 실측: 완전 중복 1,067건 / 44,178, 전량이 살기 파열(최대 5중복)과 불꽃 작살(최대 4중복) 두 스킬.
        // 세면 횟수가 부풀고 간격에 0ms 가 섞여 스킬별 통계가 망가진다.
        (DataManager dm, _, long[] clock) = Setup();
        dm.SaveSkillCast(Dealer, 16010000, clock[0]);
        dm.SaveSkillCast(Dealer, 16010000, clock[0]);
        dm.SaveSkillCast(Dealer, 16010000, clock[0]);
        dm.SaveSkillCast(Dealer, 16010000, clock[0] + 800); // 진짜 다음 시전

        List<SkillCastRow> casts = dm.BattleSkillCasts(Dealer, clock[0] - 1, clock[0] + 1_000);

        Assert.Equal(2, casts.Count);
        Assert.Equal(800, casts[1].TimestampMs - casts[0].TimestampMs);
    }

    [Fact]
    public void An_unknown_job_keeps_everything_rather_than_showing_an_empty_tab()
    {
        // 직업을 아직 모르는 uid(이름 스냅샷 전)는 밴드로 거르지 않는다 — DPS 그래프 레인과 같은 폴백이다.
        // grant 코드만은 그때도 뺀다(그건 직업과 무관하게 시전이 아니다).
        (DataManager dm, _, long[] clock) = Setup();
        const int Unknown = 8001;
        dm.SaveSkillCast(Unknown, 11010000, clock[0]);
        dm.SaveSkillCast(Unknown, 16010000, clock[0] + 100);
        dm.SaveSkillCast(Unknown, 11340028, clock[0] + 200);

        List<SkillCastRow> casts = dm.BattleSkillCasts(Unknown, clock[0] - 1, clock[0] + 1_000);

        Assert.Equal(["절단의 맹타", "냉기 충격"], casts.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void The_cooldown_start_fact_survives_to_the_row_and_the_frozen_snapshot()
    {
        // 같은 스킬의 여러 발동 중 어느 것이 쿨을 돌렸는지는 와이어에 있는 유일한 "확실히 나갔다" 신호다.
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.MobHp(BossInstance, 5000);
        dm.StartBattle(BossInstance);
        dm.SaveSkillCast(Dealer, 16010000, clock[0] + 100, startsCooldown: true);
        dm.SaveSkillCast(Dealer, 16020000, clock[0] + 600);   // 쿨 없는 발동
        Hit(dm, clock[0] + 200, 3000);
        calc.GetDps();
        Hit(dm, clock[0] + 1_600, 3000);
        calc.GetDps();
        clock[0] += 3_000;
        dm.EndBattle(BossInstance);
        calc.GetDps();

        DpsLog saved = Assert.IsType<DpsLog>(dm.BattleLog(0));
        List<SkillCastRow> frozen = saved.Report.SkillCasts[Dealer];
        Assert.True(frozen.Single(c => c.Name == "냉기 충격").StartsCooldown);
        Assert.False(frozen.Single(c => c.Name == "진공 폭발").StartsCooldown);
    }

    [Fact]
    public void When_two_frames_share_a_millisecond_the_cooldown_one_wins()
    {
        // 중복 제거가 쿨 표식을 조용히 지우면 안 된다 — 그게 그 시각에 대해 아는 더 강한 사실이다.
        (DataManager dm, _, long[] clock) = Setup();
        dm.SaveSkillCast(Dealer, 16010000, clock[0], startsCooldown: false);
        dm.SaveSkillCast(Dealer, 16010000, clock[0], startsCooldown: true);

        SkillCastRow row = Assert.Single(dm.BattleSkillCasts(Dealer, clock[0] - 1, clock[0] + 1));
        Assert.True(row.StartsCooldown);
    }

    [Fact]
    public void Damage_and_DoT_ticks_never_create_timeline_rows()
    {
        // 사용자 요구: 도트·추가타는 빼라. 이 목록은 <b>시전 채널(0x3802)만</b> 읽으므로 피해 이벤트는 애초에
        // 행을 만들지 않는다. 실측(20260831 코퍼스)이 같은 말을 한다 — 그리폰 화살은 도트틱 260개인데 시전은
        // 199개(도트가 시전을 만들면 450쯤이어야 한다), 속사는 직격 557개(다단 501)인데 시전은 282개다.
        (DataManager dm, _, long[] clock) = Setup();
        dm.MobHp(BossInstance, 5000);
        dm.StartBattle(BossInstance);
        dm.SaveSkillCast(Dealer, 16010000, clock[0] + 100);

        for (int i = 0; i < 20; i++)   // 직격 + 추가타 + 도트 틱을 쏟아부어도
        {
            Hit(dm, clock[0] + 200 + (i * 50), 1000);
        }

        Assert.Single(dm.BattleSkillCasts(Dealer, clock[0], clock[0] + 5_000)); // 행은 시전 1건뿐
    }

    [Fact]
    public void Casts_outside_the_window_are_not_returned()
    {
        (DataManager dm, _, long[] clock) = Setup();
        dm.SaveSkillCast(Dealer, 16010000, clock[0] - 5_000);
        dm.SaveSkillCast(Dealer, 16020000, clock[0] + 500);
        dm.SaveSkillCast(Dealer, 16010000, clock[0] + 90_000);

        Assert.Equal("진공 폭발", Assert.Single(dm.BattleSkillCasts(Dealer, clock[0], clock[0] + 1_000)).Name);
    }

    [Fact]
    public void The_store_sheds_stale_actors_on_its_own_so_a_battle_less_field_session_cannot_grow_forever()
    {
        // 정리는 전투가 저장될 때만 도는데, 필드에서 몇 시간을 도는 동안 저장되는 전투가 하나도 없을 수 있다.
        // 그동안 지나가는 모든 플레이어가 액터 키로 쌓인다 — 액터당 상한은 있어도 액터 수에는 없다.
        var repo = new SkillCastRepository();
        repo.Save(9001, new SkillCast(16010000, 0));            // 아주 오래된 통행인
        for (int i = 0; i < 4_096; i++)
        {
            repo.Save(9002, new SkillCast(16010000, 60 * 60 * 1000L + i));
        }

        Assert.Empty(repo.FindInWindow(9001, 0, 60 * 60 * 1000L));   // 보존 창 밖 — 스스로 떨궜다
        Assert.NotEmpty(repo.FindInWindow(9002, 0, 60 * 60 * 1000L)); // 최근 액터는 그대로
    }

    [Fact]
    public void The_retention_sweep_never_eats_the_head_of_a_battle_that_is_still_running()
    {
        // 보존 정리는 "전투가 하나도 저장되지 않는 필드 세션"을 위한 것이지, 진행 중인 전투를 자르라는 뜻이
        // 아니다. 10분을 넘기는 전투(공대·시련)에서 그 전투의 앞부분을 지우면, 얼려 둔 타임라인이 이미 잘린
        // 채로 저장돼 첫 줄 시각이 8:00 부터 시작한다.
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.MobHp(BossInstance, 5_000);
        dm.StartBattle(BossInstance);
        long start = clock[0];
        dm.SaveSkillCast(Dealer, 16010000, start + 100); // 오프닝 로테이션
        Hit(dm, start + 200, 3000);
        calc.GetDps();

        // 18분짜리 전투 — 그 사이 정리 임계(4,096건)를 여러 번 넘긴다.
        for (int i = 0; i < 9_000; i++)
        {
            dm.SaveSkillCast(Dealer, 16020000, start + 1_000 + (i * 120L));
        }

        clock[0] = start + 1_000 + (9_000 * 120L) + 1_000;
        Hit(dm, clock[0], 3000);
        calc.GetDps();
        dm.EndBattle(BossInstance);
        calc.GetDps();

        DpsLog saved = Assert.IsType<DpsLog>(dm.BattleLog(0));
        List<SkillCastRow> frozen = saved.Report.SkillCasts[Dealer];
        Assert.Contains(frozen, c => c.Name == "냉기 충격"); // 오프닝이 살아 있다
        Assert.Equal(9_001, frozen.Count);                  // 한 건도 안 잘렸다
    }

    [Fact]
    public void The_saved_report_freezes_the_timeline_so_a_history_replay_is_not_empty()
    {
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.MobHp(BossInstance, 5000);
        dm.StartBattle(BossInstance);
        dm.SaveSkillCast(Dealer, 16010000, clock[0] + 100);
        Hit(dm, clock[0] + 200, 3000);
        calc.GetDps();
        dm.SaveSkillCast(Dealer, 16020000, clock[0] + 1_200);
        Hit(dm, clock[0] + 1_300, 3000);
        calc.GetDps();

        clock[0] += 3_000;
        dm.EndBattle(BossInstance);
        calc.GetDps(); // end transition → saved

        DpsLog saved = Assert.IsType<DpsLog>(dm.BattleLog(0));
        List<SkillCastRow> frozen = saved.Report.SkillCasts[Dealer];
        Assert.Equal(["냉기 충격", "진공 폭발"], frozen.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void An_opener_cast_just_before_the_battle_anchor_is_still_in_the_window()
    {
        // 전투 시작 앵커는 첫 피해보다 앞으로 당겨지지 않으므로, 피해가 나기 전에 쓴 시전은 상시 그보다 앞선다.
        // 여유(PreemptivePacketWindowMs)가 없으면 모든 전투의 첫 스킬이 목록에서 빠진다.
        (DataManager dm, DpsCalculator calc, long[] clock) = Setup();
        dm.MobHp(BossInstance, 5000);
        dm.StartBattle(BossInstance);
        dm.SaveSkillCast(Dealer, 16010000, clock[0] - 400); // 오프너
        Hit(dm, clock[0] + 100, 3000);
        calc.GetDps();
        Hit(dm, clock[0] + 1_600, 3000); // 지속시간 0 인 전투는 애초에 기록되지 않는다
        calc.GetDps();
        clock[0] += 3_000;
        dm.EndBattle(BossInstance);
        calc.GetDps();

        DpsLog saved = Assert.IsType<DpsLog>(dm.BattleLog(0));
        Assert.Contains(saved.Report.SkillCasts[Dealer], c => c.Name == "냉기 충격");
    }
}
