using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 충전형 스킬(특화로 2~3회 충전되는 스킬)의 쿨타임 오버레이 스펙.
/// <para>충전형은 일반 쿨 경로로 오지 않는다 — 시전 프레임의 총 쿨이 0이고 0x3847 도 안 온다. 그래서 종전 미터는
/// 검성 집중 막기를 2회를 다 써도 늘 '준비됨'으로 그렸다(오너 제보). 상태는 0x3802 시전 꼬리(남은 횟수 · 다음
/// 1회까지), 0x3822 정정, 0x5100 스냅샷으로 오고, 1회가 차면 다음 1회 타이머가 1회 재충전 시간으로 다시 돈다.</para>
/// <para>여기 쓰는 시각·값은 2026-10-16 오너 캡처(검성, 집중 막기 가득 찬 2회 → 2회 사용 → 0회 → 1회 복구)의
/// 도착 간격 그대로다.</para>
/// </summary>
public sealed class SkillChargeTests
{
    private const int Me = 2641;
    private const int Ally = 2085;

    private const int FocusedBlock = 11_110_000;      // 검성 집중 막기 — 카탈로그 auto=2
    private const int FocusedBlockWire = 11_110_040;  // 오너 캡처의 특화 변종(와이어가 실제로 싣는 코드)
    private const int FocusedBlockBuff = 111_100_071; // 같은 그룹으로 접히는 9자리 버프 코드(회색 베일 확인용)
    private const int ProtectShield = 12_110_000;     // 수호성 보호의 방패 — 카탈로그 auto=3
    private const int ProtectShieldWire = 12_110_010;  // 2회 변종(Skill.json AutoLoadCount 2)
    private const int ProtectShieldTriple = 12_110_050; // 이 family 에서 3회인 유일한 변종 — 카탈로그 auto=3 은 family 최대치다
    private const int GroundSmash = 19_090_000;       // 권성 지면 강타 — [폭주](19100000)와 공유 그룹
    private const int GroundSmashBerserk = 19_100_000;

    private const int GladiatorJobByte = 5;
    private const int FighterJobByte = 45;

