namespace WaffleMeter.Data;

/// <summary>
/// Decodes a skill's specialization (특화) from the FULL wire skill code. The game does not send
/// specialization in a dedicated field or via the official API — it is baked into the skill code's last
/// four decimal digits. Dropping the ones digit (a charge/variant sub-index), each of the remaining
/// tens/hundreds/thousands digits (constrained to 1..5) names one active specialization slot. So a base
/// skill 13040000 cast as 13040240 has slots {2,4}; cast as 13042350 has {2,3,5}. Verified against a real
/// 151,937-hit capture and matched to the client's own decode.
/// <para>Pure so it is unit-testable. It lives in Data, not App.Core, because it decodes a field of a Data
/// type (<see cref="AnalyzedSkill.RawSkillCode"/>) and BOTH the in-meter detail panel and the stats upload
/// need it — App.Core references Stats, so a decoder in App.Core could never be reached from the payload
/// builder.</para>
/// </summary>
public static class SkillSpecialization
{
    /// <summary>The number of specialization slots the game exposes (the client draws five pips).</summary>
    public const int SlotCount = 5;

    /// <summary>
    /// Which of the five specialization slots this cast had active, as a length-5 bool array (index i =
    /// slot i+1). Returns null when the code carries no decodable specialization: only player skills
    /// (8-digit codes in the 11_000_000..19_999_999 band) do — basic attacks, theostone orbs (3xxxxxx),
    /// mob skills, and DoT-only rows have none, and a malformed suffix (a digit outside 1..5) yields null
    /// rather than a wrong guess.
    /// </summary>
    public static bool[]? Decode(int rawSkillCode) => Decode(rawSkillCode, SpecCatalog.Default.KindOf(rawSkillCode));

    /// <summary>
    /// <paramref name="kind"/> 를 명시하는 오버로드.
    ///
    /// <para>🔴 <b>스티그마는 언제나 null 이다.</b> 스티그마는 티어마다 별개의 데미지 컴포넌트가 있어서
    /// 코드 꼬리가 *그 타격을 낸 컴포넌트의 티어*이지 플레이어의 빌드가 아니다 — 실측으로 본인 스티그마
    /// hit 의 79.8%가 틀린 티어를 실었고, 11캐릭터 hit 기준으로는 42.8%만 맞았다. 값이 1~5 라고 해서
    /// 그럴듯하다고 쓰면 안 된다. 하한(<c>관측 꼬리 ≤ 실제 티어</c>)으로는 쓸 수 있지만 그건 빌드가 아니므로
    /// 이 API 가 돌려줄 값이 아니다.</para>
    ///
    /// <para>⚠️ <see cref="SpecKind.None"/>(카탈로그 미인덱싱 또는 분류 실패)은 <b>종전 동작 그대로</b>
    /// 꼬리를 읽는다. 여기서 null 로 막으면 카탈로그가 안 실린 호스트에서 특화가 통째로 조용히 사라진다 —
    /// 이 저장소가 반복해서 당한 모양이다. 실측으로 문제가 확인된 건 스티그마 하나이므로 거기만 막는다.</para>
    /// </summary>
    public static bool[]? Decode(int rawSkillCode, SpecKind kind)
    {
        if (rawSkillCode <= 0)
        {
            return null;
        }

        if (kind == SpecKind.Stigma)
        {
            return null;
        }

        // The wire code can exceed 8 digits (trailing framing); floor to the 8-digit skill code first, the
        // same normalization the client applies before splitting off the specialization digits.
        long code = rawSkillCode;
        while (code > 99_999_999)
        {
            code /= 10;
        }

        // Only real player skills carry specialization. Their base (last four digits zeroed) sits in the
        // 11M..19.99M band; anything else (theostone 3xxxxxx, basic attacks, mob skills) has no build.
        long baseCode = code - code % 10_000;
        if (baseCode is < 11_000_000 or > 19_999_999)
        {
            return null;
        }

        int value = (int)(code - baseCode) / 10; // drop the ones digit; the rest are the slot digits
        if (value is < 1 or > 999)
        {
            return null;
        }

        var slots = new bool[SlotCount];
        bool any = false;
        while (value > 0)
        {
            int digit = value % 10;
            if (digit is < 1 or > SlotCount)
            {
                return null; // an out-of-range digit means this isn't a specialization suffix
            }

            slots[digit - 1] = true;
            any = true;
            value /= 10;
        }

        return any ? slots : null;
    }
}
