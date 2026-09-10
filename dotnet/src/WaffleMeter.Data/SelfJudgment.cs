using WaffleMeter.Capture;

namespace WaffleMeter.Data;

/// <summary>
/// The uploader's own judgment observations for one battle, as the cross-tabs a statistical estimator needs.
///
/// <para><b>What problem this solves.</b> A boss's 강타 저항 / 막기(명중컷) / 치명타 저항 are not on the wire and
/// not in the client — the server keeps them. They can only be inferred from "what proc rate did a player with
/// a KNOWN stat actually observe against this boss". The observed rate alone is not enough: the model is
/// subtractive (<c>관측 = clamp(내 스탯 − 대상 저항, 0, 1)</c>), so without the attacker's stat the equation has
/// two unknowns. The stat dictionary is broadcast for the local player ONLY, which is why this block exists at
/// all and why it covers the uploader alone.</para>
///
/// <para><b>Why cross-tabs and not a mean.</b> The stat moves a lot inside one battle — a measured session ran
/// 강타 from 52.44% to 103.44% as party buffs came and went. A per-battle average would be biased, because hits
/// are not spread evenly over that range, and it would also hand the server a single number that IS the
/// player's stat. A histogram keyed by the stat at hit time is both unbiased and coarser: it says "82-84%
/// bucket: 300 hits, 154 procs", which is what the estimator consumes and nothing more.</para>
///
/// <para><b>Why the bins carry position.</b> Facing changes two of the three axes. Back attacks cannot be
/// blocked at all (measured 0 of 267,357; the client says so outright), and the 강타 residual differs by ~9%p
/// on position-less hits for reasons still unexplained. Keeping position as a bin key lets the estimator
/// absorb both instead of averaging over them.</para>
/// </summary>
public sealed class SelfJudgmentSnapshot
{
    /// <summary>The boss this block describes, mirrored from the report's target so the server can reject a
    /// block that got attached to the wrong battle by a merge.</summary>
    public int TargetMobCode { get; set; }

    /// <summary>Self direct hits that could carry a judgment at all — non-DoT, not folded in from a summon,
    /// switch-type 5/6/7. The single shared denominator for every axis; <c>n</c> per axis is this minus the
    /// hits that had no stat stamp yet, so <c>n / EligibleHits</c> is the coverage of the reading.</summary>
    public int EligibleHits { get; set; }

    /// <summary>Median age (ms) of the stat reading stamped onto a hit — how stale the sheet was. The server
    /// uses it to bound a known bias: a buff lands before the sheet reports it, so some hits are stamped with a
    /// pre-buff (too low) stat, which pulls the estimated resistance DOWN.</summary>
    public int StampAgeP50Ms { get; set; }

    /// <summary>Hits whose stat reading was at most 500 ms old. A subset large enough to re-fit on measures
    /// that bias directly instead of assuming it.</summary>
    public int FreshHits { get; set; }

    /// <summary>강타 — bins are <c>[pct2, pos, n, o]</c>, pct2 = the 2%p bucket floor of stat 443.</summary>
    public JudgmentAxisSnapshot Smite { get; set; } = new();

    /// <summary>완벽 — same shape, stat 442. Its true resistance is 0 on every target measured so far, which
    /// makes this axis a placebo channel: whatever residual it shows is the meter's own systematic error, and
    /// the same error scaled by the two stats' swing ratio applies to 강타.</summary>
    public JudgmentAxisSnapshot Perfect { get; set; } = new();

    /// <summary>막기 — bins are <c>[acc25, pos, n, o]</c>, acc25 = the 25-point bucket floor of stat 104
    /// (추가 명중), o = hits the target blocked. <c>pos == 1</c> (back) rows should be empty; a non-zero one is
    /// evidence the game's rules changed.</summary>
    public JudgmentAxisSnapshot Accuracy { get; set; } = new();

