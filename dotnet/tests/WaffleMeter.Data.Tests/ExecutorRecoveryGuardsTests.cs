using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 로스터 1:1 본인 복구(<c>DpsCalculator.TryRecoverExecutorFromRoster</c>)의 <b>가드</b>를 고정한다.
/// <para>두 "exactly one"(미청구 이름 하나 ↔ 무명 하나)만으로는 부족하다. 미청구 이름이 본인인 것은 확실하지만
/// — executor==0 이면 본인 닉을 아무 uid도 들고 있지 않다 — <b>그 무명 행이 본인인지</b>는 확실하지 않다.
/// 본인이 아직 딜을 안 넣은 창(전투 시작 직후 수백 ms~수 초)에서는 남의 행이 그 자리를 차지하고, 이 경로는
/// 표시 전용 복사본이 아니라 <c>SaveNickname(isExecutor: true)</c>로 신원 저장소에 <b>영구 기록</b>한다 —
/// 자기색·버프 오버레이 게이트·업로드의 본인 성적이 전부 그 uid를 따라가고, 그 행은 퍼지에서도 제외된다.</para>
/// <para><see cref="ExecutorRecoveryFromRosterTests"/>가 '무명 둘'·'미청구 둘' 같은 모호성을 막고,
/// 이 파일은 <b>1:1 이 성립해도</b> 거부해야 하는 조합을 막는다(identity-roster~S2 / #4).</para>
/// </summary>
public sealed class ExecutorRecoveryGuardsTests
{
    private const int Instance = 100;
    private const int BossCode = 2301008;
    private const int Me = 5001;       // 본인 — 게임이 "이 uid가 너다"를 말해 준 적 없다
    private const int Mate = 5002;     // 파티원 (0x3645로 이름이 붙었다)
    private const int Stranger = 5003; // 파티 밖의 이름 달린 딜러 — 여기가 공개 씬이라는 증거
    private const int Nobody = 5004;   // 파티 밖의 <b>무명</b> 행 (남의 캐릭/펫/지나가던 엔티티)
    private const int RangerSkill = 16080000; // job-locked: 무명 액터가 플레이어 행으로 등록되게 하는 조건

    private static (DataManager Dm, DpsCalculator Calc) Fight()
    {
        var dm = new DataManager { Clock = () => 1_000_000 };
        dm.LoadMobs(new Dictionary<int, Mob> { [BossCode] = new Mob(BossCode, "보스", Boss: true) });
        dm.SaveMobId(Instance, BossCode);
        dm.MobHp(Instance, 100_000);
        dm.StartBattle(Instance);
        return (dm, new DpsCalculator(dm));
    }

    private static void Hit(DataManager dm, int actor, int damage = 1000) =>
        dm.SaveDamage(
            new ParsedDamagePacket
            {
                ActorId = actor,
                TargetId = Instance,
                Damage = damage,
                SkillCode = RangerSkill,
                Timestamp = 1_000_100,
            },
            dm.CurrentEpoch());

    [Fact]
    public void A_named_outsider_proves_a_public_scene_so_the_one_to_one_match_is_refused()
    {
        // 필드보스 zerg. 로스터는 [나, 동료]이고 동료는 이미 이름이 붙어 '청구됨' → 미청구는 "나" 하나.
        // 이 틱에 본인은 아직 딜을 안 넣었고, 딜을 넣은 무명은 <b>낯선 사람</b>(Nobody) 하나뿐이라 1:1이 성립한다.
        // 가드가 없으면 그 낯선 uid가 본인 닉네임·색·IsExecutor로 영구 확정된다.
        (DataManager dm, DpsCalculator calc) = Fight();
        dm.SavePartyRoster([("나", 2003, 1), ("동료", 2003, 2)]);
        dm.SaveNickname(Mate, "동료", isExecutor: false, server: 2003, jobByte: 32);
        dm.SaveNickname(Stranger, "낯선이", isExecutor: false, server: 1005, jobByte: 32); // 파티에 없는 이름 달린 딜러

        Hit(dm, Mate);
        Hit(dm, Stranger);
        Hit(dm, Nobody); // 무명 하나 — 본인이 아니다
        DpsReport report = calc.GetDps();

        Assert.Equal(0, report.ExecutorId);
        User bare = Assert.Single(report.Contributors, u => u.Id == Nobody);
        Assert.True(string.IsNullOrEmpty(bare.Nickname)); // 남의 행이 내 이름으로 칠해지지 않는다
        Assert.False(bare.IsExecutor);
    }

