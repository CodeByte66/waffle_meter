using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>
/// 스킬 쿨타임 오버레이의 표시 순서. 기본은 직업 → 일반 → 스티그마 → 카탈로그 순서이고, 사용자가 배치한
/// 순서(<c>cooldownUi.order</c>)가 있으면 그것이 먼저다.
/// <para><b>저장은 base 코드를 정한 순서대로 늘어놓은 목록 하나다.</b> 코드가 직업마다 달라서 모든 직업이 한
/// 목록에 섞여도 서로 간섭하지 않는다. 목록에 없는 코드(패치로 새로 생긴 스킬)는 기본 순서로 그 직업의 맨 뒤에
/// 붙고, 목록에만 있는 코드(없어진 스킬)는 그냥 무시된다 — 카탈로그가 움직여도 저장값을 고칠 일이 없다.</para>
/// <para>표시 여부(<c>cooldownUi.hidden</c>)와는 따로 산다. 그래서 스킬을 껐다 켜도 자리가 그대로다.</para>
/// <para>WPF 의존이 없어 단위 테스트가 가능하다 — App.Wpf 에는 테스트 프로젝트가 없다.</para>
/// </summary>
public static class SkillCooldownOrder
{
    /// <summary>쉼표 구분 코드 → 순서 목록. 중복은 처음 나온 자리만 남긴다. 대괄호를 벗기는 이유는
    /// <see cref="CooldownVisibility"/> 와 같다 — 손으로 넣은 <c>[a,b]</c> 가 첫·마지막 항목을 잃지 않게.</summary>
    public static List<int> Parse(string? raw)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        var seen = new HashSet<int>();
        foreach (string part in raw.Trim().TrimStart('[').TrimEnd(']')
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out int code) && code > 0 && seen.Add(code))
            {
                result.Add(code);
            }
        }

        return result;
    }

    public static string Format(IEnumerable<int> codes) => string.Join(",", codes);

    /// <summary>오버레이 행을 표시 순서로 세운다. 행 키는 <see cref="SkillCooldownView.GroupId"/>(그 스킬 자신의
    /// base 코드 — 픽커 키와 같다)다.</summary>
    public static List<SkillCooldownView> Sort(IReadOnlyList<SkillCooldownView> rows, IReadOnlyList<int> order)
    {
        Dictionary<int, int> rank = Rank(order);
        return rows
            .OrderBy(r => r.Job)
            .ThenBy(r => rank.TryGetValue(r.GroupId, out int i) ? i : int.MaxValue)
            .ThenBy(r => r.IsStigma)
            .ThenBy(r => r.Order)
            .ThenBy(r => r.GroupId)
            .ToList();
    }

    /// <summary>한 직업의 전 스킬(보이든 숨겼든, 배웠든 안 배웠든)을 지금 유효한 순서로. <see cref="Sort"/> 와
    /// 같은 규칙이다 — 둘이 어긋나면 미리보기와 오버레이가 다른 순서를 그린다.</summary>
    public static List<int> JobOrder(IEnumerable<CooldownSkillInfo> jobSkills, IReadOnlyList<int> order)
    {
        Dictionary<int, int> rank = Rank(order);
        return jobSkills
            .OrderBy(s => rank.TryGetValue(s.BaseCode, out int i) ? i : int.MaxValue)
            .ThenBy(s => s.IsStigma)
            .ThenBy(s => s.Order)
            .ThenBy(s => s.BaseCode)
            .Select(s => s.BaseCode)
            .ToList();
    }

    /// <summary>
    /// 미리보기에서 보이는 스킬끼리 순서를 바꾼 결과를 저장 목록에 반영한다.
    /// <para><paramref name="jobOrder"/> = 그 직업의 전 스킬을 지금 유효한 순서로(<see cref="JobOrder"/>).
    /// <paramref name="arranged"/> = 사용자가 새로 정한 <b>보이는</b> 스킬의 순서.</para>
    /// <para>🔑 <b>보이던 스킬이 있던 자리만 새 순서로 채우고, 나머지는 제자리에 둔다.</b> 숨긴 스킬이나 아직 안
    /// 배운 스킬은 미리보기에 없으니 사용자가 옮긴 적이 없다 — 그 스킬을 다시 켰을 때 엉뚱한 곳(맨 뒤)이 아니라
    /// 원래 자리로 돌아와야 한다.</para>
    /// <para>그 직업의 코드는 전부 새 순서로 다시 쓰고(없어진 스킬의 옛 코드도 여기서 정리된다), 다른 직업의
    /// 코드는 건드리지 않는다.</para>
    /// </summary>
    public static List<int> Rearrange(IReadOnlyList<int> stored, int jobBand, IReadOnlyList<int> jobOrder, IReadOnlyList<int> arranged)
    {
        var job = new List<int>(jobOrder);
        var inJob = new HashSet<int>(job);
        foreach (int code in arranged)
        {
            if (inJob.Add(code))
            {
                job.Add(code); // 카탈로그 밖이지만 화면에 있던 칸 — 버리지 말고 뒤에 둔다
            }
        }

        var moved = new HashSet<int>(arranged);
        var next = new Queue<int>(arranged.Distinct());
        var result = stored.Where(c => BandOf(c) != jobBand && !inJob.Contains(c)).ToList();
        foreach (int code in job)
        {
            result.Add(moved.Contains(code) ? next.Dequeue() : code);
        }

        return result;
    }

    /// <summary>그 직업의 배치를 지운다("기본 순서로"). 다른 직업의 배치는 그대로다.</summary>
    public static List<int> WithoutJob(IReadOnlyList<int> stored, int jobBand) =>
        stored.Where(c => BandOf(c) != jobBand).ToList();

    /// <summary>8자리 base 코드의 앞 두 자리 = 직업 대역(11 검성 … 19 권성). 카탈로그의 <c>Job</c> 과 같다.</summary>
    public static int BandOf(int baseCode) => baseCode / 1_000_000;

    private static Dictionary<int, int> Rank(IReadOnlyList<int> order)
    {
        var rank = new Dictionary<int, int>(order.Count);
        for (int i = 0; i < order.Count; i++)
        {
            rank.TryAdd(order[i], i);
        }

        return rank;
    }
}
