using WaffleMeter.App.Core;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// "가져왔는데 안 바뀐다" 를 구조적으로 막는 계약.
///
/// <para>버프 픽커(M-14), 쿨타임 프리셋 순서(M-16), <c>replay.recordMovement</c> 게이트(M-26) 는 서로 다른
/// 기능이지만 결함은 하나였다 — 가져오기 뒤에 깨워야 할 대상 목록이 <c>SettingsBundleApplier.Apply</c> 안에
/// 손으로 나열돼 있었고, 하나씩 빠져도 아무도 몰랐다. 목록은 이제 카탈로그가 갖고 있고, 여기가 그 목록을
/// 붙잡는다. 개별 누락을 하나씩 때우기만 하면 다음 설정에서 똑같이 재발한다.</para>
///
/// <para>배선 자체(어떤 <c>Reload()</c> 를 부르는가)는 <c>WaffleMeter.App.Wpf</c> 에 있고 그 프로젝트에는
/// 테스트가 없다. 그래서 applier 는 생성자에서 "동작이 없는 태그"를 즉시 던지도록 해 두었다 — 컴파일은
/// 되지만 설정 창을 여는 순간 터진다.</para>
/// </summary>
public sealed class SettingsCatchUpPlanTests
{
    [Fact]
    public void MeterSettings_is_always_reloaded_and_always_first()
    {
        // 나머지 따라잡기 동작이 전부 갱신된 MeterSettings 를 읽는다. 여기가 뒤로 밀리면 그 동작들이
        // 가져오기 이전 값을 게이트에 심는다.
        Assert.Equal(SettingsCatchUp.Settings, SettingsCatchUpPlan.For(Array.Empty<string>())[0]);
        Assert.Equal(SettingsCatchUp.Settings, SettingsCatchUpPlan.For(new[] { "theme", "hotkey" })[0]);
    }

    [Fact]
    public void An_import_that_carries_replay_recordMovement_asks_for_the_replay_gate()
    {
        // M-26. 실효 스위치는 MeterServices.RecordReplay(volatile) 이고 파일·MeterSettings 만 바꾸면
        // 세션 내내 녹화가 안 된다 — 체크박스를 다시 눌러도 세터가 조기 반환해 복구도 안 된다.
        Assert.Contains(SettingsCatchUp.ReplayGate, SettingsCatchUpPlan.For(new[] { "replay.recordMovement" }));
    }

    [Fact]
    public void An_import_that_carries_a_buff_selection_asks_for_the_buff_step()
    {
        // M-14. 픽커를 안 깨우면 가져온 hidden/voice/pinned 가 화면에 안 보이고, 사용자가 칩 하나를
        // 만지는 순간 스테일 캐시가 통째로 덮어쓴다.
        foreach (string key in new[] { "buffUi.hidden", "buffUi.voice", "buffUi.pinned", "buffUi.presets" })
        {
            Assert.Contains(SettingsCatchUp.BuffSelection, SettingsCatchUpPlan.For(new[] { key }));
        }
    }

    [Fact]
    public void An_import_that_carries_cooldown_presets_asks_for_the_cooldown_step()
    {
        // M-16. 픽커와 프리셋이 같은 한 단계로 묶여 있어야 순서가 어긋날 수 없다.
        Assert.Contains(SettingsCatchUp.CooldownSelection, SettingsCatchUpPlan.For(new[] { "cooldownUi.presets" }));
        Assert.Contains(SettingsCatchUp.CooldownSelection, SettingsCatchUpPlan.For(new[] { "cooldownUi.hidden" }));
    }

    [Fact]
    public void An_import_that_carries_the_dummy_toggle_asks_for_the_dummy_gate()
    {
        // M-29 계열. DataManager 는 private volatile 필드를 직접 읽고 props 폴백이 없다.
        Assert.Contains(SettingsCatchUp.DummyGate, SettingsCatchUpPlan.For(new[] { "dummy.testMode" }));
    }

    [Fact]
    public void Steps_come_back_in_declared_order_without_duplicates()
    {
        // 순서는 장식이 아니다. 숫자가 곧 실행 순서라서, 한 단계를 앞뒤로 옮기려면 enum 을 고쳐야 한다.
        IReadOnlyList<SettingsCatchUp> steps =
            SettingsCatchUpPlan.For(new[] { "cooldownUi.presets", "hotkey", "buffUi.hidden", "theme", "hideHotkey" });

        Assert.Equal(
            new[]
            {
                SettingsCatchUp.Settings,
                SettingsCatchUp.Theme,
                SettingsCatchUp.Hotkeys,
                SettingsCatchUp.BuffSelection,
                SettingsCatchUp.CooldownSelection,
            },
            steps);
    }

    [Fact]
    public void An_alarm_only_code_does_not_re_register_global_hotkeys_or_repaint_the_skin()
    {
        // 알림 코드는 알림만 건드린다. 전역 핫키 재등록은 실패가 조용해서, 필요도 없는데 도는 것이
        // 그 자체로 위험하다.
        IReadOnlyList<SettingsCatchUp> steps =
            SettingsCatchUpPlan.For(new[] { "alarms.soundEnabled", "alarms.ttsVoice", "alarms.custom" });

        Assert.Contains(SettingsCatchUp.VoicePack, steps);
        Assert.DoesNotContain(SettingsCatchUp.Hotkeys, steps);
        Assert.DoesNotContain(SettingsCatchUp.Skin, steps);
        Assert.DoesNotContain(SettingsCatchUp.ReplayGate, steps);
    }

    [Fact]
    public void A_key_this_build_does_not_know_asks_for_nothing()
    {
        // applier 가 심지도 않는 키다. 여기서 뭔가를 깨우면 "모르는 키가 동작을 일으킨다"가 된다.
        Assert.Equal(new[] { SettingsCatchUp.Settings }, SettingsCatchUpPlan.For(new[] { "someFutureKey" }));
    }

    [Fact]
    public void Every_catch_up_step_is_claimed_by_at_least_one_key()
    {
        // 죽은 태그 = "이 단계는 이제 아무 설정도 안 깨운다" 인데, 실제로는 키를 옮기다 놓친 경우가 많다.
        SettingsCatchUp[] claimed = SettingsKeyCatalog.All.Select(k => k.CatchUp).Distinct().ToArray();
        string[] orphan = Enum.GetValues<SettingsCatchUp>()
            .Where(t => !claimed.Contains(t))
            .Select(t => t.ToString())
            .ToArray();

        Assert.True(orphan.Length == 0, "아무 키도 요구하지 않는 따라잡기 단계: " + string.Join(", ", orphan));
    }

    [Fact]
    public void Every_catalogued_key_declares_a_defined_catch_up()
    {
        // 지금은 SettingsKey 의 필수 인자라 컴파일러가 강제한다. 누가 기본값을 붙이는 순간 이 테스트가
        // 그 자리를 대신한다 — 조용히 "아무것도 안 깨움" 으로 분류되는 것이 M-14/16/26 의 모양이었다.
        string[] undefined = SettingsKeyCatalog.All
            .Where(k => !Enum.IsDefined(k.CatchUp))
            .Select(k => k.Key)
            .ToArray();

        Assert.True(undefined.Length == 0, "따라잡기 분류가 없는 키: " + string.Join(", ", undefined));
    }
}
