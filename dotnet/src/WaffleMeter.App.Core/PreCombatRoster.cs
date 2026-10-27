using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>전투 전 파티 프리뷰를 만드는 데 필요한 소스 전부(한 틱의 사본). <see cref="PreCombatRoster.Gather"/> 가 DataManager 에서
/// 모으고, 테스트는 직접 만든다.</summary>
/// <param name="ExecutorUid">지금 본인 uid(0 = 미인식).</param>
/// <param name="AuthoritativeParty">0x9702 로스터를 uid 로 풀어 낸 멤버(<c>DataManager.PartyRoster</c>).</param>
/// <param name="RosterIdentities">0x9702 로스터 원본(닉네임, 서버, 슬롯) — uid 해석·드롭 없음.</param>
/// <param name="MemberProfiles">0x9200/0x920D 가 실어 온 (엔티티 uid, 닉네임, 서버).</param>
/// <param name="OffZoneProfiles">0x9200/0x920D 의 본인 파티원 중 다른 존에 있어 uid 가 0 인 (닉네임, 서버).</param>
/// <param name="RecentCombatUids">최근 TTL 안에 보스를 같이 때린 uid — 기록된 순서 그대로.</param>
/// <param name="RosterJobPower">0x9702 로스터가 실어 온 (닉네임, 서버, 직업코드, 전투력).</param>
/// <param name="Force">포스(20인 공대) 로스터. 없거나 낡았으면 null.</param>
/// <param name="OwnPartyKey">최근 0x9200 이 실어 온 본인 파티의 서버 key(0 = 없음). 본인 uid 를 모를 때 포스 안에서 내 파티를 고른다.</param>
/// <param name="UserById">신원 저장소 조회.</param>
/// <param name="UserByIdentity">신원 저장소 (닉네임, 서버) 조회 — uid 없는 행에 저장된 직업·전투력을 붙일 때만 쓴다.</param>
/// <param name="UserPowerAtMs">그 uid 의 전투력이 마지막으로 기입된 시각(0 = 모름) — 포스 전투력과 '나중 것'을 고를 때.</param>
public sealed record PreCombatRosterInputs(
    int ExecutorUid,
    IReadOnlyList<User> AuthoritativeParty,
    IReadOnlyList<(string Nickname, int Server, int Slot)> RosterIdentities,
    IReadOnlyList<(int Uid, string Nickname, int Server)> MemberProfiles,
    IReadOnlyList<(string Nickname, int Server)> OffZoneProfiles,
    IReadOnlyList<int> RecentCombatUids,
    IReadOnlyList<(string Nickname, int Server, int JobCode, int Power)> RosterJobPower,
    ForceRosterView? Force,
    long OwnPartyKey,
    Func<int, User?> UserById,
    Func<string, int, User?> UserByIdentity,
    Func<int, long> UserPowerAtMs);

/// <summary>
/// 전투 전(대기) 파티 프리뷰 행을 만든다 — 종전 <c>App.xaml.cs</c> 에 있던 조립을 테스트 가능하게 옮긴 것이다.
/// 행 순서는 그대로다: 본인 먼저, 그다음 전투력 내림차순(동률은 들어온 순서), 이름만 있는 자리표시 행은 맨 뒤.
///
/// <para><b>종전과 달라진 것은 셋뿐이다.</b>
/// <list type="number">
/// <item><b>A</b> — 0x9200 프로필 행이 빈 <c>new User(uid, 닉, 서버)</c> 가 아니라 <b>저장소의 그 User</b>(직업·전투력 포함)다.
/// 본인이면 본인 User 가 먼저 자리 잡는다(종전엔 빈 객체가 먼저 들어가 본인 주입이 건너뛰어졌다).</item>
/// <item><b>B</b> — 다른 존에 있어 uid 가 0 인 0x9200/0x920D 파티원을 (닉네임, 서버) 자리표시 행으로 남긴다.</item>
/// <item><b>C</b> — 20인 공대(포스 정원 &gt; 10)에서는 <b>본인이 든 파티 다섯 명만</b> 보여 준다(직업·전투력 포함). 전투력은
/// 포스 패킷과 신원 저장소 중 <b>나중에 받은 값</b>이다. 0x9702 방 로스터가 살아 있으면(10인 공대·일반 던전) 이 규칙은 꺼진다 —
/// 그쪽은 종전 동작을 한 바이트도 바꾸지 않는다.</item>
/// </list></para>
/// </summary>
public static class PreCombatRoster
{
    /// <summary>이 정원을 넘는 포스만 '20인 공대'로 본다. 10인 공대도 두 파티짜리 포스라 포스 패킷이 오지만, 그쪽은 0x9702 로
    /// 종전처럼 보여 준다.</summary>
    public const int RaidForceMinLimit = 11;

