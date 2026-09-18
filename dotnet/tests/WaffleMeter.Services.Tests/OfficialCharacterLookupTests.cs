using WaffleMeter.Data;
using WaffleMeter.Services;
using Xunit;

namespace WaffleMeter.Services.Tests;

public sealed class OfficialCharacterLookupTests
{
    private const string SearchJson = """
        {"list":[
          {"name":"<b>Waffle</b>","serverId":3,"characterId":"abc%3D","level":80,"pcId":5},
          {"name":"Waffle","serverId":3,"characterId":"low%3D","level":50,"pcId":5}
        ]}
        """;

    private const string EquipmentJson = """
        {"skill":{"skillList":[
          {"acquired":1,"equip":1,"id":11000001,"skillLevel":3},
          {"acquired":1,"equip":0,"id":11000002,"skillLevel":5},
          {"acquired":0,"equip":1,"id":11000003,"skillLevel":2}
        ]}}
        """;

    private const string InfoJson = """{"profile":{"combatPower":12345}}""";

    /// <summary>공식 검색 API 는 race 를 필수로 받는다 — 빈 값·생략·0 은 HTTP 400 {"code":"race invalid"} 다
    /// (2026-09-05 실측). 그 400 이 체인 1단계를 죽여 파티 신청 배지가 100% 사라졌었다. 이 가짜 서버는 같은
    /// 계약을 흉내내 회귀를 막는다.</summary>
    private static string RouteStrictRace(string url, List<string> seen, string matchingRace)
    {
        if (url.Contains("/api/search/character"))
        {
            seen.Add(url);
            if (!url.Contains("race=1") && !url.Contains("race=2"))
            {
                throw new HttpRequestException("race invalid"); // 서버가 400 을 던지는 자리
            }

            return url.Contains("race=" + matchingRace) ? SearchJson : """{"list":[]}""";
        }

        return Route(url);
    }

    /// <summary>천족 서버(1001~1021)의 캐릭터를 담은 검색 결과.</summary>
    private const string ElyosSearchJson = """{"list":[{"name":"Waffle","serverId":1018,"characterId":"abc%3D","level":80,"pcId":5}]}""";

    /// <summary>마족 서버(2001~2021)의 캐릭터.</summary>
    private const string AsmoSearchJson = """{"list":[{"name":"Waffle","serverId":2003,"characterId":"abc%3D","level":80,"pcId":5}]}""";

    private static string RouteForServer(string url, List<string> seen, string body)
    {
        if (url.Contains("/api/search/character"))
        {
            seen.Add(url);
            if (!url.Contains("race=1") && !url.Contains("race=2"))
            {
                throw new HttpRequestException("race invalid");
            }

            return body;
        }

        return Route(url);
    }

    [Fact]
    public void The_race_comes_from_the_server_id_so_one_search_is_enough()
    {
        // 🔑 서버 id 가 곧 진영이다 — 1001~1021 천족. 종족을 추측하거나 두 값을 시도할 이유가 없다.
        var seen = new List<string>();
        var lookup = new OfficialCharacterLookup(u => RouteForServer(u, seen, ElyosSearchJson), () => 0);

        Assert.NotNull(lookup.LookupBlocking("Waffle", 1018, null));
        Assert.Single(seen);
        Assert.Contains("race=1", seen[0]);
    }

    [Fact]
    public void An_asmodian_server_searches_the_second_race_directly()
    {
        var seen = new List<string>();
        var lookup = new OfficialCharacterLookup(u => RouteForServer(u, seen, AsmoSearchJson), () => 0);

        Assert.NotNull(lookup.LookupBlocking("Waffle", 2003, null));
        Assert.Single(seen);
        Assert.Contains("race=2", seen[0]);
    }

