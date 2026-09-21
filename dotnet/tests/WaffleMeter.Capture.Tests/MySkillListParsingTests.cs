using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// 0x5100 MySkillList_NT — 본인이 배운 스킬 전량 스냅샷.
///
/// <para>🔴 <c>_cooltime</c> 은 <b>varint</b> 다. 고정 u32 로 읽으면 <b>86% 의 스냅샷에서는 멀쩡히 통과한다</b> —
/// 잔여 쿨이 실린 레코드가 57스냅샷 중 8개뿐이라서다. 그 8개에서만 어긋나고 값도 그럴듯해서 눈에 안 띈다.
/// 아래 골든 케이스 하나가 두 모델을 가른다.</para>
///
/// <para>레코드는 자기검증한다: <c>level == original + 증가분 5축</c> 이 실측 4538/4538 성립하고,
/// 프레임 전체가 exact-consume 된다(57/57). 어긋나면 레이아웃을 잘못 걷고 있다는 뜻이라 프레임을 통째로
/// 버린다 — 부분 채택은 보유집합을 조용히 오염시킨다.</para>
/// </summary>
public sealed class MySkillListParsingTests
{
    private sealed class RecordingData : ICaptureGameData
    {
        public readonly List<IReadOnlyList<LearnedSkill>> Snapshots = [];

        public void ApplyMySkillSnapshot(IReadOnlyList<LearnedSkill> skills, long arrivedAt) =>
            Snapshots.Add(skills);

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

    private static void WriteVarInt(List<byte> to, long value)
    {
        ulong v = (ulong)value;
        while (v >= 0x80)
        {
            to.Add((byte)(v | 0x80));
            v >>= 7;
        }

        to.Add((byte)v);
    }

    private static void Record(List<byte> body, int code, int level, long cooltime, int original = -1)
    {
        if (original < 0)
        {
            original = level;
        }

        byte mask = (byte)((cooltime > 0 ? 0x01 : 0) | 0x02);
        body.Add(mask);
        for (int i = 0; i < 4; i++)
        {
            body.Add((byte)((code >> (8 * i)) & 0xFF));
        }

        body.Add((byte)level);
        body.Add((byte)original);
        int remaining = level - original;
        for (int i = 0; i < 5; i++)
        {
            byte take = (byte)Math.Min(remaining, 255);
            body.Add(take);
            remaining -= take;
        }

        if ((mask & 0x01) != 0)
        {
            WriteVarInt(body, cooltime);
        }

        body.Add(0);
        body.Add(0);
        body.Add(0);
        WriteVarInt(body, 0); // mask & 0x02 — 실측 50/50 전부 0
    }

