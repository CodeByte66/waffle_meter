using System.Text.Json;

namespace WaffleMeter.Data;

/// <summary>Where a boss mobCode sits in the supported-dungeon catalog.
/// <para><see cref="BossIndex"/> is 1-based, matching the stats web's boss ordering.</para></summary>
public readonly record struct EncounterInfo(
    string DungeonKey,
    string DungeonName,
    string Category,
    int CategoryOrd,
    string VariantLabel,
    int DungeonId,
    int BossIndex,
    string BossName,
    bool HasVariants,
    // The web models a variant as EITHER a difficulty (원정: 탐험/보통/어려움/시련, 성역 무스펠: 보통/어려움)
    // OR a numbered stage (초월: "1".."4"); exactly one is set, both are null for a single-variant dungeon.
    // Kept apart from VariantLabel so the upload payload can state them the way the server models them.
    string? Difficulty = null,
    string? Stage = null)
{
    /// <summary>The boss name carrying its difficulty/stage — <c>"바크론 (시련)"</c>. The same boss NAME recurs
    /// across every difficulty of a dungeon (only the mobCode differs), so without this the meter shows the same
    /// title for a 탐험 clear and a 시련 clear.
    /// <para>A dungeon with a single variant (성역 루드라·침식의 정화소, label "전체") gets no suffix — there is
    /// nothing to disambiguate and the label would be noise.</para></summary>
    public string DisplayBossName =>
        HasVariants && VariantLabel.Length > 0 ? $"{BossName} ({VariantLabel})" : BossName;

    /// <summary>Whether this encounter is a 공대 (raid) rather than a party dungeon — i.e. whether sub-party
    /// slots mean anything for it.
    /// <para>The category IS the answer, and it is the only reliable one. In the client's dungeon table every
    /// 성역 is <c>EDungeonType::Raid</c> with 10/10 members, and every 원정 and 초월 is
    /// <c>EDungeonType::Party</c> capped at five — including 바크론 시련, which despite its difficulty is a
    /// five-man. Inferring it from the observed roster size instead gets it wrong in both directions: a roster
    /// stranded from earlier content tags a four-man dungeon as a raid, and a raid whose roster snapshot
    /// under-parsed (9 of 10 members) stops being one and silently loses every sub-party tag.</para></summary>
    public bool IsRaid => string.Equals(Category, RaidCategory, StringComparison.Ordinal);

    /// <summary>The stats web's category label for 공대 content. Matches the seed that generates the catalog.</summary>
    public const string RaidCategory = "성역";

    /// <summary>Whether this is the 바크론 시련 — the one variant whose difficulty is NOT determined by the boss
    /// code, because all sixteen of its levels share one map and one set of codes. It is therefore the only
    /// encounter allowed to have its label overridden by what the affix reader worked out.</summary>
    public bool IsTrial => string.Equals(Difficulty, TrialDifficultyLabel, StringComparison.Ordinal);

    /// <summary>The web's difficulty label for 바크론 시련.</summary>
    public const string TrialDifficultyLabel = "시련";
}

/// <summary>
/// The encounters the stats web publishes statistics for, as a <c>mobCode → (dungeon, variant, boss)</c> map.
/// <para>This is a shipped mirror of the web's encounter seed (regenerate with
/// <c>dotnet/tools/export-encounters.ts</c>). It earns its place twice:</para>
/// <list type="number">
/// <item>The upload gate. <c>normalizeEncounter</c> only verifies a mobCode that appears in that seed and the
/// server answers <c>400 unsupported_encounter</c> otherwise — and the meter has no retry path, so every such
/// battle is lost outright. Gating locally means we never spend the request.</item>
/// <item>Difficulty labelling. A boss mobCode is unique per (dungeon, difficulty/stage), so this table is the
/// only thing the meter needs to tell 시련 바크론 from 어려움 바크론 — no map-id tracking required.</item>
/// </list>
/// <para>Loading never throws: a missing or malformed asset yields <see cref="Empty"/>, which reports every code
/// as supported. That is deliberate — an unreadable catalog must not silently stop all uploads.</para>
/// </summary>
public sealed class EncounterCatalog
{
    /// <summary>The no-catalog fallback: nothing is known, so nothing is filtered out.</summary>
    public static readonly EncounterCatalog Empty = new(new Dictionary<int, EncounterInfo>());