    /// <summary>Per-skill block counts, <c>[rawSkillCode, n, o]</c>. Some skills ignore block entirely (the
    /// client marks 343 of them), and mixing those into the denominator understates the block rate. The server
    /// learns which is which from these counts rather than the meter shipping a catalog that goes stale.
    /// <para>Two aggregate rows may appear for what was trimmed: code 0 = trimmed rows that never blocked,
    /// code -1 = trimmed rows that did.</para></summary>
    public List<int[]> AccuracyBySkill { get; set; } = [];

    /// <summary>치명타 — bins are <c>[rating100, rawSkillCode, n, o]</c>, rating100 = the 100-point bucket floor
    /// of <c>기본 치명타 × (1 + 치명타 증가율)</c>.
    /// <para>The skill code is a bin key here (it is not on the other axes) because guaranteed-crit skills must
    /// be separated — they are 13.6% of boss direct hits and would push the rate above the cap — and because
    /// below the cap the crit rate differs by skill (chi2/df 2.96, versus 0.63 above it).</para></summary>
    public JudgmentAxisSnapshot Crit { get; set; } = new();

    /// <summary>Crit on switch-type-4 hits (the ones with no judgment region), kept apart from
    /// <see cref="Crit"/> and never to be merged with it. Two independent measurements of this population
    /// disagree with each other (40.8% vs 63.0%) and both differ structurally from the 80% the flagged hits
    /// sit at — evidence it is a different regime, not noise. Carried so the decision to exclude it stays
    /// falsifiable.</summary>
    public int CritSw4Hits { get; set; }

    public int CritSw4Crits { get; set; }

    /// <summary>Stats that do not move within a battle, sent once. See <see cref="SelfJudgmentStats"/>.</summary>
    public SelfJudgmentStats Stats { get; set; } = new();

    /// <summary>Which sections were shortened to fit the size budget, comma-separated; null when nothing was.
    /// Without this the server cannot tell "this player had no such hits" from "we dropped the rows".</summary>
    public string? Trimmed { get; set; }
}

/// <summary>One axis's counts. <see cref="N"/> is hits with a usable stat reading, <see cref="Hits"/> is how
/// many of them showed the judgment, and <see cref="Bins"/> is the same pair split by stat bucket — so
/// <c>sum(bins.n) == N</c> and <c>sum(bins.o) == Hits</c> always hold. A violation means the accumulator
/// double-counted (a cache replay that did not clear), which is otherwise symptomless: the ratio stays right
/// while the confidence interval silently narrows.</summary>
public sealed class JudgmentAxisSnapshot
{
    public int N { get; set; }

    public int Hits { get; set; }

    public List<int[]> Bins { get; set; } = [];

    /// <summary>Same shape, but only hits past 600 s of combat. Empty in every fight measured so far (the
    /// longest was 230.6 s), so it costs nothing — it exists because one client effect drops a target's 강타
    /// 저항 by 100%p after ten minutes of combat, which would silently change the answer mid-battle if a
    /// long-form encounter ever ships.</summary>
    public List<int[]> LateBins { get; set; } = [];
}

/// <summary>
/// The judgment-adjacent stats that are constant within a battle, sent once per report.
///
/// <para>These are the terms players add together when they say "명중컷", plus the crit scaling term. The
/// meter cannot tell whether they enter the game's block calculation at all — within one character they never
/// change, so their coefficient is unidentifiable locally and gets absorbed into the cut. The server can
/// identify them across a population that varies them, which is the only reason they are sent.</para>
///
/// <para><see cref="Source"/> matters more than it looks. 318/110/256 ride reliably only on a FULL sheet
/// (0x3649), which the game sends on character/zone load — a session where the meter started after the game
/// has them missing, and that is not missing-at-random (it correlates with how the player launches things).
/// The server must weight on this rather than treat an absent value as zero.</para>
/// </summary>
public sealed class SelfJudgmentStats
{
    /// <summary><c>full</c> when a full sheet was folded in, <c>delta</c> when only incremental frames were
    /// seen, <c>none</c> when nothing was captured.</summary>
    public string Source { get; set; } = "none";

