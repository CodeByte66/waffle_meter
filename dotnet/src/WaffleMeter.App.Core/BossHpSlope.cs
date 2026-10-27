namespace WaffleMeter.App.Core;

/// <summary>
/// 처치까지 남은 시간을 <b>보스 HP 기울기</b>로 잰다 — 어비스 필드보스 군중전 전용(<see cref="KillEtaEstimator"/>).
/// <para>왜 파티 DPS 로 못 나누는가: 군중전에서 서버는 남의 직접타를 보내지 않는다(실측 2026-10-27 카이라:
/// 46명 직접타 0건, DoT 만 보임). 미터의 '파티 DPS' 합은 실제 감소 속도의 일부라 ETA 가 몇 배 길게 나온다.
/// 보스 HP 는 반대로 전원의 피해가 다 반영된 값이므로 그 기울기가 곧 진짜 속도다. 최대 HP 를 쓰지 않으므로
/// 최대 HP 래치 오류에도 영향받지 않는다.</para>
/// <para>틱마다 <see cref="Observe"/> 를 부른다. 표본은 <b>HP 값이 바뀐 틱에만</b> 쌓는다 — 그래야 HP 가 안 오는
/// 구간(시야 밖, 무적 페이즈)이 '속도 0'으로 섞이지 않고 '모름'(<see cref="StaleMs"/>)으로 갈린다.</para>
/// <para>UI 스레드 전용, 상태 있음(표시 계층의 한 인스턴스). 순수 시계 주입 — 테스트가 시간을 몬다.</para>
/// </summary>
public sealed class BossHpSlope
{
    /// <summary>회귀 창. 실측 카이라(100초 킬)에서 10/15/20초 창의 오차가 비슷했고, 20초가 13초짜리 시야 공백을
    /// 넘어 이어 준다.</summary>
    public const long WindowMs = 20_000;

    /// <summary>표본이 이만큼 퍼져야 숫자를 낸다 — 처음 몇 초의 값은 군중이 모이는 중이라 2~4배 틀린다.</summary>
    public const long MinSpanMs = 8_000;

    public const int MinSamples = 3;

    /// <summary>HP 가 이만큼 안 바뀌면 '모름'. 군중전은 초당 수 회 바뀐다 — 5초 정지는 시야 밖이거나 무적이다.</summary>
    public const long StaleMs = 5_000;

    /// <summary>이보다 긴 ETA 는 숫자로서 의미가 없다(딜이 거의 안 들어가는 중).</summary>
    public const long MaxEtaMs = 3_600_000;

    private readonly List<(long T, long Hp)> _samples = new();
    private (int Id, int Code) _key;
    private long _lastHp = -1;
    private long _lastChangeAt;

    public void Reset()
    {
        _samples.Clear();
        _key = default;
        _lastHp = -1;
        _lastChangeAt = 0;
    }

    /// <summary>한 틱의 관측. 처치까지 남은 ms, 또는 숫자를 낼 근거가 없으면 null.</summary>
    public long? Observe(int targetId, int mobCode, long remainHp, long maxHp, long nowMs)
    {
        if (targetId <= 0 || remainHp <= 0)
        {
            Reset();
            return null;
        }

        if (_key != (targetId, mobCode))
        {
            Reset();
            _key = (targetId, mobCode);
        }

        if (remainHp != _lastHp)
        {
            // 위로 튀면(회복·리셋·재스폰 보정) 그 앞의 기울기는 다른 곡선이다 — 처음부터 다시 잰다.
            // 문턱은 최대 HP 의 0.5% (최대를 모르면 1) — 지터·역순 프레임 한 개로는 안 지운다.
            long jump = Math.Max(1, maxHp / 200);
            if (_lastHp > 0 && remainHp > _lastHp + jump)
            {
                _samples.Clear();
            }

            _samples.Add((nowMs, remainHp));
            _lastHp = remainHp;
            _lastChangeAt = nowMs;
        }

        // 창 밖 표본을 버리되, 창 경계 바로 앞의 표본 하나는 닻으로 남긴다 — 시야 공백 직후에 창 안 표본이
        // 짧아져 숫자가 깜빡 꺼지는 것을 막는다.
        while (_samples.Count > 2 && _samples[1].T < nowMs - WindowMs)
        {
            _samples.RemoveAt(0);
        }

        if (nowMs - _lastChangeAt > StaleMs
            || _samples.Count < MinSamples
            || _samples[^1].T - _samples[0].T < MinSpanMs)
        {
            return null;
        }

        // 최소제곱 기울기(HP/초). 틱 표본이라 끝점 두 개만 쓰면 지터를 그대로 먹는다.
        long t0 = _samples[0].T;
        double n = _samples.Count, sx = 0, sy = 0;
        foreach ((long t, long hp) in _samples)
        {
            sx += (t - t0) / 1000.0;
            sy += hp;
        }

        double mx = sx / n, my = sy / n, sxx = 0, sxy = 0;
        foreach ((long t, long hp) in _samples)
        {
            double dx = (t - t0) / 1000.0 - mx;
            sxx += dx * dx;
            sxy += dx * (hp - my);
        }

        if (sxx <= 0)
        {
            return null;
        }

        double perSecond = -sxy / sxx;
        if (perSecond <= 0)
        {
            return null;
        }

        double eta = _lastHp / perSecond * 1000.0 - (nowMs - _lastChangeAt);
        if (eta > MaxEtaMs)
        {
            return null;
        }

        return (long)Math.Max(0, eta);
    }
}
