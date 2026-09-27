using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>
/// 버프 오버레이의 표시 순서. 2계층이다 — ① 전역 정렬 모드로 한 번 줄을 세우고, ② 사용자가 "맨 앞 고정"한
/// 버프를 그 앞으로 끌어온다(고정끼리는 사용자가 정한 순서).
/// <para>기본값이 <see cref="Applied"/>(적용 순서)인 이유: 남은 시간으로 정렬하면 타이머가 흐를 때마다 아이콘이
/// 자리를 바꿔 계속 흔들린다. 적용 시각은 버프가 유지되는 동안 변하지 않아 화면이 안정적이다.</para>
/// <para>WPF 의존이 없어 단위 테스트가 가능하다 — 창은 이 순서를 그대로 그리기만 한다.</para>
/// </summary>
public static class BuffOverlayOrder
{
    /// <summary>적용 순서(먼저 걸린 버프가 앞). 기본값 — 유지되는 동안 순서가 흔들리지 않는다.</summary>
    public const string Applied = "applied";

    /// <summary>남은 시간이 많은 순. 직관적이지만 매 틱 순서가 바뀌어 아이콘이 움직인다.</summary>
    public const string Remaining = "remaining";

    /// <summary>이름 순.</summary>
    public const string Name = "name";

    /// <summary>정렬 모드 + 고정 목록을 적용한 표시 순서. <paramref name="pinned"/>는 순서가 곧 우선순위이고,
    /// 거기 없는 버프는 모드 순서대로 그 뒤에 붙는다.</summary>
    public static List<OwnerBuffView> Sort(
        IReadOnlyList<OwnerBuffView> buffs, string? mode, IReadOnlyList<int>? pinned)
    {
        var rank = new Dictionary<int, int>();
        if (pinned != null)
        {
            for (int i = 0; i < pinned.Count; i++)
            {
                rank.TryAdd(pinned[i], i);
            }
        }

        // 코드 tie-break를 둬 같은 키에서도 순서가 요동치지 않게 한다.
        IEnumerable<OwnerBuffView> byMode = mode switch
        {
            Remaining => buffs.OrderByDescending(b => b.RemainingMs).ThenBy(b => b.Code),
            Name => buffs.OrderBy(b => b.Name, StringComparer.Ordinal).ThenBy(b => b.Code),
            // 적용 시각 = 만료 - 지속시간. 무한 스탠스(지속시간 0)는 EndMs가 그대로 쓰여도 무방하다.
            // on/off 오라는 1초마다 펄스가 다시 와서 그 식이 매번 "방금"이 되므로 유지 시작 시각을 쓴다.
            _ => buffs.OrderBy(b => b.HeldSinceMs > 0 ? b.HeldSinceMs : b.EndMs - b.DurationMs).ThenBy(b => b.Code),
        };

        // LINQ OrderBy는 안정 정렬이라, 고정 순위로 한 번 더 정렬해도 같은 순위 안에서는 모드 순서가 보존된다.
        return byMode.OrderBy(b => rank.TryGetValue(b.Code, out int r) ? r : int.MaxValue).ToList();
    }

    /// <summary>고정 목록에서 코드를 넣거나 뺀다(토글). 넣을 때는 맨 뒤에 붙는다.</summary>
    public static List<int> TogglePin(IReadOnlyList<int> pinned, int code)
    {
        var list = pinned.ToList();
        if (!list.Remove(code))
        {
            list.Add(code);
        }

        return list;
    }

    /// <summary>고정 목록 안에서 한 칸 위/아래로 옮긴다. 목록에 없거나 끝이면 그대로 둔다.</summary>
    public static List<int> Move(IReadOnlyList<int> pinned, int code, bool up)
    {
        var list = pinned.ToList();
        int i = list.IndexOf(code);
        int j = up ? i - 1 : i + 1;
        if (i < 0 || j < 0 || j >= list.Count)
        {
            return list;
        }

        (list[i], list[j]) = (list[j], list[i]);
        return list;
    }
}
