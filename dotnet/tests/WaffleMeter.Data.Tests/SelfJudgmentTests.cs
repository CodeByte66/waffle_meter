using System.Reflection;
using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// Locks the judgment cross-tabs the stats payload carries for the uploader (<see cref="SelfJudgmentAccumulator"/>).
///
/// <para>These are the observations a server-side estimator uses to infer a boss's 강타 저항 / 막기 / 치명타
/// 저항, none of which exist on the wire or in the client. Every property tested here is load-bearing for that
/// inference in a way that produces no visible symptom when it breaks — a bad denominator or a mis-bucketed
/// stat still yields a plausible-looking histogram, just one that answers a different question.</para>
/// </summary>
public sealed class SelfJudgmentTests
{
    private const int Flagged = 0x36; // switch-type 6: carries a judgment region
    private const int Unflagged = 0x34; // switch-type 4: no region at all

    private static ParsedDamagePacket Hit(
        int switchVariable = Flagged,
        int hardHitBp = 5_000,
        int perfectBp = 5_000,
        int accuracy = 1_400,
        int criticalBp = 2_000,
        int criticalIncBp = 5_000,
        int mask = -1,
        int position = 2,
        int rawSkillCode = 11_110_000,
        bool crit = false,
        long timestamp = 10_000L,
        long stampAt = 10_000L,
        params SpecialDamage[] specials) => new()
        {
            ActorId = 1,
            TargetId = 2,
            Damage = 100,
            SkillCode = rawSkillCode,
            RawSkillCode = rawSkillCode,
            Type = crit ? 3 : 2,
            SwitchVariable = switchVariable,
            Position = position,
            Timestamp = timestamp,
            Specials = specials,
            JudgmentStats = new JudgmentStatStamp(
                mask == -1
                    ? JudgmentStatStamp.HasHardHit | JudgmentStatStamp.HasPerfect | JudgmentStatStamp.HasAccuracy
                      | JudgmentStatStamp.HasCritical | JudgmentStatStamp.HasCriticalInc
                    : mask,
                hardHitBp, perfectBp, accuracy, criticalBp, criticalIncBp, stampAt),
        };

    private static SelfJudgmentSnapshot Build(params ParsedDamagePacket[] hits)
    {
        var acc = new SelfJudgmentAccumulator();
        foreach (ParsedDamagePacket hit in hits) acc.Accumulate(hit, battleStart: 0L);
        return acc.Build(targetMobCode: 2_301_721, sheet: null);
    }

    /// <summary>The invariant the server uses to detect a double-counting accumulator. A cache replay that
    /// failed to clear produces exactly this violation and NOTHING else: every ratio stays correct while the
    /// apparent sample size doubles, so the confidence interval narrows by √2 with no other symptom.</summary>
    [Fact]
    public void BinCountsSumToAxisTotals()
    {
        SelfJudgmentSnapshot snapshot = Build(
            Hit(hardHitBp: 5_000, specials: SpecialDamage.DOUBLE),
            Hit(hardHitBp: 5_000),
            Hit(hardHitBp: 9_900, specials: SpecialDamage.DOUBLE),
            Hit(hardHitBp: 9_900, specials: SpecialDamage.DOUBLE));

        Assert.Equal(4, snapshot.Smite.N);
        Assert.Equal(3, snapshot.Smite.Hits);
        Assert.Equal(snapshot.Smite.N, snapshot.Smite.Bins.Sum(b => b[2]));
        Assert.Equal(snapshot.Smite.Hits, snapshot.Smite.Bins.Sum(b => b[3]));
    }