    private static byte[] Frame(params (int Code, int Level, long Cooltime)[] records)
    {
        var body = new List<byte> { 0x00, 0x51 };
        WriteVarInt(body, records.Length);
        foreach ((int code, int level, long cooltime) in records)
        {
            Record(body, code, level, cooltime);
        }

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
    public void The_golden_record_separates_the_two_cooltime_models()
    {
        // 실측 바이트 그대로(20260724-022938, 흡혈의 검):
        //   03 | e0 08 ad 00 | 0a 0a | 00×5 | d2 cb 02 | 00 00 00 | 00
        // varint 로 읽으면 42,450. 고정 u32 로 읽으면 183,250 이고, 이 스킬의 카탈로그 쿨은 90,000 이다 —
        // 즉 잘못된 모델은 "총 쿨보다 두 배 긴 잔여 쿨"을 만들어 낸다.
        byte[] body =
        [
            0x00, 0x51,
            0x01,                                     // count
            0x03,                                     // mask: cooltime + bit1
            0xE0, 0x08, 0xAD, 0x00,                   // code 11340000
            0x0A, 0x0A,                               // level 10, original 10
            0x00, 0x00, 0x00, 0x00, 0x00,             // additional x5
            0xD2, 0xCB, 0x02,                         // cooltime varint = 42450
            0x00, 0x00, 0x00,                         // 고정 3바이트
            0x00,                                     // bit1 varint
        ];

        var frame = new List<byte>();
        WriteVarInt(frame, body.Length + 3);
        frame.AddRange(body);

        LearnedSkill s = Assert.Single(Assert.Single(Feed(frame.ToArray()).Snapshots));
        Assert.Equal(11_340_000, s.Code);
        Assert.Equal(10, s.Level);
        Assert.Equal(42_450, s.CooltimeMs);
    }

    [Fact]
    public void A_ready_skill_carries_no_cooltime_field_at_all()
    {
        LearnedSkill s = Assert.Single(Assert.Single(Feed(Frame((13_020_000, 10, 0))).Snapshots));
        Assert.Equal(13_020_000, s.Code);
        Assert.Equal(0, s.CooltimeMs);
    }

    [Theory]
    [InlineData(1)]           // varint 1바이트
    [InlineData(16_383)]      // 2바이트 끝
    [InlineData(42_450)]      // 3바이트 — 골든 케이스와 같은 폭
    [InlineData(2_097_152)]   // 4바이트
    public void Every_cooltime_varint_width_parses(long cooltime)
    {
        LearnedSkill s = Assert.Single(Assert.Single(Feed(Frame((13_020_000, 10, cooltime))).Snapshots));
        Assert.Equal(cooltime, s.CooltimeMs);
    }

    [Fact]
    public void A_whole_roster_of_skills_parses_in_order()
    {
        IReadOnlyList<LearnedSkill> snap = Assert.Single(Feed(Frame(
            (13_020_000, 10, 0),
            (13_080_000, 5, 42_450),
            (13_270_000, 6, 0),
            (13_300_000, 20, 1),
            (13_310_000, 25, 0))).Snapshots);

        Assert.Equal(5, snap.Count);
        Assert.Equal([13_020_000, 13_080_000, 13_270_000, 13_300_000, 13_310_000], snap.Select(s => s.Code));
        Assert.Equal([10, 5, 6, 20, 25], snap.Select(s => s.Level));
    }

    [Fact]
    public void A_record_whose_levels_do_not_add_up_voids_the_whole_frame()
    {
        // level != original + 증가분. 레이아웃을 잘못 걷고 있다는 뜻이므로 부분 채택하지 않는다 —
        // 반쪽짜리 보유집합은 배운 스킬을 픽커에서 지워 버린다.
        var body = new List<byte> { 0x00, 0x51, 0x02 };
        Record(body, 13_020_000, 10, 0);

        // 두 번째 레코드는 손으로 쓴다 — 헬퍼는 증가분을 level-original 로 채우므로 불변식을 못 깬다.
        body.Add(0x02);                                     // mask: bit1 만
        foreach (int shift in new[] { 0, 8, 16, 24 })
        {
            body.Add((byte)((13_080_000 >> shift) & 0xFF));
        }

        body.Add(9);                                        // level 9
        body.Add(3);                                        // original 3
        body.AddRange(new byte[5]);                         // 증가분 전부 0 → 9 != 3 + 0
        body.AddRange(new byte[3]);                         // 고정 3바이트
        body.Add(0);                                        // bit1 varint

        var frame = new List<byte>();
        WriteVarInt(frame, body.Count + 3);
        frame.AddRange(body);

        Assert.Empty(Feed(frame.ToArray()).Snapshots);
    }

    [Fact]
    public void A_frame_that_does_not_consume_exactly_is_dropped()
    {
        byte[] good = Frame((13_020_000, 10, 0));
        byte[] padded = [.. good, 0x00, 0x00];

        Assert.Empty(Feed(padded).Snapshots);
    }

    [Fact]
    public void The_opcode_is_registered_so_the_parser_is_actually_reachable()
    {
        // OpcodeNames 등록이 사실상의 활성화 스위치다 — 상수와 switch 만 넣으면 파서가 한 번도 안 불린다.
        Assert.True(StreamProcessor.LooksLikeGamePacket(Frame((13_020_000, 10, 0))));
    }
}
