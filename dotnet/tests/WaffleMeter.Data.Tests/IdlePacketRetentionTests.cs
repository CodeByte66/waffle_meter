using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 대기(유휴) 중 패킷 링버퍼의 보존 규칙을 고정한다.
/// <para><b>고치기 전의 결함</b>(battle-lifecycle#3): 전투가 한 번 끝나면 <c>CurrentTarget()</c>이 -1로 굳고,
/// <c>DpsCalculator.GetDps</c>가 <b>매 틱</b>(기본 500ms) <c>FlushPacket()</c>을 돌려 <b>모든 타깃</b>의
/// 링버퍼를 통째로 비웠다. 그래서 다음 보스에 넣은 오프너(교전 토글보다 먼저 들어간 타격)가 토글이 오기 전에
/// 이미 지워져 누적 피해·기여도·DPS 분자에서 빠졌다 — <c>ActivePacketCutoff</c>(토글−1000ms)가 admit 해도
/// 돌아올 것이 없었다. 더 큰 갈래는 소급 승격(<c>PromoteUnresolvedStart</c>, TTL 60초) 대기 구간으로,
/// 그 내내 -1 이라 토글~스폰 사이 파티 전체 피해가 소각되고 시작 시각만 back-date 돼
/// <b>분모는 길고 분자는 빈 전투</b>가 저장·업로드됐다.</para>
/// <para>⚠️ 그 통짜 비우기는 유휴 중 <b>GC</b> 노릇도 겸하고 있었다(대기 중에도 잡몹 피해가 무제한으로 쌓이고
/// 치우는 자리가 거기뿐이었다). 그래서 대체물은 "안 치운다"가 아니라 <b>시간 기준 부분 정리</b>다 —
/// 아래 두 축(오프너는 살고, 오래된 것은 죽고, 열린 전투 창은 안 건드린다)을 함께 고정한다.</para>
/// </summary>
public sealed class IdlePacketRetentionTests
{
    private const int FirstBoss = 100;
    private const int NextBoss = 200;
    private const int BossCode = 2301008;
    private const int Me = 5001;

    private static ParsedDamagePacket Hit(int target, long ts, int damage = 1000) =>
        new() { ActorId = Me, TargetId = target, Damage = damage, SkillCode = 16080000, Timestamp = ts };

    [Fact]
    public void An_opener_that_lands_while_idle_survives_until_the_combat_toggle()
    {
        long[] now = { 1_000_000 };
        var dm = new DataManager { Clock = () => now[0] };
        dm.LoadMobs(new Dictionary<int, Mob> { [BossCode] = new Mob(BossCode, "보스", Boss: true) });
        dm.SaveNickname(Me, "본인", isExecutor: true, server: 2003, jobByte: 34);
        dm.SaveMobId(FirstBoss, BossCode);
        dm.SaveMobId(NextBoss, BossCode);
        var calc = new DpsCalculator(dm);

        // ---- 첫 보스를 잡고 대기 상태로 들어간다 (여기서 CurrentTarget()이 -1로 굳는다) ----
        dm.MobHp(FirstBoss, 5000);
        dm.StartBattle(FirstBoss);
        dm.SaveDamage(Hit(FirstBoss, now[0] + 100), dm.CurrentEpoch());
        calc.GetDps();
        now[0] += 3_000;
        dm.EndBattle(FirstBoss);
        calc.GetDps(); // 종료 전이 틱 — 통짜 비우기는 여기 <b>한 번</b>만 돈다

        // ---- 대기 틱이 여러 번 돈다 (예전에는 이 틱들이 매번 전 타깃 링버퍼를 비웠다) ----
        for (int i = 0; i < 4; i++)
        {
            now[0] += 500;
            calc.GetDps();
        }

        // ---- 다음 보스에 오프너를 넣는다: 교전 토글보다 300ms 앞선 타격 ----
        now[0] += 2_000;
        long openerAt = now[0];
        dm.SaveDamage(Hit(NextBoss, openerAt, damage: 7_777), dm.CurrentEpoch());

        now[0] += 200;
        calc.GetDps(); // 오프너와 토글 사이에 낀 대기 틱 — 예전에는 바로 여기서 오프너가 지워졌다

        // ---- 이제 교전 토글이 온다 ----
        now[0] += 100;
        long toggledAt = now[0];
        dm.MobHp(NextBoss, 5000);
        dm.StartBattle(NextBoss);
        DpsReport report = calc.GetDps();

        Assert.Equal(NextBoss, report.Target?.Id);
        Assert.Equal(7_777, report.Information[Me].Amount); // ← 회귀 지점: 오프너가 분자에 들어 있다
        // 시작도 토글보다 앞으로 앵커된다. 정확히 오프너 시각은 아니다 — StartAnchor 가 back-date 를
        // StartBackdateLimitMs(250ms)까지만 허용하기 때문이고, 그건 별개의 의도된 상한이다.
        Assert.True(report.BattleStart < toggledAt);
        Assert.True(report.BattleStart >= openerAt);
    }

