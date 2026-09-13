using System.Text;
using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// Spec for how the 아티팩트 점령 개수 reaches the meter. It is the only thing that says which of the two slots
/// in the 점령 현황 broadcast is OURS, so both of its sources have to work and neither may be spoofable.
///
/// <para><b>Two sources, because one is not enough.</b> 0x382A applies the abnormal when the character walks
/// into the abyss; the 0x3633 own-load snapshot carries the list of abnormals already on it. A meter started
/// while the player is ALREADY in the abyss never sees the apply — measured on the 2026-08-23 corpus, which has
/// zero 0x382A for these codes and both of them inside the own-load frame. Without the snapshot path that
/// session resolves no slot and shows no corridors at all.</para>
/// </summary>
public sealed class AbyssArtifactCountTests
{
    private sealed class RecordingData : ICaptureGameData
    {
        public readonly List<(int ZoneId, int Count)> Counts = [];

        public readonly List<(int Uid, string Nickname, bool IsExecutor)> Nicknames = [];

        public void SaveAbyssArtifactCount(int zoneId, int count) => Counts.Add((zoneId, count));

        public void SaveNickname(int uid, string nickname, bool isExecutor, int server, int jobByte) =>
            Nicknames.Add((uid, nickname, isExecutor));

        public Mob? GetMob(int code) => null;
        public int? GetMobId(int instanceId) => null;
        public void SaveMobId(int instanceId, int mobCode) { }
        public bool SkillExists(long code) => false;
        public long CurrentEpoch() => 0;
        public void SaveDamage(ParsedDamagePacket pdp, long epoch) { }
        public void StartBattle(int target) { }
        public void EndBattle(int target) { }
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

    private const int Uid = 12332;

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
        to.Add((byte)(value & 0xFF));
        to.Add((byte)((value >> 8) & 0xFF));
        to.Add((byte)((value >> 16) & 0xFF));
        to.Add((byte)((value >> 24) & 0xFF));
    }

    private static byte[] Frame(List<byte> body)
    {
        // realLength = lengthValue + lengthLength - 4 (StreamAssembler); a 1-byte length varint carrying
        // (bodyLength + 3) frames a body of exactly bodyLength.
        var frame = new List<byte>();
        WriteVarInt(frame, body.Count + 3);
        frame.AddRange(body);
        return frame.ToArray();
    }

    /// <summary>A real-layout own-load snapshot, optionally carrying abnormal codes in its trailing list.</summary>
    private static byte[] OwnLoad(string nickname = "콘팡", int server = 2003, params long[] abnormals)
    {
        var body = new List<byte> { 0x33, 0x36 };
        WriteVarInt(body, Uid);
        for (int i = 0; i < 5; i++)
        {
            body.Add(0x60);
        }

        byte[] name = Encoding.UTF8.GetBytes(nickname);
        WriteVarInt(body, name.Length);
        body.AddRange(name);
        body.Add((byte)(server & 0xFF));
        body.Add((byte)((server >> 8) & 0xFF));
        body.Add(0x10);

        foreach (long code in abnormals)
        {
            WriteU32(body, code);
        }

        return Frame(body);
    }

    /// <summary>A 0x382A apply, laid out as the real frames are: target varint, the two-byte 0x382A header, a
    /// slot varint, then the abnormal code.</summary>
    private static byte[] BuffApply(int target, long code)
    {
        var body = new List<byte> { 0x2A, 0x38 };
        WriteVarInt(body, target);
        body.Add(0x01);
        body.Add(0x11);
        WriteVarInt(body, 0x42);
        WriteU32(body, code);
        WriteU32(body, 0xFFFFFFFF); // the artifact abnormal has no fixed duration
        for (int i = 0; i < 12; i++)
        {
            body.Add(0);
        }

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

    /// <summary>The apply path: walking into the abyss puts both abnormals on the character 0.3 s after the
    /// zone loads (measured 2026-08-28), and both are read.</summary>
    [Fact]
    public void An_apply_on_the_own_character_reports_the_count()
    {
        RecordingData data = Feed(OwnLoad(), BuffApply(Uid, 12_000_262), BuffApply(Uid, 12_000_265));

        Assert.Contains((AbyssArtifactBuffCatalog.LowerZoneId, 2), data.Counts);
        Assert.Contains((AbyssArtifactBuffCatalog.MiddleZoneId, 2), data.Counts);
    }

    /// <summary>An apply on SOMEBODY ELSE is ignored. Enemy players standing beside you carry their own side's
    /// artifact abnormals, so without the target check the meter would read the enemy's occupation as ours and
    /// then show their corridors.</summary>
    [Fact]
    public void An_apply_on_another_player_is_ignored()
    {
        RecordingData data = Feed(OwnLoad(), BuffApply(Uid + 7, 12_000_261));

        Assert.Empty(data.Counts);
    }

    /// <summary>The snapshot path: an abnormal already on the character when the meter starts. This is the
    /// 2026-08-23 case — no apply in the whole capture, both codes only in this frame.</summary>
    [Fact]
    public void The_own_load_snapshot_reports_the_counts_it_carries()
    {
        RecordingData data = Feed(OwnLoad("콘팡", 2003, 12_000_261, 12_000_264));

        Assert.Equal(
            [(AbyssArtifactBuffCatalog.LowerZoneId, 1), (AbyssArtifactBuffCatalog.MiddleZoneId, 1)],
            data.Counts);
    }

    /// <summary>An own-load frame with no artifact abnormal reports nothing — never a zero. A side holding none
    /// simply gets no abnormal, and inventing a 0 here would let a character that has not been to the abyss
    /// claim its side captured nothing.</summary>
    [Fact]
    public void An_own_load_without_the_abnormal_reports_nothing()
    {
        Assert.Empty(Feed(OwnLoad()).Counts);
    }

    /// <summary>The snapshot scan rides on the 3중 게이트 that guards the executor pointer. A frame rejected as
    /// an identity — the 2026-07-30 garbage-frame shape — must not have its bytes read for a count either, or
    /// random outbound ciphertext could set which side the panel believes is ours.</summary>
    [Fact]
    public void A_rejected_identity_frame_is_not_scanned()
    {
        var body = new List<byte> { 0x33, 0x36 };
        WriteVarInt(body, Uid);
        for (int i = 0; i < 5; i++)
        {
            body.Add(0x60);
        }

        WriteVarInt(body, 1);
        body.Add((byte)'Q'); // one ASCII char — the shape the hardened gate exists to refuse
        WriteU32(body, 12_000_262);

        RecordingData data = Feed(Frame(body));

        Assert.Empty(data.Nicknames);
        Assert.Empty(data.Counts);
    }

    /// <summary>Two different counts for the SAME zone in one snapshot cannot both be true, so that zone is
    /// dropped rather than guessed at — while the other zone, which is unambiguous, still answers.</summary>
    [Fact]
    public void Two_conflicting_codes_for_one_zone_drop_only_that_zone()
    {
        RecordingData data = Feed(OwnLoad("콘팡", 2003, 12_000_261, 12_000_263, 12_000_265));

        Assert.Equal([(AbyssArtifactBuffCatalog.MiddleZoneId, 2)], data.Counts);
    }

    /// <summary>The same code twice is not a conflict — a list may repeat, and refusing that would throw away a
    /// perfectly good reading.</summary>
    [Fact]
    public void The_same_code_twice_is_not_a_conflict()
    {
        RecordingData data = Feed(OwnLoad("콘팡", 2003, 12_000_262, 12_000_262));

        Assert.Equal([(AbyssArtifactBuffCatalog.LowerZoneId, 2)], data.Counts);
    }
}
