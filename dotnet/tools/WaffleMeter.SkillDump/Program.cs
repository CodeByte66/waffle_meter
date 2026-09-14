using System.Text.Json;
using WaffleMeter.Capture;
using WaffleMeter.Capture.Corpus;
using WaffleMeter.Data;

// SKILL-CODE DUMP (investigation): replay a packet-debug .jsonl and, for each damage hit, recover the
// RAW skill code (pre-normalization, tapped via SkillExists) alongside the NORMALIZED code the parser
// emits, the direct/DoT split, and the BACK-attack flag. Aggregates per (actor, rawCode) so we can see
// exactly which raw codes collapse onto which normalized code (the 대지의 축복→대지의 징벌 merge), whether
// a 지속(DoT) code is present, and how back-attacks distribute across skills.
if (args.Length < 2) { Console.Error.WriteLine("usage: <corpus.jsonl> <skills.json> [actorFilterSubstr]"); return 1; }
string corpus = args[0], skillsPath = args[1];
string? nameFilter = args.Length >= 3 ? args[2] : null;
string jsonDir = Path.GetDirectoryName(Path.GetFullPath(skillsPath))!;

var dm = new DataManager();
dm.LoadSkills(ReferenceJson.LoadSkills(skillsPath));
string mobsPath = Path.Combine(jsonDir, "mobs.json");
if (File.Exists(mobsPath)) dm.LoadMobs(ReferenceJson.LoadMobs(mobsPath));

var spy = new Spy(dm);
var processor = new StreamProcessor(spy, spy);

var byIp = new Dictionary<string, List<CapturedSegment>>();
foreach (string line in CaptureCorpusReader.ReadLines(corpus))
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonDocument doc;
    try { doc = JsonDocument.Parse(line); } catch { continue; }
    using (doc)
    {
        JsonElement r = doc.RootElement;
        if (!r.TryGetProperty("type", out JsonElement t) || t.GetString() != "capture") continue;
        string ip = r.TryGetProperty("ip", out JsonElement ipe) ? ipe.GetString() ?? "" : "";
        var seg = new CapturedSegment(r.GetProperty("seq").GetInt64(),
            Convert.FromBase64String(r.GetProperty("data").GetString() ?? ""),
            r.GetProperty("at").GetInt64(), ip);
        (byIp.TryGetValue(ip, out List<CapturedSegment>? l) ? l : byIp[ip] = new()).Add(seg);
    }
}

var aligners = new Dictionary<string, PacketAlignmenter>();
var assemblers = new Dictionary<string, StreamAssembler>();
List<CapturedSegment> allSegs = byIp.Values.SelectMany(l => l).OrderBy(s => s.ArrivedAtMs).ThenBy(s => s.Seq).ToList();
foreach (CapturedSegment seg in allSegs)
{
    string ip = seg.SrcIp;
    if (!assemblers.TryGetValue(ip, out StreamAssembler? asm))
    {
        aligners[ip] = new PacketAlignmenter();
        assemblers[ip] = asm = new StreamAssembler((p, at) => { spy.NewPacket(); processor.OnPacketReceived(p, at); });
    }
    foreach (var chunk in aligners[ip].Feed(seg.Seq, seg.Payload, seg.ArrivedAtMs))
        asm.ProcessChunk(chunk.Data, chunk.ArrivedAt);
}

Console.WriteLine($"=== {Path.GetFileName(corpus)}  segs={allSegs.Count} streams={aligners.Count}  hits={spy.Rows.Count} ===\n");

// Which actor is the healer (치유성 = 17xxxxxx skills)? Rank actors by 17-prefixed damage.
var actorHeal = spy.Rows.Where(r => r.Norm / 1_000_000 == 17)
    .GroupBy(r => r.Actor).ToDictionary(g => g.Key, g => g.Sum(x => (long)x.Dmg));
foreach ((int actor, long heal) in actorHeal.OrderByDescending(k => k.Value).Take(5))
    Console.WriteLine($"actor {actor}: 17xx damage ≈ {heal:N0}  hits={spy.Rows.Count(r => r.Actor == actor)}");
Console.WriteLine();

