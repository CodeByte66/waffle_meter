using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>What the tier layer knows about one visible row.
/// <para><paramref name="TierRank"/> 1..8. When <paramref name="IsCareer"/> is true it is the character's
/// server-computed career tier; otherwise it is derived from THIS fight's percentile and must be worded as
/// 이번 전투 등급, never as a standing.</para>
/// <para><paramref name="BattleTopPercent"/> is this fight's locally computed percentile, null when the cohort
/// shipped no distribution row (표본 부족) — the caller renders nothing rather than guessing.</para>
/// <para><paramref name="ComparisonBasis"/> names the pool that percentile was measured against ("전체 전투력
/// 기준" / "전투력 700k–750k 미만 기준"). It travels WITH the number because the two are meaningless apart:
/// the same "상위 3%" means one thing against comparable gear and another against everyone, and nothing in the
/// figure itself distinguishes them. Null exactly when there is no percentile to qualify.</para></summary>
public readonly record struct RowTier(
    int TierRank,
    double? BattleTopPercent,
    string? DungeonLabel = null,
    bool IsCareer = false,
    string? ComparisonBasis = null);

/// <summary>
/// Turns a live/finished report into per-row tier state, entirely locally.
/// <para>The live percentile for EVERY row — self and party members alike — is computed here from the
/// downloaded distribution artifact: that row's dps against its own class cohort. It costs no request and
/// discloses nothing beyond the combat packets the row already renders. Only the career tier (a standing over
/// weeks) comes from the server, and only for characters that consented.</para>
/// </summary>
public static class TierEvaluator
{
    /// <summary>The four support classes whose presence defines a party's synergy, and their bit values.
    /// Must stay byte-identical to the web's <c>SYNERGY_BITS</c> or the cohort key silently diverges.</summary>
    private static readonly (JobClass Job, int Bit)[] SynergyBits =
    [
        (JobClass.TEMPLAR, 1),   // 수호성
        (JobClass.GLADIATOR, 2), // 검성
        (JobClass.CHANTER, 4),   // 호법성
        (JobClass.CLERIC, 8),    // 치유성
    ];

