using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// 0x3641 NpcAppear_NT 의 현재/최대 HP 를 usmap 스키마 순서로 읽는다(<see cref="StreamProcessor.TryReadNpcAppearVitals"/>).
/// <para>배경(2026-10-27 21:00 감시자 카이라, 로그 20261027-205641): 진짜 최대 HP 145.8억은 스폰에만 실렸고
/// (0x8D00 statId 7 은 0건), 미터는 그걸 안 읽은 채 10e9 상한에 걸린 0x8D00 222개를 버리고 처음 받아들인 99.98억을
/// 최대로 래치했다 — % 가 1.458배 부풀었다. 아래 픽스처는 전부 그 로그에서 뽑은 실제 프레임이다.</para>
/// <para>🔴 HP 앞 3바이트가 첫 스폰은 <c>00 52 01</c>, 재진입은 <c>f2 3c 01</c> 이다 — <c>_move_dir</c>(u16)와
/// <c>_live</c> 라 스폰마다 다르다. 패턴으로 찾으면 재진입을 못 읽는다.</para>
/// </summary>
public sealed class NpcAppearVitalsTests
{
    // 감시자 카이라(2600089, 엔티티 16497) 최초 스폰 — 151바이트 전체. 마스크 u32, CharState 0x0200.
    private const string KairaFirstSpawn =
        "99014136F18001042000000099AC27000002244ADDC60AA6EE450004114600A0E64200520180CAA4A83680CAA4A836" +
        "640000006400000000000000000000000000000000000000400D0300400D030003000000000000000000000000000000" +
        "000000000601110181969800FFFFFFFFFFFFFFFF8075D52ABB030000F180010100244ADDC60AA6EE4500041146060041" +
        "00000033C6CC7B00";

    // 같은 엔티티의 재진입 스폰(원거리 부활 뒤 13초) — 원본 6,012바이트 중 앞 107바이트. 나머지는 버프 목록이고
    // 파서는 교전 바이트(+52) 너머를 읽지 않는다. CharState 0x0240 → 교전 바이트 01.
    private const string KairaReentrySpawnHead =
        "FE2E4136F18001042000000099AC2700400230A1E6C6729C2346008C0E46169BB642F23C01F8AA97F40880CAA4A836" +
        "00000000640000000000000000000000000000000000000000000000400D030001000000000000000000000000000000" +
        "0000000001068901138101BD";

    // 이동 중 스폰(CharState 0x0208 = velocity 12바이트 포함) — 서부 진영 하늘길 수호자(2600091). 200바이트 전체.
    private const string MovingSpawn =
        "CA01413697BD010D200000009BAC27000802B44208C7A9D10B470537B846336E2F43C07C5144ADC3AF91DD413C592242" +
        "01C0F0F50BC0F0F50B6400000064000000000000000000000000000000000000000000000050C30000010000000000" +
        "00000000000000000000000000000302110181969800FFFFFFFFFFFFFFFF8075D52ABB03000097BD01010420D208C748" +
        "D30A47E72EB846110284969800FFFFFFFFFFFFFFFF8075D52ABB03000097BD010120D208C748D30A47E72EB846090100" +
        "32000000A3A6F57F00";

    // 시체로 시야에 들어온 몹(2600009, 엔티티 16751) — live 0 / hp 0 / max 60,000. 106바이트 전체.
    private const string DeadSpawn =
        "6D4136EF8201052000000049AC27000002CCAADDC64E7D034600501046999EA843D0EF0000E0D4036400000064000000" +
        "00000000000000000000000000000000000000002C010000010000000000000000000000000000000000000006000201" +
        "002D0000000C3971DF00";

    // 2026-08-08 이전 클라 세대(NpcAppear 마스크 u16) — SummonAttributionTests 의 22744 소환수 프레임과 같은 바이트.
    private const string Summon22744OldGeneration =
        "D3014136D8B1015F000109EBB094EB8298EB8298AA912C004002005F02C70008844500D8D345C18EAC41570F01A6E702A6E702" +
        "C4270000C4270000000000000000000000000000F0C6020064000000F04902000100000000000000A086010000000000C8" +
        "571000010101110181969800FFFFFFFFFFFFFFFF8075D52ABB030000D8B1010102005F02C70008844500D8D345070206832D" +
        "0000120500000000DF0706EC9CA0EC9DBC02000200000000000000000000000000000003CD00EC200000D00042030000D600" +
        "960000003200000000";

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    /// <summary>opcode 바로 뒤 오프셋(길이 varint + opcode 2바이트). 픽스처는 전부 extraFlag 없음.</summary>
    private static int AfterOpcode(byte[] p) => PacketPrimitives.ReadVarInt(p).Length + 2;

