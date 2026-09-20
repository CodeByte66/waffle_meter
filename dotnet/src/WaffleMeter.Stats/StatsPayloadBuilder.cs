using System.Globalization;
using WaffleMeter.Capture;
using WaffleMeter.Data;

namespace WaffleMeter.Stats;

/// <summary>Result of building an upload payload: either a payload or a skip reason.</summary>
public abstract record BuildResult
{
    private BuildResult()
    {
    }

    public sealed record Payload(StatsUploadPayload Value) : BuildResult;

    public sealed record Skip(string Reason) : BuildResult;
}

/// <summary>
/// Verbatim port of Kotlin <c>stats.StatsPayloadBuilder</c>: turns a finished <see cref="DpsLog"/>
/// into the anonymized upload payload (own character + participants + skills + buffs/debuffs), or a
/// skip reason. Resolves each contributor's combat power (live snapshot -&gt; name/server match -&gt;
/// official lookup) and tags buffs with self/other/party source. The "public" flag and clock are
/// injected to break the cycle with the consent manager and keep it testable.
/// </summary>
public sealed class StatsPayloadBuilder
{
    private static readonly HashSet<JobClass> SynergyJobs = new()
    {
        JobClass.TEMPLAR, JobClass.GLADIATOR, JobClass.CHANTER, JobClass.CLERIC,
    };

    /// <summary>How stale the 0x9702 roster snapshot may be before its combat-power numbers stop counting as
    /// this battle's. Uploads normally run within seconds of a kill, so this only has to survive a queued
    /// retry; past it the fallback simply does not fire and the official lookup takes over as before.</summary>
    private const long RosterPowerTtlMs = 30L * 60 * 1000;

    private readonly DataManager _data;
    private readonly Func<bool> _publicCharacter;
    private readonly Func<long> _clock;