    /// <summary>강타 buckets are 2 percentage points wide, floored. 50.00% and 51.99% share a bucket; 99.00%
    /// does not join them.</summary>
    [Fact]
    public void SmiteBucketsAreTwoPercentagePointsWide()
    {
        SelfJudgmentSnapshot snapshot = Build(
            Hit(hardHitBp: 5_000), Hit(hardHitBp: 5_199), Hit(hardHitBp: 9_900));

        Assert.Equal([50, 98], snapshot.Smite.Bins.Select(b => b[0]).OrderBy(v => v).ToArray());
        Assert.Equal(2, snapshot.Smite.Bins.Single(b => b[0] == 50)[2]);
    }

    /// <summary>A negative stat must floor DOWNWARD. C#'s integer division truncates toward zero, which would
    /// put −0.5%p in the 0 bucket — and the sub-zero region is precisely where the subtractive model clamps,
    /// i.e. the region these bins exist to resolve.</summary>
    [Fact]
    public void NegativeStatFloorsDownwardNotTowardZero()
    {
        SelfJudgmentSnapshot snapshot = Build(Hit(hardHitBp: -50));

        Assert.Equal(-2, snapshot.Smite.Bins.Single()[0]);
    }

    /// <summary>Switch-type-4 hits carry no judgment region, so 강타/완벽/막기 are structurally unmeasurable on
    /// them — they must stay out of every denominator. Crit rides a separate field and IS present there, so it
    /// is counted, but into its own bucket that must never be merged with the flagged population.</summary>
    [Fact]
    public void UnflaggedHitsAreExcludedFromEveryAxisButCountedForCritSeparately()
    {
        SelfJudgmentSnapshot snapshot = Build(
            Hit(switchVariable: Unflagged, crit: true),
            Hit(switchVariable: Unflagged),
            Hit(specials: SpecialDamage.DOUBLE));

        Assert.Equal(1, snapshot.EligibleHits);
        Assert.Equal(1, snapshot.Smite.N);
        Assert.Equal(1, snapshot.Crit.N);
        Assert.Equal(2, snapshot.CritSw4Hits);
        Assert.Equal(1, snapshot.CritSw4Crits);
    }

    /// <summary>A hit that arrived before any stat frame counts toward eligibility but not toward any axis —
    /// the coverage ratio is how the server judges whether a battle's reading is usable. Reading the absent
    /// stat as 0 would instead report a player with no 강타 at all.</summary>
    [Fact]
    public void UnstampedHitsCountAsEligibleButNotAsObservations()
    {
        SelfJudgmentSnapshot snapshot = Build(
            Hit(mask: 0, specials: SpecialDamage.DOUBLE),
            Hit(specials: SpecialDamage.DOUBLE));

        Assert.Equal(2, snapshot.EligibleHits);
        Assert.Equal(1, snapshot.Smite.N);
        Assert.Equal(1, snapshot.Smite.Hits);
    }

    /// <summary>A partially-populated sheet contributes to the axes it does have and to none of the others.
    /// Anything else would silently pair one axis's observations with another axis's population.</summary>
    [Fact]
    public void PartialStampFeedsOnlyTheAxesItCarries()
    {
        SelfJudgmentSnapshot snapshot = Build(
            Hit(mask: JudgmentStatStamp.HasAccuracy, specials: SpecialDamage.PARRY));

        Assert.Equal(1, snapshot.Accuracy.N);
        Assert.Equal(1, snapshot.Accuracy.Hits);
        Assert.Equal(0, snapshot.Smite.N);
        Assert.Equal(0, snapshot.Perfect.N);
        Assert.Equal(0, snapshot.Crit.N);
    }

    /// <summary>Crit needs BOTH terms: composing the rating from 기본 치명타 alone understates it by the whole
    /// 치명타 증가율 (measured ~1.6x), which reads as a far worse crit build than the player had.</summary>
    [Fact]
    public void CritAxisRequiresBothRatingTerms()
    {
        SelfJudgmentSnapshot onlyBase = Build(Hit(mask: JudgmentStatStamp.HasCritical, crit: true));
        Assert.Equal(0, onlyBase.Crit.N);

        SelfJudgmentSnapshot both = Build(Hit(
            mask: JudgmentStatStamp.HasCritical | JudgmentStatStamp.HasCriticalInc,
            criticalBp: 2_000, criticalIncBp: 5_000, crit: true));
        Assert.Equal(1, both.Crit.N);
        Assert.Equal(3_000, both.Crit.Bins.Single()[0]); // 2,000 × 1.5 = 3,000 → 100-point bucket floor
    }

