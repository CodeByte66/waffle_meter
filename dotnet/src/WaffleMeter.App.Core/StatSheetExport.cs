using System.Globalization;
using System.Text;
using System.Text.Json;
using WaffleMeter.Capture;
using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>Combat rates the meter MEASURED, which the stat window does not show and the stat dictionary does
/// not carry. They are observed frequencies over a real fight, not character stats.</summary>
/// <param name="CritHitRate">치명타 적중률 %, over every hit.</param>
/// <param name="DirectionalHitRate">전방/후방 타격률 %, over flag-bearing hits only (the same denominator the
/// combat detail uses — non-directional hits would otherwise dilute it).</param>
/// <param name="PreferBack">Whether back attacks outnumbered front ones, which picks the calculator's
/// direction toggle.</param>
public readonly record struct MeasuredCombatRates(double CritHitRate, double DirectionalHitRate, bool PreferBack);

/// <summary>
/// Turns the captured stat sheet into the two things the 와터기 stat calculator can consume: a clipboard blob
/// and a pre-filled calculator URL.
///
/// <para><b>Only fields whose meaning lines up are filled.</b> The calculator asks for several numbers the
/// stat dictionary does not contain and the in-game stat window does not show either — 장비 해제 공격력, the
/// weapon tooltip's 최소/최대 공격력, 장비 돌파 레벨 합계, and the 최대 공격력 합계 that means "the additive
/// max-attack bonuses" rather than "your maximum attack". Two ids in the sheet (31/33) look like the latter
/// and are NOT the same quantity, so they are deliberately left out: a plausible-looking wrong number in a
/// calculator is worse than an empty field the user knows to fill.</para>
///
/// <para>The URL uses the site's existing query contract (<c>?c=1&amp;ae=…</c>), which accepts a PARTIAL set —
/// keys we omit fall back to the calculator's defaults. That means the deep link works today with no change
/// on the site; the clipboard format is the one that needs a parser there, and it carries everything
/// (including the raw id→value map) so the site can grow into fields the URL has no key for.</para>
/// </summary>
public static class StatSheetExport
{
    /// <summary>클립보드 접두사. 붙여넣기 받는 쪽이 "이건 우리 포맷"임을 한 눈에 가릴 수 있게 한다.</summary>
    public const string ClipboardPrefix = "WAFFLE_STATS_V1:";

    /// <summary>스키마 이름/버전. 필드가 늘어나면 버전을 올린다 — <b>웹이 먼저</b> 새 버전을 읽을 수 있게 된 뒤에.</summary>
    public const string SchemaName = "waffle-stat-profile-v1";

    public const int SchemaVersion = 1;

