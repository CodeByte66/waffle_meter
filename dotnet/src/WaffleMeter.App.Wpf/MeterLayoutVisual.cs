using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using WaffleMeter.App.Core;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// <see cref="MeterLayout"/> 의 순수 수치를 XAML 이 바로 물 수 있는 WPF 타입으로 감싼 어댑터.
///
/// <para>⚠️ <b>Brush·Color 를 절대 싣지 않는다.</b> 색은 전부 <c>DynamicResource Skin.*</c> 로 남아야
/// 스킨 4종 교체를 따라간다 — 레이아웃이 색을 들고 있으면 스킨을 바꿔도 안 따라오는 스냅샷이 된다
/// (<c>ReplayWindow</c> 가 <c>TryFindResource</c> 1회성 스냅샷으로 같은 함정을 밟은 전례가 있다).</para>
///
/// <para>(id, rowHeight) 로 캐시한다. 값이 행마다·틱마다 다시 만들어지면 <c>Thickness</c> 박싱이
/// 초당 수십 번 쌓인다.</para>
/// </summary>
public sealed class MeterLayoutVisual
{
    private static readonly ConcurrentDictionary<(string Id, int RowHeight), MeterLayoutVisual> Cache = new();

    private MeterLayoutVisual(MeterLayout spec, int rowHeight)
    {
        Spec = spec;
        RowHeight = rowHeight;

        CardPadding = new Thickness(spec.CardPaddingH, spec.CardPaddingV, spec.CardPaddingH, spec.CardPaddingV);
        CardMargin = new Thickness(0, spec.CardMarginV, 0, spec.CardMarginV);

        // 무대는 하단 1px 헤어라인만 — 위·좌·우가 없어야 행이 '틈 없는 판'으로 이어진다.
        CardBorder = spec.HasCardChrome
            ? (spec.CardBorderV >= 2.0 ? new Thickness(1) : new Thickness(0, 0, 0, spec.CardBorderV))
            : default;

        CardRadius = new CornerRadius(spec.CardRadius);
        GaugeRadius = spec.GaugeRadius;
        // 좌측은 모든 행이 같은 x 에서 출발하는 **축**이라 둥글리면 연속된 세로선에 노치가 줄줄이
        // 생긴다. 우측은 값의 **종단**이라 둥글려야 "여기서 끝난다"가 생긴다.
        // 채움이 행 왼쪽 끝에 붙는 레이아웃은 좌측을 직각으로 둔다 — 가장자리에 닿아 있으므로
        // 둥글리면 카드 모서리와 어긋난 반달이 생긴다. 반대로 들여놓은 채움은 사방을 둥글린다.
        // 채움이 카드 왼쪽 끝에 닿으면 좌측은 직각으로 둔다 — 가장자리에 붙어 있어 둥글리면
        // 카드 모서리와 어긋난 반달이 생긴다.
        GaugeCorner = spec.GaugeFullBleedRight
            ? new CornerRadius(0, spec.GaugeRadius, spec.GaugeRadius, 0)
            : new CornerRadius(spec.GaugeRadius);
        // 왼쪽은 순위 거터만큼 들여 숫자가 채움 위에 절대 오지 않게 하고(기여도와 무관하게 배경 고정),
        // 오른쪽은 카드 Padding 만큼 되밀어 카드 가장자리까지 채운다. Border 가 ClipToBounds 라 그 밖으로는 못 나간다.
        // 순위 숫자가 있으면 그만큼 들이고, 없으면 카드 가장자리까지 되민다(시안은 채움이 행 끝에
        // 닿는다 — 패딩 안쪽에서 시작하면 그 경계가 '잘린 단면'으로 읽힌다).
        double bleed = MeterLayout.GaugeBleedLeft(spec);
        GaugeBleed = new Thickness(bleed, 0, bleed, 0);
        // 채움 전체가 26% 알파면 바가 어디서 끝나는지가 흐린 색 경계 하나에만 실린다.
        // 같은 색 72% 2px 세로선이 그 경계를 판독점으로 만든다. 스킨 행은 팔레트가 이미 자기
        // 하이라이트를 갖고 있어 제외한다(RowGaugeCell 쪽에서 GaugeSkinId 로 가른다).
        EdgeHighlightVisibility = spec.GaugeFullBleedRight ? Visibility.Visible : Visibility.Collapsed;
        GaugeExclusionLeft = MeterLayout.GaugeExclusionLeft(spec.Id, rowHeight);
        FxBandHeight = MeterLayout.FxBandHeight(spec.Id, rowHeight);
        RowMinHeight = rowHeight;
        RankChipVisibility = spec.ShowRankChip ? Visibility.Visible : Visibility.Collapsed;
        JobIconVisibility = spec.ShowJobIcon ? Visibility.Visible : Visibility.Collapsed;
        JobDotVisibility = spec.ShowJobDot ? Visibility.Visible : Visibility.Collapsed;
        ServerTagVisibility = spec.ShowServerTag ? Visibility.Visible : Visibility.Collapsed;
        TierChipVisibility = spec.ShowTierChip ? Visibility.Visible : Visibility.Collapsed;
        // 순위칩이 없는 레이아웃은 맨 숫자로 순위를 보인다 — 아예 빼면 몇 등인지 알 수 없다.
        // 무대는 순서만으로 등수를 말한다 — 34px 행에서 숫자는 어느 크기로도 어정쩡했다.
        BareRankVisibility = spec.ShowRankNumeral ? Visibility.Visible : Visibility.Collapsed;
        // 무대는 카드 테두리가 없어 큰 흐린 숫자가 행의 시작점 노릇을 하고, 계기판은 작고 또렷하게.
        // 0.28 은 그 행에서 가장 흐린 서버 태그(MutedFg 68%)보다도 2.4배 흐렸다 — 배경치고 진하고
        // 읽을 글자치고 흐린, 어느 쪽도 아닌 값. 0.50 이면 위계는 지키면서 판독은 된다.
        BareRankOpacity = spec.LargeRankNumeral ? 0.50 : 0.55;
        // 34px 행에서 성립하는 역할은 '큰 장식'이 아니라 '작고 또렷한 색인'이다. 0.44 배(=14px)는
        // 본문 13px 과 1px 차이라 크기 대비를 못 만들었다.
        BareRankFontSize = spec.LargeRankNumeral
            ? Math.Max(11.0, Math.Floor(rowHeight * 0.38))
            : Math.Max(9.0, Math.Floor(rowHeight * 0.36));
        // MinWidth 가 아니라 고정폭이어야 세 자리에서도 이름 좌표가 흔들리지 않는다. 무대는 카드
        // 테두리도 아이콘도 없어 이름의 좌측 정렬선이 행을 정렬하는 유일한 수직선이다.
        BareRankWidth = spec.LargeRankNumeral ? MeterLayout.BareRankWideWidth : 13.0;
        PowerBadgeVisibility = spec.ShowPowerBadge ? Visibility.Visible : Visibility.Collapsed;
        // 무대는 헤더·보스·행·타이머·푸터가 틈 없이 이어지는 한 덩어리다 — 구역 간 간격도 패널
        // 안쪽 여백도 0 이고, 구역을 가르는 건 1px 헤어라인뿐이다.
        SectionGapBelow = new Thickness(0, 0, 0, spec.SectionGap);
        SectionGapAbove = new Thickness(0, spec.SectionGap, 0, 0);
        PanelPadding = new Thickness(spec.PanelPadH, spec.PanelPadV, spec.PanelPadH, spec.PanelPadV);
        SeamlessSections = spec.SectionGap <= 0.0;
        SectionDividerVisibility = SeamlessSections ? Visibility.Visible : Visibility.Collapsed;
        NameFontSize = MeterLayout.NameSize(spec, rowHeight);
        StatFontSize = MeterLayout.StatSize(spec, rowHeight);
        BareRankGap = spec.LargeRankNumeral ? MeterLayout.BareRankWideGap : 4.0;
        BareRankAlignment = spec.LargeRankNumeral ? TextAlignment.Right : TextAlignment.Left;
        BareRankWeight = spec.LargeRankNumeral ? FontWeights.SemiBold : FontWeights.Bold;
        // 무대는 직업 점이, 계기판은 게이지 채움 자체가 직업색을 이미 말한다 — 레일은 중복이다.
        AccentRailVisibility = spec.ShowAccentRail ? Visibility.Visible : Visibility.Collapsed;
        // 배지 상자를 지우면 숫자만 남는다. 상자를 없애는 레이아웃은 투명 배경 + 테두리 0.
        StatChrome = spec.ShowStatChrome;

        // 보스칸 높이는 레이아웃마다 다르다. 행 높이에 연동하던 현행(rowHeight+6)은 무대만 유지한다 —
        // 전장은 20px 게이지 띠가 한 줄 더 들어가고, 계기판은 26px HP% 가 주인공이라 둘 다 담을 수 없다.
        // ⚠️ Border 가 ClipToBounds 라 이 값이 모자라면 글자가 잘린 채 조용히 렌더된다(계기판에서 실제로 겪음).
        BossHeight = spec.BossStyle switch
        {
            // ⚠️ 3단(이름줄 + 20px 띠 + HP줄)이 들어가므로 84 로는 마지막 줄이 잘린다.
            BossStyle.Band => 104.0,
            BossStyle.Readout => 52.0, // 이름 11.5px + HP% 26px
            // 아이콘 박스 30 + 이름 15.5 + 서브라인 10.5 + 하단 레일 4 + 패딩
            _ => 70.0,
        };
        BossBandVisibility = spec.BossStyle == BossStyle.Band ? Visibility.Visible : Visibility.Collapsed;
        BossCanvasVisibility = spec.BossStyle == BossStyle.Canvas ? Visibility.Visible : Visibility.Collapsed;
        BossReadoutVisibility = spec.BossStyle == BossStyle.Readout ? Visibility.Visible : Visibility.Collapsed;
        // 전장·무대가 공유하는 한 줄 배치. 계기판만 완전히 다른 구성이라 그 여집합으로 둔다.
        // 전장은 3단, 무대는 카드형 — 둘이 공유하던 한 줄 배치를 갈랐다.
        BossInlineVisibility = spec.BossStyle == BossStyle.Canvas ? Visibility.Visible : Visibility.Collapsed;
    }

