using System.Windows;
using System.Windows.Threading;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// The party join-request overlay panel. Reuses the <see cref="OverlayPanelWindow"/> windowing and adds
/// a 250ms timer (running only while presented) that drives the per-row countdown via
/// <see cref="JoinRequestViewModel.Tick"/>.
/// </summary>
public partial class JoinRequestPanel : OverlayPanelWindow
{
    private readonly DispatcherTimer _ticker;

    /// <summary>Raised when the ⚙ button is clicked (App opens the skill-settings flyout).</summary>
    public event Action? SettingsRequested;

    /// <summary>Each 250ms beat, before the rows repaint. The App uses it to let <c>JoinRequestStore</c> apply
    /// a 0x9709 whose matching admit never showed up — the store has no clock of its own and this timer is
    /// already running whenever a card can be on screen.</summary>
    public event Action? Heartbeat;

    public JoinRequestPanel()
    {
        InitializeComponent();
        _ticker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _ticker.Tick += (_, _) =>
        {
            Heartbeat?.Invoke();
            (DataContext as JoinRequestViewModel)?.Tick();
        };
    }

    protected override void OnPresented() => _ticker.Start();

    protected override void OnParked() => _ticker.Stop();

    private void OnSettingsButton(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
}
