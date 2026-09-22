using WaffleMeter.App.Core;
using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>Spec for the pure field-boss reminder schedule (<see cref="FieldBossAlarm"/>) and the boss
/// catalog it reads names and regions from.</summary>
public class FieldBossAlarmTests
{
    private const long Now = 1_783_000_000_000L;

    [Fact]
    public void Catalog_lists_every_boss_split_by_region()
    {
        var all = FieldBossCatalog.All();
        Assert.Equal(85, all.Count);
        Assert.Equal(24, FieldBossCatalog.InRegion(FieldBossRegion.Verteron).Count);
        Assert.Equal(24, FieldBossCatalog.InRegion(FieldBossRegion.Altgard).Count);
        Assert.Equal(12, FieldBossCatalog.InRegion(FieldBossRegion.Eltnen).Count);
        Assert.Equal(12, FieldBossCatalog.InRegion(FieldBossRegion.Morheim).Count);
        Assert.Equal(13, FieldBossCatalog.InRegion(FieldBossRegion.Abyss).Count);
        Assert.All(all, b => Assert.False(string.IsNullOrWhiteSpace(b.Name)));
        Assert.Equal(all.Count, all.Select(b => b.Code).Distinct().Count());
        Assert.Equal(all.Count, all.Select(b => b.WireCode).Distinct().Count());
    }

    [Fact]
    public void Every_region_maps_to_the_world_map_ids_the_broadcast_carries()
    {
        Assert.True(FieldBossCatalog.TryResolveRegionForMap(1010, out FieldBossRegion verteron));
        Assert.Equal(FieldBossRegion.Verteron, verteron);
        Assert.True(FieldBossCatalog.TryResolveRegionForMap(1011, out FieldBossRegion eltnen));
        Assert.Equal(FieldBossRegion.Eltnen, eltnen);
        Assert.True(FieldBossCatalog.TryResolveRegionForMap(1110, out FieldBossRegion altgard));
        Assert.Equal(FieldBossRegion.Altgard, altgard);
        Assert.True(FieldBossCatalog.TryResolveRegionForMap(1111, out FieldBossRegion morheim));
        Assert.Equal(FieldBossRegion.Morheim, morheim);
        Assert.True(FieldBossCatalog.TryResolveRegionForMap(FieldBossCatalog.AbyssLowerMapId, out FieldBossRegion low));
        Assert.Equal(FieldBossRegion.Abyss, low);
        Assert.True(FieldBossCatalog.TryResolveRegionForMap(FieldBossCatalog.AbyssMiddleMapId, out FieldBossRegion mid));
        Assert.Equal(FieldBossRegion.Abyss, mid);

        Assert.False(FieldBossCatalog.TryResolveRegionForMap(9999, out _));
    }

    [Fact]
    public void Wire_codes_resolve_slot_codes_mob_codes_and_the_legacy_alias()
    {
        // 베르테론/알트가르드/어비스 ride a per-map slot code…
        Assert.True(FieldBossCatalog.TryResolveWireCode(101002, out int kutar));
        Assert.Equal(2100040, kutar);
        Assert.True(FieldBossCatalog.TryResolveWireCode(111001, out int danar));
        Assert.Equal(2400017, danar);
        Assert.True(FieldBossCatalog.TryResolveWireCode(2002, out int kaira));
        Assert.Equal(2600089, kaira);

        // …엘테넨/모르헤임 carry the mob code itself.
        Assert.True(FieldBossCatalog.TryResolveWireCode(2406034, out int pargon));
        Assert.Equal(2406034, pargon);

        // the pre-datamine 니호그 code still resolves to the corrected one
        Assert.True(FieldBossCatalog.TryResolveWireCode(2101349, out int nidhogg));
        Assert.Equal(2101343, nidhogg);

        Assert.False(FieldBossCatalog.TryResolveWireCode(424242, out _));
    }

