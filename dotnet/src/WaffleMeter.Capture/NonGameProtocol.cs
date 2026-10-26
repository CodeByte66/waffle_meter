namespace WaffleMeter.Capture;

/// <summary>What <see cref="NonGameProtocol.Classify"/> concluded from one segment's opening bytes.</summary>
public enum NonGameVerdict
{
    /// <summary>No conclusion — the default for every segment that isn't a complete HTTP/1.x request head.</summary>
    None,

    /// <summary>A tunnel opener (CONNECT, an Upgrade request, HTTP/2 prior knowledge). Whatever rides it
    /// afterwards may be the game (a booster/proxy), so the connection must never be marked.</summary>
    Tunnel,

    /// <summary>A complete plain HTTP/1.x request head. The connection speaks HTTP, not the game protocol.</summary>
    HttpRequest,
}

/// <summary>
/// Positive identification of connections that are certainly NOT the game: a segment that opens with a
/// complete HTTP/1.x request head. Capture takes all TCP but 80/443, so local HTTP proxies ride along — the
/// NAVER streaming connector (127.0.0.1:17080) moves a whole video stream that way, and its request text
/// framed into a known opcode often enough to earn the connection a permanent game signal (10-16 실측:
/// User-Agent ".36 " = 길이 0x2E + 0x3633).
/// <para>Deliberately narrow so a game stream can never match: request line + CRLF + the whole head
/// (<c>\r\n\r\n</c>) in the same segment — a binary game segment matched that at no offset in any corpus.
/// Response status lines are NOT used: a CONNECT proxy answers <c>HTTP/1.1 200 Connection established</c>
/// and then carries the game on that very connection.</para>
/// </summary>
public static class NonGameProtocol
{
    private const int MinSegment = 16;
    private const int MaxTarget = 8000;

    public static NonGameVerdict Classify(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < MinSegment)
        {
            return NonGameVerdict.None;
        }

        if (segment.StartsWith("CONNECT "u8) || segment.StartsWith("PRI * HTTP/2.0"u8))
        {
            return NonGameVerdict.Tunnel;
        }

        int targetStart = MethodLength(segment);
        if (targetStart == 0)
        {
            return NonGameVerdict.None;
        }

        int targetEnd = targetStart;
        while (targetEnd < segment.Length && targetEnd - targetStart < MaxTarget
            && segment[targetEnd] is >= 0x21 and <= 0x7E)
        {
            targetEnd++;
        }

        ReadOnlySpan<byte> rest = segment[targetEnd..];
        if (targetEnd == targetStart || !(rest.StartsWith(" HTTP/1.1\r\n"u8) || rest.StartsWith(" HTTP/1.0\r\n"u8)))
        {
            return NonGameVerdict.None;
        }

        int headEnd = segment.IndexOf("\r\n\r\n"u8);
        if (headEnd < 0)
        {
            return NonGameVerdict.None; // 헤드가 이 세그먼트에서 안 끝났다 — 판단 보류(안전한 쪽)
        }

        return ContainsIgnoreCase(segment[..(headEnd + 2)], "\r\nupgrade:"u8)
            ? NonGameVerdict.Tunnel
            : NonGameVerdict.HttpRequest;
    }

    private static int MethodLength(ReadOnlySpan<byte> segment)
    {
        if (segment.StartsWith("GET "u8) || segment.StartsWith("PUT "u8)) return 4;
        if (segment.StartsWith("POST "u8) || segment.StartsWith("HEAD "u8)) return 5;
        if (segment.StartsWith("PATCH "u8)) return 6;
        if (segment.StartsWith("DELETE "u8)) return 7;
        if (segment.StartsWith("OPTIONS "u8)) return 8;
        return 0;
    }

    // needle is lower-case ASCII.
    private static bool ContainsIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && (haystack[i + j] | (haystack[i + j] is >= 0x41 and <= 0x5A ? 0x20 : 0)) == needle[j])
            {
                j++;
            }

            if (j == needle.Length)
            {
                return true;
            }
        }

        return false;
    }
}
