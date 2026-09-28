using System.ComponentModel;

namespace WaffleMeter.App.Core;

/// <summary>
/// 쿨타임 오버레이 프리셋 슬롯의 주인. 불변식은 버프 프리셋과 같다 — <b>라이브 <c>cooldownUi.*</c> 설정이 곧
/// 활성 슬롯의 내용</b>이다. 슬롯을 고르면 그 내용이 라이브로 밀려 나가고, 그 뒤의 모든 편집은 활성 슬롯으로
/// 되받는다. 양쪽이 늘 함께 움직이므로 어긋날 수 없고, 그래서 설정창의 취소를 포함해 아무도 둘을 화해시킬
/// 필요가 없다.
///
/// <para>🔑 <b>버프 매니저와의 결정적 차이</b>: 표시할 스킬 선택(<c>cooldownUi.hidden</c>)은
/// <see cref="MeterSettings"/> 프로퍼티가 <b>아니다</b>. <see cref="CooldownVisibility"/> 가 설정 파일에 직접
/// 읽고 쓴다. 그래서 버프가 쓰는 <c>MeterSettings.PropertyChanged</c> 훅 하나로는 픽커 편집이 <b>영원히</b>
/// 캡처되지 않는다 — 여기서는 스칼라(외형 4개 + 배치 순서)를 그 훅으로, 스킬 선택을
/// <see cref="CooldownVisibility.Changed"/> 로 잡아 두 경로를 함께 쓴다.</para>
///
/// <para>App.Core 에 산다(WPF·Data 참조 없음) — App.Wpf 에는 테스트 프로젝트가 없어서, 이 로직이 저기 있으면
/// 커버리지가 0이 된다.</para>
/// </summary>
public sealed class CooldownPresetManager : IDisposable
{
    public const int SlotCount = 3;

    // 프리셋이 소유하는 스칼라 설정. 일부러 빠진 것: cooldownUi.show(마스터 토글 — 슬롯 전환이 오버레이를
    // 켜고 끄면 안 된다), cooldownUi.presets 자신(캡처하면 Persist 가 이 핸들러로 무한 재진입한다),
    // cooldownOverlayX/Y(창 위치는 기기 고유라 설정 백업에서도 제외돼 있다).
    private static readonly HashSet<string> PresetProps = new()
    {
        nameof(MeterSettings.CooldownUiTransparent),
        nameof(MeterSettings.CooldownUiIconSize),
        nameof(MeterSettings.CooldownUiTextColor),
        nameof(MeterSettings.CooldownUiPerRow),
        nameof(MeterSettings.CooldownUiOrder),
    };

    private readonly MeterSettings _settings;
    private readonly CooldownVisibility _visibility;
    private readonly object _gate = new();

    private CooldownPresetSet _set;
    private bool _applying;

    public CooldownPresetManager(MeterSettings settings, CooldownVisibility visibility)
    {
        _settings = settings;
        _visibility = visibility;
        _set = Load();

        // Load 뒤에 구독한다 — 시드가 편집으로 잡히면 안 된다.
        _settings.PropertyChanged += OnSettingsChanged;
        _visibility.Changed += OnVisibilityChanged;
    }

    public static string DefaultName(int index) => $"프리셋 {index + 1}";

    public int ActiveIndex => _set.Active;

    public string ActiveName => _set.Slots[_set.Active].Name;

    public IReadOnlyList<string> Names => _set.Slots.Select(s => s.Name).ToList();

    /// <summary>프리셋을 설정 파일에서 다시 읽고 활성 슬롯을 재적용한다(설정 가져오기용).
    /// <para>그냥 <c>_set = Load()</c> 가 아닌 이유는 순서다: 표시할 스킬 목록을 실제로 바꾸는 것은
    /// <see cref="Apply"/> 뿐이라, 이걸 빼면 픽커는 가져온 프리셋 이름을 보여 주는데 오버레이는 옛 목록으로
    /// 계속 거른다.</para></summary>
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

    /// <summary>슬롯 이름 바꾸기. 빈 이름은 기본값 "프리셋 N" 으로 되돌아간다.</summary>
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

