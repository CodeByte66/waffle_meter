using System.Windows;
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
    /// 이 창의 대기 카드(본인 캐릭터 카드)가 <b>다른 창에</b> 있는가. UI 분리모드의 행 창만 true 다.
    ///
    /// <para>본체에서는 바로 위 보스칸이 대기 카드를 그리므로 빈 상태 안내를 접는다 — 같은 말이 두 번
    /// 나오기 때문이다. 분리모드에서는 그 카드가 보스칸 <b>창</b>에 있어서, 같은 규칙을 그대로 쓰면 행 창이
    /// 아무 설명 없는 빈 띠 하나로 남는다. 뷰는 자기가 어느 창에 얹혔는지 알 방법이 없으므로 창이 알린다.</para>
    /// </summary>
    public static readonly DependencyProperty IdleCardElsewhereProperty =
        DependencyProperty.Register(
            nameof(IdleCardElsewhere), typeof(bool), typeof(MeterRowsView), new PropertyMetadata(false));

    public bool IdleCardElsewhere
    {
        get => (bool)GetValue(IdleCardElsewhereProperty);
        set => SetValue(IdleCardElsewhereProperty, value);
    }

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
