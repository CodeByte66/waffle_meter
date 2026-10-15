using WaffleMeter.Capture;
using Xunit;

namespace WaffleMeter.Capture.Tests;

/// <summary>
/// 0x3802(시전 시 쿨타임 시작) 파서 회귀 스펙. 프레임은 전부 실제 캡처에서 뽑은 바이트 그대로다.
/// <para>액터 varint 다음 바이트는 오랫동안 레이아웃 주석에 <c>[00]</c> 리터럴로 적혀 있었지만 실은 플래그이고,
/// 0x04·0x08 비트가 켜지면 프레임 끝에 varint가 더 붙는다. 파서는 쿨타임을 "프레임의 마지막 varint"로
/// 읽으므로 그 프레임을 쿨타임 경로에 태우면 엉뚱한 숫자를 쿨로 적는다(코퍼스 5개 실측 오저장 165건). 그런
/// 프레임은 전부 충전형 스킬이고 총 쿨 필드는 0이다.</para>
/// <para>그 두 숫자는 <b>0x04 = 이 시전 뒤 남은 충전 횟수(없으면 0), 0x08 = 다음 1회가 차기까지 ms</b>다
/// (2026-10-16 검성 집중 막기 2회→0회 캡처). 그래서 그 프레임은 쿨타임 대신 충전 상태로 저장된다.</para>
/// </summary>
public class CooldownStartParsingTests
{
    private sealed class RecordingData : ICaptureGameData
    {
        public readonly List<(int SkillCode, long RemainingMs, long ArrivedAt, int ActorId, bool FromCast)> Saved = [];
        public readonly List<(int ActorId, int SkillCode, long ArrivedAt, bool StartsCooldown)> Casts = [];
        public readonly List<(int SkillCode, int Charges, long RechargeMs, long ArrivedAt, int ActorId)> Charges = [];

        public void SaveCooldown(int skillCode, long remainingMs, long arrivedAt, int actorId, bool fromCast = false)
            => Saved.Add((skillCode, remainingMs, arrivedAt, actorId, fromCast));

        public void SaveSkillCharges(int skillCode, int charges, long rechargeRemainingMs, long arrivedAt, int actorId)
            => Charges.Add((skillCode, charges, rechargeRemainingMs, arrivedAt, actorId));

        public void SaveSkillCast(int actorId, int skillCode, long arrivedAt, bool startsCooldown)
            => Casts.Add((actorId, skillCode, arrivedAt, startsCooldown));

        // 아래는 인터페이스가 기본 구현을 주지 않는 멤버들 — 이 스펙과 무관하다.
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

    private static byte[] Frame(string hex)
        => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
              .Select(b => Convert.ToByte(b, 16))
              .ToArray();

    // 20260831 궁성 세션. actor=239, flag=0x00, skill=14220050(축복의 활), 꼬리 마지막 varint = 81,100ms.
    private const string BlessedBowCast =
        "1C 02 38 EF 01 00 12 FB D8 00 52 00 EF 01 85 75 0B 43 BC 9D 01 01 CC F9 04";

    // 20260817. actor=7858, flag=0x0C, skill=11110010(집중 막기, 충전형 · NeedCoolTime=0).
    // 꼬리: A=10000 B=1 C=0 | 충전 1회 | 다음 1회까지 15,100ms. (마지막 varint 를 쿨로 읽으면 15,100 이 된다.)
    private const string FocusedBlockCharge =
        "1C 02 38 B2 3D 0C 7A 86 A9 00 B8 00 B2 3D 7C 26 7D 42 90 4E 01 00 01 FC 75";

    // 같은 스킬, flag=0x08 만 켜진 변종 — 충전 필드가 없다 = 0회, 다음 1회까지 10,500ms.
    private const string FocusedBlockChargeFlag8 =
        "1B 02 38 B2 3D 08 7A 86 A9 00 DF 00 B2 3D 55 80 0A C3 90 4E 01 00 84 52";

    // 2026-10-16 오너 캡처: 검성(actor 2641) 집중 막기(11110040) 가득 찬 2회에서 두 번 연달아 썼다.
    // 첫 시전 = 1회 남음, 다음 1회까지 14,250ms. 10.05초 뒤 두 번째 시전 = 0회, 다음 1회까지 4,100ms
    // (= 14,250 − 10,050 — 시전마다 새로 도는 타이머가 아니라 하나의 재충전 타이머다).
    private const string FocusedBlockFirstOfTwo =
        "1C 02 38 D1 14 0C 98 86 A9 00 0C 00 D1 14 1A D9 E3 42 90 4E 01 00 01 AA 6F";
    private const string FocusedBlockSecondOfTwo =
        "1B 02 38 D1 14 08 98 86 A9 00 3B 00 D1 14 1C CD E5 42 90 4E 01 00 84 20";

