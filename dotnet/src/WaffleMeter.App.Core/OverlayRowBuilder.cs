using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>
/// Pure (UI-free, deterministic) selection of which players the meter shows and in what order — extracted
/// from <c>OverlayViewModel.Update</c> so it is unit-testable (the VM lives in a WinExe WPF project with no
/// test host). Mirrors the React MeterList: sort by metric desc, take top N, always include self. Three
/// behaviors live here:
/// <list type="bullet">
/// <item>Blank-row filter: a COMBAT row whose player has no nickname (a bare 난입/mid-join provisional actor,
/// registered so its DPS isn't dropped) is hidden — it shows "broken". Its DPS still accumulates upstream and
/// the row appears the moment identity (0x3633/0x3645) enriches the SAME user, with its full history.</item>
/// <item>Pre-combat preview: when there is no combat data at all (<see cref="DpsReport.Information"/> empty)
/// AND this is the fresh pre-combat report (<see cref="DpsReport.ExecutorId"/> == 0), the recently-seen party
/// roster is surfaced as idle 0-DPS rows (the App injects the recognized self into that roster, so the local
/// player shows too).</item>
/// <item>Combat-row presence (<paramref name="hasCombatRows"/>): derived from the FILTERED set, so an all-bare
/// mid-join never renders a live boss bar / ticking timer over the "전투 대기 중" placeholder.</item>
/// </list>
/// </summary>
public static class OverlayRowBuilder
{
    /// <summary>One selected row: the player's uid, its metric info, the resolved <see cref="User"/> (may be
    /// null for an unmatched uid), and whether it is the local player ("본인").</summary>
    public readonly record struct Row(int Uid, DpsInformation Info, User? User, bool IsSelf);

    // Minimum share of the top dealer's metric a bare actor must have to be a self-recovery candidate, so an
    // incidental low-damage bare entity (pet/NPC) is never mistaken for the local player.
    // 값은 Data 계층과 공유한다 — 데이터층의 로스터 1:1 본인 복구가 같은 문턱을 쓴다(SelfRecoveryGuards).
    private const double SelfRecoveryMinShare = SelfRecoveryGuards.MinDamageShare;

    // Bug 4: minimum share of the top metric a BARE combat row must reach to be a roster-recovery candidate — a
    // real party member whose 0x3645 name-link was missed (capture began mid-fight / packet lost) is a major
    // dealer, while the trace non-party leaks the blank-row filter targets sit far below this.
    private const double BareMajorMinShare = 0.15;

