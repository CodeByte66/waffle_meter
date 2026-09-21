using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 시련 난이도의 <b>런 경계</b>는 인스턴스가 아니라 <b>방(roomKey)</b>이 긋는다.
///
/// <para>어픽스는 마을의 방에서 오고, 그 방 정보를 실은 프레임은 인스턴스 phase-1 보다 <b>3.4~4.3초 먼저</b>
/// 도착한다(실측 10/10). 그런데 <see cref="TrialDifficultyTracker.ObservePhaseWindow"/> 는 시작 시각이
/// 바뀌면 들고 있던 축을 통째로 지우고 있었다 — 즉 방에서 네 축을 다 받아 놓고도 인스턴스에 들어가는
/// 순간 날아갔다. 그게 "네 축이 다 와도 라벨이 여전히 범위"였던 이유다.</para>
///
/// <para>런마다 반드시 새 key 가 발급되고(실측 9/9) 같은 방에서 재풀하면 유지되므로, roomKey 가 창보다
/// 정확하고 순서도 앞선다.</para>
/// </summary>
public sealed class TrialRoomAffixRunBoundaryTests
{
    private const int Trial = TrialDifficultyTracker.TrialMapId;
    private const int OtherDungeon = 600153;
    private const int MainPhase = 2;

    private static int[] Quad(int timelimit, int rebirth, int bossBuff, int skillUpgrade) =>
        [timelimit, rebirth, bossBuff, skillUpgrade];

    [Fact]
    public void Four_axes_from_the_room_pin_the_level_to_a_number()
    {
        var t = new TrialDifficultyTracker();

        t.ObserveRoomAffixes(Trial, roomKey: 384095, Quad(4, 4, 4, 4));

        Assert.Equal(16, t.Current.Level);
        Assert.Equal("시련 16단계", t.Current.Label);
        Assert.True(t.Current.IsTopDifficulty);
    }

    [Fact]
    public void The_rebirth_axis_alone_moves_the_number()
    {
        // 오너가 구동한 시퀀스. 종전에는 이 축을 못 읽어 전부 "시련 13~16단계" 였다.
        foreach ((int rebirth, int expected) in new[] { (1, 13), (2, 14), (3, 15), (4, 16) })
        {
            var t = new TrialDifficultyTracker();
            t.ObserveRoomAffixes(Trial, roomKey: 384095, Quad(4, rebirth, 4, 4));
            Assert.Equal(expected, t.Current.Level);
        }
    }

    [Fact]
    public void Entering_the_instance_no_longer_discards_what_the_room_said()
    {
        // 🔴 이게 고친 결함이다. 방 → 인스턴스 순서(실측 10/10)를 그대로 재현한다.
        var t = new TrialDifficultyTracker();
        t.ObserveRoomAffixes(Trial, roomKey: 384095, Quad(4, 4, 4, 4));

        t.ObservePhaseWindow(Trial, MainPhase, startMs: 1_000_000, windowMs: 600_000);

        Assert.Equal(16, t.Current.Level);
    }

    [Fact]
    public void A_re_pull_in_the_same_room_keeps_the_settings()
    {
        // 같은 방에서 재풀하면 key 가 유지된다(07-15 room 187978, phase 1→2→3→4→1→2). 지우면 안 된다.
        var t = new TrialDifficultyTracker();
        t.ObserveRoomAffixes(Trial, roomKey: 187978, Quad(4, 4, 4, 4));
        t.ObservePhaseWindow(Trial, MainPhase, startMs: 1_000_000, windowMs: 600_000);

        t.ObservePhaseWindow(Trial, MainPhase, startMs: 2_000_000, windowMs: 600_000);

        Assert.Equal(16, t.Current.Level);
    }

    [Fact]
    public void A_new_room_starts_the_settings_over()
    {
        var t = new TrialDifficultyTracker();
        t.ObserveRoomAffixes(Trial, roomKey: 384095, Quad(4, 4, 4, 4));

        // 다음 런은 난이도를 낮춰 잡았고, 이번엔 보스강화 축이 아직 안 왔다고 하자.
        t.ObserveRoomAffixes(Trial, roomKey: 384441, Quad(1, 1, 0, 1));

        Assert.Null(t.Current.Level);                 // 세 축만 알므로 범위
        Assert.Equal(3, t.Current.KnownCount);
        Assert.Equal("시련 4~7단계", t.Current.Label); // 모르는 축은 1~4
    }

    [Fact]
    public void A_non_trial_room_clears_everything()
    {
        // 다른 컨텐츠 방을 잡았다 = 시련은 끝났다. 2026-08-11 가르가움 오염과 같은 계열의 방어다.
        var t = new TrialDifficultyTracker();
        t.ObserveRoomAffixes(Trial, roomKey: 384095, Quad(4, 4, 4, 4));

        t.ObserveRoomAffixes(OtherDungeon, roomKey: 999, Quad(4, 4, 4, 4));

        Assert.False(t.Current.IsTrial);
        Assert.Equal(string.Empty, t.Current.Label);
    }

    [Fact]
    public void Leaving_the_trial_map_still_clears()
    {
        // 맵 전환 Reset 경로는 그대로 살아 있어야 한다.
        var t = new TrialDifficultyTracker();
        t.ObserveRoomAffixes(Trial, roomKey: 384095, Quad(4, 4, 4, 4));

        t.ObservePhaseWindow(OtherDungeon, MainPhase, startMs: 3_000_000, windowMs: 600_000);

        Assert.False(t.Current.IsTrial);
    }

    [Fact]
    public void The_phase_window_still_supplies_the_timelimit_when_no_room_was_seen()
    {
        // 어픽스를 못 받은 런(남의 방 초대·자동매칭 등)에서는 종전 단독 폴백이 그대로 동작해야 한다.
        var t = new TrialDifficultyTracker();

        t.ObservePhaseWindow(Trial, MainPhase, startMs: 1_000_000, windowMs: 900_000);

        Assert.Equal(3, t.Current.Timelimit);   // 900초 = 3단계
        Assert.Null(t.Current.Level);
    }

    [Fact]
    public void The_window_agrees_with_the_room_rather_than_fighting_it()
    {
        // 실측 15/15 로 창의 Timelimit 이 어픽스 축0 과 같은 값이다. 덮어써도 충돌이 없어야 한다.
        var t = new TrialDifficultyTracker();
        t.ObserveRoomAffixes(Trial, roomKey: 384095, Quad(4, 2, 3, 1));

        t.ObservePhaseWindow(Trial, MainPhase, startMs: 1_000_000, windowMs: 600_000); // 600초 = 4단계

        Assert.Equal(4, t.Current.Timelimit);
        Assert.Equal(10, t.Current.Level);
    }

    [Fact]
    public void A_malformed_quad_is_ignored_rather_than_half_applied()
    {
        var t = new TrialDifficultyTracker();
        t.ObserveRoomAffixes(Trial, roomKey: 384095, Quad(4, 4, 4, 4));

        t.ObserveRoomAffixes(Trial, roomKey: 384095, [9, 9, 9]);   // 길이가 틀림

        Assert.Equal(16, t.Current.Level);
    }
}
