using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// Covers DataManager.SavePartyRoster / PartyRoster — the authoritative pre-combat party source. The
/// 0x9702 roster packet gives member (nickname, server); we match each to a known uid by name+server so
/// the meter shows the party on dungeon entry, before any combat.
/// </summary>
public sealed class DataManagerPartyRosterTests
{
    [Fact]
    public void PartyRoster_matches_members_to_uids_by_name_and_server_executor_first()
    {
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SaveNickname(1, "플러시", isExecutor: true, server: 2003, jobByte: 0);
        dm.SaveUserPower(1, 3000);
        dm.SaveNickname(2, "Wildz", isExecutor: false, server: 1014, jobByte: 0);
        dm.SaveUserPower(2, 5000);

        dm.SavePartyRoster(new List<(string, int, int)> { ("Wildz", 1014, 2), ("플러시", 2003, 1), ("아직없음", 1010, 3) });

        IReadOnlyList<User> roster = dm.PartyRoster(300_000);

        Assert.Equal(2, roster.Count);  // 아직없음 has no uid yet -> excluded
        Assert.Equal(1, roster[0].Id);  // executor first, even with lower power
        Assert.Equal(2, roster[1].Id);
    }

    [Fact]
    public void PartyRosterIdentities_keeps_the_members_PartyRoster_drops()
    {
        // The mirror of the contract above: the raw snapshot keeps 아직없음 (no uid this session) and its slot,
        // because that is exactly the member a nameless overlay row usually belongs to.
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SaveNickname(1, "플러시", isExecutor: true, server: 2003, jobByte: 0);
        dm.SaveNickname(2, "Wildz", isExecutor: false, server: 1014, jobByte: 0);

        dm.SavePartyRoster(new List<(string, int, int)> { ("Wildz", 1014, 2), ("플러시", 2003, 1), ("아직없음", 1010, 3) });

        Assert.Equal(2, dm.PartyRoster(300_000).Count);
        IReadOnlyList<(string Nickname, int Server, int Slot)> raw = dm.PartyRosterIdentities(300_000);
        Assert.Equal(3, raw.Count);
        Assert.Contains(("아직없음", 1010, 3), raw);
    }

    [Fact]
    public void PartyRosterIdentities_is_empty_once_the_snapshot_is_stale()
    {
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SavePartyRoster(new List<(string, int, int)> { ("플러시", 2003, 1) });

        Assert.Single(dm.PartyRosterIdentities(300_000));
        now += 300_001;
        Assert.Empty(dm.PartyRosterIdentities(300_000));
    }

    [Fact]
    public void PartyRoster_resolves_self_to_the_live_executor_despite_stale_duplicates()
    {
        // The self re-registers under a fresh uid on every zone load (0x3633), leaving stale name+server
        // duplicates. The preview's own row must be the CURRENT executor (uid 300), not a stale self (100/200),
        // or it loses self-recognition (IsExecutor=false on the demoted prior selves).
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SaveNickname(100, "플러시", isExecutor: true, server: 2003, jobByte: 0); // stale self
        dm.SaveNickname(200, "플러시", isExecutor: true, server: 2003, jobByte: 0); // stale self
        dm.SaveNickname(300, "플러시", isExecutor: true, server: 2003, jobByte: 0); // current executor
        dm.SaveNickname(2, "Wildz", isExecutor: false, server: 1014, jobByte: 0);
        dm.SavePartyRoster(new List<(string, int, int)> { ("Wildz", 1014, 2), ("플러시", 2003, 1) });

        IReadOnlyList<User> roster = dm.PartyRoster(300_000);

        Assert.Equal(2, roster.Count);
        Assert.Equal(300, roster[0].Id);    // current executor, first
        Assert.True(roster[0].IsExecutor);
        Assert.Equal(2, roster[1].Id);
    }

