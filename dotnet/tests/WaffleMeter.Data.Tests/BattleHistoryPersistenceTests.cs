using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 전투 기록이 <b>앱 재시작을 넘어 살아남는다</b>는 계약(판정 19·34).
/// <para>🔑 종전에는 <see cref="BattleLogRepository"/> 가 메모리 리스트 하나였다. 그래서 리플레이 파일은 디스크에
/// 남는데 <b>그 전투의 기록 행은 없는</b> 상태가 재시작마다 났다 — 사용자에겐 "기록이 사라졌다"로 읽힌다.</para>
/// <para>여기서 지키는 것은 세 가지다: ① 다시 켜도 같은 전투가 같은 순서로 보인다 ② 상세 화면이 읽는 동결
/// 스냅샷(스킬·버프·시계열)이 <b>값까지</b> 돌아온다 ③ 못 읽는 파일이 섞여도 기동을 막지 않는다.</para>
/// </summary>
public sealed class BattleHistoryPersistenceTests : IDisposable
{
    private readonly string _dir;

    public BattleHistoryPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wm_history_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private BattleLogRepository Restarted()
    {
        var repo = new BattleLogRepository();
        repo.AttachStore(new BattleHistoryStore(_dir));
        return repo;
    }

    private static DpsLog Battle(int targetId, int mobCode, long start, long end, double damage = 1_000_000, bool dummy = false)
    {
        var skills = new Dictionary<int, Dictionary<string, AnalyzedSkill>>
        {
            [7] = new()
            {
                ["강타"] = new AnalyzedSkill
                {
                    SkillCode = 11_010_047, RawSkillCode = 110_100_471, Name = "격파의 맹타",
                    DamageAmount = 512_345, Times = 42, CritTimes = 17, BackTimes = 9, EligibleDamage = 500_000,
                },
            },
        };
        var buffRates = new Dictionary<int, List<OperatingData>>
        {
            [7] = [new OperatingData(110_000_101, "노련한 반격", "요약", "효과", 0.83, 7, BaseCode: 110_000_100, JobPrefix: 11, Level: 5)],
        };

        var report = new DpsReport
        {
            Contributors =
            [
                new User(7, "와플", 2003, JobClass.GLADIATOR, isExecutor: true, power: 912_345),
                new User(8, "버터", 2003, JobClass.CLERIC, power: 880_000),
            ],
            Information =
            {
                [7] = new DpsInformation(damage, damage / 60.0, 62.5, 60.0),
                [8] = new DpsInformation(damage / 2, damage / 120.0, 37.5, 36.0),
            },
            BattleStart = start,
            BattleEnd = end,
            Target = new MobInfo(targetId, new Mob(mobCode, "보스", Boss: true, IsDummy: dummy), 0, 2_700_000_000L),
            ExecutorId = 7,
            TargetInstanced = true,
            TrialDifficulty = new TrialDifficulty(4, 2, 1, 3),
            PartySlots = new Dictionary<int, int> { [7] = 1, [8] = 6 },
            PartyRosterSize = 10,
            SkillDetailsSnapshot = skills,
            BuffRates = buffRates,
            BossBuffRates = [new OperatingData(12_000_101, "중독", null, null, 0.42, targetId)],
            DpsSeries = new Dictionary<int, long[]> { [7] = [100, 200, 300], [8] = [50, 60, 70] },
            BuffIntervals = new Dictionary<int, List<BuffTimeline>>
            {
                [7] = [new BuffTimeline(110_000_101, "노련한 반격", 7, 110_000_100, 11, [(start + 100, start + 900), (start + 1_500, end)], 5)],
            },
            SkillCasts = new Dictionary<int, List<SkillCastRow>>
            {
                [7] = [new SkillCastRow(11_010_047, "격파의 맹타", start + 50, StartsCooldown: true)],
            },
            DpsMetrics = new Dictionary<int, DpsMetricResult> { [7] = new(12_345.5, 15_000.25, 2_654.75, 0, 98_765) },
            PartyIdentitiesSnapshot = [new RosterMember { Nickname = "버터", Server = 2003, Slot = 6 }],
            PartySnapshot = [new User(8, "버터", 2003, JobClass.CLERIC)],
            BattleFinished = true,
        };

        return new DpsLog
        {
            Report = report,
            SummonMap = new Dictionary<int, int> { [901] = 7 },
            Packets = [],
            SkillDetails = skills,
            BuffRates = buffRates,
            BossBuffRates = report.BossBuffRates,
        };
    }

