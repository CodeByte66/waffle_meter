using System.Globalization;
using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>Spec for <see cref="FieldBossTimerParser"/> against the 0x9101 timer broadcast layout
/// <c>[u16 0][u32-LE mapId][u8 count][u8 0]</c> then <c>[var-int code][.. filler ..][int64-LE targetMs]</c>,
/// verified against real captures.</summary>
public class FieldBossTimerParserTests
{
    // A REAL 모르헤임 0x9101 body (map 1111, 12 entries), captured 2026-07-04. Every boss is dead, so every
    // record is the short form: code var-int, one filler byte, int64-LE target.
    private const string RealMorheimBody =
        "00 00 57 04 00 00 0C 00 92 ED 92 01 00 0E 11 B1 2B 9F 01 00 00 00 F1 ED 92 01 BD 8A A3 2B 9F 01 00 " +
        "00 00 CE ED 92 01 2A ED DC 2B 9F 01 00 00 00 93 ED 92 01 83 CE B1 2B 9F 01 00 00 00 B7 ED 92 01 0F " +
        "6E A7 2B 9F 01 00 00 00 CD ED 92 01 26 4B A8 2B 9F 01 00 00 00 F3 ED 92 01 38 36 A4 2B 9F 01 00 00 " +
        "00 F4 ED 92 01 CC CD 7E 2C 9F 01 00 00 00 A5 EE 92 01 00 56 AE A0 2B 9F 01 00 00 00 A6 EE 92 01 00 " +
        "CE A1 2B 9F 01 00 00 00 CE F4 92 01 64 1E 7E 2C 9F 01 00 00 00 CF F4 92 01 C4 C9 7C 2C 9F 01 00 00 " +
        "00 00 00";

    // The same table at another tick, when 포식의 거수 발라크(2406035) was UP: its record carries a 12-byte
    // position block between the code and the timestamp. The old 0..2-byte gap tolerance lost a row here.
    private const string RealMorheimBodyWithLiveBoss =
        "00 00 57 04 00 00 0C 00 92 ED 92 01 02 0E 11 B1 2B 9F 01 00 00 01 F1 ED 92 01 6C F7 19 C6 7B 89 B7 " +
        "C7 00 80 BF 45 BD 8A A3 2B 9F 01 00 00 00 CE ED 92 01 2A ED DC 2B 9F 01 00 00 00 93 ED 92 01 83 CE " +
        "B1 2B 9F 01 00 00 00 B7 ED 92 01 0F 6E A7 2B 9F 01 00 00 00 CD ED 92 01 26 4B A8 2B 9F 01 00 00 00 " +
        "F3 ED 92 01 38 36 A4 2B 9F 01 00 00 00 F4 ED 92 01 CC CD 7E 2C 9F 01 00 00 00 A5 EE 92 01 00 46 4E " +
        "58 2D 9F 01 00 00 00 A6 EE 92 01 FA 6D 59 2D 9F 01 00 00 00 CE F4 92 01 64 1E 7E 2C 9F 01 00 00 00 " +
        "CF F4 92 01 C4 C9 7C 2C 9F 01 00 00 00 00 00";