    public MeterLayout Spec { get; }

    public int RowHeight { get; }

    public Thickness CardPadding { get; }

    public Thickness CardMargin { get; }

    public Thickness CardBorder { get; }

    public CornerRadius CardRadius { get; }

    public double GaugeRadius { get; }

    /// <summary>장식을 비워 둘 왼쪽 폭. 옛 리터럴 72 를 대신하는 계산값이다.</summary>
    public double GaugeExclusionLeft { get; }

    /// <summary>장식이 실제로 그려지는 세로 대역(28/28/27). 테스트가 이 값을 잠근다.</summary>
    public double FxBandHeight { get; }

    /// <summary>
    /// 행 카드 Border 의 MinHeight. ⚠️이게 <c>GaugeFxLayer</c> 의 **유일한 높이 공급원**이다 —
    /// 카드 크롬을 0 으로 만들더라도 Border 자체와 이 값은 반드시 남아야 장식이 살아 있다.
    /// </summary>
    public double RowMinHeight { get; }

    public Visibility RankChipVisibility { get; }

    public Visibility JobIconVisibility { get; }

    /// <summary>무대는 22px 직업아이콘 대신 7px 색 점으로 직업을 표시한다.</summary>
    public Visibility JobDotVisibility { get; }

    public Visibility ServerTagVisibility { get; }

