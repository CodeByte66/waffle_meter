using WaffleMeter.Capture;
using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>
/// 어비스 필드보스 군중전(수백 명)의 표시 규칙. 그 밖의 전투(던전·공대·시련·다른 지역 필드보스)는 이 판정에서
/// <b>항상 false</b> 라 종전 동작(1.5배 여유 상한, 파티 DPS 기반 ETA)을 그대로 쓴다.
/// <para>왜 따로 다루나: 군중전에서 서버는 남의 직접타를 보내지 않는다(실측 2026-10-27 카이라 — DoT 만 보임).
/// 게다가 진영 보상은 막타가 가른다. 그래서 순위·기여도는 의미가 없고(오너 결정: 10행이면 충분), '남은 HP ÷ 보이는
/// 파티 DPS' ETA 는 실제의 몇 배로 나온다 — ETA 는 <see cref="KillEtaEstimator"/> 가 보스 HP 기울기로 잰다.</para>
/// <para>판정은 <see cref="FieldBossCatalog.Region"/> == <see cref="FieldBossRegion.Abyss"/> 하나다.
/// ⚠️ 몹 코드 접두(2600xxx 등)로 판정하지 마라 — 접두 규칙은 이 저장소가 금지한다(encounter-mapping-rules).
/// ⚠️ 상한을 던전·공대로 넓히지 마라 — 10인 공대는 여유 상한이 있어야 파티원이 안 잘린다.</para>
/// </summary>
public static class CrowdFightPolicy
{
    /// <summary>어비스 필드보스 표시 상한. 오너 결정(2026-10-27).</summary>
    public const int CrowdRowCap = 10;

    /// <summary>지금 대상이 어비스 필드보스인가.</summary>
    public static bool IsAbyssFieldBoss(DpsReport report) =>
        report.Target?.Mob is { } mob && FieldBossCatalog.Region(mob.Code) == FieldBossRegion.Abyss;

    /// <summary>OverlayRowBuilder 에 넘길 topN. 어비스 필드보스면 사용자가 고른 인원(최대 10)을 그대로 쓴다 — 1.5배
    /// 여유는 '로스터 미확정 파티원이 낯선 사람에게 밀리는' 던전 사고용이라 군중전에서는 낯선 사람 줄만 더 보탠다.
    /// 본인과 확인된 파티원은 어느 쪽이든 상한에서 면제된다(OverlayRowBuilder).</summary>
    public static int RowCap(DpsReport report, MeterSettings settings) =>
        IsAbyssFieldBoss(report)
            ? Math.Min(settings.EffectiveMaxVisibleRows, CrowdRowCap)
            : settings.DisplayRowCap;
}