    // A REAL 베르테론 body (map 1010, 24 entries), captured 2026-07-27. This region rides per-map SLOT codes
    // (101001..101024, three-byte var-ints) rather than mob codes, and the table is followed by a trailer of
    // longer records carrying float positions — the shape the whole 24-boss half of the catalog depends on.
    private const string RealVerteronBody =
        "00 00 F2 03 00 00 18 00 8C 95 06 00 85 F7 8D A3 9F 01 00 00 00 8A 95 06 A1 01 54 A3 9F 01 00 00 " +
        "00 89 95 06 57 D1 4B A3 9F 01 00 00 00 8B 95 06 72 0F 53 A3 9F 01 00 00 00 8F 95 06 13 8B 5A A3 " +
        "9F 01 00 00 00 9D 95 06 DC 29 E3 A3 9F 01 00 00 00 8D 95 06 55 A0 88 A3 9F 01 00 00 00 90 95 06 " +
        "86 4C 8B A3 9F 01 00 00 00 93 95 06 00 FE 76 F4 A3 9F 01 00 00 00 8E 95 06 CC 12 7B A3 9F 01 00 " +
        "00 00 91 95 06 18 E8 86 A3 9F 01 00 00 00 92 95 06 A0 1B 8A A3 9F 01 00 00 00 94 95 06 14 62 ED " +
        "A3 9F 01 00 00 00 95 95 06 80 EF F8 A3 9F 01 00 00 00 96 95 06 97 25 EE A3 9F 01 00 00 00 97 95 " +
        "06 F4 E1 88 A3 9F 01 00 00 00 98 95 06 00 A8 18 F8 A3 9F 01 00 00 00 99 95 06 70 7E 86 A3 9F 01 " +
        "00 00 00 9A 95 06 DD 44 F2 A3 9F 01 00 00 00 9B 95 06 04 0E 9D A3 9F 01 00 00 00 9C 95 06 71 DF " +
        "F0 A3 9F 01 00 00 00 9E 95 06 2C 72 F2 A3 9F 01 00 00 00 9F 95 06 49 90 EF A3 9F 01 00 00 00 A0 " +
        "95 06 61 5A E8 A3 9F 01 00 00 00 03 A3 B3 0B E7 C8 10 00 4B 4B 4C 00 00 48 FD 45 00 FC 43 C6 3D " +
        "03 A7 46 20 A6 4B 3B A3 9F 01 00 00 A7 B3 0B EB C8 10 00 4F 4B 4C 00 19 A9 4C 47 A3 F8 FB 47 EF " +
        "EA AC 46 02 D9 4B 3B A3 9F 01 00 00 A4 B3 0B E9 C8 10 00 4D 4B 4C 00 9A 97 73 C6 DE 38 AE 47 FA " +
        "7D 83 46 01 A6 4B 3B A3 9F 01 00 00 00";

    /// <summary>~14:30 KST on the capture day — just before the earliest target in the table.</summary>
    private const long MorheimArrivedAt = 1_783_143_000_000L;

    /// <summary>2026-07-27 20:05 KST — when the 베르테론 table was captured.</summary>
    private const long VerteronArrivedAt = 1_785_150_000_000L;

    // REAL 어비스 하층 (map 20, 8 entries) and 중층 (map 22, 5 entries) bodies, captured 2026-07-27 20:52.
    // Two-byte slot codes (2001.., 2201..). Note the 감시자 카이라 record (D2 0F): its timestamp is all
    // zeroes — the server sends no time for that one boss, which is what the fixed schedule stands in for.
    private const string RealAbyssLowerBody =
        "00 00 14 00 00 00 08 00 D1 0F 00 9D 55 E4 A3 9F 01 00 00 00 D2 0F 00 00 00 00 00 00 00 00 00 D8 " +
        "0F A0 EB 15 AE 9F 01 00 00 00 D3 0F 60 2C 47 B8 9F 01 00 00 00 D4 0F 60 2C 47 B8 9F 01 00 00 00 " +
        "D5 0F 60 2C 47 B8 9F 01 00 00 00 D6 0F A0 EB 15 AE 9F 01 00 00 00 D7 0F A0 EB 15 AE 9F 01 00 00 " +
        "00 00 00";

    private const string RealAbyssMiddleBody =
        "00 00 16 00 00 00 05 00 99 11 00 60 2C 47 B8 9F 01 00 00 00 9D 11 A0 EB 15 AE 9F 01 00 00 00 9A " +
        "11 60 2C 47 B8 9F 01 00 00 00 9B 11 A0 EB 15 AE 9F 01 00 00 00 9C 11 A0 EB 15 AE 9F 01 00 00 00 " +
        "00 00";

    /// <summary>2026-07-27 20:50 KST — when the 어비스 tables were captured.</summary>
    private const long AbyssArrivedAt = 1_785_153_000_000L;

