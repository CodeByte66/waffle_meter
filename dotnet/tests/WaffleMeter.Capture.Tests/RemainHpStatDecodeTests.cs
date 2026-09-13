using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// 0x8D00은 "몹 HP 패킷"이 아니라 엔티티 스탯 브로드캐스트다:
/// <c>[varint entity][varint mask]</c> + <c>mask&amp;1 → [u8 n][n × (u8 statId, u32)]</c>
/// + <c>mask&amp;2 → [u8 m][m × (u8 statId, u64)]</c>, statId 0 = 현재 HP / 7 = 최대 HP.
/// 종전 구현은 varint 3개를 건너뛰고 u32를 읽어 <b>우연히</b> u64 HP의 하위 32비트에 착지했고,
/// 최대 HP만 실린 프레임에서는 만피를 잔여 HP로 발행했다.
/// </summary>
public sealed class RemainHpStatDecodeTests
{
    private const int BossInstance = 12015;
    private const int BossCode = 2300582;

    private sealed class Recorder : ICaptureGameData
    {
        public readonly List<(int Instance, long Hp)> Hp = new();
        public readonly List<(int Instance, long MaxHp)> MaxHp = new();

        public Mob? GetMob(int code) => code == BossCode ? new Mob(BossCode, "보스", Boss: true) : null;
        public int? GetMobId(int instanceId) => instanceId == BossInstance ? BossCode : null;
        public void SaveMobId(int instanceId, int mobCode) { }
        public bool SkillExists(long code) => false;
        public long CurrentEpoch() => 0;
        public void SaveDamage(ParsedDamagePacket pdp, long epoch) { }
        public void StartBattle(int target) { }
        public void EndBattle(int target) { }
        public void SaveNickname(int uid, string nickname, bool isExecutor, int server, int jobByte) { }
        public void SaveUserPower(int uid, int power) { }
        public void SaveSummon(int summonId, int ownerId) { }
        public void SaveMobHp(int instanceId, long hp) => Hp.Add((instanceId, hp));
        public void SaveMobMaxHp(int instanceId, long maxHp) => MaxHp.Add((instanceId, maxHp));
        public void SaveUseBuff(int uid, int skillCode, long buffStart, long buffEnd, long duration, int actorId) { }
        public void RequestOfficialCharacterLookup(int uid) { }
        public void SavePartyRoster(IReadOnlyList<(string Nickname, int Server, int Slot)> members) { }
        public void SaveAetherStatus(int baseVal, int bonus) { }
        public void SaveShugoKey(int baseVal, int bonus) { }
        public void SaveFieldBossTimers(IReadOnlyList<(int Code, long TargetMs)> timers) { }
    }

