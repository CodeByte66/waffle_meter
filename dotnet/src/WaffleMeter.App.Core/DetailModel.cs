using WaffleMeter.Data;

namespace WaffleMeter.App.Core;

/// <summary>One skill row in the detail table (a direct hit row, a "- 지속" DOT row, or a merged
/// chain group). Pct fields are null for DOT rows (rendered as "-").</summary>
public sealed record DetailSkillRow(
    int Code,
    string Name,
    int Hits,
    long Damage,
    bool IsDot,
    int? CritPct,
    int? StrongPct,
    int? PerfectPct,
    int? BackPct,
    int? FrontPct,
    int? ParryPct,
    double Ratio,
    IReadOnlyList<bool>? Spec,
    /// <summary>이 행의 피해가 <b>다른 사람의 효과</b>로 들어온 것일 때 그 사람의 이름(아니면 null).
    /// 검성 흡혈의 검의 착취와 치유성 대지의 축복의 추가 피해는 수혜자의 미터에 <i>시전자 직업의 스킬 코드</i>로
    /// 찍히기 때문에, 이름표가 없으면 "내가 안 배운 직업 스킬이 내 스킬 목록에 있다"로 읽힌다.</summary>
    string? GrantedBy = null);

/// <summary>A chain group: the merged/displayed skill + its child rows (expandable when >1).</summary>
public sealed record DetailSkillGroup(DetailSkillRow Merged, IReadOnlyList<DetailSkillRow> Children, bool HasChildren);

/// <summary>One buff/debuff uptime row. <see cref="Count"/> is null for a normal uptime row (the window shows
/// <see cref="Rate"/> as a %); when set, the row is a PROC whose uptime % is meaningless and the window shows
/// "N회" instead — see <see cref="DetailProcRow"/>.</summary>
/// <param name="Level">시전자가 이 버프의 스킬을 가진 레벨(어노멀 레벨 1~40). 0 = 모름(소모품·주문서처럼
/// 레벨 개념이 없는 버프이거나 적용 패킷이 레벨을 안 실은 경우)이고, 그때 표시 계층은 칸을 비운다.
/// 시너지 버프의 효과량이 레벨 선형이라(노련한 반격 = 5.4% + 0.4%/레벨) 가동률만으로는 기여를 못 읽는다.</param>
public sealed record DetailBuffRow(
    int Code, string Name, double Rate, string Description, int? Count = null, int Level = 0,
    /// <summary>이 버프를 <b>나에게 걸어 준 다른 사람</b>의 이름(내가 올린 버프면 null). 「남이 준 버프」 섹션의
    /// 행만 채워지며, 표시 계층은 이 값을 그 행의 부제로 쓴다 — 섹션 이름을 40줄 반복하는 것보다
    /// "누가 줬나"가 그 행에서 유일하게 새로운 정보이기 때문이다.</summary>
    string? CasterName = null);

/// <summary>A count-based row pinned to the BOTTOM of the 내 버프 section: an effect that fires on a condition
/// with an internal cooldown, where "how many times" is the only meaningful number. Currently 회생의 계약's
/// 생명력 10% 이하 긴급 회복 (1분 재발동 제한). Meter-only — it is never part of the stats/web payload.</summary>
public sealed record DetailProcRow(int Code, string Name, int Count, string Description);

/// <param name="FromOtherPlayers">참이면 이 섹션은 <b>남이 나에게 걸어 준</b> 버프다. 표시 계층이 체크박스 하나로
/// 통째로 켜고 끄므로, 한국어 라벨 문자열이 아니라 이 플래그로 가려야 라벨을 고쳐도 필터가 안 깨진다.</param>
public sealed record DetailBuffSection(
    string Label, IReadOnlyList<DetailBuffRow> Rows, bool FromOtherPlayers = false);

