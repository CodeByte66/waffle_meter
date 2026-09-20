using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 조기 해제(0x382C)가 <b>집계 저장소</b>의 열린 구간을 끊는다는 계약.
/// <para>종전에는 슬롯이 오버레이 사전에만 실리고 <see cref="UseBuff"/> 레코드에는 안 실려서, 오버레이는
/// 즉시 지워지는데 상세창 가동률은 선언 duration 이 다 흐를 때까지 계속 셌다. 그 값은 선형으로 nDPS/rDPS 와
/// 업로드 <c>OperatingRate</c> 까지 따라간다 — 단일 원천이라 저장소 하나를 고치면 셋이 같이 맞는다.</para>
/// </summary>
public sealed class BuffEarlyReleaseTruncationTests
{
    private const int Me = 1;
    private const int Ally = 2;
    private const int Boss = 90_001;
    private const int BuffA = 110_200_050;

    private const long T0 = 1_000_000;

    private static UseBuff Open(int slot, long start = T0, long duration = 60_000, int actor = Me) =>
        new(BuffA, start, start + duration, duration, actor, Level: 10, Slot: slot);

    private static long Covered(UseBuffRepository repo, int uid) =>
        repo.FindOverlapping(uid, T0, T0 + 60_000).Sum(b => b.BuffEnd - b.BuffStart);

    [Fact]
    public void An_early_release_cuts_the_interval_at_the_moment_it_arrived()
    {
        var repo = new UseBuffRepository();
        repo.Save(Me, Open(slot: 65));

        repo.TruncateOpenSlots(Me, [65], T0 + 10_000);

        // 선언 60초짜리가 10초에 끊겼다 — 가동률은 100% 가 아니라 약 16.7% 여야 한다.
        Assert.Equal(10_000, Covered(repo, Me));
    }

    /// <summary>
    /// 🔑 <b>이 테스트가 게이트 회귀를 잡는다.</b> executor 게이트가 다시 메서드 첫 줄로 올라가면 파티원과 보스의
    /// 조기 해제가 집계에 영원히 반영되지 않는다 — 실측상 과다 표시 232.7시간 중 <b>본인은 10%</b> 뿐이고
    /// 나머지가 서포터 rDPS 입력과 보스 디버프 표다. 그러면 상세창 안에서 「내 버프」 행만 맞고 보스 디버프 행은
    /// 틀린 채 남아, 한 화면에 두 정확도가 섞인다.
    /// </summary>
    [Theory]
    [InlineData(Ally)]
    [InlineData(Boss)]
    public void A_non_executor_entity_is_truncated_too(int uid)
    {
        var repo = new UseBuffRepository();
        repo.Save(uid, Open(slot: 65, actor: Me));

        repo.TruncateOpenSlots(uid, [65], T0 + 10_000);

        Assert.Equal(10_000, Covered(repo, uid));
    }

    [Fact]
    public void A_slot_we_do_not_know_is_left_to_expire_normally()
    {
        // slot 0 = 모름(옛 경로/합성 엔트리). 슬롯 매칭이 불가능하므로 끊지 않고 만료에 맡긴다 — fail-open.
        var repo = new UseBuffRepository();
        repo.Save(Me, Open(slot: 0));

        repo.TruncateOpenSlots(Me, [0], T0 + 10_000);

        Assert.Equal(60_000, Covered(repo, Me));
    }

    [Fact]
    public void A_slot_the_release_does_not_name_is_untouched()
    {
        var repo = new UseBuffRepository();
        repo.Save(Me, Open(slot: 65));

        repo.TruncateOpenSlots(Me, [99], T0 + 10_000);

        Assert.Equal(60_000, Covered(repo, Me));
    }

    /// <summary>이미 끝난 구간을 되살리거나 늘리지 않는다 — <c>BuffEnd &gt; at</c> 조건이 그 일을 한다.</summary>
    [Fact]
    public void A_closed_interval_is_never_extended()
    {
        var repo = new UseBuffRepository();
        repo.Save(Me, Open(slot: 65, duration: 5_000)); // T0 ~ T0+5,000

        repo.TruncateOpenSlots(Me, [65], T0 + 10_000); // 이미 끝난 뒤에 도착

        Assert.Equal(5_000, Covered(repo, Me));
    }

    /// <summary>
    /// 재적용 체인: 같은 슬롯에 방금 새로 걸린 구간을 0 길이로 깎지 않는다 — <c>BuffStart &lt; at</c> 조건이
    /// 그 일을 한다. 이 조건이 빠지면 해제가 자기 직후의 재적용을 먹어 가동률이 오히려 0 에 가까워진다.
    /// </summary>
    [Fact]
    public void A_reapplication_on_the_same_slot_is_not_swallowed()
    {
        var repo = new UseBuffRepository();
        repo.Save(Me, Open(slot: 65));                          // T0 ~ T0+60,000
        repo.Save(Me, Open(slot: 65, start: T0 + 10_000));      // 재적용

        repo.TruncateOpenSlots(Me, [65], T0 + 10_000);

        // 첫 구간만 10초에서 끊기고, 같은 ms 에 시작한 재적용은 온전하다.
        List<UseBuff> all = repo.FindOverlapping(Me, T0, T0 + 120_000);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, b => b.BuffStart == T0 && b.BuffEnd == T0 + 10_000);
        Assert.Contains(all, b => b.BuffStart == T0 + 10_000 && b.BuffEnd == T0 + 70_000);
    }

    /// <summary>서버가 선언한 길이는 그 자체로 기록이라 남긴다. 가동률 경로는 BuffStart/BuffEnd 만 읽는다.</summary>
    [Fact]
    public void The_declared_duration_is_kept_as_a_record()
    {
        var repo = new UseBuffRepository();
        repo.Save(Me, Open(slot: 65));

        repo.TruncateOpenSlots(Me, [65], T0 + 10_000);

        UseBuff b = Assert.Single(repo.FindOverlapping(Me, T0, T0 + 60_000));
        Assert.Equal(60_000, b.Duration);       // 선언값은 그대로
        Assert.Equal(T0 + 10_000, b.BuffEnd);   // 실제 끝만 바뀐다
    }
}
