using WaffleMeter.App.Core;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// Spec for <see cref="JoinRequestStore"/>: dedupe-by-requester, remove/refuse/clear semantics, the
/// 20s staleness filter in Snapshot, and Changed firing — mirrors the web useJoinRequestStore.
/// </summary>
public class JoinRequestStoreTests
{
    private static JoinRequestUser User(int requester, long arrivedAt, int power = 0) => new()
    {
        Requester = requester,
        Nickname = $"u{requester}",
        ArrivedAt = arrivedAt,
        Power = power,
    };

    [Fact]
    public void Add_dedupes_by_requester_newest_wins()
    {
        var store = new JoinRequestStore(() => 1000);
        store.Add(User(1, 100, power: 10));
        store.Add(User(1, 200, power: 99)); // same requester -> replace

        var snap = store.Snapshot();
        Assert.Single(snap);
        Assert.Equal(99, snap[0].Power);
        Assert.Equal(200, snap[0].ArrivedAt);
    }

    [Fact]
    public void Snapshot_is_newest_first()
    {
        var store = new JoinRequestStore(() => 1000);
        store.Add(User(1, 100));
        store.Add(User(2, 300));
        store.Add(User(3, 200));

        var snap = store.Snapshot();
        Assert.Equal([2, 3, 1], snap.Select(r => r.Requester));
    }

    [Fact]
    public void Snapshot_drops_entries_older_than_20s()
    {
        long now = 100_000;
        var store = new JoinRequestStore(() => now);
        store.Add(User(1, now - 25_000)); // stale (>20s)
        store.Add(User(2, now - 5_000));  // fresh

        var snap = store.Snapshot();
        Assert.Single(snap);
        Assert.Equal(2, snap[0].Requester);
    }

    [Fact]
    public void Remove_drops_by_requester()
    {
        var store = new JoinRequestStore(() => 1000);
        store.Add(User(1, 100));
        store.Add(User(2, 200));
        store.Remove(1);

        Assert.Equal([2], store.Snapshot().Select(r => r.Requester));
    }

    [Fact]
    public void An_unpaired_resolve_drops_the_oldest_once_the_pairing_window_lapses()
    {
        long now = 1000;
        var store = new JoinRequestStore(() => now);
        store.Add(User(1, 300));
        store.Add(User(2, 100)); // oldest
        store.Add(User(3, 200));

        store.OnResolved();
        Assert.Equal(3, store.Snapshot().Count); // 아직 짝을 기다린다

        now += 250;
        Assert.DoesNotContain(2, store.Snapshot().Select(r => r.Requester));
        Assert.Equal(2, store.Snapshot().Count);
    }

    [Fact]
    public void An_admit_claims_the_resolve_so_nobody_elses_card_is_dropped()
    {
        // 제보의 정체: 0x9709 는 거절뿐 아니라 수락에도 온다. 파티장이 나중 신청을 먼저 수락하면, 즉시
        // "가장 오래된 것"을 지우던 종전 동작은 아직 대기 중인 남의 카드를 대신 지웠다.
        long now = 1000;
        var store = new JoinRequestStore(() => now);
        store.Add(User(1, 100)); // 먼저 온 신청 — 살아 있어야 한다
        store.Add(User(2, 200)); // 파티장이 이쪽을 수락했다

        store.OnResolved();              // 0x9709 (id 없음)
        store.Remove(2, admit: true);    // 같은 배치의 0x970B

        now += 500;
        Assert.Equal([1], store.Snapshot().Select(r => r.Requester));
    }