    /// <summary>DataManager 에서 한 틱 분량을 모은다. <paramref name="authoritativeParty"/> 는 호출자가 같은 틱에 따로 쓰므로
    /// (자기 복구 가드) 받아서 그대로 넘긴다. <paramref name="lastCombatMs"/> 의 순서가 결과의 동률 순서가 된다.</summary>
    public static PreCombatRosterInputs Gather(
        DataManager data,
        IReadOnlyList<User> authoritativeParty,
        IEnumerable<KeyValuePair<int, long>> lastCombatMs,
        long nowMs,
        long ttlMs)
    {
        var recent = new List<int>();
        foreach (KeyValuePair<int, long> kv in lastCombatMs)
        {
            if (nowMs - kv.Value <= ttlMs)
            {
                recent.Add(kv.Key);
            }
        }

        return new PreCombatRosterInputs(
            data.ExecutorId(),
            authoritativeParty,
            data.PartyRosterIdentities(ttlMs),
            data.MemberProfileRoster(ttlMs),
            data.MemberProfileOffZone(ttlMs),
            recent,
            data.PartyRosterJobPower(ttlMs),
            data.ForceRosterView(ttlMs),
            data.OwnPartyKey(ttlMs),
            data.User,
            data.FindUserByNicknameAndServer,
            data.UserPowerObservedAtMs);
    }

