using System.Windows;
using System.Windows.Input;

namespace WaffleMeter.App.Wpf;

/// <summary>쿨타임 오버레이의 "표시할 스킬 선택" 창. 참가요청 배지 픽커와 행 뷰모델은 공유하지만 창은
/// 따로다 — 모수가 221개 × 9직업이라 "내 직업만" 필터와 직업별 선택 수 표시가 필요하고, 그 둘은 배포 중인
/// 저쪽 화면에 넣을 이유가 없다.</summary>
public partial class CooldownPickerFlyout : OverlayPanelWindow
{
    public CooldownPickerFlyout()
    {
        InitializeComponent();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SkillJobGroupViewModel group })
        {
            group.SelectAll();
        }
    }

    private void OnDeselectAll(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SkillJobGroupViewModel group })
        {
            group.DeselectAll();
        }
    }

    // 전체 선택/해제는 <b>지금 보이는</b> 묶음에만 적용한다. "내 직업만"이 켜진 채로 9개 직업을 통째로
    // 꺼 버리면 사용자가 보지도 못한 선택이 사라지고, 되돌릴 방법도 화면에 없다.
    private void OnSelectAllVisible(object sender, RoutedEventArgs e) =>
        (DataContext as CooldownPickerViewModel)?.SelectAllVisible();

    private void OnDeselectAllVisible(object sender, RoutedEventArgs e) =>
        (DataContext as CooldownPickerViewModel)?.DeselectAllVisible();

    // ---- 배치 드래그 ----
    //
    // OLE DragDrop 대신 마우스 캡처로 직접 옮긴다. 이 창은 게임 포커스를 뺏지 않는 오버레이 창(NOACTIVATE)이고,
    // 옮길 곳도 같은 목록 안뿐이라 OLE 가 줄 것이 없다. 끄는 동안 컬렉션 자체를 움직여 다른 칸이 비켜서므로
    // 따로 삽입 표시가 필요 없다 — 놓기 전에 이미 결과가 보인다. 좌표는 전부 ArrangeList 기준이라 창이 좁아
    // Viewbox 가 미리보기를 줄였어도 그대로 맞는다.

    private CooldownArrangeItem? _pressed;
    private Point _pressedAt;
    private bool _arranging;

    private void OnResetArrangement(object sender, RoutedEventArgs e) =>
        (DataContext as CooldownPickerViewModel)?.ResetArrangement();

    private void OnArrangeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = (e.OriginalSource as FrameworkElement)?.DataContext as CooldownArrangeItem;
        _pressedAt = e.GetPosition(ArrangeList);
    }

    private void OnArrangeMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is null || DataContext is not CooldownPickerViewModel vm)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishArrange();
            return;
        }

        Point at = e.GetPosition(ArrangeList);
        if (!_arranging)
        {
            // 클릭 한 번(툴팁 보려고 누른 것)이 순서를 바꾸면 안 된다 — 시스템 드래그 임계값을 넘겨야 시작한다.
            if (Math.Abs(at.X - _pressedAt.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(at.Y - _pressedAt.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            // 누른 뒤 드래그가 시작되기 전에 틱이 미리보기를 새로 깔았으면 누른 칸은 이미 목록에 없다.
            if (vm.ArrangeItems.IndexOf(_pressed) < 0)
            {
                _pressed = null;
                return;
            }

            _arranging = true;
            vm.BeginArrangeDrag(_pressed);
            ArrangeList.CaptureMouse();
        }

        int target = IndexAt(at);
        int from = vm.ArrangeItems.IndexOf(_pressed);
        if (target >= 0 && from >= 0 && target != from)
        {
            vm.MoveArrangeItem(from, target);
        }
    }

    private void OnArrangeMouseUp(object sender, MouseButtonEventArgs e) => FinishArrange();

    // 창 밖에서 놓거나 다른 창이 캡처를 가져가도 드래그는 끝난다 — 그 자리에서 저장한다.
    private void OnArrangeLostCapture(object sender, MouseEventArgs e) => FinishArrange();

    private void FinishArrange()
    {
        bool wasArranging = _arranging;
        _arranging = false;
        _pressed = null;
        if (!wasArranging)
        {
            return;
        }

        if (ArrangeList.IsMouseCaptured)
        {
            ArrangeList.ReleaseMouseCapture(); // LostMouseCapture 로 다시 들어와도 위에서 걸러진다
        }

        (DataContext as CooldownPickerViewModel)?.EndArrangeDrag();
    }

    /// <summary>포인터가 올라가 있는 칸의 인덱스(칸 바깥이면 -1 — 마지막 줄의 빈 자리 등). 칸 크기가 모두 같아서
    /// 한 칸을 옮기고 나면 포인터는 곧 끌던 칸 위에 놓이고, 그래서 경계에서 두 칸이 번갈아 자리를 바꾸며
    /// 떨지 않는다.</summary>
    private int IndexAt(Point at)
    {
        for (int i = 0; i < ArrangeList.Items.Count; i++)
        {
            if (ArrangeList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement cell
                && cell.TransformToAncestor(ArrangeList).TransformBounds(new Rect(cell.RenderSize)).Contains(at))
            {
                return i;
            }
        }

        return -1;
    }
}
