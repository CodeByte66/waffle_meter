using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using WaffleMeter.Data;

namespace WaffleMeter.Services;

/// <summary>
/// Verbatim port of Kotlin <c>official.OfficialCharacterLookup</c>: resolves a character's job,
/// combat power, and equipped skills from the official aion2 site, with a TTL cache (6h hits /
/// 10min misses) and in-flight de-duplication. Used for the INITIAL combat-power value; live power
/// is parsed from packets ([[combat-power-reverify]]). Implements <see cref="IOfficialCharacterLookup"/>
/// (defined in the data layer) so <see cref="DataManager"/> can consume it without referencing Services.
///
/// HTTP is injected (<c>httpGet</c>: url -&gt; JSON body, throwing on non-2xx) so the parsing is
/// unit-testable without a network; the clock is injected too (TTL determinism), defaulting to wall
/// clock like the rest of the migration's clock seam.
/// </summary>
public sealed class OfficialCharacterLookup : IOfficialCharacterLookup
{
    private const string BaseUrl = "https://aion2.plaync.com";
    private const long SuccessTtlMs = 6L * 60 * 60 * 1000;
    private const long MissTtlMs = 10L * 60 * 1000;
    private const int ConnectTimeoutMs = 3_000;
    private const int ReadTimeoutMs = 5_000;

