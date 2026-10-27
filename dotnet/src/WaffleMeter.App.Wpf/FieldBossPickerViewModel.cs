using System.Collections.ObjectModel;
using System.ComponentModel;
using WaffleMeter.App.Core;
using WaffleMeter.Capture;

namespace WaffleMeter.App.Wpf;

/// <summary>Field-boss alarm picker: the known bosses grouped into one tab per world-map region, each with
/// an alert on/off toggle. Unchecked bosses go into the persisted disabled set, which the reminder skips.
/// The timer broadcast is map-scoped, so a region's alarms only ever fire while you are in that region —
/// the tabs are there so every region can be pre-configured. (The 수·토 아티쟁 bosses are the exception: their
/// spawn is derived from the server's war time, so they can fire without the abyss having been visited. So is
/// the daily 정령왕 아그로, whose next 09·21시 slot comes from the clock — <see cref="DailyFieldBossTimers"/>.)</summary>
public sealed class FieldBossPickerViewModel
{
    private readonly MeterSettings _settings;
    private readonly HashSet<int> _disabled;

    public FieldBossPickerViewModel(MeterSettings settings)
    {
        _settings = settings;
        _disabled = settings.FieldBossDisabledCodes;

        foreach (FieldBossRegion region in FieldBossCatalog.Regions)
        {
            // 감시자 카이라 has its own 4-hour-grid reminder in the settings, so it is not a row here.
            List<FieldBossInfo> bosses = FieldBossCatalog.InRegion(region)
                .Where(b => !FieldBossCatalog.HasOwnAlarm(b.Code)).ToList();
            if (bosses.Count == 0)
            {
                continue;
            }

            var group = new FieldBossGroup(FieldBossCatalog.RegionName(region), bosses.Count);
            foreach (FieldBossInfo b in bosses)
            {
                group.Items.Add(new FieldBossItem(
                    b.Code, b.Name, FieldBossFixedSchedule.Describe(b.Code), !_disabled.Contains(b.Code), OnToggled));
            }

            Groups.Add(group);
        }
    }

    public ObservableCollection<FieldBossGroup> Groups { get; } = new();

    private void OnToggled(int code, bool alerted)
    {
        if (alerted)
        {
            _disabled.Remove(code);
        }
        else
        {
            _disabled.Add(code);
        }

        _settings.FieldBossDisabled = string.Join(",", _disabled);
    }

    /// <summary>Toggle every boss in one region at once.</summary>
    public void SetGroup(FieldBossGroup group, bool alerted)
    {
        foreach (FieldBossItem item in group.Items)
        {
            item.SetAlertedSilently(alerted);
            if (alerted)
            {
                _disabled.Remove(item.Code);
            }
            else
            {
                _disabled.Add(item.Code);
            }
        }

        _settings.FieldBossDisabled = string.Join(",", _disabled);
    }
}

public sealed class FieldBossGroup
{
    public FieldBossGroup(string region, int count)
    {
        Region = region;
        Count = count;
    }

    public string Region { get; }

    public int Count { get; }

    /// <summary>Tab header — "베르테론 · 24".</summary>
    public string Header => $"{Region} · {Count}";

    public ObservableCollection<FieldBossItem> Items { get; } = new();
}

public sealed class FieldBossItem : INotifyPropertyChanged
{
    private readonly Action<int, bool> _onToggled;
    private bool _suppress;

    public FieldBossItem(int code, string name, string? schedule, bool alerted, Action<int, bool> onToggled)
    {
        Code = code;
        Name = name;
        Schedule = schedule;
        _alerted = alerted;
        _onToggled = onToggled;
    }

    public int Code { get; }

    public string Name { get; }

    /// <summary>Fixed-spawn hint ("금·일 22:05" / "수·토 아티쟁 종료 후" / "매일 09:00·21:00") for the 어비스 fixed-spawn bosses, null for a
    /// normal respawn timer. Rows that share a mob name (수호신장 나흐마 ×3 하층, 분노한 수호신장 나흐마 ×2 중층)
    /// share the badge too — one boss name on several spawn slots, listed in slot order; the badge does not tell
    /// them apart and is not meant to.</summary>
    public string? Schedule { get; }

    public bool HasSchedule => !string.IsNullOrEmpty(Schedule);

    private bool _alerted;
    public bool Alerted
    {
        get => _alerted;
        set
        {
            if (_alerted == value)
            {
                return;
            }

            _alerted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Alerted)));
            if (!_suppress)
            {
                _onToggled(Code, value);
            }
        }
    }

    public void SetAlertedSilently(bool value)
    {
        _suppress = true;
        Alerted = value;
        _suppress = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
