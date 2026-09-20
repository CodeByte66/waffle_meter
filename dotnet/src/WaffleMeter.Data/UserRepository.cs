using WaffleMeter.Capture;

namespace WaffleMeter.Data;

/// <summary>
/// Verbatim port of Kotlin UserRepository. Identity (nickname/server/job) and combat power can
/// arrive in separate packets, so power that arrives before identity is held in "pending" maps and
/// merged in when the user is created. mergeInto fills only missing fields (never overwrites a known
/// nickname/server; an authoritative job overrides an inferred one).
/// <para>2026-08-17 정정: 전투력만 Kotlin 원본대로 "최신 &gt; 0이 무조건 이김"이었는데, 그게 저장소
/// 전체에서 유일한 무조건 덮어쓰기였고 <see cref="DataManager"/>의 공식 조회 본 분기가 일부러 지키는
/// fill-only 규칙과 어긋났다. 지금은 전투력도 다른 필드와 같은 '빈 칸 채우기'다 — 자세한 근거는
/// <c>MergeInto</c> 주석 참조.</para>
/// <para>2026-09-18: 이 저장소 전체가 단일 <c>_gate</c> 락 뒤에 있다. 왜 그런지, 그리고 무엇을 지켜야
/// 하는지는 아래 "스레드 안전" 주석을 반드시 읽을 것.</para>
/// </summary>
public sealed class UserRepository
{
    // ── 스레드 안전 (2026-09-18) ────────────────────────────────────────────────────────────────
    // 이 저장소는 최소 세 스레드가 동시에 두드린다:
    //   ① meter-consumer      — 패킷 파서 → DataManager.SaveNickname → Save (패킷마다)
    //   ② ThreadPool          — 공식 캐릭터 조회 콜백 → DataManager.ApplyOfficialCharacterInfo → Save
    //   ③ stats-upload-queue  — StatsPayloadBuilder.ResolveUserSnapshot → Get / FindByNicknameAndServer
    // 종전에는 락이 하나도 없었다. DataManager 는 _aetherGate·_memberProfiles 등 7개 상태를 전부 막아
    // 놨는데 유독 신원 저장소만 맨몸이었고, 그 대가가 조용했다:
    //   • FindByNicknameAndServer 의 폴백(_storage.Values.LastOrDefault)이나 RemovePendingByName 의
    //     Where 열거가 다른 스레드의 Save 와 겹치면 "Collection was modified" 로 터지는데, 그 예외는
    //     ConsumeLoop / StatsUploadQueue.WorkerLoop 의 catch 가 삼켜 MarkSkipped 조차 부르지 않는다
    //     = 전투 1건이 업로드 스킵 사유 화면에도 흔적 없이 사라진다(영구 손실, 재시도 없음).
    //   • _idIndex 의 List<int> 변형(IndexIdentity/EvictOldestBeyondCap)이 겹치면 더 나쁘다 — 예외조차
    //     없이 FindByNicknameAndServer 가 엉뚱한 uid 를 돌려준다.
    //
    // 규칙 (되돌리면 위 증상이 그대로 재발한다):
    //  1) 모든 public 진입점이 읽기까지 _gate 안에서 돈다. "읽기는 안전하다"가 아니다 — 문제가 바로 열거다.
    //  2) _gate 를 잡은 채로 바깥(콜백·이벤트·HTTP·다른 컴포넌트)을 부르지 마라. 지금 이 클래스는 자기
    //     사전과 User 프로퍼티만 만지고 바깥을 한 줄도 부르지 않는다 — 덕분에 _gate 는 언제나 락 사슬의
    //     '끝'이고 DataManager 의 7개 락과 순서 역전(데드락)이 생길 수 없다. 콜백이 필요해지면 락 안에서
    //     스냅샷만 뜨고 락 밖에서 처리해라. 안 그러면 캡처 소비자 스레드가 HTTP 를 기다리게 된다.
    //  3) 시퀀스를 돌려주는 public 멤버를 새로 만들면 반드시 락 안에서 ToList() 로 굳혀라. 지연 열거를
    //     밖으로 내보내면 호출자가 락 밖에서 _storage 를 열거하게 되어 1)이 통째로 무의미해진다.
    //  4) ①이 패킷마다 이 락을 잡는다. 락 구간은 최소로 — 순수 검증과 할당은 락 밖에서 끝낸다.
    //  5) private 헬퍼는 전부 "_gate 를 잡은 상태"를 전제한다(그래서 public 래퍼를 통해서만 진입한다).
    // 남은 한계(이 파일 밖): Get/Save 가 돌려주는 User 는 살아 있는 객체라 호출자가 락 밖에서 그 필드를
    // 고친다(DataManager.SaveNickname 등). 그 필드 단위 경합은 여기서 막을 수 없다 — 이 락이 보장하는
    // 것은 저장소 자료구조(사전·인덱스)의 무결성이다.
    private readonly object _gate = new();