    private readonly Dictionary<int, EncounterInfo> _byMobCode;

    private EncounterCatalog(Dictionary<int, EncounterInfo> byMobCode) => _byMobCode = byMobCode;

    /// <summary>How many mobCodes the catalog maps (0 = the fallback that filters nothing).</summary>
    public int Count => _byMobCode.Count;

    /// <summary>True when the catalog was actually loaded and may be used as a gate.</summary>
    public bool IsLoaded => _byMobCode.Count > 0;

    /// <summary>encounters.json: <c>{ "dungeons": [ { key, category, categoryOrd, name, variantType,
    /// bosses: [{index, name}], variants: [{ label, dungeonId, mobs: [[mobCode, bossIndex], ...] }] } ] }</c>.
    /// Returns <see cref="Empty"/> rather than throwing when the file is absent or unreadable.</summary>
    public static EncounterCatalog Load(string path)
    {
        try
        {
            // The shipped asset carries 19 dungeons / 177 codes, so anything far under that is damage rather
            // than a small catalog — and refusing to gate beats gating on a fragment.
            return Parse(File.ReadAllText(path), minimumCodes: 100);
        }
        catch (Exception)
        {
            return Empty; // fail-open: a broken catalog must not stop every upload
        }
    }

    /// <summary>Parse from JSON text. Malformed dungeons/variants are skipped individually so one bad entry
    /// cannot cost the whole catalog.</summary>
    /// <param name="minimumCodes">Below this many codes the result is <see cref="Empty"/> instead — the
    /// fail-open contract has to cover PARTIAL damage too, since a catalog that "loaded" gates every code it
    /// doesn't have, and a seed whose schema shifted could leave a handful of dungeons standing. 0 disables
    /// the floor (tests build deliberately tiny catalogs); <see cref="Load"/> applies the real one.</param>
    public static EncounterCatalog Parse(string json, int minimumCodes = 0)
    {
        var map = new Dictionary<int, EncounterInfo>();
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("dungeons", out JsonElement dungeons)
            || dungeons.ValueKind != JsonValueKind.Array)
        {
            return Empty;
        }

        foreach (JsonElement dungeon in dungeons.EnumerateArray())
        {
            string key = Str(dungeon, "key");
            string name = Str(dungeon, "name");
            string category = Str(dungeon, "category");
            int categoryOrd = dungeon.TryGetProperty("categoryOrd", out JsonElement co)
                && co.ValueKind == JsonValueKind.Number ? co.GetInt32() : 0;

            // variantType "all" = the dungeon has exactly one (unnamed) variant, so its label carries no
            // information and must not be appended to a boss name.
            bool hasVariants = !string.Equals(Str(dungeon, "variantType"), "all", StringComparison.Ordinal);

            var bossNames = new Dictionary<int, string>();
            if (dungeon.TryGetProperty("bosses", out JsonElement bosses) && bosses.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement boss in bosses.EnumerateArray())
                {
                    if (boss.TryGetProperty("index", out JsonElement bi) && bi.ValueKind == JsonValueKind.Number)
                    {
                        bossNames[bi.GetInt32()] = Str(boss, "name");
                    }
                }
            }

