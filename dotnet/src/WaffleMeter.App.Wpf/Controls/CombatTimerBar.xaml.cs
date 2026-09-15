using System.Windows;
using System.Windows.Controls;

namespace WaffleMeter.App.Wpf.Controls;

/// <summary>
/// 전투 상태 점 · 상태 문구 · 이번 전투 상위 X.X% 칩 · 경과 시간. 본체 미터와 UI 분리모드의 행 창이
/// 같은 인스턴스 타입을 쓰고, DataContext 로는 둘 다 같은 <see cref="OverlayViewModel"/> 을 받는다.
/// </summary>
public partial class CombatTimerBar : UserControl
{
    public CombatTimerBar() => InitializeComponent();

    /// <summary>
    /// 이 띠가 행 <b>위</b>에 붙었는가. 무대 레이아웃의 분리 행 창만 true 다 — 거기서는 이 띠가 창의
    /// 머리 역할을 하고, 행이 창 아래변까지 이어진다.
    /// <para>구분선 방향 때문에 필요하다: 아래에 있을 땐 위쪽 테두리로 행과 갈리지만, 위에 있으면 같은
    /// 테두리가 창 맨 윗변에 아무것도 가르지 않는 선 하나로 남는다.</para>
    /// </summary>
    public static readonly DependencyProperty DockedTopProperty =
        DependencyProperty.Register(
            nameof(DockedTop), typeof(bool), typeof(CombatTimerBar), new PropertyMetadata(false));

    public bool DockedTop
    {
        get => (bool)GetValue(DockedTopProperty);
        set => SetValue(DockedTopProperty, value);
    }
}