    /// <summary>Facing is a bin key, not something to average over: block is impossible from behind and the
    /// 강타 residual differs on position-less hits. Folding the positions together destroys both.</summary>
    [Fact]
    public void PositionSplitsBinsRatherThanBeingAveraged()
    {
        SelfJudgmentSnapshot snapshot = Build(
            Hit(position: 2, accuracy: 1_400, specials: SpecialDamage.PARRY),
            Hit(position: 1, accuracy: 1_400));

        Assert.Equal(2, snapshot.Accuracy.Bins.Count);
        Assert.Equal(1, snapshot.Accuracy.Bins.Single(b => b[1] == 2)[3]);
        Assert.Equal(0, snapshot.Accuracy.Bins.Single(b => b[1] == 1)[3]);
    }

    /// <summary>명중 buckets are 25 points wide — the resolution the cut is quoted at.</summary>
    [Fact]
    public void AccuracyBucketsAreTwentyFivePointsWide()
    {
        SelfJudgmentSnapshot snapshot = Build(
            Hit(accuracy: 1_400), Hit(accuracy: 1_424), Hit(accuracy: 1_425));

        Assert.Equal([1_400, 1_425], snapshot.Accuracy.Bins.Select(b => b[0]).OrderBy(v => v).ToArray());
    }

    /// <summary>Per-skill block counts feed the server's learning of which skills ignore block entirely. Every
    /// eligible hit lands in that table regardless of whether the 명중 stat was readable — the skill split is
    /// about the skill, not about our stat coverage.</summary>
    [Fact]
    public void BlockCountsAreTrackedPerSkill()
    {
        SelfJudgmentSnapshot snapshot = Build(
            Hit(rawSkillCode: 11_110_000, specials: SpecialDamage.PARRY),
            Hit(rawSkillCode: 11_110_000),
            Hit(rawSkillCode: 11_220_000));

        int[] first = snapshot.AccuracyBySkill.Single(r => r[0] == 11_110_000);
        Assert.Equal(2, first[1]);
        Assert.Equal(1, first[2]);
        Assert.Equal(0, snapshot.AccuracyBySkill.Single(r => r[0] == 11_220_000)[2]);
    }

    /// <summary>DoT ticks carry no judgment at all and must not reach any counter.</summary>
    [Fact]
    public void DotTicksAreIgnoredEntirely()
    {
        var dot = Hit(specials: SpecialDamage.DOUBLE);
        dot.Dot = true;
        SelfJudgmentSnapshot snapshot = Build(dot);

        Assert.Equal(0, snapshot.EligibleHits);
        Assert.Equal(0, snapshot.Smite.N);
    }

    /// <summary>Hits past ten minutes of combat go to a separate table. One client effect drops a target's
    /// 강타 저항 by 100%p after that point, which would otherwise change the answer mid-battle with no way to
    /// see it had happened.</summary>
    [Fact]
    public void HitsPastTenMinutesGoToLateBins()
    {
        var acc = new SelfJudgmentAccumulator();
        acc.Accumulate(Hit(timestamp: 60_000L, stampAt: 60_000L, specials: SpecialDamage.DOUBLE), battleStart: 1_000L);
        acc.Accumulate(Hit(timestamp: 700_000L, stampAt: 700_000L, specials: SpecialDamage.DOUBLE), battleStart: 1_000L);
        SelfJudgmentSnapshot snapshot = acc.Build(1, null);

        Assert.Single(snapshot.Smite.Bins);
        Assert.Single(snapshot.Smite.LateBins);
        Assert.Equal(2, snapshot.Smite.N); // both still counted in the axis total
    }

