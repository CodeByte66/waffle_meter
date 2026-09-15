using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace WaffleMeter.App.Wpf;

/// <summary>미터 크기 배율(퍼센트 int) → ScaleTransform 배율(double). 100→1.0, 85→0.85. 0/음수는 1.0로 폴백.</summary>
public sealed class PercentToScaleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch { int i when i > 0 => i / 100.0, double d when d > 0 => d / 100.0, _ => 1.0 };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>double ratio -&gt; star GridLength, so two columns split a row into fill/rest by ratio.</summary>
public sealed class RatioToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        double ratio = value is double d ? d : 0.0;
        return new GridLength(Math.Max(ratio, 0.0), GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>value == parameter (string) -&gt; Visible, else Collapsed. Drives the settings nav rail:
/// each section panel is shown only when the selected nav key matches.</summary>
public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true -&gt; Collapsed, false -&gt; Visible (for "shown when false" hints).</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Font-family name -&gt; <see cref="FontFamily"/> that prefers a BUNDLED font (Fonts/*.ttf embedded as
/// Resource) by its internal family name, then the same name as an installed system font, then safe
/// Korean-capable fallbacks. So the chosen font renders once its file is dropped into Fonts/, and
/// degrades gracefully (Malgun Gothic) until then.
/// </summary>
public sealed class FontFamilyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        FontResolver.Resolve(value as string ?? "Malgun Gothic");

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Resolves a font-family name to a <see cref="FontFamily"/>: a BUNDLED font (Fonts/*.ttf embedded as
/// Resource) by its internal family name first, else the same name as an installed system font with safe
/// Korean-capable fallbacks (Malgun Gothic, Segoe UI). Shared by <see cref="FontFamilyConverter"/> (the
/// app-wide meter font) and <see cref="GlyphFallback"/> (the per-nickname glyph check).
/// </summary>
public static class FontResolver
{
    /// <summary>
    /// A name WPF cannot possibly match, used to learn what "not found" looks like for a given font location.
    /// </summary>
    private const string MissingProbe = "__waffle_missing_font_probe__";

    /// <summary>
    /// 'Freesentation' shipped as a picker option, but no family by that bare name exists — the three files
    /// register as 'Freesentation 4/6/7' (WPF matches the Win32 family, not the typographic one). The option
    /// therefore resolved to the fallback face for every user who picked it. The stored value stays as-is
    /// (it is in real settings files); it is corrected here so every caller is fixed at once.
    /// </summary>
    private static string Normalize(string name) =>
        string.Equals(name, "Freesentation", StringComparison.Ordinal) ? "Freesentation 4" : name;

    /// <summary>
    /// The families a <see cref="FontFamily"/> actually carries, as one comparable string. When a "#name"
    /// lookup misses, WPF does NOT return an empty family — it returns the DEFAULT one (Arial here), fully
    /// populated with typefaces. So <c>GetTypefaces().Count &gt; 0</c> can never fail, and every name used to
    /// look "found". Comparing the signature against a known-missing probe is what actually distinguishes them.
    /// </summary>
    private static string Signature(FontFamily family)
    {
        try
        {
            return string.Join("", family.FamilyNames.Values.OrderBy(v => v, StringComparer.Ordinal));
        }
        catch
        {
            return string.Empty;
        }
    }

    // Assembly-qualified location so the bundled font resolves regardless of the entry assembly (the bare pack
    // URI resolves against the host exe, which breaks UiPreview + any other host).
    private static FontFamily BundledFamily(string name) =>
        new(new Uri("pack://application:,,,/"), $"/WaffleMeter.App.Wpf;component/Fonts/#{name}");

    private static readonly Lazy<string> BundledMissSignature = new(() => Signature(BundledFamily(MissingProbe)));

    /// <summary>The bundled face for this name, or null when the folder has no such family.</summary>
    private static FontFamily? TryBundled(string name)
    {
        try
        {
            FontFamily f = BundledFamily(name);
            if (f.GetTypefaces().Count > 0 && !string.Equals(Signature(f), BundledMissSignature.Value, StringComparison.Ordinal))
            {
                return f;
            }
        }
        catch
        {
            // treat as "not bundled"
        }

        return null;
    }

    /// <summary>A face from the user's fonts folder (a .ttf/.otf added via 설정 › 커스텀 폰트 추가) — loaded
    /// straight from disk by its internal family name, no system install and no restart. Null when absent.</summary>
    private static FontFamily? TryUser(string name)
    {
        try
        {
            if (!Directory.Exists(UserFontsDir()))
            {
                return null;
            }

            Uri baseUri = UserFontsBaseUri();
            string miss = Signature(new FontFamily(baseUri, $"./#{MissingProbe}"));
            var f = new FontFamily(baseUri, $"./#{name}");
            if (f.GetTypefaces().Count > 0 && !string.Equals(Signature(f), miss, StringComparison.Ordinal))
            {
                return f;
            }
        }
        catch
        {
            // treat as "not a user font"
        }

        return null;
    }

    public static FontFamily Resolve(string name)
    {
        name = Normalize(name);
        return TryBundled(name)
               ?? TryUser(name)
               ?? new FontFamily($"{name}, Malgun Gothic, Segoe UI");
    }

    /// <summary>The folder user-added fonts are copied into (next to settings.properties).</summary>
    public static string UserFontsDir()
    {
        string appData = Environment.GetEnvironmentVariable("APPDATA")
                         ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(appData, "waffle_meter.v1.4", "fonts");
    }

    private static Uri UserFontsBaseUri()
    {
        string dir = UserFontsDir();
        return new Uri(dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar);
    }

    /// <summary>Where a font name resolves from — drives the settings badges ("사용자", "시스템").</summary>
    public enum FontOrigin
    {
        /// <summary>Shipped in Fonts/ and embedded as a Resource.</summary>
        Bundled,

        /// <summary>A file the user dropped into the fonts folder.</summary>
        User,

        /// <summary>Installed on this PC (or nothing at all — WPF's comma chain then picks the fallback).</summary>
        System,
    }

    /// <summary>Which of the three lookup steps <see cref="Resolve"/> lands on for this name.</summary>
    public static FontOrigin Classify(string name)
    {
        name = Normalize(name);
        if (TryBundled(name) is not null)
        {
            return FontOrigin.Bundled;
        }

        return TryUser(name) is not null ? FontOrigin.User : FontOrigin.System;
    }

    /// <summary>
    /// Every font family installed on this PC, for the settings "시스템 글꼴" dropdown. Deliberately NOT
    /// rendered as preview cards: this is a few hundred entries and the bundled set is the curated one.
    /// Computed once — enumerating the system collection is the expensive part, not resolving a name.
    /// </summary>
    public static IReadOnlyList<string> EnumerateSystemFontFamilies() => SystemFamilies.Value;

    private static readonly Lazy<IReadOnlyList<string>> SystemFamilies = new(() =>
    {
        var names = new List<string>();
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FontFamily fam in Fonts.SystemFontFamilies)
            {
                string? n = BestFamilyName(fam);
                if (!string.IsNullOrWhiteSpace(n) && seen.Add(n))
                {
                    names.Add(n);
                }
            }

            names.Sort(StringComparer.CurrentCultureIgnoreCase);
        }
        catch
        {
            // a broken font on the machine must never empty the settings window
        }

        return names;
    });

    /// <summary>Internal family names of every user-added font, for the settings font dropdown. Empty if none.</summary>
    public static IReadOnlyList<string> EnumerateUserFontFamilies()
    {
        var names = new List<string>();
        try
        {
            if (Directory.Exists(UserFontsDir()))
            {
                foreach (FontFamily fam in Fonts.GetFontFamilies(UserFontsBaseUri()))
                {
                    string? n = BestFamilyName(fam);
                    if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n))
                    {
                        names.Add(n);
                    }
                }
            }
        }
        catch
        {
            // a bad file in the folder must never break the settings list
        }

        return names;
    }

    /// <summary>Copy a picked .ttf/.otf into the user fonts folder and return its primary family name (the value
    /// the settings store + <see cref="Resolve"/> match on), or null if the file can't be read as a font.</summary>
    public static string? InstallUserFont(string sourcePath)
    {
        try
        {
            string dir = UserFontsDir();
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, dest, overwrite: true); // re-adding the same file is idempotent
            foreach (FontFamily fam in Fonts.GetFontFamilies(new Uri(dest))) // families in the copied file
            {
                string? n = BestFamilyName(fam);
                if (!string.IsNullOrWhiteSpace(n))
                {
                    return n;
                }
            }
        }
        catch
        {
            // unreadable / not a font / copy failed
        }

        return null;
    }

    private static string? BestFamilyName(FontFamily fam)
    {
        LanguageSpecificStringDictionary names = fam.FamilyNames;
        if (names.TryGetValue(XmlLanguage.GetLanguage("en-us"), out string? en) && !string.IsNullOrWhiteSpace(en))
        {
            return en;
        }

        foreach (string v in names.Values)
        {
            return v; // any localized family name resolves via WPF's "#name" match
        }

        return null;
    }
}

/// <summary>
/// Row height -&gt; font size, scaling like React MeterRow (sizes derive from rowHeight). Parameter is
/// "<c>mult:min</c>" (e.g. "0.4:10" primary, "0.32:9" secondary); result = max(min, floor(height*mult)).
/// </summary>
/// <summary>Adds a constant (ConverterParameter) to a numeric value — e.g. the boss/target bar height =
/// row height + a few px so it reads slightly thicker than the player rows.</summary>
public sealed class OffsetConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = value switch { int i => i, double d => d, _ => 0.0 };
        double offset = 0;
        if (parameter is string p)
        {
            double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out offset);
        }

        return v + offset;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RowHeightToFontSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        double h = value switch { int i => i, double d => d, _ => 36.0 };
        double mult = 0.4, min = 10;
        if (parameter is string p)
        {
            string[] parts = p.Split(':');
            if (parts.Length == 2)
            {
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out mult);
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out min);
            }
        }

        return Math.Max(min, Math.Floor(h * mult));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 레이아웃 스펙의 <c>double</c> 반경을 <see cref="CornerRadius"/> 로 넓힌다. 한 값이 채움 Border 와
/// <c>GaugeFxLayer.CornerRadius</c>(자기 클립 모양의 근거) 를 **동시에** 물어야 모서리가 어긋나지 않는다 —
/// 둘을 따로 두면 무대(0)에서 장식만 둥근 노치를 남긴다.
/// </summary>
public sealed class DoubleToCornerRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = value switch { int i => i, double d => d, _ => 0.0 };
        return new CornerRadius(Math.Max(0.0, v));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 스킨 색(<c>Skin.OverlayBgColor</c> 같은 <see cref="Color"/> 리소스)의 알파만 눌러 돌려준다.
/// 그라디언트 스크림이 바닥에서 완전 투명까지 떨어지면 푸터가 묻히므로, 마지막 정지점을 0 이 아니라
/// 일정 비율로 남기는 데 쓴다. 파라미터는 0~1 배율(기본 0.45).
/// </summary>
public sealed class ColorAlphaConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color c)
        {
            return Colors.Transparent;
        }

        double k = 0.45;
        if (parameter is string p)
        {
            double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out k);
        }

        return Color.FromArgb((byte)Math.Clamp(c.A * k, 0, 255), c.R, c.G, c.B);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
