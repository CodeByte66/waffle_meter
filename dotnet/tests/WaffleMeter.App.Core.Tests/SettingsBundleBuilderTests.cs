using System.Text;
using WaffleMeter.App.Core;
using WaffleMeter.Services;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// Export/import round-trips against a REAL <see cref="PropertyHandler"/> on disk, because the interesting
/// failures all live in the storage layer: values are re-decoded on the way out, so a bundle that reads through
/// one representation and writes through another looks right until the next restart.
/// </summary>
public sealed class SettingsBundleBuilderTests : IDisposable
{
    private const string AppName = "waffle_meter.v1.4";
    private readonly string _tempAppData;
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

    public SettingsBundleBuilderTests()
    {
        _tempAppData = Path.Combine(Path.GetTempPath(), "wm_bundle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempAppData);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempAppData, recursive: true); } catch { /* temp */ }
    }

    private PropertyHandler NewHandler() => new(_tempAppData);

    private string AppDir => Path.Combine(_tempAppData, AppName);

    [Fact]
    public void Carries_only_catalogued_keys()
    {
        PropertyHandler props = NewHandler();
        props.SetProperty("rowHeight", "44");
        props.SetProperty("statsInstallId", "SECRET-GUID");
        props.SetProperty("uiX", "1234");
        props.SetProperty("aether.lastValue", "840,120,1");

        SettingsBundle b = SettingsBundleBuilder.Build(props, SettingsProfile.Full, "test", Now);

        Assert.Equal("44", b.Data["rowHeight"]);
        Assert.DoesNotContain("statsInstallId", b.Data.Keys);
        Assert.DoesNotContain("uiX", b.Data.Keys);
        Assert.DoesNotContain("aether.lastValue", b.Data.Keys);
    }

    [Fact]
    public void The_whole_code_contains_no_excluded_key_name_or_value()
    {
        // The property that makes handing a code to a stranger safe. Checked against the encoded string, not
        // the object, because that is what actually leaves the machine.
        PropertyHandler props = NewHandler();
        props.SetProperty("statsInstallKeyPkcs8DpapiV1", "AAAA-PRIVATE-KEY-AAAA");
        props.SetProperty("statsConsentIdentityHash", "deadbeefdeadbeef");
        props.SetProperty("rowHeight", "44");

        string code = SettingsBundleCodec.Encode(SettingsBundleBuilder.Build(props, SettingsProfile.Full, "test", Now));
        Assert.True(SettingsBundleCodec.TryDecode(code, out SettingsBundle back, out _));

        string json = string.Join('', back.Data.Select(kv => kv.Key + "=" + kv.Value));
        Assert.DoesNotContain("PRIVATE-KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("deadbeef", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Design_code_is_a_subset_of_the_full_code()
    {
        PropertyHandler props = NewHandler();
        foreach (SettingsKey k in SettingsKeyCatalog.All)
        {
            props.SetProperty(k.Key, "x");
        }

        var full = SettingsBundleBuilder.Build(props, SettingsProfile.Full, "test", Now);
        var design = SettingsBundleBuilder.Build(props, SettingsProfile.Design, "test", Now);

        Assert.True(design.Data.Count < full.Data.Count);
        Assert.All(design.Data.Keys, k => Assert.Contains(k, full.Data.Keys));
        Assert.DoesNotContain("alarms.custom", design.Data.Keys);
    }

    [Fact]
    public void A_key_that_was_never_written_is_left_out_rather_than_exported_as_a_default()
    {
        // Exporting defaults would make the code overwrite the receiver's deliberate choices with "whatever the
        // sender never touched" — the opposite of what sharing a look is for.
        PropertyHandler props = NewHandler();
        props.SetProperty("rowHeight", "44");

        SettingsBundle b = SettingsBundleBuilder.Build(props, SettingsProfile.Full, "test", Now);
        Assert.DoesNotContain("barStyle", b.Data.Keys);
    }

    [Fact]
    public void Plan_reports_changes_unchanged_unknown_and_missing_separately()
    {
        PropertyHandler props = NewHandler();
        props.SetProperty("rowHeight", "36");
        props.SetProperty("barStyle", "fill");

        var bundle = new SettingsBundle { Profile = "D" };
        bundle.Data["rowHeight"] = "50";        // changes
        bundle.Data["barStyle"] = "fill";       // unchanged
        bundle.Data["someFutureKey"] = "1";     // unknown

        SettingsBundlePlan plan = SettingsBundleBuilder.Plan(props, bundle);

        Assert.Single(plan.Changes);
        Assert.Equal("행 높이", plan.Changes[0].Label);
        Assert.Equal("36", plan.Changes[0].From);
        Assert.Equal("50", plan.Changes[0].To);
        Assert.Equal(1, plan.UnchangedCount);
        Assert.Equal(1, plan.UnknownCount);
        Assert.True(plan.MissingCount > 0);
        Assert.True(plan.HasWork);
    }

    [Fact]
    public void Plan_writes_nothing()
    {
        PropertyHandler props = NewHandler();
        props.SetProperty("rowHeight", "36");

        var bundle = new SettingsBundle();
        bundle.Data["rowHeight"] = "50";
        SettingsBundleBuilder.Plan(props, bundle);

        Assert.Equal("36", NewHandler().GetProperty("rowHeight"));
    }

    [Fact]
    public void A_korean_value_survives_export_import_AND_a_restart()
    {
        // THE test for this feature. The storage layer re-decodes on read, so a bundle that exports the decoded
        // value and imports it back writes a different string than it read. That renders correctly for the rest
        // of the session and changes the next time the app starts — which is why the check reopens the handler.
        PropertyHandler source = NewHandler();
        source.SetProperty("theme", "{\"이름\":\"기본 테마\"}");
        source.SetProperty("fontFamily", "나눔손글씨 붓");

        string code = SettingsBundleCodec.Encode(
            SettingsBundleBuilder.Build(source, SettingsProfile.Full, "test", Now));

        // A different machine: same folder wiped, fresh handler.
        Directory.Delete(AppDir, recursive: true);
        PropertyHandler target = NewHandler();
        Assert.True(SettingsBundleCodec.TryDecode(code, out SettingsBundle bundle, out _));
        target.RunBatched(() =>
        {
            foreach ((string k, string v) in bundle.Data)
            {
                target.SetProperty(k, v);
            }
        });

        // Compared against the literals, not against source.GetProperty(...). Reading both sides through the
        // same accessor made this pass while the value was being destroyed identically on both — the assertion
        // held as an identity and said nothing about Korean surviving anything. RawEntries is checked too, so
        // a bundle that exports a decoded value and re-imports it still fails here even if reads are correct.
        PropertyHandler afterRestart = NewHandler();
        Assert.Equal("{\"이름\":\"기본 테마\"}", afterRestart.GetProperty("theme"));
        Assert.Equal("나눔손글씨 붓", afterRestart.GetProperty("fontFamily"));
        Assert.Equal("나눔손글씨 붓", afterRestart.RawEntries()["fontFamily"]);
    }

    [Fact]
    public void MeterSettings_picks_up_an_import_without_a_restart()
    {
        PropertyHandler props = NewHandler();
        var settings = new MeterSettings(props);
        Assert.Equal(36, settings.RowHeight);

        props.SetProperty("rowHeight", "52");
        settings.Reload();

        Assert.Equal(52, settings.RowHeight);
    }

    [Fact]
    public void Reload_announces_that_everything_may_have_changed()
    {
        // WPF reads an empty/null property name as "rebind everything". Listing names instead would silently
        // miss whichever key gets added next.
        PropertyHandler props = NewHandler();
        var settings = new MeterSettings(props);
        string? seen = "unset";
        settings.PropertyChanged += (_, e) => seen = e.PropertyName;

        settings.Reload();

        Assert.True(string.IsNullOrEmpty(seen));
    }

    [Fact]
    public void Backup_is_written_before_an_import_and_can_be_read_back()
    {
        PropertyHandler props = NewHandler();
        props.SetProperty("rowHeight", "44");

        string? path = SettingsBackupStore.Save(props, "test", Now);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        string? code = SettingsBackupStore.ReadNewest(props.AppDirectory());
        Assert.True(SettingsBundleCodec.TryDecode(code, out SettingsBundle back, out _));
        Assert.Equal("44", back.Data["rowHeight"]);
    }

    [Fact]
    public void Backups_are_capped_and_the_newest_wins()
    {
        PropertyHandler props = NewHandler();
        for (int i = 0; i < 14; i++)
        {
            props.SetProperty("rowHeight", (30 + i).ToString());
            SettingsBackupStore.Save(props, "test", Now.AddSeconds(i));
        }

        Assert.Equal(10, SettingsBackupStore.List(props.AppDirectory()).Count);
        Assert.True(SettingsBundleCodec.TryDecode(SettingsBackupStore.ReadNewest(props.AppDirectory()), out SettingsBundle newest, out _));
        Assert.Equal("43", newest.Data["rowHeight"]);
    }

    [Fact]
    public void Reading_a_backup_when_none_exists_is_not_an_error()
    {
        Assert.Empty(SettingsBackupStore.List(Path.Combine(_tempAppData, "nope")));
        Assert.Null(SettingsBackupStore.ReadNewest(Path.Combine(_tempAppData, "nope")));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // 백업은 내보내기와 규칙이 다르다 (M-28 / 판정31: "백업에 해당 사항 모두 포함")
    //
    // 위의 A_key_that_was_never_written_is_left_out_rather_than_exported_as_a_default 는 **공유 코드**의
    // 계약이고 그대로 유효하다. 백업은 그 반대여야 한다 — 내 PC 의 '되돌리기' 대상이니까.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_backup_records_the_settings_the_user_has_never_touched()
    {
        // 갓 설치해 컴팩트 모드·게이지 형태를 한 번도 안 만진 사용자. 그 키들이 파일에 없다는 이유로
        // 백업에서도 빠졌고, 그래서 남의 디자인 코드를 적용한 뒤 「되돌리기」가 0건을 복원했다.
        PropertyHandler props = NewHandler();
        props.SetProperty("rowHeight", "44");

        SettingsBundle b = SettingsBundleBuilder.BuildBackup(props, "test", Now);

        Assert.Equal("44", b.Data["rowHeight"]);
        Assert.Contains("barStyle", b.Absent);
        Assert.Contains("isMinimal", b.Absent);
        Assert.DoesNotContain("rowHeight", b.Absent);
    }

    [Fact]
    public void A_shared_code_never_carries_an_absent_marker()
    {
        // 이것이 '백업 전용'이어야 하는 이유. 공유 코드가 이 표시를 실으면 "남의 디자인 코드를 받았다"가
        // 받는 쪽 키를 **삭제**하는 동작이 된다 — 코드는 초기화 수단이 아니다.
        PropertyHandler props = NewHandler();
        props.SetProperty("rowHeight", "44");

        Assert.Empty(SettingsBundleBuilder.Build(props, SettingsProfile.Full, "test", Now).Absent);
        Assert.Empty(SettingsBundleBuilder.Build(props, SettingsProfile.Design, "test", Now).Absent);
    }

    [Fact]
    public void Undo_puts_back_a_never_configured_setting_by_clearing_it_not_by_writing_a_default()
    {
        // 기본값을 써 넣으면 그 기본값이 바뀌는 날 사용자의 선택으로 둔갑하고, "한 번도 안 건드림"을 보는
        // 게이트도 속는다. 백업 시점에 키가 없었으면 복원은 삭제다.
        PropertyHandler props = NewHandler();
        props.SetProperty("rowHeight", "44");
        SettingsBundle backup = SettingsBundleBuilder.BuildBackup(props, "test", Now);

        props.SetProperty("barStyle", "glass"); // 남의 디자인 코드가 심은 값

        SettingsBundlePlan plan = SettingsBundleBuilder.Plan(props, backup);

        Assert.Contains("barStyle", plan.Clear);
        Assert.Equal(1, plan.ClearedCount);
        SettingsChange change = plan.Changes.Single(c => c.Label == "게이지 형태");
        Assert.Equal("glass", change.From);
        Assert.Equal(SettingsBundleBuilder.Unset, change.To);
        Assert.True(plan.HasWork);
    }

    [Fact]
    public void A_restore_that_would_change_nothing_says_so_instead_of_reporting_success()
    {
        // M-28 의 무게중심. 상태줄이 "되돌렸습니다"를 말해도 되는지는 이 값으로 갈린다.
        PropertyHandler props = NewHandler();
        props.SetProperty("rowHeight", "44");
        SettingsBundle backup = SettingsBundleBuilder.BuildBackup(props, "test", Now);

        SettingsBundlePlan plan = SettingsBundleBuilder.Plan(props, backup);

        Assert.False(plan.HasWork);
        Assert.Equal(0, plan.ClearedCount);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void An_absent_key_that_is_still_absent_is_unchanged_not_a_change()
    {
        PropertyHandler props = NewHandler();
        SettingsBundle backup = SettingsBundleBuilder.BuildBackup(props, "test", Now);

        SettingsBundlePlan plan = SettingsBundleBuilder.Plan(props, backup);

        Assert.Empty(plan.Clear);
        Assert.True(plan.UnchangedCount >= SettingsKeyCatalog.All.Length - 1);
        // 백업은 전 키를 말한다 — 값으로든 '없었음' 으로든. 그래서 '코드가 빠뜨린 키' 는 0이어야 한다.
        Assert.Equal(0, plan.MissingCount);
    }

    [Fact]
    public void A_machine_with_every_setting_written_has_nothing_to_clear()
    {
        PropertyHandler props = NewHandler();
        foreach (SettingsKey k in SettingsKeyCatalog.All)
        {
            props.SetProperty(k.Key, "x");
        }

        SettingsBundle b = SettingsBundleBuilder.BuildBackup(props, "test", Now);

        Assert.Empty(b.Absent);
        Assert.Equal(SettingsKeyCatalog.All.Length, b.Data.Count);
    }

    [Fact]
    public void The_absent_list_survives_the_code_round_trip()
    {
        // 백업은 파일로 저장됐다가 며칠 뒤 디코드된다. 목록이 컨테이너를 통과하지 못하면 되돌리기는
        // 다시 예전처럼 조용히 반쪽이 된다.
        PropertyHandler props = NewHandler();
        props.SetProperty("rowHeight", "44");

        string code = SettingsBundleCodec.Encode(SettingsBundleBuilder.BuildBackup(props, "test", Now));

        Assert.True(SettingsBundleCodec.TryDecode(code, out SettingsBundle back, out _));
        Assert.Contains("barStyle", back.Absent);
    }

    [Fact]
    public void The_pre_import_snapshot_on_disk_carries_the_never_touched_keys()
    {
        // SettingsBackupStore 가 내보내기 빌더로 되돌아가면 이 테스트가 빨간다.
        PropertyHandler props = NewHandler();
        props.SetProperty("rowHeight", "44");

        SettingsBackupStore.Save(props, "test", Now);

        Assert.True(SettingsBundleCodec.TryDecode(
            SettingsBackupStore.ReadNewest(props.AppDirectory()), out SettingsBundle b, out _));
        Assert.Contains("isMinimal", b.Absent);
    }
}
