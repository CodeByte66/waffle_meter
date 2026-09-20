using WaffleMeter.Services;

namespace WaffleMeter.App.Core;

/// <summary>One line of the import preview.</summary>
/// <param name="Group">Section heading, in user language.</param>
/// <param name="Label">What the setting is called.</param>
/// <param name="From">Current value, formatted for display.</param>
/// <param name="To">Value the code would apply.</param>
public sealed record SettingsChange(string Group, string Label, string From, string To);

/// <summary>
/// What applying a code would actually do. Built before anything is written, so the user agrees to a specific
/// list rather than to the word "가져오기".
/// </summary>
public sealed class SettingsBundlePlan
{
    public required SettingsBundle Bundle { get; init; }

    /// <summary>Keys whose value would change, with before and after.</summary>
    public required IReadOnlyList<SettingsChange> Changes { get; init; }

    /// <summary>Keys already equal to the incoming value — carried, but nothing happens.</summary>
    public required int UnchangedCount { get; init; }

    /// <summary>Keys in the code that this build does not know. Ignored, and counted so the preview can say so
    /// instead of pretending the code applied whole.</summary>
    public required int UnknownCount { get; init; }

    /// <summary>Keys this bundle says were never configured and that the file currently HAS — applying puts
    /// them back to "never configured" by deleting them. Only a backup ever carries these
    /// (<see cref="SettingsBundle.Absent"/>), so for a shared code this is always 0.</summary>
    public required int ClearedCount { get; init; }

    /// <summary>Keys this build knows but the code omits. Local values stay — a code is not a reset.</summary>
    public required int MissingCount { get; init; }

    public bool HasWork => Changes.Count > 0;

    /// <summary>Catalogued keys to delete so a restore really lands on the pre-import state. Ordered, so the
    /// preview and the write agree on what "되돌리기" is about to do.</summary>
    public required IReadOnlyList<string> Clear { get; init; }
}

/// <summary>
/// Turns settings into a code and back into a plan.
/// <para><b>Everything moves as the RAW stored string.</b> <c>PropertyHandler.GetProperty</c> re-decodes on the
/// way out, so exporting through it and importing through a model setter would put a value in memory that the
/// file never contains — right this session, different after a restart. Raw out, raw in, then
/// <see cref="MeterSettings.Reload"/>: each side keeps the representation it expects.</para>
/// </summary>
public static class SettingsBundleBuilder
{
    public static SettingsBundle Build(PropertyHandler props, SettingsProfile profile, string appVersion, DateTimeOffset now)
    {
        IReadOnlyDictionary<string, string> raw = props.RawEntries();
        var bundle = new SettingsBundle
        {
            Version = 1,
            Profile = SettingsBundleCodec.ProfileTag(profile),
            App = appVersion,
            CreatedAt = now.ToString("O"),
        };

        foreach (SettingsKey k in SettingsKeyCatalog.For(profile))
        {
            // A key never written is left out rather than exported as its default. Sending defaults would make
            // the code overwrite the receiver's deliberate choices with "whatever the sender never touched".
            // <see cref="SettingsBundle.Absent"/> stays empty here for the same reason: a shared code must not
            // be able to delete keys on the receiving machine.
            if (raw.TryGetValue(k.Key, out string? v))
            {
                bundle.Data[k.Key] = v;
            }
        }

        return bundle;
    }

    /// <summary>
    /// The pre-import snapshot. Same as <see cref="Build"/> with one addition that only makes sense for a
    /// snapshot of THIS machine: every catalogued key the file does not have is recorded in
    /// <see cref="SettingsBundle.Absent"/>.
    /// <para><b>Why.</b> A fresh install has never written 컴팩트 모드 · 서버 표시 · 게이지 형태 · 행 높이, so
    /// the omit-what-was-never-written rule left them out of the backup too — and 「되돌리기」 then reported
    /// success while restoring nothing (M-28). "없었음" is a value; it just isn't a string.</para>
    /// <para>Restoring it means DELETING the key, not writing a default: a default written into the file is a
    /// different state (it survives a future change of that default, and it makes "한 번도 안 건드림" gates
    /// think the user chose it).</para>
    /// </summary>
    public static SettingsBundle BuildBackup(PropertyHandler props, string appVersion, DateTimeOffset now)
    {
        SettingsBundle bundle = Build(props, SettingsProfile.Full, appVersion, now);
        IReadOnlyDictionary<string, string> raw = props.RawEntries();
        foreach (SettingsKey k in SettingsKeyCatalog.All)
        {
            if (!raw.ContainsKey(k.Key))
            {
                bundle.Absent.Add(k.Key);
            }
        }

        return bundle;
    }

    /// <summary>Compare a decoded code against the current settings, without writing anything.</summary>
    public static SettingsBundlePlan Plan(PropertyHandler props, SettingsBundle bundle)
    {
        IReadOnlyDictionary<string, string> raw = props.RawEntries();
        var changes = new List<SettingsChange>();
        int unchanged = 0, unknown = 0;

        foreach ((string key, string value) in bundle.Data)
        {
            SettingsKey? known = SettingsKeyCatalog.Find(key);
            if (known is null)
            {
                // Either a key from a newer build, or one we have since retracted. Neither is worth failing the
                // whole import over; the preview reports the count.
                unknown++;
                continue;
            }

            string current = raw.GetValueOrDefault(key, string.Empty);
            if (string.Equals(current, value, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            changes.Add(new SettingsChange(known.Group, known.Label, Display(current), Display(value)));
        }

        // Keys the bundle says were never configured. Deleting one is a real change and has to be previewed as
        // one — "0건 적용" while the meter visibly changes is how the undo lost the user's trust (M-28).
        var clear = new List<string>();
        foreach (string key in bundle.Absent)
        {
            SettingsKey? known = SettingsKeyCatalog.Find(key);
            if (known is null)
            {
                unknown++;
                continue;
            }

            if (!raw.TryGetValue(key, out string? current))
            {
                unchanged++; // already unset — restoring it is a no-op
                continue;
            }

            clear.Add(key);
            changes.Add(new SettingsChange(known.Group, known.Label, Display(current), Unset));
        }

        // "이 코드가 말하지 않은 키". 백업은 절대 침묵하지 않는다 — 값으로 말하거나 '없었음' 으로 말하므로
        // Absent 에 있는 키는 누락이 아니다(현재도 없어서 할 일이 0건인 경우까지 포함).
        var described = new HashSet<string>(bundle.Absent, StringComparer.Ordinal);
        SettingsProfile profile = SettingsBundleCodec.ParseProfile(bundle.Profile);
        int missing = SettingsKeyCatalog.For(profile)
            .Count(k => !bundle.Data.ContainsKey(k.Key) && !described.Contains(k.Key));

        return new SettingsBundlePlan
        {
            Bundle = bundle,
            Changes = changes,
            UnchangedCount = unchanged,
            UnknownCount = unknown,
            MissingCount = missing,
            ClearedCount = clear.Count,
            Clear = clear,
        };
    }

    /// <summary>How the preview names "back to never configured". Distinct from "(없음)", which is an empty
    /// string that WAS written — the two behave differently on the next restart.</summary>
    public const string Unset = "(설정 안 함)";

    /// <summary>Values are stored strings — long JSON blobs and CSV lists are common. Trim for the preview.</summary>
    private static string Display(string v)
    {
        if (v.Length == 0)
        {
            return "(없음)";
        }

        string oneLine = v.Replace('\n', ' ').Replace('\r', ' ');
        return oneLine.Length <= 42 ? oneLine : oneLine[..40] + "…";
    }
}
