using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// SaveBattleLog 가 시련 난이도를 저장 리포트에 <b>동결</b>하는지.
///
/// <para>🔑 이게 왜 리포트에 붙어야 하는가: 시련은 4~16단계가 <b>같은 맵·같은 보스 코드</b>를 쓰므로,
/// 저장된 전투만 보고는 그 판이 몇 단계였는지 되짚을 방법이 전혀 없다. 렌더나 업로드 시점에 추적기를
/// 조회하면 <b>그때</b> 들어가 있는 시련의 값을 지난 전투 위에 찍게 된다 — 라이브에서는 늘 맞는 것처럼
/// 보이고 기록을 열어야 드러나는 종류의 오표기다.</para>
/// </summary>
public sealed class DataManagerTrialFreezeTests
{
    private static DataManager WithTwoPlayers()
    {
        var dm = new DataManager { Clock = () => 1_000_000 };
        dm.SaveNickname(1, "Me", isExecutor: true, server: 2003, jobByte: 0);
        dm.SaveNickname(2, "Ally", isExecutor: false, server: 2003, jobByte: 0);
        return dm;
    }

    private static DpsLog Save(DataManager dm) => dm.SaveBattleLog(
        new DpsReport
        {
            Contributors = new List<User> { dm.User(1)!, dm.User(2)! },
            Information = new Dictionary<int, DpsInformation> { [1] = new(1, 1, 1, 1), [2] = new(1, 1, 1, 1) },
        },
        new Dictionary<int, Dictionary<string, AnalyzedSkill>>(),
        new Dictionary<int, List<OperatingData>>(),
        new List<OperatingData>());

    [Fact]
    public void SaveBattleLog_freezes_the_observed_trial_difficulty()
    {
        DataManager dm = WithTwoPlayers();
        dm.SaveTrialAffix(TrialAffixGroup.BossBuff, 4, arrivedAt: 0);
        dm.SaveTrialAffix(TrialAffixGroup.BakronSkillUpgrade, 3, arrivedAt: 0);

        DpsLog log = Save(dm);

        Assert.True(log.Report.TrialDifficulty.IsTrial);
        Assert.Equal(4, log.Report.TrialDifficulty.BossBuff);
        Assert.Equal(3, log.Report.TrialDifficulty.SkillUpgrade);
    }

    /// <summary>
    /// 저장한 뒤 추적기가 바뀌어도 저장본은 안 따라간다. 던전을 나가면 추적기는 비워지고, 다음 시련에
    /// 들어가면 다른 값으로 채워진다 — 그 사이에 열어 본 기록이 새 값을 말하면 안 된다.
    /// </summary>
    [Fact]
    public void A_saved_battle_does_not_follow_the_tracker_afterwards()
    {
        DataManager dm = WithTwoPlayers();
        dm.SaveTrialAffix(TrialAffixGroup.BossBuff, 4, arrivedAt: 0);
        DpsLog log = Save(dm);

        dm.TrialDifficulty.Reset();                                   // 던전 이탈
        dm.SaveTrialAffix(TrialAffixGroup.BossBuff, 1, arrivedAt: 0); // 다음 시련 입장

        Assert.Equal(4, log.Report.TrialDifficulty.BossBuff);
    }

    /// <summary>
    /// 시련이 아닌 전투는 빈 값이어야 한다. 기본값(전 축 null)이 곧 <c>IsTrial == false</c> 이므로 라벨도
    /// 업로드 페이로드도 알아서 비는데, 그게 성립하는지 확인한다.
    /// </summary>
    [Fact]
    public void A_non_trial_battle_freezes_nothing()
    {
        DpsLog log = Save(WithTwoPlayers());

        Assert.False(log.Report.TrialDifficulty.IsTrial);
        Assert.Equal(string.Empty, log.Report.TrialDifficulty.Label);
    }
}