    /// <summary>Presence bits — see the <c>Has*</c> constants. Distinguishes "0" from "not captured", which
    /// no single value can.</summary>
    public int Mask { get; set; }

    public const int HasWeaponAccuracy = 1 << 0;
    public const int HasPveAccuracy = 1 << 1;
    public const int HasAccuracyInc = 1 << 2;
    public const int HasBlockPierce = 1 << 3;
    public const int HasCriticalInc = 1 << 4;
    public const int HasBackCritical = 1 << 5;
    public const int HasFrontCritical = 1 << 6;

    /// <summary>무기 명중 (318).</summary>
    public int WeaponAccuracy { get; set; }

    /// <summary>PvE 명중 (110).</summary>
    public int PveAccuracy { get; set; }

    /// <summary>명중 증가율 (427), basis points.</summary>
    public int AccuracyIncBp { get; set; }

    /// <summary>막기 관통 (256). ⚠️ Not 철벽 관통 (449) — that one was measured and rejected as a term in the
    /// block calculation (chi2 50.9).</summary>
    public int BlockPierce { get; set; }

    /// <summary>치명타 증가율 (429), basis points.</summary>
    public int CriticalIncBp { get; set; }

    /// <summary>후방 치명타 (100).</summary>
    public int BackCritical { get; set; }

    /// <summary>전방 치명타 (591).</summary>
    public int FrontCritical { get; set; }
}

/// <summary>
/// Accumulates <see cref="SelfJudgmentSnapshot"/> while a battle runs.
///
/// <para>Lives beside the skill-detail cache and shares its lifecycle exactly: cleared by the same reset, fed
/// by the same replayed packet window. If it were cleared on a different schedule a cache replay would count
/// every hit twice, which does not change any ratio and so produces no visible symptom — only a coverage above
/// 1 and a confidence interval narrower than the evidence supports.</para>
/// </summary>
public sealed class SelfJudgmentAccumulator
{
    /// <summary>Hits past this point in the battle go to <c>lateBins</c> instead.</summary>
    private const long LateThresholdMs = 600_000L;

    /// <summary>Stat readings at most this old count as fresh.</summary>
    private const long FreshThresholdMs = 500L;

    private const int AgeBucketMs = 100;
    private const int AgeBucketCount = 52; // 0..50 = 100 ms each up to 5 s, 51 = older

    private readonly Dictionary<long, int[]> _smite = new();
    private readonly Dictionary<long, int[]> _smiteLate = new();
    private readonly Dictionary<long, int[]> _perfect = new();
    private readonly Dictionary<long, int[]> _perfectLate = new();
    private readonly Dictionary<long, int[]> _accuracy = new();
    private readonly Dictionary<int, int[]> _accuracyBySkill = new();
    private readonly Dictionary<long, int[]> _crit = new();
    private readonly int[] _ageBuckets = new int[AgeBucketCount];

    private int _eligibleHits;
    private int _freshHits;
    private int _stampedHits;
    private int _critSw4Hits;
    private int _critSw4Crits;

    public void Clear()
    {
        _smite.Clear();
        _smiteLate.Clear();
        _perfect.Clear();
        _perfectLate.Clear();
        _accuracy.Clear();
        _accuracyBySkill.Clear();
        _crit.Clear();
        Array.Clear(_ageBuckets);
        _eligibleHits = 0;
        _freshHits = 0;
        _stampedHits = 0;
        _critSw4Hits = 0;
        _critSw4Crits = 0;
    }

