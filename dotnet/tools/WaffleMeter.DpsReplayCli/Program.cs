using System.Globalization;
using System.Text.Json;
using WaffleMeter.Capture;
using WaffleMeter.Capture.Corpus;
using WaffleMeter.Data;

// Phase 1 DPS parity: replay the corpus through the ported DataManager + DpsCalculator with a
// simulated clock (mirroring the Kotlin GoldenGenerator), then diff the saved battle history against
// dps-golden.json. Buff catalog/blacklist are intentionally NOT loaded (the Kotlin generator's
// backslash resource path fails to load them, so buff names resolve via the skill fallback).
// Usage: dotnet run --project <DpsReplayCli> -c Release -- <corpus.jsonl> <skills.json> <dps-golden.json>

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: ... <corpus.jsonl> <skills.json> <dps-golden.json>");
    return 1;
}

string corpusPath = args[0];
string skillsPath = args[1];
string goldenPath = args[2];
string mobsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(skillsPath)) ?? ".", "mobs.json");

const double Eps = 1e-6;

string jsonDir = Path.GetDirectoryName(Path.GetFullPath(skillsPath)) ?? ".";

var dm = new DataManager();
dm.LoadMobs(ReferenceJson.LoadMobs(mobsPath));
dm.LoadSkills(ReferenceJson.LoadSkills(skillsPath));
foreach (string buffFile in new[] { "buff.json", "buff_custom.json" })
{
    string bp = Path.Combine(jsonDir, buffFile);
    if (File.Exists(bp))
    {
        dm.LoadBuffs(ReferenceJson.LoadBuffs(bp));
    }
}

string blacklistPath = Path.Combine(jsonDir, "buff_blacklist.json");
if (File.Exists(blacklistPath))
{
    dm.LoadBuffBlacklist(ReferenceJson.LoadBuffBlacklist(blacklistPath));
}

long simNow = 0;
dm.Clock = () => simNow;

var aligner = new PacketAlignmenter();
StreamAssembler assembler = null!;
var calculator = new DpsCalculator(dm, () => { assembler.Flush(); aligner.Reset(); });
var processor = new StreamProcessor(NullStreamProcessorSink.Instance, dm);
assembler = new StreamAssembler((packet, at) => processor.OnPacketReceived(packet, at));

string currentIp = "";
foreach (string line in CaptureCorpusReader.ReadLines(corpusPath))
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    using JsonDocument doc = JsonDocument.Parse(line);
    JsonElement root = doc.RootElement;
    if (!root.TryGetProperty("type", out JsonElement typeEl) || typeEl.GetString() != "capture")
    {
        continue;
    }

    long seq = root.GetProperty("seq").GetInt64();
    long at = root.GetProperty("at").GetInt64();
    string ip = root.TryGetProperty("ip", out JsonElement ipEl) ? ipEl.GetString() ?? "" : "";
    byte[] data = Convert.FromBase64String(root.GetProperty("data").GetString() ?? "");

    simNow = at;
    if (ip != currentIp)
    {
        currentIp = ip;
        aligner.Reset();
    }

    foreach (AlignedChunk chunk in aligner.Feed(seq, data, at))
    {
        assembler.ProcessChunk(chunk.Data, chunk.ArrivedAt);
    }

    calculator.GetDps();
}

calculator.ResetDataStorage();

var battles = new List<DpsLog>();
int battleCount = dm.RecentBattleList().Count;
for (int i = 0; i < battleCount; i++)
{
    if (dm.BattleLog(i) is { } log)
    {
        battles.Add(log);
    }
}

using JsonDocument goldenDoc = JsonDocument.Parse(File.ReadAllText(goldenPath));
JsonElement golden = goldenDoc.RootElement;

Console.WriteLine($"=== DPS replay vs golden: {Path.GetFileName(goldenPath)} ===");
Console.WriteLine($"  .NET battles {battles.Count}   golden {golden.GetArrayLength()}");

int rc = 0;
var diffs = new List<string>();

if (battles.Count != golden.GetArrayLength())
{
    diffs.Add($"battle count: .NET {battles.Count} vs golden {golden.GetArrayLength()}");
}

