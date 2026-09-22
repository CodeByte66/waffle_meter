using System.Globalization;
using System.Windows.Threading;
using WaffleMeter.App.Core;
using WaffleMeter.Capture;

namespace WaffleMeter.App.Wpf;

/// <summary>
/// App-scoped clock that fires the 슈고 페스타 reminder. A 1s <see cref="DispatcherTimer"/> (UI thread)
/// evaluates the schedule once per wall-clock minute — the first tick that observes a new minute, so it is
/// robust to timer drift — and invokes <c>onShugo(lead)</c> when a configured lead is due. Settings are read
/// live, so turning the alarm off stops future fires without a restart. Mirrors OverlayController's
/// long-lived UI-thread timer.
/// <para>The clock is a <see cref="DateTimeOffset"/> rather than a <see cref="DateTime"/> so the one schedule
/// that is anchored to the SERVER's timezone (<see cref="KairaAlarm"/>, KST) and the ones that are the user's
/// own wall clock (슈고 페스타, 커스텀 알람) can both be derived from it without a <c>Kind</c>-dependent
/// conversion in the middle.</para>
/// </summary>
public sealed class AlarmController
{
    private readonly MeterSettings _settings;
    private readonly Action<int> _onShugo;
    private readonly Action<CustomAlarm> _onCustom;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<IReadOnlyDictionary<int, long>>? _fieldBossTimers;
    private readonly Action<FieldBossAlarm.Due>? _onFieldBoss;
    private readonly Func<bool>? _combatActive;
    private readonly Action<int, DateTime>? _onKaira;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private string _lastMinute = string.Empty;
    // dedup: each (boss, respawn, lead) fires once. Value = the respawn target ms, so entries can be pruned
    // once the respawn has passed — bounding the map without a blanket clear (which would re-fire other
    // alerts still inside their lead window).
    private readonly Dictionary<string, long> _shownFieldBoss = new();

    public AlarmController(
        MeterSettings settings,
        Action<int> onShugo,
        Action<CustomAlarm> onCustom,
        Func<DateTimeOffset>? now = null,
        Func<IReadOnlyDictionary<int, long>>? fieldBossTimers = null,
        Action<FieldBossAlarm.Due>? onFieldBoss = null,
        Func<bool>? combatActive = null,
        Action<int, DateTime>? onKaira = null)
    {
        _onKaira = onKaira;
        _settings = settings;
        _onShugo = onShugo;
        _onCustom = onCustom;
        _now = now ?? (() => DateTimeOffset.Now);
        _fieldBossTimers = fieldBossTimers;
        _onFieldBoss = onFieldBoss;
        _combatActive = combatActive;
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        DateTimeOffset now = _now();
        long nowMs = now.ToUnixTimeMilliseconds();
        // 슈고 페스타와 커스텀 알람은 사용자가 자기 벽시계로 고른 리마인더라 로컬 시각 그대로 본다.
        // 카이라만 서버 콘텐츠 일정이라 KST 격자를 쓴다 — KairaAlarm 의 doc 참고.
        DateTime local = now.LocalDateTime;

        // Field-boss reminder: evaluate EVERY second (not the minute gate) so a lead fires at the right
        // time, de-duplicated by (boss, respawn, lead) so it fires once.
        EvaluateFieldBoss(nowMs);

        string minute = local.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        if (minute == _lastMinute)
        {
            return; // already evaluated this minute
        }

        _lastMinute = minute;

        if (_settings.ShugoAlarmEnabled
            && ShugoAlarm.DueLead(local, ShugoAlarm.EnabledLeads(_settings)) is int lead)
        {
            _onShugo(lead);
        }

        // 감시자 카이라: 서버가 리젠 시각을 안 보내는 유일한 보스라 슈고 페스타처럼 시계로 돈다. 다만
        // 주기가 다르다 — 2026-09-02 패치로 4시간 확정 출현이 됐고(앵커는 KST 1시 — KairaAlarm 참고),
        // 그래서 슈고와 달리 서버 시간대에 고정한다. ⚠️ HourlyAlarm.DueLead 로 되돌리지 마라 — 그건 슈고의
        // 매시 정각용이고, 로컬 시로 4시간 격자를 재면 UTC+8 사용자는 여섯 슬롯이 전부 어긋난다.
        if (_settings.KairaAlarmEnabled && _onKaira is not null
            && KairaAlarm.DueLead(nowMs, KairaAlarm.EnabledLeads(_settings)) is int kairaLead)
        {
            DateTime spawn = DateTimeOffset.FromUnixTimeMilliseconds(KairaAlarm.NextSpawnMs(nowMs)).LocalDateTime;
            _onKaira(kairaLead, spawn);
        }

        foreach (CustomAlarm alarm in _settings.CustomAlarms)
        {
            if (CustomAlarmSchedule.IsDue(alarm, local))
            {
                _onCustom(alarm);
            }
        }
    }

    private void EvaluateFieldBoss(long nowMs)
    {
        if (!_settings.FieldBossAlarmEnabled || _fieldBossTimers is null || _onFieldBoss is null)
        {
            return;
        }

        // Prune entries whose respawn has passed: they can never be due again (DueAlerts skips remaining<=0),
        // so dropping them bounds the map without ever clearing a still-active lead marker.
        if (_shownFieldBoss.Count > 0)
        {
            foreach (string key in _shownFieldBoss.Where(kv => kv.Value <= nowMs).Select(kv => kv.Key).ToList())
            {
                _shownFieldBoss.Remove(key);
            }
        }

        // Suppress while recording an active combat, if enabled — skip WITHOUT marking the alert shown, so it
        // can still fire once the fight ends (as long as it's still inside its lead window).
        bool muteForCombat = _settings.FieldBossAlarmMuteInCombat && _combatActive?.Invoke() == true;
        HashSet<int> disabled = _settings.FieldBossDisabledCodes;

        foreach (FieldBossAlarm.Due d in FieldBossAlarm.DueAlerts(_fieldBossTimers(), nowMs, _settings.FieldBossLeads))
        {
            if (FieldBossCatalog.HasOwnAlarm(d.Code))
            {
                continue; // 감시자 카이라 has its own 4-hour-grid reminder — never double-fire here
            }

            if (disabled.Contains(d.Code))
            {
                continue; // unchecked in the boss picker
            }

            if (muteForCombat)
            {
                continue; // don't fire mid-fight; also don't dedup so it can fire after the fight
            }

            if (_shownFieldBoss.TryAdd(FieldBossAlarm.Key(d), d.TargetMs))
            {
                _onFieldBoss(d);
            }
        }
    }
}