    /// <summary>
    /// Builds the ordered display rows for one report. <paramref name="liveSelfId"/> is the live-recognized
    /// 본인 uid (used only when the report carries no frozen <see cref="DpsReport.ExecutorId"/>).
    /// <paramref name="roster"/> is the pre-combat party (already executor-first / power-desc, App-supplied).
    /// The <paramref name="selfNickname"/>/<paramref name="selfServer"/>/<paramref name="selfJob"/>/
    /// <paramref name="selfPower"/> are the recognized executor's known identity, used only for lost-executor
    /// recovery (below); pass them whenever 본인 is recognized.
    /// </summary>
    public static IReadOnlyList<Row> Build(
        DpsReport report,
        IReadOnlyList<User> roster,
        int liveSelfId,
        bool useTotalDamage,
        bool showPreCombatRoster,
        out bool hasCombatRows,
        // Max raid is 10 (two parties of 5) since the 2026-07-01 patch; was 8 (4+4). At 8 a 10-인 공대 lost
        // its two lowest dealers off the bottom of the meter (all 10 are still tracked — this only caps display).
        int topN = 10,
        string? selfNickname = null,
        int selfServer = 0,
        JobClass? selfJob = null,
        int selfPower = 0,
        IReadOnlyList<User>? authoritativeParty = null,
        // RAW 0x9702 roster (DataManager.PartyRosterIdentities) — a SUPERSET of authoritativeParty, which drops
        // every member whose uid has never been seen. Carries no uid / job / power, so it can only feed the
        // unambiguous 1:1 rescue (tier 2) and the display-cap exemption; never the job-unique match (no job) nor
        // the stale-name repair (uid-keyed).
        IReadOnlyList<(string Nickname, int Server, int Slot)>? rosterIdentities = null,
        // 0x9200 member profiles — (entity uid, nickname, server) bound in one structurally-validated record.
        // Unlike the 0x9702 roster this carries the ENTITY uid, so a bare combat row whose 0x3645 nickname was
        // missed can be named by uid DIRECTLY (no job-guessing / 1:1 ambiguity), and it survives a lost 0x9702.
        IReadOnlyList<(int Uid, string Nickname, int Server)>? memberProfiles = null,
        // Feature 1: 표시 중인 '끝난 전투'(<see cref="DpsReport.BattleFinished"/> 또는 파티 스냅샷 보유) 위로 로스터
        // 프리뷰를 다시 띄울지. 라이브 idle 경로에서만 true. 기록 재생 경로는 false로 넘겨(재생 전투도 끝난
        // 전투지만 스냅샷이 라이브 로스터와 정당히 다름) 절대 안 비운다. 기존 호출자/테스트는 기본값으로 그대로.
        bool allowRosterResurface = false,
        // 행 정렬·게이지에 쓸 대체 지표(uid -> 값). nDPS/rDPS 표시 모드일 때만 채워 넘긴다. null 이면 기존대로
        // 누적 피해량 / DPS 로 정렬한다.
        //
        // 왜 표시 문자열만 바꾸지 않는가: 정렬을 그대로 두면 1등 행에 3등 숫자가 찍힌다. 서포터의 rDPS가
        // 딜러보다 높은 것이 이 기능의 요점이므로(실측 8인 공대에서 치유성이 raw 5위 → rDPS 1위), 순서가
        // 같이 움직이지 않으면 기능이 아무것도 보여주지 못한다.
        IReadOnlyDictionary<int, double>? metricOverride = null)
    {
        double Metric(DpsInformation info) => useTotalDamage ? info.Amount : info.Dps;

        // 표시 계층과 같은 규칙: 딜이 0인 행(전투 전 로스터 프리뷰)에는 대체 지표를 붙이지 않는다. 안 그러면
        // 직전 전투에서 얼린 값으로 대기 행이 정렬돼, 아직 아무것도 안 한 사람이 맨 위에 선다.
        double RowMetric(int uid, DpsInformation info) =>
            info.Amount > 0 && metricOverride != null && metricOverride.TryGetValue(uid, out double v)
                ? v
                : Metric(info);

        // 본인 id for coloring: prefer the id frozen INTO the report (a saved/history battle), else the live one.
        int selfId = report.ExecutorId != 0 ? report.ExecutorId : liveSelfId;
        bool IsSelf(int uid, User? user) => user?.IsExecutor == true || (selfId != 0 && uid == selfId);

        // Raw COMBAT rows (pre blank-row filter), metric-sorted.
        List<Row> rawCombat = report.Information
            .Select(kv => new Row(kv.Key, kv.Value, report.Contributors.FirstOrDefault(c => c.Id == kv.Key), false))
            .OrderByDescending(e => RowMetric(e.Uid, e.Info))
            .ToList();

        // Lost-executor recovery: when 본인 re-instances (new entity id) and the new id's own-load packet
        // (0x3633) never arrives — e.g. moving to the next boss while the prior encounter is unresolved, so the
        // game never broadcasts the new identity — 본인 fights as a bare actor that the blank-row filter would
        // hide, dropping the local player's whole DPS. Recover it, but ONLY under 5 guards so normal play and
        // skipped fights are untouched: (1) GATE = the recognized executor uid dealt NO damage in this fight
        // (in a normal fight 본인 damages under its registered uid, so this is false and we never get here);
        // (2) the candidate is bare (no 0x3633/0x3645); (3) its own job-locked skills match the known executor
        // job; (4) it is a MAJOR dealer (≥ SelfRecoveryMinShare of the top); (5) it is the UNIQUE such candidate;
        // (6) PARTY-CONTEXT: no NAMED damager is a non-party OUTSIDER (see <see cref="HasNamedOutsider"/>). A
        // re-instance happens in a DUNGEON, where the only players are your party; a NAMED player who is NOT in
        // your authoritative (0x9702) party proves this is a PUBLIC scene — a field-boss zerg — where 본인 is a
        // bystander who simply dealt no damage, not a bare re-instance. Without (6), the recovery relabels a
        // random strong stranger at a field boss as 본인 (the "someone else's field boss shows as my DPS" bug).
        // Done on a display-only copy (no state mutation), recomputed every tick, so it self-corrects the moment
        // the real 본인 id appears.
        // ⚠️ topMetric 이하의 "주력 딜러인가" 판정들(SelfRecoveryMinShare / BareMajorMinShare)은 대체 지표가
        // 아니라 항상 RAW 피해량으로 잰다. 이 게이트들이 묻는 것은 "이 이름 없는 행이 실제로 큰 피해를 냈나"이고,
        // nDPS/rDPS 는 모델 추정이 섞인 값이라 신원 복구 같은 되돌리기 어려운 판단의 근거로 쓸 수 없다.
        double topMetric = rawCombat.Count > 0 ? rawCombat.Max(e => Metric(e.Info)) : 0.0;
        // 이 전투를 함께한 파티. SAVED/기록 리포트는 전투 종료 시점에 얼려 둔 스냅샷을 들고 있고, 그때는
        // 그쪽이 절대적으로 우선한다 — 호출자가 넘기는 로스터는 "지금"의 파티라, 전투가 끝나고 파티를 나가거나
        // 로스터가 만료되면 텅 비어서 그 전투의 이름 복구가 통째로 죽는다(기록을 다시 열면 참가자가 사라지던
        // 원인). 스냅샷이 비어 있으면 진행 중인 라이브 리포트다.
        bool frozenParty = report.PartySnapshot.Count > 0 || report.PartyIdentitiesSnapshot.Count > 0;
        IReadOnlyList<User>? guardParty = frozenParty ? report.PartySnapshot : authoritativeParty;
        IReadOnlyList<User> party = guardParty ?? Array.Empty<User>();
        IReadOnlyList<(string Nickname, int Server, int Slot)> rawParty = frozenParty
            ? report.PartyIdentitiesSnapshot.Select(m => (m.Nickname, m.Server, m.Slot)).ToList()
            : rosterIdentities ?? Array.Empty<(string, int, int)>();
        // rosterConfirmed keeps its old meaning — the roster resolved to real uids — because the uid-keyed
        // repairs (P3) and the job-unique match need those uids. partyKnown is the weaker "0x9702 said SOMETHING
        // about the party" gate that the raw-roster rescue runs under. Both require the caller to have supplied a
        // party context at all (null == "party unknown", which also disables the outsider guard), so a caller
        // that passes only raw identities can never open a recovery whose outsider guard is switched off.
        bool rosterConfirmed = party.Count > 0;
        bool partyKnown = guardParty is not null && (rosterConfirmed || rawParty.Count > 0);
        int? recoveredUid = null;
        if (selfId != 0 && selfJob is { } knownJob && !string.IsNullOrWhiteSpace(selfNickname)
            && rawCombat.All(e => e.Uid != selfId))
        {
            // A recovery candidate is a bare row OR — bug 3 — a row whose stored name is NOT a member of the
            // authoritative (0x9702) party: the self's re-instanced combat uid can inherit a STALE identity from
            // a prior entity that reused that id (observed: 본인 shown as a random "틸놈틸"), which the bare-only
            // filter never reclaimed. The stale-name relaxation is gated on a CONFIRMED roster so that, when the
            // party is unknown, we fall back to the safe bare-only rule (never relabel a genuine stranger).
            // ⚠️ 파티 문맥은 반드시 `party`(= guardParty)로 묻는다. 종전에는 아래 stale-name 판정 한 줄만 원본
            // `authoritativeParty`(= 지금의 라이브 파티)를 봤다. 0x9702는 세션당 1~2회라 라이브 로스터는 5분
            // TTL로 비는데 `rosterConfirmed`는 30분 스냅샷 기준으로 여전히 true여서, 얼린 리포트
            // (BattleFinished / 기록 재생)에서 이름 있는 파티원 전원이 복구 후보가 됐다 — 본인과 같은 직업인
            // 파티원 행 하나가 본인 닉네임·색·IsExecutor로 칠해지던 원인. 같은 메서드의 다른 판정·게이트
            // (외부인 가드, 상한 면제, P3 교정)는 전부 guardParty를 쓴다. 되돌리면 재발한다.
            List<Row> candidates = rawCombat
                .Where(e => (string.IsNullOrWhiteSpace(e.User?.Nickname)
                             || (rosterConfirmed && !IsPartyMember(e.User, party)))
                            && e.User?.Job == knownJob
                            && topMetric > 0 && Metric(e.Info) >= topMetric * SelfRecoveryMinShare)
                .ToList();
            if (candidates.Count == 1 && !HasNamedOutsider(rawCombat, candidates[0].Uid, selfId, guardParty))
            {
                recoveredUid = candidates[0].Uid;
            }
        }

        // Bug 4: name a bare MAJOR dealer from the authoritative (0x9702) roster when a party member is
        // UNACCOUNTED for. A member whose per-uid nickname (0x3645) was missed (capture began mid-fight / the
        // packet was lost) fights bare, and the blank-row filter would hide their whole DPS (observed: the top
        // dealer vanished for two bosses). Recover ONLY on positive roster evidence and an UNAMBIGUOUS 1:1 match
        // — exactly one unclaimed roster member ↔ exactly one bare major — in a party scene with no named
        // outsider, so a field-boss stranger (a bare non-party dealer) is never relabeled a party member. Absent
        // a roster (party unknown) this does nothing: the bare row stays hidden and enriches when identity lands.
        var partyRecovered = new Dictionary<int, User>();

        // 최우선(가장 확실한) 복구 — 0x9200 멤버 프로필이 uid↔이름을 한 레코드에 직접 실어 준 무명 전투행을
        // 그 uid로 곧장 명명한다. 직업 추측·1:1 모호성이 필요 없고, 0x9702 로스터가 통째로 유실된 입장에서도
        // 동작한다(그 파티원의 0x3645 닉이 유실됐을 뿐). 0x9200은 파티/공대 스코프라 여기 uid가 있으면 곧
        // 파티원이다 — 필드보스의 낯선 zerg는 여기에 없어 무명 그대로 숨는다. 표시 전용 복사본만 만든다.
        if (memberProfiles is { Count: > 0 })
        {
            var profileByUid = new Dictionary<int, (string Nickname, int Server)>();
            foreach ((int puid, string pnick, int pserver) in memberProfiles)
            {
                if (!string.IsNullOrWhiteSpace(pnick))
                {
                    profileByUid[puid] = (pnick, pserver);
                }
            }

            foreach (Row e in rawCombat)
            {
                if (!string.IsNullOrWhiteSpace(e.User?.Nickname) || partyRecovered.ContainsKey(e.Uid)
                    || (recoveredUid is { } rp && e.Uid == rp))
                {
                    continue; // 이름 있는 행 / 이미 복구된 행 / 복구된 본인은 건드리지 않는다
                }

                if (profileByUid.TryGetValue(e.Uid, out (string Nickname, int Server) prof))
                {
                    User disp = (e.User ?? new User(e.Uid)).Copy(); // 표시 전용 — 라이브 User는 변경하지 않는다
                    disp.Nickname = prof.Nickname;
                    disp.Server = prof.Server;
                    partyRecovered[e.Uid] = disp;
                }
            }
        }

        // Exclude the recovered self's combat uid from the outsider check: in the bug-3 case it carries a STALE
        // non-party name that would otherwise read as a "named outsider" and wrongly mark this a public scene.
        if (partyKnown && !HasNamedOutsider(rawCombat, recoveredUid ?? -1, selfId, guardParty))
        {
            var claimed = new HashSet<(string, int)>();
            foreach (Row e in rawCombat)
            {
                if (!string.IsNullOrWhiteSpace(e.User?.Nickname))
                {
                    claimed.Add((e.User!.Nickname!, e.User.Server));
                }
            }

            if (recoveredUid is not null && !string.IsNullOrWhiteSpace(selfNickname))
            {
                claimed.Add((selfNickname!, selfServer));
            }

            // member-profile(0x9200)로 이미 명명한 행의 이름도 청구된 것으로 본다 — tier 1/2가 같은 파티원을
            // 다시 붙이거나, 그 이름을 미청구 멤버로 오인하지 않도록.
            foreach (User rec in partyRecovered.Values)
            {
                if (!string.IsNullOrWhiteSpace(rec.Nickname))
                {
                    claimed.Add((rec.Nickname!, rec.Server));
                }
            }

            // 본인이 이 전투에서 미집계이면(등록 uid로 딜 0 + 재라벨도 없음) 본인 역시 '미청구 멤버'다. 그 상태의
            // 무명 major가 본인인지 파티원인지 가릴 근거가 없으므로 본인 항목을 unclaimed에 남겨 모호성을 만든다 —
            // 1:1 주장이 스스로 무너져 이름을 붙이지 않고, 직업이 양쪽에서 유일하게 갈릴 때만(그건 실제 근거다)
            // 붙는다. 이게 없으면 본인 행이 파티원 이름으로 칠해진다(실측: 코퍼스에서 로스터 복구가 실제로 발동한
            // 유일한 사례가 본인 1위 무명 행에 다른 파티원 이름을 칠한 오귀속이었다).
            bool selfMissing = selfId != 0 && recoveredUid is null && rawCombat.All(e => e.Uid != selfId);
            List<User> unclaimed = party
                .Where(m => (selfMissing || m.Id != selfId) && !string.IsNullOrWhiteSpace(m.Nickname)
                            && !claimed.Contains((m.Nickname!, m.Server)))
                .ToList();
            // 복구 후보 = 이름이 없는 행 + 이름이 있어도 그 이름이 확정 로스터에 없는 행(= STALE).
            // 후자는 uid 재사용 때문이다: 재사용된 uid가 이전 점유자의 닉네임을 그대로 들고 있으면 파티원의 딜이
            List<Row> recoverable = rawCombat
                .Where(e => string.IsNullOrWhiteSpace(e.User?.Nickname)
                            && !partyRecovered.ContainsKey(e.Uid) // member-profile로 이미 명명한 행은 제외
                            && (recoveredUid is not { } r || e.Uid != r)
                            && topMetric > 0 && Metric(e.Info) >= topMetric * BareMajorMinShare)
                .ToList();

            // 이미 이름을 가져간 로스터 멤버 — tier 2가 "아직 남은 미청구 멤버가 있는가"를 정확히 세려면 필요하다.
            var consumed = new HashSet<(string, int)>();

            void Claim(Row target, User source)
            {
                User disp = (target.User ?? new User(target.Uid)).Copy();
                disp.Nickname = source.Nickname;
                disp.Server = source.Server;
                disp.Job ??= source.Job;
                if (source.Power > 0 && disp.Power <= 0) disp.Power = source.Power;
                partyRecovered[target.Uid] = disp;
                consumed.Add((source.Nickname!, source.Server));
            }

            // 본인은 모호성을 만들기 위해 unclaimed에 남겨두는 것이지 이름의 '출처'가 아니다. 여기서 본인을
            // 출처로 쓰면 lost-executor 복구가 지키는 6개 가드(직업 일치·최소 비중·유일성·외부인 게이트)를
            // 전부 우회해, 낯선 사람이나 팬텀 행에 본인 이름이 칠해진다 — 그 복구를 대체하면 안 된다.
            if (unclaimed.Count == 1 && recoverable.Count == 1 && unclaimed[0].Id != selfId)
            {
                Claim(recoverable[0], unclaimed[0]);
            }
            else if (unclaimed.Count > 0 && recoverable.Count > 0)
            {
                // 1:1이 아니어도 직업이 유일하게 대응되면 안전하게 이름을 붙일 수 있다. 실측 지배 사례가
                // "동시에 2~3명 무명"이라, 1:1만 고집하면 정작 가장 흔한 경우에 아무도 복구되지 않는다.
                // 양쪽 모두에서 그 직업이 유일해야 한다 — 같은 직업이 둘이면 누가 누군지 알 수 없으므로
                // 이름을 붙이지 않고 넘어간다(잘못된 이름을 붙이느니 그대로 두는 편이 낫다).
                foreach (Row cand in recoverable)
                {
                    if (cand.User?.Job is not { } job)
                    {
                        continue;
                    }

                    if (recoverable.Count(x => x.User?.Job == job) != 1)
                    {
                        continue;
                    }

                    List<User> byJob = unclaimed.Where(m => m.Job == job).ToList();
                    if (byJob.Count == 1 && byJob[0].Id != selfId)
                    {
                        Claim(cand, byJob[0]);
                    }
                }
            }

            // TIER 2 — 로스터엔 있는데 uid가 이번 세션에 한 번도 해석되지 않은 멤버("처음 보는 파티원")로 무명 행을
            // 구제한다. 위의 tier 1은 PartyRoster()가 uid를 찾은 멤버만 보므로, 정작 가장 흔한 실종 케이스(그 캐릭터를
            // 이번 세션에 본 적이 없어 uid를 모름)를 원리적으로 다루지 못했다. raw 로스터에는 uid·직업·전투력이 없어
            // 직업 유일대응 규칙을 쓸 수 없으므로, 여기서는 오직 완전한 1:1일 때만 이름을 붙인다.
            //
            // ⚠️ 외부인 가드(HasNamedOutsider)는 '이름이 있는' 낯선 사람만 본다 — 0x3645를 놓쳐 무명인 낯선
            // 사람은 구조적으로 보이지 않는다. tier 1의 후보는 최소한 uid가 확인된 멤버지만 tier 2의 후보는
            // "이번 세션에 한 번도 못 본 사람"이라, 그 사각지대를 그냥 두면 필드보스의 무명 낯선 사람에게
            // 파티원 이름을 칠하게 된다(이 저장소가 가장 경계하는 둔갑 계열). 그래서 여기가 정말 파티 씬이라는
            // 양성 증거를 요구한다 — 본인 말고 다른 파티원이 이름을 달고 이 전투에 실제로 있거나, 분류된
            // 인스턴스(원정/초월/성역) 보스전이거나. 인스턴스에는 애초에 외부인이 없다.
            bool partySceneConfirmed = report.TargetInstanced
                                       || party.Any(m => m.Id != selfId
                                                         && !string.IsNullOrWhiteSpace(m.Nickname)
                                                         && claimed.Contains((m.Nickname!, m.Server)));
            if (selfId != 0 && !string.IsNullOrWhiteSpace(selfNickname) && partySceneConfirmed)
            {
                var resolvedIdentities = new HashSet<(string, int)>(
                    party.Where(m => !string.IsNullOrWhiteSpace(m.Nickname)).Select(m => (m.Nickname!, m.Server)));

                // 모호성은 slot 필터를 걸기 '전' 집합에서 센다. slot으로 먼저 걸러내면 실종자 둘 중 하나가 사라져
                // 1:1이 거짓으로 성립하고 남의 이름이 칠해진다(실측: 실제 멤버의 6.1%가 slot 0으로 온다).
                List<(string Nickname, int Server, int Slot)> rawOnly = rawParty
                    .Where(m => !string.IsNullOrWhiteSpace(m.Nickname)
                                && !resolvedIdentities.Contains((m.Nickname, m.Server))
                                && !claimed.Contains((m.Nickname, m.Server))
                                // 본인은 후보가 아니다. raw에는 uid가 없으므로 이름(+서버)으로 제외한다.
                                && !(string.Equals(m.Nickname, selfNickname, StringComparison.Ordinal)
                                     && (selfServer <= 0 || m.Server == selfServer)))
                    .GroupBy(m => (m.Nickname, m.Server))
                    .Select(g => g.First())
                    .ToList();

                List<Row> remaining = recoverable.Where(r => !partyRecovered.ContainsKey(r.Uid)).ToList();
                List<User> leftResolved = unclaimed.Where(m => !consumed.Contains((m.Nickname!, m.Server))).ToList();

                // 두 후보 풀 사이의 모호성까지 없을 때만 칠한다 — 해석된 미청구 멤버가 하나라도 남아 있으면
                // 그 사람일 수도 있으므로 붙이지 않는다. slot > 0은 마지막 구조 검증(파싱 유령 배제)으로만 쓴다.
                if (leftResolved.Count == 0 && rawOnly.Count == 1 && remaining.Count == 1 && rawOnly[0].Slot > 0)
                {
                    Claim(remaining[0], new User(remaining[0].Uid, rawOnly[0].Nickname, rawOnly[0].Server, null, false));
                }
            }
        }

        // P3 — uid 재사용으로 이전 점유자의 이름이 남은 행 교정. 판별자는 "로스터가 그 uid에 다른 이름을
        // 부여했는가" 하나뿐이다: 0x9702 로스터는 uid↔이름의 권위 있는 출처이므로, 저장된 닉네임이 로스터의
        // 이름과 다르면 그 uid를 쓰던 이전 점유자의 잔재다(실측: 신원이 바뀐 uid 37개 중 17개가 두 이름 사이
        // 구간에 딜을 넣었고, 그동안 파티원의 딜이 낯선 이름의 행에 얹혀 정작 그 파티원은 사라진 것처럼 보였다).
        // "로스터에 없는 이름 = 재사용"으로 넓히지 않는다 — 그러면 진짜 낯선 사람과 구분할 수 없어 필드보스에서
        // 남을 파티원으로 둔갑시킨다(과거 사고와 같은 계열). 그래서 외부인 가드도 건드리지 않는다.
        if (rosterConfirmed)
        {
            foreach (Row e in rawCombat)
            {
                if (string.IsNullOrWhiteSpace(e.User?.Nickname) || partyRecovered.ContainsKey(e.Uid)
                    || (recoveredUid is { } rid && e.Uid == rid))
                {
                    continue;
                }

                User? m = party.FirstOrDefault(x => x.Id == e.Uid);
                if (m is null || string.IsNullOrWhiteSpace(m.Nickname)
                    || string.Equals(m.Nickname, e.User!.Nickname, StringComparison.Ordinal))
                {
                    continue;
                }

                User fixedUp = e.User!.Copy();
                fixedUp.Nickname = m.Nickname;
                fixedUp.Server = m.Server;
                fixedUp.Job ??= m.Job;
                partyRecovered[e.Uid] = fixedUp;
            }
        }

        // COMBAT rows: keep named rows + the recovered 본인 + a roster-recovered party member (named on a copy);
        // drop other bare rows. Gate on nickname ALONE — never Power (arrives async, would transiently hide known
        // party members) nor Server.
        var entries = new List<Row>();
        foreach (Row e in rawCombat)
        {
            if (recoveredUid is { } ru && e.Uid == ru)
            {
                User disp = (e.User ?? new User(e.Uid)).Copy(); // display-only: never mutate the live User
                disp.Nickname = selfNickname;
                if (selfServer > 0) disp.Server = selfServer;
                disp.Job ??= selfJob;
                disp.IsExecutor = true;
                if (selfPower > 0 && disp.Power <= 0) disp.Power = selfPower;
                entries.Add(e with { User = disp });
            }
            else if (partyRecovered.TryGetValue(e.Uid, out User? recoveredMate))
            {
                entries.Add(e with { User = recoveredMate }); // bug 4: bare party member named from the roster
            }
            else if (!string.IsNullOrWhiteSpace(e.User?.Nickname))
            {
                entries.Add(e);
            }
        }

        // Feature 1 — 파티/공대 구성이 (닉네임+서버 기준) 바뀌면 직전 전투 대신 로스터 프리뷰를 다시 띄운다.
        // 표시 중인 전투의 파티 스냅샷과 지금 살아있는 로스터(rosterIdentities)의 신원 집합을 비교해 다르면
        // (가입/탈퇴/교체) 프리뷰로 갈아끼운다 — 직전 전투는 전투 기록에 남으니 무해. uid는 무시(닉/서버만).
        // 프리뷰의 목적이 "전투 시작 전부터 파티/공대원 전투력을 조회 없이 본다"이므로, 파티가 새 정보가 된
        // 순간(솔로→파티, 파티 교체, 인원 변동) 프리뷰가 이겨야 한다. 같은 파티 리풀은 직전 결과를 봐야 하는
        // 구간이라 그대로 둔다(SetEquals가 참).
        //
        // ⚠️ 게이트의 첫 항은 "표시 중인 리포트가 끝난 전투인가"(BattleFinished)다. 종전에는 frozenParty
        // (= 그 전투에 파티가 있었나)를 대용으로 썼는데, 혼자 잡은 전투는 스냅샷이 정당하게 비어 게이트가 영영
        // 안 열렸다 — 실측: 솔로 필드보스를 잡고 파티에 들어가도 프리뷰 대신 직전 솔로 전투가 계속 떠 있고,
        // 실제 전투가 시작돼야 파티원이 보였다. frozenParty를 OR로 남긴 것은 BattleFinished를 세팅하지 않는
        // 호출자(기록 재생·툴·기존 테스트)의 하위호환용이다.
        // ⚠️ 이 게이트에서 "끝난 전투" 조건 자체를 빼면 안 된다 — 진행 중 전투는 battleSet이 늘 공집합이라
        // 매 틱 rosterChanged=true가 되어 아래 entries.Clear()가 전투 행을 계속 지운다(전투 중 미터가 빈다).
        bool rosterChanged = false;
        if (allowRosterResurface && (report.BattleFinished || frozenParty) && rosterIdentities is { Count: > 0 })
        {
            var currentSet = new HashSet<(string, int)>(
                rosterIdentities.Where(m => !string.IsNullOrWhiteSpace(m.Nickname)).Select(m => (m.Nickname, m.Server)));
            var battleSet = new HashSet<(string, int)>(
                report.PartyIdentitiesSnapshot.Where(m => !string.IsNullOrWhiteSpace(m.Nickname)).Select(m => (m.Nickname, m.Server)));
            // 지금 파티 신원집합 ≠ 표시중 전투 신원집합이면 변경. 빈 로스터(파티 나감/TTL)는 변경으로 치지 않는다
            // (town에서 마지막 전투를 프리뷰로 지우지 않도록).
            rosterChanged = currentSet.Count > 0 && !currentSet.SetEquals(battleSet);
        }

        if (rosterChanged)
        {
            entries.Clear(); // 표시 중이던 전투 행을 비우고 새 로스터 프리뷰로 갈아끼운다
        }

        // Drives the target-info bar + combat timer: count of DISPLAYABLE combat rows (roster-resurface → 0).
        hasCombatRows = entries.Count > 0;

        // Pre-combat / party-changed preview: 프레시 리포트(Information 빔 + ExecutorId 0) 또는 파티가 바뀐 프리즌
        // 전투(rosterChanged). 둘 다 idle 0-DPS 로스터(App이 self 주입)를 보여주고, 라이브 전투 행 위로는 절대 안 뜬다.
        if (showPreCombatRoster && roster.Count > 0
            && ((report.Information.Count == 0 && report.ExecutorId == 0) || rosterChanged))
        {
            entries.AddRange(roster.Select(m => new Row(m.Id, new DpsInformation(), m, false)));
        }

        // 표시 상한. 확인된 파티원(0x9702)과 본인은 상한에서 제외한다 — 낯선 사람이나 소환수 주인 팬텀이
        // 한 줄 끼었다고 실제 파티원이 밀려나면 안 된다(그게 "간헐적으로 한 명씩 사라진다"의 한 갈래였다).
        // 순서는 metric 정렬 그대로 유지한다(예외 대상을 뒤에 몰아붙이지 않는다).
        var display = new List<Row>();
        for (int i = 0; i < entries.Count; i++)
        {
            Row e = entries[i];
            // raw 로스터도 면제 근거로 인정한다: tier 2가 방금 이름을 붙인 행은 정의상 uid가 해석되지 않은
            // 멤버라 IsPartyMember(resolved)로는 절대 걸리지 않고, 그러면 구제해 놓고 상한에서 잘려 도로
            // 사라진다. 최악의 부작용은 행이 몇 개 더 보이는 것뿐이다.
            // 복구로 이름을 붙인 행(0x9200 member-profile / tier 1·2 / stale-name 교정)은 정의상 파티원이다.
            // 그중 member-profile로 살린 행은 0x9702 resolved party에도 raw roster에도 없을 수 있어(그게 이
            // 소스의 존재 이유다) 상한에서 잘려 도로 사라질 수 있으므로 함께 면제한다.
            bool exempt = IsSelf(e.Uid, e.User)
                          || (rosterConfirmed && IsPartyMember(e.User, party))
                          || IsRawPartyMember(e.User, rawParty)
                          || partyRecovered.ContainsKey(e.Uid);
            if (i < topN || exempt)
            {
                display.Add(e);
            }
        }

        // Stamp IsSelf for the rows the VM will render (after the value-equality Contains check above).
        for (int i = 0; i < display.Count; i++)
        {
            Row r = display[i];
            display[i] = r with { IsSelf = IsSelf(r.Uid, r.User) };
        }

        return display;
    }

