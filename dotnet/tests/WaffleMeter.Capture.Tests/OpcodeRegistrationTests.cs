using System.Reflection;
using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// opcode 상수를 선언하고 <c>switch</c> 케이스까지 썼는데 <b>등록</b>을 빠뜨리면 그 파서는
/// <b>한 번도 호출되지 않는다</b>. 그런데 빌드도 통과하고, 그 경로를 안 타는 테스트도 전부 통과한다.
///
/// <para>🔑 디스패치 직전에 이 게이트가 있기 때문이다:
/// <code>
/// OpcodeNames.TryGetValue(opcodeKey, out string? name);
/// _sink.Dispatch(opcodeKey, name, extraFlag, packet.Length);
/// if (name is null) { _sink.UnknownOpcode(...); return; }   // ← switch 에 도달하지 못한다
/// </code>
/// 즉 <c>OpcodeNames</c> 등록이 사실상의 <b>활성화 스위치</b>다. 2026-09-19 에 0x971F·0x9622 를 추가하면서
/// 정확히 이걸 밟았고(상수·케이스만 넣고 등록 누락), 유닛 테스트로는 안 잡혀 코퍼스 프레임을 넣는
/// end-to-end 테스트에서야 드러났다.</para>
///
/// <para>같은 모양의 함정이 통계웹에도 있었다(Dockerfile 이 파일을 화이트리스트로 COPY 해서, 새 스크립트를
/// 넣고 목록을 안 고치면 빌드·CI 는 통과하고 컨테이너 실행에서만 죽는다). 공통점은 <b>"선언은 했는데
/// 등록을 안 해서 도달 불가가 되고, 그 경로를 안 타는 테스트는 전부 통과한다"</b>는 것이다.
/// 목록이 뒤처지지 못하게 막는 것이 이 테스트의 일이다.</para>
/// </summary>
public sealed class OpcodeRegistrationTests
{
    /// <summary>
    /// opcode 가 아닌 <c>*Key*</c> 상수는 여기 적고 <b>왜</b> 인지 남긴다. 비워 두는 것이 기본이다 —
    /// 새 이름을 넣기 전에 "정말 opcode 가 아닌가"를 먼저 의심하라.
    /// </summary>
    private static readonly HashSet<string> NotAnOpcode = new();

    /// <summary>
    /// <b>등록하지 않는 것이 의도인</b> opcode. 여기 넣는 순간 위 가드가 그 상수를 안 보게 되므로, 넣을 때는
    /// 반드시 (a) 왜 등록하면 안 되는지와 (b) 그 대신 무엇이 도달성을 지키는지를 같이 적는다.
    /// <para><c>ServerClockKey</c>(0x3600): <c>OpcodeNames</c> 는 이름표가 아니라
    /// <c>LooksLikeGamePacket</c> 의 <b>게임 스트림 판정 기준</b>이고, 그 판정이 노이즈 가드 면제와 VPN 중복
    /// 억제 하트비트를 굴린다. 0x3600 은 게임 프레임의 8~28% 라 등록하는 순간 그 휴리스틱이 조용히 바뀐다.
    /// 그래서 디스패치 직전에 가로채고 <c>return</c> 한다 — 도달성은 아래
    /// <see cref="An_intercepted_opcode_is_handled_before_the_dispatch_gate"/> 가 지킨다.</para>
    /// </summary>
    private static readonly HashSet<string> InterceptedBeforeDispatch = new(StringComparer.Ordinal)
    {
        "ServerClockKey",
    };

    private static Dictionary<int, string> OpcodeNames()
    {
        FieldInfo? f = typeof(StreamProcessor)
            .GetField("OpcodeNames", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f); // 이름이 바뀌었다면 이 테스트부터 고쳐라 — 가드가 조용히 죽으면 안 된다
        var map = f!.GetValue(null) as Dictionary<int, string>;
        Assert.NotNull(map);
        return map!;
    }