/// <summary>버프를 걷어낸/얹은 초당 피해량. 전투 상세 상단 타일에 그대로 올라간다.</summary>
/// <param name="Ndps">남이 걸어준 버프 몫을 나눠 걷어낸 값. 내 버프는 남아 있다 — 그건 내 플레이다.</param>
/// <param name="Rdps"><see cref="Ndps"/> + 내가 남에게 얹어준 몫(버프 배수 + 실제로 넘어간 피해).</param>
/// <param name="TakenBuffDps">내 DPS 중 남의 버프가 만들어 준 몫(= dps − ndps).</param>
/// <param name="GivenBuffDps">내가 남에게 얹어준 몫(= rdps − ndps). 서포터의 기여가 보이는 축.</param>
/// <param name="GrantedDamage">내 효과가 <b>남의 미터에</b> 낸 실제 피해 총량(흡혈의 검 착취 / 대지의 축복 추가
/// 피해). <see cref="GivenBuffDps"/> 중 추정이 아니라 실측인 부분이다.</param>
public sealed record DetailMetrics(
    double Ndps, double Rdps, double TakenBuffDps, double GivenBuffDps, long GrantedDamage);

/// <summary>
/// Pure computation of a player's detail breakdown — summary stats, chain-grouped skill table, and
/// own-buff / boss-debuff uptime sections — ported from the React useDetails + DetailsPanel +
/// BuffRateSection logic. No WPF deps so it is unit-testable; the window/VM only formats + styles it.
/// </summary>
public sealed record DetailModel(
    long TotalDamage,
    double Contribution,
    double CritPct,
    double StrongPct,
    double PerfectPct,
    double BackPct,
    double FrontPct,
    double ParryPct,
    int HitCount,
    long CombatMs,
    IReadOnlyList<DetailSkillGroup> Skills,
    IReadOnlyList<DetailBuffSection> Buffs,
    IReadOnlyList<DetailBuffSection> Debuffs,
    /// <summary>nDPS/rDPS. null = 계산할 수 없었던 전투(버프 정보가 없거나 지속시간 0) — 그때 표시 계층은
    /// 타일을 아예 감춘다. 0으로 그리면 "버프를 하나도 못 받았다"로 읽힌다.</summary>
    DetailMetrics? Metrics = null)
{
    private static readonly (int Main, int[] Children)[] ChainGroups =
    {
        (11020000, new[] { 11030000, 11040000 }),
        (12010000, new[] { 12040000, 12020000, 12030000 }),
        (13010000, new[] { 13030000, 13040000 }),
        // 궁성 저격(14020000) 연계 = 연사(14030000) + 나선 화살(14040000). 속사(14340000)는 별개 스킬
        // (이전 14020000→14340000 묶음은 dev skillChains.ts/CSV의 오류였음 — 사용자 정정 2026-06-12).
        (14020000, new[] { 14030000, 14040000 }),
        (15210000, new[] { 15040000, 15050000 }),
        (15010000, new[] { 15030000, 15250000 }),
        // 정령성은 연계 묶음 없음. 냉기 충격(16010000)·화염 전소(16040000)·진공 폭발(16020000)·대지 진동(16030000)은
        // 서로 다른 4개 원소 스킬이고, 인게임 전투력 측정기도 이 넷을 각각 별도 행으로 보여준다 — 연계가 아니다.
        // 종전 (16010000, {16040000,16020000,16030000}) 묶음은 이 넷을 하나로 합쳐 "냉기 충격" 한 행(합산 13.7%)으로
        // 둔갑시켜 상세 정렬이 인게임과 어긋났다(실측 2026-07-27, 정령성 허수아비 로그). 스킬명 병합(같은 이름=한 행)만
        // 남기면 나머지는 인게임과 동일하게 이름별 단일 행으로 정렬된다. ⚠️ 이 묶음을 되살리지 말 것.
        (17010000, new[] { 17020000, 17030000 }),
        (18010000, new[] { 18020000, 18030000 }),
    };

    private sealed class Raw
    {
        public int Code;
        public int RawCode; // the full wire code, for specialization (특화) decoding
        public string Name = string.Empty;
        public int Hits;
        public long Damage;
        public bool IsDot;
        public int Crit;
        public int Strong;
        public int Perfect;
        public int Back;
        public int Front;
        public int Parry;
        public int Flagged; // direct hits that carried a special-flag region (sw6) — the back/전방/강타/완벽/페리 denominator
    }

    public static DetailModel Compute(
        IReadOnlyDictionary<string, AnalyzedSkill> skills,
        IReadOnlyList<OperatingData> ownBuffs,
        IReadOnlyList<OperatingData> bossDebuffs,
        int uid,
        JobClass? job,
        double contribution,
        long combatMs,
        DetailProcRow? proc = null,
        DetailMetrics? metrics = null,
        // 넘겨준 사람 이름표(시너지 base 코드 -> 시전자 이름). 스킬 행에 "(밀피)" 처럼 붙는다.
        IReadOnlyDictionary<int, string>? grantedBy = null,
        // 시전자 uid -> 이름. 「남이 준 버프」 섹션의 행 부제를 채운다. 못 찾은 uid는 "플레이어 {uid}"로 떨어진다.
        IReadOnlyDictionary<int, string>? casterNames = null,
        // 소환수 엔티티 -> 주인 uid. 피해 경로(ResolveActor)와 같은 규칙으로 접은 뒤에 "남이 걸었나"를 따진다.
        Func<int, int>? resolveActor = null)
    {
        var raws = new List<Raw>();
        foreach (KeyValuePair<string, AnalyzedSkill> entry in skills)
        {
            AnalyzedSkill s = entry.Value;
            // Keep a skill that dealt EITHER direct or DoT damage. A DoT-ONLY skill (DamageAmount 0,
            // DotDamageAmount > 0) is real: 대지의 징벌's DoT normalizes to a different rank code (…40) than its
            // direct hits (…50/57 → base …00), so its DoT arrives as its own DoT-only AnalyzedSkill. The old
            // `DamageAmount <= 0` skip dropped it, which is why the 대지의 징벌 지속피해 row was missing.
            if (s.DamageAmount <= 0 && s.DotDamageAmount <= 0)
            {
                continue;
            }

            int code = int.TryParse(entry.Key, out int parsed) ? parsed : s.SkillCode;
            string name = string.IsNullOrWhiteSpace(s.Name) ? code.ToString() : s.Name!;

            if (s.DamageAmount > 0)
            {
                raws.Add(new Raw
                {
                    Code = code,
                    RawCode = s.RawSkillCode,
                    Name = name,
                    Hits = s.Times,
                    Damage = s.DamageAmount,
                    IsDot = false,
                    Crit = s.CritTimes,
                    Strong = s.DoubleTimes,
                    Perfect = s.PerfectTimes,
                    Back = s.BackTimes,
                    Front = s.FrontTimes,
                    Parry = s.ParryTimes,
                    Flagged = s.FlaggedTimes,
                });
            }

            if (s.DotDamageAmount > 0)
            {
                raws.Add(new Raw { Code = code, Name = name + " - 지속", Hits = s.DotTimes, Damage = s.DotDamageAmount, IsDot = true });
            }
        }

        // totals: total damage includes DOT. Crit is a per-hit field present on EVERY hit, so its rate is over
        // all non-DOT hits. Back/강타(double)/완벽(perfect)/페리(parry) come from the special-flag byte, which
        // exists ONLY on flag-bearing (sw6) hits — non-directional hits (heals/buffs/passives, sw4) have no
        // flag byte, so those judgments are unmeasurable on them and must NOT dilute the denominator.
        long totalDamage = raws.Sum(r => r.Damage);
        int totalHits = raws.Where(r => !r.IsDot).Sum(r => r.Hits);
        int totalFlagged = raws.Where(r => !r.IsDot).Sum(r => r.Flagged);
        double TotalPct(Func<Raw, int> sel, int den) =>
            den > 0 ? Math.Round((double)raws.Where(r => !r.IsDot).Sum(sel) / den * 1000, MidpointRounding.AwayFromZero) / 10.0 : 0.0;

        raws.Sort((a, b) => b.Damage.CompareTo(a.Damage));
        IReadOnlyList<DetailSkillGroup> groups = BuildGroups(raws, totalDamage, grantedBy);

        return new DetailModel(
            totalDamage,
            contribution,
            TotalPct(r => r.Crit, totalHits),
            TotalPct(r => r.Strong, totalFlagged),
            TotalPct(r => r.Perfect, totalFlagged),
            TotalPct(r => r.Back, totalFlagged),
            TotalPct(r => r.Front, totalFlagged),
            TotalPct(r => r.Parry, totalFlagged),
            totalHits,
            combatMs,
            groups,
            BuildOwnBuffs(ownBuffs, uid, job, proc, casterNames, resolveActor),
            BuildDebuffs(bossDebuffs, uid),
            metrics);
    }

    private static IReadOnlyList<DetailSkillGroup> BuildGroups(
        List<Raw> raws, long totalDamage, IReadOnlyDictionary<int, string>? grantedBy)
    {
        var used = new bool[raws.Count];
        var groups = new List<DetailSkillGroup>();

        foreach ((int main, int[] children) in ChainGroups)
        {
            var ordered = new List<Raw>();
            foreach (int code in new[] { main }.Concat(children))
            {
                for (int i = 0; i < raws.Count; i++)
                {
                    if (!used[i] && Normalize(raws[i].Code) == code)
                    {
                        ordered.Add(raws[i]);
                        used[i] = true;
                    }
                }
            }

            if (ordered.Count >= 2)
            {
                Raw merged = Merge(ordered);
                groups.Add(new DetailSkillGroup(
                    ToRow(merged, totalDamage, grantedBy),
                    ordered.Select(r => ToRow(r, totalDamage, grantedBy)).ToList(),
                    HasChildren: true));
            }
            else
            {
                // <2 rows matched — release them back to singleton handling.
                foreach (Raw r in ordered)
                {
                    used[raws.IndexOf(r)] = false;
                }
            }
        }

        // Merge remaining rows that resolve to the SAME display name. The game splits some skills into two
        // damage codes — e.g. 심판의 번개's direct cast (17041250) and its field ticks (17040250) — that both
        // resolve to 심판의 번개 yet don't share a normalized code, so they showed as two rows. Merge by NAME,
        // NOT by base code, so two DISTINCT skills that share a slot (대지의 징벌 vs 대지의 축복, both 17_40) stay
        // separate. DoT ("- 지속") rows carry their own name, so they remain their own rows (as before).
        var remaining = new List<Raw>();
        for (int i = 0; i < raws.Count; i++)
        {
            if (!used[i])
            {
                remaining.Add(raws[i]);
            }
        }

        foreach (IGrouping<string, Raw> byName in remaining.Where(r => !r.IsDot).GroupBy(r => r.Name))
        {
            List<Raw> sameName = byName.ToList();
            if (sameName.Count >= 2)
            {
                Raw merged = Merge(sameName);
                groups.Add(new DetailSkillGroup(
                    ToRow(merged, totalDamage, grantedBy),
                    sameName.Select(r => ToRow(r, totalDamage, grantedBy)).ToList(),
                    HasChildren: true));
            }
            else
            {
                DetailSkillRow row = ToRow(sameName[0], totalDamage, grantedBy);
                groups.Add(new DetailSkillGroup(row, new[] { row }, HasChildren: false));
            }
        }

        foreach (Raw dot in remaining.Where(r => r.IsDot))
        {
            DetailSkillRow row = ToRow(dot, totalDamage, grantedBy);
            groups.Add(new DetailSkillGroup(row, new[] { row }, HasChildren: false));
        }

        return groups.OrderByDescending(g => g.Merged.Damage).ToList();
    }

    private static Raw Merge(List<Raw> rows)
    {
        // The merged row keeps the main (first) row's identity — including its raw code, so a chain group
        // shows the primary skill's specialization; children carry their own when expanded.
        var m = new Raw { Code = rows[0].Code, RawCode = rows[0].RawCode, Name = rows[0].Name };
        foreach (Raw r in rows)
        {
            m.Hits += r.Hits;
            m.Damage += r.Damage;
            m.Crit += r.Crit;
            m.Strong += r.Strong;
            m.Perfect += r.Perfect;
            m.Back += r.Back;
            m.Front += r.Front;
            m.Parry += r.Parry;
            m.Flagged += r.Flagged;
        }

        return m;
    }

    private static DetailSkillRow ToRow(
        Raw r, long totalDamage, IReadOnlyDictionary<int, string>? grantedBy) => new(
        r.Code,
        r.Name,
        r.Hits,
        r.Damage,
        r.IsDot,
        r.IsDot ? null : PctInt(r.Crit, r.Hits),
        r.IsDot ? null : PctInt(r.Strong, r.Hits),
        r.IsDot ? null : PctInt(r.Perfect, r.Hits),
        // Back/front are directional judgments carried only by flag-bearing hits, so they divide by the
        // flag-bearing count (like the summary), not every hit — a skill with non-directional hits mixed in
        // otherwise reads an artificially low back/front rate.
        r.IsDot ? null : PctInt(r.Back, r.Flagged),
        r.IsDot ? null : PctInt(r.Front, r.Flagged),
        r.IsDot ? null : PctInt(r.Parry, r.Hits),
        totalDamage > 0 ? r.Damage / (double)totalDamage : 0.0,
        r.IsDot ? null : SkillSpecialization.Decode(r.RawCode),
        GrantedByOf(r.Code, grantedBy));

    /// <summary>이 스킬 행이 남의 효과로 들어온 것이면 그 사람 이름. 코드만으로는 판정할 수 없다 — 검성 본인의
    /// 흡혈의 검 타격도 같은 코드를 쓴다 — 그래서 호출자가 "이 전투에서 나에게 그 버프를 건 사람"을 이미
    /// 해석해 넘겨준 표만 참조한다.</summary>
    private static string? GrantedByOf(int code, IReadOnlyDictionary<int, string>? grantedBy)
    {
        if (grantedBy == null || grantedBy.Count == 0) return null;
        int source = PartySynergyCatalog.GrantedDamageSource(code);
        return source != 0 && grantedBy.TryGetValue(source, out string? name) ? name : null;
    }

    /// <summary>
    /// The buffs on this player, in three sections: their own class buffs, then consumables (scrolls/potions),
    /// then — last, and hidden unless the reader asks for it — the ones another player cast on them.
    /// <para>「남이 준 버프」는 기본적으로 접혀 있다. 기본 화면이 답하는 질문은 여전히 "내가 뭘 유지했나"이고,
    /// 호법성의 진언이 전원에게 90%로 떠 있다는 사실은 그 질문에 아무 답도 주지 않는다(v2.6.4에서 이 섹션을
    /// 통째로 걷어낸 이유다). 다만 서포터의 기여를 읽고 싶을 때가 있어 옵트인으로 되살렸다.</para>
    /// <para>섹션 순서에서 <b>맨 뒤</b>인 것은 의도다 — 공대에서 이 섹션은 수십 줄이라, 중간에 끼우면 몇 줄
    /// 안 되는 「그 외」(소모품)가 그 아래로 밀려 사라진다. 맨 뒤면 체크박스를 켜도 기존 표는 그대로 있고
    /// 새 행이 뒤에 붙기만 한다.</para>
    /// </summary>
    private static IReadOnlyList<DetailBuffSection> BuildOwnBuffs(
        IReadOnlyList<OperatingData> buffs, int uid, JobClass? job, DetailProcRow? proc,
        IReadOnlyDictionary<int, string>? casterNames, Func<int, int>? resolveActor)
    {
        int jobPrefix = job != null ? JobClassInfo.BasicSkillCode(job.Value) / 1_000_000 : -1;
        var mine = new List<DetailBuffRow>();
        var other = new List<DetailBuffRow>();
        var granted = new List<DetailBuffRow>();

        // Every row here landed on this player, so every row is a buff or a self-state — a player skill's debuff
        // goes on its target, never on its caster. Debuffs live in the boss's list (BuildDebuffs).
        foreach (OperatingData b in buffs)
        {
            // 소환수가 주인에게 건 버프는 "남이 준 것"이 아니다 — 피해 경로가 소환수 딜을 주인에게 접는 것과
            // 같은 규칙으로 먼저 접는다. 접지 않으면 정령성/치유성이 <b>혼자</b> 사냥해도 「남이 준 버프」가
            // 생기고, 그 행의 부제는 이름을 찾을 길이 없어 "플레이어 4713261" 같은 원시 엔티티 id 가 된다
            // (소환수 id 는 Contributors 에도 파티 스냅샷에도 절대 없다).
            int actor = resolveActor?.Invoke(b.ActorId) ?? b.ActorId;
            if (actor != uid)
            {
                // ⚠️ 접고 나서도 ActorId 에는 파티 검증이 없다. 그래서 이 섹션의 이름은 「파티원 버프」가 아니라
                // 「남이 준 버프」다 — 비파티원이 걸어 준 것도 여기로 들어올 수 있고, 파티원이라 우기면 거짓말이 된다.
                granted.Add(ToBuffRow(b, CasterNameOf(actor, casterNames)));
                continue;
            }

            DetailBuffRow row = ToBuffRow(b);
            if (jobPrefix > 0 && b.EffectiveJobPrefix == jobPrefix)
            {
                mine.Add(row);
            }
            else
            {
                other.Add(row);
            }
        }

        var sections = new List<DetailBuffSection>();
        // 발동형(회생의 계약 긴급 회복)은 가동률 정렬에서 빼고 "내 버프"의 가장 아래에 고정한다 — 도핑류가
        // 모이는 "그 외"가 아니라 직업 버프 섹션에 속하고, %가 아니라 횟수로 읽어야 하는 값이라서.
        AddSection(sections, "내 버프", mine,
            proc is null ? null : new DetailBuffRow(proc.Code, proc.Name, 0.0, proc.Description, proc.Count));
        AddSection(sections, "그 외", other);
        AddSection(sections, "남이 준 버프", granted, fromOtherPlayers: true);
        return sections;
    }

    /// <summary>시전자 uid -> 표시 이름. 못 찾으면 창 제목과 같은 규칙으로 "플레이어 {uid}" 를 쓴다 — 이름을
    /// 못 붙였다고 행을 버리면 저장된 전투에서 피해를 안 낸 서포터의 버프가 통째로 사라진다.</summary>
    private static string CasterNameOf(int actorId, IReadOnlyDictionary<int, string>? names) =>
        names != null && names.TryGetValue(actorId, out string? n) && !string.IsNullOrWhiteSpace(n)
            ? n
            : $"플레이어 {actorId}";

    /// <summary>The boss debuffs THIS player applied. One unlabelled section — every row has the same caster.</summary>
    private static IReadOnlyList<DetailBuffSection> BuildDebuffs(IReadOnlyList<OperatingData> debuffs, int uid)
    {
        var rows = debuffs
            .Where(d => d.ActorId == uid)
            .Select(d => ToBuffRow(d))
            .OrderByDescending(r => r.Rate)
            .ToList();

        var sections = new List<DetailBuffSection>();
        AddSection(sections, string.Empty, rows);
        return sections;
    }

    private static void AddSection(
        List<DetailBuffSection> sections, string label, List<DetailBuffRow> rows, DetailBuffRow? tail = null,
        bool fromOtherPlayers = false)
    {
        List<DetailBuffRow> ordered = rows.OrderByDescending(r => r.Rate).ToList();
        if (tail != null)
        {
            ordered.Add(tail); // always last — it is a count, not a rate, so it must not sort among them
        }

        if (ordered.Count == 0)
        {
            return;
        }

        sections.Add(new DetailBuffSection(label, ordered, fromOtherPlayers));
    }

    private static DetailBuffRow ToBuffRow(OperatingData b, string? casterName = null) =>
        new(b.Code, b.Name, Math.Clamp(b.OperatingRate, 0.0, 100.0), ReadableBuffText(b.Effect, b.Summary),
            Level: b.Level, CasterName: casterName);

    private static int Normalize(int code) =>
        code is >= 11_000_000 and <= 19_999_999 ? code / 10_000 * 10_000 : code;

    private static int PctInt(int num, int den) =>
        den > 0 ? (int)Math.Round((double)num / den * 100, MidpointRounding.AwayFromZero) : 0;

    // The datamined buff `effect` is the most informative (has the actual +N% values) but frequently
    // carries game markup — <desc_point>/<chat_combat> tags wrapping {abe:..}/{se:..} value references
    // that only the client can resolve. The `summary` is the human-readable version ("이동 속도 증가").
    // So: prefer a markup-free effect (keeps the numbers), else the clean summary (clear intent), else
    // the effect with markup stripped. Makes every buff/debuff tooltip readable — old and new alike.
    private static string ReadableBuffText(string? effect, string? summary)
    {
        if (!string.IsNullOrWhiteSpace(effect) && !HasMarkup(effect)) return effect.Trim();
        if (!string.IsNullOrWhiteSpace(summary) && !HasMarkup(summary)) return summary.Trim();
        return StripMarkup(effect ?? summary);
    }

    private static bool HasMarkup(string s) => s.Contains('<') || s.Contains('{');

    private static string StripMarkup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string s = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "");       // <desc_point>, <chat_combat>, </>
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\{[^}]*\}\s*%?", "");          // {abe:..}/{se:..} value refs (+ trailing %)
        s = System.Text.RegularExpressions.Regex.Replace(s, "[ \t]{2,}", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, " +(?=[,.\n])", "");             // stray space before punctuation
        return s.Trim();
    }
}