    [Fact]
    public void Map_scoped_resolution_rejects_a_code_from_another_region()
    {
        Assert.True(FieldBossCatalog.TryResolveWireCode(101002, 1010, out int onItsOwnMap));
        Assert.Equal(2100040, onItsOwnMap);
        Assert.False(FieldBossCatalog.TryResolveWireCode(101002, 1111, out _)); // 베르테론 슬롯코드 in a 모르헤임 table
    }

    [Fact]
    public void A_lead_is_due_inside_its_one_minute_window()
    {
        var timers = new Dictionary<int, long> { [2406034] = Now + 10 * 60_000L - 5_000 }; // 9m55s out
        var due = FieldBossAlarm.DueAlerts(timers, Now, new[] { 10 });

        FieldBossAlarm.Due d = Assert.Single(due);
        Assert.Equal(2406034, d.Code);
        Assert.Equal(10, d.LeadMinutes);
    }

    [Fact]
    public void A_lead_is_not_due_before_or_after_its_window()
    {
        var timers = new Dictionary<int, long> { [2406034] = Now + 12 * 60_000L }; // 12m out
        Assert.Empty(FieldBossAlarm.DueAlerts(timers, Now, new[] { 10 }));         // before the (9,10] window

        var past = new Dictionary<int, long> { [2406034] = Now - 60_000L };        // already spawned
        Assert.Empty(FieldBossAlarm.DueAlerts(past, Now, new[] { 10 }));
    }

    [Fact]
    public void Multiple_leads_can_each_fire()
    {
        var timers = new Dictionary<int, long>
        {
            [2406034] = Now + 5 * 60_000L - 1_000,   // in the 5-min window
            [2101217] = Now + 30 * 60_000L - 1_000,  // in the 30-min window
        };
        var due = FieldBossAlarm.DueAlerts(timers, Now, new[] { 5, 10, 30 });
        Assert.Equal(2, due.Count);
        Assert.Contains(due, d => d.Code == 2406034 && d.LeadMinutes == 5);
        Assert.Contains(due, d => d.Code == 2101217 && d.LeadMinutes == 30);
    }

    [Fact]
    public void Key_is_stable_per_boss_respawn_lead()
    {
        var d = new FieldBossAlarm.Due(2406034, Now, 10);
        Assert.Equal(FieldBossAlarm.Key(d), FieldBossAlarm.Key(new FieldBossAlarm.Due(2406034, Now, 10)));
        Assert.NotEqual(FieldBossAlarm.Key(d), FieldBossAlarm.Key(new FieldBossAlarm.Due(2406034, Now, 5)));
    }

    [Fact]
    public void Catalog_resolves_known_and_unknown_codes()
    {
        Assert.Equal("경계의 방랑자 파르곤", FieldBossCatalog.Name(2406034));
        Assert.True(FieldBossCatalog.IsKnown(2406034));
        Assert.False(FieldBossCatalog.IsKnown(9999999));
        Assert.Contains("9999999", FieldBossCatalog.Name(9999999));
        Assert.Equal(FieldBossRegion.Morheim, FieldBossCatalog.Region(2406034));
        Assert.Null(FieldBossCatalog.Region(9999999));
    }

    [Fact]
    public void Fixed_schedule_only_covers_the_abyss_fortress_bosses()
    {
        Assert.True(FieldBossFixedSchedule.HasFixedSchedule(2600084));   // 수호신장 나흐마 — 요새 공성
        Assert.False(FieldBossFixedSchedule.HasFixedSchedule(2406034));  // 모르헤임은 일반 리스폰 타이머
        Assert.Equal("금·일 22:05", FieldBossFixedSchedule.Describe(2600520));   // 실캡처: 금 22:05
        Assert.Equal("수·토 22:35", FieldBossFixedSchedule.Describe(2600156));   // 실캡처: 수 22:35
        Assert.Null(FieldBossFixedSchedule.Describe(2406034));

        // 감시자 카이라는 리젠 타이머가 아니라 4시간 격자 출현 알림으로 다룬다 — 여기에도, picker에도 없다.
        Assert.False(FieldBossFixedSchedule.HasFixedSchedule(FieldBossCatalog.ScheduledSpawnCode));
        Assert.True(FieldBossCatalog.HasOwnAlarm(FieldBossCatalog.ScheduledSpawnCode));
        Assert.False(FieldBossCatalog.HasOwnAlarm(2600098));   // 집행자 슬롯 카이라는 일반 알림 대상
    }

