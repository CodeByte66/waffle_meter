using WaffleMeter.App.Core;
using WaffleMeter.Data;
using WaffleMeter.Services;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 설정 가져오기가 라이브 객체를 깨우는 <b>순서</b>의 스펙 (M-16).
///
/// <para>실제 배선은 <c>SettingsBundleApplier</c>(App.Wpf, 테스트 프로젝트 없음)에 있지만, 그것이 부르는
/// 객체들은 전부 여기 App.Core 에 있다. 그래서 순서를 여기서 재연할 수 있고 — 재연해 둬야 한다. 이 결함은
/// 코드를 읽어서는 안 보인다: 두 줄의 순서가 바뀌었을 뿐인데 가져온 프리셋 blob 이 통째로 사라진다.</para>
///
/// <para><b>메커니즘.</b> <see cref="CooldownVisibility.Reload"/> 는 값이 바뀌었든 아니든 <c>Changed</c> 를
/// 무조건 쏜다. <see cref="CooldownPresetManager"/> 는 그 이벤트를 "사용자가 픽커에서 칩을 토글했다"로 읽고
/// 활성 슬롯을 라이브 값으로 캡처해 <c>cooldownUi.presets</c> 에 다시 쓴다. 가져오기 직후에 그게 터지면
/// 매니저의 <c>_set</c> 은 아직 <b>옛 값</b>이라, 방금 파일에 심은 남의 프리셋을 내 옛 프리셋으로 덮는다.</para>
/// </summary>
public sealed class SettingsImportCatchUpOrderTests : IDisposable
{
    private readonly string _temp;

    public SettingsImportCatchUpOrderTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "wm_importorder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static CooldownCatalog Catalog(params int[] codes)
    {
        string rows = string.Join(",", codes.Select(c =>
            $"\"{c}\":{{\"j\":{c / 1_000_000},\"n\":\"스킬{c}\",\"cd\":1000,\"gct\":{c},\"auto\":0,\"order\":0}}"));
        string path = Path.Combine(Path.GetTempPath(), "wm_cdcat_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, $"{{\"generatedFrom\":\"test\",\"skills\":{{{rows}}},\"gctOverride\":{{}}}}");
        try
        {
            return CooldownCatalog.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>보내는 사람의 프리셋. 세 슬롯 전부 이름이 다르고 활성 슬롯이 0이 아니다 — 세 가지가 함께
    /// 사라지는 것이 이 결함의 증상이라 셋 다 구분 가능해야 한다.</summary>
    private static string IncomingPresets(string hiddenOfActive) =>
        CooldownPresetCodec.Encode(new CooldownPresetSet
        {
            Active = 2,
            Slots = new List<CooldownPreset>
            {
                new() { Name = "보스", IconSize = 30, PerRow = 4, Hidden = string.Empty },
                new() { Name = "쩔", IconSize = 44, PerRow = 6, Hidden = "1000001" },
                new() { Name = "필드", IconSize = 52, PerRow = 9, Hidden = hiddenOfActive },
            },
        });

    private sealed record Machine(
        PropertyHandler Props,
        MeterSettings Settings,
        CooldownVisibility Visibility,
        CooldownPresetManager Presets);

    /// <summary>슬롯을 한 번도 안 건드린 로컬 PC: 세 슬롯 모두 기본 이름, 활성 0.</summary>
    private Machine LocalMachine(string name, CooldownCatalog catalog)
    {
        string dir = Path.Combine(_temp, name);
        Directory.CreateDirectory(dir);
        var props = new PropertyHandler(dir);
        var settings = new MeterSettings(props);
        var visibility = new CooldownVisibility(props, catalog);
        var presets = new CooldownPresetManager(settings, visibility);
        Assert.Equal(0, presets.ActiveIndex);
        Assert.Equal(CooldownPresetManager.DefaultName(0), presets.ActiveName);
        return new Machine(props, settings, visibility, presets);
    }

    /// <summary>applier 가 하는 일의 앞부분: 코드에 실린 키를 원문 그대로 파일에 심는다(한 번의 저장).
    /// <para>스칼라도 함께 심는 것이 실제 모양이다 — 불변식이 "라이브 <c>cooldownUi.*</c> 가 곧 활성 슬롯의
    /// 내용"이라 <see cref="CooldownPresetManager"/> 의 Load 는 활성 슬롯을 <b>라이브 값으로</b> 다시 캡처한다.
    /// blob 만 심으면 활성 슬롯의 외형이 받는 쪽 기본값으로 덮이는 것이 정상 동작이다.</para></summary>
    private static void WriteImport(Machine m, string presetsBlob, string hidden)
    {
        m.Props.RunBatched(() =>
        {
            m.Props.SetProperty("cooldownUi.presets", presetsBlob);
            m.Props.SetProperty("cooldownUi.hidden", hidden);
            m.Props.SetProperty("cooldownUi.iconSize", "52");
            m.Props.SetProperty("cooldownUi.perRow", "9");
        });
    }

    [Fact]
    public void Waking_the_preset_manager_is_what_makes_an_imported_cooldown_preset_stick()
    {
        // ✅ 지금의 순서: 설정 → 프리셋 매니저. 매니저의 Reload 가 Load→Apply→SetRawHidden 까지 하므로
        // 픽커도 같은 호출 안에서 제자리 갱신된다.
        CooldownCatalog catalog = Catalog(1000001, 1000002, 1000003);
        Machine m = LocalMachine("good", catalog);

        WriteImport(m, IncomingPresets("1000002"), "1000002");
        m.Settings.Reload();
        m.Presets.Reload();

        Assert.Equal(2, m.Presets.ActiveIndex);
        Assert.Equal(new[] { "보스", "쩔", "필드" }, m.Presets.Names);
        Assert.Equal(52, m.Settings.CooldownUiIconSize);
        Assert.Equal(9, m.Settings.CooldownUiPerRow);
        // 픽커가 들고 있는 집합(같은 인스턴스)도 따라와야 한다 — 여집합 "1000002" 를 숨긴 결과.
        Assert.DoesNotContain(1000002, m.Visibility.Codes);
        Assert.Contains(1000001, m.Visibility.Codes);

        // 파일까지 확정됐는지. 여기서 옛 blob 이 남아 있으면 재시작에서 되살아난다.
        Assert.Equal(new[] { "보스", "쩔", "필드" },
            CooldownPresetCodec.Decode(new PropertyHandler(Path.Combine(_temp, "good")).GetProperty("cooldownUi.presets"))!
                .Slots.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Waking_the_picker_first_destroys_the_imported_preset_blob()
    {
        // ❌ M-16 그 자체. 이 테스트는 "이렇게 하면 안 된다"를 고정한다 — 순서를 되돌리거나, 매니저를 깨운 뒤
        // 픽커를 한 번 더 깨우는 코드를 넣으면 실제 앱이 다시 이 상태가 된다.
        CooldownCatalog catalog = Catalog(1000001, 1000002, 1000003);
        Machine m = LocalMachine("bad", catalog);

        WriteImport(m, IncomingPresets("1000002"), "1000002");
        m.Settings.Reload();
        m.Visibility.Reload(); // ← _applying 밖에서 Changed 가 터진다
        m.Presets.Reload();

        // 활성 슬롯 스칼라만 살아남고(설정 파일에서 직접 읽으니까), 슬롯 이름 세 개와 활성 인덱스는 사라진다.
        Assert.Equal(0, m.Presets.ActiveIndex);
        Assert.Equal(
            new[]
            {
                CooldownPresetManager.DefaultName(0),
                CooldownPresetManager.DefaultName(1),
                CooldownPresetManager.DefaultName(2),
            },
            m.Presets.Names);
    }

    [Fact]
    public void Without_a_preset_manager_the_picker_still_has_to_be_reloaded()
    {
        // 프리셋 매니저가 없는 구성(자산 누락 등)에서는 픽커를 직접 깨우는 것이 유일한 경로다. applier 의
        // 분기가 사라지면 가져온 표시 스킬이 재시작 전까지 안 먹는다.
        CooldownCatalog catalog = Catalog(1000001, 1000002, 1000003);
        string dir = Path.Combine(_temp, "nomanager");
        Directory.CreateDirectory(dir);
        var props = new PropertyHandler(dir);
        var visibility = new CooldownVisibility(props, catalog);
        Assert.Contains(1000002, visibility.Codes);

        props.SetProperty("cooldownUi.hidden", "1000002");
        visibility.Reload();

        Assert.DoesNotContain(1000002, visibility.Codes);
    }
}
