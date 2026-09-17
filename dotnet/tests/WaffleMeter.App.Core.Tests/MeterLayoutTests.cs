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
    [InlineData("dashboard", 28, 51.0)]   // 카드패딩 8 + 맨 숫자 거터(13+4) + 아이콘(18+8)
    [InlineData("stage", 34, 41.0)]       // 카드패딩 11 + 아이콘(22+8) — 순위 숫자를 안 그린다
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
    /// 전폭 블리드 레이아웃은 채움이 **카드 가장자리까지** 깔린다 — 순위·이름은 그 위에 얹힌다.
    /// <para>한때 순위를 채움 밖으로 빼려고 채움을 오른쪽으로 밀었는데, 그게 "순위가 바깥에 있어
    /// 어색하다"의 원인이었다. 여백은 채움을 미는 게 아니라 글자를 들이는 것으로 만든다.</para>
    /// </summary>
    [Fact]
    public void Full_bleed_layouts_reach_the_card_edge()
    {
        foreach (MeterLayout l in MeterLayout.All)
        {
            double bleed = MeterLayout.GaugeBleedLeft(l);
            if (l.GaugeFullBleedRight)
            {
                Assert.Equal(-l.CardPaddingH, bleed, 3);
            }
            else
            {
                Assert.Equal(0.0, bleed, 3);
            }
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

    // ── 보스칸 높이(사용자 배율) ──────────────────────────────────────────────────
    // BossBarView 루트 Border 는 고정 높이 + ClipToBounds 다. 칸과 칸 안 내용이 **같은 배율**을 곱한다는
    // 불변식이 깨지면 글자가 잘린 채 조용히 렌더되고, 그건 화면을 봐야만 보인다 — App.Wpf 에는 그걸
    // 잡아 줄 테스트가 없으므로 여기서 잠근다.

    /// <summary>100% 는 출고 높이와 **정확히** 같아야 한다. 기본값 사용자의 화면이 1px 도 변하면 안 된다.</summary>
    [Theory]
    [InlineData(BossStyle.Band, 104.0)]
    [InlineData(BossStyle.Readout, 52.0)]
    [InlineData(BossStyle.Canvas, 70.0)]
    public void Boss_slot_at_100_percent_is_exactly_the_shipped_height(BossStyle style, double expected)
    {
        Assert.Equal(expected, MeterLayout.BossNaturalHeight(style), 6);
        Assert.Equal(expected, MeterLayout.BossSlotHeight(style, MeterLayout.BossScaleDefault), 6);
    }

    /// <summary>
    /// 🔑 내용이 들어갈 자리는 어느 배율에서도 "기준 자리 × 배율" 이어야 한다. 내용은 배율을 곱하고
    /// 세로 크롬(테두리 1px 위아래)은 안 곱하므로, 칸 높이도 크롬을 뺀 뒤 곱해야 비율이 보존된다 —
    /// 전체를 그냥 곱하면 축소할수록 크롬 몫이 상대적으로 커져 여유가 없는 계기판에서 아랫줄이 잘린다.
    /// </summary>
    [Theory]
    [InlineData(BossStyle.Band)]
    [InlineData(BossStyle.Readout)]
    [InlineData(BossStyle.Canvas)]
    public void Boss_slot_keeps_the_same_room_for_its_contents_at_every_scale(BossStyle style)
    {
        double room100 = MeterLayout.BossNaturalHeight(style) - MeterLayout.BossChromeHeight;
        for (int percent = MeterLayout.BossScaleMin; percent <= MeterLayout.BossScaleMax; percent++)
        {
            double room = MeterLayout.BossSlotHeight(style, percent) - MeterLayout.BossChromeHeight;
            Assert.Equal(room100 * MeterLayout.BossScale(percent), room, 6);
        }
    }

    /// <summary>슬라이더 밖 값(수기 편집·남의 공유코드)은 범위로 접힌다.</summary>
    [Fact]
    public void Boss_scale_clamps_outside_the_slider_range()
    {
        Assert.Equal(MeterLayout.BossScaleMin / 100.0, MeterLayout.BossScale(1), 6);
        Assert.Equal(MeterLayout.BossScaleMax / 100.0, MeterLayout.BossScale(9999), 6);
        Assert.Equal(1.0, MeterLayout.BossScale(MeterLayout.BossScaleDefault), 6);
    }

    /// <summary>슬라이더를 오른쪽으로 끌면 칸은 반드시 커진다(어느 레이아웃에서도).</summary>
    [Fact]
    public void Boss_slot_height_grows_with_the_slider()
    {
        foreach (MeterLayout l in MeterLayout.All)
        {
            for (int p = MeterLayout.BossScaleMin; p < MeterLayout.BossScaleMax; p++)
            {
                Assert.True(MeterLayout.BossSlotHeight(l.BossStyle, p)
                    < MeterLayout.BossSlotHeight(l.BossStyle, p + 1));
            }
        }
    }

    /// <summary>기본 배율은 슬라이더 범위 안에 있고, 양 끝에서도 칸이 사라지지 않는다.</summary>
    [Fact]
    public void Boss_scale_default_sits_inside_the_slider_range()
    {
        Assert.InRange(MeterLayout.BossScaleDefault, MeterLayout.BossScaleMin, MeterLayout.BossScaleMax);
        foreach (MeterLayout l in MeterLayout.All)
        {
            Assert.True(MeterLayout.BossSlotHeight(l.BossStyle, MeterLayout.BossScaleMin) > 0.0);
            Assert.True(MeterLayout.BossSlotHeight(l.BossStyle, MeterLayout.BossScaleMax) > 0.0);
        }
    }

    // ── 설정창 레이아웃 프리뷰 패널이 읽는 두 목록 ──────────────────────────────
    // 문구를 손으로 적지 않고 스펙에서 파생하기로 한 결정을 여기서 잠근다. 파생이 끊기면(누가 리터럴로
    // 되돌리면) 설명만 옛 모습으로 남는데, 그 어긋남은 화면을 봐도 안 보인다 — 설명이 그럴듯해서 오히려
    // 틀린 쪽을 믿게 된다.

    /// <summary>모든 레이아웃이 모든 축에 대해 말을 한다. 빈 칸은 "이 레이아웃은 이걸 안 한다"가 아니라
    /// 그냥 설명이 빠진 것이고, 사용자에겐 구분이 안 된다.</summary>
    [Fact]
    public void Every_layout_describes_every_axis()
    {
        foreach (MeterLayout l in MeterLayout.All)
        {
            IReadOnlyList<MeterLayout.LayoutNote> traits = MeterLayout.TraitsOf(l);
            Assert.NotEmpty(traits);
            Assert.All(traits, n =>
            {
                Assert.False(string.IsNullOrWhiteSpace(n.Label));
                Assert.False(string.IsNullOrWhiteSpace(n.Detail));
            });

            // 같은 축을 두 번 설명하지 않는다.
            Assert.Equal(traits.Count, traits.Select(n => n.Label).Distinct(StringComparer.Ordinal).Count());
        }
    }

    /// <summary>세 레이아웃의 설명이 서로 달라야 패널이 의미를 갖는다. 전부 같은 글이 뜨면 패널은 장식이다.</summary>
    [Fact]
    public void Layout_descriptions_actually_differ()
    {
        string[] rendered = MeterLayout.All
            .Select(l => string.Join("|", MeterLayout.TraitsOf(l).Select(n => $"{n.Label}={n.Detail}")))
            .ToArray();

        Assert.Equal(rendered.Length, rendered.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>잠금 목록은 스펙 플래그와 1:1 이다 — 목록과 실제 비활성화가 어긋나면
    /// "잠긴다고 적혀 있는데 눌리는" 상태가 된다(설정창 쪽은 UiPreview 하네스가 같은 등식을 확인한다).</summary>
    [Fact]
    public void Locks_follow_the_spec_flags()
    {
        foreach (MeterLayout l in MeterLayout.All)
        {
            IReadOnlyList<MeterLayout.LayoutNote> locks = MeterLayout.LocksOf(l);
            Assert.Equal(l.RequiresFillGauge, locks.Any(n => n.Label == "게이지 형태"));
            Assert.Equal(!l.ShowServerTag, locks.Any(n => n.Label == "서버 표시"));
            Assert.All(locks, n => Assert.False(string.IsNullOrWhiteSpace(n.Detail)));
        }
    }

    /// <summary>전장은 아무것도 빼앗지 않는 기준선이다 — 여기서 잠기는 게 생기면 "기본 UI 로 돌아가면
    /// 다시 고를 수 있다"는 다른 잠금들의 안내가 통째로 거짓이 된다.</summary>
    [Fact]
    public void The_baseline_layout_locks_nothing()
    {
        Assert.Empty(MeterLayout.LocksOf(MeterLayout.Battlefield));
    }
}
