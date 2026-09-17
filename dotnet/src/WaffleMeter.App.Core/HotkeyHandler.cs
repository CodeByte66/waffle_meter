using System.Runtime.InteropServices;
using WaffleMeter.Services;

namespace WaffleMeter.App.Core;

/// <summary>A modifier + virtual-key combo (Kotlin HotkeyHandler.HotkeyCombo). Persisted as
/// "<c>modifiers=M,vkCode=V</c>".</summary>
public sealed record HotkeyCombo(int Modifiers, int VkCode)
{
    public override string ToString() => $"modifiers={Modifiers},vkCode={VkCode}";

    public static HotkeyCombo? TryParse(string s)
    {
        try
        {
            var map = new Dictionary<string, int>();
            foreach (string part in s.Split(','))
            {
                string[] kv = part.Split('=');
                if (kv.Length != 2)
                {
                    return null;
                }

                map[kv[0].Trim()] = int.Parse(kv[1].Trim());
            }

            if (!map.TryGetValue("modifiers", out int modifiers) || !map.TryGetValue("vkCode", out int vkCode))
            {
                return null;
            }

            return new HotkeyCombo(modifiers, vkCode);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Verbatim port of Kotlin <c>config.HotkeyHandler</c>: registers up to three global hotkeys (reset
/// combat / toggle visibility / toggle click-through) and pumps a message loop on a dedicated thread,
/// dispatching to callbacks. Combos persist via <see cref="PropertyHandler"/> (keys hotkey/hideHotkey/
/// clickThroughHotkey) with the same defaults (Ctrl+R / Ctrl+H / Ctrl+T). Any combo may be left
/// unassigned (<c>null</c>, persisted as "<c>none</c>"), in which case no global hotkey is registered
/// for that action. The WPF app wires the callbacks to overlay actions.
/// </summary>
public sealed class HotkeyHandler : IDisposable
{
    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;

    private const uint WmHotkey = 0x0312;
    private const uint PmRemove = 0x0001;
    private const int ResetId = 1;
    private const int VisibilityId = 2;
    private const int ClickThroughId = 3;
    private const int DummyToggleId = 4;
    private const int DummyResetId = 5;
    private const int SplitUiId = 6;
    private const int AetherListId = 7;

    // A held global hotkey auto-repeats: while the combo stays down Windows posts WM_HOTKEY at the keyboard
    // repeat RATE (up to ~30/s, i.e. ~33ms apart), and there is no key-up message to mark the release. Collapse
    // that repeat STREAM into a single action: fire on the leading edge, then ignore further WM_HOTKEY for the
    // same id while they keep arriving within this window (the timestamp is refreshed on EVERY message below, so
    // a continuous stream keeps extending the quiet window and never re-fires).
    // The window must be LONGER than the fastest auto-repeat interval (~33ms) yet SHORTER than a deliberate
    // re-tap, or it swallows the user's real second press. The original 400ms was far too long: hiding with
    // Ctrl+H then pressing again within 400ms to show was suppressed as if it were auto-repeat, so the overlay
    // stayed hidden ("숨긴 뒤 다시 눌러도 안 나옴"). 60ms clears the 33ms stream with margin while passing every
    // deliberate tap. Toggle parity is no longer the guard's job — ToggleVisibility keys off the window's real
    // parked state, so any press that DOES fire is self-correcting (that removed the even/odd-cancel motive for
    // the long window).
    internal const long HotkeyRepeatSuppressMs = 60;

    private const string KeyReset = "hotkey";
    private const string KeyVisibility = "hideHotkey";
    private const string KeyClickThrough = "clickThroughHotkey";
    private const string KeyDummyToggle = "dummyToggleHotkey";
    private const string KeyDummyReset = "dummyResetHotkey";
    private const string KeySplitUi = "splitUiHotkey";
    private const string KeyAetherList = "aetherListHotkey";

    // Persisted marker for an explicitly-unassigned hotkey. Distinct from "property never set" (→ default)
    // and from a corrupt/unparseable value (→ default): an unassigned combo registers no global hotkey.
    private const string NoneSentinel = "none";

    private static readonly HotkeyCombo DefaultReset = new(ModControl, 0x52);        // Ctrl+R
    private static readonly HotkeyCombo DefaultVisibility = new(ModControl, 0x48);   // Ctrl+H
    private static readonly HotkeyCombo DefaultClickThrough = new(ModControl, 0x54); // Ctrl+T

    private readonly PropertyHandler _props;
    private volatile HotkeyCombo? _reset;
    private volatile HotkeyCombo? _visibility;
    private volatile HotkeyCombo? _clickThrough;
    private volatile HotkeyCombo? _dummyToggle;
    private volatile HotkeyCombo? _dummyReset;
    private volatile HotkeyCombo? _splitUi;
    private volatile HotkeyCombo? _aetherList;
    private Thread? _listener;
    private volatile bool _running;
    private readonly Dictionary<int, long> _lastHotkeyTick = new(); // per-id leading-edge debounce; listener-thread-only

    public Action? OnReset { get; set; }
    public Action? OnVisibility { get; set; }
    public Action? OnClickThrough { get; set; }
    public Action? OnDummyToggle { get; set; }
    public Action? OnDummyReset { get; set; }

    /// <summary>UI 분리모드 켜기/끄기. 기본 미지정 — 인게임 키와 겹칠 위험이 있는 조합을 우리가 고르지 않는다.</summary>
    public Action? OnSplitUi { get; set; }

    /// <summary>컨텐츠 관리 창 열기/닫기. 같은 이유로 기본 미지정 — 사용자가 단축키 탭에서 고른다.</summary>
    public Action? OnAetherList { get; set; }

    public HotkeyHandler(PropertyHandler props)
    {
        _props = props;
        ReadAll();
    }

    /// <summary>
    /// Re-read the combos from the properties file and re-register them. For the settings import.
    /// <para>⚠ <c>RegisterHotKey</c> failure is silent by design here, so a combo that collides with another
    /// program simply stops working with no error anywhere. That is exactly why hotkeys are carried only by the
    /// full backup (moving to your own new PC) and never by a code you hand to someone else.</para>
    /// </summary>
    public void Reload()
    {
        ReadAll();
        if (_running)
        {
            Stop();
            Start();
        }
    }

    private void ReadAll()
    {
        _reset = Load(KeyReset, DefaultReset);
        _visibility = Load(KeyVisibility, DefaultVisibility);
        _clickThrough = Load(KeyClickThrough, DefaultClickThrough);
        _dummyToggle = LoadOptional(KeyDummyToggle); // 허수아비 hotkeys ship UNASSIGNED — user opts in via the tab
        _dummyReset = LoadOptional(KeyDummyReset);
        _splitUi = LoadOptional(KeySplitUi); // 분리모드도 UNASSIGNED 출고 — 사용자가 단축키 탭에서 고른다
        _aetherList = LoadOptional(KeyAetherList); // 컨텐츠 관리도 UNASSIGNED 출고
    }

    public HotkeyCombo? Reset => _reset;
    public HotkeyCombo? Visibility => _visibility;
    public HotkeyCombo? ClickThrough => _clickThrough;
    public HotkeyCombo? DummyToggle => _dummyToggle;
    public HotkeyCombo? DummyReset => _dummyReset;
    public HotkeyCombo? SplitUi => _splitUi;
    public HotkeyCombo? AetherList => _aetherList;

    /// <summary>Set (or with <c>null</c>, unassign) the reset hotkey; persists and re-registers live.</summary>
    public void SetReset(HotkeyCombo? combo) => Update(v => _reset = v, KeyReset, combo);
    public void SetVisibility(HotkeyCombo? combo) => Update(v => _visibility = v, KeyVisibility, combo);
    public void SetClickThrough(HotkeyCombo? combo) => Update(v => _clickThrough = v, KeyClickThrough, combo);
    public void SetDummyToggle(HotkeyCombo? combo) => Update(v => _dummyToggle = v, KeyDummyToggle, combo);
    public void SetDummyReset(HotkeyCombo? combo) => Update(v => _dummyReset = v, KeyDummyReset, combo);
    public void SetSplitUi(HotkeyCombo? combo) => Update(v => _splitUi = v, KeySplitUi, combo);
    public void SetAetherList(HotkeyCombo? combo) => Update(v => _aetherList = v, KeyAetherList, combo);

    private void Update(Action<HotkeyCombo?> assign, string key, HotkeyCombo? value)
    {
        assign(value);
        _props.SetProperty(key, value?.ToString() ?? NoneSentinel);
        if (_running)
        {
            Stop();
            Start();
        }
    }

    private HotkeyCombo? Load(string key, HotkeyCombo fallback)
    {
        string? raw = _props.GetProperty(key);
        if (raw == null)
        {
            return fallback; // never set → default
        }

        if (raw == NoneSentinel)
        {
            return null; // explicitly unassigned → no hotkey (do NOT fall back to the default)
        }

        return HotkeyCombo.TryParse(raw) ?? fallback;
    }

    /// <summary>Load a hotkey that ships UNASSIGNED: never-set OR the "none" marker OR a corrupt value all yield
    /// null (no global hotkey); only a valid persisted combo registers. Unlike <see cref="Load"/> there is no
    /// default combo to fall back to.</summary>
    private HotkeyCombo? LoadOptional(string key)
    {
        string? raw = _props.GetProperty(key);
        return raw == null || raw == NoneSentinel ? null : HotkeyCombo.TryParse(raw);
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _listener = new Thread(ListenLoop) { IsBackground = true, Name = "HotkeyListener" };
        _listener.Start();
    }

    private void ListenLoop()
    {
        // Register only the assigned combos (a null combo = unassigned → no global hotkey; RegisterHotKey
        // is short-circuited past for nulls). Registration may also fail when a combo is already owned by
        // another app. Either way we DON'T tear the thread down: the message loop stays alive so a later
        // SetX (which does Stop()/Start() only while _running) can re-register — even from the all-unassigned
        // state. The loop just idles (PeekMessage + sleep) when nothing is registered.
        if (_reset is { } r)
        {
            RegisterHotKey(IntPtr.Zero, ResetId, (uint)r.Modifiers, (uint)r.VkCode);
        }

        if (_visibility is { } v)
        {
            RegisterHotKey(IntPtr.Zero, VisibilityId, (uint)v.Modifiers, (uint)v.VkCode);
        }

        if (_clickThrough is { } c)
        {
            RegisterHotKey(IntPtr.Zero, ClickThroughId, (uint)c.Modifiers, (uint)c.VkCode);
        }

        if (_dummyToggle is { } dt)
        {
            RegisterHotKey(IntPtr.Zero, DummyToggleId, (uint)dt.Modifiers, (uint)dt.VkCode);
        }

        if (_dummyReset is { } dr)
        {
            RegisterHotKey(IntPtr.Zero, DummyResetId, (uint)dr.Modifiers, (uint)dr.VkCode);
        }

        if (_splitUi is { } su)
        {
            RegisterHotKey(IntPtr.Zero, SplitUiId, (uint)su.Modifiers, (uint)su.VkCode);
        }

        if (_aetherList is { } al)
        {
            RegisterHotKey(IntPtr.Zero, AetherListId, (uint)al.Modifiers, (uint)al.VkCode);
        }

        try
        {
            while (_running)
            {
                if (PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, PmRemove))
                {
                    if (msg.message == WmHotkey && PassesRepeatGuard((int)msg.wParam))
                    {
                        switch ((int)msg.wParam)
                        {
                            case ResetId:
                                OnReset?.Invoke();
                                break;
                            case VisibilityId:
                                OnVisibility?.Invoke();
                                break;
                            case ClickThroughId:
                                OnClickThrough?.Invoke();
                                break;
                            case DummyToggleId:
                                OnDummyToggle?.Invoke();
                                break;
                            case DummyResetId:
                                OnDummyReset?.Invoke();
                                break;
                            case SplitUiId:
                                OnSplitUi?.Invoke();
                                break;
                            case AetherListId:
                                OnAetherList?.Invoke();
                                break;
                        }
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, ResetId);
            UnregisterHotKey(IntPtr.Zero, VisibilityId);
            UnregisterHotKey(IntPtr.Zero, ClickThroughId);
            UnregisterHotKey(IntPtr.Zero, DummyToggleId);
            UnregisterHotKey(IntPtr.Zero, DummyResetId);
            UnregisterHotKey(IntPtr.Zero, SplitUiId);
            UnregisterHotKey(IntPtr.Zero, AetherListId);
        }
    }

    /// <summary>Leading-edge debounce against a held hotkey's auto-repeat. Returns true the first time an id
    /// fires after a quiet gap, false while the same id keeps arriving within <see cref="HotkeyRepeatSuppressMs"/>.
    /// The timestamp is updated on EVERY message, so a continuous repeat stream keeps extending the quiet
    /// window and never re-fires. Runs only on the listener thread, so it needs no synchronization.</summary>
    private bool PassesRepeatGuard(int id)
    {
        long now = Environment.TickCount64;
        bool fire = ShouldFire(_lastHotkeyTick.TryGetValue(id, out long last), last, now);
        _lastHotkeyTick[id] = now; // unconditional (incl. suppressed presses): a held auto-repeat stream keeps
                                   // extending the quiet window so the whole burst collapses to one action
        return fire;
    }

    /// <summary>Pure leading-edge decision, split out so the timing can be unit-tested without a real clock:
    /// fire on the first press for an id, and on any later press whose gap since the previous WM_HOTKEY is at
    /// least <see cref="HotkeyRepeatSuppressMs"/>; a shorter gap is treated as OS auto-repeat and suppressed.</summary>
    internal static bool ShouldFire(bool hasPrevious, long previousTick, long nowTick) =>
        !hasPrevious || nowTick - previousTick >= HotkeyRepeatSuppressMs;

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _listener?.Join(1000);
        _listener = null;
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }
}