    /// <summary>계산기 딥링크의 기본 주소. 도메인 자체는 <c>StatsApiClient</c>가 단독 보관하므로 호출부가
    /// <c>StatsApiClient.CalculatorPageUrl</c>을 넘긴다 — 여기 값은 그 인자를 안 넘겼을 때의 안전한 기본값이다.</summary>
    public const string CalculatorUrl = "https://xn--ok0b896b9wh.kr/calculator";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// 클립보드에 넣을 문자열: <c>WAFFLE_STATS_V1:</c> + UTF-8 JSON의 Base64.
    /// <para>Base64로 감싸는 이유는 숨기려는 게 아니라(그 안은 평문 JSON이다) 줄바꿈·공백이 섞인 붙여넣기에서도
    /// 한 덩어리로 살아남게 하려는 것이다.</para>
    /// </summary>
    public static string BuildClipboard(
        PlayerStatSheet sheet,
        string? jobName,
        string? nickname,
        int server,
        long capturedAt,
        MeasuredCombatRates? measured = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", SchemaName);
            writer.WriteNumber("version", SchemaVersion);
            writer.WriteNumber("capturedAt", capturedAt);
            writer.WriteBoolean("fullSnapshot", sheet.FullSnapshotSeen);
            if (!string.IsNullOrWhiteSpace(jobName)) writer.WriteString("job", jobName);
            if (!string.IsNullOrWhiteSpace(nickname)) writer.WriteString("nickname", nickname);
            if (server > 0) writer.WriteNumber("server", server);

            // Named stats, already in the unit a human reads: percent-ish ids divided by 100, the rest as-is.
            writer.WriteStartObject("stats");
            foreach ((int id, int value) in sheet.Values.OrderBy(kv => kv.Key))
            {
                string? label = PlayerStatIds.Label(id);
                if (label == null) continue;
                if (PlayerStatIds.IsPercent(id)) writer.WriteNumber(label, value / 100.0);
                else writer.WriteNumber(label, value);
            }

            if (sheet.CooldownPercent() is { } cooldown) writer.WriteNumber("쿨타임 감소", cooldown);
            writer.WriteEndObject();

            // 스탯창에 뜨는 합계. 와이어에는 없고 미터가 항을 합친 값이라 별도 블록으로 둔다 — 받는 쪽이
            // "이건 패킷 원본이 아니라 계산된 값"임을 구분할 수 있어야 한다.
            writer.WriteStartObject("derived");
            if (sheet.AttackPower() is { } ap) writer.WriteNumber("공격력", Math.Round(ap));
            if (sheet.DefensePower() is { } dp) writer.WriteNumber("방어력", Math.Round(dp));
            if (sheet.AccuracyTotal() is { } acc) writer.WriteNumber("명중", Math.Round(acc));
            if (sheet.CriticalTotal() is { } crit) writer.WriteNumber("치명타", Math.Round(crit));
            writer.WriteEndObject();

            if (measured is { } m)
            {
                // 스탯창에 없는 값들 — 미터가 실제 전투에서 센 빈도다. 계산기의 '전투 환경' 칸이 이걸 원한다.
                writer.WriteStartObject("measured");
                writer.WriteNumber("치명타 적중률", Math.Round(m.CritHitRate, 2));
                writer.WriteNumber("전방후방 타격률", Math.Round(m.DirectionalHitRate, 2));
                writer.WriteString("방향", m.PreferBack ? "back" : "front");
                writer.WriteEndObject();
            }

            // 이름을 아직 못 붙인 id까지 전부. 라벨링은 인게임 스탯창과 대조해야 확정되는데, 그 대조를 하려면
            // 값이 남아 있어야 한다 — 모르는 id를 버리면 영영 못 붙인다.
            writer.WriteStartObject("raw");
            foreach ((int id, int value) in sheet.Values.OrderBy(kv => kv.Key))
            {
                writer.WriteNumber(id.ToString(Inv), value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return ClipboardPrefix + Convert.ToBase64String(buffer.ToArray());
    }

    /// <summary>
    /// 계산기를 열 URL. 사이트의 기존 쿼리 계약(<c>c=1</c> + 축약 키)을 그대로 쓰므로 <b>웹 수정 없이</b> 동작한다.
    /// 못 채우는 칸은 아예 안 싣는다 — 사이트가 빠진 키를 기본값으로 두기 때문이고, 0을 실으면 "관통 0"처럼
    /// 사용자가 고칠 줄 모르는 거짓말이 된다.
    /// </summary>
    public static string BuildCalculatorUrl(
        PlayerStatSheet sheet, MeasuredCombatRates? measured = null, string baseUrl = CalculatorUrl)
    {
        var query = new List<string> { "c=1" };

        void Flat(string key, int statId)
        {
            if (sheet.Raw(statId) is { } v) query.Add($"{key}={v.ToString(Inv)}");
        }

        void Pct(string key, int statId)
        {
            if (sheet.Percent(statId) is { } v) query.Add($"{key}={v.ToString("0.##", Inv)}");
        }

        // 착용 공격력 = 스탯창에 뜨는 그 숫자다. 와이어에는 합계가 없고 항만 오므로 미터가 합친다
        // (PlayerStatSheet.AttackPower — 실측 대조로 소수점까지 일치). 항 하나(id 317)만 넣으면 실제의 28%
        // 수준이 되고, 계산기의 한계효율은 유한차분이라 분모가 작을수록 공격력 계열 1포인트가 커 보여
        // 인게임에서는 결코 강타 1%를 못 넘는 공격력 옵션이 최상위로 뒤집힌다.
        if (sheet.AttackPower() is { } attackPower)
        {
            query.Add($"ae={Math.Round(attackPower).ToString(Inv)}");
        }

        // 최소/최대 공격력. 계산기가 이 둘을 그대로 받도록 바뀌었다(종전에는 무기 툴팁 구간과 최대 공격력
        // 합계를 따로 받아 rawRange = 무기구간×2 + 합계 로 근사했고, 그 계수 2는 근거가 없었다).
        //   폭 = (최대 − 최소) × (1+증가율) = (1,563 − 973) × 2.2301 = 1,316  ← 실측 일치
        Flat("n1", PlayerStatIds.MinimumAttack);
        Flat("n2", PlayerStatIds.MaximumAttack);

        // 공격력 증가율. 계산기는 이걸 파괴·위력·돌파에서 역산하는데, 실측 캐릭터에서 그 역산이 123.01% 대
        // 119.9%(돌파를 상한까지 채워도)로 어긋난다. mIncEquipped 가 공격력 구성과 구간을 모두 좌우하므로
        // 아는 값을 그대로 넘긴다 — 계산기는 0(미입력)일 때만 역산한다.
        Pct("ai", PlayerStatIds.AttackIncreasePercent);
        Flat("pe", PlayerStatIds.Penetration);       // 관통
        Flat("de", PlayerStatIds.Destruction);       // 파괴
        Flat("mi", PlayerStatIds.Might);             // 위력
        Pct("am", PlayerStatIds.DamageAmplifyPercent);
        Pct("wa", PlayerStatIds.WeaponDamageAmplifyPercent);
        Pct("ca", PlayerStatIds.CriticalDamageAmplifyPercent);
        Pct("sm", PlayerStatIds.HardHitPercent);
        Pct("pf", PlayerStatIds.PerfectPercent);
        Pct("pv", PlayerStatIds.PveDamageAmplifyPercent);
        Pct("ba", PlayerStatIds.BossDamageAmplifyPercent);
        Pct("mh", PlayerStatIds.AdditionalHitAccuracyPercent);

        // 방향 증폭은 앞/뒤가 따로 실린다. 실제로 그 방향으로 때린 비율이 큰 쪽을 고른다 — 계산기가 한 방향만
        // 받기 때문이다. 측정값이 없으면 값이 있는 쪽, 둘 다 있으면 큰 쪽.
        bool preferBack = measured?.PreferBack
            ?? (sheet.Percent(PlayerStatIds.BackDamageAmplifyPercent) ?? 0)
               >= (sheet.Percent(PlayerStatIds.FrontDamageAmplifyPercent) ?? 0);
        int directionalId = preferBack
            ? PlayerStatIds.BackDamageAmplifyPercent
            : PlayerStatIds.FrontDamageAmplifyPercent;
        Pct("da", directionalId);
        query.Add(preferBack ? "di=b" : "di=f");

        if (measured is { } m)
        {
            query.Add($"cr={m.CritHitRate.ToString("0.##", Inv)}");
            query.Add($"dh={m.DirectionalHitRate.ToString("0.##", Inv)}");
        }

        query.Add("p=1"); // PvE

        return baseUrl + "?" + string.Join("&", query);
    }

    /// <summary>사용자에게 보여줄 "무엇이 아직 안 채워졌나" 목록. 빈 목록이면 딥링크가 계산기 입력을 다 채운다는 뜻.
    /// <para>계산기가 요구하지만 <b>어떤 경로로도</b> 자동으로 못 채우는 칸은 여기 고정으로 들어간다 — 사용자가
    /// "왜 안 채워졌지"를 묻기 전에 답해 두는 쪽이 낫다.</para></summary>
    public static IReadOnlyList<string> UnfilledFields(PlayerStatSheet sheet)
    {
        _ = sheet;
        return new List<string>
        {
            // 장비를 벗어야 알 수 있는 값이라 어떤 패킷에도 없다.
            "장비 해제 공격력",
            "장비 돌파 레벨 합계",
        };
    }

    /// <summary>
    /// The captured sheet as the settings tab shows it — the numbers the calculator actually consumes, plus
    /// the four stat-window totals to check them against.
    ///
    /// <para><b>Everything else is captured but not displayed.</b> The sheet holds ~150 entries; the fields
    /// this feature exists to fill are a dozen. The rest — the player's own resistances, 명중/치명타/방어
    /// breakdowns, 주신 스탯 beyond 파괴·위력, and the ids we have not named — were shown while the id→name
    /// mapping was still being confirmed against the in-game stat window. That confirmation is done, so the
    /// list is now the shorter, useful one.</para>
    ///
    /// <para>Nothing is lost by hiding them: <see cref="BuildClipboard"/> still carries every id, named and
    /// unnamed, in its <c>raw</c> block. An id whose value we stopped capturing could never be named later;
    /// an id we merely stopped drawing can.</para>
    /// </summary>
    public static IReadOnlyList<StatSheetGroup> Groups(PlayerStatSheet sheet)
    {
        StatSheetGroup Group(string title, params int[] ids)
        {
            var rows = new List<StatSheetRow>();
            foreach (int id in ids)
            {
                if (sheet.Raw(id) is not { } value) continue;
                rows.Add(new StatSheetRow(PlayerStatIds.Label(id) ?? "#" + id.ToString(Inv), Format(id, value)));
            }

            return new StatSheetGroup(title, rows);
        }

        // 스탯창에 뜨는 합계. 와이어에는 이 숫자가 없고 항만 오므로 미터가 합친 것이라, 사용자가 눈으로
        // 맞춰 볼 수 있게 맨 위에 둔다.
        var derived = new StatSheetGroup("인게임 스탯창 값 (미터가 합산)", new List<StatSheetRow>());
        void Derived(string label, double? value)
        {
            if (value is { } v) derived.Rows.Add(new StatSheetRow(label, Math.Round(v).ToString("N0", Inv)));
        }

        Derived("공격력", sheet.AttackPower());
        Derived("방어력", sheet.DefensePower());
        Derived("명중", sheet.AccuracyTotal());
        // 커뮤니티가 '명중컷'을 말할 때 쓰는 합계(명중 + PvE 명중 + 막기 관통). 스탯창에는 이 줄이 없지만,
        // 통계웹이 발행하는 명중컷이 이 단위라서 사용자가 자기 값과 바로 비교할 수 있어야 한다.
        // ⚠️ 버프·도핑이 반영된 값이다 — 마을에서 본 무버프 값과 전투 중 값은 다르다.
        Derived("명중(컷 기준)", sheet.AccuracyCutTotal());
        Derived("치명타", sheet.CriticalTotal());

        var groups = new List<StatSheetGroup>
        {
            derived,

            // 계산기 딥링크가 실어 보내는 값 그대로. 이 목록과 BuildCalculatorUrl 은 함께 움직여야 한다 —
            // 화면에 보이는데 안 넘어가거나, 넘어가는데 안 보이면 사용자가 어느 쪽을 믿을지 알 수 없다.
            Group("계산기로 넘어가는 값",
                PlayerStatIds.Penetration, PlayerStatIds.DamageAmplifyPercent,
                PlayerStatIds.WeaponDamageAmplifyPercent, PlayerStatIds.CriticalDamageAmplifyPercent,
                PlayerStatIds.PveDamageAmplifyPercent, PlayerStatIds.BossDamageAmplifyPercent,
                PlayerStatIds.HardHitPercent, PlayerStatIds.PerfectPercent,
                PlayerStatIds.AdditionalHitAccuracyPercent,
                PlayerStatIds.FrontDamageAmplifyPercent, PlayerStatIds.BackDamageAmplifyPercent,
                PlayerStatIds.Destruction, PlayerStatIds.Might),

            // 위 '공격력' 한 줄이 어떻게 나왔는지. 계산기가 최소/최대 공격력을 직접 받게 되면 이 항들도
            // 그대로 넘어간다(웹 핸드오프 §11-1).
            Group("공격력 구성",
                PlayerStatIds.Attack, PlayerStatIds.AdditionalAttack,
                PlayerStatIds.MinimumAttack, PlayerStatIds.MaximumAttack,
                PlayerStatIds.AttackIncreasePercent),
        };

        return groups.Where(g => g.Rows.Count > 0).ToList();
    }

    private static string Format(int id, int value) => PlayerStatIds.IsPercent(id)
        ? (value / 100.0).ToString("0.##", Inv) + "%"
        : value.ToString("N0", Inv);
}

/// <summary>One titled block of the stat sheet, as the settings screen lays it out.</summary>
public sealed record StatSheetGroup(string Title, List<StatSheetRow> Rows);

/// <summary>One stat line: a name and the value already in the unit a person reads.</summary>
public sealed record StatSheetRow(string Label, string Value);