    public Visibility TierChipVisibility { get; }

    /// <summary>순위칩을 안 쓰는 레이아웃의 맨 순위 숫자.</summary>
    public Visibility BareRankVisibility { get; }

    public double BareRankOpacity { get; }

    public double BareRankFontSize { get; }

    public double BareRankWidth { get; }

    /// <summary>행 이름 글자 크기. 시안이 절대값을 지정한 레이아웃은 행 높이와 무관하게 고정된다.</summary>
    public Visibility PowerBadgeVisibility { get; }

    public Thickness SectionGapBelow { get; }

    public Thickness SectionGapAbove { get; }

    /// <summary>루트 패널 안쪽 여백. 한 덩어리 레이아웃은 0 이라 구역이 가장자리까지 닿는다.</summary>
    public Thickness PanelPadding { get; }

    /// <summary>구역 사이에 틈이 없는가(무대). 그러면 대신 헤어라인으로 가른다.</summary>
    public bool SeamlessSections { get; }

    public Visibility SectionDividerVisibility { get; }

    public double NameFontSize { get; }

    /// <summary>딜·비중 숫자 크기.</summary>
    public double StatFontSize { get; }

    public double BareRankGap { get; }

    public TextAlignment BareRankAlignment { get; }

    public FontWeight BareRankWeight { get; }

