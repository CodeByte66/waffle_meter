using System.Globalization;
using System.Text;
using K4os.Compression.LZ4;

namespace WaffleMeter.Capture;

/// <summary>
/// Diagnostics/observation hooks mirroring the Kotlin <c>PacketDebugLogger</c> calls inside
/// <c>StreamProcessor</c>. The host (live app, replay CLI, tests) supplies a sink to observe
/// dispatch/decompression/damage/meta/battle decisions; the parser logic itself stays pure.
/// </summary>
public interface IStreamProcessorSink
{
    void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len);
    void UnknownOpcode(int opcode, bool extraFlag, int len);
    void CompressedPacket(int len, bool extraFlag);
    void ParserError(string stage, string reason);

    /// <summary>A parsed direct/DoT damage event (Kotlin PacketDebugLogger.damage). reason is set
    /// only when saved == false; mobCode requires runtime mob state (deferred — null for now).</summary>
    void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode);

    /// <summary>A non-damage state event (Kotlin PacketDebugLogger.meta). The fields mirror the
    /// exact key/value set Kotlin logs for that meta type.</summary>
    void Meta(string type, params (string Key, object? Value)[] fields);

    /// <summary>A battle start/end/rejection event (Kotlin PacketDebugLogger.battle).</summary>
    void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason);
}

/// <summary>No-op sink (default).</summary>
public sealed class NullStreamProcessorSink : IStreamProcessorSink
{
    public static readonly NullStreamProcessorSink Instance = new();
    public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) { }
    public void UnknownOpcode(int opcode, bool extraFlag, int len) { }
    public void CompressedPacket(int len, bool extraFlag) { }
    public void ParserError(string stage, string reason) { }
    public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode) { }
    public void Meta(string type, params (string Key, object? Value)[] fields) { }
    public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
}

/// <summary>
/// Verbatim port of the dispatch + decompression + parsing core of Kotlin <c>StreamProcessor</c>
/// (src/main/kotlin/packet/StreamProcessor.kt). Opcode routing + FF FF LZ4 decompression (L3a),
/// direct/DoT damage (L3b), and the meta scanners — nickname/own-power (L3c byte-derived) and
/// summon/mob_spawn/remain_hp/battle/buff (L3c via the data context).
///
/// Deferred data-layer side effects (Phase 3 wiring elsewhere): saveNickname / saveUserPower /
/// saveDamage / mobId(target) for the damage mobCode / saveSummon / mobHp / saveUseBuff /
/// startBattle / endBattle / touchDummyBattle. The parser only needs the mob catalog, the runtime
/// instanceId->mobCode map, and skill-code membership, supplied via <see cref="ICaptureGameData"/>.
///
/// CORRECTNESS-CRITICAL: offsets, the signed-byte extraFlag range, FF FF detection, the heuristic
/// scans, and all bounds guards must match Kotlin exactly.
/// </summary>
public sealed class StreamProcessor
{
    private const int Mask = 0x0F;

    // Synthetic per-apply duration stamped on an indefinite-duration stance (폭주, duration 0xFFFFFFFF). Used as
    // the buff's recorded length for uptime stats. The LIVE combat-assist overlay does NOT expire on this alone
    // — the data layer keeps the maintained-stance slot alive for a much longer keep-alive window
    // (DataManager.IndefiniteStanceOverlayKeepAliveMs) because held re-broadcasts are not perfectly periodic
    // (combat lulls, dropped frames, momentary owner==0), and a short TTL false-expired it mid-hold.
    private const long IndefiniteStanceFallbackMs = 6000;

    // 회생의 계약 긴급 회복(생명력 10% 이하 → 최대 HP의 일정 비율 즉시 회복, 재발동 1분)의 스킬 코드.
    // 이 스킬을 가진 5직업만 존재한다(검성/수호성/치유성/호법성은 '생존 의지'로 회복 효과 자체가 없음).
    // 원본 코드와 NormalizeDamageSkillCode가 만들어 내는 정규화형을 모두 담는다 — 코퍼스 실측 정규화형:
    // 14790000(궁성) 182건, 13790000(살성) 82건, 15790007(마도성) 53건, 16790001(정령성) 39건, 19790000(권성) 4건.
    private static bool IsRevivalHealCode(int code) => code is
        13790000 or 13790007 or   // 살성
        14790000 or 14790007 or   // 궁성
        15790000 or 15790007 or   // 마도성
        16790000 or 16790001 or   // 정령성
        19790000 or 19790001;     // 권성

    private static int Key(int b1, int b2) => b1 | (b2 << 8);

    private const int DamageKey = 0x04 | (0x38 << 8);          // 0x3804
    private const int DoTKey = 0x05 | (0x38 << 8);             // 0x3805
    // 2026-06-10 server patch inserted a message into the 0x36 category, shifting every 0x36 opcode
    // whose first byte >= 0x40 by +1 (Kotlin StreamProcessor fix 88ca14e / release v1.7.9). Other
    // categories (0x38 damage, 0x8D battle) are untouched. OwnNickname (0x33 < 0x40) is unchanged.
    private const int OwnNicknameKey = 0x33 | (0x36 << 8);     // 0x3633 (unchanged)
    private const int OtherNicknameKey = 0x45 | (0x36 << 8);   // 0x3645 (was 0x3644)
    private const int OwnCombatPowerKey = 0x56 | (0x36 << 8);  // 0x3656 (was 0x3655)
    private const int SummonKey = 0x41 | (0x36 << 8);          // 0x3641 (was 0x3640)
    private const int BuffApplyKey = 0x2A | (0x38 << 8);       // 0x382A
    // 버프 제거 브로드캐스트. 슬롯 단위라 "정확히 그 인스턴스"를 지목한다(코드만 주는 0x921A와 다름).
    private const int BuffRemoveKey = 0x2C | (0x38 << 8);      // 0x382C
    private const int BuffApply2Key = 0x2B | (0x38 << 8);      // 0x382B
    // Skill cooldown snapshot (0x38 category): a table of {u32 skillCode, varint remainingMs} for the local
    // player's hotbar (remaining 0 = ready). Drives the buff overlay's "grayed while on cooldown" option.
    private const int CooldownKey = 0x47 | (0x38 << 8);        // 0x3847
    // Per-cast cooldown START (multi-actor) — grays the overlay the instant a skill is cast, before the
    // periodic 0x3847 snapshot catches up. remaining = the frame's LAST varint (ground-truth verified: 바이젤/
    // 지원사격 39100ms, 축복의활 78200ms). Filtered to self.
    private const int CooldownStartKey = 0x02 | (0x38 << 8);   // 0x3802
    // 캐릭터 스탯 사전. 0x364A = 변경분(엔티티 id 포함), 0x3649 = 전체 스냅샷(엔티티 id 없음 — 본인 것이고
    // 수신 측이 현재 본인 uid에 묶는다). 2026-06-10 패치의 0x36 시프트(첫 바이트 ≥0x40이면 +1) 이전 번호는
    // 각각 0x3649 / 0x3648 이었다. 레이아웃은 아래 ParseStatSheet 참조.
    private const int StatSheetDeltaKey = 0x4A | (0x36 << 8);  // 0x364A (was 0x3649)
    private const int StatSheetFullKey = 0x49 | (0x36 << 8);   // 0x3649 (was 0x3648)
    private const int BattleToggleKey = 0x21 | (0x8D << 8);    // 0x8D21
    private const int RemainHpKey = 0x00 | (0x8D << 8);        // 0x8D00
    // 엔티티 사망 브로드캐스트. 죽은 엔티티 id가 첫 varint. 코퍼스 3세션 481프레임이 전부 그 엔티티의 HP=0
    // 직후(~50ms)에 왔고 본인 사망 횟수와 1:1(오탐 0). 본인 사망 시 버프 오버레이를 비우는 근거 신호다.
    private const int EntityDeathKey = 0x04 | (0x8D << 8);     // 0x8D04
    // Party join-request family (0x97 category — untouched by the 2026-06-10 0x36 shift).
    private const int JoinRequestKey = 0x07 | (0x97 << 8);     // 0x9707
    private const int CancelJoinKey = 0x25 | (0x97 << 8);      // 0x9725
    private const int AdmitJoinKey = 0x0B | (0x97 << 8);       // 0x970B
    private const int RefuseJoinKey = 0x09 | (0x97 << 8);      // 0x9709
    private const int InstanceStartKey = 0x18 | (0x97 << 8);   // 0x9718
    private const int ExitPartyKey = 0x1D | (0x97 << 8);       // 0x971D
    private const int PartyRosterKey = 0x02 | (0x97 << 8);     // 0x9702 — full party/raid roster snapshot
    // 0x9200 — 파티/공대 멤버 상세 프로필. 0x9702 로스터와 달리 레코드마다 엔티티 uid를 함께 싣는 유일한
    // 브로드캐스트라, 본인 로드 패킷(0x3633)이 오지 않은 재인스턴스에서 본인을 새 uid에 묶는 근거가 된다.
    private const int MemberProfileKey = 0x00 | (0x92 << 8);   // 0x9200
    // Resource-status family (0x61 category) carrying the aether (오드) balance. Two opcodes ride the same
    // marker-based body layout; both are handled by the one resource parser.
    private const int AetherKeyA = 0x0B | (0x61 << 8);         // 0x610B
    private const int AetherKeyB = 0x0C | (0x61 << 8);         // 0x610C
    // Field-boss respawn-timer broadcast (0x91 category). Carries a table of boss-code → target-time records.
    private const int FieldBossTimerKey = 0x01 | (0x91 << 8);  // 0x9101
    // Dungeon instance phase windows (same 0x61 category as the resource family). Body is
    // [u32-LE mapId][u8 phase][u64-LE startMs][u64-LE endMs]. Only the trial needs it: its main phase length
    // IS the 제한 시간 난이도 setting, and that setting is not an abnormal so nothing else carries it.
    private const int InstancePhaseKeyA = 0x00 | (0x61 << 8);  // 0x6100
    private const int InstancePhaseKeyB = 0x01 | (0x61 << 8);  // 0x6101
    // 어비스 아티팩트 점령 현황. 0xE305 = the zone just loaded (one zone, no count prefix); 0xE307 = every zone,
    // sent at login and on each world-map open. This is what tells the meter WHICH 회랑 the side holds — the
    // corridor instance maps it used to rely on never appear on the wire. See AbyssArtifactParser.
    private const int AbyssArtifactZoneKey = 0x05 | (0xE3 << 8);  // 0xE305
    private const int AbyssArtifactAllKey = 0x07 | (0xE3 << 8);   // 0xE307

    // Epoch-ms sanity bounds for the phase window (2020-01-01 .. 2100-01-01). A window is only believed when
    // both ends land inside these, so a coincidental map-id match can't manufacture one.
    private const long MinPlausibleEpochMs = 1_577_836_800_000L;
    private const long MaxPlausibleEpochMs = 4_102_444_800_000L;

    // Opcodes safe to replay from a DUP-SUPPRESSED second game stream (see OnPacketReceived identityOnly). These
    // are all IDEMPOTENT — nickname (own/other), power, party roster, member profile, and mob spawn just
    // register / overwrite the same identity or code, so parsing them twice can't inflate anything. The party
    // roster frequently rides the suppressed connection (observed: a 10-인 공대 whose roster/profile packets
    // never reached the parser because a second server connection was suppressed as a VPN duplicate). Spawn is
    // included because a boss whose 0x3641 rode the suppressed stream then registers + retro-promotes here.
    // Damage / DoT / buff / cooldown / battle-toggle / HP stay suppressed so the single-stream damage lock holds.
    //
    // The 0x610x resource family (오드 / 슈고 열쇠 / 주간 성역) joined the list on 2026-08-11. It is idempotent in
    // the strongest sense — every record is an ABSOLUTE balance that overwrites, never a delta that accumulates —
    // and leaving it out produced exactly the reported symptom: the login snapshot rode the suppressed connection
    // while 0x3633 came through the kept one, so the character was recognized with no 오드 beside it.
    private static readonly HashSet<int> IdentityReplayOpcodes = new()
    {
        OwnNicknameKey, OtherNicknameKey, OwnCombatPowerKey, SummonKey, PartyRosterKey, MemberProfileKey,
        AetherKeyA, AetherKeyB,
        // The 어비스 아티팩트 broadcast joins for the same reason the 0x610x family did: it is an ABSOLUTE state
        // (who holds each artifact right now), so replaying it cannot inflate anything, and it arrives at login
        // — exactly the moment a second server connection is most likely to be the one carrying it.
        AbyssArtifactZoneKey, AbyssArtifactAllKey,
    };

    private static readonly Dictionary<int, string> OpcodeNames = new()
    {
        [OwnNicknameKey] = "OwnNickname",
        [OwnCombatPowerKey] = "OwnCombatPower",
        [OtherNicknameKey] = "OtherNickname",
        [SummonKey] = "Summon",
        [DamageKey] = "Damage",
        [DoTKey] = "DoT",
        [BuffApplyKey] = "BuffApply",
        [BuffApply2Key] = "BuffApply2",
        [CooldownKey] = "Cooldown",
        [CooldownStartKey] = "CooldownStart",
        [BattleToggleKey] = "BattleToggle",
        [StatSheetDeltaKey] = "StatSheet",
        [StatSheetFullKey] = "StatSheetFull",
        [RemainHpKey] = "RemainHp",
        [BuffRemoveKey] = "BuffRemove",
        [EntityDeathKey] = "EntityDeath",
        [Key(0x07, 0x97)] = "JoinRequest",
        [Key(0x25, 0x97)] = "CancelJoin",
        [Key(0x0B, 0x97)] = "AdmitJoin",
        [Key(0x09, 0x97)] = "RefuseJoin",
        [Key(0x18, 0x97)] = "InstanceStart",
        [Key(0x1D, 0x97)] = "ExitParty",
        [Key(0x02, 0x97)] = "PartyRoster",
        [MemberProfileKey] = "MemberProfile",
        [AetherKeyA] = "AetherStatus",
        [AetherKeyB] = "AetherStatus",
        [InstancePhaseKeyA] = "InstancePhase",
        [InstancePhaseKeyB] = "InstancePhase",
        [FieldBossTimerKey] = "FieldBossTimer",
        [AbyssArtifactZoneKey] = "AbyssArtifact",
        [AbyssArtifactAllKey] = "AbyssArtifact",
    };

    private static readonly byte[] PowerMarker = { 0xF4, 0xCB, 0x1F };

    private readonly IStreamProcessorSink _sink;
    private readonly ICaptureGameData _data;
    private readonly IJoinRequestSink _joinSink;

    // Mirrors DataManager.executorId(): set from the own-nickname snapshot, read by 0x3655.
    private int _executorId;

    // Last own combat power seen from 0x3656 + the nickname it belonged to; carried onto a new executor
    // uid only on re-entry of the SAME character (req 3), so a different character never inherits it.
    private int _lastOwnPower;
    private string _lastOwnNickname = string.Empty;

    public StreamProcessor(IStreamProcessorSink? sink = null, ICaptureGameData? data = null, IJoinRequestSink? joinSink = null)
    {
        _sink = sink ?? NullStreamProcessorSink.Instance;
        _data = data ?? NullCaptureGameData.Instance;
        _joinSink = joinSink ?? NullJoinRequestSink.Instance;
    }

    /// <summary>
    /// Cheap structural check used by the connection classifier (NOT by the parser): does this assembled
    /// packet look like a GAME packet — a known opcode key, or an LZ4 (FF FF) compressed game packet?
    /// Lets the app tell the game stream apart from high-volume non-game noise (P2P/streaming) purely by
    /// content, so loopback/booster game paths — which DO yield game packets — are never excluded.
    /// Mirrors the header walk in <see cref="OnPacketReceived"/>; a false negative only delays a
    /// connection earning "game" status, a false positive only spares a noisy connection.
    /// </summary>
    public static bool LooksLikeGamePacket(byte[] packet)
    {
        if (packet.Length < 4)
        {
            return false;
        }

        VarIntOutput lengthInfo = PacketPrimitives.ReadVarInt(packet);
        if (lengthInfo.Length < 0 || lengthInfo.Length >= packet.Length)
        {
            return false;
        }

        int flagByte = packet[lengthInfo.Length];
        bool extraFlag = flagByte >= 0xF0 && flagByte < 0xFF;

        if (extraFlag)
        {
            if (lengthInfo.Length + 2 < packet.Length
                && packet[lengthInfo.Length + 1] == 0xFF && packet[lengthInfo.Length + 2] == 0xFF)
            {
                return true; // FF FF LZ4-compressed game packet
            }
        }
        else if (lengthInfo.Length + 1 < packet.Length
            && packet[lengthInfo.Length] == 0xFF && packet[lengthInfo.Length + 1] == 0xFF)
        {
            return true;
        }

        int opcodeOffset = lengthInfo.Length + (extraFlag ? 1 : 0);
        if (opcodeOffset + 1 >= packet.Length)
        {
            return false;
        }

        int opcodeKey = (packet[opcodeOffset] & 0xFF) | ((packet[opcodeOffset + 1] & 0xFF) << 8);
        return OpcodeNames.ContainsKey(opcodeKey);
    }