    /// <summary>Staleness is measured, not assumed. A buff lands before the sheet reports it, so some hits
    /// carry a pre-buff stat; the server needs the fresh subset to size that bias rather than guess it.</summary>
    [Fact]
    public void StampAgeIsMeasuredAndFreshHitsCounted()
    {
        var acc = new SelfJudgmentAccumulator();
        acc.Accumulate(Hit(timestamp: 10_000L, stampAt: 9_900L), battleStart: 0L);  // 100 ms — fresh
        acc.Accumulate(Hit(timestamp: 10_000L, stampAt: 6_000L), battleStart: 0L);  // 4 s — stale
        acc.Accumulate(Hit(timestamp: 10_000L, stampAt: 6_000L), battleStart: 0L);
        SelfJudgmentSnapshot snapshot = acc.Build(1, null);

        Assert.Equal(1, snapshot.FreshHits);
        Assert.Equal(4_000, snapshot.StampAgeP50Ms);
    }

    /// <summary>Clearing must be total. The accumulator shares the skill cache's lifecycle precisely because a
    /// divergence there is invisible — see <see cref="BinCountsSumToAxisTotals"/>.</summary>
    [Fact]
    public void ClearResetsEveryCounter()
    {
        var acc = new SelfJudgmentAccumulator();
        acc.Accumulate(Hit(specials: SpecialDamage.DOUBLE), battleStart: 0L);
        acc.Accumulate(Hit(switchVariable: Unflagged, crit: true), battleStart: 0L);
        acc.Clear();
        SelfJudgmentSnapshot snapshot = acc.Build(1, null);

        Assert.Equal(0, snapshot.EligibleHits);
        Assert.Equal(0, snapshot.Smite.N);
        Assert.Equal(0, snapshot.CritSw4Hits);
        Assert.Empty(snapshot.Smite.Bins);
    }

    /// <summary>A sheet the meter never saw leaves the static scalars ABSENT rather than zero — "0 막기 관통"
    /// and "we never captured it" must stay distinguishable, or the server averages over a population that
    /// silently includes unknowns.</summary>
    [Fact]
    public void StaticStatsDistinguishAbsentFromZero()
    {
        SelfJudgmentSnapshot none = Build(Hit());
        Assert.Equal("none", none.Stats.Source);
        Assert.Equal(0, none.Stats.Mask);

        var sheet = new PlayerStatSheet(
            new Dictionary<int, int> { [PlayerStatIds.BlockPierce] = 0, [PlayerStatIds.PveAccuracy] = 320 },
            UpdatedAt: 1L,
            FullSnapshotSeen: true);
        var acc = new SelfJudgmentAccumulator();
        acc.Accumulate(Hit(), battleStart: 0L);
        SelfJudgmentSnapshot present = acc.Build(1, sheet);

        Assert.Equal("full", present.Stats.Source);
        Assert.True((present.Stats.Mask & SelfJudgmentStats.HasBlockPierce) != 0); // present AND zero
        Assert.Equal(0, present.Stats.BlockPierce);
        Assert.True((present.Stats.Mask & SelfJudgmentStats.HasWeaponAccuracy) == 0); // genuinely absent
    }

