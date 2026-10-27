using WaffleMeter.Capture;

namespace WaffleMeter.App.Core;

/// <summary>
/// The clock half of the field-boss timer supply for the bosses that spawn every day at fixed KST hours
/// (<see cref="FieldBossFixedSchedule.DailyCodes"/> — 정령왕 아그로 since the 2026-10-28 patch, 09·21시).
/// <para><b>Why a clock (owner decision 2026-10-27).</b> The 0x9101 table is map-scoped, so a per-kill boss only
/// ever rang for players who had stood in its map this session. A daily fixed spawn is known in advance, and the
/// point of the reminder is to travel there before it — so its next spawn is laid over the table wherever you
/// are, like the 아티쟁 bosses (<see cref="ArtifactWarBossTimers"/>). It stays an ordinary field-boss row: the
/// picker toggle, the leads and the combat mute all apply, and the toast/voice are the shared ones.</para>
/// <para><b>The server wins.</b> A 0x9101 time that has not yet passed (within
/// <see cref="ArtifactWarSchedule.BossTargetGraceMs"/>) is kept as is, and a past one that sits within
/// <see cref="SameSlotWindowMs"/> of the derived slot is that slot's spawn already timed by the server, so the
/// derived one stays out — one value per boss, never a second set of leads for a spawn that has happened.
/// That matters because the hours are a patch notice's, not a capture's (see
/// <see cref="FieldBossFixedSchedule.DailySpawnHoursKst"/>).</para>
/// <para>Stateless and pure: the next slot from the clock is always the right one, so unlike the 아티쟁 supply
/// there is nothing to remember across polls.</para>
/// </summary>
public static class DailyFieldBossTimers
{
    /// <summary>How close a past server-sent time and a derived slot must be to count as the SAME spawn: half the
    /// shortest gap between two consecutive slots (09·21시 → 6 h), so a server time can only ever be claimed by
    /// the slot nearest to it.</summary>
    public static long SameSlotWindowMs { get; } = ShortestSlotGapMs() / 2;

    /// <summary>The timers to evaluate at <paramref name="nowMs"/>: <paramref name="timers"/> plus the next
    /// clock slot of every daily boss the server has not timed. Returns <paramref name="timers"/> itself when
    /// there is nothing to add.</summary>
    public static IReadOnlyDictionary<int, long> Merge(IReadOnlyDictionary<int, long> timers, long nowMs)
    {
        Dictionary<int, long>? merged = null;
        foreach (int code in FieldBossFixedSchedule.DailyCodes)
        {
            if (!FieldBossFixedSchedule.TryNextSpawn(code, nowMs, out long derived))
            {
                continue;
            }

            if (timers.TryGetValue(code, out long sent)
                && (sent >= nowMs - ArtifactWarSchedule.BossTargetGraceMs
                    || Math.Abs(derived - sent) < SameSlotWindowMs))
            {
                continue; // the server timed this spawn itself — still ahead, or already past for this same slot
            }

            merged ??= new Dictionary<int, long>(timers);
            merged[code] = derived;
        }

        return merged ?? timers;
    }

    private static long ShortestSlotGapMs()
    {
        IReadOnlyList<int> hours = FieldBossFixedSchedule.DailySpawnHoursKst;
        int shortest = 24 - hours[^1] + hours[0]; // the gap across midnight
        for (int i = 1; i < hours.Count; i++)
        {
            shortest = Math.Min(shortest, hours[i] - hours[i - 1]);
        }

        return shortest * 60L * 60 * 1000;
    }
}
