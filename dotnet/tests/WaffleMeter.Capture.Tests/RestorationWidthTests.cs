using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// 흡혈(<c>_restoration_hp</c>, 타격 피해의 20%를 시전자가 회복)이 붙은 타격 프레임에서, 회복량은
/// <b>피해 숫자 앞에</b> varint 로 끼어 있고 <b>폭이 값에 따라 1~3바이트로 변한다.</b>
///
/// <para>종전 파서는 각도 바이트를 <c>region[2]</c> 에, 뒤따르는 varint 들의 밀림을 <c>+2</c> 에 <b>둘 다
/// 고정</b>해 읽었다. 그건 폭이 2일 때만 맞는다.</para>
///
/// <para><b>실측(6세션 흡혈 프레임 120건)</b>: 폭 1이 33건 · 폭 2가 70건. 흡혈은 "타격 피해의 20%"라는
/// 독립 불변식이 있어 모델을 가를 수 있다 —</para>
/// <code>
///            회복/피해 가 0.15~0.25 에 드는 비율
///   폭 1 :   밀림 0 또는 1 → 33/33 (100%)      밀림 2(종전) →  0/33 (0%)   ← 깨진다
///   폭 2 :   밀림 1 또는 2 → 68/70  (97%)      밀림 0        → 18/70 (26%)
/// </code>
/// <para>즉 종전 코드는 <b>폭 2에서는 맞고 폭 1에서 깨진다.</b> 그래서 밀림을 폭 그대로 쓴다 — 이미 맞게
/// 읽히던 70건이 한 바이트도 안 움직인다(코퍼스는 '폭−1' 모델도 똑같이 만족시켜 두 모델이 갈리지 않으므로,
/// 갈리지 않을 때는 현행 동작을 보존하는 쪽을 골랐다. 폭 3 표본이 생기면 그때 갈린다).</para>
///
/// <para>🔴 <b>딜 오차보다 각도 오독이 중요하다.</b> 각도는 명중/저항 모델의 버킷 키이고 상세창에 뜬다.
/// 고정 읽기는 6·13·5 같은 값을 내지만 폭을 반영하면 120건 전부 {0,1,2} 로 떨어진다.</para>
/// </summary>
public sealed class RestorationWidthTests
{
    private const int Actor = 7777;   // actor == target 으로 두면 파서가 판정까지 끝낸 뒤 조기 반환한다
    private const int SkillCode = 11010000;
    private const int SwitchSix = 6;  // region 11바이트
    private const int RegionSize = 11;

    private sealed class RecordingSink : IStreamProcessorSink
    {
        public readonly List<ParsedDamagePacket> Packets = [];

        public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode) =>
            Packets.Add(packet);

