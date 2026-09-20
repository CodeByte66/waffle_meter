using WaffleMeter.App.Core;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// 본인 복구의 <b>stale-name 판정</b>이 어떤 파티를 보는지 고정한다(overlay-rows#3).
/// <para><c>OverlayRowBuilder.Build</c>는 파티 문맥을 전부
/// <c>guardParty = frozenParty ? report.PartySnapshot : authoritativeParty</c>로 통일해 쓰는데, stale-name
/// 후보 판정 한 줄만 원본 <c>authoritativeParty</c>(=<b>지금</b>의 라이브 파티)를 보고 있었다. 0x9702는
/// 세션당 1~2회라 라이브 로스터는 5분 TTL로 비는 반면 <c>rosterConfirmed</c>는 30분 스냅샷 기준으로 여전히
/// true여서, 얼린 리포트(전투 종료 후 대기 화면 / 기록 재생)에서 <b>이름 있는 파티원 전원</b>이 복구 후보가
/// 됐다 — 본인과 같은 직업인 파티원 행 하나가 본인 닉네임·색·<c>IsExecutor</c>로 칠해졌다.</para>
/// <para>표시 전용이라 저장·업로드는 안 더럽히지만, 사용자가 결과를 읽는 화면에서 남의 성적이 내 이름을
/// 달고 서 있는다.</para>
/// </summary>
public sealed class SelfRecoveryPartySourceTests
{
    private const int Self = 15482;
    private const int Mate = 101;      // 다른 직업 파티원 (1등 딜러)
    private const int SameJobMate = 102; // 본인과 같은 직업인 파티원 — 종전에는 이 행이 본인으로 칠해졌다

    /// <summary>본인이 딜을 0 넣고 끝난 전투(난입 미집계·조기 사망)의 <b>얼린</b> 리포트.</summary>
    private static DpsReport FrozenReport()
    {
        var report = new DpsReport { ExecutorId = Self, BattleFinished = true };
        report.Contributors.Add(new User(Mate, "다즈비", 1005) { Job = JobClass.SORCERER });
        report.Information[Mate] = new DpsInformation(6000, 600, 60, 20);
        report.Contributors.Add(new User(SameJobMate, "설핏", 1001) { Job = JobClass.GLADIATOR });
        report.Information[SameJobMate] = new DpsInformation(4000, 400, 40, 13);
        report.PartySnapshot =
        [
            new User(Mate, "다즈비", 1005),
            new User(SameJobMate, "설핏", 1001),
            new User(Self, "하아앙", 2003) { IsExecutor = true },
        ];
        return report;
    }

    private static IReadOnlyList<OverlayRowBuilder.Row> Build(DpsReport report, IReadOnlyList<User>? liveParty) =>
        OverlayRowBuilder.Build(
            report, [], liveSelfId: Self, useTotalDamage: true, showPreCombatRoster: false, out _,
            selfNickname: "하아앙", selfServer: 2003, selfJob: JobClass.GLADIATOR, authoritativeParty: liveParty);

    [Fact]
    public void A_frozen_report_judges_membership_by_its_own_snapshot_not_the_expired_live_roster()
    {
        // 라이브 로스터가 5분 TTL로 비었다. 얼린 스냅샷은 그대로 살아 있으므로 판정도 거기서 나와야 한다.
        IReadOnlyList<OverlayRowBuilder.Row> rows = Build(FrozenReport(), liveParty: []);

        OverlayRowBuilder.Row sameJob = Assert.Single(rows, r => r.Uid == SameJobMate);
        Assert.Equal("설핏", sameJob.User!.Nickname); // ← 회귀 지점: 남의 행이 "하아앙"으로 칠해지지 않는다
        Assert.False(sameJob.IsSelf);
        Assert.DoesNotContain(rows, r => r.IsSelf);   // 본인은 이 전투에 없었다 — 없는 채로 둔다
    }

    [Fact]
    public void A_live_report_is_unchanged_by_the_source_switch()
    {
        // 라이브(얼리지 않은) 리포트에서는 guardParty == authoritativeParty 라 두 값이 같다 — 출처를 맞춘
        // 대가로 기존 동작이 바뀌지 않았음을 고정한다. 여기서는 stale-name 복구가 정상적으로 발동해야 한다:
        // 본인의 재인스턴스된 uid 가 이전 엔티티의 이름을 물려받았고, 확인된 로스터가 그 이름을 부정한다.
        var report = new DpsReport { ExecutorId = Self };
        report.Contributors.Add(new User(Mate, "다즈비", 1005) { Job = JobClass.SORCERER });
        report.Information[Mate] = new DpsInformation(6000, 600, 60, 20);
        report.Contributors.Add(new User(4162, "틸놈틸", 2003) { Job = JobClass.GLADIATOR }); // stale 비파티 이름
        report.Information[4162] = new DpsInformation(4000, 400, 40, 13);

        IReadOnlyList<OverlayRowBuilder.Row> rows = Build(
            report, liveParty: [new User(Mate, "다즈비", 1005), new User(Self, "하아앙", 2003) { IsExecutor = true }]);

        OverlayRowBuilder.Row self = Assert.Single(rows, r => r.Uid == 4162);
        Assert.True(self.IsSelf);
        Assert.Equal("하아앙", self.User!.Nickname);
    }
}
