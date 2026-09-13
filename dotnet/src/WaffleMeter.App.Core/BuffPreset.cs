namespace WaffleMeter.App.Core;

/// <summary>
/// One buff-overlay preset slot: a named snapshot of the whole combat-assist buff configuration — the
/// per-job 표시/음성 choice (the hidden + voice base-code sets) plus the overlay's display options.
///
/// The master <see cref="MeterSettings.ShowBuffUi"/> toggle is deliberately NOT part of a preset: picking a
/// slot must never turn the overlay itself on or off. Nor is <see cref="MeterSettings.BuffUiObserved"/> —
/// the catalog of buffs ever seen is shared across slots, so a preset can't shrink it.
/// </summary>
public sealed record BuffPreset
{
    public string Name { get; init; } = "";

    public bool Transparent { get; init; } = true;

    public int IconSize { get; init; } = 40;

    public string TextColor { get; init; } = "#FFFFFF";

    public bool TtsOnStart { get; init; }

    public bool TtsOnEnd { get; init; }

    /// <summary>버프 종료 3초 전 알림(점멸 + "이름 오프 예정").</summary>
    public bool EndWarning3s { get; init; }

    public bool GrayOnCooldown { get; init; }

    public bool ShowOther { get; init; } = true;

    /// <summary>Comma-separated base skill codes hidden from the overlay (a verbatim copy of buffUi.hidden,
    /// kept as the raw CSV so it can be assigned straight back without a lossy set round-trip).</summary>
    public string Hidden { get; init; } = "";

    /// <summary>Comma-separated base skill codes that fire the start/end voice alert (buffUi.voice).</summary>
    public string Voice { get; init; } = "";

    /// <summary>오버레이 정렬 모드(buffUi.sortMode).</summary>
    public string SortMode { get; init; } = BuffOverlayOrder.Applied;

    /// <summary>맨 앞에 고정한 base 코드들 — 순서가 곧 표시 순서다(buffUi.pinned).</summary>
    public string Pinned { get; init; } = "";
}

/// <summary>Every preset slot plus the one currently applied. Persisted as a single Base64(JSON) value —
/// see <see cref="BuffPresetCodec"/>.</summary>
public sealed record BuffPresetSet
{
    public int Active { get; init; }

    public List<BuffPreset> Slots { get; init; } = new();
}