            if (!dungeon.TryGetProperty("variants", out JsonElement variants)
                || variants.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement variant in variants.EnumerateArray())
            {
                string label = Str(variant, "label");
                int dungeonId = variant.TryGetProperty("dungeonId", out JsonElement di)
                    && di.ValueKind == JsonValueKind.Number ? di.GetInt32() : 0;
                string? difficulty = NullableStr(variant, "difficulty");
                string? stage = NullableStr(variant, "stage");

                if (!variant.TryGetProperty("mobs", out JsonElement mobs) || mobs.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement pair in mobs.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2)
                    {
                        continue;
                    }

                    JsonElement codeEl = pair[0];
                    JsonElement indexEl = pair[1];
                    if (codeEl.ValueKind != JsonValueKind.Number || indexEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    int mobCode = codeEl.GetInt32();
                    int bossIndex = indexEl.GetInt32();
                    map[mobCode] = new EncounterInfo(
                        DungeonKey: key,
                        DungeonName: name,
                        Category: category,
                        CategoryOrd: categoryOrd,
                        VariantLabel: label,
                        DungeonId: dungeonId,
                        BossIndex: bossIndex,
                        BossName: bossNames.GetValueOrDefault(bossIndex, string.Empty),
                        HasVariants: hasVariants,
                        Difficulty: difficulty,
                        Stage: stage);
                }
            }
        }

        return map.Count > 0 && map.Count >= minimumCodes ? new EncounterCatalog(map) : Empty;
    }

    /// <summary>The catalog entry for a boss mobCode, or null when the web does not publish stats for it.</summary>
    public EncounterInfo? Lookup(int mobCode) =>
        _byMobCode.TryGetValue(mobCode, out EncounterInfo info) ? info : null;

    /// <summary>Whether a battle on this boss is worth uploading. An unloaded catalog answers true for
    /// everything, so a missing asset degrades to the pre-catalog behaviour instead of blocking uploads.</summary>
    public bool IsSupported(int mobCode) => !IsLoaded || _byMobCode.ContainsKey(mobCode);

    /// <summary>The boss name to SHOW for a mobCode — with its difficulty/stage appended when the catalog knows
    /// one. Falls back to <paramref name="mobName"/> untouched for anything uncatalogued (field bosses, trash,
    /// new content). Never used for the upload payload: the web matches on the raw name.</summary>
    /// <param name="variantOverride">Replaces the catalogued variant label — but ONLY for the 시련, the one
    /// encounter whose difficulty the boss code cannot express (all sixteen levels share one dungeonId and one
    /// set of codes). Everywhere else the code already IS the answer, so an override could only overwrite a
    /// fact with a guess: applying it unconditionally, as this did until 2026-08-11, is what stamped
    /// "(시련 13~16단계)" onto a 초월 2단계 boss for the rest of a session that happened to open with a trial.</param>
    public string DisplayName(int mobCode, string? mobName, string? variantOverride = null)
    {
        string fallback = mobName ?? string.Empty;
        if (Lookup(mobCode) is not EncounterInfo info || !info.HasVariants || info.VariantLabel.Length == 0)
        {
            return fallback;
        }

        if (info.IsTrial && !string.IsNullOrWhiteSpace(variantOverride))
        {
            info = info with { VariantLabel = variantOverride! };
        }

        // Prefer the live mob name over the catalog's: the catalog's boss names are the web's display names and
        // can drift from mobs.json (e.g. "바실루스" vs "위악의 바실루스"). Only the SUFFIX comes from here.
        // Blank-not-empty has to be caught as well, or a whitespace name renders as a bare "  (시련)".
        string name = fallback.Trim().Length > 0 ? fallback : info.BossName;
        return name.Trim().Length > 0 ? $"{name} ({info.VariantLabel})" : fallback;
    }

    /// <summary>
    /// <see cref="DisplayName"/> 과 같은 판정을 쓰되, 이름과 라벨을 <b>합치지 않고</b> 돌려준다.
    /// 오버레이가 난이도를 칩으로 그리려면 괄호로 합쳐진 문자열을 되쪼갤 수 없기 때문이다.
    ///
    /// <para>⚠️ <see cref="DisplayName"/> 은 그대로 둔다 — 전투 기록·업로드가 합쳐진 문자열을 계속
    /// 기대하고 테스트 20여 줄이 그 형태를 잠그고 있다. 여기는 순수 추가다.</para>
    ///
    /// <para>⚠️ 시련 override 게이트(<c>IsTrial</c>)도 똑같이 여기 안에 남긴다. 호출부가 라벨을 직접
    /// 조립하게 만드는 순간 2026-08-11 회귀가 재현된다 — 시련 라벨이 초월 2단계 보스에 찍혔던 그 건.</para>
    /// </summary>
    public (string Name, string? Variant, string? Dungeon) DisplayParts(
        int mobCode, string? mobName, string? variantOverride = null)
    {
        string fallback = mobName ?? string.Empty;
        if (Lookup(mobCode) is not EncounterInfo info || !info.HasVariants || info.VariantLabel.Length == 0)
        {
            return (fallback, null, null);
        }

        if (info.IsTrial && !string.IsNullOrWhiteSpace(variantOverride))
        {
            info = info with { VariantLabel = variantOverride! };
        }

        string name = fallback.Trim().Length > 0 ? fallback : info.BossName;
        return name.Trim().Length > 0
            ? (name, info.VariantLabel, info.DungeonName)
            : (fallback, null, null);
    }

    private static string? NullableStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
}