    private static SpawnVitalsResult Read(byte[] p) =>
        StreamProcessor.TryReadNpcAppearVitals(p, AfterOpcode(p), out StreamProcessor.SpawnVitals v)
            ? new SpawnVitalsResult(true, v)
            : new SpawnVitalsResult(false, default);

    private readonly record struct SpawnVitalsResult(bool Ok, StreamProcessor.SpawnVitals V);

    [Fact]
    public void Kaira_first_spawn_carries_the_true_max_hp()
    {
        SpawnVitalsResult r = Read(Hex(KairaFirstSpawn));

        Assert.True(r.Ok);
        Assert.Equal(16497, r.V.Key);
        Assert.Equal(2600089, r.V.PcId);
        Assert.Equal(1, r.V.Live);
        Assert.Equal(14_580_000_000L, r.V.Hp);
        Assert.Equal(14_580_000_000L, r.V.HpMax);
        Assert.False(r.V.InCombat);
        Assert.Equal(4, r.V.MaskBytes);
    }

    [Fact]
    public void Kaira_reentry_spawn_carries_the_hp_left_while_out_of_sight()
    {
        SpawnVitalsResult r = Read(Hex(KairaReentrySpawnHead));

        Assert.True(r.Ok);
        Assert.Equal(16497, r.V.Key);
        Assert.Equal(2_391_135_608L, r.V.Hp);
        Assert.Equal(14_580_000_000L, r.V.HpMax);
        Assert.True(r.V.InCombat);
    }

    [Fact]
    public void A_moving_spawn_skips_the_velocity_and_reads_its_hp()
    {
        SpawnVitalsResult r = Read(Hex(MovingSpawn));

        Assert.True(r.Ok);
        Assert.Equal(2600091, r.V.PcId);
        Assert.Equal(25_000_000L, r.V.Hp);
        Assert.Equal(25_000_000L, r.V.HpMax);
    }

    [Fact]
    public void A_moving_spawn_still_defeats_the_old_mob_code_marker_scan()
    {
        // 회귀 단언: mobCode 등록 소스는 이번에 바꾸지 않았다(pc_id 로 바꾸는 건 별도 승인 사항). 이 프레임은
        // 마커(00 40 02 / 00 00 02)가 없어 SaveMobId 가 불리지 않는다 — 순찰 중 시야에 든 보스가 빠지는 구멍은 남아 있다.
        var data = new Recorder();
        new StreamProcessor(NullStreamProcessorSink.Instance, data).OnPacketReceived(Hex(MovingSpawn), 0);

        Assert.Empty(data.MobIds);
        Assert.Empty(data.SpawnVitals);
    }

    [Fact]
    public void A_corpse_spawn_reads_live_zero_hp_zero()
    {
        SpawnVitalsResult r = Read(Hex(DeadSpawn));

        Assert.True(r.Ok);
        Assert.Equal(16751, r.V.Key);
        Assert.Equal(0, r.V.Live);
        Assert.Equal(0L, r.V.Hp);
        Assert.Equal(60_000L, r.V.HpMax);
    }

    [Fact]
    public void The_old_two_byte_mask_generation_is_read_by_the_fallback()
    {
        SpawnVitalsResult r = Read(Hex(Summon22744OldGeneration));

        Assert.True(r.Ok);
        Assert.Equal(2, r.V.MaskBytes);
        Assert.Equal(22744, r.V.Key);
        Assert.Equal(45_990L, r.V.Hp);
        Assert.Equal(45_990L, r.V.HpMax);
    }

    // ---- 음성 케이스: 실제 첫 스폰의 HP 두 칸만 바꿔 끼운다 (live @36, hp @37..41, max @42..46) ----

    private static byte[] WithHp(long hp, long max, byte live = 1)
    {
        byte[] real = Hex(KairaFirstSpawn);
        var f = new List<byte>(real[..36]) { live };
        f.AddRange(VarU64(hp));
        f.AddRange(VarU64(max));
        f.AddRange(real[47..]);
        return f.ToArray();
    }

    private static byte[] VarU64(long v)
    {
        var b = new List<byte>();
        ulong u = (ulong)v;
        do
        {
            byte x = (byte)(u & 0x7F);
            u >>= 7;
            b.Add(u != 0 ? (byte)(x | 0x80) : x);
        }
        while (u != 0);
        return b.ToArray();
    }

    [Fact]
    public void The_splice_helper_reproduces_the_real_frame()
    {
        Assert.Equal(Hex(KairaFirstSpawn), WithHp(14_580_000_000L, 14_580_000_000L));
    }

