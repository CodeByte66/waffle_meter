using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// Guards the SHIPPED encounter catalog (Assets/json/encounters.json), which the upload gate depends on: a
/// mobCode missing here means that battle is never uploaded, and a mobCode here that mobs.json doesn't mark as
/// a boss means a battle that can never start in the first place. Regenerate the asset with
/// <c>dotnet/tools/export-encounters.ts</c> when the stats web's catalog changes.
/// </summary>
public sealed class ShippedEncounterCatalogTests
{
    private static string AssetsJsonDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "Assets", "json");
            if (File.Exists(Path.Combine(candidate, "encounters.json")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Assets/json/encounters.json not found above " + AppContext.BaseDirectory);
    }

    private static EncounterCatalog Shipped() =>
        EncounterCatalog.Load(Path.Combine(AssetsJsonDir(), "encounters.json"));

    private static Dictionary<int, Mob> ShippedMobs() =>
        ReferenceJson.LoadMobs(Path.Combine(AssetsJsonDir(), "mobs.json"));

    /// <summary>The raw asset. <see cref="EncounterCatalog"/> keys by mobCode, so defects that live at the
    /// VARIANT level — a duplicate dungeonId, a variant with no mobs — are invisible through it.</summary>
    private static JsonElement ShippedJson() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AssetsJsonDir(), "encounters.json"))).RootElement;

    [Fact]
    public void The_shipped_catalog_loads()
    {
        EncounterCatalog catalog = Shipped();

        Assert.True(catalog.IsLoaded);
        Assert.True(catalog.Count >= 150, $"only {catalog.Count} mobCodes — did the export drop dungeons?");
    }

    /// <summary>Every catalogued boss must exist in mobs.json AND be flagged <c>boss</c>. A code that isn't a
    /// boss never opens a battle, so the meter would silently record nothing for that encounter.</summary>
    [Fact]
    public void Every_catalogued_code_is_a_known_boss_in_mobs_json()
    {
        EncounterCatalog catalog = Shipped();
        Dictionary<int, Mob> mobs = ShippedMobs();

        var missing = new List<int>();
        var notBoss = new List<int>();
        foreach (int code in AllCodes(catalog))
        {
            if (!mobs.TryGetValue(code, out Mob? mob))
            {
                missing.Add(code);
            }
            else if (!mob.Boss)
            {
                notBoss.Add(code);
            }
        }

        Assert.True(missing.Count == 0, "not in mobs.json: " + string.Join(", ", missing));
        Assert.True(notBoss.Count == 0, "in mobs.json but boss=false: " + string.Join(", ", notBoss));
    }

    /// <summary>The lookup is a plain mobCode -> encounter map, so a code shared by two variants would make the
    /// difficulty ambiguous and silently mis-label one of them. The exporter also rejects this; assert it here
    /// too so a hand-edited asset can't slip through.</summary>
    [Fact]
    public void No_mob_code_belongs_to_two_encounters()
    {
        List<int> codes = AllCodes(Shipped()).ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    /// <summary>Every variant must carry mobs. A variant with an empty mob list maps no code, so it can never
    /// label a battle — it is dead weight that only shows the seed grew a row it shouldn't have.
    /// <para>Found 2026-08-11: 바크론의 공중섬 carried a second 시련 row labelled <c>"시련 13~16단계"</c> with no
    /// mobs at all. That string is the METER's runtime label for an unpinned trial level, so something on the
    /// round trip is minting variants out of what the client displayed rather than out of the game's data. The
    /// meter itself is not the culprit — its payload sends the canonical <c>difficulty</c> ("시련") and the level
    /// separately as numbers — but this asserts we notice next time.</para></summary>
    [Fact]
    public void Every_variant_carries_at_least_one_mob()
    {
        var empty = new List<string>();
        foreach (JsonElement dungeon in ShippedJson().GetProperty("dungeons").EnumerateArray())
        {
            foreach (JsonElement variant in dungeon.GetProperty("variants").EnumerateArray())
            {
                if (variant.GetProperty("mobs").GetArrayLength() == 0)
                {
                    empty.Add($"{dungeon.GetProperty("name").GetString()} / {variant.GetProperty("label").GetString()}");
                }
            }
        }

        Assert.True(empty.Count == 0, "variants with no mobs: " + string.Join(", ", empty));
    }

    /// <summary>The one dungeonId clash we know about and cannot yet resolve: 610030~610033.
    /// <para>The client puts 노이란의 숨겨진 유산 there (Dungeon.dat rows → Map.dat LegacyNoiran_G_01~04, whose
    /// MapData.dat did not exist before the 2026-08-12 patch), while the seed has long put 심연의 뿔 암굴 there —
    /// and the capture corpus backs the seed, having seen phase mapId 610030 paired with 심연의 뿔 암굴's bosses
    /// ten times. The client meanwhile files 심연의 뿔 암굴 under 610070~610073 (map FafniteGully_G_01~04), in the
    /// 2026-08-05 build too, so nothing was renumbered: the id the game BROADCASTS and the id the client's Dungeon
    /// table carries simply differ for that dungeon. Nobody has run 노이란 yet, so its broadcast id is unknown and
    /// the client value stands. Resolve with <c>docs/tools/corpus-encounter/empirical_pairing.py</c> once a capture
    /// exists. Harmless meanwhile: DungeonId is stored on EncounterInfo and read by nothing — not the upload
    /// payload, not the gate.</para></summary>
    private static readonly HashSet<int> KnownDungeonIdClashes = [610030, 610031, 610032, 610033];

    /// <summary>A dungeonId identifies ONE instance, so two variants claiming the same one cannot both be right
    /// — and the pairing is what any map-based cross-check would have to trust.
    /// <para>Found 2026-08-11: 무스펠의 성배 보통 and 어려움 both claimed 620021, while the capture corpus shows
    /// the game running them as 620022 and 620021 respectively. Harmless for the meter's own labels (those key
    /// off mobCode, which is unique) but wrong in the seed the web aggregates by.</para>
    /// <para>✅ Fixed upstream 2026-08-20: 통계웹 <c>490aaf1</c> moves 보통 to 620022 and migration <c>0047</c>
    /// backfills the rows already stored, so a regeneration now carries the right id on its own. The guard stays —
    /// it is what caught this — but it has nothing left to catch here.</para></summary>
    [Fact]
    public void No_dungeon_id_belongs_to_two_variants()
    {
        var seen = new Dictionary<int, string>();
        var clashes = new List<string>();
        foreach (JsonElement dungeon in ShippedJson().GetProperty("dungeons").EnumerateArray())
        {
            string name = dungeon.GetProperty("name").GetString() ?? "?";
            foreach (JsonElement variant in dungeon.GetProperty("variants").EnumerateArray())
            {
                int id = variant.GetProperty("dungeonId").GetInt32();
                string here = $"{name} / {variant.GetProperty("label").GetString()}";
                if (seen.TryGetValue(id, out string? there))
                {
                    if (!KnownDungeonIdClashes.Contains(id))
                    {
                        clashes.Add($"{id}: {there} vs {here}");
                    }
                }
                else
                {
                    seen[id] = here;
                }
            }
        }

        Assert.True(clashes.Count == 0, "dungeonId claimed twice: " + string.Join("; ", clashes));
    }

    /// <summary>The known clash must stay exactly as documented — if one side moves, the exemption above is
    /// stale and the ids should be reconciled rather than silently kept exempt.</summary>
    [Fact]
    public void The_known_dungeon_id_clash_is_still_exactly_those_two_dungeons()
    {
        var byId = new Dictionary<int, List<string>>();
        foreach (JsonElement dungeon in ShippedJson().GetProperty("dungeons").EnumerateArray())
        {
            string name = dungeon.GetProperty("name").GetString() ?? "?";
            foreach (JsonElement variant in dungeon.GetProperty("variants").EnumerateArray())
            {
                int id = variant.GetProperty("dungeonId").GetInt32();
                if (KnownDungeonIdClashes.Contains(id))
                {
                    (byId.TryGetValue(id, out List<string>? xs) ? xs : byId[id] = []).Add(name);
                }
            }
        }

        Assert.Equal(KnownDungeonIdClashes.Count, byId.Count);
        foreach ((int id, List<string> names) in byId)
        {
            Assert.Equal(["노이란의 숨겨진 유산", "심연의 뿔 암굴"], names.OrderBy(n => n, StringComparer.Ordinal).ToList());
        }
    }

    /// <summary>The case that motivated the catalog: 시련 바크론 shares its boss NAMES with the other three
    /// difficulties but has its own codes, and the web registered it on 2026-08-03.</summary>
    [Fact]
    public void Bakron_trial_is_catalogued_and_distinct_from_the_other_difficulties()
    {
        EncounterCatalog catalog = Shipped();

        EncounterInfo? trial = catalog.Lookup(2300582);
        Assert.NotNull(trial);
        Assert.Equal("바크론의 공중섬", trial!.Value.DungeonName);
        Assert.Equal("시련", trial.Value.VariantLabel);
        Assert.Equal("바크론 (시련)", catalog.DisplayName(2300582, "바크론"));

        // Same boss, other difficulties — different codes, different labels.
        Assert.Equal("탐험", catalog.Lookup(2310812)!.Value.VariantLabel);
        Assert.Equal("보통", catalog.Lookup(2300812)!.Value.VariantLabel);
        Assert.Equal("어려움", catalog.Lookup(2320812)!.Value.VariantLabel);
    }

    /// <summary>The 2026-08-12 patch's two dungeons, entered by hand ahead of the web seed. Their mobCodes come
    /// from each map's <c>MapData.dat SpawnInfoList.NpcIdList</c> filtered to <c>NpcSubType::HeroMonster</c> — the
    /// game's own spawn list, not a naming rule: the code prefix does NOT encode difficulty (데우스 연구기지 spans
    /// 232xxxx~234xxxx across its four stages), so only the spawn data can pair a code with a variant.</summary>
    [Theory]
    // 잠식된 데우스 연구기지 (원정) — map BlasphemyLab_Easy/Normal/Hard = dungeonId 600161/600162/600163
    [InlineData(2300410, "잠식된 데우스 연구기지", "탐험", "감독관 그롬카스")]
    [InlineData(2300409, "잠식된 데우스 연구기지", "탐험", "연구소장 자일러스")]
    [InlineData(2300407, "잠식된 데우스 연구기지", "탐험", "오만의 아티엘")]
    [InlineData(2301750, "잠식된 데우스 연구기지", "보통", "감독관 그롬카스")]
    [InlineData(2301749, "잠식된 데우스 연구기지", "보통", "연구소장 자일러스")]
    [InlineData(2301748, "잠식된 데우스 연구기지", "보통", "오만의 아티엘")]
    [InlineData(2300556, "잠식된 데우스 연구기지", "어려움", "감독관 그롬카스")]
    [InlineData(2300555, "잠식된 데우스 연구기지", "어려움", "연구소장 자일러스")]
    [InlineData(2300481, "잠식된 데우스 연구기지", "어려움", "오만의 아티엘")]
    // 노이란의 숨겨진 유산 (초월) — map LegacyNoiran_G_01~04, 단계는 PartyDungeonLevel.DungeonLevel
    [InlineData(2300437, "노이란의 숨겨진 유산", "1단계", "불완전한 브라운트")]
    [InlineData(2300438, "노이란의 숨겨진 유산", "1단계", "광기의 클로민스터")]
    [InlineData(2300436, "노이란의 숨겨진 유산", "1단계", "아스크란")]
    [InlineData(2300441, "노이란의 숨겨진 유산", "2단계", "광기의 클로민스터")]
    [InlineData(2300444, "노이란의 숨겨진 유산", "3단계", "광기의 클로민스터")]
    [InlineData(2300445, "노이란의 숨겨진 유산", "4단계", "아스크란")]
    public void The_2026_08_12_dungeons_are_catalogued(int mobCode, string dungeon, string variant, string boss)
    {
        EncounterInfo? info = Shipped().Lookup(mobCode);

        Assert.NotNull(info);
        Assert.Equal(dungeon, info!.Value.DungeonName);
        Assert.Equal(variant, info.Value.VariantLabel);
        Assert.Equal(boss, info.Value.BossName);
    }

    /// <summary>비탄의 설원 (2026-09-09 패치) — the 성역 raid added with client VersionInfo 107. Same sourcing as
    /// the 2026-08-12 pair: every code is a <c>NpcSubType::HeroMonster</c> out of the map's own
    /// <c>MapData.SpawnInfoList</c> (FrozenLament_03/02/01 = dungeonId 620027/620026/620025).
    /// <para>⚠️ The difficulty runs BACKWARDS in the ids — 620025 is 어려움 and 620027 is 쉬움, the second raid to
    /// do this after 무스펠의 성배 (620021 어려움 &lt; 620022 보통). A test that assumes ascending = harder would
    /// pass on every other dungeon and mislabel this one.</para>
    /// <para>⚠️ 델트라스 owns TWO codes per difficulty because the fight swaps actors at phase 2. The reward
    /// table's <c>FinalBossName</c> points at <c>_V01_02</c> (…165/…145/…115), but <c>_V01_01</c>
    /// (…163/…143/…113) has <c>bCanBeAttacked</c> and <c>bAutoDamageAnalyze</c> set and really does take damage —
    /// unlike 무스펠 칼드릭스's invulnerable dummy 2301061. So the phase-1 codes are aliases of the same boss, and
    /// a battle that starts on one must not come out unlabelled or under a second name.</para></summary>
    [Theory]
    // 쉬움 — FrozenLament_03, dungeonId 620027
    [InlineData(2301162, "쉬움", "귀환자 트리톤")]
    [InlineData(2301165, "쉬움", "델트라스")]
    [InlineData(2301163, "쉬움", "델트라스")]  // 1페이즈 별칭
    // 보통 — FrozenLament_02, dungeonId 620026
    [InlineData(2301142, "보통", "귀환자 트리톤")]
    [InlineData(2301145, "보통", "델트라스")]
    [InlineData(2301143, "보통", "델트라스")]  // 1페이즈 별칭
    // 어려움 — FrozenLament_01, dungeonId 620025
    [InlineData(2301112, "어려움", "귀환자 트리톤")]
    [InlineData(2301115, "어려움", "델트라스")]
    [InlineData(2301113, "어려움", "델트라스")]  // 1페이즈 별칭
    public void The_frozen_lament_sanctuary_is_catalogued(int mobCode, string variant, string boss)
    {
        EncounterInfo? info = Shipped().Lookup(mobCode);

        Assert.NotNull(info);
        Assert.Equal("비탄의 설원", info!.Value.DungeonName);
        Assert.Equal(variant, info.Value.VariantLabel);
        Assert.Equal(boss, info.Value.BossName);
    }

    /// <summary>Nailing the phase-1 aliases down as aliases, not as a lookalike second encounter: a battle that
    /// opens on 델트라스 1페이즈 has to upload under the SAME dungeon and difficulty as one that opens on phase 2,
    /// or the same clear lands in two buckets depending on which actor got hit first.</summary>
    [Theory]
    [InlineData(2301163, 2301165)]
    [InlineData(2301143, 2301145)]
    [InlineData(2301113, 2301115)]
    public void Both_deltras_phases_resolve_to_the_same_encounter(int phase1, int phase2)
    {
        EncounterCatalog catalog = Shipped();

        EncounterInfo first = catalog.Lookup(phase1)!.Value;
        EncounterInfo second = catalog.Lookup(phase2)!.Value;

        Assert.Equal(second.DungeonName, first.DungeonName);
        Assert.Equal(second.VariantLabel, first.VariantLabel);
        Assert.Equal(second.BossName, first.BossName);
        Assert.True(catalog.IsSupported(phase1));  // the upload gate must not drop a phase-1 opener
    }

    /// <summary>The gate has to be tight where it matters: a field boss must NOT be uploadable, or we are back
    /// to spending a request the server answers 400 for.</summary>
    [Fact]
    public void A_field_boss_is_not_supported()
    {
        EncounterCatalog catalog = Shipped();

        Assert.False(catalog.IsSupported(2600068)); // 정령왕 아그로 (어비스 필드보스)
        Assert.False(catalog.IsSupported(2600520)); // 처형관 드라모스
    }

    /// <summary>Category labels are the web's three, and every dungeon carries one.</summary>
    [Fact]
    public void Categories_are_the_three_the_web_publishes()
    {
        EncounterCatalog catalog = Shipped();

        var categories = AllCodes(catalog)
            .Select(code => catalog.Lookup(code)!.Value.Category)
            .Distinct()
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["성역", "원정", "초월"], categories);
    }

    // The catalog exposes lookups rather than an enumeration, so walk the shipped asset's codes directly.
    private static IEnumerable<int> AllCodes(EncounterCatalog catalog)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AssetsJsonDir(), "encounters.json")));

        foreach (var dungeon in doc.RootElement.GetProperty("dungeons").EnumerateArray())
        {
            foreach (var variant in dungeon.GetProperty("variants").EnumerateArray())
            {
                foreach (var pair in variant.GetProperty("mobs").EnumerateArray())
                {
                    yield return pair[0].GetInt32();
                }
            }
        }
    }
}