    /// <summary>
    /// Pins each presence bit to the stat it stands for. The server gates its display-axis rollup on
    /// <c>mask &amp; 15 == 15</c> (the four 명중컷 terms), so reordering these constants would silently change
    /// WHICH blocks that axis keeps — and the blocks it drops are not a random subset, which is the whole
    /// reason that gate was moved off <c>src</c> in the first place.
    /// <para>A fully-populated sheet gives mask 127 and cannot distinguish any ordering; this asserts the bits
    /// one at a time, which is the only form that can fail.</para>
    /// </summary>
    [Fact]
    public void StatMaskBitsArePinnedToTheirStatIds()
    {
        Assert.Equal(1, SelfJudgmentStats.HasWeaponAccuracy);   // 318 무기 명중
        Assert.Equal(2, SelfJudgmentStats.HasPveAccuracy);      // 110 PvE 명중
        Assert.Equal(4, SelfJudgmentStats.HasAccuracyInc);      // 427 명중 증가율
        Assert.Equal(8, SelfJudgmentStats.HasBlockPierce);      // 256 막기 관통
        Assert.Equal(16, SelfJudgmentStats.HasCriticalInc);     // 429 치명타 증가율
        Assert.Equal(32, SelfJudgmentStats.HasBackCritical);    // 100 후방 치명타
        Assert.Equal(64, SelfJudgmentStats.HasFrontCritical);   // 591 전방 치명타

        // Each id alone must light exactly its own bit — a swap between two of them survives the "all present"
        // case and every aggregate check, and shows up only here.
        AssertOnly(PlayerStatIds.WeaponAccuracy, SelfJudgmentStats.HasWeaponAccuracy);
        AssertOnly(PlayerStatIds.PveAccuracy, SelfJudgmentStats.HasPveAccuracy);
        AssertOnly(PlayerStatIds.AccuracyIncreasePercent, SelfJudgmentStats.HasAccuracyInc);
        AssertOnly(PlayerStatIds.BlockPierce, SelfJudgmentStats.HasBlockPierce);
        AssertOnly(PlayerStatIds.CriticalIncreasePercent, SelfJudgmentStats.HasCriticalInc);
        AssertOnly(PlayerStatIds.BackCritical, SelfJudgmentStats.HasBackCritical);
        AssertOnly(PlayerStatIds.FrontCritical, SelfJudgmentStats.HasFrontCritical);

        // The four terms the display axis needs, and nothing else, must be exactly 15.
        var acc = new SelfJudgmentAccumulator();
        acc.Accumulate(Hit(), battleStart: 0L);
        SelfJudgmentSnapshot four = acc.Build(1, new PlayerStatSheet(
            new Dictionary<int, int>
            {
                [PlayerStatIds.WeaponAccuracy] = 391,
                [PlayerStatIds.PveAccuracy] = 320,
                [PlayerStatIds.AccuracyIncreasePercent] = 5_350,
                [PlayerStatIds.BlockPierce] = 210,
            },
            UpdatedAt: 1L, FullSnapshotSeen: false));
        Assert.Equal(15, four.Stats.Mask);

        static void AssertOnly(int statId, int expectedBit)
        {
            var probe = new SelfJudgmentAccumulator();
            probe.Accumulate(Hit(), battleStart: 0L);
            SelfJudgmentSnapshot snapshot = probe.Build(1, new PlayerStatSheet(
                new Dictionary<int, int> { [statId] = 1 }, UpdatedAt: 1L, FullSnapshotSeen: false));
            Assert.Equal(expectedBit, snapshot.Stats.Mask);
        }
    }

