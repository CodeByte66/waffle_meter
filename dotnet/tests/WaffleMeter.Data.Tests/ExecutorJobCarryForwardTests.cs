using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 존 이동·난입으로 본인의 엔티티 id가 새로 발급됐는데 본인 로드 패킷(0x3633)이 다시 오지 않으면, 미터는
/// 0x9200 이름앵커로 그 uid를 본인으로 승격한다(<see cref="ExecutorNameAnchorRebindTests"/>). 그 경로가
/// 닉네임·서버만 옮기고 <b>직업을 안 옮기던</b> 것이 이 테스트들이 지키는 결함이다.
/// <para>증상은 직업 아이콘이 아니라 <b>쿨타임 픽커</b>에서 난다 — 승격된 본인은 Job=null / JobSource=None
/// 으로 출발하고, '내 직업만 보기'가 <c>CanFilterByJob =&gt; OwnJobBand != 0</c> 이라 통째로 잠겨 9직업
/// 221개가 그대로 뜬다. 본인이 직업 전용 스킬을 처음 꽂을 때(OwnSkill)까지 그 상태가 유지된다.</para>
/// <para>⚠️ 이건 승격이 아니라 <b>이관</b>이다. <see cref="User.TrySetJob"/>이 provenance 사다리를 그대로
/// 지키므로(STRICTLY higher만 기록) 새 uid가 이미 잡은 직업을 덮지 않고, 캐릭터가 실제로 바뀐 경우에는
/// 아예 넘기지 않는다. 그 두 가지를 같이 고정해 두지 않으면 "빈 칸 채우기"가 "남의 직업 칠하기"로 번진다.</para>
/// </summary>
public sealed class ExecutorJobCarryForwardTests
{
    private const string Me = "와플";
    private const int MyServer = 2003;

    private const int ClericByte = 29;   // ConvertFromCode: 29..32 -> CLERIC
    private const int FighterByte = 45;  // 45..48 -> FIGHTER
    private const int UnknownByte = 0;   // -> null

    private static long _now;

    private static DataManager WithSelf(int uid, int jobByte)
    {
        _now = 1_000_000;
        var dm = new DataManager { Clock = () => _now };
        dm.SaveNickname(uid, Me, isExecutor: true, server: MyServer, jobByte: jobByte);
        return dm;
    }

    /// <summary>승격의 유일한 트리거 — 그 uid로 데미지가 지나가야 한다.</summary>
    private static void Damage(DataManager dm, int actorId) =>
        dm.SaveDamage(
            new ParsedDamagePacket { ActorId = actorId, TargetId = 9999, SkillCode = 1, Damage = 100 },
            dm.CurrentEpoch());

    [Fact]
    public void The_job_follows_the_character_onto_its_re_instanced_uid()
    {
        DataManager dm = WithSelf(100, ClericByte);
        Assert.Equal(JobClass.CLERIC, dm.User(100)?.Job); // 전제: 로드 패킷이 직업을 이미 채웠다

        dm.TryBindExecutorByIdentity(200, Me, MyServer);
        Damage(dm, 200);

        Assert.Equal(200, dm.ExecutorId());
        Assert.Equal(JobClass.CLERIC, dm.User(200)?.Job);
        Assert.Equal(JobProvenance.Authoritative, dm.User(200)?.JobSource);
    }

    [Fact]
    public void A_character_switch_does_not_inherit_the_previous_characters_job()
    {
        // 다른 캐릭터로 갈아탔는데 그 캐릭터의 jobByte를 못 읽은 경우(truncated 0x3633 등). 직업 미상으로
        // 남는 게 옳다 — 직전 캐릭터의 직업을 칠하면 픽커·아이콘·버프 프리셋이 통째로 남의 것이 된다.
        DataManager dm = WithSelf(100, ClericByte);

        dm.SaveNickname(300, "마이농", isExecutor: true, server: MyServer, jobByte: UnknownByte);

        Assert.Equal(300, dm.ExecutorId());
        Assert.Null(dm.User(300)?.Job);
        Assert.Equal(JobProvenance.None, dm.User(300)?.JobSource);
    }

    [Fact]
    public void A_cross_server_same_name_executor_is_a_switch_too()
    {
        // 같은 닉네임이라도 서버가 다르면 다른 캐릭터다. 서버는 둘 다 알 때만 비교하므로(-1은 미상),
        // 여기서만 '바뀐 것'으로 읽혀야 한다.
        DataManager dm = WithSelf(100, ClericByte);

        dm.SaveNickname(300, Me, isExecutor: true, server: 2004, jobByte: UnknownByte);

        Assert.Equal(300, dm.ExecutorId());
        Assert.Null(dm.User(300)?.Job);
    }

    [Fact]
    public void The_carried_job_never_overwrites_what_the_new_uid_already_knows()
    {
        // 새 uid가 이미 같은 티어(Authoritative)로 직업을 잡아 뒀으면 first-write-wins가 그걸 지킨다.
        // 이관이 그 규칙을 우회하면 provenance 사다리가 무의미해진다.
        DataManager dm = WithSelf(100, ClericByte);
        dm.SaveNickname(200, Me, isExecutor: false, server: MyServer, jobByte: FighterByte);

        dm.TryBindExecutorByIdentity(200, Me, MyServer);
        Damage(dm, 200);

        Assert.Equal(200, dm.ExecutorId());
        Assert.Equal(JobClass.FIGHTER, dm.User(200)?.Job);
    }

    [Fact]
    public void An_unknown_previous_job_carries_nothing_and_breaks_nothing()
    {
        // 직전 uid의 직업 자체를 모르던 경우. TrySetJob은 null을 무시하므로 승격은 그대로 되고 직업만 빈다.
        DataManager dm = WithSelf(100, UnknownByte);

        dm.TryBindExecutorByIdentity(200, Me, MyServer);
        Damage(dm, 200);

        Assert.Equal(200, dm.ExecutorId());
        Assert.Equal(Me, dm.User(200)?.Nickname);
        Assert.Null(dm.User(200)?.Job);
    }
}
