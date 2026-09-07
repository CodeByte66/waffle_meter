using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>One saved battle as shown in the history panel (port of React useHistory HistoryItem).
/// <see cref="Index"/> is the repository index used to re-open the full log.</summary>
public sealed record BattleHistoryItem(
    int Index,
    string MobName,
    bool IsBoss,
    double TotalAmount,
    long BattleTimeMs,
    long BattleStartMs,
    /// <summary>허수아비 측정 런인가. 저장 스냅샷이 <c>Mob</c> 레코드를 그대로 실어 가므로 별도 필드 없이
    /// <c>Target.Mob.IsDummy</c> 하나로 판정된다 — 패널의 '허수아비' 탭이 이 값으로 갈린다.</summary>
    bool IsDummy = false);

/// <summary>
/// Maps the data layer's saved-battle list into history rows. Pure (no WPF) so it is unit-testable.
/// Mirrors React useHistory: drop zero-duration battles, newest first.
/// </summary>
public static class BattleHistory
{
    /// <param name="encounters">Supplies the difficulty/stage suffix, so two saved runs of the same dungeon at
    /// different difficulties don't show as identical rows. Omit for the bare boss names.</param>
    public static IReadOnlyList<BattleHistoryItem> Build(
        IEnumerable<(int Index, DpsReport Report)> battles,
        EncounterCatalog? encounters = null)
    {
        EncounterCatalog catalog = encounters ?? EncounterCatalog.Empty;
        var items = new List<BattleHistoryItem>();
        foreach ((int index, DpsReport report) in battles)
        {
            long battleTime = Math.Max(report.BattleEnd - report.BattleStart, 0L);
            if (battleTime <= 0)
            {
                continue; // React filters battleTime > 0
            }

            items.Add(new BattleHistoryItem(
                Index: index,
                MobName: report.Target is { } target
                    ? catalog.DisplayName(target.Mob.Code, target.Mob.Name)
                    : "알 수 없음",
                IsBoss: report.Target?.Mob.Boss ?? false,
                TotalAmount: report.Information.Values.Sum(i => i.Amount),
                BattleTimeMs: battleTime,
                BattleStartMs: report.BattleStart,
                IsDummy: report.Target?.Mob.IsDummy ?? false));
        }

        items.Reverse(); // repository is oldest-first; show newest first
        return items;
    }

    /// <summary>패널 탭 하나. 허수아비 런은 진짜 전투와 성격이 달라(같은 대상, 고정 길이, 반복) 한 목록에
    /// 섞이면 둘 다 읽기 어렵다.</summary>
    public enum Tab
    {
        Battle,
        Dummy,
    }

    /// <summary>탭 필터. 순수 함수로 여기 두는 이유는 App.Wpf 에 테스트 프로젝트가 없기 때문이다.</summary>
    public static IReadOnlyList<BattleHistoryItem> Filter(IReadOnlyList<BattleHistoryItem> items, Tab tab) =>
        items.Where(i => i.IsDummy == (tab == Tab.Dummy)).ToList();
}