    /// <summary>Fold in one of the executor's own direct hits. The caller must have established that this
    /// packet is the local player's OWN un-folded damage — a summon's hit attributed to its owner carries the
    /// owner's stat stamp but the summon's proc roll, and pairing those would corrupt every axis.</summary>
    public void Accumulate(ParsedDamagePacket packet, long battleStart)
    {
        if (packet.Dot) return;

        bool flagged = (packet.SwitchVariable & 0x0F) is 5 or 6 or 7;
        if (!flagged)
        {
            // switch-type 4 carries no judgment region, so 강타/완벽/막기 are structurally unmeasurable on it —
            // but crit rides a separate field that IS present. Kept apart, never merged.
            _critSw4Hits++;
            if (packet.IsCrit) _critSw4Crits++;
            return;
        }

        _eligibleHits++;

        JudgmentStatStamp stamp = packet.JudgmentStats;
        if (!stamp.Stamped) return;

        _stampedHits++;

        long age = packet.Timestamp > 0L && stamp.At > 0L ? Math.Max(packet.Timestamp - stamp.At, 0L) : 0L;
        if (age <= FreshThresholdMs) _freshHits++;
        _ageBuckets[(int)Math.Min(age / AgeBucketMs, AgeBucketCount - 1)]++;

        bool late = battleStart > 0L && packet.Timestamp - battleStart >= LateThresholdMs;
        int pos = packet.Position;

        if (stamp.Has(JudgmentStatStamp.HasHardHit))
        {
            Bump(late ? _smiteLate : _smite, Key(Bucket2Pp(stamp.HardHitBp), pos),
                packet.Specials.Contains(SpecialDamage.DOUBLE));
        }

        if (stamp.Has(JudgmentStatStamp.HasPerfect))
        {
            Bump(late ? _perfectLate : _perfect, Key(Bucket2Pp(stamp.PerfectBp), pos),
                packet.Specials.Contains(SpecialDamage.PERFECT));
        }

        bool parried = packet.Specials.Contains(SpecialDamage.PARRY);
        if (stamp.Has(JudgmentStatStamp.HasAccuracy))
        {
            Bump(_accuracy, Key(FloorBucket(stamp.Accuracy, 25), pos), parried);
        }

        int rawSkill = packet.RawSkillCode != 0 ? packet.RawSkillCode : packet.SkillCode;
        Bump(_accuracyBySkill, rawSkill, parried);

        if (stamp.CriticalRating is { } rating)
        {
            Bump(_crit, Key(FloorBucket(rating, 100), rawSkill), packet.IsCrit);
        }
    }

    /// <summary>Freeze what was accumulated. <paramref name="sheet"/> supplies the static scalars; a null sheet
    /// leaves them absent rather than zero.</summary>
    public SelfJudgmentSnapshot Build(int targetMobCode, PlayerStatSheet? sheet)
    {
        var snapshot = new SelfJudgmentSnapshot
        {
            TargetMobCode = targetMobCode,
            EligibleHits = _eligibleHits,
            FreshHits = _freshHits,
            StampAgeP50Ms = MedianAgeMs(),
            CritSw4Hits = _critSw4Hits,
            CritSw4Crits = _critSw4Crits,
            Smite = Axis(_smite, _smiteLate),
            Perfect = Axis(_perfect, _perfectLate),
            Accuracy = Axis(_accuracy, null),
            Crit = Axis(_crit, null),
            AccuracyBySkill = BySkillRows(),
            Stats = BuildStats(sheet),
        };

        snapshot.Trimmed = Trim(snapshot);
        return snapshot;
    }

    // ---- accumulation helpers ----

    private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

    private static int KeyHigh(long key) => (int)(key >> 32);

    private static int KeyLow(long key) => (int)(uint)key;

    /// <summary>Basis points to the floor of its 2-percentage-point bucket. <c>Math.Floor</c> on a double, not
    /// integer division: C# truncates toward zero, so a negative stat (the client allows down to −100%p) would
    /// bucket to the wrong side — and that is exactly the region where the subtractive model clamps, which is
    /// the region the bins exist to resolve.</summary>
    private static int Bucket2Pp(int basisPoints) => (int)Math.Floor(basisPoints / 200.0) * 2;

    private static int FloorBucket(int value, int size) => (int)Math.Floor((double)value / size) * size;