    private readonly Dictionary<int, User> _storage = new();
    private readonly Dictionary<string, User> _pendingByNameServer = new();
    private readonly Dictionary<int, User> _pendingById = new();
    private readonly Dictionary<string, User> _pendingByNickname = new();
    private int _executor;

    // Per-identity (name+server) index of uids, ordered oldest-first / newest-last. The self re-registers under
    // a FRESH uid on every zone/instance load (0x3633), so without bounding, its prior User objects accumulate
    // without limit (5+ in one session) and a plain name lookup picks an arbitrary stale one. This index lets
    // FindByNicknameAndServer return the NEWEST (= live) instance and caps each identity to MaxUidsPerIdentity,
    // evicting the long-dead oldest while keeping a small buffer so a late in-flight packet for the just-replaced
    // uid still resolves. _uidIdentity tracks each uid's current group so a reused uid is re-grouped cleanly.
    private const int MaxUidsPerIdentity = 3;
    private readonly Dictionary<string, List<int>> _idIndex = new();
    private readonly Dictionary<int, string> _uidIdentity = new();

    public User? Save(int key, User value)
    {
        lock (_gate)
        {
            return SaveLocked(key, value);
        }
    }

    /// <summary>
    /// 기존 <see cref="User"/>를 <b>락 안에서</b> 고치고 바로 재인덱싱한다. 없으면 아무 일도 안 하고 false.
    /// <para>🔑 <c>_gate</c>는 사전·인덱스의 무결성만 지킨다. <see cref="Get"/>가 돌려주는 것은 <b>살아 있는
    /// 객체</b>라, 흔한 <c>Get → 필드 수정 → Save</c> 패턴은 수정 구간이 락 밖이다. 공식 조회 콜백은
    /// ThreadPool 에서, 닉네임·전투력 저장은 캡처 소비자 스레드에서 오므로 둘이 <b>같은 객체의 같은 필드를
    /// 동시에</b> 고친다. <c>User.Job</c>은 <c>JobClass?</c>(Nullable 구조체)라 쓰기가 원자적이지 않아
    /// <c>HasValue</c>와 값이 찢어져 보일 수 있고, <c>TrySetJob</c>의 read-compare-write 도 그 사이에 끼어든다.</para>
    /// <para>⚠️ <paramref name="edit"/>는 락을 잡은 채 실행된다 — 그 안에서 네트워크·UI·다른 저장소를 부르지 마라.
    /// 캡처 소비자 스레드가 패킷마다 이 락을 기다린다.</para>
    /// </summary>
    public bool Mutate(int id, Action<User> edit)
    {
        lock (_gate)
        {
            if (!_storage.TryGetValue(id, out User? user))
            {
                return false;
            }

            edit(user);
            SaveLocked(id, user); // 닉네임/서버가 바뀌었을 수 있으므로 인덱스를 같은 락 안에서 다시 세운다
            return true;
        }
    }

    public void SavePending(User user)
    {
        lock (_gate)
        {
            SavePendingLocked(user);
        }
    }

