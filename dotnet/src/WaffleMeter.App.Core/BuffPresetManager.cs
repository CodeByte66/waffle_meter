using System.ComponentModel;

namespace WaffleMeter.App.Core;

/// <summary>
/// Owns the buff-overlay preset slots. The invariant: the live <c>buffUi.*</c> settings ARE the active
/// slot's contents. Selecting a slot pushes it into <see cref="MeterSettings"/> and into the buff store;
/// every later edit of a buff setting is captured straight back into the active slot. Because both halves
/// always move together they cannot drift, so nothing else — including the settings window's Cancel — has
/// to reconcile them.
///
/// Lives in App.Core (no WPF, no Data reference) so it is unit-testable on its own; the two store calls a
/// hidden/voice change needs are injected as delegates.
/// </summary>
public sealed class BuffPresetManager : IDisposable
{
    public const int SlotCount = 3;

    // The settings a preset owns. Absent on purpose: buffUi.show (the master toggle — switching a preset
    // must not turn the overlay off), buffUi.observed (the catalog is shared across slots and churns as new
    // buffs are seen), buffUi.defaultsApplied (a one-time global migration flag), and buffUi.presets itself
    // — capturing that last one would make Persist() re-enter this handler forever.
    private static readonly HashSet<string> PresetProps = new()
    {
        nameof(MeterSettings.BuffUiTransparent),
        nameof(MeterSettings.BuffUiIconSize),
        nameof(MeterSettings.BuffUiTextColor),
        nameof(MeterSettings.BuffTtsOnStart),
        nameof(MeterSettings.BuffTtsOnEnd),
        nameof(MeterSettings.BuffEndWarning3s),
        nameof(MeterSettings.BuffUiGrayOnCooldown),
        nameof(MeterSettings.ShowOtherPlayerBuffs),
        nameof(MeterSettings.BuffUiHidden),
        nameof(MeterSettings.BuffUiVoice),
        nameof(MeterSettings.BuffUiSortMode),
        nameof(MeterSettings.BuffUiPinned),
    };

    private readonly MeterSettings _settings;
    private readonly Action<HashSet<int>> _applyHidden;
    private readonly Action<HashSet<int>> _applyVoice;
    private readonly object _gate = new();

    private BuffPresetSet _set;
    private bool _applying;

    /// <param name="applyHidden">Replaces the store's hidden base-code set (DataManager.SetHiddenBuffBases).</param>
    /// <param name="applyVoice">Replaces the store's voice base-code set (DataManager.SetVoiceBuffBases).</param>
    public BuffPresetManager(MeterSettings settings, Action<HashSet<int>> applyHidden, Action<HashSet<int>> applyVoice)
    {
        _settings = settings;
        _applyHidden = applyHidden;
        _applyVoice = applyVoice;
        _set = Load();
        _settings.PropertyChanged += OnSettingsChanged; // after Load, so seeding isn't captured as an edit
    }

    public static string DefaultName(int index) => $"프리셋 {index + 1}";

    public int ActiveIndex => _set.Active;

    public string ActiveName => _set.Slots[_set.Active].Name;

    public IReadOnlyList<string> Names => _set.Slots.Select(s => s.Name).ToList();

    /// <summary>Apply a slot to the live settings + the buff store, and remember it as the active one.
    /// Re-selecting the active slot is a harmless re-apply.</summary>
    /// <summary>
    /// Re-read the preset set from settings and re-apply the active slot. For the settings import.
    /// <para>Order matters and is the reason this is not just <c>_set = Load()</c>: <see cref="Apply"/> is what
    /// pushes the slot's hidden/voice sets into the data store, so without it the picker would show the imported
    /// preset names while the overlay kept filtering by the old ones.</para>
    /// </summary>
    public void Reload()
    {
        lock (_gate)
        {
            _set = Load();
            Apply(_set.Slots[_set.Active]);
        }
    }

    public void SelectSlot(int index)
    {
        if (index < 0 || index >= SlotCount)
        {
            return;
        }

        lock (_gate)
        {
            Apply(_set.Slots[index]);
            _set = _set with { Active = index };
            Persist();
        }
    }

    /// <summary>Rename a slot. A blank name falls back to the default "프리셋 N".</summary>
    public void RenameSlot(int index, string? name)
    {
        if (index < 0 || index >= SlotCount)
        {
            return;
        }

        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            trimmed = DefaultName(index);
        }

