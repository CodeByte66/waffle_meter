using System.Globalization;

namespace WaffleMeter.App.Core;

/// <summary>
/// The 감시자 카이라 (어비스 하층) reminder. This boss is the one field boss the server never times: its
/// 0x9101 record arrives with a zeroed timestamp in every capture we have, so it cannot hang off the
/// respawn-timer alarm at all. It gets a clock of its own, and that clock fires wherever you are rather than
/// only inside the abyss — the whole point is to travel there before it spawns.
/// <para><b>2026-09-02 인게임 패치.</b> 출현 확률 20% → <b>100%</b>, 주기는 4시간 확정. 종전의 "매시 정각,
/// 나올 수도 안 나올 수도"가 아니다. 헛걸음이 없어졌으므로 알림 문구에서도 확률성("출현 가능")을 걷어냈다.</para>
/// <para><b>⚠️ 2026-09-22 정정 — 격자의 기준은 0시가 아니라 1시다.</b> 인게임 패치 문구가 "0시를 기준으로
/// 4시간마다"였고 그걸 그대로 믿어 00·04·08·12·16·20시로 넣었는데, 실제 출현은
/// <b>01·05·09·13·17·21시</b>였다(사용자 제보). 주기(4시간)는 맞고 <b>앵커만 한 시간 밀려 있었다</b> —
/// 즉 알림이 매번 <b>한 시간 일찍</b> 울렸다. 인게임 문구는 격자의 근거로 쓸 수 없다는 뜻이므로,
/// 앵커를 <see cref="SpawnAnchorHourKst"/> 상수로 뽑아 두고 설정 화면 문구까지 여기서 파생시킨다 —
/// 다음에 또 밀리면 상수 하나만 고치면 되고, 화면에 적힌 시각이 로직과 어긋날 수가 없다.
/// (종전엔 XAML 에 "0·4·8·12·16·20시"를 손으로 적어 둬서 둘이 따로 놀 수 있었다.)</para>
/// <para><b>격자는 머신 로컬이 아니라 고정 +09:00 에 건다.</b> <c>WeeklyContentReset</c>·
/// <c>FieldBossFixedSchedule</c> 과 같은 논리다 — 한국 서버의 콘텐츠 일정은 PC 시간대가 달라도 움직이지
/// 않고, 한국엔 DST 가 없어 고정 오프셋이 tz 데이터베이스와 정확히 동등하다. 매시 정각이던 시절엔 분
/// (minute)만 봐서 정수 오프셋 시간대에선 우연히 맞았지만 <b>4시간 격자는 그렇지 않다</b>: 로컬 시로
/// 재면 UTC+8(중국·홍콩·대만·싱가포르)은 여섯 슬롯이 전부 한 시간 어긋나고, DST 가 있는 지역은 전환일에
/// 사용자가 아무것도 안 했는데 조용히 고장난다. (슈고 페스타는 여전히 매시 정각이라 사용자 벽시계를 쓰는
/// <see cref="HourlyAlarm"/> 그대로다.)</para>
/// </summary>
public static class KairaAlarm
{
    private static readonly TimeSpan Kst = TimeSpan.FromHours(9);

    /// <summary>출현 주기(시간). <see cref="SpawnAnchorHourKst"/> 부터 이 간격마다 정각 출현.</summary>
    public const int SpawnIntervalHours = 4;

    /// <summary>격자의 기준 시각(KST 시). 여기서부터 <see cref="SpawnIntervalHours"/> 간격 —
    /// 01·05·09·13·17·21시. ⚠️ 이 값이 유일한 정본이다: 슬롯 목록도 설정 화면 문구도 여기서 파생된다.
    /// 게임이 격자를 또 옮기면 <b>여기만</b> 고쳐라.</summary>
    public const int SpawnAnchorHourKst = 1;

    private const int CycleMinutes = SpawnIntervalHours * 60;

    private const int AnchorMinutes = SpawnAnchorHourKst * 60;

    /// <summary>출현 정각(KST 시)을 오름차순으로. 설정 화면 문구와 테스트가 이걸 읽는다.</summary>
    public static IReadOnlyList<int> SpawnHoursKst { get; } = BuildSpawnHours();

