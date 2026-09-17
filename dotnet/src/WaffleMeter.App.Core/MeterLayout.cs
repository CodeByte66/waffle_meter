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
    bool ShowTierRowOutline,
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
    bool ShowPowerBadge,
    double SectionGap,
    double PanelPadH,
    double PanelPadV,
    double NameFontSize,
    double StatFontSize,
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
    private const double BareRankNarrowGap = 4.0;

    /// <summary>
    /// 맨 순위 숫자가 차지하는 총 폭(숫자 + 오른쪽 여백). 숫자를 안 쓰는 레이아웃은 0 이다 —
    /// 무대는 순서만으로 등수를 말하고 숫자를 그리지 않는다(참고 미터도 같은 선택을 한다).
    /// </summary>
    public static double BareRankGutter(MeterLayout l) =>
        !l.ShowRankNumeral ? 0.0
        : l.LargeRankNumeral ? BareRankWideWidth + BareRankWideGap
        : BareRankNarrowWidth + BareRankNarrowGap;

    /// <summary>
    /// 게이지 채움이 좌우로 얼마나 넘치는가. 전폭 블리드 레이아웃은 **카드 가장자리까지** 깔린다.
    /// <para>🔑 순위 숫자를 채움 밖으로 빼려고 채움을 오른쪽으로 밀었던 적이 있는데, 그게 "순위가
    /// 바깥에 있어서 어색하다"의 원인이었다. 시안은 채움이 카드 끝에서 시작하고 순위·이름이 그 **위에**
    /// 카드 패딩만큼 들어와 앉는다 — 여백은 채움을 미는 게 아니라 글자를 들이는 것으로 만든다.</para>
    /// </summary>
    public static double GaugeBleedLeft(MeterLayout l) => l.GaugeFullBleedRight ? -l.CardPaddingH : 0.0;


    /// <summary>
    /// 직업아이콘 한 변. XAML 이 <c>ConverterParameter='0.66:18'</c> 로 계산하는 값과 **같은 산술**이어야
    /// 한다(<c>RowHeightToFontSizeConverter</c> = <c>Math.Max(min, Math.Floor(h * mult))</c>).
    /// 여기서 반올림 방식이 갈리면 장식 여백이 1~2px 어긋난다.
    /// </summary>
    /// <summary>
    /// 이름·수치 글자 크기. 0 이면 행 높이에서 파생한다(전장 = 현행 동작 유지).
    /// 계기판·무대는 시안이 절대값을 지정하므로 행 높이와 무관하게 고정한다.
    /// </summary>
    public static double NameSize(MeterLayout l, int rowHeight) =>
        l.NameFontSize > 0 ? l.NameFontSize : Math.Max(10.0, Math.Floor(rowHeight * 0.4));

    public static double StatSize(MeterLayout l, int rowHeight) =>
        l.StatFontSize > 0 ? l.StatFontSize : Math.Max(10.0, Math.Floor(rowHeight * 0.4));

    public static double JobIconSize(int rowHeight) => Math.Max(18.0, Math.Floor(rowHeight * 0.66));

    // ── 보스칸 높이 ────────────────────────────────────────────────────────────────
    // 100% 기준 높이는 레이아웃마다 다르다. 행 높이에 연동하던 옛 방식(rowHeight+6)은 무대만 유지한다 —
    // 전장은 20px 게이지 띠가 한 줄 더 들어가고, 계기판은 26px HP% 가 주인공이라 둘 다 담을 수 없다.
    // 🔑 수치와 산식이 App.Wpf 가 아니라 여기 있는 이유: App.Wpf 에는 테스트 프로젝트가 없어서 XAML 쪽
    //    회귀를 잡아 줄 장치가 하나도 없다. MeterLayoutVisual 은 이 값을 읽어 XAML 로 넘기기만 한다.

    /// <summary>배율 100% 일 때의 보스칸 높이.</summary>
    /// <remarks>
    /// ⚠️ BossBarView 루트 Border 는 고정 높이 + ClipToBounds 다 — 이 값이 모자라면 예외도 경고도 없이
    /// 글자가 잘린 채 렌더된다(계기판 52px 에서 실제로 겪었다).
    /// </remarks>
    public static double BossNaturalHeight(BossStyle style) => style switch
    {
        // ⚠️ 3단(이름줄 + 20px 띠 + HP줄)이 들어가므로 84 로는 마지막 줄이 잘린다.
        BossStyle.Band => 104.0,
        BossStyle.Readout => 52.0, // 이름 11.5px + HP% 26px
        // 아이콘 박스 30 + 이름 15.5 + 서브라인 10.5 + 하단 레일 4 + 패딩
        _ => 70.0,
    };

    /// <summary>
    /// 칸 높이에 실리지만 <b>배율을 따라가지 않는</b> 세로 크롬 = 루트 Border 의
    /// <c>BorderThickness="1"</c> 위아래 합.
    /// <para>🔑 이만큼 빼고 곱해야 '내용이 칸에 들어맞는 비율'이 배율과 무관하게 보존된다. 전체를 그냥
    /// 곱하면 축소할수록 1px 테두리가 상대적으로 커지면서 내용 몫이 줄어, 여유가 거의 없는 계기판
    /// (52px = 이름 11.5 + HP% 26 그 자체)에서 아랫줄이 조용히 잘린다.</para>
    /// </summary>
    public const double BossChromeHeight = 2.0;

    // 설정 슬라이더 범위.
    //   하한 70 — 계기판 이름줄(11.5px)이 그 아래에서는 읽히지 않는다(11.5 × 0.7 ≈ 8px).
    //   상한 150 — ⚠️ 세로가 아니라 **가로**가 정한 값이다. 전장 3줄째는 'HP 수치 / 처치까지 / 큰 HP%'
    //     가 한 줄에 앉는데 Auto 열이라 폭이 모자라면 줄어들지 않고 **마지막 열(큰 HP%)이 잘린다**.
    //     기본 폭(460px)에서 그 줄의 실폭은 100% 기준 약 280px 이라 1.6배에서 넘친다 — 180 으로 열었더니
    //     실제로 "47.2%" 가 "47" 로 잘렸다. 여유를 두고 150 에서 끊는다.
    //     (같은 이유로 창을 아주 좁히면 100% 에서도 잘린다. 그건 이 기능 이전부터 그랬다.)
    public const int BossScaleMin = 70;
    public const int BossScaleMax = 150;
    public const int BossScaleDefault = 100;

    /// <summary>
    /// 보스칸 배율(<see cref="BossScaleMin"/>~<see cref="BossScaleMax"/> 퍼센트를 배로 환산한 값).
    /// 칸 높이와 칸 안의 글자·게이지·세로 여백이 <b>똑같이</b> 이 값을 곱한다 —
    /// 그래서 어떤 배율에서도 100% 와 같은 여백 비율이 유지되고, 조용한 잘림이 생길 수 없다.
    /// </summary>
    public static double BossScale(int percent) =>
        Math.Clamp(percent, BossScaleMin, BossScaleMax) / 100.0;

    /// <summary>사용자 배율을 반영한 보스칸 높이. 100% 면 <see cref="BossNaturalHeight"/> 와 정확히 같다.</summary>
    public static double BossSlotHeight(BossStyle style, int percent) =>
        ((BossNaturalHeight(style) - BossChromeHeight) * BossScale(percent)) + BossChromeHeight;

    // ── 이름 대응표 ────────────────────────────────────────────────────────────────
    //   TYPE A = battlefield (전장)   TYPE B = dashboard (계기판)   TYPE C = stage (무대)
    // 화면에 나가는 이름은 Label 뿐이고, Id 와 이 파일 아래의 설계 주석은 개발 당시의 한국어 별칭을
    // 그대로 쓴다. ⚠️ Id 는 settings.properties 에 저장된 값이라 절대 바꾸지 마라 — 바꾸는 순간
    // 사용자가 고른 레이아웃이 고아가 되어 전원이 기본값으로 되돌아간다.

    /// <summary>01 전장 — 행은 현행과 수치가 완전히 같다. 기존 사용자의 기본값이자 되돌리기 기준.</summary>
    public static readonly MeterLayout Battlefield = new(
        Id: "battlefield",
        Label: "TYPE A",
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
        ShowTierRowOutline: true,
        ShowStatChrome: true,
        ScrimPanel: false,
        ShowPanelBackground: true,
        ShowRankNumeral: false,
        LargeRankNumeral: false,
        EtchText: false,
        GaugeFillGradient: false,
        NameFontSize: 0.0,
        StatFontSize: 0.0,
        ShowPowerBadge: true,
        SectionGap: 6.0,
        PanelPadH: 10.0,
        PanelPadV: 8.0,
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
        Label: "TYPE B",
        DefaultRowHeight: 28,
        CardPaddingV: 0.0,
        CardPaddingH: 8.0,
        CardBorderV: 0.0,
        CardRadius: 0.0,
        CardMarginV: 1.0,   // 리본이 딱 붙으면 답답하다 — 숨 쉴 틈을 준다
        HasCardChrome: false,
        ShowRankChip: false,
        GaugeRadius: 2.0,
        BossStyle: BossStyle.Readout,
        RequiresFillGauge: true,
        // 닉네임 앞 직업아이콘. 이름 색·게이지 색만으로는 '무슨 직업인지'가 아니라 '누가 누구인지'만
        // 말한다 — 색을 외우고 있어야 읽히는 정보였다.
        ShowJobIcon: true,
        ShowJobDot: false,
        ShowServerTag: false,
        ShowTierChip: true,
        ShowTierRowOutline: false,
        ShowStatChrome: false,
        ScrimPanel: true,
        ShowPanelBackground: false,
        ShowRankNumeral: true,
        LargeRankNumeral: false,
        EtchText: true,
        GaugeFillGradient: false,
        NameFontSize: 13.0,
        StatFontSize: 12.0,
        ShowPowerBadge: true,
        SectionGap: 6.0,
        PanelPadH: 10.0,
        PanelPadV: 8.0,
        GaugeFullBleedRight: true,
        ShowAccentRail: false,
        PercentUsesJobColor: false,
        PlainFillOpacity: 0.42);

    /// <summary>03 무대 — 틈 없는 판 하나. 행 사이는 하단 1px 헤어라인만 남는다.</summary>
    public static readonly MeterLayout Stage = new(
        Id: "stage",
        Label: "TYPE C",
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
        // 7px 직업색 점을 아이콘으로 바꿨다. 34px 행에 22px 아이콘이 답답할까 봐 점으로 뒀던 건데,
        // 점은 직업을 **색으로만** 말해서 결국 색표를 외운 사람에게만 정보였다. 직업색은 게이지 채움과
        // 비중% 가 이미 들고 있으므로 점이 빠져도 잃는 신호가 없다.
        ShowJobIcon: true,
        ShowJobDot: false,
        ShowServerTag: true,
        ShowTierChip: true,
        ShowTierRowOutline: false,
        ShowStatChrome: false,
        ScrimPanel: false,
        ShowPanelBackground: true,
        ShowRankNumeral: false,
        LargeRankNumeral: true,
        EtchText: false,
        GaugeFillGradient: true,
        NameFontSize: 13.5,
        StatFontSize: 12.5,
        ShowPowerBadge: true,
        SectionGap: 0.0,
        PanelPadH: 0.0,
        PanelPadV: 0.0,
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
        // 제외하게 되고, 그만큼 장식이 실제 좌측 묶음 뒤로 번진다.
        // 채움이 카드 가장자리부터 깔리는 레이아웃은 기준점이 그만큼 왼쪽이므로 카드 패딩을 더한다.
        double left = l.GaugeFullBleedRight ? l.CardPaddingH : 0.0;
        left += l.ShowAccentRail ? RailWidth + RailGap : 0.0;
        left += l.ShowRankChip ? RankChipWidth + RankChipGap : BareRankGutter(l);
        left += l.ShowJobIcon ? JobIconSize(rowHeight) + JobIconGap
              : l.ShowJobDot ? JobDotSize + JobDotGap
              : 0.0;

        return Math.Max(0.0, left);
    }

    /// <summary>레이아웃 설명 한 줄. <see cref="Label"/> 은 무엇을 정하는지, <see cref="Detail"/> 은 그 값이다.</summary>
    public readonly record struct LayoutNote(string Label, string Detail);

    /// <summary>
    /// "이 레이아웃이 정하는 것" 목록. 설정에서 고를 수 없고 레이아웃이 통째로 결정하는 항목들이다.
    ///
    /// <para>🔑 문구를 손으로 적지 않고 <b>스펙 플래그에서 파생</b>한다. 손으로 적으면 스펙을 고칠 때
    /// 같이 안 고쳐져 설명만 옛 모습으로 남는데, 그 어긋남은 화면을 봐도 안 보인다(설명이 그럴듯해서
    /// 오히려 틀린 쪽을 믿게 된다). 새 레이아웃을 더해도 이 목록은 저절로 맞는다.</para>
    /// </summary>
    public static IReadOnlyList<LayoutNote> TraitsOf(MeterLayout l) => new[]
    {
        new LayoutNote("기본 행 높이", $"{l.DefaultRowHeight}px"),
        new LayoutNote("보스칸", l.BossStyle switch
        {
            BossStyle.Band => "이름 아래 독립 게이지 띠",
            BossStyle.Readout => "판 없이 큰 HP% 한 줄",
            _ => "카드 채움 + 하단 레일",
        }),
        new LayoutNote("순위", l.ShowRankChip ? "왼쪽 번호 칩"
            : l.ShowRankNumeral ? "게이지 안 숫자"
            : "표시하지 않음"),
        new LayoutNote("직업", l.ShowJobIcon ? "아이콘"
            : l.ShowJobDot ? "직업색 점"
            : "이름 색으로만"),
        new LayoutNote("티어", l.ShowTierChip && l.ShowTierRowOutline ? "칩 + 행 테두리"
            : l.ShowTierChip ? "칩만 (행 테두리 없음)"
            : "표시하지 않음"),
        new LayoutNote("행 배경", l.HasCardChrome ? "카드" : "없음 — 게이지가 곧 행"),
        new LayoutNote("미터 판", l.ScrimPanel ? "그라데이션 스크림"
            : l.ShowPanelBackground ? "불투명 판"
            : "없음"),
    };

    /// <summary>
    /// "이 레이아웃에서 잠기는 설정" 목록. 실제로 설정 컨트롤이 비활성화되는 항목만 넣는다 —
    /// <see cref="TraitsOf"/> 의 '정하는 것'과 달리 이건 <b>사용자가 켜 둔 값이 무시된다</b>는 뜻이라,
    /// 적어 두지 않으면 "껐는데 왜 그대로냐"가 된다.
    /// <para>저장값은 건드리지 않는다 — 다른 레이아웃으로 돌아가면 고른 값이 그대로 되살아난다.</para>
    /// </summary>
    public static IReadOnlyList<LayoutNote> LocksOf(MeterLayout l)
    {
        var locks = new List<LayoutNote>();
        if (l.RequiresFillGauge)
        {
            locks.Add(new LayoutNote("게이지 형태",
                "직업색 채움으로 고정 — 얇은 바·표시 안 함은 여기선 빈 리본이 된다"));
        }

        if (!l.ShowServerTag)
        {
            locks.Add(new LayoutNote("서버 표시", "이 레이아웃은 이름 뒤에 서버를 넣지 않는다"));
        }

        return locks;
    }
}
