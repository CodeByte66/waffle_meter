using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 라이브 전투 <b>중</b> 미터 행을 클릭하는 경로가 예외 없이 읽히는지 고정한다(dps-calculation#1).
/// <para><see cref="DpsCalculator"/>는 "캡처 소비자 스레드 전용"이라는 계약으로 살아왔고, 리포트 틱은 블로킹
/// <c>Dispatcher.Invoke</c> 펜스 안이라 실제로 안전하다. 그 계약을 깨는 경로가 딱 하나 있다 —
/// <c>MeterRowsView.OnRowClick</c> → <c>new DetailsViewModel(...)</c> 생성자가 <b>UI 스레드에서 즉시</b>
/// <see cref="DpsCalculator.BattleDetails"/>/<see cref="DpsCalculator.GetDpsSeries"/>를 부르는데, 그건 펜스
/// 밖이고 그동안 파서는 같은 사전에 계속 키를 꽂는다. 그러면 열거가
/// <c>InvalidOperationException</c>("Collection was modified")으로 끝나고 게임 위에 전체 스택트레이스
/// 대화상자가 뜬다("보스 처치 직후 클릭하면 가끔 상세창이 안 열린다").</para>
/// <para>⚠️ 이 테스트가 요구하는 것은 "읽기가 안전하다"이지 "예외가 안 보인다"가 아니다 —
/// try/catch 로 덮는 수정은 증상만 숨기고 상세창은 그대로 안 열린다.</para>
/// <para><b>이 모양이어야 하는 이유(튜닝하려는 사람에게).</b> ① 읽는 쪽이 오래 걸릴 만큼 캐시를 미리 키운다 —
/// 작은 사전은 열거가 한 타임슬라이스 안에 끝나 락을 빼도 거의 안 부딪친다. ② 쓰는 쪽은 <b>읽는 쪽이 끝날
/// 때까지</b> 돈다 — 쓰기 총량을 고정하면 락 없는 빌드에서 쓰기가 먼저 끝나 아무것도 안 겹친다.
/// ③ 쓰기에 SpinWait 을 끼워 메모리 폭주를 막는다. 셋 중 하나라도 빼면 이 테스트는 결함을 놓친다
/// (실측: 이 모양은 락을 빼면 3/3 실패, 캐시가 작으면 3/3 통과).</para>
/// </summary>
public sealed class LiveCacheReadSafetyTests
{
    private const int Instance = 100;
    private const int BossCode = 2301008;
    private const int Me = 5001;

    [Fact]
    public void Opening_the_detail_view_mid_battle_never_throws()
    {
        // 소비자 스레드는 전투를 돌리고(매 틱 새 스킬 키·새 초 버킷이 꽂힌다), 테스트 스레드는 UI 스레드가
        // 하는 일을 그대로 한다 — 행 클릭 시 DetailsViewModel 생성자가 부르는 두 조회.
        long[] now = { 1_000_000 };
        var dm = new DataManager { Clock = () => now[0] };
        dm.LoadMobs(new Dictionary<int, Mob> { [BossCode] = new Mob(BossCode, "보스", Boss: true) });
        dm.SaveNickname(Me, "본인", isExecutor: true, server: 2003, jobByte: 34);
        dm.SaveMobId(Instance, BossCode);
        dm.MobHp(Instance, 1_000_000_000);
        dm.StartBattle(Instance);
        var calc = new DpsCalculator(dm);

        int hit = 0;

        void Beat()
        {
            // 초당 한 대씩 — 스킬 코드도 매번 새것이라 스킬 표(_cachedSkillDetails)와 초 버킷(_cachedDpsBuckets)이
            // 둘 다 구조적으로 커진다.
            now[0] += 1_000;
            dm.SaveDamage(
                new ParsedDamagePacket
                {
                    ActorId = Me,
                    TargetId = Instance,
                    Damage = 100,
                    SkillCode = 16080000 + hit * 10,
                    Timestamp = now[0],
                },
                dm.CurrentEpoch());
            hit++;
        }

        for (int i = 0; i < 20_000; i++)
        {
            Beat(); // 읽기가 한 타임슬라이스에 안 끝날 만큼 미리 키운다 (위 ① 참조)
        }

        calc.GetDps();

        Exception? failure = null;
        var readerDone = new ManualResetEventSlim(false);
        var consumer = new Thread(() =>
        {
            while (!readerDone.IsSet)
            {
                Beat();
                calc.GetDps(); // 누적이 일어나는 자리 — 여기서 두 캐시에 새 키가 꽂힌다
                Thread.SpinWait(200);
            }
        }) { IsBackground = true };

        consumer.Start();
        try
        {
            for (int round = 0; round < 40; round++)
            {
                DpsReport live = calc.GetRecentData();
                calc.BattleDetails(live, Me);
                calc.GetDpsSeries(Me, live.BattleStart, live.BattleEnd + 1_000);
            }
        }
        catch (Exception e)
        {
            failure = e;
        }
        finally
        {
            readerDone.Set();
        }

        consumer.Join();
        Assert.Null(failure);
    }
}
