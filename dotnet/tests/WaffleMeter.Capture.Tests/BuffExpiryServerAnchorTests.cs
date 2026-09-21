using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// 버프 <b>갱신</b>(0x382B)의 만료시각은 서버가 직접 말해준다. 미터는 그 필드를 읽어 놓고도 진단 로그로만
/// 흘린 채, 만료를 <c>도착시각 + _duration_ms</c>로 계산해 왔다.
///
/// <para>🔑 <c>_duration_ms</c>는 지속시간이 아니다 — 그 버프 인스턴스의 <b>나이 + 잔여</b>다. 같은 도착
/// 시각에 만료는 같은데 duration만 5000/5599/6400으로 갈리고, 한 계열의 duration이 도착 간격만큼 정확히
/// 증가한다(6900→7651, Δat=751). 그래서 오차 부호가 전부 음수고(p01 −7,490ms) 6세션에서 0x382B 프레임의
/// 21.0~38.2%가 500ms 이상 어긋났다. 증상은 오버레이 카운트다운이 늦게 끝나는 것과 업타임 과대 계상이다
/// (원소의 흐름 +46.5%).</para>
///
/// <para>⚠️ 반면 <b>최초 적용</b>(0x382A)은 6세션 43,921건 중 500ms 초과가 <b>0건</b>이라 고칠 게 없다.
/// 두 경로를 같이 바꾸면 이득 없이 회귀 면적만 넓어지므로, 이 테스트들이 0x382A의 무변경을 같이 고정한다.</para>
/// </summary>
public sealed class BuffExpiryServerAnchorTests
{
    /// <summary>실측 오프셋(서버 − 로컬)은 세션 중앙값 0.99~4.36초다. 1초로 잡는다.</summary>
    private const long ClockOffsetMs = 1000;

    /// <summary>에폭 하한(2020-01-01) 위에 있어야 시계 검사를 통과한다.</summary>
    private const long T = 1_789_900_000_000L;

    private const int Target = 100;
    private const int Actor = 200;
    private const int Slot = 1;

    /// <summary>직업 버프 대역(11xxxxxxx~19xxxxxxx). 시련 어픽스·아티팩트 개수 코드와 겹치지 않는다.</summary>
    private const int JobBuffCode = 161000010;

    /// <summary>폭주(권성) 변종 대역 — 유일하게 무기한 지속이 허용되는 코드다.</summary>
    private const int PokjuCode = 191300000;

    /// <summary>무기한 프레임이 싣고 오는 만료시각. 전 세션 고정값이고, 2100-01-01 KST라
    /// <c>MaxPlausibleEpochMs</c>(4,102,444,800,000)보다 9시간 <b>작아서</b> 어떤 절대 상한도 통과한다.</summary>
    private const long Year2100Sentinel = 4_102_412_400_000L;

    private sealed class RecordingData : ICaptureGameData
    {
        public readonly List<(int Uid, int SkillCode, long Start, long End, long Duration)> Buffs = [];

        public void SaveUseBuff(int uid, int skillCode, long buffStart, long buffEnd, long duration, int actorId) =>
            Buffs.Add((uid, skillCode, buffStart, buffEnd, duration));

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

