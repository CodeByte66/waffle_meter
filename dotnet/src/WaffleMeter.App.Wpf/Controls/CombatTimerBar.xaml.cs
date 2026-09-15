using System.Windows.Controls;

namespace WaffleMeter.App.Wpf.Controls;

/// <summary>
/// 전투 상태 점 · 상태 문구 · 이번 전투 상위 X.X% 칩 · 경과 시간. 본체 미터와 UI 분리모드의 행 창이
/// 같은 인스턴스 타입을 쓰고, DataContext 로는 둘 다 같은 <see cref="OverlayViewModel"/> 을 받는다.
/// </summary>
public partial class CombatTimerBar : UserControl
{
    public CombatTimerBar() => InitializeComponent();
}