    public void RemovePending(User user)
    {
        // 키 정규화는 공유 상태를 안 만지는 순수 문자열 연산이라 락 밖에서 끝낸다 (규칙 4).
        string? nickname = NormalizedNickname(user.Nickname);
        int id = user.Id;
        int server = user.Server;

        lock (_gate)
        {
            if (id > 0)
            {
                _pendingById.Remove(id);
            }

            if (nickname == null)
            {
                return;
            }

            if (server > 0)
            {
                _pendingByNameServer.Remove(NameServerKey(nickname, server));
            }

            _pendingByNickname.Remove(nickname);
        }
    }

    public User? Get(int id)
    {
        lock (_gate)
        {
            return _storage.GetValueOrDefault(id);
        }
    }

    public bool Exist(int id)
    {
        lock (_gate)
        {
            return _storage.ContainsKey(id);
        }
    }

    public User? FindByNicknameAndServer(string nickname, int server)
    {
        // A blank name is not a real identity (IndexIdentity never indexes one); reject it up front so the
        // LastOrDefault fallback below can't match a stray empty-named/provisional user. (순수 검사 = 락 밖.)
        if (string.IsNullOrWhiteSpace(nickname))
        {
            return null;
        }

        lock (_gate)
        {
            // Return the NEWEST matching User. Same name+server = the same character; its most recently registered
            // uid is the live instance (the current executor / current sighting), so this never returns a stale
            // duplicate the way the old FirstOrDefault did. The index is ordered oldest-first, so walk it newest-first.
            if (_idIndex.TryGetValue(NameServerKey(nickname, server), out List<int>? uids))
            {
                for (int i = uids.Count - 1; i >= 0; i--)
                {
                    if (_storage.TryGetValue(uids[i], out User? indexed)
                        && indexed.Nickname == nickname && indexed.Server == server)
                    {
                        return indexed;
                    }
                }
            }

            // Fallback for any entry whose identity was set without going through Save (not expected, but safe).
            // ⚠️ 이 줄이 _storage 를 통째로 열거한다 — 락 없이 돌던 시절 다른 스레드의 Save 와 겹쳐
            // "Collection was modified"가 터지던 바로 그 지점이다. 락 밖으로 빼지 마라.
            return _storage.Values.LastOrDefault(u => u.Nickname == nickname && u.Server == server);
        }
    }

    /// <summary>Kotlin 병행 유지용. <b>현재 호출자 없음</b>(dotnet/src 전역 0건) — 신원보다 전투력이 먼저
    /// 도착하는 소스(예: 0x921C 주기 전투력 브로드캐스트)를 배선할 때 쓰라고 남아 있는 주입구다.
    /// 그 성격상 검증 없는 값이 pending을 통해 <see cref="MergeInto"/>로 흘러 들어가므로, 되살아나는
    /// 순간 2026-08-17 전투력 오염과 같은 종류의 구멍이 된다. 그래서 지금 상한을 걸어 둔다.</summary>
    public void RememberPower(int id, string? nickname, int server, JobClass? job, int power)
    {
        // 상한 검사도 User 할당도 공유 상태를 안 만진다 → 락 밖 (규칙 4).
        if (!CombatPower.IsPlausible(power))
        {
            return;
        }

        var pending = new User(id, nickname, server, job, power: power);
        lock (_gate)
        {
            SavePendingLocked(pending);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _storage.Clear();
            _pendingByNameServer.Clear();
            _pendingById.Clear();
            _pendingByNickname.Clear();
            _idIndex.Clear();
            _uidIdentity.Clear();
        }
    }

    // _executor 는 int 라 읽기 자체는 찢어지지 않지만, EvictOldestBeyondCap 이 이 값을 보고 "이 uid 는
    // 버리지 않는다"를 판단한다. 교체와 퇴출이 같은 락 아래 있어야 방금 승격된 executor 가 다른 스레드의
    // Save 가 촉발한 퇴출에 휩쓸리지 않는다.
    public int Executor()
    {
        lock (_gate)
        {
            return _executor;
        }
    }