    [Fact]
    public void A_backdated_promotion_still_finds_the_damage_that_landed_while_it_waited()
    {
        // 소급 승격 경로: 시작 토글은 왔는데 mobCode가 없어 거부됐고, 스폰이 5초 뒤에 도착한다. 그 5초간의
        // 피해가 링버퍼에 남아 있어야 back-date 된 창의 <b>분자</b>가 채워진다 — 예전에는 그 5초 내내 매 틱
        // 플러시가 돌아 마지막 한 틱분만 남고 나머지가 소각됐다.
        long[] now = { 1_000_000 };
        var dm = new DataManager { Clock = () => now[0] };
        dm.LoadMobs(new Dictionary<int, Mob> { [BossCode] = new Mob(BossCode, "보스", Boss: true) });
        dm.SaveNickname(Me, "본인", isExecutor: true, server: 2003, jobByte: 34);
        var calc = new DpsCalculator(dm);

        // ⚠️ 먼저 전투를 하나 끝내 <b>대기 상태</b>(CurrentTarget()==-1)로 들어가야 한다. 기동 직후는 0이라
        // GetDps 가 더 앞에서 반환하고 플러시 경로를 아예 안 타므로, 그 상태로는 이 결함이 재현되지 않는다.
        dm.SaveMobId(FirstBoss, BossCode);
        dm.MobHp(FirstBoss, 5000);
        dm.StartBattle(FirstBoss);
        dm.SaveDamage(Hit(FirstBoss, now[0] + 100), dm.CurrentEpoch());
        calc.GetDps();
        now[0] += 2_000;
        dm.EndBattle(FirstBoss);
        calc.GetDps();

        now[0] += 1_000;
        long toggledAt = now[0];
        dm.RememberUnresolvedBattleStart(NextBoss); // 파서가 mob_code_missing으로 거부한 시작 토글
        calc.GetDps();

        long dealt = 0;
        for (int i = 0; i < 10; i++) // 5초 동안 500ms 틱마다 때린다
        {
            now[0] += 500;
            dm.SaveDamage(Hit(NextBoss, now[0], damage: 1_000), dm.CurrentEpoch());
            dealt += 1_000;
            calc.GetDps(); // 대기 틱 — 여기서 링버퍼가 비워지면 안 된다
        }

        dm.SaveMobId(NextBoss, BossCode); // 늦게 도착한 스폰 → 토글 시각으로 back-date 승격
        Assert.Equal(toggledAt, dm.CurrentBattleStart());

        DpsReport report = calc.GetDps();
        Assert.Equal(dealt, report.Information[Me].Amount); // 분모만 길고 분자가 빈 전투가 아니다
    }

    [Fact]
    public void Retention_drops_only_what_no_future_battle_could_admit()
    {
        // GC 축. 다음 전투의 ActivePacketCutoff 는 아무리 이르게 잡아도 "지금 − 오프너 창"보다 앞설 수 없으므로
        // 그보다 오래된 패킷은 어떤 전투도 admit 할 수 없다 = 버려도 되는 것이 정확히 그것뿐이다.
        var repo = new PacketRepository();
        repo.Save(Hit(FirstBoss, 1_000_000));
        repo.Save(Hit(FirstBoss, 1_050_000));
        repo.Save(Hit(NextBoss, 1_050_000));

        repo.PruneOlderThan(1_020_000);

        Assert.Equal(1_050_000, Assert.Single(repo.Get(FirstBoss)!).Timestamp); // 오래된 것만 빠졌다
        Assert.Single(repo.Get(NextBoss)!);

        // 링 자체가 비면 타깃도 사전에서 사라진다 — 필드에서 몇 시간 도는 동안 링 <b>개수</b>가 무제한으로
        // 늘어나는 것이 대기 틱 통짜 비우기를 없앤 뒤의 유일한 성장 축이기 때문이다.
        repo.PruneOlderThan(2_000_000);
        Assert.False(repo.Exist(FirstBoss));
        Assert.False(repo.Exist(NextBoss));
    }

    [Fact]
    public void An_open_battle_window_is_never_pruned()
    {
        // ⚠️ 진행 중인 전투의 앞부분을 지우면, 캐시를 시퀀스 0부터 다시 누적하는 읽기 쪽이 이미 잘린 창을 본다.
        // 그래서 cutoff 는 CurrentBattleStart − 오프너 창을 절대 넘지 못한다.
        var repo = new PacketRepository();
        repo.SaveCurrentBattleStart(1_000_000);
        repo.Save(Hit(FirstBoss, 999_500));   // 토글 500ms 전 오프너 — 이것도 그 전투의 것이다
        repo.Save(Hit(FirstBoss, 1_030_000));

        repo.PruneOlderThan(1_020_000); // "20초 전 것까지 버려라" — 열린 창이 이를 막는다

        Assert.Equal(2, repo.Get(FirstBoss)!.Count);
    }

    [Fact]
    public void A_read_window_after_pruning_never_replays_a_packet_twice()
    {
        // 정리는 _totalAdded 를 건드리지 않는다. 줄이면 WindowFrom 의 firstSequence 가 뒤로 밀려, 읽는 쪽이
        // 이미 누적한 패킷을 다시 받아 피해가 두 번 계산된다.
        var repo = new PacketRepository();
        repo.Save(Hit(FirstBoss, 1_000_000));
        repo.Save(Hit(FirstBoss, 1_000_100));
        PacketWindow first = repo.GetWindow(FirstBoss, 0);
        Assert.Equal(2, first.Packets.Count);

        repo.PruneOlderThan(1_000_050);
        repo.Save(Hit(FirstBoss, 1_000_200));

        PacketWindow next = repo.GetWindow(FirstBoss, first.NextSequence);
        Assert.Equal(1_000_200, Assert.Single(next.Packets).Timestamp);
        Assert.False(next.DroppedBeforeStart);
    }
}
