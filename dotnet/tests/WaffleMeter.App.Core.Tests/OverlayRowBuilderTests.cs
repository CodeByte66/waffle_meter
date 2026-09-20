using WaffleMeter.App.Core;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// Covers OverlayRowBuilder — the pure row-selection extracted from OverlayViewModel.Update. Pins three
/// behaviors: (#3) a no-nickname COMBAT row (a bare mid-join provisional actor) is hidden but its DPS is
/// retained and pops in once identity arrives; (#1) the recognized self appears in the pre-combat roster and
/// self-colors; (#2) the roster is suppressed once a real combat report is on screen.
/// </summary>
public sealed class OverlayRowBuilderTests
{
    private static DpsReport CombatReport(params (int Uid, string? Nick, double Amount)[] rows)
    {
        var report = new DpsReport();
        foreach ((int uid, string? nick, double amount) in rows)
        {
            report.Contributors.Add(new User(uid, nick, nick == null ? -1 : 2003));
            report.Information[uid] = new DpsInformation(amount, amount, amount, amount);
        }

        return report;
    }

    [Fact]
    public void Blank_nickname_combat_row_is_filtered_and_does_not_count_as_combat()
    {
        DpsReport report = CombatReport((13601, null, 1000)); // a bare 난입 provisional actor

        IReadOnlyList<OverlayRowBuilder.Row> rows = OverlayRowBuilder.Build(
            report, roster: [], liveSelfId: 0, useTotalDamage: true, showPreCombatRoster: true, out bool hasCombatRows);

        Assert.Empty(rows);              // no blank "broken" row
        Assert.False(hasCombatRows);     // CRITICAL: regate — no boss bar / ticking timer over the placeholder
    }