    /// <summary><paramref name="identityOnly"/> = this segment came from a dup-suppressed second game stream:
    /// dispatch ONLY the idempotent identity/roster/spawn opcodes (<see cref="IdentityReplayOpcodes"/>) so the
    /// roster is recovered without letting damage/buff double-count.</summary>
    public void OnPacketReceived(byte[] packet, long arrivedAt, bool identityOnly = false)
    {
        if (packet.Length == 3)
        {
            return;
        }

        VarIntOutput lengthInfo = PacketPrimitives.ReadVarInt(packet);
        if (lengthInfo.Length < 0 || lengthInfo.Length >= packet.Length)
        {
            _sink.ParserError("processor", "invalid_packet_length_varint");
            return;
        }

        int flagByte = packet[lengthInfo.Length];
        bool extraFlag = flagByte >= 0xF0 && flagByte < 0xFF;

        if (extraFlag)
        {
            if (lengthInfo.Length + 2 < packet.Length
                && packet[lengthInfo.Length + 1] == 0xFF
                && packet[lengthInfo.Length + 2] == 0xFF)
            {
                _sink.CompressedPacket(packet.Length, true);
                DecompressPacket(packet, lengthInfo.Length, true, arrivedAt, identityOnly);
                return;
            }
        }
        else
        {
            if (lengthInfo.Length + 1 < packet.Length
                && packet[lengthInfo.Length] == 0xFF
                && packet[lengthInfo.Length + 1] == 0xFF)
            {
                _sink.CompressedPacket(packet.Length, false);
                DecompressPacket(packet, lengthInfo.Length, false, arrivedAt, identityOnly);
                return;
            }
        }

        int opcodeOffset = lengthInfo.Length + (extraFlag ? 1 : 0);
        if (opcodeOffset + 1 >= packet.Length)
        {
            return;
        }

        int opcodeKey = (packet[opcodeOffset] & 0xFF) | ((packet[opcodeOffset + 1] & 0xFF) << 8);

        // Dup-suppressed second game stream: replay ONLY the idempotent identity/roster/spawn opcodes so the
        // roster (which often rides the suppressed connection) is recovered. Everything else (damage/buff/…) is
        // skipped ENTIRELY here — before the dispatch breadcrumb and any parsing — so the single-stream damage
        // lock is untouched (no double-count) and the skipped packets don't even read as processed.
        if (identityOnly && !IdentityReplayOpcodes.Contains(opcodeKey))
        {
            return;
        }

        OpcodeNames.TryGetValue(opcodeKey, out string? name);
        _sink.Dispatch(opcodeKey, name, extraFlag, packet.Length);

        if (name is null)
        {
            _sink.UnknownOpcode(opcodeKey, extraFlag, packet.Length);
            return;
        }

        // Content-based capture feeds non-game / truncated TCP through here; a handler that reads past
        // a short buffer must be IGNORED (counted as a parser error), never crash the consumer. The
        // game stream is identified by which bytes parse cleanly as known opcodes.
        try
        {
            switch (opcodeKey)
            {
                case DamageKey:
                    ParsingDamage(packet, extraFlag, arrivedAt);
                    break;
                case DoTKey:
                    ParseDoTPacket(packet, extraFlag, arrivedAt);
                    break;
                case OwnNicknameKey:
                    SearchOwnNickname(packet, lengthInfo, arrivedAt);
                    break;
                case OtherNicknameKey:
                    SearchOtherNickname(packet, lengthInfo, arrivedAt);
                    break;
                case OwnCombatPowerKey:
                    ParseOwnCombatPower(packet, lengthInfo, extraFlag, arrivedAt);
                    break;
                case StatSheetDeltaKey:
                    ParseStatSheet(packet, lengthInfo, extraFlag, withEntityId: true);
                    break;
                case StatSheetFullKey:
                    ParseStatSheet(packet, lengthInfo, extraFlag, withEntityId: false);
                    break;
                case SummonKey:
                    ParseSummonPacket(packet, extraFlag);
                    break;
                case BattleToggleKey:
                    ParseBattlePacket(packet, lengthInfo, extraFlag);
                    break;
                case RemainHpKey:
                    ParseRemainHp(packet, lengthInfo, extraFlag);
                    break;
                case EntityDeathKey:
                    ParseEntityDeath(packet, lengthInfo, extraFlag, arrivedAt);
                    break;
                case BuffRemoveKey:
                    ParseBuffRemove(packet, lengthInfo, extraFlag);
                    break;
                case BuffApplyKey:
                case BuffApply2Key:
                    ParseBuffPacket(packet, lengthInfo, extraFlag, arrivedAt);
                    break;
                case CooldownKey:
                    ParseCooldownPacket(packet, lengthInfo, extraFlag, arrivedAt);
                    break;
                case CooldownStartKey:
                    ParseCooldownStartPacket(packet, lengthInfo, extraFlag, arrivedAt);
                    break;
                case JoinRequestKey:
                    ParseJoinRequest(packet, lengthInfo, extraFlag, arrivedAt);
                    break;
                case CancelJoinKey:
                    ParseCancelJoin(packet, lengthInfo, extraFlag);
                    break;
                case AdmitJoinKey:
                    ParseAdmitJoin(packet, lengthInfo, extraFlag);
                    break;
                case RefuseJoinKey:
                    ParseRefuseJoin(packet, lengthInfo, extraFlag);
                    break;
                case InstanceStartKey:
                    ParseInstanceStart(packet, lengthInfo, extraFlag);
                    break;
                case ExitPartyKey:
                    ParseExitParty(packet, lengthInfo, extraFlag);
                    break;
                case PartyRosterKey:
                    ParsePartyRoster(packet, lengthInfo, extraFlag);
                    break;
                case MemberProfileKey:
                    ParseMemberProfile(packet, lengthInfo, extraFlag);
                    break;
                case AetherKeyA:
                case AetherKeyB:
                    ParseAetherStatus(packet, opcodeOffset + 2, fromSnapshot: opcodeKey == AetherKeyA);
                    ParseShugoKey(packet, opcodeOffset + 2, fromSnapshot: opcodeKey == AetherKeyA);
                    ParseWeeklyContent(packet, opcodeOffset + 2, fromSnapshot: opcodeKey == AetherKeyA);
                    ParseAbyssCorridor(packet, opcodeOffset + 2, fromSnapshot: opcodeKey == AetherKeyA);
                    break;
                case FieldBossTimerKey:
                    ParseFieldBossTimers(packet, opcodeOffset + 2, arrivedAt);
                    break;
                case InstancePhaseKeyA:
                case InstancePhaseKeyB:
                    ParseInstancePhase(packet, opcodeOffset + 2);
                    break;
                case AbyssArtifactZoneKey:
                case AbyssArtifactAllKey:
                    ParseAbyssArtifacts(packet, opcodeOffset + 2, wholeAbyss: opcodeKey == AbyssArtifactAllKey);
                    break;
            }
        }
        catch (Exception e)
        {
            _sink.ParserError("dispatch", e.GetType().Name);
        }
    }

    /// <summary>LZ4 프레임을 풀어 안쪽 패킷들을 다시 <see cref="OnPacketReceived"/>로 넣는다.
    /// <paramref name="identityOnly"/>는 <b>반드시</b> 그대로 전파해야 한다 — 압축 분기가 identityOnly 게이트보다
    /// 앞에 있어서(위 :270 부근), 전파하지 않으면 억제된 중복 게임 스트림의 압축 프레임 안쪽이 전부 처리된다.
    /// 실측(코퍼스 4종): 게임 데미지 패킷의 40~73%가 압축 프레임 안에 실려 오므로, 누락 시 VPN 듀얼캡처
    /// 이중집계 방어(single-game-stream lock)가 그 비율만큼 무력화된다.</summary>
    private void DecompressPacket(byte[] packet, int headerLength, bool extraFlag, long arrivedAt, bool identityOnly)
    {
        try
        {
            int offset = headerLength + 2;
            if (extraFlag)
            {
                offset += 1;
            }

            int originLength = PacketPrimitives.ParseUInt32Le(packet, offset);
            offset += 4;

            var restored = new byte[originLength];
            LZ4Codec.Decode(packet.AsSpan(offset, packet.Length - offset), restored.AsSpan(0, originLength));

            int innerOffset = 0;
            while (innerOffset < restored.Length)
            {
                int pastInnerOffset = innerOffset;
                VarIntOutput lengthInfo = PacketPrimitives.ReadVarInt(restored, innerOffset);
                if (lengthInfo.Value == 0)
                {
                    innerOffset += 1;
                    continue;
                }

                int realLength = lengthInfo.Value + lengthInfo.Length - 4;
                if (realLength <= 0)
                {
                    _sink.ParserError("decompress", "invalid_inner_length");
                    break;
                }

                OnPacketReceived(restored[pastInnerOffset..(pastInnerOffset + realLength)], arrivedAt, identityOnly);
                innerOffset += realLength;
            }
        }
        catch (Exception e)
        {
            _sink.ParserError("decompress", e.GetType().Name);
        }
    }

    /// <summary>Direct damage (opcode 0x3804). Verbatim port of Kotlin parsingDamage (593-712).</summary>
    private void ParsingDamage(byte[] packet, bool extraFlag, long arrivedAt)
    {
        if (packet[0] == 0x20)
        {
            return;
        }

        int offset = 0;
        VarIntOutput packetLengthInfo = PacketPrimitives.ReadVarInt(packet);
        if (packetLengthInfo.Length < 0)
        {
            return;
        }

        var pdp = new ParsedDamagePacket();
        offset += packetLengthInfo.Length;
        if (extraFlag)
        {
            offset += 1;
        }

        if (offset >= packet.Length) return;
        if (packet[offset] != 0x04) return;
        if (packet[offset + 1] != 0x38) return;
        offset += 2;
        if (offset >= packet.Length) return;

        VarIntOutput targetInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (targetInfo.Length < 0) return;
        pdp.TargetId = targetInfo.Value;
        offset += targetInfo.Length;
        if (offset >= packet.Length) return;

        VarIntOutput switchInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (switchInfo.Length < 0) return;
        pdp.SwitchVariable = switchInfo.Value;
        offset += switchInfo.Length;
        if (offset >= packet.Length) return;

        VarIntOutput flagInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (flagInfo.Length < 0) return;
        pdp.Flag = flagInfo.Value;
        offset += flagInfo.Length;
        if (offset >= packet.Length) return;

        VarIntOutput actorInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (actorInfo.Length < 0) return;
        pdp.ActorId = actorInfo.Value;
        offset += actorInfo.Length;
        if (offset >= packet.Length) return;

        if (offset + 5 >= packet.Length) return;

        int temp = offset;
        int skillCodeCandidate = PacketPrimitives.ParseUInt32Le(packet, offset);
        pdp.RawSkillCode = skillCodeCandidate;
        pdp.SkillCode = DamageParsing.NormalizeDamageSkillCode(skillCodeCandidate, (skillCodeCandidate / 10) * 10, _data.SkillExists);
        offset = temp + 5;

        VarIntOutput typeInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (typeInfo.Length < 0) return;
        pdp.Type = typeInfo.Value;
        offset += typeInfo.Length;
        if (offset >= packet.Length) return;

        int andResult = switchInfo.Value & Mask;
        int start = offset;
        // Size of the special-damage region that sits between the type field and the [power][damage]
        // varints, keyed by switchVariable & 0x0F. The 2026-07-01 patch grew the region for switch-type 6
        // by ONE byte (its fixed prefix went 2→3 bytes: pre `08 00 …` → post `8C 00 02 …`), so with the
        // old size 10 the parser stopped one byte short and read the REAL damage varint as the "power"
        // field and a much smaller field as the damage — undercounting every switch-6 hit (the dominant
        // hit type; ~80% of boss damage). 10→11 realigns it: verified against pre/post captures, per-target
        // credited damage then matches boss HP consumed within ~1% for four independent bosses.
        // (switch-types 5/7 never occur in the capture corpus — switchVariable & 0x0F is only ever 4 or 6 —
        // so their sizes are left as the ported defaults, unverifiable but never exercised.)
        int tempV = andResult switch
        {
            4 => 8,
            5 => 12,
            6 => 11,
            7 => 14,
            _ => -1,
        };
        if (tempV < 0) return;
        if (start + tempV > packet.Length) return;

        byte[] region = packet[start..(start + tempV)];
        pdp.Specials = DamageParsing.ParseSpecialDamageFlags(region);
        // Attack direction (후방/전방) — the position byte the 07-01 patch added at region offset [2].
        // Independent of the special-flag byte at [0], so it is read separately.
        pdp.Position = DamageParsing.ParsePosition(region);
        if (pdp.Specials.Contains(SpecialDamage.Restoration))
        {
            offset += 2;
        }

        offset += tempV;
        if (offset >= packet.Length) return;

        VarIntOutput unknownInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (unknownInfo.Length < 0) return;
        pdp.Unknown = unknownInfo.Value;
        offset += unknownInfo.Length;
        if (offset >= packet.Length) return;

        VarIntOutput damageInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (damageInfo.Length < 0) return;
        pdp.Damage = damageInfo.Value;
        offset += damageInfo.Length;
        if (offset >= packet.Length) return;

        // Multi-hit trailer: [count][count identical per-hit varints]. Read only to flag the hit as
        // multi-hit for the UI (pdp.Loop). It is NOT summed into pdp.Damage — the damage varint above is
        // already the full total for the hit (the repeats are a per-hit breakdown, not additional damage);
        // summing them double-counts (verified: adding the repeats overshoots boss HP consumed by ~20-25%).
        MultiHitOutput multiHitInfo = DamageParsing.TryParseMultiHit(packet, offset);
        pdp.Loop = multiHitInfo.Time;

        pdp.Timestamp = arrivedAt;
        if (pdp.ActorId == pdp.TargetId)
        {
            // 회생의 계약의 "생명력 10% 이하 즉시 회복"은 버프(0x382A/0x382B)로는 전혀 방송되지 않고 오직 이
            // self 프레임으로만 온다 — actor == target, damage varint = 회복량. 자가 프레임 자체는 각성/자원
            // 소모 등으로 매우 흔하므로(코퍼스 상위: 18160030 4.3만건, 11730007 2.2만건) 반드시 코드
            // 화이트리스트로만 통과시킨다. 회복이지 피해가 아니므로 SaveDamage로는 절대 넘기지 않는다.
            if (IsRevivalHealCode(pdp.SkillCode) || IsRevivalHealCode(pdp.RawSkillCode))
            {
                _data.SaveRevivalHeal(pdp.TargetId, pdp.SkillCode, pdp.Damage, arrivedAt);
                _sink.Damage("direct", pdp, false, "revival_heal", null);
                return;
            }

            _sink.Damage("direct", pdp, false, "actor_equals_target", null);
            return;
        }

        if (pdp.Damage >= 10000000)
        {
            _sink.Damage("direct", pdp, false, "damage_guard", null);
            return;
        }

        _data.SaveDamage(pdp, _data.CurrentEpoch());
        _sink.Damage("direct", pdp, true, null, null);
    }

