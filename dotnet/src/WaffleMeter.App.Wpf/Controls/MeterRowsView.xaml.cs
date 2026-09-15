using System.Windows.Controls;
using System.Windows.Input;

namespace WaffleMeter.App.Wpf.Controls;

/// <summary>
/// 미터 행 목록. 본체 창과 분리모드의 행 창이 **같은 인스턴스 타입**을 쓰고, DataContext 로는 둘 다
/// 같은 <see cref="OverlayViewModel"/> 을 받는다(두 번째 VM 을 만들면 <c>NameFxSheen.SetDemand</c> 가
/// 대입이라 서로의 수요를 지워 한쪽 창 연출이 간헐 정지한다).
/// </summary>
public partial class MeterRowsView : UserControl
{
    public MeterRowsView() => InitializeComponent();

    /// <summary>
    /// 행 클릭 → 전투 상세 선택 토글. 컴파일은 통과하면서 조용히 죽기 쉬운 경로라(행 Border 의
    /// Background 가 <c>{x:Null}</c> 이면 히트테스트 자체가 안 된다) 레이아웃을 바꿀 때마다 먼저 확인한다.
    /// </summary>
    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { DataContext: RowViewModel row }
            && DataContext is OverlayViewModel vm)
        {
            vm.ToggleSelection(row.Id);
        }
    }
}
