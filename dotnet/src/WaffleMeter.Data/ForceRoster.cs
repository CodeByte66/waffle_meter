using WaffleMeter.Capture;

namespace WaffleMeter.Data;

/// <summary>포스 로스터의 멤버 한 명(불변 스냅샷).</summary>
/// <param name="PartyKey">속한 파티의 서버 key. 0 = 아직 모름.</param>
/// <param name="EntityUid">현재 존의 엔티티 uid. 0 = 다른 존(또는 아직 모름).</param>
/// <param name="Power">마지막으로 받은 전투력(0 = 받은 적 없음). <paramref name="PowerAtMs"/> 가 그 시각이다.</param>
public sealed record ForceRosterMember(
    long Dbid, long PartyKey, int Slot, int EntityUid, string Nickname, int Server, int PcId, long Power, long PowerAtMs);

/// <summary>포스 안 파티 하나(불변 스냅샷). 멤버는 슬롯 순.</summary>
public sealed record ForceRosterParty(long PartyKey, int Number, IReadOnlyList<ForceRosterMember> Members);

/// <summary>포스 로스터 전체(불변 스냅샷). 파티는 번호 순.</summary>
/// <param name="LimitMember">ForceInfo 의 정원(20인 공대 = 20). 0 = 스냅샷을 아직 못 받았다.</param>
/// <param name="UpdatedAtMs">마지막으로 이 로스터를 건드린 포스 패킷의 시각(0x962C 상태 포함).</param>
public sealed record ForceRosterView(long ForceKey, int LimitMember, long UpdatedAtMs, IReadOnlyList<ForceRosterParty> Parties)
{
    /// <summary>마지막으로 <b>구성</b>(누가 어느 파티에 있나)을 바꾼 포스 패킷의 시각 — 스냅샷·추가·파티 추가·이동·재접속·제거.
    /// 0x962C 상태는 넣지 않는다. 표시 쪽이 "그 뒤에 온 0x9200 이 포스에 없는 파티를 말한다(공대를 떠났다)"를 가리는 데 쓴다.</summary>
    public long MembershipAtMs { get; init; }

    /// <summary><paramref name="match"/> 를 만족하는 멤버가 든 파티. 없으면 null.</summary>
    public ForceRosterParty? PartyOf(Func<ForceRosterMember, bool> match) =>
        Parties.FirstOrDefault(p => p.Members.Any(match));
}

/// <summary>
/// 포스(20인 공대 = 4파티 × 5) 로스터 상태기 — 0x96xx 계열을 받아 쌓는다. <b>사람은 dbid 로 지목한다</b>:
/// 엔티티 uid 는 존을 옮길 때마다 바뀌고 다른 존에 있으면 0 이라 키가 될 수 없다(실측 콘팡 2851 → 12208 → 7376).
/// <list type="bullet">
/// <item>0x9602 스냅샷 → 통째 교체.</item>
/// <item>0x960A 추가 / 0x961D 파티 추가 → dbid upsert.</item>
/// <item>0x961E 이동 → 실린 파티들의 멤버 목록을 통째로 갈아끼운다(실린 파티는 이동 후 전체 목록이다).</item>
/// <item>0x961B 재접속 → 이미 있는 멤버의 엔티티 uid·전투력 갱신(모르는 사람을 만들지 않는다 — 파티를 모른다).</item>
/// <item>0x9622 제거 → dbid 로 삭제(없으면 no-op).</item>
/// <item>0x962C 상태 → 이미 있는 멤버의 직업·전투력만.</item>
/// </list>
/// 전부 멱등이다(중복 스트림 재생 대상). 전투력은 <b>마지막에 받은 값</b>이 이기고 그 시각을 같이 든다 — 표시 쪽이 다른
/// 소스(0x3645·0x3656)와 시각으로 겨룬다. 스레드: 쓰기는 소비자 스레드, 읽기(<see cref="View"/>)는 UI 스레드라 잠근다.
/// <para>⚠️ 표시 전용이다. 통계 업로드 입력(0x9702 슬롯·정원)에는 절대 흘리지 않는다 — 웹은 8/10인 공대만 받는다.</para>
/// </summary>
public sealed class ForceRoster
{
    private sealed class Member
    {
        public long Dbid;
        public long PartyKey;
        public int Slot;
        public int EntityUid;
        public string Nickname = string.Empty;
        public int Server;
        public int PcId;
        public long Power;
        public long PowerAtMs;
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, int> _partyNumbers = new();   // party key -> 파티 번호
    private readonly Dictionary<long, Member> _members = new();     // dbid -> 멤버
    private long _forceKey;
    private int _limitMember;
    private long _updatedAtMs;
    private long _membershipAtMs;
    private long _snapshotAtMs;

