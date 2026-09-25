using System.IO;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// Spec for the supported-encounter catalog: the upload gate ("would the stats web accept this boss?") and the
/// difficulty/stage suffix the meter puts on a boss name.
/// </summary>
public sealed class EncounterCatalogTests
{
    // 바크론의 공중섬 is the interesting shape: four difficulties, the same three boss NAMES in each, and a
    // different mobCode per difficulty. 심연의 재련 stands in for a single-variant (성역) dungeon.
    private const string Json = """
    {
      "dungeons": [
        {
          "key": "expedition-bakron-floating-island",
          "category": "원정",
          "categoryOrd": 1,
          "name": "바크론의 공중섬",
          "variantType": "difficulty",
          "bosses": [{"index": 1, "name": "티에"}, {"index": 2, "name": "타몬"}, {"index": 3, "name": "바크론"}],
          "variants": [
            {"label": "보통", "dungeonId": 600072, "difficulty": "보통", "stage": null,
             "mobs": [[2300810, 1], [2300811, 2], [2300812, 3]]},
            {"label": "시련", "dungeonId": 600074, "difficulty": "시련", "stage": null,
             "mobs": [[2300580, 1], [2300581, 2], [2300582, 3]]}
          ]
        },
        {
          "key": "sanctuary-rudra",
          "category": "성역",
          "categoryOrd": 3,
          "name": "심연의 재련 : 루드라",
          "variantType": "all",
          "bosses": [{"index": 1, "name": "영겁의 루드라"}],
          "variants": [{"label": "전체", "dungeonId": 600082, "mobs": [[2301014, 1]]}]
        }
      ]
    }
    """;

    [Fact]
    public void Maps_a_mob_code_to_its_dungeon_variant_and_boss()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(Json);