        lock (_gate)
        {
            if (_set.Slots[index].Name == trimmed)
            {
                return;
            }

            List<BuffPreset> slots = [.. _set.Slots];
            slots[index] = slots[index] with { Name = trimmed };
            _set = _set with { Slots = slots };
            Persist();
        }
    }

    private void Apply(BuffPreset preset)
    {
        _applying = true;
        try
        {
            _settings.BuffUiTransparent = preset.Transparent;
            _settings.BuffUiIconSize = preset.IconSize;
            _settings.BuffUiTextColor = preset.TextColor;
            _settings.BuffTtsOnStart = preset.TtsOnStart;
            _settings.BuffTtsOnEnd = preset.TtsOnEnd;
            _settings.BuffEndWarning3s = preset.EndWarning3s;
            _settings.BuffUiGrayOnCooldown = preset.GrayOnCooldown;
            _settings.ShowOtherPlayerBuffs = preset.ShowOther;
            _settings.BuffUiHidden = preset.Hidden;
            _settings.BuffUiVoice = preset.Voice;
            _settings.BuffUiSortMode = preset.SortMode;
            _settings.BuffUiPinned = preset.Pinned;
        }
        finally
        {
            _applying = false;
        }

        // Unconditional. The overlay re-reads the seven scalars off MeterSettings every tick, but nothing
        // polls the hidden/voice strings — the store only learns them through these two calls, and a setter
        // that happened to write an unchanged value raises no event to trigger them.
        _applyHidden(MeterSettings.ParseCodeSet(preset.Hidden));
        _applyVoice(MeterSettings.ParseCodeSet(preset.Voice));
    }

    // Every buff edit — the settings-window toggles and the per-job picker alike — lands on a MeterSettings
    // setter, so this one hook captures them all into the active slot.
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying || e.PropertyName is not { } name || !PresetProps.Contains(name))
        {
            return;
        }

        lock (_gate)
        {
            List<BuffPreset> slots = [.. _set.Slots];
            slots[_set.Active] = CaptureLive(slots[_set.Active].Name);
            _set = _set with { Slots = slots };
            Persist();
        }
    }

    private BuffPreset CaptureLive(string name) => new()
    {
        Name = name,
        Transparent = _settings.BuffUiTransparent,
        IconSize = _settings.BuffUiIconSize,
        TextColor = _settings.BuffUiTextColor,
        TtsOnStart = _settings.BuffTtsOnStart,
        TtsOnEnd = _settings.BuffTtsOnEnd,
        EndWarning3s = _settings.BuffEndWarning3s,
        GrayOnCooldown = _settings.BuffUiGrayOnCooldown,
        ShowOther = _settings.ShowOtherPlayerBuffs,
        Hidden = _settings.BuffUiHidden,
        Voice = _settings.BuffUiVoice,
        SortMode = _settings.BuffUiSortMode,
        Pinned = _settings.BuffUiPinned,
    };

    // Startup never *applies* a preset: the live settings already hold the active slot's config and the store
    // was seeded from them. It only re-reads the slots — and heals the active one, because the live settings
    // are the truth if anything moved them behind the blob's back (an older build, a hand-edited file).
    private BuffPresetSet Load()
    {
        BuffPresetSet? stored = BuffPresetCodec.Decode(_settings.BuffUiPresets);
        BuffPresetSet set;
        if (IsUsable(stored))
        {
            List<BuffPreset> slots = [.. stored.Slots];
            slots[stored.Active] = CaptureLive(slots[stored.Active].Name);
            set = stored with { Slots = slots };
        }
        else
        {
            // First launch (or a corrupt blob): seed every slot from the config the user already has, so
            // switching slots does nothing until one is actually edited. Seeding slots 2/3 with factory
            // defaults instead would silently discard the picker choices of anyone who tried them.
            BuffPreset current = CaptureLive(string.Empty);
            set = new BuffPresetSet
            {
                Active = 0,
                Slots = [.. Enumerable.Range(0, SlotCount).Select(i => current with { Name = DefaultName(i) })],
            };
        }

        _settings.BuffUiPresets = BuffPresetCodec.Encode(set); // no-ops when the blob is already identical
        return set;
    }

    private static bool IsUsable([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] BuffPresetSet? set) =>
        set is { Slots.Count: SlotCount, Active: >= 0 and < SlotCount } && set.Slots.All(s => s is not null);

    private void Persist() => _settings.BuffUiPresets = BuffPresetCodec.Encode(_set);

    public void Dispose() => _settings.PropertyChanged -= OnSettingsChanged;
}