    /// <summary>
    /// Row widths are a load-bearing contract, not an implementation detail: the server's validator drops the
    /// WHOLE judgment block when a bins row is shorter than 4, and reads only the first four columns when it is
    /// longer. So widening a row is a compatible change and narrowing one is a silent total loss — this test is
    /// the thing that makes that asymmetry visible to whoever edits the accumulator next.
    /// <para>Accepted ranges on the server side: bins 4-8, bySkill 3-8, sw4 2-4.</para>
    /// </summary>
    [Fact]
    public void RowWidthsMatchTheServerContract()
    {
        var acc = new SelfJudgmentAccumulator();
        acc.Accumulate(Hit(specials: SpecialDamage.DOUBLE), battleStart: 0L);
        acc.Accumulate(Hit(specials: SpecialDamage.PARRY), battleStart: 0L);
        acc.Accumulate(Hit(timestamp: 700_000L, stampAt: 700_000L), battleStart: 1_000L);
        acc.Accumulate(Hit(switchVariable: Unflagged, crit: true), battleStart: 0L);
        // Force the bySkill aggregate rows, which are built by a different code path than the ordinary ones.
        for (int i = 0; i < 20; i++) acc.Accumulate(Hit(rawSkillCode: 12_000_000 + i), battleStart: 0L);
        SelfJudgmentSnapshot snapshot = acc.Build(1, null);

        Assert.All(snapshot.Smite.Bins, row => Assert.Equal(4, row.Length));
        Assert.All(snapshot.Smite.LateBins, row => Assert.Equal(4, row.Length));
        Assert.All(snapshot.Perfect.Bins, row => Assert.Equal(4, row.Length));
        Assert.All(snapshot.Perfect.LateBins, row => Assert.Equal(4, row.Length));
        Assert.All(snapshot.Accuracy.Bins, row => Assert.Equal(4, row.Length));
        Assert.All(snapshot.Crit.Bins, row => Assert.Equal(4, row.Length));
        Assert.All(snapshot.AccuracyBySkill, row => Assert.Equal(3, row.Length));
        Assert.NotEmpty(snapshot.Smite.LateBins);
        Assert.NotEmpty(snapshot.AccuracyBySkill);
    }

    /// <summary>Trimming must announce itself. Silent truncation reads downstream as "this player never used
    /// those skills", which is the opposite of what happened.</summary>
    [Fact]
    public void TrimmingIsReportedAndPreservesTotals()
    {
        var acc = new SelfJudgmentAccumulator();
        // 20 rarely-used skills that never blocked, plus one that did — the blocked one must survive.
        for (int i = 0; i < 20; i++) acc.Accumulate(Hit(rawSkillCode: 12_000_000 + i), battleStart: 0L);
        acc.Accumulate(Hit(rawSkillCode: 19_999_999, specials: SpecialDamage.PARRY), battleStart: 0L);
        SelfJudgmentSnapshot snapshot = acc.Build(1, null);

        Assert.Contains("accuracyBySkill", snapshot.Trimmed);
        Assert.Contains(snapshot.AccuracyBySkill, r => r[0] == 19_999_999);
        // Trimmed rows are folded into an aggregate, never dropped: the denominator must still add up.
        Assert.Equal(21, snapshot.AccuracyBySkill.Sum(r => r[1]));
        Assert.Equal(1, snapshot.AccuracyBySkill.Sum(r => r[2]));
    }
}

/// <summary>
/// Guards the three hand-written copy surfaces for <see cref="AnalyzedSkill"/>. Adding a counter and forgetting
/// one of them produces a battle that is correct in the common case and zeroed in the rare one (a saved report,
/// or a character whose uid split across a zone boundary) — a failure mode this repository has already shipped
/// once, with per-skill rates that the web silently dropped for months.
/// </summary>
public sealed class AnalyzedSkillCopyGuardTests
{
    [Fact]
    public void CopyCarriesEveryProperty()
    {
        PropertyInfo[] properties = typeof(AnalyzedSkill)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite || p.SetMethod?.IsPublic == true || p.Name == nameof(AnalyzedSkill.SkillCode))
            .ToArray();

        var source = new AnalyzedSkill { SkillCode = 7 };
        int seed = 1;
        foreach (PropertyInfo property in properties)
        {
            if (!property.CanWrite) continue;
            if (property.PropertyType == typeof(int)) property.SetValue(source, seed++);
            else if (property.PropertyType == typeof(long)) property.SetValue(source, (long)seed++);
            else if (property.PropertyType == typeof(string)) property.SetValue(source, "n" + seed++);
        }

        AnalyzedSkill copy = source.Copy();
        foreach (PropertyInfo property in properties)
        {
            Assert.Equal(property.GetValue(source), property.GetValue(copy));
        }
    }
}
