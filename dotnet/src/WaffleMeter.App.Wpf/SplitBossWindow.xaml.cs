using System.Windows;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// UI 분리모드의 보스칸 창. 본체 미터가 숨은 동안 보스 HP(또는 대기 카드)를 화면 어디에나 둘 수 있게 한다.
/// <para>DataContext 는 본체와 <b>같은</b> <see cref="OverlayViewModel"/> 인스턴스다 —
/// <see cref="Controls.MeterRowsView"/> 주석의 이유와 같다.</para>
/// </summary>
public partial class SplitBossWindow : OverlayPanelWindow
{
    public SplitBossWindow() => InitializeComponent();

    // 분리모드에선 본체가 내내 faded 다 — NameFxSheen 이 '본체 호스트' 하나만 보면 연출이 멈춘 것으로
    // 읽는다. 이 창이 스스로 호스트로 등록해야 집계('등록 호스트 ≥1 이고 전부 숨김일 때만 정지')가 성립한다.
    protected override void OnPresented() => NameFxSheen.SetHostVisible(this, true);

    protected override void OnParked() => NameFxSheen.SetHostVisible(this, false);

    /// <summary>전투 기록 패널 열기. 헤더가 사라지면서 이 창이 유일한 진입점 중 하나가 됐다.</summary>
    public event Action? HistoryRequested;

    /// <summary>설정 창 열기 — 분리모드에서 나머지 기능은 전부 설정 안에 있다.</summary>
    public event Action? SettingsRequested;

    private void OnHistory(object sender, RoutedEventArgs e) => HistoryRequested?.Invoke();

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
}