int n = Math.Min(battles.Count, golden.GetArrayLength());
for (int i = 0; i < n; i++)
{
    CompareBattle(i, battles[i], golden[i], diffs);
}

if (diffs.Count == 0)
{
    Console.WriteLine($"  RESULT: ALL DPS PARITY OK — {n} battles, per-player damage/dps/contribution/skills/buffs match");
}
else
{
    rc = 2;
    Console.WriteLine($"  RESULT: {diffs.Count} divergence(s):");
    foreach (string d in diffs.Take(40))
    {
        Console.WriteLine($"    - {d}");
    }
}

return rc;

void CompareBattle(int idx, DpsLog got, JsonElement exp, List<string> outDiffs)
{
    string p = $"battle#{idx}";
    JsonElement er = exp.GetProperty("report");

    Eq(outDiffs, $"{p}.battleStart", got.Report.BattleStart, er.GetProperty("battleStart").GetInt64());
    Eq(outDiffs, $"{p}.battleEnd", got.Report.BattleEnd, er.GetProperty("battleEnd").GetInt64());

    // target
    if (er.TryGetProperty("target", out JsonElement et) && et.ValueKind == JsonValueKind.Object)
    {
        MobInfo? gt = got.Report.Target;
        if (gt == null)
        {
            outDiffs.Add($"{p}.target: .NET null vs golden present");
        }
        else
        {
            Eq(outDiffs, $"{p}.target.id", gt.Id, et.GetProperty("id").GetInt32());
            Eq(outDiffs, $"{p}.target.mob.code", gt.Mob.Code, et.GetProperty("mob").GetProperty("code").GetInt32());
            Eq(outDiffs, $"{p}.target.maxHp", gt.MaxHp, et.GetProperty("maxHp").GetInt64());
        }
    }

    // information by uid
    JsonElement ei = er.GetProperty("information");
    var gotUids = got.Report.Information.Keys.OrderBy(x => x).ToList();
    var expUids = ei.EnumerateObject().Select(x => int.Parse(x.Name, CultureInfo.InvariantCulture)).OrderBy(x => x).ToList();
    if (!gotUids.SequenceEqual(expUids))
    {
        outDiffs.Add($"{p}.information uids: .NET [{string.Join(",", gotUids)}] vs golden [{string.Join(",", expUids)}]");
    }

    foreach (int uid in gotUids.Intersect(expUids))
    {
        DpsInformation gi = got.Report.Information[uid];
        JsonElement gj = ei.GetProperty(uid.ToString(CultureInfo.InvariantCulture));
        EqD(outDiffs, $"{p}.info[{uid}].amount", gi.Amount, gj.GetProperty("amount").GetDouble());
        EqD(outDiffs, $"{p}.info[{uid}].dps", gi.Dps, gj.GetProperty("dps").GetDouble());
        EqD(outDiffs, $"{p}.info[{uid}].contribution", gi.Contribution, gj.GetProperty("contribution").GetDouble());
        EqD(outDiffs, $"{p}.info[{uid}].entireContribution", gi.EntireContribution, gj.GetProperty("entireContribution").GetDouble());
    }

    // contributors by id
    Dictionary<int, JsonElement> ec = er.GetProperty("contributors")
        .EnumerateArray().ToDictionary(u => u.GetProperty("id").GetInt32(), u => u);
    foreach (User u in got.Report.Contributors)
    {
        if (!ec.TryGetValue(u.Id, out JsonElement eu))
        {
            outDiffs.Add($"{p}.contributor {u.Id} missing in golden");
            continue;
        }

        Eq(outDiffs, $"{p}.contrib[{u.Id}].power", u.Power, eu.GetProperty("power").GetInt32());
        string? gJob = u.Job?.ClassName();
        string? eJob = eu.TryGetProperty("job", out JsonElement ej) && ej.ValueKind == JsonValueKind.String ? ej.GetString() : null;
        if (gJob != eJob)
        {
            outDiffs.Add($"{p}.contrib[{u.Id}].job: .NET {gJob ?? "null"} vs golden {eJob ?? "null"}");
        }
    }

    if (got.Report.Contributors.Count != ec.Count)
    {
        outDiffs.Add($"{p}.contributors count: .NET {got.Report.Contributors.Count} vs golden {ec.Count}");
    }

    // skill details
    CompareSkillDetails(p, got.SkillDetails, exp.GetProperty("skillDetails"), outDiffs);

    // buff rates (per uid) + boss buff rates
    JsonElement ebr = exp.GetProperty("buffRates");
    foreach (KeyValuePair<int, List<OperatingData>> kv in got.BuffRates)
    {
        JsonElement arr = ebr.TryGetProperty(kv.Key.ToString(CultureInfo.InvariantCulture), out JsonElement e) ? e : default;
        CompareOperating($"{p}.buffRates[{kv.Key}]", kv.Value, arr, outDiffs);
    }

    CompareOperating($"{p}.bossBuffRates", got.BossBuffRates, exp.GetProperty("bossBuffRates"), outDiffs);
}

