using System.Reflection;

namespace WaffleMeter.Services;

/// <summary>
/// The running app version. Source of truth is the build version stamped into the entry assembly's
/// <see cref="AssemblyInformationalVersionAttribute"/> (csproj <c>WaffleVersion</c>).
///
/// It deliberately does NOT read the persisted <c>version</c> property: the Kotlin build merged a
/// bundled <c>/version.properties</c> into the in-memory property bag and then saved the whole bag,
/// so existing users' <c>settings.properties</c> still carries a stale <c>version=1.7.x</c> that has
/// nothing to do with the installed .NET build. Reading it made the settings window (and the version
/// reported to the stats server) show the old Kotlin version.
/// </summary>
public sealed record VersionConfig(string Version)
{
    /// <summary>Used when no build version can be resolved (kept in sync with the csproj default).</summary>
    public const string Fallback = "3.2.3-dev";

    /// <summary>
    /// Resolve the app version. Prefers <paramref name="explicitVersion"/> (injectable for tests/CLI),
    /// then the entry assembly's informational version — stripping any <c>+commit</c> the SDK appends —
    /// and finally <see cref="Fallback"/>.
    /// </summary>
    public static VersionConfig Resolve(string? explicitVersion = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitVersion))
        {
            return new VersionConfig(explicitVersion);
        }

        string? info = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            return new VersionConfig(Fallback);
        }

        int plus = info.IndexOf('+');
        return new VersionConfig(plus >= 0 ? info[..plus] : info);
    }
}
