using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// 0xE005 UpdateGroggyInfo_NT — 보스 무력화(그로기) 게이지. 미터는 이 opcode 를 등록조차 하지 않아 세션당
/// 수천 프레임을 unknown 으로 버리고 있었다.
///
/// <para>실측 907프레임에서 본문은 두 모양뿐이다:
/// <code>
/// 17B: [entity varint][mask=0x03][state=0x01 GroggyGuard][max u32 LE][cur u32 LE][flags]
///  9B: [entity varint][mask=0x00][state=0x03 Groggy][flags]      ← 발동 순간(표본 0.3%)
/// </code>
/// 게이지는 max 에서 0 으로 <b>깎이고</b>, 바닥에서 그로기가 터진 뒤 만충으로 리필된다.</para>
///
/// <para>🔴 <b>길이로 판정하지 않는다.</b> mask/state 로 모양을 가리고 수치가 들어갈 자리가 실제로 있을 때만
/// 읽는다 — 새 변종이 생겼을 때 오독하는 것보다 조용히 버리는 쪽이 낫다.</para>
/// </summary>
public sealed class GroggyGaugeParsingTests
{
    private sealed class RecordingData : ICaptureGameData
    {
        public readonly List<(int Entity, long Max, long Cur)> Gauges = [];

        public void SaveGroggyGauge(int entityId, long max, long cur) => Gauges.Add((entityId, max, cur));

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

    private static byte[] Frame(List<byte> body)
    {
        var frame = new List<byte>();
        WriteVarInt(frame, body.Count + 3);
        frame.AddRange(body);
        return frame.ToArray();
    }

    /// <summary>수치가 실린 17바이트 모양.</summary>
    private static byte[] Gauge(int entity, long max, long cur, byte flags = 0x02)
    {
        var body = new List<byte> { 0x05, 0xE0 };
        WriteVarInt(body, entity);
        body.Add(0x03); // mask
        body.Add(0x01); // state = GroggyGuard
        WriteU32(body, max);
        WriteU32(body, cur);
        body.Add(flags);
        return Frame(body);
    }

    /// <summary>발동 순간의 9바이트 모양 — 수치가 없다.</summary>
    private static byte[] Triggered(int entity)
    {
        var body = new List<byte> { 0x05, 0xE0 };
        WriteVarInt(body, entity);
        body.Add(0x00); // mask = 필드 없음
        body.Add(0x03); // state = Groggy
        body.Add(0x02);
        return Frame(body);
    }

    private static RecordingData Feed(params byte[][] frames)
    {
        var data = new RecordingData();
        var processor = new StreamProcessor(NullStreamProcessorSink.Instance, data);
        foreach (byte[] frame in frames)
        {
            processor.OnPacketReceived(frame, 0);
        }

        return data;
    }

    [Fact]
    public void A_real_frame_decodes_to_entity_max_and_current()
    {
        // 코퍼스 실측 프레임 그대로: 보스 18307, max 7500, cur 7500(만충), flags 0x03.
        RecordingData data = Feed(Gauge(18307, 7500, 7500, flags: 0x03));

        Assert.Equal((18307, 7500L, 7500L), Assert.Single(data.Gauges));
    }

    [Fact]
    public void The_gauge_is_read_at_whatever_tier_the_boss_is_on()
    {
        // 🔴 max 를 상수로 박으면 안 된다 — 실측 4티어(2250/3000/6000/7500)이고 시련 어픽스로 전투 도중에도 바뀐다.
        RecordingData data = Feed(
            Gauge(1000, 2250, 450),
            Gauge(1000, 3000, 600),
            Gauge(1000, 6000, 1200),
            Gauge(1000, 7500, 1500));

        Assert.Equal([2250L, 3000L, 6000L, 7500L], data.Gauges.Select(g => g.Max));
    }

    [Fact]
    public void The_trigger_frame_carries_no_numbers_and_is_ignored()
    {
        Assert.Empty(Feed(Triggered(18307)).Gauges);
    }

    [Fact]
    public void An_unknown_shape_is_dropped_rather_than_misread()
    {
        // mask/state 가 (0x03, 0x01)이 아닌 프레임을 수치로 읽으면 엉뚱한 비율이 나온다 — 조용히 버린다.
        var body = new List<byte> { 0x05, 0xE0 };
        WriteVarInt(body, 18307);
        body.Add(0x07); // 미지 mask
        body.Add(0x01);
        WriteU32(body, 7500);
        WriteU32(body, 100);
        body.Add(0x02);

        Assert.Empty(Feed(Frame(body)).Gauges);
    }

    [Fact]
    public void A_truncated_frame_never_reads_past_the_buffer()
    {
        var body = new List<byte> { 0x05, 0xE0 };
        WriteVarInt(body, 18307);
        body.Add(0x03);
        body.Add(0x01);
        WriteU32(body, 7500); // cur 자리가 없다

        Assert.Empty(Feed(Frame(body)).Gauges);
    }
}
