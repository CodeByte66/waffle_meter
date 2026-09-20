using WaffleMeter.Services;

namespace WaffleMeter.App.Core;

/// <summary>
/// A snapshot of the carried settings, written immediately before an import.
/// <para>Every setter in this app writes through to the file the moment it is touched, so there is no "unsaved"
/// state to abandon — once an import runs, the previous configuration is gone unless something kept a copy.
/// The settings window's own Cancel does not help: it restores 19 values captured when the window opened, so
/// cancelling after a 70-key import leaves a mixture that no backup describes.</para>
/// <para>Snapshots live on disk rather than in memory on purpose. The undo has to still be there after the
/// settings window is closed, and after the app is restarted — which is exactly when someone realises the
/// import was a mistake.</para>
/// </summary>
public static class SettingsBackupStore
{
    private const int Keep = 10;

    public static string Directory(string appDirectory) => Path.Combine(appDirectory, "backups");

    /// <summary>Write a full-profile snapshot and return its path, or null if the disk said no. A failed backup
    /// must be reported, never swallowed — the caller refuses to import without one.
    /// <para>Built with <see cref="SettingsBundleBuilder.BuildBackup"/>, NOT the export builder: a snapshot has
    /// to describe settings the user has never touched, and the export rule deliberately omits those. Using
    /// <c>Build</c> here is what made 「되돌리기」 restore 0 keys on a fresh install while still reporting
    /// success (M-28). Swapping it back re-opens that hole.</para></summary>
    public static string? Save(PropertyHandler props, string appVersion, DateTimeOffset now)
    {
        try
        {
            string dir = Directory(props.AppDirectory());
            System.IO.Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"before-import-{now:yyyyMMdd-HHmmss}.wmset");
            File.WriteAllText(path, SettingsBundleCodec.Encode(
                SettingsBundleBuilder.BuildBackup(props, appVersion, now)));
            Prune(dir);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Snapshots, newest first.</summary>
    public static IReadOnlyList<FileInfo> List(string appDirectory)
    {
        try
        {
            var dir = new DirectoryInfo(Directory(appDirectory));
            return dir.Exists
                ? dir.GetFiles("before-import-*.wmset").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToArray()
                : Array.Empty<FileInfo>();
        }
        catch
        {
            return Array.Empty<FileInfo>();
        }
    }

    public static string? ReadNewest(string appDirectory)
    {
        FileInfo? newest = List(appDirectory).FirstOrDefault();
        try
        {
            return newest is null ? null : File.ReadAllText(newest.FullName);
        }
        catch
        {
            return null;
        }
    }

    private static void Prune(string dir)
    {
        try
        {
            foreach (FileInfo f in new DirectoryInfo(dir)
                         .GetFiles("before-import-*.wmset")
                         .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                         .Skip(Keep))
            {
                f.Delete();
            }
        }
        catch
        {
            // keeping ten is a nicety; failing to prune must never fail the import
        }
    }
}