        EncounterInfo? trial = catalog.Lookup(2300582);
        Assert.NotNull(trial);
        Assert.Equal("바크론의 공중섬", trial!.Value.DungeonName);
        Assert.Equal("원정", trial.Value.Category);
        Assert.Equal("시련", trial.Value.VariantLabel);
        Assert.Equal(600074, trial.Value.DungeonId);
        Assert.Equal(3, trial.Value.BossIndex);
        Assert.Equal("바크론", trial.Value.BossName);
    }

    /// <summary>The whole point of the table: the same boss name at two difficulties is two mobCodes, so the
    /// difficulty is recoverable without knowing the map.</summary>
    [Fact]
    public void The_same_boss_at_another_difficulty_is_a_different_code()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(Json);

        Assert.Equal("시련", catalog.Lookup(2300582)!.Value.VariantLabel);
        Assert.Equal("보통", catalog.Lookup(2300812)!.Value.VariantLabel);
        Assert.Equal("바크론", catalog.Lookup(2300582)!.Value.BossName);
        Assert.Equal("바크론", catalog.Lookup(2300812)!.Value.BossName);
    }

    [Fact]
    public void Display_name_appends_the_difficulty()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(Json);

        Assert.Equal("바크론 (시련)", catalog.DisplayName(2300582, "바크론"));
        Assert.Equal("바크론 (보통)", catalog.DisplayName(2300812, "바크론"));
    }

    /// <summary>The 시련 is the one encounter whose difficulty its boss code cannot express — all sixteen levels
    /// share one dungeonId and one set of codes — so it alone accepts the affix reader's answer.</summary>
    [Fact]
    public void Display_name_takes_the_variant_override_for_the_trial()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(Json);

        Assert.Equal("바크론 (시련 13~16단계)", catalog.DisplayName(2300582, "바크론", "시련 13~16단계"));
    }

    /// <summary>Everywhere else the boss code IS the difficulty, so an override could only overwrite a fact with
    /// a guess. Applying it unconditionally is what stamped "(시련 13~16단계)" onto every dungeon that followed a
    /// trial — the difficulty tracker latched, and this had no opinion about who it was labelling.</summary>
    [Fact]
    public void Display_name_ignores_the_variant_override_for_every_other_encounter()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(Json);

        Assert.Equal("바크론 (보통)", catalog.DisplayName(2300812, "바크론", "시련 13~16단계"));
        Assert.Equal("영겁의 루드라", catalog.DisplayName(2301014, "영겁의 루드라", "시련 13~16단계"));
        Assert.Equal("정령왕 아그로", catalog.DisplayName(2600068, "정령왕 아그로", "시련 13~16단계"));
    }

    /// <summary>A dungeon with one variant has nothing to disambiguate — "(전체)" would be noise.</summary>
    [Fact]
    public void Display_name_leaves_a_single_variant_dungeon_alone()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(Json);

        Assert.Equal("영겁의 루드라", catalog.DisplayName(2301014, "영겁의 루드라"));
    }

    /// <summary>Field bosses, trash and brand-new content aren't in the table; their names must pass through
    /// untouched rather than being blanked or decorated.</summary>
    [Fact]
    public void Display_name_passes_an_uncatalogued_mob_through()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(Json);

        Assert.Equal("정령왕 아그로", catalog.DisplayName(2600068, "정령왕 아그로"));
    }

    /// <summary>The live mob name wins over the catalog's — the web's display names can drift from mobs.json
    /// (e.g. "바실루스" vs "위악의 바실루스"), and only the SUFFIX should come from the catalog.</summary>
    [Fact]
    public void Display_name_keeps_the_live_mob_name()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(Json);

        Assert.Equal("위대한 바크론 (시련)", catalog.DisplayName(2300582, "위대한 바크론"));
    }

    // 비탄의 설원 after the 2026-09-25 split: one live mob name ("델트라스") behind two catalog bosses, and a boss
    // index that is not the array position (2페이즈 kept 2, 1페이즈 was appended as 3).
    private const string PhasedJson = """
    {
      "dungeons": [
        {
          "key": "sanctuary-snowfield-of-lament",
          "category": "성역",
          "categoryOrd": 3,
          "name": "비탄의 설원",
          "variantType": "difficulty",
          "bosses": [{"index": 1, "name": "귀환자 트리톤"}, {"index": 3, "name": "델트라스 1페이즈"}, {"index": 2, "name": "델트라스 2페이즈"}],
          "variants": [
            {"label": "어려움", "dungeonId": 620025, "difficulty": "어려움", "stage": null,
             "mobs": [[2301112, 1], [2301113, 3], [2301115, 2]]}
          ]
        }
      ]
    }
    """;

    /// <summary>The one exception to "the live name wins": when the catalog name is the live name plus a
    /// space-separated qualifier, the web split one mob into several bosses and only the catalog knows which is
    /// which. mobs.json calls all six 델트라스 codes "델트라스".</summary>
    [Fact]
    public void Display_name_takes_the_catalog_name_when_it_qualifies_the_live_name()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(PhasedJson);

        Assert.Equal("델트라스 1페이즈 (어려움)", catalog.DisplayName(2301113, "델트라스"));
        Assert.Equal("델트라스 2페이즈 (어려움)", catalog.DisplayName(2301115, "델트라스"));
        Assert.Equal(("델트라스 1페이즈", "어려움", "비탄의 설원"), catalog.DisplayParts(2301113, "델트라스"));
        Assert.Equal(("델트라스 2페이즈", "어려움", "비탄의 설원"), catalog.DisplayParts(2301115, "델트라스"));
        Assert.Equal("귀환자 트리톤 (어려움)", catalog.DisplayName(2301112, "귀환자 트리톤"));
    }

    /// <summary>The index is the web's stored coordinate, read from <c>bosses[].index</c> — not the position in
    /// the array.</summary>
    [Fact]
    public void Boss_index_comes_from_the_bosses_entry_not_its_position()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(PhasedJson);

        Assert.Equal(3, catalog.Lookup(2301113)!.Value.BossIndex);
        Assert.Equal(2, catalog.Lookup(2301115)!.Value.BossIndex);
        Assert.Equal("델트라스 1페이즈", catalog.Lookup(2301113)!.Value.BossName);
    }

    /// <summary>The exception has to stay narrow. A catalog name that merely CONTAINS the live name, or that is
    /// the shorter of the two ("바실루스" for a live "위악의 바실루스"), keeps the live name.</summary>
    [Theory]
    [InlineData("델트라스의 심장", "델트라스", "델트라스 (어려움)")]      // no space after the live name
    [InlineData("바실루스", "위악의 바실루스", "위악의 바실루스 (어려움)")] // catalog is the shorter name
    [InlineData("가라앉은 에몬", "에몬", "에몬 (어려움)")]                 // qualifier in front, not behind
    public void Display_name_keeps_the_live_name_unless_the_catalog_extends_it(string catalogName, string liveName, string expected)
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(PhasedJson.Replace("델트라스 1페이즈", catalogName));

        Assert.Equal(expected, catalog.DisplayName(2301113, liveName));
    }

    [Fact]
    public void Supported_covers_exactly_the_catalogued_codes()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(Json);

        Assert.True(catalog.IsSupported(2300582));
        Assert.True(catalog.IsSupported(2301014));
        Assert.False(catalog.IsSupported(2600068)); // a field boss the web has no statistics for
    }

    /// <summary>Fail-open. A missing or broken asset must degrade to the pre-catalog behaviour (upload
    /// everything) rather than silently stopping every upload.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"dungeons\":[]}")]
    public void An_empty_catalog_gates_nothing(string json)
    {
        EncounterCatalog catalog = json.Length == 0 ? EncounterCatalog.Empty : EncounterCatalog.Parse(json);

        Assert.False(catalog.IsLoaded);
        Assert.True(catalog.IsSupported(2600068));
        Assert.True(catalog.IsSupported(2300582));
        Assert.Equal("아무개", catalog.DisplayName(2300582, "아무개"));
    }

    /// <summary>The fail-open contract has to cover PARTIAL damage. Individual dungeons are skipped silently,
    /// so a seed whose schema shifted could leave a fragment standing — and a catalog that "loaded" gates every
    /// code it doesn't have. A fragment must gate nothing rather than gate almost everything.</summary>
    [Fact]
    public void A_fragment_of_the_expected_catalog_gates_nothing()
    {
        EncounterCatalog fragment = EncounterCatalog.Parse(Json, minimumCodes: 100);

        Assert.False(fragment.IsLoaded);
        Assert.True(fragment.IsSupported(2600068));
        Assert.True(fragment.IsSupported(2300582));
    }

    [Fact]
    public void A_full_sized_catalog_passes_the_floor()
    {
        Assert.True(EncounterCatalog.Parse(Json, minimumCodes: 2).IsLoaded);
    }

    /// <summary>A blank (not empty) mob name must not render as a bare suffix — " (시련)" with nothing
    /// in front reads as a rendering bug.</summary>
    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    public void Display_name_does_not_leave_a_bare_suffix(string blank)
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(Json);

        // Falls back to the catalog's own boss name rather than emitting "  (시련)".
        Assert.Equal("바크론 (시련)", catalog.DisplayName(2300582, blank));
    }

    /// <summary>The trial needs this: every one of its levels 4~16 shares one dungeonId and one set of boss
    /// codes, so the catalogue can only ever say "시련" and the level has to come from the packet stream.</summary>
    [Fact]
    public void A_variant_override_replaces_the_catalogued_label()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(Json);

        Assert.Equal("바크론 (시련 16단계)", catalog.DisplayName(2300582, "바크론", "시련 16단계"));
        Assert.Equal("바크론 (시련 13~16단계)", catalog.DisplayName(2300582, "바크론", "시련 13~16단계"));
        Assert.Equal("바크론 (시련)", catalog.DisplayName(2300582, "바크론", null));
        Assert.Equal("바크론 (시련)", catalog.DisplayName(2300582, "바크론", "   "));
    }

    /// <summary>An override must not conjure a suffix onto a dungeon that has none, nor onto an
    /// uncatalogued boss.</summary>
    [Fact]
    public void A_variant_override_does_not_add_a_suffix_where_there_was_none()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse(Json);

        Assert.Equal("영겁의 루드라", catalog.DisplayName(2301014, "영겁의 루드라", "시련 9단계"));
        Assert.Equal("정령왕 아그로", catalog.DisplayName(2600068, "정령왕 아그로", "시련 9단계"));
    }

    [Fact]
    public void A_missing_file_loads_as_empty_instead_of_throwing()
    {
        EncounterCatalog catalog = EncounterCatalog.Load(Path.Combine(Path.GetTempPath(), "no-such-encounters.json"));

        Assert.False(catalog.IsLoaded);
        Assert.Equal(0, catalog.Count);
    }

    [Fact]
    public void A_malformed_variant_costs_only_that_variant()
    {
        EncounterCatalog catalog = EncounterCatalog.Parse("""
        {
          "dungeons": [
            {
              "key": "d", "category": "원정", "categoryOrd": 1, "name": "던전", "variantType": "difficulty",
              "bosses": [{"index": 1, "name": "보스"}],
              "variants": [
                {"label": "보통", "dungeonId": 1, "mobs": [[111, 1], ["nope"], [222]]},
                {"label": "어려움", "dungeonId": 2, "mobs": [[333, 1]]}
              ]
            }
          ]
        }
        """);

        Assert.Equal(2, catalog.Count);
        Assert.Equal("보통", catalog.Lookup(111)!.Value.VariantLabel);
        Assert.Equal("어려움", catalog.Lookup(333)!.Value.VariantLabel);
    }

    [Fact]
    public void DataManager_exposes_the_loaded_catalog()
    {
        var dm = new DataManager();
        Assert.False(dm.Encounters.IsLoaded); // nothing loaded yet -> gate is inert

        dm.LoadEncounters(EncounterCatalog.Parse(Json));

        Assert.True(dm.Encounters.IsSupported(2300582));
        Assert.False(dm.Encounters.IsSupported(2600068));
    }
}
