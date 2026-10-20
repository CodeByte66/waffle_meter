using System.Globalization;

namespace WaffleMeter.App.Core;

/// <summary>How player names are shown (React nameDisplay).</summary>
public enum NameDisplay
{
    All,
    MeOnly,
    Hidden,
}

/// <summary>Server color tier (React name color by server range).</summary>
public enum ServerColorTier
{
    Default,
    A, // 천족 1001-1021
    B, // 마족 2001-2021
}

/// <summary>
/// Pure meter-row formatting helpers, ported from the React UI (utils/format.ts, MeterRow.tsx) so
/// the WPF overlay shows identical text. Kept in App.Core (no WPF deps) to be unit-testable.
/// </summary>
public static class MeterFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>format.ts formatAmount: B/M/K with 2-decimal TRUNCATION (not rounding); else integer.</summary>
    public static string FormatAmount(double amount)
    {
        double n = Math.Truncate(amount);
        if (n >= 1_000_000_000)
        {
            return Trunc2(n / 1_000_000_000).ToString("0.##", Inv) + "B";
        }

        if (n >= 1_000_000)
        {
            return Trunc2(n / 1_000_000).ToString("0.##", Inv) + "M";
        }

        if (n >= 1_000)
        {
            return Trunc2(n / 1_000).ToString("0.##", Inv) + "K";
        }

        return ((long)n).ToString(Inv);
    }

    /// <summary>
    /// Combat power badge: "{power/1000 .1f}k" (format.ts formatPower), and from a million up
    /// "{power/1000000 .3f}M" — same width as "999.9k", so the badge doesn't grow as powers climb.
    /// The M form keeps its trailing zeros for that width; the tier band label (TierLadder) trims them.
    /// <para>The cut is 999,950, not 1,000,000: anything at or above it already rounds to "1000.0k".</para>
    /// </summary>
    public static string FormatPower(int power) =>
        power >= 999_950
            ? (power / 1_000_000.0).ToString("0.000", Inv) + "M"
            : (power / 1000.0).ToString("0.0", Inv) + "k";

    /// <summary>DPS column text: "{dps:N0}/s" (React `${dps.toLocaleString()}/s`).</summary>
    public static string FormatDps(double dps) => Math.Truncate(dps).ToString("N0", Inv) + "/s";

    /// <summary>Battle duration "MM:SS" (React useMeter formatBattleTime). Non-positive → "00:00".</summary>
    public static string FormatBattleTime(long ms)
    {
        if (ms <= 0)
        {
            return "00:00";
        }

        long totalSec = ms / 1000;
        long min = totalSec / 60;
        long sec = totalSec % 60;
        return $"{min:00}:{sec:00}";
    }

    /// <summary>Contribution column text: one-decimal percent.</summary>
    public static string FormatPercent(double contribution) => contribution.ToString("F1", Inv) + "%";

    /// <summary>MeterRow name masking: me_only shows self only; hidden masks all; mask = first char + "***".</summary>
    public static string DisplayName(string? name, NameDisplay mode, bool isUser) => mode switch
    {
        NameDisplay.All => name ?? string.Empty,
        NameDisplay.MeOnly => isUser ? name ?? string.Empty : Mask(name),
        NameDisplay.Hidden => Mask(name),
        _ => name ?? string.Empty,
    };

    public static ServerColorTier ServerTier(int server) => server switch
    {
        >= 1001 and <= 1021 => ServerColorTier.A,
        >= 2001 and <= 2021 => ServerColorTier.B,
        _ => ServerColorTier.Default,
    };

    private static string Mask(string? name) =>
        !string.IsNullOrEmpty(name) ? name[0] + "***" : "***";

    private static double Trunc2(double x) => Math.Truncate(x * 100) / 100;
}
