using System.Text;
using WaffleMeter.App.Core;
using WaffleMeter.Data;
using WaffleMeter.Services;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 쿨타임 오버레이 프리셋 3슬롯의 스펙. 버프 프리셋과 같은 불변식을 쓴다 — <b>라이브 <c>cooldownUi.*</c>
/// 설정이 곧 활성 슬롯의 내용</b>.
/// <para>여기서 지키려는 것 중 버프에 없는 것이 둘 있다: ①표시할 스킬은 <see cref="MeterSettings"/> 프로퍼티가
/// 아니라 <see cref="CooldownVisibility"/> 가 직접 쓰므로 별도 훅으로 캡처돼야 하고, ②그 값은 <b>원문</b>으로
/// 담겨야 한다(여집합을 다시 계산하면 카탈로그가 비어 있는 실행 한 번이 세 슬롯을 전부 덮는다).</para>
/// </summary>
public sealed class CooldownPresetTests : IDisposable
{
    private readonly string _temp;

    public CooldownPresetTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "wm_cdpresets_" + Guid.NewGuid().ToString("N"));
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

    private PropertyHandler Props() => new(_temp);

    private static CooldownCatalog Catalog(params int[] codes)
    {
        // 배포 자산에 의존하지 않는 최소 카탈로그. 로더가 공개 API 이므로 JSON 을 만들어 통과시킨다.
        string rows = string.Join(",", codes.Select(c => $"\"{c}\":{{\"j\":{c / 1_000_000},\"n\":\"스킬{c}\",\"cd\":1000,\"gct\":{c},\"auto\":0,\"order\":0}}"));
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

    private static CooldownPresetSet Blob(MeterSettings s)
    {
        CooldownPresetSet? set = CooldownPresetCodec.Decode(s.CooldownUiPresets);
        Assert.NotNull(set);
        return set;
    }

    // ---- codec ----------------------------------------------------------------

    [Fact]
    public void Encoded_blob_is_pure_ascii_even_with_a_korean_slot_name()
    {
        // 설정 읽기가 값을 Latin-1 -> EUC-KR 로 재디코드해 비-Latin-1 문자를 '?' 로 바꾼다. Base64 출력이
        // 순수 ASCII 라는 것이 한글 슬롯 이름이 재시작을 넘어 살아남는 유일한 이유다.
        string encoded = CooldownPresetCodec.Encode(new CooldownPresetSet
        {
            Active = 0,
            Slots = [new CooldownPreset { Name = "보스 딜링" }],
        });

        Assert.All(encoded, ch => Assert.InRange(ch, (char)0x20, (char)0x7E));
        Assert.Equal("보스 딜링", CooldownPresetCodec.Decode(encoded)!.Slots[0].Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 !!")]
    public void A_broken_blob_decodes_to_null_instead_of_throwing(string? raw)
    {
        // 설정 파일 손상이 앱 기동을 인질로 잡으면 안 된다.
        Assert.Null(CooldownPresetCodec.Decode(raw));
    }

    [Fact]
    public void Valid_base64_that_is_not_json_decodes_to_null()
    {
        Assert.Null(CooldownPresetCodec.Decode(Convert.ToBase64String(Encoding.UTF8.GetBytes("nonsense"))));
    }

    // ---- manager --------------------------------------------------------------

    [Fact]
    public void Seeds_three_slots_from_live_settings_on_first_launch()
    {
        PropertyHandler props = Props();
        var settings = new MeterSettings(props) { CooldownUiIconSize = 56, CooldownUiPerRow = 5 };
        var vis = new CooldownVisibility(props, Catalog(14_220_000, 14_310_000));

        using var mgr = new CooldownPresetManager(settings, vis);

        CooldownPresetSet set = Blob(settings);
        Assert.Equal(3, set.Slots.Count);
        Assert.Equal(0, set.Active);
        Assert.All(set.Slots, s => Assert.Equal(56, s.IconSize));
        Assert.All(set.Slots, s => Assert.Equal(5, s.PerRow));
        Assert.Equal(new[] { "프리셋 1", "프리셋 2", "프리셋 3" }, set.Slots.Select(s => s.Name));
    }

    [Fact]
    public void A_scalar_edit_is_captured_into_the_active_slot_only()
    {
        PropertyHandler props = Props();
        var settings = new MeterSettings(props);
        var vis = new CooldownVisibility(props, Catalog(14_220_000));
        using var mgr = new CooldownPresetManager(settings, vis);

        mgr.SelectSlot(1);
        settings.CooldownUiIconSize = 72;

        CooldownPresetSet set = Blob(settings);
        Assert.Equal(72, set.Slots[1].IconSize);
        Assert.Equal(40, set.Slots[0].IconSize);
        Assert.Equal(40, set.Slots[2].IconSize);
    }

    [Fact]
    public void A_skill_toggle_is_captured_even_though_it_never_touches_MeterSettings()
    {
        // 🔑 이번 이식의 1순위 함정. 픽커의 칩 토글은 CooldownVisibility 가 직접 파일에 쓰므로
        // MeterSettings.PropertyChanged 에 절대 안 잡힌다. 버프 매니저를 그대로 복사했다면 이 테스트가 깨진다.
        PropertyHandler props = Props();
        var settings = new MeterSettings(props);
        var vis = new CooldownVisibility(props, Catalog(14_220_000, 14_310_000));
        using var mgr = new CooldownPresetManager(settings, vis);

        vis.Set(14_310_000, visible: false);

        Assert.Equal("14310000", Blob(settings).Slots[0].Hidden);
    }

    [Fact]
    public void Switching_slots_restores_that_slots_skill_selection()
    {
        PropertyHandler props = Props();
        var settings = new MeterSettings(props);
        var vis = new CooldownVisibility(props, Catalog(14_220_000, 14_310_000));
        using var mgr = new CooldownPresetManager(settings, vis);

        vis.Set(14_310_000, visible: false);   // 슬롯 1: 바이젤 끔
        mgr.SelectSlot(1);
        Assert.True(vis.IsVisible(14_310_000)); // 슬롯 2 는 시드 그대로(전부 보임)

        vis.Set(14_220_000, visible: false);   // 슬롯 2: 축복의 활 끔
        mgr.SelectSlot(0);

        Assert.True(vis.IsVisible(14_220_000));
        Assert.False(vis.IsVisible(14_310_000));
    }

    [Fact]
    public void Each_slot_keeps_its_own_skill_arrangement()
    {
        PropertyHandler props = Props();
        var settings = new MeterSettings(props);
        var vis = new CooldownVisibility(props, Catalog(14_220_000, 14_310_000));
        using var mgr = new CooldownPresetManager(settings, vis);

        settings.CooldownUiOrder = "14310000,14220000"; // 슬롯 1: 바이젤을 앞으로
        Assert.Equal("14310000,14220000", Blob(settings).Slots[0].Order);

        mgr.SelectSlot(1);
        Assert.Equal("", settings.CooldownUiOrder);     // 슬롯 2 는 시드 그대로(기본 순서)

        mgr.SelectSlot(0);
        Assert.Equal("14310000,14220000", settings.CooldownUiOrder);
    }

    [Fact]
    public void A_slot_saved_before_arrangements_existed_reads_as_the_default_order()
    {
        // 이 필드가 생기기 전의 blob 에는 Order 키가 없다 — 빈 값(기본 순서)으로 읽혀야 하고 던지면 안 된다.
        string legacy = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "{\"Active\":0,\"Slots\":[{\"Name\":\"a\",\"Hidden\":\"\"},{\"Name\":\"b\"},{\"Name\":\"c\"}]}"));

        CooldownPresetSet set = Assert.IsType<CooldownPresetSet>(CooldownPresetCodec.Decode(legacy));
        Assert.All(set.Slots, s => Assert.Equal("", s.Order));
    }

    [Fact]
    public void The_hidden_string_is_stored_verbatim_not_recomputed()
    {
        // 🔑 여집합을 다시 계산해 담으면 (a) 프리셋이 '켠 목록' 저장소가 되고 (b) 카탈로그가 비어 있는 실행
        // 한 번이 세 슬롯의 선택을 전부 "숨긴 것 없음" 으로 덮는다. 카탈로그 밖 코드가 원문에 남아 있는지로
        // 그 사실을 못 박는다.
        PropertyHandler props = Props();
        props.SetProperty("cooldownUi.hidden", "14310000,99999999");
        var settings = new MeterSettings(props);
        var vis = new CooldownVisibility(props, Catalog(14_220_000, 14_310_000));

        using var mgr = new CooldownPresetManager(settings, vis);

        Assert.Equal("14310000,99999999", Blob(settings).Slots[0].Hidden);
    }

    [Fact]
    public void The_master_toggle_is_not_part_of_a_preset()
    {
        // 슬롯을 고르는 것이 오버레이를 켜고 끄면 안 된다.
        PropertyHandler props = Props();
        var settings = new MeterSettings(props) { ShowCooldownUi = true };
        var vis = new CooldownVisibility(props, Catalog(14_220_000));
        using var mgr = new CooldownPresetManager(settings, vis);

        mgr.SelectSlot(2);

        Assert.True(settings.ShowCooldownUi);
    }

    [Fact]
    public void A_blob_with_the_wrong_shape_is_discarded_and_reseeded_from_live_settings()
    {
        PropertyHandler props = Props();
        var settings = new MeterSettings(props) { CooldownUiIconSize = 64 };
        settings.CooldownUiPresets = CooldownPresetCodec.Encode(new CooldownPresetSet
        {
            Active = 7, // 범위 밖
            Slots = [new CooldownPreset { Name = "하나뿐" }],
        });
        var vis = new CooldownVisibility(props, Catalog(14_220_000));

        using var mgr = new CooldownPresetManager(settings, vis);

        CooldownPresetSet set = Blob(settings);
        Assert.Equal(3, set.Slots.Count);
        Assert.Equal(0, set.Active);
        Assert.All(set.Slots, s => Assert.Equal(64, s.IconSize));
    }

    [Fact]
    public void An_out_of_range_slot_index_is_ignored()
    {
        PropertyHandler props = Props();
        var settings = new MeterSettings(props);
        var vis = new CooldownVisibility(props, Catalog(14_220_000));
        using var mgr = new CooldownPresetManager(settings, vis);

        mgr.SelectSlot(-1);
        mgr.SelectSlot(CooldownPresetManager.SlotCount);

        Assert.Equal(0, mgr.ActiveIndex);
    }

    [Fact]
    public void A_blank_name_falls_back_to_the_default()
    {
        PropertyHandler props = Props();
        var settings = new MeterSettings(props);
        var vis = new CooldownVisibility(props, Catalog(14_220_000));
        using var mgr = new CooldownPresetManager(settings, vis);

        mgr.RenameSlot(0, "보스전");
        Assert.Equal("보스전", mgr.ActiveName);

        mgr.RenameSlot(0, "   ");
        Assert.Equal("프리셋 1", mgr.ActiveName);
    }

    [Fact]
    public void The_selection_survives_a_restart()
    {
        PropertyHandler props = Props();
        var settings = new MeterSettings(props);
        var vis = new CooldownVisibility(props, Catalog(14_220_000, 14_310_000));
        using (var mgr = new CooldownPresetManager(settings, vis))
        {
            mgr.SelectSlot(1);
            mgr.RenameSlot(1, "쫄작");
            settings.CooldownUiPerRow = 12;
        }

        PropertyHandler reopened = Props();
        var settings2 = new MeterSettings(reopened);
        var vis2 = new CooldownVisibility(reopened, Catalog(14_220_000, 14_310_000));
        using var mgr2 = new CooldownPresetManager(settings2, vis2);

        Assert.Equal(1, mgr2.ActiveIndex);
        Assert.Equal("쫄작", mgr2.ActiveName);
        Assert.Equal(12, settings2.CooldownUiPerRow);
    }
}
