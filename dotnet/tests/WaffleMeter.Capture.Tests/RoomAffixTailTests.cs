using System.Text;
using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// 시련 난이도 네 축은 <b>방 스냅샷(0x9702)의 꼬리</b>에 실려 온다 —
/// <c>[count u8][affix i32 LE × n][_reason u8]</c>, 값 = <c>축번호*10 + 레벨</c>.
///
/// <para>이 경로가 붙기 전에는 부활 제한을 읽을 캐리어가 없어서 단계가 언제나 "시련 13~16단계" 같은
/// <b>범위</b>였다. 네 축이 다 오므로 점값이 된다.</para>
///
/// <para>🔴 <b>반드시 프레임 끝에서 역으로 앵커한다.</b> 멤버 배열이 가변이라 앞에서 누적하면 깨진다 —
/// 실측으로 7월 비시련 꼬리는 <c>…01|00|reason</c> 인데 9월엔 <c>…01|XX|00|reason</c> 으로 바이트가 하나
/// 늘어 있었다. 뒤에서 세면 count 는 언제나 <c>Length - 18</c> 이고 53/53 적중했다.</para>
///
/// <para>채택은 fail-closed 다: 축 0~3 이 각각 정확히 한 번, 레벨 1~4. 비시련 방 505건 + 우연히 4가 서 있던
/// 3건 전부 탈락(오탐 0). 잘못된 단계 라벨은 라벨 없음보다 나쁘다 — 티어 백분위가 그걸 소비한다.</para>
/// </summary>
public sealed class RoomAffixTailTests
{
    private const int TrialDungeonId = 600074;
    private const int RoomKey = 384095;

    private sealed class RecordingData : ICaptureGameData
    {
        public readonly List<(int DungeonId, int RoomKey, int[] Levels)> Affixes = [];

        public void ObserveRoomAffixes(int dungeonId, int roomKey, int[] levels) =>
            Affixes.Add((dungeonId, roomKey, levels));

        public Mob? GetMob(int code) => null;
        public int? GetMobId(int instanceId) => null;
        public void SaveMobId(int instanceId, int mobCode) { }
        public bool SkillExists(long code) => false;
        public long CurrentEpoch() => 0;
        public void SaveDamage(ParsedDamagePacket pdp, long epoch) { }
        public void StartBattle(int target) { }
        public void EndBattle(int target) { }
        public void SaveNickname(int uid, string nickname, bool isExecutor, int server, int jobByte) { }
        public void SaveUserPower(int uid, int power) { }
        public void SaveSummon(int summonId, int ownerId) { }
        public void SaveMobHp(int instanceId, long hp) { }
        public void SaveUseBuff(int uid, int skillCode, long buffStart, long buffEnd, long duration, int actorId) { }
        public void RequestOfficialCharacterLookup(int uid) { }
        public void SavePartyRoster(IReadOnlyList<(string Nickname, int Server, int Slot)> members) { }
        public void SaveAetherStatus(int baseVal, int bonus) { }
        public void SaveShugoKey(int baseVal, int bonus) { }
        public void SaveFieldBossTimers(IReadOnlyList<(int Code, long TargetMs)> timers) { }
    }

    private static void WriteVarInt(List<byte> to, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            to.Add((byte)(v | 0x80));
            v >>= 7;
        }

