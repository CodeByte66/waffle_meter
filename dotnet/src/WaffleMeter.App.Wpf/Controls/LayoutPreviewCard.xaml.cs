using System.Windows.Controls;

namespace WaffleMeter.App.Wpf.Controls;

/// <summary>
/// 설정창의 레이아웃 미리보기. 미터 본체가 쓰는 컨트롤 셋을 그대로 얹어 표본 전투를 그린다.
/// DataContext 는 <c>SettingsViewModel.LayoutPreview</c> — preview 모드 <see cref="OverlayViewModel"/> 이라
/// 공용 애니메이션 시계에 수요를 보고하지 않는다.
/// </summary>
public partial class LayoutPreviewCard : UserControl
{
    public LayoutPreviewCard() => InitializeComponent();
}
