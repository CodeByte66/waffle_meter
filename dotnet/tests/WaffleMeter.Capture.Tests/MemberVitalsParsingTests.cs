using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// 0x921B(파티) / 0x962B(공대) — 멤버 HP·MP 브로드캐스트. 본문 마지막 바이트 <c>_live</c> 가 사망 구간을
/// <b>닫는</b> 유일한 신호다(실측 41창 전부 해제, 부활석 부활 포함).
///
/// <para>🔴 <b>길이로 판정하면 사망 프레임만 골라서 놓친다.</b> 실측 본문이 31/32/33바이트로 보이는 건 이
/// 파티 구성의 우연이다 — 31B가 뜻하는 건 "hp==0"이 아니라 <b>"hp&lt;128"</b>(varint 폭이 줄어서)이고, key
/// varint 폭도 마침 전부 2였을 뿐이다. 1차 조사가 33B 고정으로 짜서 사망 프레임 14건을 통째로 놓쳤고,
/// 그래서 "_live 는 부활 신호가 아니다"라는 틀린 결론까지 냈다.</para>
///
/// <para>채택 조건은 <c>offset + 25 == Length</c> 하나다. 서버가 필드를 늘리면 오독이 아니라 드롭으로 떨어진다.</para>
/// </summary>
public sealed class MemberVitalsParsingTests
{
    private sealed class RecordingData : ICaptureGameData
    {
        public readonly List<(int Key, long Hp, byte Live)> Vitals = [];

        public void SaveMemberVitals(int key, long hp, byte live, long arrivedAt) => Vitals.Add((key, hp, live));

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

    /// <summary>실측 레이아웃: <c>[key varint][hp varint][hp_max varint][25바이트 꼬리, 마지막이 live]</c>.</summary>
    private static byte[] Vitals(int key, long hp, long hpMax, byte live, bool raid = false, int extraTail = 0)
    {
        var body = new List<byte> { raid ? (byte)0x2B : (byte)0x1B, raid ? (byte)0x96 : (byte)0x92 };
        WriteVarInt(body, key);
        WriteVarInt(body, hp);
        WriteVarInt(body, hpMax);
        for (int i = 0; i < 24 + extraTail; i++)
        {
            body.Add(0);
        }

        body.Add(live);

        var frame = new List<byte>();
        WriteVarInt(frame, body.Count + 3);
        frame.AddRange(body);
        return frame.ToArray();
    }

    private static RecordingData Feed(params byte[][] frames)
    {
        var data = new RecordingData();
        var p = new StreamProcessor(NullStreamProcessorSink.Instance, data);
        foreach (byte[] f in frames)
        {
            p.OnPacketReceived(f, 0);
        }

        return data;
    }

    [Fact]
    public void A_living_member_reports_live_one()
    {
        Assert.Equal((1758, 24657L, (byte)1), Assert.Single(Feed(Vitals(1758, 24657, 36575, live: 1)).Vitals));
    }

    [Fact]
    public void A_dead_member_is_not_lost_when_the_hp_varint_shrinks()
    {
        // 🔴 여기가 1차 조사가 밟은 함정이다. hp==0 이면 varint 가 3바이트에서 1바이트로 줄어 본문이 짧아진다.
        // 길이를 고정으로 잡으면 **사망 프레임만** 걸러져 "부활 신호가 없다"는 결론이 나온다.
        Assert.Equal((1758, 0L, (byte)0), Assert.Single(Feed(Vitals(1758, 0, 36575, live: 0)).Vitals));
    }

    [Theory]
    [InlineData(1)]        // hp varint 1바이트
    [InlineData(127)]      // 아직 1바이트
    [InlineData(128)]      // 2바이트로 넘어감
    [InlineData(16_383)]   // 2바이트 끝
    [InlineData(16_384)]   // 3바이트
    public void Every_hp_varint_width_parses(long hp)
    {
        Assert.Equal(hp, Assert.Single(Feed(Vitals(1758, hp, 36575, live: 1)).Vitals).Hp);
    }

    [Theory]
    [InlineData(7)]          // key varint 1바이트
    [InlineData(1758)]       // 2바이트
    [InlineData(2_000_000)]  // 4바이트
    public void Every_key_varint_width_parses(int key)
    {
        Assert.Equal(key, Assert.Single(Feed(Vitals(key, 100, 200, live: 1)).Vitals).Key);
    }

    [Fact]
    public void The_raid_opcode_shares_the_handler()
    {
        // 공대에서는 0x921B(본인 서브파티)와 0x962B(전원)가 동시에 온다 — 같은 키가 양쪽에 실린다.
        RecordingData data = Feed(
            Vitals(16329, 500, 1000, live: 1, raid: false),
            Vitals(16329, 500, 1000, live: 1, raid: true));

        Assert.Equal(2, data.Vitals.Count);
        Assert.All(data.Vitals, v => Assert.Equal(16329, v.Key));
    }

    [Fact]
    public void A_tail_that_is_not_exactly_25_bytes_is_dropped_rather_than_misread()
    {
        // 서버가 필드를 늘리면 오독이 아니라 드롭이어야 한다.
        Assert.Empty(Feed(Vitals(1758, 100, 200, live: 1, extraTail: 3)).Vitals);
    }

    [Fact]
    public void An_out_of_range_live_byte_still_delivers_the_hp()
    {
        // live 가 {0,1} 밖이면 그 필드만 의심스럽다. 소비 측이 fail-open 이라 프레임을 통째로 버리면
        // 해제 신호가 사라진다 — hp 는 반드시 살려서 넘긴다.
        (int _, long hp, byte live) = Assert.Single(Feed(Vitals(1758, 900, 1000, live: 7)).Vitals);
        Assert.Equal(900, hp);
        Assert.Equal(7, live);
    }
}