    /// <summary>0x9602 — 통째 교체.</summary>
    public void Apply(ForceSnapshot force, long nowMs)
    {
        lock (_gate)
        {
            _partyNumbers.Clear();
            _members.Clear();
            _forceKey = force.ForceKey;
            _limitMember = force.LimitMember;
            foreach (ForceParty party in force.Parties)
            {
                PutPartyLocked(party, nowMs);
            }

            _updatedAtMs = _membershipAtMs = _snapshotAtMs = nowMs;
        }
    }

    /// <summary>0x960A — 그 파티에 한 명(dbid upsert).</summary>
    public void AddMember(long partyKey, WireRosterMember member, long nowMs)
    {
        lock (_gate)
        {
            UpsertLocked(member, partyKey, nowMs);
            _updatedAtMs = _membershipAtMs = nowMs;
        }
    }

    /// <summary>0x961D — 파티 하나(번호·멤버 upsert).</summary>
    public void AddParty(ForceParty party, long nowMs)
    {
        lock (_gate)
        {
            PutPartyLocked(party, nowMs);
            _updatedAtMs = _membershipAtMs = nowMs;
        }
    }

    /// <summary>0x961E — 실린 파티마다 기존 멤버를 비우고 실린 목록으로 채운다.</summary>
    public void Move(IReadOnlyList<ForceParty> changes, long nowMs)
    {
        lock (_gate)
        {
            var touched = new HashSet<long>(changes.Select(p => p.PartyKey));
            foreach (long dbid in _members.Where(kv => touched.Contains(kv.Value.PartyKey)).Select(kv => kv.Key).ToList())
            {
                _members.Remove(dbid);
            }

            foreach (ForceParty party in changes)
            {
                PutPartyLocked(party, nowMs);
            }

            _updatedAtMs = _membershipAtMs = nowMs;
        }
    }

    /// <summary>0x961B — 이미 있는 멤버만 갱신한다(파티 key 는 그대로).</summary>
    public void Reconnect(WireRosterMember member, long nowMs)
    {
        lock (_gate)
        {
            if (!_members.TryGetValue(member.Dbid, out Member? existing))
            {
                return;
            }

            UpsertLocked(member, existing.PartyKey, nowMs);
            _updatedAtMs = _membershipAtMs = nowMs;
        }
    }

    /// <summary>0x9622 — dbid 로 삭제.</summary>
    public void Remove(long dbid, long nowMs)
    {
        lock (_gate)
        {
            if (_members.Remove(dbid))
            {
                _updatedAtMs = _membershipAtMs = nowMs;
            }
        }
    }

    /// <summary>0x962C — 이미 있는 멤버의 직업·전투력.</summary>
    public void UpdateStatus(ForceMemberStatus status, long nowMs)
    {
        lock (_gate)
        {
            if (!_members.TryGetValue(status.Dbid, out Member? m))
            {
                return;
            }

            if (status.PcId > 0)
            {
                m.PcId = status.PcId;
            }

            if (status.CombatPower > 0)
            {
                m.Power = status.CombatPower;
                m.PowerAtMs = nowMs;
            }

            _updatedAtMs = nowMs;
        }
    }