    [Fact]
    public void Plain_cast_frame_stores_the_cooldown_and_marks_it_as_a_cast()
    {
        var data = new RecordingData();
        new StreamProcessor(data: data).OnPacketReceived(Frame(BlessedBowCast), 5_000);

        (int SkillCode, long RemainingMs, long ArrivedAt, int ActorId, bool FromCast) saved = Assert.Single(data.Saved);
        Assert.Equal(14_220_050, saved.SkillCode);
        Assert.Equal(81_100, saved.RemainingMs);
        Assert.Equal(5_000, saved.ArrivedAt);
        Assert.Equal(239, saved.ActorId);
        Assert.True(saved.FromCast, "0x3802 값은 잠정값이므로 시전 출처로 표시되어야 한다");
    }

    [Theory]
    [InlineData(FocusedBlockCharge, 7858, 11_110_010, 1, 15_100)]
    [InlineData(FocusedBlockChargeFlag8, 7858, 11_110_010, 0, 10_500)]
    [InlineData(FocusedBlockFirstOfTwo, 2641, 11_110_040, 1, 14_250)]
    [InlineData(FocusedBlockSecondOfTwo, 2641, 11_110_040, 0, 4_100)]
    public void A_charge_cast_frame_stores_charges_and_never_a_cooldown(string hex, int actor, int skill, int charges, long rechargeMs)
    {
        var data = new RecordingData();
        new StreamProcessor(data: data).OnPacketReceived(Frame(hex), 5_000);

        // 쿨타임으로 저장되면 남은 충전이 있는데도 회색으로 칠해진다(종전 오저장 165건의 증상).
        Assert.Empty(data.Saved);

        (int SkillCode, int Charges, long RechargeMs, long ArrivedAt, int ActorId) saved = Assert.Single(data.Charges);
        Assert.Equal(skill, saved.SkillCode);
        Assert.Equal(charges, saved.Charges);
        Assert.Equal(rechargeMs, saved.RechargeMs);
        Assert.Equal(5_000, saved.ArrivedAt);
        Assert.Equal(actor, saved.ActorId); // 파티원 것도 오므로 본인 필터는 데이터 계층 몫이다
    }

    [Fact]
    public void A_plain_cast_frame_stores_no_charges()
    {
        var data = new RecordingData();
        new StreamProcessor(data: data).OnPacketReceived(Frame(BlessedBowCast), 5_000);

        Assert.Empty(data.Charges);
    }

    [Fact]
    public void A_charge_frame_that_does_not_consume_exactly_stores_nothing()
    {
        // 꼬리 끝에 모르는 바이트가 하나 더 붙었다 — 아는 형태가 아니면 부분 채택하지 않는다.
        var data = new RecordingData();
        new StreamProcessor(data: data).OnPacketReceived(Frame(FocusedBlockFirstOfTwo.Replace("1C 02 38", "1D 02 38") + " 00"), 5_000);

        Assert.Empty(data.Charges);
        Assert.Empty(data.Saved);
        Assert.Single(data.Casts); // 시전 자체는 여전히 진짜다 — 타임라인은 잃지 않는다
    }

    [Fact]
    public void Truncated_frame_is_swallowed()
    {
        var data = new RecordingData();
        // 액터 varint 까지만 있고 플래그 바이트가 없는 프레임 — 인덱스 밖을 읽어 터지면 안 된다.
        new StreamProcessor(data: data).OnPacketReceived(Frame("05 02 38 B2 3D"), 5_000);

        Assert.Empty(data.Saved);
    }

    /// <summary>스킬 타임라인의 원천. 같은 0x3802 프레임에서 쿨타임과 <b>따로</b> 갈라져 나온다.</summary>
    [Fact]
    public void A_plain_cast_frame_emits_both_a_cast_and_a_cooldown()
    {
        var data = new RecordingData();
        new StreamProcessor(data: data).OnPacketReceived(Frame(BlessedBowCast), 5_000);

        (int ActorId, int SkillCode, long ArrivedAt, bool StartsCooldown) cast = Assert.Single(data.Casts);
        Assert.Equal(239, cast.ActorId);
        Assert.Equal(14_220_050, cast.SkillCode);
        Assert.Equal(5_000, cast.ArrivedAt);
        Assert.True(cast.StartsCooldown); // 이 프레임은 쿨을 돌렸다 — 타임라인이 "확실히 나간 시전"으로 표시한다
        Assert.Single(data.Saved);        // 쿨타임 경로는 그대로 동작한다
    }

