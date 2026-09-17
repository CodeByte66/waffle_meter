namespace WaffleMeter.App.Core;

/// <summary>Which share codes a key travels in. Flags — a key is usually in several.</summary>
[Flags]
public enum SettingsProfile
{
    None = 0,

    /// <summary>전체 백업 — 재설치·PC 이사용.</summary>
    Full = 1,

    /// <summary>디자인 — 남에게 보여줄 외형만.</summary>
    Design = 2,

    /// <summary>알림 — 슈고·필드보스·카이라·커스텀 알람과 소리.</summary>
    Alarms = 4,
}

/// <summary>One exportable setting: its storage key and which codes carry it.</summary>
/// <param name="Key">The literal in <c>settings.properties</c>.</param>
/// <param name="Profiles">Which codes include it. <see cref="SettingsProfile.Full"/> is implied by the builder
/// for every entry here, so the flags only distinguish Design/Alarms membership.</param>
/// <param name="Group">Where the import preview groups it, in user language.</param>
/// <param name="Label">User-facing name, for the preview list.</param>
/// <param name="External">True when a class other than <c>MeterSettings</c> owns the key. Those have no live
/// property to read through, so the bundle reads them from the raw file — see
/// <c>PropertyHandler.RawEntries</c> for why the two sources must not be mixed per key.</param>
public sealed record SettingsKey(
    string Key,
    SettingsProfile Profiles,
    string Group,
    string Label,
    bool External = false);

/// <summary>
/// What a settings code may carry, and — just as importantly — what it may not.
/// <para><b>Why a whitelist.</b> Everything lives in one flat <c>settings.properties</c>: display preferences,
/// the ECDSA install private key, the per-character consent map, window coordinates, and one-shot migration
/// flags. "Copy the file" would hand a stranger your identity along with your colours. Only keys listed here
/// ever leave the machine.</para>
/// <para><b>Why the exclusions are listed rather than merely omitted.</b> A key that is simply absent looks the
/// same as a key nobody thought about. <see cref="ExcludedKeys"/> records the decision and the reason, and the
/// completeness test makes a NEW key fail the build until it lands in one list or the other.</para>
/// </summary>
public static class SettingsKeyCatalog
{
    private const SettingsProfile FD = SettingsProfile.Full | SettingsProfile.Design;
    private const SettingsProfile FA = SettingsProfile.Full | SettingsProfile.Alarms;
    private const SettingsProfile F = SettingsProfile.Full;