    [Fact]
    public void An_unobserved_char_state_bit_fails_closed()
    {
        byte[] p = Hex(KairaFirstSpawn);
        p[16] |= 0x01; // CharState 마스크 하위 바이트 — bit0(로컬 좌표)는 폭을 모른다

        Assert.False(Read(p).Ok);
    }

    [Fact]
    public void Current_hp_above_max_fails()
    {
        Assert.False(Read(WithHp(14_580_000_001L, 14_580_000_000L)).Ok);
    }

    [Fact]
    public void Max_hp_above_the_abyss_cap_fails_and_the_cap_itself_passes()
    {
        Assert.False(Read(WithHp(1_000_000_000_001L, 1_000_000_000_001L)).Ok);
        Assert.True(Read(WithHp(1_000_000_000_000L, 1_000_000_000_000L)).Ok);
    }

    [Fact]
    public void Live_and_hp_must_agree()
    {
        Assert.False(Read(WithHp(5_000L, 60_000L, live: 0)).Ok);
        Assert.False(Read(WithHp(0L, 60_000L, live: 1)).Ok);
    }

    [Fact]
    public void A_truncated_frame_fails()
    {
        byte[] real = Hex(KairaFirstSpawn);

        Assert.False(Read(real[..60]).Ok); // HP 뒤 52바이트(mp…)가 다 오기 전에 끊겼다
        Assert.False(Read(real[..40]).Ok); // HP varint 한가운데
    }

    // ---- StreamProcessor 연결: 어비스 필드보스만 넘긴다 ----

    [Fact]
    public void An_abyss_field_boss_spawn_forwards_its_vitals_to_the_data_layer()
    {
        var data = new Recorder();
        var sp = new StreamProcessor(NullStreamProcessorSink.Instance, data);
        sp.OnPacketReceived(Hex(KairaFirstSpawn), 0);

        Assert.Equal((16497, 2600089), Assert.Single(data.MobIds));
        Assert.Equal((16497, 1, 14_580_000_000L, 14_580_000_000L, false), Assert.Single(data.SpawnVitals));
    }

    [Fact]
    public void A_spawn_of_a_mob_outside_the_abyss_field_boss_table_is_not_read()
    {
        // 2600009 는 어비스 대역이지만 필드보스 표에 없다 — 접두 규칙이 아니라 표가 스코프다.
        var data = new Recorder();
        new StreamProcessor(NullStreamProcessorSink.Instance, data).OnPacketReceived(Hex(DeadSpawn), 0);

        Assert.Equal((16751, 2600009), Assert.Single(data.MobIds));
        Assert.Empty(data.SpawnVitals);
    }

    [Fact]
    public void A_spawn_whose_pc_id_disagrees_with_the_registered_code_is_not_forwarded()
    {
        // 재진입 스폰의 마커 스캔이 실패해도(이동 중) 옛 코드가 남아 있다 — 그 id 가 다른 NPC 에게 넘어갔다면
        // 그 NPC 의 HP 가 보스 것으로 둔갑한다. pc_id 가 다르면 넘기지 않는다.
        var data = new Recorder();
        data.SaveMobId(24215, 2600089); // 엔티티 24215 를 카이라로 알고 있는 상태
        new StreamProcessor(NullStreamProcessorSink.Instance, data).OnPacketReceived(Hex(MovingSpawn), 0);

        Assert.Empty(data.SpawnVitals);
    }

    // ---- 0x8D00: 어비스 필드보스만 상한 1e12 ----

    private static byte[] RemainHpFrame(byte[] entity, long hp)
    {
        var body = new List<byte>(entity) { 0x02, 0x01, 0x00 };
        body.AddRange(BitConverter.GetBytes(hp));
        var f = new List<byte> { (byte)(body.Count + 3 + 3), 0x00, 0x8D };
        f.AddRange(body);
        return f.ToArray();
    }

    private static readonly byte[] KairaEntity = { 0xF1, 0x80, 0x01 }; // 16497
    private static readonly byte[] DungeonEntity = { 0xEF, 0x5D };     // 12015

    [Fact]
    public void An_abyss_field_boss_hp_frame_above_ten_billion_is_accepted()
    {
        var data = new Recorder();
        data.SaveMobId(16497, 2600089);
        new StreamProcessor(NullStreamProcessorSink.Instance, data)
            .OnPacketReceived(RemainHpFrame(KairaEntity, 14_579_798_624L), 0);

        Assert.Equal((16497, 14_579_798_624L), Assert.Single(data.Hp));
    }