    /// <summary>Damage-over-time (opcode 0x3805). Based on Kotlin parseDoTPacket (367-448); the
    /// post-2026-07-01 flag gate + sentinel handling diverge — see the inline notes below.</summary>
    private void ParseDoTPacket(byte[] packet, bool extraFlag, long arrivedAt)
    {
        int offset = 0;
        var pdp = new ParsedDamagePacket { Dot = true };
        VarIntOutput packetLengthInfo = PacketPrimitives.ReadVarInt(packet);
        if (packetLengthInfo.Length < 0) return;
        offset += packetLengthInfo.Length;
        if (extraFlag)
        {
            offset += 1;
        }

        if (packet[offset] != 0x05) return;
        if (packet[offset + 1] != 0x38) return;
        offset += 2;
        if (packet.Length < offset) return;

        VarIntOutput targetInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (targetInfo.Length < 0) return;
        offset += targetInfo.Length;
        if (packet.Length < offset) return;
        pdp.TargetId = targetInfo.Value;

        // The byte after the target is a DoT flag byte (0x00/0x08/0x09/0x0A/0x0B/0x30 observed). It is
        // consumed but NOT gated on: the previous `& 0x02` gate dropped ~1/3 of real DoT ticks (every
        // flag-0x08 tick and the non-self flag-0x09 ticks lack bit 0x02). DoT-ness is opcode-determined
        // (0x3805) — every tick's damage counts. Non-tick variants are filtered structurally instead: the
        // actor == target guard below drops the self-referential application/refresh frames (all flag 0x00
        // and 0x30, most 0x09/0x0B), and the damage sanity cap at the end drops the sentinel frames whose
        // damage field is 0xFFFFFFFF/0x7FFFFFFF (DoT apply/expire markers, not ticks).
        offset++;
        if (packet.Length < offset) return;

        VarIntOutput actorInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (actorInfo.Length < 0) return;
        if (actorInfo.Value == targetInfo.Value) return;
        offset += actorInfo.Length;
        if (packet.Length < offset) return;
        pdp.ActorId = actorInfo.Value;

        VarIntOutput unknownInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (unknownInfo.Length < 0) return;
        offset += unknownInfo.Length;

        int skillCodeCandidate = PacketPrimitives.ParseUInt32Le(packet, offset);
        pdp.SkillCode = DamageParsing.NormalizeDamageSkillCode(skillCodeCandidate, skillCodeCandidate / 100, _data.SkillExists);
        offset += 4;
        if (packet.Length <= offset) return;

        VarIntOutput damageInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (damageInfo.Length < 0) return;
        pdp.Damage = damageInfo.Value;

        // Same sanity cap as the direct-damage path: reject the sentinel/garbage frames (damage field
        // 0xFFFFFFFF → parses to a ~2.1B varint) that the removed flag gate no longer filters out.
        if (pdp.Damage >= 10000000)
        {
            _sink.Damage("dot", pdp, false, "damage_guard", null);
            return;
        }

        pdp.Timestamp = arrivedAt;
        _data.SaveDamage(pdp, _data.CurrentEpoch());
        _sink.Damage("dot", pdp, true, null, null);
    }

    /// <summary>
    /// 캐릭터 스탯 사전(0x364A 변경분 / 0x3649 전체 스냅샷).
    /// <code>
    /// 0x364A: [varint len][4A 36][varint entityId][varint count][count × (u16 LE statId, i32 LE value)][8B 꼬리]
    /// 0x3649: [varint len][49 36][00 00]         [varint count][count × (u16 LE statId, i32 LE value)][8B 꼬리]
    /// </code>
    /// <para>값은 <b>부호 있는</b> i32다(저항·감소 계열이 음수로 온다). 단위는 id마다 다르다 — 퍼센트 계열은
    /// basis point(값/100 = %)이고 나머지는 그대로 정수다. 어느 쪽인지는 값만 봐서는 알 수 없어
    /// <see cref="PlayerStatIds.IsPercent"/>가 id별로 선언한다.</para>
    /// <para><b>게이트</b>: <c>offset + count*6 + 8 == packet.Length</c> 로 프레임을 정확히 소진해야만 채택한다.
    /// 스탯 사전은 값의 모양으로는 검증할 수 없다(어떤 i32든 그럴듯하다) — 소진 검사가 유일한 방어선이다.
    /// count 상한 1024는 폭주한 varint가 거대한 루프를 돌지 못하게 막는다.</para>
    /// <para>0x3649는 엔티티 id를 싣지 않는다. 본인 캐릭터 전용이고, 수신 측이 <b>지금</b>의 본인 uid에 묶는
    /// 구조다 — 그래서 신원을 모르는 동안 도착하면 데이터 계층이 잠시 들고 있다가 신원이 확정될 때 반영한다
    /// (실측: 스탯 프레임이 본인 신원 패킷보다 약 6초 먼저 온다).</para>
    /// </summary>
    private void ParseStatSheet(byte[] packet, VarIntOutput lengthInfo, bool extraFlag, bool withEntityId)
    {
        try
        {
            int offset = lengthInfo.Length + (extraFlag ? 1 : 0) + 2;
            int entityId = 0;
            if (withEntityId)
            {
                if (offset >= packet.Length) return;
                VarIntOutput entity = PacketPrimitives.ReadVarInt(packet, offset);
                entityId = entity.Value;
                offset += entity.Length;
            }
            else
            {
                offset += 2; // 전체 스냅샷은 opcode 뒤에 00 00 두 바이트가 붙는다
            }

            if (offset >= packet.Length) return;
            VarIntOutput countInfo = PacketPrimitives.ReadVarInt(packet, offset);
            offset += countInfo.Length;
            int count = countInfo.Value;
            if (count is < 0 or > MaxStatSheetEntries)
            {
                _sink.ParserError("stat_sheet", "implausible count");
                return;
            }

            if (offset + (count * StatSheetEntryLength) + StatSheetTrailerLength != packet.Length)
            {
                _sink.ParserError("stat_sheet", "frame not exhausted");
                return;
            }

            // count 0 = "이번엔 바뀐 게 없다". 정상 프레임이므로 오류로 세지 않고 조용히 끝낸다 — 실측 코퍼스
            // 2,904 델타 프레임 중 40%가 이 모양이었고, 이걸 오류로 세면 파서가 고장난 것처럼 보인다.
            if (count == 0)
            {
                return;
            }

            var stats = new (int Stat, int Value)[count];
            for (int i = 0; i < count; i++)
            {
                stats[i] = (
                    packet[offset] | (packet[offset + 1] << 8),
                    (int)PacketPrimitives.ParseUInt32Le(packet, offset + 2));
                offset += StatSheetEntryLength;
            }

            _data.SaveStatSheet(entityId, stats, fullSnapshot: !withEntityId);
            _sink.Meta("stat_sheet", ("entity", entityId), ("count", count), ("full", !withEntityId));
        }
        catch
        {
            // swallowed — a short/garbage frame must never take the consumer down.
        }
    }

    /// <summary>스탯 사전 한 항목 = <c>[u16 statId][i32 value]</c>.</summary>
    private const int StatSheetEntryLength = 6;

    /// <summary>스탯 사전 프레임 꼬리(실측 8바이트). 소진 검사에 쓰인다.</summary>
    private const int StatSheetTrailerLength = 8;

    /// <summary>한 프레임이 실어 나를 수 있다고 보는 스탯 개수 상한. 실측 최대는 87개다 — 상한은 폭주한 varint가
    /// 거대한 루프를 돌지 못하게 막는 용도이지 정확한 경계가 아니다.</summary>
    private const int MaxStatSheetEntries = 1024;

    /// <summary>Own combat-power packet 0x3656 (was 0x3655). Kotlin parseOwnCombatPower (177-190).
    /// <para>본문은 실측 고정 레이아웃이다: <c>[u32 LE 현재 전투력][00 00 00 00][u32 LE 최고 전투력]
    /// [00 00 00 00]</c> — 프레임 전체로는 19바이트. 2026-08-17 코퍼스에서 dispatch된 0x3656 138프레임 중
    /// 134가 이 모양이었고, 나머지 4(길이 11 셋 · 5 하나)는 다른 모양이었다. 둘째 필드는 그 캐릭터의
    /// <b>최고 전투력</b>이다: 세션 내내 상수로 있다가 현재값이 그걸 넘는 순간 같이 올라간다(실측 108프레임
    /// 전부 <c>최고 ≥ 현재</c>, 동일 3건). 다만 그 대소를 게이트로 쓰지는 않는다 — 서버가 두 필드를 한
    /// 프레임 어긋나게 보내는 날 정상 전투력이 통째로 막힌다.</para>
    /// <para>🔑 이 파서는 <b>본인 전투력을 직접 갈아치우는 유일한 패킷 경로</b>인데, 종전에는
    /// "u32 하나를 읽을 만큼 길기만 하면" 통과였다 — 프레임 뒤 2바이트가 우연히 <c>56 36</c>인 아무
    /// 페이로드나(아웃바운드 암호문·루프백·압축 번들 내부 리싱크 오차) 본인 전투력이 될 수 있었다.
    /// 0x3633 본인 로드가 같은 형태의 구멍으로 신원을 탈취당한 전례가 있다
    /// (<c>OwnNicknameValidationTests</c>). 하필 0x3656 버스트는 새 게임 스트림 핸드셰이크와 <b>같은
    /// 밀리초</b>에 온다(실측 02:28:06.391·02:28:34.105에서 game_signal_first·dup_drop·own_combat_power가
    /// 동시각) — 얼라이너가 프레임 경계를 다시 잡는, 검증이 가장 필요한 순간이다.</para>
    /// <para>실측 사고: 2026-08-17 02:24:49 롭스티노 전투에서 본인 전투력이 356,559 대신
    /// <b>2,285,1xx</b>로 떴다. ⚠️ 그 프레임 자체는 어디에도 안 남았다 — 패킷 로깅이 그 전투가 끝나고
    /// 28초 뒤(02:25:54)에 시작됐다. 근거는 소거법이다: 재진입 직후(02:26:09·02:26:11)부터 0x3656 134프레임·
    /// 0x9702 로스터 42스냅샷·공식 사이트가 전부 356,559 / 340,370을 말했고 11.5시간 동안 100만을 넘긴
    /// 표본이 0건인데, executor의 <c>User.Power</c>에 값을 쓸 수 있는 경로는 이 파서뿐이다(0x3645는
    /// '주변 남' 스냅샷이라 실측 540건 중 executor 0건, 0x9702 스캔은 <c>User.Power</c>에 닿지 않는다).
    /// 값이 표시층이 아니라 데이터 계층에 있었다는 건 티어 배지가 증언한다 — <c>TierLadder.MinPower</c>가
    /// 40만이라 356,559로는 배지가 뜰 수 없다. 그 값은 저장 전투에 얼어붙고 통계 업로드까지 나갔다.</para>
    /// <para>그래서 값이 아니라 <b>모양</b>을 검사한다: 두 u32와 그 사이·뒤의 0 패딩 8바이트를 전부 요구하고,
    /// 두 값 모두 <see cref="CombatPower.IsPlausible"/>을 통과해야 한다. 우연히 이걸 다 만족하는 난수
    /// 프레임은 사실상 없다. 길이는 "16바이트 본문이 있는가"로만 보고 <b>정확히 19</b>는 요구하지 않는다 —
    /// 서버가 뒤에 필드를 덧붙여도 조용히 죽지 않게. 모양이 안 맞으면 흔적을 남긴다(레이아웃이 바뀌면
    /// 전투력이 조용히 0이 되는 대신 로그로 보인다).</para></summary>
    private void ParseOwnCombatPower(byte[] packet, VarIntOutput lengthInfo, bool extraFlag, long arrivedAt)
    {
        try
        {
            int opcodeOffset = lengthInfo.Length + (extraFlag ? 1 : 0);
            int valueOffset = opcodeOffset + 2;
            if (valueOffset + OwnCombatPowerBodyLength > packet.Length)
            {
                _sink.ParserError("own_combat_power", "body too short");
                return;
            }

            long power = PacketPrimitives.ReadUInt32LeAsLong(packet, valueOffset);
            long second = PacketPrimitives.ReadUInt32LeAsLong(packet, valueOffset + 8);
            if (!IsZeroRun(packet, valueOffset + 4, 4) || !IsZeroRun(packet, valueOffset + 12, 4))
            {
                _sink.ParserError("own_combat_power", "padding mismatch");
                return;
            }

            if (!CombatPower.IsPlausible(power) || !CombatPower.IsPlausible(second))
            {
                _sink.ParserError("own_combat_power", "implausible value");
                return;
            }

            int executor = _executorId;
            if (executor <= 0) return;
            _lastOwnPower = (int)power; // remember for carry-forward onto re-entry uids (req 3)
            _data.SaveUserPower(executor, (int)power);
            _sink.Meta("own_combat_power", ("uid", executor), ("power", (int)power));
        }
        catch
        {
            // swallowed (matches Kotlin)
        }
    }

    /// <summary>0x3656 본문 크기: <c>[u32][0 4바이트][u32][0 4바이트]</c>.</summary>
    private const int OwnCombatPowerBodyLength = 16;

