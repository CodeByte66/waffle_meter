using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>Covers the saved-battle history cap (raised 12 -> 30 so the scrollable history panel can show more),
/// the 120s same-battle merge, and the two ways a 허수아비 run is exempt from both.</summary>
public sealed class BattleLogRepositoryTests
{
    [Fact]
    public void History_keeps_at_most_30_battles_dropping_the_oldest()
    {
        var repo = new BattleLogRepository();
        for (int i = 1; i <= 31; i++)
        {
            // Target null => IsSameBattle is always false => no merge, each Save is a distinct battle.
            var report = new DpsReport { BattleStart = i * 1000, BattleEnd = i * 1000 + 500 };
            report.Information[1] = new DpsInformation(100, 0, 0, 0);
            repo.Save(new DpsLog { Report = report });
        }

        Assert.Equal(30, repo.GetAll().Count);                 // capped at 30
        Assert.Equal(2000, repo.GetAll()[0].Report.BattleStart); // the first (1000) was dropped; oldest now 2000
    }

    private static DpsLog Log(int targetId, int mobCode, long start, long end, double damage, bool isDummy = false)
    {
        var report = new DpsReport
        {
            BattleStart = start,
            BattleEnd = end,
            Target = new MobInfo(targetId, new Mob(mobCode, isDummy ? "훈련용 허수아비" : "보스", Boss: !isDummy, IsDummy: isDummy)),
        };
        report.Information[1] = new DpsInformation(damage, 0, 0, 0);
        return new DpsLog { Report = report };
    }

    /// <summary>특성화 테스트 — 아래 허수아비 예외가 이 기존 동작을 건드리지 않았음을 보이기 위한 기준선이다.
    /// (같은 대상 + 몹 코드 + 120초 이내 = 같은 전투로 보고 더 나은 쪽으로 대체.)</summary>
    [Fact]
    public void Two_boss_pulls_within_120s_merge_into_one_row()
    {
        var repo = new BattleLogRepository();
        repo.Save(Log(100, 2301008, 1_000_000, 1_030_000, damage: 100));
        repo.Save(Log(100, 2301008, 1_060_000, 1_090_000, damage: 500)); // 30초 뒤 재풀

        DpsLog only = Assert.Single(repo.GetAll());
        Assert.Equal(500, only.Report.Information[1].Amount); // 피해가 큰 쪽으로 대체
    }

    [Fact]
    public void Dummy_runs_never_merge_even_back_to_back()
    {
        // 허수아비는 인스턴스 id 도 몹 코드도 고정이라 위 규칙에 100% 걸린다 — 연습 3회가 한 줄이 된다.
        var repo = new BattleLogRepository();
        repo.Save(Log(200, 2300229, 1_000_000, 1_060_000, damage: 100, isDummy: true));
        repo.Save(Log(200, 2300229, 1_066_000, 1_126_000, damage: 500, isDummy: true));

        Assert.Equal(2, repo.GetAll().Count);
    }

    [Fact]
    public void Dummy_runs_have_their_own_quota_and_never_evict_a_real_battle()
    {
        var repo = new BattleLogRepository();
        repo.Save(Log(100, 2301008, 1_000, 2_000, damage: 100)); // 보스 전투 1건
        for (int i = 0; i < 31; i++)
        {
            // 각 런은 다른 시작 시각 — 하지만 병합 예외 덕분에 어차피 별개로 쌓인다.
            long start = 1_000_000 + (i * 200_000L);
            repo.Save(Log(200, 2300229, start, start + 60_000, damage: 100 + i, isDummy: true));
        }

        Assert.Equal(30, repo.GetAll().Count(l => l.Report.Target!.Mob.IsDummy));  // 허수아비 정원 30
        Assert.Single(repo.GetAll(), l => !l.Report.Target!.Mob.IsDummy);          // 보스 전투는 살아 있다
    }
}
