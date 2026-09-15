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
}