    private sealed class NullSink : IStreamProcessorSink
    {
        public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) { }
        public void UnknownOpcode(int opcode, bool extraFlag, int len) { }
        public void CompressedPacket(int len, bool extraFlag) { }
        public void ParserError(string stage, string reason) { }
        public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode) { }
        public void Meta(string type, params (string Key, object? Value)[] fields) { }
        public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
    }

    private static readonly byte[] Entity = { 0xEF, 0x5D }; // varint 12015

    private static byte[] B(params byte[] bytes) => bytes;

    private static byte[] U64(long v) => BitConverter.GetBytes(v);

    private static byte[] Cat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (byte[] p in parts)
        {
            all.AddRange(p);
        }

        return all.ToArray();
    }

    // 실제 캡처된 프레임 형태 그대로: 첫 varint 길이값 = 실제 프레임 길이 + 3.
    private static byte[] Frame(params byte[][] bodyParts)
    {
        byte[] body = Cat(bodyParts);
        var f = new byte[1 + 2 + body.Length];
        f[0] = (byte)(f.Length + 3);
        f[1] = 0x00;
        f[2] = 0x8D;
        body.CopyTo(f, 3);
        return f;
    }

    private static Recorder Run(byte[] frame)
    {
        var data = new Recorder();
        new StreamProcessor(new NullSink(), data).OnPacketReceived(frame, 0);
        return data;
    }

    [Fact]
    public void Current_hp_is_read_from_statId_0()
    {
        // mask=2, 항목 1개, statId 0, u64 = 880,000,000 (바크론 티어)
        Recorder r = Run(Frame(Entity, B(0x02, 0x01, 0x00), U64(880_000_000L)));

        Assert.Equal((BossInstance, 880_000_000L), Assert.Single(r.Hp));
        Assert.Empty(r.MaxHp);
    }

    [Fact]
    public void A_frame_carrying_only_max_hp_does_not_publish_a_remaining_hp()
    {
        // 종전 버그: statId를 보지 않고 첫 값을 읽어 최대 HP를 잔여 HP로 발행 → 교전 첫 프레임에 만피로 튐.
        Recorder r = Run(Frame(Entity, B(0x02, 0x01, 0x07), U64(880_000_000L)));

        Assert.Empty(r.Hp);
        Assert.Equal((BossInstance, 880_000_000L), Assert.Single(r.MaxHp));
    }

    [Fact]
    public void Both_stats_in_one_frame_are_read_by_id_not_by_position()
    {
        // 최대 HP가 현재 HP보다 앞에 실려도 각각 제자리로 간다.
        Recorder r = Run(Frame(Entity, B(0x02, 0x02, 0x07), U64(880_000_000L), B(0x00), U64(123_456_789L)));

        Assert.Equal((BossInstance, 123_456_789L), Assert.Single(r.Hp));
        Assert.Equal((BossInstance, 880_000_000L), Assert.Single(r.MaxHp));
    }

    [Fact]
    public void The_u32_resource_list_is_skipped_before_the_u64_list()
    {
        // mask=3: [u8 n][n × (statId,u32)] 다음에 u64 리스트가 온다.
        Recorder r = Run(Frame(
            Entity,
            B(0x03, 0x01, 0x01), BitConverter.GetBytes(4257), // u32 자원 스탯 1개
            B(0x01, 0x00), U64(555_000_000L)));               // u64 리스트: 1개, statId 0

        Assert.Equal((BossInstance, 555_000_000L), Assert.Single(r.Hp));
    }

    [Fact]
    public void An_unknown_mask_bit_drops_the_frame()
    {
        // 정상 게임 프레임의 mask는 1/2/3뿐(실측 416,356건 중 위반 0). 그 밖은 노이즈 스트림이다.
        Recorder r = Run(Frame(Entity, B(0x04, 0x01, 0x00), U64(880_000_000L)));

        Assert.Empty(r.Hp);
        Assert.Empty(r.MaxHp);
    }

    [Fact]
    public void A_frame_we_do_not_consume_exactly_is_dropped()
    {
        Recorder r = Run(Frame(Entity, B(0x02, 0x01, 0x00), U64(880_000_000L), B(0xAA, 0xBB)));

        Assert.Empty(r.Hp);
    }

    [Fact]
    public void An_implausible_hp_is_rejected_even_though_the_frame_parses_exactly()
    {
        // 실측 프레임: 13 00 8D EF 5D 02 01 00 AF 52 08 ED 00 00 34 2A — 프레임을 정확히 소진하지만
        // 값이 3.0e18이다. 길이 검사로는 못 걸러지므로 상한 검사가 따로 필요하다.
        byte[] frame = { 0x13, 0x00, 0x8D, 0xEF, 0x5D, 0x02, 0x01, 0x00, 0xAF, 0x52, 0x08, 0xED, 0x00, 0x00, 0x34, 0x2A };
        Recorder r = Run(frame);

        Assert.Empty(r.Hp);
    }

    [Fact]
    public void An_hp_above_int_max_is_not_saturated()
    {
        // 종전에는 저장 직전 Saturate()가 int.MaxValue로 잘랐다. 델트라스(27억대)·차원핵 계열이 그 상한을
        // 넘겨 게이지·보스 체력 기여도·전투 기록이 통째로 21.47억에 붙었다(replay-diag 실측 144건/13코드).
        // 와이어는 u64로 정확했으므로 잘리는 건 저장 경로뿐이었다 — 그 경로가 다시 좁아지면 여기서 걸린다.
        const long twentySevenHundredMillion = 2_720_000_000L;
        Recorder r = Run(Frame(Entity, B(0x02, 0x02, 0x07), U64(twentySevenHundredMillion), B(0x00), U64(2_500_000_000L)));

        Assert.Equal((BossInstance, 2_500_000_000L), Assert.Single(r.Hp));
        Assert.Equal((BossInstance, twentySevenHundredMillion), Assert.Single(r.MaxHp));
    }

    [Fact]
    public void A_non_boss_entity_is_ignored_as_before()
    {
        Recorder r = Run(Frame(B(0x02), B(0x02, 0x01, 0x00), U64(1234L))); // entity 2 = 미등록

        Assert.Empty(r.Hp);
    }
}