    private static void Bump<TKey>(Dictionary<TKey, int[]> bins, TKey key, bool observed) where TKey : notnull
    {
        if (!bins.TryGetValue(key, out int[]? cell))
        {
            cell = new int[2];
            bins[key] = cell;
        }

        cell[0]++;
        if (observed) cell[1]++;
    }

    private int MedianAgeMs()
    {
        if (_stampedHits == 0) return 0;

        int half = (_stampedHits + 1) / 2;
        int running = 0;
        for (int i = 0; i < AgeBucketCount; i++)
        {
            running += _ageBuckets[i];
            if (running >= half) return i * AgeBucketMs;
        }

        return (AgeBucketCount - 1) * AgeBucketMs;
    }

    private static JudgmentAxisSnapshot Axis(Dictionary<long, int[]> bins, Dictionary<long, int[]>? lateBins)
    {
        var axis = new JudgmentAxisSnapshot
        {
            Bins = Rows(bins),
            LateBins = lateBins is null ? [] : Rows(lateBins),
        };

        foreach (int[] row in axis.Bins)
        {
            axis.N += row[2];
            axis.Hits += row[3];
        }

        foreach (int[] row in axis.LateBins)
        {
            axis.N += row[2];
            axis.Hits += row[3];
        }

        return axis;
    }

    private static List<int[]> Rows(Dictionary<long, int[]> bins)
    {
        var rows = new List<int[]>(bins.Count);
        foreach ((long key, int[] cell) in bins)
        {
            rows.Add([KeyHigh(key), KeyLow(key), cell[0], cell[1]]);
        }

        rows.Sort(static (a, b) => a[0] != b[0] ? a[0].CompareTo(b[0]) : a[1].CompareTo(b[1]));
        return rows;
    }

    private List<int[]> BySkillRows()
    {
        var rows = new List<int[]>(_accuracyBySkill.Count);
        foreach ((int skill, int[] cell) in _accuracyBySkill)
        {
            rows.Add([skill, cell[0], cell[1]]);
        }

        rows.Sort(static (a, b) => b[1].CompareTo(a[1]));
        return rows;
    }

    private static SelfJudgmentStats BuildStats(PlayerStatSheet? sheet)
    {
        var stats = new SelfJudgmentStats();
        if (sheet is null) return stats;

        stats.Source = sheet.FullSnapshotSeen ? "full" : "delta";
        Take(sheet, PlayerStatIds.WeaponAccuracy, SelfJudgmentStats.HasWeaponAccuracy, v => stats.WeaponAccuracy = v);
        Take(sheet, PlayerStatIds.PveAccuracy, SelfJudgmentStats.HasPveAccuracy, v => stats.PveAccuracy = v);
        Take(sheet, PlayerStatIds.AccuracyIncreasePercent, SelfJudgmentStats.HasAccuracyInc, v => stats.AccuracyIncBp = v);
        Take(sheet, PlayerStatIds.BlockPierce, SelfJudgmentStats.HasBlockPierce, v => stats.BlockPierce = v);
        Take(sheet, PlayerStatIds.CriticalIncreasePercent, SelfJudgmentStats.HasCriticalInc, v => stats.CriticalIncBp = v);
        Take(sheet, PlayerStatIds.BackCritical, SelfJudgmentStats.HasBackCritical, v => stats.BackCritical = v);
        Take(sheet, PlayerStatIds.FrontCritical, SelfJudgmentStats.HasFrontCritical, v => stats.FrontCritical = v);
        return stats;

        void Take(PlayerStatSheet s, int statId, int flag, Action<int> assign)
        {
            if (s.Raw(statId) is not { } value) return;
            assign(value);
            stats.Mask |= flag;
        }
    }

    // ---- trimming ----

    /// <summary>Rough serialized size of the block, in bytes. Deliberately an estimate: the point is to stay
    /// far under the transport limit, not to predict it.</summary>
    private const int SizeBudgetBytes = 24_000;

    private const int BytesPerInt = 7;

    private const int BySkillKeepTop = 8;
    private const int BySkillKeepMinHits = 50;

