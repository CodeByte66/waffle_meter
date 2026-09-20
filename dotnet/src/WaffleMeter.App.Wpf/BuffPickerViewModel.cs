using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using WaffleMeter.App.Core;
using WaffleMeter.Data;

namespace WaffleMeter.App.Wpf;

/// <summary>Per-job buff picker: every buff observed on the local player / party, grouped by job, each with a
/// 4-way mode — 알림끔 (off) / 오버레이만 (shown) / 오버레이+음성 (shown + voice) / 음성만 (voice, not shown).
/// Consumable/item buffs never reach this list (the data layer keeps only job-skill codes). Modes persist to two
/// sets: hidden codes and voice codes — hidden&voice = 음성만, hidden-only = 알림끔, voice-only = 오버레이+음성,
/// neither = 오버레이만.</summary>
public sealed class BuffPickerViewModel : INotifyPropertyChanged
{
    private readonly DataManager _data;
    private readonly MeterSettings _settings;
    private readonly HashSet<int> _hidden;
    private readonly HashSet<int> _voice;
    private List<int> _pinned;

    public BuffPickerViewModel(DataManager data, MeterSettings settings)
    {
        _data = data;
        _settings = settings;
        _hidden = MeterSettings.ParseCodeSet(settings.BuffUiHidden);
        _voice = MeterSettings.ParseCodeSet(settings.BuffUiVoice);
        _pinned = MeterSettings.ParseCodeList(settings.BuffUiPinned);
        Rebuild();

        // 🔑 이 이벤트는 **캡처 소비자 스레드**에서 올라온다. 핸들러가 바인딩된 `Groups`(ObservableCollection)를
        // 건드리므로 그 스레드에서 그대로 실행하면 WPF 가 NotSupportedException 을 던진다 — 설정창 버프 탭을
        // 열어 둔 채 전투하면 픽커가 새 버프 코드로 갱신되지 않는 것이 그 증상이다. 디스패처로 넘긴다.
        // 구독/해제가 **같은 델리게이트**여야 Dispose 가 실제로 떼어낸다(람다를 직접 += 하면 -= 가 무효다).
        // 본보기: App.xaml.cs 의 `BuffCatalogChanged += () => Dispatcher.BeginInvoke(...)`.
        _catalogChanged = () => Application.Current?.Dispatcher.BeginInvoke(OnCatalogChanged);
        _data.BuffCatalogChanged += _catalogChanged;
    }

    /// <summary>구독에 쓴 델리게이트 그대로. Dispose 가 이것으로 해제한다.</summary>
    private readonly Action _catalogChanged;

    public ObservableCollection<BuffJobGroup> Groups { get; } = new();

    /// <summary>Re-read the hidden/voice sets from settings and rebuild the rows. Call after a preset switch
    /// has rewritten them underneath us: the cached sets are seeded once in the constructor, so a stale
    /// picker would not only show the old modes but write them back over the applied preset on its next
    /// <see cref="Persist"/>. Rebuilding seeds each row's mode through the field, not the property, so no
    /// per-item callback fires and nothing is re-persisted here.</summary>
    public void Reload()
    {
        _hidden.Clear();
        _hidden.UnionWith(MeterSettings.ParseCodeSet(_settings.BuffUiHidden));
        _voice.Clear();
        _voice.UnionWith(MeterSettings.ParseCodeSet(_settings.BuffUiVoice));
        _pinned = MeterSettings.ParseCodeList(_settings.BuffUiPinned);
        Rebuild();
    }