    private static byte[] Hex(string s)
    {
        string[] parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            bytes[i] = byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    private static void WriteVarInt(List<byte> to, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            to.Add((byte)(v | 0x80));
            v >>= 7;
        }

        to.Add((byte)v);
    }

    /// <summary>Builds a table body: header + one short-form record per (wireCode, target).</summary>
    private static byte[] Body(int mapId, params (int Wire, long Target)[] rows)
    {
        var b = new List<byte> { 0, 0 };
        b.AddRange(BitConverter.GetBytes(mapId));
        b.Add((byte)rows.Length);
        b.Add(0);
        foreach ((int wire, long target) in rows)
        {
            WriteVarInt(b, wire);
            b.Add(0);
            b.AddRange(BitConverter.GetBytes(target));
        }

        return b.ToArray();
    }

    [Fact]
    public void Parses_a_real_morheim_table_whole()
    {
        FieldBossTimerParser.Result r = FieldBossTimerParser.ParseTable(Hex(RealMorheimBody), 0, MorheimArrivedAt);

        Assert.Equal(1111, r.MapId);
        Assert.Equal(12, r.Timers.Count);
        Assert.All(r.Timers, t => Assert.Equal(FieldBossRegion.Morheim, FieldBossCatalog.Region(t.Code)));
        Assert.Contains(r.Timers, t => t.Code == 2406034 && t.TargetMs == 0x0000019F2BB1110EL);
        Assert.Contains(r.Timers, t => t.Code == 2406991);
    }

    [Fact]
    public void Recovers_the_record_of_a_boss_that_is_currently_up()
    {
        // The live boss's record has a 12-byte position between code and timestamp; all 12 rows must survive.
        FieldBossTimerParser.Result r =
            FieldBossTimerParser.ParseTable(Hex(RealMorheimBodyWithLiveBoss), 0, MorheimArrivedAt);

        Assert.Equal(12, r.Timers.Count);
        Assert.Contains(r.Timers, t => t.Code == 2406035);  // 발라크 — the live one
        Assert.Contains(r.Timers, t => t.Code == 2406129);  // 피오스 — dropped by the old 0..2 gap scan
    }

    [Fact]
    public void Parses_a_real_verteron_table_whole()
    {
        FieldBossTimerParser.Result r =
            FieldBossTimerParser.ParseTable(Hex(RealVerteronBody), 0, VerteronArrivedAt);

        Assert.Equal(1010, r.MapId);
        Assert.Equal(24, r.Timers.Count);                 // == the header's declared count
        Assert.All(r.Timers, t => Assert.Equal(FieldBossRegion.Verteron, FieldBossCatalog.Region(t.Code)));
        // slot code 101021 -> 영원의 가르투아, the sub-region that had to be unlocked before it showed up
        Assert.Contains(r.Timers, t => t.Code == 2101074);
        Assert.Contains(r.Timers, t => t.Code == 2100003);  // first slot (101001) 동쪽의 네이켈
        Assert.Contains(r.Timers, t => t.Code == 2101131);  // last slot (101024) 군단장 라그타
        Assert.All(r.Timers, t => Assert.InRange(t.TargetMs, VerteronArrivedAt, VerteronArrivedAt + 86_400_000L));
    }