    [Fact]
    public void A_partial_snapshot_does_not_shrink_a_fuller_roster()
    {
        // 0x9702 스냅샷은 부분으로 오는 것이 정상이다(코퍼스 실측: 정원 10 방 502건 중 완전한 것 290건).
        // 종전엔 통째 교체 + subset 가드로 막았는데, 지금은 슬롯 기준으로 합치므로 애초에 줄어들 일이 없다.
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2), ("C", 1, 3), ("D", 1, 4), ("E", 1, 5) });
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2) }); // 부분 재방송 — 합쳐진다

        Assert.Equal(5, dm.PartyMemberIdentities(300_000).Count);
    }

    /// <summary>
    /// 🔑 스냅샷은 <b>합쳐진다</b>(갈아끼우지 않는다). 0x9702 는 부분 로스터를 보내는 것이 정상이라
    /// (실측: 정원 10 방 스냅샷 502건 중 완전한 것 290건), 새 멤버가 하나 보인다고 나머지를 버리면
    /// 10인 공대가 2명으로 쪼그라드는 경우가 생긴다.
    /// <para>진짜 파티 교체는 <b>파티 id</b> 가 말해 준다 — 그건 아래
    /// <see cref="A_smaller_party_under_a_new_party_id_replaces_the_roster"/> 가 고정한다.</para>
    /// </summary>
    [Fact]
    public void A_snapshot_with_a_new_member_merges_rather_than_replacing()
    {
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2) });
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1), ("X", 1, 3) }); // 부분 재방송 + 새 멤버

        IReadOnlyList<(string Nickname, int Server)> ids = dm.PartyMemberIdentities(300_000);
        Assert.Equal(3, ids.Count);
        Assert.Contains(ids, m => m.Nickname == "X");
        Assert.Contains(ids, m => m.Nickname == "B"); // 안 실렸다고 해서 나간 것이 아니다
    }

    [Fact]
    public void PartyRoster_is_empty_when_the_snapshot_is_stale()
    {
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SaveNickname(1, "플러시", isExecutor: true, server: 2003, jobByte: 0);
        dm.SavePartyRoster(new List<(string, int, int)> { ("플러시", 2003, 1) });

        now += 300_001; // past the freshness window
        Assert.Empty(dm.PartyRoster(300_000));
    }

    [Fact]
    public void PartyRoster_is_empty_when_no_snapshot()
    {
        var dm = new DataManager { Clock = () => 1_000_000 };
        Assert.Empty(dm.PartyRoster(300_000));
    }

    [Fact]
    public void PartyRoster_is_cleared_on_a_character_switch()
    {
        // 콘팡 connects, a roster snapshot is taken (so the preview surfaces it), then the user switches to a
        // DIFFERENT character 마이농 (same server). The previous character's roster must not linger.
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SaveNickname(1, "콘팡", isExecutor: true, server: 2003, jobByte: 0);
        dm.SavePartyRoster(new List<(string, int, int)> { ("콘팡", 2003, 1) });
        Assert.Single(dm.PartyRoster(300_000)); // sanity: 콘팡 previews before the switch

        dm.SaveNickname(2, "마이농", isExecutor: true, server: 2003, jobByte: 0); // switch

        Assert.Empty(dm.PartyRoster(300_000)); // stale 콘팡 roster dropped
    }

    [Fact]
    public void PartyRoster_is_cleared_on_a_cross_server_same_name_switch()
    {
        // Same nickname on a DIFFERENT (known) server = a cross-server alt switch, also a real switch.
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SaveNickname(1, "콘팡", isExecutor: true, server: 2003, jobByte: 0);
        dm.SavePartyRoster(new List<(string, int, int)> { ("콘팡", 2003, 1) });

        dm.SaveNickname(2, "콘팡", isExecutor: true, server: 2004, jobByte: 0); // cross-server alt

        Assert.Empty(dm.PartyRoster(300_000));
    }

    [Fact]
    public void PartyRoster_survives_a_same_character_reinstance_same_server()
    {
        // Same character re-instancing under a fresh uid on a zone load (town -> dungeon) must KEEP the roster
        // that was saved first — the party about to form in the new zone is still ours. (The existing
        // self-resolution test saves the roster LAST, so it does not exercise this clear-then-read order.)
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SaveNickname(1, "콘팡", isExecutor: true, server: 2003, jobByte: 0);
        dm.SavePartyRoster(new List<(string, int, int)> { ("콘팡", 2003, 1) });

        dm.SaveNickname(2, "콘팡", isExecutor: true, server: 2003, jobByte: 0); // re-instance, same identity

        IReadOnlyList<User> roster = dm.PartyRoster(300_000);
        Assert.Single(roster);
        Assert.Equal("콘팡", roster[0].Nickname);
    }

    [Fact]
    public void PartyRoster_survives_a_same_character_reinstance_with_unknown_server()
    {
        // A truncated 0x3633 own-load yields Server=-1 (User ctor default). A same-name re-instance whose new
        // load is truncated must NOT be read as a cross-server switch — the naive oldServer != newServer check
        // would false-clear a legitimate dungeon party preview here. The server>0 guard preserves it.
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SaveNickname(1, "콘팡", isExecutor: true, server: 2003, jobByte: 0);
        dm.SavePartyRoster(new List<(string, int, int)> { ("콘팡", 2003, 1) });

        dm.SaveNickname(2, "콘팡", isExecutor: true, server: -1, jobByte: 0); // truncated re-instance

        IReadOnlyList<User> roster = dm.PartyRoster(300_000);
        Assert.Single(roster);
        Assert.Equal("콘팡", roster[0].Nickname);
    }

    [Fact]
    public void ExecutorIdentityChanged_fires_once_on_a_switch_and_never_on_a_reinstance()
    {
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        int fired = 0;
        dm.ExecutorIdentityChanged += () => fired++;

        dm.SaveNickname(1, "콘팡", isExecutor: true, server: 2003, jobByte: 0);  // first connect (no prior executor)
        dm.SaveNickname(2, "콘팡", isExecutor: true, server: 2003, jobByte: 0);  // re-instance, same identity
        dm.SaveNickname(3, "콘팡", isExecutor: true, server: -1, jobByte: 0);    // truncated re-instance
        Assert.Equal(0, fired);

        dm.SaveNickname(4, "마이농", isExecutor: true, server: 2003, jobByte: 0); // real switch
        Assert.Equal(1, fired);
    }

    /// <summary>The subset guard used to have no way to tell "my roster, minus someone" from "a different,
    /// smaller party formed out of the same people" — so leaving a raid and grouping up with four of its
    /// members kept the ten-man roster for the next ten minutes, and the battles in that window uploaded as
    /// raids. The server's party id separates them: it survives joins and leaves, and changes when the group
    /// is re-formed.</summary>
    [Fact]
    public void A_smaller_party_under_a_new_party_id_replaces_the_roster()
    {
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SavePartyRoster(
            new List<(string, int, int)>
            {
                ("A", 1, 1), ("B", 1, 2), ("C", 1, 3), ("D", 1, 4), ("E", 1, 5),
                ("F", 1, 6), ("G", 1, 7), ("H", 1, 8), ("I", 1, 9), ("J", 1, 10),
            },
            partyId: 417506);

        // the raid ends and four of them re-group — a strict subset, but a different party
        dm.SavePartyRoster(
            new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2), ("C", 1, 3), ("D", 1, 4) },
            partyId: 423993);

        Assert.Equal(4, dm.PartyMemberIdentities(300_000).Count);
    }

    /// <summary>…and the same subset under the SAME id is still just a partial re-broadcast. This is the case
    /// the guard exists for, and it must not be released: measured in the corpus, a 5→1 collapse of this shape
    /// had its full roster back 17.3 seconds later.</summary>
    [Fact]
    public void A_partial_snapshot_under_the_same_party_id_is_still_held()
    {
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SavePartyRoster(
            new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2), ("C", 1, 3), ("D", 1, 4), ("E", 1, 5) },
            partyId: 500100);
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1) }, partyId: 500100);
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1) }, partyId: 500100); // and again

        Assert.Equal(5, dm.PartyMemberIdentities(300_000).Count);
    }

    /// <summary>An unread id (0) must never read as "changed" — a short packet would otherwise disarm the
    /// guard entirely and bring back the 5→4→3→2 shrink.</summary>
    [Fact]
    public void An_unknown_party_id_does_not_release_the_guard()
    {
        long now = 1_000_000;
        var dm = new DataManager { Clock = () => now };
        dm.SavePartyRoster(
            new List<(string, int, int)> { ("A", 1, 1), ("B", 1, 2), ("C", 1, 3) }, partyId: 500100);
        dm.SavePartyRoster(new List<(string, int, int)> { ("A", 1, 1) }, partyId: 0);

        Assert.Equal(3, dm.PartyMemberIdentities(300_000).Count);
    }
}