    private static readonly TimeSpan Kst = TimeSpan.FromHours(9);

    /// <summary>KST 벽시계 시각을 Unix ms 로. 테스트가 실행 머신 시간대에 좌우되면 안 되므로 오프셋을
    /// 명시한다 — 이 저장소는 CI 에서 테스트를 돌리지 않아 그런 드리프트를 아무도 못 잡는다.</summary>
    private static long KstMs(int h, int m, int day = 2) =>
        new DateTimeOffset(2026, 9, day, h, m, 0, Kst).ToUnixTimeMilliseconds();

    /// <summary>격자의 정본은 <see cref="KairaAlarm.SpawnAnchorHourKst"/> 하나다. 시각을 어딘가에 또 적으면
    /// 그 사본이 로직과 갈라진다 — 2026-09-22 에 실제로 갈라졌다(로직은 0시 앵커, 실제 출현은 1시 앵커).</summary>
    [Fact]
    public void The_grid_is_anchored_at_kst_one_oclock_every_four_hours()
    {
        Assert.Equal(1, KairaAlarm.SpawnAnchorHourKst);
        Assert.Equal(4, KairaAlarm.SpawnIntervalHours);
        Assert.Equal(new[] { 1, 5, 9, 13, 17, 21 }, KairaAlarm.SpawnHoursKst);

        // 설정 화면 문구도 같은 목록에서 나온다 — 손으로 적어 두면 거기만 옛 시각이 남는다.
        Assert.Equal("1·5·9·13·17·21", KairaAlarm.SpawnHoursText);
    }

    /// <summary>이번 수정의 핵심 회귀 그물. 종전 구현은 0시 앵커라 여섯 슬롯이 전부 <b>한 시간 일찍</b>
    /// 울렸다. 옛 슬롯의 리드 분은 조용하고, 정확히 한 시간 뒤가 울려야 한다.</summary>
    [Fact]
    public void The_old_midnight_anchored_slots_no_longer_fire()
    {
        var leads = new[] { 10, 5, 1 };

        // 옛 격자(0·4·8·12·16·20시)의 10분 전 — 지금은 전부 조용해야 한다.
        Assert.Null(KairaAlarm.DueLead(KstMs(23, 50, day: 1), leads));   // 옛 0시 슬롯
        Assert.Null(KairaAlarm.DueLead(KstMs(3, 50), leads));            // 옛 4시 슬롯
        Assert.Null(KairaAlarm.DueLead(KstMs(19, 50), leads));           // 옛 20시 슬롯

        // 새 격자(1·5·9·13·17·21시) — 정확히 한 시간 뒤가 슬롯이다.
        Assert.Equal(10, KairaAlarm.DueLead(KstMs(0, 50), leads));       // 1시 슬롯
        Assert.Equal(10, KairaAlarm.DueLead(KstMs(4, 50), leads));       // 5시 슬롯
        Assert.Equal(10, KairaAlarm.DueLead(KstMs(20, 50), leads));      // 21시 슬롯
    }

