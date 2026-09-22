using System.Globalization;

namespace WaffleMeter.Capture;

/// <summary>The world map region a field boss belongs to. The 0x9101 timer broadcast is scoped to the map
/// the character is standing in, so the region is also what the alarm picker groups by. 어비스 하층/중층은
/// 서로 다른 맵(20/22)이지만 한 탭으로 묶어 보여준다.</summary>
public enum FieldBossRegion
{
    Verteron,
    Altgard,
    Eltnen,
    Morheim,
    Abyss,
}

/// <summary>One field boss: its mob code, display name, the code the timer packet carries for it
/// (<see cref="TimerCode"/>, 0 when the packet uses the mob code itself), its region and the world map id.</summary>
public readonly record struct FieldBossInfo(int Code, string Name, int TimerCode, FieldBossRegion Region, int MapId)
{
    /// <summary>The value the 0x9101 record actually carries for this boss.</summary>
    public int WireCode => TimerCode > 0 ? TimerCode : Code;
}

/// <summary>
/// Field-boss table for the respawn-timer alerts: boss code → name, region and the code the timer packet
/// carries. Lives in the capture assembly because <see cref="FieldBossTimerParser"/> needs the set of valid
/// wire codes — 베르테론/알트가르드는 몹 코드가 아니라 <b>맵별 슬롯 코드</b>(맵id×100+순번, 예 1010→101001)를
/// 싣고, 어비스는 2001·2201처럼 아주 작은 값이라 숫자 범위만으로는 잡음과 구분되지 않는다.
/// <para>이름은 클라이언트 데이터마인(<c>Assets/json/mobs.json</c>) 기준이며, 지역·슬롯코드·고정 스케줄은
/// 실캡처(모르헤임 맵 1111, 12엔트리)로 교차검증했다.</para>
/// </summary>
public static class FieldBossCatalog
{
    private static readonly FieldBossInfo[] Bosses =
    {
        // ---- 베르테론 (map 1010) ----
        new(2100040, "썩은 쿠타르", 101002, FieldBossRegion.Verteron, 1010),
        new(2100076, "광투사 쿠산", 101004, FieldBossRegion.Verteron, 1010),
        new(2100003, "동쪽의 네이켈", 101001, FieldBossRegion.Verteron, 1010),
        new(2100050, "서쪽의 케르논", 101003, FieldBossRegion.Verteron, 1010),
        new(2100077, "제사장 가르심", 101005, FieldBossRegion.Verteron, 1010),
        new(2100079, "호위병 티간트", 101006, FieldBossRegion.Verteron, 1010),
        new(2100141, "만개한 코린", 101007, FieldBossRegion.Verteron, 1010),
        new(2100177, "분노한 사루스", 101008, FieldBossRegion.Verteron, 1010),
        new(2100178, "피송곳니 프닌", 101009, FieldBossRegion.Verteron, 1010),
        new(2100582, "배교자 레일라", 101010, FieldBossRegion.Verteron, 1010),
        new(2100617, "검은 촉수 라와", 101011, FieldBossRegion.Verteron, 1010),
        new(2100661, "환몽의 카시아", 101012, FieldBossRegion.Verteron, 1010),
        new(2100708, "백부장 데미로스", 101013, FieldBossRegion.Verteron, 1010),
        new(2100718, "신성한 안사스", 101014, FieldBossRegion.Verteron, 1010),
        new(2100876, "수확관리자 모샤브", 101015, FieldBossRegion.Verteron, 1010),
        new(2100877, "감시병기 크나쉬", 101016, FieldBossRegion.Verteron, 1010),
        new(2100988, "학자 라울라", 101017, FieldBossRegion.Verteron, 1010),
        new(2100989, "숲전사 우라무", 101018, FieldBossRegion.Verteron, 1010),
        new(2100991, "추격자 타울로", 101019, FieldBossRegion.Verteron, 1010),
        new(2101016, "연구관 세트람", 101020, FieldBossRegion.Verteron, 1010),
        new(2101074, "영원의 가르투아", 101021, FieldBossRegion.Verteron, 1010),
        new(2101120, "침묵의 타르탄", 101022, FieldBossRegion.Verteron, 1010),
        new(2101122, "영혼 지배자 카샤파", 101023, FieldBossRegion.Verteron, 1010),
        new(2101131, "군단장 라그타", 101024, FieldBossRegion.Verteron, 1010),

        // ---- 알트가르드 (map 1110) ----
        new(2400017, "녹아내린 다나르", 111001, FieldBossRegion.Altgard, 1110),
        new(2400074, "검은 전사 아에드", 111002, FieldBossRegion.Altgard, 1110),
        new(2400140, "충실한 라지트", 111003, FieldBossRegion.Altgard, 1110),
        new(2400141, "광전사 발그", 111004, FieldBossRegion.Altgard, 1110),
        new(2400212, "포식자 가르산", 111005, FieldBossRegion.Altgard, 1110),
        new(2400223, "혈전사 란나르", 111006, FieldBossRegion.Altgard, 1110),
        new(2400274, "기만자 트리드", 111007, FieldBossRegion.Altgard, 1110),
        new(2400335, "푸른물결 켈피나", 111008, FieldBossRegion.Altgard, 1110),
        new(2400353, "총감독관 누타", 111009, FieldBossRegion.Altgard, 1110),
        new(2400358, "참모관 르사나", 111010, FieldBossRegion.Altgard, 1110),
        new(2400419, "별동대장 링크스", 111011, FieldBossRegion.Altgard, 1110),
        new(2400424, "모독자 노블루드", 111012, FieldBossRegion.Altgard, 1110),
        new(2400425, "망혼의 아칸 악시오스", 111013, FieldBossRegion.Altgard, 1110),
        new(2400474, "중독된 하디룬", 111014, FieldBossRegion.Altgard, 1110),
        new(2400504, "처형자 바르시엔", 111015, FieldBossRegion.Altgard, 1110),
        new(2400593, "드라칸 부대병기 구루타", 111016, FieldBossRegion.Altgard, 1110),
        new(2400607, "백전노장 슈자칸", 111017, FieldBossRegion.Altgard, 1110),
        new(2400608, "비전의 카루카", 111018, FieldBossRegion.Altgard, 1110),
        new(2400659, "흑암의 비슈베다", 111019, FieldBossRegion.Altgard, 1110),
        new(2400709, "예리한 쉬라크", 111020, FieldBossRegion.Altgard, 1110),
        new(2400800, "불멸의 가르투아", 111021, FieldBossRegion.Altgard, 1110),
        new(2400853, "군단장 라그타", 111022, FieldBossRegion.Altgard, 1110),
        new(2400854, "영혼 지배자 카샤파", 111023, FieldBossRegion.Altgard, 1110),
        new(2400855, "침묵의 타르탄", 111024, FieldBossRegion.Altgard, 1110),

        // ---- 엘테넨 (map 1011) — 이 지역은 타이머 패킷이 몹 코드를 그대로 싣는다 ----
        new(2101217, "응집된 베레놈", 0, FieldBossRegion.Eltnen, 1011),
        new(2101218, "옛 두목 비고르", 0, FieldBossRegion.Eltnen, 1011),
        new(2101257, "꺾인 날개 츠바인", 0, FieldBossRegion.Eltnen, 1011),
        new(2101278, "탐욕의 이게티스", 0, FieldBossRegion.Eltnen, 1011),
        new(2101279, "생명의 신수 수페르비아", 0, FieldBossRegion.Eltnen, 1011),
        new(2101306, "썩은 뿌리 멜트림", 0, FieldBossRegion.Eltnen, 1011),
        new(2101343, "니호그", 0, FieldBossRegion.Eltnen, 1011),
        new(2101350, "최초의 실험체 크티마", 0, FieldBossRegion.Eltnen, 1011),
        new(2101415, "세 개의 뿔 마이노", 0, FieldBossRegion.Eltnen, 1011),
        new(2101416, "고통의 람푸스", 0, FieldBossRegion.Eltnen, 1011),
        new(2101600, "3부대장 카르코티", 0, FieldBossRegion.Eltnen, 1011),
        new(2101601, "부군단장 비바츠라", 0, FieldBossRegion.Eltnen, 1011),

        // ---- 모르헤임 (map 1111) — 실캡처로 검증된 유일한 지역 ----
        new(2406034, "경계의 방랑자 파르곤", 0, FieldBossRegion.Morheim, 1111),
        new(2406035, "포식의 거수 발라크", 0, FieldBossRegion.Morheim, 1111),
        new(2406071, "핏빛 눈보라 레눌프", 0, FieldBossRegion.Morheim, 1111),
        new(2406093, "서리갑옷 하르칸", 0, FieldBossRegion.Morheim, 1111),
        new(2406094, "푸른 눈물 글레이시아", 0, FieldBossRegion.Morheim, 1111),
        new(2406129, "업화의 날개 피오스", 0, FieldBossRegion.Morheim, 1111),
        new(2406131, "용암심장 바투", 0, FieldBossRegion.Morheim, 1111),
        new(2406132, "정예 심문관 브란트", 0, FieldBossRegion.Morheim, 1111),
        new(2406181, "미쳐버린 파수꾼 불라간", 0, FieldBossRegion.Morheim, 1111),
        new(2406182, "화산 군주 그림니르", 0, FieldBossRegion.Morheim, 1111),
        new(2406990, "3부대장 미나사라", 0, FieldBossRegion.Morheim, 1111),
        new(2406991, "부군단장 사르바카", 0, FieldBossRegion.Morheim, 1111),

        // ---- 어비스 하층 (map 20) ----
        new(2600068, "정령왕 아그로", 2001, FieldBossRegion.Abyss, AbyssLowerMapId),
        new(2600089, "감시자 카이라", 2002, FieldBossRegion.Abyss, AbyssLowerMapId),
        new(2600084, "수호신장 나흐마", 2003, FieldBossRegion.Abyss, AbyssLowerMapId),
        new(2600093, "수호신장 나흐마", 2004, FieldBossRegion.Abyss, AbyssLowerMapId),
        new(2600094, "수호신장 나흐마", 2005, FieldBossRegion.Abyss, AbyssLowerMapId),
        new(2600096, "집행자 타마사", 2006, FieldBossRegion.Abyss, AbyssLowerMapId),
        new(2600097, "집행자 아그로", 2007, FieldBossRegion.Abyss, AbyssLowerMapId),
        new(2600098, "집행자 카이라", 2008, FieldBossRegion.Abyss, AbyssLowerMapId),

        // ---- 어비스 중층 (map 22) ----
        new(2600150, "분노한 수호신장 나흐마", 2201, FieldBossRegion.Abyss, AbyssMiddleMapId),
        new(2600156, "분노한 수호신장 나흐마", 2204, FieldBossRegion.Abyss, AbyssMiddleMapId),
        new(2600520, "처형관 드라모스", 2202, FieldBossRegion.Abyss, AbyssMiddleMapId),
        new(2600521, "반역자 듀칼", 2203, FieldBossRegion.Abyss, AbyssMiddleMapId),
        new(2600522, "파멸자 마라카", 2205, FieldBossRegion.Abyss, AbyssMiddleMapId),
    };