    [Fact]
    public void TopN_caps_the_displayed_rows()
    {
        DpsReport report = CombatReport(
            (1, "일", 1000), (2, "이", 900), (3, "삼", 800), (4, "사", 700), (5, "오", 600));

        IReadOnlyList<OverlayRowBuilder.Row> rows = OverlayRowBuilder.Build(
            report, [], liveSelfId: 0, useTotalDamage: true, showPreCombatRoster: false, out _, topN: 3);

        Assert.Equal(3, rows.Count);                         // capped
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.Uid)); // top 3 by amount
    }

    [Fact]
    public void Self_is_shown_even_below_the_topN_cap()
    {
        // self (uid 5) is the lowest dealer, outside a top-2 cap — must still appear.
        DpsReport report = CombatReport(
            (1, "일", 1000), (2, "이", 900), (3, "삼", 800), (5, "본인", 100));
        report.Contributors.First(c => c.Id == 5).IsExecutor = true;

        IReadOnlyList<OverlayRowBuilder.Row> rows = OverlayRowBuilder.Build(
            report, [], liveSelfId: 5, useTotalDamage: true, showPreCombatRoster: false, out _, topN: 2);

        Assert.Contains(rows, r => r.Uid == 5);              // self always included
        Assert.Contains(rows, r => r.Uid == 1);              // plus the top-2
        Assert.Contains(rows, r => r.Uid == 2);
        Assert.DoesNotContain(rows, r => r.Uid == 3);        // a non-self below the cap is dropped
    }

    [Fact]
    public void Only_named_combat_rows_show_even_if_the_bare_actor_deals_more()
    {
        DpsReport report = CombatReport((1, "플러시", 500), (13601, null, 1000));

        IReadOnlyList<OverlayRowBuilder.Row> rows = OverlayRowBuilder.Build(
            report, [], 0, true, false, out bool hasCombatRows);

        Assert.Single(rows);
        Assert.Equal(1, rows[0].Uid);
        Assert.True(hasCombatRows);
    }

    [Fact]
    public void A_bare_actor_that_gains_a_nickname_appears_with_its_accumulated_dps()
    {
        var report = new DpsReport();
        var user = new User(13601); // bare provisional (Nickname null)
        report.Contributors.Add(user);
        report.Information[13601] = new DpsInformation(1000, 100, 50, 10);

        Assert.Empty(OverlayRowBuilder.Build(report, [], 0, true, false, out _)); // hidden while bare

        user.Nickname = "플러시"; // 0x3633/0x3645 enriches the SAME object in place
        user.Server = 2003;

        IReadOnlyList<OverlayRowBuilder.Row> rows = OverlayRowBuilder.Build(report, [], 0, true, false, out bool hasCombatRows);
        Assert.Single(rows);
        Assert.Equal(1000.0, rows[0].Info.Amount); // full accumulated DPS retained
        Assert.True(hasCombatRows);
    }

    [Fact]
    public void Recognized_self_appears_first_in_the_pre_combat_roster_and_self_colors()
    {
        var exec = new User(1, "플러시", 2003) { IsExecutor = true, Power = 3000 };
        var mate = new User(2, "Wildz", 1014) { Power = 5000 };
        var report = new DpsReport(); // pre-combat: empty Information, ExecutorId 0

        IReadOnlyList<OverlayRowBuilder.Row> rows = OverlayRowBuilder.Build(
            report, roster: new[] { exec, mate }, liveSelfId: 1, useTotalDamage: true, showPreCombatRoster: true, out bool hasCombatRows);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].Uid);    // executor first (roster order preserved)
        Assert.True(rows[0].IsSelf);     // self-colored via User.IsExecutor
        Assert.False(rows[1].IsSelf);
        Assert.False(hasCombatRows);     // idle preview is not "in combat"
    }

    [Fact]
    public void Pre_combat_roster_is_suppressed_once_a_real_combat_report_is_shown()
    {
        DpsReport report = CombatReport((1, "플러시", 1000));
        report.ExecutorId = 1; // a saved/post-combat report freezes the executor uid

        IReadOnlyList<OverlayRowBuilder.Row> rows = OverlayRowBuilder.Build(
            report, roster: new[] { new User(2, "Wildz", 1014) }, liveSelfId: 1, useTotalDamage: true, showPreCombatRoster: true, out bool hasCombatRows);

        Assert.Single(rows);          // only the combat row; the roster is NOT merged in
        Assert.Equal(1, rows[0].Uid);
        Assert.True(hasCombatRows);
    }

    [Fact]
    public void All_bare_combat_with_a_known_roster_shows_neither_a_blank_row_nor_the_preview()
    {
        // Mid-join: the only damager is bare (no nickname) and the executor isn't recognized yet (ExecutorId 0),
        // but a 0x9702 party roster is known. The idle preview must NOT resurface OVER the live fight — the
        // gate keys on report.Information (real combat present), not the post-filter row count.
        DpsReport report = CombatReport((13601, null, 1000));
        var roster = new[] { new User(2, "Wildz", 1014), new User(3, "Mate", 1014) };

        IReadOnlyList<OverlayRowBuilder.Row> rows = OverlayRowBuilder.Build(
            report, roster, liveSelfId: 0, useTotalDamage: true, showPreCombatRoster: true, out bool hasCombatRows);

        Assert.Empty(rows);          // no blank combat row AND no idle preview injected over live combat
        Assert.False(hasCombatRows);
    }

    [Fact]
    public void Self_outside_the_top_ten_is_still_appended()
    {
        // 10 named dealers (a full 5+5 raid) plus self as the lowest — self falls outside the top 10 cap and
        // must still be appended so 본인 always shows.
        var rows = new (int, string?, double)[]
        {
            (10, "A", 100), (11, "B", 90), (12, "C", 80), (13, "D", 70),
            (14, "E", 60), (15, "F", 50), (16, "G", 40), (17, "H", 30),
            (18, "I", 20), (19, "J", 15),
            (1, "Me", 5),
        };
        DpsReport report = CombatReport(rows);
        report.Contributors[10].IsExecutor = true; // uid 1 = self, lowest damage

        IReadOnlyList<OverlayRowBuilder.Row> display = OverlayRowBuilder.Build(report, [], 1, true, false, out _);

        Assert.Equal(11, display.Count);                      // top 10 + self appended
        Assert.Contains(display, r => r.Uid == 1 && r.IsSelf);
    }

    // Lost-executor recovery: 본인 re-instanced (new entity id) and the new id's own-load (0x3633) never
    // arrived, so 본인 fights bare. Build a report where the recognized executor (15482) dealt no damage and a
    // bare GLADIATOR (4162) is a major dealer; selfJob=GLADIATOR is the known executor job.
    private static DpsReport LostExecutorReport(JobClass bareJob, double bareAmount)
    {
        var report = new DpsReport { ExecutorId = 15482 }; // recognized 본인 uid, but it deals nothing here
        report.Contributors.Add(new User(101, "다즈비", 1005));
        report.Information[101] = new DpsInformation(6000, 600, 30, 10);
        report.Contributors.Add(new User(102, "설핏", 1001));
        report.Information[102] = new DpsInformation(5000, 500, 25, 8);
        report.Contributors.Add(new User(4162) { Job = bareJob }); // 본인's new bare id (job from own skills)
        report.Information[4162] = new DpsInformation(bareAmount, bareAmount / 10, 20, 7);
        return report;
    }

    private static IReadOnlyList<OverlayRowBuilder.Row> BuildWithSelf(DpsReport report, int liveSelfId = 15482) =>
        OverlayRowBuilder.Build(report, [], liveSelfId, useTotalDamage: true, showPreCombatRoster: false, out _,
            selfNickname: "하아앙", selfServer: 2003, selfJob: JobClass.GLADIATOR, selfPower: 0);

    [Fact]
    public void Lost_executor_is_recovered_as_self_named_and_self_colored_without_mutating_the_live_user()
    {
        DpsReport report = LostExecutorReport(JobClass.GLADIATOR, bareAmount: 4000); // ≥20% of top 6000 -> major
        var bareUser = report.Contributors.First(u => u.Id == 4162);

        IReadOnlyList<OverlayRowBuilder.Row> rows = BuildWithSelf(report);

        OverlayRowBuilder.Row self = Assert.Single(rows.Where(r => r.Uid == 4162));
        Assert.True(self.IsSelf);                       // self-colored
        Assert.Equal("하아앙", self.User!.Nickname);     // named from the known executor identity
        Assert.Equal(3, rows.Count);                    // recovered self + the two named, none hidden
        Assert.Null(bareUser.Nickname);                 // display-only copy — the live User is untouched
    }

    [Fact]
    public void No_recovery_when_the_executor_uid_is_present_in_the_fight()
    {
        // Normal fight: 본인's registered uid IS dealing damage -> the gate is false -> recovery never runs;
        // a separate bare same-job actor is simply hidden by the blank-row filter.
        var report = new DpsReport { ExecutorId = 101 };
        report.Contributors.Add(new User(101, "하아앙", 2003) { IsExecutor = true });
        report.Information[101] = new DpsInformation(6000, 600, 60, 10);
        report.Contributors.Add(new User(4162) { Job = JobClass.GLADIATOR });
        report.Information[4162] = new DpsInformation(4000, 400, 40, 7);

        IReadOnlyList<OverlayRowBuilder.Row> rows = BuildWithSelf(report, liveSelfId: 101);

        Assert.Single(rows);
        Assert.Equal(101, rows[0].Uid);
    }

    [Fact]
    public void No_recovery_when_two_same_job_bare_dealers_are_ambiguous()
    {
        DpsReport report = LostExecutorReport(JobClass.GLADIATOR, bareAmount: 4000);
        report.Contributors.Add(new User(7777) { Job = JobClass.GLADIATOR }); // a SECOND bare gladiator
        report.Information[7777] = new DpsInformation(3800, 380, 19, 6);

        IReadOnlyList<OverlayRowBuilder.Row> rows = BuildWithSelf(report);

        Assert.Equal(2, rows.Count);                                 // only the two named; ambiguous -> no recovery
        Assert.DoesNotContain(rows, r => r.Uid == 4162 || r.Uid == 7777);
    }

    [Fact]
    public void No_recovery_when_the_bare_dealer_job_mismatches_self()
    {
        DpsReport report = LostExecutorReport(JobClass.SORCERER, bareAmount: 4000); // not 본인's GLADIATOR

        IReadOnlyList<OverlayRowBuilder.Row> rows = BuildWithSelf(report);

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Uid == 4162);
    }

    [Fact]
    public void No_recovery_when_the_bare_dealer_is_minor()
    {
        DpsReport report = LostExecutorReport(JobClass.GLADIATOR, bareAmount: 500); // ~8% of top 6000 -> below 20%

        IReadOnlyList<OverlayRowBuilder.Row> rows = BuildWithSelf(report);

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Uid == 4162);
    }

    private static IReadOnlyList<OverlayRowBuilder.Row> BuildWithSelfAndParty(DpsReport report, IReadOnlyList<User> party) =>
        OverlayRowBuilder.Build(report, [], liveSelfId: 15482, useTotalDamage: true, showPreCombatRoster: false, out _,
            selfNickname: "하아앙", selfServer: 2003, selfJob: JobClass.GLADIATOR, selfPower: 0, authoritativeParty: party);

    [Fact]
    public void No_recovery_at_a_field_boss_where_named_damagers_are_outsiders()
    {
        // 본인(15482) is idle at a PUBLIC field boss: a bare same-job major (4162) is a STRANGER, and the named
        // damagers (다즈비/설핏) are zerg outsiders NOT in the authoritative party. Party-context guard (6) must
        // suppress — else a random strong stranger is relabeled 본인 (the field-boss "my DPS" bug).
        DpsReport report = LostExecutorReport(JobClass.GLADIATOR, bareAmount: 4000);

        IReadOnlyList<OverlayRowBuilder.Row> rows = BuildWithSelfAndParty(report, party: []); // solo: no party

        Assert.Equal(2, rows.Count);                       // only the two named strangers
        Assert.DoesNotContain(rows, r => r.Uid == 4162);   // the stranger is NOT relabeled 본인
        Assert.DoesNotContain(rows, r => r.IsSelf);
    }

    [Fact]
    public void Recovery_still_fires_in_a_party_dungeon_when_named_damagers_are_party_members()
    {
        // Identical shape, but the named damagers ARE the authoritative (0x9702) party — a genuine dungeon
        // re-instance. Guard (6) must NOT block the legit recovery (no side effect on the real use case).
        DpsReport report = LostExecutorReport(JobClass.GLADIATOR, bareAmount: 4000);
        var party = new[] { new User(101, "다즈비", 1005), new User(102, "설핏", 1001) };

        IReadOnlyList<OverlayRowBuilder.Row> rows = BuildWithSelfAndParty(report, party);

        OverlayRowBuilder.Row self = Assert.Single(rows.Where(r => r.Uid == 4162));
        Assert.True(self.IsSelf);
        Assert.Equal("하아앙", self.User!.Nickname);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void Stale_named_self_combat_uid_is_recovered_when_the_roster_confirms_it_is_not_a_party_member()
    {
        // Bug 3: 본인's re-instanced combat uid (4162) inherited a STALE non-party name ("틸놈틸") from a prior
        // entity that reused the id. With a CONFIRMED roster that does not contain 틸놈틸, the relaxed candidate
        // rule reclaims it as 본인 — the bare-only filter never would (it is not bare).
        var report = new DpsReport { ExecutorId = 15482 };
        report.Contributors.Add(new User(101, "다즈비", 1005));
        report.Information[101] = new DpsInformation(6000, 600, 30, 10);
        report.Contributors.Add(new User(4162, "틸놈틸", 2003) { Job = JobClass.GLADIATOR }); // stale non-party name
        report.Information[4162] = new DpsInformation(4000, 400, 20, 7);
        var party = new[] { new User(101, "다즈비", 1005), new User(15482, "하아앙", 2003) { IsExecutor = true } };

        IReadOnlyList<OverlayRowBuilder.Row> rows = BuildWithSelfAndParty(report, party);

        OverlayRowBuilder.Row self = Assert.Single(rows.Where(r => r.Uid == 4162));
        Assert.True(self.IsSelf);
        Assert.Equal("하아앙", self.User!.Nickname); // relabeled to the known executor identity, not "틸놈틸"
    }

    [Fact]
    public void Stale_named_self_recovery_needs_a_confirmed_roster()
    {
        // Same shape but NO roster: without a party to prove "틸놈틸" is an outsider, the safe bare-only rule holds
        // and the row is NOT relabeled 본인 (never steal a genuine stranger's row when the party is unknown).
        var report = new DpsReport { ExecutorId = 15482 };
        report.Contributors.Add(new User(101, "다즈비", 1005));
        report.Information[101] = new DpsInformation(6000, 600, 30, 10);
        report.Contributors.Add(new User(4162, "틸놈틸", 2003) { Job = JobClass.GLADIATOR });
        report.Information[4162] = new DpsInformation(4000, 400, 20, 7);

        IReadOnlyList<OverlayRowBuilder.Row> rows = BuildWithSelf(report); // authoritativeParty == null

        Assert.DoesNotContain(rows, r => r.IsSelf);
        Assert.Equal("틸놈틸", rows.Single(r => r.Uid == 4162).User!.Nickname);
    }

    [Fact]
    public void Bare_major_party_member_is_named_from_the_authoritative_roster()
    {
        // Bug 4: a party member (엽록소) fights bare — its 0x3645 name-link was missed. The roster knows it, and it
        // is the ONLY unaccounted member ↔ the ONLY bare major, so it is recovered and named.
        var report = new DpsReport();
        report.Contributors.Add(new User(101, "다즈비", 1005));
        report.Information[101] = new DpsInformation(6000, 600, 30, 10);
        report.Contributors.Add(new User(102, "설핏", 1001));
        report.Information[102] = new DpsInformation(5000, 500, 25, 8);
        report.Contributors.Add(new User(4162) { Job = JobClass.SORCERER }); // bare major = 엽록소's combat uid
        report.Information[4162] = new DpsInformation(7000, 700, 35, 12);
        var party = new[]
        {
            new User(101, "다즈비", 1005), new User(102, "설핏", 1001),
            new User(9, "엽록소", 1014) { Job = JobClass.SORCERER }, // unaccounted roster member
        };

        IReadOnlyList<OverlayRowBuilder.Row> rows = OverlayRowBuilder.Build(
            report, [], liveSelfId: 0, useTotalDamage: true, showPreCombatRoster: false, out _, authoritativeParty: party);

        Assert.Equal(3, rows.Count);
        Assert.Equal("엽록소", rows.Single(r => r.Uid == 4162).User!.Nickname);
    }

    [Fact]
    public void Bare_major_is_not_named_without_a_roster()
    {
        // Safety: absent a roster, a bare major could be a field-boss STRANGER — it stays hidden (never a "파티원").
        var report = new DpsReport();
        report.Contributors.Add(new User(101, "다즈비", 1005));
        report.Information[101] = new DpsInformation(6000, 600, 30, 10);
        report.Contributors.Add(new User(4162) { Job = JobClass.SORCERER });
        report.Information[4162] = new DpsInformation(7000, 700, 35, 12);

        IReadOnlyList<OverlayRowBuilder.Row> rows = OverlayRowBuilder.Build(
            report, [], liveSelfId: 0, useTotalDamage: true, showPreCombatRoster: false, out _, authoritativeParty: null);

        Assert.Single(rows);                              // only the named 다즈비
        Assert.DoesNotContain(rows, r => r.Uid == 4162);
    }

    [Fact]
    public void Bare_party_recovery_is_skipped_when_the_match_is_ambiguous()
    {
        // Two bare majors but only one unaccounted roster member → ambiguous → recover NEITHER (never guess).
        var report = new DpsReport();
        report.Contributors.Add(new User(101, "다즈비", 1005));
        report.Information[101] = new DpsInformation(6000, 600, 30, 10);
        report.Contributors.Add(new User(4162) { Job = JobClass.SORCERER });
        report.Information[4162] = new DpsInformation(5000, 500, 25, 8);
        report.Contributors.Add(new User(7777) { Job = JobClass.SORCERER });
        report.Information[7777] = new DpsInformation(4000, 400, 20, 7);
        var party = new[]
        {
            new User(101, "다즈비", 1005), new User(9, "엽록소", 1014) { Job = JobClass.SORCERER },
        };

        IReadOnlyList<OverlayRowBuilder.Row> rows = OverlayRowBuilder.Build(
            report, [], liveSelfId: 0, useTotalDamage: true, showPreCombatRoster: false, out _, authoritativeParty: party);

        Assert.Single(rows);                                          // only the named 다즈비
        Assert.DoesNotContain(rows, r => r.Uid == 4162 || r.Uid == 7777);
    }

    [Fact]
    public void Recovery_still_fires_solo_when_there_is_no_other_named_damager()
    {
        // Solo dungeon re-instance: the ONLY damager is 본인's bare new id. No named outsider exists, so guard (6)
        // is vacuously satisfied even with an empty authoritative party → recovery runs (no side effect on solo).
        var report = new DpsReport { ExecutorId = 15482 };
        report.Contributors.Add(new User(4162) { Job = JobClass.GLADIATOR });
        report.Information[4162] = new DpsInformation(4000, 400, 100, 20);

        IReadOnlyList<OverlayRowBuilder.Row> rows = BuildWithSelfAndParty(report, party: []);

        OverlayRowBuilder.Row self = Assert.Single(rows);
        Assert.Equal(4162, self.Uid);
        Assert.True(self.IsSelf);
        Assert.Equal("하아앙", self.User!.Nickname);
    }
}
