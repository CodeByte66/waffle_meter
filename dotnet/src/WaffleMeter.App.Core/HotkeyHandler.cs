using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WaffleMeter.Services;

namespace WaffleMeter.App.Core;


/// <summary>A modifier + virtual-key combo (Kotlin HotkeyHandler.HotkeyCombo). Persisted as
/// "<c>modifiers=M,vkCode=V</c>".</summary>
public sealed record HotkeyCombo(int Modifiers, int VkCode)
{
    public override string ToString() => $"modifiers={Modifiers},vkCode={VkCode}";

    /// <summary>
    /// Is this virtual-key a modifier in its own right, i.e. unusable as a combo's main key?
    /// <para>🔑 The left/right pairs (0xA0–0xA5) are the load-bearing half. WPF's <c>Key</c> enum has no
    /// generic Ctrl/Shift/Alt member, so <c>KeyInterop.VirtualKeyFromKey</c> on a real keypress returns
    /// VK_LCONTROL (0xA2) and never VK_CONTROL (0x11). A filter that lists only the generic codes therefore
    /// matches NOTHING a user can actually press — which is exactly how "CTRL + VK_162" got stored.</para>
    /// <para>The generic codes stay listed anyway: they are what a hand-edited properties file or a combo
    /// ported from the old Kotlin build can contain, and they are no more registrable than the specific ones.</para>
    /// </summary>
    public static bool IsPureModifierVk(int vk) => vk is
        0x10 or 0x11 or 0x12          // VK_SHIFT / VK_CONTROL / VK_MENU — generic, never produced by WPF
        or 0xA0 or 0xA1               // VK_LSHIFT / VK_RSHIFT
        or 0xA2 or 0xA3               // VK_LCONTROL / VK_RCONTROL
        or 0xA4 or 0xA5               // VK_LMENU / VK_RMENU
        or 0x5B or 0x5C;              // VK_LWIN / VK_RWIN

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

            // A combo whose MAIN key is itself a modifier is not a usable hotkey, and until the capture box
            // was fixed it was easy to save one: pressing Ctrl to start a combo stored "CTRL + VK_162"
            // (VK_LCONTROL) on the way to the real key. RegisterHotKey accepts it, so the action then fired on
            // bare Ctrl presses — the "컨텐츠 관리가 되다말다" report. Rejecting it HERE retires the values
            // already on disk: Load falls back to the default and LoadOptional yields 미지정, either of which
            // the user can reassign. Without this the fix would only help installs that never hit the bug.
            return IsPureModifierVk(vkCode) ? null : new HotkeyCombo(modifiers, vkCode);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 단축키 한 칸이 <b>지금 실제로 동작하지 않는</b> 사유. 둘 다 종전에는 완전히 무음이라,
/// 사용자 눈에는 "설정엔 들어가 있는데 안 먹는다"로만 보였다(3.1.0 제보, 2026-09-19).
/// </summary>
public enum HotkeyIssue
{
    None = 0,

    /// <summary>
    /// 저장돼 있던 값이 <b>지금은 쓸 수 없는 조합</b>이라 해제됐다. 옛 빌드가 수식키 단독
    /// (예: <c>CTRL + VK_LCONTROL</c>)을 저장해 둔 설치본이 3.1.0 이상으로 올라올 때 발생한다 —
    /// <see cref="HotkeyCombo.TryParse"/> 가 그런 값을 거부하므로 그 칸은 조용히 미지정이 된다.
    /// 조치는 <b>다시 지정</b>.
    /// </summary>
    Retired,

    /// <summary>
    /// 조합 자체는 멀쩡한데 OS 등록이 실패했다. 대개 다른 프로그램이 같은 조합을 먼저 잡고 있다
    /// (<c>ERROR_HOTKEY_ALREADY_REGISTERED</c> = 1409). 조치는 <b>다른 조합으로 변경</b>.
    /// </summary>
    RegisterFailed,
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

    /// <summary>칸(id)별 문제 상태. 로드는 생성 스레드, 등록은 리스너 스레드, 읽기는 UI 스레드라 concurrent.</summary>
    private readonly ConcurrentDictionary<int, HotkeyIssue> _issues = new();

