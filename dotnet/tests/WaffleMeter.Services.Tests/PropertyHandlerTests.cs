using System.Text;
using WaffleMeter.Services;
using Xunit;

namespace WaffleMeter.Services.Tests;

public sealed class PropertyHandlerTests : IDisposable
{
    private const string AppName = "waffle_meter.v1.4";
    private readonly string _tempAppData;

    public PropertyHandlerTests()
    {
        _tempAppData = Path.Combine(Path.GetTempPath(), "wm_ph_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempAppData);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempAppData, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void SetProperty_persists_across_instances()
    {
        var first = new PropertyHandler(_tempAppData);
        first.SetProperty("opacity", "0.8");
        first.SetProperty("isAutoHide", "true");

        var second = new PropertyHandler(_tempAppData);
        Assert.Equal("0.8", second.GetProperty("opacity"));
        Assert.Equal("true", second.GetProperty("isAutoHide"));
        Assert.Equal(Path.Combine(_tempAppData, AppName), second.AppDirectory());
    }

    [Fact]
    public void GetProperty_returns_default_when_missing()
    {
        var ph = new PropertyHandler(_tempAppData);
        Assert.Null(ph.GetProperty("nope"));
        Assert.Equal("fallback", ph.GetProperty("nope", "fallback"));
    }

    [Fact]
    public void Legacy_settings_are_copied_forward_once()
    {
        string legacyDir = Path.Combine(_tempAppData, "waffle_meter.v1.3");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "settings.properties"), "carried=over\n", Encoding.Latin1);

        var ph = new PropertyHandler(_tempAppData);

        Assert.Equal("over", ph.GetProperty("carried"));
        Assert.True(File.Exists(Path.Combine(_tempAppData, AppName, "settings.properties")));
    }

    [Fact]
    public void Ascii_values_are_unaffected_by_the_euckr_requantize()
    {
        var ph = new PropertyHandler(_tempAppData);
        ph.SetProperty("server.ip", "206.127.156.0/24");
        ph.SetProperty("server.port", "13328");

        var reopened = new PropertyHandler(_tempAppData);
        Assert.Equal("206.127.156.0/24", reopened.GetProperty("server.ip"));
        Assert.Equal("13328", reopened.GetProperty("server.port"));
    }

    /// <summary>
    /// The regression that reset 알림 음성 to 와순이 on every restart. Store escapes 와붕이 to \uXXXX and Load
    /// brings it back correctly, but the EUC-KR re-decode then pushed it through Latin-1 — which cannot hold
    /// Hangul and best-fits every char to '?'. The value survived on disk and died on the way out of the file.
    /// </summary>
    [Fact]
    public void Korean_value_round_trips_through_SetProperty_and_a_reopen()
    {
        var ph = new PropertyHandler(_tempAppData);
        ph.SetProperty("alarms.ttsVoice", "와붕이");

        var reopened = new PropertyHandler(_tempAppData);
        Assert.Equal("와붕이", reopened.GetProperty("alarms.ttsVoice"));
        Assert.Equal("와붕이", reopened.RawEntries()["alarms.ttsVoice"]);
    }

    /// <summary>fontFamily is the one key holding arbitrary user text with no encoding of its own, so a
    /// Korean-only family name is the second thing the re-decode destroyed.</summary>
    [Fact]
    public void Korean_font_family_survives_a_reopen()
    {
        var ph = new PropertyHandler(_tempAppData);
        ph.SetProperty("fontFamily", "나눔손글씨 붓");

        Assert.Equal("나눔손글씨 붓", new PropertyHandler(_tempAppData).GetProperty("fontFamily"));
    }

