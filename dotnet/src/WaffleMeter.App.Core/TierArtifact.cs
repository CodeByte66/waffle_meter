using System.Globalization;
using System.Text.Json;
using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>One cohort cell's coordinate in the tier distribution artifact. Mirrors the server's row keys
/// (<c>r,m,k,d,v,b,j,s,p,g</c>) as small ints so lookups allocate nothing.
/// <para><b>Sentinels:</b> <see cref="DungeonOrd"/>/<see cref="VariantOrd"/>/<see cref="BossIndex"/>/
/// <see cref="SynergyCount"/> use <b>-1</b> for "axis removed"; <see cref="PartyMode"/> uses <b>0</b> (not -1).
/// <see cref="CategoryId"/> and <see cref="JobId"/> are NEVER dropped — a rung that ignored category would
/// match the first of the three co-existing R6 rows (성역/원정/초월) and apply the wrong distribution.</para>
/// <para><see cref="PowerBand"/> is the schema-v2 combat-power axis: the band's lower bound, or <b>-1</b> for
/// the whole-cohort row. A v1 artifact carries no such field, so every one of its rows parses as -1 and the
/// same lookup code serves both schemas.</para></summary>
public readonly record struct TierRowKey(
    int Rung,
    int MetricId,      // 0 = dps, 1 = ndps
    int CategoryId,
    int DungeonOrd,
    int VariantOrd,
    int BossIndex,
    int JobId,
    int SynergyCount,
    int PartyMode,
    int PowerBand = TierArtifact.WholeCohortBand);

/// <summary>Where a mobCode sits in the dungeon catalog. <paramref name="BossIndex"/> is <b>1-based</b>.</summary>
public readonly record struct TierMobPlacement(int DungeonOrd, int VariantOrd, int BossIndex);

/// <summary>
/// How a client reaches a variant that no mobCode can point at.
/// <para>시련's levels 4~16 share three boss mobCodes, so its top-difficulty variant cannot appear in
/// <c>mobs</c> — that map is 1:1 and those codes already belong to the pooled 시련 variant, which is not
/// ranked. This carries the missing half: the codes, the exact coordinate to use instead, and the affix values
/// that must hold for it to apply.</para>
/// <para>The codes stay out of <c>mobs</c> deliberately. A build that does not know about this section keeps
/// resolving 바크론 to nothing, which is the right answer for a client that cannot tell the difficulties
/// apart — whereas putting them in <c>mobs</c> would make every already-shipped meter score a level-4 clear
/// against top-difficulty cuts.</para>
/// </summary>
public sealed class TierTrialGate
{
    public TierTrialGate(
        int dungeonOrd,
        int variantOrd,
        IReadOnlyDictionary<int, int> bossIndexByMobCode,
        IReadOnlyDictionary<string, int> axes)
    {
        DungeonOrd = dungeonOrd;
        VariantOrd = variantOrd;
        BossIndexByMobCode = bossIndexByMobCode;
        Axes = axes;
    }

    public int DungeonOrd { get; }

    public int VariantOrd { get; }

    /// <summary>mobCode → bossIndex. Replaces what <c>mobs</c> would have supplied.</summary>
    public IReadOnlyDictionary<int, int> BossIndexByMobCode { get; }

    /// <summary>Affix name → the value that must hold. Every entry must match.</summary>
    public IReadOnlyDictionary<string, int> Axes { get; }
}

