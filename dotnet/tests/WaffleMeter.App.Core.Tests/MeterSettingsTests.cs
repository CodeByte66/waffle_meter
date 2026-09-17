using WaffleMeter.App.Core;
using WaffleMeter.Services;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

public sealed class MeterSettingsTests : IDisposable
{
    private readonly string _temp;

    public MeterSettingsTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "wm_settings_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Defaults_match_react_store()
    {
        var s = new MeterSettings(new PropertyHandler(_temp));
        Assert.Equal("dps_percent", s.DisplayMode);
        Assert.Equal("dps", s.DamageValueMode);
        Assert.Equal("contribution", s.ContributionMode);
        Assert.Equal("all", s.NameDisplay);
        Assert.Equal(36, s.RowHeight);
        Assert.Equal(0.4, s.MeterOpacity);
        Assert.False(s.IsMinimal);
        Assert.False(s.MultiMonitorMode);
        Assert.Equal("dark", s.OverlayTheme);
    }

    // 버프 아이콘 크기는 px 로 저장되고 설정에서만 퍼센트로 보인다(40px = 100%, 슬라이더 80~200% = 32~80px).
    // 저장 단위가 px 인 이유는 이 키가 디자인 공유코드와 프리셋 blob 을 타고 구버전과 오가기 때문 —
    // 그래서 남이 보낸 값이 범위 밖일 수 있고, 읽기/쓰기 양쪽에서 막아야 한다.
    [Fact]
    public void Buff_icon_size_clamps_on_write_and_on_read()
    {
        var props = new PropertyHandler(_temp);
        var s = new MeterSettings(props) { BuffUiIconSize = 999 };

        Assert.Equal(80, s.BuffUiIconSize);
        Assert.Equal("80", props.GetProperty("buffUi.iconSize"));

        s.BuffUiIconSize = 1;
        Assert.Equal(32, s.BuffUiIconSize);

        // 값 검증 없이 심기는 경로(공유코드 적용·수기 편집)를 흉내 낸다.
        props.SetProperty("buffUi.iconSize", "999999");
        Assert.Equal(80, new MeterSettings(props).BuffUiIconSize);
    }

    // 보스칸 높이는 퍼센트로 저장된다 — 100% 기준 높이가 레이아웃마다 달라(전장 104 / 계기판 52 / 무대 70)
    // px 로 저장하면 레이아웃을 바꾼 순간 같은 숫자가 다른 뜻이 되기 때문. 범위 밖 값은 칸이 내용을
    // 못 담게 만들고 그 실패는 조용하므로(ClipToBounds) 버프 아이콘처럼 읽기/쓰기 양쪽에서 막는다.
    [Fact]
    public void Boss_slot_scale_defaults_to_100_and_clamps_on_write_and_on_read()
    {
        var props = new PropertyHandler(_temp);
        var s = new MeterSettings(props);
        Assert.Equal(MeterLayout.BossScaleDefault, s.BossSlotScalePercent);

        s.BossSlotScalePercent = 85;
        Assert.Equal("85", props.GetProperty("bossSlotScale"));
        Assert.Equal(85, new MeterSettings(props).BossSlotScalePercent);

        s.BossSlotScalePercent = 9999;
        Assert.Equal(MeterLayout.BossScaleMax, s.BossSlotScalePercent);

        // 값 검증 없이 심기는 경로(공유코드 적용·수기 편집)를 흉내 낸다.
        props.SetProperty("bossSlotScale", "5");
        Assert.Equal(MeterLayout.BossScaleMin, new MeterSettings(props).BossSlotScalePercent);
    }

    // 종전 "작게" 값 34px 은 정확히 85% — 새 슬라이더의 5% 눈금 위에 그대로 있어야 한다. 아래 범위로
    // 밀려나면 설정창을 여는 것만으로 사용자 값이 조용히 재기록된다.
    [Fact]
    public void Legacy_buff_icon_sizes_survive_the_percent_switch()
    {
        var props = new PropertyHandler(_temp);
        props.SetProperty("buffUi.iconSize", "34");

        Assert.Equal(34, new MeterSettings(props).BuffUiIconSize);
        Assert.Equal(0, 34 * 100 / 40 % 5); // 85% — 눈금 위
    }