    /// <summary>혼돈의 에레슈란타 하층.</summary>
    public const int AbyssLowerMapId = 20;

    /// <summary>혼돈의 에레슈란타 중층.</summary>
    public const int AbyssMiddleMapId = 22;

    /// <summary>감시자 카이라 (어비스 하층). The server sends a ZEROED timestamp for this one boss in every
    /// capture, so it has no respawn time to remind against. It therefore gets its own clock-based reminder
    /// and is kept out of the boss picker and the timer-driven alarm — see <c>KairaAlarm</c>.
    /// <para>2026-09-02 패치로 <b>4시간마다 100% 확정 출현</b>이 됐다(격자의 앵커는 KST 1시 — 시각은
    /// <c>KairaAlarm.SpawnAnchorHourKst</c> 가 정본이니 여기에 다시 적지 마라).
    /// 종전 이름은 <c>HourlySpawnCode</c> 였는데, 그 이름이 남아 있으면 '매시 정각'이라는 죽은 전제를 계속
    /// 퍼뜨린다 — 이 저장소는 "주석으로만 두면 회귀한다"가 명문 규칙이라 이름 쪽을 고쳤다.</para>
    /// <para>⚠️ 그렇다고 <c>FieldBossFixedSchedule</c> 표에 넣지 마라. 넣는 순간
    /// <c>FieldBossTimerParser</c> 의 폴백이 <b>서버가 명시적으로 0으로 보낸 보스에 미터가 타이머를 지어내고</b>,
    /// 그게 서버 사실인 척 흘러간다. 게다가 그 알림은 그 지역에 있을 때만 울려서 "어디에 있든 울린다"와도
    /// 충돌한다.</para></summary>
    public const int ScheduledSpawnCode = 2600089;

