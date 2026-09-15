using WaffleMeter.App.Core;
using WaffleMeter.Capture;
using WaffleMeter.Data;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// 설정창 레이아웃 미리보기가 그릴 표본 전투.
///
/// <para>🔑 <b>카탈로그에 실제로 등록된 보스</b>를 쓴다(크라오 동굴 · 어려움 · 완성체 베르크). 미등록 몹을
/// 넣으면 난이도 칩과 서브라인이 조용히 비고, 사용자는 "내 레이아웃엔 칩이 없구나"로 읽는다 — 미리보기가
/// 실물보다 적게 보여 주는 쪽이 더 나쁜 오해다.</para>
///
/// <para>남은 HP 도 0 이 아니어야 게이지와 '처치까지'가 나온다. 인원은 4명 — 순위 칩·직업 표시·티어 칩이
/// 전부 한 화면에 들어가면서 미리보기 상자가 세로로 넘치지 않는 최소 수다.</para>
/// </summary>
internal static class LayoutPreviewSample
{
    /// <summary>미리보기에 쓰는 가상 닉네임. 실제 플레이어를 특정할 수 없어야 한다.</summary>
    public const string PreviewNickname = "와터기";

    /// <summary>1001 = 시엘. 서버 태그가 있는 레이아웃에서 "[시엘]" 로 그려진다.</summary>
    public const int PreviewServer = 1001;

    /// <summary>표본 전투 한 판. 시각은 고정 오프셋이라 미리보기를 다시 그려도 숫자가 흔들리지 않는다.</summary>
    public static DpsReport Report(long now) => new()
    {
        BattleStart = now - 145_300,
        BattleEnd = now,
        Target = new MobInfo(999, new Mob(2320171, "완성체 베르크", true), remainHp: 79_650_000, maxHp: 168_750_000),
        // ⚠️ 닉네임은 전부 같은 가상 이름이다. 설정 화면은 스크린샷으로 돌아다니는 곳이라, 실제 플레이어를
        // 특정할 수 있는 이름이 표본에 들어가면 안 된다. 서버도 1001(시엘) 하나로 통일한다.
        Contributors = new List<User>
        {
            new(1, PreviewNickname, PreviewServer, JobClass.SORCERER, isExecutor: true, power: 656_000),
            new(2, PreviewNickname, PreviewServer, JobClass.GLADIATOR, power: 663_400),
            new(3, PreviewNickname, PreviewServer, JobClass.RANGER, power: 659_500),
            new(4, PreviewNickname, PreviewServer, JobClass.CLERIC, power: 591_700),
        },
        Information = new Dictionary<int, DpsInformation>
        {
            [1] = new DpsInformation(59_300_000, 408_239, 35.1, 35.1),
            [2] = new DpsInformation(46_200_000, 318_077, 27.4, 27.4),
            [3] = new DpsInformation(36_300_000, 249_953, 21.5, 21.5),
            [4] = new DpsInformation(27_000_000, 185_861, 16.0, 16.0),
        },
    };

    /// <summary>
    /// 고정 티어. 실제 평가기는 다운로드한 분포 아티팩트를 읽는데, 설정창을 여는 시점에 그게 있으리란
    /// 보장이 없다 — 없으면 칩이 통째로 비어 "이 레이아웃은 티어를 안 보여준다"로 오독된다. 레이아웃마다
    /// 티어 표시가 어떻게 달라지는지가 이 미리보기의 핵심 비교점이라 여기서는 값을 박아 둔다.
    /// <para>4번째 행은 일부러 티어가 없다 — 동의하지 않았거나 표본이 모자란 캐릭터의 모습이다.</para>
    /// </summary>
    public static IReadOnlyDictionary<int, RowTier> Tiers { get; } = new Dictionary<int, RowTier>
    {
        [1] = new RowTier(1, 0.6, "크라오 동굴", ComparisonBasis: "전투력 650k–700k 미만 기준"),
        [2] = new RowTier(2, 4.2, "크라오 동굴", ComparisonBasis: "전투력 650k–700k 미만 기준"),
        [3] = new RowTier(4, 24.8, "크라오 동굴", ComparisonBasis: "전투력 650k–700k 미만 기준"),
    };
}