    /// <summary>Party-context guard for lost-executor recovery (guard 6). Returns true when some NAMED damaging
    /// contributor — other than the recovery candidate or 본인 — is NOT a member of the authoritative
    /// (0x9702-only) party, which marks the fight as a PUBLIC scene (field-boss zerg / nearby players) rather
    /// than a private dungeon party. Matches party membership by uid OR (nickname, server) identity, so a
    /// re-instanced party member whose new uid hasn't reconciled still counts as party. A solo dungeon (no other
    /// named damager) and a party dungeon (every named damager is your party) both return false → recovery runs.
    /// <paramref name="authoritativeParty"/> == null disables the guard (unit tests / callers that don't supply
    /// it) — the live App always passes the 0x9702 party (never the display roster, which folds in recent combat
    /// contributors that at a field boss would themselves be the zerg).</summary>
    /// <summary>True when <paramref name="u"/> is a member of the authoritative party — by uid, or by
    /// (nickname, server) identity (so a re-instanced member whose new uid hasn't reconciled still counts).</summary>
    private static bool IsPartyMember(User? u, IReadOnlyList<User>? party)
    {
        if (u is null || party is null)
        {
            return false;
        }

        foreach (User m in party)
        {
            if (m.Id == u.Id
                || (!string.IsNullOrWhiteSpace(u.Nickname)
                    && string.Equals(m.Nickname, u.Nickname, StringComparison.Ordinal) && m.Server == u.Server))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="u"/>'s (nickname, server) appears in the RAW 0x9702 roster. Used ONLY
    /// for the display-cap exemption — the raw roster has no uids, so it can never answer "is this uid party?",
    /// and it is deliberately NOT fed to the outsider guard (a raw-only named damager measures 0 on the corpus,
    /// and widening that guard is how a genuine stranger gets read as a party member).</summary>
    private static bool IsRawPartyMember(User? u, IReadOnlyList<(string Nickname, int Server, int Slot)> rawParty)
    {
        if (u is null || string.IsNullOrWhiteSpace(u.Nickname))
        {
            return false;
        }

        foreach ((string nickname, int server, int _) in rawParty)
        {
            if (string.Equals(nickname, u.Nickname, StringComparison.Ordinal) && server == u.Server)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasNamedOutsider(
        IReadOnlyList<Row> rawCombat, int candidateUid, int selfId, IReadOnlyList<User>? authoritativeParty,
        int alsoExcludeUid = -1)
    {
        if (authoritativeParty is null)
        {
            return false;
        }

        // 판정 자체는 Data 계층의 SelfRecoveryGuards 에 있다 — 데이터층의 로스터 1:1 본인 복구
        // (DpsCalculator.TryRecoverExecutorFromRoster) 가 같은 질문을 하는데, 종전에는 이 가드가 표시층에만
        // 있어서 화면에서는 거부된 조합이 신원 저장소에는 영구 기록됐다(identity-roster~S2). 한 곳에 둬야
        // 두 층이 같은 답을 낸다.
        return SelfRecoveryGuards.HasNamedOutsider(
            rawCombat.Select(e => (e.Uid, e.User?.Nickname, e.User?.Server ?? 0)).ToList(),
            authoritativeParty.Select(m => (m.Id, m.Nickname, m.Server)).ToList(),
            candidateUid, selfId, alsoExcludeUid);
    }
}