    public static readonly SettingsKey[] All =
    {
        // ── 표시 형식 ──────────────────────────────────────────────────────────────
        new("displayMode", FD, "표시 형식", "표시 형식"),
        new("damageValueMode", FD, "표시 형식", "딜량 기준"),
        new("rowDpsMetric", FD, "표시 형식", "DPS 종류(DPS/nDPS/rDPS)"),
        new("contributionMode", FD, "표시 형식", "기여도 표시 방식"),
        new("nameDisplay", FD, "표시 형식", "아이디 표기"),
        new("showServerTag", FD, "표시 형식", "서버 표시"),
        new("targetInfoDisplayMode", FD, "표시 형식", "보스 표시 형식"),
        new("barStyle", FD, "표시 형식", "게이지 형태"),
        new("meterLayout", FD, "표시 형식", "레이아웃"),
        // 분리모드는 창을 하나 더 띄우는 **기능 토글**이라 Design(FD) 이면 안 된다 — 공유 코드로
        // 남의 화면에 창이 생기면 안 되기 때문. F(Full) 에만 실린다.
        new("splitUiMode", F, "표시 형식", "UI 분리모드"),
        new("maxVisibleRows", FD, "표시 형식", "표시 인원"),

        // ── 크기와 글꼴 ────────────────────────────────────────────────────────────
        new("fontFamily", FD, "크기와 글꼴", "글꼴"),
        new("meterScalePercent", FD, "크기와 글꼴", "미터 크기"),
        new("rowHeight", FD, "크기와 글꼴", "행 높이"),
        new("bossSlotScale", FD, "크기와 글꼴", "보스칸 높이"),
        new("meterOpacity", FD, "크기와 글꼴", "미터 투명도"),

        // ── 색상 · 스킨 ────────────────────────────────────────────────────────────
        new("skin", FD, "색상 · 스킨", "스타일(스킨)", External: true),
        new("theme", FD, "색상 · 스킨", "테마 색상", External: true),
        new("overlayTheme", FD, "색상 · 스킨", "오버레이 테마"),
        new("nameFx.mode", FD, "색상 · 스킨", "닉네임 효과 표시"),
        new("nameFx.showSelf", FD, "색상 · 스킨", "내 닉네임에 적용"),
        new("nameFx.showOthers", FD, "색상 · 스킨", "파티원 닉네임에 적용"),
        new("nameFx.speedPercent", FD, "색상 · 스킨", "닉네임 효과 속도"),
        new("nameFx.brightnessPercent", FD, "색상 · 스킨", "닉네임 효과 밝기"),
        new("nameFx.gauge", FD, "색상 · 스킨", "게이지 스킨 사용"),

        // ── 상태 표시 · 던전 티어 · 컴팩트 ─────────────────────────────────────────
        new("showAetherStatus", FD, "상태 표시", "오드 표시"),
        new("showLatencyIndicator", FD, "상태 표시", "서버 응답속도 표시"),
        new("tier.show", FD, "던전 티어", "티어 표시(마스터)"),
        new("tier.effects", FD, "던전 티어", "티어 표시"),
        new("tier.showOthers", FD, "던전 티어", "파티원 티어 표시"),
        new("tier.showSelfChip", FD, "던전 티어", "전투 시간 옆 요약"),
        new("isMinimal", FD, "컴팩트 모드", "컴팩트 모드"),
        new("showCombatTimerInMinimal", FD, "컴팩트 모드", "컴팩트 중 전투 시간"),
        new("showTargetInfoInMinimal", FD, "컴팩트 모드", "컴팩트 중 보스"),

        // ── 성능 ───────────────────────────────────────────────────────────────────
        new("refreshIntervalMs", F, "성능", "갱신 주기"),
        new("lowSpecMode", F, "성능", "저사양 모드"),

        // ── 창 동작 ────────────────────────────────────────────────────────────────
        new("isAutoHide", F, "창 동작", "아이온 활성화 시 표시", External: true),
        new("taskbarMode", F, "창 동작", "작업표시줄 / Alt+Tab 모드"),
        new("keepOverlayWhenMeterHidden", F, "창 동작", "미터를 숨겨도 오버레이 유지", External: true),
        new("multiMonitorMode", F, "창 동작", "다중 모니터 이동"),
        new("showJoinPanel", F, "창 동작", "파티 신청 패널 자동 표시"),
        new("closeAction", F, "창 동작", "닫기 버튼 동작"),

        // ── 버프 오버레이 ──────────────────────────────────────────────────────────
        // 아이콘 크기·색·투명도는 순수 외형이라 디자인에도 실린다. 나머지(무엇을 보여줄지, 음성, 프리셋)는
        // 기능 선택이라 전체 백업에만 — 남의 디자인 코드를 받았다고 내 버프 목록이 바뀌면 안 된다.
        new("buffUi.iconSize", FD, "버프 오버레이", "아이콘 크기"),
        new("buffUi.textColor", FD, "버프 오버레이", "지속시간 글씨 색상"),
        new("buffUi.transparent", FD, "버프 오버레이", "투명 배경"),
        new("buffUi.show", F, "버프 오버레이", "버프 오버레이 표시"),
        new("buffUi.showOther", F, "버프 오버레이", "다른 캐릭터가 준 버프 표시"),
        new("buffUi.grayOnCooldown", F, "버프 오버레이", "쿨타임 중 아이콘 회색"),
        new("buffUi.showLevel", F, "버프 오버레이", "아이콘에 버프 레벨 표시"),
        new("buffUi.sortMode", F, "버프 오버레이", "표시 순서"),
        new("buffUi.ttsOnStart", F, "버프 오버레이", "버프 시작 음성"),
        new("buffUi.ttsOnEnd", F, "버프 오버레이", "버프 종료 음성"),
        new("buffUi.endWarning3s", F, "버프 오버레이", "버프 종료 3초 전 알림"),
        new("buffUi.hidden", F, "버프 오버레이", "숨긴 버프"),
        new("buffUi.voice", F, "버프 오버레이", "음성 버프"),
        new("buffUi.pinned", F, "버프 오버레이", "위치 고정 버프"),
        new("buffUi.presets", F, "버프 오버레이", "프리셋 3슬롯"),
        // 관측된 버프 카탈로그. 받는 쪽의 픽커가 '소스가 본 버프'까지 보여줘야 hidden/voice 선택이 말이 된다.
        new("buffUi.observed", F, "버프 오버레이", "관측된 버프 목록"),

        // 전투 상세창의 표시 선택. 오버레이의 buffUi.* 와 별개다 — 프리셋이 건드리는 값과 섞으면
        // 버프 프리셋을 바꿀 때 상세창 필터까지 따라 뒤집힌다.
        new("detail.showPartyBuffs", F, "전투 상세", "버프 업타임에 남이 준 버프 표시"),
        new("detail.timelineCooldownOnly", F, "전투 상세", "스킬 타임라인에 쿨 시작만 표시"),

        new("cooldownUi.show", F, "스킬 쿨타임", "쿨타임 오버레이 표시"),
        new("cooldownUi.iconSize", FD, "스킬 쿨타임", "아이콘 크기"),
        new("cooldownUi.textColor", FD, "스킬 쿨타임", "남은시간 글씨 색상"),
        new("cooldownUi.transparent", FD, "스킬 쿨타임", "투명 배경"),
        new("cooldownUi.perRow", FD, "스킬 쿨타임", "한 줄 최대 개수"),
        new("cooldownUi.presets", F, "스킬 쿨타임", "프리셋 3슬롯"),
        // External: MeterSettings 에 프로퍼티가 없는 키(CooldownVisibility 가 직접 읽고 쓴다) — 손으로
        // 넣지 않으면 완성도 테스트의 시야 밖이라 전체 백업에서 조용히 빠진다. joinSkills.hidden 과 같다.
        new("cooldownUi.hidden", F, "스킬 쿨타임", "표시할 스킬", External: true),
        new("joinSkills.hidden", F, "버프 오버레이", "표시 스킬", External: true),

        // ── 알림 ───────────────────────────────────────────────────────────────────
        new("alarms.soundEnabled", FA, "알림", "알림 소리"),
        new("alarms.volume", FA, "알림", "알림 음량"),
        new("alarms.ttsEnabled", FA, "알림", "음성 알림(한국어)"),
        new("alarms.ttsVoice", FA, "알림", "알림 음성"),
        new("alarms.shugoEnabled", FA, "알림", "슈고 페스타 알림"),
        new("alarms.shugoLead10", FA, "알림", "슈고 10분 전"),
        new("alarms.shugoLead5", FA, "알림", "슈고 5분 전"),
        new("alarms.shugoLead1", FA, "알림", "슈고 1분 전"),
        new("alarms.shugoStart", FA, "알림", "슈고 시작"),
        new("alarms.fieldBossEnabled", FA, "알림", "필드보스 알림"),
        new("alarms.fieldBossLead5", FA, "알림", "필드보스 5분 전"),
        new("alarms.fieldBossLead10", FA, "알림", "필드보스 10분 전"),
        new("alarms.fieldBossLead30", FA, "알림", "필드보스 30분 전"),
        new("alarms.fieldBossMuteInCombat", FA, "알림", "전투 중 알림 숨김"),
        new("alarms.fieldBossDisabled", FA, "알림", "알림 제외 보스"),
        // '감시자' 를 붙여 둔다 — 같은 어비스 하층에 집행자 카이라(2600098)가 따로 있어서, 백업/복원
        // 목록에 "카이라 …" 로만 뜨면 어느 쪽 설정인지 갈리지 않는다. 키는 그대로다(바꾸면 설정이 고아가 된다).
        new("alarms.kairaEnabled", FA, "알림", "감시자 카이라 출현 알림"),
        new("alarms.kairaLead10", FA, "알림", "감시자 카이라 10분 전"),
        new("alarms.kairaLead5", FA, "알림", "감시자 카이라 5분 전"),
        new("alarms.kairaLead1", FA, "알림", "감시자 카이라 1분 전"),
        new("alarms.custom", FA, "알림", "커스텀 알람"),

        // ── 전투 집계 ──────────────────────────────────────────────────────────────
        new("showPreCombatRoster", F, "전투 집계", "전투 전 파티원 표시"),
        new("forceInstanceTracking", F, "전투 집계", "던전 강제 집계"),
        new("dummy.testMode", F, "전투 집계", "허수 테스트"),
        new("dummy.durationSeconds", F, "전투 집계", "허수아비 측정 시간"),
        new("replay.recordMovement", F, "전투 집계", "리플레이 자동 저장", External: true),

        // ── 단축키 ─────────────────────────────────────────────────────────────────
        // 전용 프로파일은 만들지 않는다: RegisterHotKey 실패가 조용해서, 남의 조합을 받아 충돌하면
        // "눌러도 아무 일이 없다"만 남고 원인을 짚을 방법이 없다. 전체 백업(내 PC 이사)에만 싣는다.
        new("hotkey", F, "단축키", "전투 초기화", External: true),
        new("hideHotkey", F, "단축키", "표시 / 숨김", External: true),
        new("clickThroughHotkey", F, "단축키", "클릭 통과 / 잠금", External: true),
        new("dummyToggleHotkey", F, "단축키", "허수아비 켜기/끄기", External: true),
        new("dummyResetHotkey", F, "단축키", "허수아비 DPS 초기화", External: true),
        new("splitUiHotkey", F, "단축키", "UI 분리모드 켜기/끄기", External: true),
        new("aetherListHotkey", F, "단축키", "컨텐츠 관리 열기/닫기", External: true),
    };