    /// <summary>문제 상태가 바뀌었다. ⚠️ <b>리스너 스레드에서</b> 올 수 있으니 구독자가 마셜해야 한다.</summary>
    public event Action? IssuesChanged;

    public HotkeyIssue ResetIssue => IssueOf(ResetId);
    public HotkeyIssue VisibilityIssue => IssueOf(VisibilityId);
    public HotkeyIssue ClickThroughIssue => IssueOf(ClickThroughId);
    public HotkeyIssue DummyToggleIssue => IssueOf(DummyToggleId);
    public HotkeyIssue DummyResetIssue => IssueOf(DummyResetId);
    public HotkeyIssue SplitUiIssue => IssueOf(SplitUiId);
    public HotkeyIssue AetherListIssue => IssueOf(AetherListId);

    private HotkeyIssue IssueOf(int id) => _issues.TryGetValue(id, out HotkeyIssue v) ? v : HotkeyIssue.None;

    /// <summary>같은 값이면 이벤트를 내지 않는다 — 설정창이 열릴 때마다 Stop/Start 가 도는데 매번 깜빡이면 안 된다.</summary>
    private void SetIssue(int id, HotkeyIssue issue)
    {
        HotkeyIssue previous = IssueOf(id);
        if (previous == issue)
        {
            return;
        }

        if (issue == HotkeyIssue.None)
        {
            _issues.TryRemove(id, out _);
        }
        else
        {
            _issues[id] = issue;
        }

        IssuesChanged?.Invoke();
    }

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
        _reset = Load(KeyReset, DefaultReset, ResetId);
        _visibility = Load(KeyVisibility, DefaultVisibility, VisibilityId);
        _clickThrough = Load(KeyClickThrough, DefaultClickThrough, ClickThroughId);
        _dummyToggle = LoadOptional(KeyDummyToggle, DummyToggleId); // 허수아비 hotkeys ship UNASSIGNED — user opts in via the tab
        _dummyReset = LoadOptional(KeyDummyReset, DummyResetId);
        _splitUi = LoadOptional(KeySplitUi, SplitUiId); // 분리모드도 UNASSIGNED 출고 — 사용자가 단축키 탭에서 고른다
        _aetherList = LoadOptional(KeyAetherList, AetherListId); // 컨텐츠 관리도 UNASSIGNED 출고
    }

    public HotkeyCombo? Reset => _reset;
    public HotkeyCombo? Visibility => _visibility;
    public HotkeyCombo? ClickThrough => _clickThrough;
    public HotkeyCombo? DummyToggle => _dummyToggle;
    public HotkeyCombo? DummyReset => _dummyReset;
    public HotkeyCombo? SplitUi => _splitUi;
    public HotkeyCombo? AetherList => _aetherList;

    /// <summary>Set (or with <c>null</c>, unassign) the reset hotkey; persists and re-registers live.</summary>
    public void SetReset(HotkeyCombo? combo) => Update(v => _reset = v, KeyReset, ResetId, combo);
    public void SetVisibility(HotkeyCombo? combo) => Update(v => _visibility = v, KeyVisibility, VisibilityId, combo);
    public void SetClickThrough(HotkeyCombo? combo) => Update(v => _clickThrough = v, KeyClickThrough, ClickThroughId, combo);
    public void SetDummyToggle(HotkeyCombo? combo) => Update(v => _dummyToggle = v, KeyDummyToggle, DummyToggleId, combo);
    public void SetDummyReset(HotkeyCombo? combo) => Update(v => _dummyReset = v, KeyDummyReset, DummyResetId, combo);
    public void SetSplitUi(HotkeyCombo? combo) => Update(v => _splitUi = v, KeySplitUi, SplitUiId, combo);
    public void SetAetherList(HotkeyCombo? combo) => Update(v => _aetherList = v, KeyAetherList, AetherListId, combo);

    private void Update(Action<HotkeyCombo?> assign, string key, int id, HotkeyCombo? value)
    {
        assign(value);
        _props.SetProperty(key, value?.ToString() ?? NoneSentinel);
        // 사용자가 직접 고른 값이다 — 은퇴 경고는 여기서 사라져야 한다. 등록 실패는
        // 아래 Start() 가 다시 판정한다(리스너가 돌 때만 의미가 있다).
        SetIssue(id, HotkeyIssue.None);
        if (_running)
        {
            Stop();
            Start();
        }
    }

