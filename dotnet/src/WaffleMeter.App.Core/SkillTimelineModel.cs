using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>타임라인의 한 줄 = 시전 1회.</summary>
/// <param name="OffsetMs">전투 시작 기준 경과 ms. <b>음수일 수 있다</b> — 전투 시작 앵커는 첫 피해보다 앞으로
/// 당겨지지 않으므로 오프너(피해가 나기 전에 쓴 버프·이동기)는 상시 그보다 앞선다. 0으로 접으면 첫 몇 개가
/// 같은 시각에 겹쳐 보이므로 접지 않는다.</param>
/// <param name="GapFromPrevMs">직전 시전(스킬 무관)과의 간격. 첫 줄은 null.</param>
/// <param name="StartsCooldown">이 발동이 쿨타임을 돌렸는가. <b>참이면 확실히 나간 시전</b>이고, 거짓이라고
/// 안 나간 것은 아니다 — 쿨타임이 없는 스킬은 애초에 돌릴 쿨이 없다(실측: 전체 발동의 17.5%만 참).</param>
public sealed record SkillCastEntry(
    long OffsetMs, int Code, string Name, long? GapFromPrevMs, bool StartsCooldown = false);

/// <summary>스킬 한 종류의 사용 간격 집계. 간격은 <b>같은 스킬의 연속 시전 사이</b>이므로 표본 수는
/// <see cref="Count"/> − 1 이고, 한 번만 쓴 스킬은 간격이 전부 null 이다.</summary>
/// <param name="CooldownStartCount">그중 쿨타임을 돌린 발동 수. 쿨타임이 있는 스킬이라면 이 수가 곧
/// "진짜 나간 횟수"다. 쿨타임이 없는 스킬은 0으로 남는다 — 0이 곧 문제라는 뜻이 아니다.</param>
public sealed record SkillCastSummaryRow(
    int Code,
    string Name,
    int Count,
    long? MeanGapMs,
    long? MinGapMs,
    long? MaxGapMs,
    long? MedianGapMs,
    int CooldownStartCount = 0);

/// <summary>
/// 스킬 타임라인 탭의 모델. 시전 목록(간격 포함)과 스킬별 간격 집계, 그리고 한 줄 요약 숫자들.
/// <para><b>전역 지표 두 개(<see cref="MedianGapMs"/>·<see cref="MeanGapMs"/>)를 로테이션 속도로 읽지 마라.</b>
/// 실제 코퍼스 재생 실측(5인 파티, 궁성 2,142시전/145초): 중앙값 50ms, 평균 67ms. 한 입력이 연계 스킬을
/// 동시에 여러 개 내보내기 때문에(표적 화살 + 바이젤의 권능이 같은 ms) 인접 간격의 절반가량이 100ms 미만이고,
/// 중앙값도 평균도 그 군집 위에 앉는다. 두 수는 "입력 밀도"이지 "스킬을 얼마나 자주 누르나"가 아니다.
/// 사용자가 물은 "스킬별 평균 간격"에 답하는 것은 <see cref="Skills"/> 쪽이다 — 거기는 같은 스킬끼리만
/// 재므로 이 군집이 끼지 않는다(같은 실측에서 송곳 화살 449ms, 속사 508ms, 지원 사격 708ms).</para>
/// <para>순수 계산이라 App.Core 에 둔다 — App.Wpf 에는 테스트 프로젝트가 없다.</para>
/// </summary>
public sealed record SkillTimelineModel(
    IReadOnlyList<SkillCastEntry> Casts,
    IReadOnlyList<SkillCastSummaryRow> Skills,
    long? MedianGapMs,
    long? MeanGapMs)
{
    public static readonly SkillTimelineModel Empty = new([], [], null, null);

    /// <param name="casts">시각 오름차순일 필요는 없다 — 여기서 다시 정렬한다.</param>
    /// <param name="battleStart">오프셋의 기준(절대 ms).</param>
    public static SkillTimelineModel Compute(IReadOnlyList<SkillCastRow> casts, long battleStart)
    {
        if (casts.Count == 0)
        {
            return Empty;
        }

        List<SkillCastRow> ordered = casts
            .OrderBy(c => c.TimestampMs)
            .ThenBy(c => c.Code)
            .ToList();

        var entries = new List<SkillCastEntry>(ordered.Count);
        var globalGaps = new List<long>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            long? gap = i == 0 ? null : ordered[i].TimestampMs - ordered[i - 1].TimestampMs;
            if (gap is long g)
            {
                globalGaps.Add(g);
            }

            entries.Add(new SkillCastEntry(
                ordered[i].TimestampMs - battleStart, ordered[i].Code, ordered[i].Name, gap,
                ordered[i].StartsCooldown));
        }

        // 스킬별 집계는 <b>이름</b>으로 묶는다. 0x3802 는 특화 접미가 붙은 원본 코드를 싣기 때문에 코드로 묶으면
        // 같은 스킬이 특화 조합마다 다른 행으로 흩어진다 — 상세의 스킬 표가 이름으로 병합하는 것과 같은 이유다.
        var rows = new List<SkillCastSummaryRow>();
        foreach (IGrouping<string, SkillCastRow> group in ordered.GroupBy(c => c.Name))
        {
            List<SkillCastRow> uses = group.ToList();
            var gaps = new List<long>(Math.Max(uses.Count - 1, 0));
            for (int i = 1; i < uses.Count; i++)
            {
                gaps.Add(uses[i].TimestampMs - uses[i - 1].TimestampMs);
            }

            rows.Add(new SkillCastSummaryRow(
                uses[0].Code,
                group.Key,
                uses.Count,
                gaps.Count > 0 ? (long)Math.Round(gaps.Average(), MidpointRounding.AwayFromZero) : null,
                gaps.Count > 0 ? gaps.Min() : null,
                gaps.Count > 0 ? gaps.Max() : null,
                Median(gaps),
                uses.Count(u => u.StartsCooldown)));
        }

        // 많이 쓴 스킬이 위로. 같은 횟수면 이름으로 안정 정렬해 매 틱 순서가 흔들리지 않게 한다.
        List<SkillCastSummaryRow> skills = rows
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        return new SkillTimelineModel(
            entries,
            skills,
            Median(globalGaps),
            globalGaps.Count > 0 ? (long)Math.Round(globalGaps.Average(), MidpointRounding.AwayFromZero) : null);
    }

    private static long? Median(List<long> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        long[] sorted = values.ToArray();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (long)Math.Round((sorted[mid - 1] + sorted[mid]) / 2.0, MidpointRounding.AwayFromZero);
    }
}