int focusActor = actorHeal.Count > 0 ? actorHeal.MaxBy(k => k.Value).Key : 0;
Console.WriteLine($"=== focus actor {focusActor} — per RAW skill code (17xx 치유성 highlighted) ===");
Console.WriteLine($"{"rawCode",-10} {"inJson",-6} {"->norm",-10} {"normName",-22} {"dir",5} {"dot",5} {"back",5} {"dmg",14}");

var byRaw = spy.Rows.Where(r => r.Actor == focusActor)
    .GroupBy(r => r.Raw)
    .Select(g => new
    {
        Raw = g.Key,
        Norm = g.First().Norm,
        Dir = g.Count(x => !x.Dot),
        Dot = g.Count(x => x.Dot),
        Back = g.Count(x => x.Back),
        Dmg = g.Sum(x => (long)x.Dmg),
    })
    .OrderByDescending(x => x.Dmg).ToList();

foreach (var g in byRaw)
{
    bool inJson = dm.SkillExists(g.Raw);
    string nm = dm.Skill(g.Norm)?.Name ?? "?";
    string mark = g.Raw / 1_000_000 == 17 ? "*" : " ";
    Console.WriteLine($"{mark}{g.Raw,-9} {(inJson ? "yes" : "NO"),-6} {g.Norm,-10} {nm,-22} {g.Dir,5} {g.Dot,5} {g.Back,5} {g.Dmg,14:N0}");
}

// Show the merge explicitly: normalized codes fed by >1 raw code.
Console.WriteLine("\n=== normalized codes fed by MULTIPLE raw codes (the merge) ===");
foreach (var grp in byRaw.GroupBy(x => x.Norm).Where(g => g.Count() > 1))
{
    string nm = dm.Skill(grp.Key)?.Name ?? "?";
    Console.WriteLine($"norm {grp.Key} ({nm}) <= raws: {string.Join(", ", grp.Select(x => $"{x.Raw}{(dm.SkillExists(x.Raw) ? "" : "*NO-JSON")}"))}");
}

// ④ switch-type breakdown: does 판정 (back/crit) only appear on sw6 (directional) hits?
Console.WriteLine("\n=== ④ switch-type per normalized skill (focus actor, non-DoT) — sw6=directional(판정 가능), sw4=비방향성 ===");
Console.WriteLine($"{"norm",-10} {"name",-20} {"hits",5} {"sw6",5} {"sw4",5} {"back",5} {"crit",5} {"bk/sw6",7}");
foreach (var g in spy.Rows.Where(r => r.Actor == focusActor && !r.Dot)
    .GroupBy(r => r.Norm)
    .Select(gr => new { Norm = gr.Key, Hits = gr.Count(), Sw6 = gr.Count(x => x.Sw == 6), Sw4 = gr.Count(x => x.Sw == 4), Back = gr.Count(x => x.Back), Crit = gr.Count(x => x.Crit) })
    .OrderByDescending(x => x.Hits).Take(22))
{
    string nm = dm.Skill(g.Norm)?.Name ?? "?";
    Console.WriteLine($"{g.Norm,-10} {nm,-20} {g.Hits,5} {g.Sw6,5} {g.Sw4,5} {g.Back,5} {g.Crit,5} {(g.Sw6 > 0 ? $"{100.0 * g.Back / g.Sw6:F0}%" : "-"),7}");
}

var nd = spy.Rows.Where(r => r.Actor == focusActor && !r.Dot).ToList();
int allHits = nd.Count, sw6 = nd.Count(r => r.Sw == 6), sw4 = nd.Count(r => r.Sw == 4), backAll = nd.Count(r => r.Back);
Console.WriteLine($"\n=== ④ summary (focus actor {focusActor}) ===");
Console.WriteLine($"non-DoT hits={allHits}  sw6(dir)={sw6}  sw4(nondir)={sw4}  other-sw={allHits - sw6 - sw4}");
Console.WriteLine($"back={backAll}  |  back% over ALL={(allHits > 0 ? 100.0 * backAll / allHits : 0):F1}% (current, diluted)  |  back% over sw6={(sw6 > 0 ? 100.0 * backAll / sw6 : 0):F1}% (proposed)");
Console.WriteLine($"sanity: back on sw4={nd.Count(r => r.Back && r.Sw == 4)} (should be ~0)   crit on sw4={nd.Count(r => r.Crit && r.Sw == 4)}   crit on sw6={nd.Count(r => r.Crit && r.Sw == 6)}");

