using System.Text.RegularExpressions;
using WaffleMeter.App.Core;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// The catalogue decides what leaves the machine. Its danger is not being wrong today — it is going stale:
/// a setting added next month is carried by nothing and quietly missing from every "전체 백업", or worse, a
/// secret added next month is carried by everything.
/// <para>So the source of truth is the source file. These tests read <c>MeterSettings.cs</c>, pull out every
/// storage key it actually uses, and require each one to be a deliberate decision — carried or excluded.</para>
/// </summary>
public sealed class SettingsKeyCatalogTests
{
    private static readonly Regex KeyLiteral = new(
        """(?:ReadBool|ReadInt|ReadDouble|ReadEnum|SetBool|SetInt|SetDouble|SetProp|GetProperty|SetProperty)\s*\(\s*(?:ref\s+\w+\s*,\s*)?"([^"]+)"”?""".Replace("”?", string.Empty),
        RegexOptions.Compiled);

    private static string MeterSettingsSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "dotnet", "src", "WaffleMeter.App.Core", "MeterSettings.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("MeterSettings.cs 를 찾지 못했습니다 — 테스트가 저장소 밖에서 실행됐습니다.");
    }

    private static IEnumerable<string> KeysUsedByMeterSettings() =>
        KeyLiteral.Matches(MeterSettingsSource())
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);

    [Fact]
    public void Every_MeterSettings_key_is_either_carried_or_deliberately_excluded()
    {
        string[] unclassified = KeysUsedByMeterSettings()
            .Where(k => !SettingsKeyCatalog.IsKnown(k) && !SettingsKeyCatalog.ExcludedKeys.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unclassified.Length == 0,
            "새 설정 키는 SettingsKeyCatalog.All 이나 ExcludedKeys 중 한쪽에 반드시 분류해야 합니다 — " +
            "분류하지 않으면 '전체 백업'이라는 이름의 기능이 조용히 거짓말이 됩니다.\n  분류 안 된 키: " +
            string.Join(", ", unclassified));
    }

    [Fact]
    public void The_scan_actually_finds_keys()
    {
        // A regex that silently matches nothing would make the test above pass forever. Pin a floor.
        Assert.True(KeysUsedByMeterSettings().Count() > 60);
    }

    [Fact]
    public void No_key_is_both_carried_and_excluded()
    {
        string[] both = SettingsKeyCatalog.All
            .Select(k => k.Key)
            .Where(SettingsKeyCatalog.ExcludedKeys.ContainsKey)
            .ToArray();

        Assert.True(both.Length == 0, "모순: " + string.Join(", ", both));
    }

    [Fact]
    public void Catalogue_keys_are_unique()
    {
        var dupes = SettingsKeyCatalog.All
            .GroupBy(k => k.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.True(dupes.Length == 0, "중복: " + string.Join(", ", dupes));
    }

    [Fact]
    public void Nothing_that_identifies_the_machine_or_the_account_is_carried()
    {
        // The property that makes sharing a code safe at all. Written as a pattern rather than a list so a
        // future statsSomethingNew cannot slip past by not being named here.
        string[] leaked = SettingsKeyCatalog.All
            .Select(k => k.Key)
            .Where(k => k.StartsWith("stats", StringComparison.Ordinal)
                     || k.StartsWith("aether.", StringComparison.Ordinal)
                     || k.StartsWith("content.", StringComparison.Ordinal)
                     || k.StartsWith("server.", StringComparison.Ordinal)
                     || k.StartsWith("capture.", StringComparison.Ordinal)
                     || k.EndsWith("Migrated", StringComparison.Ordinal)
                     || k is "meterWidth" or "meterHeight" or "uiX" or "uiY" or "windowX" or "windowY")
            .ToArray();

        Assert.True(leaked.Length == 0, "코드에 실리면 안 되는 키: " + string.Join(", ", leaked));
    }

    [Fact]
    public void Design_code_carries_no_functional_toggle()
    {
        // 남의 디자인 코드를 받았다고 창이 켜지거나 집계 방식이 바뀌면 안 된다. 'Design' 은 보이는 것만이다.
        string[] functional =
        {
            "buffUi.show", "buffUi.presets", "buffUi.hidden", "buffUi.voice",
            "showJoinPanel", "showPreCombatRoster",
            "dummy.testMode", "closeAction", "isAutoHide", "taskbarMode",
            "lowSpecMode", "refreshIntervalMs", "replay.recordMovement",
        };
        string[] inDesign = SettingsKeyCatalog.For(SettingsProfile.Design).Select(k => k.Key).ToArray();

        string[] bad = functional.Where(inDesign.Contains).ToArray();
        Assert.True(bad.Length == 0, "디자인 코드에 기능 토글이 섞였습니다: " + string.Join(", ", bad));
    }

    [Fact]
    public void Alarm_code_carries_only_alarm_keys()
    {
        string[] notAlarms = SettingsKeyCatalog.For(SettingsProfile.Alarms)
            .Select(k => k.Key)
            .Where(k => !k.StartsWith("alarms.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(notAlarms.Length == 0, "알림 코드에 알림이 아닌 키: " + string.Join(", ", notAlarms));
    }

    [Fact]
    public void Full_carries_everything_and_the_subsets_are_smaller()
    {
        int full = SettingsKeyCatalog.For(SettingsProfile.Full).Count();
        Assert.Equal(SettingsKeyCatalog.All.Length, full);
        Assert.InRange(SettingsKeyCatalog.For(SettingsProfile.Design).Count(), 1, full - 1);
        Assert.InRange(SettingsKeyCatalog.For(SettingsProfile.Alarms).Count(), 1, full - 1);
    }

    [Fact]
    public void Every_entry_has_a_group_and_a_label()
    {
        Assert.All(SettingsKeyCatalog.All, k =>
        {
            Assert.False(string.IsNullOrWhiteSpace(k.Group));
            Assert.False(string.IsNullOrWhiteSpace(k.Label));
        });
    }
}
