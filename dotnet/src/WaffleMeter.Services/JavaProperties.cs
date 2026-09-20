using System.Globalization;
using System.Text;

namespace WaffleMeter.Services;

/// <summary>
/// Faithful port of <c>java.util.Properties</c> load/store, so the .NET app reads and writes the
/// exact <c>settings.properties</c> format the Kotlin app produced (existing users' files must load
/// identically). Mirrors OpenJDK's <c>LineReader</c> / <c>load0</c> / <c>loadConvert</c> /
/// <c>saveConvert</c> / <c>store0</c>: ISO-8859-1 byte encoding, <c>\\uXXXX</c> escapes, <c>=</c>/
/// <c>:</c>/whitespace key terminators, <c>#</c>/<c>!</c> comments, and backslash line continuation.
/// </summary>
public sealed class JavaProperties
{
    // Insertion order is irrelevant to load, but keeping it makes store output stable/diffable.
    private readonly Dictionary<string, string> _map = new();

    public string? GetProperty(string key) => _map.TryGetValue(key, out string? v) ? v : null;

    public string GetProperty(string key, string defaultValue) => _map.TryGetValue(key, out string? v) ? v : defaultValue;

    public void SetProperty(string key, string value) => _map[key] = value;

    /// <summary>Drops a key entirely. Distinct from setting it to "": a removed key reads back as null, which
    /// is how callers tell "never configured" from "configured as empty".</summary>
    public bool Remove(string key) => _map.Remove(key);

    public IReadOnlyDictionary<string, string> Entries => _map;

    /// <summary>
    /// Drops every key. Used when <see cref="Load"/> throws part-way: the prefix that happened to parse is not
    /// a settings file, it is the first N lines of one, and keeping it would make the next
    /// <see cref="Store"/> write that half out as if it were the whole truth. Starting from defaults is the
    /// honest state, and the caller keeps the original bytes elsewhere.
    /// </summary>
    public void Clear() => _map.Clear();

    /// <summary>Reads a properties file (the stream is decoded as ISO-8859-1, like Java).
    /// <para>⚠ Throws <see cref="FormatException"/> on a malformed <c>\\uXXXX</c> escape, exactly like
    /// OpenJDK. A file truncated mid-escape (<c>fontFamily=\\u12</c>) is the realistic way to get there, and
    /// the parity with Java is deliberate — the CALLER decides what a broken file means. See
    /// <c>PropertyHandler.LoadSettings</c>: letting this escape used to leave the app as a window-less
    /// process that still held the single-instance mutex.</para></summary>
    public void Load(Stream input)
    {
        using var reader = new StreamReader(input, Encoding.Latin1, detectEncodingFromByteOrderMarks: false);
        Load0(reader.ReadToEnd());
    }

    /// <summary>Writes a properties file (ISO-8859-1, non-Latin1 escaped to <c>\\uXXXX</c>).</summary>
    public void Store(Stream output, string? comments)
    {
        var sb = new StringBuilder();
        if (comments != null)
        {
            sb.Append('#').Append(comments).Append('\n');
        }

        // Java writes "#" + new Date(). It is a comment (ignored on load); we keep a timestamp line
        // for shape parity without trying to match Java's Date.toString() formatting byte-for-byte.
        sb.Append('#').Append(DateTime.Now.ToString("ddd MMM dd HH:mm:ss zzz yyyy", CultureInfo.InvariantCulture)).Append('\n');

        foreach (KeyValuePair<string, string> e in _map)
        {
            string key = SaveConvert(e.Key, escapeSpace: true);
            string val = SaveConvert(e.Value, escapeSpace: false);
            sb.Append(key).Append('=').Append(val).Append('\n');
        }

        byte[] bytes = Encoding.Latin1.GetBytes(sb.ToString());
        output.Write(bytes, 0, bytes.Length);
    }

    private void Load0(string input)
    {
        var lr = new LineReader(input);
        int limit;
        while ((limit = lr.ReadLine()) >= 0)
        {
            char[] line = lr.LineBuf;
            int keyLen = 0;
            int valueStart = limit;
            bool hasSep = false;
            bool precedingBackslash = false;

            while (keyLen < limit)
            {
                char c = line[keyLen];
                if ((c == '=' || c == ':') && !precedingBackslash)
                {
                    valueStart = keyLen + 1;
                    hasSep = true;
                    break;
                }

                if ((c == ' ' || c == '\t' || c == '\f') && !precedingBackslash)
                {
                    valueStart = keyLen + 1;
                    break;
                }

                precedingBackslash = c == '\\' && !precedingBackslash;
                keyLen++;
            }

            while (valueStart < limit)
            {
                char c = line[valueStart];
                if (c != ' ' && c != '\t' && c != '\f')
                {
                    if (!hasSep && (c == '=' || c == ':'))
                    {
                        hasSep = true;
                    }
                    else
                    {
                        break;
                    }
                }

                valueStart++;
            }

            string key = LoadConvert(line, 0, keyLen);
            string value = LoadConvert(line, valueStart, limit - valueStart);
            _map[key] = value;
        }
    }