        to.Add((byte)v);
    }

    private static void WriteU32(List<byte> to, long value)
    {
        for (int i = 0; i < 4; i++)
        {
            to.Add((byte)((value >> (8 * i)) & 0xFF));
        }
    }

    /// <summary>실측 헤더 그대로: <c>[02 97][roomKey u32][descLen u8][desc][_limit_member u8][_dungeon_id u32]</c>,
    /// 그 뒤 멤버 배열(길이 가변), 마지막이 어픽스 꼬리.</summary>
    private static byte[] RoomSnapshot(
        int[]? affixes,
        int dungeonId = TrialDungeonId,
        int roomKey = RoomKey,
        string desc = "시련 갑니다",
        int padding = 0,
        byte reason = 10)
    {
        var body = new List<byte> { 0x02, 0x97 };
        WriteU32(body, roomKey);
        byte[] d = Encoding.UTF8.GetBytes(desc);
        body.Add((byte)d.Length);
        body.AddRange(d);
        body.Add(5);                 // _limit_member
        WriteU32(body, dungeonId);

        // 멤버 배열 자리. 길이를 바꿔 가며 넣어 역방향 앵커가 정말 앞을 안 보는지 확인한다.
        for (int i = 0; i < padding; i++)
        {
            body.Add(0x11);
        }

        if (affixes is not null)
        {
            body.Add((byte)affixes.Length);
            foreach (int a in affixes)
            {
                WriteU32(body, a);
            }
        }

        body.Add(reason);

        var frame = new List<byte>();
        WriteVarInt(frame, body.Count + 3);
        frame.AddRange(body);
        return frame.ToArray();
    }

    private static RecordingData Feed(byte[] frame)
    {
        var data = new RecordingData();
        new StreamProcessor(NullStreamProcessorSink.Instance, data).OnPacketReceived(frame, 0);
        return data;
    }

    [Fact]
    public void All_four_axes_come_off_the_tail()
    {
        // 축0=제한시간4 · 축1=부활제한4 · 축2=보스강화4 · 축3=패턴강화4 → 16단계
        RecordingData data = Feed(RoomSnapshot([4, 14, 24, 34]));

        (int dungeonId, int roomKey, int[] levels) = Assert.Single(data.Affixes);
        Assert.Equal(TrialDungeonId, dungeonId);
        Assert.Equal(RoomKey, roomKey);
        Assert.Equal([4, 4, 4, 4], levels);
    }

    [Fact]
    public void The_rebirth_axis_is_read_independently_of_the_others()
    {
        // 오너가 직접 구동한 시퀀스 — 부활제한만 1→2→3→4 로 올리고 나머지 셋은 고정.
        foreach ((int raw, int expected) in new[] { (11, 1), (12, 2), (13, 3), (14, 4) })
        {
            RecordingData data = Feed(RoomSnapshot([4, raw, 24, 34]));
            int[] levels = Assert.Single(data.Affixes).Levels;
            Assert.Equal(expected, levels[(int)TrialAffixGroup.Rebirthlimit]);
            Assert.Equal([4, 4, 4], new[] { levels[0], levels[2], levels[3] });
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(255)]
    public void The_member_array_length_does_not_move_the_anchor(int padding)
    {
        // 🔴 이게 역방향 앵커의 존재 이유다. 앞에서 누적했다면 여기서 전부 깨진다.
        RecordingData data = Feed(RoomSnapshot([4, 14, 24, 34], padding: padding));

        Assert.Equal([4, 4, 4, 4], Assert.Single(data.Affixes).Levels);
    }

    [Fact]
    public void A_room_with_no_affixes_reports_nothing()
    {
        // 비시련 방은 count=0 이다 — 실측 505건 전부.
        Assert.Empty(Feed(RoomSnapshot(affixes: null)).Affixes);
    }

    [Theory]
    [InlineData(new[] { 4, 14, 24, 44 })]    // 축 4 = 존재하지 않음
    [InlineData(new[] { 4, 14, 24, 30 })]    // 레벨 0
    [InlineData(new[] { 4, 14, 24, 35 })]    // 레벨 5
    [InlineData(new[] { 4, 14, 14, 34 })]    // 축1 중복 · 축2 누락
    [InlineData(new[] { 0, 14, 24, 34 })]    // 축0 레벨 0
    public void A_quad_that_is_not_four_distinct_axes_is_thrown_away(int[] affixes)
    {
        // fail-closed. 잘못된 단계 라벨은 라벨 없음보다 나쁘다 — 티어 백분위가 소비한다.
        Assert.Empty(Feed(RoomSnapshot(affixes)).Affixes);
    }

    [Fact]
    public void A_non_trial_dungeon_id_is_passed_through_rather_than_filtered_here()
    {
        // 파서는 스코프를 판정하지 않는다 — 하드코딩하면 신규 시련형 콘텐츠에 흔적이 안 남는다.
        // 걸러내는 건 데이터 계층(TrialDifficultyTracker)의 일이다.
        RecordingData data = Feed(RoomSnapshot([4, 14, 24, 34], dungeonId: 600153));

        Assert.Equal(600153, Assert.Single(data.Affixes).DungeonId);
    }

    [Fact]
    public void A_wrong_count_byte_is_not_treated_as_a_quad()
    {
        // count 가 4가 아니면 애초에 어픽스 꼬리가 아니다.
        Assert.Empty(Feed(RoomSnapshot([4, 14, 24])).Affixes);
    }
}