    [Fact]
    public void Every_other_boss_keeps_the_ten_billion_cap()
    {
        var data = new Recorder();
        data.SaveMobId(12015, 2300582); // 시련 바크론 — 던전 보스
        var sp = new StreamProcessor(NullStreamProcessorSink.Instance, data);
        sp.OnPacketReceived(RemainHpFrame(DungeonEntity, 10_000_000_001L), 0);
        Assert.Empty(data.Hp);

        sp.OnPacketReceived(RemainHpFrame(DungeonEntity, 10_000_000_000L), 0);
        Assert.Equal((12015, 10_000_000_000L), Assert.Single(data.Hp));
    }

    [Fact]
    public void An_abyss_field_boss_hp_frame_above_the_abyss_cap_is_rejected()
    {
        var data = new Recorder();
        data.SaveMobId(16497, 2600089);
        new StreamProcessor(NullStreamProcessorSink.Instance, data)
            .OnPacketReceived(RemainHpFrame(KairaEntity, 1_000_000_000_001L), 0);

        Assert.Empty(data.Hp);
    }

    // ---- 0x3642 ----

    [Fact]
    public void A_disappear_frame_is_forwarded_with_its_entity_only()
    {
        var data = new Recorder();
        var sp = new StreamProcessor(NullStreamProcessorSink.Instance, data);
        sp.OnPacketReceived(Hex("0B4236F180010001"), 0); // 실측: 카이라 시야 이탈 (사유 01)
        sp.OnPacketReceived(Hex("0B4236F180010003"), 0); // 실측: 처치 뒤 (사유 03) — 사유는 게이트에 안 간다

        Assert.Equal(new[] { 16497, 16497 }, data.Disappeared);
    }

    [Fact]
    public void A_disappear_frame_of_an_unknown_shape_is_ignored()
    {
        var data = new Recorder();
        var sp = new StreamProcessor(NullStreamProcessorSink.Instance, data);
        sp.OnPacketReceived(Hex("0C4236F18001000100"), 0); // 꼬리가 남는다
        sp.OnPacketReceived(Hex("0B4236F180010101"), 0);   // mask 가 켜졌다 — 폭을 모르는 필드

        Assert.Empty(data.Disappeared);
    }

    private sealed class Recorder : ICaptureGameData
    {
        private static readonly Dictionary<int, Mob> Mobs = new()
        {
            [2600089] = new Mob(2600089, "감시자 카이라", Boss: true),
            [2600009] = new Mob(2600009, "어비스 몹", Boss: false),
            [2300582] = new Mob(2300582, "바크론", Boss: true),
        };

        private readonly Dictionary<int, int> _ids = new();
        public readonly List<(int Instance, int Code)> MobIds = new();
        public readonly List<(int Instance, int Live, long Hp, long Max, bool Combat)> SpawnVitals = new();
        public readonly List<(int Instance, long Hp)> Hp = new();
        public readonly List<int> Disappeared = new();

        public Mob? GetMob(int code) => Mobs.GetValueOrDefault(code);
        public int? GetMobId(int instanceId) => _ids.TryGetValue(instanceId, out int c) ? c : null;

        public void SaveMobId(int instanceId, int mobCode)
        {
            _ids[instanceId] = mobCode;
            MobIds.Add((instanceId, mobCode));
        }

        public void SaveMobSpawnVitals(int instanceId, int live, long hp, long maxHp, bool inCombat) =>
            SpawnVitals.Add((instanceId, live, hp, maxHp, inCombat));

        public void SaveMobDisappear(int instanceId) => Disappeared.Add(instanceId);
        public bool SkillExists(long code) => false;
        public long CurrentEpoch() => 0;
        public void SaveDamage(ParsedDamagePacket pdp, long epoch) { }
        public void StartBattle(int target) { }
        public void EndBattle(int target) { }
        public void SaveNickname(int uid, string nickname, bool isExecutor, int server, int jobByte) { }
        public void SaveUserPower(int uid, int power) { }
        public void SaveSummon(int summonId, int ownerId) { }
        public void SaveMobHp(int instanceId, long hp) => Hp.Add((instanceId, hp));
        public void SaveUseBuff(int uid, int skillCode, long buffStart, long buffEnd, long duration, int actorId) { }
        public void RequestOfficialCharacterLookup(int uid) { }
        public void SavePartyRoster(IReadOnlyList<(string Nickname, int Server, int Slot)> members) { }
        public void SaveAetherStatus(int baseVal, int bonus) { }
        public void SaveShugoKey(int baseVal, int bonus) { }
        public void SaveFieldBossTimers(IReadOnlyList<(int Code, long TargetMs)> timers) { }
    }
}