    private static string? Trim(SelfJudgmentSnapshot snapshot)
    {
        var trimmed = new List<string>();

        // Always applied: the per-skill block table is the one section that grows with rotation breadth rather
        // than with stat range, and most of its rows carry no information (a skill that never blocked and was
        // barely used says nothing). Keep every row that DID block — those are the learning signal — plus the
        // heavily-used ones, and fold the rest into two aggregate rows so their hits stay in the denominator.
        if (TrimBySkill(snapshot)) trimmed.Add("accuracyBySkill");

        if (EstimateBytes(snapshot) > SizeBudgetBytes)
        {
            snapshot.Crit.Bins = MergeBins(snapshot.Crit.Bins, 200, keyIndex: 0);
            trimmed.Add("critBins");
        }

        if (EstimateBytes(snapshot) > SizeBudgetBytes)
        {
            snapshot.Accuracy.Bins = MergeBins(snapshot.Accuracy.Bins, 50, keyIndex: 0);
            trimmed.Add("accuracyBins");
        }

        if (EstimateBytes(snapshot) > SizeBudgetBytes)
        {
            snapshot.Smite.Bins = MergeBins(snapshot.Smite.Bins, 4, keyIndex: 0);
            snapshot.Perfect.Bins = MergeBins(snapshot.Perfect.Bins, 4, keyIndex: 0);
            trimmed.Add("smiteBins,perfectBins");
        }

        return trimmed.Count == 0 ? null : string.Join(",", trimmed);
    }

    private static bool TrimBySkill(SelfJudgmentSnapshot snapshot)
    {
        List<int[]> rows = snapshot.AccuracyBySkill;
        if (rows.Count <= BySkillKeepTop) return false;

        var kept = new List<int[]>();
        int[] blockedRemainder = [-1, 0, 0];
        int[] cleanRemainder = [0, 0, 0];
        bool dropped = false;

        for (int i = 0; i < rows.Count; i++)
        {
            int[] row = rows[i];
            bool keep = row[2] > 0 || i < BySkillKeepTop || row[1] >= BySkillKeepMinHits;
            if (keep)
            {
                kept.Add(row);
                continue;
            }

            dropped = true;
            int[] target = row[2] > 0 ? blockedRemainder : cleanRemainder;
            target[1] += row[1];
            target[2] += row[2];
        }

        if (!dropped) return false;

        if (cleanRemainder[1] > 0) kept.Add(cleanRemainder);
        if (blockedRemainder[1] > 0) kept.Add(blockedRemainder);
        snapshot.AccuracyBySkill = kept;
        return true;
    }

    /// <summary>Widen a bin axis by folding its key onto a coarser multiple. Counts are preserved exactly —
    /// only the resolution of the stat axis drops.</summary>
    private static List<int[]> MergeBins(List<int[]> rows, int factor, int keyIndex)
    {
        if (rows.Count == 0) return rows;

        var merged = new Dictionary<long, int[]>();
        foreach (int[] row in rows)
        {
            int coarse = (int)Math.Floor((double)row[keyIndex] / factor) * factor;
            long key = Key(coarse, row[1]);
            if (!merged.TryGetValue(key, out int[]? cell))
            {
                cell = new int[2];
                merged[key] = cell;
            }

            cell[0] += row[2];
            cell[1] += row[3];
        }

        return Rows(merged);
    }

    private static int EstimateBytes(SelfJudgmentSnapshot snapshot)
    {
        int ints = 0;
        ints += CountInts(snapshot.Smite);
        ints += CountInts(snapshot.Perfect);
        ints += CountInts(snapshot.Accuracy);
        ints += CountInts(snapshot.Crit);
        foreach (int[] row in snapshot.AccuracyBySkill) ints += row.Length;
        return ints * BytesPerInt;

        static int CountInts(JudgmentAxisSnapshot axis)
        {
            int total = 0;
            foreach (int[] row in axis.Bins) total += row.Length;
            foreach (int[] row in axis.LateBins) total += row.Length;
            return total;
        }
    }
}