    /// <summary>설정 화면에 그대로 박히는 시각 목록("1·5·9·13·17·21"). 화면 문구를 손으로 적지 않기
    /// 위한 것이다 — 그렇게 두면 격자가 움직였을 때 로직만 고치고 문구는 옛 시각을 계속 주장한다.
    /// <para>일부러 <b>계산 속성</b>이다(초기화식이 아니라). 정적 초기화식은 소스 순서로 도는데, 누가 문서를
    /// 정리하며 이 줄을 <see cref="SpawnHoursKst"/> 위로 올리면 그때 null 이라 <c>TypeInitializationException</c>
    /// 이 나고, 그게 터지는 자리는 앱 기동이 아니라 <c>AlarmController.Poll()</c> — UI 스레드의 1초 타이머다.</para></summary>
    public static string SpawnHoursText =>
        string.Join("·", SpawnHoursKst.Select(h => h.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The lead minutes enabled in settings (10 / 5 / 1 minutes before the spawn).</summary>
    public static IReadOnlyCollection<int> EnabledLeads(MeterSettings s)
    {
        var leads = new HashSet<int>();
        if (s.KairaLead10)
        {
            leads.Add(10);
        }

        if (s.KairaLead5)
        {
            leads.Add(5);
        }

        if (s.KairaLead1)
        {
            leads.Add(1);
        }

        return leads;
    }

    /// <summary>
    /// The lead (minutes before the next KST 01/05/09/13/17/21 spawn) that is due exactly at
    /// <paramref name="nowMs"/>, or null when none of <paramref name="enabledLeads"/> matches this minute.
    /// At most one lead can be due in any given minute.
    /// <para>Unix ms rather than a <see cref="DateTime"/> on purpose: a <c>DateTime</c> whose <c>Kind</c> is
    /// <c>Unspecified</c> gets read as LOCAL time, so a test written that way would answer one thing on a KST
    /// box and another on a UTC one — and this repo runs no tests in CI, so that drift would never surface.</para>
    /// </summary>
    public static int? DueLead(long nowMs, IReadOnlyCollection<int> enabledLeads)
    {
        int untilSpawn = MinutesUntilSpawn(nowMs);
        return enabledLeads.Contains(untilSpawn) ? untilSpawn : null;
    }

    /// <summary>The next spawn instant at or after <paramref name="nowMs"/>, truncated to the minute so the
    /// toast can print an exact HH:mm. Callers render it in the user's own local time — the grid is anchored
    /// to KST, but what the user reads off their clock is their own wall time.
    /// <para>날짜를 만지지 않고 "이번 분 + 남은 분"으로 더하므로 자정을 넘는 슬롯(21시 다음 = 익일 1시)도
    /// 그대로 맞는다 — 앵커가 0시가 아니게 되면서 자정 횡단이 <b>정상 경로</b>가 됐다.</para></summary>
    public static long NextSpawnMs(long nowMs)
    {
        long thisMinute = nowMs - (nowMs % 60_000L);
        return thisMinute + (MinutesUntilSpawn(nowMs) * 60_000L);
    }

    // HourlyAlarm.DueLead 의 `Minute == 0 ? 0 : 60 - Minute` 를 cycle=240 + 앵커로 일반화한 것. 공유하지
    // 않고 일부러 복제했다 — 하나로 합치면 다음 사람이 이 4시간 격자를 슈고 페스타 쪽으로 흘리게 된다.
    // 1440 % CycleMinutes == 0 이라 앵커를 어디에 걸든 격자가 자정에서 끊기지 않는다.
    private static int MinutesUntilSpawn(long nowMs)
    {
        DateTimeOffset kst = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).ToOffset(Kst);
        int sinceAnchor = ((kst.Hour * 60) + kst.Minute) - AnchorMinutes;
        // ⚠️ C# 의 % 는 피연산자가 음수면 음수를 낸다(0 쪽으로 절삭). 앵커가 0시가 아닌 이상 00:00~00:59
        // 는 실제로 음수라 한 번 더 감싸야 한다 — 이걸 빼면 하루 중 그 한 시간만, 그것도 하필 01:00 슬롯
        // 직전에 조용히 틀린다(00:30 이면 240-(-30)=270 을 돌려준다).
        // ⚠️ `(x - Anchor + Cycle) % Cycle` 로 "단순화"하지 마라 — 지금은 같지만 앵커가 주기보다 커지는
        // 순간 깨지고, BuildSpawnHours 는 그 경우를 일부러 허용한다(아래). 두 경로의 관용도가 갈린다.
        int intoCycle = ((sinceAnchor % CycleMinutes) + CycleMinutes) % CycleMinutes;
        return intoCycle == 0 ? 0 : CycleMinutes - intoCycle;
    }

    private static int[] BuildSpawnHours()
    {
        // 앵커를 주기로 접어 하루의 첫 슬롯을 찾는다(앵커가 주기보다 커도 같은 격자가 나오도록).
        var hours = new List<int>();
        for (int h = SpawnAnchorHourKst % SpawnIntervalHours; h < 24; h += SpawnIntervalHours)
        {
            hours.Add(h);
        }

        return hours.ToArray();
    }
}