    /// <summary>
    /// Keys that must NEVER travel, and why. Listed rather than omitted so the completeness test can tell
    /// "decided against" from "not yet considered".
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ExcludedKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // 이 PC 의 1회성 업그레이드 전용 키다. 카탈로그에 있으면 Full 프로필에 실려 나가고, 도착한 쪽에서
        // SkillVisibility 의 변환 분기가 다시 돌면서 받는 사람이 그동안 골라 둔 선택을 통째로 덮는다
        // — 게다가 변환은 '보낸 사람의 목록이 몰랐던 직업'을 숨김으로 만든다. 주석이 "나가는 코드에는 절대
        // 실리지 않는다" 고 적어 두었지만 카탈로그 등록이 그 말을 거짓으로 만들고 있었다.
        ["visibleSkillCodes"] = "2.10.3 이하 → 2.10.4+ 업그레이드용 로컬 전용 키. 설정 코드로 옮기면 받는 쪽 선택을 덮는다.",
        // 🔒 신원·비밀. 이게 새면 남이 내 캐릭터로 업로드할 수 있다. DPAPI 키는 다른 PC 에서 어차피
        // 복호화가 실패해 조용히 재생성되므로 옮겨 봐야 이득도 없다.
        ["statsInstallKeyPkcs8DpapiV1"] = "설치 서명 개인키",
        ["statsInstallId"] = "설치 식별자",
        ["statsConsentState"] = "동의 상태",
        ["statsUploadEnabled"] = "동의 상태",
        ["statsPublicCharacter"] = "동의 상태",
        ["statsConsentVersion"] = "동의 상태",
        ["statsConsentUpdatedAt"] = "동의 상태",
        ["statsConsentIdentityHash"] = "캐릭터 신원 해시",
        ["statsConsentRemoteExists"] = "동의 상태",
        ["statsConsentSyncStatus"] = "동의 상태",
        ["statsConsentSyncError"] = "동의 상태",
        ["statsConsentServerUpdatedAt"] = "동의 상태",
        ["statsConsentLastSeenAt"] = "동의 상태",
        ["statsConsentCharacters"] = "캐릭터별 동의 맵",