    /// <summary>
    /// Build the uid → tier map for a report.
    /// </summary>
    /// <param name="careerTiers">Server-supplied career tiers keyed by identity hash (self from the upload
    /// response, others from a consent-gated batch lookup). Rows missing here still get a 이번 전투 등급.</param>
    /// <param name="identityHashOf">Resolves a row's identity hash; returns null when the character is not
    /// identified yet (a bare mid-join actor), in which case only the local percentile applies.</param>
    /// <param name="trial">The 시련 affixes this run was observed at, if any. Only the trial needs them: its
    /// difficulties share one set of boss mobCodes, so the artifact's mob map cannot place it and the
    /// artifact's trial gate does — but only when these match what the gate requires. Default (all unknown)
    /// closes every gate, which is the right answer for a run nothing was read for.</param>
    public static Dictionary<int, RowTier> Evaluate(
        DpsReport report,
        TierArtifact? artifact,
        IReadOnlyDictionary<string, int>? careerTiers = null,
        Func<User, string?>? identityHashOf = null,
        TrialDifficulty trial = default)
    {
        var result = new Dictionary<int, RowTier>();
        if (artifact == null || report.Target is not MobInfo target)
        {
            return result;
        }

        long durationMs = report.BattleEnd - report.BattleStart;
        if (durationMs < TierLadder.MinBattleDurationMs)
        {
            return result;
        }

        if (artifact.Placement(target.Mob.Code, trial) is not TierMobPlacement placement)
        {
            return result; // fail-closed: an unmapped boss gets no tier at all
        }

        string? dungeonLabel = DungeonLabel(artifact, placement);
        // 🔑 partyMode 는 **던전 카테고리의 순함수**다. 서버가 그렇게 발행한다:
        //   (CASE WHEN category = '성역' THEN 10 ELSE 5 END)
        // 발행본 실측(artifact 62b07db4aabc29eb, 7,646행)에서 (성역,10) / (원정·초월,5), **예외 0건**.
        // ⚠️ 종전에는 `Contributors.Count`(= 딜을 넣은 사람 수)로 유도했다. 그러면 버스팟처럼 일부만 딜한
        // 공대가 5인 분포로 채점되고, 더 나쁘게는 "파티라서 신뢰"로 시너지 검사를 통째로 건너뛴다.
        // 같은 저장소의 업로드 빌더가 이 유도를 명시적으로 거부하는 이유와 같다(StatsPayloadBuilder.cs:526).
        // 전제: 서버가 성역에 p=5 행을 싣기 시작하면 이 매핑이 깨진다.
        bool isRaid = artifact.CategoryIdForDungeon(placement.DungeonOrd)
                      == artifact.CategoryId(EncounterInfo.RaidCategory);
        int partyMode = isRaid ? 10 : 5;
        bool trusted = IsSynergyTrusted(report, isRaid);

        foreach (User user in report.Contributors)
        {
            if (!report.Information.TryGetValue(user.Id, out DpsInformation? info) || info.Dps <= 0)
            {
                continue;
            }

            string? job = user.Job is JobClass jc ? jc.ClassName() : null;
            int synergyCount = SynergyCountFor(report, user, trusted);

            TierCohort? cohort = TierLadder.CohortFor(
                artifact, target.Mob.Code, job, user.Power, durationMs, synergyCount, partyMode, trusted, trial);

            // Keep the whole evaluation, not just the number: which power band actually answered is decided
            // inside the ladder (the requested band can be absent and fall back), so the basis has to be read
            // off the result rather than recomputed from what we asked for.
            TierEvaluation? evaluation = cohort is TierCohort c
                ? TierLadder.Evaluate(artifact, c, info.Dps)
                : null;
            double? battlePercent = evaluation?.TopPercent;

            string? hash = identityHashOf?.Invoke(user);
            bool hasCareer = hash != null && careerTiers != null && careerTiers.TryGetValue(hash, out int careerRank);
            int rank = hasCareer
                ? careerTiers![hash!]
                : battlePercent is double bp ? TierLadder.TierRankOf(bp) : 0;

            if (rank <= 0)
            {
                continue; // neither a standing nor a measurable fight — render nothing
            }

            result[user.Id] = new RowTier(
                rank, battlePercent, dungeonLabel, hasCareer, evaluation?.ComparisonBasis);
        }

        return result;
    }

    /// <summary>"무스펠의 성배 · 어려움", or just the dungeon when the variant label is unknown.</summary>
    private static string? DungeonLabel(TierArtifact artifact, TierMobPlacement placement)
    {
        string? dungeon = artifact.DungeonName(placement.DungeonOrd);
        if (dungeon == null)
        {
            return null;
        }

        string? variant = artifact.VariantLabel(placement.DungeonOrd, placement.VariantOrd);
        return variant == null ? dungeon : $"{dungeon} · {variant}";
    }