            List<CooldownPreset> slots = [.. _set.Slots];
            slots[index] = slots[index] with { Name = trimmed };
            _set = _set with { Slots = slots };
            Persist();
        }
    }

    private void Apply(CooldownPreset preset)
    {
        _applying = true;
        try
        {
            _settings.CooldownUiTransparent = preset.Transparent;
            _settings.CooldownUiIconSize = preset.IconSize;
            _settings.CooldownUiTextColor = preset.TextColor;
            _settings.CooldownUiPerRow = preset.PerRow;
            _settings.CooldownUiOrder = preset.Order;

            // 스킬 선택은 가드 안에서 쓴다. 버프는 이 자리에서 '스토어 푸시'를 하므로 가드 밖이어도 되지만,
            // 여기서는 이것이 설정 파일 쓰기라서 밖에 두면 방금 적용한 값을 곧바로 다시 캡처하게 된다.
            // 무조건 호출한다 — 오버레이는 스칼라만 250ms 마다 다시 읽고 표시 목록은 아무도 폴링하지 않으므로,
            // 값이 같아 보여도 여기서 Reload 를 걸어야 픽커와 오버레이가 새 선택을 본다.
            _visibility.SetRawHidden(preset.Hidden);
        }
        finally
        {
            _applying = false;
        }
    }

    // 스칼라 편집(설정창 토글·슬라이더·색상)은 전부 MeterSettings setter 를 지나므로 이 훅 하나로 잡힌다.
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying || e.PropertyName is not { } name || !PresetProps.Contains(name))
        {
            return;
        }

        CaptureIntoActiveSlot();
    }

    // 스킬 선택은 MeterSettings 를 지나지 않는다 — 픽커의 칩 토글은 CooldownVisibility 가 직접 파일에 쓰고
    // 이 이벤트만 낸다. 이 구독이 없으면 "스킬을 골랐는데 슬롯을 바꿨다 돌아오면 원래대로"가 된다.
    private void OnVisibilityChanged()
    {
        if (_applying)
        {
            return;
        }

        CaptureIntoActiveSlot();
    }

    private void CaptureIntoActiveSlot()
    {
        lock (_gate)
        {
            List<CooldownPreset> slots = [.. _set.Slots];
            slots[_set.Active] = CaptureLive(slots[_set.Active].Name);
            _set = _set with { Slots = slots };
            Persist();
        }
    }

    private CooldownPreset CaptureLive(string name) => new()
    {
        Name = name,
        Transparent = _settings.CooldownUiTransparent,
        IconSize = _settings.CooldownUiIconSize,
        TextColor = _settings.CooldownUiTextColor,
        PerRow = _settings.CooldownUiPerRow,
        // 🔑 원문 그대로. 여기서 Codes 의 여집합을 다시 계산하면 (a) 프리셋이 '켠 목록' 저장소가 되어
        // 카탈로그가 커질 때 새 스킬을 슬롯 수만큼 숨기고, (b) 카탈로그 자산이 없는 실행 한 번이 세 슬롯의
        // 선택을 전부 "숨긴 것 없음"으로 덮는다.
        Hidden = _visibility.RawHidden,
        Order = _settings.CooldownUiOrder,
    };

    // 기동은 프리셋을 절대 *적용*하지 않는다: 라이브 설정이 이미 활성 슬롯의 내용이고, 픽커도 그 파일에서
    // 읽었다. 슬롯만 다시 읽고, 무언가 blob 뒤에서 값을 움직였다면(구버전·손으로 고친 파일) 라이브가 이기고
    // 활성 슬롯이 그것을 따라가게 치유한다.
    private CooldownPresetSet Load()
    {
        CooldownPresetSet? stored = CooldownPresetCodec.Decode(_settings.CooldownUiPresets);
        CooldownPresetSet set;
        if (IsUsable(stored))
        {
            List<CooldownPreset> slots = [.. stored.Slots];
            slots[stored.Active] = CaptureLive(slots[stored.Active].Name);
            set = stored with { Slots = slots };
        }
        else
        {
            // 첫 실행(또는 깨진 blob): 사용자가 이미 가진 설정으로 세 슬롯을 전부 시드한다. 그래야 슬롯을
            // 실제로 편집하기 전까지는 전환이 아무 일도 하지 않는다. 슬롯 2·3 을 공장 기본값으로 채우면
            // 이미 픽커를 만져 본 사람의 선택을 조용히 버리게 된다.
            CooldownPreset current = CaptureLive(string.Empty);
            set = new CooldownPresetSet
            {
                Active = 0,
                Slots = [.. Enumerable.Range(0, SlotCount).Select(i => current with { Name = DefaultName(i) })],
            };
        }

        _settings.CooldownUiPresets = CooldownPresetCodec.Encode(set); // 이미 같은 값이면 no-op
        return set;
    }

    private static bool IsUsable([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] CooldownPresetSet? set) =>
        set is { Slots.Count: SlotCount, Active: >= 0 and < SlotCount } && set.Slots.All(s => s is not null);

    private void Persist() => _settings.CooldownUiPresets = CooldownPresetCodec.Encode(_set);

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        _visibility.Changed -= OnVisibilityChanged;
    }
}