    [Fact]
    public void Three_resolves_with_one_admit_drop_exactly_the_two_refused()
    {
        // 실측 장면(2026-09-12 +4831~+4834s): 세 명이 대기 중, 0x9709 가 3발, 그중 마지막과 같은 ms 에
        // 0x970B(할매미) 가 붙었다. 남는 건 수락된 한 명이 아니라 '거절된 둘'이어야 한다.
        long now = 1000;
        var store = new JoinRequestStore(() => now);
        store.Add(User(1, 100)); // 랄부킥
        store.Add(User(2, 200)); // 명인만두
        store.Add(User(3, 300)); // 할매미 — 수락됨

        store.OnResolved();
        store.OnResolved();
        store.OnResolved();
        store.Remove(3, admit: true);

        now += 500;
        Assert.Empty(store.Snapshot().Where(r => r.Requester is 1 or 2));
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void A_cancel_does_not_consume_a_pending_resolve()
    {
        // 신청자 본인의 취소(0x9725)에는 0x9709 가 따라오지 않는다 — 그 둘을 짝지으면 진짜 거절이 묻힌다.
        long now = 1000;
        var store = new JoinRequestStore(() => now);
        store.Add(User(1, 100));
        store.Add(User(2, 200));

        store.OnResolved();            // 1번을 거절했다
        store.Remove(2, admit: false); // 그 사이 2번이 스스로 물렀다

        now += 500;
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void A_resolve_never_drops_an_expired_ghost_instead_of_a_visible_card()
    {
        // 사전은 만료 항목을 그대로 들고 있다(재신청이 배지를 물려받게 하려고). 필터 없이 '가장 오래된 것'을
        // 고르면 화면에 없는 유령이 지워지고 카드는 그대로 남아, 사용자 눈에는 아무 일도 안 일어난다.
        long now = 100_000;
        var store = new JoinRequestStore(() => now);
        store.Add(User(1, now - 25_000)); // 이미 만료돼 화면에 없다
        store.Add(User(2, now - 1_000));  // 화면의 유일한 카드

        store.OnResolved();
        now += 500;

        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Enrich_fills_a_live_card_but_never_resurrects_a_resolved_one()
    {
        var store = new JoinRequestStore(() => 1000);
        store.Add(User(1, 100));

        Assert.True(store.Enrich(1, u => u with { Power = 777 }));
        Assert.Equal(777, Assert.Single(store.Snapshot()).Power);

        store.Remove(1, admit: true);
        Assert.False(store.Enrich(1, u => u with { Power = 999 })); // 공식 조회가 1.7초 뒤에 도착한 경우
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void ClearAll_empties()
    {
        var store = new JoinRequestStore(() => 1000);
        store.Add(User(1, 100));
        store.Add(User(2, 200));
        store.ClearAll();

        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Changed_fires_on_mutation_but_not_on_clear()
    {
        var store = new JoinRequestStore(() => 1000);
        int changed = 0;
        store.Changed += () => changed++;

        store.Add(User(1, 100));     // +1
        store.Add(User(1, 150));     // +1 (replace)
        store.Remove(1);             // +1
        store.Remove(1);             // no-op (already gone) -> no fire
        store.FlushResolved();       // nothing pending -> no fire
        store.ClearAll();            // fires Cleared, NOT Changed

        Assert.Equal(3, changed);
    }

    [Fact]
    public void ClearAll_fires_Cleared_and_empties()
    {
        var store = new JoinRequestStore(() => 1000);
        int cleared = 0;
        store.Cleared += () => cleared++;
        store.Add(User(1, 100));
        store.Add(User(2, 200));

        store.ClearAll();

        Assert.Equal(1, cleared);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void TryGet_returns_current_entry_or_false()
    {
        var store = new JoinRequestStore(() => 1000);
        Assert.False(store.TryGet(1, out _));

        store.Add(User(1, 100, power: 42));
        Assert.True(store.TryGet(1, out JoinRequestUser? got));
        Assert.Equal(42, got!.Power);
    }

    [Fact]
    public void OnJoinRequest_carries_forward_resolved_skill_badges()
    {
        var store = new JoinRequestStore(() => 1000);
        var adapter = new JoinRequestSinkAdapter(store, new DataManager()); // no OfficialLookup -> no live enrichment
        var skills = new Dictionary<int, int> { [12345] = 3 };
        store.Add(User(1, 100) with { Skill = skills }); // a previously-enriched card

        // Same requester re-applies: the fresh packet carries no skills, but the badges must persist.
        adapter.OnJoinRequest(requester: 1, nickname: "u1", jobCode: 0, server: 3, power: 50, arrivedAt: 200);

        JoinRequestUser row = Assert.Single(store.Snapshot());
        Assert.Equal(50, row.Power);     // refreshed from the new packet
        Assert.Equal(skills, row.Skill); // badges carried forward, not blanked
    }
}
