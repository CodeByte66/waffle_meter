namespace WaffleMeter.App.Core;

/// <summary>보스칸이 HP를 그리는 방식. 레이아웃마다 하나씩 고른다.</summary>
public enum BossStyle
{
    /// <summary>01 전장 — 글자 아래에 제 몫의 두꺼운 띠를 따로 세운다.</summary>
    Band,

    /// <summary>02 계기판 — 판때기 없이 HP%를 크게 새기고 게이지는 얇은 레일로.</summary>
    Readout,

    /// <summary>03 무대 — 게이지가 카드 배경 전체를 채운다.</summary>
    Canvas,
}

/// <summary>
/// 미터 레이아웃 3종의 수치 사양. **WPF 타입을 참조하지 않는다** — App.Core 에 두는 이유가 그것이고,
/// 덕분에 아래 기하 불변식을 xUnit 으로 잠글 수 있다(App.Wpf 에는 테스트 프로젝트가 없다).
///
/// <para>레이아웃은 색을 싣지 않는다. 색은 전부 <c>DynamicResource Skin.*</c> 로 남아 스킨 4종과
/// 직교한다 — 레이아웃 스펙에 Brush 를 넣는 순간 스킨 스왑을 못 따라가는 스냅샷이 된다.</para>
///
/// <para>id 는 반드시 ASCII 다. settings.properties 는 읽을 때마다 Latin-1 → EUC-KR 재디코드를
/// 통과하므로 한글 값을 쓰면 Base64 로 싸야 하고, enum 값에 그럴 이유가 없다.</para>
/// </summary>
public sealed record MeterLayout(
    string Id,
    string Label,
    int DefaultRowHeight,
    double CardPaddingV,
    double CardPaddingH,
    double CardBorderV,
    double CardRadius,
    double CardMarginV,
    bool HasCardChrome,
    bool ShowRankChip,
    double GaugeRadius,
    BossStyle BossStyle,
    bool RequiresFillGauge,
    bool ShowJobIcon,
    bool ShowJobDot,
    bool ShowServerTag,
    bool ShowTierChip,
    bool ShowStatChrome,
    bool ShowPanelBackground,
    bool ScrimPanel,
    bool LargeRankNumeral,
    bool ShowRankNumeral,
    bool EtchText,
    bool PercentUsesJobColor,
    bool ShowAccentRail,
    bool GaugeFillGradient,
    bool GaugeFullBleedRight,
    double PlainFillOpacity)
{
    // ── 행 왼쪽 클러스터의 실제 치수 (OverlayWindow.xaml 행 템플릿에서 그대로 옮긴 값) ──
    // 이 상수들이 실물과 어긋나면 게이지 장식이 순위칩·직업아이콘 뒤로 번진다.
    private const double RailWidth = 3.0;    // 좌측 accent rail Width="3"
    private const double RailGap = 8.0;      // 그 Margin="0,2,8,2" 의 오른쪽 8
    private const double RankChipWidth = 22.0; // 순위칩 MinWidth="22"
    private const double RankChipGap = 6.0;    // 그 Margin="0,0,6,0"
    private const double JobIconGap = 8.0;     // 직업아이콘 Margin="0,0,8,0"
    private const double JobDotSize = 7.0;     // 무대의 직업 점
    private const double JobDotGap = 9.0;

    // 순위칩을 안 쓰는 레이아웃의 맨 숫자 거터. 무대는 게이지를 이만큼 들여 숫자가 채움 위에
    // 절대 오지 않게 한다 — 그래야 기여도와 무관하게 글리프 배경이 고정된다.
    public const double BareRankWideWidth = 14.0;
    public const double BareRankWideGap = 10.0;
    private const double BareRankNarrowWidth = 13.0;
    private const double BareRankNarrowGap = 9.0;

    /// <summary>
    /// 맨 순위 숫자가 차지하는 총 폭(숫자 + 오른쪽 여백). 숫자를 안 쓰는 레이아웃은 0 이다 —
    /// 무대는 순서만으로 등수를 말하고 숫자를 그리지 않는다(참고 미터도 같은 선택을 한다).
    /// </summary>
    public static double BareRankGutter(MeterLayout l) =>
        !l.ShowRankNumeral ? 0.0
        : l.LargeRankNumeral ? BareRankWideWidth + BareRankWideGap
        : BareRankNarrowWidth + BareRankNarrowGap;

    /// <summary>
    /// 게이지 자체가 왼쪽으로 들어간 폭. 전폭 블리드 레이아웃은 왼쪽 묶음(순위 숫자 + 직업 점)만큼
    /// 들여, 그 요소들이 채움 위에 절대 오지 않게 한다.
    /// <para>🔑 직업 점은 채움과 <b>같은 색</b>이라 채움 위에 올라가면 사라진다 — 숫자를 빼도 점 몫은
    /// 반드시 남겨야 한다.</para>
    /// </summary>
    public static double GaugeInsetLeft(MeterLayout l) =>
        !l.GaugeFullBleedRight ? 0.0
        : BareRankGutter(l) + (l.ShowJobDot ? JobDotSize + JobDotGap : 0.0);

    /// <summary>
    /// 직업아이콘 한 변. XAML 이 <c>ConverterParameter='0.66:18'</c> 로 계산하는 값과 **같은 산술**이어야
    /// 한다(<c>RowHeightToFontSizeConverter</c> = <c>Math.Max(min, Math.Floor(h * mult))</c>).
    /// 여기서 반올림 방식이 갈리면 장식 여백이 1~2px 어긋난다.
    /// </summary>
    public static double JobIconSize(int rowHeight) => Math.Max(18.0, Math.Floor(rowHeight * 0.66));

    /// <summary>01 전장 — 행은 현행과 수치가 완전히 같다. 기존 사용자의 기본값이자 되돌리기 기준.</summary>
    public static readonly MeterLayout Battlefield = new(
        Id: "battlefield",
        Label: "전장",
        DefaultRowHeight: 36,
        CardPaddingV: 3.0,   // Padding="5,3"
        CardPaddingH: 5.0,
        CardBorderV: 2.0,    // BorderThickness="1" 위아래 합
        CardRadius: 6.0,
        CardMarginV: 1.0,    // Margin="0,1"
        HasCardChrome: true,
        ShowRankChip: true,
        GaugeRadius: 4.0,
        BossStyle: BossStyle.Band,
        RequiresFillGauge: false,
        ShowJobIcon: true,
        ShowJobDot: false,
        ShowServerTag: true,
        ShowTierChip: true,
        ShowStatChrome: true,
        ScrimPanel: false,
        ShowPanelBackground: true,
        ShowRankNumeral: false,
        LargeRankNumeral: false,
        EtchText: false,
        GaugeFillGradient: false,
        GaugeFullBleedRight: false,
        ShowAccentRail: true,
        PercentUsesJobColor: false,
        PlainFillOpacity: 0.3);

    /// <summary>
    /// 02 계기판 — 카드 크롬을 전부 0 으로 만든다. ⚠️Border 자체를 지우면 안 된다: 그 Border 의
    /// <c>MinHeight</c> 가 <c>GaugeFxLayer</c> 의 **유일한 높이 공급원**이고(레이어의 MeasureOverride 는
    /// 0 을 돌려준다), 배경도 <c>Transparent</c> 여야 한다 — <c>{x:Null}</c> 이면 히트테스트가 죽어
    /// 행 클릭(상세창을 여는 유일 경로)과 hover 가 함께 조용히 사라진다.
    /// </summary>
    public static readonly MeterLayout Dashboard = new(
        Id: "dashboard",
        Label: "계기판",
        DefaultRowHeight: 28,
        CardPaddingV: 0.0,
        CardPaddingH: 8.0,
        CardBorderV: 0.0,
        CardRadius: 0.0,
        CardMarginV: 2.0,   // 리본이 딱 붙으면 답답하다 — 숨 쉴 틈을 준다
        HasCardChrome: false,
        ShowRankChip: false,
        GaugeRadius: 2.0,
        BossStyle: BossStyle.Readout,
        RequiresFillGauge: true,
        ShowJobIcon: false,
        ShowJobDot: false,
        ShowServerTag: false,
        ShowTierChip: false,
        ShowStatChrome: false,
        ScrimPanel: true,
        ShowPanelBackground: false,
        ShowRankNumeral: true,
        LargeRankNumeral: false,
        EtchText: true,
        GaugeFillGradient: false,
        GaugeFullBleedRight: false,
        ShowAccentRail: false,
        PercentUsesJobColor: false,
        PlainFillOpacity: 0.42);

    /// <summary>03 무대 — 틈 없는 판 하나. 행 사이는 하단 1px 헤어라인만 남는다.</summary>
    public static readonly MeterLayout Stage = new(
        Id: "stage",
        Label: "무대",
        DefaultRowHeight: 34,
        CardPaddingV: 3.0,
        CardPaddingH: 11.0,
        CardBorderV: 1.0,    // 하단 1px 만
        CardRadius: 0.0,
        CardMarginV: 0.0,
        HasCardChrome: true,
        ShowRankChip: false,  // 칩 대신 큰 흐린 숫자를 쓴다
        GaugeRadius: 3.0,   // 값의 종단이 보이려면 2px 는 안티에일리어싱에 먹힌다
        BossStyle: BossStyle.Canvas,
        RequiresFillGauge: true,
        ShowJobIcon: false,
        ShowJobDot: true,
        ShowServerTag: true,
        ShowTierChip: false,
        ShowStatChrome: false,
        ScrimPanel: false,
        ShowPanelBackground: true,
        ShowRankNumeral: false,
        LargeRankNumeral: true,
        EtchText: false,
        GaugeFillGradient: true,
        GaugeFullBleedRight: true,
        ShowAccentRail: false,
        PercentUsesJobColor: true,
        PlainFillOpacity: 0.26);

    public static readonly IReadOnlyList<MeterLayout> All = [Battlefield, Dashboard, Stage];

    /// <summary>설정 허용값 배열. <c>MeterSettings</c> 의 ReadEnum 목록과 이 배열은 반드시 같아야 한다.</summary>
    public static readonly string[] Ids = [.. All.Select(l => l.Id)];

    public const string DefaultId = "battlefield";

    /// <summary>모르는 id 는 기본값으로 떨어진다 — 구버전 설정이나 손으로 고친 값이 앱을 못 띄우면 안 된다.</summary>
    public static MeterLayout For(string? id)
    {
        foreach (MeterLayout layout in All)
        {
            if (layout.Id == id)
            {
                return layout;
            }
        }

        return Battlefield;
    }

    /// <summary>
    /// 게이지 장식이 실제로 그려지는 세로 대역의 높이 = 행 Border 의 내용 상자 높이.
    /// <para>🔑 <c>GaugeFxArt</c> 의 상수(코로나 두께 4.4, 프리즘 대각 Δx=9, 세그먼트 5 …)는 전부 **절대값**이라
    /// 이 값이 변하면 장식의 인상이 같이 변한다. 세 레이아웃을 28/28/27 로 맞춰 둔 것은 우연이 아니라
    /// 설계다 — 오늘 기본값(36 − 6 − 2 = 28)과 같게 두면 장식 코드를 한 줄도 건드리지 않아도 되고,
    /// 건드리면 rowHeight 를 조정해 둔 **모든 기존 사용자**의 화면이 함께 바뀌어 회귀 원인 분리가 불가능해진다.</para>
    /// </summary>
    public static double FxBandHeight(string id, int rowHeight)
    {
        MeterLayout l = For(id);
        return Math.Max(0.0, rowHeight - (l.CardPaddingV * 2.0) - l.CardBorderV);
    }

    /// <summary>
    /// 장식을 그리지 않고 비워 둘 왼쪽 폭. rail + (순위칩) + 직업아이콘 묶음의 실폭이다.
    /// <para>지금까지는 <c>ContentExclusionLeft="72"</c> 리터럴이었는데, 그건 rowHeight 36 전용 매직넘버라
    /// 28 에서는 과잉, 80 에서는 부족해 입자가 직업아이콘 뒤로 번지는 잠복 결함이 있었다. 계산식으로
    /// 바꾸면 레이아웃 3종 대응과 그 결함이 한 번에 해결된다.</para>
    /// </summary>
    public static double GaugeExclusionLeft(string id, int rowHeight)
    {
        MeterLayout l = For(id);

        // ⚠️ 레일은 **있을 때만** 센다. 무조건 더하면 레일이 Collapsed 인 레이아웃에서 유령 11px 을
        // 제외하게 되고, 그만큼 장식이 실제 좌측 묶음 뒤로 번진다(무대에서 16px 실측).
        double left = l.ShowAccentRail ? RailWidth + RailGap : 0.0;
        left += l.ShowRankChip ? RankChipWidth + RankChipGap : BareRankGutter(l);
        left += l.ShowJobIcon ? JobIconSize(rowHeight) + JobIconGap
              : l.ShowJobDot ? JobDotSize + JobDotGap
              : 0.0;

        // 게이지가 이미 왼쪽으로 들어가 있으면 제외 기준점이 게이지 자신의 좌측 가장자리로 옮겨간다.
        return Math.Max(0.0, left - GaugeInsetLeft(l));
    }
}