    public int Executor(int id)
    {
        lock (_gate)
        {
            int past = _executor;
            _executor = id;
            return past;
        }
    }

    // ── 이하 private: 전부 _gate 를 잡은 상태에서만 호출된다 (규칙 5) ──────────────────────────────

    private User? SaveLocked(int key, User value)
    {
        User? previous = _storage.GetValueOrDefault(key);
        User target = previous ?? value;

        if (previous != null && !ReferenceEquals(previous, value))
        {
            MergeInto(previous, value);
        }

        User? byId = RemovePendingById(key);
        if (byId != null)
        {
            MergeInto(target, byId);
        }

        User? byName = RemovePendingByName(target.Nickname, target.Server);
        if (byName != null)
        {
            MergeInto(target, byName);
        }

        _storage[key] = target;
        IndexIdentity(key, target);
        return previous;
    }

    private void SavePendingLocked(User user)
    {
        if (user.Id > 0)
        {
            if (_storage.TryGetValue(user.Id, out User? existing))
            {
                MergeInto(existing, user);
                return;
            }

            _pendingById[user.Id] = user;
        }

        string? nickname = NormalizedNickname(user.Nickname);
        if (nickname == null)
        {
            return;
        }

        if (user.Server > 0)
        {
            _pendingByNameServer[NameServerKey(nickname, user.Server)] = user;
        }

        _pendingByNickname[nickname] = user;
    }

    private static void MergeInto(User target, User source)
    {
        if (string.IsNullOrWhiteSpace(target.Nickname) && !string.IsNullOrWhiteSpace(source.Nickname))
        {
            target.Nickname = source.Nickname;
        }

        if (target.Server <= 0 && source.Server > 0)
        {
            target.Server = source.Server;
        }

        // Merge the job by confidence: a higher-provenance source (e.g. own-skill over a jobByte) overrides,
        // an equal/lower one doesn't (see User.TrySetJob / JobProvenance).
        target.TrySetJob(source.Job, source.JobSource);

        if (!target.IsExecutor && source.IsExecutor)
        {
            target.IsExecutor = true;
        }

        // 전투력만 저장소 전체에서 유일하게 '무조건 덮어쓰기'였다 — 같은 공식 조회 값인데 경로에 따라
        // 규칙이 달라지는 비대칭이었다: uid가 이미 등록돼 있으면 DataManager.ApplyOfficialCharacterInfo가
        // 일부러 `existing.Power <= 0`일 때만 채우는데, uid가 아직 없으면 SavePending -> Save -> 여기로
        // 흘러 이미 맞던 값을 갈아치웠다. 게다가 RemovePendingByName은 정확 (닉,서버) 키가 빗나가면
        // 서버를 무시한 닉네임 단독 폴백으로 내려가므로 동명이인의 전투력이 넘어올 수도 있다.
        // 그래서 pending 전투력도 '빈 칸 채우기'로만 쓴다 — 본 분기와 같은 규칙.
        if (target.Power <= 0 && source.Power > 0)
        {
            target.Power = source.Power;
        }
    }

    private User? RemovePendingById(int id)
    {
        if (id <= 0)
        {
            return null;
        }

        if (_pendingById.Remove(id, out User? removed))
        {
            return removed;
        }

        return null;
    }

