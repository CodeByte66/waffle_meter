namespace WaffleMeter.Data;

/// <summary>One row of the skill-cooldown overlay: a skill the local player has actually used (or that the
/// server has reported a cooldown for) this session, plus where its cooldown stands right now.
/// </summary>
/// <param name="GroupId">The shared-cooldown group this skill belongs to. 🔑 이것은 <b>행의 정체가 아니다</b> —
/// 행 키는 그 스킬 자신의 base 코드(픽커가 쓰는 키와 같다)이고, 공유 쿨 그룹의 두 스킬은 <b>칸을 하나씩</b>
/// 갖되 <b>쿨 상태를 공유</b>한다. 종전 문구는 "두 스킬이 한 행"이라고 적혀 있었는데, 그 전제로 프리필이
/// base 코드로 칸을 만들고 보고는 그룹 키로 들어오면서 권성의 7칸이 쿨 도는 내내 '준비됨'으로 남았다.</param>
/// <param name="DisplayCode">The last wire code seen for this group, used to pick the icon.</param>
/// <param name="Name">Display name from the catalog.</param>
/// <param name="RemainingMs">Time left, 0 when ready. 충전형은 <b>다음 1회가 차기까지</b>의 시간이라, 1회 이상
/// 남아 <see cref="IsReady"/>인 동안에도 0이 아닐 수 있다(가득 차면 0).</param>
/// <param name="TotalMs">The full cooldown this character actually has (from the cast frame), i.e. the ring's
/// denominator. 0 when only a correction has been seen and the total is still unknown. 충전형은 1회 재충전 시간.</param>
/// <param name="IsReady">Whether the skill can be recast now. 충전형은 1회라도 남았으면 true.</param>
/// <param name="Job">Job band (11–19), for grouping.</param>
/// <param name="Order">Stable position inside the job.</param>
/// <param name="IsStigma">스티그마 스킬인가. 기본 순서가 직업 안에서 일반을 먼저, 스티그마를 나중에 세운다 —
/// 카탈로그 순서(base 코드 오름차순)만으로는 둘이 뒤섞여 픽커의 일반/스티그마 묶음과 어긋난다.</param>
/// <param name="Charges">충전형 스킬의 지금 남은 횟수. 충전형이 아니거나 <b>아직 와이어가 충전형이라고 말하지
/// 않았으면</b> -1 — 카탈로그만으로는 정하지 않는다(충전형은 특화를 찍었을 때만이다).</param>
/// <param name="MaxCharges">충전형의 최대 횟수(모르면 0).</param>
public readonly record struct SkillCooldownView(
    int GroupId,
    int DisplayCode,
    string Name,
    long RemainingMs,
    long TotalMs,
    bool IsReady,
    int Job,
    int Order,
    bool IsStigma = false,
    int Charges = -1,
    int MaxCharges = 0);
