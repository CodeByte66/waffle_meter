using System.Text.Json;

namespace WaffleMeter.Data;

/// <summary>One player skill the cooldown overlay can draw a slot for.</summary>
/// <param name="BaseCode">8-digit base code (the catalog key, also the icon file name).</param>
/// <param name="Job">Job band — the first two digits (11 검성 … 19 권성).</param>
/// <param name="Name">Display name, taken from <c>skills.json</c> (never synthesised from l10n keys).</param>
/// <param name="CatalogCooldownMs">The client table's cooldown. NOT used as the ring's denominator — see the
/// class remarks — only to decide "does this skill have a cooldown at all" and as a sanity ceiling.</param>
/// <param name="GroupId">Shared-cooldown group. Two skills with the same value share one cooldown.</param>
/// <param name="AutoLoadCount">Charge stacks (2–3) when a specialization turns the skill into a charge type,
/// 0 otherwise. Only a prior for the maximum: whether the character actually runs the skill as a charge type,
/// and how many charges it has right now, comes off the wire (0x3802 tail, 0x3822, 0x5100) — and a "full at N"
/// from the server overrides this value.</param>
/// <param name="Order">Stable position inside the job (base code ascending).</param>
/// <param name="IsStigma">스티그마 스킬인가 — 픽커가 직업 안에서 일반/스티그마 두 묶음으로 나눠 보여 준다.</param>
public readonly record struct CooldownSkillInfo(
    int BaseCode,
    int Job,
    string Name,
    long CatalogCooldownMs,
    int GroupId,
    int AutoLoadCount,
    int Order,
    bool IsStigma);

/// <summary>
/// The static half of the skill-cooldown overlay: which player skills have a cooldown, what they are called,
/// and which of them share one. A shipped snapshot of the client tables, regenerated per patch with
/// <c>dotnet/tools/cooldown-catalog-export.py</c>.
/// <para><b>What it deliberately does NOT provide is the cooldown length.</b> The client value is not the
/// player's cooldown: a cooldown-reduction stat scales it, skill level cuts it by up to 65%, and a
/// specialization can cut it further or zero it outright — 106 of the 249 skills cannot be resolved from the
/// tables at all. It is not even an upper bound (호법성 회전격 came over the wire at 22,200 ms against a table
/// value of 10,000). The wire is the authority: 0x3802 carries the character's real total on every cast and
/// 0x3847 corrects the remainder. The catalog is here for identity, not arithmetic.</para>
/// <para>Its second job is <see cref="GroupId"/>. Cooldowns are keyed by the client's shared-cooldown group,
/// not by folding the skill code, because both naive schemes fail: the raw wire code is a specialization
/// variant 99.4% of the time (so nothing would ever match), while <c>code / 10000 * 10000</c> collapses
/// 긴급 회피 onto 무기 장착 and merges skills that do not actually share a cooldown.</para>
/// <para><b>A skill with no shipped icon is not in here.</b> Verified in game on 2026-09-04: the 28 rows the
/// generator used to emit without an icon are all dummies or skills that no longer exist — the client table
/// still carries them, but no icon was ever assigned, and that absence turned out to be the tell. Filtering
/// them out at generation is what keeps the picker, the overlay and the job pre-fill consistent in one place.
/// The risk it buys is the opposite one: a genuinely new skill whose icon has not been extracted yet would
/// vanish silently, which is what the per-job minimums in <c>ShippedCooldownCatalogTests</c> are for.</para>
/// </summary>
public sealed class CooldownCatalog
{
    /// <summary>No catalog (asset missing). Group ids still fold, so the buff overlay behaves exactly as it did
    /// before the catalog existed; the cooldown overlay simply has no rows to name and stays empty.</summary>
    public static readonly CooldownCatalog Empty = new(new Dictionary<int, CooldownSkillInfo>(), new Dictionary<int, int>(), string.Empty);

    private readonly Dictionary<int, CooldownSkillInfo> _skills;      // baseCode -> info
    private readonly Dictionary<int, int> _gctOverride;               // wire code -> group id (only where folding is wrong)

    private CooldownCatalog(Dictionary<int, CooldownSkillInfo> skills, Dictionary<int, int> gctOverride, string generatedFrom)
    {
        _skills = skills;
        _gctOverride = gctOverride;
        GeneratedFrom = generatedFrom;
    }