    /// <summary>
    /// 저장된 값이 있었는데 <see cref="HotkeyCombo.TryParse"/> 가 거부했는가. 옛 빌드가 써 둔 수식키 단독
    /// 조합이 여기 걸린다 — 종전에는 <b>조용히</b> 버려져서 사용자는 이유 없이 단축키를 잃었다.
    /// </summary>
    private void NoteRetired(int id, string? raw, HotkeyCombo? parsed) =>
        SetIssue(id, raw != null && raw != NoneSentinel && parsed == null ? HotkeyIssue.Retired : HotkeyIssue.None);

    private HotkeyCombo? Load(string key, HotkeyCombo fallback, int id)
    {
        string? raw = _props.GetProperty(key);
        if (raw == null)
        {
            SetIssue(id, HotkeyIssue.None);
            return fallback; // never set → default
        }

        if (raw == NoneSentinel)
        {
            SetIssue(id, HotkeyIssue.None);
            return null; // explicitly unassigned → no hotkey (do NOT fall back to the default)
        }

        HotkeyCombo? parsed = HotkeyCombo.TryParse(raw);
        // 기본값으로 되돌아가더라도 **사용자가 고른 값은 사라진 것**이라 알려 준다. 안 그러면
        // "내가 지정한 조합이 아닌 게 걸려 있다"를 설명할 방법이 없다.
        NoteRetired(id, raw, parsed);
        return parsed ?? fallback;
    }

    /// <summary>Load a hotkey that ships UNASSIGNED: never-set OR the "none" marker OR a corrupt value all yield
    /// null (no global hotkey); only a valid persisted combo registers. Unlike <see cref="Load"/> there is no
    /// default combo to fall back to.</summary>
    private HotkeyCombo? LoadOptional(string key, int id)
    {
        string? raw = _props.GetProperty(key);
        HotkeyCombo? parsed = raw == null || raw == NoneSentinel ? null : HotkeyCombo.TryParse(raw);
        NoteRetired(id, raw, parsed);
        return parsed;
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
        //
        // 🔑 반환값을 본다. 종전에는 7번 모두 버려서, 다른 프로그램이 같은 조합을 먼저 잡고 있으면
        // **그 칸만 죽고 설정 화면에는 멀쩡히 조합이 표시**됐다 — "설정엔 들어가 있는데 안 먹는다"의 정체다.
        Register(ResetId, _reset);
        Register(VisibilityId, _visibility);
        Register(ClickThroughId, _clickThrough);
        Register(DummyToggleId, _dummyToggle);
        Register(DummyResetId, _dummyReset);
        Register(SplitUiId, _splitUi);
        Register(AetherListId, _aetherList);

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

    /// <summary>
    /// 한 칸을 등록하고 결과를 기록한다. 미지정이면 등록하지 않고 상태도 비운다.
    /// <para>⚠️ 등록 실패는 <b>여기서만</b> 판정된다 — <c>RegisterHotKey</c> 는 리스너 스레드에서 불러야
    /// 그 스레드가 <c>WM_HOTKEY</c> 를 받으므로, 상태도 그 스레드에서 쓰인다(그래서 저장소가 concurrent 다).</para>
    /// <para>⚠️ <see cref="HotkeyIssue.Retired"/> 는 덮지 않는다. 그건 로드 시점의 사유이고, 해제된 칸은
    /// 애초에 등록 대상이 아니라 여기 오지 않는다 — 와도 combo 가 null 이라 아래 early-return 에 걸린다.</para>
    /// </summary>
    private void Register(int id, HotkeyCombo? combo)
    {
        if (combo == null)
        {
            // 미지정이거나 은퇴된 칸. 은퇴 사유는 유지하고, 그 외에는 비운다.
            if (IssueOf(id) != HotkeyIssue.Retired)
            {
                SetIssue(id, HotkeyIssue.None);
            }

            return;
        }

        bool ok = RegisterHotKey(IntPtr.Zero, id, (uint)combo.Modifiers, (uint)combo.VkCode);
        SetIssue(id, ok ? HotkeyIssue.None : HotkeyIssue.RegisterFailed);
    }

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
