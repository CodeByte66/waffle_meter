using WaffleMeter.App.Core;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 레이아웃 3종의 기하 불변식. App.Wpf 에는 테스트 프로젝트가 없어서 XAML 쪽 회귀를 잡아 줄 장치가
/// 하나도 없다 — 그래서 수치를 App.Core 로 끌어내려 여기서 잠근다. 이 파일이 유일한 자동 검증선이다.
/// </summary>
public class MeterLayoutTests
{
    /// <summary>
    /// 🔑 장식 대역 높이는 세 레이아웃에서 28/28/27 이어야 한다. 오늘 기본값(36 − 6 − 2 = 28)과 같은
    /// 값이라 <c>GaugeFxArt</c> 의 절대 상수를 손대지 않아도 장식 인상이 유지된다. 이 수치가 흔들리면
    /// 장식 기하를 함께 손봐야 하고, 그러면 rowHeight 를 조정해 둔 기존 사용자 화면까지 바뀐다.
    /// </summary>
    [Theory]
    [InlineData("battlefield", 36, 28.0)]
    [InlineData("dashboard", 28, 28.0)]
    [InlineData("stage", 34, 27.0)]
    public void FxBandHeight_stays_in_the_band_the_decoration_constants_were_tuned_for(
        string id, int rowHeight, double expected)
    {
        Assert.Equal(expected, MeterLayout.FxBandHeight(id, rowHeight), 3);
    }

    /// <summary>장식 대역은 어떤 행 높이에서도 음수가 되지 않는다(GaugeFxLayer 는 h&lt;=1 에서 조기 반환한다).</summary>
    [Fact]
    public void FxBandHeight_never_goes_negative_across_the_slider_range()
    {
        foreach (MeterLayout layout in MeterLayout.All)
        {
            for (int h = 24; h <= 80; h++)
            {
                Assert.True(MeterLayout.FxBandHeight(layout.Id, h) >= 0.0);
            }
        }
    }

    /// <summary>
    /// 장식 제외 폭이 행 왼쪽 클러스터의 실폭과 맞아야 한다. 값이 모자라면 입자가 순위칩·직업아이콘 뒤로
    /// 번져 반투명 스킨에서 배지를 물들이고, 과하면 짧은 바에서 장식이 통째로 잘린다.
    /// </summary>
    [Theory]
    // 🔑 전장 70 은 회귀 앵커다 — 이 값이 변하면 계산식이 틀린 것이다(기존 사용자 화면이 안 바뀌어야 한다).
    [InlineData("battlefield", 36, 70.0)] // rail 11 + 순위칩 28 + 아이콘(23+8)
    [InlineData("dashboard", 28, 22.0)]   // 레일 없음 + 맨 숫자 거터(13+9) — 예전엔 유령 rail 11 을 세고 있었다
    [InlineData("stage", 34, 0.0)]        // 점(7+9) − 게이지 인셋(점 몫 16) = 0. 게이지가 좌측 묶음 바로 뒤에서 시작한다
    public void GaugeExclusionLeft_matches_the_real_left_cluster(string id, int rowHeight, double expected)
    {
        Assert.Equal(expected, MeterLayout.GaugeExclusionLeft(id, rowHeight), 3);
    }

    /// <summary>
    /// 직업아이콘 크기는 XAML 의 <c>ConverterParameter='0.66:18'</c> 과 **같은 산술**이어야 한다.
    /// 반올림 방식이 갈리면(Round vs Floor) 장식 여백이 실물과 1~2px 어긋난다.
    /// </summary>
    [Theory]
    [InlineData(24, 18.0)] // floor(15.84)=15 → 하한 18 이 이긴다
    [InlineData(28, 18.0)] // floor(18.48)=18
    [InlineData(34, 22.0)] // floor(22.44)=22
    [InlineData(36, 23.0)] // floor(23.76)=23
    [InlineData(80, 52.0)] // floor(52.8)=52
    public void JobIconSize_mirrors_the_xaml_converter_arithmetic(int rowHeight, double expected)
    {
        Assert.Equal(expected, MeterLayout.JobIconSize(rowHeight), 3);
    }