        public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) { }
        public void UnknownOpcode(int opcode, bool extraFlag, int len) { }
        public void CompressedPacket(int len, bool extraFlag) { }
        public void ParserError(string stage, string reason) { }
        public void Meta(string type, params (string Key, object? Value)[] fields) { }
        public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
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

    private static int VarIntWidth(long value)
    {
        var tmp = new List<byte>();
        WriteVarInt(tmp, value);
        return tmp.Count;
    }

    /// <summary>실측 레이아웃 그대로의 0x3804 프레임을 만든다.
    /// region = <c>[플래그][_restoration_hp varint][각도][채움…]</c> 이고, 회복량이 1바이트를 넘으면 그만큼
    /// 뒤가 밀리므로 region 뒤에 <b>폭만큼</b> 채움 바이트가 더 붙는다.</summary>
    private static byte[] DamageFrame(long restorationHp, int angle, long damage, bool restoration = true)
    {
        var body = new List<byte> { 0x04, 0x38 };
        WriteVarInt(body, Actor);        // target
        WriteVarInt(body, SwitchSix);    // switch
        WriteVarInt(body, 0);            // flag
        WriteVarInt(body, Actor);        // actor (== target)
        body.Add((byte)(SkillCode & 0xFF));
        body.Add((byte)((SkillCode >> 8) & 0xFF));
        body.Add((byte)((SkillCode >> 16) & 0xFF));
        body.Add((byte)((SkillCode >> 24) & 0xFF));
        body.Add(0);                     // 스킬코드는 u32 뒤 1바이트를 더 소비한다
        WriteVarInt(body, 1);            // type

        var region = new List<byte> { (byte)(restoration ? 0x20 : 0x00) };
        WriteVarInt(region, restorationHp);
        int width = region.Count - 1;
        region.Add((byte)angle);
        while (region.Count < RegionSize)
        {
            region.Add(0);
        }

        body.AddRange(region.GetRange(0, RegionSize));
        if (restoration)
        {
            for (int i = 0; i < width; i++)
            {
                body.Add(0); // 회복량이 밀어낸 만큼
            }
        }

        WriteVarInt(body, 10_000);       // unknown(power)
        WriteVarInt(body, damage);
        // 피해 varint 직후에 프레임이 끝나면 파서가 sink 에 닿기 전에 반환한다(경계 검사). 실제 프레임에는
        // 다단히트 꼬리가 붙으므로 여기서도 붙인다 — count 0 = 단일 타격.
        body.Add(0);
        body.Add(0);

        var frame = new List<byte>();
        WriteVarInt(frame, body.Count + 3);
        frame.AddRange(body);
        return frame.ToArray();
    }

    private static ParsedDamagePacket Parse(byte[] frame)
    {
        var sink = new RecordingSink();
        new StreamProcessor(sink, NullCaptureGameData.Instance).OnPacketReceived(frame, 0);
        return Assert.Single(sink.Packets);
    }

    [Fact]
    public void A_one_byte_restoration_value_no_longer_shifts_the_damage_varint()
    {
        // 실측 폭1 프레임 33건이 전부 이 모양이다 — 종전 읽기로는 회복/피해 비가 0.15~0.25 에 0건 들어갔다.
        Assert.Equal(1, VarIntWidth(96));
        ParsedDamagePacket p = Parse(DamageFrame(restorationHp: 96, angle: 2, damage: 480));

        Assert.Equal(480, p.Damage);
        Assert.Equal(2, p.Position);   // 전방
    }

    [Fact]
    public void A_two_byte_restoration_value_still_reads_exactly_as_before()
    {
        // 폭2 는 종전 코드가 이미 맞게 읽던 경우다. 한 바이트도 움직이면 안 된다.
        Assert.Equal(2, VarIntWidth(1427));
        ParsedDamagePacket p = Parse(DamageFrame(restorationHp: 1427, angle: 1, damage: 7135));

        Assert.Equal(7135, p.Damage);
        Assert.Equal(1, p.Position);   // 후방
    }

    [Fact]
    public void A_three_byte_restoration_value_is_handled_too()
    {
        // 코퍼스에 아직 표본이 없지만 varint 폭은 값의 함수다 — 고정 폭 가정이 남아 있으면 여기서 깨진다.
        Assert.Equal(3, VarIntWidth(200_000));
        ParsedDamagePacket p = Parse(DamageFrame(restorationHp: 200_000, angle: 2, damage: 1_000_000));

        Assert.Equal(1_000_000, p.Damage);
        Assert.Equal(2, p.Position);
    }

    [Fact]
    public void A_hit_without_restoration_is_untouched()
    {
        // 흡혈이 아닌 프레임은 이 필드가 1바이트라 종전 오프셋이 이미 맞다. 회귀 방지용 대조군.
        ParsedDamagePacket p = Parse(DamageFrame(restorationHp: 0, angle: 1, damage: 4242, restoration: false));

        Assert.Equal(4242, p.Damage);
        Assert.Equal(1, p.Position);
    }

    [Fact]
    public void The_angle_byte_is_never_read_out_of_the_restoration_varint()
    {
        // 폭2 회복량의 두 번째 바이트가 우연히 1이나 2면 종전 읽기는 그걸 각도로 보고했다.
        // 0x93 0x0B (=1427) 의 둘째 바이트는 0x0B 이므로 여기서는 각도 0 이 나와야 한다(값이 1/2 가 아니다).
        ParsedDamagePacket p = Parse(DamageFrame(restorationHp: 1427, angle: 0, damage: 7135));

        Assert.Equal(0, p.Position);
    }
}