void CompareSkillDetails(string p, Dictionary<int, Dictionary<string, AnalyzedSkill>> got, JsonElement exp, List<string> outDiffs)
{
    foreach (KeyValuePair<int, Dictionary<string, AnalyzedSkill>> uk in got)
    {
        if (!exp.TryGetProperty(uk.Key.ToString(CultureInfo.InvariantCulture), out JsonElement eu))
        {
            outDiffs.Add($"{p}.skillDetails[{uk.Key}] missing in golden");
            continue;
        }

        foreach (KeyValuePair<string, AnalyzedSkill> sk in uk.Value)
        {
            if (!eu.TryGetProperty(sk.Key, out JsonElement es))
            {
                outDiffs.Add($"{p}.skill[{uk.Key}/{sk.Key}] missing in golden");
                continue;
            }

            AnalyzedSkill a = sk.Value;
            Eq(outDiffs, $"{p}.skill[{uk.Key}/{sk.Key}].damageAmount", a.DamageAmount, es.GetProperty("damageAmount").GetInt32());
            Eq(outDiffs, $"{p}.skill[{uk.Key}/{sk.Key}].dotDamageAmount", a.DotDamageAmount, es.GetProperty("dotDamageAmount").GetInt32());
            Eq(outDiffs, $"{p}.skill[{uk.Key}/{sk.Key}].times", a.Times, es.GetProperty("times").GetInt32());
            Eq(outDiffs, $"{p}.skill[{uk.Key}/{sk.Key}].dotTimes", a.DotTimes, es.GetProperty("dotTimes").GetInt32());
            Eq(outDiffs, $"{p}.skill[{uk.Key}/{sk.Key}].critTimes", a.CritTimes, es.GetProperty("critTimes").GetInt32());
            Eq(outDiffs, $"{p}.skill[{uk.Key}/{sk.Key}].multiHitTimes", a.MultiHitTimes, es.GetProperty("multiHitTimes").GetInt32());
        }
    }
}

void CompareOperating(string p, List<OperatingData> got, JsonElement exp, List<string> outDiffs)
{
    int expLen = exp.ValueKind == JsonValueKind.Array ? exp.GetArrayLength() : 0;
    if (got.Count != expLen)
    {
        outDiffs.Add($"{p} count: .NET {got.Count} vs golden {expLen}");
        return;
    }

    Dictionary<(int, int), double> eByKey = exp.ValueKind == JsonValueKind.Array
        ? exp.EnumerateArray().ToDictionary(
            o => (o.GetProperty("code").GetInt32(), o.GetProperty("actorId").GetInt32()),
            o => o.GetProperty("operatingRate").GetDouble())
        : new();

    foreach (OperatingData o in got)
    {
        if (!eByKey.TryGetValue((o.Code, o.ActorId), out double rate))
        {
            outDiffs.Add($"{p} entry (code={o.Code}, actor={o.ActorId}) missing in golden");
            continue;
        }

        EqD(outDiffs, $"{p}(code={o.Code},actor={o.ActorId}).rate", o.OperatingRate, rate);
    }
}

void Eq(List<string> outDiffs, string what, long got, long exp)
{
    if (got != exp)
    {
        outDiffs.Add($"{what}: .NET {got} vs golden {exp}");
    }
}

void EqD(List<string> outDiffs, string what, double got, double exp)
{
    if (Math.Abs(got - exp) > Eps)
    {
        outDiffs.Add($"{what}: .NET {got:R} vs golden {exp:R}");
    }
}