    /// <summary>
    /// Can this report's synergy be trusted to describe what each player ACTUALLY received?
    /// <para>🔑 서버 규칙을 그대로 옮긴 것이다: <c>(category &lt;&gt; '성역' OR sub_party_known)</c>. 즉
    /// <b>비-성역은 슬롯을 아예 보지 않고 항상 신뢰</b>하고, 성역만 슬롯 정합성을 본다. 미터가 여기서 서버보다
    /// 조금이라도 빡세면 그 전투는 R0 밖으로 밀려나는데, <b>밴드행은 rung 0 에만 실리므로</b>(빌더가 모든 밴드행을
    /// <c>0 AS rung</c> 으로 싣는다) 시너지 버킷뿐 아니라 <b>전투력 밴드까지 통째로 잃는다.</b></para>
    /// <para>성역의 조건은 <c>hasCoherentRaidSlots</c> 와 동일하다 — <b>참가자 전원</b>이 1..10 범위의 서로 다른
    /// 슬롯을 갖는다. ⚠️ <b>출석 요구는 없다.</b> "로스터 전원이 딜을 넣었을 것"은 예전 규칙이었고 의도적으로
    /// 버려졌다 — 기믹 분할 전투는 원리적으로 만족할 수 없기 때문이다(케투는 항상 slot 5~8 만, 라후는 항상
    /// slot 1~5 만 그 보스와 싸운다). 실측으로도 2.9.2 공대 46건에서 29→31 건을 살렸고, 더 보수적인 규칙은
    /// 29건 그대로여서 아무것도 살리지 못했다.</para>
    /// <para>🔴 <b>정원은 10 뿐이다.</b> 서버 <c>RAID_ROSTER_SIZES = [10]</c>. 8 을 공대로 취급하면 서버가
    /// 신뢰하지 않는 전투를 미터가 신뢰하게 된다(= 그 전투가 속한 적 없는 코호트의 백분위를 주장한다).
    /// 코드 어딘가에 남아 있는 4+4 는 <c>rosterSize</c> 를 안 보내던 시절의 옛 기록 전용 경로다.</para>
    /// </summary>
    private static bool IsSynergyTrusted(DpsReport report, bool isRaid)
    {
        if (!isRaid)
        {
            return true;
        }

        if (report.PartyRosterSize != RaidRosterSize)
        {
            return false;
        }

        var slots = new HashSet<int>();
        foreach (User user in report.Contributors)
        {
            if (!report.PartySlots.TryGetValue(user.Id, out int slot) || slot < 1 || slot > RaidRosterSize)
            {
                return false;
            }

            if (!slots.Add(slot))
            {
                return false; // duplicate slot — the roster is not coherent
            }
        }

        // 종전의 `slots.Count == partySize` 는 위 두 검사가 참이면 항상 참인 죽은 조건이었고, 출석을 요구하는
        // 것처럼 읽혀 오해를 불렀다. 서버는 출석을 요구하지 않는다.
        return slots.Count > 0;
    }

    /// <summary>공대 정원. 서버 <c>RAID_ROSTER_SIZES</c> 가 <b>10 하나</b>다 — 8인 공대는 존재하지 않는다.</summary>
    private const int RaidRosterSize = 10;

    /// <summary>Distinct synergy classes in the player's own group, capped at 3 (the server caps the same way,
    /// so a 4-synergy party folds into the 3 bucket rather than creating a rare cell). The mask includes the
    /// player's own class — a 치유성 counts their own 축복 like everyone else's.</summary>
    private static int SynergyCountFor(DpsReport report, User user, bool trusted)
    {
        int group = SubGroupOf(report, user, trusted);
        int mask = 0;
        foreach (User other in report.Contributors)
        {
            if (other.Job is not JobClass job)
            {
                continue;
            }

            if (group > 0 && SubGroupOf(report, other, trusted) != group)
            {
                continue;
            }

            foreach ((JobClass synergyJob, int bit) in SynergyBits)
            {
                if (job == synergyJob)
                {
                    mask |= bit;
                }
            }
        }

        return Math.Min(System.Numerics.BitOperations.PopCount((uint)mask), 3);
    }

    /// <summary>
    /// 신뢰된 공대면 1 또는 2(10인 정원을 반으로: 1~5 / 6~10), 아니면 0(파티 전체가 한 그룹).
    /// <para>⚠️ 경계는 <b>정원</b>의 절반이지 <b>딜을 넣은 사람 수</b>의 절반이 아니다. 종전 코드는 후자여서
    /// 10인 공대에서 딜러가 8명이면 슬롯 5가 <c>5 &gt; 4</c> 로 <b>반대편 파티</b>의 시너지 마스크로 평가됐다 —
    /// 가장 구체적인 rung(R0)에서 가장 틀린 코호트를 고르는 셈이다.</para>
    /// </summary>
    private static int SubGroupOf(DpsReport report, User user, bool trusted)
    {
        if (!trusted)
        {
            return 0;
        }

        return report.PartySlots.TryGetValue(user.Id, out int slot) && slot > RaidRosterSize / 2 ? 2 : 1;
    }
}