    [Fact]
    public void A_party_dungeon_with_no_outsider_still_recovers()
    {
        // 위와 같은 모양이되 이름 달린 딜러가 전부 파티원 — 진짜 던전 재인스턴스다. 외부인 가드가 정상 경로를
        // 막으면 안 된다(가드를 넣은 대가로 기능이 죽지 않았음을 고정한다).
        (DataManager dm, DpsCalculator calc) = Fight();
        dm.SavePartyRoster([("나", 2003, 1), ("동료", 2003, 2)]);
        dm.SaveNickname(Mate, "동료", isExecutor: false, server: 2003, jobByte: 32);

        Hit(dm, Mate);
        Hit(dm, Me);
        DpsReport report = calc.GetDps();

        Assert.Equal(Me, report.ExecutorId);
        Assert.Equal("나", Assert.Single(report.Contributors, u => u.Id == Me).Nickname);
    }

    [Fact]
    public void A_trace_damage_nameless_row_is_never_promoted_to_self()
    {
        // 스치듯 딜을 넣은 무명 행(펫/NPC/지나가던 엔티티)이 "무명 하나"가 되는 순간을 본인으로 확정하지 않는다.
        // 표시층과 같은 문턱(1등 딜러의 20%)·같은 지표(RAW 피해량).
        (DataManager dm, DpsCalculator calc) = Fight();
        dm.SavePartyRoster([("나", 2003, 1), ("동료", 2003, 2)]);
        dm.SaveNickname(Mate, "동료", isExecutor: false, server: 2003, jobByte: 32);

        Hit(dm, Mate, damage: 1_000_000);
        Hit(dm, Nobody, damage: 500); // 0.05% — 1등의 20% 한참 아래
        DpsReport report = calc.GetDps();

        Assert.Equal(0, report.ExecutorId);
        Assert.True(string.IsNullOrEmpty(Assert.Single(report.Contributors, u => u.Id == Nobody).Nickname));
    }

    [Fact]
    public void A_known_summon_is_never_promoted_to_self()
    {
        // identity-roster#4: 소유자 맵(0x3641)이 늦게 와 무명 플레이어 행으로 등록된 소환수. 지금은 바로 앞의
        // PurgeResolvedNonPlayers 가 먼저 걷어 내지만, 그 순서가 바뀌어도 남의 소환수 딜이 내 성적으로
        // 표시·업로드되지 않아야 한다(그 뒤 전투부터는 own_result_missing 으로 조용히 스킵된다).
        (DataManager dm, DpsCalculator calc) = Fight();
        dm.SavePartyRoster([("나", 2003, 1), ("동료", 2003, 2)]);
        dm.SaveNickname(Mate, "동료", isExecutor: false, server: 2003, jobByte: 32);

        Hit(dm, Mate);
        Hit(dm, Nobody);          // 소환수가 소유자 맵보다 먼저 때렸다 → 무명 임시 행
        dm.SaveSummon(Nobody, Mate); // 뒤늦게 도착한 0x3641: 이건 동료의 소환수다
        DpsReport report = calc.GetDps();

        Assert.Equal(0, report.ExecutorId);
        Assert.DoesNotContain(report.Contributors, u => u.Id == Nobody && u.IsExecutor);
    }
}