    public StatsPayloadBuilder(DataManager data, Func<bool> publicCharacterProvider, Func<long>? clock = null)
    {
        _data = data;
        _publicCharacter = publicCharacterProvider;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public StatsOwnCharacter OwnCharacter()
    {
        int id = _data.ExecutorId();
        User? user = _data.User(id);
        if (user == null)
        {
            return new StatsOwnCharacter(false);
        }

        User resolved = ResolveUserSnapshot(user);
        return new StatsOwnCharacter(
            Detected: !string.IsNullOrWhiteSpace(resolved.Nickname),
            Id: resolved.Id,
            Nickname: resolved.Nickname,
            Server: resolved.Server,
            Job: resolved.Job?.ClassName(),
            Power: resolved.Power);
    }

    public BuildResult Build(DpsLog log, string clientVersion, bool killConfirmed)
    {
        DpsReport report = log.Report;
        MobInfo? target = report.Target;
        if (target == null)
        {
            return new BuildResult.Skip("target_missing");
        }

        Mob mob = target.Mob;
        if (!mob.Boss || mob.IsDummy)
        {
            return new BuildResult.Skip("not_uploadable_boss");
        }

        if (!killConfirmed)
        {
            return new BuildResult.Skip("not_kill");
        }

        // 이 전투에 얼어붙은 본인 id를 우선한다. 업로드는 처치 후(StatsUploadQueue의 재확인 지연) 실행되는데,
        // 그 사이 존 이동으로 본인이 새 엔티티 id로 옮겨가면 라이브 ExecutorId()는 이 전투에 없던 uid를 가리키고,
        // 아래 own_result_missing으로 정상 처치가 무증상 누락된다. 리포트의 ExecutorId는 전투 종료 시점에
        // 동결된 값이라 그 레이스가 없다.
        int ownId = report.ExecutorId != 0 ? report.ExecutorId : _data.ExecutorId();
        if (ownId == 0)
        {
            return new BuildResult.Skip("executor_missing");
        }

        User? own = report.Contributors.FirstOrDefault(u => u.Id == ownId) ?? _data.User(ownId);
        if (own == null)
        {
            return new BuildResult.Skip("own_character_missing");
        }

        string? ownNickname = NonBlank(own.Nickname);
        if (ownNickname == null)
        {
            return new BuildResult.Skip("own_nickname_missing");
        }

        if (!report.Information.ContainsKey(own.Id))
        {
            return new BuildResult.Skip("own_result_missing");
        }

        DpsInformation ownInfo = report.Information[own.Id];
        if (ownInfo.Amount <= 0.0)
        {
            return new BuildResult.Skip("own_damage_empty");
        }

        long duration = report.BattleEnd - report.BattleStart;
        if (duration <= 0)
        {
            return new BuildResult.Skip("invalid_duration");
        }

        List<User> contributors = ResolveContributors(report.Contributors);
        User resolvedOwn = contributors.FirstOrDefault(u => u.Id == own.Id) ?? own;

        var identityHashCache = new Dictionary<int, string?>();
        string? ActorIdentity(int actorId)
        {
            if (identityHashCache.TryGetValue(actorId, out string? cached))
            {
                return cached;
            }

            User? actor = contributors.FirstOrDefault(u => u.Id == actorId) ?? _data.User(actorId);
            string? nickname = NonBlank(actor?.Nickname);
            string? resolved = actor != null && nickname != null
                ? StatsIdentity.CharacterIdentityHash(actor.Server, nickname)
                : null;
            identityHashCache[actorId] = resolved;
            return resolved;
        }

        // 같은 캐릭터가 두 uid로 들어온 경우를 여기서 접는다(FoldParticipants 참조). 이 아래로는 uid가 아니라
        // '대표 uid' 기준이며, 본인 성적도 접힌 값을 쓴다 — 안 그러면 자기 딜이 한쪽 uid 몫만 올라간다.
        FoldedParticipants folded = FoldParticipants(log, SortedParticipantUsers(log, contributors));
        List<User> participantUsers = folded.Representatives;
        int ownRepresentativeId = folded.RepresentativeOf.GetValueOrDefault(own.Id, own.Id);
        DpsInformation ownFoldedInfo = folded.Information.GetValueOrDefault(ownRepresentativeId) ?? ownInfo;

        long totalDamage = RoundToLong(ownFoldedInfo.Amount);
        Dictionary<string, AnalyzedSkill> ownSkills = folded.Skills.GetValueOrDefault(ownRepresentativeId)
            ?? log.SkillDetails.GetValueOrDefault(own.Id)
            ?? new Dictionary<string, AnalyzedSkill>();
        List<StatsSkillPayload> skillPayloads = BuildSkillPayloads(ownSkills, totalDamage);
        RateSummary resultRates = SummarizeRates(ownSkills.Values);

        // 접힌 uid도 대표의 인덱스를 가리켜야 버프의 시전자 참조가 끊기지 않는다.
        var indexByRepresentative = new Dictionary<int, int>();
        for (int i = 0; i < participantUsers.Count; i++)
        {
            indexByRepresentative[participantUsers[i].Id] = i;
        }

        var participantIndexById = new Dictionary<int, int>();
        foreach (KeyValuePair<int, int> alias in folded.RepresentativeOf)
        {
            if (indexByRepresentative.TryGetValue(alias.Value, out int index))
            {
                participantIndexById[alias.Key] = index;
            }
        }

        List<StatsParticipantPayload> participantPayloads = BuildParticipantPayloads(log, ownRepresentativeId, folded, participantIndexById, ActorIdentity);
        if (resolvedOwn.Power <= 0)
        {
            return new BuildResult.Skip("own_power_unresolved");
        }

        // 참가자 한 명의 전투력을 못 읽었다고 전투를 통째로 버리던 자리. 이제 그 참가자의 Power 만 null 로
        // 나가고(BuildParticipantPayloads) 서버가 그 사람만 집계에서 뺀다 — 전투 자체와 나머지 참가자 기록은 산다.
        // 🔑 참가자를 배열에서 빼는 것으로 대신하면 안 되는 이유는 StatsParticipantPayload.Power 주석 참고.

        string? ownIdentityHash = StatsIdentity.CharacterIdentityHash(own.Server, ownNickname);
        if (ownIdentityHash == null)
        {
            return new BuildResult.Skip("own_identity_missing");
        }

        // 인원 세 곳(partySize·jobs·synergy)은 전부 <b>접힌</b> 대표 기준이다. 접기 전 기여자로 세면 재등록된
        // 한 사람이 두 번 세어져 ①정원이 부풀고 ②그 사람의 직업이 두 번 들어가 시너지 구성이 틀린다. 게다가 웹의
        // 중복 병합 그룹 키가 partySize와 jobs 조합을 쓰므로(battle-group.ts), 같은 전투를 올린 두 미터의 키가
        // 갈려 하나로 안 묶인다 — 접으면 양쪽이 같은 값에 수렴한다.
        // 값이 실제로 달라지는 건 중복이 있을 때뿐이다: 운영 실측(7일 12.7만건)에서 partySize가 참가자 수보다
        // 큰 리포트는 0건, 즉 딜 0인 기여자는 없다.
        Dictionary<string, int> jobCounts = folded.Representatives
            .Where(u => u.Job != null)
            .Select(u => u.Job!.Value.ClassName())
            .GroupBy(name => name)
            .ToDictionary(g => g.Key, g => g.Count());
        StatsSynergyPayload synergy = BuildSynergy(folded.Representatives);
        string battleHash = BattleHash(own.Server, ownNickname, mob.Code, report.BattleStart, report.BattleEnd, totalDamage, duration);

        // ── Combat-detail DPS graph sources (frozen at save time, so present here — but omit if a report somehow
        //    lacks them, e.g. an old/pre-freeze snapshot). ──
        // 시계열은 v6부터 참가자 <b>전원</b>이 각자 싣는다(BuildParticipantPayloads). 이 최상위 필드는 업로더 몫으로
        // 남는다 — v5까지만 읽는 웹이 업로더 그래프를 잃지 않게 하는 하위호환 자리이고, 같은 소스·같은 다운샘플을
        // 거치므로 참가자 행과 항상 같은 값이다.
        // ⚠️ 접기 전 own.Id가 아니라 <b>대표 uid</b>로 읽는다. 본인이 재등록돼 두 uid로 갈린 전투에서 예전 코드는
        //    최상위 Result·Skills·Buffs 는 접힌 합인데 그래프만 한쪽 uid 몫이라, 같은 payload 안에서
        //    sum(dpsSeries.damage) != result.totalDamage 였다.
        StatsDpsSeriesPayload? dpsSeries = BuildSeriesPayload(folded.Series.GetValueOrDefault(ownRepresentativeId));

        // 버프 인터벌은 아직 업로더 전용이고 접기 전 uid를 쓴다. 참가자별 버프 레인은 웹 UI가 "내 버프" 전제로
        // 만들어져 있어 이번 슬라이스 밖이다 — 손대려면 시전자 필터를 참가자별로 일반화하는 게 먼저다.
        IReadOnlyList<StatsSelfBuffIntervalPayload>? selfBuffIntervals = null;
        if (report.BuffIntervals.GetValueOrDefault(own.Id) is { Count: > 0 } ownTimelines)
        {
            long battleStart = report.BattleStart;
            int? ownPrefix = resolvedOwn.Job is { } job ? job.BasicSkillCode() / 1_000_000 : null;
            List<StatsSelfBuffIntervalPayload> built = ownTimelines
                // BuffSource == "self": the uploader's own-class(딜) buffs only — excludes consumables
                // (EffectiveJobPrefix 0) and other players' buffs (ActorId != own). Mirrors DetailModel.BuildOwnBuffs.
                .Where(t => t.ActorId == own.Id && ownPrefix is int p && t.EffectiveJobPrefix != 0 && t.EffectiveJobPrefix == p)
                .Select(t => new StatsSelfBuffIntervalPayload(
                    t.BaseCode,
                    t.Name,
                    t.Spans
                        .SelectMany(s => new[] { (int)((s.Start - battleStart) / 1000), (int)((s.End - battleStart) / 1000) })
                        .ToList()))
                .ToList();
            selfBuffIntervals = built.Count > 0 ? built : null;
        }

        var payload = new StatsUploadPayload(
            // v6 = participants[].dpsSeries (전원 DPS 추이 그래프). ⚠️ 웹의 zod가 스키마 번호를 리터럴 유니온으로
            // 못박고 있고 미터는 4xx를 재시도하지 않는다 — 웹이 6을 받아들이기 전에 이 빌드를 내보내면 그 전투들은
            // 400 invalid_schema로 <b>영구 소실</b>된다. 배포 순서: 웹 먼저, 미터 나중.
            SchemaVersion: 6,
            ClientVersion: clientVersion,
            BattleHash: battleHash,
            IdentityHashVersion: StatsIdentity.IdentityHashVersion,
            ConsentVersion: StatsConsentManager.ConsentVersion,
            UploadedAt: _clock(),
            // character.public is informational only: the server IGNORES it on /reports (§2.3, fail-closed) —
            // an upload never makes a character public. Going public happens solely through the consent accept
            // path with a valid grant (§2.4). We send the current local flag for parity but never rely on it.
            Character: new StatsCharacterPayload(
                ownIdentityHash,
                ownNickname,
                own.Server,
                resolvedOwn.Job?.ClassName(),
                resolvedOwn.Power,
                _publicCharacter()),
            Encounter: BuildEncounterPayload(mob, report),
            Battle: new StatsBattlePayload(
                report.BattleStart,
                report.BattleEnd,
                duration,
                participantPayloads.Count, // 접힌 인원 — 위 jobCounts 주석 참조
                report.PartyRosterSize > 0 ? report.PartyRosterSize : null),
            PartyComposition: new StatsPartyCompositionPayload(jobCounts, synergy),
            Participants: participantPayloads,
            // ⚠️ 최상위 Result·Buffs 도 접힌 값을 써야 한다. 참가자 행은 접힌 합인데 여기만 한쪽 uid 몫이면
            // 같은 전투에서 업로더의 숫자가 두 군데서 다르게 나간다(skillPayloads/resultRates는 이미 접힌 값).
            Result: BuildResultPayload(ownFoldedInfo, resultRates, CountJudgments(ownSkills.Values)),
            Skills: skillPayloads,
            Buffs: (folded.Buffs.GetValueOrDefault(ownRepresentativeId)
                    ?? log.BuffRates.GetValueOrDefault(own.Id)
                    ?? new List<OperatingData>())
                .Select(v => ToBuffPayload(
                    v, "participant", "buff", ownRepresentativeId, resolvedOwn.Job,
                    IndexOrNull(participantIndexById, ownRepresentativeId),
                    IndexOrNull(participantIndexById, v.ActorId),
                    ActorIdentity(v.ActorId)))
                .ToList(),
            BossDebuffs: log.BossBuffRates
                .Select(v => ToBuffPayload(
                    v, "boss", "debuff", null, null, null,
                    IndexOrNull(participantIndexById, v.ActorId),
                    ActorIdentity(v.ActorId)))
                .ToList(),
            DpsSeries: dpsSeries,
            SelfBuffIntervals: selfBuffIntervals,
            SelfJudgment: BuildSelfJudgmentPayload(report.SelfJudgment));

        return new BuildResult.Payload(payload);
    }

    /// <summary>Whether this battle's boss belongs to a 공대 dungeon, or null when the shipped catalog cannot
    /// say (not loaded, or a boss it does not know).</summary>
    private bool? RaidByEncounter(DpsLog log) =>
        log.Report.Target is { } target ? _data.Encounters.Lookup(target.Mob.Code)?.IsRaid : null;

    private List<User> SortedParticipantUsers(DpsLog log, IEnumerable<User> contributors)
    {
        return contributors
            .Where(u => AmountOf(log, u.Id) > 0.0)
            .OrderByDescending(u => AmountOf(log, u.Id))
            .ToList();
    }

    /// <summary>같은 캐릭터가 여러 uid로 들어온 것을 하나로 접은 결과. <see cref="RepresentativeOf"/>는
    /// <b>모든</b> uid(대표 포함)를 대표 uid로 보낸다.</summary>
    private sealed record FoldedParticipants(
        List<User> Representatives,
        Dictionary<int, int> RepresentativeOf,
        Dictionary<int, DpsInformation> Information,
        Dictionary<int, Dictionary<string, AnalyzedSkill>> Skills,
        Dictionary<int, List<OperatingData>> Buffs,
        Dictionary<int, int> PartySlots,
        Dictionary<int, long[]> Series,
        Dictionary<int, DpsMetricResult> Metrics,
        /// <summary>대표 uid 가 둘 이상의 uid 를 흡수했는지. true 면 nDPS/rDPS 를 보내지 않는다(합산 불가).</summary>
        Dictionary<int, bool> MetricsFolded);

    /// <summary>
    /// 같은 캐릭터가 두 uid로 참가자에 들어온 경우를 하나로 접는다.
    /// <para>🔑 왜: executor는 존/인스턴스 로드마다 새 uid로 재등록되는데, 그 경계에서 한 전투의 데미지가 두
    /// uid에 걸치면 <b>같은 사람이 참가자 목록에 두 번</b> 올라간다. 실측(2026-08-09 운영 DB): 10인 공대 리포트
    /// 중 참가자가 로스터를 넘는 11건이 있었고 그중 2건이 이 중복이었다 — 한쪽은 실제 딜(1.1억/1,302타),
    /// 다른 쪽은 <c>딜 1 / 1타</c>였다. 전 참가자 행 기준으로는 2.9.3에서 146,527행 중 2행으로 드물다.</para>
    /// <para>피해는 드문 것보다 크다: ①웹은 참가자 <b>전원</b>이 슬롯을 가져야 서브파티를 신뢰하는데,
    /// 로스터 매칭이 두 uid 중 <b>먼저 만난 쪽</b>에만 슬롯을 붙여 나머지 한 행이 슬롯 없이 남는다(그 리포트는
    /// 통째로 '구분 이전 지표'가 된다) ②참가자 수가 정원을 넘어 웹의 소거법도 못 쓴다 ③그 캐릭터의 성적이
    /// 두 행으로 쪼개져 보인다.</para>
    /// <para>접는 키는 <b>신원 해시</b>(server|nickname)다 — 웹이 참가자를 식별하는 키와 같아야 하기 때문이다.
    /// 닉네임이나 서버가 없어 해시를 못 만드는 행은 접지 않는다(서로 다른 사람일 수 있다). 대표는 <b>딜이 가장
    /// 많은 uid</b>이고(호출부가 이미 내림차순으로 넘긴다) 숫자·스킬·버프·초당 시계열은 전부 합산한다 — 버리는 값이 없다.
    /// 슬롯은 접힌 uid 중 하나라도 갖고 있으면 대표가 물려받는다(①의 직접 해소).</para>
    /// </summary>
    private FoldedParticipants FoldParticipants(DpsLog log, List<User> damageSorted)
    {
        var representatives = new List<User>();
        var representativeOf = new Dictionary<int, int>();
        var byIdentity = new Dictionary<string, int>(StringComparer.Ordinal);
        var information = new Dictionary<int, DpsInformation>();
        var skills = new Dictionary<int, Dictionary<string, AnalyzedSkill>>();
        var buffs = new Dictionary<int, List<OperatingData>>();
        var partySlots = new Dictionary<int, int>();
        var series = new Dictionary<int, long[]>();
        var metrics = new Dictionary<int, DpsMetricResult>();
        var metricFolded = new Dictionary<int, bool>();

        foreach (User user in damageSorted)
        {
            string? identity = NonBlank(user.Nickname) is { } nickname
                ? StatsIdentity.CharacterIdentityHash(user.Server, nickname)
                : null;

            int representative;
            if (identity != null && byIdentity.TryGetValue(identity, out int existing))
            {
                representative = existing;
            }
            else
            {
                representative = user.Id;
                if (identity != null)
                {
                    byIdentity[identity] = representative;
                }

                representatives.Add(user);
                information[representative] = new DpsInformation();
                skills[representative] = new Dictionary<string, AnalyzedSkill>(StringComparer.Ordinal);
                buffs[representative] = new List<OperatingData>();
            }

            representativeOf[user.Id] = representative;

            if (log.Report.Information.TryGetValue(user.Id, out DpsInformation? source))
            {
                DpsInformation target = information[representative];
                target.Amount += source.Amount;
                target.Dps += source.Dps;                            // 같은 전투 길이를 나눈 값이라 더해도 정합
                target.Contribution += source.Contribution;          // 비중은 합이 곧 그 캐릭터의 몫
                target.EntireContribution += source.EntireContribution;
            }

            MergeSkillsInto(skills[representative], log.SkillDetails.GetValueOrDefault(user.Id));
            if (log.BuffRates.GetValueOrDefault(user.Id) is { } rates)
            {
                buffs[representative].AddRange(rates);
            }

            if (log.Report.PartySlots.TryGetValue(user.Id, out int slot) && !partySlots.ContainsKey(representative))
            {
                partySlots[representative] = slot;
            }

            // 초당 시계열도 합산 대상이다. 안 접으면 그 행의 Result.TotalDamage 는 두 uid의 합인데 그래프는 한쪽
            // 몫이라, 같은 참가자 행 안에서 sum(damage) != totalDamage 가 된다(웹이 바로 잡아낼 수 있는 모순).
            // 🚨 저장된 배열을 제자리에서 더하지 않는다. DataManager.SaveBattleLog 는 이 dict와 배열을 <b>참조로</b>
            //    넘겨서 히스토리 패널·현재 표시 중인 리포트·리플레이 엔진이 같은 인스턴스를 보고 있고, 이 빌더는
            //    업로드 워커 스레드에서 돈다 — 제자리 수정은 곧 남의 화면 오염이자 데이터 레이스다. 실제로 합칠
            //    때만 새 배열을 만든다(중복 자체가 드물어 평상시엔 복사가 0회다).
            if (log.Report.DpsSeries.GetValueOrDefault(user.Id) is { Length: > 0 } frozenSeries)
            {
                series[representative] = series.TryGetValue(representative, out long[]? mergedSoFar)
                    ? AddSeries(mergedSoFar, frozenSeries)
                    : frozenSeries;
            }

            // nDPS/rDPS 는 <b>합산할 수 없다</b>. Dps 는 모든 uid 가 같은 전투 길이로 나눈 값이라 부분의 합이
            // 전체지만, ndps = ownDps / (1 + totalGain) 는 그렇지 않다 — 한 캐릭터가 두 uid 로 갈리면 버프
            // 가동률도 함께 갈려 각 조각의 분모가 따로 놀고, 더하면 실제보다 커진다. 정확한 값을 내려면 접은
            // 뒤에 다시 계산해야 하는데 그러려면 버프 구간이 필요하고, 이 빌더는 그걸 들고 있지 않다.
            //
            // 그래서 갈린 참가자에게는 <b>아무 값도 보내지 않는다</b>(아래에서 null). 틀린 줄 아는 숫자를 보내는
            // 것보다 낫고, 실측상 매우 드물다(2.9.3 기준 146,527 참가자 행 중 2행).
            if (log.Report.DpsMetrics.TryGetValue(user.Id, out DpsMetricResult m))
            {
                metricFolded[representative] = metrics.ContainsKey(representative);
                metrics[representative] = m;
            }
        }

        return new FoldedParticipants(
            representatives, representativeOf, information, skills, buffs, partySlots, series, metrics, metricFolded);
    }

    /// <summary>두 초당 배열을 <b>새</b> 배열에 원소별로 더한다(입력은 건드리지 않는다). 같은 전투 창에서 만들어져
    /// 길이가 같지만(GetDpsSeries 는 BattleStart/BattleEnd 로만 길이를 정한다) 방어적으로 긴 쪽에 맞춘다.</summary>
    private static long[] AddSeries(long[] first, long[] second)
    {
        long[] merged = new long[Math.Max(first.Length, second.Length)];
        Array.Copy(first, merged, first.Length);
        for (int i = 0; i < second.Length; i++)
        {
            merged[i] += second[i];
        }

        return merged;
    }

    /// <summary>스킬 분해를 대상 사전에 더한다. 카운터는 전부 가산이고, 스킬코드·특화(RawSkillCode)·이름은
    /// 같은 캐릭터의 같은 스킬이라 먼저 채워진 값을 유지한다.</summary>
    private static void MergeSkillsInto(
        Dictionary<string, AnalyzedSkill> target,
        Dictionary<string, AnalyzedSkill>? source)
    {
        if (source == null)
        {
            return;
        }

        foreach (KeyValuePair<string, AnalyzedSkill> entry in source)
        {
            if (!target.TryGetValue(entry.Key, out AnalyzedSkill? merged))
            {
                target[entry.Key] = entry.Value.Copy();
                continue;
            }

            AnalyzedSkill add = entry.Value;
            merged.DamageAmount += add.DamageAmount;
            merged.DotDamageAmount += add.DotDamageAmount;
            merged.DotTimes += add.DotTimes;
            merged.CritTimes += add.CritTimes;
            merged.Times += add.Times;
            merged.BackTimes += add.BackTimes;
            merged.FrontTimes += add.FrontTimes;
            merged.PerfectTimes += add.PerfectTimes;
            merged.DoubleTimes += add.DoubleTimes;
            merged.ParryTimes += add.ParryTimes;
            merged.ShardTimes += add.ShardTimes;
            merged.MultiHitTimes += add.MultiHitTimes;
            merged.FlaggedTimes += add.FlaggedTimes;
            merged.EligibleDamage += add.EligibleDamage;
            merged.SummonTimes += add.SummonTimes;
            merged.SummonFlaggedTimes += add.SummonFlaggedTimes;
            merged.SummonDoubleTimes += add.SummonDoubleTimes;
            merged.SummonPerfectTimes += add.SummonPerfectTimes;
            if (merged.RawSkillCode == 0)
            {
                merged.RawSkillCode = add.RawSkillCode;
            }

            merged.Name ??= add.Name;
        }
    }

    private List<StatsParticipantPayload> BuildParticipantPayloads(
        DpsLog log,
        int ownId,
        FoldedParticipants folded,
        Dictionary<int, int> participantIndexById,
        Func<int, string?> actorIdentity)
    {
        List<User> participants = folded.Representatives;
        var result = new List<StatsParticipantPayload>();
        // Tag each participant's sub-party slot for a 공대 so the stats site can split raid synergy. Two sizes
        // exist: 8-인 (two parties of 4) and 10-인 (two parties of 5, since the 2026-07-01 patch).
        //
        // WHICH DUNGEON THIS IS decides whether it is a raid — not how many people we counted. The boss mobCode
        // names the dungeon exactly (that is what the encounter catalog is for), and in the client's own table
        // every 성역 is a 10-인 Raid while every 원정 and 초월 is a Party capped at five — 바크론 시련 included.
        //
        // Counting was wrong in both directions. A roster left over from earlier content tagged a four-man
        // dungeon as a raid (measured: a fresh 4-man party's snapshots were ignored three times running while
        // the roster still held the previous 10-man raid, so those battles would have uploaded isRaid=true).
        // And a real raid whose 0x9702 snapshot under-parsed — 9 of 10 members, 62 such snapshots in the corpus
        // — stopped being a raid and silently dropped every sub-party tag it did have.
        //
        // The count-based test survives only as the fallback for when the catalog cannot answer: it is absent
        // (asset missing → EncounterCatalog.Empty) or the boss is not in it. A battle with an unknown boss
        // never uploads anyway (the queue's unsupported_encounter gate), so in practice this is the no-catalog
        // case, where preserving the old behaviour beats inventing a new one.
        bool isRaid = RaidByEncounter(log)
            ?? (log.Report.PartyRosterSize is 8 or 10
                || (log.Report.PartyRosterSize == 0 && log.Report.PartySlots.Values.Any(s => s > 5)));
        foreach (User user in participants)
        {
            if (!folded.Information.TryGetValue(user.Id, out DpsInformation? info))
            {
                continue;
            }

            long totalDamage = RoundToLong(info.Amount);
            int? partySlot = isRaid && folded.PartySlots.TryGetValue(user.Id, out int slot) ? slot : null;
            // Deliberately NOT derived here. The sub-party boundary depends on the raid size — 4 for an 8-인
            // 공대, 5 for a 10-인 — and this builder cannot know it reliably (battle.partySize below is the
            // number of people who DEALT DAMAGE, not the roster size). The old formula hardcoded /5, so every
            // 8-인 공대 sent slot 5 as party 1 while the site computes party 2; the site's consistency check
            // then rejected the sub-party split for the WHOLE battle even though all 8 slots were correct.
            // Sending null loses nothing: the site derives partyNumber from partySlot itself once it knows the
            // raid size, and its check passes when this field is absent.
            int? partyNumber = null;
            Dictionary<string, AnalyzedSkill> skills = folded.Skills.GetValueOrDefault(user.Id) ?? new Dictionary<string, AnalyzedSkill>();
            RateSummary rates = SummarizeRates(skills.Values);
            string? identityHash = NonBlank(user.Nickname) is { } nickname
                ? StatsIdentity.CharacterIdentityHash(user.Server, nickname)
                : null;

            result.Add(new StatsParticipantPayload(
                IdentityHash: identityHash,
                IsUploader: user.Id == ownId,
                PartyNumber: partyNumber,
                PartySlot: partySlot,
                Job: user.Job?.ClassName(),
                // 못 읽었으면 0 이 아니라 null 이다 — 0 은 값이고 null 은 "모른다"다. 서버는 NULL 을
                // 파티 편차에서 무시하고 `power >= 400000` 게이트로 그 참가자만 배제한다.
                Power: user.Power > 0 ? user.Power : null,
                Result: BuildResultPayload(info, rates, user.Id == ownId ? CountJudgments(skills.Values) : null),
                Skills: BuildSkillPayloads(skills, totalDamage),
                Buffs: (folded.Buffs.GetValueOrDefault(user.Id) ?? new List<OperatingData>())
                    .Select(v => ToBuffPayload(
                        v, "participant", "buff", user.Id, user.Job,
                        IndexOrNull(participantIndexById, user.Id),
                        IndexOrNull(participantIndexById, v.ActorId),
                        actorIdentity(v.ActorId)))
                    .ToList(),
                // 이 사람의 초당 피해 시계열. 계산·동결은 이미 전원분으로 돌고 있었고(DpsCalculator.BuildDpsSeries가
                // 기여자 전체를 돈다) payload가 업로더 것만 꺼내 쓰느라 버려지던 값이다 — 웹 전투상세가 파티원
                // 전원에게 DPS 추이 그래프를 그릴 수 있게 하는 유일한 소스. 없으면 null(빈 배열 아님).
                DpsSeries: BuildSeriesPayload(folded.Series.GetValueOrDefault(user.Id)),
                // 레벨을 반영한 nDPS/rDPS. 웹도 같은 두 숫자를 ingest 시점에 계산하지만, 웹의 계수표에는 시전자
                // 스킬 레벨이 들어갈 자리가 없다(웹 소스 자신이 그렇게 적어 두었다). 없으면 null.
                Metrics: folded.MetricsFolded.GetValueOrDefault(user.Id)
                    ? null
                    : BuildMetricsPayload(folded.Metrics, user.Id)));
        }

        return result;
    }

    /// <summary>참가자 한 명이 실어 보내는 시계열 샘플 수 상한. 웹 스키마는 3,600까지 받지만 그건 <b>업로더 한 명</b>
    /// 기준이라 16명 × 3,600 = 5.7만 샘플이 되면 본문 1MB 한도에 붙는다. 웹 표시는 어차피 3초 버킷이라 900샘플이면
    /// 약 15분 전투까지 step=1 무손실이고, 그보다 긴 전투만 접힌다.</summary>
    /// <summary>이 참가자의 nDPS/rDPS를 payload 모양으로. 계산이 안 된 전투(버프 정보 없음/길이 0)면 null —
    /// 0을 실어 보내면 웹이 "버프를 하나도 못 받았다"로 읽는다.</summary>
    private static StatsDpsMetricsPayload? BuildMetricsPayload(
        Dictionary<int, DpsMetricResult> metrics, int uid) =>
        metrics.TryGetValue(uid, out DpsMetricResult m)
            ? new StatsDpsMetricsPayload(RoundToLong(m.Ndps), RoundToLong(m.Rdps), m.GrantedDamage)
            : null;

    private const int MaxSeriesSamples = 900;

    /// <summary>웹 zod가 <c>step</c>에 걸어둔 상한. 정상 전투는 근처도 못 간다(step 60 = 15시간 창). 하지만 전투
    /// 창 길이는 이 빌더가 정하는 값이 아니라 BattleStart~BattleEnd 실측이고, 이 미터는 전투가 안 닫히는 결함을
    /// 여러 번 겪었다 — 창이 비정상적으로 길어지면 step이 상한을 넘어 payload가 통째로 400나고, 4xx는 재시도가
    /// 없으니 <b>그 전투가 영구 소실</b>된다. 여기서 잘라 앞부분만 그래프로 남기고 전투 자체는 살린다.</summary>
    private const int MaxSeriesStep = 60;

    /// <summary>초당 피해 배열을 payload 시계열로 만든다. 스냅샷이 없으면 null — <b>빈 배열을 보내지 않는다</b>
    /// (StatsJson은 null만 생략하고 <c>[]</c>는 그대로 쓴다).
    /// <para><see cref="MaxSeriesSamples"/>를 넘으면 N초 버킷의 <b>합</b>으로 접고 <c>Step=N</c>을 실어 웹이 그대로
    /// 해석하게 한다 — 웹은 합/(샘플수×step)으로 DPS를 만들므로 여기에 평균을 넣으면 표시 DPS가 1/N로 준다.
    /// 마지막 불완전 버킷은 <b>버린다</b>: 잔여 초를 그대로 실으면 웹이 그것도 step초로 간주해 마지막 점이 과소
    /// 표시된다. 대신 그래프 길이가 최대 step-1초 짧아지는데, 15분 넘는 전투에서만 생기는 오차다.</para></summary>
    private static StatsDpsSeriesPayload? BuildSeriesPayload(long[]? series)
    {
        if (series is not { Length: > 0 })
        {
            return null;
        }

        int step = Math.Min((series.Length + MaxSeriesSamples - 1) / MaxSeriesSamples, MaxSeriesStep);
        if (step <= 1)
        {
            return new StatsDpsSeriesPayload(Step: 1, Damage: series);
        }

        // 정수 누산만 쓴다 — 웹 zod가 damage 원소를 nonnegative int로 못박고 있어 소수가 섞이면 그 업로드가 통째로
        // 400난다. 완전한 버킷만 돈다(series.Length / step): 잔여 초는 위 주석대로 버린다. step이 상한에 걸린
        // 비정상 길이에서는 샘플 수도 같이 잘라 앞부분만 남긴다(전투를 통째로 잃는 것보다 낫다).
        long[] bucketed = new long[Math.Min(series.Length / step, MaxSeriesSamples)];
        for (int bucket = 0; bucket < bucketed.Length; bucket++)
        {
            long sum = 0;
            int end = (bucket + 1) * step;
            for (int i = bucket * step; i < end; i++)
            {
                sum += series[i];
            }

            bucketed[bucket] = sum;
        }

        return new StatsDpsSeriesPayload(step, bucketed);
    }

    private List<User> ResolveContributors(IEnumerable<User> contributors) =>
        contributors.Select(ResolveUserSnapshot).ToList();

    private User ResolveUserSnapshot(User user)
    {
        User resolved = user.Copy();
        MergeUserInfo(resolved, _data.User(user.Id));
        string? nickname = NonBlank(resolved.Nickname);
        int server = resolved.Server;

        if (resolved.Power <= 0 && nickname != null && server > 0)
        {
            MergeUserInfo(resolved, _data.FindUserByNicknameAndServer(nickname, server));
        }

        // The 0x9702 roster carries each member's combat power, and the parser already stores it — until now
        // only the pre-combat preview read it. Ask it BEFORE the official lookup: the lookup is a synchronous
        // HTTP call on the upload worker that caches a failure for ten minutes, so one hiccup at the site
        // silently drops every battle for the next ten. This costs no network at all, and it is the only
        // source that ever works for a character whose profile is private or whose name is shared — for those
        // players the lookup returns nothing forever, so every one of their battles was being dropped.
        if (resolved.Power <= 0 && nickname != null && server > 0)
        {
            // 로스터 전투력은 0x9702 본문을 "0x04 마커 뒤 u32"로 훑어 얻은 값이라 세 소스 중 가장 무르다.
            // 여기서 나온 값은 업로드뿐 아니라 본인 행 배지(OwnCharacter -> SetRecognized -> selfPower)까지
            // 가므로, 파서 게이트와 같은 기준을 한 번 더 건다.
            int rosterPower = _data.PartyRosterPower(nickname, server, RosterPowerTtlMs);
            if (CombatPower.IsPlausible(rosterPower))
            {
                resolved.Power = rosterPower;
            }
        }

        if (resolved.Power <= 0 && nickname != null && server > 0)
        {
            OfficialCharacterInfo? info = _data.ResolveOfficialCharacterInfo(resolved.Id, nickname, server, resolved.Job);
            if (info != null)
            {
                if (string.IsNullOrWhiteSpace(resolved.Nickname))
                {
                    resolved.Nickname = info.Nickname;
                }

                if (resolved.Server <= 0)
                {
                    resolved.Server = info.Server;
                }

                if (resolved.Job == null && info.Job != null)
                {
                    resolved.Job = info.Job;
                }

                // 공식 조회 값은 스캔이 아니라 구조화된 JSON이라 CombatPower 상한을 걸지 않는다
                // (DataManager.ApplyOfficialCharacterInfo의 같은 판단 참조).
                if (info.Power > 0)
                {
                    resolved.Power = info.Power;
                }
            }
        }

        return resolved;
    }

    private static void MergeUserInfo(User target, User? source)
    {
        if (source == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(target.Nickname) && !string.IsNullOrWhiteSpace(source.Nickname))
        {
            target.Nickname = source.Nickname;
        }

        if (target.Server <= 0 && source.Server > 0)
        {
            target.Server = source.Server;
        }

        if (target.Job == null && source.Job != null)
        {
            target.Job = source.Job;
        }

        if (target.Power <= 0 && source.Power > 0)
        {
            target.Power = source.Power;
        }
    }

    private static StatsSynergyPayload BuildSynergy(IEnumerable<User> contributors)
    {
        HashSet<JobClass> jobs = contributors.Where(u => u.Job != null).Select(u => u.Job!.Value).ToHashSet();
        int count = Math.Min(jobs.Count(j => SynergyJobs.Contains(j)), 3);
        return new StatsSynergyPayload(
            HasGuardian: jobs.Contains(JobClass.TEMPLAR),
            HasGladiator: jobs.Contains(JobClass.GLADIATOR),
            HasChanter: jobs.Contains(JobClass.CHANTER),
            HasCleric: jobs.Contains(JobClass.CLERIC),
            SynergyCount: count);
    }

    /// <summary>The active 특화 slot numbers (1..5) for a skill, or null when it carries none. Decoded from the
    /// full wire code, which is where the game keeps it — there is no specialization field on the wire.</summary>
    private static IReadOnlyList<int>? DecodeSpecialization(int rawSkillCode)
    {
        bool[]? slots = SkillSpecialization.Decode(rawSkillCode);
        if (slots == null) return null;

        var active = new List<int>();
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i]) active.Add(i + 1);
        }

        return active.Count > 0 ? active : null;
    }

    private List<StatsSkillPayload> BuildSkillPayloads(Dictionary<string, AnalyzedSkill> skills, long totalDamage)
    {
        double Share(long amount) => totalDamage > 0 ? OneDecimal((double)amount / totalDamage * 100.0) : 0.0;

        var entries = new List<StatsSkillPayload>();
        foreach (KeyValuePair<string, AnalyzedSkill> entry in skills)
        {
            if (!int.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
            {
                continue;
            }

            AnalyzedSkill skill = entry.Value;
            string name = NonBlank(skill.Name) ?? code.ToString(CultureInfo.InvariantCulture);

            long directDamage = Math.Max((long)skill.DamageAmount, 0);
            if (directDamage > 0)
            {
                RateSummary rate = SummarizeRates(new[] { skill });
                entries.Add(new StatsSkillPayload(
                    SkillCode: code,
                    SkillName: name,
                    DamageType: "direct",
                    Damage: directDamage,
                    HitCount: skill.Times,
                    CritRate: rate.CritRate,
                    StrongRate: rate.StrongRate,
                    PerfectRate: rate.PerfectRate,
                    Share: Share(directDamage),
                    // 미터 상세 표가 스킬마다 보여주는 나머지 칸들. 종전에는 참가자 합계로만 올라가서, 웹은
                    // "이 스킬은 후방을 몇 %나 쳤나"를 그릴 수 없었다.
                    BackRate: rate.BackRate,
                    FrontRate: rate.FrontRate,
                    ParryRate: rate.ParryRate,
                    Specialization: DecodeSpecialization(skill.RawSkillCode)));
            }

            long dotDamage = Math.Max((long)skill.DotDamageAmount, 0);
            if (dotDamage > 0)
            {
                entries.Add(new StatsSkillPayload(
                    SkillCode: code,
                    SkillName: name,
                    DamageType: "dot",
                    Damage: dotDamage,
                    HitCount: skill.DotTimes,
                    CritRate: 0.0,
                    StrongRate: 0.0,
                    PerfectRate: 0.0,
                    Share: Share(dotDamage)));
            }
        }

        return entries.OrderByDescending(e => e.Damage).ToList();
    }

    /// <summary>The encounter block. <c>mobCode</c> is the authority — the server resolves the dungeon and its
    /// difficulty/stage from it, because a boss mobCode is unique per (dungeon, variant). The descriptive fields
    /// come from the meter's own copy of the same catalog: redundant while the two agree, and a record of what
    /// the client believed when they don't. <c>bossName</c> stays the RAW mob name (the server falls back to
    /// matching on it), never the difficulty-decorated one the UI shows.</summary>
    private StatsEncounterPayload BuildEncounterPayload(Mob mob, DpsReport report)
    {
        if (_data.Encounters.Lookup(mob.Code) is not EncounterInfo info)
        {
            return new StatsEncounterPayload(mob.Code, mob.Name);
        }

        return new StatsEncounterPayload(
            mob.Code,
            mob.Name,
            DungeonName: info.DungeonName,
            Category: info.Category,
            Difficulty: info.Difficulty,
            Stage: info.Stage,
            BossIndex: info.BossIndex,
            Trial: BuildTrialPayload(info, report));
    }

    /// <summary>The 시련 난이도 block, or null when this run carried no difficulty knobs — which is every
    /// dungeon except the trial, and a trial run we somehow observed nothing for.
    /// <para>Gated on the ENCOUNTER, not just on the tracker holding something. The knobs are read from
    /// abnormals that only the trial broadcasts, but they had no expiry until 2026-08-11 and outlived the run —
    /// so a 초월 battle fought after a trial uploaded that trial's difficulty attached to it. The tracker now
    /// clears on leaving; this makes the payload right even if it ever leaks again, exactly as
    /// <see cref="EncounterCatalog.DisplayName"/> does for the label.</para></summary>
    private static StatsTrialDifficultyPayload? BuildTrialPayload(EncounterInfo info, DpsReport report)
    {
        // 리포트에 동결된 값을 쓴다. 추적기를 라이브로 읽으면 스풀에 며칠 묵었다 재시도되는 업로드가
        // 그 시점의 난이도를 실어 보낸다 — 화면 라벨과 같은 구멍이고, 이쪽은 서버에 남아 되돌릴 수 없다.
        Data.TrialDifficulty trial = report.TrialDifficulty;
        if (!info.IsTrial || !trial.IsTrial)
        {
            return null;
        }

        return new StatsTrialDifficultyPayload(
            trial.Level,
            trial.LevelMin,
            trial.LevelMax,
            trial.Timelimit,
            trial.Rebirthlimit,
            trial.BossBuff,
            trial.SkillUpgrade);
    }

    private static StatsResultPayload BuildResultPayload(
        DpsInformation info, RateSummary rates, JudgmentCounts? counts = null) => new(
        TotalDamage: RoundToLong(info.Amount),
        Dps: RoundToLong(info.Dps),
        PartyContribution: OneDecimal(info.Contribution),
        BossHpContribution: OneDecimal(info.EntireContribution),
        HitCount: rates.HitCount,
        CritRate: rates.CritRate,
        StrongRate: rates.StrongRate,
        PerfectRate: rates.PerfectRate,
        BackRate: rates.BackRate,
        FrontRate: rates.FrontRate,
        ParryRate: rates.ParryRate,
        BossBlockRate: 0.0,
        // 업로더 행에만 실린다. 파티원 행에 넣지 않는 것은 표본을 아껴서가 아니라, 저 정수들이 뜻을 가지려면
        // 짝이 되는 스탯(강타·명중·치명 수치)이 있어야 하는데 그건 본인 것만 얻을 수 있기 때문이다.
        FlaggedHits: counts?.FlaggedHits,
        SmiteHits: counts?.SmiteHits,
        PerfectHits: counts?.PerfectHits,
        CritHits: counts?.CritHits,
        ParryHits: counts?.ParryHits,
        BackTimes: counts?.BackTimes,
        EligibleDamage: counts?.EligibleDamage,
        SummonFlaggedHits: counts?.SummonFlaggedHits,
        SummonSmiteHits: counts?.SummonSmiteHits,
        SummonPerfectHits: counts?.SummonPerfectHits);

    /// <summary>The uploader's raw judgment counters, summed over their skill rows.</summary>
    private sealed record JudgmentCounts(
        int FlaggedHits,
        int SmiteHits,
        int PerfectHits,
        int CritHits,
        int ParryHits,
        int BackTimes,
        long EligibleDamage,
        int SummonFlaggedHits,
        int SummonSmiteHits,
        int SummonPerfectHits);

    private static JudgmentCounts CountJudgments(IEnumerable<AnalyzedSkill> skillsEnumerable)
    {
        List<AnalyzedSkill> skills = skillsEnumerable as List<AnalyzedSkill> ?? skillsEnumerable.ToList();
        return new JudgmentCounts(
            FlaggedHits: skills.Sum(s => s.FlaggedTimes),
            SmiteHits: skills.Sum(s => s.DoubleTimes),
            PerfectHits: skills.Sum(s => s.PerfectTimes),
            CritHits: skills.Sum(s => s.CritTimes),
            ParryHits: skills.Sum(s => s.ParryTimes),
            BackTimes: skills.Sum(s => s.BackTimes),
            EligibleDamage: skills.Sum(s => s.EligibleDamage),
            SummonFlaggedHits: skills.Sum(s => s.SummonFlaggedTimes),
            SummonSmiteHits: skills.Sum(s => s.SummonDoubleTimes),
            SummonPerfectHits: skills.Sum(s => s.SummonPerfectTimes));
    }

    /// <summary>Map the frozen judgment cross-tabs onto the wire DTO. Null in, null out — a battle where the
    /// local player dealt no measurable damage simply omits the block.</summary>
    private static StatsSelfJudgmentPayload? BuildSelfJudgmentPayload(SelfJudgmentSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.EligibleHits <= 0) return null;

        return new StatsSelfJudgmentPayload(
            TargetMobCode: snapshot.TargetMobCode,
            EligibleHits: snapshot.EligibleHits,
            StampAgeP50Ms: snapshot.StampAgeP50Ms,
            FreshHits: snapshot.FreshHits,
            Smite: Axis(snapshot.Smite),
            Perfect: Axis(snapshot.Perfect),
            Accuracy: new StatsJudgmentAccuracyPayload(
                snapshot.Accuracy.N, snapshot.Accuracy.Hits, snapshot.Accuracy.Bins, snapshot.AccuracyBySkill),
            Crit: new StatsJudgmentCritPayload(
                snapshot.Crit.N, snapshot.Crit.Hits, snapshot.Crit.Bins,
                [snapshot.CritSw4Hits, snapshot.CritSw4Crits]),
            Stats: new StatsSelfJudgmentStatsPayload(
                Src: snapshot.Stats.Source,
                Mask: snapshot.Stats.Mask,
                Acc318: snapshot.Stats.WeaponAccuracy,
                Pve110: snapshot.Stats.PveAccuracy,
                AccInc427: snapshot.Stats.AccuracyIncBp,
                BlockPierce256: snapshot.Stats.BlockPierce,
                CritInc429: snapshot.Stats.CriticalIncBp,
                BackCrit100: snapshot.Stats.BackCritical,
                FrontCrit591: snapshot.Stats.FrontCritical),
            Trimmed: snapshot.Trimmed);

        // 빈 배열은 보내지 않는다 — "이 구간 타격이 없었다"와 "이 필드를 안 쓰는 미터다"를 웹이 구분해야 한다.
        static StatsJudgmentAxisPayload Axis(JudgmentAxisSnapshot axis) => new(
            axis.N, axis.Hits, axis.Bins, axis.LateBins.Count > 0 ? axis.LateBins : null);
    }

    private static StatsBuffPayload ToBuffPayload(
        OperatingData value,
        string scope,
        string category,
        int? ownerId,
        JobClass? ownerJob,
        int? ownerParticipantIndex,
        int? actorParticipantIndex,
        string? actorIdentityHash)
    {
        string? source = scope == "participant" && ownerId != null
            ? BuffSource(ownerId.Value, ownerJob, value)
            : null;
        return new StatsBuffPayload(
            BuffCode: value.Code,
            BuffName: value.Name,
            OperatingRate: OneDecimal(value.OperatingRate),
            Scope: scope,
            Category: category,
            Source: source,
            ActorIdentityHash: actorIdentityHash,
            OwnerParticipantIndex: ownerParticipantIndex,
            ActorParticipantIndex: actorParticipantIndex,
            BaseCode: value.BaseCode > 0 ? value.BaseCode : null,
            // 0 = the wire never gave a level (consumable/scroll, or the tail self-validation declined) — send
            // null rather than 0 so the site can tell "no level" apart from a real level.
            Level: value.Level > 0 ? value.Level : null);
    }

    // Same classification as the local meter's DetailModel.BuildOwnBuffs():
    //   self  = caster == owner AND the buff's job prefix == owner job
    //   other = caster == owner but prefix mismatch (consumable/scroll/other-job self-buff)
    //   party = caster != owner (another player applied it; same-job dupes split by actorIdentityHash)
    //
    // The prefix comes from OperatingData.EffectiveJobPrefix, which is derived from the RAW packet code. Reading
    // it off the payload's buffCode would be wrong twice over: the fallback path emits an 8-digit base (11390000
    // / 10_000_000 == 1, so a self-buff could never match), and an 8-digit mob code (12000101 = 중독) would
    // otherwise read as 수호성 and turn a mob's debuff into that player's self-buff.
    private static string BuffSource(int ownerId, JobClass? ownerJob, OperatingData value)
    {
        if (value.ActorId != ownerId)
        {
            return "party";
        }

        int? ownerPrefix = ownerJob != null ? ownerJob.Value.BasicSkillCode() / 1_000_000 : null;
        int codePrefix = value.EffectiveJobPrefix;
        return ownerPrefix != null && codePrefix != 0 && codePrefix == ownerPrefix ? "self" : "other";
    }

    private sealed record RateSummary(
        int HitCount,
        double CritRate,
        double StrongRate,
        double PerfectRate,
        double BackRate,
        double FrontRate,
        double ParryRate);

    private static RateSummary SummarizeRates(IEnumerable<AnalyzedSkill> skillsEnumerable)
    {
        List<AnalyzedSkill> skills = skillsEnumerable as List<AnalyzedSkill> ?? skillsEnumerable.ToList();
        int directHits = Math.Max(skills.Sum(s => s.Times), 0);
        // Back/front (facing) judgments ride ONLY on flag-bearing hits, so they divide by FlaggedTimes — this
        // matches the meter's 후방/전방 detail tiles (DetailModel). Crit/강타/완벽/페리 keep the historical Times
        // basis. (BackRate previously used Times too, which under-read vs the meter; FrontRate is new.)
        int flaggedHits = Math.Max(skills.Sum(s => s.FlaggedTimes), 0);
        int allHits = directHits + Math.Max(skills.Sum(s => s.DotTimes), 0);
        double Rate(int count) => directHits > 0 ? OneDecimal((double)count / directHits * 100.0) : 0.0;
        double RateFlagged(int count) => flaggedHits > 0 ? OneDecimal((double)count / flaggedHits * 100.0) : 0.0;

        return new RateSummary(
            HitCount: allHits,
            CritRate: Rate(skills.Sum(s => s.CritTimes)),
            StrongRate: Rate(skills.Sum(s => s.DoubleTimes)),
            PerfectRate: Rate(skills.Sum(s => s.PerfectTimes)),
            BackRate: RateFlagged(skills.Sum(s => s.BackTimes)),
            FrontRate: RateFlagged(skills.Sum(s => s.FrontTimes)),
            ParryRate: Rate(skills.Sum(s => s.ParryTimes)));
    }

    private static string BattleHash(int server, string nickname, int mobCode, long startedAt, long endedAt, long totalDamage, long durationMs)
    {
        long roundedStart = startedAt / 10_000L * 10_000L;
        long roundedEnd = endedAt / 10_000L * 10_000L;
        string raw = string.Join("|", new object[] { server, nickname, mobCode, roundedStart, roundedEnd, totalDamage, durationMs });
        return StatsIdentity.Sha256(raw);
    }

    private static double AmountOf(DpsLog log, int uid) =>
        log.Report.Information.TryGetValue(uid, out DpsInformation? info) ? info.Amount : 0.0;

    // Kotlin Double.roundToLong(): nearest, ties toward +infinity (= floor(x + 0.5)).
    private static long RoundToLong(double value) => (long)Math.Floor(value + 0.5);

    // Kotlin kotlin.math.round(): nearest, ties to even (banker's) — System.Text default.
    private static double OneDecimal(double value) => Math.Round(value * 10.0, MidpointRounding.ToEven) / 10.0;

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? IndexOrNull(Dictionary<int, int> map, int key) => map.TryGetValue(key, out int value) ? value : null;
}