    [Fact]
    public void Kaira_leads_are_due_only_before_a_four_hour_slot()
    {
        var leads = new[] { 10, 5, 1 };

        // 21시는 출현 슬롯 — 리드가 맞는 분에만 뜬다.
        Assert.Equal(10, KairaAlarm.DueLead(KstMs(20, 50), leads));
        Assert.Equal(5, KairaAlarm.DueLead(KstMs(20, 55), leads));
        Assert.Equal(1, KairaAlarm.DueLead(KstMs(20, 59), leads));
        Assert.Null(KairaAlarm.DueLead(KstMs(20, 52), leads));   // 리드에 없는 분
        Assert.Null(KairaAlarm.DueLead(KstMs(21, 0), leads));    // 출현 정각 자체는 0 lead → 켜진 리드가 없다

        // 22시는 슬롯이 아니다 — 옛 '매시 정각' 구현이라면 여기서 울렸다.
        Assert.Null(KairaAlarm.DueLead(KstMs(21, 50), leads));
        Assert.Null(KairaAlarm.DueLead(KstMs(21, 55), leads));
        Assert.Null(KairaAlarm.DueLead(KstMs(21, 59), leads));
    }

    /// <summary>하루 24시간을 전부 훑어 슬롯 집합이 <see cref="KairaAlarm.SpawnHoursKst"/> 와 정확히 같음을
    /// 확인한다. 00:00~00:59 는 앵커(01:00)보다 <b>이전</b>이라 나머지 연산이 음수로 가는 유일한 구간이다 —
    /// C# 의 % 는 음수 피연산자에 음수를 그대로 내므로, 감싸지 않으면 이 한 시간만 조용히 틀린다.</summary>
    [Fact]
    public void Every_hour_of_the_day_matches_the_declared_slot_list()
    {
        var leads = new[] { 10 };

        for (int hour = 0; hour < 24; hour++)
        {
            // 그 시각 정각의 10분 전 = (hour-1):50. 0시의 10분 전은 전날 23:50 이다.
            long tenBefore = KstMs(hour == 0 ? 23 : hour - 1, 50, day: hour == 0 ? 1 : 2);
            int? due = KairaAlarm.DueLead(tenBefore, leads);
            if (KairaAlarm.SpawnHoursKst.Contains(hour))
            {
                Assert.Equal(10, due);
            }
            else
            {
                Assert.Null(due);
            }
        }
    }

    /// <summary>앵커가 0시가 아니게 되면서 자정 횡단이 <b>정상 경로</b>가 됐다: 21시 슬롯 다음은 익일 1시다.
    /// NextSpawnMs 는 날짜를 만지지 않고 "이번 분 + 남은 분"으로 더하므로 여기서 끊기기 쉽다
    /// (1440 % 240 == 0 이라 실제로는 끊기지 않는다는 것을 못박는다).</summary>
    [Fact]
    public void The_first_slot_of_the_day_rolls_over_from_the_previous_evening()
    {
        var leads = new[] { 10, 5, 1 };

        long firstSlot = new DateTimeOffset(2026, 9, 2, 1, 0, 0, Kst).ToUnixTimeMilliseconds();
        Assert.Equal(firstSlot, KairaAlarm.NextSpawnMs(KstMs(21, 1, day: 1)));    // 전날 21시 슬롯 직후
        Assert.Equal(firstSlot, KairaAlarm.NextSpawnMs(KstMs(23, 59, day: 1)));   // 자정 직전
        Assert.Equal(firstSlot, KairaAlarm.NextSpawnMs(KstMs(0, 0)));             // 자정 직후
        Assert.Equal(firstSlot, KairaAlarm.NextSpawnMs(KstMs(0, 50)));            // 앵커 이전(나머지가 음수인 구간)

        // 리드도 자정을 넘겨 이어진다 — 00:50 은 같은 날 01:00 의 10분 전이다.
        Assert.Equal(10, KairaAlarm.DueLead(KstMs(0, 50), leads));
        Assert.Equal(1, KairaAlarm.DueLead(KstMs(0, 59), leads));
    }

