using System.Windows;
using System.Windows.Controls;

namespace WaffleMeter.App.Wpf.Controls;

/// <summary>
/// 보스칸(타겟 정보). 본체 창과 분리모드의 보스 창이 같은 컨트롤을 쓰고 DataContext 도 같은
/// <see cref="OverlayViewModel"/> 하나다.
/// </summary>
public partial class BossBarView : UserControl
{
    public BossBarView() => InitializeComponent();

    /// <summary>
    /// 카드 <b>안쪽</b> 오른쪽에 비워 둘 폭. UI 분리모드의 보스칸 창이 우상단 컨트롤(잠금·기록·설정) 자리를
    /// 이걸로 알린다.
    ///
    /// <para>왜 바깥이 아니라 안쪽인가: 컨트롤을 Grid 열로 옆에 세우면 칸 자체가 좁아지는데, 무대처럼
    /// 카드가 창 끝까지 차는 레이아웃에서는 카드가 버튼 앞에서 끊겨 회색 이음매가 생긴다("한 덩어리로
    /// 보인다"가 무대의 요점이라 그게 눈에 띈다). 배경은 그대로 두고 내용만 비키게 하는 게 맞다.</para>
    ///
    /// <para>겹쳐 올리는 방식은 이미 실패했다 — 대기 카드의 전투력 숫자가 설정 아이콘 뒤로 들어갔다.</para>
    /// </summary>
    public static readonly DependencyProperty TrailingInsetProperty =
        DependencyProperty.Register(
            nameof(TrailingInset), typeof(Thickness), typeof(BossBarView), new PropertyMetadata(default(Thickness)));

    public Thickness TrailingInset
    {
        get => (Thickness)GetValue(TrailingInsetProperty);
        set => SetValue(TrailingInsetProperty, value);
    }

    /// <summary>
    /// 대기 카드(인식된 본인 캐릭터)의 높이 하한. 본체는 기본값 46 이고, 분리모드의 보스칸 창이 58 을 준다.
    ///
    /// <para>왜 창마다 다른가: 분리 보스 창은 자동 높이라 이 하한이 곧 카드 높이다 — 직업 문장 칸(44)에 위아래
    /// 여백을 주려면 58 이 필요하다. 본체는 보스 행이 * 라 평소 그보다 넓은 칸을 받으므로 하한이 클 이유가 없고,
    /// 58 을 걸면 배율 125~130% 에서 높이를 최소로 고정했을 때 카드가 칸보다 커져 아래가 잘렸다.</para>
    /// </summary>
    public static readonly DependencyProperty IdleCardMinHeightProperty =
        DependencyProperty.Register(
            nameof(IdleCardMinHeight), typeof(double), typeof(BossBarView), new PropertyMetadata(46.0));

    public double IdleCardMinHeight
    {
        get => (double)GetValue(IdleCardMinHeightProperty);
        set => SetValue(IdleCardMinHeightProperty, value);
    }
}