    [Fact]
    public void Battles_survive_a_restart_in_order()
    {
        BattleLogRepository first = Restarted();
        first.Save(Battle(10, 2_301_601, 1_000_000, 1_060_000));
        first.Save(Battle(11, 2_301_602, 2_000_000, 2_090_000));

        IReadOnlyList<DpsLog> after = Restarted().GetAll();

        Assert.Equal(2, after.Count);
        Assert.Equal(2_301_601, after[0].Report.Target!.Mob.Code);
        Assert.Equal(2_301_602, after[1].Report.Target!.Mob.Code);
        Assert.Equal(1_000_000, after[0].Report.BattleStart);
    }

    /// <summary>
    /// 🔑 상세 화면은 <b>동결된 스냅샷</b>만 읽는다(라이브 계산기가 아니라). 그래서 껍데기만 돌아오면
    /// 기록은 보이는데 상세가 전부 비는, 더 나쁜 상태가 된다.
    /// </summary>
    [Fact]
    public void The_frozen_detail_snapshot_comes_back_with_its_values()
    {
        BattleLogRepository first = Restarted();
        first.Save(Battle(10, 2_301_601, 1_000_000, 1_060_000));

        DpsReport r = Restarted().GetAll()[0].Report;

        AnalyzedSkill skill = r.SkillDetailsSnapshot[7]["강타"];
        Assert.Equal(512_345, skill.DamageAmount);
        Assert.Equal(42, skill.Times);
        Assert.Equal("격파의 맹타", skill.Name);

        Assert.Equal(0.83, r.BuffRates[7][0].OperatingRate, 6);
        Assert.Equal(5, r.BuffRates[7][0].Level);
        Assert.Equal(11, r.BuffRates[7][0].EffectiveJobPrefix);

        Assert.Equal([100L, 200L, 300L], r.DpsSeries[7]);
        Assert.Equal(15_000.25, r.DpsMetrics[7].Rdps, 6);
        Assert.Equal(2_700_000_000L, r.Target!.MaxHp);      // int32 포화가 없는지
        Assert.Equal(new TrialDifficulty(4, 2, 1, 3), r.TrialDifficulty);
        Assert.Equal(6, r.PartySlots[8]);
        Assert.Equal(10, r.PartyRosterSize);
        Assert.Equal(JobClass.CLERIC, r.Contributors.Single(u => u.Id == 8).Job);
        Assert.True(r.Contributors.Single(u => u.Id == 7).IsExecutor);
    }

    /// <summary>
    /// 🔴 <c>(long Start, long End)</c> 는 프로퍼티가 아니라 <b>필드</b>다. 기본 설정의 System.Text.Json 은
    /// 이걸 <c>{}</c> 로 써서, 버프 구간이 <b>아무 오류 없이</b> 통째로 사라진다. 그래프의 버프 레인이 비는
    /// 종류의 손상이라 저장할 땐 아무도 모른다.
    /// </summary>
    [Fact]
    public void Buff_spans_are_not_silently_emptied()
    {
        BattleLogRepository first = Restarted();
        first.Save(Battle(10, 2_301_601, 1_000_000, 1_060_000));

        BuffTimeline line = Restarted().GetAll()[0].Report.BuffIntervals[7][0];

        Assert.Equal(2, line.Spans.Count);
        Assert.Equal((1_000_100L, 1_000_900L), line.Spans[0]);
        Assert.Equal((1_001_500L, 1_060_000L), line.Spans[1]);
        Assert.Equal(5, line.Level);
    }

    [Fact]
    public void A_merge_replaces_the_stored_battle_rather_than_adding_one()
    {
        BattleLogRepository first = Restarted();
        first.Save(Battle(10, 2_301_601, 1_000_000, 1_060_000, damage: 1_000_000));
        first.Save(Battle(10, 2_301_601, 1_000_000, 1_070_000, damage: 3_000_000)); // 같은 전투, 피해 더 큼

        IReadOnlyList<DpsLog> after = Restarted().GetAll();

        Assert.Single(after);
        Assert.Equal(3_000_000, after[0].Report.Information[7].Amount);
    }

