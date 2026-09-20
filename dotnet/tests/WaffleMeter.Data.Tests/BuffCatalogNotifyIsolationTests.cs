using System;
using System.Collections.Generic;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 이 설치본에서 <b>처음 관측되는</b> 버프 base 코드가 나갈 때 <c>BuffCatalogChanged</c> 알림이 함께 나간다.
/// 그 알림은 캡처 소비 스레드에서 UI 구독자에게 직접 꽂히므로 <b>언제든 예외를 던질 수 있다</b>(설정창 버프
/// 탭이 열려 있으면 WPF가 바인딩된 컬렉션 변경에 NotSupportedException을 던진다).
/// <para>여기서 고정하는 회귀는 두 가지다(M-13). ① 구독자의 예외가 호출자를 되감아 <b>그 버프의 등록 자체를
/// 건너뛰던 것</b> — 관측 집합에는 이미 들어가 있어 다음 프레임부터는 알림조차 안 나므로, 그 코드는 오버레이·
/// 음성·패킷로그에서 한 번 빠진 뒤 영영 단서가 없었다. ② 진단 카운터 <c>SelfAccepted</c>가 스토어보다
/// <b>먼저</b> 올라가 "수용됨"으로 거짓 보고하던 것.</para>
/// </summary>
public sealed class BuffCatalogNotifyIsolationTests
{
    private const int Me = 7;
    private const int JobBuffCode = 118_000_071; // 일반 직업 버프(런타임 코드) — base 11800000
    private const int JobBuffBase = 11_800_000;

    private static DataManager WithExecutor()
    {
        var dm = new DataManager();
        dm.SaveNickname(Me, "본인", isExecutor: true, server: 3, jobByte: 0);
        return dm;
    }

    [Fact]
    public void A_throwing_catalog_subscriber_does_not_cost_the_buff_that_discovered_it()
    {
        long t0 = 1_000_000;
        DataManager dm = WithExecutor();
        dm.BuffCatalogChanged += () => throw new NotSupportedException("이 스레드에서 Dispatcher 컬렉션을 건드렸다");

        dm.SaveUseBuff(Me, JobBuffCode, t0, t0 + 6_000, 6_000, actorId: Me);

        // 예외는 알림에서 끝나야 한다: 버프는 오버레이에 올라가 있어야 하고 SaveUseBuff는 던지지 않는다.
        OwnerBuffView row = Assert.Single(dm.ActiveOwnerBuffs(t0 + 1_000));
        Assert.Equal(JobBuffBase, row.Code);
    }

    [Fact]
    public void The_catalog_is_notified_only_after_the_buff_is_already_on_the_overlay()
    {
        long t0 = 1_000_000;
        DataManager dm = WithExecutor();

        (long SelfAccepted, int StoreCount) atNotify = (-1, -1);
        dm.BuffCatalogChanged += () =>
        {
            var snap = dm.BuffDiagSnapshot(t0);
            atNotify = (snap.SelfAccepted, snap.StoreCount);
        };

        dm.SaveUseBuff(Me, JobBuffCode, t0, t0 + 6_000, 6_000, actorId: Me);

        // 알림 시점에 이미 스토어에 들어가 있어야 한다 — 이 순서가 뒤집히면 구독자 예외 하나가 등록을 통째로
        // 날린다(그리고 카운터만 남아 "수용됨"으로 거짓말한다).
        Assert.Equal(1, atNotify.StoreCount);
        Assert.Equal(1, atNotify.SelfAccepted);
    }

    [Fact]
    public void The_accepted_counter_and_the_store_agree_even_when_a_subscriber_throws()
    {
        long t0 = 1_000_000;
        DataManager dm = WithExecutor();
        dm.BuffCatalogChanged += () => throw new InvalidOperationException("구독자 사고");

        dm.SaveUseBuff(Me, JobBuffCode, t0, t0 + 6_000, 6_000, actorId: Me);

        var snap = dm.BuffDiagSnapshot(t0 + 1_000);
        Assert.Equal(1, snap.JobBuffSeen);
        Assert.Equal(0, snap.OwnerZero);
        Assert.Equal(1, snap.SelfAccepted);
        Assert.Equal(1, snap.StoreCount); // 수용 카운트와 스토어가 어긋나면 진단이 쓸모없어진다
    }

    [Fact]
    public void A_fully_off_buff_is_still_discovered_for_the_picker()
    {
        // 등록을 알림보다 앞으로 옮기면서 "스토어에 안 담기는 버프(완전 Off)는 카탈로그에도 안 들어간다"가
        // 되면 안 된다 — picker가 그 코드를 영영 못 배워 사용자가 다시 켤 방법이 없어진다.
        long t0 = 1_000_000;
        DataManager dm = WithExecutor();
        dm.SetHiddenBuffBases(new[] { JobBuffBase });

        dm.SaveUseBuff(Me, JobBuffCode, t0, t0 + 6_000, 6_000, actorId: Me);

        Assert.Empty(dm.ActiveOwnerBuffs(t0 + 1_000));          // Off 이므로 화면엔 없고
        Assert.Contains(JobBuffBase, dm.ObservedBuffBases());   // 관측 카탈로그에는 있다
    }

    [Fact]
    public void The_revival_heal_slot_survives_a_throwing_subscriber_too()
    {
        // SaveRevivalHeal 도 같은 순서 문제를 갖고 있었다(합성 코드라 항상 "처음 보는 코드"로 시작한다).
        long t0 = 1_000_000;
        DataManager dm = WithExecutor();
        dm.BuffCatalogChanged += () => throw new NotSupportedException("구독자 사고");

        dm.SaveRevivalHeal(Me, 14_790_007, amount: 20_000, arrivedAt: t0);

        OwnerBuffView slot = Assert.Single(dm.ActiveOwnerBuffs(t0 + 30_000));
        Assert.Equal(14_790_007, slot.Code);
    }
}
