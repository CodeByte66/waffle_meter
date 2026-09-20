using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WaffleMeter.App.Core;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// Interactive hotkey rebinding box: focus to capture, press a key. A single key (no modifier) is
/// allowed, as are Ctrl/Alt combos; Shift/Win are ignored as modifiers and pure modifier presses are
/// skipped (wait for the real key). Maps the WPF key to a Win32 VK so the stored combo matches the JS
/// keyCode. Two-way <see cref="Combo"/> binds to the view model's pending hotkey. A null combo means
/// "unassigned" — shown as "미지정"; <see cref="Unassign"/> (wired to the row's ✕ button) resets to it.
/// </summary>
public sealed class HotkeyCaptureBox : TextBox
{
    private const string Prompt = "키 입력…";

    /// <summary>설정창의 빨간 경고줄과 같은 색(HotkeyWarning 스타일).</summary>
    private static readonly System.Windows.Media.Brush SwallowedBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x7A, 0x7A));

    /// <summary>
    /// 캡처가 시작/끝날 때 전역 핫키를 잠시 내렸다 올린다. <see cref="App"/> 이 기동 때 한 번 꽂는다.
    ///
    /// <para>🔑 <b>없으면 이미 등록된 조합은 캡처가 아예 불가능하다.</b> <c>RegisterHotKey</c> 로 잡아 둔
    /// 조합은 OS 가 가로채 <c>WM_HOTKEY</c> 로 보내므로, 그 키 입력은 포커스를 가진 컨트롤에 <b>도달하지
    /// 않는다</b>. 그래서 사용자가 지금 쓰고 있는 조합(예: 컨텐츠 관리에 걸린 Ctrl+F11)을 다시 지정하려고
    /// 누르면 박스는 "키 입력…" 그대로인 채 <b>미터가 숨겨지거나 패널이 토글된다</b> — 사용자 경험은
    /// 정확히 "이 조합은 입력이 안 된다"이고, 이게 3.1.0 제보의 한 축이다(2026-09-19).</para>
    /// </summary>
    public static Action<bool>? SuspendGlobalHotkeys { get; set; }

    public static readonly DependencyProperty ComboProperty = DependencyProperty.Register(
        nameof(Combo),
        typeof(HotkeyCombo),
        typeof(HotkeyCaptureBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnComboChanged));

    public HotkeyCaptureBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        GotKeyboardFocus += (_, _) =>
        {
            _sawKeySinceFocus = false;
            _capturedSinceFocus = false;
            ClearValue(ForegroundProperty); // 지난번 경고색을 지운다
            Text = Prompt;
            SuspendGlobalHotkeys?.Invoke(true);
        };
        LostKeyboardFocus += (_, _) =>
        {
            SuspendGlobalHotkeys?.Invoke(false);

            // 키는 눌렀는데 아무것도 안 잡혔고 포커스가 다른 프로세스로 넘어갔다 = 그 조합을 남이 쥐고 있다.
            // (Alt+Tab 처럼 Windows 자신이 가져간 경우도 같은 신호인데, 문구가 그 경우에도 사실이다.)
            if (_sawKeySinceFocus && !_capturedSinceFocus && ForegroundLeftThisProcess())
            {
                Foreground = SwallowedBrush;
                Text = "다른 프로그램이 먼저 쓰는 키입니다";
                return;
            }

            ClearValue(ForegroundProperty);
            UpdateText();
        };
    }

    /// <summary>
    /// 이번 포커스 동안 키를 한 번이라도 받았는가 / 조합이 실제로 잡혔는가.
    /// <para>🔑 둘을 나눠 두는 이유: <b>다른 프로그램이 전역 등록한 조합은 우리 박스에 도달하지 않는다.</b>
    /// 예를 들어 NVIDIA 오버레이가 Alt+Z 를 쥐고 있으면, 수식키 Alt 의 KeyDown 은 평범한 입력이라 우리에게
    /// 오지만 완성 키 Z 는 OS 가 그쪽으로 보내 버린다. 결과는 "눌렀는데 아무 일도 안 일어나고 포커스만
    /// 뺏긴다" 이고, 종전에는 그 상태에 아무 설명이 없었다(실사용 확인, 2026-09-20).</para>
    /// <para>⚠️ 이건 <see cref="HotkeyIssue"/> 로 못 다룬다 — 저장된 상태가 아니라 <b>캡처 순간의 사건</b>이고,
    /// 애초에 값이 안 잡혔으니 그 칸에 남길 것도 없다. 그래서 박스 자체에 표시한다.</para>
    /// </summary>
    private bool _sawKeySinceFocus;
    private bool _capturedSinceFocus;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>포커스가 <b>다른 프로세스</b>로 넘어갔는가. 우리 창 안에서 옮겨간 것(다른 칸 클릭)은 제외한다.</summary>
    private static bool ForegroundLeftThisProcess()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(fg, out uint pid);
        return pid != 0 && pid != (uint)Environment.ProcessId;
    }

    public HotkeyCombo? Combo
    {
        get => (HotkeyCombo?)GetValue(ComboProperty);
        set => SetValue(ComboProperty, value);
    }

    /// <summary>Unassign the hotkey (set it to "미지정"). The two-way binding propagates the null to the
    /// view model; the bound action then registers no global hotkey. (Named to avoid hiding the inherited
    /// <see cref="System.Windows.Controls.TextBox.Clear"/>, which clears text rather than the combo.)</summary>
    public void Unassign() => Combo = null;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        _sawKeySinceFocus = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (HotkeyCombo.IsPureModifierVk(vk))
        {
            // A modifier was pressed on its own — it is the START of a combo, not the combo. Wait for the
            // real key. The list this used to consult held only the GENERIC codes (VK_CONTROL 0x11 …), which
            // WPF never reports: Key has LeftCtrl/RightCtrl, so KeyInterop hands back VK_LCONTROL (0xA2).
            // So the guard matched nothing, and simply reaching for Ctrl wrote "CTRL + VK_162" into the box.
            //
            // 🔑 ⚠️ 그런데 v3.1.0 은 여기서 **조용히** 빠져나갔다. 종전에는 (틀린) 값이나마 즉시 표시됐던
            // 자리가 "키 입력…" 그대로 멈추는 바람에, 사용자에게는 "컨트롤키가 입력이 안 된다"로 읽혔다
            // (3.1.0 실사용자 제보, 2026-09-19). 사라진 것은 기능이 아니라 시각 피드백이다 —
            // Ctrl 을 누른 채 진짜 키를 마저 누르면 조합은 정상으로 들어간다.
            //
            // 그래서 **표시만** 진행 상태로 바꾼다. Combo 는 절대 건드리지 않는다 — 여기서 Combo 를 세우면
            // 3.1.0 이 고친 "CTRL + VK_162" 쓰레기 저장이 그대로 부활한다.
            ShowPendingModifiers();
            return;
        }

        // Single key (no modifier) is allowed; Ctrl/Alt combine if held. Shift/Win are ignored as
        // modifiers (RegisterHotKey + the label formatter only model Ctrl/Alt).
        ModifierKeys mods = Keyboard.Modifiers;
        int modifiers = (mods.HasFlag(ModifierKeys.Control) ? HotkeyHandler.ModControl : 0)
                        | (mods.HasFlag(ModifierKeys.Alt) ? HotkeyHandler.ModAlt : 0);
        Combo = new HotkeyCombo(modifiers, vk);
        _capturedSinceFocus = true;
    }

    /// <summary>수식키에서 손을 떼면 진행 표시를 되돌린다 — 안 그러면 "CTRL + …" 가 남아 조합이 잡힌 것처럼 보인다.</summary>
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (IsKeyboardFocusWithin && !AnyModifierDown())
        {
            Text = Combo != null ? HotkeyFormat.Format(Combo.Modifiers, Combo.VkCode) : Prompt;
        }
    }

    /// <summary>
    /// "CTRL + …" 처럼 <b>진행 중</b>임을 보여 준다. 저장되는 값과 무관한 라벨 전용이다.
    /// <para>⚠️ <see cref="HotkeyFormat.Format"/> 을 쓰지 않는다 — 그건 항상 키 라벨을 붙이므로
    /// vk 0 을 넘기면 "CTRL + VK_0" 같은 게 나온다. 여기 필요한 건 접두사뿐이다.</para>
    /// </summary>
    private void ShowPendingModifiers()
    {
        // ⚠️ Keyboard.Modifiers 는 지금 막 눌린 그 수식키를 아직 반영하지 않았을 수 있다(이 KeyDown 이
        // 바로 그 키다). 실제 키 상태로 메운다.
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                    || Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)
                   || Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);

        var parts = new List<string>();
        if (ctrl)
        {
            parts.Add("CTRL");
        }

        if (alt)
        {
            parts.Add("ALT");
        }

        // Shift/Win 은 조합 수식키로 쓰지 않는다(RegisterHotKey·라벨 포매터가 Ctrl/Alt 만 모델링한다).
        // 그것들만 눌린 상태에서 진행 표시를 내면 "잡히는 중"이라는 거짓 신호가 된다.
        Text = parts.Count == 0 ? Prompt : string.Join(" + ", parts) + " + …";
    }

    private static bool AnyModifierDown() =>
        Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)
        || Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);

    private static void OnComboChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HotkeyCaptureBox)d).UpdateText();

    private void UpdateText() => Text = Combo != null ? HotkeyFormat.Format(Combo.Modifiers, Combo.VkCode) : "미지정";
}