    private static CooldownCatalog ShippedCatalog()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "Assets", "json", "cooldown_catalog.json");
            if (File.Exists(candidate))
            {
                return CooldownCatalog.Load(candidate);
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("cooldown_catalog.json not found above " + AppContext.BaseDirectory);
    }

    private static DataManager WithSelf(long t0, int jobByte = GladiatorJobByte)
    {
        var dm = new DataManager { Clock = () => t0 };
        dm.LoadCooldownCatalog(ShippedCatalog());
        dm.SaveNickname(Me, "본인", isExecutor: true, server: 3, jobByte: jobByte);
        return dm;
    }

    private static SkillCooldownView Row(DataManager dm, long atMs, int code = FocusedBlock)
        => Assert.Single(dm.ActiveCooldowns(atMs), r => r.GroupId == code);

    [Fact]
    public void The_owner_capture_walks_two_one_zero_and_refills_on_its_own()
    {
        // 도착 간격(ms): 첫 시전 0 · 정정 +351 · 두 번째 시전 +10,051 · 정정 +10,401 · 1회 복구 통지 +14,050.
        long t0 = 1_792_083_908_565;
        DataManager dm = WithSelf(t0);

        dm.SaveSkillCharges(FocusedBlockWire, 1, 14_250, t0, Me);
        SkillCooldownView r = Row(dm, t0 + 100);
        Assert.Equal(1, r.Charges);
        Assert.Equal(2, r.MaxCharges);
        Assert.True(r.IsReady);                 // 1회 남았다 — 누를 수 있다
        Assert.Equal(14_150, r.RemainingMs);    // 다음 1회까지(오버레이가 옅은 파이로 그린다)
        Assert.Equal(14_250, r.TotalMs);

        dm.SaveSkillCharges(FocusedBlockWire, 1, 13_800, t0 + 351, 0);
        Assert.Equal(14_250, Row(dm, t0 + 400).TotalMs); // 정정의 잔여가 분모를 줄이지 않는다

        dm.SaveSkillCharges(FocusedBlockWire, 0, 4_100, t0 + 10_051, Me);
        r = Row(dm, t0 + 10_100);
        Assert.Equal(0, r.Charges);
        Assert.False(r.IsReady);                // 0회 — 이게 제보된 "쿨이 안 뜬다"의 자리다
        Assert.Equal(4_051, r.RemainingMs);
        Assert.Equal(14_250, r.TotalMs);

        dm.SaveSkillCharges(FocusedBlockWire, 0, 3_650, t0 + 10_401, 0);
        Assert.False(Row(dm, t0 + 14_000).IsReady);

        // 1회 복구 통지가 오기 전이라도 다음 1회가 차는 순간 1회로 넘어간다(정정 기준 t0 + 14,051).
        r = Row(dm, t0 + 14_052);
        Assert.Equal(1, r.Charges);
        Assert.True(r.IsReady);

        dm.SaveSkillCharges(FocusedBlockWire, 1, 14_250, t0 + 14_050, 0);
        r = Row(dm, t0 + 20_000);
        Assert.Equal(1, r.Charges);
        Assert.Equal(14_250 - (20_000 - 14_050), r.RemainingMs);

        r = Row(dm, t0 + 14_050 + 14_250);
        Assert.Equal(2, r.Charges);             // 가득 참
        Assert.True(r.IsReady);
        Assert.Equal(0, r.RemainingMs);
    }

    [Fact]
    public void Without_any_notice_the_charges_roll_forward_and_stop_at_the_maximum()
    {
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);

        dm.SaveSkillCharges(FocusedBlockWire, 1, 14_250, t0, Me);
        dm.SaveSkillCharges(FocusedBlockWire, 0, 4_100, t0 + 10_050, Me);

        Assert.Equal(0, Row(dm, t0 + 14_149).Charges);

        SkillCooldownView r = Row(dm, t0 + 14_151);
        Assert.Equal(1, r.Charges);
        Assert.Equal(14_249, r.RemainingMs); // 1회가 찬 순간 다음 1회가 1회 재충전 시간으로 다시 돈다

        r = Row(dm, t0 + 14_150 + 14_250);
        Assert.Equal(2, r.Charges);
        Assert.Equal(0, r.RemainingMs);

        Assert.Equal(2, Row(dm, t0 + 10_000_000).Charges); // 최대치에서 멈춘다
    }

    [Fact]
    public void The_buff_overlay_grays_a_charge_skill_only_at_zero_charges()
    {
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);
        dm.SaveUseBuff(Me, FocusedBlockBuff, t0, t0 + 60_000, 60_000, actorId: Me);
        bool OnCooldown(long at) => Assert.Single(dm.ActiveOwnerBuffs(at)).OnCooldown;

        dm.SaveSkillCharges(FocusedBlockWire, 1, 14_250, t0, Me);
        Assert.False(OnCooldown(t0 + 1_000)); // 1회 남았다 — 회색이면 거짓말이다

        dm.SaveSkillCharges(FocusedBlockWire, 0, 4_100, t0 + 2_000, Me);
        Assert.True(OnCooldown(t0 + 2_001));  // 충전형은 시전 값도 잠정값이 아니다 — 즉시 회색
        Assert.False(OnCooldown(t0 + 6_101)); // 1회가 찼다
    }

    [Fact]
    public void A_full_notice_is_the_maximum_even_against_the_catalog()
    {
        // 보호의 방패는 카탈로그가 3회라고 하지만, 서버가 "가득 참 2회"라고 했으면 2회다.
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);

        dm.SaveSkillCharges(ProtectShieldWire, 2, 0, t0, 0);
        SkillCooldownView r = Row(dm, t0 + 1, ProtectShield);
        Assert.Equal(2, r.Charges);
        Assert.Equal(2, r.MaxCharges);

        dm.SaveSkillCharges(ProtectShieldWire, 1, 17_500, t0 + 1_000, Me);
        Assert.Equal(2, Row(dm, t0 + 1_001, ProtectShield).MaxCharges);
        Assert.Equal(2, Row(dm, t0 + 1_000_000, ProtectShield).Charges); // 3회로 넘어가지 않는다
    }

    [Fact]
    public void The_catalog_is_the_maximum_while_the_wire_has_said_nothing_about_it()
    {
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);

        dm.SaveSkillCharges(ProtectShieldTriple, 2, 17_500, t0, Me);
        SkillCooldownView r = Row(dm, t0 + 1, ProtectShield);
        Assert.Equal(2, r.Charges);
        Assert.Equal(3, r.MaxCharges);
        Assert.Equal(3, Row(dm, t0 + 17_501, ProtectShield).Charges);
    }

    [Fact]
    public void A_bare_cooldown_snapshot_does_not_overwrite_a_charge_skill()
    {
        // 0x3847·0x5100 의 맨 쿨 값엔 충전 횟수를 말할 자리가 없다. 받아들이면 0회인 칸이 '준비됨'으로 뒤집힌다.
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);

        dm.SaveSkillCharges(FocusedBlockWire, 0, 4_100, t0, Me);
        dm.SaveCooldown(FocusedBlock, 0, t0 + 500, actorId: 0);

        SkillCooldownView r = Row(dm, t0 + 600);
        Assert.Equal(0, r.Charges);
        Assert.False(r.IsReady);
    }

    [Fact]
    public void A_cast_carrying_a_real_cooldown_turns_the_skill_back_into_a_plain_one()
    {
        // 특화를 바꿔 충전형이 풀리면 시전 프레임이 총 쿨을 싣는다 — 그게 모드를 되돌리는 유일한 와이어 증거다.
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);

        dm.SaveSkillCharges(FocusedBlockWire, 1, 14_250, t0, Me);
        dm.SaveCooldown(11_110_000, 20_000, t0 + 30_000, actorId: Me, fromCast: true);

        SkillCooldownView r = Row(dm, t0 + 31_000);
        Assert.Equal(-1, r.Charges);
        Assert.False(r.IsReady);
        Assert.Equal(20_000, r.TotalMs); // 충전형 시절의 1회 재충전 시간을 분모로 이어받지 않는다
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_skill_snapshot_sets_the_count_whatever_order_its_records_come_in(bool variantFirst)
    {
        // 0x5100 은 같은 스킬의 base 레코드(충전 필드 없음)와 특화 레코드(충전 2회)를 함께 싣는다.
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);

        var baseRecord = new LearnedSkill(11_110_000, 13, 0);
        var variant = new LearnedSkill(11_110_020, 13, 0, 2);
        dm.ApplyMySkillSnapshot(variantFirst ? [variant, baseRecord] : [baseRecord, variant], t0);

        SkillCooldownView r = Row(dm, t0 + 1);
        Assert.Equal(2, r.Charges);
        Assert.Equal(2, r.MaxCharges);
        Assert.True(r.IsReady);
    }

    [Fact]
    public void A_snapshot_taken_mid_recharge_reads_the_autoload_fields_not_the_cooltime()
    {
        // 존 이동 0x5100: 1회 남음 + 다음 1회까지 9초(_autoload_time). 충전형의 _cooltime 은 0 이다 — 그걸 재충전
        // 잔여로 읽으면 "타이머 없음 = 가득 참"으로 오인해 최대치가 1로 깎이고 9초 뒤에도 2로 안 오른다.
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);

        dm.ApplyMySkillSnapshot([new LearnedSkill(11_110_000, 13, 0), new LearnedSkill(11_110_020, 13, 0, 1, 9_000)], t0);

        SkillCooldownView r = Row(dm, t0 + 1);
        Assert.Equal(1, r.Charges);
        Assert.Equal(2, r.MaxCharges);
        Assert.True(r.IsReady);
        Assert.Equal(8_999, r.RemainingMs);
        Assert.Equal(2, Row(dm, t0 + 9_000).Charges);
    }

    [Fact]
    public void A_snapshot_at_zero_charges_is_on_cooldown()
    {
        // 0회는 횟수 필드가 생략되고 _autoload_time 만 온다 — 파서가 Charges 0 으로 넘긴다.
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);

        dm.ApplyMySkillSnapshot([new LearnedSkill(11_110_020, 13, 0, 0, 9_000)], t0);

        SkillCooldownView r = Row(dm, t0 + 1);
        Assert.Equal(0, r.Charges);
        Assert.False(r.IsReady);
        Assert.Equal(8_999, r.RemainingMs);
    }

    [Fact]
    public void A_snapshot_with_no_charge_record_for_the_group_ends_charge_mode()
    {
        // 특화를 해제했다: 스냅샷에 그 그룹의 충전 레코드가 없다. 옛 횟수 배지가 다음 시전까지 남으면 안 된다.
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);

        dm.SaveSkillCharges(FocusedBlockWire, 2, 0, t0, 0);
        Assert.Equal(2, Row(dm, t0 + 1).Charges);

        dm.ApplyMySkillSnapshot([new LearnedSkill(11_110_000, 13, 18_000)], t0 + 60_000);

        SkillCooldownView r = Row(dm, t0 + 61_000);
        Assert.Equal(-1, r.Charges);
        Assert.False(r.IsReady);
        Assert.Equal(17_000, r.RemainingMs);
    }

    [Fact]
    public void Charge_rows_wait_for_the_self_to_be_recognised()
    {
        // 확정 전엔 본인 시전(횟수를 깎는 쪽)은 스테이징되는데 본인 전용 0x3822(채우는 쪽)는 바로 들어온다 —
        // 그 반쪽만 그리면 0회가 '1회·준비됨'으로 보인다(2026-08-08 궁성 코퍼스).
        long t0 = 1_000_000;
        var dm = new DataManager { Clock = () => t0 };
        dm.LoadCooldownCatalog(ShippedCatalog());

        dm.SaveSkillCharges(FocusedBlockWire, 1, 7_900, t0, 0);           // 0x3822: 1회가 찼다
        dm.SaveSkillCharges(FocusedBlockWire, 0, 7_850, t0 + 50, Me);     // 바로 그 1회를 썼다(누구 것인지 아직 모름)

        Assert.DoesNotContain(dm.ActiveCooldowns(t0 + 100), r => r.GroupId == FocusedBlock);

        dm.SaveNickname(Me, "본인", isExecutor: true, server: 3, jobByte: GladiatorJobByte);
        SkillCooldownView row = Row(dm, t0 + 100);
        Assert.Equal(0, row.Charges);
        Assert.False(row.IsReady);
    }

    [Fact]
    public void A_party_members_charge_cast_is_not_mine()
    {
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);

        dm.SaveSkillCharges(FocusedBlockWire, 0, 4_100, t0, Ally);

        Assert.Equal(-1, Row(dm, t0 + 1).Charges); // 프리필 칸 그대로 — 충전형인지도 모른다
    }

    [Fact]
    public void A_charge_cast_before_the_self_is_known_is_kept_and_replayed()
    {
        long t0 = 1_000_000;
        var dm = new DataManager { Clock = () => t0 };
        dm.LoadCooldownCatalog(ShippedCatalog());

        dm.SaveSkillCharges(FocusedBlockWire, 0, 4_100, t0, Me);
        dm.SaveNickname(Me, "본인", isExecutor: true, server: 3, jobByte: GladiatorJobByte);

        SkillCooldownView r = Row(dm, t0 + 100);
        Assert.Equal(0, r.Charges);
        Assert.False(r.IsReady);
    }

    [Fact]
    public void A_charge_skill_the_wire_has_not_confirmed_shows_no_count()
    {
        // 카탈로그 auto>0 은 "특화를 찍으면 충전형"일 뿐이다. 와이어가 말하기 전엔 숫자를 띄우지 않는다.
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0);

        SkillCooldownView r = Row(dm, t0);
        Assert.Equal(-1, r.Charges);
        Assert.True(r.IsReady);
    }

    [Fact]
    public void Both_slots_of_a_shared_group_carry_the_same_charges()
    {
        // 권성 지면 강타와 [폭주]는 한 그룹이다 — 한쪽을 쓰면 두 칸의 횟수가 같이 준다.
        long t0 = 1_000_000;
        DataManager dm = WithSelf(t0, FighterJobByte);

        dm.SaveSkillCharges(19_090_010, 1, 8_000, t0, Me);

        SkillCooldownView a = Row(dm, t0 + 1, GroundSmash);
        SkillCooldownView b = Row(dm, t0 + 1, GroundSmashBerserk);
        Assert.Equal(1, a.Charges);
        Assert.Equal(1, b.Charges);
        Assert.Equal(a.RemainingMs, b.RemainingMs);
        Assert.Equal(a.MaxCharges, b.MaxCharges);
    }
}
