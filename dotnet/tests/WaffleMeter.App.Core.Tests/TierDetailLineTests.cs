using WaffleMeter.App.Core;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 전투상세 티어 타일의 문구 규칙.
/// <para>🔑 이 타일이 존재하는 이유는 세 번째 줄(비교 기준)이다. 미터 행은 파티원의 `상위 X.X%` 를 칩으로
/// 그리지만 폭이 모자라 그 숫자가 <b>전체 전투력 기준</b>인지 <b>그 사람의 전투력 구간 기준</b>인지는 툴팁에만
/// 있다. 각 행은 자기 전투력으로 밴드가 매겨지므로, 같은 백분위가 파티원마다 다른 뜻이 될 수 있다.</para>
/// </summary>
public sealed class TierDetailLineTests
{
    private const string Band = "전투력 700k–750k 미만 기준";
    private const string Whole = "전체 전투력 기준";

    [Fact]
    public void A_percentile_carries_its_comparison_basis()
    {
        TierDetailLine line = TierDetail.Build(
            new RowTier(3, 0.7, ComparisonBasis: Band), "챌린저");

        Assert.True(line.HasValue);
        Assert.Equal("이번 전투 등급", line.Label);
        Assert.Equal("챌린저", line.Rank);
        Assert.Equal("상위 0.7%", line.Percent);
        Assert.Equal(Band, line.Basis);
    }

    [Fact]
    public void The_whole_cohort_basis_is_spelled_out_too()
    {
        // "기준이 안 적혀 있으면 전체" 로 읽게 두면, 구간 기준일 때만 문장이 붙어 오히려 헷갈린다.
        TierDetailLine line = TierDetail.Build(
            new RowTier(5, 12.5, ComparisonBasis: Whole), "골드");

        Assert.Equal(Whole, line.Basis);
    }

    [Fact]
    public void A_career_tier_is_labelled_as_a_standing_not_as_this_fight()
    {
        // 두 값은 서로 다른 시점을 말한다 — 이번 주 성적과 이번 전투 결과를 같은 말로 부르면 안 된다.
        TierDetailLine line = TierDetail.Build(
            new RowTier(2, 1.2, IsCareer: true, ComparisonBasis: Whole), "마스터");

        Assert.Equal("이번 주 등급", line.Label);
    }

    [Fact]
    public void No_percentile_hides_the_basis_as_well()
    {
        // 기준은 백분위의 수식어지 독립 정보가 아니다. 홀로 남은 "전체 전투력 기준" 은 하지도 않은 측정을
        // 설명하는 문장이 된다(미터 푸터 칩이 같은 규칙을 쓴다).
        TierDetailLine line = TierDetail.Build(
            new RowTier(4, null, ComparisonBasis: Whole), "플래티넘");

        Assert.True(line.HasValue);
        Assert.Equal("플래티넘", line.Rank);
        Assert.Equal(string.Empty, line.Percent);
        Assert.Equal(string.Empty, line.Basis);
    }

    [Fact]
    public void No_tier_at_all_collapses_the_tile()
    {
        // '표본 부족'(분포에 행이 없음)과 '모집단 밖'(전투력 미확보 등)은 다른 상태다. 후자는 그릴 것이 없다.
        Assert.False(TierDetail.Build(null, "챌린저").HasValue);
    }

    [Fact]
    public void An_unreadable_rank_collapses_the_tile()
    {
        // 팔레트가 등급 이름을 못 주면(범위 밖 rank) 번호만 남은 타일을 그리느니 접는다.
        Assert.False(TierDetail.Build(new RowTier(0, 3.0, ComparisonBasis: Whole), null).HasValue);
        Assert.False(TierDetail.Build(new RowTier(0, 3.0, ComparisonBasis: Whole), string.Empty).HasValue);
    }

    [Fact]
    public void A_missing_basis_string_does_not_crash_the_line()
    {
        // ComparisonBasis 는 null 이 될 수 있다(평가가 값을 못 채운 경우). 백분위는 살리고 기준만 접는다.
        TierDetailLine line = TierDetail.Build(new RowTier(6, 30.0, ComparisonBasis: null), "실버");

        Assert.True(line.HasValue);
        Assert.Equal("실버", line.Rank);
        Assert.Equal(string.Empty, line.Basis);
    }
}
