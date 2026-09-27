using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 인게임에서 서로 중복 적용되지 않는 버프 쌍은 오버레이에도 하나만 떠야 한다. 서버 동작이 쌍마다 달라
/// (노련한 반격↔격앙은 서버가 둘 다 보내고, 대지의 축복↔질풍의 권능은 이미 걸린 축복을 제거해 주지 않는다)
/// 표시 계층에서 지는 쪽을 감춘다. 승자는 어노멀 레벨이 높은 쪽이고, 규칙이 명문화된 쌍은 고정 승자를 쓴다.
/// </summary>
public sealed class ExclusiveBuffPairTests
{
    private const int Me = 900;

    // 런타임 코드 → base: 노련한 반격 11780000 / 격앙 12780000 / 보호의 빛 17410000 /
    // 불패의 진언 18190000 / 대지의 축복 17400058 / 질풍의 권능 18250000
    private const int Counter = 117800071;   // 검성 노련한 반격
    private const int Rage = 127800011;      // 수호성 격앙
    private const int Protect = 174100511;   // 치유성 보호의 빛
    private const int Invincible = 181900511; // 호법성 불패의 진언
    private const int Blessing = 174000571;  // 치유성 대지의 축복
    private const int Gale = 182500511;      // 호법성 질풍의 권능

    private static DataManager Self(long t0)
    {
        var dm = new DataManager { Clock = () => t0 };
        dm.SaveNickname(Me, "본인", isExecutor: true, server: 3, jobByte: 0);
        return dm;
    }

    private static int[] Codes(DataManager dm, long at) =>
        dm.ActiveOwnerBuffs(at).Select(b => b.Code).OrderBy(c => c).ToArray();

    [Fact]
    public void Higher_level_wins_when_both_are_up()
    {
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, Counter, t0, t0 + 30_000, 30_000, Me, level: 12);
        dm.SaveUseBuff(Me, Rage, t0, t0 + 30_000, 30_000, Me, level: 25);

        Assert.Equal(new[] { 12780000 }, Codes(dm, t0 + 1_000)); // 격앙(25) 승
    }

    [Fact]
    public void The_other_direction_wins_too()
    {
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, Counter, t0, t0 + 30_000, 30_000, Me, level: 25);
        dm.SaveUseBuff(Me, Rage, t0, t0 + 30_000, 30_000, Me, level: 12);

        Assert.Equal(new[] { 11780000 }, Codes(dm, t0 + 1_000)); // 노련한 반격(25) 승
    }

    [Fact]
    public void A_tie_goes_to_the_documented_winner()
    {
        // 인게임 설명문: "스킬 레벨이 동일할 경우 불패의 진언 효과를 받습니다."
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, Protect, t0, t0 + 40_000, 40_000, Me, level: 25);
        dm.SaveUseBuff(Me, Invincible, t0, t0 + 40_000, 40_000, Me, level: 25);

        Assert.Equal(new[] { 18190000 }, Codes(dm, t0 + 1_000));
    }

    [Fact]
    public void A_lapsed_toggle_yields_to_the_one_actually_up()
    {
        // 보호의 빛·불패의 진언은 1초마다 오는 펄스(1,250ms / 2,400ms)라 3초 유예가 붙는다. 레벨 높은 보호의
        // 빛이 꺼지면 서버가 불패의 진언을 대신 건다 — 그 유예 동안 레벨 비교가 불패를 가리면 안 된다.
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, Protect, t0, t0 + 1_250, 1_250, 3, level: 30);
        dm.SaveUseBuff(Me, Invincible, t0 + 2_000, t0 + 4_400, 2_400, 4, level: 20);

        Assert.Equal(new[] { 18190000 }, Codes(dm, t0 + 2_500)); // 보호의 빛은 유예로만 남아 있다
    }

    [Fact]
    public void Two_live_toggles_still_go_by_level()
    {
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, Protect, t0, t0 + 1_250, 1_250, 3, level: 30);
        dm.SaveUseBuff(Me, Invincible, t0, t0 + 2_400, 2_400, 4, level: 20);

        Assert.Equal(new[] { 17410000 }, Codes(dm, t0 + 500)); // 둘 다 펄스 안 — 레벨 높은 보호의 빛
    }

    [Fact]
    public void Gale_always_beats_the_blessing()
    {
        // 서버가 질풍 활성 중에는 축복 적용을 막지만 이미 걸린 축복은 제거하지 않는다(최대 ~20초 잔존).
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, Blessing, t0, t0 + 20_000, 20_000, Me, level: 30); // 레벨이 높아도
        dm.SaveUseBuff(Me, Gale, t0, t0 + 20_000, 20_000, Me, level: 5);

        Assert.Equal(new[] { 18250000 }, Codes(dm, t0 + 1_000));
    }

    [Fact]
    public void Only_one_of_the_pair_up_hides_nothing()
    {
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, Counter, t0, t0 + 30_000, 30_000, Me, level: 12);

        Assert.Equal(new[] { 11780000 }, Codes(dm, t0 + 1_000));
    }

    [Fact]
    public void Unknown_levels_fall_back_to_the_later_application()
    {
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, Counter, t0, t0 + 10_000, 10_000, Me, level: 0);
        dm.SaveUseBuff(Me, Rage, t0, t0 + 30_000, 30_000, Me, level: 0); // 더 늦게까지 유지 = 나중 적용

        Assert.Equal(new[] { 12780000 }, Codes(dm, t0 + 1_000));
    }

    [Fact]
    public void Unrelated_buffs_are_never_suppressed()
    {
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, Counter, t0, t0 + 30_000, 30_000, Me, level: 12);
        dm.SaveUseBuff(Me, 191300401, t0, t0 + 6_000, 6_000, Me, level: 10); // 권성 폭주

        Assert.Equal(new[] { 11780000, 19130000 }, Codes(dm, t0 + 1_000));
    }
}