/// <summary>
/// The downloaded tier distribution artifact — the immutable quantile ladder the meter evaluates locally so a
/// live "상위 X.X%" costs ZERO server requests per battle.
/// <para>Parsing never throws: a malformed/oversized/unknown-schema document yields null and the feature stays
/// silently off (the same posture as <c>JoinIcons.TryLoad</c>). Unknown extra keys are ignored so the server can
/// extend the document without a meter release.</para>
/// </summary>
public sealed class TierArtifact
{
    /// <summary>
    /// Artifact schemas this build can read. Anything else is refused outright (fail-closed).
    /// <para>🔑 This is a SET, not a single value, and it has to stay one. The check is exact-match, so a build
    /// that understands only the newest schema stops taking tier updates until the server flips — and a build
    /// that understands only the oldest stops the moment it does. Both directions have to be live at once for
    /// a rollout to have no gap, and the meter is the side that must go first.</para>
    /// <para>v1 → v2 added the combat-power band axis (<c>g</c>) to each row. v1 rows read back as
    /// <see cref="WholeCohortBand"/>, which is exactly what they are.</para>
    /// <para>v2 → v3 changes NOTHING about the document's shape — same keys, same row schema, parsed by the
    /// same code. The version exists only to SPLIT THE AUDIENCE: v3 declares a narrower
    /// <see cref="PowerBandSize"/> (25k rather than 50k), and a build older than 2.10.0 ignores that
    /// declaration and bands on a hardcoded 50k grid. Because every multiple of 50k is also a multiple of 25k,
    /// such a build's row key still RESOLVES — to the band below the character's real one. It would be ranked
    /// against a weaker population, with no miss to fall back from and no way for anyone to notice. Serving
    /// those builds a v2 (50k) document and this one a v3 is what keeps that from happening, so the server
    /// publishes both and answers each client with the highest version it asked for.</para>
    /// </summary>
    public static readonly IReadOnlySet<int> SupportedSchemaVersions = new HashSet<int> { 1, 2, 3 };

    /// <summary>Row sentinel for "not split by combat power" — the fallback every banded lookup falls back to,
    /// and what every row of a v1 artifact is.</summary>
    public const int WholeCohortBand = -1;

    /// <summary>Band width to assume when the artifact does not declare one. The server picked 50k because a
    /// 20k band leaves only 55.7% of characters above the sample floor against 79.3% at 50k.
    /// <para>⚠️ This is only a FALLBACK for artifacts published before the spec was carried on the wire — read
    /// <see cref="PowerBandSize"/> instead. See that property for why.</para></summary>
    public const int DefaultPowerBandSize = 50_000;

    /// <summary>Floor to assume when the artifact does not declare one. Matches the ladder's own
    /// <see cref="TierLadder.MinPower"/>, so in practice no battle lands under it.</summary>
    public const int DefaultPowerBandFloor = 400_000;

    /// <summary>
    /// Width of a combat-power band, as THIS artifact declares it.
    /// <para>🔑 Why it is read rather than hardcoded: the band a character belongs to is computed here
    /// (<see cref="BandFor"/>) and looked up as part of the row key, while the rows themselves are bucketed by
    /// the server. If the two disagree the lookup simply misses — every user silently drops to the whole-cohort
    /// fallback and nobody sees an error, only a worse comparison. That made the width impossible to change:
    /// the server could not narrow or widen it without shipping a meter on the same day.</para>
    /// <para>The width is expected to MOVE. It is a function of how much data the population has produced —
    /// 50k today, and narrower (20k) once the sample floor is comfortably cleared, which is a better comparison
    /// because the band is closer to the player's actual gear. So the artifact declares it and this follows.</para>
    /// <para>Absent from the document (every artifact published before this shipped) → the defaults above,
    /// which are exactly what those artifacts were built with.</para>
    /// </summary>
    public int PowerBandSize { get; }

    /// <summary>Bands start here; everything below shares the lowest band. Declared by the artifact for the
    /// same reason as <see cref="PowerBandSize"/>.</summary>
    public int PowerBandFloor { get; }

    /// <summary>The band a character's combat power belongs to, under THIS artifact's spec.</summary>
    public int BandFor(int power) => BandFor(power, PowerBandSize, PowerBandFloor);

    /// <summary>Band arithmetic on an explicit spec — the artifact-free form, so the rule stays testable and
    /// the two callers cannot drift apart.</summary>
    public static int BandFor(int power, int bandSize, int bandFloor) =>
        bandSize <= 0 ? bandFloor : Math.Max(bandFloor, power / bandSize * bandSize);

    public static bool IsSupportedSchemaVersion(int version) => SupportedSchemaVersions.Contains(version);

    /// <summary>What the manifest request asks for. Derived from <see cref="SupportedSchemaVersions"/> so that
    /// teaching this build a new schema is one edit — a second literal somewhere in the HTTP layer would drift
    /// the moment only one of the two got updated, and the failure is silent (the server keeps answering the
    /// older document forever).</summary>
    public static int MaxSupportedSchemaVersion => SupportedSchemaVersions.Max();

