namespace WaffleMeter.Data;

/// <summary>한 스킬의 특화 칸이 몇 개짜리인가.</summary>
public enum SpecKind
{
    /// <summary>특화가 없는 코드(기본 공격·테오스톤·몹 스킬·미상).</summary>
    None = 0,

    /// <summary>일반 액티브 — 3칸 중 최대 3개를 고른다.</summary>
    Normal = 1,

    /// <summary>스티그마 — 레벨 5/10/15/20/25 에서 한 티어씩 <b>누적</b>으로 열린다(최대 5).</summary>
    Stigma = 2,
}

/// <summary>
/// 스킬 코드가 <b>일반 액티브</b>인지 <b>스티그마</b>인지 가른다.
///
/// <para>왜 가르는가 — 두 종류에서 코드 꼬리의 <b>의미가 다르다</b>. 일반은 꼬리가 플레이어의 빌드를
/// 96.7% 정확히 싣는다. 스티그마는 티어마다 <b>별개의 데미지 컴포넌트</b>가 있어서 꼬리가 *그 타격을 낸
/// 컴포넌트의 티어*이지 빌드가 아니다 — 실측으로 본인 스티그마 hit 의 79.8%가 틀린 티어를 싣고 있었고,
/// 11캐릭터로 넓히면 hit 기준 42.8%만 맞는다. 한 스킬이 한 세션에 2~3행으로 쪼개지기도 한다
/// (생명의 권능 lvl20 이 티어 2·3·4 를 동시에 때린다 — 누적 모델의 실증이기도 하다).</para>
///
/// <para>판별은 새 자산을 만들지 않고 <c>skills.json</c> 을 인덱싱해서 한다. 같은 base 를 가진 변형 코드들의
/// 꼬리 자릿수가 종류를 말해 준다 — 클라가 스티그마에는 <b>다자리 조합 코드를 아예 만들지 않기</b> 때문이다
/// (실측 118/118 이 최대 1자리).</para>
/// </summary>
public sealed class SpecCatalog
{
    /// <summary>일반 액티브가 고를 수 있는 칸 수.</summary>
    public const int NormalCapacity = 3;

    /// <summary>스티그마의 티어 수(레벨 5/10/15/20/25).</summary>
    public const int StigmaCapacity = 5;

    /// <summary>앱이 <c>LoadSkills</c> 때 채우는 공용 인스턴스. 채워지지 않으면 전부 <see cref="SpecKind.None"/>
    /// 이고, 그 경우 호출부는 특화를 아예 안 그린다(fail-closed — 틀린 핍보다 없는 핍이 낫다).</summary>
    public static SpecCatalog Default { get; } = new();

    private readonly Dictionary<int, SpecKind> _byBase = new();
    private readonly object _gate = new();

    /// <summary>
    /// 꼬리 문법: 각 자리가 1~5 이고 <b>엄격 증가</b>(중복 불가), 길이 1~3.
    /// 예 <c>245</c> = 슬롯 2·4·5. 문법에 안 맞으면 특화 꼬리가 아니다.
    /// </summary>
    public static int[]? ParseTail(int tail)
    {
        if (tail is < 1 or > 999)
        {
            return null;
        }

        Span<int> digits = stackalloc int[3];
        int n = 0;
        for (int v = tail; v > 0; v /= 10)
        {
            int d = v % 10;
            if (d is < 1 or > 5)
            {
                return null;
            }

            digits[n++] = d;
        }

        // 위 루프가 낮은 자리부터 담았으므로 뒤집으면 표기 순서다. 엄격 증가여야 한다.
        var slots = new int[n];
        for (int i = 0; i < n; i++)
        {
            slots[i] = digits[n - 1 - i];
        }

        for (int i = 1; i < n; i++)
        {
            if (slots[i] <= slots[i - 1])
            {
                return null;
            }
        }

        return slots;
    }

