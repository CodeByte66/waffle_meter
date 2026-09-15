using System.Windows;
using System.Windows.Controls;

namespace WaffleMeter.App.Wpf.Controls;

/// <summary>
/// 미터 행의 기여도 게이지 한 칸. 채움과 게이지 스킨 장식을 한 덩어리로 소유한다.
///
/// <para>공개 프로퍼티는 <b>기하 셋뿐</b>이다 — <see cref="GaugeRadius"/>, <see cref="ContentExclusionLeft"/>,
/// <see cref="MinBandHeight"/>. Brush·Opacity 는 일부러 노출하지 않는다: 레이아웃이 채움 불투명도를
/// 덮어쓸 수 있으면 돈을 낸 사람의 게이지 스킨이 희미해지고 입자만 뜬다. 그 값은 행마다
/// <c>OverlayViewModel</c> 이 계산해(스킨 없음 0.3 / 스킨 있음 0.58) DataContext 로 들어온다.</para>
///
/// <para>DataContext 는 <c>RowViewModel</c> 을 상속받는다. 내부 바인딩(<c>BarRatio</c>, <c>GaugeBrush</c>,
/// <c>GaugeSkinId</c> …)은 전부 거기서 풀린다.</para>
/// </summary>
public partial class RowGaugeCell : UserControl
{
    public RowGaugeCell() => InitializeComponent();

    /// <summary>
    /// 채움 모서리 반경. <b>한 값이</b> 채움 Border 와 <c>GaugeFxLayer.CornerRadius</c>(장식이 자기를
    /// 클립하는 근거)를 동시에 물어야 한다 — 따로 두면 무대(0)에서 장식만 둥근 노치를 남긴다.
    /// </summary>
    public static readonly DependencyProperty GaugeRadiusProperty = DependencyProperty.Register(
        nameof(GaugeRadius), typeof(double), typeof(RowGaugeCell),
        new FrameworkPropertyMetadata(4.0));

    public double GaugeRadius
    {
        get => (double)GetValue(GaugeRadiusProperty);
        set => SetValue(GaugeRadiusProperty, value);
    }

    /// <summary>
    /// 장식을 그리지 않고 비워 둘 왼쪽 폭(rail + 순위칩 + 직업아이콘 묶음의 실폭).
    /// 값은 <c>MeterLayout.GaugeExclusionLeft(id, rowHeight)</c> 가 계산한다 — 예전의 리터럴 72 는
    /// rowHeight 36 전용이라 28 에서 과잉, 80 에서 부족했다.
    /// </summary>
    public static readonly DependencyProperty ContentExclusionLeftProperty = DependencyProperty.Register(
        nameof(ContentExclusionLeft), typeof(double), typeof(RowGaugeCell),
        new FrameworkPropertyMetadata(0.0));

    public double ContentExclusionLeft
    {
        get => (double)GetValue(ContentExclusionLeftProperty);
        set => SetValue(ContentExclusionLeftProperty, value);
    }

    /// <summary>
    /// 장식 대역의 최소 높이. <c>GaugeFxLayer.MeasureOverride</c> 가 0 을 돌려주므로 높이는 부모가 대야
    /// 하는데, 행 카드 Border 의 <c>MinHeight</c> 가 유일한 공급원이라 그 하나가 사라지면 장식이 조용히
    /// 죽는다. 이 프로퍼티는 그 계약을 컨트롤 안에서도 한 번 더 붙잡아 두는 안전망이다(기본 0 = 부모에 위임).
    /// </summary>
    public static readonly DependencyProperty MinBandHeightProperty = DependencyProperty.Register(
        nameof(MinBandHeight), typeof(double), typeof(RowGaugeCell),
        new FrameworkPropertyMetadata(0.0));

    public double MinBandHeight
    {
        get => (double)GetValue(MinBandHeightProperty);
        set => SetValue(MinBandHeightProperty, value);
    }
}