    /// <summary>Cuts are transported as /100-quantized deltas; this scales them back.</summary>
    private const int CutQuantum = 100;

    private readonly Dictionary<TierRowKey, long[]> _rows;
    private readonly Dictionary<int, TierMobPlacement> _mobs;
    private readonly List<TierTrialGate> _trialGates;
    private readonly Dictionary<int, int> _dungeonCategoryId;
    private readonly Dictionary<int, string> _dungeonNames;
    private readonly Dictionary<(int DungeonOrd, int Ord), string> _variantLabels;
    private readonly Dictionary<string, int> _categoryIds;
    private readonly Dictionary<string, int> _jobIds;

    private TierArtifact(
        string artifactId,
        int windowDays,
        string generatedAt,
        int powerBandSize,
        int powerBandFloor,
        double[] grid,
        Dictionary<TierRowKey, long[]> rows,
        Dictionary<int, TierMobPlacement> mobs,
        List<TierTrialGate> trialGates,
        Dictionary<int, int> dungeonCategoryId,
        Dictionary<int, string> dungeonNames,
        Dictionary<(int, int), string> variantLabels,
        Dictionary<string, int> categoryIds,
        Dictionary<string, int> jobIds)
    {
        ArtifactId = artifactId;
        WindowDays = windowDays;
        GeneratedAt = generatedAt;
        PowerBandSize = powerBandSize;
        PowerBandFloor = powerBandFloor;
        Grid = grid;
        _rows = rows;
        _mobs = mobs;
        _trialGates = trialGates;
        _dungeonCategoryId = dungeonCategoryId;
        _dungeonNames = dungeonNames;
        _variantLabels = variantLabels;
        _categoryIds = categoryIds;
        _jobIds = jobIds;
    }

    public string ArtifactId { get; }

    public int WindowDays { get; }

    /// <summary>Server-side ISO-8601 build time (informational; staleness is judged from the local fetch time).</summary>
    public string GeneratedAt { get; }

    /// <summary>The 31 quantile anchors as TOP-percent, DESCENDING (100 → 0.1). Element i pairs with cuts[i],
    /// which run ASCENDING in metric value — i.e. grid[0]=100 is the lowest metric value.</summary>
    public double[] Grid { get; }

    public int RowCount => _rows.Count;

    public int MobCount => _mobs.Count;

    /// <summary>Distinct dungeons the artifact covers. NOT the same as <see cref="MobCount"/> — one dungeon
    /// contributes one boss mobCode per boss per difficulty, so a 7-dungeon artifact carries ~41 codes.</summary>
    public int DungeonCount => _dungeonNames.Count;

    /// <summary>Dungeon/variant/boss for a boss mobCode, or null when the code is not in the catalog.
    /// <b>Unmapped = no tier</b> (fail-closed) — never fall back to a neighbouring dungeon.</summary>
    public TierMobPlacement? Placement(int mobCode) =>
        _mobs.TryGetValue(mobCode, out TierMobPlacement p) ? p : null;

    /// <summary>
    /// Placement for a mobCode whose difficulty the code alone cannot express — 시련's, in practice.
    /// <para>Falls back to <see cref="TierArtifact.Placement(int)"/> first; only when that misses does it try
    /// the trial gates, and a gate applies only when EVERY affix it lists equals what the packet stream
    /// actually reported. Fail-closed in three directions, all deliberate: no gates in the artifact means no
    /// tier rather than a guessed one; an affix the meter could not read is not equal to anything, so it does
    /// not match; and an axis name this build does not know can never match, so a newer server cannot
    /// half-apply a gate to an older meter.</para>
    /// </summary>
    public TierMobPlacement? Placement(int mobCode, TrialDifficulty trial)
    {
        if (_mobs.TryGetValue(mobCode, out TierMobPlacement p))
        {
            return p;
        }

        foreach (TierTrialGate gate in _trialGates)
        {
            if (!gate.BossIndexByMobCode.TryGetValue(mobCode, out int bossIndex))
            {
                continue;
            }

            if (gate.Axes.All(axis => AffixValue(trial, axis.Key) == axis.Value))
            {
                return new TierMobPlacement(gate.DungeonOrd, gate.VariantOrd, bossIndex);
            }
        }

        return null;
    }