    private static string LoadConvert(char[] input, int off, int len)
    {
        var sb = new StringBuilder(len);
        int end = off + len;
        while (off < end)
        {
            char c = input[off++];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            if (off >= end)
            {
                sb.Append('\\'); // trailing lone backslash — keep literal
                break;
            }

            c = input[off++];
            if (c == 'u')
            {
                int value = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (off >= end)
                    {
                        throw new FormatException("Malformed \\uxxxx encoding.");
                    }

                    char h = input[off++];
                    value = h switch
                    {
                        >= '0' and <= '9' => (value << 4) + (h - '0'),
                        >= 'a' and <= 'f' => (value << 4) + 10 + (h - 'a'),
                        >= 'A' and <= 'F' => (value << 4) + 10 + (h - 'A'),
                        _ => throw new FormatException("Malformed \\uxxxx encoding."),
                    };
                }

                sb.Append((char)value);
            }
            else
            {
                sb.Append(c switch
                {
                    't' => '\t',
                    'r' => '\r',
                    'n' => '\n',
                    'f' => '\f',
                    _ => c,
                });
            }
        }

        return sb.ToString();
    }

    private static string SaveConvert(string s, bool escapeSpace)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (char aChar in s)
        {
            if (aChar > 61 && aChar < 127)
            {
                if (aChar == '\\')
                {
                    sb.Append('\\').Append('\\');
                }
                else
                {
                    sb.Append(aChar);
                }

                continue;
            }

            switch (aChar)
            {
                case ' ':
                    if (escapeSpace || sb.Length == 0)
                    {
                        sb.Append('\\');
                    }

                    sb.Append(' ');
                    break;
                case '\t':
                    sb.Append('\\').Append('t');
                    break;
                case '\n':
                    sb.Append('\\').Append('n');
                    break;
                case '\r':
                    sb.Append('\\').Append('r');
                    break;
                case '\f':
                    sb.Append('\\').Append('f');
                    break;
                case '=':
                case ':':
                case '#':
                case '!':
                    sb.Append('\\').Append(aChar);
                    break;
                default:
                    if (aChar < 0x0020 || aChar > 0x007e)
                    {
                        sb.Append("\\u")
                          .Append(HexDigit(aChar >> 12))
                          .Append(HexDigit(aChar >> 8))
                          .Append(HexDigit(aChar >> 4))
                          .Append(HexDigit(aChar));
                    }
                    else
                    {
                        sb.Append(aChar);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    private static char HexDigit(int nibble) => "0123456789ABCDEF"[nibble & 0xF];

    // 'escapeSpace' note: the leading-space escape uses sb.Length == 0 as the "first output char"
    // proxy. Java keys never start un-escaped, and SaveConvert is called per-field, so this matches
    // Java's x == 0 check for the field's first character.

    /// <summary>
    /// Char-for-char port of OpenJDK <c>Properties.LineReader</c>: yields one logical line at a
    /// time into <see cref="LineBuf"/>, honouring comments, blank lines, and backslash continuation
    /// (leading whitespace of a continued line is dropped; the continuation backslash is removed).
    /// </summary>
    private sealed class LineReader
    {
        private readonly string _src;
        private int _pos;
        public char[] LineBuf = new char[1024];

        public LineReader(string src) => _src = src;

        private int Read() => _pos < _src.Length ? _src[_pos++] : -1;

        public int ReadLine()
        {
            int len = 0;
            bool skipWhiteSpace = true;
            bool isCommentLine = false;
            bool isNewLine = true;
            bool appendedLineBegin = false;
            bool precedingBackslash = false;
            bool skipLf = false;

            while (true)
            {
                int ci = Read();
                if (ci < 0)
                {
                    if (isCommentLine || len == 0)
                    {
                        return -1;
                    }

                    if (precedingBackslash)
                    {
                        len--;
                    }

                    return len;
                }

                var c = (char)ci;

                if (skipLf)
                {
                    skipLf = false;
                    if (c == '\n')
                    {
                        continue;
                    }
                }

                if (skipWhiteSpace)
                {
                    if (c == ' ' || c == '\t' || c == '\f')
                    {
                        continue;
                    }

                    if (!appendedLineBegin && (c == '\r' || c == '\n'))
                    {
                        continue;
                    }

                    skipWhiteSpace = false;
                    appendedLineBegin = false;
                }

                if (isNewLine)
                {
                    isNewLine = false;
                    if (c == '#' || c == '!')
                    {
                        isCommentLine = true;
                        continue;
                    }
                }

                if (c != '\n' && c != '\r')
                {
                    if (len >= LineBuf.Length)
                    {
                        Array.Resize(ref LineBuf, LineBuf.Length * 2);
                    }

                    LineBuf[len++] = c;
                    precedingBackslash = c == '\\' && !precedingBackslash;
                }
                else
                {
                    if (isCommentLine || len == 0)
                    {
                        isCommentLine = false;
                        isNewLine = true;
                        skipWhiteSpace = true;
                        len = 0;
                        continue;
                    }

                    if (_pos >= _src.Length)
                    {
                        if (precedingBackslash)
                        {
                            len--;
                        }

                        return len;
                    }

                    if (precedingBackslash)
                    {
                        len--;
                        skipWhiteSpace = true;
                        appendedLineBegin = true;
                        precedingBackslash = false;
                        if (c == '\r')
                        {
                            skipLf = true;
                        }
                    }
                    else
                    {
                        return len;
                    }
                }
            }
        }
    }
}