    /// <summary>
    /// 제외 폭은 행 높이에 따라 단조 증가해야 한다 — 아이콘이 커지는데 여백이 줄면 그 구간에서 번진다.
    /// </summary>
    [Fact]
    public void GaugeExclusionLeft_grows_with_row_height()
    {
        foreach (MeterLayout layout in MeterLayout.All)
        {
            double previous = 0.0;
            for (int h = 24; h <= 80; h++)
            {
                double current = MeterLayout.GaugeExclusionLeft(layout.Id, h);
                Assert.True(current >= previous, $"{layout.Id} h={h}: {current} < {previous}");
                previous = current;
            }
        }
    }

    /// <summary>모르는 id·null 은 예외가 아니라 기본 레이아웃으로 떨어져야 한다(손으로 고친 설정 파일).</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("Battlefield")] // 대소문자 불일치도 폴백
    public void For_falls_back_to_the_default_layout(string? id)
    {
        Assert.Equal(MeterLayout.Battlefield, MeterLayout.For(id));
    }

    /// <summary>id 는 ASCII 여야 한다 — settings.properties 가 Latin-1 → EUC-KR 재디코드를 거치기 때문.</summary>
    [Fact]
    public void Ids_are_ascii_so_the_properties_round_trip_is_safe()
    {
        foreach (string id in MeterLayout.Ids)
        {
            Assert.All(id, c => Assert.InRange(c, ' ', '~'));
        }
    }

    [Fact]
    public void Ids_are_unique_and_the_default_is_one_of_them()
    {
        Assert.Equal(MeterLayout.Ids.Length, MeterLayout.Ids.Distinct().Count());
        Assert.Contains(MeterLayout.DefaultId, MeterLayout.Ids);
    }

    /// <summary>
    /// 카드 크롬이 없는 레이아웃은 반경·테두리·세로 패딩이 0 이어야 한다. 하나라도 남으면
    /// '배경 없음'인데 테두리만 뜨는 어중간한 상태가 된다.
    /// <para>⚠️ <c>CardMarginV</c> 는 여기 포함하지 않는다 — 그건 카드 <b>바깥</b>의 행 간격이라
    /// 크롬이 아니다. 크롬 없는 리본도 서로 딱 붙으면 답답해서 간격은 따로 가질 수 있다
    /// (계기판이 실제로 0 → 2 로 바뀌었고, 그때 이 테스트가 둘을 뭉뚱그린 걸 잡아냈다).</para>
    /// </summary>
    [Fact]
    public void Chromeless_layout_zeroes_every_card_dimension()
    {
        foreach (MeterLayout layout in MeterLayout.All.Where(l => !l.HasCardChrome))
        {
            Assert.Equal(0.0, layout.CardBorderV);
            Assert.Equal(0.0, layout.CardRadius);
            Assert.Equal(0.0, layout.CardPaddingV);
        }
    }

    /// <summary>행 간격은 어느 레이아웃에서도 음수가 아니어야 한다.</summary>
    /// <summary>
    /// 🔑 직업 점은 채움과 같은 색이라 채움 위에 올라가면 보이지 않는다. 전폭 블리드 레이아웃은
    /// 순위 숫자를 빼더라도 점 몫만큼은 반드시 게이지를 들여야 한다.
    /// </summary>
    [Fact]
    public void Full_bleed_layouts_keep_the_job_dot_off_the_fill()
    {
        foreach (MeterLayout l in MeterLayout.All.Where(x => x.GaugeFullBleedRight && x.ShowJobDot))
        {
            Assert.True(MeterLayout.GaugeInsetLeft(l) >= 16.0, l.Id);
        }
    }

    /// <summary>순위 숫자를 안 그리는 레이아웃은 그 거터도 0 이어야 한다 — 빈 칸이 남으면 안 된다.</summary>
    [Fact]
    public void Layouts_without_a_rank_numeral_reserve_no_gutter()
    {
        foreach (MeterLayout l in MeterLayout.All.Where(x => !x.ShowRankNumeral))
        {
            Assert.Equal(0.0, MeterLayout.BareRankGutter(l));
        }
    }

    [Fact]
    public void Row_gaps_are_never_negative()
    {
        Assert.All(MeterLayout.All, l => Assert.True(l.CardMarginV >= 0.0));
    }

    /// <summary>기본 행 높이는 설정 슬라이더 범위(24~80) 안이어야 한다 — 레이아웃 전환이 값을 덮으므로.</summary>
    [Fact]
    public void Default_row_heights_sit_inside_the_slider_range()
    {
        foreach (MeterLayout layout in MeterLayout.All)
        {
            Assert.InRange(layout.DefaultRowHeight, 24, 80);
        }
    }
}
