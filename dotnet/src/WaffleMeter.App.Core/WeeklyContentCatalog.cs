using WaffleMeter.Capture;

namespace WaffleMeter.App.Core;

/// <summary>One weekly-reset 성역 raid as the 컨텐츠 관리 panel presents it.</summary>
/// <param name="Slug">Stable ASCII key used in the settings blob. NEVER change one — it is the persisted
/// identity of a character's clear record, and a rename orphans everyone's history.</param>
/// <param name="Kind">The packet-side counter this dungeon's clears arrive on.</param>
/// <param name="Name">Full name, for tooltips.</param>
/// <param name="ShortName">What fits next to the icon in a narrow panel.</param>
/// <param name="IconFile">File name under <c>Assets/Icons</c> (the client's own 최종 보스 처치권 art).</param>
/// <param name="FinalBossCodes">The mobCodes of the raid's FINAL boss — the kill the game deducts on. Present
/// for diagnostics and tests, not for the counter itself: the count comes from the server, not from a kill we
/// happened to observe.</param>
public readonly record struct WeeklyContentInfo(
    string Slug,
    WeeklyContentKind Kind,
    string Name,
    string ShortName,
    string IconFile,
    IReadOnlyList<int> FinalBossCodes);

/// <summary>
/// The 성역 raids that reset weekly, one clear per character each.
/// <para>Sourced from the client's own <c>Contents_Ticket_&lt;Dungeon&gt;_Clear</c> currencies
/// ("[성역] &lt;던전&gt; 최종 보스 처치 횟수"), whose Korean description is literally
/// <i>"최종 보스 처치 시 횟수가 차감됩니다"</i>. 무스펠의 성배 gets ONE entry because the client has one such
/// currency for it — 보통(620022) and 어려움(620021) share the weekly count. 비탄의 설원 is the same shape with
/// three difficulties (쉬움 620027 / 보통 620026 / 어려움 620025) behind the one
/// <c>Contents_Ticket_FrozenLament_Clear</c>.</para>
/// </summary>
public static class WeeklyContentCatalog
{
    /// <summary>The base weekly grant, and therefore the denominator the panel shows (<c>n/1</c>).</summary>
    public const int WeeklyGrant = 1;

    public static IReadOnlyList<WeeklyContentInfo> All { get; } =
    [
        new("rud", WeeklyContentKind.Rudra, "심연의 재련 : 루드라", "루드라", "content_rudra.png", [2301014]),
        new("ero", WeeklyContentKind.ErosionPurifier, "침식의 정화소", "정화소", "content_erosion.png", [2301208]),
        new("mus", WeeklyContentKind.MuspelGrail, "무스펠의 성배", "성배", "content_muspel.png", [2301090, 2301060]),

        // 델트라스는 한 전투 안에서 액터가 갈리는 2페이즈 보스다. 여기 적는 건 차감이 걸린 2페이즈 코드뿐 —
        // 1페이즈(2301163/2301143/2301113)도 실제로 딜을 받지만 그 죽음은 페이즈 전환이지 클리어가 아니다.
        // (encounters.json 은 그 셋을 같은 보스의 별칭으로 함께 싣는다: 전투 라벨링은 두 페이즈를 다 알아야 한다.)
        new("lam", WeeklyContentKind.FrozenLament, "비탄의 설원", "설원", "content_lament.png", [2301165, 2301145, 2301115]),
    ];

    public static WeeklyContentInfo? BySlug(string? slug) =>
        All.FirstOrDefault(c => string.Equals(c.Slug, slug, StringComparison.Ordinal)) is { Slug.Length: > 0 } hit
            ? hit
            : null;

    public static WeeklyContentInfo ByKind(WeeklyContentKind kind) => All.First(c => c.Kind == kind);
}

