namespace WaffleMeter.App.Core;

/// <summary>전투상세 창의 티어 타일에 그릴 세 조각. <see cref="HasValue"/> 가 false 면 타일 자체를 접는다.</summary>
/// <param name="HasValue">그릴 것이 있는가. 티어 정보가 없거나(전투력 미확보·미지원 보스) 등급을 못 읽으면 false.</param>
/// <param name="Label">타일 제목 — `이번 주 등급` / `이번 전투 등급`. 두 값은 서로 다른 시점을 말하므로 뭉개면 안 된다.</param>
/// <param name="Rank">등급 이름 — <c>챌린저</c>.</param>
/// <param name="Percent">이번 전투 백분위 — <c>상위 0.7%</c>. 표본이 없으면 빈 문자열.</param>
/// <param name="Basis">그 백분위가 무엇과 비교한 값인지 — <c>전체 전투력 기준</c> / <c>전투력 700k–750k 미만 기준</c>.
/// 백분위가 없으면 빈 문자열이다(수식할 대상이 없다).</param>
public readonly record struct TierDetailLine(bool HasValue, string Label, string Rank, string Percent, string Basis)
{
    public static readonly TierDetailLine None = new(false, string.Empty, string.Empty, string.Empty, string.Empty);
}

/// <summary>
/// 전투상세의 티어 타일 문구를 만든다.
///
/// <para>이 타일이 존재하는 이유는 <b>비교 기준</b> 하나다. 미터 행은 파티원의 `상위 X.X%` 를 칩으로 그리지만
/// 폭이 490px 이라 두 번째 줄을 놓을 자리가 없어, 그 숫자가 <b>전체 전투력 기준</b>인지 <b>그 사람의 전투력
/// 구간 기준</b>인지는 툴팁에만 있다. 각 행은 자기 전투력으로 밴드가 매겨지므로 파티원은 나와 다른 풀과
/// 비교될 수 있고, 같은 "상위 3%" 가 장비가 비슷한 사람들 사이에서 나온 것인지 전체에서 나온 것인지에 따라
/// 뜻이 완전히 달라진다. 상세창은 그 문장을 마우스를 올리지 않고 읽을 수 있는 유일한 자리다.</para>
///
/// <para>순수 함수로 App.Core 에 둔다 — App.Wpf 에는 테스트 프로젝트가 없어서, 이 판정이 저기 있으면
/// 커버리지가 0이 된다(같은 이유로 미터 행의 <c>TierRowInfo.Build</c> 는 지금도 테스트가 없다).</para>
/// </summary>
public static class TierDetail
{
    /// <param name="tier">그 캐릭터의 티어. null 이면 애초에 모집단 밖이다(전투력 미확보 등) — `표본 부족`과
    /// 구분해야 할 상태이므로 타일을 통째로 접는다.</param>
    /// <param name="tierName">등급 이름. 팔레트가 App.Wpf 에 있어 호출자가 풀어서 넘긴다. 빈 값이면 등급을
    /// 못 읽은 것이므로 역시 접는다.</param>
    public static TierDetailLine Build(RowTier? tier, string? tierName)
    {
        if (tier is not RowTier t || string.IsNullOrEmpty(tierName))
        {
            return TierDetailLine.None;
        }

        string percent = t.BattleTopPercent is double p ? TierLadder.FormatTopPercent(p) : string.Empty;

        // 백분위가 없으면 기준도 함께 숨긴다. 기준은 백분위의 수식어지 독립 정보가 아니라서, 홀로 남은
        // "전체 전투력 기준" 은 하지도 않은 측정을 설명하는 문장이 된다(미터 푸터 칩이 같은 규칙을 쓴다).
        string basis = percent.Length > 0 ? t.ComparisonBasis ?? string.Empty : string.Empty;

        // 등급과 백분위를 합쳐 한 줄로 담지 않는다 — 타일 실사용 폭이 최소 창폭에서 ~95px 이라
        // "챌린저 · 상위 0.7%" 가 잘린다(실측). 줄을 나누면 어느 쪽도 안 잘린다.
        return new TierDetailLine(
            true,
            // 파티원은 언제나 이번 전투 등급이고(커리어 티어는 본인 것만 서버에서 온다), 본인도 기록 재생에서는
            // 이번 전투 등급이다 — 그 구분을 라벨이 직접 말해야 한다. 커리어 티어는 매주 수요일에 초기화되는
            // 주간 성적이다(비교 모집단만 최근 30일). 그래서 '누적'이 아니라 '이번 주'라고 부른다.
            t.IsCareer ? "이번 주 등급" : "이번 전투 등급",
            tierName,
            percent,
            basis);
    }
}
