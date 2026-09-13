using System.Text.RegularExpressions;
using WaffleMeter.App.Core;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// The packs are addressed by a hash of the spoken line, so a wording change orphans every clip for that line
/// — and does it silently: the alert still fires, just a beat late over the network, or as a chime offline.
/// Nothing in the build joins the wording to the rendered files.
///
/// <para>So these tests reconstruct what the app can actually say — from the same catalogues the app reads,
/// and through the same filters the app applies — and require a clip to exist for every one. Reading the
/// source is the same trick <see cref="SettingsKeyCatalogTests"/> uses, and for the same reason: a
/// hand-maintained duplicate of the wording would drift with the thing it is supposed to be checking.</para>
///
/// <para><b>Why the buff axis reads buff_catalog.json.</b> Because that file is now the buff list itself —
/// <c>BuffPickerCatalog</c> returns exactly its contents and the overlay refuses to draw anything outside it.
/// Reading it used to be the bug rather than the fix: back when the picker listed <c>observed ∪ curated</c>,
/// deriving the expected lines from the curated file meant asking the packs to contain exactly what the packs
/// had been baked from, so the test re-confirmed the bake instead of checking it and 살성 '환영 분신' plus
/// 300-odd others fell through to the network. The same read is correct now only because the two sets were
/// made one.</para>
/// </summary>
public sealed class ShippedVoicePackTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "dotnet", "Assets", "voice")))
        {
            dir = dir.Parent;
        }

        return dir ?? throw new InvalidOperationException("dotnet/Assets/voice 를 찾지 못했습니다.");
    }

    private static string RepoPath(params string[] parts) => Path.Combine(RepoRoot().FullName, Path.Combine(parts));

    private static string VoiceRoot() => RepoPath("dotnet", "Assets", "voice");

    private static string ReadSource(params string[] parts) => File.ReadAllText(RepoPath(parts));

    /// <summary>Every line the app can speak from a fixed catalogue, built the way the app builds it.</summary>
    private static IEnumerable<string> ExpectedLines()
    {
        foreach (int lead in new[] { 10, 5, 1 })
        {
            yield return $"슈고 페스타, {lead}분 뒤 시작합니다";
            // 카이라는 lead 0 줄이 없다 — KairaAlarm.EnabledLeads 가 10/5/1 만 넣어 SetKaira(0) 이 도달 불가다.
            yield return $"{SpokenName.Of(KairaName())}, {lead}분 뒤 출현합니다";
        }

        yield return "슈고 페스타, 지금 시작합니다";

        // A custom alarm's title is free text and cannot be baked, but the default title is not: the composer
        // in AlarmToastViewModel substitutes "알람" for a blank one, and the 새 알람 field is pre-filled with
        // it, so this exact line is the default path rather than an edge case.
        yield return "알람, 지금입니다";

        foreach (string boss in FieldBossNames())
        {
            foreach (int lead in new[] { 5, 10, 30 })
            {
                yield return $"{SpokenName.Of(boss)}, {lead}분 뒤 리젠";
            }
        }

        foreach (string buff in BuffNames())
        {
            yield return $"{buff} 온";
            yield return $"{buff} 오프";
            // "버프 종료 3초 전 알림"이 켜졌을 때의 문구. 켜지면 만료 직전 "오프"를 대체한다(둘 다 나가면 같은
            // 버프를 2.8초 간격으로 두 번 말한다) — 그래도 두 문구 모두 앱이 말할 수 있는 문구이므로 둘 다
            // 구워져 있어야 한다. 리드 값(3초)은 문구에 넣지 않는다: 넣으면 리드를 바꾸는 순간 클립 전체가
            // 키를 잃는다(파일명이 sha256(문구)다).
            yield return $"{buff} 오프 예정";
        }
    }

    /// <summary>감시자 카이라 — 리젠 타이머가 아니라 4시간 격자 전용 큐(<c>SetKaira</c>)를 쓰므로 리젠
    /// 문구 목록에서는 빠지지만, 그 전용 문구를 만들 때 <b>이름은 여기서 가져와야 한다</b>. 손으로 다시
    /// 적어 두면 몹 이름이 교정돼도 테스트와 앱이 같은 옛 이름을 공유해 그린인 채로 팩만 고아가 된다.</summary>
    private const string ScheduledSpawnCode = "2600089";

    private static Dictionary<string, string> CatalogueNamesByCode()
    {
        string src = ReadSource("dotnet", "src", "WaffleMeter.Capture", "FieldBossCatalog.cs");
        string body = src[src.IndexOf("Bosses =", StringComparison.Ordinal)
                          ..src.IndexOf("public static bool HasOwnAlarm", StringComparison.Ordinal)];
        var byCode = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(body, @"new\(\s*(\d+),\s*""([^""]+)"""))
        {
            byCode[m.Groups[1].Value] = m.Groups[2].Value;
        }

        Assert.True(byCode.ContainsKey(ScheduledSpawnCode),
            "FieldBossCatalog 에서 감시자 카이라(2600089) 행을 찾지 못했습니다 — 표 형식이 바뀌었는지 보세요.");
        return byCode;
    }

    private static string KairaName() => CatalogueNamesByCode()[ScheduledSpawnCode];

    private static IEnumerable<string> FieldBossNames() =>
        CatalogueNamesByCode()
            .Where(kv => kv.Key != ScheduledSpawnCode)
            .Select(kv => kv.Value)
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The skill-icon codes that ship as PNGs. Read out of the source because <c>SkillIconManifest</c> is
    /// internal to App.Wpf, which this project does not reference — the same reason FieldBossNames parses text.
    /// </summary>
    private static HashSet<int> IconCodes()
    {
        string src = ReadSource("dotnet", "src", "WaffleMeter.App.Wpf", "SkillIconManifest.cs");
        string body = src[src.IndexOf("Codes = new()", StringComparison.Ordinal)..];
        return Regex.Matches(body, @"\b(\d{7,9})\b").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
    }

    /// <summary>Every buff name that can be given a voice — read through the app's own loader so a change in
    /// how the catalogue parses cannot drift from what the test believes. Names repeat across jobs (회생의
    /// 계약 and its 회계·회복 rider exist for five, 균형의 갑옷 for two), and the spoken line is the name, so
    /// they collapse to one clip.</summary>
    private static IEnumerable<string> BuffNames() =>
        ReferenceJson.LoadBuffCatalog(RepoPath("dotnet", "Assets", "json", "buff_catalog.json")).Catalog
            .Select(x => x.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The picker drops any row whose code has no bundled icon, so a catalogued buff without one is
    /// invisible — no row to switch on, and (since the overlay now follows the catalogue) a slot drawn with a
    /// blank circle. Cheap to check here, and the failure mode is silent otherwise.</summary>
    [Fact]
    public void Every_catalogued_buff_has_an_icon()
    {
        HashSet<int> icons = IconCodes();
        bool HasIcon(int code) =>
            icons.Contains(code)
            || (code is >= 11_000_000 and <= 19_999_999 && icons.Contains(code / 10_000 * 10_000));

        int[] blind = ReferenceJson.LoadBuffCatalog(RepoPath("dotnet", "Assets", "json", "buff_catalog.json")).Catalog
            .Select(x => x.Code)
            .Where(c => !HasIcon(c))
            .ToArray();

        Assert.True(blind.Length == 0, $"아이콘 없는 카탈로그 항목 {blind.Length}건: {string.Join(", ", blind.Take(10))}");
    }

    /// <summary>
    /// Lines that are known to have no clip yet. Rendering a pack needs the local voice model, so the gap is
    /// closed in batches; this file is what keeps the test a regression guard in the meantime instead of one
    /// permanently red assertion. A line is removed from here the moment it is baked — the tests below
    /// enforce both directions so it cannot rot into a place where misses go to be forgotten.
    /// </summary>
    private static HashSet<string> UnbakedBaseline()
    {
        string path = Path.Combine(VoiceRoot(), "_source", "unbaked.txt");
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool HasClip(string pack, string line) =>
        File.Exists(Path.Combine(VoiceRoot(), pack, BakedVoicePack.FileNameFor(line)));

    [Fact]
    public void Every_line_the_app_can_speak_has_a_clip_in_every_pack()
    {
        HashSet<string> unbaked = UnbakedBaseline();
        string[] missing = (from pack in BakedVoicePack.All
                            from line in ExpectedLines().Distinct(StringComparer.Ordinal)
                            where !HasClip(pack, line) && !unbaked.Contains(line)
                            select $"{pack} / {line}").ToArray();

        Assert.True(missing.Length == 0,
            $"음성 팩에 없는 문구 {missing.Length}건 — 문구를 바꿨다면 그 줄을 다시 구워야 합니다.\n  "
            + string.Join("\n  ", missing.Take(10)));
    }

    /// <summary>The baseline is an admission of a gap, not a suppression list: once a line is baked into both
    /// packs it has to leave, or the next rename of that line would land in a hole nobody is watching.</summary>
    [Fact]
    public void The_unbaked_baseline_lists_nothing_that_is_already_baked()
    {
        string[] stale = UnbakedBaseline()
            .Where(line => BakedVoicePack.All.All(pack => HasClip(pack, line)))
            .ToArray();

        Assert.True(stale.Length == 0,
            $"이미 구워진 문구가 unbaked.txt 에 남아 있습니다 {stale.Length}건 — 지워 주세요.\n  "
            + string.Join("\n  ", stale.Take(10)));
    }

    /// <summary>A baseline entry that no longer matches a line the app can say means the wording moved and the
    /// entry was left behind — exactly the drift the hash addressing makes invisible.</summary>
    [Fact]
    public void The_unbaked_baseline_lists_nothing_the_app_can_no_longer_say()
    {
        var speakable = ExpectedLines().ToHashSet(StringComparer.Ordinal);
        string[] orphaned = UnbakedBaseline().Where(l => !speakable.Contains(l)).ToArray();

        Assert.True(orphaned.Length == 0,
            $"앱이 더 이상 말하지 않는 문구가 unbaked.txt 에 있습니다 {orphaned.Length}건 — 문구가 바뀌었는지 확인하세요.\n  "
            + string.Join("\n  ", orphaned.Take(10)));
    }

    /// <summary>A clip nothing asks for is dead weight in the installer, and usually the fossil of an old
    /// wording that was re-rendered under a new key.</summary>
    [Fact]
    public void No_pack_carries_a_clip_nothing_asks_for()
    {
        var wanted = ExpectedLines().Select(BakedVoicePack.FileNameFor).ToHashSet(StringComparer.Ordinal);
        foreach (string pack in BakedVoicePack.All)
        {
            string[] orphans = Directory.GetFiles(Path.Combine(VoiceRoot(), pack), "*.mp3")
                .Select(Path.GetFileName)
                .Where(f => !wanted.Contains(f!))
                .ToArray()!;
            Assert.True(orphans.Length == 0, $"{pack}: 아무도 찾지 않는 클립 {orphans.Length}개");
        }
    }

    /// <summary>The preview button is the one place a user hears the pack on purpose; a near-miss there
    /// demonstrates the online fallback while looking like it worked.</summary>
    [Fact]
    public void The_voice_preview_button_speaks_a_line_the_pack_contains()
    {
        string src = ReadSource("dotnet", "src", "WaffleMeter.App.Wpf", "SettingsViewModel.cs");
        Match m = Regex.Match(src, @"TestTts\(\)\s*=>\s*TtsSpeech\.Speak\(""([^""]+)""");
        Assert.True(m.Success, "TestTts 의 발화 문구를 찾지 못했습니다 — 아래 정규식을 함께 고치세요.");
        Assert.Contains(m.Groups[1].Value, ExpectedLines());
        Assert.All(BakedVoicePack.All, pack => Assert.True(HasClip(pack, m.Groups[1].Value)));
    }

    /// <summary>Clips are all short single utterances; an empty or truncated render would slip past a
    /// file-exists check.</summary>
    [Fact]
    public void No_clip_is_empty_or_absurdly_short()
    {
        foreach (string pack in BakedVoicePack.All)
        {
            foreach (string f in Directory.GetFiles(Path.Combine(VoiceRoot(), pack), "*.mp3"))
            {
                Assert.True(new FileInfo(f).Length > 2000, $"{pack}/{Path.GetFileName(f)} 가 너무 작습니다");
            }
        }
    }

    [Fact]
    public void Both_packs_hold_the_same_lines()
    {
        var sets = BakedVoicePack.All
            .Select(p => Directory.GetFiles(Path.Combine(VoiceRoot(), p), "*.mp3")
                .Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal))
            .ToArray();
        Assert.Equal(sets[0].Count, sets[1].Count);
        Assert.Empty(sets[0].Except(sets[1]));
    }
}