    public static List<User> Build(PreCombatRosterInputs inp)
    {
        int execUid = inp.ExecutorUid;
        User? self = execUid != 0 ? inp.UserById(execUid) : null;
        if (self is not null && string.IsNullOrWhiteSpace(self.Nickname))
        {
            self = null;
        }

        if (TryBuildRaidForceParty(inp, self) is { } forceRows)
        {
            return forceRows;
        }

        // Authoritative party from the 0x9702 roster packet (fires on party formation, so the party shows on dungeon
        // entry BEFORE any combat), unioned with recent boss-combat contributors as a fallback (covers a party seen only
        // in combat / before its roster snapshot arrives). Dedup by uid, executor first then power desc.
        var rosterById = new Dictionary<int, User>();
        foreach (User member in inp.AuthoritativeParty)
        {
            rosterById[member.Id] = member;
        }

        // 현재 파티(최신 0x9702 스냅샷)의 신원 집합. 아래 0x9200 프로필·최근 전투 기여자 폴백은 5분 TTL이라 파티를 떠난 이전
        // 파티원을 대기 프리뷰에 계속 누적한다. 0x9702 로스터가 있을 땐 현재 파티에 있는 멤버(닉+서버)만 폴백으로 추가하고,
        // 로스터가 없으면(필드보스·입장 버스트로 로스터 미도착) 종전대로 폴백을 그대로 쓴다. 본인은 뒤에서 별도 주입.
        var currentPartyIds = new HashSet<(string, int)>(inp.RosterIdentities.Select(m => (m.Nickname, m.Server)));
        bool filterToCurrentParty = currentPartyIds.Count > 0;

        // 0x9200 멤버 프로필(uid 동반)도 프리뷰 로스터에 넣는다 — 0x9702 로스터 스냅샷이 입장 버스트에서 유실돼도 파티가
        // 미리 뜨게 하는 이중 소스. (A) 행은 저장소의 그 User 다 — 직업 아이콘·전투력이 같이 온다.
        foreach ((int mpUid, string mpNick, int mpServer) in inp.MemberProfiles)
        {
            if (!rosterById.ContainsKey(mpUid) && !string.IsNullOrWhiteSpace(mpNick)
                && (!filterToCurrentParty || currentPartyIds.Contains((mpNick, mpServer))))
            {
                rosterById[mpUid] = ProfileRow(inp, self, mpUid, mpNick, mpServer);
            }
        }

        foreach (int uid in inp.RecentCombatUids)
        {
            if (rosterById.ContainsKey(uid))
            {
                continue;
            }

            User? u = inp.UserById(uid);
            if (u != null && !string.IsNullOrWhiteSpace(u.Nickname)
                && (!filterToCurrentParty || u.Id == execUid || currentPartyIds.Contains((u.Nickname!, u.Server))))
            {
                rosterById[uid] = u;
            }
        }

        // Did a real party source (0x9702 roster / 0x9200 / recent combat) report anyone? If not, the only thing in the
        // preview is the self-injection below — a purely solo preview we suppress.
        bool hasPartySource = rosterById.Count > 0 || inp.OffZoneProfiles.Count > 0;

        // Pin the recognized 본인 so the local player shows even when the party packets omit self. Dedup by uid.
        if (self is not null && !rosterById.ContainsKey(execUid))
        {
            rosterById[execUid] = self;
        }

        List<User> partyRoster = rosterById.Values
            .OrderByDescending(u => u.Id == execUid)
            .ThenByDescending(u => u.Power)
            .ToList();
        // Dedup by character identity (nickname+server): 본인 can persist under an OLD uid AND the current executor uid,
        // so keep the first — the executor sorts first, so 본인 keeps its executor uid.
        var seenIdentity = new HashSet<(string, int)>();
        partyRoster = partyRoster
            .Where(u => string.IsNullOrWhiteSpace(u.Nickname) || seenIdentity.Add((u.Nickname!, u.Server)))
            .ToList();
        // Suppress a PURELY self-injected solo preview (no party source).
        if (!hasPartySource && partyRoster.Count == 1 && partyRoster[0].Id == execUid)
        {
            partyRoster.Clear();
        }

        // 0x9702 로스터 이름 중 uid 가 아직 안 풀린 멤버는 자리표시 행으로 채운다(합성 음수 uid). (B) 다른 존에 있는 0x9200
        // 파티원도 같은 방식으로 붙는다 — 저장소가 그 (닉네임, 서버)를 알면 직업·전투력도 같이.
        if (partyRoster.Count > 0 || hasPartySource)
        {
            var shownIdentities = new HashSet<(string, int)>(
                partyRoster.Where(u => !string.IsNullOrWhiteSpace(u.Nickname)).Select(u => (u.Nickname!, u.Server)));
            int placeholderId = -1;
            foreach ((string rNick, int rServer, int _) in inp.RosterIdentities)
            {
                if (!string.IsNullOrWhiteSpace(rNick) && shownIdentities.Add((rNick, rServer)))
                {
                    partyRoster.Add(new User(placeholderId--, rNick, rServer));
                }
            }

            foreach ((string oNick, int oServer) in inp.OffZoneProfiles)
            {
                if (string.IsNullOrWhiteSpace(oNick)
                    || (filterToCurrentParty && !currentPartyIds.Contains((oNick, oServer)))
                    || !shownIdentities.Add((oNick, oServer)))
                {
                    continue;
                }

                User row = new(placeholderId--, oNick, oServer);
                if (inp.UserByIdentity(oNick, oServer) is { } known)
                {
                    row.Job = known.Job;
                    row.JobSource = known.JobSource;
                    row.Power = known.Power;
                }

                partyRoster.Add(row);
            }
        }

        // 0x9702 로스터는 직업·전투력도 실어 온다. 프리뷰 행이 그 멤버의 0x3645/0x3633을 아직 못 받았으면 '빈 값만' 채운다 —
        // display-only, 본인·이미 채워진 행은 건드리지 않고, repo User 오염을 막으려 Copy에 쓴다.
        var rosterJp = new Dictionary<(string, int), (JobClass? Job, int Power)>();
        foreach ((string jpNick, int jpServer, int jpJobCode, int jpPower) in inp.RosterJobPower)
        {
            if (!string.IsNullOrWhiteSpace(jpNick))
            {
                rosterJp[(jpNick, jpServer)] = (JobClassInfo.ConvertFromCode(jpJobCode), jpPower);
            }
        }

        if (rosterJp.Count > 0)
        {
            for (int i = 0; i < partyRoster.Count; i++)
            {
                User u = partyRoster[i];
                if (u.Id == execUid || string.IsNullOrWhiteSpace(u.Nickname) || (u.Job != null && u.Power > 0))
                {
                    continue;
                }

                if (!rosterJp.TryGetValue((u.Nickname!, u.Server), out (JobClass? Job, int Power) jp))
                {
                    continue;
                }

                User c = u.Copy();
                if (c.Job == null && jp.Job != null)
                {
                    c.Job = jp.Job;
                    c.JobSource = JobProvenance.Authoritative;
                }

                if (c.Power <= 0 && jp.Power > 0)
                {
                    c.Power = jp.Power;
                }

                partyRoster[i] = c;
            }
        }

        return partyRoster;
    }

