using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaffleMeter.App.Core;

/// <summary>A decoded settings code.</summary>
public sealed class SettingsBundle
{
    /// <summary>Bundle format version. Bumped only for a change a v1 reader could misread.</summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    /// <summary>F | D | A — which code this is, for the preview to name it.</summary>
    [JsonPropertyName("p")]
    public string Profile { get; set; } = "F";

    /// <summary>Meter version that produced it. Diagnostic only; never gates the import.</summary>
    [JsonPropertyName("app")]
    public string App { get; set; } = string.Empty;

    /// <summary>Creation time, ISO-8601. Shown in the preview so an old code is recognisable as old.</summary>
    [JsonPropertyName("at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("d")]
    public Dictionary<string, string> Data { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Catalogued keys that were NOT in the properties file when this bundle was built — "이 설정은 손댄 적이
    /// 없다" recorded as a fact rather than as an omission. Restoring one means <c>RemoveProperty</c>, which is
    /// the only way back to the default a user who never touched the setting actually had.
    /// <para><b>Backups only.</b> A shared code must never carry these: it would make "남의 디자인 코드를 받았다"
    /// delete the receiver's own keys, and a code is not a reset. <see cref="SettingsBundleBuilder.Build"/>
    /// leaves it empty; only <see cref="SettingsBundleBuilder.BuildBackup"/> fills it.</para>
    /// <para>Older builds simply ignore this field — they then restore exactly what they restored before
    /// (nothing for these keys), so no version bump is needed.</para>
    /// </summary>
    [JsonPropertyName("x")]
    public List<string> Absent { get; set; } = new();
}

/// <summary>Why a code could not be read, in the user's language.</summary>
public enum SettingsCodeError
{
    None,
    NotFound,
    Corrupt,
    ChecksumMismatch,
    FutureVersion,
}

/// <summary>
/// The share-code container: <c>WM1.&lt;profile&gt;.&lt;base64url(gzip(json))&gt;.&lt;fingerprint&gt;</c>.
/// <para><b>Compressed</b> because settings are repetitive text and a code has to survive being pasted into a
/// chat message — the uncompressed form of a full backup runs to five figures of characters.</para>
/// <para><b>Fingerprinted</b> because without one every failure collapses into "잘못된 코드" and a user who
/// merely clipped the last character gets the same message as one who was handed something forged. Eight hex
/// digits separate "복사가 잘렸다" from "이건 우리 코드가 아니다". It is a checksum, not a signature: the
/// contents are readable by anyone, which is why nothing identifying may ever go in one
/// (see <see cref="SettingsKeyCatalog.ExcludedKeys"/>).</para>
/// </summary>
public static class SettingsBundleCodec
{
    public const string Magic = "WM1";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ProfileTag(SettingsProfile p) => p switch
    {
        SettingsProfile.Design => "D",
        SettingsProfile.Alarms => "A",
        _ => "F",
    };

    public static SettingsProfile ParseProfile(string? tag) => tag switch
    {
        "D" => SettingsProfile.Design,
        "A" => SettingsProfile.Alarms,
        _ => SettingsProfile.Full,
    };

    public static string Encode(SettingsBundle bundle)
    {
        byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(bundle, Json);
        using var mem = new MemoryStream();
        using (var gz = new GZipStream(mem, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gz.Write(utf8, 0, utf8.Length);
        }

        string payload = ToBase64Url(mem.ToArray());
        return $"{Magic}.{bundle.Profile}.{payload}.{Fingerprint(payload)}";
    }

    public static bool TryDecode(string? text, out SettingsBundle bundle, out SettingsCodeError error)
    {
        bundle = new SettingsBundle();
        error = SettingsCodeError.NotFound;

        string? code = Extract(text);
        if (code is null)
        {
            return false;
        }

        string[] parts = code.Split('.');
        if (parts.Length != 4 || parts[0] != Magic)
        {
            error = SettingsCodeError.Corrupt;
            return false;
        }

        if (!string.Equals(Fingerprint(parts[2]), parts[3], StringComparison.OrdinalIgnoreCase))
        {
            // Almost always a clipped or re-wrapped paste rather than tampering — the message says so.
            error = SettingsCodeError.ChecksumMismatch;
            return false;
        }

        try
        {
            byte[] gz = FromBase64Url(parts[2]);
            using var input = new MemoryStream(gz);
            using var dec = new GZipStream(input, CompressionMode.Decompress);
            using var outMem = new MemoryStream();
            dec.CopyTo(outMem);

            SettingsBundle? parsed = JsonSerializer.Deserialize<SettingsBundle>(outMem.ToArray(), Json);
            if (parsed is null)
            {
                error = SettingsCodeError.Corrupt;
                return false;
            }

            if (parsed.Version > 1)
            {
                // A newer container may mean something different by the same field. Refusing whole beats
                // applying half of a document whose meaning is a guess.
                error = SettingsCodeError.FutureVersion;
                return false;
            }

            parsed.Data ??= new Dictionary<string, string>(StringComparer.Ordinal);
            parsed.Absent ??= new List<string>();
            parsed.Profile = parts[1];
            bundle = parsed;
            error = SettingsCodeError.None;
            return true;
        }
        catch
        {
            error = SettingsCodeError.Corrupt;
            return false;
        }
    }

    /// <summary>
    /// Pull the code out of whatever the user pasted — a chat line, a quoted block, something wrapped across
    /// three lines. Whitespace, zero-width characters and a leading backtick are skipped; anything outside the
    /// base64url alphabet ENDS the code.
    /// <para>That last rule is deliberate. The obvious implementation (<c>char.IsLetterOrDigit</c>) returns true
    /// for Hangul, so "WM1.F.xxxx 좋아요" swallows the comment into the payload and fails to decode — with a
    /// message that blames the code.</para>
    /// </summary>
    public static string? Extract(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        int at = text.IndexOf(Magic + ".", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (int i = at; i < text.Length; i++)
        {
            char c = text[i];
            if (IsBodyChar(c))
            {
                sb.Append(c);
                continue;
            }

            if (IsSkippable(c))
            {
                continue; // a line break inside the code is the common case, not an error
            }

            break;
        }

        return sb.Length > Magic.Length ? sb.ToString() : null;
    }

    private static bool IsBodyChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.';

    private static bool IsSkippable(char c) =>
        char.IsWhiteSpace(c) || c is '﻿' or '​' or '‌' or '‍';

    private static string Fingerprint(string payload)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash)[..8];
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        string b64 = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '='));
    }
}
