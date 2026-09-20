using System;
using System.Collections.Generic;
using System.IO;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// Covers the instanced-content (원정/초월/성역) boss classification that gives the roster rescue its
/// party-scene proof: ReferenceJson.LoadContentTypes parsing + DataManager.IsInstancedBoss / ContentCategory.
/// </summary>
public sealed class ContentTypesTests
{
    [Fact]
    public void ReferenceJson_loads_categories_to_a_code_map()
    {
        string path = Path.Combine(Path.GetTempPath(), "ct-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{\"categories\":{\"expedition\":[2300508],\"transcendence\":[2300334,2300336],\"sanctuary\":[2301216]}}");
        try
        {
            Dictionary<int, string> map = ReferenceJson.LoadContentTypes(path);

            Assert.Equal(4, map.Count);
            Assert.Equal("expedition", map[2300508]);
            Assert.Equal("transcendence", map[2300334]);
            Assert.Equal("sanctuary", map[2301216]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DataManager_classifies_only_the_loaded_instanced_bosses()
    {
        var dm = new DataManager();
        dm.LoadContentTypes(new Dictionary<int, string> { { 2300334, "transcendence" }, { 2301216, "sanctuary" } });

        Assert.True(dm.IsInstancedBoss(2300334));
        Assert.Equal("transcendence", dm.ContentCategory(2300334));
        Assert.False(dm.IsInstancedBoss(2300473)); // a field-boss circuit code — unclassified, toggle stays inert
        Assert.Null(dm.ContentCategory(2300473));
    }
}