    /// <summary>플레이어 스킬의 base 코드(뒤 4자리 0), 대역 밖이면 0.</summary>
    public static int BaseOf(long rawSkillCode)
    {
        if (rawSkillCode <= 0)
        {
            return 0;
        }

        long code = rawSkillCode;
        while (code > 99_999_999)
        {
            code /= 10;
        }

        long baseCode = code - (code % 10_000);
        return baseCode is < 11_000_000 or > 19_999_999 ? 0 : (int)baseCode;
    }

    /// <summary>
    /// <c>skills.json</c> 전량으로 인덱싱한다. 같은 base 의 변형 코드 중 <b>문법에 맞는 꼬리</b>만 세고,
    /// 그 최대 자릿수가 종류를 정한다 — 1자리뿐이면 스티그마, 2자리 이상이 있으면 일반.
    /// </summary>
    public void Index(IEnumerable<Skill> skills)
    {
        var maxDigits = new Dictionary<int, int>();
        foreach (Skill s in skills)
        {
            int baseCode = BaseOf(s.Code);
            if (baseCode == 0)
            {
                continue;
            }

            maxDigits.TryAdd(baseCode, 0);
            int[]? slots = ParseTail((int)((s.Code - baseCode) / 10));
            if (slots is not null && slots.Length > maxDigits[baseCode])
            {
                maxDigits[baseCode] = slots.Length;
            }
        }

        lock (_gate)
        {
            _byBase.Clear();
            foreach ((int baseCode, int digits) in maxDigits)
            {
                _byBase[baseCode] = digits switch
                {
                    0 => SpecKind.None,
                    1 => SpecKind.Stigma,
                    _ => SpecKind.Normal,
                };
            }
        }
    }

    /// <summary>이 코드가 어느 종류인가.</summary>
    public SpecKind KindOf(int rawSkillCode)
    {
        int baseCode = BaseOf(rawSkillCode);
        if (baseCode == 0)
        {
            return SpecKind.None;
        }

        lock (_gate)
        {
            return _byBase.GetValueOrDefault(baseCode, SpecKind.None);
        }
    }

    /// <summary>
    /// 전투 중에 본 코드로 종류를 올린다. <c>skills.json</c> 에 변형이 아예 없는 base 가 실재하기 때문이다
    /// (실측 <c>14200000 퇴보 베기</c>, <c>13380000 암격</c>).
    /// <para>승격만 하고 <b>강등은 안 한다</b> — 2자리 이상 꼬리를 한 번이라도 봤으면 그건 일반 액티브라는
    /// 증거이고, 그 뒤에 1자리만 계속 본다고 해서 증거가 사라지지 않는다.</para>
    /// </summary>
    public void Observe(int rawSkillCode)
    {
        int baseCode = BaseOf(rawSkillCode);
        if (baseCode == 0)
        {
            return;
        }

        int[]? slots = ParseTail((NormalizedCode(rawSkillCode) - baseCode) / 10);
        if (slots is null)
        {
            return;
        }

        SpecKind seen = slots.Length == 1 ? SpecKind.Stigma : SpecKind.Normal;
        lock (_gate)
        {
            SpecKind current = _byBase.GetValueOrDefault(baseCode, SpecKind.None);
            if (Evidence(seen) > Evidence(current))
            {
                _byBase[baseCode] = seen;
            }
        }
    }

    /// <summary>승격 사다리. <b>enum 값 순서가 아니다</b> — 열거 순서는 의미 구분일 뿐이고, 여기서 재는 건
    /// "그 종류라는 증거가 얼마나 강한가"다. 2자리 이상 꼬리는 그 스킬이 일반 액티브라는 <b>직접 증거</b>고,
    /// 1자리는 스티그마일 수도 있고 일반의 한 칸만 꽂은 것일 수도 있어 약하다.</summary>
    private static int Evidence(SpecKind kind) => kind switch
    {
        SpecKind.Normal => 2,
        SpecKind.Stigma => 1,
        _ => 0,
    };

    private static int NormalizedCode(int rawSkillCode)
    {
        long code = rawSkillCode;
        while (code > 99_999_999)
        {
            code /= 10;
        }

        return (int)code;
    }
}
