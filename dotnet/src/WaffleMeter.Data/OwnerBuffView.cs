namespace WaffleMeter.Data;

/// <summary>
/// One active buff on the local player, as surfaced to the combat-assist overlay + voice alerts.
/// <para><see cref="Code"/> is the BASE skill code (level/rank-independent), so the same buff cast by two
/// different players collapses to one slot and one alert.</para>
/// <para><see cref="Overlay"/> is false for a "음성만" (voice-only) buff — it is returned so the announce path
/// can speak it, but the overlay must not draw it.</para>
/// <para><see cref="EndMs"/> is the absolute expiry (same clock as the caller's <c>nowMs</c>), used to re-arm
/// the end alert when a re-cast extends the buff.</para>
/// <para><see cref="OnCooldown"/> is true when the skill granting this buff is still on cooldown (from the
/// 0x3847 snapshot) — the overlay grays the icon when the user enables that option.</para>
/// <para><see cref="Indefinite"/> is true for an actively-maintained stance with no packet-declared expiry
/// (폭주/권성): its <see cref="RemainingMs"/>/<see cref="EndMs"/> are a synthetic keep-alive, not a real
/// countdown, so the overlay draws no ring/timer and the voice alert never pre-warns its (guessed) end.</para>
/// <para><see cref="Level"/> is the caster's skill level for this buff (어노멀 레벨 1~40; 0 = unknown, which
/// the overlay draws as no badge rather than as "0").</para>
/// <para><see cref="Toggle"/>은 켜 두는 동안 서버가 짧은 지속시간을 1초마다 다시 보내는 on/off 오라
/// (보호의 빛·질주의 진언·불패의 진언)다. 남은 시간이 실제 잔여가 아니라 펄스 사이의 톱니라 오버레이는
/// 시간·링을 그리지 않는다. <see cref="EndMs"/>는 마지막 펄스의 선언 만료 + 3초 유예라, 펄스가 끊긴 뒤
/// 그만큼 더 떠 있고 종료 음성도 그 시점을 따른다.</para>
/// <para><see cref="HeldSinceMs"/>는 on/off 오라가 끊김 없이(유예 안에서) 이어진 유지의 시작 시각이다.
/// 적용 순서 정렬이 이걸 써서 펄스마다 아이콘이 자리를 옮기지 않는다. 그 밖의 버프는 0이다.</para>
/// </summary>
public readonly record struct OwnerBuffView(
    int Code,
    string Name,
    long RemainingMs,
    long DurationMs,
    long EndMs,
    bool ByOther,
    bool Overlay,
    bool OnCooldown,
    bool Indefinite,
    int Level = 0,
    bool Toggle = false,
    long HeldSinceMs = 0);