        // 캐릭터별 누적 데이터 — 설정이 아니다. 남의 PC 에 심으면 존재하지 않는 캐릭터의 기록이 뜬다.
        ["aether.lastValue"] = "캐릭터 오드 기록",
        ["aether.perCharacter"] = "캐릭터 오드 기록",
        ["aether.characterNames"] = "캐릭터 이름 캐시",
        ["content.weeklyClears"] = "주간 클리어 기록",
        ["content.abyssCorridors"] = "어비스 회랑 기록",
        ["content.abyssArtifacts"] = "어비스 아티팩트 점령 현황",

        // 기기 고유 — 모니터 구성·네트워크 환경에 묶인다.
        ["uiX"] = "창 위치", ["uiY"] = "창 위치", ["windowX"] = "창 위치", ["windowY"] = "창 위치",
        ["meterWidth"] = "창 크기", ["meterHeight"] = "창 크기",
        ["settingsWidth"] = "창 크기", ["settingsHeight"] = "창 크기",
        ["detailWidth"] = "창 크기", ["detailHeight"] = "창 크기",
        ["joinPanelWidth"] = "창 크기", ["joinPanelHeight"] = "창 크기",
        ["joinPanelX"] = "창 위치", ["joinPanelY"] = "창 위치",
        ["skillFlyoutWidth"] = "창 크기", ["skillFlyoutHeight"] = "창 크기",
        ["cooldownPickerWidth"] = "창 크기", ["cooldownPickerHeight"] = "창 크기",
        ["historyPanelWidth"] = "창 크기", ["historyPanelHeight"] = "창 크기",
        ["historyPanelX"] = "창 위치", ["historyPanelY"] = "창 위치",
        ["aetherPanelWidth"] = "창 크기", ["aetherPanelHeight"] = "창 크기",
        ["aetherPanelX"] = "창 위치", ["aetherPanelY"] = "창 위치",
        ["buffOverlayX"] = "창 위치", ["buffOverlayY"] = "창 위치",
        ["cooldownOverlayX"] = "창 위치", ["cooldownOverlayY"] = "창 위치",
        ["server.ip"] = "캡처 환경", ["server.port"] = "캡처 환경",
        ["server.timeout"] = "캡처 환경", ["server.maxSnapshotSize"] = "캡처 환경",
        ["capture.dedupeGameStreams"] = "캡처 환경", ["capture.selfHealGapMs"] = "캡처 환경",
        ["tier.artifactId"] = "로컬 캐시 포인터", ["tier.fetchedAtMs"] = "로컬 캐시 포인터",
        ["namefx.artifactId"] = "로컬 캐시 포인터", ["namefx.fetchedAtMs"] = "로컬 캐시 포인터",

