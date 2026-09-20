using System.Text.Json;
using WaffleMeter.Stats;
using Xunit;

namespace WaffleMeter.Stats.Tests;

/// <summary>
/// 참가자 전투력을 못 읽었을 때 <b>전투를 버리지 않고 그 참가자만 null 로 보낸다</b>는 계약.
/// <para>서버는 <c>battle_participants.power</c> 가 원래부터 nullable 이고, 티어 엔진의 파티 편차가 NULL 을
/// 설계상 무시하며 <c>power &gt;= 400000</c> 게이트가 NULL 을 자동 배제한다. 그래서 null 하나면 그 참가자만
/// 집계에서 빠지고 전투는 산다(2026-09-18 웹 배포 <c>7a7b23e</c> 로 스키마가 열렸다).</para>
/// </summary>
public class ParticipantPowerNullTests
{
    private static StatsParticipantPayload Participant(int? power) => new(
        IdentityHash: "a".PadRight(64, 'a'),
        IsUploader: false,
        Job: "검성",
        Power: power,
        Result: new StatsResultPayload(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        Skills: [],
        Buffs: []);

    /// <summary>
    /// 🔑 이 테스트가 이 변경의 **핵심 함정**을 잡는다.
    /// <para><see cref="StatsJson"/> 은 <c>DefaultIgnoreCondition = WhenWritingNull</c> 이다. 아무 조치 없이
    /// <c>int?</c> 로만 바꾸면 null 인 순간 <b>키가 통째로 사라진다.</b> 서버 스키마는 nullable 이지
    /// <b>optional 이 아니므로</b> 키가 없으면 <c>invalid_schema</c> 로 거부된다 — 즉 "전투를 살리려던 변경"이
    /// 전투를 400 으로 죽이는 결과가 된다. 그래서 이 필드만 <c>JsonIgnoreCondition.Never</c> 로 명시 출력한다.</para>
    /// </summary>
    [Fact]
    public void An_unknown_power_is_written_as_an_explicit_null_not_omitted()
    {
        string json = StatsJson.Serialize(Participant(null));

        Assert.Contains("\"power\":null", json);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("power", out JsonElement power), "power 키가 생략됐다");
        Assert.Equal(JsonValueKind.Null, power.ValueKind);
    }

    [Fact]
    public void A_known_power_still_rides_as_a_number()
    {
        string json = StatsJson.Serialize(Participant(732_500));

        Assert.Contains("\"power\":732500", json);
        Assert.DoesNotContain("\"power\":null", json);
    }

    /// <summary>0 은 값이고 null 은 "모른다"다. 둘을 같은 것으로 쓰면 서버가 "전투력 0 인 캐릭터"로 집계한다 —
    /// 실제로 웹 전투상세가 종전에 「전투력 0」 으로 찍던 자리가 여기였다.</summary>
    [Fact]
    public void Zero_and_unknown_are_not_the_same_value()
    {
        Assert.Contains("\"power\":0", StatsJson.Serialize(Participant(0)));
        Assert.Contains("\"power\":null", StatsJson.Serialize(Participant(null)));
    }

    /// <summary>왕복해도 null 이 0 으로 접히지 않는다.</summary>
    [Fact]
    public void Null_survives_a_round_trip()
    {
        StatsParticipantPayload back = StatsJson.Deserialize<StatsParticipantPayload>(
            StatsJson.Serialize(Participant(null)));

        Assert.Null(back.Power);
    }
}