    [Fact]
    public void Persists_with_byte_compatible_encoding()
    {
        var props = new PropertyHandler(_temp);
        var s = new MeterSettings(props)
        {
            IsMinimal = true,
            RowHeight = 48,
            MeterOpacity = 0.7,
            DisplayMode = "amount_percent",
        };

        // booleans lowercase, numbers invariant, enums raw — like the Kotlin/React store.
        Assert.Equal("true", props.GetProperty("isMinimal"));
        Assert.Equal("48", props.GetProperty("rowHeight"));
        Assert.Equal("0.7", props.GetProperty("meterOpacity"));
        Assert.Equal("amount_percent", props.GetProperty("displayMode"));

        var reopened = new MeterSettings(new PropertyHandler(_temp));
        Assert.True(reopened.IsMinimal);
        Assert.Equal(48, reopened.RowHeight);
        Assert.Equal(0.7, reopened.MeterOpacity);
        Assert.Equal("amount_percent", reopened.DisplayMode);
    }

    [Fact]
    public void Display_performance_defaults_and_effective_values()
    {
        var s = new MeterSettings(new PropertyHandler(_temp));
        Assert.Equal(500, s.RefreshIntervalMs);
        Assert.Equal(10, s.MaxVisibleRows);
        Assert.False(s.LowSpecMode);
        Assert.Equal(500, s.EffectiveRefreshIntervalMs);
        Assert.Equal(10, s.EffectiveMaxVisibleRows);
    }

    [Fact]
    public void Low_spec_mode_pins_the_interval_ignoring_the_slider()
    {
        var s = new MeterSettings(new PropertyHandler(_temp)) { RefreshIntervalMs = 100 };
        Assert.Equal(100, s.EffectiveRefreshIntervalMs); // slider honored when not low-spec
        s.LowSpecMode = true;
        Assert.Equal(500, s.EffectiveRefreshIntervalMs); // pinned, slider ignored
    }

    [Fact]
    public void Effective_values_clamp_out_of_range_persisted_input()
    {
        var props = new PropertyHandler(_temp);
        props.SetProperty("refreshIntervalMs", "50");   // below floor
        props.SetProperty("maxVisibleRows", "99");      // above cap
        var s = new MeterSettings(props);
        Assert.Equal(100, s.EffectiveRefreshIntervalMs); // clamped to [100,1000]
        Assert.Equal(10, s.EffectiveMaxVisibleRows);     // clamped to [1,10]
    }

    [Fact]
    public void Display_performance_round_trips()
    {
        var props = new PropertyHandler(_temp);
        _ = new MeterSettings(props) { RefreshIntervalMs = 300, MaxVisibleRows = 5, LowSpecMode = true };
        Assert.Equal("300", props.GetProperty("refreshIntervalMs"));
        Assert.Equal("5", props.GetProperty("maxVisibleRows"));
        Assert.Equal("true", props.GetProperty("lowSpecMode"));

        var reopened = new MeterSettings(new PropertyHandler(_temp));
        Assert.Equal(300, reopened.RefreshIntervalMs);
        Assert.Equal(5, reopened.MaxVisibleRows);
        Assert.True(reopened.LowSpecMode);
    }

    [Fact]
    public void Coerces_unknown_enum_to_default()
    {
        var props = new PropertyHandler(_temp);
        props.SetProperty("displayMode", "garbage");
        props.SetProperty("nameDisplay", "me_only");

        var s = new MeterSettings(props);
        Assert.Equal("dps_percent", s.DisplayMode); // coerced
        Assert.Equal("me_only", s.NameDisplay);      // valid
        Assert.Equal(NameDisplay.MeOnly, s.NameDisplayMode);
    }

    /// <summary>
    /// alarms.ttsVoice is the only ReadEnum whitelist made of Korean, so it was the only setting where the
    /// storage layer's Latin-1 damage turned into a visible reset: the stored 와붕이 came back as "???",
    /// missed the whitelist, and coerced to the 와순이 default on every single launch.
    /// </summary>
    [Fact]
    public void Tts_voice_survives_a_restart()
    {
        var s = new MeterSettings(new PropertyHandler(_temp));
        s.TtsVoice = BakedVoicePack.Wabungi;

        var reopened = new MeterSettings(new PropertyHandler(_temp));
        Assert.Equal(BakedVoicePack.Wabungi, reopened.TtsVoice);
    }

    /// <summary>Same damage, different key: a font family with no Latin name is free text with no whitelist to
    /// coerce it, so it silently fell back to the default font instead.</summary>
    [Fact]
    public void Korean_font_family_survives_a_restart()
    {
        var s = new MeterSettings(new PropertyHandler(_temp));
        s.FontFamily = "나눔손글씨 붓";

        Assert.Equal("나눔손글씨 붓", new MeterSettings(new PropertyHandler(_temp)).FontFamily);
    }

    [Fact]
    public void Raises_property_changed_on_csharp_name()
    {
        var s = new MeterSettings(new PropertyHandler(_temp));
        string? changed = null;
        s.PropertyChanged += (_, e) => changed = e.PropertyName;
        s.RowHeight = 50;
        Assert.Equal(nameof(MeterSettings.RowHeight), changed);
    }
}
