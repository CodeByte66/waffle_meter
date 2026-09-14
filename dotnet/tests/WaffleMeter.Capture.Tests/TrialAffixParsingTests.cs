using System.Collections.Generic;
using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// Parser-level spec for reading 시련: 바크론의 공중섬's difficulty.
/// <para>The affix abnormals sit BELOW the job-buff code band and carry an indefinite duration, so the buff
/// parser's two drop rules would each discard them on their own. These tests pin that they are taken out
/// before either rule — and that they never reach the buff store, which would put a mob's buff on the
/// player's buff overlay.</para>
/// </summary>
public sealed class TrialAffixParsingTests
{
    private sealed class RecordingData : ICaptureGameData
    {
        public List<(TrialAffixGroup Group, int Level)> Affixes { get; } = [];
        public List<(int MapId, int Phase, long StartMs, long WindowMs)> Windows { get; } = [];
        public int BuffSaves { get; private set; }

        public void SaveTrialAffix(TrialAffixGroup group, int level, long arrivedAt)
            => Affixes.Add((group, level));

        public void SaveInstancePhaseWindow(int mapId, int phase, long startMs, long windowMs)
            => Windows.Add((mapId, phase, startMs, windowMs));

        public void SaveUseBuff(int uid, int skillCode, long buffStart, long buffEnd, long duration, int actorId)
            => BuffSaves++;

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
        public void SaveAetherStatus(int baseVal, int bonus) { }
        public void SaveShugoKey(int baseVal, int bonus) { }
        public void SaveFieldBossTimers(IReadOnlyList<(int Code, long TargetMs)> timers) { }
        public void SavePartyRoster(IReadOnlyList<(string Nickname, int Server, int Slot)> members) { }
        public void RequestOfficialCharacterLookup(int uid) { }
    }