    [Fact]
    public void The_cap_evicts_the_oldest_from_disk_too()
    {
        BattleLogRepository first = Restarted();
        for (int i = 0; i < 33; i++)
        {
            // 인스턴스 id 를 매번 바꿔 병합 규칙을 피한다 — 정원만 보는 테스트다.
            first.Save(Battle(100 + i, 2_301_601, 1_000_000 + (i * 300_000), 1_060_000 + (i * 300_000)));
        }

        IReadOnlyList<DpsLog> after = Restarted().GetAll();

        Assert.Equal(30, after.Count);
        Assert.Equal(1_000_000 + (3 * 300_000), after[0].Report.BattleStart); // 가장 오래된 3건이 밀려났다
        Assert.Equal(30, Directory.GetFiles(_dir, "*.json.gz").Length);
    }

    /// <summary>허수아비 정원은 따로 센다 — 디스크에서도 마찬가지여야 연습 30번이 보스 기록을 밀어내지 않는다.</summary>
    [Fact]
    public void Dummy_runs_do_not_evict_real_battles_from_disk()
    {
        BattleLogRepository first = Restarted();
        first.Save(Battle(10, 2_301_601, 1_000_000, 1_060_000));
        for (int i = 0; i < 32; i++)
        {
            first.Save(Battle(50, 9_999_999, 2_000_000 + (i * 100_000), 2_030_000 + (i * 100_000), dummy: true));
        }

        IReadOnlyList<DpsLog> after = Restarted().GetAll();

        Assert.Equal(31, after.Count);                                       // 진짜 1 + 허수아비 30
        Assert.Equal(2_301_601, after[0].Report.Target!.Mob.Code);           // 진짜 전투가 살아 있다
    }

    [Fact]
    public void A_reset_clears_the_files_too()
    {
        BattleLogRepository first = Restarted();
        first.Save(Battle(10, 2_301_601, 1_000_000, 1_060_000));
        first.Flush();

        Assert.Empty(Restarted().GetAll());
        Assert.Empty(Directory.GetFiles(_dir, "*.json.gz"));
    }

    /// <summary>손상된 파일 하나가 나머지 기록까지 데려가면 안 된다 — 영속화는 편의이지 집계의 전제가 아니다.</summary>
    [Fact]
    public void A_corrupt_file_costs_only_that_battle()
    {
        BattleLogRepository first = Restarted();
        first.Save(Battle(10, 2_301_601, 1_000_000, 1_060_000));
        first.Save(Battle(11, 2_301_602, 2_000_000, 2_090_000));

        File.WriteAllText(Path.Combine(_dir, "1.json.gz"), "not gzip at all");

        IReadOnlyList<DpsLog> after = Restarted().GetAll();

        Assert.Single(after);
        Assert.Equal(2_301_602, after[0].Report.Target!.Mob.Code);
    }

    /// <summary>다른 포맷 번호로 쓰인 파일은 건너뛴다 — 반쯤 읽힌 전투를 화면에 올리는 것보다 없는 게 낫다.</summary>
    [Fact]
    public void A_file_from_another_format_version_is_skipped()
    {
        BattleLogRepository first = Restarted();
        first.Save(Battle(10, 2_301_601, 1_000_000, 1_060_000));

        string path = Directory.GetFiles(_dir, "*.json.gz").Single();
        using (FileStream raw = File.Create(path))
        using (var gz = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionLevel.Fastest))
        using (var w = new StreamWriter(gz))
        {
            w.Write("{\"Schema\":999,\"Report\":{\"BattleStart\":1}}");
        }

        Assert.Empty(Restarted().GetAll());
    }

    /// <summary>스토어를 붙이지 않으면 예전 그대로 메모리 전용이다 — 테스트와 진단 도구가 그 모양을 쓴다.</summary>
    [Fact]
    public void Without_a_store_nothing_touches_the_disk()
    {
        var repo = new BattleLogRepository();
        repo.Save(Battle(10, 2_301_601, 1_000_000, 1_060_000));

        Assert.Single(repo.GetAll());
        Assert.False(Directory.Exists(_dir));
    }
}
