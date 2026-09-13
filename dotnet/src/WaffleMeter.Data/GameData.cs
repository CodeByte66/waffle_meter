using WaffleMeter.Capture;

namespace WaffleMeter.Data;

/// <summary>
/// Concrete <see cref="ICaptureGameData"/>: a static mob catalog + skill-code set (loaded from
/// mobs.json / skills.json) plus the runtime instanceId-&gt;mobCode map built from spawn packets
/// (Kotlin DataManager's mobIdRepository). Single-consumer; not thread-safe (the live pipeline
/// drives it from one consumer, matching the capture pipeline).
/// </summary>
public sealed class GameData : ICaptureGameData
{
    private readonly IReadOnlyDictionary<int, Mob> _mobs;
    private readonly HashSet<long> _skillCodes;
    private readonly Dictionary<int, int> _mobId = new();
    private readonly HashSet<int> _knownUsers = new();

    public GameData(IReadOnlyDictionary<int, Mob> mobs, HashSet<long> skillCodes)
    {
        _mobs = mobs;
        _skillCodes = skillCodes;
    }

    public Mob? GetMob(int code) => _mobs.TryGetValue(code, out Mob? m) ? m : null;

    public int? GetMobId(int instanceId) => _mobId.TryGetValue(instanceId, out int code) ? code : null;

    // Kotlin saveMobId overwrites (last write wins); the previous!=code battle-state side effect is deferred.
    public void SaveMobId(int instanceId, int mobCode) => _mobId[instanceId] = mobCode;

    public bool SkillExists(long code) => _skillCodes.Contains(code);

    // Track observed player uids so the summon-owner fallback can be validated during replay (matching the
    // live DataManager, which answers from its user repository).
    public bool IsKnownUser(int uid) => _knownUsers.Contains(uid);

    // Capture-only reference context: the DPS write side effects are no-ops here (they do not affect
    // the parser's emitted events). The full DataManager implements them to drive DPS.
    public long CurrentEpoch() => 0;
    public void SaveDamage(ParsedDamagePacket pdp, long epoch) { }
    public void StartBattle(int target) { }
    public void EndBattle(int target) { }
    public void SaveNickname(int uid, string nickname, bool isExecutor, int server, int jobByte) => _knownUsers.Add(uid);
    public void SaveUserPower(int uid, int power) { }
    public void SaveSummon(int summonId, int ownerId) { }
    public void SaveMobHp(int instanceId, long hp) { }
    public void SaveUseBuff(int uid, int skillCode, long buffStart, long buffEnd, long duration, int actorId) { }
    public void RequestOfficialCharacterLookup(int uid) { }
    public void SavePartyRoster(IReadOnlyList<(string Nickname, int Server, int Slot)> members) { }
    public void SaveAetherStatus(int baseVal, int bonus) { }
    public void SaveShugoKey(int baseVal, int bonus) { }
    public void SaveFieldBossTimers(IReadOnlyList<(int Code, long TargetMs)> timers) { }
}
