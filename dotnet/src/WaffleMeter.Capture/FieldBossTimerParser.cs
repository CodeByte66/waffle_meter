namespace WaffleMeter.Capture;

/// <summary>
/// Extracts field-boss respawn timers from the 0x9101 status broadcast.
/// <para><b>Body layout</b> (verified on real captures): <c>[u16 0][u32-LE mapId][u8 entryCount][u8 0]</c>
/// then <paramref>entryCount</paramref> records of <c>[var-int code][.. 0-2 filler ..][int64-LE targetMs]</c>.
/// A record for a boss that is currently ALIVE carries a 12-byte position block between the code and the
/// timestamp, so the scan is resynchronising rather than fixed-stride.</para>
/// <para><b>The table is map-scoped</b> — the server only sends the bosses of the map the character is in,
/// which is what lets the alarm work without any separate map detection. ⚠️ 그래서 알림의 범위는
/// <b>"현재 맵"이 아니라 "미터를 켜고 방문했던 맵"</b>이다: 한 번 받은 시각표는 앱을 끌 때까지 남으므로 다른
/// 지역·던전에 있어도 그 보스 알림이 울린다. 파서가 맵 단위로 받는 것과 알림이 맵 단위로 우는 것은 다른
/// 이야기이고, 종전 문구("현재 맵 기준")는 그 둘을 뭉개고 있었다. The header's map id
/// picks the region, and only that region's codes are accepted; that is what lets us take codes as small as
/// 2001 (어비스) without matching noise. Codes are NOT uniformly mob codes: 베르테론/알트가르드/어비스 carry a
/// per-map slot code (맵id×100+순번) which <see cref="FieldBossCatalog"/> maps back to the mob code, while
/// 엘테넨/모르헤임 carry the mob code itself.</para>
/// <para>Pure and gated behind the opcode by the caller.</para>
/// </summary>
public static class FieldBossTimerParser
{
    private const long TwoMinutesMs = 2 * 60 * 1000L;

    /// <summary>어비스 요새 보스는 주간 일정(금·일 / 수·토)이라 다음 스폰이 최대 일주일 뒤다 — 하루로 자르면
    /// 그 줄이 통째로 사라진다.</summary>
    private const long EightDaysMs = 8 * 24 * 60 * 60 * 1000L;

    /// <summary>Fallback code window for a broadcast whose map id we do not know (a new region, or a shifted
    /// header). Wide enough for both slot codes and mob codes, narrow enough to stay out of small-int noise.</summary>
    private const int LooseMinCode = 100_000;
    private const int LooseMaxCode = 9_999_999;

    /// <summary>One parsed table: which map it described and the boss timers in it.</summary>
    public readonly record struct Result(int MapId, IReadOnlyList<(int Code, long TargetMs)> Timers);

    /// <summary>Scan <paramref name="packet"/> from <paramref name="bodyStart"/> for boss-code → target-time
    /// records. Deduplicated by boss code (first write wins, matching the packet's own ordering).
    /// <paramref name="arrivedAtMs"/> bounds the accepted timestamps (now-2m .. now+8d).</summary>
    public static IReadOnlyList<(int Code, long TargetMs)> Parse(byte[] packet, int bodyStart, long arrivedAtMs)
        => ParseTable(packet, bodyStart, arrivedAtMs).Timers;

    /// <summary>As <see cref="Parse"/> but also reports the map id the table belongs to (0 when the header
    /// did not look like a boss table).</summary>
    public static Result ParseTable(byte[] packet, int bodyStart, long arrivedAtMs)
    {
        int start = Math.Max(0, bodyStart);
        var found = new Dictionary<int, long>();
        int mapId = ReadMapId(packet, start);

        // Known map → accept that map's codes only, and let the record walk resync past a position block.
        if (mapId != 0 && FieldBossCatalog.IsKnownMap(mapId))
        {
            // +7 is the first record's leading flag byte; start there rather than at the first code so a
            // table whose first record has no flag is not skipped (the walk resyncs byte by byte anyway).
            ScanScoped(packet, start + 7, arrivedAtMs, mapId, found);
        }
        else
        {
            ScanLoose(packet, start, arrivedAtMs, found);
        }

        return new Result(mapId, found.Select(kv => (kv.Key, kv.Value)).ToList());
    }

    /// <summary>The u32 map id at body+2, or 0 when the body is too short / does not match the header shape.</summary>
    private static int ReadMapId(byte[] packet, int bodyStart)
    {
        if (bodyStart + 8 > packet.Length || packet[bodyStart] != 0 || packet[bodyStart + 1] != 0)
        {
            return 0;
        }

        return PacketPrimitives.ParseUInt32Le(packet, bodyStart + 2);
    }

    /// <summary>Walk the records of a table whose map (and therefore region) we know.</summary>
    private static void ScanScoped(byte[] packet, int from, long arrivedAtMs, int mapId, Dictionary<int, long> found)
    {
        int i = from;
        while (i < packet.Length)
        {
            VarIntOutput v = PacketPrimitives.ReadVarInt(packet, i);
            if (v.Length <= 0)
            {
                i++;
                continue;
            }

            if (!FieldBossCatalog.TryResolveWireCode(v.Value, mapId, out int bossCode))
            {
                i++;
                continue;
            }

            int at = i + v.Length;
            if (TryReadTarget(packet, at, arrivedAtMs, out long target, out int consumed))
            {
                found.TryAdd(bossCode, target);
                i = at + consumed;
                continue;
            }

            // No timestamp on this record: a fixed-schedule boss (어비스 요새) still gets its next spawn.
            if (FieldBossFixedSchedule.TryNextSpawn(bossCode, arrivedAtMs, out long scheduled))
            {
                found.TryAdd(bossCode, scheduled);
            }

            i = at;
        }
    }

    /// <summary>Fallback for an unrecognised map id: the original heuristic, over a wide code window.</summary>
    private static void ScanLoose(byte[] packet, int from, long arrivedAtMs, Dictionary<int, long> found)
    {
        int i = from;
        while (i < packet.Length)
        {
            VarIntOutput v = PacketPrimitives.ReadVarInt(packet, i);
            if (v.Length <= 0)
            {
                i++;
                continue;
            }

            if (v.Value is >= LooseMinCode and <= LooseMaxCode
                && FieldBossCatalog.TryResolveWireCode(v.Value, out int bossCode)
                && TryReadTarget(packet, i + v.Length, arrivedAtMs, out long target, out int consumed))
            {
                found.TryAdd(bossCode, target);
                i += v.Length + consumed;
                continue;
            }

            i++;
        }
    }

    /// <summary>Read the int64-LE target time that follows a code, allowing the small filler byte(s) and the
    /// 12-byte position block an alive boss carries. Reports how many bytes the record consumed.</summary>
    private static bool TryReadTarget(byte[] packet, int at, long arrivedAtMs, out long target, out int consumed)
    {
        // 0..2 = the small filler byte(s); 12 = the position block (3 floats) a boss that is currently up
        // carries between its code and its timestamp. Measured on real captures for both forms.
        foreach (int gap in Gaps)
        {
            int offset = at + gap;
            if (offset + 8 > packet.Length)
            {
                break;
            }

            long value = PacketPrimitives.ReadUInt64Le(packet, offset);
            if (value >= arrivedAtMs - TwoMinutesMs && value <= arrivedAtMs + EightDaysMs)
            {
                target = value;
                consumed = gap + 8;
                return true;
            }
        }

        target = 0;
        consumed = 0;
        return false;
    }

    private static readonly int[] Gaps = { 0, 1, 2, 12 };
}