    private User? RemovePendingByName(string? nickname, int server)
    {
        string? normalized = NormalizedNickname(nickname);
        if (normalized == null)
        {
            return null;
        }

        if (server > 0 && _pendingByNameServer.Remove(NameServerKey(normalized, server), out User? exact))
        {
            RemoveNicknameReference(normalized, exact);
            return exact;
        }

        User? nicknameMatch = _pendingByNickname.GetValueOrDefault(normalized);
        // ⚠️ _pendingByNameServer 를 열거한다 — 락 없이 돌던 시절 SavePending 과 겹쳐 터지던 두 번째
        // 지점이다. 이 메서드는 SaveLocked 를 통해서만 들어오므로 항상 _gate 안이다.
        var sameNameEntries = _pendingByNameServer
            .Where(e => NormalizedNickname(e.Value.Nickname) == normalized)
            .ToList();

        var candidates = new List<User>();
        if (nicknameMatch != null)
        {
            candidates.Add(nicknameMatch);
        }

        foreach (KeyValuePair<string, User> e in sameNameEntries)
        {
            candidates.Add(e.Value);
        }

        candidates = candidates
            .GroupBy(u => $"{u.Id}:{u.Server}:{u.Power}")
            .Select(g => g.First())
            .ToList();

        if (candidates.Count != 1)
        {
            return null;
        }

        User selected = candidates[0];
        RemoveIf(_pendingByNickname, normalized, selected);
        foreach (KeyValuePair<string, User> e in sameNameEntries
                     .Where(e => ReferenceEquals(e.Value, selected) || (e.Value.Power == selected.Power && e.Value.Id == selected.Id)))
        {
            RemoveIf(_pendingByNameServer, e.Key, e.Value);
        }

        return selected;
    }

    private void RemoveNicknameReference(string nickname, User user) => RemoveIf(_pendingByNickname, nickname, user);

    private static void RemoveIf(Dictionary<string, User> map, string key, User expected)
    {
        if (map.TryGetValue(key, out User? current) && ReferenceEquals(current, expected))
        {
            map.Remove(key);
        }
    }

    private static string? NormalizedNickname(string? nickname)
    {
        string? trimmed = nickname?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    // Record this uid as the newest under its (name+server) identity, re-grouping it if it changed identity
    // (a reused entity id taken over by a different player), then cap the group. Only indexes a known identity
    // (non-blank nickname + valid server); a bare/provisional uid stays unindexed until it is named.
    private void IndexIdentity(int uid, User target)
    {
        if (string.IsNullOrWhiteSpace(target.Nickname) || target.Server <= 0)
        {
            return;
        }

        string key = NameServerKey(target.Nickname!, target.Server);
        if (_uidIdentity.TryGetValue(uid, out string? oldKey) && oldKey != key
            && _idIndex.TryGetValue(oldKey, out List<int>? oldList))
        {
            oldList.Remove(uid);
            if (oldList.Count == 0)
            {
                _idIndex.Remove(oldKey);
            }
        }

        _uidIdentity[uid] = key;
        if (!_idIndex.TryGetValue(key, out List<int>? uids))
        {
            uids = new List<int>();
            _idIndex[key] = uids;
        }

        uids.Remove(uid); // re-touch: move to the newest end so recency stays accurate on re-registration
        uids.Add(uid);
        EvictOldestBeyondCap(uids);
    }

    // Drop the oldest uids past the cap, but never the current executor (which is the newest for the self anyway).
    // SAFETY INVARIANT: removing a uid from _storage is only safe because a superseded same-name+server uid never
    // emits damage again after the zone/instance load that retires it (DpsCalculator resolves actors purely by the
    // packet uid via _dm.User(actor), so a still-live evicted uid would silently lose its damage). This holds today
    // because a fresh same-character entity-uid is only issued on a zone load that ends the prior battle, and the
    // 3-deep cap keeps the recent ones; if a future packet flow ever lets a retired uid keep dealing, add an
    // is-this-uid-live guard here (e.g. skip eviction for a uid still in the live DPS cache) before removing it.
    private void EvictOldestBeyondCap(List<int> uids)
    {
        int i = 0;
        while (uids.Count > MaxUidsPerIdentity && i < uids.Count)
        {
            if (uids[i] == _executor)
            {
                i++; // keep the executor; evict the next-oldest instead
                continue;
            }

            int victim = uids[i];
            uids.RemoveAt(i);
            RemoveUidCompletely(victim);
        }
    }

    private void RemoveUidCompletely(int uid)
    {
        _storage.Remove(uid);
        _pendingById.Remove(uid);
        _uidIdentity.Remove(uid);
    }

    private static string NameServerKey(string nickname, int server) => $"{nickname}:{server}";
}