    private bool _isEmpty;
    /// <summary>True until at least one buff has been observed (shows the "play to populate" hint).</summary>
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }

    // Job display order (prefix 11..19), so groups read top-to-bottom in the familiar class order.
    private static readonly string[] JobOrder =
    {
        "검성", "수호성", "살성", "궁성", "마도성", "정령성", "치유성", "호법성", "권성", "기타",
    };

    private void OnCatalogChanged() => Rebuild();

    private void Rebuild()
    {
        var catalog = _data.BuffPickerCatalog();
        var byJob = catalog
            .GroupBy(c => c.Job)
            .OrderBy(g => Array.IndexOf(JobOrder, g.Key) is int i and >= 0 ? i : int.MaxValue);

        Groups.Clear();
        foreach (var g in byJob)
        {
            var group = new BuffJobGroup(g.Key);
            foreach ((int baseCode, string name, _, _) in g.OrderBy(c => c.BaseCode))
            {
                // Skip buffs with no bundled icon — they clutter the list and can't render an icon.
                ImageSource? icon = JoinIcons.Skill(baseCode);
                if (icon is null)
                {
                    continue;
                }

                var row = new BuffPickerItem(baseCode, name, ModeOf(baseCode), OnItemModeChanged, icon)
                {
                    IsPinned = _pinned.Contains(baseCode),
                };
                group.Items.Add(row);
            }

            if (group.Items.Count > 0)
            {
                Groups.Add(group);
            }
        }

        IsEmpty = Groups.Count == 0;
    }

    // States are two independent sets: hidden (not drawn) × voice (spoken). 음성만 = hidden AND voice.
    private int ModeOf(int baseCode)
    {
        bool hidden = _hidden.Contains(baseCode);
        bool voice = _voice.Contains(baseCode);
        if (hidden)
        {
            return voice ? BuffPickerItem.VoiceOnly : BuffPickerItem.Off;
        }

        return voice ? BuffPickerItem.Voice : BuffPickerItem.Overlay;
    }

    private void OnItemModeChanged(int baseCode, int mode)
    {
        Apply(baseCode, mode);
        Persist();
    }

    private void Apply(int baseCode, int mode)
    {
        _hidden.Remove(baseCode);
        _voice.Remove(baseCode);
        switch (mode)
        {
            case BuffPickerItem.Off:
                _hidden.Add(baseCode);
                break;
            case BuffPickerItem.Voice:
                _voice.Add(baseCode);
                break;
            case BuffPickerItem.VoiceOnly:
                _hidden.Add(baseCode); // hidden from the overlay ...
                _voice.Add(baseCode);  // ... but still spoken
                break;
            // Overlay: in neither set.
        }
    }

    /// <summary>Set every buff in one job group to the same mode at once.</summary>
    public void SetGroup(BuffJobGroup group, int mode)
    {
        foreach (BuffPickerItem item in group.Items)
        {
            item.SetModeSilently(mode);
            Apply(item.BaseCode, mode);
        }

        Persist();
    }

    /// <summary>맨 앞 고정을 켜고 끈다. 켤 때는 고정 목록 맨 뒤에 붙는다.</summary>
    public void TogglePin(BuffPickerItem item)
    {
        _pinned = BuffOverlayOrder.TogglePin(_pinned, item.BaseCode);
        item.IsPinned = _pinned.Contains(item.BaseCode);
        PersistPins();
    }

    private void PersistPins() => _settings.BuffUiPinned = string.Join(",", _pinned);

    private void Persist()
    {
        _settings.BuffUiHidden = string.Join(",", _hidden);
        _settings.BuffUiVoice = string.Join(",", _voice);
        _data.SetHiddenBuffBases(_hidden);
        _data.SetVoiceBuffBases(_voice);
    }

    public void Dispose() => _data.BuffCatalogChanged -= _catalogChanged;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>One job's buffs in the picker.</summary>
public sealed class BuffJobGroup
{
    public BuffJobGroup(string job) => Job = job;
    public string Job { get; }
    public ObservableCollection<BuffPickerItem> Items { get; } = new();
}

/// <summary>One buff row: icon + name + a 3-way mode (bound to a ComboBox SelectedIndex).</summary>
public sealed class BuffPickerItem : INotifyPropertyChanged
{
    public const int Off = 0;        // 알림끔
    public const int Overlay = 1;    // 오버레이만
    public const int Voice = 2;      // 오버레이+음성
    public const int VoiceOnly = 3;  // 음성만 (음성 알림만, 오버레이 표시 없음)

    private readonly Action<int, int> _onModeChanged;
    private bool _suppress;

    public BuffPickerItem(int baseCode, string name, int mode, Action<int, int> onModeChanged, ImageSource? icon)
    {
        BaseCode = baseCode;
        Name = name;
        IconSource = icon;
        _mode = mode;
        _onModeChanged = onModeChanged;
    }

    public int BaseCode { get; }
    public string Name { get; }
    public ImageSource? IconSource { get; }

    private bool _pinned;
    /// <summary>맨 앞 고정 여부. 고정된 버프는 정렬 모드와 무관하게 오버레이 앞쪽에 온다.</summary>
    public bool IsPinned
    {
        get => _pinned;
        internal set
        {
            if (_pinned == value)
            {
                return;
            }

            _pinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
        }
    }

    private int _mode;
    /// <summary>0 = 알림끔, 1 = 오버레이만, 2 = 오버레이+음성 (bound to ComboBox.SelectedIndex).</summary>
    public int Mode
    {
        get => _mode;
        set
        {
            if (_mode == value || value is < Off or > VoiceOnly)
            {
                return; // ignore the transient -1 a ComboBox can push during template init
            }

            _mode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mode)));
            if (!_suppress)
            {
                _onModeChanged(BaseCode, value);
            }
        }
    }

    /// <summary>Set the mode without firing the per-item callback (group toggle persists once for the group).</summary>
    public void SetModeSilently(int value)
    {
        _suppress = true;
        Mode = value;
        _suppress = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