    /// <summary>채움 모서리. 무대는 좌측 직각 / 우측 둥근 비대칭이다.</summary>
    public CornerRadius GaugeCorner { get; }

    /// <summary>게이지가 행 안에서 좌우로 얼마나 들어가거나 넘치는가.</summary>
    public Thickness GaugeBleed { get; }

    public Visibility EdgeHighlightVisibility { get; }

    public Visibility AccentRailVisibility { get; }

    /// <summary>딜·비중 배지에 상자(배경+테두리)를 씌울지. 계기판·무대는 맨 숫자로 둔다.</summary>
    public bool StatChrome { get; }

    /// <summary>글자에 그림자를 넣을지. 판때기가 없는 계기판은 이게 가독성의 전부다.</summary>
    public bool EtchText => Spec.EtchText;

    /// <summary>비중%를 직업색으로 칠할지(무대).</summary>
    public bool PercentUsesJobColor => Spec.PercentUsesJobColor;

    /// <summary>창 배경(판때기)을 그릴지. 계기판은 게임 위에 글자만 새긴다.</summary>
    public bool ShowPanelBackground => Spec.ShowPanelBackground;

    /// <summary>
    /// 판때기 대신 **그라디언트 스크림**을 깔지. 완전 투명은 밝은 맵에서 글자가 곤란해지고, 불투명
    /// 판때기는 계기판의 정체성을 지운다 — 위가 진하고 아래로 옅어지는 스크림이 그 사이를 잡는다.
    /// </summary>
    public bool ScrimPanel => Spec.ScrimPanel;

    /// <summary>
    /// 게이지 스킨이 **없는** 행의 채움 불투명도. ⚠️스킨이 있는 행은 항상 0.58 이다 — 팔레트의 채도·
    /// 하이라이트 폭이 그 뒤를 전제로 튜닝돼 있어서, 레이아웃이 이걸 덮으면 돈 낸 스킨이 희미해진다.
    /// </summary>
    public double PlainFillOpacity => Spec.PlainFillOpacity;

    public bool HasCardChrome => Spec.HasCardChrome;

    /// <summary>레이아웃이 '직업색 게이지가 곧 행'을 전제하는가 — 게이지 형태 콤보를 잠글지 판단한다.</summary>
    public bool RequiresFillGauge => Spec.RequiresFillGauge;

    public double BossHeight { get; }

    public Visibility BossBandVisibility { get; }

    public Visibility BossCanvasVisibility { get; }

    public Visibility BossReadoutVisibility { get; }

    /// <summary>계기판이 아닌 레이아웃(전장·무대)이 쓰는 한 줄 배치의 가시성.</summary>
    public Visibility BossInlineVisibility { get; }

    public static MeterLayoutVisual For(string? id, int rowHeight)
    {
        MeterLayout spec = MeterLayout.For(id);
        return Cache.GetOrAdd((spec.Id, rowHeight), key => new MeterLayoutVisual(MeterLayout.For(key.Id), key.RowHeight));
    }

    /// <summary>현행과 수치가 같은 기본값. 바인딩이 아직 안 붙은 순간에도 화면이 깨지지 않게 한다.</summary>
    public static readonly MeterLayoutVisual Default = For(MeterLayout.DefaultId, 36);
}