    /// <summary>The observed value of a named affix, or null when this build does not know the name or the
    /// meter never read it. Null never equals a required value, which is what closes the gate.</summary>
    private static int? AffixValue(TrialDifficulty trial, string axis) => axis switch
    {
        "timelimit" => trial.Timelimit,
        "rebirthlimit" => trial.Rebirthlimit,
        "bossBuff" => trial.BossBuff,
        "skillUpgrade" => trial.SkillUpgrade,
        _ => null,
    };

    /// <summary>Display name for a dungeon ord (settings/tooltips), or null.</summary>
    public string? DungeonName(int dungeonOrd) =>
        _dungeonNames.TryGetValue(dungeonOrd, out string? n) ? n : null;

    /// <summary>Difficulty/stage label. Keyed by (dungeonOrd, ord) — the SAME ord means "보통" in an 원정
    /// dungeon, "2단계" in an 초월 one and "어려움" in 무스펠의 성배, so a single-key lookup is wrong.</summary>
    public string? VariantLabel(int dungeonOrd, int variantOrd) =>
        _variantLabels.TryGetValue((dungeonOrd, variantOrd), out string? l) ? l : null;

    /// <summary>Interned category id for a dungeon ord, or -1 when unknown. Category is not carried in the
    /// mob map, so it must be resolved through the dungeon catalog before a row can be matched.</summary>
    public int CategoryIdForDungeon(int dungeonOrd) =>
        _dungeonCategoryId.TryGetValue(dungeonOrd, out int id) ? id : -1;

    /// <summary>Interned job id, or -1 when the job is not one of the nine ranked classes.</summary>
    public int JobId(string? job) =>
        job is not null && _jobIds.TryGetValue(job, out int id) ? id : -1;

    /// <summary>Interned category id, or -1.</summary>
    public int CategoryId(string? category) =>
        category is not null && _categoryIds.TryGetValue(category, out int id) ? id : -1;

    /// <summary>The 31 ascending metric cuts for a cohort cell, or null when the cell was not shipped
    /// (= it did not clear the server's sample floor, so "존재함 == 자격을 갖췄음").</summary>
    public long[]? Cuts(TierRowKey key) => _rows.TryGetValue(key, out long[]? cuts) ? cuts : null;

