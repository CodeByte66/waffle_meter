using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 아이콘은 <b>두 곳이 맞아야</b> 화면에 뜬다: <c>SkillIconManifest.Codes</c> 에 코드가 있어야
/// <c>JoinIcons.Skill</c> 이 pack URI 를 만들고, 그 URI 가 가리키는
/// <c>Assets/SkillIcons/&lt;code&gt;.png</c> 가 실려 있어야 그림이 나온다. 둘 중 하나만 있으면 실패가
/// 조용하다 — 매니페스트에만 있으면 TryLoad 가 null 을 삼켜 24px 빈칸이 되고(전투 상세 &gt; 버프
/// 업타임 '그 외' 섹션이 소모품 버프에 대해 정확히 이랬다), PNG 만 있으면 아무도 안 찾는 파일이
/// 설치본에 실린다.
///
/// <para>매니페스트는 App.Wpf 의 <c>internal</c> 이고 이 프로젝트는 App.Wpf 를 참조하지 않으므로
/// <see cref="ShippedVoicePackTests"/> 의 <c>IconCodes()</c> 와 <b>같은 방식</b>으로 소스 텍스트에서
/// 읽는다(InternalsVisibleTo 를 새로 뚫지 않는다). 저장소 루트를 걸어 올라가는 헬퍼도 이 프로젝트의
/// 관례대로 여기 한 벌 둔다 — <c>ReleaseNotesConsistencyTests</c>·<c>VoicePlaybackInvariantTests</c> 가
/// 같은 이유로 각자 갖고 있다.</para>
/// </summary>
public sealed class ShippedSkillIconAssetsTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "dotnet", "Assets", "SkillIcons")))
        {
            dir = dir.Parent;
        }

        return dir ?? throw new InvalidOperationException("dotnet/Assets/SkillIcons 를 찾지 못했습니다.");
    }

    private static string RepoPath(params string[] parts) => Path.Combine(RepoRoot().FullName, Path.Combine(parts));

    private static string ReadSource(params string[] parts) => File.ReadAllText(RepoPath(parts));

    private static string IconsDir() => RepoPath("dotnet", "Assets", "SkillIcons");

    /// <summary>초기화자 본문. 앵커 <c>Codes = new()</c> 는 ShippedVoicePackTests 도 쓴다 — 지우지 말 것.</summary>
    private static string ManifestBody()
    {
        string src = ReadSource("dotnet", "src", "WaffleMeter.App.Wpf", "SkillIconManifest.cs");
        int at = src.IndexOf("Codes = new()", StringComparison.Ordinal);
        Assert.True(at >= 0, "SkillIconManifest 에서 'Codes = new()' 앵커를 찾지 못했습니다.");
        return src[at..];
    }

    private static HashSet<int> ManifestCodes(string body) =>
        Regex.Matches(body, @"\b(\d{7,9})\b").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();

    private static HashSet<int> ShippedCodes() =>
        Directory.GetFiles(IconsDir(), "*.png")
            .Select(f => int.Parse(Path.GetFileNameWithoutExtension(f)))
            .ToHashSet();

    [Fact]
    public void The_manifest_and_the_shipped_pngs_are_the_same_set()
    {
        HashSet<int> manifest = ManifestCodes(ManifestBody());
        HashSet<int> shipped = ShippedCodes();

        int[] blank = manifest.Except(shipped).Order().ToArray();
        int[] orphan = shipped.Except(manifest).Order().ToArray();

        Assert.True(blank.Length == 0,
            $"매니페스트에 있는데 PNG 가 없는 코드 {blank.Length}건 — 화면에 빈칸으로 뜹니다: "
            + string.Join(", ", blank.Take(10)));
        Assert.True(orphan.Length == 0,
            $"PNG 는 실렸는데 매니페스트에 없는 코드 {orphan.Length}건 — 아무도 찾지 않는 파일입니다: "
            + string.Join(", ", orphan.Take(10)));
    }

    /// <summary>
    /// <see cref="ShippedVoicePackTests"/> 의 <c>IconCodes()</c> 는 초기화자 안의 <b>모든</b> 7~9자리
    /// 숫자를 코드로 읽는다 — 주석에 예시 코드를 적으면 그 테스트가 있지도 않은 아이콘을 있다고 믿는다.
    /// 그 함정을 문서가 아니라 테스트로 막는다: 주석을 지우고 센 코드가 그대로여야 한다.
    /// </summary>
    [Fact]
    public void No_number_in_the_manifest_comments_can_pass_for_a_code()
    {
        string body = ManifestBody();
        string stripped = Regex.Replace(body, @"//[^\n]*", string.Empty);

        int[] ghosts = ManifestCodes(body).Except(ManifestCodes(stripped)).Order().ToArray();
        Assert.True(ghosts.Length == 0,
            $"주석 안의 숫자가 아이콘 코드로 읽히고 있습니다 {ghosts.Length}건: {string.Join(", ", ghosts.Take(10))}");
    }

    /// <summary>실린 아이콘은 48x48 이다. 데이터마인 원본은 256x256 이라 리샘플을 빠뜨리면 설치본이
    /// 조용히 수십 배로 부푼다(과거에 한 실수). PNG IHDR 만 읽으면 되므로 이미지 라이브러리가 필요 없다.</summary>
    [Fact]
    public void Every_shipped_icon_is_48_by_48()
    {
        string[] wrong = Directory.GetFiles(IconsDir(), "*.png")
            .Where(f => PngSize(f) != (48, 48))
            .Select(f => $"{Path.GetFileName(f)} {PngSize(f)}")
            .ToArray();

        Assert.True(wrong.Length == 0, $"48x48 이 아닌 아이콘 {wrong.Length}개: {string.Join(", ", wrong.Take(10))}");
    }

    private static (int Width, int Height) PngSize(string path)
    {
        byte[] head = new byte[24];
        using FileStream fs = File.OpenRead(path);
        Assert.Equal(head.Length, fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false));
        return (BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16, 4)),
                BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20, 4)));
    }
}