    private static readonly HttpClient SharedClient = new(
        new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromMilliseconds(ConnectTimeoutMs) })
    {
        Timeout = TimeSpan.FromMilliseconds(ReadTimeoutMs),
    };

    private readonly Func<string, string> _httpGet;
    private readonly Func<long> _clock;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    public OfficialCharacterLookup(Func<string, string>? httpGet = null, Func<long>? clock = null)
    {
        _httpGet = httpGet ?? DefaultHttpGet;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void LookupAsync(string? nickname, int server, JobClass? fallbackJob, Action<OfficialCharacterInfo> callback)
    {
        string? normalized = NormalizeNickname(nickname);
        if (normalized == null || server <= 0)
        {
            return;
        }

        string key = CacheKey(normalized, server);
        long now = _clock();
        if (_cache.TryGetValue(key, out CacheEntry? cached))
        {
            if (cached.ExpiresAt > now)
            {
                if (cached.Info != null)
                {
                    callback(cached.Info);
                }

                return;
            }

            _cache.TryRemove(KeyValuePair.Create(key, cached));
        }

        if (!_inFlight.TryAdd(key, 0))
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                OfficialCharacterInfo? info = Lookup(normalized, server, fallbackJob);
                _cache[key] = new CacheEntry(info, now + (info == null ? MissTtlMs : SuccessTtlMs));
                if (info != null)
                {
                    callback(info);
                }
            }
            catch
            {
                _cache[key] = new CacheEntry(null, now + MissTtlMs);
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        });
    }

    public OfficialCharacterInfo? LookupBlocking(string? nickname, int server, JobClass? fallbackJob)
    {
        string? normalized = NormalizeNickname(nickname);
        if (normalized == null || server <= 0)
        {
            return null;
        }

        string key = CacheKey(normalized, server);
        long now = _clock();
        if (_cache.TryGetValue(key, out CacheEntry? cached))
        {
            if (cached.ExpiresAt > now)
            {
                return cached.Info;
            }

            _cache.TryRemove(KeyValuePair.Create(key, cached));
        }

        try
        {
            OfficialCharacterInfo? info = Lookup(normalized, server, fallbackJob);
            _cache[key] = new CacheEntry(info, now + (info == null ? MissTtlMs : SuccessTtlMs));
            return info;
        }
        catch
        {
            _cache[key] = new CacheEntry(null, now + MissTtlMs);
            return null;
        }
    }

    private OfficialCharacterInfo? Lookup(string nickname, int server, JobClass? fallbackJob)
    {
        CharacterSearchResult? character = FindCharacter(nickname, server, fallbackJob);
        if (character == null)
        {
            return null;
        }

        (IReadOnlyDictionary<int, int> equipped, IReadOnlyDictionary<int, int> unequipped) =
            FetchSkills(character.CharacterId, character.ServerId);
        int power = FetchCombatPower(character.CharacterId, character.ServerId);
        return new OfficialCharacterInfo(
            nickname,
            character.ServerId,
            character.Job ?? fallbackJob,
            power,
            equipped,
            unequipped);
    }

    /// <summary>공식 검색 API 가 요구하는 종족 값을 서버 id 에서 뽑는다. 1 = 천족, 2 = 마족.
    /// <para>🔴 <b>필수값이다.</b> 빈 값·파라미터 생략·0 은 전부 <c>HTTP 400 {"code":"race invalid"}</c> 로
    /// 떨어진다(2026-09-05 실측). 예전에는 빈 값이 통했고, 서버가 바꾼 뒤로는 검색이 체인 1단계라 전체가 죽어
    /// <b>파티 신청 배지가 100% 안 떴다</b> — 조회가 던지면 호출자가 콜백 없이 10분짜리 실패를 캐시하므로 로그에도
    /// 화면에도 흔적이 남지 않는다.</para>
    /// <para>🔑 <b>서버 id 가 곧 진영이다</b> — 1001~1021 천족, 2001~2021 마족. 그래서 종족을 추측하거나 두 값을
    /// 차례로 시도할 필요가 없다. 실측(2026-09-05): 서버 1018 은 race=1 에서만, 2003·2011 은 race=2 에서만 결과가
    /// 나온다(반대쪽은 200-빈목록). 같은 사실이 <c>MeterFormat.ServerTier</c> 와 <c>AetherRoster</c> 주석에도 이미
    /// 적혀 있다.</para></summary>
    private static int RaceFor(int server) => server / 1000;

    /// <summary>알 수 없는 서버 대역(미래에 3xxx 가 생기는 등)일 때만 쓰는 폴백. 진영은 둘뿐이므로 차례로
    /// 시도하면 최소한 배지가 사라지지는 않는다.</summary>
    private static readonly int[] FallbackRaces = [1, 2];

    private CharacterSearchResult? FindCharacter(string nickname, int server, JobClass? fallbackJob)
    {
        int race = RaceFor(server);
        if (race is 1 or 2)
        {
            return FindCharacter(nickname, server, fallbackJob, race);
        }

        foreach (int fallback in FallbackRaces)
        {
            // 200 인데 못 찾은 경우에만 다음 종족으로 넘어간다. 통신 실패는 그대로 위로 던져 호출자가
            // 실패로 캐시하게 둔다 — 여기서 삼키면 네트워크가 끊긴 동안 요청이 두 배로 나간다.
            if (FindCharacter(nickname, server, fallbackJob, fallback) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private CharacterSearchResult? FindCharacter(string nickname, int server, JobClass? fallbackJob, int race)
    {
        string url = $"{BaseUrl}/api/search/character?{Query(
            ("keyword", nickname),
            ("pcId", ""),
            ("race", race.ToString(CultureInfo.InvariantCulture)),
            ("serverId", server.ToString(CultureInfo.InvariantCulture)),
            ("sort", "desc"),
            ("page", "1"),
            ("size", "20"))}";

        using JsonDocument doc = JsonDocument.Parse(_httpGet(url));
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("list", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        CharacterSearchResult? best = null;
        foreach (JsonElement element in list.EnumerateArray())
        {
            string name = StripHtml(ContentOrNull(element, "name") ?? string.Empty);
            int? serverId = IntOrNull(element, "serverId");
            if (serverId == null || name != nickname || serverId != server)
            {
                continue;
            }

            string? rawId = ContentOrNull(element, "characterId");
            if (rawId == null)
            {
                continue;
            }

            // The search API returns characterId already URL-encoded (e.g. '=' -> '%3D'); decode once
            // here so Query()'s re-encoding does not double it (%3D -> %253D) and break info/equipment.
            var result = new CharacterSearchResult(
                WebUtility.UrlDecode(rawId),
                serverId.Value,
                IntOrNull(element, "level") ?? 0,
                IntOrNull(element, "pcId") is { } pcId ? JobClassInfo.ConvertFromCode(pcId) : null);

            if (best == null)
            {
                best = result;
                continue;
            }

            // Disambiguate same-name same-server namesakes: prefer a candidate whose class matches the
            // local job hint (the snapshot jobByte / own-skill job we already have), otherwise keep the
            // highest level (maxByOrNull{level}, as before). A job-matching candidate beats a non-matching
            // one regardless of level — this avoids stamping a higher-level namesake's class onto the wrong
            // character. When no hint is supplied, behavior is identical to the prior maxByLevel.
            bool resultMatches = fallbackJob != null && result.Job == fallbackJob;
            bool bestMatches = fallbackJob != null && best.Job == fallbackJob;
            if (resultMatches && !bestMatches)
            {
                best = result;
            }
            else if (resultMatches == bestMatches && result.Level > best.Level)
            {
                best = result;
            }
        }

        return best;
    }

    /// <summary>
    /// Split the site's skill list into equipped and merely-owned, both as code → level.
    /// <para>The equip flag is meaningful for actives and stigmas (the character slots them) and meaningless
    /// for passives, which the site reports as <c>equip:0</c> forever. Returning both halves lets the caller
    /// apply the right rule per code instead of this layer guessing; a level of 0 is dropped either way,
    /// because a badge reading "Lv0" is worse than no badge.</para>
    /// </summary>
    private (IReadOnlyDictionary<int, int> Equipped, IReadOnlyDictionary<int, int> Unequipped) FetchSkills(
        string characterId, int server)
    {
        string url = $"{BaseUrl}/api/character/equipment?{Query(
            ("lang", "ko"),
            ("characterId", characterId),
            ("serverId", server.ToString(CultureInfo.InvariantCulture)))}";

        using JsonDocument doc = JsonDocument.Parse(_httpGet(url));
        JsonElement root = doc.RootElement;
        var equippedMap = new Dictionary<int, int>();
        var unequippedMap = new Dictionary<int, int>();
        if (!root.TryGetProperty("skill", out JsonElement skill) || skill.ValueKind != JsonValueKind.Object ||
            !skill.TryGetProperty("skillList", out JsonElement skillList) || skillList.ValueKind != JsonValueKind.Array)
        {
            return (equippedMap, unequippedMap);
        }

        foreach (JsonElement element in skillList.EnumerateArray())
        {
            int acquired = IntOrNull(element, "acquired") ?? 0;
            if (acquired <= 0)
            {
                continue; // not learned — there is no level to report
            }

            int? code = IntOrNull(element, "id");
            if (code == null)
            {
                continue;
            }

            int equipped = IntOrNull(element, "equip") ?? 0;
            if (equipped == 1)
            {
                equippedMap[code.Value] = IntOrNull(element, "skillLevel") ?? 0;
                continue;
            }

            // Owned but not slotted. Only a real level is worth carrying: the passive badges downstream print
            // "Lv{n}", so a 0 would read as a level rather than as "the site did not say".
            int level = IntOrNull(element, "skillLevel") ?? 0;
            if (level > 0)
            {
                unequippedMap[code.Value] = level;
            }
        }

        return (equippedMap, unequippedMap);
    }

    private int FetchCombatPower(string characterId, int server)
    {
        try
        {
            string url = $"{BaseUrl}/api/character/info?{Query(
                ("lang", "ko"),
                ("characterId", characterId),
                ("serverId", server.ToString(CultureInfo.InvariantCulture)))}";

            using JsonDocument doc = JsonDocument.Parse(_httpGet(url));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("profile", out JsonElement profile) && profile.ValueKind == JsonValueKind.Object)
            {
                return IntOrNull(profile, "combatPower") ?? 0;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string DefaultHttpGet(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("User-Agent", "waffle_meter");
        request.Headers.TryAddWithoutValidation("Referer", $"{BaseUrl}/ko-kr/characters/index");

        using HttpResponseMessage response = SharedClient.Send(request);
        string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        int status = (int)response.StatusCode;
        if (status is < 200 or > 299)
        {
            string preview = text.Length > 160 ? text[..160] : text;
            throw new InvalidOperationException($"HTTP {status}: {preview}");
        }

        return text;
    }

    private static string Query(params (string Key, string Value)[] parameters) =>
        string.Join("&", parameters.Select(p => $"{WebUtility.UrlEncode(p.Key)}={WebUtility.UrlEncode(p.Value)}"));

    private static string? NormalizeNickname(string? nickname)
    {
        string? trimmed = nickname?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string CacheKey(string nickname, int server) => $"{server}:{nickname}";

    private static string StripHtml(string value) => Regex.Replace(value, "<[^>]+>", string.Empty).Trim();

    private static bool TryGetPrimitive(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value) &&
            value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? ContentOrNull(JsonElement obj, string name)
    {
        if (!TryGetPrimitive(obj, name, out JsonElement v))
        {
            return null;
        }

        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
    }

    private static int? IntOrNull(JsonElement obj, string name)
    {
        if (!TryGetPrimitive(obj, name, out JsonElement v))
        {
            return null;
        }

        string raw = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }

    private sealed record CharacterSearchResult(string CharacterId, int ServerId, int Level, JobClass? Job);

    private sealed record CacheEntry(OfficialCharacterInfo? Info, long ExpiresAt);
}
