using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// <see cref="DpsReport.TargetInstanced"/>가 <b>전투 종료 틱을 넘어서도 유지</b>되는지 고정한다(detail-history#1).
/// <para>이 플래그는 로스터 구제의 <b>'여기는 파티 씬이다' 증거</b>다 — 인스턴스에는 외부인이 없으므로,
/// 표시 계층은 <c>report.TargetInstanced</c>를 보고 무명 행에 파티원 이름을 붙여도 되는지 판단한다.
/// 종료 틱에서 <c>RefreshRecentReportFromCache</c>가 <b>새 리포트</b>로 <c>_recentData</c>를 통째로 교체하는데
/// 이 필드가 초기화 목록에 없으면 기본값 false 로 떨어지고, 대기 화면에 계속 나가는 게 그 객체라 false 가
/// 고정된다. 결과: <b>보스가 죽는 바로 그 틱</b>에 구제된 행이 사라지고 보이는 비중 합이 100%에 못 미친다 —
/// 사용자가 결과를 읽는 순간에 행이 줄어든다. 데이터는 멀쩡하고 게이트만 꺼진 것이라 증상이 표시에만 난다.</para>
/// </summary>
public sealed class TargetInstancedSurvivesBattleEndTests
{
    private const int Instance = 100;
    private const int InstancedBossCode = 2300334; // content-types에 분류된 초월 보스
    private const int FieldBossCode = 2300473;     // 분류 밖 — 필드보스 대역
    private const int Me = 5001;

    private static (DataManager Dm, DpsCalculator Calc, long[] Now) Fight(int bossCode, bool classified)
    {
        long[] now = { 1_000_000 };
        var dm = new DataManager { Clock = () => now[0] };
        dm.LoadMobs(new Dictionary<int, Mob> { [bossCode] = new Mob(bossCode, "보스", Boss: true) });
        if (classified)
        {
            dm.LoadContentTypes(new Dictionary<int, string> { [bossCode] = "transcendence" });
        }

        dm.SaveNickname(Me, "본인", isExecutor: true, server: 2003, jobByte: 34);
        dm.SaveMobId(Instance, bossCode);
        dm.MobHp(Instance, 5_000);
        dm.StartBattle(Instance);
        return (dm, new DpsCalculator(dm), now);
    }

    [Fact]
    public void The_instanced_flag_survives_the_end_tick_and_the_standby_report()
    {
        (DataManager dm, DpsCalculator calc, long[] now) = Fight(InstancedBossCode, classified: true);
        dm.SaveDamage(
            new ParsedDamagePacket
            {
                ActorId = Me, TargetId = Instance, Damage = 3_000, SkillCode = 16080000, Timestamp = now[0] + 100,
            },
            dm.CurrentEpoch());

        Assert.True(calc.GetDps().TargetInstanced); // 진행 중

        now[0] += 3_000;
        dm.EndBattle(Instance);
        Assert.True(calc.GetDps().TargetInstanced); // ← 회귀 지점: 보스가 죽는 그 틱

        Assert.True(calc.GetDps().TargetInstanced); // 대기 화면에 계속 나가는 같은 객체
    }

    [Fact]
    public void An_unclassified_field_boss_never_gains_the_flag()
    {
        // 이월이 "무조건 true 로 굳힌다"가 되면 안 된다 — 분류 밖 필드보스에서 우회가 열리면 낯선 무명 zerg
        // 행이 미터에 올라온다. 종료 틱에서도 몹 코드로 다시 묻는다.
        (DataManager dm, DpsCalculator calc, long[] now) = Fight(FieldBossCode, classified: false);
        dm.SaveDamage(
            new ParsedDamagePacket
            {
                ActorId = Me, TargetId = Instance, Damage = 3_000, SkillCode = 16080000, Timestamp = now[0] + 100,
            },
            dm.CurrentEpoch());

        Assert.False(calc.GetDps().TargetInstanced);

        now[0] += 3_000;
        dm.EndBattle(Instance);
        Assert.False(calc.GetDps().TargetInstanced);
    }
}