    /// <summary>(A) 0x9200 프로필 한 건의 행. 본인이면 본인 User, 저장소가 그 uid 를 같은 (닉네임, 서버)로 알면 그 User,
    /// 아니면(uid 재사용·미등록) 종전처럼 이름만 든 새 User.</summary>
    private static User ProfileRow(PreCombatRosterInputs inp, User? self, int uid, string nickname, int server)
    {
        if (self is not null && uid == inp.ExecutorUid)
        {
            return self;
        }

        User? known = inp.UserById(uid);
        return known is not null
               && string.Equals(known.Nickname, nickname, StringComparison.Ordinal)
               && known.Server == server
            ? known
            : new User(uid, nickname, server);
    }

    /// <summary>(C) 20인 공대: 본인이 든 파티만. 조건 — 포스 정원 &gt; 10, 0x9702 방 로스터 없음, 그리고 내 파티를 고를 수 있음
    /// (포스 안에서 본인을 찾거나, 본인을 아직 모르면 0x9200 이 알려 준 내 파티 key 와 같은 파티). 하나라도 아니면 null(종전 경로).
    /// <para>본인을 모르는 경우가 실제로 있다: 미터를 공대 도중에 켜면 0x3633 이 다시 오지 않는다(실측 20261027-205641 —
    /// 본인 인식이 풀 뒤 21:01 에야 됐다). 그래도 0x9200 은 내 서브파티만 싣고 그 key 가 포스 파티 key 와 같다.</para></summary>
    private static List<User>? TryBuildRaidForceParty(PreCombatRosterInputs inp, User? self)
    {
        if (inp.Force is not { LimitMember: >= RaidForceMinLimit } force || inp.RosterIdentities.Count > 0)
        {
            return null;
        }

        int execUid = inp.ExecutorUid;
        bool IsSelf(ForceRosterMember m) =>
            (execUid > 0 && m.EntityUid == execUid)
            || (self is not null && string.Equals(m.Nickname, self.Nickname, StringComparison.Ordinal) && m.Server == self.Server);

        ForceRosterParty? own = force.PartyOf(IsSelf)
            ?? (inp.OwnPartyKey != 0 ? force.Parties.FirstOrDefault(p => p.PartyKey == inp.OwnPartyKey) : null);
        if (own is null)
        {
            return null;
        }

        var rows = new List<User>(own.Members.Count);
        int placeholderId = -1;
        foreach (ForceRosterMember m in own.Members)
        {
            User? known = self is not null && IsSelf(m)
                ? self
                : m.EntityUid > 0 && inp.UserById(m.EntityUid) is { } byUid
                  && string.Equals(byUid.Nickname, m.Nickname, StringComparison.Ordinal) && byUid.Server == m.Server
                    ? byUid
                    : null;

            JobClass? forceJob = JobClassInfo.ConvertFromCode(m.PcId);
            int knownPower = known?.Power ?? 0;
            long knownAt = known is not null ? inp.UserPowerAtMs(known.Id) : 0;
            // '나중에 받은 값'이 이긴다. 저장소 쪽 시각을 모르면(0) 포스 쪽이 이긴다 — 포스 값은 시각을 반드시 든다.
            int power = m.Power > 0 && (knownPower <= 0 || m.PowerAtMs >= knownAt) ? (int)m.Power : knownPower;

            User row;
            if (known is not null)
            {
                row = known;
                if (row.Power != power || (row.Job == null && forceJob != null))
                {
                    row = known.Copy(); // 저장소 User 를 건드리지 않는다
                    row.Power = power;
                    if (row.Job == null && forceJob != null)
                    {
                        row.Job = forceJob;
                        row.JobSource = JobProvenance.Authoritative;
                    }
                }
            }
            else
            {
                row = new User(m.EntityUid > 0 ? m.EntityUid : placeholderId--, m.Nickname, m.Server, forceJob, power: power)
                {
                    JobSource = forceJob != null ? JobProvenance.Authoritative : JobProvenance.None,
                };
            }

            rows.Add(row);
        }

        return rows
            .OrderByDescending(u => u.Id == execUid)
            .ThenByDescending(u => u.Power)
            .ToList();
    }
}
