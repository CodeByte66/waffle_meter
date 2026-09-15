using System.Windows.Controls;

namespace WaffleMeter.App.Wpf.Controls;

/// <summary>
/// 보스칸(타겟 정보). 본체 창과 분리모드의 보스 창이 같은 컨트롤을 쓰고 DataContext 도 같은
/// <see cref="OverlayViewModel"/> 하나다.
/// </summary>
public partial class BossBarView : UserControl
{
    public BossBarView() => InitializeComponent();
}