    /// <summary>True when this boss is driven by its own alarm instead of the shared respawn-timer one.</summary>
    public static bool HasOwnAlarm(int code) => code == ScheduledSpawnCode;

    /// <summary>Wire codes the boss table does not list directly, kept so a record that still carries the old
    /// value resolves. 2101349는 옛 표가 쓰던 "맹목적인 니호그" 코드인데 현행 데이터마인엔 보스가 아니고,
    /// 같은 이름의 실보스는 2101343이라 그쪽을 정본으로 삼되 옛 코드도 받아 준다.</summary>
    private static readonly Dictionary<int, int> WireAliases = new() { [2101349] = 2101343 };

    private static readonly Dictionary<int, FieldBossInfo> ByCode =
        Bosses.ToDictionary(b => b.Code);

    private static readonly Dictionary<int, int> BossByWireCode = BuildWireIndex();

    private static readonly Dictionary<int, FieldBossRegion> RegionByMap =
        Bosses.GroupBy(b => b.MapId).ToDictionary(g => g.Key, g => g.First().Region);

    private static Dictionary<int, int> BuildWireIndex()
    {
        var index = new Dictionary<int, int>(Bosses.Length + WireAliases.Count);
        foreach (FieldBossInfo b in Bosses)
        {
            index[b.WireCode] = b.Code;
        }

        foreach ((int wire, int code) in WireAliases)
        {
            index[wire] = code;
        }

        return index;
    }