    private sealed class NullSink : IStreamProcessorSink
    {
        public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) { }
        public void UnknownOpcode(int opcode, bool extraFlag, int len) { }
        public void CompressedPacket(int len, bool extraFlag) { }
        public void ParserError(string stage, string reason) { }
        public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode) { }
        public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
        public void Meta(string type, params (string Key, object? Value)[] fields) { }
    }

    private static byte[] Packet(List<byte> body)
    {
        var packet = new byte[body.Count + 1];
        packet[0] = (byte)System.Math.Min(body.Count, 255);
        body.CopyTo(packet, 1);
        return packet;
    }

    private static void AddU32(List<byte> b, long v)
    {
        b.Add((byte)(v & 0xFF));
        b.Add((byte)((v >> 8) & 0xFF));
        b.Add((byte)((v >> 16) & 0xFF));
        b.Add((byte)((v >> 24) & 0xFF));
    }

    private static void AddU64(List<byte> b, long v)
    {
        for (int i = 0; i < 8; i++)
        {
            b.Add((byte)((v >> (8 * i)) & 0xFF));
        }
    }

    /// <summary>0x382A apply: [0x2A][0x38][varint target][01][kind][varint slot][u32 code][u32 duration]
    /// [u32 pad][u64 serverTime][varint actor].</summary>
    private static List<byte> BuffApply(int skillCode, long duration)
    {
        var body = new List<byte> { 0x2A, 0x38, 0x40, 0x01, 0x00, 0x01 };
        AddU32(body, skillCode);
        AddU32(body, duration);
        AddU32(body, 0);
        AddU64(body, 1_784_807_104_918);
        body.Add(0x41);
        body.AddRange(new byte[8]);
        return body;
    }

    private static RecordingData Run(List<byte> body)
    {
        var data = new RecordingData();
        new StreamProcessor(new NullSink(), data, null).OnPacketReceived(Packet(body), 1000);
        return data;
    }

    /// <summary>보스 강화 4단계 — below the job band (19,993,701 &lt; 20,000,000) and indefinite
    /// (0xFFFFFFFF), i.e. dropped twice over before this change.</summary>
    [Fact]
    public void An_affix_apply_is_captured_as_a_difficulty_not_as_a_buff()
    {
        RecordingData data = Run(BuffApply(19993701, 4294967295L));

        Assert.Equal((TrialAffixGroup.BossBuff, 4), Assert.Single(data.Affixes));
        Assert.Equal(0, data.BuffSaves); // must NOT reach the buff overlay — it is a mob's buff
    }

    [Fact]
    public void The_other_affix_group_is_captured_too()
    {
        RecordingData data = Run(BuffApply(19806331, 4294967295L));

        Assert.Equal((TrialAffixGroup.BakronSkillUpgrade, 4), Assert.Single(data.Affixes));
    }

    /// <summary>An ordinary job buff (9-digit, in the 11x~19x band, finite duration) is untouched by the
    /// affix interception and still reaches the buff store.</summary>
    [Fact]
    public void An_ordinary_buff_still_reaches_the_buff_store()
    {
        RecordingData data = Run(BuffApply(119000001, 30_000));

        Assert.Empty(data.Affixes);
        Assert.Equal(1, data.BuffSaves);
    }

    /// <summary>The affixes ride the mob-spawn packet far more often than the buff-apply one — measured over a
    /// four-run capture, 360 broadcasts reached the client and only 8 were buff-applies, so reading applies
    /// alone leaves whole runs unlabelled.</summary>
    [Fact]
    public void An_affix_embedded_in_a_spawn_packet_is_recovered()
    {
        // 0x3641 spawn: [0x41][0x36][varint entity]...[affix code somewhere in the body]...
        var body = new List<byte> { 0x41, 0x36, 0x40, 0x11, 0x22, 0x33 };
        AddU32(body, 19993601);            // 보스 강화 3단계
        body.AddRange(new byte[] { 0x00, 0x40, 0x02, 0x44, 0x55, 0x66 });

        RecordingData data = Run(body);

        Assert.Contains((TrialAffixGroup.BossBuff, 3), data.Affixes);
    }

    [Fact]
    public void A_spawn_without_an_affix_reports_none()
    {
        var body = new List<byte> { 0x41, 0x36, 0x40, 0x11, 0x22, 0x33 };
        AddU32(body, 2300582);             // an ordinary mobCode, not an affix
        body.AddRange(new byte[] { 0x00, 0x40, 0x02, 0x44, 0x55, 0x66 });

        Assert.Empty(Run(body).Affixes);
    }

    /// <summary>0x6100 body: [u32 mapId][u8 phase][u64 startMs][u64 endMs].</summary>
    private static List<byte> PhaseWindow(int opcodeLow, int mapId, int phase, long startMs, long endMs)
    {
        var body = new List<byte> { (byte)opcodeLow, 0x61 };
        AddU32(body, mapId);
        body.Add((byte)phase);
        AddU64(body, startMs);
        AddU64(body, endMs);
        return body;
    }

    [Fact]
    public void A_phase_window_is_forwarded_with_its_duration()
    {
        const long start = 1_784_807_104_918;
        RecordingData data = Run(PhaseWindow(0x00, 600074, 2, start, start + 600_000));

        Assert.Equal((600074, 2, start, 600_000L), Assert.Single(data.Windows));
    }

    [Fact]
    public void Both_opcodes_of_the_phase_family_are_read()
    {
        const long start = 1_784_807_104_918;
        RecordingData data = Run(PhaseWindow(0x01, 600074, 2, start, start + 900_000));

        Assert.Equal(900_000L, Assert.Single(data.Windows).WindowMs);
    }

    /// <summary>Without the epoch sanity check a coincidental map-id match would manufacture a window out of
    /// arbitrary bytes, and that window would become a difficulty level.</summary>
    [Theory]
    [InlineData(0L, 600_000L)]                    // start not an epoch
    [InlineData(1_784_807_104_918L, -600_000L)]   // end before start
    public void An_implausible_window_is_rejected(long start, long delta)
    {
        RecordingData data = Run(PhaseWindow(0x00, 600074, 2, start, start + delta));

        Assert.Empty(data.Windows);
    }
}