    /// <summary>Parse a decompressed artifact document. Returns null on anything unexpected.</summary>
    public static TierArtifact? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryInt(root, "schemaVersion", out int schemaVersion) || !IsSupportedSchemaVersion(schemaVersion))
            {
                return null;
            }

            string artifactId = TryString(root, "artifactId") ?? string.Empty;
            if (artifactId.Length == 0)
            {
                return null;
            }

            TryInt(root, "windowDays", out int windowDays);
            string generatedAt = TryString(root, "generatedAt") ?? string.Empty;
            // 밴드 규격은 서버가 정하고 여기서 따른다(PowerBandSize 참조). 문서에 없으면 — 이 필드가
            // 생기기 전 아티팩트 — 그때 쓰던 값이 곧 정답이므로 기본값으로 읽는다. 0/음수는 문서가 깨진
            // 것이므로 마찬가지로 기본값으로 되돌린다(밴드 나눗셈이 0으로 나누는 것을 막는다).
            int powerBandSize = TryInt(root, "powerBandSize", out int declaredSize) && declaredSize > 0
                ? declaredSize
                : DefaultPowerBandSize;
            int powerBandFloor = TryInt(root, "powerBandFloor", out int declaredFloor) && declaredFloor > 0
                ? declaredFloor
                : DefaultPowerBandFloor;

            double[]? grid = ParseGrid(root);
            if (grid == null)
            {
                return null;
            }

            var jobIds = new Dictionary<string, int>(StringComparer.Ordinal);
            if (root.TryGetProperty("jobs", out JsonElement jobs) && jobs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement job in jobs.EnumerateArray())
                {
                    if (job.ValueKind == JsonValueKind.String)
                    {
                        string? name = job.GetString();
                        if (!string.IsNullOrEmpty(name) && !jobIds.ContainsKey(name!))
                        {
                            jobIds[name!] = jobIds.Count;
                        }
                    }
                }
            }

            if (jobIds.Count == 0)
            {
                return null;
            }

            var categoryIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var dungeonCategoryId = new Dictionary<int, int>();
            var dungeonNames = new Dictionary<int, string>();
            ParseDungeons(root, categoryIds, dungeonCategoryId, dungeonNames);

            var variantLabels = new Dictionary<(int, int), string>();
            ParseVariants(root, variantLabels);

            var mobs = new Dictionary<int, TierMobPlacement>();
            ParseMobs(root, mobs);

            var trialGates = new List<TierTrialGate>();
            ParseTrialGates(root, trialGates);

            var rows = new Dictionary<TierRowKey, long[]>();
            ParseRows(root, grid.Length, categoryIds, jobIds, rows);

            if (rows.Count == 0 || mobs.Count == 0)
            {
                return null;
            }

            return new TierArtifact(
                artifactId, windowDays, generatedAt, powerBandSize, powerBandFloor, grid, rows, mobs, trialGates,
                dungeonCategoryId, dungeonNames, variantLabels, categoryIds, jobIds);
        }
        catch
        {
            // A corrupt artifact must never crash the meter — the feature just stays off until the next fetch.
            return null;
        }
    }

    private static double[]? ParseGrid(JsonElement root)
    {
        if (!root.TryGetProperty("grid", out JsonElement grid) || grid.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<double>(31);
        foreach (JsonElement item in grid.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double v))
            {
                return null;
            }

            values.Add(v);
        }

        // The grid must be strictly descending top-percent; anything else means we misunderstand the document.
        if (values.Count < 2)
        {
            return null;
        }

        for (int i = 1; i < values.Count; i++)
        {
            if (values[i] >= values[i - 1])
            {
                return null;
            }
        }

        return values.ToArray();
    }

    private static void ParseDungeons(
        JsonElement root,
        Dictionary<string, int> categoryIds,
        Dictionary<int, int> dungeonCategoryId,
        Dictionary<int, string> dungeonNames)
    {
        if (!root.TryGetProperty("dungeons", out JsonElement dungeons) || dungeons.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement d in dungeons.EnumerateArray())
        {
            if (d.ValueKind != JsonValueKind.Object || !TryInt(d, "ord", out int ord))
            {
                continue;
            }

            string? category = TryString(d, "category");
            if (!string.IsNullOrEmpty(category))
            {
                if (!categoryIds.TryGetValue(category!, out int id))
                {
                    id = categoryIds.Count;
                    categoryIds[category!] = id;
                }

                dungeonCategoryId[ord] = id;
            }

            string? name = TryString(d, "name");
            if (!string.IsNullOrEmpty(name))
            {
                dungeonNames[ord] = name!;
            }
        }
    }

    private static void ParseVariants(JsonElement root, Dictionary<(int, int), string> variantLabels)
    {
        if (!root.TryGetProperty("variants", out JsonElement variants) || variants.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement v in variants.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Object
                || !TryInt(v, "dungeonOrd", out int dungeonOrd)
                || !TryInt(v, "ord", out int ord))
            {
                continue;
            }

            string? label = TryString(v, "label");
            if (!string.IsNullOrEmpty(label))
            {
                variantLabels[(dungeonOrd, ord)] = label!;
            }
        }
    }

    private static void ParseMobs(JsonElement root, Dictionary<int, TierMobPlacement> mobs)
    {
        if (!root.TryGetProperty("mobs", out JsonElement map) || map.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty entry in map.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mobCode)
                || entry.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            int dungeonOrd = 0;
            int variantOrd = 0;
            int bossIndex = 0;
            int n = 0;
            foreach (JsonElement item in entry.Value.EnumerateArray())
            {
                if (n >= 3 || item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int value))
                {
                    n = -1;
                    break;
                }

                switch (n++)
                {
                    case 0: dungeonOrd = value; break;
                    case 1: variantOrd = value; break;
                    default: bossIndex = value; break;
                }
            }

            if (n == 3)
            {
                mobs[mobCode] = new TierMobPlacement(dungeonOrd, variantOrd, bossIndex);
            }
        }
    }

    private static void ParseTrialGates(JsonElement root, List<TierTrialGate> gates)
    {
        if (!root.TryGetProperty("trialGates", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return; // an artifact built before the gate existed simply yields no tier for those bosses
        }

        foreach (JsonElement gate in list.EnumerateArray())
        {
            if (gate.ValueKind != JsonValueKind.Object
                || !TryInt(gate, "dungeonOrd", out int dungeonOrd)
                || !TryInt(gate, "variantOrd", out int variantOrd)
                || !gate.TryGetProperty("mobs", out JsonElement mobs) || mobs.ValueKind != JsonValueKind.Object
                || !gate.TryGetProperty("axes", out JsonElement axes) || axes.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var bossIndexByMobCode = new Dictionary<int, int>();
            foreach (JsonProperty entry in mobs.EnumerateObject())
            {
                if (int.TryParse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mobCode)
                    && entry.Value.ValueKind == JsonValueKind.Number
                    && entry.Value.TryGetInt32(out int bossIndex))
                {
                    bossIndexByMobCode[mobCode] = bossIndex;
                }
            }

            var required = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (JsonProperty entry in axes.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetInt32(out int value))
                {
                    required[entry.Name] = value;
                }
                else
                {
                    // An axis whose value we cannot read must not be silently dropped — dropping it would
                    // loosen the gate. Poison the whole gate instead so it can never match.
                    required[entry.Name] = int.MinValue;
                }
            }

            if (bossIndexByMobCode.Count > 0 && required.Count > 0)
            {
                gates.Add(new TierTrialGate(dungeonOrd, variantOrd, bossIndexByMobCode, required));
            }
        }
    }

    private static void ParseRows(
        JsonElement root,
        int gridLength,
        Dictionary<string, int> categoryIds,
        Dictionary<string, int> jobIds,
        Dictionary<TierRowKey, long[]> rows)
    {
        if (!root.TryGetProperty("rows", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement row in list.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryInt(row, "r", out int rung) || !TryInt(row, "d", out int dungeonOrd)
                || !TryInt(row, "v", out int variantOrd) || !TryInt(row, "b", out int bossIndex)
                || !TryInt(row, "s", out int synergyCount) || !TryInt(row, "p", out int partyMode))
            {
                continue;
            }

            string? metric = TryString(row, "m");
            int metricId = TierLadder.MetricId(metric);
            if (metricId < 0)
            {
                continue;
            }

            // 'k' (category) and 'j' (job) are never sentinels — a row without them is unusable, because
            // matching on the remaining axes would silently pick another category's distribution.
            string? category = TryString(row, "k");
            string? job = TryString(row, "j");
            if (category == null || job == null
                || !categoryIds.TryGetValue(category, out int categoryId)
                || !jobIds.TryGetValue(job, out int jobId))
            {
                continue;
            }

            long[]? cuts = DecodeCuts(row, gridLength);
            if (cuts == null)
            {
                continue;
            }

            // 'g' is schema-v2 only. Absent means the row is not split by combat power, which is precisely what
            // every v1 row is — so a v1 artifact becomes a v2 one whose bands are all whole-cohort, and the
            // lookup needs no schema branch at all.
            int powerBand = TryInt(row, "g", out int band) ? band : WholeCohortBand;

            var key = new TierRowKey(
                rung, metricId, categoryId, dungeonOrd, variantOrd, bossIndex, jobId, synergyCount, partyMode, powerBand);
            rows[key] = cuts;
        }
    }

    /// <summary>Undo the server's /100 quantization + delta encoding: <c>acc += c[i]; cuts[i] = acc * 100</c>.
    /// Rejects a row whose length disagrees with the grid, or whose accumulation is not non-decreasing
    /// (equal neighbours ARE legal — a low-sample cohort can collapse several quantiles into one bin).</summary>
    private static long[]? DecodeCuts(JsonElement row, int gridLength)
    {
        if (!row.TryGetProperty("c", out JsonElement encoded) || encoded.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var cuts = new long[gridLength];
        long acc = 0;
        int i = 0;
        foreach (JsonElement item in encoded.EnumerateArray())
        {
            if (i >= gridLength || item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out long delta))
            {
                return null;
            }

            acc += delta;
            if (acc < 0)
            {
                return null;
            }

            cuts[i++] = acc * CutQuantum;
        }

        return i == gridLength ? cuts : null;
    }

    private static bool TryInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out JsonElement e)
            && e.ValueKind == JsonValueKind.Number
            && e.TryGetInt32(out value);
    }

    private static string? TryString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
