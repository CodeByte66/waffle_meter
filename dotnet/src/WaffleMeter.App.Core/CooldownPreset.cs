namespace WaffleMeter.App.Core;

/// <summary>
/// 쿨타임 오버레이 프리셋 한 슬롯: 표시할 스킬 선택과 오버레이 외형을 통째로 담은 이름 붙은 스냅샷.
///
/// <para>마스터 토글 <see cref="MeterSettings.ShowCooldownUi"/> 는 <b>일부러</b> 프리셋에 없다 — 슬롯을 고르는
/// 것이 오버레이를 켜고 끄면 안 된다. <c>cooldownUi.presets</c> 자신도 없다(캡처하면 저장이 무한 재진입한다).
/// 버프 프리셋(<see cref="BuffPreset"/>)이 같은 이유로 같은 것들을 뺀다.</para>
/// </summary>
public sealed record CooldownPreset
{
    public string Name { get; init; } = "";

    public bool Transparent { get; init; } = true;

    public int IconSize { get; init; } = 40;

    public string TextColor { get; init; } = "#FFFFFF";

    public int PerRow { get; init; } = 8;

    /// <summary>표시하지 <b>않는</b> base 스킬 코드의 쉼표 목록 — <c>cooldownUi.hidden</c> 의 <b>원문 복사</b>다.
    /// <para>🔑 집합으로 왕복시키면 안 된다. 저장 형식이 여집합인 이유가 "카탈로그가 커지면 새 스킬이 자동으로
    /// 보인다"인데, 슬롯이 '켠 목록'을 들면 그 성질이 슬롯 수만큼 뒤집힌다. 게다가 여집합을 다시 계산해서
    /// 담으면 카탈로그 자산이 없는 실행 한 번이 세 슬롯의 선택을 전부 "숨긴 것 없음"으로 덮는다 — 쿨타임
    /// 카탈로그는 실제로 하루 만에 249에서 221로 움직였다.</para></summary>
    public string Hidden { get; init; } = "";

    /// <summary>사용자 배치 — <c>cooldownUi.order</c> 원문(<see cref="SkillCooldownOrder"/>). 빈 값 = 기본 순서.
    /// 이 필드가 생기기 전의 슬롯은 빈 값으로 읽혀 기본 순서가 된다.</summary>
    public string Order { get; init; } = "";
}

/// <summary>슬롯 전부와 지금 적용된 슬롯. Base64(JSON) 한 값으로 저장된다 —
/// <see cref="CooldownPresetCodec"/> 참고.</summary>
public sealed record CooldownPresetSet
{
    public int Active { get; init; }

    public List<CooldownPreset> Slots { get; init; } = new();
}