    /// <summary>The datamine pass this snapshot came from, for diagnostics.</summary>
    public string GeneratedFrom { get; }

    /// <summary>Number of skills with a cooldown.</summary>
    public int Count => _skills.Count;

    /// <summary>Number of wire codes whose group id cannot be derived by folding.</summary>
    public int OverrideCount => _gctOverride.Count;

    /// <summary>The shared-cooldown group a wire skill code belongs to — the key everything else uses.
    /// <para>Falls back to the fold for anything the catalog does not know, so an absent asset degrades to the
    /// behaviour that shipped before it.</para></summary>
    public int GroupId(int skillCode)
    {
        int resolved = Resolve(skillCode);
        if (_skills.ContainsKey(resolved))
        {
            return resolved;
        }

        // A group id is itself often a specialization variant rather than the base row it belongs to — 1,148 of
        // the 1,487 override targets are not catalogued directly, and folding brings 655 of those home. Without
        // this second step those variants silently lose their row on the overlay. The rest genuinely have no
        // row (their skill has no cooldown, or is passive); returning the unfolded id for them is correct — it
        // just never matches a catalogue entry, which is exactly what "not shown" should look like.
        if (resolved is >= 11_000_000 and <= 19_999_999)
        {
            int folded = resolved / 10_000 * 10_000;
            if (_skills.ContainsKey(folded))
            {
                return folded;
            }
        }

        return resolved;
    }

    private int Resolve(int skillCode)
    {
        if (_gctOverride.TryGetValue(skillCode, out int direct))
        {
            return direct;
        }

        if (skillCode is < 11_000_000 or > 19_999_999)
        {
            return skillCode; // common / consumable / mob code — no job folding applies
        }

        int folded = skillCode / 10_000 * 10_000;
        return _gctOverride.TryGetValue(folded, out int viaBase) ? viaBase : folded;
    }

    /// <summary>Look up the row a group id draws as. A group id is the base code of its representative skill,
    /// so this is a direct hit for every skill the overlay can name.</summary>
    public bool TryGet(int groupId, out CooldownSkillInfo info) => _skills.TryGetValue(groupId, out info);

    /// <summary>All catalogued skills, for tests and diagnostics.</summary>
    public IReadOnlyCollection<CooldownSkillInfo> Skills => _skills.Values;

    /// <summary>Read the shipped <c>cooldown_catalog.json</c>. A malformed or unreadable file yields
    /// <see cref="Empty"/> rather than throwing — a broken asset must not stop the meter from starting.</summary>
    public static CooldownCatalog Load(string path)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;

            string generatedFrom = root.TryGetProperty("generatedFrom", out JsonElement gf) ? gf.GetString() ?? string.Empty : string.Empty;

            var skills = new Dictionary<int, CooldownSkillInfo>();
            if (root.TryGetProperty("skills", out JsonElement skillsEl) && skillsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in skillsEl.EnumerateObject())
                {
                    if (!int.TryParse(p.Name, out int baseCode))
                    {
                        continue;
                    }

                    JsonElement v = p.Value;
                    string name = v.TryGetProperty("n", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
                    if (name.Length == 0)
                    {
                        continue; // an unnamed row could only draw as a bare number — drop it
                    }

                    skills[baseCode] = new CooldownSkillInfo(
                        baseCode,
                        Int(v, "j"),
                        name,
                        Int(v, "cd"),
                        v.TryGetProperty("gct", out JsonElement g) && g.TryGetInt32(out int gct) && gct > 0 ? gct : baseCode,
                        Int(v, "auto"),
                        Int(v, "order"),
                        v.TryGetProperty("stig", out JsonElement st) && st.ValueKind == JsonValueKind.True);
                }
            }

            var overrides = new Dictionary<int, int>();
            if (root.TryGetProperty("gctOverride", out JsonElement ovEl) && ovEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in ovEl.EnumerateObject())
                {
                    if (int.TryParse(p.Name, out int code) && p.Value.TryGetInt32(out int gid) && gid > 0)
                    {
                        overrides[code] = gid;
                    }
                }
            }

            return skills.Count == 0 ? Empty : new CooldownCatalog(skills, overrides, generatedFrom);
        }
        catch
        {
            return Empty;
        }
    }

    private static int Int(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement el) && el.TryGetInt32(out int v) ? v : 0;
}
