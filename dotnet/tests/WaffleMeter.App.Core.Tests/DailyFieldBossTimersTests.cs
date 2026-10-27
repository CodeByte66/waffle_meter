using WaffleMeter.App.Core;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// Spec for <see cref="DailyFieldBossTimers"/>: 정령왕 아그로 spawns every day at KST 09:00·21:00 since the
/// 2026-10-28 patch, so its next slot is offered from the clock wherever the player is (owner decision
/// 2026-10-27), and a 0x9101 time from the server still wins. Times are written as KST literals on purpose —
/// see <c>FieldBossAlarmTests.The_daily_agro_spawns_at_nine_and_twenty_one_kst</c>.
/// </summary>
public sealed class DailyFieldBossTimersTests
{
    private const int Agro = 2600068;

    private static readonly IReadOnlyDictionary<int, long> NoServerTimers = new Dictionary<int, long>();

    private static long Kst(int mo, int d, int h, int mi, int s = 0) =>
        new DateTimeOffset(2026, mo, d, h, mi, s, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();

    [Fact]
    public void Without_any_server_table_the_next_slot_is_offered()
    {
        Assert.Equal(Kst(10, 28, 9, 0), DailyFieldBossTimers.Merge(NoServerTimers, Kst(10, 28, 3, 0))[Agro]);
        Assert.Equal(Kst(10, 28, 21, 0), DailyFieldBossTimers.Merge(NoServerTimers, Kst(10, 28, 9, 0, 1))[Agro]);
        Assert.Equal(Kst(10, 29, 9, 0), DailyFieldBossTimers.Merge(NoServerTimers, Kst(10, 28, 21, 0, 1))[Agro]);
    }

    [Fact]
    public void The_other_bosses_are_passed_through_untouched()
    {
        var timers = new Dictionary<int, long> { [2406034] = Kst(10, 28, 4, 0) };

        IReadOnlyDictionary<int, long> merged = DailyFieldBossTimers.Merge(timers, Kst(10, 28, 3, 0));

        Assert.Equal(Kst(10, 28, 4, 0), merged[2406034]);
        Assert.Equal(2, merged.Count);
        Assert.Single(timers); // the caller's table is not mutated
    }

    [Fact]
    public void A_live_server_time_wins()
    {
        // The patch notice's hour could be off (감시자 카이라's was, by one hour) — the wire is the truth.
        var timers = new Dictionary<int, long> { [Agro] = Kst(10, 28, 20, 55) };

        Assert.Same(timers, DailyFieldBossTimers.Merge(timers, Kst(10, 28, 15, 0)));
    }

    [Fact]
    public void A_live_server_time_wins_even_far_from_the_derived_slot()
    {
        // The server skips tonight's 21:00 and names 05:00 tomorrow — eight hours from the derived slot, so only
        // the "still ahead" rule keeps it. Without that rule the derived 21:00 would ring for a spawn not coming.
        var timers = new Dictionary<int, long> { [Agro] = Kst(10, 29, 5, 0) };

        Assert.Equal(Kst(10, 29, 5, 0), DailyFieldBossTimers.Merge(timers, Kst(10, 28, 15, 0))[Agro]);
    }

    [Fact]
    public void A_past_server_time_for_the_same_slot_keeps_the_derived_one_out()
    {
        // The server said 20:00 and it has passed; the derived 21:00 is that same spawn — offering it would ring a
        // second set of leads (20:30/20:50/20:55) for a boss that is already up.
        var timers = new Dictionary<int, long> { [Agro] = Kst(10, 28, 20, 0) };

        Assert.Equal(Kst(10, 28, 20, 0), DailyFieldBossTimers.Merge(timers, Kst(10, 28, 20, 10))[Agro]);
    }

    [Fact]
    public void A_past_server_time_does_not_block_the_next_slot()
    {
        // The session table is never cleared: tonight's 21:00 from the server, once over, must hand over to
        // tomorrow's 09:00 — even when the server's value was a few seconds late.
        var timers = new Dictionary<int, long> { [Agro] = Kst(10, 28, 21, 0, 30) };

        Assert.Equal(Kst(10, 29, 9, 0), DailyFieldBossTimers.Merge(timers, Kst(10, 28, 21, 10))[Agro]);
    }

    [Fact]
    public void A_stale_per_kill_time_from_before_the_patch_does_not_block_the_slot()
    {
        // A per-kill respawn heard before the patch (03:00, not a slot) is six hours from 09:00 — not the same spawn.
        var timers = new Dictionary<int, long> { [Agro] = Kst(10, 28, 3, 0) };

        Assert.Equal(Kst(10, 28, 9, 0), DailyFieldBossTimers.Merge(timers, Kst(10, 28, 4, 0))[Agro]);
    }

    [Fact]
    public void The_same_slot_window_is_half_the_twelve_hour_gap()
    {
        Assert.Equal(6L * 60 * 60 * 1000, DailyFieldBossTimers.SameSlotWindowMs);
    }

    [Theory]
    [InlineData(8, 30, 30)]
    [InlineData(8, 50, 10)]
    [InlineData(8, 55, 5)]
    [InlineData(20, 30, 30)]
    public void The_shared_alarm_rings_each_lead_without_the_abyss_visited(int h, int mi, int lead)
    {
        // Half a minute into the lead's band — DueAlerts fires while the remaining time is in (L-1, L] minutes.
        long now = Kst(10, 28, h, mi) + 30_000;

        IReadOnlyList<FieldBossAlarm.Due> due =
            FieldBossAlarm.DueAlerts(DailyFieldBossTimers.Merge(NoServerTimers, now), now, [5, 10, 30]);

        FieldBossAlarm.Due d = Assert.Single(due);
        Assert.Equal(Agro, d.Code);
        Assert.Equal(lead, d.LeadMinutes);
    }
}