    /// <summary>Display name for a boss code, or a generic "필드보스" label with the code when unknown.</summary>
    public static string Name(int code)
        => ByCode.TryGetValue(code, out FieldBossInfo b) ? b.Name : $"필드보스 {code.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>True when the code is a known field boss.</summary>
    public static bool IsKnown(int code) => ByCode.ContainsKey(code);

    /// <summary>Every known boss in catalog (= display) order.</summary>
    public static IReadOnlyList<FieldBossInfo> All() => Bosses;

    /// <summary>The bosses of one region, in catalog order.</summary>
    public static IReadOnlyList<FieldBossInfo> InRegion(FieldBossRegion region)
        => Bosses.Where(b => b.Region == region).ToList();

    /// <summary>The region a boss belongs to, or null when the code is unknown.</summary>
    public static FieldBossRegion? Region(int code)
        => ByCode.TryGetValue(code, out FieldBossInfo b) ? b.Region : null;

    /// <summary>Korean label for a region tab.</summary>
    public static string RegionName(FieldBossRegion region) => region switch
    {
        FieldBossRegion.Verteron => "베르테론",
        FieldBossRegion.Altgard => "알트가르드",
        FieldBossRegion.Eltnen => "엘테넨",
        FieldBossRegion.Morheim => "모르헤임",
        FieldBossRegion.Abyss => "어비스",
        _ => "기타",
    };

    /// <summary>The regions in tab order.</summary>
    public static IReadOnlyList<FieldBossRegion> Regions { get; } = new[]
    {
        FieldBossRegion.Verteron, FieldBossRegion.Altgard, FieldBossRegion.Eltnen,
        FieldBossRegion.Morheim, FieldBossRegion.Abyss,
    };

    /// <summary>Maps a value carried by a 0x9101 record to the boss code it stands for.</summary>
    public static bool TryResolveWireCode(int wireCode, out int bossCode)
        => BossByWireCode.TryGetValue(wireCode, out bossCode);

    /// <summary>As <see cref="TryResolveWireCode(int,out int)"/> but only accepts a boss that lives on
    /// <paramref name="mapId"/> — the broadcast is map-scoped, so this rejects coincidental matches.</summary>
    public static bool TryResolveWireCode(int wireCode, int mapId, out int bossCode)
    {
        bossCode = 0;
        return BossByWireCode.TryGetValue(wireCode, out int code)
            && ByCode.TryGetValue(code, out FieldBossInfo b)
            && b.MapId == mapId
            && (bossCode = code) != 0;
    }

    /// <summary>The region a world-map id belongs to (1010 베르테론 / 1011 엘테넨 / 1110 알트가르드 /
    /// 1111 모르헤임 / 20·22 어비스). Verified against a real capture for 모르헤임.</summary>
    public static bool TryResolveRegionForMap(int mapId, out FieldBossRegion region)
        => RegionByMap.TryGetValue(mapId, out region);

    /// <summary>True when the map id is one this catalog knows.</summary>
    public static bool IsKnownMap(int mapId) => RegionByMap.ContainsKey(mapId);
}
