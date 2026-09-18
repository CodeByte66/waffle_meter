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
        GotKeyboardFocus += (_, _) => Text = "키 입력…";
        LostKeyboardFocus += (_, _) => UpdateText();
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

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (HotkeyCombo.IsPureModifierVk(vk))
        {
            // A modifier was pressed on its own — it is the START of a combo, not the combo. Wait for the
            // real key. The list this used to consult held only the GENERIC codes (VK_CONTROL 0x11 …), which
            // WPF never reports: Key has LeftCtrl/RightCtrl, so KeyInterop hands back VK_LCONTROL (0xA2).
            // So the guard matched nothing, and simply reaching for Ctrl wrote "CTRL + VK_162" into the box.
            return;
        }

        // Single key (no modifier) is allowed; Ctrl/Alt combine if held. Shift/Win are ignored as
        // modifiers (RegisterHotKey + the label formatter only model Ctrl/Alt).
        ModifierKeys mods = Keyboard.Modifiers;
        int modifiers = (mods.HasFlag(ModifierKeys.Control) ? HotkeyHandler.ModControl : 0)
                        | (mods.HasFlag(ModifierKeys.Alt) ? HotkeyHandler.ModAlt : 0);
        Combo = new HotkeyCombo(modifiers, vk);
    }

    private static void OnComboChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HotkeyCaptureBox)d).UpdateText();

    private void UpdateText() => Text = Combo != null ? HotkeyFormat.Format(Combo.Modifiers, Combo.VkCode) : "미지정";
}