    /// <summary>The quirk still has to run for values this app never wrote — that is the whole reason it is
    /// kept. A legacy file holds Korean as raw EUC-KR bytes, which load as Latin-1 chars and must be undone.
    /// </summary>
    [Fact]
    public void Raw_euckr_bytes_in_file_are_recovered_as_korean()
    {
        // Simulate a legacy value written as raw EUC-KR bytes (not \u escaped). Java's load reads it
        // as ISO-8859-1, and getProperty re-decodes those bytes as EUC-KR — the preserved quirk.
        Directory.CreateDirectory(Path.Combine(_tempAppData, AppName));
        byte[] korean = Encoding.GetEncoding(51949).GetBytes("가"); // 가 -> B0 A1
        using (var fs = File.Create(Path.Combine(_tempAppData, AppName, "settings.properties")))
        {
            fs.Write(Encoding.Latin1.GetBytes("nick="));
            fs.Write(korean);
            fs.Write(Encoding.Latin1.GetBytes("\n"));
        }

        var ph = new PropertyHandler(_tempAppData);
        Assert.Equal("가", ph.GetProperty("nick"));
    }
    [Fact]
    public void RunBatched_writes_the_file_once_and_persists_everything()
    {
        var ph = new PropertyHandler(_tempAppData);
        ph.SetProperty("seed", "1");
        int before = ph.SaveCount;

        ph.RunBatched(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                ph.SetProperty("k" + i, i.ToString());
            }
        });

        // The point of batching. Counted, not timed: 20 rewrites of a small file all land inside one filesystem
        // timestamp tick, so a before/after timestamp cannot tell the two apart.
        Assert.Equal(before + 1, ph.SaveCount);

        var reloaded = new PropertyHandler(_tempAppData);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(i.ToString(), reloaded.GetProperty("k" + i));
        }
    }

    [Fact]
    public void RunBatched_still_saves_when_the_body_throws()
    {
        // A half-applied import must not also be an unsaved one: whatever did land has to survive a restart,
        // or the user is left with a state that no backup file describes.
        var ph = new PropertyHandler(_tempAppData);
        Assert.Throws<InvalidOperationException>(() => ph.RunBatched(() =>
        {
            ph.SetProperty("before", "yes");
            throw new InvalidOperationException("boom");
        }));

        Assert.Equal("yes", new PropertyHandler(_tempAppData).GetProperty("before"));
    }

    [Fact]
    public void Writes_after_a_batch_save_immediately_again()
    {
        // The failure mode this API exists to prevent: a batch that never closes would make every later write
        // memory-only, with no symptom until restart.
        var ph = new PropertyHandler(_tempAppData);
        ph.RunBatched(() => ph.SetProperty("inside", "1"));
        ph.SetProperty("outside", "2");

        Assert.Equal("2", new PropertyHandler(_tempAppData).GetProperty("outside"));
    }

    [Fact]
    public void RawEntries_returns_the_stored_value_without_the_EucKr_re_decode()
    {
        // GetProperty always runs Latin-1 -> EUC-KR on the way out, which is a documented quirk the app relies
        // on. Export needs the untouched value for keys no live model owns, so the two must differ here.
        var ph = new PropertyHandler(_tempAppData);
        ph.SetProperty("theme", "{\"name\":\"가\"}");

        var reloaded = new PropertyHandler(_tempAppData);
        Assert.Equal("{\"name\":\"가\"}", reloaded.RawEntries()["theme"]);
    }

    [Fact]
    public void RawEntries_is_a_snapshot_not_a_live_view()
    {
        var ph = new PropertyHandler(_tempAppData);
        ph.SetProperty("a", "1");
        var snap = ph.RawEntries();
        ph.SetProperty("a", "2");

        Assert.Equal("1", snap["a"]);
        Assert.Equal("2", ph.RawEntries()["a"]);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var ph = new PropertyHandler(_tempAppData);
        ph.SetProperty("a", "1");
        Assert.Empty(Directory.GetFiles(Path.Combine(_tempAppData, AppName), "*.tmp"));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // 손상된 settings.properties 로도 앱은 뜬다 (M-27)
    //
    // 예전에는 여기서 FormatException 이 그대로 새어 나갔고, App.OnStartup 은 창·트레이·show 리스너보다
    // **먼저** PropertyHandler 를 만든다. 결과는 UI 가 0개인 채 살아 있는 프로세스 — 뮤텍스를 쥐고 있어
    // 재실행도 무반응이고, 작업관리자로 죽이기 전까지 미터를 못 켠다.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private string SettingsFile => Path.Combine(_tempAppData, AppName, "settings.properties");

    private void WriteRawSettings(string text)
    {
        Directory.CreateDirectory(Path.Combine(_tempAppData, AppName));
        File.WriteAllText(SettingsFile, text, Encoding.Latin1);
    }

    [Fact]
    public void A_file_truncated_mid_unicode_escape_starts_from_defaults_instead_of_throwing()
    {
        // 한글 값은 \\uXXXX 로 저장된다. 그 이스케이프 한가운데서 파일이 잘리면 LoadConvert 가
        // FormatException 을 던지는데, 예전 catch (IOException) 은 그걸 못 잡았다.
        WriteRawSettings("rowHeight=44" + "\n" + "fontFamily=\\u12");

        var ph = new PropertyHandler(_tempAppData);

        Assert.Equal(SettingsStoreFault.Corrupt, ph.Fault);
        Assert.False(string.IsNullOrEmpty(ph.FaultDetail));
        // 반쪽 파싱 결과(터지기 전까지 읽힌 앞줄)를 남기지 않는다 — 남기면 첫 Save 가 그 반쪽을
        // '전체 설정' 으로 확정해 버린다.
        Assert.Null(ph.GetProperty("rowHeight"));
    }

    [Fact]
    public void The_corrupt_file_is_moved_aside_rather_than_deleted()
    {
        // 사용자의 설정 원본이다. "재설치하세요" 라고 답하더라도 파일은 남아 있어야 복구가 가능하다.
        WriteRawSettings("fontFamily=\\u12");

        var ph = new PropertyHandler(_tempAppData);

        Assert.NotNull(ph.QuarantinedFilePath);
        Assert.True(File.Exists(ph.QuarantinedFilePath!));
        Assert.Contains("fontFamily", File.ReadAllText(ph.QuarantinedFilePath!, Encoding.Latin1));
        Assert.False(File.Exists(SettingsFile)); // 치웠으니 다음 Save 가 깨끗한 파일을 쓴다
    }

    [Fact]
    public void After_a_corrupt_file_the_meter_can_still_save_and_reopen_cleanly()
    {
        WriteRawSettings("fontFamily=\\u12");

        var ph = new PropertyHandler(_tempAppData);
        ph.SetProperty("rowHeight", "50");

        var reopened = new PropertyHandler(_tempAppData);
        Assert.Equal("50", reopened.GetProperty("rowHeight"));
        Assert.Equal(SettingsStoreFault.None, reopened.Fault);
        Assert.Null(reopened.QuarantinedFilePath);
    }

    [Fact]
    public void A_second_corruption_does_not_clobber_the_first_quarantined_copy()
    {
        // 재실행 루프에서 같은 초에 두 번 걸릴 수 있다. 먼저 치워 둔 사용자 설정을 덮으면 복구할 것이 없어진다.
        WriteRawSettings("first=1" + "\n" + "bad=\\u9");
        var first = new PropertyHandler(_tempAppData);

        WriteRawSettings("second=2" + "\n" + "bad=\\u9");
        var second = new PropertyHandler(_tempAppData);

        Assert.NotNull(first.QuarantinedFilePath);
        Assert.NotNull(second.QuarantinedFilePath);
        Assert.NotEqual(first.QuarantinedFilePath, second.QuarantinedFilePath);
        Assert.Contains("first=1", File.ReadAllText(first.QuarantinedFilePath!, Encoding.Latin1));
        Assert.Contains("second=2", File.ReadAllText(second.QuarantinedFilePath!, Encoding.Latin1));
    }

    [Fact]
    public void A_healthy_file_reports_no_fault_at_all()
    {
        var ph = new PropertyHandler(_tempAppData);
        ph.SetProperty("rowHeight", "44");

        var reopened = new PropertyHandler(_tempAppData);
        Assert.Equal(SettingsStoreFault.None, reopened.Fault);
        Assert.Null(reopened.FaultDetail);
        Assert.Null(reopened.QuarantinedFilePath);
    }

    [Fact]
    public void A_file_that_cannot_be_written_is_reported_instead_of_thrown()
    {
        // Save 의 in-place 폴백은 catch **밖**에 있었다. 파일이 멀쩡해도 권한 하나로 M-27 과 같은 결말에
        // 도달한다 — OnStartup 은 창을 띄우기 전에 SetProperty 를 부른다.
        var ph = new PropertyHandler(_tempAppData);
        ph.SetProperty("a", "1");

        File.SetAttributes(SettingsFile, FileAttributes.ReadOnly);
        try
        {
            ph.SetProperty("b", "2"); // 던지면 안 된다
            Assert.Equal(SettingsStoreFault.NotWritable, ph.Fault);
        }
        finally
        {
            File.SetAttributes(SettingsFile, FileAttributes.Normal);
        }

        // 쓰기가 다시 가능해지면 사실도 해제된다 — 낡은 경고를 영원히 띄우지 않는다.
        ph.SetProperty("c", "3");
        Assert.Equal(SettingsStoreFault.None, ph.Fault);
        Assert.Equal("3", new PropertyHandler(_tempAppData).GetProperty("c"));
    }

    [Fact]
    public void A_korean_value_still_round_trips_after_the_corruption_handling()
    {
        // 손상 처리 경로를 넣으면서 EUC-KR 계열 디코딩을 건드리지 않았다는 것을 붙잡는다.
        var ph = new PropertyHandler(_tempAppData);
        ph.SetProperty("alarms.ttsVoice", "와붕이");
        ph.SetProperty("fontFamily", "나눔손글씨 붓");

        var reopened = new PropertyHandler(_tempAppData);
        Assert.Equal(SettingsStoreFault.None, reopened.Fault);
        Assert.Equal("와붕이", reopened.GetProperty("alarms.ttsVoice"));
        Assert.Equal("나눔손글씨 붓", reopened.RawEntries()["fontFamily"]);
    }
}
