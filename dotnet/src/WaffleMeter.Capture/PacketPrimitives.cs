namespace WaffleMeter.Capture;

/// <summary>
/// Result of reading a LEB128-style little-endian varint.
/// <c>(-1, -1)</c> signals out-of-bounds or 32-bit overflow.
/// Mirrors Kotlin <c>StreamProcessor.VarIntOutput</c> (StreamProcessor.kt:19).
/// </summary>
public readonly record struct VarIntOutput(int Value, int Length);

/// <summary>Result of <see cref="PacketPrimitives.ReadVarUInt64"/>. <c>(-1, -1)</c> = 실패(경계 밖·비정규 인코딩·
/// <see cref="long.MaxValue"/> 초과).</summary>
public readonly record struct VarLongOutput(long Value, int Length);

/// <summary>
/// Verbatim ports of the byte-reading primitives in Kotlin <c>StreamProcessor</c>
/// (readVarInt / parseUInt16le / parseUInt32le / readUInt32leAsLong / readUInt64le / toHex).
/// CORRECTNESS-CRITICAL: the <c>-1</c> sentinels and the exact OOB / 32-bit-overflow behavior
/// must match Kotlin so the framing and parse layers reject identically. Signed-int results are
/// intentional (Kotlin returns <see cref="int"/>); downstream guards rely on the sign.
/// </summary>
public static class PacketPrimitives
{
    /// <summary>Port of <c>StreamProcessor.readVarInt</c> (StreamProcessor.kt:748-779).</summary>
    public static VarIntOutput ReadVarInt(byte[] bytes, int offset = 0)
    {
        int value = 0;
        int shift = 0;
        int count = 0;

        while (true)
        {
            if (offset + count >= bytes.Length)
            {
                return new VarIntOutput(-1, -1);   // out of bounds
            }

            int byteVal = bytes[offset + count] & 0xff;
            count++;

            value |= (byteVal & 0x7F) << shift;

            if ((byteVal & 0x80) == 0)
            {
                return new VarIntOutput(value, count);
            }

            shift += 7;
            if (shift >= 32)
            {
                return new VarIntOutput(-1, -1);   // 32-bit overflow
            }
        }
    }

    /// <summary>64비트 varint(LEB128). <see cref="ReadVarInt"/>는 Kotlin 패리티 때문에 32비트에서 끊는데, 스폰
    /// 패킷(0x3641)의 <c>_hp</c>/<c>_hp_max</c>는 32비트를 넘는다(실측 감시자 카이라 145.8억 = 5바이트).
    /// <para>실패 조건: 경계 밖, 10바이트 초과, 비정규 인코딩(여러 바이트인데 마지막 바이트가 0x00 — 정상
    /// 인코더는 절대 만들지 않으므로 오프셋이 어긋났다는 신호다), 결과가 <see cref="long.MaxValue"/> 초과.
    /// 실패하면 <c>(-1, -1)</c>을 돌려준다.</para></summary>
    public static VarLongOutput ReadVarUInt64(byte[] bytes, int offset = 0)
    {
        ulong value = 0;
        int shift = 0;
        int count = 0;

        while (true)
        {
            if (count >= 10 || offset + count >= bytes.Length)
            {
                return new VarLongOutput(-1, -1);
            }

            int byteVal = bytes[offset + count] & 0xff;
            count++;

            // 10번째 바이트(shift 63)에는 1비트만 남는다. 그 이상이면 64비트를 넘친다.
            if (shift == 63 && (byteVal & 0x7E) != 0)
            {
                return new VarLongOutput(-1, -1);
            }

            value |= (ulong)(byteVal & 0x7F) << shift;

            if ((byteVal & 0x80) == 0)
            {
                if (count > 1 && byteVal == 0)
                {
                    return new VarLongOutput(-1, -1); // 비정규 인코딩
                }

                return value > long.MaxValue ? new VarLongOutput(-1, -1) : new VarLongOutput((long)value, count);
            }

            shift += 7;
            if (shift > 63)
            {
                return new VarLongOutput(-1, -1);
            }
        }
    }

    /// <summary>Port of <c>parseUInt16le</c> (StreamProcessor.kt:556).</summary>
    public static int ParseUInt16Le(byte[] packet, int offset = 0)
        => (packet[offset] & 0xff) | ((packet[offset + 1] & 0xff) << 8);

    /// <summary>Port of <c>parseUInt32le</c> (StreamProcessor.kt:560). Returns a signed int, as Kotlin does.</summary>
    public static int ParseUInt32Le(byte[] packet, int offset = 0)
    {
        if (offset + 4 > packet.Length)
        {
            throw new ArgumentException("패킷 길이가 필요길이보다 짧음");
        }

        return (packet[offset] & 0xFF)
             | ((packet[offset + 1] & 0xFF) << 8)
             | ((packet[offset + 2] & 0xFF) << 16)
             | ((packet[offset + 3] & 0xFF) << 24);
    }

    /// <summary>Port of <c>readUInt32leAsLong</c> (StreamProcessor.kt:568).</summary>
    public static long ReadUInt32LeAsLong(byte[] packet, int offset = 0)
    {
        if (offset + 4 > packet.Length)
        {
            throw new ArgumentException("패킷 길이가 필요길이보다 짧음");
        }

        return ((long)(packet[offset] & 0xFF))
             | ((long)(packet[offset + 1] & 0xFF) << 8)
             | ((long)(packet[offset + 2] & 0xFF) << 16)
             | ((long)(packet[offset + 3] & 0xFF) << 24);
    }

    /// <summary>Port of <c>readUInt64le</c> (StreamProcessor.kt:576).</summary>
    public static long ReadUInt64Le(byte[] packet, int offset = 0)
    {
        if (offset + 8 > packet.Length)
        {
            throw new ArgumentException("패킷 길이가 필요길이보다 짧음");
        }

        return ((long)(packet[offset] & 0xFF))
             | ((long)(packet[offset + 1] & 0xFF) << 8)
             | ((long)(packet[offset + 2] & 0xFF) << 16)
             | ((long)(packet[offset + 3] & 0xFF) << 24)
             | ((long)(packet[offset + 4] & 0xFF) << 32)
             | ((long)(packet[offset + 5] & 0xFF) << 40)
             | ((long)(packet[offset + 6] & 0xFF) << 48)
             | ((long)(packet[offset + 7] & 0xFF) << 56);
    }

    /// <summary>Port of <c>toHex</c> (StreamProcessor.kt:743). Uppercase, space-separated.</summary>
    public static string ToHex(byte[] bytes)
        => string.Join(" ", bytes.Select(b => b.ToString("X2")));
}
