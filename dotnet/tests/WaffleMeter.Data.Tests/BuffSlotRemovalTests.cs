using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 0x382C 버프 제거 브로드캐스트 반영. 종전에는 제거 신호가 없다고 보고 duration이 다 흐를 때까지 슬롯을
/// 남겨 뒀는데, 서버는 예상 만료보다 일찍 끊는 경우가 절반을 넘는다. 제거는 <b>슬롯</b>으로 지목되므로 같은
/// 버프 코드가 겹쳐 걸려도 엉뚱한 인스턴스를 지울 수 없다(코드만 주는 0x921A는 그 구분이 원리적으로 불가).
/// </summary>
public sealed class BuffSlotRemovalTests
{
    private const int Me = 500;
    private const int Ally = 501;
    private const int BuffA = 117800071; // 검성 노련한 반격 → base 11780000
    private const int BaseA = 11780000;
    private const int BuffB = 191300401; // 권성 폭주 → base 19130000
    private const int BaseB = 19130000;

    private static DataManager Self(long t0)
    {
        var dm = new DataManager { Clock = () => t0 };
        dm.SaveNickname(Me, "본인", isExecutor: true, server: 3, jobByte: 0);
        return dm;
    }

    private static int[] Codes(DataManager dm, long at) =>
        dm.ActiveOwnerBuffs(at).Select(b => b.Code).OrderBy(c => c).ToArray();

    [Fact]
    public void A_removal_drops_exactly_the_slot_it_names()
    {
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, BuffA, t0, t0 + 60_000, 60_000, Me, level: 10, slot: 65);
        dm.SaveUseBuff(Me, BuffB, t0, t0 + 60_000, 60_000, Me, level: 10, slot: 66);
        Assert.Equal(new[] { BaseA, BaseB }, Codes(dm, t0 + 1_000));

        dm.RemoveBuffSlots(Me, new[] { 65 }, t0 + 1_000);

        Assert.Equal(new[] { BaseB }, Codes(dm, t0 + 1_000)); // 남은 수명이 한참인데도 즉시 사라진다
    }

    [Fact]
    public void Several_slots_can_be_cleared_at_once()
    {
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, BuffA, t0, t0 + 60_000, 60_000, Me, level: 10, slot: 65);
        dm.SaveUseBuff(Me, BuffB, t0, t0 + 60_000, 60_000, Me, level: 10, slot: 66);

        dm.RemoveBuffSlots(Me, new[] { 65, 66 }, t0 + 1_000);

        Assert.Empty(dm.ActiveOwnerBuffs(t0 + 1_000));
    }

    [Fact]
    public void A_party_members_removal_never_touches_my_slots()
    {
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, BuffA, t0, t0 + 60_000, 60_000, Me, level: 10, slot: 65);

        dm.RemoveBuffSlots(Ally, new[] { 65 }, t0 + 1_000);

        Assert.Equal(new[] { BaseA }, Codes(dm, t0 + 1_000));
    }

    [Fact]
    public void An_unknown_slot_is_a_no_op()
    {
        // 실측상 제거 브로드캐스트의 상당수는 우리가 추적하지 않는 버프를 가리킨다 — 무해해야 한다.
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, BuffA, t0, t0 + 60_000, 60_000, Me, level: 10, slot: 65);

        dm.RemoveBuffSlots(Me, new[] { 99 }, t0 + 1_000);

        Assert.Equal(new[] { BaseA }, Codes(dm, t0 + 1_000));
    }

    [Fact]
    public void Entries_with_an_unknown_slot_are_left_to_expire_normally()
    {
        // slot 0 = 모름(옛 경로/합성 엔트리). 슬롯 매칭이 불가능하므로 건드리지 않고 만료에 맡긴다.
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, BuffA, t0, t0 + 60_000, 60_000, Me, level: 10, slot: 0);

        dm.RemoveBuffSlots(Me, new[] { 0 }, t0 + 1_000);

        Assert.Equal(new[] { BaseA }, Codes(dm, t0 + 1_000));
    }

    [Fact]
    public void A_re_application_adopts_the_new_slot()
    {
        // 같은 버프가 다른 슬롯으로 재적용되면 옛 슬롯 제거는 더 이상 그 항목을 가리키지 않아야 한다.
        long t0 = 1_000_000;
        DataManager dm = Self(t0);
        dm.SaveUseBuff(Me, BuffA, t0, t0 + 60_000, 60_000, Me, level: 10, slot: 65);
        dm.SaveUseBuff(Me, BuffA, t0 + 100, t0 + 60_100, 60_000, Me, level: 10, slot: 70);

        dm.RemoveBuffSlots(Me, new[] { 65 }, t0 + 1_000);
        Assert.Equal(new[] { BaseA }, Codes(dm, t0 + 1_000)); // 옛 슬롯 = 무효

        dm.RemoveBuffSlots(Me, new[] { 70 }, t0 + 1_000);
        Assert.Empty(dm.ActiveOwnerBuffs(t0 + 1_000));
    }
}
