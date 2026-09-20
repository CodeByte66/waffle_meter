using System.Globalization;
using System.Text;

namespace WaffleMeter.Services;

/// <summary>
/// Why the settings store is not operating normally. Surfaced rather than swallowed: every one of these used
/// to be either a silent "설정이 초기화됐다" or — for a parse failure during startup — an app that never drew a
/// window while still holding the single-instance mutex, so relaunching did nothing either (M-27).
/// </summary>
public enum SettingsStoreFault
{
    /// <summary>Normal.</summary>
    None = 0,

    /// <summary><c>settings.properties</c> could not be parsed. It was moved aside (see
    /// <see cref="PropertyHandler.QuarantinedFilePath"/>) and this session started from defaults.</summary>
    Corrupt,

    /// <summary>The file is there but could not be read — locked, or the folder is not ours. It is left
    /// EXACTLY where it is (its contents may be perfectly fine), and this session runs on defaults. Anything
    /// saved from here will overwrite it once the block clears, which is why this has to be visible.</summary>
    Unreadable,

    /// <summary>Writes are not reaching disk. Settings appear to work and are gone on the next start.</summary>
    NotWritable,
}

/// <summary>
/// Settings store, ported verbatim from Kotlin <c>config.PropertyHandler</c>: a Java-format
/// <c>settings.properties</c> under <c>%APPDATA%\waffle_meter.v1.4</c>, with one-time copy-forward
/// from legacy app dirs, and the EUC-KR re-decode quirk on every read.
///
/// The quirk: Java's <c>Properties.load</c> reads the file as ISO-8859-1, so Korean stored as raw
/// EUC-KR bytes comes back as Latin-1 chars; <see cref="EncodeToEucKr"/> reverses that (Latin-1
/// bytes re-decoded as EUC-KR). For ASCII values it is a no-op, so the behaviour is identical for
/// the booleans/numbers/hotkey codes that make up real settings. Kept exactly so existing users'
/// files behave the same byte-for-byte — with one carve-out.
///
/// The carve-out: the quirk can only ever have applied to values that are entirely Latin-1, because that is
/// the only shape legacy mojibake has. It used to run on every value, which silently destroyed the ones this
/// app itself writes — <c>Store</c> escapes non-Latin-1 as <c>\\uXXXX</c> and <c>Load</c> restores it, so
/// those arrive already correct and Latin-1 has no room to hold them. See <see cref="EncodeToEucKr"/>.
/// </summary>
public sealed class PropertyHandler
{
    private const string AppName = "waffle_meter.v1.4";
    private static readonly string[] LegacyAppNames = { "waffle_meter.v1.3", "waffle_meter.v1.2" };
    private const string SettingFileName = "settings.properties";

    private static readonly Encoding EucKr;

