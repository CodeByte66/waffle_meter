using System.Text.Json;
using WaffleMeter.Data;
using WaffleMeter.Services;
using Xunit;

namespace WaffleMeter.Stats.Tests;

/// <summary>
/// 업로드 스킵 카운터가 <b>앱 재시작을 넘어 살아남는다</b>는 계약.
/// <para>🔑 종전에는 메모리 전용이라 앱을 끄면 0 으로 돌아갔다. 그 결과 "내 전투가 왜 안 올라갔지"를 사후에
/// 확인할 수단이 어디에도 없었다 — 설정 화면에서 <b>이번 세션</b> 값을 눈으로 읽는 것이 전부였고, 서버 쪽은
/// 애초에 도달하지 못한 전투라 볼 수 없다. 즉 미터도 웹도 결손률을 못 재는 상태였다(2026-09-19 실측 확인).</para>
/// </summary>
public sealed class SkipCounterPersistenceTests : IDisposable
{
    private readonly string _dir;

    public SkipCounterPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wm_skipcounts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    // ⚠️ 카운터 파일은 `_dir` 바로 밑이 아니라 PropertyHandler 가 쓰는 AppData 네임스페이스
    // (`waffle_meter.v1.4`) 밑에 있다. 그 이름을 테스트에 박아두면 리네임 금지 규칙이 두 군데로
    // 흩어지니, 경로는 PropertyHandler 에게 직접 물어본다.
    private string CountsPath() =>
        Path.Combine(new PropertyHandler(_dir).AppDirectory(), "stats-upload", "skip-counts.json");

    private void WriteCounts(object doc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CountsPath())!);
        File.WriteAllText(CountsPath(), JsonSerializer.Serialize(doc));
    }

    private StatsUploadStatus StatusFromDisk()
    {
        var props = new PropertyHandler(_dir);
        var dm = new DataManager();
        // 네트워크를 타지 않는다 — 이 테스트가 보는 건 생성자의 카운터 로드뿐이다.
        var api = new StatsApiClient(() => "install-test", (_, _, _, _) => new StatsHttpResponse(200, "{\"ok\":true}"));
        var builder = new StatsPayloadBuilder(dm, () => false);
        var consent = new StatsConsentManager(props, dm, api, () => builder.OwnCharacter());

        using var queue = new StatsUploadQueue(consent, builder, api, dm, props,
            dispatch: job => job(), killRecheckDelay: () => { }, clock: () => 2_000_000, retryDelay: _ => { });
        return queue.Status();
    }

    [Fact]
    public void Previous_session_counts_are_carried_forward()
    {
        WriteCounts(new
        {
            since = 1_000_000L,
            updatedAt = 1_500_000L,
            uploaded = 12,
            skipped = 7,
            failed = 2,
            reasons = new Dictionary<string, int>
            {
                ["participant_power_unresolved"] = 5,
                ["own_power_unresolved"] = 2,
            },
        });

        StatsUploadStatus status = StatusFromDisk();

        Assert.Equal(12, status.Uploaded);
        Assert.Equal(7, status.Skipped);
        Assert.Equal(2, status.Failed);
        Assert.NotNull(status.SkipReasons);
        Assert.Equal(5, status.SkipReasons!.Single(r => r.Reason == "participant_power_unresolved").Count);
        Assert.Equal(2, status.SkipReasons.Single(r => r.Reason == "own_power_unresolved").Count);
    }

    /// <summary>카운터 파일이 손상돼도 기동을 막지 않는다 — 0 부터 다시 센다.</summary>
    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"reasons\":\"nope\"}")]
    public void A_corrupt_counter_file_does_not_block_startup(string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CountsPath())!);
        File.WriteAllText(CountsPath(), body);

        StatsUploadStatus status = StatusFromDisk();

        Assert.Equal(0, status.Uploaded);
        Assert.Equal(0, status.Skipped);
    }

    [Fact]
    public void A_missing_counter_file_is_a_clean_start()
    {
        StatsUploadStatus status = StatusFromDisk();

        Assert.Equal(0, status.Uploaded);
        Assert.Equal(0, status.Skipped);
        Assert.Equal(0, status.Failed);
    }
}