    /// <summary>격자는 머신 시간대가 아니라 서버(KST)에 걸려 있다. 로컬 시로 재면 UTC+8 사용자는 여섯
    /// 슬롯이 전부 한 시간 어긋난다 — timeBasis 결정 전체를 지키는 그물이다.</summary>
    [Fact]
    public void The_grid_is_anchored_to_kst_not_to_the_machine_timezone()
    {
        var leads = new[] { 10 };

        // 같은 순간을 UTC+8 벽시계로 쓰면 20:50 이 아니라 19:50 이다. 그래도 KST 20:50 이므로 떠야 한다.
        long sameInstantFromPlus8 =
            new DateTimeOffset(2026, 9, 2, 19, 50, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
        Assert.Equal(KstMs(20, 50), sameInstantFromPlus8);
        Assert.Equal(10, KairaAlarm.DueLead(sameInstantFromPlus8, leads));

        // 반대로 UTC+8 사용자의 로컬 20:50 은 KST 21:50 이라 슬롯이 아니다.
        long localEveningInPlus8 =
            new DateTimeOffset(2026, 9, 2, 20, 50, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
        Assert.Null(KairaAlarm.DueLead(localEveningInPlus8, leads));
    }

    /// <summary>하루치를 분 단위로 훑어 두 스케줄이 실제로 갈렸음을 고정한다. 슈고는 매시 정각 그대로
    /// (24슬롯 × 3리드 = 72회), 카이라는 4시간 격자(6슬롯 × 3리드 = 18회).
    /// <para>⚠️ <b>개수만 세면 앵커를 못 잡는다.</b> 1440 = 6 × 240 이라 앵커가 0시든 1시든 하루 큐는
    /// 똑같이 18회다 — 2026-09-02 의 틀린 앵커를 그린으로 통과시킨 게 정확히 이 착시였다. 그래서 개수가
    /// 아니라 <b>큐가 뜬 분의 집합</b>을 고정한다. 기댓값은 상수에서 파생시키지 않고 리터럴로 적는다
    /// (파생시키면 앵커가 틀릴 때 기댓값도 같이 틀려 계속 그린이다).</para></summary>
    [Fact]
    public void A_full_day_gives_kaira_eighteen_cues_at_exactly_these_minutes()
    {
        var leads = new[] { 10, 5, 1 };
        var kairaAt = new List<int>();   // KST 자정으로부터 몇 분째에 큐가 떴나
        int shugo = 0;

        for (int minute = 0; minute < 24 * 60; minute++)
        {
            long ms = KstMs(0, 0) + (minute * 60_000L);
            if (KairaAlarm.DueLead(ms, leads) is not null)
            {
                kairaAt.Add(minute);
            }

            // 슈고는 사용자 벽시계 기준이므로 같은 분을 KST 벽시계로 그대로 넘긴다.
            DateTime wall = new DateTimeOffset(2026, 9, 2, 0, 0, 0, Kst).AddMinutes(minute).DateTime;
            if (ShugoAlarm.DueLead(wall, leads) is not null)
            {
                shugo++;
            }
        }

        Assert.Equal(
            new[]
            {
                50, 55, 59,           // 00:50/00:55/00:59 → 01시 슬롯
                290, 295, 299,        // 04:50…           → 05시
                530, 535, 539,        // 08:50…           → 09시
                770, 775, 779,        // 12:50…           → 13시
                1010, 1015, 1019,     // 16:50…           → 17시
                1250, 1255, 1259,     // 20:50…           → 21시
            },
            kairaAt);
        Assert.Equal(72, shugo);
    }

    /// <summary>토스트가 찍는 "· HH:mm" 의 근거. DueLead 와 같은 격자를 공유해야 한다.</summary>
    [Fact]
    public void The_next_spawn_is_the_slot_the_lead_is_counting_down_to()
    {
        long spawn21 = new DateTimeOffset(2026, 9, 2, 21, 0, 0, Kst).ToUnixTimeMilliseconds();
        Assert.Equal(spawn21, KairaAlarm.NextSpawnMs(KstMs(20, 50)));
        Assert.Equal(spawn21, KairaAlarm.NextSpawnMs(KstMs(20, 59)));
        Assert.Equal(spawn21, KairaAlarm.NextSpawnMs(KstMs(17, 1)));   // 17시 슬롯 직후 → 다음은 21시
        Assert.Equal(spawn21, KairaAlarm.NextSpawnMs(KstMs(21, 0)));   // 정각 자신
    }

    /// <summary>격자 수학이 말없이 기대는 두 전제. 누가 주기를 5시간으로 바꾸면 <c>1440 % 300 != 0</c> 이라
    /// 자정에서 격자가 점프하고(23:59 는 "익일 02:00", 2분 뒤인 00:01 은 "01:00" 이라 답한다),
    /// <see cref="KairaAlarm.SpawnHoursText"/> 는 모듈러 결과와 다른 목록을 인쇄한다. 조용히 깨지는 자리라
    /// 전제를 그물로 세워 둔다.</summary>
    [Fact]
    public void The_grid_math_assumes_the_interval_divides_both_the_day_and_the_hour_count()
    {
        Assert.Equal(0, 24 * 60 % (KairaAlarm.SpawnIntervalHours * 60));
        Assert.Equal(24 / KairaAlarm.SpawnIntervalHours, KairaAlarm.SpawnHoursKst.Count);
    }

    /// <summary>화면 문구가 상수에서 파생된다는 이번 설계의 유일한 실효 그물. App.Wpf 엔 테스트 프로젝트가
    /// 없어 바인딩 자체는 못 돌려 보므로, 대신 <c>SettingsWindow.xaml</c> 소스를 읽어 <b>출현 시각을 손으로
    /// 적은 리터럴이 다시 들어오지 않았는지</b>를 본다. 2026-09-22 이전엔 거기 "0·4·8·12·16·20시"가 박혀
    /// 있어서, 로직만 고치면 화면은 계속 옛 시각을 주장하는 상태였다.</summary>
    [Fact]
    public void The_settings_screen_does_not_hardcode_the_spawn_hours()
    {
        string xaml = RepoFile("dotnet", "src", "WaffleMeter.App.Wpf", "SettingsWindow.xaml");

        Assert.Contains("{Binding KairaScheduleDesc}", xaml, StringComparison.Ordinal);
        foreach (string literal in new[] { "0·4·8", "1·5·9", "00·04·08", "01·05·09" })
        {
            Assert.DoesNotContain(literal, xaml, StringComparison.Ordinal);
        }
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"{parts[^1]} 를 찾지 못했습니다 — 테스트가 저장소 밖에서 실행됐습니다.");
    }

    [Fact]
    public void Weekly_schedule_returns_the_next_matching_day_and_time()
    {
        // 2026-07-27 is a Monday → the next 수·토 22:35 is Wednesday the 29th. This is the value the real
        // 2026-07-27 어비스 capture carried for the 수·토 group, so the schedule reproduces the wire.
        long monday = new DateTimeOffset(2026, 7, 27, 9, 0, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
        Assert.True(FieldBossFixedSchedule.TryNextSpawn(2600521, monday, out long next));
        Assert.Equal(new DateTimeOffset(2026, 7, 29, 22, 35, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds(), next);

        // …and the 금·일 group's next spawn from that same Monday was Friday the 31st.
        Assert.True(FieldBossFixedSchedule.TryNextSpawn(2600520, monday, out long friday));
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 22, 5, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds(), friday);

        // Same day but past the time → rolls to the pair's other day.
        long wedLate = new DateTimeOffset(2026, 7, 29, 23, 0, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
        Assert.True(FieldBossFixedSchedule.TryNextSpawn(2600521, wedLate, out long after));
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 22, 35, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds(), after);
    }
}