/// <summary>
/// The weekly reset boundary. Everything the game resets weekly — every category in the client's
/// <c>CurrencyLimitSchedule</c> table — does so on <b>Wednesday 05:00 KST</b>, so that is what a remembered
/// clear is judged stale against.
/// <para>Fixed +09:00 rather than the machine's local time: a Korean game's reset does not move because the PC
/// is set to another timezone, and Korea has no DST so an offset is as correct as a timezone database (same
/// reasoning as <c>FieldBossFixedSchedule</c> and <c>KairaAlarm</c>). 슈고 페스타·커스텀 알람은 여전히
/// <c>DateTime.Now</c> 를 쓰는데, 그건 사용자가 자기 벽시계로 고른 리마인더라 다른 얘기다 — 서버 일정을
/// 겨냥하는 알람(<c>KairaAlarm</c> 의 4시간 격자)은 여기와 같은 이유로 KST 에 고정한다.</para>
/// </summary>
public static class WeeklyContentReset
{
    private static readonly TimeSpan Kst = TimeSpan.FromHours(9);

    public const DayOfWeek Day = DayOfWeek.Wednesday;
    public const int Hour = 5;

    /// <summary>The most recent reset at or before <paramref name="atMs"/>, as Unix ms. A record stamped before
    /// this has been recharged by the server since, so its value must not be shown.</summary>
    public static long LastResetAtOrBefore(long atMs)
    {
        if (atMs < DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
            || atMs > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            return 0; // a hand-edited settings file must not throw the panel down
        }

        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(atMs).ToOffset(Kst);

        // Walk back at most a full week: today if it is Wednesday and 05:00 has already passed, else the
        // Wednesday before. i == 7 is what catches "today is Wednesday but it is still 04:00".
        for (int i = 0; i <= 7; i++)
        {
            DateTime day = now.Date.AddDays(-i);
            if (day.DayOfWeek != Day)
            {
                continue;
            }

            var at = new DateTimeOffset(day.Year, day.Month, day.Day, Hour, 0, 0, Kst);
            if (at <= now)
            {
                return at.ToUnixTimeMilliseconds();
            }
        }

        return 0;
    }

    /// <summary>True when a record stamped <paramref name="savedAtMs"/> still describes the current week.</summary>
    public static bool IsCurrentWeek(long savedAtMs, long nowMs) =>
        savedAtMs > 0 && savedAtMs >= LastResetAtOrBefore(nowMs);
}

/// <summary>
/// Decides when a weekly counter from the 0x610B login/zone-in dump may be filed against the character the
/// meter currently believes it is watching.
/// <para>The dump beats the own-load packet that NAMES the character by about four seconds — measured on 14 of
/// 14 zone-ins across the capture corpus, no counter-example. Filing on arrival is therefore wrong exactly when
/// it matters: on a character switch it writes the incoming character's counters onto the outgoing character's
/// record, and a weekly clear is a week-long claim rather than a number that refreshes on its own. Waiting for
/// an identity established at or after the dump picks out, by construction, the character the dump describes.</para>
/// <para>A 0x610C delta is not subject to this: it only fires when a counter changes, which means the character
/// has been in the zone fighting and the identity settled long ago.</para>
/// </summary>
public static class WeeklyContentOwnership
{
    /// <summary>How long a dump waits for its naming packet before it is filed under whoever is current.
    /// Generous next to the ~4 s observed, because filing early corrupts another character's week while filing
    /// late costs only a delayed refresh — and a dump that is never followed by a naming packet (a re-send while
    /// already in the zone) must not freeze the panel on a stale reading forever.</summary>
    public const long SettleMs = 30_000;

    /// <summary>Whether a dump that arrived at <paramref name="dumpAtMs"/> can be attributed to the identity
    /// established at <paramref name="identityAtMs"/> (0 = never established).</summary>
    public static bool CanFile(long dumpAtMs, long identityAtMs, long nowMs) =>
        (identityAtMs > 0 && identityAtMs >= dumpAtMs) || nowMs - dumpAtMs >= SettleMs;
}
