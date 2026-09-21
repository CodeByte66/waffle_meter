using WaffleMeter.Capture;

namespace WaffleMeter.Data;

/// <summary>
/// Verbatim port of the parts of Kotlin <c>DataManager</c> that the DPS pipeline needs: the
/// reference catalogs (mob/skill/buff/blacklist), the runtime repositories, the battle state
/// machine (start/end/dummy), and the packet store. Implements <see cref="ICaptureGameData"/> so
/// the capture parser can drive it directly.
///
/// Kotlin's DataManager is a singleton <c>object</c>; here it is an instance (one per replay/app).
/// Time is read through <see cref="Clock"/> (default wall clock) — set a simulated clock to replay a
/// recorded corpus deterministically, exactly like the Kotlin clock seam.
///
/// Not ported (irrelevant to DPS numbers): the raw-packet logging buffer, the official character API
/// (network) — <see cref="RequestOfficialCharacterLookup"/> is a no-op, matching a no-network run.
/// </summary>
public sealed class DataManager : ICaptureGameData
{
    // Death-rattle window: after a boss dies the game may emit a residual battle-start toggle (0x8D21) on the
    // corpse — swallow only that brief tail. A genuine re-pull happens well after this, so it is never blocked
    // here; and a re-pull whose toggle DOES land inside the window is recovered by _pendingStart (see below).
    // (Was 30 min — far longer than any death rattle — which froze the meter on the previous battle when a
    // re-pull's start-toggle arrived before the boss's fresh HP packet. Upstream Kotlin has no such guard.)
    private const long EndedBattleStartIgnoreMs = 3_000L;

    // A swallowed re-pull start (see _pendingStart) is replayed only if the boss's first HP>0 packet arrives
    // within this window of the suppressed toggle — long enough to cover any realistic in-combat HP delay, short
    // enough that a much-later HP broadcast on the same instance id can't trigger a spurious empty battle.
    private const long PendingStartTtlMs = 60_000L;
    private const long DummyTimeoutMs = 5000L;

    /// <summary>보스 전투 유휴 종료 임계. 추적 중인 타깃에 대해 이 시간 동안 데미지도 HP 보고도 없으면 전투를
    /// 끝난 것으로 본다.
    /// <para>🔑 왜 필요한가: 전투 종료 경로가 <c>0x8D21 toggle==0</c>(그리고 사망) 하나뿐이라, 보스가 죽지도
    /// 종료 토글도 없이 조용해지면 리포트가 <b>무기한</b> 살아 있었다. 버스(캐리)에서 기사가 보스를 끌고 가거나
    /// 승객이 AoI를 벗어나면 정확히 이 상태가 된다 — 서버가 그 엔티티 갱신을 더 이상 보내지 않는다. 2026-08-08
    /// 제보 로그 실측: 나사라크가 HP 43%로 남은 채 41.2초에 끊겨 <b>104초</b> 동안 화면이 고착됐고, 그 사이
    /// 캡처·조립·디스패치는 정상이었다(미터가 멈춘 게 아니다).</para>
    /// <para>값 근거: 코퍼스 29파일 270전투의 <b>전투 중</b> 정상 공백 분포 = 중앙값 0.5s / p90 2.0s / p95 4.7s /
    /// p99 19.1s / <b>최대 27.8s</b>(칼드릭스 페이즈 전환). 60초는 그 최대의 2.2배다. 비대칭이 크기 때문에 길게
    /// 잡는다 — 짧으면 살아있는 전투를 반으로 갈라 저장·업로드가 오염되지만(191M 사건), 길면 고착이 조금 더
    /// 이어질 뿐이다.</para></summary>
    private const long BossIdleTimeoutMs = 60_000L;

    /// <summary>유휴 종료의 <b>두 번째</b> 조건: 이 시간 동안 <b>어떤 타깃에도</b> 데미지가 없어야 한다.
    /// <para>🔑 왜 필요한가: 보스가 무적/비타격 기믹에 들어가면 그 엔티티는 <b>완전 무음</b>이 된다 — 실측
    /// (칼드릭스 27.8초 공백 창 전수조사) 결과 HP·피격·버프 어느 이벤트에도 등장하지 않는다. 즉 공백 길이는
    /// 곧 기믹 길이이고 원리상 상한이 없다. <see cref="BossIdleTimeoutMs"/> 하나만 보면 우리가 관측하지 못한
    /// 더 긴 기믹에서 <b>살아있는 전투가 반으로 갈린다</b>(앞쪽은 사망 없이 저장, 뒤쪽만 킬로 업로드 → DPS·시간
    /// 과소 기록). 그런데 그 기믹 구간에도 파티는 쫄을 계속 때리고 있다 — 실측 창에서 다른 타깃 6종에 수백 건씩.
    /// 그래서 "아무 데도 안 때리고 있다"를 함께 요구하면 기믹 길이와 무관하게 안전해진다.</para>
    /// <para>반대로 버려진 보스(제보 사례)는 파티가 정말로 아무것도 안 때린다 — 실측 t=50~140s 데미지 0건.
    /// 두 경우가 이 신호로 갈린다.</para></summary>
    private const long AnyCombatQuietMs = 20_000L;

    private readonly record struct EndedBattle(int? MobCode, long EndedAt);

    private readonly Dictionary<int, Mob> _mobs = new();
    // Instanced-content (원정/초월/성역) boss mobCode -> category. Loaded from content-types.json; empty until then.
    // 로스터 구제의 '여기는 파티 씬이다' 증거로 쓰인다 — 인스턴스에는 외부인이 없다.
    private readonly Dictionary<int, string> _contentTypes = new();
    private readonly HashSet<int> _buffBlacklist = new();

    private readonly PacketRepository _packetRepository = new();
    private readonly UserRepository _userRepository = new();
    private readonly MobIdRepository _mobIdRepository = new();
    private readonly MobHpRepository _mobHpRepository = new();
    private readonly SummonRepository _summonRepository = new();
    private readonly UseBuffRepository _useBuffRepository = new();
    private readonly SkillCastRepository _skillCastRepository = new();
    private readonly BattleLogRepository _battleLogRepository = new();
    private readonly SkillRepository _skillRepository = new();
    private readonly BuffRepository _buffRepository = new();

    private long _resetEpoch;
    private long _battleRevision;
    private readonly Dictionary<int, EndedBattle> _recentlyEndedBattles = new();
    private int? _activeBattleMobCode;
    // A StartBattle the corpse-guard suppressed (a re-pull whose start-toggle beat the boss's fresh HP packet).
    // Replayed the instant the boss next reports HP>0 (within PendingStartTtlMs), so a genuine re-pull never
    // stays frozen on the previous battle even when the game emits no second start-toggle (see StartBattle +
    // MobHp). At = when it was suppressed, so a stale pending can't fire a spurious battle much later.
    private (int MobId, int? MobCode, long At)? _pendingStart;

    // 시작 토글은 왔는데 그 엔티티의 mobCode가 아직 없어서 전투를 못 연 건들(entityId -> 토글 시각).
    // 보스 스폰(0x3641)은 교전당 1회뿐이고 전투 중 재방송이 없어, 스폰을 놓치거나 늦게 받으면 그 판은 끝까지
    // 안 열렸다. SaveMobId가 도착하면 여기서 되살린다. 플레이어 엔티티도 이 토글을 쏘지만 플레이어에겐
    // SaveMobId가 오지 않으므로 자연히 만료된다. 무한 증식 방지용으로 개수를 제한한다.
    private readonly Dictionary<int, long> _unresolvedStarts = new();
    // Feature 2 (염화의 수호검 한정): 무스펠 성배는 파티가 5/5로 나뉘어 근처 두 '염화의 수호검'을 동시에 잡는다.
    // 단일 _currentTarget은 먼저 교전된 쪽에 primary-lock으로 고정돼, 본인이 반대쪽을 때리면 남의 전투가 보인다.
    // 본인(executor)이 지속적으로 딜을 넣는 수호검으로 _currentTarget이 따라가게 한다. **현재·신규가 둘 다 이
    // 이름일 때만** 켜져, 그 외 인카운터는 지금 그대로(primary-lock) 동작한다.
    private const string SplitBossName = "염화의 수호검";
    private readonly Dictionary<int, (long FirstMs, long LastMs, int Hits)> _selfDamageStreak = new();
    private readonly Dictionary<int, long> _bossEngageAtMs = new(); // instanceId -> 최근 0x8D21 start-toggle 시각(전환 back-date용)
    // instanceId -> (그 값을 쌓은 전투의 리비전, 그 전투에서 관측된 <b>최저</b> 잔여 HP).
    // 전투 기록이 얼려야 할 값이며 라이브 게이지는 쓰지 않는다. 왜 필요한지는 BattleLowMobHp 주석 참조.
    // ⚠️ 리비전을 함께 들고 있는 게 핵심이다. 같은 보스를 바로 다시 걸면 새 전투가 열리는 시점이 직전 판의
    // 저장(리포트 틱)보다 빠를 수 있는데, mobId 만으로 키잉하면 그 저장이 새 판의 만피를 읽어 간다.
    private readonly Dictionary<int, (long Revision, long Low)> _battleLowHp = new();
    private const long SelfStreakGapMs = 2_000L;    // 이 간격 넘으면 스트릭 리셋(스치는 AoE 누적 방지)
    private const long SelfSwitchDwellMs = 1_500L;  // 전환 전 지속 자기딜 요건
    private const int SelfSwitchMinHits = 3;
    private const long CurrentSelfQuietMs = 3_000L; // 현재 표시 타깃을 본인이 아직 때리면 절대 안 뺏김
    private const int SelfStreakCap = 64;
    private const int UnresolvedStartsCap = 64;

    private long _lastDummyHitTime;

    /// <summary>현 타깃에 대해 마지막으로 전투 신호(데미지 또는 HP 보고)를 본 시각. <see cref="BossIdleTimeoutMs"/>
    /// 판정과, 유휴 종료 시 <b>종료 스탬프</b>에 쓴다 — 종료를 <c>now</c>로 찍으면 아무 일도 없던 유휴 구간이
    /// 전투 길이에 들어가 DPS가 희석된다(실측상 정상 전투의 마지막 이벤트→종료 꼬리는 최대 3.5초다).
    /// <para>소비자 스레드가 쓰고 리포트 스레드가 읽는다(<see cref="TickBossBattleIdle"/>). 64비트 정렬된 long의
    /// 읽기/쓰기는 찢어지지 않고, 한 틱 늦게 읽혀도 종료가 한 틱 밀릴 뿐이라 <c>_lastDummyHitTime</c>과 같은
    /// 평범한 필드로 둔다.</para></summary>
    private long _lastBossActivityMs;

    /// <summary>타깃을 가리지 않고 마지막으로 데미지를 본 시각(<see cref="AnyCombatQuietMs"/> 판정용).
    /// 기믹 중 쫄 딜이 여기에 찍혀 "전투가 아직 살아 있다"를 증명한다.</summary>
    private long _lastAnyDamageMs;

    // Training-dummy (허수아비) test mode. Written by the UI / hotkey thread, read by the consumer thread —
    // volatile is enough (a one-tick staleness is harmless). When OFF, a dummy hit never starts/continues a
    // battle so the meter shows NO combat for it; when ON, a dummy hit drives a live battle exactly like a boss
    // until the chosen duration elapses, at which point _dummyCutoff latches and further hits are ignored until
    // a reset clears it. Mode + duration survive resets; only the cutoff latch is cleared.
    private volatile bool _dummyTestMode;
    private volatile int _dummyDurationSec = 60;
    private bool _dummyCutoff; // consumer-thread only: latched once the duration hard cut has fired

    // 허수아비 측정 창은 시계 두 개를 <b>따로</b> 들고 간다. 섞으면 파이프 지연 L 만큼 컷이 일찍 터져
    // 마지막 L ms 의 타격이 분자에서 빠지는데 분모(고정 60,000ms)는 그대로라 DPS 가 조용히 낮게 나온다.
    //  · _dummyWindowStartPacketMs = 첫 타격의 패킷 시각. 리포트 창(BattleStart)과 고정 종료 스탬프의 기준.
    //  · _dummyWindowStartClockMs  = 그때의 소비자 시각. 타격이 멎어도 컷을 <i>발화</i>시키는 틱 판정에만 쓴다.
    // 각 뺄셈의 양변이 언제나 같은 시계다.
    private long _dummyWindowStartPacketMs;
    private long _dummyWindowStartClockMs;

    /// <summary>이 런에 적용되는 측정 길이(ms). <b>창을 열 때 잠근다</b> — 매번 설정을 다시 읽으면, 런 도중에
    /// 측정 시간을 줄였을 때 컷이 <i>이미 지나간</i> 시각을 종료로 찍는다. 그러면 분자(누적 피해)는 그대로인데
    /// 분모만 짧아져 DPS 가 통째로 부풀고, 그 값이 기록에 그대로 남는다. 바뀐 설정은 다음 런부터 적용된다
    /// (설정 문구 "첫 타격부터 설정한 시간만큼"과도 그쪽이 맞다).</summary>
    private long _dummyWindowDurationMs;

    /// <summary>이 런에서 마지막으로 <b>채택된</b> 타격의 패킷 시각. 컷 스탬프가 이보다 앞서지 못하게 막는
    /// 바닥이다 — 스탬프가 실제 집계된 피해보다 앞서면 분자와 분모가 서로 다른 창이 된다.</summary>
    private long _lastAcceptedDummyHitPacketMs;

    /// <summary>컷이 찍은 고정 종료 시각(패킷 시계). 0 = 허수아비 고정 창이 없다(보스 전투 포함).</summary>
    private long _dummyFixedEnd;

    // 허수아비 창이 방금 열렸다는 1회성 신호. 소비자 루프가 이걸 보고 리포트를 즉시 한 번 발행해,
    // 첫 타격과 첫 행 사이의 리포트 주기(기본 500ms)만큼의 공백을 없앤다. 읽는 쪽도 소비자 스레드다.
    private bool _dummyBattleOpened;

    // 컷 이후 드롭된 타격까지 포함해 "마지막으로 허수아비를 때린 순간"(소비자 시계). 재무장 판정 전용이라
    // FlushPacket 에서 0으로 지우면 안 된다 — 0 이면 Clock() - 0 이 거대해져 다음 틱에 즉시 재무장된다.
    private long _lastDummyHitObservedMs;
    private readonly Dictionary<int, long> _officialLookupAttempts = new();
    // Latest full party/raid roster snapshot (0x9702 packet): each member's (nickname, server) + when it
    // arrived. Matched to known uids on demand for the pre-combat party preview (see PartyRoster).
    /// <summary>How stale the 0x9702 roster may be and still be frozen into a saved battle as that battle's
    /// party. Matches the window the roster's other readers already use.</summary>
    private const long RosterFreezeTtlMs = 30L * 60 * 1000;

    private readonly List<(string Nickname, int Server, int Slot)> _partyRoster = new();
    /// <summary>로스터 상태 — <b>슬롯</b>이 키다. 0x9702 스냅샷은 부분으로 오는 것이 정상이라
    /// 합치면서 쌓고, key 가 아니라 슬롯을 키로 잡아야 정원을 구조적으로 못 넘는다(상세는 SavePartyRoster).</summary>
    private readonly Dictionary<int, (string Nickname, int Server, int Slot, int Key)> _partyRosterBySlot = new();
    /// <summary>(닉네임,서버) → 로스터 key. 제거(0x9622)가 key 하나로만 오기 때문에 미리 모아 둔다.</summary>
    private readonly Dictionary<(string Nickname, int Server), int> _rosterKeys = new();
    /// <summary>0x9702 헤더의 <c>_limit_member</c>(방 정원). 성역=10 · 파티=5 로 실측된다.
    /// <para>🔑 이것이 <c>PartyRosterSize</c> 의 정본이다 — 통계웹 스키마 주석이 그 필드를 처음부터
    /// "로스터 <b>정원</b>" 로 정의하고 있었다. 파싱된 멤버 수를 보내던 종전 동작이 계약을 벗어난 쪽이었고,
    /// 그 탓에 성역 30일 1,385건(3.362%)이 공대 판별에서 탈락했다(웹 실측 2026-09-19; 탈락분의 roster 값은
    /// 9 가 1,340건으로 압도적 — "부분 스냅샷에서 한 명 빠짐" 그대로다).</para></summary>
    private int _partyRosterCapacity;
    /// <summary>The server's id for the party the held roster belongs to (0 = unknown).</summary>
    private int _partyRosterId;
    /// <summary>When the roster CONTENT was last replaced — unlike <c>_partyRosterAtMs</c>, a held-through
    /// partial snapshot does not refresh it. The battle freeze dates the roster by this, so a roster the meter
    /// has merely been holding cannot pass as freshly confirmed.</summary>
    private long _partyRosterSetAtMs;
    // 0x9702가 실어 온 (닉네임,서버)→(직업코드,전투력). 전투 전 프리뷰 행의 직업/전투력 채움용. 병합-갱신만 하고
    // (제거 없음) 신선도는 _partyRosterAtMs가 게이트한다(떠난 멤버의 잔여 엔트리는 _partyRoster에 없어 무해).
    private readonly Dictionary<(string Nickname, int Server), (int JobCode, int Power)> _partyRosterJobPower = new();
    private long _partyRosterAtMs;

    // 0x9200 멤버 프로필이 실어 온 (엔티티 uid -> 닉네임 + 서버 + 도착시각). 0x9702 로스터엔 uid가 없고,
    // 타인 닉(0x3645)이 유실되면 그 파티원의 전투행이 무명으로 통째 숨는다. 0x9200은 uid를 직접 실어 오므로
    // 그 무명 행을 uid로 곧장 명명할 수 있고(구조검증이 엄격해 오탐 거의 0), 0x9702가 입장 버스트에서 통째로
    // 유실돼도 로스터를 확보하는 이중 소스가 된다. 신원 저장소(_userRepository)에는 절대 쓰지 않는다 — uid
    // 재사용으로 매핑이 틀리면 남의 이름이 저장소에 박히므로, 표시 계층 명명에만 쓰고 TTL로 낡은 매핑을 버린다.
    private readonly Dictionary<int, (string Nickname, int Server, long At)> _memberProfiles = new();
    private const int MemberProfileCap = 32;