    /// <summary>캐릭터 전환·초기화.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _partyNumbers.Clear();
            _members.Clear();
            _forceKey = 0;
            _limitMember = 0;
            _updatedAtMs = 0;
            _membershipAtMs = 0;
            _snapshotAtMs = 0;
        }
    }

    /// <summary>캐릭터 전환 때 이 로스터를 남겨도 되는가 — 0x9602 스냅샷이 <paramref name="sinceMs"/> 이후에 왔고 그 안에
    /// <paramref name="isIncoming"/>(새 캐릭터)가 들어 있을 때만 true. 존 로드에서 ForceInfo 는 본인 로드 0x3633 보다 약 7초
    /// 먼저 오므로(실측 20261027-205641, 21:02:41.9 → 21:02:49.0 · 21:02:59.1 → 21:03:06.3), 전환을 알아챈 순간 들고 있는
    /// 스냅샷은 대개 <b>새 캐릭터의 공대</b>다. 새 캐릭터가 그 안에 없으면 나가는 캐릭터의 공대이니 버린다.</summary>
    public bool SnapshotHolds(Func<ForceRosterMember, bool> isIncoming, long sinceMs)
    {
        lock (_gate)
        {
            return _snapshotAtMs > 0 && _snapshotAtMs >= sinceMs
                && _members.Values.Any(m => isIncoming(new ForceRosterMember(
                    m.Dbid, m.PartyKey, m.Slot, m.EntityUid, m.Nickname, m.Server, m.PcId, m.Power, m.PowerAtMs)));
        }
    }

    /// <summary>지금 상태의 불변 사본. 멤버가 하나도 없으면 null.</summary>
    public ForceRosterView? View()
    {
        lock (_gate)
        {
            if (_members.Count == 0)
            {
                return null;
            }

            var parties = _members.Values
                .GroupBy(m => m.PartyKey)
                .Select(g => new ForceRosterParty(
                    g.Key,
                    _partyNumbers.TryGetValue(g.Key, out int n) ? n : 0,
                    g.OrderBy(m => m.Slot)
                        .Select(m => new ForceRosterMember(
                            m.Dbid, m.PartyKey, m.Slot, m.EntityUid, m.Nickname, m.Server, m.PcId, m.Power, m.PowerAtMs))
                        .ToList()))
                .OrderBy(p => p.Number == 0 ? int.MaxValue : p.Number)
                .ToList();
            return new ForceRosterView(_forceKey, _limitMember, _updatedAtMs, parties) { MembershipAtMs = _membershipAtMs };
        }
    }

    private void PutPartyLocked(ForceParty party, long nowMs)
    {
        _partyNumbers[party.PartyKey] = party.Number;
        foreach (WireRosterMember m in party.Members)
        {
            UpsertLocked(m, party.PartyKey, nowMs);
        }
    }

    private void UpsertLocked(WireRosterMember src, long partyKey, long nowMs)
    {
        if (!_members.TryGetValue(src.Dbid, out Member? m))
        {
            m = new Member { Dbid = src.Dbid };
            _members[src.Dbid] = m;
        }

        // 엔티티 uid 는 존 안에서 한 사람 것이다. 다른 멤버가 같은 uid 를 들고 있으면 그건 이전 존의 낡은 값이다.
        if (src.EntityUid > 0)
        {
            foreach (Member other in _members.Values)
            {
                if (!ReferenceEquals(other, m) && other.EntityUid == src.EntityUid)
                {
                    other.EntityUid = 0;
                }
            }
        }

        m.PartyKey = partyKey;
        m.Slot = src.Slot;
        m.EntityUid = src.EntityUid; // 0 도 그대로 받는다 — 지금 다른 존에 있다는 뜻이다
        m.Nickname = src.Nickname;
        m.Server = src.Server;
        if (src.PcId > 0)
        {
            m.PcId = src.PcId;
        }

        if (src.CombatPower > 0)
        {
            m.Power = src.CombatPower;
            m.PowerAtMs = nowMs;
        }
    }
}
