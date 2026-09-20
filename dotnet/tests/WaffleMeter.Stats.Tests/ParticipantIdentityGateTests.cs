using WaffleMeter.Capture;
using WaffleMeter.Data;
using WaffleMeter.Stats;
using Xunit;

namespace WaffleMeter.Stats.Tests;

/// <summary>
/// 페이로드의 참가자 배열은 <b>신원이 있는 사람만</b> 담는다 — 미터 화면과 같은 규칙이다.
///
/// <para>🔑 <c>OverlayRowBuilder</c> 는 닉네임 없는 전투 행을 화면에서 숨기는데(blank-row filter) 이 빌더에는
/// 같은 필터가 없어서, <b>사용자가 미터에서 본 적 없는 행이 웹으로 올라갔다</b>. 종전에는 전투력 0 이 400 을
/// 받아 전투가 통째로 죽는 바람에 드러나지 않았고, <c>power: null</c> 로 전투가 살아남기 시작하면서 웹 집계에
/// 처음 보였다(통계웹 실측 2026-09-21: 24시간 참가자 67,782명 중 <c>identity_hash</c> 없는 행 2건 — 평균 대비
/// 타격 수 1/27 · dps 1/38 의 미량 피해).</para>
///
/// <para>해가 실제로 있었다: 5인 원정이 <c>partySize 7</c> 로 올라가 웹의 지원 자격 게이트가 수령자를
/// <c>ceil(4/2)=2</c> 대신 <c>ceil(6/2)=3</c> 요구했다. 파티 편차·티어·리더보드는 이미 NULL 전투력을
/// 자동 배제하므로, 남아 있던 유일한 영향이 인원수였다.</para>
/// </summary>
public sealed class ParticipantIdentityGateTests
{
    /// <summary>업로더(uid 1) + 이름 있는 파티원(uid 2) + <b>이름 없는 기여자</b>(uid 9).</summary>
    private static (DataManager Dm, List<User> Contributors) PartyWithAnAnonymousDamager()
    {
        var dm = new DataManager();
        dm.SaveNickname(1, "Me", isExecutor: true, server: 3, jobByte: 5);
        dm.SaveUserPower(1, 500_000);
        dm.SaveNickname(2, "Ally", isExecutor: false, server: 3, jobByte: 25);
        dm.SaveUserPower(2, 480_000);

        // 이름도 서버도 없는 기여자 — 0x3633/0x3645 가 끝내 오지 않은 엔티티다. DataManager 에 등록하지 않는
        // 것이 핵심이다: 이 행은 '신원 패킷을 받은 적 없는' 상태 그 자체다.
        var anonymous = new User(9);

        return (dm, [dm.User(1)!, dm.User(2)!, anonymous]);
    }

    private static DpsLog LogWith(List<User> contributors, IReadOnlyDictionary<int, double> amounts)
    {
        var info = new Dictionary<int, DpsInformation>();
        foreach ((int uid, double amount) in amounts)
        {
            info[uid] = new DpsInformation(amount, amount / 30.0, 0.0, 0.0);
        }

        var report = new DpsReport
        {
            Contributors = contributors,
            BattleStart = 1_000_000,
            BattleEnd = 1_030_000,
            Target = new MobInfo(100, new Mob(12345, "센터보스", true), remainHp: 0, maxHp: 1_000_000),
            Information = info,
            PartyRosterSize = 5,
        };

        return new DpsLog
        {
            Report = report,
            SkillDetails = new Dictionary<int, Dictionary<string, AnalyzedSkill>>(),
            BuffRates = new Dictionary<int, List<OperatingData>>(),
            BossBuffRates = new List<OperatingData>(),
        };
    }

    private static StatsUploadPayload Build(DataManager dm, DpsLog log) =>
        Assert.IsType<BuildResult.Payload>(
            new StatsPayloadBuilder(dm, publicCharacterProvider: () => false, clock: () => 1_700_000_000_000)
                .Build(log, "3.2.0", killConfirmed: true)).Value;

    [Fact]
    public void A_damager_with_no_identity_is_left_out_of_the_participants()
    {
        (DataManager dm, List<User> contributors) = PartyWithAnAnonymousDamager();
        StatsUploadPayload payload = Build(
            dm, LogWith(contributors, new Dictionary<int, double> { [1] = 1_000_000, [2] = 900_000, [9] = 26_000 }));

        Assert.Equal(2, payload.Participants.Count);
        Assert.All(payload.Participants, p => Assert.NotNull(p.IdentityHash));
    }

    /// <summary>🔑 이게 실제 해를 낸 자리다 — <c>battle.partySize</c> 가 참가자 수를 그대로 쓴다.</summary>
    [Fact]
    public void The_party_size_counts_only_identified_participants()
    {
        (DataManager dm, List<User> contributors) = PartyWithAnAnonymousDamager();
        StatsUploadPayload payload = Build(
            dm, LogWith(contributors, new Dictionary<int, double> { [1] = 1_000_000, [2] = 900_000, [9] = 26_000 }));

        Assert.Equal(2, payload.Battle.PartySize); // 3 이 아니다
    }