    /// <summary>Injectable clock (default wall clock; app behavior unchanged). Mirrors the Kotlin seam.</summary>
    public Func<long> Clock { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Official-site lookup (Kotlin used a global object). Left null offline/in replay so enrichment
    /// is a no-op and the DPS golden is unchanged; the live app injects WaffleMeter.Services.OfficialCharacterLookup.
    /// </summary>
    public IOfficialCharacterLookup? OfficialLookup { get; set; }

    public long CurrentEpoch() => _resetEpoch;
    public long CurrentBattleRevision() => _battleRevision;

    /// <summary>허수아비 test mode: when on, hitting a training dummy (<see cref="Mob.IsDummy"/>) drives a live
    /// battle; when off, dummy hits register no combat. Set live from the UI/hotkey; read on the consumer thread.</summary>
    public bool DummyTestMode { get => _dummyTestMode; set => _dummyTestMode = value; }

    /// <summary>허수아비 런이 컷으로 얼려 둔 종료 시각(패킷 시계), 없으면 0. 리포트를 캐시에서 다시 지을 때
    /// "마지막 타격"이 아니라 이 값을 종료로 쓰라는 신호다 — 그래야 전투 시간이 설정한 그대로(1:00) 나온다.
    /// 보스 전투에서는 언제나 0이라 그쪽 계산식은 손대지 않는다.</summary>
    public long DummyFixedBattleEnd => _dummyFixedEnd;

    /// <summary>허수아비 전투가 방금 열렸으면 true 를 한 번 돌려주고 신호를 내린다. 소비자 스레드 전용.</summary>
    public bool ConsumeDummyBattleOpened()
    {
        if (!_dummyBattleOpened)
        {
            return false;
        }

        _dummyBattleOpened = false;
        return true;
    }

    /// <summary>Dummy test run length in seconds; the live battle is hard-cut at this duration. Clamped to &gt; 0
    /// (falls back to 60s).</summary>
    public int DummyDurationSec { get => _dummyDurationSec; set => _dummyDurationSec = value > 0 ? value : 60; }

    private long DummyDurationMs => Math.Max(1, _dummyDurationSec) * 1000L;

    // ---- reference catalogs ----

    public void LoadMobs(IReadOnlyDictionary<int, Mob> mobs)
    {
        foreach (KeyValuePair<int, Mob> kv in mobs)
        {
            _mobs[kv.Key] = kv.Value;
        }
    }

    public void LoadSkills(IEnumerable<Skill> skills)
    {
        foreach (Skill s in skills)
        {
            _skillRepository.Save(s.Code, s);
        }
    }

    /// <summary>Load the instanced-content (원정/초월/성역) boss classification: mobCode -> category.</summary>
    public void LoadContentTypes(IReadOnlyDictionary<int, string> contentTypes)
    {
        foreach (KeyValuePair<int, string> kv in contentTypes)
        {
            _contentTypes[kv.Key] = kv.Value;
        }
    }

    /// <summary>The instanced-content category (expedition/transcendence/sanctuary) of a boss mobCode, or null
    /// when the code isn't a classified 원정/초월/성역 boss.</summary>
    public string? ContentCategory(int mobCode) => _contentTypes.GetValueOrDefault(mobCode);

    /// <summary>True when <paramref name="mobCode"/> is a classified instanced (원정/초월/성역) boss. Used as
    /// positive proof that a nameless combat row belongs to the party (an instance admits no outsiders).</summary>
    public bool IsInstancedBoss(int mobCode) => _contentTypes.ContainsKey(mobCode);

    /// <summary>The encounters the stats web publishes statistics for. Drives the upload gate and the
    /// difficulty/stage suffix on a boss name. <see cref="EncounterCatalog.Empty"/> until the asset loads.</summary>
    public EncounterCatalog Encounters { get; private set; } = EncounterCatalog.Empty;

    /// <summary>The 시련 난이도 knobs seen for the current instance. Every trial level shares one map and one
    /// set of boss codes, so this is the only thing that tells a level-4 run from a level-16 one.</summary>
    public TrialDifficultyTracker TrialDifficulty { get; } = new();

    public void SaveTrialAffix(TrialAffixGroup group, int level, long arrivedAt) =>
        TrialDifficulty.Observe(group, level);

    public void SaveInstancePhaseWindow(int mapId, int phase, long startMs, long windowMs) =>
        TrialDifficulty.ObservePhaseWindow(mapId, phase, startMs, windowMs);

    /// <summary>Raised (packet-consumer thread) with the instance map the character just loaded into and when.
    /// Stateless on purpose — the only consumer is the 어비스 회랑 clock, which needs "entered a corridor" and
    /// "left it" and nothing else. Every other map id flows through as a no-op for it.</summary>
    public event Action<int, long>? InstanceMapChanged;

    public void SaveInstanceMap(int mapId) => InstanceMapChanged?.Invoke(mapId, Clock());

    /// <summary>Raised (packet-consumer thread) with one 어비스 아티팩트 zone's 점령 현황 and the server's own
    /// 점령 주기 window, plus when it was heard.</summary>
    public event Action<int, long, long, IReadOnlyList<AbyssArtifactHolding>, long>? AbyssArtifactsChanged;

    public void SaveAbyssArtifacts(int zoneId, long cycleStartMs, long cycleEndMs, IReadOnlyList<AbyssArtifactHolding> holdings) =>
        AbyssArtifactsChanged?.Invoke(zoneId, cycleStartMs, cycleEndMs, holdings, Clock());

    /// <summary>Raised (packet-consumer thread) with how many artifacts the active character's side holds in
    /// one zone, and when. The consumer needs it to work out which owner slot in
    /// <see cref="AbyssArtifactsChanged"/> is ours.</summary>
    public event Action<int, int, long>? AbyssArtifactCountChanged;

    public void SaveAbyssArtifactCount(int zoneId, int count) =>
        AbyssArtifactCountChanged?.Invoke(zoneId, count, Clock());

    public void LoadEncounters(EncounterCatalog catalog) => Encounters = catalog;

    public void LoadBuffs(IEnumerable<Buff> buffs)
    {
        foreach (Buff b in buffs)
        {
            _buffRepository.Save(b);
        }
    }

    public static bool IsPlaceholderBuffName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Equals("None", StringComparison.OrdinalIgnoreCase);

    public void LoadBuffBlacklist(IEnumerable<int> codes)
    {
        foreach (int c in codes)
        {
            _buffBlacklist.Add(c);
        }
    }

    public bool IsBuffBlacklisted(int code) => _buffBlacklist.Contains(code);

    private readonly HashSet<int> _passiveSkills = [];
    private readonly HashSet<int> _activeSkillOverrides = [];

    /// <summary>스킬 분류 자산을 적재한다(<c>skill_class.json</c>, 클라 <c>Skill.dat</c>의 SkillType).
    /// 없으면 아무 것도 걸러지지 않고 예전 동작 그대로다.</summary>
    public void LoadSkillClass(IEnumerable<int> passive, IEnumerable<int> activeOverrides)
    {
        foreach (int c in passive)
        {
            _passiveSkills.Add(c);
        }

        foreach (int c in activeOverrides)
        {
            _activeSkillOverrides.Add(c);
        }
    }

    /// <summary>이 스킬 코드가 <b>패시브</b>인가 — 즉 사용자가 누른 게 아니라 저절로 터진 것인가.
    /// <para>0x3802 는 시전과 패시브 프록을 같은 채널로 보낸다. 실측(2026-08-31 5인 코퍼스, 직업 밴드
    /// 32,951 시전): 패시브 판정이 20.1%이고 그중 <b>72.8%가 직전 시전과 0ms 간격</b>(프록 신호)인 반면,
    /// 눌러서 쓰는 핵심 스킬은 1.8~8.2%였다 — 클라의 분류가 행동으로도 확증된다.</para>
    /// <para>판정 순서가 중요하다. ①<see cref="_activeSkillOverrides"/>가 최우선 — 자기 타입은 Active 인데
    /// base 가 Passive 인 코드가 104개 있고(각 직업 긴급 회피 등), 그냥 접으면 통째로 사라진다. 이름 해석이
    /// 같은 함정을 갖는 것과 같은 이유다(11000100 긴급 회피 → base 검성 무기 장착). ②그 다음 코드 자신,
    /// ③마지막으로 base — 0x3802 는 특화·랭크 변종 코드를 싣고(14720007), 변종 자신의 타입은 판정이 아니라
    /// <c>System</c>이라 base 로 접어야 답이 나온다. 어디에도 없으면 <b>남긴다</b>(모르는 것을 숨기지 않는다).</para></summary>
    public bool IsPassiveSkill(int code)
    {
        if (_activeSkillOverrides.Contains(code))
        {
            return false;
        }

        if (_passiveSkills.Contains(code))
        {
            return true;
        }

        int baseCode = code is >= 11_000_000 and <= 19_999_999 ? code / 10_000 * 10_000 : code;
        return baseCode != code && _passiveSkills.Contains(baseCode);
    }

    // ---- player stat sheet (0x364A / 0x3649) ----
    private readonly PlayerStatStore _playerStats = new();

    /// <summary>본인 캐릭터의 스탯 사전. "내 스탯 복사"와 계산기 딥링크가 읽는 단일 원천.</summary>
    public PlayerStatStore PlayerStats => _playerStats;

    /// <summary><see cref="ICaptureGameData.SaveStatSheet"/> 구현. 도착 시각은 캡처 클럭을 쓴다.</summary>
    public void SaveStatSheet(int entityId, IReadOnlyList<(int Stat, int Value)> stats, bool fullSnapshot) =>
        _playerStats.Accept(entityId, stats, fullSnapshot, Clock());

    // ---- buff gain values (nDPS/rDPS) ----
    private readonly BuffValueCatalog _buffValues = new();

    /// <summary>Per-buff-code effect values used by <see cref="DpsMetrics"/>. Empty until buff_values.json is
    /// loaded, which is fine: an empty catalog just means every non-synergy buff prices at zero gain, so
    /// nDPS falls back to raw DPS rather than to a wrong number.</summary>
    public BuffValueCatalog BuffValues => _buffValues;

    public void LoadBuffValues(IEnumerable<(int Code, IReadOnlyList<BuffGainEffect> Effects)> rows) =>
        _buffValues.Load(rows);


    // ---- per-job buff picker (combat-assist overlay) ----
    // Names + job for each base skill code (110000000-buff / 11000000-skill share a base), for the picker UI.
    private readonly Dictionary<int, (string Name, string Job)> _buffNames = new();
    // Base skill codes ever seen on the local player / party — the catalog the picker lists.
    private readonly HashSet<int> _observedBuffBases = new();
    // Curated self-buff bases from the bundled catalog (datamine-verified) — listed in the picker even before
    // they're observed, so a buff can be configured up front.
    private readonly HashSet<int> _knownBuffBases = new();
    // True once buff_catalog.json has been loaded. See LoadBuffCatalog for why this is not `_knownBuffBases.Count > 0`.
    private bool _buffCatalogLoaded;
    // Bases that should default to Off (toggle/aura buffs that stay on indefinitely) — applied on first run.
    private readonly HashSet<int> _defaultOffBuffBases = new();
    // Base skill codes the user unchecked — the overlay suppresses these.
    private readonly HashSet<int> _hiddenBuffBases = new();
    // Base skill codes set to voice ("오버레이+음성" or "음성만") — the store keeps these even when hidden so a
    // 음성만 buff still reaches the announce path (hidden AND voice = 음성만).
    private readonly HashSet<int> _voiceBuffBases = new();
    private readonly object _buffPickerGate = new();

    /// <summary>Runtime job-buff code (110000000..199999999) -> its base skill code (8-digit), the key both
    /// the name table and the picker/hidden sets use. Mirrors JoinIcons' buff→base mapping.</summary>
    public static int BuffBaseCode(int code) => code is >= 110_000_000 and <= 199_999_999 ? code / 100_000 * 10_000 : code;

    // 치유성 '대지의 징벌'(17400000)은 대상 몹에게 디버프 '대지의 징벌'을, 본인+파티원에게는 이름이 다른 버프
    // '대지의 축복'을 건다. 둘 다 BuffBaseCode로 접으면 17400000 한 슬롯이 되어 오버레이·음성·picker가
    // 인게임과 다른 이름("대지의 징벌")과 다른 아이콘(바위 가시)을 쓴다. 축복 쪽 abnormal 코드만 별도 표시
    // base로 돌린다 — 17400058은 skills.json에 이미 '대지의 축복'으로 있고 클라에서도 같은 아이콘을 쓰는
    // 실제 코드라, 이름표·아이콘·상세 스킬행이 한 코드로 정합된다. (데이터마인 07-01/07-15 동일 확인)
    // 2026-08-26 패치가 이 스킬을 둘로 쪼갰다: 종전엔 한 어노멀(…271/371/571)이 시전자+파티 전원에게
    // 걸렸는데(SkillEffectFilter includeCaster=True), 이제 자신용과 파티용 코드가 갈렸다. SkillEffect.dat의
    // SkillEffectLvGroupId 실측: …271/371/571 = Cleric_Skill040_LvUp_A*_Buff_Self, …281/381/591 = *_Buff_Party.
    // 파티용 3코드는 2026-09-01 카탈로그 갱신 전까지 buff.json에서 이름이 'None'(=IsPlaceholderBuff)이라 통째로
    // 버려졌던 탓에 이 표에 없었다. 이름이 들어온 지금 빠뜨리면 파티원의 축복이 base 17400000('대지의 징벌')으로
    // 접혀 ①오버레이/음성이 틀린 이름·아이콘을 쓰고 ②PartySynergyCatalog.Effects(DisplayBase)가 null을 돌려줘
    // 파티원 이득이 0으로 계산되며 ③질풍의 권능과의 배타쌍이 파티원에게 안 걸린다.
    private static readonly Dictionary<int, int> BuffDisplayBaseOverrides = new()
    {
        [174000271] = 17400058,   // A2 자신 (Cleric_Skill040_LvUp_A2_Buff_Self)
        [174000281] = 17400058,   // A2 파티 (…_A2_Buff_Party)
        [174000371] = 17400058,   // A3 자신
        [174000381] = 17400058,   // A3 파티
        [174000571] = 17400058,   // A5 자신
        [174000591] = 17400058,   // A5 파티
    };

    // 인게임에서 서로 중복 적용되지 않는 버프 쌍. 둘 다 활성으로 보이면 지는 쪽을 오버레이에서 감춘다.
    // 코퍼스 실측상 쌍마다 서버 동작이 다르다:
    //  · 노련한 반격↔격앙  : 서버가 둘 다 보낸다(전 지속시간 겹침, p50 10s) → 우리가 반드시 감춰야 한다.
    //  · 보호의 빛↔불패의 진언 : 서버가 중재하고 잔상 최대 2.4초. 인게임 설명문에 "스킬 레벨이 높은 1개만
    //    적용, 동일하면 불패의 진언" 이라고 명문화돼 있어 동률 승자를 고정한다.
    //  · 대지의 축복↔질풍의 권능 : 서버가 새 적용은 막지만(질풍 우선) 이미 걸린 축복을 제거하진 않아
    //    최대 ~20초 잔존 → 질풍이 살아 있으면 축복을 감춘다(고정 승자).
    /// <summary>공개 이유: 오버레이(<see cref="SuppressExclusiveLosers"/>)와 nDPS/rDPS 계산
    /// (<see cref="DpsMetrics"/>)이 <b>같은 배타 규칙</b>을 써야 한다. 두 벌로 갈라두면 화면에서는 감춘 버프를
    /// 계산에서는 이득으로 세는(또는 그 반대) 어긋남이 조용히 생긴다.</summary>
    public readonly record struct ExclusiveBuffPair(int A, int B, int FixedWinner, int TieWinner);

    /// <summary>인게임에서 서로 중복 적용되지 않는 버프 쌍(표시용 base 코드 기준).</summary>
    public static IReadOnlyList<ExclusiveBuffPair> ExclusivePairs => ExclusiveBuffPairs;

    private static readonly ExclusiveBuffPair[] ExclusiveBuffPairs =
    {
        new(11780000, 12780000, FixedWinner: 0, TieWinner: 0),          // 검성 노련한 반격 ↔ 수호성 격앙
        new(17410000, 18190000, FixedWinner: 0, TieWinner: 18190000),   // 치유성 보호의 빛 ↔ 호법성 불패의 진언
        new(17400058, 18250000, FixedWinner: 18250000, TieWinner: 0),   // 치유성 대지의 축복 ↔ 호법성 질풍의 권능
    };

    /// <summary>오버레이/음성/picker가 쓰는 표시용 base 코드. 한 스킬이 이름이 다른 두 효과를 뿌리는 경우만
    /// <see cref="BuffBaseCode"/>와 갈라진다. 집계·통계 경로는 <see cref="BuffBaseCode"/>를 그대로 쓴다.</summary>
    public static int BuffDisplayBase(int code) =>
        BuffDisplayBaseOverrides.TryGetValue(code, out int mapped) ? mapped : BuffBaseCode(code);

    /// <summary>buff_names.json: base skill code -> (name, job) for the per-job buff picker.</summary>
    public void LoadBuffNames(IEnumerable<(int Code, string Name, string Job)> names)
    {
        lock (_buffPickerGate)
        {
            foreach ((int code, string name, string job) in names)
            {
                _buffNames[code] = (name, job);
            }
        }
    }

    /// <summary>Replace the hidden-buff set (base codes the user unchecked in the picker).</summary>
    public void SetHiddenBuffBases(IEnumerable<int> baseCodes)
    {
        lock (_buffPickerGate)
        {
            _hiddenBuffBases.Clear();
            foreach (int c in baseCodes)
            {
                _hiddenBuffBases.Add(c);
            }
        }
    }

    /// <summary>Replace the voice-buff set (base codes set to "오버레이+음성" or "음성만" in the picker).</summary>
    public void SetVoiceBuffBases(IEnumerable<int> baseCodes)
    {
        lock (_buffPickerGate)
        {
            _voiceBuffBases.Clear();
            foreach (int c in baseCodes)
            {
                _voiceBuffBases.Add(c);
            }
        }
    }

    /// <summary>Seed the observed catalog from a persisted set (so the picker isn't empty on launch).</summary>
    public void SeedObservedBuffBases(IEnumerable<int> baseCodes)
    {
        lock (_buffPickerGate)
        {
            foreach (int c in baseCodes)
            {
                _observedBuffBases.Add(c);
            }
        }
    }

    /// <summary>buff_catalog.json: curated self-buff bases (datamine-verified) that the picker lists even
    /// before they're observed, plus the default-off (toggle/aura) subset. Names are merged into the table.</summary>
    public void LoadBuffCatalog(IEnumerable<(int Code, string Name, string Job)> catalog, IEnumerable<int> defaultOff)
    {
        lock (_buffPickerGate)
        {
            // Tracked separately from _knownBuffBases being non-empty: the revival-heal path also adds to that
            // set as a safety net, and inferring "a catalogue was loaded" from its size would let one synthetic
            // code silently switch the whole overlay from show-everything to show-almost-nothing.
            _buffCatalogLoaded = true;
            foreach ((int code, string name, string job) in catalog)
            {
                _knownBuffBases.Add(code);
                if (!_buffNames.ContainsKey(code) && !string.IsNullOrEmpty(name))
                {
                    _buffNames[code] = (name, string.IsNullOrEmpty(job) ? "기타" : job);
                }
            }

            foreach (int c in defaultOff)
            {
                _defaultOffBuffBases.Add(c);
            }
        }
    }

    /// <summary>The toggle/aura buffs that should default to Off (applied once on first run by the app).</summary>
    public IReadOnlyCollection<int> DefaultOffBuffBases()
    {
        lock (_buffPickerGate)
        {
            return _defaultOffBuffBases.ToList();
        }
    }

    private bool IsBuffHidden(int runtimeCode)
    {
        lock (_buffPickerGate)
        {
            return _hiddenBuffBases.Contains(BuffDisplayBase(runtimeCode));
        }
    }

    private bool IsBuffVoice(int runtimeCode)
    {
        lock (_buffPickerGate)
        {
            return _voiceBuffBases.Contains(BuffDisplayBase(runtimeCode));
        }
    }

    /// <summary>
    /// The picker catalog: the curated buff list, grouped-ready as (base code, name, job, hidden).
    ///
    /// <para>This used to be <c>observed ∪ curated</c>, which made the list whatever the game happened to
    /// broadcast — it grew to 182 rows on a well-played install, carried pure attack skills and enemy
    /// debuffs, and showed rows labelled "스킬 13790007" for codes no name table covers. The curated
    /// catalogue is now the whole list, so what the picker offers, what the overlay draws, and what the
    /// voice packs are baked from are one set that can actually be kept in step.</para>
    ///
    /// <para>Observation is still recorded (see <see cref="RecordObservedBuff"/>) — it is how a buff a
    /// patch adds gets discovered and added to the catalogue, it just no longer shows itself.</para>
    /// </summary>
    public IReadOnlyList<(int BaseCode, string Name, string Job, bool Hidden)> BuffPickerCatalog()
    {
        lock (_buffPickerGate)
        {
            // Empty catalogue = the JSON is missing; fall back to the old observed-driven list so the picker
            // degrades the same way the overlay does (see IsBuffInCatalog) instead of going blank.
            IReadOnlyCollection<int> bases = _buffCatalogLoaded ? _knownBuffBases : _observedBuffBases;
            var list = new List<(int, string, string, bool)>(bases.Count);
            foreach (int b in bases)
            {
                (string name, string job) = _buffNames.TryGetValue(b, out (string Name, string Job) v)
                    ? v
                    : ($"스킬 {b}", "기타");
                list.Add((b, name, job, _hiddenBuffBases.Contains(b)));
            }

            return list;
        }
    }

    /// <summary>
    /// True when the buff overlay is allowed to draw / announce this code. The catalogue is the list, so a
    /// code outside it has no picker row — leaving it visible would draw a buff the user has no way to turn
    /// off.
    ///
    /// <para>Empty catalogue = no opinion, not "nothing qualifies". <c>MeterServices</c> only calls
    /// <see cref="LoadBuffCatalog"/> when buff_catalog.json is actually present, so a publish that dropped
    /// the asset would otherwise blank the entire buff overlay with no error anywhere. Degrading to the old
    /// show-everything behaviour is the failure worth having.</para>
    /// </summary>
    private bool IsBuffInCatalog(int runtimeCode)
    {
        lock (_buffPickerGate)
        {
            return !_buffCatalogLoaded || _knownBuffBases.Contains(BuffDisplayBase(runtimeCode));
        }
    }

    /// <summary>The current observed base-code set (for persistence).</summary>
    public IReadOnlyCollection<int> ObservedBuffBases()
    {
        lock (_buffPickerGate)
        {
            return _observedBuffBases.ToList();
        }
    }

    /// <summary>Raised when a new base buff code is observed (so the picker can refresh its catalog).</summary>
    public event Action? BuffCatalogChanged;

    // ---- ICaptureGameData (parser-facing) ----

    public Mob? GetMob(int code) => _mobs.GetValueOrDefault(code);
    public int? GetMobId(int instanceId) => _mobIdRepository.Get(instanceId)?.Code;

    public void SaveMobId(int mid, int code)
    {
        int? previous = GetMobId(mid);
        if (previous != null && previous != code)
        {
            _recentlyEndedBattles.Remove(mid);
            if (_pendingStart?.MobId == mid)
            {
                _pendingStart = null; // this instance id was recycled to a different mob — drop the stale retry
            }
        }

        _mobIdRepository.Save(mid, code);
        PromoteUnresolvedStart(mid, code);
    }

    /// <summary>시작 토글이 mobCode 미해결로 거부됐던 엔티티의 스폰이 이제 도착했다면 그 전투를 되살린다.
    /// 되살릴 때는 <b>원래 토글 시각</b>으로 시작을 스탬프한다 — 지금 시각으로 열면 그 사이의 딜이
    /// ActivePacketCutoff에 걸려 통째로 빠진다(패킷 자체는 타겟별 링버퍼에 남아 있다).
    /// <para>가드: 토글 이후 <see cref="PendingStartTtlMs"/> 이내 + 진행 중인 전투 없음 + 해석된 몹이
    /// 보스이고 허수아비가 아님. 특히 보스 검사를 빼면 잡몹 스폰이 늦게 올 때마다 전투가 열려 전투창이
    /// 절단·분할된다(191M 오염과 같은 계열).</para></summary>
    private void PromoteUnresolvedStart(int mid, int code)
    {
        if (!_unresolvedStarts.TryGetValue(mid, out long toggledAt))
        {
            return;
        }

        _unresolvedStarts.Remove(mid); // 성공하든 말든 한 번만 시도한다
        if (Clock() - toggledAt > PendingStartTtlMs || CurrentTarget() > 0)
        {
            return;
        }

        if (Mob(code) is not { Boss: true, IsDummy: false })
        {
            return;
        }

        StartBattleAt(mid, toggledAt);
    }

    public void RememberUnresolvedBattleStart(int mobId)
    {
        if (mobId <= 0)
        {
            return;
        }

        long now = Clock();
        if (_unresolvedStarts.Count >= UnresolvedStartsCap)
        {
            // 만료분부터 정리하고, 그래도 꽉 차 있으면 가장 오래된 것을 밀어낸다.
            foreach (int stale in _unresolvedStarts.Where(kv => now - kv.Value > PendingStartTtlMs).Select(kv => kv.Key).ToList())
            {
                _unresolvedStarts.Remove(stale);
            }

            if (_unresolvedStarts.Count >= UnresolvedStartsCap)
            {
                _unresolvedStarts.Remove(_unresolvedStarts.OrderBy(kv => kv.Value).First().Key);
            }
        }

        _unresolvedStarts[mobId] = now;
    }

    // 스폰(0x3641)이 통째로 유실돼 mobCode가 등록되지 않은 던전 보스를 되살릴 때 쓰는 합성 코드. 실제 몹 코드
    // 대역(2.3M~2.9M) 밖의 8자리 값이라 어떤 카탈로그와도 충돌하지 않고, 추정 신원이므로 통계 업로드에서
    // 제외된다(StatsUploadQueue). 표시 이름은 '미상 보스'.
    public const int UnknownBossMobCode = 29_999_999;
    private const string UnknownBossName = "미상 보스";
    // 던전 보스로 볼 HP 임계 — 던전 대역 잡몹 최대 관측치(12.69M)의 1.58배. 플레이어(HP 수만~수십만)와 잡몹을
    // 배제하고 오탐 0(07-01 이후 6세션 실측). 최대HP 티어를 복제하는 292xxxx 기믹은 아래 교전-토글 게이트로
    // 걸러진다(기믹·주변 엔티티는 0x8D21 교전 토글을 쏘지 않는다).
    private const long UnknownBossHpThreshold = 20_000_000L;

    /// <summary>스폰 유실로 미등록인 던전 보스를 HP 휴리스틱으로 되살린다(사용자 선택: 안전 게이트 강제집계).
    /// <para>게이트 — ① HP(현재 또는 최대)가 던전 보스 임계 이상 ② 아직 mobCode 미등록 ③ 진행 중인 전투 없음
    /// ④ 이 엔티티가 교전 토글(0x8D21 toggle=1)을 쏜 적이 있다(=<see cref="_unresolvedStarts"/>에 있다). ④가
    /// 핵심 안전선이다 — 플레이어는 ①에서, 상시-스폰 잡몹은 ①에서, 기믹 오브젝트는 ④에서 걸러진다.</para>
    /// <para>통과하면 합성 '미상 보스'를 등록하고 <see cref="SaveMobId"/>가 <see cref="PromoteUnresolvedStart"/>를
    /// 태워 <b>원래 토글 시각</b>으로 back-date StartBattle 한다(지금 열면 그 사이 딜이 ActivePacketCutoff에
    /// 걸려 유실되고, 늦은 시작이 창을 잘라먹는 191M 오염과 같은 계열이 된다).</para></summary>
    public void TryPromoteUnregisteredBoss(int entityId, long hp)
    {
        if (entityId <= 0 || hp < UnknownBossHpThreshold)
        {
            return;
        }

        if (GetMobId(entityId) is not null)
        {
            return;
        }

        if (CurrentTarget() > 0)
        {
            return;
        }

        if (!_unresolvedStarts.ContainsKey(entityId))
        {
            return;
        }

        if (!_mobs.ContainsKey(UnknownBossMobCode))
        {
            _mobs[UnknownBossMobCode] = new Mob(UnknownBossMobCode, UnknownBossName, true, false);
        }

        SaveMobId(entityId, UnknownBossMobCode); // PromoteUnresolvedStart가 back-date StartBattle을 발화
        SaveMobMaxHp(entityId, hp);
    }

    public bool SkillExists(long code) => _skillRepository.Exist(code);

    // A recognized player = a uid with an observed nickname. Excludes provisional (nickname-less) EnsureUser
    // rows, so the summon-owner fallback validates only against real players.
    public bool IsKnownUser(int uid) => !string.IsNullOrEmpty(_userRepository.Get(uid)?.Nickname);

    // ---- mob / hp ----

    public Mob? Mob(int mobCode) => _mobs.GetValueOrDefault(mobCode);
    public Skill? Skill(long code) => _skillRepository.Get(code);
    public Buff? Buff(int code) => _buffRepository.Get(code);

    public long? MobHp(int mobId) => _mobHpRepository.Get(mobId);

    public void MobHp(int mobId, long mobHp)
    {
        _mobHpRepository.Set(mobId, mobHp);
        if (mobId == CurrentTarget())
        {
            // HP 보고만으로도 "그 보스가 아직 우리 시야에 있다"는 증거다 — 파티가 딜을 멈춘 페이즈(칼드릭스)에도
            // 계속 오므로, 데미지만 신호로 삼으면 정상 페이즈를 유휴로 오판한다.
            _lastBossActivityMs = Clock();
        }

        // 이 전투에서 가장 많이 깎였던 지점을 따로 기억한다. 전투가 열려 있는 동안에만 갱신하고, 내려갈 때만
        // 받는다 — 전멸 직후 보스가 제자리로 돌아가며 쏘는 만피 보고가 이 값을 되돌리지 못하게 하는 게 목적이다.
        if (mobId == CurrentTarget() && CurrentBattleStart() > 0L && CurrentBattleEnd() == 0L)
        {
            _battleLowHp[mobId] = _battleLowHp.TryGetValue(mobId, out (long Revision, long Low) seen) && seen.Revision == _battleRevision
                ? (seen.Revision, Math.Min(seen.Low, mobHp))
                : (_battleRevision, mobHp);
        }

        if (mobHp > 0)
        {
            _recentlyEndedBattles.Remove(mobId);
            SaveMobMaxHp(mobId, mobHp);

            // A re-pull whose start-toggle we swallowed as a death-rattle: the boss now shows HP, so honor that
            // start (the game may not re-send the toggle). The recently-ended entry was just removed above, so
            // StartBattle no longer suppresses; the CurrentTarget<=0 guard keeps it from stomping a live battle.
            if (_pendingStart is { } ps && ps.MobId == mobId && ps.MobCode == GetMobId(mobId) && CurrentTarget() <= 0)
            {
                _pendingStart = null; // consumed either way, so a stale pending can't linger and fire later
                if (Clock() - ps.At <= PendingStartTtlMs)
                {
                    StartBattle(mobId);
                }
            }
        }
    }

    /// <summary>그 전투에서 관측된 <b>최저</b> 잔여 HP — 전투 기록에 얼려야 할 값이다. 한 번도 HP를 못 받았으면 null.
    /// <para><b>왜 라이브 HP를 그대로 못 쓰는가.</b> 파티가 전멸하면 보스가 제자리로 돌아가며 만피를 한 번
    /// 방송한다. 실측(2026-08-31 바실루스)에서 그 프레임은 <b>종료 토글보다 1.495초 먼저</b> 왔다 —
    /// 25.4%까지 깎아 놓고 진 전투가 기록에 <b>100%</b>로 박혔고, 그 사이 라이브 틱들이 이미 리포트에 만피를
    /// 써 넣은 뒤였다. 즉 "종료 순간에 얼린다"로는 못 막는다. <c>replay-diag</c> 전수에서 죽지 않고 끝난
    /// 비포화 전투 601건 중 194건(32%)이 이렇게 만피로 기록돼 있었다.</para>
    /// <para><b>왜 최저값인가.</b> 피해는 줄어들기만 하므로 정상 전투에서 최저 잔여 HP = 마지막 타격 직후의
    /// 잔여 HP다(킬이면 0). 즉 이 값은 평시에 라이브 값과 같고, 보스가 회복한 경우에만 갈린다 — 그리고 그때는
    /// 누적 피해량과도 이쪽이 앞뒤가 맞는다(누적 피해는 회복분을 되돌리지 않는다).</para></summary>
    public long? BattleLowMobHp(int mobId, long battleRevision)
        => _battleLowHp.TryGetValue(mobId, out (long Revision, long Low) seen) && seen.Revision == battleRevision
            ? seen.Low
            : null;

    public long? MobMaxHp(int mobId)
    {
        long? maxHp = _mobIdRepository.Get(mobId)?.MaxHp;
        return maxHp is > 0 ? maxHp : null;
    }

    public void SaveMobMaxHp(int mid, long maxHp) => _mobIdRepository.SaveMaxHp(mid, maxHp);

    public bool IsMobInstance(int id) => _mobIdRepository.Exist(id);

    // ---- summon ----

    public void SaveSummon(int summonId, int summonerId) => _summonRepository.Save(summonId, summonerId);
    public int? SummonerId(int summonId) => _summonRepository.Get(summonId);

    /// <summary>소환수 엔티티면 주인 uid, 아니면 받은 그대로.
    /// <para><b>재사용 방어가 핵심이다.</b> 소환수 맵은 전투를 넘어 살아남고(비우는 건 <see cref="HardReset"/>
    /// 뿐이다) 엔티티 id 는 서버가 재발급한다. 그래서 낡은 <c>summonId→owner</c> 항목이, 그 id 를 물려받은
    /// <b>다른 플레이어</b>의 것을 통째로 옛 주인에게 끌어올 수 있다. 이미 아는 플레이어면 접지 않는다 —
    /// 피해 경로의 <c>ResolveActor</c>가 같은 이유로 같은 가드를 들고 있다.</para></summary>
    public int ResolveSummonOwner(int actorId)
    {
        if (actorId <= 0 || User(actorId) != null)
        {
            return actorId;
        }

        return SummonerId(actorId) ?? actorId;
    }

    // ---- user ----

    public User? User(int uid) => _userRepository.Get(uid);
    public int ExecutorId() => _userRepository.Executor();

    /// <summary>Raised when the connected character is switched to a DIFFERENT character (a real char
    /// switch — different nickname, or a different known server — NOT the same character re-instancing
    /// under a fresh uid on a zone load). Lets the UI drop its own per-character derived preview state
    /// (the recent-combat party tracker) so the previous character doesn't linger as a stale idle row.
    /// Fires on the packet-consumer thread.</summary>
    public event Action? ExecutorIdentityChanged;

    // ---- aether (오드) resource, the local player's balance shown next to the recognized character ----
    // Written on the packet-consumer thread, read (composite) on the UI thread → guard so a read can't
    // observe a torn base/bonus/total mid-update.
    private readonly object _aetherGate = new();
    private int _aetherBase;
    private int _aetherBonus;
    private int _aetherTotal;
    private bool _aetherHasValue;
    private long _aetherAtMs;
    private bool _aetherFromSnapshot;
    private bool _aetherIsLive;

    /// <summary>How long after a broadcast the balance still counts as "just arrived" for the character-switch
    /// decision below. The 0x610B login dump lands ~4-6 s before the packet that names its character (measured:
    /// 7 of 7 switches, 5.7-6.1 s, no counter-example), so at the moment a switch is detected the newest reading
    /// is the INCOMING character's, not the outgoing one's. Deliberately tighter than the 30 s the weekly
    /// counters wait: keeping the wrong character's balance shows a wrong number, whereas dropping the right
    /// one now merely costs a re-seed from the per-character store.</summary>
    private const long AetherHandoverGraceMs = 15_000;

    /// <summary>Raised (packet-consumer thread) when the aether balance changes, so the overlay can refresh.</summary>
    public event Action? AetherStatusChanged;

    /// <summary>The local player's current aether balance, or (0,0,false) until one has been seen.</summary>
    public (int Base, int Bonus, int Total, bool HasValue) CurrentAether
    {
        get { lock (_aetherGate) { return (_aetherBase, _aetherBonus, _aetherTotal, _aetherHasValue); } }
    }

    /// <summary>Where the current balance came from.
    /// <list type="bullet">
    /// <item><c>AtMs</c> — when the balance was OBSERVED (Unix ms, 0 = unknown). For a live broadcast that is
    /// its arrival; for a restored one it is when the reading being restored was originally taken, which is what
    /// the offline 자연회복 projection measures elapsed time from.</item>
    /// <item><c>FromSnapshot</c> — the 0x610B login/zone-in dump rather than a 0x610C change notice. A dump
    /// arrives ~4 s before its owner is named, so filing one on arrival writes the incoming character's 오드 onto
    /// the outgoing character's record.</item>
    /// <item><c>IsLive</c> — read off the wire this session, as opposed to restored from memory. Only a live
    /// reading is authoritative; a restored one is displayed as an estimate and never persisted.</item>
    /// </list></summary>
    public (long AtMs, bool FromSnapshot, bool IsLive) AetherOrigin
    {
        get { lock (_aetherGate) { return (_aetherAtMs, _aetherFromSnapshot, _aetherIsLive); } }
    }

    /// <summary>Record the 오드 balance. Every broadcast carries BOTH pools authoritatively — the packet's
    /// field mask omits a pool only when it is zero — so there is nothing to back-compute here. (Until
    /// 2026-07-30 the single-pool form was mis-read as a "total" and its delta was absorbed into 자연회복,
    /// which is why a 오드 회복 소모품 grew the number outside the parentheses instead of the one inside.)</summary>
    public void SaveAetherStatus(int baseVal, int bonus) => SaveAetherStatus(baseVal, bonus, fromSnapshot: false);

    /// <inheritdoc cref="SaveAetherStatus(int, int)"/>
    public void SaveAetherStatus(int baseVal, int bonus, bool fromSnapshot)
    {
        long at = Clock();
        lock (_aetherGate)
        {
            _aetherBase = baseVal;
            _aetherBonus = bonus;
            _aetherTotal = baseVal + bonus;
            _aetherHasValue = true;
            _aetherAtMs = at;
            _aetherFromSnapshot = fromSnapshot;
            _aetherIsLive = true;
        }

        AetherStatusChanged?.Invoke(); // outside the lock (avoid holding it during event dispatch)
    }

    /// <summary>Seed the aether balance from a remembered value so the badge isn't blank until the game's next
    /// resource broadcast. A live broadcast is never overridden (guarded by <paramref name="onlyIfEmpty"/>).
    /// <para>Store the reading EXACTLY as it was taken, with <paramref name="observedAtMs"/> saying when — the
    /// offline 자연회복 projection is then applied at display time, by whoever renders it. Projecting here
    /// instead would bake an estimate into the stored value, and any later re-projection would compound on top
    /// of it.</para></summary>
    public void RestoreAetherStatus(int baseVal, int bonus, long observedAtMs = 0, bool onlyIfEmpty = true)
    {
        lock (_aetherGate)
        {
            if (onlyIfEmpty && _aetherHasValue)
            {
                return; // a live value already arrived — don't clobber it with the restored one
            }

            _aetherBase = baseVal;
            _aetherBonus = bonus;
            _aetherTotal = baseVal + bonus;
            _aetherHasValue = true;
            _aetherAtMs = observedAtMs;
            _aetherFromSnapshot = false;

            // A restore is NOT an observation. This is what keeps it from passing as a just-arrived login dump
            // on the next character switch, and what tells the persister not to write it back — its timestamp
            // is older than the record it came from and re-stamping would lose the accrual it stands for.
            _aetherIsLive = false;
        }

        AetherStatusChanged?.Invoke();
    }

    /// <summary>Forget a RESTORED balance (a live one is left alone). Called once the identity is established
    /// and turns out to have no remembered balance of its own: the launch-time cache is a single global value,
    /// so what is on screen is then some other character's, and showing nothing beats showing that.</summary>
    public void DropRestoredAether()
    {
        lock (_aetherGate)
        {
            if (_aetherIsLive || !_aetherHasValue)
            {
                return;
            }
        }

        ClearAetherStatus();
    }

    /// <summary>Whether the balance now held arrived close enough to <paramref name="identityAtMs"/> to be the
    /// INCOMING character's login dump rather than the outgoing character's last reading.
    /// <para>Restricted to the 0x610B DUMP, because that is the whole of the evidence: the dump is what precedes
    /// its naming packet. A 0x610C change notice is by definition the outgoing character's — it only fires when
    /// a balance changes, which means someone was logged in and playing — so letting one through here would pin
    /// the previous character's 오드 to the new one AND suppress the re-seed that would have corrected it.</para>
    /// A restored value never qualifies either; it is not an observation at all.</summary>
    private bool AetherArrivedWithHandover(long identityAtMs)
    {
        lock (_aetherGate)
        {
            return _aetherHasValue
                && _aetherIsLive
                && _aetherFromSnapshot
                && _aetherAtMs > 0
                && identityAtMs - _aetherAtMs is >= 0 and <= AetherHandoverGraceMs;
        }
    }

    private void ClearAetherStatus()
    {
        lock (_aetherGate)
        {
            if (!_aetherHasValue && _aetherBase == 0 && _aetherBonus == 0 && _aetherTotal == 0)
            {
                return; // nothing to clear — skip the change event
            }

            _aetherBase = _aetherBonus = _aetherTotal = 0;
            _aetherHasValue = false;
            _aetherAtMs = 0;
            _aetherFromSnapshot = false;
            _aetherIsLive = false;
        }

        AetherStatusChanged?.Invoke();
    }

    // ---- shugo-festa key (슈고 페스타 보상 열쇠), shown in the footer next to aether ----
    // Rides the same 0x610x packets as aether (different key byte); same threading + back-compute semantics.
    private readonly object _shugoKeyGate = new();
    private int _shugoKeyBase;
    private int _shugoKeyBonus;
    private int _shugoKeyTotal;
    private bool _shugoKeyHasValue;
    private long _shugoKeyAtMs;
    private bool _shugoKeyFromSnapshot;

    /// <summary>Raised (packet-consumer thread) when the shugo-key count changes, so the overlay can refresh.</summary>
    public event Action? ShugoKeyChanged;

    /// <summary>The local player's current shugo-festa key count, or (0,0,false) until one has been seen.</summary>
    public (int Base, int Bonus, int Total, bool HasValue) CurrentShugoKey
    {
        get { lock (_shugoKeyGate) { return (_shugoKeyBase, _shugoKeyBonus, _shugoKeyTotal, _shugoKeyHasValue); } }
    }

    /// <summary>Record the shugo-festa key count. Like aether, every broadcast carries both pools
    /// authoritatively, so there is nothing to back-compute. (The old total-only branch here was unreachable —
    /// the parser never produced one — but it kept alive the same wrong premise that broke aether.)</summary>
    public void SaveShugoKey(int baseVal, int bonus) => SaveShugoKey(baseVal, bonus, fromSnapshot: false);

    /// <inheritdoc cref="SaveShugoKey(int, int)"/>
    public void SaveShugoKey(int baseVal, int bonus, bool fromSnapshot)
    {
        long at = Clock();
        lock (_shugoKeyGate)
        {
            _shugoKeyBase = baseVal;
            _shugoKeyBonus = bonus;
            _shugoKeyTotal = baseVal + bonus;
            _shugoKeyHasValue = true;
            _shugoKeyAtMs = at;
            _shugoKeyFromSnapshot = fromSnapshot;
        }

        ShugoKeyChanged?.Invoke();
    }

    /// <summary>The shugo-key counterpart of <see cref="AetherArrivedWithHandover"/>, kept SEPARATE on purpose.
    /// Deciding this resource's fate from the other one's arrival stamp is wrong in a way that shows: the key
    /// parser has no empty-mask branch (its group id is <c>00 00 00</c>, which is far too weak a needle to scan
    /// for), so a character holding ZERO keys produces no reading at all. Its login dump would then keep the
    /// previous character's key count alive — and the aether stamp, which did arrive, would have vetoed the
    /// clear that used to save us.</summary>
    private bool ShugoArrivedWithHandover(long identityAtMs)
    {
        lock (_shugoKeyGate)
        {
            return _shugoKeyHasValue
                && _shugoKeyFromSnapshot
                && _shugoKeyAtMs > 0
                && identityAtMs - _shugoKeyAtMs is >= 0 and <= AetherHandoverGraceMs;
        }
    }

    private void ClearShugoKey()
    {
        lock (_shugoKeyGate)
        {
            if (!_shugoKeyHasValue && _shugoKeyBase == 0 && _shugoKeyBonus == 0 && _shugoKeyTotal == 0)
            {
                return;
            }

            _shugoKeyBase = _shugoKeyBonus = _shugoKeyTotal = 0;
            _shugoKeyHasValue = false;
            _shugoKeyAtMs = 0;
            _shugoKeyFromSnapshot = false;
        }

        ShugoKeyChanged?.Invoke();
    }

    // ---- 주간 성역 '최종 보스 처치 횟수' (컨텐츠 관리 패널) ----
    // Rides the same 0x610x packets as aether; one counter per 성역 raid, for the ACTIVE character only.
    // Deliberately STATELESS here, unlike aether and the shugo key: the durable answer is a per-character
    // settings record the app owns, and a mirror of "the last value seen" would only be a second copy that can
    // disagree with it. It did, briefly — a dedupe against that mirror swallowed exactly the broadcasts that
    // needed to correct a record the panel's own ✕ or manual toggle had changed behind it.
    private long _executorIdentityAtMs;

    /// <summary>When the executor's identity was last established (Unix ms), or 0 if it never has been.
    /// See the note in <see cref="SaveExecutorId"/>: the weekly counters use this to tell "the identity that
    /// arrived after this snapshot" from "the identity that merely happened to still be current".</summary>
    public long ExecutorIdentityAtMs => Interlocked.Read(ref _executorIdentityAtMs);

    /// <summary>Raised (packet-consumer thread) when a weekly 성역 counter arrives:
    /// <c>(kind, remaining, arrivedAtMs, fromSnapshot)</c>. <c>fromSnapshot</c> distinguishes the 0x610B
    /// login/zone-in dump — whose owner is ambiguous until the own-load packet lands — from a 0x610C delta,
    /// which can only happen mid-play with the identity long settled.</summary>
    public event Action<WeeklyContentKind, int, long, bool>? WeeklyContentChanged;

    /// <summary>Record one weekly 성역 counter. Both pools are authoritative — a spent counter arrives as
    /// (0, 0) because the packet's field mask omits an empty pool, so their sum is the answer as-is.
    /// <para>Raised UNCONDITIONALLY, exactly like <see cref="SaveAetherStatus"/> and unlike an earlier version
    /// of this method, which skipped the event when the value matched what it already held. That optimisation
    /// assumed this cache and the persisted store could not disagree — but the store has two other writers (the
    /// panel's ✕ and its manual chip toggle), so a repeat broadcast that "changed nothing" was exactly the one
    /// that had to re-sync, and the wrong value latched until the app restarted. The store's own Upsert still
    /// skips the write when nothing changed, so the repeat costs a parse, not a disk write.</para></summary>
    public void SaveWeeklyContent(WeeklyContentKind kind, int baseVal, int bonus, bool fromSnapshot) =>
        WeeklyContentChanged?.Invoke(
            kind, Math.Max(0, baseVal) + Math.Max(0, bonus), Clock(), fromSnapshot);

    /// <summary>Raised (packet-consumer thread) when a 어비스 회랑 이용 시간 arrives:
    /// <c>(ticketId, remainingMs, arrivedAtMs, fromSnapshot)</c>. Forwarded unconditionally for the same reason
    /// as <see cref="SaveWeeklyContent"/> — the persisted store has writers this cache cannot see, so a repeat
    /// broadcast is exactly the one that has to re-sync it.</summary>
    public event Action<int, long, long, bool>? AbyssCorridorChanged;

    /// <summary>Record one corridor's remaining 이용 시간. A spent corridor arrives as 0 because the packet's
    /// field mask omits an empty field, so the value is the answer as-is.</summary>
    public void SaveAbyssCorridor(int ticketId, long remainingMs, bool fromSnapshot) =>
        AbyssCorridorChanged?.Invoke(ticketId, Math.Max(0, remainingMs), Clock(), fromSnapshot);

    // ---- field-boss respawn timers (boss code -> target Unix-ms), from the 0x9101 broadcast ----
    // Written on the packet-consumer thread, read (snapshot) on the UI thread → guard with a lock.
    private readonly Dictionary<int, long> _fieldBossTimers = new();
    private readonly object _fieldBossGate = new();

    /// <summary>Raised (packet-consumer thread) when the field-boss timer table changes.</summary>
    public event Action? FieldBossTimersChanged;

    /// <summary>A thread-safe snapshot of the current field-boss respawn timers (code -> target Unix-ms).</summary>
    public IReadOnlyDictionary<int, long> CurrentFieldBossTimers
    {
        get { lock (_fieldBossGate) { return new Dictionary<int, long>(_fieldBossTimers); } }
    }

    public void SaveFieldBossTimers(IReadOnlyList<(int Code, long TargetMs)> timers)
    {
        bool changed = false;
        lock (_fieldBossGate)
        {
            foreach ((int code, long targetMs) in timers)
            {
                if (!_fieldBossTimers.TryGetValue(code, out long existing) || existing != targetMs)
                {
                    _fieldBossTimers[code] = targetMs;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            FieldBossTimersChanged?.Invoke();
        }
    }

    public User? FindUserByNicknameAndServer(string nickname, int server) =>
        _userRepository.FindByNicknameAndServer(nickname, server);

    public void SavePartyRoster(IReadOnlyList<(string Nickname, int Server, int Slot)> members) =>
        SavePartyRoster(members, partyId: 0);

    /// <summary>
    /// 0x9702 스냅샷을 로스터 상태에 <b>합친다</b>(갈아끼우지 않는다).
    ///
    /// <para><b>🔑 왜 합치나.</b> 0x9702 는 <b>부분 로스터</b>를 보내는 것이 정상이다 — 코퍼스 실측(2026-07-09
    /// 이후 세션): 정원 10 방의 스냅샷 502건 중 완전한 것은 290건(57.8%)뿐이고, 나머지는 7~9명만 실려 온다.
    /// 같은 슬롯이 사라졌다 <b>되돌아오는</b> 것도 확인했다(슬롯 5·7이 각각 3회). 종전처럼 스냅샷 하나로
    /// 로스터를 통째 교체하면, 하필 부분 스냅샷 직후 전투가 끝난 경우 <c>PartyRosterSize</c> 가 10이 아니라
    /// 8~9로 나가고, 서버의 <c>RAID_ROSTER_SIZES = [10]</c> 공대 판별에서 그 전투가 통째로 탈락한다.</para>
    ///
    /// <para><b>🔑 왜 슬롯 기준인가.</b> key 기준으로 누적하면 떠난 멤버가 안 지워져 <b>10인 방에 11~13명</b>이
    /// 된다(실측 초과 23건). 9명보다 13명이 더 나쁘다. 슬롯(1..정원)으로 키를 잡으면 정원을 구조적으로
    /// 넘을 수 없다. 실측: 슬롯 기준 상태기로 "정원만큼 찬" 비율이 정원5 48.5%→70.9%, 정원10 57.8%→68.5%
    /// 로 오르고 <b>초과 0건 · 슬롯 오염 0건</b>(상태기가 든 이름이 스냅샷의 그 슬롯 이름과 달랐던 적이 없다).</para>
    ///
    /// <para><b>파티 교체만 초기화한다.</b> 서버 파티 id 는 입·퇴장에는 유지되고 재결성에만 바뀐다(코퍼스:
    /// 멤버 집합이 교체·분리된 스냅샷은 22/22가 다른 id, 동일 집합은 2,251/2,251이 같은 id). 그래서 id 가
    /// 실제로 바뀐 경우에만 상태를 버린다. id 0 = 못 읽음이고 절대 '바뀜'으로 치지 않는다.</para>
    ///
    /// <para>⚠️ 종전의 anti-shrink 가드는 <b>제거했다</b>. 그건 "부분 스냅샷이 완전본을 밀어내는" 것을 막으려던
    /// 근사치였는데, 합치는 방식에서는 애초에 밀어낼 일이 없다. 가드가 <c>_partyRosterSetAtMs</c> 를 일부러
    /// 갱신하지 않아 <b>파티는 멀쩡한데 동결만 30분 만료되는</b> 경로(F-05-8)도 같이 사라진다.</para>
    /// </summary>
    public void SavePartyRoster(IReadOnlyList<(string Nickname, int Server, int Slot)> members, int partyId)
    {
        bool differentParty = partyId != 0 && _partyRosterId != 0 && partyId != _partyRosterId;
        if (differentParty)
        {
            _partyRosterBySlot.Clear();
        }

        if (partyId != 0)
        {
            _partyRosterId = partyId;
        }

        // 슬롯을 못 읽은 멤버는 놓을 자리가 없다. 슬롯 충돌이 감지된 스냅샷은 파서가 전 멤버의 슬롯을 0 으로
        // 만들어 보내는데, 그런 스냅샷은 통째로 무시하는 것이 맞다 — 확실히 틀린 배치보다 직전 상태가 낫다.
        int placed = 0;
        foreach ((string nickname, int server, int slot) in members)
        {
            if (slot <= 0)
            {
                continue;
            }

            _partyRosterBySlot[slot] = (nickname, server, slot, RosterKeyOf(nickname, server));
            placed++;
        }

        if (placed == 0 && !differentParty)
        {
            return; // 이 스냅샷이 기여한 것이 없다 — 시계도 건드리지 않는다.
        }

        RepublishRoster();
    }

    /// <summary>0x9702 헤더의 방 정원. <see cref="FreshPartySlots"/> 가 이 값을 로스터 크기로 싣는다.</summary>
    public void SavePartyRosterCapacity(int limitMember)
    {
        if (limitMember is > 0 and <= 20)
        {
            _partyRosterCapacity = limitMember;
        }
    }

    /// <summary>0x971F 멤버 갱신. 실린 슬롯이 권위라 그대로 덮어쓴다.</summary>
    public void UpdatePartyMember(string nickname, int server, int slot, int key)
    {
        if (string.IsNullOrEmpty(nickname) || slot <= 0)
        {
            return;
        }

        _partyRosterBySlot[slot] = (nickname, server, slot, key);
        RepublishRoster();
    }

    /// <summary>0x9622 멤버 제거. 로스터 key 로만 지운다.</summary>
    public void RemovePartyMemberByKey(int key)
    {
        if (key <= 0)
        {
            return;
        }

        int? hit = null;
        foreach ((int slot, (string _, int _, int _, int memberKey)) in _partyRosterBySlot)
        {
            if (memberKey == key)
            {
                hit = slot;
                break;
            }
        }

        if (hit is not { } found || !_partyRosterBySlot.Remove(found))
        {
            return;
        }

        RepublishRoster();
    }

    /// <summary>0x9702 가 같이 실어 온 로스터 key 를 (닉네임, 서버) 로 맞춰 채운다. 제거가 key 로만 오므로
    /// 이게 없으면 스냅샷으로만 알게 된 멤버는 영영 못 지운다.</summary>
    public void SavePartyRosterKeys(IReadOnlyList<(string Nickname, int Server, int Key)> keys)
    {
        foreach ((string nickname, int server, int key) in keys)
        {
            if (key <= 0)
            {
                continue;
            }

            _rosterKeys[(nickname, server)] = key;
            foreach ((int slot, (string nm, int srv, int st, int existing)) in _partyRosterBySlot)
            {
                if (existing == key || !string.Equals(nm, nickname, StringComparison.Ordinal) || srv != server)
                {
                    continue;
                }

                _partyRosterBySlot[slot] = (nm, srv, st, key);
                break;
            }
        }
    }

    private int RosterKeyOf(string nickname, int server) =>
        _rosterKeys.TryGetValue((nickname, server), out int k) ? k : 0;

    /// <summary>슬롯 맵을 기존 독자들이 읽는 리스트로 다시 펴고 시계를 찍는다. 순서는 슬롯 오름차순이다.</summary>
    private void RepublishRoster()
    {
        _partyRoster.Clear();
        foreach (int slot in _partyRosterBySlot.Keys.OrderBy(s => s))
        {
            (string nickname, int server, int st, int _) = _partyRosterBySlot[slot];
            _partyRoster.Add((nickname, server, st));
        }

        _partyRosterAtMs = Clock();
        _partyRosterSetAtMs = _partyRosterAtMs;
    }

    /// <summary>로스터 상태를 통째로 버린다(캐릭터 전환·초기화).</summary>
    private void ClearPartyRosterState()
    {
        _partyRoster.Clear();
        _partyRosterBySlot.Clear();
        _rosterKeys.Clear();
        _partyRosterId = 0;
        _partyRosterCapacity = 0;
        _partyRosterAtMs = 0;
    }

    /// <summary>Known Users for the current party/raid roster — the 0x9702 snapshot matched to uids by
    /// name+server — executor first then power desc. Empty when no roster is known, or when the last
    /// snapshot is older than <paramref name="withinMs"/> (the party was left / it is stale). This is the
    /// authoritative pre-combat party source (the roster packet fires on party formation, before combat).</summary>
    public IReadOnlyList<User> PartyRoster(long withinMs)
    {
        if (_partyRoster.Count == 0 || Clock() - _partyRosterAtMs > withinMs)
        {
            return Array.Empty<User>();
        }

        int exec = _userRepository.Executor();
        User? execUser = exec > 0 ? _userRepository.Get(exec) : null;
        var result = new List<User>();
        foreach ((string nickname, int server, int _) in _partyRoster)
        {
            // Prefer the LIVE executor for the self's roster entry: the self re-registers under a fresh uid each
            // zone load (0x3633) leaving stale name+server duplicates, so FindByNicknameAndServer (FirstOrDefault)
            // would otherwise return a stale self uid (Id != exec, IsExecutor=false) and the preview's own row
            // would fail self-recognition. Mirrors ResolveRosterMemberUid so the data layer is self-consistent.
            User? user = execUser != null
                         && string.Equals(execUser.Nickname, nickname, StringComparison.Ordinal)
                         && execUser.Server == server
                ? execUser
                : _userRepository.FindByNicknameAndServer(nickname, server);
            if (user != null && !string.IsNullOrWhiteSpace(user.Nickname))
            {
                result.Add(user);
            }
        }

        return result
            .OrderByDescending(u => u.Id == exec)
            .ThenByDescending(u => u.Power)
            .ToList();
    }

    /// <summary>The (nickname, server) of every current party/raid roster member (the 0x9702 snapshot),
    /// if it arrived within <paramref name="withinMs"/>; empty otherwise. Unlike <see cref="PartyRoster"/>
    /// this returns the raw roster identities (no uid resolution / drop), used to scope the movement replay
    /// to party/raid members only — works for any party size (slots aren't required, unlike CurrentPartySlots).</summary>
    public IReadOnlyList<(string Nickname, int Server)> PartyMemberIdentities(long withinMs)
    {
        if (_partyRoster.Count == 0 || Clock() - _partyRosterAtMs > withinMs)
        {
            return Array.Empty<(string, int)>();
        }

        return _partyRoster.Select(m => (m.Nickname, m.Server)).ToList();
    }

    /// <summary>The RAW 0x9702 roster — (nickname, server, slot) with NO uid resolution and NO drop.
    /// <para><see cref="PartyRoster"/> silently discards a member whose (nickname, server) matches no uid in the
    /// repository (line "user != null"), and that is exactly the member a nameless row usually belongs to: the
    /// party member this session has never seen. Measured on the corpus, that drop is ~5% of roster members at
    /// combat time — unrecoverable by the display-layer roster recovery, which only ever saw the resolved list.</para>
    /// <para>Display-layer fallback only: these entries carry no uid, no job and no power, so they cannot drive
    /// the job-unique match nor the uid-keyed stale-name repair.</para></summary>
    public IReadOnlyList<(string Nickname, int Server, int Slot)> PartyRosterIdentities(long withinMs)
    {
        if (_partyRoster.Count == 0 || Clock() - _partyRosterAtMs > withinMs)
        {
            return Array.Empty<(string, int, int)>();
        }

        return _partyRoster.ToList(); // defensive copy — the caller hands this to the UI thread
    }

    /// <summary>0x9702 로스터가 실어 온 (닉네임, 서버, 직업코드, 전투력) — 전투 전 파티 프리뷰 행의 직업 아이콘·
    /// 전투력을 채우는 display-only 소스(uid 해석/드롭 없음). 스냅샷이 <paramref name="withinMs"/>보다 오래됐으면 빔.</summary>
    public IReadOnlyList<(string Nickname, int Server, int JobCode, int Power)> PartyRosterJobPower(long withinMs)
    {
        if (_partyRoster.Count == 0 || Clock() - _partyRosterAtMs > withinMs)
        {
            return Array.Empty<(string, int, int, int)>();
        }

        return _partyRosterJobPower
            .Select(kv => (kv.Key.Nickname, kv.Key.Server, kv.Value.JobCode, kv.Value.Power))
            .ToList();
    }

    /// <summary>0x9702 로스터가 실어 온 그 캐릭터의 전투력(없거나 스냅샷이 오래됐으면 0).
    /// <para>본인 전투력은 0x3656이 <b>바뀔 때만</b> 오므로 평범한 세션에서는 거의 오지 않는다(실측 본인 행의
    /// 5.3%). 그 공백을 지금까지 공식 웹 조회가 메워 왔는데, 그건 업로드 워커에서 동기 HTTP로 돌고 실패를 10분간
    /// 캐시하므로 API가 한 번 삐끗하면 그 뒤 10분 치 전투가 통째로 스킵된다. 로스터는 같은 숫자를 패킷으로 이미
    /// 실어 오고(실측 본인 행의 82.5%), 두 소스가 모두 있는 1,474행에서 95.3%가 정확히 일치했다.</para></summary>
    public int PartyRosterPower(string nickname, int server, long withinMs)
    {
        if (string.IsNullOrWhiteSpace(nickname) || server <= 0
            || _partyRoster.Count == 0 || Clock() - _partyRosterAtMs > withinMs)
        {
            return 0;
        }

        return _partyRosterJobPower.TryGetValue((nickname, server), out (int JobCode, int Power) v) ? v.Power : 0;
    }

    /// <summary>0x9702 직업/전투력 스냅샷을 병합 저장(<see cref="StreamProcessor"/> ParsePartyRoster가 SavePartyRoster
    /// 직후 호출). (닉네임,서버)별 최신값으로 갱신만 한다 — 신선도 게이트는 <see cref="PartyRosterJobPower"/>가
    /// <see cref="_partyRosterAtMs"/>로 건다.</summary>
    public void SavePartyRosterJobPower(IReadOnlyList<(string Nickname, int Server, int JobCode, int Power)> members)
    {
        foreach ((string nick, int server, int jobCode, int power) in members)
        {
            _partyRosterJobPower[(nick, server)] = (jobCode, power);
        }
    }

    /// <summary>0x9200 멤버 프로필 한 건 저장(엔티티 uid ↔ 닉네임 + 서버). <see cref="StreamProcessor"/>의
    /// ParseMemberProfile이 구조검증(GUID + 양쪽 서버 일치)을 통과한 멤버마다 호출한다. 표시-계층 보조 소스라
    /// 신원 저장소는 건드리지 않는다.</summary>
    public void SaveMemberProfile(int uid, string nickname, int server)
    {
        if (uid <= 0 || string.IsNullOrWhiteSpace(nickname) || server <= 0)
        {
            return;
        }

        lock (_memberProfiles)
        {
            _memberProfiles[uid] = (nickname, server, Clock());
            if (_memberProfiles.Count > MemberProfileCap)
            {
                // 가장 오래된 매핑부터 버린다(재사용 uid의 낡은 이름이 오래 남지 않도록).
                int oldest = _memberProfiles.OrderBy(kv => kv.Value.At).First().Key;
                _memberProfiles.Remove(oldest);
            }
        }
    }

    /// <summary>최근 <paramref name="withinMs"/> 안에 0x9200이 실어 온 파티/공대 멤버 (uid, 닉네임, 서버).
    /// 무명 전투행을 uid로 직접 명명하는 표시-계층 보조 소스이자, 0x9702가 유실됐을 때의 로스터 폴백.</summary>
    public IReadOnlyList<(int Uid, string Nickname, int Server)> MemberProfileRoster(long withinMs)
    {
        long now = Clock();
        lock (_memberProfiles)
        {
            return _memberProfiles
                .Where(kv => now - kv.Value.At <= withinMs)
                .Select(kv => (kv.Key, kv.Value.Nickname, kv.Value.Server))
                .ToList();
        }
    }

    /// <summary>전투력 기입의 <b>유일한</b> 관문(파서 3경로가 전부 여기로 들어온다). 값 검증을 파서마다
    /// 흩어 두지 않고 여기서 한 번 더 막는다 — 전투력은 배지뿐 아니라 티어 구간·통계 업로드
    /// (사이트의 <c>characters.latest_power</c>)까지 타고 흐르는데, 한 번 잘못 앉으면 되돌릴 경로가
    /// 없기 때문이다: 공식 조회 보정은 <c>Power &lt;= 0</c>일 때만 채우고(<see cref="ApplyOfficialCharacterInfo"/>),
    /// 파서의 carry-forward는 그 값을 재입장마다 새 uid로 다시 찍는다(StreamProcessor의 <c>_lastOwnPower</c>).
    /// 실측 사고(2026-08-17)에서 본인 전투력이 356,559 대신 2,285,1xx로 앉았고, 그 세션의 저장 전투에
    /// 그대로 얼어붙었다.</summary>
    public void SaveUserPower(int uid, int power)
    {
        if (!CombatPower.IsPlausible(power)) return;

        // Get → 수정 → Save 를 따로 하면 수정 구간이 락 밖이다. 공식 조회 콜백(ThreadPool)이 같은 객체의
        // 같은 필드를 동시에 고치므로 Mutate 로 한 번에 끝낸다.
        _userRepository.Mutate(uid, user =>
        {
            if (user.Power != power)
            {
                user.Power = power;
            }
        });
    }

    /// <summary>Returns the User for <paramref name="uid"/>, creating and persisting a bare one (no
    /// nickname/server/job/power) if none exists yet. Lets a damaging actor whose identity packet hasn't
    /// arrived — notably the executor on 난입 (mid-join), whose own-nickname 0x3633 comes late — still get a
    /// row instead of being dropped; the SAME object is enriched in place when SaveNickname / the official
    /// lookup later arrives, so naming, self-color, and upload reconcile automatically.</summary>
    public User EnsureUser(int uid)
    {
        User? existing = _userRepository.Get(uid);
        if (existing != null)
        {
            return existing;
        }

        var user = new User(uid);
        _userRepository.Save(uid, user);
        return user;
    }

    public void SaveNickname(int uid, string nickname, bool isExecutor, int server, int jobByte)
    {
        // 2차 방어선. executor 승격은 되돌리기가 비싼 부작용을 줄줄이 단다(파티 로스터·오드·슈고열쇠·버프
        // 초기화 + 통계 신원 교체 + 동의 모달). 파서가 뚫리면 신원 저장소까지 바로 오염되므로, 엔티티 id
        // 상한만은 여기서 한 번 더 막는다 — 오프셋을 잘못 잡은 varint는 예외 없이 이 범위를 넘는다
        // (2026-07-30 실측 106900). 서버 범위 검증은 파서(SearchOwnNickname)가 담당한다: isExecutor:true의
        // 유일한 생산자가 그 파서이고, 여기에 서버 게이트를 두면 테스트 픽스처의 임의 server 값까지 막힌다.
        if (isExecutor && uid is <= 0 or > MaxEntityUid)
        {
            return;
        }

        JobClass? job = JobClassInfo.ConvertFromCode(jobByte);
        User? user = _userRepository.Get(uid);
        if (user == null)
        {
            user = new User(uid, nickname, server, null, isExecutor);
            _userRepository.Save(uid, user);
        }
        else if (!string.IsNullOrWhiteSpace(user.Nickname)
                 && !string.IsNullOrWhiteSpace(nickname)
                 && !string.Equals(user.Nickname, nickname, StringComparison.Ordinal))
        {
            // Entity ids are reused across pulls (DpsCalculator.ResolveActor relies on it). When a reused
            // id is taken over by a DIFFERENT player (its stored non-blank nickname changes), the prior
            // player's job is still locked on this object and TrySetJob's monotonic first-write-wins would
            // keep it, mislabeling the new occupant with the old class icon. Reset job/power provenance so
            // the new player's jobByte / own skill / official lookup can set the correct values. Gated
            // strictly on a nickname change, so the normal repeated-probe (same name -> same player) path
            // that own-skill correction depends on is untouched.
            user.Job = null;
            user.JobSource = JobProvenance.None;
            user.Power = 0;
            _officialLookupAttempts.Remove(uid);

            // 이 uid가 다른 플레이어에게 넘어갔다 — 그 id를 겨누던 본인 후보는 그 순간 무효다.
            if (_pendingExecutorAnchor?.Uid == uid)
            {
                _pendingExecutorAnchor = null;
            }

            // ...그리고 그 uid로 스테이징해 둔 본인 후보 버프도 무효다 — 새 점유자의 버프가 본인 것으로
            // 재생되면 안 된다.
            lock (_ownerBuffGate)
            {
                _pendingSelfBuffs.Remove(uid);
            }
        }

        user.Nickname = nickname;
        if (server > 0)
        {
            user.Server = server;
        }

        // Snapshot jobByte (ConvertFromCode) is an Authoritative source (the byte right after a probed
        // nickname): it fills a missing job and isn't overwritten by a later same-tier source (e.g. the
        // official lookup), but the player's own job-locked damage skills (OwnSkill) outrank it and can
        // correct a mis-read byte. First write wins within the tier.
        user.TrySetJob(job, JobProvenance.Authoritative);

        _userRepository.Save(uid, user);
        if (isExecutor)
        {
            // 본인 로드 패킷(0x3633)은 "이 uid가 본인"이라는 서버의 직접 선언이다 — 앵커는 그게 오지 않을 때를
            // 메우려고 존재하므로, 도착하는 순간 스테이징된 후보는 무효다. 이걸 지우지 않으면 나중에 옛 후보
            // uid가 딜을 넣을 때 방금 확정된 본인을 도로 밀어낸다.
            _pendingExecutorAnchor = null;
            SaveExecutorId(uid);
        }

        // 여기 있던 이름 앵커 재바인딩은 제거했다. 이 else 분기의 유일한 실제 호출자는 0x3645(타인 닉네임)인데
        // 0x3645는 본인 닉네임을 싣지 않는다(코퍼스 13,076프레임 0건) — 그래서 그 코드는 한 번도 실행되지
        // 않았다. 앵커는 0x9200 멤버 프로필 → TryBindExecutorByIdentity로 옮겼고, 즉시 승격하지 않는다.
    }

    /// <summary>엔티티 id 공간의 상한. 코퍼스 4,718개 uid의 최댓값이 정확히 이 값이고 초과 사례가 0이라,
    /// 이보다 큰 값은 오프셋 오독(패킷 안의 다른 필드를 uid로 읽음)이다.</summary>
    private const int MaxEntityUid = 16383;

    /// <summary>스테이징된 앵커의 수명. 이 안에 그 uid가 등장하지 않으면 버린다. 짧게 잡는 이유는
    /// <b>엔티티 id 재사용</b>이다 — 후보를 오래 들고 있을수록 그 사이 게임이 그 id를 다른 플레이어에게
    /// 재발급할 창이 커진다. 실측상 유효한 후보는 수 초~수십 초 안에 판가름 난다(승격까지 0.35초, 전투 전
    /// 도착도 40~46초).</summary>
    private const long PendingAnchorTtlMs = 90 * 1000L;

    private (int Uid, string Nickname, int Server, long AtMs)? _pendingExecutorAnchor;

    /// <summary>이름 앵커 — 본인 로드 패킷(0x3633) 없이도 본인을 새 엔티티 id에 다시 묶는다.
    /// <para>존 이동·난입으로 본인의 uid가 바뀌어도 게임이 본인 로드 패킷을 항상 다시 보내지는 않는다. 그동안
    /// 본인 딜은 신원 미상으로 남고, 그걸 메우려던 휴리스틱 복구가 낯선 사람을 본인으로 둔갑시킨 사고가 있었다
    /// (필드보스 오귀속). 이 경로는 추정이 아니라 <b>신원 완전일치</b>다: 0x9200 멤버 프로필이 실어 온
    /// (닉네임, 서버)가 현재 본인과 정확히 같을 때만 그 uid가 후보가 된다.</para>
    /// <para>여기서 <b>즉시 승격하지 않는다</b>. 실측상 본인 레코드의 21%가 그 세션 내내 한 번도 등장하지 않는
    /// uid를 가리키는데, 그런 uid로 executor를 옮기면 본인 행·자기색·버프 게이트·통계 업로드가 통째로 죽는다.
    /// 대신 후보로 적재해 두고 <see cref="PromotePendingAnchorIfActive"/>가 "그 uid가 실제로 데미지를 넣었다"는
    /// 증거를 본 뒤에 승격시킨다 — 안 싸우는 uid는 영영 승격되지 않으므로 그 실패 모드가 구조적으로 사라진다.</para>
    /// <para>가드 — ① 현재 본인이 확정돼 있어야 한다(앵커가 없으면 본인을 만들어낼 수 없다) ② 닉네임 완전일치
    /// ③ 서버는 <b>양쪽 다</b> 알아야 하고 같아야 한다(fail-closed — 아래 참조) ④ 엔티티 id 공간 밖은 오프셋
    /// 오독 ⑤ 몹/소환수로 이미 알려진 id는 본인일 수 없다 ⑥ 그 uid에 이미 <b>다른 이름</b>이 박혀 있으면
    /// 안 된다.</para>
    /// <para>서버를 fail-closed로 두는 이유: 모를 때 통과시키면 <b>타 서버 동명이인</b>이 본인으로 승격되고,
    /// 그 뒤로는 아무 증상 없이 남의 캐릭터 신원으로 통계가 올라간다. 실측상 본인 로드 489건이 전부 서버를
    /// 싣고 왔으므로(서버 미상 0건) 막아서 잃는 것이 없다.</para></summary>
    public void TryBindExecutorByIdentity(int uid, string nickname, int server)
    {
        if (uid is <= 0 or > MaxEntityUid || string.IsNullOrWhiteSpace(nickname) || server <= 0)
        {
            return;
        }

        int executor = _userRepository.Executor();
        if (executor == 0 || executor == uid)
        {
            return;
        }

        User? current = _userRepository.Get(executor);
        if (current == null
            || string.IsNullOrWhiteSpace(current.Nickname)
            || !string.Equals(current.Nickname, nickname, StringComparison.Ordinal))
        {
            return;
        }

        if (current.Server <= 0 || current.Server != server)
        {
            return; // 타 서버 동명이인이거나, 본인 서버를 모른다 — 어느 쪽이든 본인이라고 단정할 수 없다
        }

        if (!IsAnchorableUid(uid, nickname))
        {
            return;
        }

        _pendingExecutorAnchor = (uid, nickname, server, Clock());
    }

    /// <summary>그 엔티티 id를 본인으로 삼아도 되는가. 스테이징 때와 승격 때 <b>두 번</b> 확인한다 — 그 사이에
    /// 게임이 id를 재발급하거나(엔티티 id는 실제로 재사용된다) 몹/소환수로 정체가 드러날 수 있고, 그때 그냥
    /// 승격시키면 executor가 남을 가리킨 채로 통계까지 그 신원으로 올라간다.</summary>
    private bool IsAnchorableUid(int uid, string nickname)
    {
        if (IsMobInstance(uid) || SummonerId(uid) != null)
        {
            return false;
        }

        User? occupant = _userRepository.Get(uid);
        return occupant == null
               || string.IsNullOrWhiteSpace(occupant.Nickname)
               || string.Equals(occupant.Nickname, nickname, StringComparison.Ordinal);
    }

    /// <summary>스테이징된 이름 앵커를, 바로 그 uid가 데미지를 넣은 순간 승격시킨다. 가드는 승격 직전에 다시
    /// 확인한다 — 그 사이에 0x3633이 도착해 본인이 이미 옮겨갔거나 캐릭터가 바뀌었을 수 있다.</summary>
    private void PromotePendingAnchorIfActive(int uid)
    {
        if (_pendingExecutorAnchor is not { } pending)
        {
            return;
        }

        if (Clock() - pending.AtMs > PendingAnchorTtlMs)
        {
            _pendingExecutorAnchor = null;
            return;
        }

        if (pending.Uid != uid)
        {
            return;
        }

        int executor = _userRepository.Executor();
        User? current = executor != 0 ? _userRepository.Get(executor) : null;
        if (current == null || !string.Equals(current.Nickname, pending.Nickname, StringComparison.Ordinal))
        {
            _pendingExecutorAnchor = null; // 앵커가 사라졌거나 다른 캐릭터가 됐다 — 이 후보는 무효다
            return;
        }

        _pendingExecutorAnchor = null;
        if (executor == uid)
        {
            return; // 그 사이 0x3633이 같은 uid로 도착했다
        }

        // 스테이징 이후에 그 id가 다른 플레이어에게 재발급됐거나 몹/소환수로 밝혀졌을 수 있다. 여기서 다시
        // 확인하지 않으면 "재사용된 uid + 그 새 주인이 딜"이 곧바로 본인 둔갑이 된다(몹은 상시 피격이라
        // 데미지 증거도 자동으로 충족된다).
        if (!IsAnchorableUid(pending.Uid, pending.Nickname))
        {
            return;
        }

        // 딜만 넣고 신원 패킷이 없던 uid는 이름이 비어 있다. 승격이 곧 "이 uid가 본인"이라는 확정이므로 채운다.
        User promoted = EnsureUser(pending.Uid);
        if (string.IsNullOrWhiteSpace(promoted.Nickname))
        {
            promoted.Nickname = pending.Nickname;
            if (pending.Server > 0)
            {
                promoted.Server = pending.Server;
            }

            _userRepository.Save(pending.Uid, promoted);
        }

        SaveExecutorId(pending.Uid);
    }

    private void SaveExecutorId(int uid)
    {
        int executor = _userRepository.Executor();
        if (executor != uid)
        {
            // Capture both identities BEFORE flipping the flag so we can tell a real character SWITCH (a
            // different character connects) from the same character RE-INSTANCING under a fresh uid on a
            // zone/instance load. The new executor's nickname is already set (SaveNickname writes it before
            // calling here); the prior executor User is still present (the 3-cap eviction never removes it).
            User? oldExec = executor != 0 ? _userRepository.Get(executor) : null;
            User? newExec = _userRepository.Get(uid);

            // 승격 대상이 저장소에 없으면 포인터를 뒤집지 않는다. 종전 순서(먼저 Executor(uid) → 그 다음
            // newExec! 역참조)는 NRE가 나는 순간 "ExecutorId()는 0이 아닌데 User(ExecutorId())는 null"인
            // 반영구 상태를 남겼고, 그 예외는 dispatch의 catch에 삼켜져 증상만 남는다.
            if (newExec == null)
            {
                return;
            }

            if (oldExec != null)
            {
                oldExec.IsExecutor = false;
            }

            _userRepository.Executor(uid);
            newExec.IsExecutor = true;

            // When this install last learned WHO it is watching. The weekly 성역 counters need it: the 0x610B
            // login snapshot beats the own-load packet that names the character by ~4 s at every zone-in
            // measured, so a counter filed against "whoever the executor is right now" lands on the PREVIOUS
            // character on a switch. Stamping the identity lets the app wait for the identity that came after
            // the snapshot rather than the one that happened to still be current when it arrived.
            long identityAtMs = Clock();
            if (!string.IsNullOrWhiteSpace(newExec.Nickname))
            {
                Interlocked.Exchange(ref _executorIdentityAtMs, identityAtMs);
            }

            // A character switch (콘팡 -> 마이농) must drop the previous character's pre-combat preview state
            // — the 0x9702 party snapshot here, and the UI-side recent-combat tracker via the event below — so
            // the previous character doesn't linger as a stale idle 0/s row under the new character. A
            // same-character re-instance (same name+server, fresh uid on a zone load) KEEPS it: the party about
            // to form in the new zone is still ours. Both nicknames must be non-blank (an unknown identity never
            // triggers a clear), and the server is compared ONLY when both are known (>0): a truncated 0x3633
            // leaves Server=-1, which must not read as a cross-server switch (that would false-clear a
            // legitimate dungeon party preview on every truncated re-instance).
            bool identityKnown = oldExec != null
                && !string.IsNullOrWhiteSpace(oldExec.Nickname)
                && !string.IsNullOrWhiteSpace(newExec.Nickname);
            bool identityChanged = false;
            if (identityKnown)
            {
                bool nameChanged = !string.Equals(oldExec!.Nickname, newExec.Nickname, StringComparison.Ordinal);
                bool serverChanged = oldExec.Server > 0 && newExec.Server > 0 && oldExec.Server != newExec.Server;
                identityChanged = nameChanged || serverChanged;
            }

            // 같은 캐릭터가 새 uid로 재등록된 경우(존/인스턴스 로드, 난입) 직업을 넘겨준다. 이게 없으면
            // 0x9200 이름앵커로 승격된 본인은 Job=null / JobSource=None 으로 출발하고, 본인이 직업 전용
            // 스킬을 처음 꽂을 때(OwnSkill)까지 직업 미상으로 남는다 — 쿨타임 픽커의 '내 직업만 보기'가
            // CanFilterByJob => OwnJobBand != 0 이라 아예 잠기고 9직업 221개가 통째로 뜨는 증상이 그것이다.
            // 0x3633(본인 로드)이 오는 경로는 그 패킷이 직업 바이트를 같이 실어 여기 오기 전에 이미
            // TrySetJob(Authoritative)을 마쳤으므로, 실제로 비는 건 앵커 승격 경로뿐이다.
            //
            // ⚠️ 승격이 아니라 '이관'이다 — TrySetJob이 provenance 사다리를 그대로 지킨다(STRICTLY higher만
            // 기록). 새 uid가 이미 같은/더 높은 출처로 직업을 잡았으면 건드리지 않고, OwnSkill을 Authoritative로
            // 강등시키지도 않는다. 캐릭터가 실제로 바뀐 경우(identityChanged)와 한쪽 신원이 비어 있는 경우
            // (identityKnown == false)는 남의 직업을 칠할 수 있으므로 제외한다.
            if (identityKnown && !identityChanged)
            {
                newExec.TrySetJob(oldExec!.Job, oldExec.JobSource);
            }

            if (identityChanged)
            {
                ClearPartyRosterState();

                // 오드 / 슈고 열쇠 ride the 0x610B login dump, which the comment above records as arriving ~4 s
                // BEFORE this naming packet. So the newest reading at this instant is usually the INCOMING
                // character's, and clearing it unconditionally — as this did until 2026-08-11 — threw away the
                // one correct value we had, blanking the footer badge until the game next chose to broadcast
                // (observed: 9 s to 15 min, sometimes not for the rest of the session). A reading older than the
                // grace window really is the outgoing character's and still goes.
                // Judged per resource, from that resource's OWN arrival stamp. Sharing one verdict looks tidy and
                // is wrong: the two travel in the same packet but not in the same records, and the shugo key
                // goes silent entirely at zero (see ShugoArrivedWithHandover), so the aether stamp would veto
                // exactly the clear that keeps the previous character's key count off the new character.
                if (!AetherArrivedWithHandover(identityAtMs))
                {
                    ClearAetherStatus();
                }

                if (!ShugoArrivedWithHandover(identityAtMs))
                {
                    ClearShugoKey();
                }

                ClearOwnerBuffs();   // the previous character's buffs, likewise
                ExecutorIdentityChanged?.Invoke();
            }

            // 스탯 사전의 주인을 확정한다. 신원보다 스탯이 먼저 도착하므로(실측 ~6초) 여기서 보류분이 반영된다.
            // ⚠️ uid가 바뀌었다고 비우면 안 된다 — 본인은 존/인스턴스를 넘을 때마다 새 uid로 재등록되므로
            // (이 메서드 맨 위 주석이 그 구분을 위해 oldExec/newExec를 미리 잡아 둔다) uid 기준으로 비우면
            // 로딩 때마다 스탯이 날아가고, 하필 그 로딩 중에 오는 전체 스냅샷까지 같이 날아간다. 캐릭터가
            // 실제로 바뀐 경우(identityChanged)에만 비운다.
            _playerStats.SetOwner(uid, resetSheet: identityChanged);

            // Now that this uid is the confirmed executor, replay any self-buffs that were staged while it went
            // unrecognized (owner==0 / stale on a late 0x3633). MUST run after the identityChanged ClearOwnerBuffs
            // above — replaying before it would wipe the freshly-restored buffs on a character switch.
            ReplayStagedSelfBuffs(uid);
            ReplayStagedSelfCooldowns(uid);
        }
    }

    public void RequestOfficialCharacterLookup(int uid)
    {
        User? user = _userRepository.Get(uid);
        if (user == null)
        {
            return;
        }

        RequestOfficialCharacterLookup(uid, user.Nickname, user.Server, user.Job);
    }

    public void RequestOfficialCharacterLookup(
        int uid,
        string? nickname,
        int server,
        JobClass? job,
        Action<OfficialCharacterInfo>? onResult = null)
    {
        if (OfficialLookup == null)
        {
            return; // no network (replay / headless without enrichment)
        }

        if (string.IsNullOrWhiteSpace(nickname) || server <= 0)
        {
            return;
        }

        long now = Clock();
        // The 10-min throttle only guards the fire-and-forget power-enrichment path (onResult == null), whose
        // result is persisted on the User object so a re-request within the window is pure waste. A caller that
        // passes a callback (the party-join panel, which injects skill/stigma badges per request) MUST always
        // reach LookupAsync — its own 6h/10min TTL cache + in-flight de-dup already suppress redundant network
        // calls, and answer a cached character synchronously. Throttling the callback path here silently dropped
        // the callback on any re-application within 10 min, leaving the join card with no badges.
        if (onResult == null && _officialLookupAttempts.TryGetValue(uid, out long previous) && now - previous < 10 * 60 * 1000L)
        {
            return;
        }

        if (uid > 0)
        {
            _officialLookupAttempts[uid] = now;
        }

        OfficialLookup.LookupAsync(nickname, server, job, info =>
        {
            ApplyOfficialCharacterInfo(uid, info);
            onResult?.Invoke(info);
        });
    }

    /// <summary>
    /// 공식 조회 값을 <b>기다리지 않고</b> 돌려준다: 캐시에 있으면 그 값, 없으면 비동기 요청만 걸고 null.
    /// <para>🔑 이 메서드는 리포트 빌드 경로에서만 불리고, 그 경로는 <b>UI 스레드</b>에서 돈다
    /// (<c>App.xaml.cs</c> 의 <c>Dispatcher.Invoke</c> 람다 → <c>StatsPayloadBuilder</c>). 종전에는 여기서
    /// <c>LookupBlocking</c> 을 쳐서 연결 8초 / 읽기 15초 타임아웃만큼 오버레이가 얼었고, 캡처 소비자 스레드도
    /// <c>Invoke</c> 반환을 기다리며 같이 멈췄다 — 전투력이 안 잡히는 캐릭터에서 10분마다 최대 5초씩.</para>
    /// <para>이번 틱에 null 을 돌려줘도 손해가 작다: 리포트 틱은 전투 중 500ms 마다 돌고, 첫 틱이 건 요청이
    /// 몇 초 안에 끝나면 <b>그 다음 틱부터</b> 캐시 적중이라 전투 종료 시점의 업로드 페이로드에는 값이 실린다.
    /// 반대로 얼어붙는 비용은 전투 중 사용자가 바로 체감한다.</para>
    /// </summary>
    public OfficialCharacterInfo? ResolveOfficialCharacterInfo(int uid, string? nickname, int server, JobClass? job)
    {
        if (OfficialLookup == null)
        {
            return null;
        }

        if (OfficialLookup.LookupCached(nickname, server) is { } cached)
        {
            ApplyOfficialCharacterInfo(uid, cached);
            return cached;
        }

        // 요청만 걸고 이번 틱은 포기한다. 이 경로는 10분 스로틀 + 조회기 자신의 TTL·in-flight 중복제거가
        // 걸려 있어 매 틱 호출해도 네트워크가 늘지 않는다.
        RequestOfficialCharacterLookup(uid, nickname, server, job);
        return null;
    }

    private void ApplyOfficialCharacterInfo(int uid, OfficialCharacterInfo info)
    {
        // 🔑 이 메서드는 **ThreadPool**(공식 조회 콜백)에서 돈다. 아래 수정은 캡처 소비자 스레드의
        // SaveNickname/SaveUserPower 와 같은 객체를 건드리므로 반드시 저장소 락 안에서 일어나야 한다.
        bool mutated = uid > 0 && _userRepository.Mutate(uid, existing =>
        {
            if (string.IsNullOrWhiteSpace(existing.Nickname))
            {
                existing.Nickname = info.Nickname;
            }

            if (existing.Server <= 0)
            {
                existing.Server = info.Server;
            }

            // Official pcId is Authoritative (same tier as the snapshot jobByte; first write wins, so it
            // doesn't clobber a job the live snapshot already set). The player's own job-locked skills
            // (OwnSkill) still win — a short-name lookup can resolve a DIFFERENT same-name character, so live
            // combat evidence is the final arbiter.
            existing.TrySetJob(info.Job, JobProvenance.Authoritative);

            // 공식 조회 값에는 상한을 걸지 않는다. <see cref="CombatPower"/> 상한은 "바이트 스캔이 엉뚱한
            // u32를 집었다"를 막는 장치인데, 이쪽은 스캔이 아니라 캐릭터를 정확히 지목해 받은 구조화된
            // JSON이라 그런 오염이 없다. 오히려 상한을 여기까지 걸면 전투력 인플레가 상한을 넘겼을 때
            // 패킷·웹 양쪽이 동시에 막혀 전투력이 통째로 사라진다 — 지금은 패킷이 막혀도 이 경로가
            // 정상값을 채워 주는 안전망이다.
            if (existing.Power <= 0 && info.Power > 0)
            {
                existing.Power = info.Power;
            }
        });

        if (mutated)
        {
            return;
        }

        var pending = new User(uid, info.Nickname, info.Server, info.Job, power: info.Power)
        {
            JobSource = info.Job != null ? JobProvenance.Authoritative : JobProvenance.None,
        };
        _userRepository.SavePending(pending);
    }

    // ---- buff ----

    public void SaveUseBuff(int uid, UseBuff useBuff) => _useBuffRepository.Save(uid, useBuff);

    /// <summary>스킬 시전 1회를 액터별로 적재한다(0x3802). <b>self 게이트를 재사용하지 않는다</b> —
    /// 상세창은 클릭한 아무 행이나 그리므로 본인만 담으면 파티원 행에서 타임라인이 빈다. 필드에서 지나가는
    /// 비파티원의 시전까지 쌓일 수 있지만, 창 질의(<see cref="BattleSkillCasts"/>)와 저장 시 동결이 전투
    /// 참가자만 남기고, 저장소 자체도 액터당 상한 + 전투 저장 시 정리로 묶여 있다.</summary>
    public void SaveSkillCast(int actorId, int skillCode, long arrivedAt, bool startsCooldown = false)
    {
        if (actorId <= 0)
        {
            return;
        }

        // 열려 있는 전투 창은 보존 정리에서 뺀다 — 10분을 넘기는 전투에서 정리가 진행 중인 그 전투의
        // 앞부분을 지우면, 얼려 둔 타임라인이 이미 잘린 채로 저장된다. 조회 쪽이 주는 여유와 같은 폭을 준다.
        long battleStart = CurrentBattleStart();
        long keepFrom = battleStart > 0 ? battleStart - PreemptiveCastWindowMs : long.MaxValue;
        _skillCastRepository.Save(actorId, new SkillCast(skillCode, arrivedAt, startsCooldown), keepFrom);
    }

    /// <summary>시전 조회·보존이 전투 시작보다 앞서 허용하는 여유. 피해 파이프라인의
    /// <c>PreemptivePacketWindowMs</c>와 같은 값이어야 오프너 시전이 조회에는 잡히는데 정리에는 지워지는
    /// 어긋남이 안 생긴다.</summary>
    private const long PreemptiveCastWindowMs = 1000L;

    public void SaveUseBuff(int uid, int skillCode, long buffStart, long buffEnd, long duration, int actorId) =>
        SaveUseBuff(uid, skillCode, buffStart, buffEnd, duration, actorId, 0);

    /// <summary><paramref name="level"/> = 어노멀 레벨(0 = 모름). 서로 중복 적용되지 않는 버프 쌍에서 높은 쪽을
    /// 고르는 데 쓰인다.</summary>
    public void SaveUseBuff(int uid, int skillCode, long buffStart, long buffEnd, long duration, int actorId, int level) =>
        SaveUseBuff(uid, skillCode, buffStart, buffEnd, duration, actorId, level, 0);

    /// <summary><paramref name="slot"/> = 그 대상의 버프 슬롯 번호(0 = 모름). 제거 브로드캐스트(0x382C)가
    /// 이 슬롯을 지목하므로, 들고 있어야 정확히 그 인스턴스만 지울 수 있다.</summary>
    public void SaveUseBuff(int uid, int skillCode, long buffStart, long buffEnd, long duration, int actorId, int level, int slot)
    {
        SaveUseBuff(uid, new UseBuff(skillCode, buffStart, buffEnd, duration, actorId, level, slot));

        // Live combat-assist overlay: track buffs currently ON the local player (recipient == executor), so
        // the overlay can show what's active + how long is left. Job-skill buffs only — consumable/item buffs
        // (food/drink/scroll/potion, in the lower item-code band) and blacklisted buffs are excluded.
        int owner = _userRepository.Executor();

        // Buff-tracking diagnostics (crowded-raid overlay failure investigation). Counts, per job-buff seen,
        // whether it was accepted onto the self-overlay (uid==owner), or lost because the executor is unknown
        // (owner==0, e.g. a self-recognition 0x3633 dropped on a flooded instance entry). Single-consumer
        // thread, so plain increments. Read via BuffDiagSnapshot on the same thread.
        if (IsJobBuffCode(skillCode) && !IsBuffBlacklisted(skillCode))
        {
            _diagJobBuffSeen++;
            if (owner == 0)
            {
                _diagOwnerZeroJobBuff++;
            }

            // SelfAccepted is NOT counted here. It used to be, one branch above the store — so a frame that
            // passed the executor gate but never reached _ownerBuffs (M-13: a UI subscriber threw out of
            // RecordObservedBuff and unwound the rest of this method) still reported itself as accepted. A
            // counter that says "on the overlay" about a buff that is not on the overlay removes the only
            // instrument that could find that loss. It is incremented past the store below instead. (A
            // fully-Off buff still counts there: the picker dropping it is a decision, not a loss, and the
            // counter's job is to discriminate executor-gate failures, not picker settings.)
        }

        if (!IsJobBuffCode(skillCode) || IsBuffBlacklisted(skillCode))
        {
            return; // item/consumable/blacklisted — never on the job-buff overlay
        }

        // Store unless fully Off (hidden AND not voice). A "음성만" buff (hidden + voice) is still stored so the
        // announce path can speak it; the overlay drops it downstream via OwnerBuffView.Overlay.
        bool storable = !IsBuffHidden(skillCode) || IsBuffVoice(skillCode);

        if (owner != 0 && uid == owner)
        {
            if (storable)
            {
                (int baseCode, var entry) = ComputeOwnerBuffEntry(skillCode, buffStart, buffEnd, duration, actorId, level, slot);
                lock (_ownerBuffGate)
                {
                    // Key by BASE code so the SAME buff re-cast by a different player/rank refreshes the one slot
                    // in place (no duplicate icon, no duplicate start alert) — the later cast takes over.
                    _ownerBuffs[baseCode] = entry;
                }

                LiveBuffsChanged?.Invoke();
            }

            // Counted only now, after the store the counter claims. See the diagnostics block above.
            _diagSelfBuffAccepted++;

            // The picker catalogue is populated LAST, after the buff is on the overlay. It raises
            // BuffCatalogChanged, whose subscribers live in the UI layer; putting it first meant one throwing
            // subscriber skipped the store for the very frame that discovered the code — and since the code is
            // then already in _observedBuffBases, no later frame raises the event again, so the loss never
            // repeats and never shows up as anything but a missing icon (M-13). Order is the half of that fix
            // that lives here; RecordObservedBuff isolates the raise itself.
            RecordObservedBuff(skillCode);
            return;
        }

        if (IsPartyMember(uid))
        {
            // A party member's job buff — not shown on the (self-only) overlay, but catalogued so the picker
            // lists other jobs' buffs too (self + party coverage).
            RecordObservedBuff(skillCode);
        }

        // The executor may not be recognized yet: the own-load 0x3633 is a single, easily-lost packet that on a
        // reconnect / character switch arrives tens of seconds late (owner==0 or stale meanwhile — measured +246 s
        // on one corpus). Any self job-buff in that window fails the uid==owner gate above and is dropped, so the
        // overlay stays blank for the first fight (the reported "버프 오버레이가 첫 전투엔 안 뜨다가 설정 다녀오면
        // 뜬다" — it is elapsed-time DATA recovery, not visibility). Stage this buff as a SELF CANDIDATE keyed by
        // its entity uid; when SaveExecutorId later CONFIRMS that uid is the executor (via 0x3633 or the identity
        // anchor), its still-live staged buffs are replayed onto the overlay. Only the confirmed-executor uid ever
        // commits — a party member / mob uid never becomes executor and is TTL-pruned — so no mis-attribution.
        if (storable && CouldBeSelfEntity(uid))
        {
            StageSelfBuffCandidate(uid, skillCode, buffStart, buffEnd, duration, actorId, level, slot);
        }
    }

    private (int BaseCode, (long End, int Actor, long Duration, bool Indefinite, int Level, int Slot) Entry) ComputeOwnerBuffEntry(
        int skillCode, long buffStart, long buffEnd, long duration, int actorId, int level, int slot)
    {
        int baseCode = BuffDisplayBase(skillCode);
        bool indefinite = baseCode == IndefiniteStanceBaseCode; // 폭주: synthetic-TTL maintained stance
        // Keep the maintained stance on screen well past its short synthetic duration so a held re-broadcast gap
        // doesn't false-expire it; a real "off" then clears within the keep-alive.
        long overlayEnd = indefinite ? buffStart + IndefiniteStanceOverlayKeepAliveMs : buffEnd;
        return (baseCode, (overlayEnd, actorId, duration, indefinite, level, slot));
    }

    // A uid that could plausibly be the local player before its own-load packet is recognized: inside the entity
    // id space, not a known mob, not a summon. Bounds the staging set to player-ish entities (~party size).
    private bool CouldBeSelfEntity(int uid) =>
        uid is > 0 and <= MaxEntityUid && !IsMobInstance(uid) && SummonerId(uid) is null;

    /// <summary>버프 제거 브로드캐스트(0x382C) 반영. <b>슬롯이 일치하는 항목만</b> 다룬다.
    /// <para>지금까지는 제거 신호가 없다고 보고 duration이 다 흐를 때까지 슬롯을 남겨 뒀는데, 실측상 서버가
    /// 예상 만료보다 1초 이상 일찍 끊는 경우가 절반을 넘어(0x382C로 종료된 인스턴스의 57.6%) 오버레이가
    /// 오래 과다 표시되고 있었다. 슬롯 매칭이라 같은 코드가 겹쳐 걸려도 엉뚱한 인스턴스를 지울 수 없다.</para>
    /// <para>슬롯을 모르는(0) 엔트리는 건드리지 않는다 — 기존 만료 로직이 그대로 처리한다(fail-open).</para>
    /// <para>🔑 <b>두 저장소의 범위가 다르다.</b> 집계 저장소(<c>_useBuffRepository</c>)는 <b>전 엔티티</b>를
    /// 끊고, 오버레이 사전은 <b>본인 것만</b> 지운다. executor 게이트가 종전처럼 메서드 첫 줄에 있으면 파티원과
    /// 보스의 조기 해제가 집계에 영원히 반영되지 않는다 — 실측상 조기 해제로 인한 과다 표시 232.7시간 중
    /// <b>본인은 23.2시간(10%)</b> 뿐이고 나머지 90%가 파티원(서포터 rDPS 입력)과 보스(디버프 표)다. 그대로 두면
    /// 상세창 안에서 「내 버프」 행만 맞고 보스 디버프 행은 틀린 채 남아, 한 화면에 두 정확도가 섞인다.</para>
    /// <para>⚠️ 반대로 오버레이 삭제까지 전 엔티티로 열면 <b>파티원의 해제가 내 오버레이를 지운다</b> — 원래
    /// 게이트가 막고 있던 것이 그것이다. 그래서 게이트를 없애는 게 아니라 <b>오버레이 블록 전용으로 내린다.</b></para></summary>
    public void RemoveBuffSlots(int entityId, IReadOnlyList<int> slots, long arrivedAt)
    {
        if (entityId <= 0 || slots.Count == 0)
        {
            return;
        }

        // 집계 저장소: 전 엔티티. 여기가 상세창 가동률 · nDPS/rDPS · 업로드 OperatingRate 의 단일 원천이다.
        _useBuffRepository.TruncateOpenSlots(entityId, slots, arrivedAt);

        // 오버레이 사전: 본인 것만. 위 주석의 ⚠️ 참고.
        if (entityId != _userRepository.Executor())
        {
            return;
        }

        bool changed = false;
        lock (_ownerBuffGate)
        {
            foreach (int baseCode in _ownerBuffs
                         .Where(kv => kv.Value.Slot != 0 && slots.Contains(kv.Value.Slot))
                         .Select(kv => kv.Key)
                         .ToList())
            {
                _ownerBuffs.Remove(baseCode);
                changed = true;
            }
        }

        if (changed)
        {
            LiveBuffsChanged?.Invoke();
        }
    }

    /// <summary>엔티티 사망(0x8D04). <b>본인</b>이 죽었을 때만 버프 오버레이 스토어를 비운다 — 사망 후
    /// 부활하면 게임에서 모든 버프가 날아간 상태이기 때문이다.
    /// <para>쿨다운(<c>_cooldowns</c>)은 <b>비우지 않는다</b>: 사망이 스킬 쿨다운을 초기화하지는 않으므로
    /// 함께 지우면 다음 0x3847 스냅샷이 올 때까지 "쿨타임 회색" 표시가 틀리게 된다.</para>
    /// <para><see cref="OwnerBuffClearRevision"/>을 올려 두면 500ms 오버레이 틱이 "이번 틱에 사망 클리어가
    /// 있었다"를 알 수 있다 — 스냅샷을 뜬 직후 클리어가 들어오는 서브초 레이스에서 잔여 버프가 종료 음성을
    /// 외치는 것을 막는 용도다(사망으로 인한 초기화에는 종료 알림을 내지 않는다).</para></summary>
    public void SaveEntityDeath(int entityId, long arrivedAt)
    {
        int owner = _userRepository.Executor();
        if (owner == 0 || entityId != owner)
        {
            return; // 몹·파티원 사망은 오버레이와 무관
        }

        lock (_ownerBuffGate)
        {
            _ownerBuffs.Clear();
            _ownerBuffClearRevision++;
        }

        LiveBuffsChanged?.Invoke();
    }

    private long _ownerBuffClearRevision;

    /// <summary>사망으로 버프 스토어가 비워질 때마다 증가. 오버레이 틱이 값 변화를 보고 종료 음성을 건너뛴다.</summary>
    public long OwnerBuffClearRevision
    {
        get { lock (_ownerBuffGate) { return _ownerBuffClearRevision; } }
    }

    /// <summary>회생의 계약 긴급 회복 발동. 발동 시각을 기록하고(가동률 표의 "N회" 집계용), 본인 것이면
    /// 오버레이 슬롯을 60초 재발동 대기로 채운다.
    /// <para>일부러 <see cref="SaveUseBuff(int, UseBuff)"/>를 쓰지 않는다 — 그 경로는 _useBuffRepository →
    /// 버프 업타임 집계 → 통계 웹 페이로드로 흘러가므로, 우리가 합성한 60초짜리 "버프"가 실제로는 존재하지
    /// 않는 업타임으로 업로드된다. 이 데이터는 미터 화면 전용이다.</para></summary>
    public void SaveRevivalHeal(int uid, int skillCode, long amount, long arrivedAt)
    {
        if (uid <= 0)
        {
            return;
        }

        lock (_revivalHealGate)
        {
            if (!_revivalHeals.TryGetValue(uid, out List<(long At, int Code)>? list))
            {
                list = new List<(long At, int Code)>();
                _revivalHeals[uid] = list;
            }

            list.Add((arrivedAt, skillCode));
            if (list.Count > RevivalHealsPerUserCap)
            {
                list.RemoveRange(0, list.Count - RevivalHealsPerUserCap);
            }
        }

        // 오버레이/음성은 본인 것만. (이 프레임은 주변 플레이어 전원에 대해 방송된다.)
        if (uid != _userRepository.Executor())
        {
            return;
        }

        int baseCode = RevivalContractBase(skillCode);
        int cooldownCode = RevivalHealCooldownCode(baseCode);

        // 합성 코드는 이제 buff_catalog.json 이 다섯 직업분을 모두 싣고 있으므로 이름·직업·picker 노출이
        // 기동 시점에 이미 서 있다. 아래는 카탈로그에 빠진 직업이 생겼을 때의 그물일 뿐이다.
        //
        // 예전에는 이 등록이 유일한 경로였고, 그게 결함이었다: 이름표는 프록이 실제로 터져야 채워지는데
        // buffUi.observed 는 저장된다 → 재기동 후 그 직업을 하기 전까지 picker 에 "스킬 13790007" 이라는
        // 정체불명 행으로 남았다. 지금 하는 직업만 멀쩡해 보이는 이유가 이것이었다.
        lock (_buffPickerGate)
        {
            if (!_buffNames.ContainsKey(cooldownCode))
            {
                string job = _buffNames.TryGetValue(baseCode, out (string Name, string Job) bn) ? bn.Job : "기타";
                _buffNames[cooldownCode] = (RevivalHealCooldownName, job);
                _knownBuffBases.Add(cooldownCode);
            }
        }

        if (!IsBuffHidden(cooldownCode) || IsBuffVoice(cooldownCode)) // picker에서 완전히 끈 항목은 담지 않는다
        {
            lock (_ownerBuffGate)
            {
                _ownerBuffs[cooldownCode] = (arrivedAt + RevivalHealCooldownMs, uid, RevivalHealCooldownMs, false, 0, 0);
            }

            LiveBuffsChanged?.Invoke();
        }

        // 관측 기록은 스토어 뒤에 — SaveUseBuff와 같은 이유다(M-13). 이 호출이 UI 구독자에게 이벤트를 쏘므로
        // 앞에 두면 구독자 한 명의 예외가 이 슬롯 등록을 통째로 건너뛴다. 순서를 되돌리면 그 손실이 되살아난다.
        RecordObservedBuff(cooldownCode);
    }

    /// <summary>회복 프록 코드(예: 15790007)를 그 직업의 회생의 계약 버프 base(15790000)로. <see cref="BuffBaseCode"/>는
    /// 9자리 직업 버프 대역 전용이라 8자리인 이 코드에는 쓸 수 없다.</summary>
    private static int RevivalContractBase(int skillCode) => skillCode / 10000 * 10000;

    /// <summary>[<paramref name="start"/>, <paramref name="end"/>] 창에서 회생의 계약 긴급 회복이 몇 번
    /// 발동했는지 + 표시용 base 코드/이름. 가동률(%)이 무의미한 발동형이라 상세 창이 "N회"로 그린다.
    /// 통계 웹에는 보내지 않는다(미터 전용).</summary>
    public (int Count, int Code, string Name) RevivalHealSummary(int uid, long start, long end)
    {
        int count = 0, code = 0;
        lock (_revivalHealGate)
        {
            if (_revivalHeals.TryGetValue(uid, out List<(long At, int Code)>? list))
            {
                foreach ((long at, int c) in list)
                {
                    if (at >= start && at <= end)
                    {
                        count++;
                        code = c;
                    }
                }
            }
        }

        if (count == 0)
        {
            return (0, 0, string.Empty);
        }

        // (A) 5초 저항 스택도 "회생의 계약" 이름으로 가동률(%) 행을 차지하므로, 발동 횟수 행은 오버레이와
        // 같은 이름/코드를 써서 구분한다(아이콘은 base 폴백으로 동일하게 나온다).
        return (count, RevivalHealCooldownCode(RevivalContractBase(code)), RevivalHealCooldownName);
    }

    private void RecordObservedBuff(int runtimeCode)
    {
        int baseCode = BuffDisplayBase(runtimeCode);
        bool added;
        lock (_buffPickerGate)
        {
            added = _observedBuffBases.Add(baseCode);
        }

        if (!added)
        {
            return;
        }

        // BuffCatalogChanged is raised on the capture consumer thread, and at least one subscriber (the
        // settings window's buff picker) mutates a data-bound collection straight from it — WPF answers that
        // with NotSupportedException. Unisolated, that exception unwound the CALLER, which is how a buff code
        // seen for the first time on this install vanished from the overlay, the voice path and the packet log
        // for exactly one frame (M-13): the code was already in _observedBuffBases, so no later frame raised
        // the event again and nothing ever retried. A failed notification must cost the notification only.
        //
        // The observation itself is NOT rolled back: it happened, it is what the persisted buffUi.observed set
        // is for, and re-raising on every subsequent frame would just throw again inside a battle loop. The
        // picker re-reads the catalogue when it is opened, so a missed refresh self-heals.
        //
        // This is deliberately independent of marshalling the subscriber onto the dispatcher (the other half of
        // M-13, in the UI layer): the data layer must not lose data because a subscriber misbehaves, whoever
        // that subscriber turns out to be.
        try
        {
            BuffCatalogChanged?.Invoke();
        }
        catch (Exception)
        {
            // swallowed by design — see above
        }
    }

    private bool IsPartyMember(int uid)
    {
        User? u = _userRepository.Get(uid);
        if (u?.Nickname is not { Length: > 0 } nick)
        {
            return false;
        }

        IReadOnlyList<(string Nickname, int Server)> party = PartyMemberIdentities(30 * 60 * 1000L);
        foreach ((string Nickname, int Server) m in party)
        {
            if (m.Nickname == nick && m.Server == u.Server)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A class-skill buff code (11xxxxxxx 검성 .. 19xxxxxxx 권성), as opposed to an item/consumable
    /// buff in the lower code band (food/drink/scroll/potion) which the overlay excludes. Also the only safe
    /// gate for reading a job prefix off a code: 8-digit mob/consumable codes (12000101 = 중독) sit in the same
    /// leading digits as a class and would otherwise pass for that class's self-buff.</summary>
    public static bool IsJobBuffCode(int code) => code is >= 110_000_000 and <= 199_999_999;

    // ---- live owner-buff store (for the combat-assist overlay) ----
    // Keyed by BASE skill code (level-independent) so a re-cast of the same buff refreshes one entry.
    private readonly Dictionary<int, (long End, int Actor, long Duration, bool Indefinite, int Level, int Slot)> _ownerBuffs = new(); // baseCode -> (expiry, applier, duration, indefinite, abnormal level)

    // Self job-buffs seen for an entity uid BEFORE the executor was confirmed (owner==0 / stale), keyed by uid
    // then base code (last-write-wins). Replayed onto _ownerBuffs the instant SaveExecutorId confirms that uid is
    // the executor — so a late/lost own-load 0x3633 no longer blacks the overlay out for the first fight. Guarded
    // by _ownerBuffGate (same as _ownerBuffs). Bounded by a uid cap + pruned of fully-expired buffers.
    private readonly Dictionary<int, Dictionary<int, (long End, int Actor, long Duration, bool Indefinite, int Level, int Slot)>> _pendingSelfBuffs = new();
    private const int PendingSelfBuffUidCap = 24;

    // 폭주 (권성): the only maintained-stance buff broadcast with no expiry (duration 0xFFFFFFFF). The parser
    // gives every apply a short synthetic duration (StreamProcessor.IndefiniteStanceFallbackMs) and its whole
    // runtime band 191300000..191399999 collapses to this base. On the LIVE overlay we keep the slot alive far
    // longer than that synthetic duration so an ordinary held re-broadcast gap (combat lull / dropped frame /
    // momentary owner==0) doesn't false-expire it — the reported "폭주가 유지되는데 꺼졌다고 뜬다" bug.
    private const int IndefiniteStanceBaseCode = 19130000;
    private const long IndefiniteStanceOverlayKeepAliveMs = 20_000;

    // ---- 회생의 계약: (B) 긴급 회복 프록 ----
    // 이 스킬은 두 효과를 가지는데 서버가 버프로 방송하는 건 (A) 5초 상태이상-저항 스택뿐이다. 실전에서 의미
    // 있는 (B) "생명력 10% 이하 즉시 회복"은 버프로 존재하지 않고 actor == target 인 0x3804 프레임으로만 오며,
    // 1분 재발동 제한을 알려주는 서버 신호도 없다(60초짜리 마커 버프도, 0x3847 쿨다운 항목도 없음). 그래서
    // 락아웃은 발동 시각부터 우리가 센다. 상수 근거 = 코퍼스 186개 간격의 최솟값 60,101ms(60초 미만 0건).
    private const long RevivalHealCooldownMs = 60_000;
    private const int RevivalHealsPerUserCap = 512; // 1분 쿨이라 장시간 세션도 수백 건 이하
    private readonly object _revivalHealGate = new();
    private readonly Dictionary<int, List<(long At, int Code)>> _revivalHeals = new();

    /// <summary>회생의 계약 계열의 버프 base 코드(살성13·궁성14·마도성15·정령성16·권성19).</summary>
    private static bool IsRevivalContractBase(int baseCode) => baseCode is
        13790000 or 14790000 or 15790000 or 16790000 or 19790000;

    // 회복 쿨다운은 (A) 5초 상태이상-저항 스택과 별개의 슬롯으로 띄운다 — 같은 base 코드를 공유하면
    // _ownerBuffs가 last-write-wins라 (A)가 60초 카운트다운을 5초로 잘라먹기 때문이다(실측: 회복 발동의 7%가
    // ±200ms 내 (A)와 동시 발동). base + 7 을 합성 키로 쓰면 JoinIcons.Skill이 8자리 코드를
    // code/10000*10000 으로 접어 아이콘을 찾으므로 회생의 계약 아이콘이 그대로 재사용된다.
    private const string RevivalHealCooldownName = "회계·회복";
    private static int RevivalHealCooldownCode(int baseCode) => baseCode + 7;
    // Skill cooldowns from 0x3847 / 0x3802, keyed by the client's shared-cooldown group so a buff slot can be
    // grayed while its skill is on cooldown AND the cooldown overlay can draw one row per group.
    //   End              cooldown end (ms, capture clock)
    //   ProvisionalUntil 0 for an authoritative 0x3847 value; for a cast-sourced 0x3802 value, the instant the
    //                    guess stops being a guess (see CastCooldownGraceMs)
    //   TotalMs          the character's real full cooldown, learned from the cast frame — the ring denominator
    //   DisplayCode      the last wire code seen for the group, for the icon
    // Entries are never removed, so the key set doubles as "which skills this character has used this session" —
    // the overlay's learned display list. ClearOwnerBuffs wipes it on a real character switch, which is right:
    // the next character's skills are different.
    private readonly Dictionary<int, (long End, long ProvisionalUntil, long TotalMs, int DisplayCode)> _cooldowns = new();
    private readonly object _ownerBuffGate = new();

    // Cast frames that arrived before the executor was known. The self's own cooldowns are indistinguishable
    // from a party member's until then, and simply dropping them costs a lot: in one session 224 of 811 self
    // casts (27.6%, 17 distinct skills) landed in the 118 s before the own-nickname packet did. Staged per uid
    // and replayed the moment that uid is confirmed — same shape as _pendingSelfBuffs.
    private readonly Dictionary<int, Dictionary<int, (long End, long ProvisionalUntil, long TotalMs, int DisplayCode)>> _pendingSelfCooldowns = new();
    private const int PendingSelfCooldownUidCap = 16;

    private CooldownCatalog _cooldownCatalog = CooldownCatalog.Empty;

    /// <summary>Install the shipped cooldown catalog (names, shared-cooldown groups, order). Optional: without
    /// it group ids fall back to the plain fold and the cooldown overlay simply has nothing it can name.</summary>
    public void LoadCooldownCatalog(CooldownCatalog catalog) => _cooldownCatalog = catalog;

    /// <summary>The installed cooldown catalog. The picker needs the same instance the overlay keys against —
    /// building a second one from the file would drift the moment the asset is regenerated mid-session.</summary>
    public CooldownCatalog CooldownCatalog => _cooldownCatalog;

    // A cast (0x3802) only PROPOSES a cooldown. The server routinely resets or shortens it immediately and says
    // so with a 0x3847 that lands a median 251–348 ms later (p90 352 ms), so acting on the cast the instant it
    // arrives paints the icon gray for a quarter-second on EVERY cast: 649 casts of 도약 찍기 in one session
    // added up to 255 s of false gray, 529 casts of 단죄 to 197 s. A cast-sourced entry therefore stays
    // provisional for this window and grays nothing; a 0x3847 for the same skill — the authority — clears the
    // window and wins outright, whichever way it decides.
    private const long CastCooldownGraceMs = 400;

    /// <summary>Cooldown update from 0x3847 (self snapshot, <paramref name="actorId"/>=0) or 0x3802 (per-cast,
    /// real actor, <paramref name="fromCast"/>). Stored under the buff overlay's base scheme (skill 8-digit ->
    /// /10000*10000, buff 9-digit -> /100000*10000 — validated to line up with buff bases). remaining 0 = ready
    /// (end in the past). Only the self's cooldowns are kept: actorId 0 (snapshot) or == executor.
    /// Consumer-thread writer.</summary>
    public void SaveCooldown(int skillCode, long remainingMs, long arrivedAt, int actorId, bool fromCast = false)
    {
        int executor = _userRepository.Executor();
        int groupId = CooldownGroupId(skillCode);
        (long End, long ProvisionalUntil, long TotalMs, int DisplayCode) fresh =
            (arrivedAt + Math.Max(0, remainingMs), fromCast ? arrivedAt + CastCooldownGraceMs : 0, Math.Max(0, remainingMs), skillCode);

        if (actorId != 0 && actorId != executor)
        {
            if (executor == 0)
            {
                StageSelfCooldownCandidate(actorId, groupId, fresh);
            }

            return; // another player's cooldown (or an unknown self) — not for the self overlay yet
        }

        lock (_ownerBuffGate)
        {
            Apply(groupId, fresh);
        }
    }

    // Merge one cooldown report onto the store. The total is the one field a correction must NOT clobber: a
    // 0x3847 carries only what is LEFT, so taking it as the total would shrink the ring's denominator every
    // tick and make a 60 s cooldown look like a 3 s one. Only a larger value (or the first one seen) raises it.
    // Caller holds _ownerBuffGate.
    private void Apply(int groupId, (long End, long ProvisionalUntil, long TotalMs, int DisplayCode) fresh)
    {
        long total = fresh.TotalMs;
        if (_cooldowns.TryGetValue(groupId, out (long End, long ProvisionalUntil, long TotalMs, int DisplayCode) prev)
            && prev.TotalMs > total)
        {
            total = prev.TotalMs;
        }

        _cooldowns[groupId] = (fresh.End, fresh.ProvisionalUntil, total, fresh.DisplayCode);
    }

    /// <summary>The shared-cooldown group a wire skill code belongs to. With no catalog loaded this is the old
    /// fold, so the buff overlay's gray veil keys exactly as it did before.</summary>
    private int CooldownGroupId(int skillCode)
    {
        if (skillCode is < 11_000_000 or > 19_999_999)
        {
            return BuffBaseCode(skillCode);
        }

        int groupId = _cooldownCatalog.GroupId(skillCode);

        // 카탈로그가 돌려준 키가 그 자체로 "남의 그룹에 매달린 스킬"인 경우가 있다. 자기 자신을 가리키는
        // gctOverride 변종 5개(19080001·19200027·1910210/220/230)는 접힘을 거쳐 19080000·19100000·19200000
        // 같은 그룹 비대표 base로 착지한다. 그대로 두면 같은 공유 쿨이 어느 코드로 시전했느냐에 따라 두 키로
        // 갈라져, 권성 [폭주] 짝 칸 하나는 쿨이 돌고 다른 하나는 "준비됨"으로 남는다. 키는 항상 그룹 대표로
        // 정규화한다 — 되돌리면 그 쌍의 쿨 공유가 다시 시전 코드별로 쪼개진다(M-15의 반대 방향 증상).
        if (_cooldownCatalog.TryGet(groupId, out CooldownSkillInfo info)
            && info.GroupId != groupId
            && _cooldownCatalog.TryGet(info.GroupId, out _))
        {
            return info.GroupId;
        }

        return groupId;
    }

    /// <summary>True when <paramref name="groupId"/>'s skill should be drawn as on cooldown at
    /// <paramref name="nowMs"/> — i.e. the cooldown has not run out AND the value is no longer a cast's guess
    /// waiting on its 0x3847 correction. Caller holds <see cref="_ownerBuffGate"/>.</summary>
    private bool IsOnCooldown(int groupId, long nowMs) =>
        _cooldowns.TryGetValue(groupId, out (long End, long ProvisionalUntil, long TotalMs, int DisplayCode) cd)
        && cd.End > nowMs
        && nowMs >= cd.ProvisionalUntil;

    // Hold a cast from an unconfirmed uid. Bounded like _pendingSelfBuffs: prune buffers whose every entry has
    // expired, then refuse to grow — a dropped cast costs one icon until the skill is used again.
    private void StageSelfCooldownCandidate(int uid, int groupId, (long End, long ProvisionalUntil, long TotalMs, int DisplayCode) entry)
    {
        lock (_ownerBuffGate)
        {
            if (!_pendingSelfCooldowns.TryGetValue(uid, out Dictionary<int, (long End, long ProvisionalUntil, long TotalMs, int DisplayCode)>? buffer))
            {
                if (_pendingSelfCooldowns.Count >= PendingSelfCooldownUidCap)
                {
                    long now = Clock();
                    foreach (int stale in _pendingSelfCooldowns
                                 .Where(kv => kv.Value.Values.All(e => e.End <= now))
                                 .Select(kv => kv.Key).ToList())
                    {
                        _pendingSelfCooldowns.Remove(stale);
                    }

                    if (_pendingSelfCooldowns.Count >= PendingSelfCooldownUidCap)
                    {
                        return;
                    }
                }

                _pendingSelfCooldowns[uid] = buffer = new Dictionary<int, (long, long, long, int)>();
            }

            buffer[groupId] = entry;
        }
    }

    // Replay a newly-confirmed executor's staged casts. Called from SaveExecutorId after the pointer is set and
    // after any identity-change clear, so a character switch clears first and only then replays. Expired entries
    // are dropped rather than resurrected.
    private void ReplayStagedSelfCooldowns(int uid)
    {
        lock (_ownerBuffGate)
        {
            if (!_pendingSelfCooldowns.Remove(uid, out Dictionary<int, (long End, long ProvisionalUntil, long TotalMs, int DisplayCode)>? buffer))
            {
                return;
            }

            long now = Clock();
            foreach (KeyValuePair<int, (long End, long ProvisionalUntil, long TotalMs, int DisplayCode)> kv in buffer)
            {
                if (kv.Value.End > now)
                {
                    Apply(kv.Key, kv.Value);
                }
            }
        }
    }

    /// <summary>The local player's skill cooldowns for the overlay, one row per <b>skill</b> the picker can
    /// offer, ordered by job then by the catalog's stable order so icons never move under the cursor.
    /// <para>Skills that share a cooldown group therefore get a slot each, and those slots share ONE cooldown:
    /// casting either of them puts both on cooldown, because that is what the game does. Collapsing them into
    /// a single row instead would leave the other skill's picker chip toggling a slot that never appears.
    /// The pre-fill below used to key off <c>BaseCode</c> while the store keys off the group, so the
    /// non-representative half of each pair was re-laid every tick as "ready, no ring" and 권성 carried seven
    /// slots that stayed bright through their own cooldown (M-15).</para>
    /// <para>Once the character is recognised, the whole of that job's catalogue is laid out at once — a skill
    /// does not have to be cast before it gets a slot. The alternative (only what the session has seen) makes
    /// the picker look broken: you tick a skill and nothing appears until you press it.</para>
    /// <para>A skill nobody has cast yet is reported as <b>ready</b>, not as unknown. The client sends no login
    /// snapshot of the hotbar, so a meter started in the middle of a cooldown genuinely cannot know — but that
    /// window is small (you rarely open the overlay mid-rotation) and it closes for good the first time the
    /// skill is used. A permanent "unknown" badge on every untouched icon would cost more than it tells.</para>
    /// <para>Rows the wire has reported are always included even if they fall outside the recognised job — a
    /// job byte that arrived wrong should not blank the overlay for skills we have live data for.</para></summary>
    public IReadOnlyList<SkillCooldownView> ActiveCooldowns(long nowMs)
    {
        int band = User(_userRepository.Executor())?.Job?.SkillBand() ?? 0;
        var result = new List<SkillCooldownView>();

        // 그룹 키 -> 그 그룹의 현재 쿨 상태. 아래 프리필이 이 표를 봐야 같은 공유 쿨 그룹에 매달린 다른
        // 스킬 칸도 함께 쿨로 그려진다. 키 공간은 _cooldowns 와 같은 "그룹 대표 base 코드"다(CooldownGroupId).
        var groupState = new Dictionary<int, (long RemainingMs, long TotalMs, bool IsReady)>();

        lock (_ownerBuffGate)
        {
            foreach (KeyValuePair<int, (long End, long ProvisionalUntil, long TotalMs, int DisplayCode)> kv in _cooldowns)
            {
                if (!_cooldownCatalog.TryGet(kv.Key, out CooldownSkillInfo info))
                {
                    continue; // outside the catalog — no name and no icon, so there is nothing to draw
                }

                bool cooling = IsOnCooldown(kv.Key, nowMs);
                (long RemainingMs, long TotalMs, bool IsReady) state =
                    (cooling ? kv.Value.End - nowMs : 0, kv.Value.TotalMs, !cooling);
                groupState[kv.Key] = state;
                result.Add(new SkillCooldownView(
                    kv.Key,
                    kv.Value.DisplayCode,
                    info.Name,
                    state.RemainingMs,
                    state.TotalMs,
                    state.IsReady,
                    info.Job,
                    info.Order));
            }
        }

        if (band != 0)
        {
            foreach (CooldownSkillInfo info in _cooldownCatalog.Skills)
            {
                if (info.Job != band || groupState.ContainsKey(info.BaseCode))
                {
                    continue; // 다른 직업이거나, 그 칸은 위에서 이미 실측 상태로 나갔다
                }

                // 공유 쿨 그룹의 비대표 스킬: 스토어는 그룹 대표 키 하나에만 쓰이므로 이 칸은 영원히
                // reported 에 못 들어간다. 그룹 상태를 그대로 복사해 두 칸이 같은 쿨을 돈다 — 이걸 빼면
                // 권성 7칸이 쿨 도는 내내 "준비됨"으로 밝게 남아, 정보가 없는 게 아니라 거짓말을 한다(M-15).
                // 행 키(=픽커 키)는 반드시 이 스킬 자신의 BaseCode 여야 한다: 그룹 키로 바꾸면
                // CooldownVisibility(_all 이 BaseCode 기준)의 칩과 어긋나 "껐는데 안 사라지는" 칸이 생긴다.
                if (groupState.TryGetValue(info.GroupId, out (long RemainingMs, long TotalMs, bool IsReady) shared))
                {
                    result.Add(new SkillCooldownView(
                        info.BaseCode, info.BaseCode, info.Name,
                        shared.RemainingMs, shared.TotalMs, shared.IsReady, info.Job, info.Order));
                    continue;
                }

                // TotalMs 0 = 아직 이 캐릭터의 실제 쿨 길이를 모른다(첫 시전이 알려 준다). 링을 그릴 일이
                // 없으므로 분모가 없어도 무해하고, 클라 테이블 값으로 채우면 쿨감이 빠진 거짓 분모가 된다.
                result.Add(new SkillCooldownView(
                    info.BaseCode, info.BaseCode, info.Name, 0, 0, true, info.Job, info.Order));
            }
        }

        return result.OrderBy(r => r.Job).ThenBy(r => r.Order).ToList();
    }

    // Buff-tracking diagnostics (see SaveUseBuff). Written on the single consumer thread only.
    private long _diagJobBuffSeen;        // job-buff apply/refresh frames seen (any recipient)
    private long _diagSelfBuffAccepted;   // ... of those, target==executor -> counted onto the self overlay
    private long _diagOwnerZeroJobBuff;   // ... seen while executor is unknown (owner==0): self-recognition lost

    /// <summary>Snapshot of the buff-tracking diagnostic counters + current executor and live owner-buff store
    /// size. Read on the consumer thread. Discriminates the crowded-raid overlay failure: healthy
    /// <c>SelfAccepted</c> means self buff frames arrive and pass the gate (fault is downstream / refresh loss);
    /// a spike in <c>OwnerZero</c> or <c>SelfAccepted</c> stalling to 0 while <c>JobBuffSeen</c> keeps rising
    /// means the executor gate is blacking out self buffs.</summary>
    public (long JobBuffSeen, long SelfAccepted, long OwnerZero, int Owner, int StoreCount, int CdStore, int CdActive, int BuffsOnCd) BuffDiagSnapshot(long nowMs)
    {
        int storeCount, cdStore, cdActive = 0, buffsOnCd = 0;
        lock (_ownerBuffGate)
        {
            storeCount = _ownerBuffs.Count;
            cdStore = _cooldowns.Count;
            foreach ((long End, long ProvisionalUntil, long TotalMs, int DisplayCode) cd in _cooldowns.Values)
            {
                if (cd.End > nowMs)
                {
                    cdActive++;
                }
            }

            // active owner buffs whose skill is on cooldown right now — these are the ones that SHOULD gray.
            // Same rule the overlay uses, so a gap between this counter and the screen is never the rule itself.
            foreach (KeyValuePair<int, (long End, int Actor, long Duration, bool Indefinite, int Level, int Slot)> kv in _ownerBuffs)
            {
                if (kv.Value.End > nowMs && IsOnCooldown(CooldownGroupId(kv.Key), nowMs))
                {
                    buffsOnCd++;
                }
            }
        }

        return (_diagJobBuffSeen, _diagSelfBuffAccepted, _diagOwnerZeroJobBuff, _userRepository.Executor(), storeCount, cdStore, cdActive, buffsOnCd);
    }

    /// <summary>Raised when a buff on the local player is applied/refreshed.</summary>
    public event Action? LiveBuffsChanged;

    /// <summary>The buffs currently active on the local player at <paramref name="nowMs"/>, longest remaining
    /// first. <c>Code</c> is the base skill code; <c>DurationMs</c> is the full duration (for the countdown
    /// ring); <c>ByOther</c> = applied by someone else; <c>Overlay</c> = draw it (false for a 음성만 buff, which
    /// is returned only so the announce path can speak it). Fully-Off buffs (hidden + not voice) are excluded.</summary>
    public IReadOnlyList<OwnerBuffView> ActiveOwnerBuffs(long nowMs)
    {
        int owner = _userRepository.Executor();
        var result = new List<OwnerBuffView>();
        lock (_ownerBuffGate)
        {
            foreach (KeyValuePair<int, (long End, int Actor, long Duration, bool Indefinite, int Level, int Slot)> kv in _ownerBuffs)
            {
                if (kv.Value.End <= nowMs)
                {
                    continue; // expired
                }

                if (!IsBuffInCatalog(kv.Key))
                {
                    continue; // outside the curated list — no picker row exists, so it could not be turned off
                }

                bool hidden = IsBuffHidden(kv.Key);
                if (hidden && !IsBuffVoice(kv.Key))
                {
                    continue; // Off — unchecked in the picker; hide immediately, don't wait for expiry
                }

                string name = _buffNames.TryGetValue(BuffBaseCode(kv.Key), out (string Name, string Job) bn)
                    ? bn.Name
                    : Buff(kv.Key)?.Name ?? Skill(kv.Key)?.Name ?? $"버프 {kv.Key}";
                bool onCooldown = IsOnCooldown(CooldownGroupId(kv.Key), nowMs);
                result.Add(new OwnerBuffView(
                    kv.Key, name, kv.Value.End - nowMs, kv.Value.Duration, kv.Value.End,
                    owner != 0 && kv.Value.Actor != owner,
                    !hidden,  // Overlay: 음성만 (hidden + voice) is announced but not drawn
                    onCooldown,
                    kv.Value.Indefinite,
                    kv.Value.Level));
            }

            SuppressExclusiveLosers(result, nowMs);
        }

        return result.OrderByDescending(r => r.RemainingMs).ToList();
    }

    /// <summary>인게임에서 서로 중복 적용되지 않는 버프 쌍이 둘 다 살아 있으면 지는 쪽을 목록에서 뺀다.
    /// 승자 판정: 고정 승자가 있으면 그것, 없으면 어노멀 레벨이 높은 쪽, 레벨이 같거나 둘 다 모르면(0)
    /// 지정된 동률 승자, 그것도 없으면 나중에 적용된 쪽(End가 늦은 쪽)을 남긴다.
    /// <para>_ownerBuffGate를 이미 잡은 상태에서 호출된다.</para></summary>
    private void SuppressExclusiveLosers(List<OwnerBuffView> rows, long nowMs)
    {
        foreach (ExclusiveBuffPair pair in ExclusiveBuffPairs)
        {
            int ai = rows.FindIndex(r => r.Code == pair.A);
            int bi = rows.FindIndex(r => r.Code == pair.B);
            if (ai < 0 || bi < 0)
            {
                continue; // 한쪽만 켜져 있으면 아무것도 감추지 않는다
            }

            OwnerBuffView a = rows[ai], b = rows[bi];
            int loser;
            if (pair.FixedWinner != 0)
            {
                loser = pair.FixedWinner == pair.A ? pair.B : pair.A;
            }
            else
            {
                int la = _ownerBuffs.TryGetValue(pair.A, out var va) ? va.Level : 0;
                int lb = _ownerBuffs.TryGetValue(pair.B, out var vb) ? vb.Level : 0;
                if (la != lb && la > 0 && lb > 0)
                {
                    loser = la > lb ? pair.B : pair.A;
                }
                else if (pair.TieWinner != 0)
                {
                    loser = pair.TieWinner == pair.A ? pair.B : pair.A;
                }
                else
                {
                    loser = a.EndMs >= b.EndMs ? pair.B : pair.A; // 레벨을 모르면 나중에 걸린 쪽을 남긴다
                }
            }

            rows.RemoveAll(r => r.Code == loser);
        }
    }

    private void ClearOwnerBuffs()
    {
        lock (_ownerBuffGate)
        {
            _ownerBuffs.Clear();
            _cooldowns.Clear();
        }
    }

    // Stage a self-buff candidate for a not-yet-confirmed executor uid (see SaveUseBuff). Last-write-wins per base
    // code; bounded by a uid cap (prune fully-expired buffers first, then refuse to grow — the self simply re-casts).
    private void StageSelfBuffCandidate(int uid, int skillCode, long buffStart, long buffEnd, long duration, int actorId, int level, int slot)
    {
        (int baseCode, var entry) = ComputeOwnerBuffEntry(skillCode, buffStart, buffEnd, duration, actorId, level, slot);
        lock (_ownerBuffGate)
        {
            if (!_pendingSelfBuffs.TryGetValue(uid, out var buffer))
            {
                if (_pendingSelfBuffs.Count >= PendingSelfBuffUidCap)
                {
                    long now = Clock();
                    foreach (int stale in _pendingSelfBuffs
                                 .Where(kv => kv.Value.Values.All(e => e.End <= now))
                                 .Select(kv => kv.Key).ToList())
                    {
                        _pendingSelfBuffs.Remove(stale);
                    }

                    if (_pendingSelfBuffs.Count >= PendingSelfBuffUidCap)
                    {
                        return;
                    }
                }

                _pendingSelfBuffs[uid] = buffer = new Dictionary<int, (long, int, long, bool, int, int)>();
            }

            buffer[baseCode] = entry;
        }
    }

    // Replay a newly-confirmed executor's staged self-buffs (still live at the injected clock) onto the overlay
    // store. Called from SaveExecutorId AFTER the executor pointer is set and after any identity-change clear, so
    // a character switch clears the previous character first and only THEN replays the new one's buffs.
    private void ReplayStagedSelfBuffs(int uid)
    {
        bool changed = false;
        lock (_ownerBuffGate)
        {
            if (_pendingSelfBuffs.Remove(uid, out var buffer))
            {
                long now = Clock();
                foreach (var kv in buffer)
                {
                    if (kv.Value.End > now)
                    {
                        _ownerBuffs[kv.Key] = kv.Value;
                        changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            LiveBuffsChanged?.Invoke();
        }
    }

    public void SaveMobHp(int instanceId, long hp) => MobHp(instanceId, hp);

    public List<UseBuff> BattleBuff(int uid, long start, long end) => _useBuffRepository.FindOverlapping(uid, start, end);

    /// <summary>[start, end] 창에 든 이 플레이어의 시전들, 시각 오름차순. 이름까지 붙여 돌려준다.
    /// <para><b>소환수 시전을 주인에게 접는다</b> — 피해 경로가 <c>ResolveActor</c>로 하는 것과 같은 규칙이다.
    /// 접지 않으면 정령성 상세에서 소환수 스킬이 통째로 사라진다(그 엔티티 id 는 참가자 목록에 없다).</para></summary>
    public List<SkillCastRow> BattleSkillCasts(int uid, long start, long end)
    {
        if (uid <= 0 || end < start)
        {
            return [];
        }

        List<SkillCast> casts = _skillCastRepository.FindInWindow(uid, start, end);
        // 살아 있는 딕셔너리를 그대로 열거하지 않는다 — 이 메서드는 행 클릭 시 UI 스레드에서도 불린다.
        foreach (KeyValuePair<int, int> summon in _summonRepository.GetAll().ToList())
        {
            // ResolveSummonOwner 와 같은 가드: 그 엔티티 id 를 물려받은 실제 플레이어의 시전을
            // 옛 주인의 타임라인으로 끌어오지 않는다.
            if (summon.Value == uid && summon.Key != uid && ResolveSummonOwner(summon.Key) == uid)
            {
                casts.AddRange(_skillCastRepository.FindInWindow(summon.Key, start, end));
            }
        }

        int band = User(uid)?.Job is { } job ? JobClassInfo.BasicSkillCode(job) / 1_000_000 : 0;
        var rows = new List<SkillCastRow>(casts.Count);
        var seen = new HashSet<(string Name, long At)>();
        foreach (SkillCast c in casts.OrderBy(c => c.TimestampMs).ThenBy(c => c.SkillCode))
        {
            if (!IsOwnCast(c.SkillCode, band))
            {
                continue;
            }

            string name = SkillCastName(c.SkillCode);

            // 한 번의 시전이 같은 ms 에 프레임을 여러 개 낸다 — 실측(5인 파티 코퍼스 44,178건)에서 완전 중복
            // 1,067건이 나왔고 전량이 딱 두 스킬이었다: 살기 파열(같은 코드가 최대 5번)과 불꽃 작살(최대 4번).
            // 둘 다 한 스킬이 여러 코드/여러 프레임으로 나가는 것으로 이미 알려진 스킬이다. 세면 횟수가 부풀고
            // 간격에 0ms 가 섞여 통계가 망가지므로, (이름, 시각)이 같으면 한 번으로 센다.
            // ⚠️ 같은 ms 에 쿨을 돌린 프레임과 아닌 프레임이 함께 오면 <b>돌린 쪽</b>을 남긴다 — 그게 그 시각에
            // 대해 아는 더 강한 사실이고, 버리면 표식이 조용히 사라진다.
            if (!seen.Add((name, c.TimestampMs)))
            {
                if (c.StartsCooldown)
                {
                    int at = rows.FindLastIndex(r => r.Name == name && r.TimestampMs == c.TimestampMs);
                    if (at >= 0 && !rows[at].StartsCooldown)
                    {
                        rows[at] = rows[at] with { Code = c.SkillCode, StartsCooldown = true };
                    }
                }

                continue;
            }

            rows.Add(new SkillCastRow(c.SkillCode, name, c.TimestampMs, c.StartsCooldown));
        }

        return rows;
    }

    /// <summary>이 프레임을 "이 사람이 <b>누른</b> 스킬"로 셀 수 있나.
    /// <para>0x3802 는 시전만 싣지 않는다. 실측(5인 파티, 직업 밴드 44,178건): 검성의 <b>흡혈의 검 착취</b>
    /// (11340028)가 <b>수혜자를 actor 로</b> 파티원 전원에게 브로드캐스트된다 — 궁성 751회, 마도성 508회,
    /// 치유성 587회. 검성 본인은 밴드 밖 프레임이 0건이었다. 즉 남의 목록에 있는 흡혈의 검은 "그 사람이 쓴 것"이
    /// 아니라 그 사람에게 <i>터진 것</i>이다. <see cref="PartySynergyCatalog"/>가 같은 현상을 피해 채널에서
    /// 이미 문서화하고 있다("arrive as REAL DAMAGE PACKETS on each party member, carrying the granting
    /// class's skill code").</para>
    /// <para>그래서 두 가지를 건다. ① <b>직업 밴드</b> — 남의 직업 스킬이 내 목록에 있을 이유가 없다(DPS 그래프
    /// 레인이 쓰는 규칙과 같다). ② <b>측정형 grant 코드</b> — 이건 시전자 본인 밴드라 ①로 안 걸리는데, 시전자
    /// 목록에도 프록이 그대로 쌓인다. 대가로 검성은 흡혈의 검을 "누른" 기록을 잃는다(20초마다 한 번). 그쪽이
    /// 수백 줄의 유령 행보다 정직하다 — 그 버프의 가동률은 버프 업타임 탭이 이미 정확히 보여 준다.</para>
    /// <para>직업을 아직 모르면(<paramref name="jobBand"/> 0) ①을 걸지 않는다 — 그래프 레인과 같은 폴백이다.</para></summary>
    private bool IsOwnCast(int skillCode, int jobBand)
    {
        int baseCode = skillCode is >= 11_000_000 and <= 19_999_999 ? skillCode / 10_000 * 10_000 : skillCode;
        if (PartySynergyCatalog.IsMeasuredGrant(baseCode))
        {
            return false;
        }

        // 패시브·자동 발동은 "사용한 스킬"이 아니다 — 액티브(스티그마 포함)만 남긴다.
        if (IsPassiveSkill(skillCode))
        {
            return false;
        }

        return jobBand <= 0 || skillCode / 1_000_000 == jobBand;
    }

    /// <summary>시전 코드의 표시 이름. 0x3802는 특화 접미가 붙은 <b>원본</b> 코드를 싣는다(실측: 직업 밴드
    /// 시전의 52%만 skills.json 과 정확히 일치, 나머지는 base 접기로 잡힌다). 그래서 원본을 먼저 보고,
    /// 없을 때만 base 로 접는다 — 무조건 접으면 8종이 다른 스킬 이름으로 표시된다
    /// (11010047 격파의 맹타 → 절단의 맹타, 11000100 긴급 회피 → 검성 무기 장착 …).</summary>
    private string SkillCastName(int code)
    {
        if (Skill(code)?.Name is { Length: > 0 } exact)
        {
            return exact;
        }

        int baseCode = code is >= 11_000_000 and <= 19_999_999 ? code / 10_000 * 10_000 : code;
        if (baseCode != code && Skill(baseCode)?.Name is { Length: > 0 } folded)
        {
            return folded;
        }

        return code.ToString();
    }

    // ---- packet store ----

    public List<ParsedDamagePacket>? BattleData(int targetId) => targetId <= 0 ? null : _packetRepository.Get(targetId);

    public PacketWindow BattleDataSince(int targetId, long sequence) =>
        targetId <= 0 ? new PacketWindow([], sequence, false, 0) : _packetRepository.GetWindow(targetId, sequence);

    public void FlushPacket()
    {
        _packetRepository.Flush();
        _packetRepository.CurrentTarget(-1);
        _packetRepository.FlushBattleTime();
        _activeBattleMobCode = null;
        _lastDummyHitTime = 0;
        // 고정 종료는 그 창에만 속한다. 남겨 두면 다음 <b>보스</b> 전투의 종료 스탬프로 새어 나간다.
        ClearDummyWindow();
        _dummyWindowDurationMs = 0;
        _lastAcceptedDummyHitPacketMs = 0;
    }

    public void SaveDamage(ParsedDamagePacket pdp, long epoch)
    {
        if (_resetEpoch != epoch) return;
        // 이름 앵커는 "그 uid가 지금 살아서 이 전투에 있다"는 증거를 본 뒤에 승격한다 — 여기가 그 증거가
        // 지나가는 자리다. 때리는 쪽과 맞는 쪽 둘 다 증거다: 실측에서 0x9200은 그 uid의 마지막 타격보다 늦게
        // 오는 경우가 흔한데(4건 중 3건), 그중 하나는 피격 프레임으로만 살아 있음이 드러났다. 나머지 둘은
        // 바인드 이후 그 uid가 다시는 등장하지 않는 진짜 사장된 uid라 승격되지 않는 게 맞다.
        PromotePendingAnchorIfActive(pdp.ActorId);
        PromotePendingAnchorIfActive(pdp.TargetId);
        if (pdp.TargetId > 0)
        {
            long hitAt = Clock();
            _lastAnyDamageMs = hitAt; // 기믹 중 쫄 딜 — "전투가 아직 살아 있다"의 증거
            if (pdp.TargetId == CurrentTarget())
            {
                _lastBossActivityMs = hitAt;
            }
        }

        // Training-dummy test mode: a hit on a dummy drives (and is gated by) the dummy battle machine. Drop it —
        // never record — when test mode is off or the duration cut has fired, so an idle/finished dummy shows no
        // combat and post-cut damage can't inflate the frozen result. Non-dummy targets take the plain path.
        if (pdp.TargetId > 0 && IsMobDummy(pdp.TargetId) && !AcceptDummyHit(pdp))
        {
            return;
        }

        // 판정 스탯 스탬프. 본인의 직격에만, 저장소에 넣기 <b>직전에</b> 찍는다.
        // 🔑 여기여야 하는 이유: _packetRepository 는 이 객체의 참조를 그대로 보관하고, DpsCalculator 는 캐시
        // 리셋 때마다 창 전체를 시퀀스 0부터 다시 누적한다. 누적 시점에 시트를 읽으면 "마지막 재생이 일어난
        // 시각의 시트"가 그 전투의 모든 타격에 소급 적용된다 — 최악의 경우 전투 종료 시점(버프가 다 빠진) 값이
        // 오프너에까지 붙는다. 도착 시각에 찍어 두면 재생을 몇 번 하든 값이 변하지 않는다.
        // 본인 것만 찍는 이유: 스탯 사전(0x364A/0x3649)은 로컬 플레이어에게만 방송된다 — 파티원의 강타·명중
        // 수치는 어떤 경로로도 얻을 수 없다(코퍼스 6종 4,806프레임에서 파티원 0건).
        if (!pdp.Dot && pdp.ActorId != 0 && pdp.ActorId == ExecutorId())
        {
            pdp.JudgmentStats = _playerStats.JudgmentStats();
        }

        _packetRepository.Save(pdp);
        MaybeFollowSelfTarget(pdp); // Feature 2 (염화의 수호검): 본인이 때리는 수호검으로 표시 전환
    }

    // ---- 그로기(무력화) 게이지 ----

    /// <summary>알림 임계. 원정/초월/성역 보스 실측(게이지형 하강 사이클 281건)에서 잔여 20%면 말이 끝난 뒤
    /// 평균 4.7초가 남고 성공률 86.1%다. 15%도 성공률은 같지만(85.8%) 남는 시간이 3.4초로 줄고 문구가
    /// 7음절을 넘으면 78.6%로 무너진다. 12%부터는 절벽이다(76.5% → 10%에서 59.8% → 5%에서 6%).</summary>
    private const double GroggyAlertRatio = 0.20;

    /// <summary>래치 해제선. 리필(만충 복귀)이면 다음 사이클이므로 다시 울릴 수 있어야 한다.</summary>
    private const double GroggyRearmRatio = 0.95;

    private int _groggyEntity;
    private long _groggyMax;
    private double _groggyRatio = 1.0;
    private bool _groggyAlerted;

    /// <summary>현재 타깃의 그로기 게이지가 알림 임계 아래로 내려왔다. 사이클당 한 번만 발화한다.</summary>
    public event Action? GroggyImminent;

    /// <summary>현재 타깃의 그로기 잔여 비율(1.0 = 만충, 모르면 1.0).</summary>
    public double GroggyRatio() => _groggyRatio;

    /// <summary>0xE005 갱신. <b>현재 타깃 것만</b> 본다 — 한 세션에서 그로기를 방송하는 엔티티가 수십 개이고
    /// 거기엔 잡몹·수정체 같은 비보스가 섞인다.
    /// <para>🔴 <paramref name="max"/>를 캐시하지 않는다. 같은 보스도 세션마다 티어가 다르고(실측 2250/3000/
    /// 6000/7500) 시련 '보스 강화' 어픽스로 전투 도중에 바뀌기도 한다. 바뀐 프레임은 새 max 와 옛 cur 이
    /// 같이 실려 비율이 1을 넘을 수 있으므로, 그 틱은 래치 판정을 건너뛰고 값만 받아 둔다 — 안 그러면
    /// 거짓 리필로 읽혀 래치가 풀리고 같은 사이클에 두 번 울린다.</para></summary>
    public void SaveGroggyGauge(int entityId, long max, long cur)
    {
        if (entityId <= 0 || max <= 0)
        {
            return;
        }

        int target = CurrentTarget();
        if (target <= 0 || entityId != target)
        {
            return;
        }

        // 타깃이 바뀌었으면(페이즈 전환·재풀) 상태를 통째로 새로 시작한다.
        bool retargeted = entityId != _groggyEntity;
        bool rescaled = !retargeted && max != _groggyMax;
        _groggyEntity = entityId;
        _groggyMax = max;

        double ratio = Math.Clamp((double)Math.Min(cur, max) / max, 0.0, 1.0);
        _groggyRatio = ratio;

        if (retargeted)
        {
            // 새 엔티티 = 새 사이클. 단 판정은 건너뛰지 않는다 — 처음 본 프레임이 이미 임계 아래면
            // (전투 중간에 미터를 켰거나 페이즈가 넘어간 직후) 그때가 바로 알려야 할 때다.
            _groggyAlerted = false;
        }
        else if (rescaled)
        {
            // 🔴 래치를 건드리지 않고 값만 받는다. 이 프레임은 새 max 와 옛 cur 이 섞여 비율을 못 믿는다 —
            // 여기서 래치를 풀면 같은 하강에서 두 번 울린다(거짓 리필).
            return;
        }

        if (ratio >= GroggyRearmRatio)
        {
            _groggyAlerted = false;
            return;
        }

        if (!_groggyAlerted && ratio <= GroggyAlertRatio)
        {
            _groggyAlerted = true;
            GroggyImminent?.Invoke();
        }
    }

    /// <summary>전투가 끝나면 게이지 상태를 버린다 — 다음 전투가 남의 래치를 물려받으면 안 된다.</summary>
    private void ClearGroggyState()
    {
        _groggyEntity = 0;
        _groggyMax = 0;
        _groggyRatio = 1.0;
        _groggyAlerted = false;
    }

    // ---- battle state machine ----

    public int CurrentTarget() => _packetRepository.CurrentTarget();
    private void SaveCurrentTarget(int targetId) => _packetRepository.CurrentTarget(targetId);
    public long CurrentBattleStart() => _packetRepository.CurrentBattleStart();
    public long CurrentBattleEnd() => _packetRepository.CurrentBattleEnd();
    private void SaveCurrentBattleEnd(long time) => _packetRepository.SaveCurrentBattleEnd(time);

    public bool IsMobDummy(int mobId)
    {
        if (mobId <= 0) return false;
        int? mobCode = GetMobId(mobId);
        return mobCode != null && Mob(mobCode.Value)?.IsDummy == true;
    }

    public bool IsCurrentTargetDummy() => IsMobDummy(CurrentTarget());

    /// <summary>Decide whether a damage packet against a training dummy should be RECORDED (and drive the live
    /// dummy battle). Called from <see cref="SaveDamage"/> on the consumer thread. Returns false — so the packet
    /// is dropped and never counted — when the dummy test mode is off, or the chosen duration has elapsed (the
    /// hard cut). The first accepted hit opens the battle window; a hit at/after the duration ends the run and
    /// latches <see cref="_dummyCutoff"/> so every later hit is ignored until a reset clears it.</summary>
    private bool AcceptDummyHit(ParsedDamagePacket pdp)
    {
        long clockNow = Clock();
        // 채택하든 드롭하든 "방금 허수아비를 때렸다"는 사실은 남긴다 — 컷 뒤 재무장이 이 값으로 정숙 구간을 잰다.
        // 그래서 이 줄은 아래 조기 반환들보다 반드시 위에 있어야 한다.
        _lastDummyHitObservedMs = clockNow;

        if (!_dummyTestMode || _dummyCutoff) return false;

        // 🔴 진행 중인 전투가 허수아비의 것이 아니면 손대지 않는다. 이 가드가 없으면, 보스와 싸우는 중에
        // 허수아비로 분류된 프레임 하나가 (인스턴스 id 재사용이든, 옆에 선 허수아비든) 아래 컷 분기로 들어가
        // <b>보스 전투의</b> CurrentBattleStart 를 기준으로 컷을 계산한다 — 그 보스 전투를 강제 종료시키고
        // 종료 시각을 "보스 시작 + 측정 시간"으로 찍어, 실제로는 5분짜리였던 전투가 30초로 굳어 DPS 가
        // 10배로 부푼 채 기록되고 업로드 후보가 된다.
        if (CurrentTarget() > 0 && !IsCurrentTargetDummy())
        {
            return false;
        }

        if (CurrentTarget() <= 0)
        {
            _battleRevision++; // a fresh battle id, so DpsCalculator resets its per-battle cache/sequence
            // 창의 시작을 <b>패킷 시각</b>으로 찍는다. StartAnchor() 가 리포트 시작을
            // max(첫 패킷 ts, CurrentBattleStart() - 250ms) 로 잡기 때문에, 여기서 처리 시각을 쓰면
            // 파이프 지연이 250ms 를 넘는 순간 리포트 시작이 첫 타격보다 뒤로 밀린다.
            _packetRepository.SaveCurrentBattleStart(pdp.Timestamp);
            SaveCurrentTarget(pdp.TargetId);
            _dummyWindowStartPacketMs = pdp.Timestamp;
            _dummyWindowStartClockMs = clockNow;
            _dummyWindowDurationMs = DummyDurationMs; // 이 런의 길이를 여기서 잠근다
            _dummyFixedEnd = 0;
            _dummyBattleOpened = true;
            _lastDummyHitTime = clockNow;
            _lastAcceptedDummyHitPacketMs = pdp.Timestamp;
            return true;
        }

        // 창의 기준은 공용 CurrentBattleStart 가 아니라 <b>이 런의 패킷 시계 앵커</b>다 — 양변이 같은 시계이고,
        // 다른 전투의 시작 시각이 여기로 새어 들어올 수 없다.
        if (_dummyWindowStartPacketMs > 0 && pdp.Timestamp - _dummyWindowStartPacketMs >= _dummyWindowDurationMs)
        {
            CutDummyRun(_dummyWindowStartPacketMs + _dummyWindowDurationMs);
            return false; // this hit is past the cut — drop it too
        }

        _lastDummyHitTime = clockNow;
        _lastAcceptedDummyHitPacketMs = pdp.Timestamp;
        return true;
    }

    /// <summary>컷 한 번 = 종료 스탬프 고정 + 타깃 해제 + 래치. 두 호출자(타격 경로·틱 경로)가 정확히 같은
    /// 일을 하도록 한 자리에 모아 둔다 — 예전에는 두 곳에 복사돼 있어 한쪽만 고치기 쉬웠다.</summary>
    private void CutDummyRun(long fixedEndPacketMs)
    {
        // 스탬프는 실제로 집계된 마지막 타격보다 앞설 수 없다. 앞서면 분자(누적 피해)는 그대로인데 분모만
        // 짧아져 DPS 가 부푼다 — 창 길이를 잠근 지금은 도달 불가지만, 바닥을 남겨 두면 다음에 이 계산식을
        // 건드리는 사람이 같은 사고를 반복하지 못한다.
        _dummyFixedEnd = Math.Max(fixedEndPacketMs, _lastAcceptedDummyHitPacketMs);
        fixedEndPacketMs = _dummyFixedEnd;
        SaveCurrentBattleEnd(fixedEndPacketMs);
        SaveCurrentTarget(-1);
        _dummyCutoff = true;
    }

    /// <summary>Per-report-tick maintenance of a live dummy battle (called at the top of
    /// <see cref="DpsCalculator.GetDps"/>). Enforces the duration hard cut even when hits pause, ends the run
    /// promptly if test mode is switched off mid-run, and keeps the original 5s idle auto-end.</summary>
    public void TickDummyBattle()
    {
        long now = Clock();

        // 컷으로 끝난 런은 손을 멈추고 <see cref="DummyTimeoutMs"/> 만큼 조용해지면 스스로 재무장한다 —
        // 그 다음 타격이 <b>별개의</b> 허수아비 전투를 연다. 예전에는 이 래치를 사람이 초기화 버튼으로만
        // 풀 수 있어서, 연속 측정이 "때려도 미터가 안 뜬다"로 보였다.
        // 정숙 구간이 필요한 이유: 만료 순간에도 대개 계속 때리고 있으므로, 0으로 두면 다음 패킷이 몇 ms 뒤에
        // 새 전투를 열어 방금 나온 결과를 읽을 틈이 없다. 5초 유휴 자동 종료와 같은 상수를 공유한다.
        // ⚠️ CurrentTarget() <= 0 조건은 "종료 전이가 이미 관측됐다"를 보장한다 — 그 전에 래치를 풀면
        // 같은 틱 안에서 새 창이 열려 방금 런이 화면에서 사라진다.
        if (_dummyCutoff && CurrentTarget() <= 0 && now - _lastDummyHitObservedMs > DummyTimeoutMs)
        {
            _dummyCutoff = false;
        }

        int current = CurrentTarget();
        if (current <= 0 || !IsCurrentTargetDummy()) return;

        if (!_dummyTestMode)
        {
            SaveCurrentBattleEnd(now); // mode turned off mid-run — end now (no cutoff latch; re-enabling starts fresh)
            SaveCurrentTarget(-1);
            _lastDummyHitTime = 0;
            _dummyFixedEnd = 0;
            return;
        }

        // 타격이 멎어도 컷은 제 시간에 터져야 한다. 판정은 <b>소비자 시계끼리</b>(창 시작 clock 대비 now),
        // 스탬프는 <b>패킷 시계</b>로 — 두 시계를 뺄셈 하나 안에서 섞지 않는다.
        if (_dummyWindowStartClockMs > 0 && now - _dummyWindowStartClockMs >= _dummyWindowDurationMs)
        {
            CutDummyRun(_dummyWindowStartPacketMs + _dummyWindowDurationMs);
            return;
        }

        if (now - _lastDummyHitTime > DummyTimeoutMs)
        {
            SaveCurrentBattleEnd(_lastDummyHitTime);
            SaveCurrentTarget(-1);
            _lastDummyHitTime = 0;
            _dummyFixedEnd = 0;
        }
    }

    /// <summary>Clear the duration hard-cut latch so the next dummy hit opens a fresh window (used by the dummy
    /// DPS reset and the full/soft resets). The mode and chosen duration are intentionally NOT touched here.</summary>
    public void ResetDummyCutoff()
    {
        _dummyCutoff = false;
        ClearDummyWindow();
    }

    /// <summary>허수아비 측정 창에 딸린 상태를 전부 내려놓는다. 한 자리에 모아 둔 이유는 필드가 다섯 개라
    /// 어느 한 곳에서 하나를 빠뜨리면 그 값이 다음 전투로 새기 때문이다(특히 고정 종료).
    /// ⚠️ <c>_lastDummyHitObservedMs</c> 는 여기 넣지 마라 — 그건 창이 아니라 <b>재무장 타이머</b>의 기준점이고,
    /// 0으로 만들면 <c>Clock() - 0</c> 이 거대해져 다음 틱에 즉시 재무장된다.</summary>
    private void ClearDummyWindow()
    {
        _dummyFixedEnd = 0;
        _dummyWindowStartPacketMs = 0;
        _dummyWindowStartClockMs = 0;
        _dummyWindowDurationMs = 0;
        _lastAcceptedDummyHitPacketMs = 0;
    }

    public void StartBattle(int mobId) => StartBattleAt(mobId, Clock());

    /// <summary>전투 시작. <paramref name="startAt"/>는 <b>스탬프할</b> 시작 시각으로, 통상 경로에서는 현재
    /// 시각이지만 소급 승격(<see cref="PromoteUnresolvedStart"/>)에서는 원래 토글 시각이다. 억제 가드들은
    /// 스탬프 시각이 아니라 항상 현재 시각으로 판단한다.</summary>
    private void StartBattleAt(int mobId, long startAt)
    {
        int? mobCode = GetMobId(mobId);
        long now = Clock();
        _bossEngageAtMs[mobId] = now; // Feature 2: 나중 자기딜 전환이 창을 back-date하도록 교전 시각 기록(primary-lock에 막혀도)
        EndedBattle? endedBattle = _recentlyEndedBattles.TryGetValue(mobId, out EndedBattle eb) ? eb : null;
        if (CurrentTarget() <= 0
            && endedBattle != null
            && endedBattle.Value.MobCode == mobCode
            && (MobHp(mobId) ?? 0) == 0 // long? — a despawned corpse loses HP tracking (null); null must count as
                                        // "corpse", else the guard leaks and a ghost restart re-stamps
                                        // CurrentBattleStart at the kill (→ split + 191M-DPS upload)
            && now - endedBattle.Value.EndedAt <= EndedBattleStartIgnoreMs)
        {
            // Likely a residual post-kill toggle on the corpse — don't restart now. But remember the intent: if
            // the boss next reports HP>0 (a real re-pull/respawn), MobHp replays this start so we never freeze.
            _pendingStart = (mobId, mobCode, now);
            return;
        }

        if (CurrentTarget() == mobId
            && CurrentBattleStart() > 0L
            && CurrentBattleEnd() == 0L
            && _activeBattleMobCode == mobCode)
        {
            return;
        }

        // 살아있는 보스 전투 보호(primary-lock, 2026-07-23): 이미 다른 타깃으로 전투가 열려 있고(시작됐고 아직
        // 안 끝남) 그 타깃이 아직 살아 있으면(remain HP>0), 다른 엔티티의 start-토글로 현 전투를 덮어쓰지 않는다.
        // 바크론패턴강화의 boss=true 가시덩굴/가시속박 기믹이 0x8D21 start를 쏴 살아있는 바크론 전투를 가로채
        // (stomp) 미터가 통째로 비던 버그의 근본 차단(실측: 두 바퀴 모두 교전 ~14초 뒤 가시속박 2921427이 stomp).
        // 현 보스가 죽으면(HP 0 → EndBattle이 CurrentTarget=-1) 이 가드가 풀려 다음 보스가 정상 개시된다. HP
        // 미보고(null)/0은 보호하지 않는다 — 갓-시작 순간의 미세 창엔 실측상 기믹이 오지 않고, null 보호는 종료
        // 토글 유실 시 다음 보스를 얼릴 수 있어서다. 이 stomp는 잘린 전투 저장·업로드(191M 오염)도 유발하므로,
        // 막는 편이 오염 위험을 오히려 낮춘다.
        // ⚠️ 신선도 항(2026-08-08)이 없으면 이 가드가 영구 차단이 된다: 조용해진 보스는 마지막 HP가 >0으로
        // 남아 아래 조건을 계속 만족하므로, 다음 보스의 start가 매번 거부된다. 종전 주석은 "현 보스가 죽으면
        // 가드가 풀린다"만 상정했고 HP=null(종료토글 유실)만 예외로 뒀다 — 살아있는데 무소식인 경우가 구멍이었다.
        // TickBossBattleIdle이 리포트 틱마다 같은 일을 하지만, 그 틱과 이 경로(소비자 스레드)의 순서는 보장되지
        // 않으므로 여기서도 같은 기준으로 양보한다.
        if (CurrentTarget() > 0
            && mobId != CurrentTarget()
            && CurrentBattleStart() > 0L
            && CurrentBattleEnd() == 0L
            && (MobHp(CurrentTarget()) ?? 0) > 0
            && now - _lastBossActivityMs <= BossIdleTimeoutMs)
        {
            return;
        }

        _pendingStart = null;
        _unresolvedStarts.Remove(mobId);
        _recentlyEndedBattles.Remove(mobId);
        _battleRevision++; // 최저치는 리비전으로 무효화된다 — 여기서 지우면 아직 저장 안 된 직전 판이 값을 잃는다
        
        // 보스 전투의 시작. 허수아비의 고정 종료가 여기까지 살아남으면 그 값이 이 전투의 종료 스탬프로
        // 새어 나간다(191M 오염과 같은 계열의 사고다). 창을 여는 자리에서 확실히 지운다.
        // (FlushPacket 도 같은 일을 하지만, 이 경로가 그걸 반드시 거친다는 보장은 없다.)
        ClearDummyWindow();
        _packetRepository.SaveCurrentBattleStart(startAt);
        SaveCurrentTarget(mobId);
        _activeBattleMobCode = mobCode;
        // 교전 직후 아직 아무 데미지/HP도 안 왔을 때 유휴 타이머가 0에서 시작해 곧바로 만료되지 않도록 기준을 둔다.
        _lastBossActivityMs = now;
    }

    /// <summary>Feature 2 — 무스펠 성배 '염화의 수호검' 5/5 분할에서, 본인(executor)이 실제로 딜을 넣는 수호검으로
    /// _currentTarget을 따라가게 한다. **염화의 수호검 한정**: 현재·신규 타깃이 둘 다 이 이름일 때만 전환하고, 그 외
    /// 인카운터는 primary-lock 그대로다. SaveDamage(소비자 스레드)에서 호출 — StartBattle/EndBattle과 같은 스레드라
    /// _currentTarget 변경에 새 경합이 없다. 데미지는 타깃별로 이미 버킷팅돼 있어(Repositories), 포인터/창만 옮긴다.</summary>
    private void MaybeFollowSelfTarget(ParsedDamagePacket pdp)
    {
        int exec = ExecutorId();
        if (exec == 0 || pdp.ActorId != exec || pdp.TargetId <= 0)
        {
            return; // 본인 '직접' 타격만 신호로 인정
        }

        int current = CurrentTarget();
        if (current <= 0)
        {
            return; // 열린 전투 없음 — 개시는 StartBattle 소관(여기서 열지 않는다)
        }

        // 스코프 게이트: 지금 보여주는 타깃이 '염화의 수호검'일 때만 이 특수 전환을 켠다(그 외 인카운터는 그대로).
        if (_activeBattleMobCode is not { } curCode || GetMob(curCode) is not { Name: SplitBossName })
        {
            return;
        }

        int target = pdp.TargetId;
        long now = pdp.Timestamp; // CurrentBattleStart / ActivePacketCutoff와 같은 시계

        // 본인이 때리는 타깃(현재 포함)의 지속-자기딜 스트릭 갱신 — 아래 "현재에 조용한가" 판정이 현재 LastMs를 읽는다.
        if (!_selfDamageStreak.TryGetValue(target, out (long FirstMs, long LastMs, int Hits) s) || now - s.LastMs > SelfStreakGapMs)
        {
            s = (now, now, 0);
        }

        s = (s.FirstMs, now, s.Hits + 1);
        _selfDamageStreak[target] = s;
        if (_selfDamageStreak.Count > SelfStreakCap)
        {
            foreach (int stale in _selfDamageStreak.Where(kv => now - kv.Value.LastMs > 5 * 60_000L).Select(kv => kv.Key).ToList())
            {
                _selfDamageStreak.Remove(stale);
            }
        }

        if (target == current)
        {
            return; // 이미 보여주는 타깃
        }

        // 신규 타깃도 살아있는 '염화의 수호검'이어야 한다(기믹/잡몹/시체 배제).
        if (GetMobId(target) is not { } newCode || GetMob(newCode) is not { Name: SplitBossName } || (MobHp(target) ?? 1) <= 0)
        {
            return;
        }

        // 지속 자기딜(단발 스치기 아님) + 현재 타깃엔 본인이 조용해야(현재를 계속 때리면 절대 안 뺏김).
        if (s.Hits < SelfSwitchMinHits || now - s.FirstMs < SelfSwitchDwellMs)
        {
            return;
        }

        if (_selfDamageStreak.TryGetValue(current, out (long FirstMs, long LastMs, int Hits) cur) && now - cur.LastMs < CurrentSelfQuietMs)
        {
            return;
        }

        // 전환 승인 — 창을 신규 보스 교전시각(또는 본인 첫 타격)으로 back-date. 데미지는 타깃별 버킷이라 이미 저장돼
        // 있고 ActivePacketCutoff=start-1000이 그 버킷 전체를 admit → 창↔데미지 일관(191M 없음). 나가는 보스는
        // GetDps가 자기 캐시된 토글로 정상 저장한다.
        long backdatedStart = s.FirstMs;
        if (_bossEngageAtMs.TryGetValue(target, out long eng) && eng < backdatedStart)
        {
            backdatedStart = eng;
        }

        _battleRevision++;
        _packetRepository.SaveCurrentBattleStart(backdatedStart);
        SaveCurrentTarget(target);
        _activeBattleMobCode = newCode;
        _unresolvedStarts.Remove(target);
        _recentlyEndedBattles.Remove(target);
        _pendingStart = null;
        // 타깃이 바뀌었으니 유휴 기준도 새 타깃 것으로 옮긴다. 안 옮기면 나가는 수호검이 조용했던 시간이 그대로
        // 새 전투의 유휴로 계산돼, 방금 연 전투가 곧바로 만료될 수 있다(다음 데미지가 다시 찍어주긴 하지만
        // 그 자가 복구에 기대지 않는다). 이 전환을 부른 타격 자체가 활동이므로 그 시각으로 찍는다.
        _lastBossActivityMs = now;
    }

    /// <summary>리포트 틱마다 부르는 보스 전투 유휴 점검(<see cref="DpsCalculator.GetDps"/> 상단, 더미 틱 옆).
    /// 추적 중인 보스가 <see cref="BossIdleTimeoutMs"/> 동안 아무 전투 신호도 안 내면 전투를 닫는다.
    /// <para>종료 시각은 <b>마지막 활동 시각</b>으로 찍는다 — 유휴 구간을 전투 길이에 넣지 않기 위해서다.
    /// 사망 확인이 없으므로 업로드는 <c>not_kill</c>로 자동 스킵되고(로컬 히스토리에는 남는다), 통계는 오염되지
    /// 않는다.</para>
    /// <para>더미는 자기 상태기(<see cref="TickDummyBattle"/>)가 5초 유휴로 따로 처리하므로 건드리지 않는다.</para></summary>
    public void TickBossBattleIdle()
    {
        int current = CurrentTarget();
        if (current <= 0 || IsCurrentTargetDummy()) return;
        if (CurrentBattleStart() <= 0L || CurrentBattleEnd() != 0L) return;

        long last = _lastBossActivityMs;
        long now = Clock();
        if (last <= 0L || now - last <= BossIdleTimeoutMs) return;

        // 두 번째 조건: 파티가 아무 데도 딜을 안 넣고 있어야 한다. 기믹으로 보스만 무음인 동안에는 쫄 딜이
        // 계속 찍히므로 여기서 걸려 전투가 유지된다 — 기믹이 아무리 길어도 안전하다.
        if (_lastAnyDamageMs > 0L && now - _lastAnyDamageMs <= AnyCombatQuietMs) return;

        SaveCurrentBattleEnd(last);
        SaveCurrentTarget(-1);
        _recentlyEndedBattles[current] = new EndedBattle(_activeBattleMobCode ?? GetMobId(current), last);
        _activeBattleMobCode = null;
    }

    public void EndBattle(int mobId)
    {
        if (CurrentTarget() != mobId) return;
        int? mobCode = _activeBattleMobCode ?? GetMobId(mobId);
        SaveCurrentBattleEnd(Clock());
        SaveCurrentTarget(-1);
        _recentlyEndedBattles[mobId] = new EndedBattle(mobCode, Clock());
        _activeBattleMobCode = null;
        ClearGroggyState();
    }

    // ---- battle log ----

    public DpsLog SaveBattleLog(
        DpsReport data,
        Dictionary<int, Dictionary<string, AnalyzedSkill>> skillDetails,
        Dictionary<int, List<OperatingData>> buffRates,
        List<OperatingData> bossBuffRates)
    {
        // The roster describes THIS battle only while it is still fresh. Every other reader of _partyRoster
        // gates on its age (PartyRoster, PartyMemberIdentities, PartyRosterJobPower, …); this freeze was the
        // one place that read it raw, so a roster left behind by content the player had already finished got
        // stamped verbatim onto whatever they fought next — including a solo field pull after the party broke
        // up. 30 minutes matches the window the other readers and the payload builder's roster-power fallback
        // already use, and is comfortably past the observed re-broadcast gap (p99 ≈ 9 minutes; 0x9702 arrives
        // in bursts rather than on a cadence, so a tight window would drop a live party's roster mid-run).
        (Dictionary<int, int> rosterSlots, int rosterSize) = FreshPartySlots(data.Contributors);

        var snapshot = new DpsReport
        {
            Contributors = data.Contributors.Select(CopyUser).ToList(),
            BattleStart = data.BattleStart,
            BattleEnd = data.BattleEnd,
            Information = data.Information.ToDictionary(kv => kv.Key, kv => CopyInfo(kv.Value)),
            Target = data.Target is { } t ? new MobInfo(t.Id, t.Mob, t.RemainHp, t.MaxHp) : null,
            Packets = null,
            ExecutorId = ExecutorId(),     // freeze the 본인 uid so a history replay self-colors the own row (CopyUser froze IsExecutor — usually false)
            // 기록 재생에서도 로스터 구제의 '파티 씬' 증거가 살아 있어야 구제된 파티원 행이 안 사라진다.
            // ⚠️ 이 한 줄만으로는 부족했다 — `RefreshRecentReportFromCache`의 초기화 목록이 종료 틱에 이 값을
            // 떨어뜨리고 있어서 여기서 복사해 봐야 false 였다. 그쪽을 먼저 고쳤기에 이제 의미가 있다.
            TargetInstanced = data.TargetInstanced,
            BuffRates = buffRates,         // frozen so the detail (history replay) matches the web
            BossBuffRates = bossBuffRates,
            SkillDetailsSnapshot = skillDetails, // frozen so the replayed detail's skill table + summary aren't empty
            // frozen 0x9702 sub-party slots (1-5/6-10), keyed to the actual battle uids, and how many people
            // that roster held — both only when the roster is still this battle's (see rosterFresh above).
            // 0 is the documented "unknown" roster size, which is what a pre-rosterSize saved battle carries.
            PartySlots = rosterSlots,
            PartyRosterSize = rosterSize,
            DpsSeries = data.DpsSeries,          // frozen per-second damage series so the replayed DPS graph isn't empty
            BuffIntervals = data.BuffIntervals,  // frozen buff timeline (built pre-prune by the caller) for the graph's icon lane
            SkillCasts = data.SkillCasts,        // frozen cast timeline (built pre-prune by the caller) for the 스킬 타임라인 탭
            DpsMetrics = data.DpsMetrics,        // frozen nDPS/rDPS — unrecomputable once the buff repo is pruned below
            SelfJudgment = data.SelfJudgment,    // frozen 판정 교차표 — 누적기는 다음 전투 시작 때 비워진다
            // frozen 시련 난이도. ⚠️ data 가 아니라 추적기에서 직접 읽는다 — 이 목록은 손으로 나열돼 있어
            // 상류 어딘가가 스탬프를 빠뜨리면 기록만 조용히 비고, 그건 화면을 봐도 안 보인다.
            // 추적기는 던전을 나갈 때 비워지므로 저장 시점 값이 곧 이 전투의 난이도다.
            TrialDifficulty = TrialDifficulty.Current,
        };

        var log = new DpsLog
        {
            Report = snapshot,
            SummonMap = new Dictionary<int, int>(_summonRepository.GetAll()),
            Packets = [],
            SkillDetails = skillDetails,
            BuffRates = buffRates,
            BossBuffRates = bossBuffRates,
        };

        _battleLogRepository.Save(log);
        // 허수아비 런은 버프 저장소를 자르지 않는다. 자르면 런 하나가 끝날 때마다 그 시점 이전의 버프 이력이
        // 통째로 날아가, 이어지는 <b>진짜</b> 전투의 가동률·nDPS/rDPS 가 미리 걸어 둔 장기 버프를 못 본다.
        // 연습 30번이면 30번 잘린다. 허수아비가 기록에 남기 시작한 뒤로 실제로 발생 가능한 경로가 됐다.
        if (snapshot.Target?.Mob.IsDummy != true)
        {
            _useBuffRepository.PruneBefore(data.BattleEnd + 1);
            _skillCastRepository.PruneBefore(data.BattleEnd + 1);
        }

        return log;
    }

    public List<(int Index, DpsReport Report)> RecentBattleList()
    {
        var list = new List<(int, DpsReport)>();
        IReadOnlyList<DpsLog> logs = _battleLogRepository.GetAll();
        for (int i = 0; i < logs.Count; i++)
        {
            list.Add((i, logs[i].Report));
        }

        return list;
    }

    public DpsLog? BattleLog(int idx) => _battleLogRepository.Get(idx);

    /// <summary>
    /// 전투 기록을 <paramref name="directory"/> 에 영속화하고, 이미 남아 있던 기록을 불러온다.
    /// <para>기동 때 한 번만 부른다. 부르지 않으면 기록은 예전처럼 메모리에만 산다 — 테스트와 진단 도구가
    /// 그 모양을 그대로 쓴다.</para>
    /// </summary>
    public void EnableBattleHistoryPersistence(string directory) =>
        _battleLogRepository.AttachStore(new BattleHistoryStore(directory));

    public void HardReset()
    {
        _resetEpoch++;
        _battleRevision = 0;
        _battleLogRepository.Flush();
        _mobHpRepository.Flush();
        _mobIdRepository.Flush();
        _userRepository.Flush();
        _summonRepository.Flush();
        _useBuffRepository.Flush();
        _skillCastRepository.Flush();
        _packetRepository.Flush();
        _recentlyEndedBattles.Clear();
        _activeBattleMobCode = null;
        _pendingStart = null;
        _lastDummyHitTime = 0;
        _dummyCutoff = false; // full wipe re-arms the dummy test window (mode/duration are preserved)
        _selfDamageStreak.Clear(); // Feature 2
        _bossEngageAtMs.Clear();
        _battleLowHp.Clear();
        ClearPartyRosterState();
        ClearAetherStatus();
        ClearShugoKey();
        ClearOwnerBuffs();
    }

    /// <summary>
    /// Soft reset for the user "초기화" button: clears the battle LEDGER (saved history + the in-flight damage
    /// packets) and all battle-lifecycle transients, but PRESERVES every piece of runtime reference state that
    /// the game only re-broadcasts on a zone load — recognized users (incl. the executor), the mob-instance map,
    /// mob HP, the summon map, buff intervals, the party roster, official-lookup throttles, and the catalogs.
    /// This is what makes reset usable inside a dungeon with no map transition: the executor stays recognized
    /// (0x3633 won't re-fire) AND already-spawned bosses keep their instance→code mapping (0x3640 won't re-fire),
    /// so the very next pull still starts a battle and attributes the local player's DPS. Use <see cref="HardReset"/>
    /// only for a true full wipe.
    /// </summary>
    public void ResetBattleRecords()
    {
        _resetEpoch++;            // reject in-flight SaveDamage(pdp, oldEpoch) captured before this reset
        _battleRevision = 0;      // assign 0 (mirror HardReset); DpsCalculator zeroes _currentBattleRevision in lockstep
        _battleLogRepository.Flush(); // clear saved battle history (the 전투 기록 panel)
        _packetRepository.Flush();    // drop the in-flight/old battle's damage packets
        _recentlyEndedBattles.Clear();
        _activeBattleMobCode = null;
        _pendingStart = null;
        _lastDummyHitTime = 0;
        _dummyCutoff = false;     // the 초기화 button re-arms the dummy test window (mode/duration are preserved)
        _selfDamageStreak.Clear(); // Feature 2
        _bossEngageAtMs.Clear();
        _battleLowHp.Clear();
        // drop the 0x9702 party snapshot — a stale party (e.g. after leaving the dungeon and returning
        // to town) must not preview on reset; it re-fills on party formation
        ClearPartyRosterState();
        // PRESERVE (do NOT flush): _userRepository (recognized chars + executor), _mobIdRepository (boss
        // instance→code, needed for the next StartBattle in a no-respawn dungeon), _mobHpRepository,
        // _summonRepository, _useBuffRepository, _officialLookupAttempts, and the load-once catalogs
        // (_mobs/_skillRepository/_buffRepository/_buffBlacklist).
    }

    /// <summary>Current 0x9702 roster mapped to the uids the stats payload tags (uid -&gt; slot 1-8), frozen into
    /// a saved report (<see cref="SaveBattleLog"/>) so the stats upload can tag each participant's sub-party for
    /// an 8-인 공대 — slots 1-4 = party 1, 5-8 = party 2. Members with slot 0 (header unmatched) or no recognized
    /// uid are skipped; empty for a non-raid / unknown roster (the upload then omits party tags).</summary>
    /// <summary>
    /// 지금 로스터가 <b>이 전투의 것</b>일 때만 (uid→슬롯, 정원)을 낸다. 아니면 (빈 사전, 0).
    /// <para>🔑 게이트를 이 함수 <b>안에</b> 둔 이유: 저장 스냅샷과 라이브 리포트가 둘 다 이걸 쓰는데, 각자
    /// 게이트를 걸면 서로 다른 시계를 보게 된다 — <c>_partyRosterSetAtMs</c>(콘텐츠 갱신 시각)와
    /// <c>_partyRosterAtMs</c>(재방송 시각)는 다른 값이다. 그러면 "라이브엔 stale 슬롯이 있는데 저장본엔 없는"
    /// 새 불일치가 생긴다 — M-22 가 고치려는 것과 정확히 같은 종류의 갈림이다.</para>
    /// </summary>
    public (Dictionary<int, int> Slots, int RosterSize) FreshPartySlots(IReadOnlyList<User> contributors)
    {
        // 30분. 다른 로스터 독자들과 페이로드 빌더의 전투력 폴백이 이미 쓰는 창이고, 관측된 재방송 간격
        // (p99 ≈ 9분)보다 충분히 길다 — 0x9702 는 주기가 아니라 버스트로 온다.
        bool fresh = _partyRoster.Count > 0 && Clock() - _partyRosterSetAtMs <= RosterFreezeTtlMs;
        // 🔑 정원을 싣는다, 파싱된 멤버 수가 아니다. 0x9702 스냅샷은 부분으로 오는 것이 정상이라 멤버 수는
        // 수시로 정원에 못 미친다 — 그걸 그대로 보내면 10인 공대가 9 로 나가고 서버의
        // RAID_ROSTER_SIZES = [10] 에서 통째로 탈락한다(웹 실측 2026-09-19: 성역 30일 1,385건 = 3.362%,
        // 그 중 roster=9 가 1,340건). 통계웹 스키마 주석도 이 필드를 처음부터 "로스터 정원"으로 정의한다.
        // 정원을 모를 때만 멤버 수로 폴백한다(옛 캡처 로그·테스트 경로).
        int size = _partyRosterCapacity > 0 ? _partyRosterCapacity : _partyRoster.Count;
        return fresh
            ? (CurrentPartySlots(contributors), size)
            : (new Dictionary<int, int>(), 0); // 0 = 정원 모름(옛 저장 전투가 싣는 값과 같다)
    }

    private Dictionary<int, int> CurrentPartySlots(IReadOnlyList<User> contributors)
    {
        int executorId = _userRepository.Executor();
        User? executor = executorId > 0 ? _userRepository.Get(executorId) : null;

        var slots = new Dictionary<int, int>();
        foreach ((string nickname, int server, int slot) in _partyRoster)
        {
            if (slot <= 0)
            {
                continue;
            }

            int? uid = ResolveRosterMemberUid(nickname, server, executor, contributors);
            if (uid != null)
            {
                slots[uid.Value] = slot;
            }
        }

        return slots;
    }

    /// <summary>Resolve a 0x9702 roster member (name+server) to the uid the stats payload actually tags. The
    /// executor re-registers under a FRESH uid on every zone/instance load (0x3633), but its prior User objects
    /// linger in the repository, so a plain name+server lookup (<see cref="UserRepository.FindByNicknameAndServer"/>
    /// returns FirstOrDefault) often returns a STALE self uid — the slot then keys to a non-participant and the
    /// uploader's own row never gets its slot (the 8-인 공대 sub-party split stays off). The same hazard hits any
    /// party member seen under more than one uid. So resolve against the uids the payload actually tags: first a
    /// battle contributor (the recognized+damaging self and every dealer match here, by their live combat uid),
    /// then the live executor (a recognized self that dealt no damage — keeps its slot for isRaid even if it isn't
    /// among the contributors, and never a stale repository uid), and only then fall back to the repository for a
    /// roster member who didn't deal damage (keeps the party-2 slots present so the sub-party detection still
    /// fires). Contributor-first means a same-name dealer always wins over a possibly-lagging executor pointer.
    /// <para>🔑 서버 비교는 <b>양쪽이 모두 &gt;0일 때만</b> 한다. 잘린 0x3633은 Server=-1을, 아직 스냅샷을 못 본
    /// 기여자는 0을 남기는데, 그걸 불일치로 읽으면 이름이 맞는데도 매칭이 통째로 실패한다. 같은 완화가
    /// <c>identityChanged</c> 판정(이 파일 위쪽)에 이미 같은 이유로 들어가 있다 — 여기만 빠져 있었다.
    /// 정확 일치(이름+서버)를 먼저 한 바퀴 돌아 항상 이기게 하고, 느슨한 일치는 그 다음 바퀴에서만 쓴다.</para>
    /// <para>실측(2026-08-09, 운영 DB): 2.9.3 공대 미신뢰 24건이 전부 "정확히 1명만 슬롯 없음"이고 그중
    /// <b>19건(79%)이 업로더 본인</b>이었다. 참가자(평균 6.9)가 로스터(10)보다 적은 기믹 분할이라 웹의 소거법으로는
    /// 메울 수 없는 구간이고, 여기서 본인 uid를 제대로 돌려주는 것이 유일한 해법이다.</para></summary>
    private int? ResolveRosterMemberUid(string nickname, int server, User? executor, IReadOnlyList<User> contributors)
    {
        // 1) 정확 일치 — 가장 강한 근거이므로 항상 먼저 이긴다.
        foreach (User contributor in contributors)
        {
            if (string.Equals(contributor.Nickname, nickname, StringComparison.Ordinal) && contributor.Server == server)
            {
                return contributor.Id;
            }
        }

        // 2) 서버가 한쪽이라도 미상이면 이름만으로 인정. 기여자는 페이로드가 실제로 태그하는 uid라
        //    executor/저장소 폴백보다 항상 낫다 — 그 둘은 이 전투에 없는 uid를 돌려줄 수 있다.
        foreach (User contributor in contributors)
        {
            if (string.Equals(contributor.Nickname, nickname, StringComparison.Ordinal) && ServerCompatible(contributor.Server, server))
            {
                return contributor.Id;
            }
        }

        if (executor != null
            && string.Equals(executor.Nickname, nickname, StringComparison.Ordinal)
            && ServerCompatible(executor.Server, server))
        {
            return executor.Id;
        }

        return _userRepository.FindByNicknameAndServer(nickname, server)?.Id;
    }

    /// <summary>두 서버 값이 서로 모순되지 않는가. 어느 한쪽이라도 미상(0 또는 음수)이면 모순이 아니다 —
    /// 미상을 불일치로 읽으면 이름이 맞는 본인/파티원도 놓친다.</summary>
    private static bool ServerCompatible(int a, int b) => a <= 0 || b <= 0 || a == b;

    private static User CopyUser(User u) => new(u.Id, u.Nickname, u.Server, u.Job, u.IsExecutor, u.Power) { JobSource = u.JobSource };

    private static DpsInformation CopyInfo(DpsInformation i) =>
        new(i.Amount, i.Dps, i.Contribution, i.EntireContribution);
}
