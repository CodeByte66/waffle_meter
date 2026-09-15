namespace WaffleMeter.App.Wpf;

/// <summary>
/// UI 분리모드의 미터 행 창. 본체와 같은 <see cref="OverlayViewModel"/> 인스턴스를 공유한다.
/// </summary>
public partial class SplitRowsWindow : OverlayPanelWindow
{
    public SplitRowsWindow() => InitializeComponent();

    // 분리모드에선 본체가 내내 faded 다 — NameFxSheen 이 '본체 호스트' 하나만 보면 연출이 멈춘 것으로
    // 읽는다. 이 창이 스스로 호스트로 등록해야 집계('등록 호스트 ≥1 이고 전부 숨김일 때만 정지')가 성립한다.
    protected override void OnPresented() => NameFxSheen.SetHostVisible(this, true);

    protected override void OnParked() => NameFxSheen.SetHostVisible(this, false);
}