// ⑤ buff/debuff intervals: are re-applications (refreshes) captured, and does the union cover the window?
static long UnionMs(IEnumerable<(long S, long E)> iv)
{
    List<(long S, long E)> list = iv.Where(x => x.E > x.S).OrderBy(x => x.S).ToList();
    if (list.Count == 0) return 0;
    long total = 0, curS = list[0].S, curE = list[0].E;
    for (int i = 1; i < list.Count; i++)
    {
        (long s, long e) = list[i];
        if (s > curE) { total += curE - curS; curS = s; curE = e; }
        else if (e > curE) curE = e;
    }
    return total + (curE - curS);
}

Console.WriteLine($"\n=== ⑥ aether / shugo-key (0x610x) parses ===");
Console.WriteLine($"aether: {spy.Aethers.Count}");
foreach (var a in spy.Aethers) Console.WriteLine($"   base={a.Base} bonus={a.Bonus} total={a.Base + a.Bonus}");
Console.WriteLine($"shugokey: {spy.ShugoKeys.Count}");
foreach (var s in spy.ShugoKeys) Console.WriteLine($"   base={s.Base} bonus={s.Bonus} total={s.Base + s.Bonus}");

Console.WriteLine($"\n=== ⑤ buff/debuff intervals (SaveUseBuff) — top by apply-count ===");
Console.WriteLine($"total buff-apply packets={spy.Buffs.Count}");
Console.WriteLine($"{"code",-11} {"name",-18} {"applies",7} {"durs(ms)",-18} {"span(s)",8} {"cover(s)",8} {"cov%",5}");
foreach (var g in spy.Buffs.GroupBy(b => b.Code).OrderByDescending(gr => gr.Count()).Take(20))
{
    long span = g.Max(x => x.End) - g.Min(x => x.Start);
    long cover = UnionMs(g.Select(x => (x.Start, x.End)));
    string nm = dm.Buff(g.Key)?.Name ?? "?";
    string durs = string.Join(",", g.Select(x => x.Dur).Distinct().OrderBy(x => x).Take(4));
    Console.WriteLine($"{g.Key,-11} {nm,-18} {g.Count(),7} {durs,-18} {span / 1000.0,8:F1} {cover / 1000.0,8:F1} {(span > 0 ? 100.0 * cover / span : 0),4:F0}%");
}

// ⑦ cooldown (0x3847) — validate the raw skillCode -> base normalization matches the buff overlay's bases.
static int CdBase(int code) => code is >= 11_000_000 and <= 19_999_999 ? code / 10_000 * 10_000
    : code is >= 110_000_000 and <= 199_999_999 ? code / 100_000 * 10_000 : code;
Console.WriteLine($"\n=== ⑦ cooldown (0x3847) — base-matching validation ===");
Console.WriteLine($"total cooldown records={spy.Cooldowns.Count}");
var cdCodes = spy.Cooldowns.Select(c => c.Skill).Distinct().OrderBy(x => x).ToList();
var cdBases = cdCodes.Select(CdBase).ToHashSet();
var buffBases = spy.Buffs.Select(b => DataManager.BuffBaseCode(b.Code)).ToHashSet();
var overlap = cdBases.Intersect(buffBases).OrderBy(x => x).ToList();
Console.WriteLine($"distinct cooldown codes={cdCodes.Count}  cooldown bases={cdBases.Count}  buff bases={buffBases.Count}");
Console.WriteLine($"OVERLAP (cd base ∩ buff base) = {overlap.Count}: {string.Join(", ", overlap.Take(30))}");
Console.WriteLine($"remaining(ms) sample: {string.Join(", ", spy.Cooldowns.Where(c => c.Remaining > 0).Select(c => c.Remaining).Distinct().Take(12))}");
Console.WriteLine("sample cooldown codes -> base (inJson / name / matchesBuff):");
foreach (int c in cdCodes.Take(30))
    Console.WriteLine($"   {c,-11} -> base {CdBase(c),-11} inJson={dm.SkillExists(c),-5} name={dm.Skill(c)?.Name ?? "?",-20} matchesBuff={buffBases.Contains(CdBase(c))}");

