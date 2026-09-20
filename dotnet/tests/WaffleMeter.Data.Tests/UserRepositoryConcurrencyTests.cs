using System.Collections.Concurrent;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// UserRepository 는 최소 세 스레드가 동시에 두드리는데(meter-consumer 의 패킷 Save, 공식 조회 콜백의
/// ThreadPool Save, 업로드 워커의 조회) 2026-09-18 이전까지 락이 하나도 없었다. 피해가 조용한 게 핵심이다:
/// 열거 중 변경으로 던진 예외를 ConsumeLoop / StatsUploadQueue.WorkerLoop 의 catch 가 삼켜, 전투 1건이
/// 업로드 스킵 사유에도 안 남고 사라진다.
/// <para>아래 테스트는 "동작한다"가 아니라 <b>그 레이스가 되살아나면 빨개지는</b> 것들이다 — UserRepository 의
/// <c>_gate</c> 락을 지우면 ①②는 "Collection was modified" 로, ③은 인덱스 카운트 어긋남(또는 행 hang)으로
/// 깨진다. 락을 걷어내고 싶어지면 여기부터 돌려 봐라.</para>
/// </summary>
public sealed class UserRepositoryConcurrencyTests
{
    // 반복 횟수는 "레이스 창이 충분히 열리는 최소"로 잡았다. 락이 있으면 전부 1초 안쪽이고, 락이 없으면
    // 보통 수백 회 안에 터진다. 줄이면 테스트가 회귀를 놓치기 시작한다.
    private const int EnumerationIterations = 20_000;
    private const int PendingIterations = 10_000;
    private const int IndexIterations = 2_000;

    /// <summary>
    /// ① FindByNicknameAndServer 의 폴백(<c>_storage.Values.LastOrDefault</c>)이 다른 스레드의 Save 와
    /// 겹쳐도 던지지 않는다. 조회 이름을 일부러 "한 번도 등록되지 않은 닉"으로 둬서 _idIndex 를 반드시
    /// 미스시키고 폴백 열거를 100% 밟게 만든다 — 이게 M-9 의 확정 크래시 경로였다.
    /// </summary>
    [Fact]
    public void Storage_fallback_lookup_survives_concurrent_saves()
    {
        var repo = new UserRepository();
        const int server = 1019;
        const string neverRegistered = "없는닉네임";

        RunRacing(
            EnumerationIterations,
            // 두 작성자: 매 회 새 uid 를 넣고(추가) 상한 3을 넘긴 오래된 uid 를 퇴출한다(삭제).
            // 추가·삭제 둘 다 Dictionary 의 _version 을 올리므로 열거자가 확실히 무효화된다.
            i => repo.Save(100_000 + i, new User(100_000 + i, "동료" + (i % 7), server)),
            i => repo.Save(600_000 + i, new User(600_000 + i, "동료" + (i % 7), server)),
            _ => repo.FindByNicknameAndServer(neverRegistered, server),
            _ => repo.FindByNicknameAndServer(neverRegistered, server));
    }

    /// <summary>
    /// ② RemovePendingByName 의 <c>_pendingByNameServer.Where(...)</c> 열거가 다른 스레드의
    /// SavePending/RemovePending 과 겹쳐도 던지지 않는다. 정확 (닉,서버) 키를 일부러 빗나가게 해서
    /// 폴백 열거로 내려가게 한다.
    /// </summary>
    [Fact]
    public void Pending_name_fallback_survives_concurrent_pending_writes()
    {
        var repo = new UserRepository();
        const int server = 1019;

        RunRacing(
            PendingIterations,
            // pending 맵을 넣었다 뺐다 한다. 같은 키에 덮어쓰기만 하면 Dictionary 의 _version 이 안 올라
            // 레이스가 재현되지 않으므로 반드시 추가+삭제 쌍이어야 한다.
            i =>
            {
                var pending = new User(200_000 + (i % 64), "펜딩" + (i % 64), server, power: 12_345);
                repo.SavePending(pending);
                repo.RemovePending(pending);
            },
            // "타인"은 pending 에 없으므로 exact 키가 빗나가고 Where 열거로 내려간다.
            i => repo.Save(300_000 + i, new User(300_000 + i, "타인", server)));
    }

    /// <summary>
    /// ③ 같은 신원 아래로 두 스레드가 동시에 등록해도 _idIndex 의 List&lt;int&gt; 가 꼬이지 않는다.
    /// 락이 없으면 List 의 Add/RemoveAt 이 서로를 덮어써 살아남는 uid 수가 상한(3)과 어긋나거나
    /// FindByNicknameAndServer 가 이미 퇴출된 uid 를 돌려준다 — 예외 없이 <b>엉뚱한 uid</b> 가 나오는,
    /// M-9 에서 가장 조용한 결과다.
    /// </summary>
    [Fact]
    public void Identity_index_stays_bounded_and_resolvable_under_concurrent_saves()
    {
        var repo = new UserRepository();
        const string name = "Me";
        const int server = 2003;

        RunRacing(
            IndexIterations,
            i => repo.Save(10_000 + i, new User(10_000 + i, name, server)),
            i => repo.Save(50_000 + i, new User(50_000 + i, name, server)),
            _ => repo.FindByNicknameAndServer(name, server),
            i => repo.Get(10_000 + i));

        // executor 가 0이라 퇴출 면제가 없다 → 어떤 순서로 섞였든 이 신원으로 살아남는 uid 는 정확히 상한만큼.
        List<int> alive = Enumerable.Range(10_000, IndexIterations)
            .Concat(Enumerable.Range(50_000, IndexIterations))
            .Where(repo.Exist)
            .ToList();
        Assert.Equal(3, alive.Count);

        foreach (int uid in alive)
        {
            User? survivor = repo.Get(uid);
            Assert.NotNull(survivor);
            Assert.Equal(name, survivor!.Nickname);
            Assert.Equal(server, survivor.Server);
        }

        // 조회가 "살아 있는" uid 를 돌려주는지 — 퇴출된 uid 를 돌려주면 그 행의 딜이 통째로 엉뚱해진다.
        User? found = repo.FindByNicknameAndServer(name, server);
        Assert.NotNull(found);
        Assert.Contains(found!.Id, alive);
    }

    /// <summary>
    /// 워커들을 배리어로 동시에 출발시켜 <paramref name="iterations"/> 회씩 돌리고, 아무도 던지지 않았고
    /// 아무도 멈추지 않았음을 단언한다. Join 타임아웃은 락 순서 역전(데드락)이나 사전 손상으로 인한
    /// 무한 루프를 잡기 위한 것이다 — 통과 시에는 절대 도달하지 않는다.
    /// </summary>
    private static void RunRacing(int iterations, params Action<int>[] workers)
    {
        var errors = new ConcurrentBag<Exception>();
        using var gun = new Barrier(workers.Length);
        Thread[] threads = workers.Select(work => new Thread(() =>
        {
            gun.SignalAndWait();
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    work(i);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        })
        {
            IsBackground = true,
        }).ToArray();

        foreach (Thread t in threads)
        {
            t.Start();
        }

        foreach (Thread t in threads)
        {
            Assert.True(t.Join(TimeSpan.FromSeconds(60)), "워커가 끝나지 않았다 — 데드락 또는 사전 손상 의심");
        }

        Assert.True(
            errors.IsEmpty,
            "동시 접근에서 예외가 났다 (UserRepository 의 _gate 락이 사라졌는지 확인하라):\n"
                + string.Join("\n", errors.Select(e => e.GetType().Name + ": " + e.Message)));
    }
}