    private static bool IsZeroRun(byte[] packet, int offset, int count)
    {
        for (int i = offset; i < offset + count; i++)
        {
            if (packet[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    // ---- party join-request handlers (Kotlin parseJoinRequest/CancelJoin/AdmitJoin/RefuseJoin/
    //      InstanceStart/ExitParty). Skill enrichment (official lookup) + rememberUserPower are deferred;
    //      the join UI carries nickname/server/job/power from the packet itself. ----

    /// <summary>0x9707: a party-join applicant. Byte layout corpus-verified.</summary>
    private void ParseJoinRequest(byte[] packet, VarIntOutput lengthInfo, bool extraFlag, long arrivedAt)
    {
        int offset = lengthInfo.Length;
        if (extraFlag) offset++;
        if (packet.Length < offset + 2) return;
        if (packet[offset] != 0x07 || packet[offset + 1] != 0x97) return;
        offset += 2;

        _ = PacketPrimitives.ParseUInt32Le(packet, offset); offset += 4; // roomNum (unused, parity)
        int requester = PacketPrimitives.ParseUInt32Le(packet, offset); offset += 4;
        _ = PacketPrimitives.ParseUInt32Le(packet, offset); offset += 4; // unknown2
        int jobCode = PacketPrimitives.ParseUInt32Le(packet, offset); offset += 4;
        _ = PacketPrimitives.ParseUInt32Le(packet, offset); offset += 4; // unknown4
        _ = PacketPrimitives.ParseUInt32Le(packet, offset); offset += 4; // unknown5

        VarIntOutput nameLen = PacketPrimitives.ReadVarInt(packet, offset);
        offset += nameLen.Length;
        // The 0x9707 dispatch keys off just two bytes (07 97); a mis-assembled/corrupt frame that happens to
        // carry them reaches here and reads RANDOM bytes as the applicant name — that surfaced as a phantom
        // party-join card with a mojibake name while the user stood idle (실측 2026-07-27). Guard the trust
        // boundary: an applicant name is a short, clean UTF-8 string, so reject an implausible length or a name
        // that isn't text (invalid UTF-8 decodes to U+FFFD; real nicknames never carry control chars) instead of
        // emitting a bogus request (which would also fire an official-site lookup on the garbage nickname).
        if (nameLen.Value is <= 0 or > 48 || offset + nameLen.Value > packet.Length)
        {
            _sink.ParserError("join_request", "implausible name length");
            return;
        }

        string nickname = Encoding.UTF8.GetString(packet, offset, nameLen.Value);
        offset += nameLen.Value;
        if (nickname.Contains('�') || nickname.Any(c => c < ' '))
        {
            _sink.ParserError("join_request", "non-text nickname");
            return;
        }

        int server = PacketPrimitives.ParseUInt16Le(packet, offset);
        offset += 6; // Kotlin skips 6 after reading the 2-byte server
        int power = PacketPrimitives.ParseUInt32Le(packet, offset);

        _sink.Meta("join_request", ("requester", requester), ("nickname", nickname), ("server", server), ("job", jobCode), ("power", power));
        _joinSink.OnJoinRequest(requester, nickname, jobCode, server, power, arrivedAt);
    }

    /// <summary>0x9725 cancel — 신청자가 스스로 물렀다. uid가 opcode 바로 뒤에 온다.</summary>
    /// <remarks>실측 프레임(2026-09-12): <c>0E 25 97 AB 33 00 00 00 00 D3 07</c> — uid 0x33AB=13227,
    /// 뒤이어 <c>00 00</c>과 서버 2003. 같은 세션의 0x9707 신청 기록과 uid·서버가 정확히 일치한다.</remarks>
    private void ParseCancelJoin(byte[] packet, VarIntOutput lengthInfo, bool extraFlag) =>
        RemoveByRequester(packet, lengthInfo, extraFlag, 0x25, uidSkip: 0, admit: false);

    /// <summary>0x970B — 파티에 멤버가 추가됐다는 브로드캐스트. 신청 수락이 그 중 하나다.</summary>
    /// <remarks>
    /// <para><b>종전에는 uid를 2바이트 앞에서 읽었다.</b> 옛 주석이 그 사실을 자백해 뒀고("admit emits a
    /// non-matching id and the card is NOT removed on accept; it instead expires via the 20s timeout"),
    /// 그게 곧 "인게임에서 수락했는데 카드가 20초 내내 남는다"는 제보의 정체였다. 실측 레이아웃은
    /// <c>[len][0B 97][flag][slot][uid u32 LE][00 00][server u16][nameLen][name UTF-8]…</c> 이고,
    /// 2026-09-12 세션의 수락 10건이 모두 직전 0x9707의 uid·서버·닉네임과 일치한다
    /// (예: <c>34 0B 97 0C 04 EA 07 00 00 00 00 E2 07 06 …데드</c> = uid 2026 / 서버 2018).</para>
    /// <para><b>flag 바이트는 열거하지 않는다.</b> 수락은 0x0C, 이미 파티에 있는 멤버를 같은 ms에 2~3발씩
    /// 다시 싣는 열거 브로드캐스트는 0x0E, 2026-06 코퍼스에는 0x3A도 있었다. 열거형이 실어 오는 uid는 이미
    /// 파티원이라 대기 카드가 있을 수 없으므로 제거는 무해한 no-op이다 — 다만 저장소는 만료된 신청을 배지
    /// 승계용으로 계속 들고 있으므로, 그 유령까지 '수락'으로 세지 않도록 짝짓기는 <b>화면에 살아 있던
    /// 카드</b>에만 걸린다(<c>JoinRequestStore.Remove</c>).</para>
    /// <para>수락 10건 중 9건은 직전 0x9707 과 uid·서버·닉네임이 일치했고, 1건은 선행 신청도 0x9709 도 없이
    /// 왔다(초대·파티찾기로 들어온 멤버로 보인다) — 그래서 '수락 = 신청 해소'로 단정하지 않는다.</para>
    /// </remarks>
    private void ParseAdmitJoin(byte[] packet, VarIntOutput lengthInfo, bool extraFlag) =>
        RemoveByRequester(packet, lengthInfo, extraFlag, 0x0B, uidSkip: 2, admit: true);

    /// <summary>
    /// <paramref name="uidSkip"/> = uid 앞에 붙는 바이트 수(0x970B의 <c>[flag][slot]</c>).
    /// <para><b>모양 게이트가 필수다.</b> 이 계열은 opcode 2바이트만 맞으면 실행되므로, 게임이 아닌 LAN
    /// 스트림(스트리밍·프록시)의 난수 프레임이 우연히 <c>0B 97</c>·<c>25 97</c>을 만들면 쓰레기 id로 카드를
    /// 지운다. 진짜 프레임은 uid 뒤에 <c>00 00</c>과 서버 id가 반드시 따라오므로 그 모양으로 거른다 —
    /// 코퍼스 8세션 재현에서 이 게이트가 제거 계열 노이즈 15건(길이 13 · 패드 2)을 걸렀고, 진짜 프레임은
    /// 한 건도 걸리지 않았다.</para>
    /// </summary>
    private void RemoveByRequester(byte[] packet, VarIntOutput lengthInfo, bool extraFlag, byte opcodeLow, int uidSkip, bool admit)
    {
        int offset = lengthInfo.Length;
        if (extraFlag) offset++;
        if (packet.Length < offset + 2) return;
        if (packet[offset] != opcodeLow || packet[offset + 1] != 0x97) return;
        offset += 2 + uidSkip;
        if (packet.Length < offset + 8) return; // uid u32 + 00 00 + server u16

        int requester = PacketPrimitives.ParseUInt32Le(packet, offset);
        if (requester <= 0) return;
        if (packet[offset + 4] != 0x00 || packet[offset + 5] != 0x00) return;
        if (!IsPlausibleServerBand(PacketPrimitives.ParseUInt16Le(packet, offset + 6))) return;

        // 이름이 "admit"이 아니라 "member_add"인 이유: 0x970B 는 신청 수락 전용이 아니라 파티에 멤버가
        // 붙었다는 브로드캐스트다. 실측상 30건 중 수락은 9~10건이고 나머지는 기존 멤버를 같은 ms 에 2~3발씩
        // 다시 싣는 열거다 — 진단에서 이걸 '수락'으로 세면 3배로 부풀어 보인다.
        _sink.Meta(admit ? "join_member_add" : "join_cancel", ("requester", requester));
        _joinSink.OnJoinRequestRemove(requester, admit);
    }

    /// <summary>0x9709 — 대기 중이던 신청 하나가 해소됐다. <b>id가 없고, 거절뿐 아니라 수락에도 온다</b>
    /// (수락이면 같은 배치로 0x970B가 따라붙는다). 그래서 즉시 "가장 오래된 것"을 지우면 안 된다 — 파티장이
    /// 나중 신청을 먼저 수락한 순간 애먼 카드가 사라진다. 짝이 될 0x970B를 잠깐 기다렸다가 남은 것만
    /// 해소하는 판정은 <c>JoinRequestStore</c>가 한다.</summary>
    /// <remarks>진짜 프레임은 예외 없이 5바이트다(<c>08 09 97 00 00</c>, 마지막 바이트가 0이 아닌 변종 1건
    /// 포함). 코퍼스 8세션 재현에서 길이가 다른 노이즈 19건(7·11·41·45·49바이트)이 여기서 걸렸고 진짜
    /// 프레임은 한 건도 안 걸렸다.</remarks>
    private void ParseRefuseJoin(byte[] packet, VarIntOutput lengthInfo, bool extraFlag)
    {
        int offset = lengthInfo.Length;
        if (extraFlag) offset++;
        if (packet.Length != offset + 4) return; // opcode 2 + payload 2 — 그 밖은 노이즈다
        if (packet[offset] != 0x09 || packet[offset + 1] != 0x97) return;
        _joinSink.OnRefuseJoinRequest();
    }

    /// <summary>0x9718 instance-start / 0x971D exit-party — clear all pending requests.</summary>
    private void ParseInstanceStart(byte[] packet, VarIntOutput lengthInfo, bool extraFlag) =>
        ClearOnOpcode(packet, lengthInfo, extraFlag, 0x18);

    private void ParseExitParty(byte[] packet, VarIntOutput lengthInfo, bool extraFlag) =>
        ClearOnOpcode(packet, lengthInfo, extraFlag, 0x1D);

    private void ClearOnOpcode(byte[] packet, VarIntOutput lengthInfo, bool extraFlag, byte opcodeLow)
    {
        int offset = lengthInfo.Length;
        if (extraFlag) offset++;
        // 0x9709과 같은 이유의 길이 게이트 — 진짜 프레임은 5바이트뿐인데(08 18 97 00 00 / 08 1D 97 00 00)
        // 종전에는 opcode 2바이트만 보고 패널을 통째로 비웠다. 코퍼스 8세션 재현에서 이 길이 검사가 거른
        // 난수 프레임이 0x9718 12건 · 0x971D 9건이다(전부 길이 7·11·31·45).
        if (packet.Length != offset + 4) return;
        if (packet[offset] != opcodeLow || packet[offset + 1] != 0x97) return;
        // NOTE: deliberately does NOT clear the party roster. 0x971D fires spuriously mid-dungeon (observed while
        // a 5-man party was fully intact), so clearing here would empty a valid roster — the exact failure the
        // subset-ignore in SavePartyRoster is meant to prevent. A real party change is handled by a replacing
        // 0x9702 snapshot, roster staleness, and the resets.
        _joinSink.OnExitPartyUi();
    }

    /// <summary>0x9702 full party/raid roster snapshot. Every member is encoded as a
    /// [serverId u16 LE][nameLen u8][name UTF-8] record (RE'd against a live party-join capture: the roster
    /// grows 2→3→4 as members join). Scan the packet for those records — gated on a valid server + a
    /// plausible name — and hand the whole set to the data layer, which matches each member to a known uid
    /// (by name+server, the same identity our 0x3645/0x3633 snapshots carry) for the pre-combat party
    /// preview. A full snapshot, so it REPLACES the roster.</summary>
    private void ParsePartyRoster(byte[] packet, VarIntOutput lengthInfo, bool extraFlag)
    {
        int offset = lengthInfo.Length + (extraFlag ? 1 : 0);
        if (offset + 2 > packet.Length || packet[offset] != 0x02 || packet[offset + 1] != 0x97)
        {
            return;
        }

        var members = new List<(string Nickname, int Server, int Slot)>();
        var jobPower = new List<(string Nickname, int Server, int JobCode, int Power)>(); // 0x9702가 실어 온 직업·전투력 (프리뷰 채움용)
        var seen = new HashSet<string>();
        int headerless = 0; // [server][len][name] shapes with no record header in front of them
        for (int n = offset + 2; n + 3 < packet.Length; n++)
        {
            int server = PacketPrimitives.ParseUInt16Le(packet, n);
            if (!IsPartyServer(server))
            {
                continue;
            }

            int len = packet[n + 2] & 0xFF;
            if (len < 1 || len > 30 || n + 3 + len > packet.Length)
            {
                continue;
            }

            string name = Encoding.UTF8.GetString(packet, n + 3, len);
            if (Encoding.UTF8.GetByteCount(name) != len || !IsValidNickname(name))
            {
                continue;
            }

            // A real member is always preceded by its record header. A [server][len][name] shape with NO
            // header in front of it is a coincidence inside some other field, and adopting it invents a party
            // member who does not exist. Measured: a member's own 전투력 u32 supplies exactly that shape — a
            // 5-인 party's roster parsed as SIX because 끕's power (413042) ends in byte 0x72, and "r" is a
            // legal one-letter name that IsValidNickname accepts. Outside 성역 a party caps at five, so that 6
            // went to the stats site as battle.rosterSize, and SavePartyRoster's anti-shrink guard then held it
            // for the next ten and a half minutes of that dungeon — every correct 5-member snapshot after it
            // looked like a subset and was ignored.
            //
            // Rejected BEFORE seen.Add on purpose: the phantom must reach neither the member list nor the
            // dedupe set.
            int slot = MemberSlot(packet, n);
            if (slot == 0)
            {
                headerless++;
                continue;
            }

            if (seen.Add(name + " " + server.ToString(CultureInfo.InvariantCulture)))
            {
                // 0x9702는 이름 뒤에 직업(job u32 LE 하위바이트 @ nameEnd)·전투력(0x04 마커 뒤 u32 LE)도 싣는다 —
                // 전투 전 파티 프리뷰의 직업 아이콘·전투력 채움용(0x3645는 근접 멤버만 와 로스터 전원 못 채움).
                // power는 고정 오프셋이 아니라 "뒤 u32가 [1,1000만]인 0x04 마커"를 좁은 창에서 스캔(레코드별 여분
                // 1바이트 때문에 +17 고정은 일부 power를 오독).
                int nameEnd = n + 3 + len;
                int jobCode = nameEnd < packet.Length ? packet[nameEnd] : 0;
                int power = 0;
                int scanEnd = nameEnd + 24;
                if (scanEnd > packet.Length - 5)
                {
                    scanEnd = packet.Length - 5;
                }

                for (int k = nameEnd + 4; k <= scanEnd; k++)
                {
                    if (packet[k] == 0x04)
                    {
                        long pw = PacketPrimitives.ReadUInt32LeAsLong(packet, k + 1);
                        if (CombatPower.IsPlausible(pw))
                        {
                            power = (int)pw;
                            break;
                        }
                    }
                }

                members.Add((name, server, slot));
                jobPower.Add((name, server, jobCode, power));
            }

            n += 2 + len; // skip past this record (the loop's n++ steps over the final byte)
        }

        // The header gate above fails CLOSED: if the game ever changes the record stride, every member is
        // rejected, this returns, and SavePartyRoster is never called — so the roster silently freezes on its
        // last value instead of emptying. That is the safer failure, but it is invisible, so say so out loud.
        // A healthy snapshot rejects zero or one shape (the trailing 전투력 coincidence); a run of these with
        // no members kept is the signature of a layout change.
        if (headerless > 0)
        {
            _sink.Meta("party_roster_headerless", ("dropped", headerless), ("kept", members.Count));
        }

        if (members.Count == 0)
        {
            return;
        }

        // Snapshot coherence: two members cannot hold the same slot. If they appear to, the header layout has
        // drifted (a new marker byte, a changed stride) and every slot in this snapshot is suspect — so drop
        // them all rather than hand out an assignment that is confidently wrong. A wrong sub-party is worse
        // than none: the site stores what it is told, and nothing downstream can tell the two apart.
        var claimed = new HashSet<int>();
        bool duplicate = members.Any(m => m.Slot > 0 && !claimed.Add(m.Slot));
        if (duplicate)
        {
            _sink.Meta("party_roster_slot_conflict", ("count", members.Count));
            members = members.Select(m => (m.Nickname, m.Server, Slot: 0)).ToList();
        }

        // The u32 that opens the body is the server's party id. It is the only thing in this packet that says
        // WHICH party these members are — a join or a leave keeps it, re-forming the group changes it —
        // which is what lets the data layer tell "my roster, minus someone" from "a different, smaller
        // party". Measured over the corpus: a snapshot whose member set is a swap of, or disjoint from, the
        // previous one ALWAYS carries a different id (15 and 7 cases, zero exceptions), while an identical
        // member set always carries the same one (2,251 cases).
        int partyId = offset + 6 <= packet.Length ? (int)PacketPrimitives.ReadUInt32LeAsLong(packet, offset + 2) : 0;
        _data.SavePartyRoster(members, partyId);
        _data.SavePartyRosterJobPower(jobPower);

        var sb = new StringBuilder();
        foreach ((string nick, int srv, int slot) in members)
        {
            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(nick).Append('[').Append(srv);
            if (slot > 0)
            {
                sb.Append('#').Append(slot);
            }

            sb.Append(']');
        }

        _sink.Meta("party_roster", ("count", members.Count), ("members", sb.ToString()));
    }

    // Sub-group slot (1-8) for a 0x9702 member, read from the fixed-width record header preceding the matched
    // server: [marker][slot 1-8][handle 6-byte LE][server u16]. The handle is a SIX-byte field, so the slot
    // byte is at serverOffset-7 and the record marker at serverOffset-8. Slots 1-4 = party 1, 5-8 = party 2 for
    // an 8-인 공대 (see DataManager.CurrentPartySlots). The marker is 0x7A/0x7E for an existing member and 0x3A
    // for one that just joined this snapshot. 0 = header didn't match (slot unknown).
    //
    // The earlier guard required packet[serverOffset-4..-1] == 00 00 00 00, but those four bytes are the HIGH
    // four bytes of the 6-byte handle — only zero when the handle < 0x10000. Most real members have a larger
    // handle, so that test dropped them to slot 0 (production captures: it resolved only ~54% of members, so an
    // 8-인 공대 never reached a full 1-8 set and the stats web's sub-party split stayed off). Anchoring on the
    // marker instead recovers every member; the byte-scan has already validated the server+name at serverOffset.
    // 2026-07-01 patch raised party 4→5 and raid 8→10 (two parties of 5), so slots now span 1-10.
    //
    // ⚠️ The marker byte is NOT enumerated, deliberately. It was — 0x7A/0x7E/0x3A, then 0x3E was added after it
    // cost a 10-인 공대 its whole first party — and each list was a guess that the next capture falsified. The
    // corpus now shows at least NINE values (0x1C 0x1E 0x38 0x3A 0x3C 0x3E 0x5E 0x7A 0x7E), and in a
    // 2026-08-07 session 0x3C alone carried 122 of 957 members, i.e. an enumeration written today would lose
    // 13.9% of sub-party slots tomorrow. So the marker is only required to be NON-ZERO (every observed value is
    // >= 0x1C; the byte is 0x00 where no record header exists) and the real test is the slot byte itself.
    //
    // That slot byte is also what separates a record from a coincidence. Measured over 1,385 members in four
    // sessions: every real member has packet[serverOffset-7] in 1..10, and the ONLY value outside it is the
    // phantom member the scan used to invent out of a 전투력 u32 (see the adoption gate in ParsePartyRoster).
    private static int MemberSlot(byte[] packet, int serverOffset) =>
        serverOffset >= 8
        && packet[serverOffset - 8] != 0x00
        && packet[serverOffset - 7] is >= 1 and <= 10
            ? packet[serverOffset - 7]
            : 0;

    /// <summary>0x9200 멤버 프로필 스냅샷. 파티/공대 멤버마다 한 레코드씩 싣는데, <b>엔티티 uid를 이름과 같은
    /// 레코드에 담는 유일한 브로드캐스트</b>다(0x9702 로스터에는 uid가 없다). 이름 오프셋 기준 레이아웃 —
    /// <c>-54 uid u32LE / -50 server u16LE / -48 ? / -46 0x24 / -45..-10 36바이트 ASCII GUID / -9..-4 handle /
    /// -3 server u16LE / -1 nameLen u8 / 0 name UTF-8</c> (실제 캡처 프레임에서 검증: uid 15360 · '플러시' · 2003).
    /// <para>0x9702 파서와 같은 byte-scan이되 구조 검증이 훨씬 빡빡하다: GUID 길이 마커 0x24 + 36바이트 ASCII
    /// GUID + 두 곳의 server 일치. 실측 특이도 — 한 세션에서 이름 바이트 히트 ~40건 중 이 검증을 통과한 건
    /// 정확히 1건(진짜 레코드)이었다. 앵커가 잘못 발화하면 본인이 엉뚱한 uid로 옮겨가므로 느슨한 매칭은
    /// 허용하지 않는다.</para>
    /// <para><b>모든</b> 레코드를 데이터 계층으로 넘긴다. "현재 본인과 신원 완전일치인가"는 executor를 아는
    /// 쪽만 판단할 수 있고 남의 레코드는 거기서 그대로 버려진다 — 여기서 본인을 골라내려 하면 uid↔이름 결합이
    /// 틀렸을 때 남의 이름을 저장소에 쓰게 된다. 그래서 <see cref="ICaptureGameData.SaveNickname"/>으로는
    /// 절대 흘리지 않는다(그 경로는 이름이 바뀌면 직업·전투력 출처를 리셋한다).</para></summary>
    private void ParseMemberProfile(byte[] packet, VarIntOutput lengthInfo, bool extraFlag)
    {
        int offset = lengthInfo.Length + (extraFlag ? 1 : 0);
        if (offset + 2 > packet.Length || packet[offset] != 0x00 || packet[offset + 1] != 0x92)
        {
            return;
        }

        var sb = new StringBuilder();
        int found = 0;
        for (int n = offset + 2; n + 3 < packet.Length; n++)
        {
            if (n < 51)
            {
                continue; // uid 필드(이름 기준 -54 = n-51)가 패킷 앞을 벗어난다
            }

            int server = PacketPrimitives.ParseUInt16Le(packet, n);
            if (!IsPartyServer(server))
            {
                continue;
            }

            int len = packet[n + 2] & 0xFF;
            if (len < 1 || len > 30 || n + 3 + len > packet.Length)
            {
                continue;
            }

            string name = Encoding.UTF8.GetString(packet, n + 3, len);
            if (Encoding.UTF8.GetByteCount(name) != len || !IsValidNickname(name))
            {
                continue;
            }

            if (packet[n - 43] != 0x24                                     // GUID 길이 마커 (이름 기준 -46)
                || !IsAsciiGuid(packet, n - 42)                            // 36바이트 ASCII GUID
                || PacketPrimitives.ParseUInt16Le(packet, n - 47) != server) // 앞쪽 server가 같아야 한다
            {
                continue;
            }

            int uid = PacketPrimitives.ParseUInt32Le(packet, n - 51);
            _data.TryBindExecutorByIdentity(uid, name, server);
            // 같은 레코드가 실어 온 (uid, 이름, 서버)를 표시-계층 보조 로스터에도 넣는다 — 0x9702 유실 시
            // 로스터 폴백 + 무명 파티원 전투행의 uid 직접 명명(구조검증을 통과한 레코드만 여기 도달한다).
            _data.SaveMemberProfile(uid, name, server);
            found++;
            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(name).Append('[').Append(server).Append("]#").Append(uid);

            n += 2 + len; // 이 레코드를 건너뛴다 (루프의 n++이 마지막 바이트를 넘긴다)
        }

        if (found > 0)
        {
            _sink.Meta("member_profile", ("count", found), ("members", sb.ToString()));
        }
    }

    /// <summary>36바이트 정규 GUID 문자열(8-4-4-4-12, 대문자/소문자 hex + '-')인지. 0x9200 레코드 구조 검증의
    /// 핵심 — 임의의 바이트 런이 이걸 통과할 확률은 사실상 0이라, 이름-모양 오탐을 여기서 전부 걷어낸다.</summary>
    private static bool IsAsciiGuid(byte[] packet, int offset)
    {
        if (offset < 0 || offset + 36 > packet.Length)
        {
            return false;
        }

        for (int i = 0; i < 36; i++)
        {
            byte c = packet[offset + i];
            bool dash = i is 8 or 13 or 18 or 23;
            if (dash)
            {
                if (c != (byte)'-')
                {
                    return false;
                }

                continue;
            }

            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex)
            {
                return false;
            }
        }

        return true;
    }

    // Valid Aion2 server-id range for a party member record (same range our nickname snapshots use, so a
    // matched member's server lines up with its 0x3645/0x3633 identity). Tight enough to reject the random
    // [server][len][name]-shaped byte runs that would otherwise false-match inside the packet body.
    private static bool IsPartyServer(int server) =>
        (server is >= 1001 and <= 1021) || (server is >= 2001 and <= 2021);

    /// <summary>같은 두 대역이되 <b>상한을 열어 둔</b> 서버 검사. 신청 카드를 지우는 경로에만 쓴다.
    /// <para><see cref="IsPartyServer"/>의 상한(1021·2021)은 현재 운영 중인 마지막 서버다 — 2021(이스할겐)이
    /// 2026-08-12 라이브 패치로 들어왔다. 그걸 <b>제거</b> 경로에 그대로 쓰면 비대칭이 생긴다: 신규 서버
    /// 유저의 신청은 카드로 <b>뜨지만</b>(추가 경로 0x9707 에는 서버 검사가 없다) 수락해도 그 카드가 안
    /// 지워지고, 대신 짝을 못 찾은 0x9709 가 애먼 카드를 지운다. 증설이 곧 그 버그의 배포다.</para>
    /// <para>느슨하게 해도 잃는 게 없다는 건 실측으로 확인했다 — 코퍼스 8세션에서 이 검사가 탈락시킨
    /// 프레임은 <b>0건</b>이고, 노이즈는 그 앞의 길이·패드(<c>00 00</c>) 검사가 전부 잡았다.</para></summary>
    private static bool IsPlausibleServerBand(int server) =>
        (server is >= 1001 and < 2000) || (server is >= 2001 and < 3000);

    /// <summary>엔티티 id 상한. <c>DataManager.MaxEntityUid</c>와 같은 값이지만 Capture는 Data를 참조하지
    /// 않으므로(의존 방향이 Data → Capture다) 여기에 둔다. 오프셋을 잘못 잡은 varint는 곧바로 이 범위를
    /// 벗어나므로(실측 106900) 신원 파싱의 1차 sanity가 된다. 코퍼스 실측 최대 정상 본인 uid = 15510.</summary>
    private const int MaxEntityUid = 16383;

    /// <summary>Own nickname snapshot 0x3633. Kotlin searchOwnNickname (192-250).</summary>
    private void SearchOwnNickname(byte[] packet, VarIntOutput lengthInfo, long arrivedAt)
    {
        int offset = lengthInfo.Length;
        if (packet[offset] != 0x33) return;
        if (packet[offset + 1] != 0x36) return;
        offset += 2;
        if (packet.Length < offset) return;

        VarIntOutput userInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (userInfo.Length < 0) return;

        // 이 파서는 미터에서 **유일하게 executor 포인터를 직접 갈아치우는** 경로다(SaveNickname isExecutor:true
        // → SaveExecutorId). 그래서 신원 파서 중 유일하게 fail-closed여야 하는데, 종전에는 uid·서버·닉네임을
        // 하나도 검증하지 않는 유일한 경로이기도 했다. 캡처는 방향 제한이 없어(WinDivertBackend) 클라→서버
        // 아웃바운드(암호문 = 사실상 난수)와 루프백까지 같은 파이프라인에 들어오는데, 길이 varint는 평문이라
        // 난수 페이로드도 정상 프레이밍되고 그 뒤 2바이트가 우연히 33 36이면 그대로 여기로 온다.
        // 실측: 2026-07-28 코퍼스의 아웃바운드 프레임 `0E 33 36 94 C3 06 D0 60 01 51 0C`가 uid=106900,
        // nickname="Q"로 파싱됐고, 2026-07-30에는 같은 형태가 nickname="I" / server=47200(0xB860)을 본인으로
        // 심어 로스터·오드·버프 초기화 + 통계 동의 모달 재출현 + 업로드 차단까지 갔다.
        // 엔티티 id 상한부터 건다(오프셋 오독은 varint를 폭주시켜 곧바로 상한을 넘는다 — 실측 106900).
        if (userInfo.Value is <= 0 or > MaxEntityUid) return;
        offset += userInfo.Length;
        if (offset >= packet.Length) return;

        // Locate the own nickname. The pre-2026-07-01 own-load packet placed a 0x07 spliter in the 10 bytes
        // after the uid, immediately before the [varint len][nickname]. The 2026-07-01 patch DROPPED the
        // 0x07 spliter (a fixed 5-byte prefix now precedes the name length), so anchoring on 0x07 returns -1
        // and the own character is never recognized (root cause of "내 캐릭터를 인식 못함"). Probe the next few
        // offsets for a valid [varint len][UTF-8 nickname] instead — the same robust approach as
        // SearchOtherNickname — which handles both the old (0x07) and new (fixed-prefix) layouts.
        string? nickname = null;
        int server = -1;
        int job = -1;
        bool sawNameWithoutServer = false;
        for (int probe = 0; probe < 12; probe++)
        {
            int p = offset + probe;
            if (p >= packet.Length) break;
            VarIntOutput nl = PacketPrimitives.ReadVarInt(packet, p);
            if (nl.Length is <= 0 or > 71) continue;
            // 길이 1은 정의상 ASCII 한 글자다 — 한글은 UTF-8 3바이트라 정상 닉의 최소 길이는 3이고, 실측
            // 오탐은 전부 이 형태였다("I", "Q"). IsValidNickname 자체는 손대지 않는다: 타인 닉 경로엔 1글자
            // 한글 닉('벵','쭌')이 실재하고, 그건 UTF-8 3바이트라 여기 하한에 걸리지 않는다.
            if (nl.Value is < 2 or > 71) continue;
            int q = p + nl.Length;
            if (q + nl.Value > packet.Length) continue;
            string candidate = Encoding.UTF8.GetString(packet[q..(q + nl.Value)]);
            if (!IsValidNickname(candidate)) continue;

            // 서버 검증을 **후보 채택 조건으로** 끌어올린다. 종전에는 이름을 먼저 확정하고 그 뒤 2바이트를
            // 무검증으로 읽어 server에 넣어서(0xB860 = 47200이 그대로 통과), 이름 앵커가 틀린 프레임도
            // executor를 뒤집었다. 타인 닉 파서(SearchOtherNickname)는 처음부터 이 범위를 검증하고 있었다 —
            // 판돈이 가장 큰 본인 경로에만 빠져 있던 게 이 버그의 린치핀이다.
            int nameEnd = q + nl.Value;
            if (nameEnd + 2 > packet.Length)
            {
                sawNameWithoutServer = true;
                continue;
            }

            int serverCandidate = PacketPrimitives.ParseUInt16Le(packet, nameEnd);
            if (!IsPartyServer(serverCandidate))
            {
                sawNameWithoutServer = true;
                continue;
            }

            nickname = candidate;
            server = serverCandidate;
            job = nameEnd + 2 < packet.Length ? packet[nameEnd + 2] & 0xFF : -1;
            break;
        }

        if (nickname is null)
        {
            // 2026-07-01 패치가 이미 한 번 본인 로드 레이아웃을 바꿔 본인 인식을 통째로 깨뜨린 적이 있다
            // (위 주석 참조). 그때처럼 서버가 필드를 옮기면 이 게이트가 조용히 본인 인식을 막게 되므로,
            // "이름은 찾았는데 그 뒤가 유효 서버가 아니다"를 흔적으로 남겨 다음 패치 때 즉시 보이게 한다.
            if (sawNameWithoutServer)
            {
                _sink.ParserError("own_nickname", "name candidate found but no valid server followed");
            }

            return;
        }

        // req 3 fix: own power comes from the live 0x3656 packet, which is keyed to the executor uid. On
        // re-entry the executor gets a NEW uid and 0x3656 often does not arrive again in-session, leaving
        // own power at 0 (the reported "my power shows wrong/0" bug). The own-nickname snapshot's marker
        // points at a different field (not the real power — verified ~106k vs the true ~380k), so it is
        // NOT a usable source. Instead carry the last known own power forward onto the new executor uid —
        // but only for a re-entry of the SAME character, so switching characters never inherits a stale
        // value. A fresh 0x3656 still refines it.
        bool sameCharacter = nickname == _lastOwnNickname;
        _lastOwnNickname = nickname;
        _executorId = userInfo.Value;
        _data.SaveNickname(userInfo.Value, nickname, true, server, job);
        if (server > 0)
        {
            _data.RequestOfficialCharacterLookup(userInfo.Value);
        }

        if (sameCharacter && _lastOwnPower > 0)
        {
            _data.SaveUserPower(userInfo.Value, _lastOwnPower);
        }
        else if (!sameCharacter)
        {
            _lastOwnPower = 0; // a different character: forget the old power; its own 0x3656/API sets it
        }

        _sink.Meta("nickname", ("own", true), ("uid", userInfo.Value), ("nickname", nickname), ("server", server), ("job", job), ("power", sameCharacter ? _lastOwnPower : 0));

        ReadAbyssArtifactCounts(packet);
    }

    /// <summary>본인 로드 스냅샷이 싣고 온 어보노멀 목록에서 아티팩트 점령 개수(12000261~266)를 읽는다.
    ///
    /// <para><b>왜 0x382A만으로는 부족한가.</b> 0x382A는 어비스에 <i>들어가는 순간</i>에만 온다. 이미 어비스
    /// 안에 있는 상태로 미터를 켜면 그 프레임은 이미 지나갔고, 개수는 이 스냅샷의 목록에만 남는다 —
    /// 2026-08-23 코퍼스가 정확히 그 경우다(0x382A 0건, 이 패킷 안에 12000261·12000264). 그러면 점령 슬롯이
    /// 영영 안 풀려서 회랑이 하나도 안 뜬다.</para>
    ///
    /// <para><b>왜 목록 전체를 파싱하지 않고 코드만 훑는가.</b> 이 3.5KB 스냅샷의 어보노멀 목록 레이아웃은
    /// 해독되어 있지 않고, 필요한 건 값이 아니라 <i>코드의 존재</i>뿐이다(코드가 곧 개수). 오탐 위험은
    /// 세 겹으로 막는다 — ① 이 지점은 uid·서버·닉네임 3중 게이트를 통과한 진짜 본인 로드 프레임이고
    /// (2026-07-30 쓰레기 신원 사건의 그 게이트다), ② 한 존에서 서로 다른 코드가 둘 이상 보이면 그 존은
    /// 통째로 버린다, ③ 최종 판정은 브로드캐스트가 말한 슬롯별 개수와 정확히 일치할 때만 성립한다
    /// (<c>AbyssArtifactStore.SideFor</c>). 실측: 08-23·08-28 두 스냅샷 모두 층당 정확히 한 코드만
    /// 나왔다.</para></summary>
    private void ReadAbyssArtifactCounts(byte[] packet)
    {
        int lowerCount = 0;
        int middleCount = 0;
        bool lowerAmbiguous = false;
        bool middleAmbiguous = false;

        for (int o = 0; o + 4 <= packet.Length; o++)
        {
            long code = PacketPrimitives.ReadUInt32LeAsLong(packet, o);
            if (!AbyssArtifactBuffCatalog.TryResolve(code, out int zoneId, out int count))
            {
                continue;
            }

            if (zoneId == AbyssArtifactBuffCatalog.LowerZoneId)
            {
                lowerAmbiguous |= lowerCount != 0 && lowerCount != count;
                lowerCount = count;
            }
            else
            {
                middleAmbiguous |= middleCount != 0 && middleCount != count;
                middleCount = count;
            }
        }

        if (lowerCount > 0 && !lowerAmbiguous)
        {
            _data.SaveAbyssArtifactCount(AbyssArtifactBuffCatalog.LowerZoneId, lowerCount);
            _sink.Meta("abyssartifact-count",
                ("zone", AbyssArtifactBuffCatalog.LowerZoneId), ("count", lowerCount), ("fromSnapshot", 1));
        }

        if (middleCount > 0 && !middleAmbiguous)
        {
            _data.SaveAbyssArtifactCount(AbyssArtifactBuffCatalog.MiddleZoneId, middleCount);
            _sink.Meta("abyssartifact-count",
                ("zone", AbyssArtifactBuffCatalog.MiddleZoneId), ("count", middleCount), ("fromSnapshot", 1));
        }
    }

    /// <summary>Other-player nickname snapshot 0x3644. Kotlin searchOtherNickname (252-348).</summary>
    private void SearchOtherNickname(byte[] packet, VarIntOutput lengthInfo, long arrivedAt)
    {
        int offset = lengthInfo.Length;
        if (packet[offset] != 0x45) return; // was 0x44 (2026-06-10 0x36 +1 shift)
        if (packet[offset + 1] != 0x36) return;
        offset += 2;
        if (packet.Length < offset) return;

        VarIntOutput userInfo = PacketPrimitives.ReadVarInt(packet, offset);
        offset += userInfo.Length;
        if (packet.Length < offset) return;

        VarIntOutput unknownInfo1 = PacketPrimitives.ReadVarInt(packet, offset);
        offset += unknownInfo1.Length;
        if (packet.Length < offset) return;

        VarIntOutput unknownInfo2 = PacketPrimitives.ReadVarInt(packet, offset);
        offset += unknownInfo2.Length;
        if (packet.Length < offset) return;

        if (packet.Length - offset <= 2) return;
        offset += 1;
        int probeBase = offset;

        string? nickname = null;
        int nickEndOffset = -1;
        for (int i = 0; i < 5; i++)
        {
            offset = probeBase + i;
            if (packet.Length < offset) continue;
            VarIntOutput nicknameLengthInfo = PacketPrimitives.ReadVarInt(packet, offset);
            if (nicknameLengthInfo.Length <= 0) continue;
            offset += nicknameLengthInfo.Length;
            if (nicknameLengthInfo.Value < 1 || nicknameLengthInfo.Value > 71) continue;
            if (packet.Length < offset) continue;
            if (packet.Length < offset + nicknameLengthInfo.Value) continue;
            byte[] np = packet[offset..(offset + nicknameLengthInfo.Value)];
            string candidate = Encoding.UTF8.GetString(np);
            offset += nicknameLengthInfo.Value;
            if (!IsValidNickname(candidate)) continue;
            nickname = candidate;
            nickEndOffset = offset;
            break;
        }

        if (nickname is null || nickEndOffset == -1) return;
        offset = nickEndOffset;

        int job = packet[offset] & 0xFF;
        offset += 1;
        if (packet.Length < offset) return;
        int serverBase = offset;

        int server = -1;
        int i2 = 0;
        while (true)
        {
            offset = serverBase + i2;
            i2++;
            if (packet.Length < offset + 2) break;
            int serverCandidate = PacketPrimitives.ParseUInt16Le(packet, offset);
            if (!IsPartyServer(serverCandidate)) continue; // 같은 범위가 세 군데 흩어져 있던 것을 한 곳으로
            offset += 2;
            if (packet.Length < offset) continue;
            VarIntOutput legionNameLengthInfo = PacketPrimitives.ReadVarInt(packet, offset);
            if (legionNameLengthInfo.Value < 2 || legionNameLengthInfo.Value > 24) continue;
            offset += legionNameLengthInfo.Length;
            if (packet.Length < offset + legionNameLengthInfo.Value) continue;
            byte[] lnp = packet[offset..(offset + legionNameLengthInfo.Value)];
            string legionNameCandidate = Encoding.UTF8.GetString(lnp);
            if (legionNameCandidate.Any(c => !char.IsDigit(c)))
            {
                server = serverCandidate;
            }
        }

        _data.SaveNickname(userInfo.Value, nickname, false, server, job);

        // 0x3645는 '주변 남'의 스냅샷이다 — 실측 11.5시간 / 540스냅샷에서 executor uid도 본인 닉네임도
        // 단 한 번도 실려 오지 않았다. 반면 여기서 쓰는 전투력은 마커+11부터 "뒤 u32가 0인 첫 그럴듯한
        // u32"를 1바이트씩 밀며 찾는 슬라이딩 스캔이라(ParseSnapshotPower) 세 소스 중 오프셋 오독에 가장
        // 약하다. 본인은 0x3656이라는 전용·고정 오프셋 소스를 이미 갖고 있으므로, 만에 하나 본인 uid가
        // 실려 와도 이 스캔값이 본인 전투력을 덮게 두지 않는다 — CombatPower 상한만으로는 [40만, 상한]
        // 구간의 오독이 그대로 통과한다. 닉/서버/직업 갱신은 그대로 받는다(위 SaveNickname).
        int? power = ParseSnapshotPower(packet);
        if (power != null && userInfo.Value != _executorId)
        {
            _data.SaveUserPower(userInfo.Value, power.Value);
        }

        _sink.Meta("nickname", ("own", false), ("uid", userInfo.Value), ("nickname", nickname), ("server", server), ("job", job), ("power", power ?? 0));
    }

    /// <summary>Snapshot combat-power scan. Kotlin parseSnapshotPower (810-822).</summary>
    private static int? ParseSnapshotPower(byte[] packet)
    {
        int markerIdx = LastIndexOf(packet, PowerMarker);
        if (markerIdx < 0) return null;
        int offset = markerIdx + 11;
        while (offset + 8 <= packet.Length)
        {
            long power = PacketPrimitives.ReadUInt32LeAsLong(packet, offset);
            if (CombatPower.IsPlausible(power) && PacketPrimitives.ParseUInt32Le(packet, offset + 4) == 0)
            {
                return (int)power;
            }

            offset += 1;
        }

        return null;
    }

    /// <summary>
    /// Recover the 시련 난이도 affixes from a spawn packet's embedded buff list.
    /// <para>The affixes are applied to the dungeon's mobs, so they ride the buff-apply packet AND every
    /// spawn — and the spawn is overwhelmingly the bigger source: measured over a four-run capture, 360 affix
    /// broadcasts reached the client but only 8 came through the buff-apply path, so reading applies alone
    /// misses ~98% of them and whole runs can go unlabelled (one of those four did).</para>
    /// <para>The embedded list's structure is not decoded — the packet is scanned for the sixteen known affix
    /// codes as u32-LE instead. That is safe precisely because the codes are the identity: each is a specific
    /// 8-digit value, so a coincidental 4-byte match is a ~1-in-4-billion event per position, and a wrong hit
    /// could at worst mislabel a difficulty the very next spawn corrects.</para>
    /// </summary>
    private void ScanSpawnForTrialAffix(byte[] packet, int bodyStart)
    {
        for (int i = bodyStart; i + 4 <= packet.Length; i++)
        {
            int code = PacketPrimitives.ParseUInt32Le(packet, i);
            if (TrialAffixCatalog.TryResolve(code, out TrialAffix affix))
            {
                _data.SaveTrialAffix(affix.Group, affix.Level, 0);
                _sink.Meta("trial-affix",
                    ("group", (int)affix.Group), ("level", affix.Level), ("code", code), ("from", "spawn"));
            }
        }
    }

    /// <summary>Summon / mob-spawn packet 0x3640. Kotlin parseSummonPacket (502-559). Emits a
    /// mob_spawn meta (and records the instanceId-&gt;mobCode map) plus a summon_map meta.</summary>
    private void ParseSummonPacket(byte[] packet, bool extraFlag)
    {
        int offset = 0;
        VarIntOutput packetLengthInfo = PacketPrimitives.ReadVarInt(packet);
        if (packetLengthInfo.Length < 0) return;
        offset += packetLengthInfo.Length;
        if (extraFlag)
        {
            offset += 1;
        }

        if (packet[offset] != 0x41) return; // was 0x40 (2026-06-10 0x36 +1 shift)
        if (packet[offset + 1] != 0x36) return;
        offset += 2;

        VarIntOutput summonInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (summonInfo.Length < 0)
        {
            return;
        }

        ScanSpawnForTrialAffix(packet, offset);

        int codeMarkerIdx = FindArrayIndex(packet, 0x00, 0x40, 0x02);
        if (codeMarkerIdx == -1)
        {
            codeMarkerIdx = FindArrayIndex(packet, 0x00, 0x00, 0x02);
        }

        if (codeMarkerIdx != -1)
        {
            int mobCode = ((packet[codeMarkerIdx - 1] & 0xFF) << 16)
                          | ((packet[codeMarkerIdx - 2] & 0xFF) << 8)
                          | (packet[codeMarkerIdx - 3] & 0xFF);
            _data.SaveMobId(summonInfo.Value, mobCode);
            Mob? mob = _data.GetMob(mobCode);
            _sink.Meta("mob_spawn",
                ("instanceId", summonInfo.Value),
                ("mobCode", mobCode),
                ("mobName", mob?.Name),
                ("boss", mob?.Boss ?? false));
            // [combat-diag] per-iid census: codeMarker found → boss (registered), trash (registered non-boss),
            // or unmapped (code decoded but maps to no Mob = garbage code / entity-id reuse candidate).
            // if (mob?.Boss == true) addon.parsingMobSpawnAddon(...) — deferred
        }
        else
        {
            // [combat-diag] 0x3641 arrived but neither code marker (00 40 02 / 00 00 02) was found, so the sole
            // mobCode registration is skipped even though we received the packet — a first boss that hits this
            // becomes unrecognizable downstream (parse-miss drop path).
        }

        int keyIdx = FindArrayIndex(packet, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
        if (keyIdx == -1) return;
        byte[] afterPacket = packet[(keyIdx + 8)..];

        // The summon-owner sub-record is normally tagged 07 02 06. A summon whose 0x3641 carries an EXTRA
        // sub-record (observed as an atypical 251-byte packet vs the uniform 209) tags its trailing owner
        // entry 07 02 01 instead, so the fixed 07 02 06 scan missed it → SaveSummon never ran → the summon
        // was left unmapped and surfaced as a standalone (non-party) contributor row (the 그리오사 phantom).
        // The owner u16 sits 3 bytes past the 07 in either variant. The 07 02 01 sequence ALSO occurs as an
        // unrelated field inside mob-spawn packets (where it would mis-read a fixed non-player value), so the
        // fallback is accepted only when its owner resolves to a recognized player (a real summon's owner).
        bool viaFallback = false;
        int opcodeIdx = FindArrayIndex(afterPacket, 0x07, 0x02, 0x06);
        if (opcodeIdx == -1)
        {
            opcodeIdx = FindArrayIndex(afterPacket, 0x07, 0x02, 0x01);
            if (opcodeIdx == -1) return;
            viaFallback = true;
        }

        offset = keyIdx + opcodeIdx + 11;

        if (offset + 2 > packet.Length) return;
        int realActorId = PacketPrimitives.ParseUInt16Le(packet, offset);

        if (viaFallback && !_data.IsKnownUser(realActorId)) return;

        _data.SaveSummon(summonInfo.Value, realActorId);
        _sink.Meta("summon_map", ("summonId", summonInfo.Value), ("ownerId", realActorId));
    }

    /// <summary>엔티티 스탯 브로드캐스트 0x8D00. 이름은 RemainHp지만 실제로는 <b>몹·플레이어 공통의 스탯
    /// 묶음</b>이다. 레이아웃(코퍼스 3.96GB / 프레임 416,370개에서 99.997%가 정확히 소진되어 검증됨):
    /// <code>
    /// [varint entity][varint mask]
    ///   mask&amp;1 → [u8 n][ n × (u8 statId, u32 LE) ]   // 플레이어 전용 자원류
    ///   mask&amp;2 → [u8 m][ m × (u8 statId, u64 LE) ]   // statId 0 = 현재 HP, 7 = 최대 HP
    /// </code>
    /// <para>종전 구현은 "varint 3개를 건너뛰고 u32를 읽는" 형태였는데, 이는 몹 프레임이 항상
    /// mask=2/1개/statId=0 한 형태여서 u64 HP의 하위 32비트에 <b>우연히</b> 착지했기 때문에 동작했다.
    /// 그래서 최대 HP만 실린 프레임(실측 9건)에서는 최대치를 "잔여 HP"로 발행해 교전 첫 프레임에 보스 HP가
    /// 순간적으로 만피로 튀었다. 이제 statId를 실제로 보고 현재 HP가 있을 때만 발행한다.</para></summary>
    private void ParseRemainHp(byte[] packet, VarIntOutput lengthInfo, bool extraFlag)
    {
        int offset = lengthInfo.Length;
        if (extraFlag)
        {
            offset++;
        }

        if (packet.Length < offset + 2) return;
        if (packet[offset] != 0x00) return;
        if (packet[offset + 1] != 0x8D) return;
        offset += 2;

        VarIntOutput mobIdInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (mobIdInfo.Length <= 0) return;
        offset += mobIdInfo.Length;

        VarIntOutput maskInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (maskInfo.Length <= 0) return;
        offset += maskInfo.Length;

        // 정상 게임 프레임의 mask는 1/2/3뿐이다(실측 416,356건 중 위반 0). 그 밖의 값은 비게임 노이즈
        // 스트림이 우연히 0x8D00으로 프레이밍된 것이므로 통째로 버린다.
        if ((maskInfo.Value & ~3) != 0) return;

        long? currentHp = null;
        long? maxHp = null;

        if ((maskInfo.Value & 1) != 0)
        {
            if (offset >= packet.Length) return;
            int n = packet[offset++];
            if (offset + (n * 5) > packet.Length) return;
            offset += n * 5; // u32 자원 스탯 — HP가 아니라 이 경로에는 statId 0/7이 실리지 않는다
        }

        if ((maskInfo.Value & 2) != 0)
        {
            if (offset >= packet.Length) return;
            int m = packet[offset++];
            if (offset + (m * 9) > packet.Length) return;
            for (int i = 0; i < m; i++)
            {
                int statId = packet[offset];
                long value = PacketPrimitives.ReadUInt64Le(packet, offset + 1);
                offset += 9;
                if (statId == 0) currentHp = value;
                else if (statId == 7) maxHp = value;
            }
        }

        // 프레임을 정확히 소진하지 못했으면 우리가 아는 형태가 아니다 — 노이즈이거나 서버가 포맷을 바꿨다.
        if (offset != packet.Length) return;

        int? mobCode = _data.GetMobId(mobIdInfo.Value);
        if (mobCode is null)
        {
            // 스폰(0x3641)이 통째로 유실돼 mobCode 미등록인데, 이 엔티티가 보스급 HP를 실어 왔다면(그리고 교전
            // 토글을 쏜 적이 있다면) '미상 보스'로 소급 승격한다 — "3번째 네임드가 집계 안 됨"의 완전유실 갈래.
            // 게이트(HP 임계·교전 토글·기믹 배제)는 데이터 계층이 판정한다. HP 신호는 현재 HP 우선, 없으면 최대.
            long hpSignal = currentHp is { } c && IsSaneHp(c) ? c : maxHp is { } mx2 && IsSaneHp(mx2) ? mx2 : 0;
            if (hpSignal >= 20_000_000L)
            {
            }

            if (hpSignal > 0)
            {
                _data.TryPromoteUnregisteredBoss(mobIdInfo.Value, hpSignal);
            }

            return; // [combat-diag slim] hp_no_mobcode dropped as noise (0x8D00 fires for trash too)
        }
        Mob? mob = _data.GetMob(mobCode.Value);
        if (mob is null) return;
        if (!mob.Boss) return;

        if (maxHp is { } mx && IsSaneHp(mx))
        {
            _data.SaveMobMaxHp(mobIdInfo.Value, mx);
        }

        // 현재 HP가 실리지 않은 프레임(최대 HP만 오는 경우)은 잔여 HP를 발행하지 않는다 — 종전 버그.
        if (currentHp is not { } hp || !IsSaneHp(hp)) return;

        long mobHp = hp;
        _data.SaveMobHp(mobIdInfo.Value, mobHp);
        _sink.Meta("remain_hp",
            ("target", mobIdInfo.Value),
            ("mobCode", mobCode.Value),
            ("mobName", mob.Name),
            ("hp", mobHp));
    }

    // 그보다 훨씬 큰 값은 프레임이 우리 해석과 다르다는 뜻이므로 버린다 — 진짜 게임 프레임 중에도 3.0e18짜리가
    // 1건 있었고, 프레임을 정확히 소진해서 길이 검사로는 못 걸러진다.
    // ⚠️ 100e9 에서 10e9 로 조였다. 종전에는 이 검사를 통과해도 저장 직전 Saturate 가 21.47억에 붙여 줘서
    // 상한이 느슨해도 피해가 제한적이었는데, 그 포화를 걷어내면서 이게 유일한 가드가 됐다. 최대 HP 는 단조
    // 증가로 래치되고 소프트 리셋에서도 보존되므로(Repositories.SaveMaxHp / DataManager.ResetBattleRecords),
    // 이상값 한 프레임이 그 엔티티의 분모를 세션 내내 오염시킨다. 실측 최대 27.2억(델트라스) 대비 3.7배 여유.
    private const long MaxPlausibleHp = 10_000_000_000L;

    private static bool IsSaneHp(long hp) => hp >= 0 && hp <= MaxPlausibleHp;

    // 여기서 int.MaxValue로 포화시키던 자리다. 그 주석은 "여유가 1.17배뿐"이라며 넘칠 것을 예고해 뒀는데,
    // 실제로 비탄의 설원(델트라스 27억대)과 차원핵 계열이 넘겨서 그 보스들의 HP 게이지·보스 체력 기여도·전투
    // 기록이 통째로 21.47억에 붙어 버렸다(replay-diag 실측 144건 / 13코드). 와이어는 u64로 정확했고 잘린 건
    // 저장 직전 이 한 줄뿐이었으므로, 포화를 없애고 데이터 계층을 long으로 넓혔다. 업로드 payload에는 HP
    // 원본 필드가 없다(bossHpContribution만 나간다) — 스키마 번호는 그대로다.

    /// <summary>엔티티 사망 0x8D04 — 죽은 엔티티 id varint 하나가 전부다. 몹·파티원에게도 오므로 "본인인가"
    /// 판정은 executor를 아는 데이터 계층에 맡긴다. 본인 사망 시 버프 오버레이를 비우는 데 쓴다(사망 후
    /// 부활하면 모든 버프가 날아간 상태이므로).</summary>
    private void ParseEntityDeath(byte[] packet, VarIntOutput lengthInfo, bool extraFlag, long arrivedAt)
    {
        int offset = lengthInfo.Length;
        if (extraFlag)
        {
            offset++;
        }

        if (packet.Length < offset + 2) return;
        if (packet[offset] != 0x04) return;
        if (packet[offset + 1] != 0x8D) return;
        offset += 2;

        VarIntOutput entityInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (entityInfo.Length <= 0) return;

        _data.SaveEntityDeath(entityInfo.Value, arrivedAt);
        _sink.Meta("death", ("entity", entityInfo.Value));
    }

    /// <summary>Battle start/end toggle 0x8D21. Kotlin parseBattlePacket (878-924).</summary>
    private void ParseBattlePacket(byte[] packet, VarIntOutput lengthInfo, bool extraFlag)
    {
        int offset = lengthInfo.Length;
        if (extraFlag)
        {
            offset++;
        }

        if (packet.Length < offset + 2) return;
        if (packet[offset] != 0x21) return;
        if (packet[offset + 1] != 0x8D) return;
        offset += 2;

        VarIntOutput battleInfo = PacketPrimitives.ReadVarInt(packet, offset);
        if (battleInfo.Length <= 0) return;
        offset += battleInfo.Length;

        offset += PacketPrimitives.ReadVarInt(packet, offset).Length;
        VarIntOutput toggleInfo = PacketPrimitives.ReadVarInt(packet, offset);

        int? mobCode = _data.GetMobId(battleInfo.Value);
        if (mobCode is null)
        {
            // [combat-diag] battle toggle (0x8D21) for an entity whose mobCode never registered → StartBattle
            // never fires. THE first-boss-miss smoking gun; EngageMiss self-labels the drop path (parse/loss/nospawn).
            if (toggleInfo.Value == 1)
            {
                // 스폰이 늦게 도착하면 되살릴 수 있도록 기억해 둔다(플레이어 엔티티도 이 토글을 쏘지만,
                // 플레이어에겐 SaveMobId가 영영 오지 않으므로 그 항목은 그냥 만료된다).
                _data.RememberUnresolvedBattleStart(battleInfo.Value);
            }

            _sink.Battle(battleInfo.Value, toggleInfo.Value, null, null, false, "mob_code_missing");
            return;
        }

        Mob? mob = _data.GetMob(mobCode.Value);
        if (mob is null)
        {
            _sink.Battle(battleInfo.Value, toggleInfo.Value, mobCode, null, false, "mob_missing");
            return;
        }

        if (!mob.Boss || mob.IsDummy)
        {
            _sink.Battle(battleInfo.Value, toggleInfo.Value, mobCode, mob.Name, false, "not_boss_or_dummy");
            return;
        }

        switch (toggleInfo.Value)
        {
            case 1:
                _data.StartBattle(battleInfo.Value);
                _sink.Battle(battleInfo.Value, toggleInfo.Value, mobCode, mob.Name, true, "start");
                break;
            case 0:
                _data.EndBattle(battleInfo.Value);
                _sink.Battle(battleInfo.Value, toggleInfo.Value, mobCode, mob.Name, true, "end");
                break;
            default:
                _sink.Battle(battleInfo.Value, toggleInfo.Value, mobCode, mob.Name, false, "unknown_toggle");
                break;
        }
    }

    /// <summary>Buff/debuff apply 0x382A/0x382B. Kotlin parseBuffPacket (1075-1130).</summary>
    private void ParseBuffPacket(byte[] packet, VarIntOutput lengthInfo, bool extraFlag, long arrivedAt)
    {
        try
        {
            int offset = lengthInfo.Length;
            if (extraFlag)
            {
                offset++;
            }

            if (packet[offset] != 0x2A && packet[offset] != 0x2B) return;
            if (packet[offset + 1] != 0x38) return;
            bool refresh = packet[offset] == 0x2B; // 0x382B = 갱신, 0x382A = 최초 적용
            offset += 2;

            VarIntOutput targetInfo = PacketPrimitives.ReadVarInt(packet, offset);
            offset += targetInfo.Length;

            // 두 opcode의 헤더 길이가 다르다 — 0x382A는 [01][kind] 2바이트, 0x382B는 [kind] 1바이트가
            // slot varint 앞에 붙는다. 종전에는 둘 다 2를 건너뛰어 0x382B에서 한 바이트씩 밀렸다.
            // slot이 2바이트 varint인 프레임은 우연히 자리가 맞아 지금까지 드러나지 않았고, 1바이트인
            // 프레임만 깨졌다. 실측(20260719-022140 원본 바이트): 0x382A는 수정 전후 결과가 완전히 동일하고,
            // 0x382B만 유효 코드가 4,040 → 4,791로 늘며 실재하지 않는 유령 코드 157289171(로그에 934건,
            // 참조 데이터엔 0건)이 98 → 0건으로 사라진다.
            offset += refresh ? 1 : 2;

            VarIntOutput slotInfo = PacketPrimitives.ReadVarInt(packet, offset);
            offset += slotInfo.Length;

            int skillCode = PacketPrimitives.ParseUInt32Le(packet, offset);
            offset += 4;

            // 시련 난이도 어픽스는 던전 안 몹에 걸리는 무기한 버프다. 아래 두 게이트(직업 버프 대역 / 무기한
            // 지속)에서 각각 한 번씩 버려지므로 여기서 가로채고, 버프 저장소로는 보내지 않는다 — 보내면 몹
            // 버프가 버프 오버레이에 뜬다. 코드 자체가 단계를 1:1로 말해줘서 값 디코딩이 필요 없다.
            if (TrialAffixCatalog.TryResolve(skillCode, out TrialAffix affix))
            {
                _data.SaveTrialAffix(affix.Group, affix.Level, arrivedAt);
                _sink.Meta("trial-affix", ("group", (int)affix.Group), ("level", affix.Level), ("code", skillCode));
                return;
            }

            // 아티팩트 점령 개수 어보노멀. 코드가 곧 값이라 읽을 필드가 없고, 아래 직업 버프 대역 게이트가
            // 어차피 버릴 코드다(12000262 < 20000000). 본인에게 걸린 것만 받는다 — 옆에 서 있는 적진영
            // 플레이어도 자기 진영의 개수 버프를 달고 있어서, 대상 검사 없이는 상대의 점령 수를 우리 것으로
            // 읽는다. 실측(2026-08-28): 어비스 로딩 0.30초 뒤 0x382A가 본인 uid를 대상으로 두 존을 함께 보낸다.
            if (AbyssArtifactBuffCatalog.TryResolve(skillCode, out int artifactZone, out int artifactCount))
            {
                if (_executorId > 0 && targetInfo.Value == _executorId)
                {
                    _data.SaveAbyssArtifactCount(artifactZone, artifactCount);
                    _sink.Meta("abyssartifact-count", ("zone", artifactZone), ("count", artifactCount));
                }

                return;
            }

            // Job-buff codes are <2-digit job prefix><...>: 11xxxxxxx(검성)..19xxxxxxx(권성, 2026-07-01 패치).
            // The upper bound was 190_000_000 (8 classes, max 18x); 권성 buffs are 190_000_000..199_999_999,
            // so it must reach 199_999_999 or every 권성 buff/debuff is dropped here.
            if (skillCode < 110000000 || skillCode > 199999999)
            {
                if (skillCode >= 30000000 || skillCode < 20000000)
                {
                    return;
                }
            }

            long duration = PacketPrimitives.ReadUInt32LeAsLong(packet, offset);
            offset += 8;

            long serverTime = PacketPrimitives.ReadUInt64Le(packet, offset);
            offset += 8;

            VarIntOutput actorInfo = PacketPrimitives.ReadVarInt(packet, offset);

            // duration 0xFFFFFFFF = "no fixed duration". Most such applies are passive/always-on states (auras,
            // consumable-less stances) that would clutter the overlay, so they are dropped — EXCEPT 폭주 (권성),
            // an actively-maintained combat stance ("폭주 상태") the player deliberately keeps up by managing 분노.
            // It is NOT truly infinite: it ends when 분노 hits 0. It re-broadcasts ~every 1.5 s while held
            // (measured: 23 applies / 280 s, held-gap p50 1.5 s), so give it a short fallback duration that each
            // re-broadcast refreshes — the overlay then shows it as a maintained buff and it fades a few seconds
            // after the stance actually ends (there is no buff-remove opcode to end it exactly).
            if (duration == 4294967295L)
            {
                if (skillCode is < 191300000 or > 191399999) // 폭주's variant band (base 19130000); others drop
                {
                    return;
                }

                duration = IndefiniteStanceFallbackMs;
            }

            int level = ReadAbnormalLevel(packet, offset + actorInfo.Length);
            _data.SaveUseBuff(targetInfo.Value, skillCode, arrivedAt, arrivedAt + duration, duration, actorInfo.Value, level, slotInfo.Value);
            _sink.Meta("buff",
                ("target", targetInfo.Value),
                ("actor", actorInfo.Value),
                ("skill", skillCode),
                ("duration", duration),
                ("level", level),
                ("serverTime", serverTime));
        }
        catch
        {
            // swallowed (matches Kotlin's try/catch around parseBuffPacket)
        }
    }

    /// <summary>버프 적용 패킷의 actor varint 바로 뒤에 붙는 꼬리에서 <b>어노멀 레벨</b>을 읽는다.
    /// 레이아웃: <c>[u8 level][u32 LE 소스 8자리 스킬코드][u8 flag][float x][float y][float z]</c>.
    /// <para>자기검증형이다 — level이 1~40이고 소스코드가 직업 스킬 대역(11000000~19999999)일 때만 채택한다.
    /// 두 조건은 코퍼스 4,688프레임에서 100% 동시 성립했고, 어긋나면 0(모름)을 돌려 상위 로직이 레벨 비교를
    /// 건너뛰게 한다. 서로 중복 적용되지 않는 버프 쌍에서 "레벨이 높은 쪽"을 고르는 데 쓴다.</para></summary>
    private static int ReadAbnormalLevel(byte[] packet, int offset)
    {
        if (offset < 0 || offset + 5 > packet.Length)
        {
            return 0;
        }

        int level = packet[offset];
        if (level is < 1 or > 40)
        {
            return 0;
        }

        int source = PacketPrimitives.ParseUInt32Le(packet, offset + 1);
        return source is >= 11_000_000 and <= 19_999_999 ? level : 0;
    }

    /// <summary>버프 제거 0x382C. 레이아웃(코퍼스 105,128 이벤트 중 99.996%가 끝까지 정확히 파싱):
    /// <code>
    /// [varint entity][u8 n]
    ///   n × ( [varint kind][varint slot][varint reason] + (kind != 0 ? [varint x][8 raw bytes] : 없음) )
    /// </code>
    /// <para>slot은 적용 패킷이 싣는 슬롯 번호와 같은 값이라, 같은 버프 코드가 겹쳐 걸려 있어도 어느
    /// 인스턴스를 닫는 신호인지 모호하지 않다. kind != 0 인 롱폼(스택 일괄 소거 등)을 건너뛰지 않고 함께
    /// 읽어야 조기 해제의 최대 사유를 놓치지 않는다.</para></summary>
    private void ParseBuffRemove(byte[] packet, VarIntOutput lengthInfo, bool extraFlag)
    {
        try
        {
            int offset = lengthInfo.Length;
            if (extraFlag)
            {
                offset++;
            }

            if (packet.Length < offset + 2) return;
            if (packet[offset] != 0x2C || packet[offset + 1] != 0x38) return;
            offset += 2;

            VarIntOutput entityInfo = PacketPrimitives.ReadVarInt(packet, offset);
            if (entityInfo.Length <= 0) return;
            offset += entityInfo.Length;

            if (offset >= packet.Length) return;
            int count = packet[offset++];
            if (count <= 0 || count > 64) return; // 실측 상한을 크게 넘는 값 = 우리 형태가 아님

            var slots = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                VarIntOutput kind = PacketPrimitives.ReadVarInt(packet, offset);
                if (kind.Length <= 0) return;
                offset += kind.Length;

                VarIntOutput slot = PacketPrimitives.ReadVarInt(packet, offset);
                if (slot.Length <= 0) return;
                offset += slot.Length;

                VarIntOutput reason = PacketPrimitives.ReadVarInt(packet, offset);
                if (reason.Length <= 0) return;
                offset += reason.Length;

                if (kind.Value != 0)
                {
                    VarIntOutput extra = PacketPrimitives.ReadVarInt(packet, offset);
                    if (extra.Length <= 0) return;
                    offset += extra.Length + 8;
                    if (offset > packet.Length) return;
                }

                slots.Add(slot.Value);
            }

            // 프레임을 정확히 소진하지 못했으면 우리가 아는 형태가 아니다 — 부분 적용은 하지 않는다.
            if (offset != packet.Length) return;

            _data.RemoveBuffSlots(entityInfo.Value, slots);
            _sink.Meta("buff_remove", ("entity", entityInfo.Value), ("slots", string.Join("|", slots)));
        }
        catch
        {
            // 다른 0x38 파서와 같은 정책 — 예외를 위로 던지지 않는다.
        }
    }

    /// <summary>Skill cooldown snapshot 0x3847: <c>[count][ {u32 LE skillCode, varint remainingMs} × count ]</c>
    /// for the local player's hotbar (remaining 0 = ready). Emits each record to the data layer, which keys it
    /// by base code for the buff overlay's "grayed while on cooldown" option. Raw code is passed through so the
    /// data layer owns the normalization. Bounds-guarded + swallowing like the other 0x38 parsers.</summary>
    private void ParseCooldownPacket(byte[] packet, VarIntOutput lengthInfo, bool extraFlag, long arrivedAt)
    {
        try
        {
            int offset = lengthInfo.Length;
            if (extraFlag)
            {
                offset++;
            }

            if (packet[offset] != 0x47 || packet[offset + 1] != 0x38)
            {
                return;
            }

            offset += 2;

            int count = packet[offset];
            offset++;
            if (count is <= 0 or > 128)
            {
                return; // implausible record count — a misaligned / noise frame
            }

            for (int i = 0; i < count; i++)
            {
                int skillCode = PacketPrimitives.ParseUInt32Le(packet, offset);
                offset += 4;

                VarIntOutput remInfo = PacketPrimitives.ReadVarInt(packet, offset);
                if (remInfo.Length < 0)
                {
                    return;
                }

                offset += remInfo.Length;

                _data.SaveCooldown(skillCode, remInfo.Value, arrivedAt, 0); // 0x3847 = self hotbar snapshot
                _sink.Meta("cooldown", ("skill", skillCode), ("remaining", remInfo.Value));
            }
        }
        catch
        {
            // swallowed — a short/garbage buffer must never crash the consumer (matches the buff parser)
        }
    }

    /// <summary>Per-cast cooldown START 0x3802 (multi-actor). Layout: <c>[actor v][flag u8][u32 skillCode]
    /// [counter][…][varint remaining]</c> where remaining is the frame's LAST varint (0 on non-cast/ready
    /// frames, the full cooldown on a cast — ground-truth: 바이젤/지원사격 39100ms, 축복의활 78200ms). Only cast
    /// frames (remaining &gt; 0) are stored, filtered to self; 0x3847 handles the accurate decay/clear after.
    /// <para>The byte after the actor varint — long documented here as a literal <c>[00]</c> — is a FLAG. When
    /// bit 0x04 or 0x08 is set the frame carries an extra trailing varint (a buff/charge remainder), so the
    /// "last varint" walk below reads THAT and stores a duration as if it were a cooldown. Every such frame in
    /// the corpus belongs to a charge-type skill whose real cooldown is 0, i.e. the skill is ready — the exact
    /// opposite of what gets stored. Measured over five packet corpora: 165/165 mis-stores are removed by the
    /// guard and nothing real is lost (all 165 carry cooldown 0, which the <c>remaining &lt;= 0</c> guard below
    /// would have dropped anyway). Symptom it fixes: 쾌유의 주문(호법성)·표적 화살(궁성)·지면 강타(권성) sat
    /// grayed for the full 8.0 s their buff was on screen.</para></summary>
    private void ParseCooldownStartPacket(byte[] packet, VarIntOutput lengthInfo, bool extraFlag, long arrivedAt)
    {
        try
        {
            int opcodeOffset = lengthInfo.Length + (extraFlag ? 1 : 0);
            if (packet[opcodeOffset] != 0x02 || packet[opcodeOffset + 1] != 0x38)
            {
                return;
            }

            VarIntOutput actor = PacketPrimitives.ReadVarInt(packet, opcodeOffset + 2);
            if (actor.Length < 0)
            {
                return;
            }

            int flagOff = opcodeOffset + 2 + actor.Length;
            if (flagOff >= packet.Length)
            {
                return;
            }

            int skillOff = flagOff + 1; // the flag byte, then the u32 skill code
            if (skillOff + 4 > packet.Length)
            {
                return;
            }

            int skillCode = PacketPrimitives.ParseUInt32Le(packet, skillOff);
            if (skillCode is < 11_000_000 or > 19_999_999)
            {
                return; // job skills only (the band the buff overlay grays)
            }

            // 쿨타임 잔여시간을 <b>먼저</b> 구한다. 값 자체는 쿨타임 오버레이의 것이지만, "이 발동이 쿨을
            // 돌렸다"는 사실은 시전 타임라인도 쓴다 — 같은 스킬의 여러 발동 중 어느 것이 진짜 눌러서 나간
            // 것인지 가를 수 있는, 와이어에 존재하는 유일한 신호다.
            long remaining = 0;
            if ((packet[flagOff] & 0x0C) == 0)
            {
                // remaining = the LAST varint of the frame. Walk back from the terminal byte over continuation bytes.
                // (flag 0x04/0x08 이 켜진 프레임은 꼬리에 varint 가 하나 더 붙어 이 워크가 지속시간을 쿨로
                //  오독한다 — 그래서 그 프레임은 애초에 잔여시간을 읽지 않는다. 실측 오저장 165건 가드.)
                int start = packet.Length - 1;
                int floor = Math.Max(skillOff + 4, packet.Length - 5);
                while (start > floor && (packet[start - 1] & 0x80) != 0)
                {
                    start--;
                }

                remaining = PacketPrimitives.ReadVarInt(packet, start).Value;
            }

            bool startsCooldown = remaining is > 0 and <= 3_600_000;

            // 시전 타임라인은 여기서 갈라진다 — 아래 쿨타임 게이트보다 앞이다. 그 게이트는 "이 프레임에서
            // 쿨타임을 읽을 수 있는가"를 판정할 뿐, "시전이 있었는가"가 아니다.
            // 실측(5인 파티 1세션 코퍼스): 직업 밴드 0x3802 프레임 중 remaining > 0 은 17.5%. 즉 쿨타임
            // 경로는 발동의 82.5%를 버린다. 그것들도 전부 진짜 발동이다(쿨 없는 스킬·이동기·버프·연계).
            _data.SaveSkillCast(actor.Value, skillCode, arrivedAt, startsCooldown);
            _sink.Meta("skill_cast", ("actor", actor.Value), ("skill", skillCode), ("cd", startsCooldown ? 1 : 0));

            if (!startsCooldown)
            {
                return; // ready/non-cast (0) or implausible — only cooldown STARTS gray instantly
            }

            _data.SaveCooldown(skillCode, remaining, arrivedAt, actor.Value, fromCast: true);
        }
        catch
        {
            // swallowed — a short/garbage buffer must never crash the consumer
        }
    }

    /// <summary>Aether (오드) resource status 0x610B/0x610C. Gated behind the opcode so the marker scan can't
    /// false-match a coincidental byte run in an unrelated packet (the compact marker prefix occurs in HP /
    /// damage payloads too).</summary>
    /// <param name="fromSnapshot">True for the 0x610B login/zone-in dump, false for a 0x610C change notice. The
    /// dump beats the packet that NAMES the character by ~4 s, so whoever files this away per-character has to
    /// know which of the two it is — same distinction the weekly counters already draw.</param>
    private void ParseAetherStatus(byte[] packet, int bodyStart, bool fromSnapshot)
    {
        AetherParse a = AetherStatusParser.TryParse(packet, bodyStart);
        if (!a.Ok)
        {
            return;
        }

        _data.SaveAetherStatus(a.Base, a.Bonus, fromSnapshot);
        _sink.Meta("aether", ("base", a.Base), ("bonus", a.Bonus), ("total", a.Total));
    }

    /// <summary>Shugo-festa key (슈고 페스타 보상 열쇠) status, riding the same 0x610B/0x610C packets as aether
    /// (a different key byte selects it). Tried alongside aether so both resources update from one packet.</summary>
    /// <param name="fromSnapshot">True for the 0x610B login/zone-in dump — see the aether overload.</param>
    private void ParseShugoKey(byte[] packet, int bodyStart, bool fromSnapshot)
    {
        ShugoKeyParse s = ShugoKeyParser.TryParse(packet, bodyStart);
        if (!s.Ok)
        {
            return;
        }

        _data.SaveShugoKey(s.Base, s.Bonus, fromSnapshot);
        _sink.Meta("shugokey", ("base", s.Base), ("bonus", s.Bonus), ("total", s.Total));
    }

    /// <summary>주간 성역 '최종 보스 처치 횟수' 4종, 오드·슈고 열쇠와 같은 0x610B/0x610C 패킷에 실려 온다
    /// (통화 id로 구분). 전체 스냅샷은 전부 싣고 델타는 하나만 싣기 때문에 종류마다 한 번씩 시도한다 — 실패는 정상이다.
    /// <para>델타는 최종 보스 사망 +0.13~0.43초에 도착한다(코퍼스 3세션 실측). 즉 이 훅은 "전투가 끝났다"를
    /// 미터가 판정할 필요가 없다 — 서버가 차감을 알려준다.</para></summary>
    private void ParseWeeklyContent(byte[] packet, int bodyStart, bool fromSnapshot)
    {
        foreach (WeeklyContentKind kind in WeeklyContentKinds)
        {
            WeeklyContentParse w = WeeklyContentParser.TryParse(packet, bodyStart, kind);
            if (!w.Ok)
            {
                continue;
            }

            _data.SaveWeeklyContent(kind, w.Base, w.Bonus, fromSnapshot);
            _sink.Meta("weeklycontent",
                ("kind", (int)kind), ("base", w.Base), ("bonus", w.Bonus), ("snapshot", fromSnapshot ? 1 : 0));
        }
    }

    /// <summary>Every counter the 0x610B/0x610C pair is walked for. ⚠️ A kind missing from HERE is read off the
    /// wire never — parser and catalog can both be complete and the chip still sits at a permanent 1/1.</summary>
    private static readonly WeeklyContentKind[] WeeklyContentKinds =
    [
        WeeklyContentKind.Rudra,
        WeeklyContentKind.ErosionPurifier,
        WeeklyContentKind.MuspelGrail,
        WeeklyContentKind.FrozenLament,
    ];

    /// <summary>어비스 회랑 이용 시간(ms) — 오드/주간 성역과 같은 0x610B/0x610C에 통화 id 10000001~10000012로
    /// 실려 온다. 스냅샷은 12개를 전부, 델타는 입장(130000)과 소진(0) 두 순간만 싣는다.
    /// <para>⚠️ 이 파서는 옆의 세 파서와 필드 폭이 다르다(mask 0x01 = u64 8바이트 고정). 복붙하면 조용히
    /// 아무것도 못 읽는다 — <see cref="AbyssCorridorParser"/> 주석 참조.</para></summary>
    private void ParseAbyssCorridor(byte[] packet, int bodyStart, bool fromSnapshot)
    {
        Span<AbyssCorridorTicket> tickets = stackalloc AbyssCorridorTicket[AbyssCorridorParser.MaxTickets];
        int count = AbyssCorridorParser.TryParse(packet, bodyStart, fromSnapshot, tickets);
        if (count < 0)
        {
            // The frame did not walk cleanly, so nothing may be inferred from it — not even a zero. Logged
            // because the failure mode this feature has is SILENT: a layout drift would simply stop reporting
            // corridors, which looks exactly like a player who captured none.
            _sink.Meta("abysscorridor", ("rejected", 1), ("len", packet.Length), ("snapshot", fromSnapshot ? 1 : 0));
            return;
        }

        if (count == 0)
        {
            return; // a neighbouring currency's broadcast — understood, just not ours
        }

        for (int i = 0; i < count; i++)
        {
            _data.SaveAbyssCorridor(tickets[i].TicketId, tickets[i].RemainingMs, fromSnapshot);
        }

        _sink.Meta("abysscorridor",
            ("count", count),
            ("first", tickets[0].TicketId),
            ("firstMs", tickets[0].RemainingMs),
            ("snapshot", fromSnapshot ? 1 : 0));
    }

    /// <summary>어비스 아티팩트 점령 현황(0xE305 존 단위 / 0xE307 전체). 어느 슬롯이 어떤 아티팩트를 들고
    /// 있는지와 이번 점령 주기의 시작·종료 시각을 그대로 싣는다. 종전 근거였던 '회랑 맵에 들어가 봤는가'는
    /// 옳지만 거의 답을 주지 못한다 — 그 캐릭터가 직접 들어간 회랑만 알 수 있어서 8월 한 달 통틀어 6번
    /// 발화했다. 이 프레임은 여섯 개 전부를 한 번에, 아무 데도 안 가고 알려준다.
    /// <para>실측 4일치(08-17·08-19·08-23·08-28) 44프레임(존 레코드 62건) 전부 body를 마지막 바이트까지
    /// 소진했고 거절 0건이다.
    /// 08-17에도 같은 문법으로 있었으므로 와이어가 바뀐 적은 없다 — 미터가 원래부터 안 읽고 있었다.</para>
    /// <para>⚠️ owner 바이트는 종족이 아니라 <b>이번 서버 매칭 안의 슬롯</b>이다. 같은 캐릭터가 08-23엔 1,
    /// 08-28엔 2였다 — 어느 쪽이 우리인지는 <see cref="AbyssArtifactBuffCatalog"/>의 점령 개수 어보노멀로
    /// 따로 알아낸다.</para></summary>
    private void ParseAbyssArtifacts(byte[] packet, int bodyStart, bool wholeAbyss)
    {
        Span<AbyssArtifactZone> zones = stackalloc AbyssArtifactZone[AbyssArtifactParser.MaxZones];
        Span<AbyssArtifactHolding> holdings = stackalloc AbyssArtifactHolding[AbyssArtifactParser.MaxArtifacts];
        int count = AbyssArtifactParser.TryParse(packet, bodyStart, wholeAbyss, zones, holdings, out int zoneCount);
        if (count <= 0)
        {
            // Rejected frames are logged because this feature's failure mode is SILENT: capture is
            // direction-unrestricted, unrelated traffic reaches this dispatcher (the 08-28 corpus delivers DNS
            // answers as 0xEDC2/0x3AE9), and a layout drift would simply stop reporting occupation — which is
            // indistinguishable from a side that captured nothing.
            _sink.Meta("abyssartifact", ("rejected", 1), ("len", packet.Length), ("all", wholeAbyss ? 1 : 0));
            return;
        }

        for (int z = 0; z < zoneCount; z++)
        {
            AbyssArtifactZone zone = zones[z];

            // A zone's artifacts are the ids sharing its thousands group (1001~1003 / 2001~2003), which is how
            // the server keys the zone itself. 0xE307 lists all six in one flat run, so they are split back out
            // by id here rather than by position.
            var forZone = new List<AbyssArtifactHolding>(AbyssArtifactParser.ArtifactsPerZone);
            for (int i = 0; i < count; i++)
            {
                if (holdings[i].ArtifactId / 1000 == zone.ZoneId / 1000)
                {
                    forZone.Add(holdings[i]);
                }
            }

            if (forZone.Count != AbyssArtifactParser.ArtifactsPerZone)
            {
                continue; // a zone whose artifacts did not all arrive says nothing usable about occupation
            }

            _data.SaveAbyssArtifacts(zone.ZoneId, zone.StartMs, zone.EndMs, forZone);
            _sink.Meta("abyssartifact",
                ("zone", zone.ZoneId),
                ("owners", (forZone[0].OwnerSide * 100) + (forZone[1].OwnerSide * 10) + forZone[2].OwnerSide),
                ("cycleStart", zone.StartMs),
                ("cycleEnd", zone.EndMs),
                ("all", wholeAbyss ? 1 : 0));
        }
    }

    /// <summary>
    /// Dungeon instance phase window (0x6100 / 0x6101): <c>[u32-LE mapId][u8 phase][u64-LE startMs][u64-LE
    /// endMs]</c>. The trial's main phase (2) is exactly its 제한 시간 setting, which is one of the four
    /// difficulty knobs and the only one with no abnormal to read it from.
    /// <para>Layout verified two ways: the trial's phase-2 window measures 600 s in both captured runs (=
    /// 제한 시간 4단계, matching the two abnormal-borne affixes in the same runs, which are also 4), and a
    /// non-trial control (무스펠의 성배) measures 7,200 s — its catalogued dungeon time exactly. Both
    /// timestamps are validated as plausible epoch-ms so a coincidental mapId match can't be mistaken for a
    /// window.</para>
    /// </summary>
    private void ParseInstancePhase(byte[] packet, int bodyStart)
    {
        if (bodyStart + 21 > packet.Length)
        {
            return;
        }

        int mapId = PacketPrimitives.ParseUInt32Le(packet, bodyStart);
        int phase = packet[bodyStart + 4];
        long startMs = PacketPrimitives.ReadUInt64Le(packet, bodyStart + 5);
        long endMs = PacketPrimitives.ReadUInt64Le(packet, bodyStart + 13);

        // Both ends must be plausible epoch-ms and ordered. Without this a stray 4-byte match on a map id
        // would turn arbitrary bytes into a "window".
        bool sane = mapId > 0 && phase > 0
            && startMs >= MinPlausibleEpochMs && startMs <= MaxPlausibleEpochMs
            && endMs > startMs && endMs <= MaxPlausibleEpochMs;

        // Logged either way, and BEFORE the gate: this frame spans TCP segments, so it can't be recovered
        // from a packet log by scanning raw bytes — the only way to see what it actually said is to record
        // what the parser made of it. A rejected frame is the more interesting one to see.
        _sink.Meta("instance-phase",
            ("mapId", mapId), ("phase", phase),
            ("windowMs", endMs - startMs), ("startMs", startMs), ("accepted", sane));

        if (sane)
        {
            _data.SaveInstancePhaseWindow(mapId, phase, startMs, endMs - startMs);
        }

        // Reported even when the WINDOW was rejected: a 어비스 회랑's phase packet carries endMs = 0, so the
        // gate above always throws it away — and its map id is the only signal that the corridor clock just
        // started or stopped.
        //
        // But it gets its own gate rather than none. The consumer treats an unrecognised id as "left the
        // corridor", so a garbage frame is NOT harmless: one stray id stops a running clock. Everything the
        // window check tests except the end time is therefore still required here — a real phase packet always
        // has a phase and a plausible start, and 0x6101 (which this parser reads with the 0x6100 layout though
        // its body is offset by a padding byte) fails that on anything but the shortest frames.
        if (mapId > 0
            && phase > 0
            && startMs >= MinPlausibleEpochMs
            && startMs <= MaxPlausibleEpochMs)
        {
            _data.SaveInstanceMap(mapId);
        }
    }

    /// <summary>Field-boss respawn timers 0x9101. Extracts boss-code → target-time records and forwards them
    /// to the data layer, which drives the lead-time alert.</summary>
    private void ParseFieldBossTimers(byte[] packet, int bodyStart, long arrivedAt)
    {
        FieldBossTimerParser.Result table = FieldBossTimerParser.ParseTable(packet, bodyStart, arrivedAt);
        if (table.Timers.Count == 0)
        {
            return;
        }

        _data.SaveFieldBossTimers(table.Timers);
        // mapId is logged too: the table is map-scoped, so an unfamiliar id is the first sign of a new region.
        _sink.Meta("fieldboss", ("count", table.Timers.Count), ("mapId", table.MapId));
    }

    /// <summary>Kotlin isValidNickname (867-876).</summary>
    private static bool IsValidNickname(string str)
    {
        bool hasKoreanOrEnglish = str.Any(c =>
            (c >= '가' && c <= '힣') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
        bool allValid = str.All(c =>
            (c >= '가' && c <= '힣') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || char.IsDigit(c));
        return hasKoreanOrEnglish && allValid;
    }

    /// <summary>KMP first-occurrence index of a byte pattern (Kotlin findArrayIndex varargs).</summary>
    private static int FindArrayIndex(byte[] data, params int[] pattern)
    {
        if (pattern.Length == 0) return 0;

        var p = new byte[pattern.Length];
        for (int i = 0; i < pattern.Length; i++)
        {
            p[i] = (byte)pattern[i];
        }

        var lps = new int[p.Length];
        int len = 0;
        for (int i = 1; i < p.Length; i++)
        {
            while (len > 0 && p[i] != p[len]) len = lps[len - 1];
            if (p[i] == p[len]) len++;
            lps[i] = len;
        }

        int ii = 0, j = 0;
        while (ii < data.Length)
        {
            if (data[ii] == p[j])
            {
                ii++;
                j++;
                if (j == p.Length) return ii - j;
            }
            else if (j > 0)
            {
                j = lps[j - 1];
            }
            else
            {
                ii++;
            }
        }

        return -1;
    }

    /// <summary>Last-occurrence index of a byte pattern (Kotlin lastIndexOf, 786-799).</summary>
    private static int LastIndexOf(byte[] data, byte[] pattern)
    {
        if (pattern.Length == 0 || data.Length < pattern.Length) return -1;
        for (int i = data.Length - pattern.Length; i >= 0; i--)
        {
            bool matched = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched) return i;
        }

        return -1;
    }
}
