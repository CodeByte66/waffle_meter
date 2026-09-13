using WaffleMeter.Capture;
using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>
/// The single source of truth for pending party-join requests. Kotlin had no server-side list (the
/// React UI owned it); the .NET port has no web UI, so this lives here. Keyed by Requester (newest
/// wins). A request lives 20s — modeled here in <see cref="Snapshot"/> so a stale row never survives
/// even if the UI countdown tick stalls; the visible per-row bar is driven separately by the panel.
/// Mutated on the meter-consumer thread; <see cref="Changed"/> is marshalled to the UI by the caller.
/// </summary>
public sealed class JoinRequestStore
{
    public const long LifetimeMs = 20_000L;

    /// <summary>0x9709(해소됨, id 없음)을 받고 나서 짝이 될 0x970B(수락, id 있음)를 기다리는 시간.
    /// <para>실측(2026-09-12 세션 수락 10건)에서 둘은 <b>같은 assembled 배치</b>로 왔고 디스패치 간격이
    /// 0~2ms였다. 250ms는 그보다 두 자릿수 넉넉하면서, 짝이 끝내 안 오는 진짜 거절이 화면에서 사라지는 것을
    /// 사람이 알아채지 못할 만큼 짧다.</para></summary>
    private const long AdmitPairingWindowMs = 250L;

    private readonly object _gate = new();
    private readonly Dictionary<int, JoinRequestUser> _byRequester = new();
    /// <summary>아직 주인을 못 찾은 0x9709들의 도착 시각(오래된 것부터). 자세한 건 <see cref="OnResolved"/>.</summary>
    private readonly List<long> _pendingResolves = new();
    private readonly Func<long> _now;

    public JoinRequestStore(Func<long>? now = null)
        => _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>Raised when the request set changes (add / remove / refuse) — re-render, stay open.</summary>
    public event Action? Changed;

    /// <summary>Raised on a full clear (instance start / party exit) — the panel closes (web isOpen=false).
    /// Kept distinct from <see cref="Changed"/> so a remove that empties the list leaves the panel open
    /// (web parity: only clearAll closes it).</summary>
    public event Action? Cleared;

    /// <summary>Add or replace by requester (newest wins / re-arm the timer).</summary>
    public void Add(JoinRequestUser u)
    {
        lock (_gate) _byRequester[u.Requester] = u;
        Changed?.Invoke();
    }

    /// <summary>Cancel(0x9725) + admit(0x970B) both remove by requester id. An admit also claims one pending
    /// 0x9709 — the server sends that id-less "resolved" signal for accepts too, and if it were left pending it
    /// would go on to drop somebody else's card when the pairing window lapses.</summary>
    public void Remove(int requester, bool admit = false)
    {
        bool changed;
        lock (_gate)
        {
            changed = _byRequester.Remove(requester);
            if (admit && changed && _pendingResolves.Count > 0)
            {
                _pendingResolves.RemoveAt(0); // 이 수락이 그 0x9709의 정체였다
            }
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// 0x9709 — 대기 중이던 신청 하나가 해소됐지만 서버가 <b>누구인지는 말해 주지 않는다</b>.
    /// <para>이 신호는 거절만이 아니라 <b>수락에도</b> 온다. 그래서 곧바로 "가장 오래된 것"을 지우면, 파티장이
    /// 나중에 온 신청을 먼저 수락한 순간 아직 대기 중인 남의 카드가 대신 사라진다. 짝이 될 0x970B(수락, id 포함)는
    /// 실측상 같은 배치로 0~2ms 안에 오므로, <see cref="AdmitPairingWindowMs"/> 동안 보류했다가 짝을 못 찾은
    /// 것만 FIFO로 해소한다 — 거절에는 id를 실은 패킷이 아예 존재하지 않아서 그 이상은 알 길이 없다.</para>
    /// </summary>
    public void OnResolved()
    {
        lock (_gate) _pendingResolves.Add(_now());
        Changed?.Invoke(); // 아직 바뀐 건 없지만, UI가 곧 Snapshot을 다시 읽게 한다
    }

    /// <summary>보류 중인 해소 중 짝짓기 창이 지난 것을 적용한다. 저장소를 읽을 때마다(그리고 패널의 250ms
    /// 하트비트마다) 불린다 — 별도 타이머를 만들지 않으려는 것이고, 어느 쪽이 먼저 부르든 결과는 같다.</summary>
    public void FlushResolved()
    {
        bool changed;
        lock (_gate) changed = FlushResolvedLocked(_now());
        if (changed) Changed?.Invoke();
    }

    private bool FlushResolvedLocked(long now)
    {
        bool changed = false;
        while (_pendingResolves.Count > 0 && now - _pendingResolves[0] >= AdmitPairingWindowMs)
        {
            _pendingResolves.RemoveAt(0);

            // 화면에 아직 떠 있는 것 중 가장 오래된 것만 대상이다. 사전은 만료 항목을 그대로 들고 있으므로
            // (재신청이 배지를 물려받게 하려고 일부러 그렇게 뒀다) 필터 없이 고르면 이미 안 보이는 유령을
            // 지우고 화면의 카드는 그대로 남는다 — 증상만 보면 아무 일도 안 일어난 것처럼 보인다.
            long cutoff = now - LifetimeMs;
            JoinRequestUser? oldest = null;
            foreach (JoinRequestUser r in _byRequester.Values)
            {
                if (r.ArrivedAt >= cutoff && (oldest is null || r.ArrivedAt < oldest.ArrivedAt))
                {
                    oldest = r;
                }
            }

            if (oldest is null)
            {
                _pendingResolves.Clear(); // 지울 게 없으면 남은 보류도 의미가 없다
                break;
            }

            changed |= _byRequester.Remove(oldest.Requester);
        }

        return changed;
    }

    /// <summary>Instance start + party exit — clear everything and close the panel (fires
    /// <see cref="Cleared"/>, not <see cref="Changed"/>).</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            _byRequester.Clear();
            _pendingResolves.Clear();
        }

        Cleared?.Invoke();
    }