    private static void WriteU64(List<byte> to, long value)
    {
        for (int i = 0; i < 8; i++)
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

    /// <summary>0x3600 MapFrame_NT — 실측 레이아웃 그대로: 본문이 Int64 LE 서버 시계 하나뿐인 11바이트 프레임
    /// (최근 3세션 20,507프레임 전부 11B).</summary>
    private static byte[] ServerClock(long serverNowMs)
    {
        var body = new List<byte> { 0x00, 0x36 };
        WriteU64(body, serverNowMs);
        return Frame(body);
    }

    /// <summary>0x382A(최초 적용) / 0x382B(갱신). 헤더 길이가 다르다 — 0x382A는 slot varint 앞에 2바이트,
    /// 0x382B는 1바이트가 붙는다.</summary>
    private static byte[] Buff(bool refresh, int skillCode, long durationMs, long serverExpiryMs)
    {
        var body = new List<byte> { refresh ? (byte)0x2B : (byte)0x2A, 0x38 };
        WriteVarInt(body, Target);
        body.Add(0x11);
        if (!refresh)
        {
            body.Add(0x01);
        }

        WriteVarInt(body, Slot);
        WriteU32(body, skillCode);
        WriteU32(body, durationMs);
        WriteU32(body, 0); // duration 뒤 4바이트 패딩(파서가 offset += 8 로 건너뛴다)
        WriteU64(body, serverExpiryMs);
        WriteVarInt(body, Actor);
        return Frame(body);
    }

    private static RecordingData Feed(params (byte[] Frame, long ArrivedAt)[] frames)
    {
        var data = new RecordingData();
        var processor = new StreamProcessor(NullStreamProcessorSink.Instance, data);
        foreach ((byte[] frame, long arrivedAt) in frames)
        {
            processor.OnPacketReceived(frame, arrivedAt);
        }

        return data;
    }

    [Fact]
    public void A_refresh_takes_its_expiry_from_the_server_not_from_the_duration_field()
    {
        // 서버축: 지금이 T+6000, 만료가 T+9000 → 잔여 3초. duration 필드는 10초(나이 7초 + 잔여 3초)를 말한다.
        // 종전 계산(도착 + duration)은 T+15000 으로 7초를 더 살려 뒀다.
        RecordingData data = Feed(
            (ServerClock(T + ClockOffsetMs), T),
            (Buff(refresh: true, JobBuffCode, durationMs: 10_000, serverExpiryMs: T + 9000), T + 5000));

        (int _, int _, long start, long end, long duration) = Assert.Single(data.Buffs);
        Assert.Equal(T + 5000, start);
        Assert.Equal(T + 8000, end);      // 서버 만료(T+9000) − 오프셋(1000)
        Assert.Equal(3000, duration);
    }

    [Fact]
    public void A_first_apply_is_left_exactly_as_it_was()
    {
        // 0x382A는 오차가 실측 0건이라 손대지 않는다 — 서버 만료가 달라도 도착 + duration 을 그대로 쓴다.
        RecordingData data = Feed(
            (ServerClock(T + ClockOffsetMs), T),
            (Buff(refresh: false, JobBuffCode, durationMs: 5000, serverExpiryMs: T + 9000), T + 5000));

        (int _, int _, long _, long end, long duration) = Assert.Single(data.Buffs);
        Assert.Equal(T + 10_000, end);
        Assert.Equal(5000, duration);
    }

    [Fact]
    public void Without_a_server_clock_the_refresh_falls_back_to_the_old_arithmetic()
    {
        // 시계를 한 번도 못 봤으면(세션 극초반, 또는 0x3600이 억제된 스트림을 탄 경우) 종전 동작으로 떨어진다.
        RecordingData data = Feed(
            (Buff(refresh: true, JobBuffCode, durationMs: 10_000, serverExpiryMs: T + 9000), T + 5000));

        (int _, int _, long _, long end, long duration) = Assert.Single(data.Buffs);
        Assert.Equal(T + 15_000, end);
        Assert.Equal(10_000, duration);
    }

    [Fact]
    public void The_year_2100_sentinel_never_reaches_the_overlay()
    {
        // 🔴 무기한 버프(폭주)의 만료시각은 절대 상한을 통과한다. 그대로 쓰면 오버레이에 영구 잔존한다.
        // 무기한 프레임은 서버 만료를 아예 보지 않고, 재방송이 갱신하는 짧은 폴백 지속시간을 쓴다.
        RecordingData data = Feed(
            (ServerClock(T + ClockOffsetMs), T),
            (Buff(refresh: true, PokjuCode, durationMs: 4294967295L, serverExpiryMs: Year2100Sentinel), T + 5000));

        (int _, int _, long _, long end, long duration) = Assert.Single(data.Buffs);
        Assert.Equal(6000, duration);            // IndefiniteStanceFallbackMs
        Assert.Equal(T + 11_000, end);
        Assert.True(end < Year2100Sentinel);
    }

    [Fact]
    public void An_expiry_already_in_the_past_falls_back_instead_of_recording_a_negative_buff()
    {
        RecordingData data = Feed(
            (ServerClock(T + ClockOffsetMs), T),
            (Buff(refresh: true, JobBuffCode, durationMs: 10_000, serverExpiryMs: T + 1000), T + 5000));

        (int _, int _, long _, long end, long duration) = Assert.Single(data.Buffs);
        Assert.Equal(T + 15_000, end);
        Assert.Equal(10_000, duration);
    }

    [Fact]
    public void An_absurd_expiry_falls_back_rather_than_pinning_a_buff_for_hours()
    {
        RecordingData data = Feed(
            (ServerClock(T + ClockOffsetMs), T),
            (Buff(refresh: true, JobBuffCode, durationMs: 10_000, serverExpiryMs: T + 7_200_000), T + 5000));

        (int _, int _, long _, long end, long _) = Assert.Single(data.Buffs);
        Assert.Equal(T + 15_000, end);
    }

    [Fact]
    public void A_clock_frame_that_is_wildly_off_is_rejected_so_a_bad_parse_cannot_poison_the_offset()
    {
        // 로컬 시계가 몇 분씩 틀어져 있거나 프레임을 잘못 읽었으면 채택을 포기하고 종전 동작으로 떨어진다.
        RecordingData data = Feed(
            (ServerClock(T + 3_600_000), T),
            (Buff(refresh: true, JobBuffCode, durationMs: 10_000, serverExpiryMs: T + 9000), T + 5000));

        (int _, int _, long _, long end, long _) = Assert.Single(data.Buffs);
        Assert.Equal(T + 15_000, end);
    }

    [Fact]
    public void The_clock_frame_itself_is_not_dispatched_as_a_known_opcode()
    {
        // 0x3600 은 OpcodeNames 에 등록하면 안 된다 — 그 딕셔너리가 LooksLikeGamePacket 의 게임 스트림
        // 판정 기준이고, 0x3600 은 게임 프레임의 8~28% 라 등록하는 순간 노이즈 가드·VPN 중복 억제
        // 휴리스틱이 조용히 바뀐다. 가로채기 방식이라 여기서 false 여야 한다.
        Assert.False(StreamProcessor.LooksLikeGamePacket(ServerClock(T)));
    }
}