    /// <summary>이름은 있는데 서버를 모르는 행도 같은 이유로 뺀다 — 웹의 신원 키가 (서버, 닉)이라 해시가 안 선다.</summary>
    [Fact]
    public void A_named_damager_with_no_server_is_left_out_too()
    {
        var dm = new DataManager();
        dm.SaveNickname(1, "Me", isExecutor: true, server: 3, jobByte: 5);
        dm.SaveUserPower(1, 500_000);
        var serverless = new User(2, "이름은있음", server: 0);

        StatsUploadPayload payload = Build(
            dm,
            LogWith([dm.User(1)!, serverless], new Dictionary<int, double> { [1] = 1_000_000, [2] = 900_000 }));

        StatsParticipantPayload only = Assert.Single(payload.Participants);
        Assert.True(only.IsUploader);
    }

    /// <summary>
    /// ⚠️ 무명이라도 <b>큰 딜러면 남긴다</b>가 아니다 — 이 게이트는 크기를 보지 않는다.
    /// <para>무명 대형 딜러의 정체는 보통 <b>본인</b>이다(0x3633 을 못 받은 엔티티로 재인스턴스). 성역 실측
    /// (2026-09-20 로그)에서 그 행이 몹 피해의 11.47%(244M)였다. 그 복구는 이 빌더보다 <b>앞</b>에서 끝나고
    /// (<c>DpsCalculator</c> 의 로스터 1:1 본인 복구가 <c>SaveNickname(isExecutor: true)</c> 로 이름을 붙인다)
    /// 그때는 이름이 있으니 여기서 통과한다. 여기까지 무명으로 온 큰 행은 복구가 <b>거절한</b> 행이므로
    /// 화면에도 안 뜬다 — 화면과 페이로드가 같은 답을 내는 것이 이 게이트의 목적이다.</para>
    /// </summary>
    [Fact]
    public void Size_does_not_buy_a_way_past_the_gate()
    {
        (DataManager dm, List<User> contributors) = PartyWithAnAnonymousDamager();
        StatsUploadPayload payload = Build(
            dm, LogWith(contributors, new Dictionary<int, double> { [1] = 1_000_000, [2] = 100_000, [9] = 5_000_000 }));

        Assert.Equal(2, payload.Participants.Count);
        Assert.DoesNotContain(payload.Participants, p => p.IdentityHash == null);
    }

    /// <summary>
    /// 🔑 업로더는 id 로 지킨다. 여기 걸려 본인이 빠지면 그 뒤 <c>own_identity_missing</c> 으로 전투가 통째로
    /// 죽거나, 더 나쁘게는 본인 딜만 빠진 페이로드가 <b>400 도 없이</b> 올라간다.
    /// </summary>
    [Fact]
    public void The_uploader_always_rides()
    {
        (DataManager dm, List<User> contributors) = PartyWithAnAnonymousDamager();
        StatsUploadPayload payload = Build(
            dm, LogWith(contributors, new Dictionary<int, double> { [1] = 1_000_000, [2] = 900_000, [9] = 26_000 }));

        StatsParticipantPayload me = Assert.Single(payload.Participants, p => p.IsUploader);
        Assert.Equal(1_000_000, me.Result.TotalDamage);
    }

    /// <summary>
    /// ⚠️ <b>전투력만 모르는 파티원은 계속 싣는다.</b> 이 게이트를 "전투력 없으면 뺀다"로 넓히면
    /// <see cref="StatsParticipantPayload.Power"/> 가 막으려던 것이 되살아난다 — 파티 편차를 자격자가 아니라
    /// 참가자 전원으로 계산하도록 일부러 설계돼 있어서, 한 명을 빼면 편차가 줄어 들어와선 안 될 전투가 들어온다.
    /// </summary>
    [Fact]
    public void A_known_member_whose_power_is_unknown_still_rides_with_a_null_power()
    {
        var dm = new DataManager();
        dm.SaveNickname(1, "Me", isExecutor: true, server: 3, jobByte: 5);
        dm.SaveUserPower(1, 500_000);
        dm.SaveNickname(2, "Ally", isExecutor: false, server: 3, jobByte: 25); // 전투력 미상

        StatsUploadPayload payload = Build(
            dm,
            LogWith([dm.User(1)!, dm.User(2)!], new Dictionary<int, double> { [1] = 1_000_000, [2] = 900_000 }));

        Assert.Equal(2, payload.Participants.Count);
        StatsParticipantPayload ally = payload.Participants.Single(p => !p.IsUploader);
        Assert.NotNull(ally.IdentityHash);
        Assert.Null(ally.Power);
    }

    /// <summary>빠진 행이 낸 피해는 <b>업로더 성적에 얹히지 않는다</b> — 남의 딜이 내 기록이 되면 안 된다.</summary>
    [Fact]
    public void Dropping_a_row_never_moves_its_damage_onto_someone_else()
    {
        (DataManager dm, List<User> contributors) = PartyWithAnAnonymousDamager();
        StatsUploadPayload payload = Build(
            dm, LogWith(contributors, new Dictionary<int, double> { [1] = 1_000_000, [2] = 900_000, [9] = 26_000 }));

        Assert.Equal(1_000_000, payload.Participants.Single(p => p.IsUploader).Result.TotalDamage);
        Assert.Equal(900_000, payload.Participants.Single(p => !p.IsUploader).Result.TotalDamage);
    }
}