        // 부작용이 크고 재시작이 필요하다. Npcap 이 없는 PC 로 npcap 설정이 딸려가면 캡처 자체가 죽는다.
        ["captureBackend"] = "재시작 필요 · 환경 의존",
        ["vrrCompatMode"] = "재시작 필요",

        // 1회성 마이그레이션 플래그 — 이식하면 대상 기기가 그 마이그레이션을 영구히 건너뛴다.
        ["meterWidthTierChipMigrated"] = "1회성 마이그레이션",
        ["joinPanelWidthTierChipMigrated"] = "1회성 마이그레이션",
        ["buffUi.defaultsApplied"] = "1회성 기본값 적용",

        // 세션·표시 이력.
        ["patchNotes.lastShownVersion"] = "패치노트 표시 이력",

        // 은퇴한 키. 파일에 고아로 남아 있지만 더 이상 읽지 않는다.
        ["gameOpt.includeAdvanced"] = "은퇴한 키",
    };

    private static readonly Dictionary<string, SettingsKey> ByKey =
        All.ToDictionary(k => k.Key, StringComparer.Ordinal);

    public static SettingsKey? Find(string key) => ByKey.GetValueOrDefault(key);

    public static bool IsKnown(string key) => ByKey.ContainsKey(key);

    /// <summary>Every key a given code carries. <see cref="SettingsProfile.Full"/> means "all of them".</summary>
    public static IEnumerable<SettingsKey> For(SettingsProfile profile) =>
        profile == SettingsProfile.Full ? All : All.Where(k => (k.Profiles & profile) != 0);
}