return 0;

sealed class Spy(DataManager dm) : IStreamProcessorSink, ICaptureGameData
{
    public readonly List<(int Actor, long Raw, int Norm, bool Dot, bool Back, int Dmg, int Sw, bool Crit)> Rows = new();
    private readonly List<long> _queries = new();
    private bool _resolving;

    public void NewPacket() => _queries.Clear();

    // read side — delegate to the real catalog, and record every queried candidate this packet so Damage can
    // recover the RAW code deterministically (the raw is the query whose normalization == the emitted code).
    public bool SkillExists(long code)
    {
        if (!_resolving) _queries.Add(code);
        return dm.SkillExists(code);
    }
    public Mob? GetMob(int code) => dm.GetMob(code);
    public int? GetMobId(int instanceId) => dm.GetMobId(instanceId);
    public long CurrentEpoch() => 0;

    // sink — recover the raw code by matching normalization against the emitted (normalized) code, which
    // skips any pollution from other messages packed into the same assembled frame.
    public void Damage(string kind, ParsedDamagePacket p, bool saved, string? reason, int? mobCode)
    {
        _resolving = true;
        long raw = -1;
        foreach (long q in _queries)
        {
            int fallback = p.Dot ? (int)q / 100 : ((int)q / 10) * 10;
            if (DamageParsing.NormalizeDamageSkillCode((int)q, fallback, dm.SkillExists) == p.SkillCode) { raw = q; break; }
        }
        _resolving = false;
        Rows.Add((p.ActorId, raw, p.SkillCode, p.Dot, p.Specials.Contains(SpecialDamage.BACK), p.Damage, p.SwitchVariable & 0x0F, p.IsCrit));
    }
    public void Dispatch(int o, string? n, bool e, int l) { }
    public void UnknownOpcode(int o, bool e, int l) { }
    public void CompressedPacket(int l, bool e) { }
    public void ParserError(string s, string r) { }
    public void Battle(int t, int tg, int? mc, string? mn, bool a, string? r) { }
    public void Meta(string type, params (string Key, object? Value)[] f) { }

    // write side — no-op (we read the parser's emitted packets via Damage above)
    public readonly List<(int Target, int Code, long Start, long End, long Dur, int Actor)> Buffs = new();
    public void SaveMobId(int instanceId, int mobCode) { }
    public void SaveDamage(ParsedDamagePacket pdp, long epoch) { }
    public void SaveUseBuffCapture(int target, int code, long start, long end, long dur, int actor) => Buffs.Add((target, code, start, end, dur, actor));
    public void StartBattle(int target) { }
    public void EndBattle(int target) { }
    public void SaveNickname(int uid, string nickname, bool isExecutor, int server, int jobByte) { }
    public void SaveUserPower(int uid, int power) { }
    public void SaveSummon(int summonId, int ownerId) { }
    public void SaveMobHp(int instanceId, long hp) { }
    public void SaveUseBuff(int uid, int skillCode, long buffStart, long buffEnd, long duration, int actorId) => SaveUseBuffCapture(uid, skillCode, buffStart, buffEnd, duration, actorId);
    public readonly List<(int Skill, long Remaining, long At)> Cooldowns = new();
    public void SaveCooldown(int skillCode, long remainingMs, long arrivedAt, int actorId, bool fromCast = false) => Cooldowns.Add((skillCode, remainingMs, arrivedAt));
    public void RequestOfficialCharacterLookup(int uid) { }
    public void SavePartyRoster(IReadOnlyList<(string Nickname, int Server, int Slot)> members) { }
    public readonly List<(int Base, int Bonus)> Aethers = new();
    public readonly List<(int Base, int Bonus)> ShugoKeys = new();
    public void SaveAetherStatus(int baseVal, int bonus) => Aethers.Add((baseVal, bonus));
    public void SaveShugoKey(int baseVal, int bonus) => ShugoKeys.Add((baseVal, bonus));
    public void SaveFieldBossTimers(IReadOnlyList<(int Code, long TargetMs)> timers) { }
}