    /// <summary>이미 들고 있는 신청의 부가 정보(스킬 배지·전투력·직업)만 채운다. <b>없는 신청을 되살리지
    /// 않는다</b>는 게 요점이다 — 공식 캐릭터 조회는 응답까지 1.7초 이상 걸리는데, 그 사이 파티장이 처리해
    /// 카드가 사라졌는데도 예전에는 콜백이 무조건 다시 넣어 버렸다. 되살아난 카드는 원래 도착 시각을 그대로
    /// 들고 있어 20초를 마저 채우고, 목록이 비었다 다시 차는 전이라 <b>패널이 저절로 다시 열리기까지</b> 했다.
    /// 반환값은 실제로 갱신했는지 여부.</summary>
    public bool Enrich(int requester, Func<JoinRequestUser, JoinRequestUser> update)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_byRequester.TryGetValue(requester, out JoinRequestUser? existing))
            {
                _byRequester[requester] = update(existing);
                changed = true;
            }
        }

        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>The current entry for a requester, ignoring the 20s display cutoff so a re-application can
    /// inherit already-resolved skill/stigma badges. False if none is held.</summary>
    public bool TryGet(int requester, out JoinRequestUser? user)
    {
        lock (_gate) return _byRequester.TryGetValue(requester, out user);
    }

    /// <summary>Newest-first, with entries older than 20s dropped.</summary>
    public IReadOnlyList<JoinRequestUser> Snapshot()
    {
        long now = _now();
        long cutoff = now - LifetimeMs;
        lock (_gate)
        {
            FlushResolvedLocked(now); // 읽는 김에 보류된 해소를 적용한다(패널이 닫혀 하트비트가 없을 때의 경로)
            return _byRequester.Values
                .Where(r => r.ArrivedAt >= cutoff)
                .OrderByDescending(r => r.ArrivedAt)
                .ToList();
        }
    }
}

/// <summary>
/// Bridges the primitives-only <see cref="IJoinRequestSink"/> (raised by the capture pipeline) to the
/// domain <see cref="JoinRequestStore"/>: resolves the job code to its Korean class name and builds the
/// <see cref="JoinRequestUser"/> record. Keeps <c>WaffleMeter.Capture</c> free of a data-layer dependency.
/// </summary>
public sealed class JoinRequestSinkAdapter(JoinRequestStore store, DataManager data) : IJoinRequestSink
{
    private const long EnrichWindowMs = 20_000L;

    public void OnJoinRequest(int requester, string nickname, int jobCode, int server, int power, long arrivedAt)
    {
        JobClass? job = JobClassInfo.ConvertFromCode(jobCode);
        var user = new JoinRequestUser
        {
            Requester = requester,
            Nickname = nickname,
            Job = job?.ClassName(),
            Server = server,
            Power = power,
            ArrivedAt = arrivedAt,
        };

        // Carry any already-resolved badges forward: the initial packet has no skills, so a re-application (or a
        // duplicate 0x9707 while the card is still up) would otherwise blank the badges until the lookup callback
        // re-fires. Keeping the prior skills makes the badges stable across the refresh.
        if (store.TryGet(requester, out JoinRequestUser? existing) && existing!.Skill.Count > 0)
        {
            user = user with { Skill = existing.Skill };
        }

        store.Add(user);

        // Enrich with the official-site skills (live only; no-op offline). The callback fires on a
        // background thread; store.Add is thread-safe and re-renders the card with the skill badges.
        data.RequestOfficialCharacterLookup(requester, nickname, server, job, info =>
        {
            if (info.Skills.Count == 0)
            {
                return;
            }

            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - arrivedAt > EnrichWindowMs)
            {
                return; // request already expired
            }

            // Enrich, never Add: the request may already be gone (accepted or refused in-game while the three
            // HTTP hops ran), and re-Adding it there is what made a resolved card sit out its full 20s — and
            // re-opened the panel on the empty->non-empty transition.
            store.Enrich(requester, current => current with
            {
                Skill = new Dictionary<int, int>(info.Skills),
                Power = current.Power > 0 ? current.Power : info.Power,
                Job = current.Job ?? info.Job?.ClassName(),
            });
        });
    }

    public void OnJoinRequestRemove(int requester, bool admit) => store.Remove(requester, admit);
    public void OnRefuseJoinRequest() => store.OnResolved();
    public void OnExitPartyUi() => store.ClearAll();
}