    [Fact]
    public void Parses_both_real_abyss_tables_whole()
    {
        FieldBossTimerParser.Result low =
            FieldBossTimerParser.ParseTable(Hex(RealAbyssLowerBody), 0, AbyssArrivedAt);
        Assert.Equal(FieldBossCatalog.AbyssLowerMapId, low.MapId);
        Assert.Equal(7, low.Timers.Count);   // 8 declared; 감시자 카이라's record carries no time
        Assert.All(low.Timers, t => Assert.Equal(FieldBossRegion.Abyss, FieldBossCatalog.Region(t.Code)));

        FieldBossTimerParser.Result mid =
            FieldBossTimerParser.ParseTable(Hex(RealAbyssMiddleBody), 0, AbyssArrivedAt);
        Assert.Equal(FieldBossCatalog.AbyssMiddleMapId, mid.MapId);
        Assert.Equal(5, mid.Timers.Count);
        Assert.All(mid.Timers, t => Assert.Equal(FieldBossRegion.Abyss, FieldBossCatalog.Region(t.Code)));

        // The siege groups are what the capture actually carried, slot by slot: 2201/2202 (분노한 수호신장 나흐마
        // ×2) on the 금 window, 2203/2204/2205 (처형관 드라모스 / 반역자 듀칼 / 파멸자 마라카) on the 수 one — the
        // client's WorldMapFieldNamed + PeriodSpawn say the same. Until 2026-10-07 the catalog put 드라모스 on 2202
        // and a 나흐마(2600156) on 2204, so this very fixture "showed" 드라모스 on Friday.
        long friday = new DateTimeOffset(2026, 7, 31, 22, 5, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
        long wednesday = new DateTimeOffset(2026, 7, 29, 22, 35, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
        Assert.Contains(mid.Timers, t => t.Code == 2600479 && t.TargetMs == friday);
        Assert.Contains(mid.Timers, t => t.Code == 2600480 && t.TargetMs == friday);
        Assert.Contains(mid.Timers, t => t.Code == 2600520 && t.TargetMs == wednesday);
        Assert.Contains(mid.Timers, t => t.Code == 2600521 && t.TargetMs == wednesday);
        Assert.Contains(mid.Timers, t => t.Code == 2600522 && t.TargetMs == wednesday);
    }

    /// <summary>The 중층 slot → NPC pairing is the client's (<c>WorldMapFieldNamed</c> ID = the wire slot, its NPC
    /// resolved through <c>NpcData</c>, datamine 2026-09-28). Pinned literally: from 2026-07-27 to 10-07 the table
    /// resolved 2202/2203/2204 to the wrong bosses and every test stayed green, because each one only re-read the
    /// table.</summary>
    [Theory]
    [InlineData(2201, 2600479)] // M_AR3_AbBoss_01_002 분노한 수호신장 나흐마
    [InlineData(2202, 2600480)] // M_AR3_AbBoss_01_003 분노한 수호신장 나흐마
    [InlineData(2203, 2600520)] // M_AR3_Kashapa_01_CV02_001 처형관 드라모스
    [InlineData(2204, 2600521)] // M_AR3_Evspirit_02_CV02_001 반역자 듀칼
    [InlineData(2205, 2600522)] // M_AR3_Nahid_01_CV02_001 파멸자 마라카
    public void Middle_floor_slots_resolve_to_the_clients_npc(int slot, int expected)
    {
        Assert.True(FieldBossCatalog.TryResolveWireCode(slot, FieldBossCatalog.AbyssMiddleMapId, out int code));
        Assert.Equal(expected, code);
    }

    /// <summary>Every 어비스 record in the real 07-27 capture lands on the weekday its schedule group says — the
    /// 금·일 group on the Friday, the 아티쟁 group on the Wednesday. This is what ties the catalog's slot pairing
    /// and <see cref="FieldBossFixedSchedule"/>'s grouping to the wire: had either drifted (a 금·일 boss filed under
    /// the war group or the reverse), a record here would carry the other weekday.</summary>
    [Fact]
    public void The_schedule_group_of_every_real_abyss_record_matches_its_weekday()
    {
        var kst = TimeSpan.FromHours(9);
        List<(int Code, long TargetMs)> timers =
            FieldBossTimerParser.ParseTable(Hex(RealAbyssLowerBody), 0, AbyssArrivedAt).Timers
                .Concat(FieldBossTimerParser.ParseTable(Hex(RealAbyssMiddleBody), 0, AbyssArrivedAt).Timers)
                .Where(t => FieldBossFixedSchedule.HasFixedSchedule(t.Code))
                .Where(t => !FieldBossFixedSchedule.IsDaily(t.Code)) // 정령왕 아그로: per-kill in this pre-10-28 capture
                .ToList();

        Assert.Equal(11, timers.Count); // 13 records − 정령왕 아그로(per-kill then) − 감시자 카이라(zeroed)
        Assert.All(timers, t => Assert.Equal(
            FieldBossFixedSchedule.IsArtifactWarTied(t.Code) ? DayOfWeek.Wednesday : DayOfWeek.Friday,
            DateTimeOffset.FromUnixTimeMilliseconds(t.TargetMs).ToOffset(kst).DayOfWeek));
    }

    [Fact]
    public void The_zeroed_kaira_record_produces_no_timer()
    {
        // 감시자 카이라 is the one boss the server never times, so there is nothing to remind against here —
        // it is driven by its own 4-hour-grid alarm instead, and must not appear as a respawn timer.
        FieldBossTimerParser.Result low =
            FieldBossTimerParser.ParseTable(Hex(RealAbyssLowerBody), 0, AbyssArrivedAt);

        Assert.DoesNotContain(low.Timers, t => t.Code == FieldBossCatalog.ScheduledSpawnCode);
        Assert.False(FieldBossFixedSchedule.HasFixedSchedule(FieldBossCatalog.ScheduledSpawnCode));
    }

    [Fact]
    public void Maps_a_verteron_slot_code_back_to_the_mob_code()
    {
        long target = MorheimArrivedAt + 20 * 60_000L;
        byte[] body = Body(1010, (101001, target), (101002, target + 60_000));

        FieldBossTimerParser.Result r = FieldBossTimerParser.ParseTable(body, 0, MorheimArrivedAt);

        Assert.Equal(1010, r.MapId);
        Assert.Equal(2, r.Timers.Count);
        Assert.Contains(r.Timers, t => t.Code == 2100003 && t.TargetMs == target);   // 동쪽의 네이켈
        Assert.Contains(r.Timers, t => t.Code == 2100040);                           // 썩은 쿠타르
    }

    [Fact]
    public void Rejects_a_code_that_belongs_to_another_region()
    {
        // A 베르테론 slot code inside a 모르헤임 table is noise, not a boss.
        byte[] body = Body(1111, (101001, MorheimArrivedAt + 20 * 60_000L));

        Assert.Empty(FieldBossTimerParser.ParseTable(body, 0, MorheimArrivedAt).Timers);
    }

    [Fact]
    public void Accepts_a_weekly_abyss_target_beyond_the_one_day_horizon()
    {
        long sixDaysOut = MorheimArrivedAt + 6 * 24 * 60 * 60 * 1000L;
        byte[] body = Body(FieldBossCatalog.AbyssMiddleMapId, (2202, sixDaysOut));

        FieldBossTimerParser.Result r = FieldBossTimerParser.ParseTable(body, 0, MorheimArrivedAt);

        Assert.Contains(r.Timers, t => t.Code == 2600480 && t.TargetMs == sixDaysOut); // 분노한 수호신장 나흐마
    }

    [Fact]
    public void Falls_back_to_the_fixed_schedule_when_a_record_has_no_usable_time()
    {
        // 수호신장 나흐마(wire 2003) listed with a zeroed time → the siege schedule fills it in.
        byte[] body = Body(FieldBossCatalog.AbyssLowerMapId, (2001, MorheimArrivedAt + 20 * 60_000L), (2003, 0));

        FieldBossTimerParser.Result r = FieldBossTimerParser.ParseTable(body, 0, MorheimArrivedAt);

        (int Code, long TargetMs) nahma = Assert.Single(r.Timers, t => t.Code == 2600084);
        Assert.True(FieldBossFixedSchedule.TryNextSpawn(2600084, MorheimArrivedAt, out long expected));
        Assert.Equal(expected, nahma.TargetMs);
    }

    /// <summary>정령왕 아그로(wire 2001) became a daily 09·21시 spawn on 2026-10-28, so a record with no usable time
    /// (the boss is up: position block + a past time) gets the next slot — and a server-sent time still wins.</summary>
    [Fact]
    public void The_daily_agro_falls_back_to_its_next_slot_and_a_server_time_still_wins()
    {
        var kst = TimeSpan.FromHours(9);
        long arrived = new DateTimeOffset(2026, 10, 28, 21, 7, 0, kst).ToUnixTimeMilliseconds();
        long nextMorning = new DateTimeOffset(2026, 10, 29, 9, 0, 0, kst).ToUnixTimeMilliseconds();

        (int Code, long TargetMs) fallback = Assert.Single(
            FieldBossTimerParser.ParseTable(Body(FieldBossCatalog.AbyssLowerMapId, (2001, 0)), 0, arrived).Timers,
            t => t.Code == 2600068);
        Assert.Equal(nextMorning, fallback.TargetMs);

        long sent = arrived + 3 * 60 * 60_000L; // not a slot — the wire is what counts
        (int Code, long TargetMs) server = Assert.Single(
            FieldBossTimerParser.ParseTable(Body(FieldBossCatalog.AbyssLowerMapId, (2001, sent)), 0, arrived).Timers,
            t => t.Code == 2600068);
        Assert.Equal(sent, server.TargetMs);
    }

    /// <summary>The 수·토 아티쟁 bosses (하층 slots 2006-2008, 중층 2203-2205) get NO fallback any more: their spawn
    /// follows the server's own war start, which the parser cannot see, and the fixed 22:35 it used to fill in
    /// was wrong for two war groups out of three — and invented a same-day ghost alarm for a boss already up.
    /// The 금·일 나흐마 beside them still falls back.</summary>
    [Fact]
    public void An_artifact_war_boss_with_no_usable_time_gets_no_invented_timer()
    {
        byte[] lower = Body(FieldBossCatalog.AbyssLowerMapId, (2006, 0), (2007, 0), (2008, 0), (2003, 0));
        byte[] middle = Body(FieldBossCatalog.AbyssMiddleMapId, (2203, 0), (2204, 0), (2205, 0));

        FieldBossTimerParser.Result low = FieldBossTimerParser.ParseTable(lower, 0, MorheimArrivedAt);
        FieldBossTimerParser.Result mid = FieldBossTimerParser.ParseTable(middle, 0, MorheimArrivedAt);

        (int Code, long TargetMs) nahma = Assert.Single(low.Timers);
        Assert.Equal(2600084, nahma.Code);
        Assert.Empty(mid.Timers);
    }

    /// <summary>…while a time the server DID send for one of them is taken exactly as sent — the derived time in
    /// App.Core only ever fills a gap the server left.</summary>
    [Fact]
    public void An_artifact_war_boss_with_a_server_time_keeps_it()
    {
        long target = MorheimArrivedAt + 2 * 60 * 60_000L;
        byte[] body = Body(FieldBossCatalog.AbyssLowerMapId, (2006, target));

        FieldBossTimerParser.Result r = FieldBossTimerParser.ParseTable(body, 0, MorheimArrivedAt);

        Assert.Contains(r.Timers, t => t.Code == 2600096 && t.TargetMs == target); // 집행자 타마사
    }

    [Fact]
    public void Rejects_a_target_time_outside_the_sane_window()
    {
        byte[] body = Body(1111, (2406034, MorheimArrivedAt - 60 * 60_000L)); // an hour in the past
        Assert.Empty(FieldBossTimerParser.ParseTable(body, 0, MorheimArrivedAt).Timers);
    }

    [Fact]
    public void Ignores_a_body_that_is_not_a_boss_table()
    {
        // The same opcode also carries a short 29-byte message with no table; it must yield nothing.
        byte[] body = Hex("00 00 E5 4E 09 00 00 01 8E 9C 02 9D 19 23 00 00 0C 94 C6 00 2C 21 46 00 00 BF 45 00 00");
        Assert.Empty(FieldBossTimerParser.ParseTable(body, 0, MorheimArrivedAt).Timers);
    }
}