    private static IEnumerable<(string Name, int Value)> OpcodeConstants() =>
        typeof(StreamProcessor)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(int))
            .Where(f => f.Name.Contains("Key", StringComparison.Ordinal))
            .Where(f => !NotAnOpcode.Contains(f.Name))
            .Select(f => (f.Name, (int)f.GetRawConstantValue()!));

    /// <summary>선언된 opcode 상수는 전부 <c>OpcodeNames</c> 에 있어야 한다 — 없으면 그 파서는 死코드다.</summary>
    [Fact]
    public void Every_opcode_constant_is_registered_in_OpcodeNames()
    {
        Dictionary<int, string> names = OpcodeNames();

        string[] missing = OpcodeConstants()
            .Where(c => !InterceptedBeforeDispatch.Contains(c.Name))
            .Where(c => !names.ContainsKey(c.Value))
            .Select(c => $"{c.Name} (0x{c.Value:X4})")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "OpcodeNames 에 등록되지 않은 opcode 상수가 있다 — 이 opcode 는 디스패치 게이트에 걸려 "
            + "파서가 한 번도 호출되지 않는다:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>상수 하나는 opcode 하나다. 두 상수가 같은 값을 들고 있으면 한쪽 파서가 조용히 덮인다.</summary>
    [Fact]
    public void No_two_opcode_constants_share_a_value()
    {
        string[] dupes = OpcodeConstants()
            .GroupBy(c => c.Value)
            .Where(g => g.Select(c => c.Name).Distinct().Count() > 1)
            .Select(g => $"0x{g.Key:X4} = {string.Join(", ", g.Select(c => c.Name).Distinct().Order(StringComparer.Ordinal))}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.True(dupes.Length == 0, "같은 값을 가진 opcode 상수가 있다:\n  " + string.Join("\n  ", dupes));
    }

    /// <summary>가드가 실제로 무언가를 보고 있는지 — 상수가 0개면 위 두 테스트는 항상 통과한다(공허한 그린).</summary>
    [Fact]
    public void The_guard_actually_sees_the_opcode_constants()
    {
        Assert.True(OpcodeConstants().Count() >= 20, "opcode 상수를 못 찾았다 — 리플렉션 조건이 낡았다");
    }

    /// <summary>면제 목록에 이름이 오타로 남아 가드가 조용히 헐거워지는 것을 막는다.</summary>
    [Fact]
    public void Every_exempted_name_is_a_real_opcode_constant()
    {
        string[] known = OpcodeConstants().Select(c => c.Name).ToArray();
        string[] stale = InterceptedBeforeDispatch.Where(n => !known.Contains(n)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            stale.Length == 0,
            "면제 목록에 존재하지 않는 상수 이름이 있다 — 상수를 지웠거나 이름이 바뀌었다:\n  "
            + string.Join("\n  ", stale));
    }

    private sealed class CountingSink : IStreamProcessorSink
    {
        public readonly List<int> Dispatched = [];
        public readonly List<int> Unknown = [];

        public void Dispatch(int opcode, string? opcodeName, bool extraFlag, int len) => Dispatched.Add(opcode);
        public void UnknownOpcode(int opcode, bool extraFlag, int len) => Unknown.Add(opcode);
        public void CompressedPacket(int len, bool extraFlag) { }
        public void ParserError(string stage, string reason) { }
        public void Damage(string kind, ParsedDamagePacket packet, bool saved, string? reason, int? mobCode) { }
        public void Meta(string type, params (string Key, object? Value)[] fields) { }
        public void Battle(int target, int toggle, int? mobCode, string? mobName, bool accepted, string? reason) { }
    }

    /// <summary>
    /// 면제된 opcode 가 정말로 <b>가로채기</b>로 처리되는지 — 등록을 안 했으니 그냥 두면 unknown 으로 버려진다
    /// (면제가 "도달 불가"를 정당화하는 구멍이 되면 이 파일 전체가 무의미해진다).
    /// <para>동시에 이것이 0x3600 을 등록하지 않는 두 번째 이유를 지킨다 — 가로채기는 dispatch·unknown
    /// 브레드크럼을 <b>둘 다</b> 남기지 않으므로, 패킷 로그가 20Hz × 두 줄만큼 커지는 게 아니라 오히려 줄어든다.</para>
    /// </summary>
    [Fact]
    public void An_intercepted_opcode_is_handled_before_the_dispatch_gate()
    {
        // 0x3600 MapFrame_NT 실측 레이아웃: [len varint][00 36][Int64 LE 서버시계] = 11바이트.
        byte[] frame = [0x0E, 0x00, 0x36, 0x69, 0xED, 0x53, 0xBE, 0xA0, 0x01, 0x00, 0x00];

        var sink = new CountingSink();
        new StreamProcessor(sink, NullCaptureGameData.Instance).OnPacketReceived(frame, 0);

        Assert.Empty(sink.Dispatched);
        Assert.Empty(sink.Unknown);
    }
}
