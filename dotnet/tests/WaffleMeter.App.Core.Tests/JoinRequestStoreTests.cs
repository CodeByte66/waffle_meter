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
    public void An_admit_for_an_already_expired_ghost_does_not_eat_a_real_refusal()
    {
        // 0x970B 는 수락 전용이 아니라 '멤버가 붙었다' 브로드캐스트라 기존 파티원을 다시 싣기도 하고,
        // 사전은 만료된 신청을 배지 승계용으로 계속 들고 있다. 그 유령을 '수락'으로 세면 진짜 거절의
        // 보류를 대신 삼켜, 거절당한 사람 카드가 20초를 마저 채운다.
        long now = 100_000;
        var store = new JoinRequestStore(() => now);
        store.Add(User(1, now - 25_000)); // 만료된 유령(화면엔 없다)
        store.Add(User(2, now - 1_000));  // 방금 거절당한 카드

        store.OnResolved();
        store.Remove(1, admit: true);     // 열거 브로드캐스트가 유령 uid 를 싣고 왔다

        now += 500;
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void A_resolve_that_outlives_a_card_lifetime_is_dropped_instead_of_eating_a_later_applicant()
    {
        // 패널이 닫혀 있으면 하트비트가 멈춰 보류가 분 단위로 살아남는다. 그동안 그 보류가 가리키던 카드는
        // 이미 전부 만료됐으므로, 그대로 적용하면 한참 뒤에 도착한 '새' 신청을 대신 삼킨다.
        long now = 1000;
        var store = new JoinRequestStore(() => now);
        store.Add(User(1, now));
        store.OnResolved();

        now += 60_000;               // 패널이 닫힌 채 1분
        store.Add(User(2, now));     // 새 신청

        now += 500;
        Assert.Equal([2], store.Snapshot().Select(r => r.Requester));
    }

    [Fact]
    public void Pairing_survives_the_admit_arriving_before_the_resolve()
    {
        // 실측 순서는 언제나 0x9709 → 0x970B 였지만, 그 순서에만 기대면 한 번 뒤집히는 순간 종전 버그가
        // 그대로 돌아온다(애먼 카드가 지워진다).
        long now = 1000;
        var store = new JoinRequestStore(() => now);
        store.Add(User(1, 100)); // 살아 있어야 한다
        store.Add(User(2, 200)); // 수락됨

        store.Remove(2, admit: true); // 0x970B 가 먼저
        store.OnResolved();           // 0x9709 가 나중

        now += 500;
        Assert.Equal([1], store.Snapshot().Select(r => r.Requester));
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

    /// <summary>
    /// 🔑 주요 패시브의 레벨은 <b>미장착</b> 쪽에서 온다. 공식 홈은 패시브를 영원히 <c>equip:0</c> 으로
    /// 주므로(2026-08-23 라이브 108명 실측), 장착 맵만 보면 패시브는 한 번도 안 뜬다.
    /// <para>동시에 미장착을 통째로 가져오면 안 된다 — 신청자가 <b>안 낀</b> 액티브가 낀 것처럼 카드에 뜬다.
    /// 그 "안 꼈다"는 사실 자체가 패널이 빠뜨림으로 보여 주려는 정보다.</para>
    /// </summary>
    [Fact]
    public void Enrichment_takes_passive_levels_from_unequipped_but_leaves_unequipped_actives_out()
    {
        var store = new JoinRequestStore(() => 1000);
        var data = new DataManager
        {
            OfficialLookup = new FakeLookup(new OfficialCharacterInfo(
                "u1", 3, null, 900,
                Skills: new Dictionary<int, int> { [11020000] = 7 },        // 예리한 일격 — 장착 액티브
                Unequipped: new Dictionary<int, int>
                {
                    [11780000] = 34,   // 노련한 반격 — 주요 패시브. equip:0 이지만 레벨은 실려 온다
                    [11050000] = 9,    // 분쇄 파동 — 안 낀 액티브. 넘어오면 안 된다
                    [11800000] = 0,    // 살기 파열이지만 레벨 0 = 사이트가 말해 주지 않은 것. 버린다
                })),
        };
        var adapter = new JoinRequestSinkAdapter(store, data);

        adapter.OnJoinRequest(1, "u1", 0, 3, 50, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        JoinRequestUser row = Assert.Single(store.Snapshot());
        Assert.Equal(7, row.Skill[11020000]);            // 장착 액티브는 그대로
        Assert.Equal(34, row.Skill[11780000]);           // 패시브 레벨이 들어왔다
        Assert.DoesNotContain(11050000, row.Skill.Keys); // 안 낀 액티브는 빠진다
        Assert.DoesNotContain(11800000, row.Skill.Keys); // 레벨 0 은 "Lv0" 뱃지가 되느니 없는 편이 낫다
    }

    /// <summary>콜백을 그 자리에서 부르는 조회기 — 배선만 보면 되므로 스레드를 끌어들이지 않는다.</summary>
    private sealed class FakeLookup(OfficialCharacterInfo info) : IOfficialCharacterLookup
    {
        public void LookupAsync(string? nickname, int server, JobClass? fallbackJob, Action<OfficialCharacterInfo> callback)
            => callback(info);

        public OfficialCharacterInfo? LookupBlocking(string? nickname, int server, JobClass? fallbackJob) => info;
    }
}