    static PropertyHandler()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        EucKr = Encoding.GetEncoding(51949); // EUC-KR
    }

    private readonly JavaProperties _props = new();
    private readonly string _settingFilePath;
    private readonly object _gate = new();

    /// <param name="appDataOverride">Overrides the %APPDATA% base (used by tests).</param>
    public PropertyHandler(string? appDataOverride = null)
    {
        string appData = appDataOverride
            ?? Environment.GetEnvironmentVariable("APPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dir = Path.Combine(appData, AppName);
        _settingFilePath = Path.Combine(dir, SettingFileName);

        // Outside any try until now: a locked-down %APPDATA% (managed PC, PC방 이미지) threw straight out of
        // the constructor, and App.OnStartup builds this BEFORE the window, the tray icon and the show-listener
        // — so the process stayed alive with no UI and no way to reach it. Defaults-in-memory is a usable app;
        // a window-less process holding the mutex is not.
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Fault = SettingsStoreFault.NotWritable;
            FaultDetail = ex.Message;
        }

        if (!File.Exists(_settingFilePath))
        {
            foreach (string legacy in LegacyAppNames)
            {
                string legacyPath = Path.Combine(appData, legacy, SettingFileName);
                if (File.Exists(legacyPath))
                {
                    try
                    {
                        File.Copy(legacyPath, _settingFilePath, overwrite: false);
                    }
                    catch
                    {
                        // 이전 설정파일 복사에 실패했습니다.
                    }

                    break;
                }
            }
        }

        LoadSettings();
    }

    /// <summary>
    /// Why this store is degraded, for the caller to surface. Nothing in here throws; the app must be able to
    /// start with a broken settings file, and the user must be told rather than left wondering why everything
    /// reset. (<c>None</c> for the normal case.)
    /// </summary>
    public SettingsStoreFault Fault { get; private set; }

    /// <summary>The exception message behind <see cref="Fault"/>, for the log / the support answer.</summary>
    public string? FaultDetail { get; private set; }

    /// <summary>Where the unparseable file was moved, when <see cref="Fault"/> is
    /// <see cref="SettingsStoreFault.Corrupt"/>. Never deleted — it is the only copy of the user's settings,
    /// and "재설치하세요" is a much easier answer when the old file is still on disk.</summary>
    public string? QuarantinedFilePath { get; private set; }

    private void LoadSettings()
    {
        if (!File.Exists(_settingFilePath))
        {
            try
            {
                File.Create(_settingFilePath).Dispose();
            }
            catch (Exception ex)
            {
                // Not fatal: every later Save retries, and until then the app runs on defaults.
                Fault = SettingsStoreFault.NotWritable;
                FaultDetail = ex.Message;
            }

            return;
        }

        try
        {
            using FileStream fs = File.OpenRead(_settingFilePath);
            _props.Load(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 열지 못한 것뿐이다 — 내용은 멀쩡할 수 있으니 절대 옮기거나 지우지 않는다.
            _props.Clear();
            Fault = SettingsStoreFault.Unreadable;
            FaultDetail = ex.Message;
        }
        catch (Exception ex)
        {
            // 파싱 실패. 대표 경로는 잘린 \\uXXXX 이스케이프(<c>fontFamily=\\u12</c>)이고,
            // JavaProperties 가 FormatException 을 던진다 — 예전의 catch (IOException) 은 그걸 못 잡았다.
            //
            // 여기서 던지면 안 되는 이유가 M-27 이다: OnStartup 이 창·트레이·show 리스너보다 먼저 이 객체를
            // 만들고, ShutdownMode 가 지정된 곳이 없어서 닫을 창이 없으면 종료도 안 된다. 결과는 아이콘을
            // 눌러도 아무 일이 없고, 뮤텍스를 쥔 채 살아 있어서 재실행도 무반응인 상태 — 작업관리자 말고는
            // 복구 수단이 없다.
            //
            // 그래서: 손상 파일은 이름을 바꿔 보존하고, 이번 기동은 기본값으로 연다. 부분 파싱된 앞부분을
            // 남기지 않는 것도 의도다(그대로 두면 첫 Save 가 그 반쪽을 전체 설정으로 확정해 버린다).
            _props.Clear();
            Fault = SettingsStoreFault.Corrupt;
            FaultDetail = ex.Message;
            QuarantinedFilePath = Quarantine();
        }
    }

    /// <summary>Move the unparseable file aside so the app can write a fresh one. Best effort: if the move
    /// fails we still start on defaults, and the first Save then replaces the broken file.</summary>
    private string? Quarantine()
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string target = _settingFilePath + ".corrupt-" + stamp;
            // A relaunch loop can hit the same second twice; never clobber an earlier casualty.
            for (int i = 2; File.Exists(target) && i < 100; i++)
            {
                target = _settingFilePath + ".corrupt-" + stamp + "-" + i.ToString(CultureInfo.InvariantCulture);
            }

            File.Move(_settingFilePath, target);
            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Merge an additional properties resource (Kotlin loaded /version.properties too).</summary>
    public void MergeResource(Stream stream) => _props.Load(stream);

    public string AppDirectory() => Path.GetDirectoryName(_settingFilePath)!;

    // Reads take the same gate as writes: the underlying JavaProperties dictionary isn't thread-safe, and
    // settings are now written off the UI thread too (e.g. the stats upload queue caching a character grant),
    // so an unlocked read could race a concurrent write and throw/tear. EncodeToEucKr is pure (no _props
    // access), so holding the lock across it is brief and deadlock-free.
    public string? GetProperty(string key)
    {
        lock (_gate)
        {
            return EncodeToEucKr(_props.GetProperty(key));
        }
    }

    public string? GetProperty(string key, string defaultValue)
    {
        lock (_gate)
        {
            return EncodeToEucKr(_props.GetProperty(key, defaultValue));
        }
    }

    public void SetProperty(string key, string value)
    {
        lock (_gate)
        {
            _props.SetProperty(key, value);
            if (_batchDepth == 0)
            {
                Save();
            }
            else
            {
                _batchDirty = true;
            }
        }
    }

    /// <summary>
    /// Deletes a key from the file. Needed by one-shot key migrations: writing "" would leave the old key
    /// present-but-empty, and a migration that keys off "is the legacy key still there?" would then run on
    /// every load forever.
    /// </summary>
    public void RemoveProperty(string key)
    {
        lock (_gate)
        {
            if (!_props.Remove(key))
            {
                return;
            }

            if (_batchDepth == 0)
            {
                Save();
            }
            else
            {
                _batchDirty = true;
            }
        }
    }

    private int _batchDepth;
    private bool _batchDirty;

    /// <summary>
    /// Run several writes as one save. Every <see cref="SetProperty"/> normally rewrites the WHOLE file, so a
    /// settings import touching ~70 keys would rewrite it ~70 times.
    /// <para>Deliberately a callback and not an <c>IDisposable</c> scope: a missed <c>Dispose</c> would leave the
    /// process in a state where every later setting write lands in memory only, with no symptom at all until the
    /// next restart. There is no way to forget to close this one.</para>
    /// </summary>
    public void RunBatched(Action body)
    {
        lock (_gate)
        {
            _batchDepth++;
            try
            {
                body();
            }
            finally
            {
                _batchDepth--;
                if (_batchDepth == 0 && _batchDirty)
                {
                    _batchDirty = false;
                    Save();
                }
            }
        }
    }

    /// <summary>
    /// The stored values EXACTLY as they sit in the file, bypassing <see cref="GetProperty"/>'s EUC-KR
    /// re-decode. Needed for keys no live model owns (theme JSON, hotkeys, skill visibility) — reading those
    /// through the normal path and writing them back would round-trip non-ASCII through Latin-1 and lose it.
    /// <para>⚠ Do NOT use this for keys a model does own. Those are held in memory in the decoded
    /// representation, so mixing the two sources in one bundle produces values that render correctly this
    /// session and break on restart.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> RawEntries()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_props.Entries, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Atomic replace: write a temp file, then swap. <c>File.Create</c> truncates in place, so a crash (or a
    /// full disk) part-way through left a truncated settings file — every setting gone. The batch window above
    /// widens that gap, so it is closed here in the same change.
    /// </summary>
    /// <summary>How many times the file has been rewritten. The batching above exists to keep this from being
    /// one-per-key, and a wall-clock timestamp cannot tell 1 write from 20 — they land in the same tick.</summary>
    public int SaveCount { get; private set; }

    private void Save()
    {
        SaveCount++;
        string dir = Path.GetDirectoryName(_settingFilePath)!;
        string temp = Path.Combine(dir, Path.GetFileName(_settingFilePath) + ".tmp");
        try
        {
            using (FileStream fs = File.Create(temp))
            {
                _props.Store(fs, "settings");
            }

            File.Move(temp, _settingFilePath, overwrite: true);
        }
        catch
        {
            // Fall back to the in-place write rather than losing the change entirely (e.g. a temp file blocked
            // by an AV scanner). Worst case is the old behaviour, not a worse one.
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // best effort
            }

            try
            {
                using FileStream fs = File.Create(_settingFilePath);
                _props.Store(fs, "settings");
            }
            catch (Exception ex)
            {
                // This line used to sit outside the catch, so one permission error — no file corruption
                // needed — reached the same dead end as M-27: OnStartup calls SetProperty before it ever
                // shows a window. A settings write that cannot happen is bad; a meter that cannot open is worse.
                Fault = SettingsStoreFault.NotWritable;
                FaultDetail = ex.Message;
                return;
            }
        }

        // A save that got through clears only the write-side fault. Corrupt/Unreadable describe what happened
        // to the file we started from and stay true for this session — the user still lost those settings.
        if (Fault == SettingsStoreFault.NotWritable)
        {
            Fault = SettingsStoreFault.None;
            FaultDetail = null;
        }
    }

    private static string? EncodeToEucKr(string? value)
    {
        if (value == null)
        {
            return null;
        }

        // Only a value that is ENTIRELY inside Latin-1 can be the legacy mojibake this undoes: that shape is
        // raw EUC-KR bytes read back as ISO-8859-1 chars, so every char is <= 0xFF by construction.
        //
        // A value holding anything above that came from a \uXXXX escape, which JavaProperties has already
        // decoded correctly — and pushing it through Latin-1 replaces each such char with '?' (Latin-1's encoder
        // fallback is best-fit, so 와순이 -> 63 63 63 -> "???"). That is not a cosmetic loss: it is how
        // alarms.ttsVoice failed its ReadEnum whitelist and reset the alert voice to 와순이 on every restart,
        // and it would do the same to any Korean-only font family name. So that case is returned untouched.
        foreach (char c in value)
        {
            if (c > 0xFF)
            {
                return value;
            }
        }

        return EucKr.GetString(Encoding.Latin1.GetBytes(value));
    }
}