    /// <summary>
    /// 회귀 스펙의 핵심. <c>flag &amp; 0x0C</c> 가드는 "이 프레임에서 쿨타임을 읽으면 안 된다"는 뜻이지
    /// "시전이 없었다"가 아니다 — 그 프레임들은 전부 충전형 스킬의 <b>진짜 시전</b>이다. 시전 방출이 그
    /// 가드보다 앞에 있어야 타임라인이 그것들을 잃지 않는다.
    /// </summary>
    [Theory]
    [InlineData(FocusedBlockCharge)]
    [InlineData(FocusedBlockChargeFlag8)]
    public void A_charge_frame_emits_the_cast_even_though_the_cooldown_is_correctly_dropped(string frame)
    {
        var data = new RecordingData();
        new StreamProcessor(data: data).OnPacketReceived(Frame(frame), 7_000);

        (int ActorId, int SkillCode, long ArrivedAt, bool StartsCooldown) cast = Assert.Single(data.Casts);
        Assert.Equal(7858, cast.ActorId);
        Assert.Equal(11_110_010, cast.SkillCode);
        Assert.False(cast.StartsCooldown); // 충전형이라 돌릴 쿨이 없다 — 시전은 남되 표식은 안 붙는다
        Assert.Empty(data.Saved);          // 쿨타임은 여전히 저장되지 않는다 (165건 오저장 가드 유지)
    }

    // ---- 0x3822 충전 상태 통지 (본인 전용) ----

    // 2026-10-16 검성 집중 막기: 첫 시전 ~350ms 뒤 정정(1회, 13,800), 두 번째 시전 ~350ms 뒤 정정(0회, 3,650),
    // 1회가 다시 찬 순간(1회, 다음 1회까지 = 1회 재충전 시간 14,250).
    [Theory]
    [InlineData("0E 22 38 03 98 86 A9 00 01 E8 6B", 11_110_040, 1, 13_800)]
    [InlineData("0D 22 38 02 98 86 A9 00 C2 1C", 11_110_040, 0, 3_650)]
    [InlineData("0E 22 38 03 98 86 A9 00 01 AA 6F", 11_110_040, 1, 14_250)]
    // 2026-08-08 궁성 표적 화살(14090450): 1회가 찼다 / 마지막 1회가 차 가득 참 2회(잔여 필드 없음).
    [InlineData("0E 22 38 03 D2 00 D7 00 01 DC 3D", 14_090_450, 1, 7_900)]
    [InlineData("0C 22 38 01 D2 00 D7 00 02", 14_090_450, 2, 0)]
    // 2026-10-07 검성 집중 막기 다른 특화 변종(11110020) — 가득 참 2회.
    [InlineData("0C 22 38 01 84 86 A9 00 02", 11_110_020, 2, 0)]
    public void A_charge_notice_is_parsed_as_self_charges(string hex, int skill, int charges, long rechargeMs)
    {
        var data = new RecordingData();
        new StreamProcessor(data: data).OnPacketReceived(Frame(hex), 9_000);

        (int SkillCode, int Charges, long RechargeMs, long ArrivedAt, int ActorId) saved = Assert.Single(data.Charges);
        Assert.Equal(skill, saved.SkillCode);
        Assert.Equal(charges, saved.Charges);
        Assert.Equal(rechargeMs, saved.RechargeMs);
        Assert.Equal(9_000, saved.ArrivedAt);
        Assert.Equal(0, saved.ActorId); // actor 필드가 없다 = 본인 전용
    }

    [Theory]
    // 클라 아웃바운드 스트림에서 실제로 잡힌 모양(mask 0x95) — 모르는 비트가 켜졌다.
    [InlineData("0E 22 38 95 98 86 A9 00 01 E8 6B")]
    // mask 0 — 아무것도 말하지 않는 통지는 없다.
    [InlineData("0B 22 38 00 98 86 A9 00")]
    // 프레임이 남는다(정확히 소진하지 못함).
    [InlineData("0F 22 38 03 98 86 A9 00 01 E8 6B 00")]
    // 잔여 varint 가 잘렸다.
    [InlineData("0D 22 38 03 98 86 A9 00 01 E8")]
    // 직업 스킬 밴드 밖(공용 스킬 4900202).
    [InlineData("0C 22 38 01 6A C5 4A 00 03")]
    public void A_charge_notice_that_is_not_the_known_shape_is_dropped(string hex)
    {
        var data = new RecordingData();
        new StreamProcessor(data: data).OnPacketReceived(Frame(hex), 9_000);

        Assert.Empty(data.Charges);
    }
}
