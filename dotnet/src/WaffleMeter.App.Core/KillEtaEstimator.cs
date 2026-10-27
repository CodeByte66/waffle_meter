using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>
/// 보스칸 '처치까지' 숫자. 어비스 필드보스(<see cref="CrowdFightPolicy.IsAbyssFieldBoss"/>)는 보스 HP 기울기
/// (<see cref="BossHpSlope"/>), 그 밖의 전투는 종전 그대로 '남은 HP ÷ 지금 파티 DPS'(<see cref="PartyDpsEta"/>).
/// <para>기울기 표본은 <b>라이브 리포트</b>로만 쌓는다. 기록에서 연 저장 전투는 HP 한 점으로 얼어 있어 기울기가
/// 없으므로 어비스 필드보스면 숨기고, 라이브 표본도 건드리지 않는다(재생을 닫고 돌아왔을 때 이어서 잰다).</para>
/// <para>UI 스레드 전용, 상태 있음 — 표시 계층(OverlayViewModel)이 한 인스턴스를 든다.</para>
/// </summary>
public sealed class KillEtaEstimator
{
    private readonly BossHpSlope _slope = new();

    /// <summary>처치까지 남은 ms, 또는 숨길 때 null. <paramref name="liveFeed"/> 는 이 리포트가 라이브 경로에서
    /// 왔는지(기록 재생이면 false), <paramref name="nowMs"/> 는 관측 시각(벽시계 ms).</summary>
    public long? Estimate(DpsReport report, bool liveFeed, long nowMs)
    {
        if (report.Target is not { } target)
        {
            if (liveFeed)
            {
                _slope.Reset();
            }

            return null;
        }

        if (!CrowdFightPolicy.IsAbyssFieldBoss(report))
        {
            if (liveFeed)
            {
                _slope.Reset();
            }

            return PartyDpsEta(report);
        }

        return liveFeed
            ? _slope.Observe(target.Id, target.Mob.Code, target.RemainHp, target.MaxHp, nowMs)
            : null;
    }

    /// <summary>종전 공식: 남은 HP ÷ 지금 파티 DPS. 파티 DPS 나 남은 HP 가 0 이면 null.
    /// ⚠️ 보스 최대 HP 가 틀리면(포화·미측정) 이 값도 같이 틀린다 — 그래서 남은 HP 를 모르는 상황에서는 아예
    /// 감춘다. 틀린 값을 자신 있게 보여주는 것보다 안 보여주는 게 낫다.</summary>
    public static long? PartyDpsEta(DpsReport report)
    {
        if (report.Target is not { } target)
        {
            return null;
        }

        double partyDps = report.Information.Values.Sum(i => i.Dps);
        return partyDps > 0 && target.RemainHp > 0
            ? (long)(target.RemainHp / partyDps * 1000.0)
            : null;
    }
}