    [Fact]
    public void The_search_never_sends_an_empty_race()
    {
        // 빈 값·생략·0 은 서버가 HTTP 400 {"code":"race invalid"} 로 거절한다 — 그 400 이 체인 1단계를 죽여
        // 파티 신청 배지가 100% 사라졌던 회귀다.
        var seen = new List<string>();
        var lookup = new OfficialCharacterLookup(u => RouteForServer(u, seen, ElyosSearchJson), () => 0);

        lookup.LookupBlocking("Waffle", 1018, null);

        Assert.All(seen, u => Assert.DoesNotContain("race=&", u));
        Assert.All(seen, u => Assert.False(u.EndsWith("race=", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_unknown_server_range_falls_back_to_trying_both_races()
    {
        // 진영은 둘뿐이므로, 미래에 새 대역(3xxx)이 생겨도 배지가 사라지지는 않게 둔다.
        var seen = new List<string>();
        var lookup = new OfficialCharacterLookup(u => RouteStrictRace(u, seen, "2"), () => 0);

        Assert.NotNull(lookup.LookupBlocking("Waffle", 3, null));
        Assert.Equal(2, seen.Count);
        Assert.Contains("race=1", seen[0]);
        Assert.Contains("race=2", seen[1]);
    }

    private static string Route(string url)
    {
        if (url.Contains("/api/search/character"))
        {
            return SearchJson;
        }

        if (url.Contains("/api/character/equipment"))
        {
            return EquipmentJson;
        }

        if (url.Contains("/api/character/info"))
        {
            return InfoJson;
        }

        throw new InvalidOperationException("unexpected url " + url);
    }

    // Two same-name same-server namesakes with DIFFERENT classes: a higher-level 궁성 (pcId 14) and a
    // lower-level 치유성 (pcId 29). Without a hint, maxByLevel picks the 궁성; with a matching job hint the
    // correct lower-level character must win (H5 namesake disambiguation).
    private const string NamesakeJson = """
        {"list":[
          {"name":"Twin","serverId":3,"characterId":"hi%3D","level":80,"pcId":14},
          {"name":"Twin","serverId":3,"characterId":"lo%3D","level":50,"pcId":29}
        ]}
        """;

    private static string RouteNamesake(string url)
    {
        if (url.Contains("/api/search/character")) return NamesakeJson;
        if (url.Contains("/api/character/equipment")) return EquipmentJson;
        if (url.Contains("/api/character/info")) return InfoJson;
        throw new InvalidOperationException("unexpected url " + url);
    }

    [Fact]
    public void Disambiguates_same_name_namesakes_by_fallback_job()
    {
        var lookup = new OfficialCharacterLookup(RouteNamesake, clock: () => 0);

        OfficialCharacterInfo? info = lookup.LookupBlocking("Twin", 3, fallbackJob: JobClass.CLERIC);

        Assert.NotNull(info);
        Assert.Equal(JobClass.CLERIC, info!.Job); // matched the hint, beating the higher-level 궁성
    }

    [Fact]
    public void Falls_back_to_highest_level_when_no_job_hint()
    {
        var lookup = new OfficialCharacterLookup(RouteNamesake, clock: () => 0);

        OfficialCharacterInfo? info = lookup.LookupBlocking("Twin", 3, fallbackJob: null);

        Assert.NotNull(info);
        Assert.Equal(JobClass.RANGER, info!.Job); // maxByLevel unchanged when there is no hint
    }

    [Fact]
    public void Resolves_job_power_and_equipped_skills()
    {
        var lookup = new OfficialCharacterLookup(Route, clock: () => 0);

        OfficialCharacterInfo? info = lookup.LookupBlocking("Waffle", 3, fallbackJob: JobClass.CLERIC);

        Assert.NotNull(info);
        Assert.Equal("Waffle", info!.Nickname);
        Assert.Equal(3, info.Server);
        Assert.Equal(JobClass.GLADIATOR, info.Job); // pcId 5 -> GLADIATOR (not the fallback)
        Assert.Equal(12345, info.Power);
        Assert.Equal(new Dictionary<int, int> { [11000001] = 3 }, info.Skills); // only acquired>0 && equip==1

        // 🔑 acquired 지만 equip:0 인 것은 버리지 않고 따로 담는다 — 공식 홈이 패시브를 영원히 equip:0 으로
        // 주기 때문이다(2026-08-23 라이브 108명 실측). 레벨은 같은 행에 그대로 실려 온다.
        // acquired:0 은 아예 배운 적이 없는 것이라 어느 쪽에도 안 들어간다.
        Assert.Equal(new Dictionary<int, int> { [11000002] = 5 }, info.UnequippedOrEmpty);
    }

    [Fact]
    public void Caches_hits_and_skips_further_http()
    {
        int calls = 0;
        string Counting(string url)
        {
            calls++;
            return Route(url);
        }

        var lookup = new OfficialCharacterLookup(Counting, clock: () => 1000);

        OfficialCharacterInfo? first = lookup.LookupBlocking("Waffle", 3, null);
        int afterFirst = calls;
        OfficialCharacterInfo? second = lookup.LookupBlocking("Waffle", 3, null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(3, afterFirst);   // search + equipment + info
        Assert.Equal(3, calls);        // second call served from cache, no new HTTP
    }

    [Fact]
    public void Falls_back_to_job_when_pcId_absent()
    {
        string Search(string url) => url.Contains("/api/search/character")
            ? """{"list":[{"name":"Waffle","serverId":3,"characterId":"abc","level":80}]}"""
            : Route(url);

        var lookup = new OfficialCharacterLookup(Search, clock: () => 0);

        OfficialCharacterInfo? info = lookup.LookupBlocking("Waffle", 3, fallbackJob: JobClass.RANGER);

        Assert.NotNull(info);
        Assert.Equal(JobClass.RANGER, info!.Job); // character.job null -> fallback
    }

    [Fact]
    public void Returns_null_and_caches_miss_when_not_found()
    {
        int calls = 0;
        string Empty(string url)
        {
            calls++;
            return url.Contains("/api/search/character") ? """{"list":[]}""" : Route(url);
        }

        var lookup = new OfficialCharacterLookup(Empty, clock: () => 0);

        Assert.Null(lookup.LookupBlocking("Ghost", 1018, null));
        Assert.Equal(1, calls); // only the search call; no equipment/info
        Assert.Null(lookup.LookupBlocking("Ghost", 1018, null));
        Assert.Equal(1, calls); // miss cached -> no new HTTP
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData("", 3)]
    [InlineData("   ", 3)]
    [InlineData("Waffle", 0)]
    [InlineData("Waffle", -1)]
    public void Guards_blank_nickname_and_nonpositive_server(string? nickname, int server)
    {
        var lookup = new OfficialCharacterLookup(_ => throw new InvalidOperationException("must not hit HTTP"), clock: () => 0);
        Assert.Null(lookup.LookupBlocking(nickname, server, null));
    }
}
