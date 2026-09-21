using System;
using System.IO;
using System.Linq;
using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.Data.Tests;

/// <summary>
/// 쿨타임 픽커는 인식된 직업 밴드의 카탈로그 스킬을 <b>전량</b> 프리필한다. 캐릭터가 그 스킬을 배웠는지 알
/// 방법이 없었기 때문인데, 실측 저숙련 부캐는 카탈로그 24개 중 15개만 갖고 있었다 — 나머지 9칸은 체크해도
/// 영원히 불이 안 들어온다. 0x5100 이 그 목록을 준다.
///
/// <para>🔴 <b>이 집합은 '축소 근거'가 아니라 '표시 힌트'다.</b> 저장 모수(<c>CooldownVisibility._all</c>)는
/// 카탈로그 그대로 두고 프리필 단계에서만 거른다 — 모수를 좁히면 캐릭터마다 모수가 달라져 프리셋 blob 의
/// 의미가 캐릭터 간에 깨지고, 칩 하나만 눌러도 다른 캐릭터의 숨김 설정이 증발한다(2026-08-21 사고 경로).</para>
///
/// <para>🔴 <b>수명주기가 함정이다.</b> 0x5100 은 0x3633(본인 로드)보다 <b>먼저</b> 온다 — 실측 51/51, 같은
/// 밀리초, 같은 LZ4 번들. "신원이 오면 비운다"로 짜면 방금 채운 집합이 같은 번들 안에서 매번 즉시 소거되고,
/// fail-open 이라 증상이 "그냥 아무 효과 없음"이라 눈치채기도 어렵다.</para>
/// </summary>
public sealed class LearnedSkillPrefillTests
{
    private const int Me = 100;
    private const int Cleric = 29;   // ConvertFromCode: 29..32 -> CLERIC

    private static long _now;

    /// <summary>실제 배포 카탈로그를 쓴다 — 여기 쓰는 코드가 카탈로그에서 사라지면 그것도 잡아야 할 회귀다.</summary>
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

    private static DataManager Meter()
    {
        _now = 1_000_000;
        var dm = new DataManager { Clock = () => _now };
        dm.LoadCooldownCatalog(ShippedCatalog());
        return dm;
    }

    private static void Load(DataManager dm, int uid, string nickname, int jobByte) =>
        dm.SaveNickname(uid, nickname, isExecutor: true, server: 2003, jobByte: jobByte);

    private static void Snapshot(DataManager dm, params int[] codes) =>
        dm.ApplyMySkillSnapshot(codes.Select(c => new LearnedSkill(c, 10, 0)).ToList(), _now);

    private static IReadOnlyList<int> PrefilledCodes(DataManager dm) =>
        dm.ActiveCooldowns(_now).Select(v => v.GroupId).ToList();

    [Fact]
    public void With_no_snapshot_the_whole_band_is_prefilled()
    {
        // fail-open. 0x5100 이 언제 올지 확정이 안 됐다 — 실전 던전 세션 존 전환 14회 중 2회뿐이다.
        DataManager dm = Meter();
        Load(dm, Me, "와플", Cleric);

        Assert.NotEmpty(PrefilledCodes(dm));
    }

    [Fact]
    public void A_snapshot_narrows_the_prefill_to_what_was_learned()
    {
        DataManager dm = Meter();
        Load(dm, Me, "와플", Cleric);
        IReadOnlyList<int> all = PrefilledCodes(dm);
        Assert.True(all.Count >= 2, "치유성 밴드에 칸이 둘 이상은 있어야 이 테스트가 의미가 있다");

        Snapshot(dm, all[0]);

        IReadOnlyList<int> narrowed = PrefilledCodes(dm);
        Assert.Contains(all[0], narrowed);
        Assert.DoesNotContain(all[1], narrowed);
    }

    [Fact]
    public void The_snapshot_survives_the_identity_packet_that_follows_it()
    {
        // 🔴 이게 고친 결함이다. 실제 와이어 순서(0x5100 → 0x3633, 같은 ms)를 그대로 재현한다.
        DataManager dm = Meter();
        Load(dm, Me, "와플", Cleric);
        IReadOnlyList<int> all = PrefilledCodes(dm);

        Snapshot(dm, all[0]);
        Load(dm, Me, "와플", Cleric);   // 같은 번들의 0x3633

        Assert.Single(PrefilledCodes(dm));
    }

    [Fact]
    public void A_re_instance_of_the_same_character_keeps_it()
    {
        // 존 이동으로 uid 만 새로 발급된 경우. 같은 캐릭터이므로 배운 스킬도 같다.
        DataManager dm = Meter();
        Load(dm, Me, "와플", Cleric);
        IReadOnlyList<int> all = PrefilledCodes(dm);
        Snapshot(dm, all[0]);

        Load(dm, 200, "와플", Cleric);

        Assert.Single(PrefilledCodes(dm));
    }

    [Fact]
    public void A_character_switch_drops_it()
    {
        DataManager dm = Meter();
        Load(dm, Me, "와플", Cleric);
        IReadOnlyList<int> all = PrefilledCodes(dm);
        Snapshot(dm, all[0]);
        Assert.Single(PrefilledCodes(dm));

        Load(dm, 300, "마이농", Cleric);

        // 새 캐릭터의 스냅샷은 아직 안 왔다 — 밴드 전량으로 돌아간다(fail-open).
        Assert.Equal(all.Count, PrefilledCodes(dm).Count);
    }

    [Fact]
    public void A_specialised_variant_folds_onto_its_base()
    {
        // 와이어는 특화 변형 코드를 싣는다. 카탈로그는 base 공간이라 접어서 비교해야 한다.
        DataManager dm = Meter();
        Load(dm, Me, "와플", Cleric);
        int baseCode = PrefilledCodes(dm)[0];

        dm.ApplyMySkillSnapshot([new LearnedSkill(baseCode + 40, 10, 0)], _now);

        Assert.Contains(baseCode, PrefilledCodes(dm));
    }

    [Fact]
    public void An_empty_snapshot_is_ignored_rather_than_emptying_the_picker()
    {
        DataManager dm = Meter();
        Load(dm, Me, "와플", Cleric);
        int before = PrefilledCodes(dm).Count;

        dm.ApplyMySkillSnapshot([], _now);

        Assert.Equal(before, PrefilledCodes(dm).Count);
    }

    [Fact]
    public void A_skill_the_snapshot_missed_still_shows_once_it_is_actually_used()
    {
        // 스냅샷이 틀렸거나 낡았어도, 실제로 쓴 스킬은 실측 쿨 경로로 나가므로 사라지지 않는다.
        DataManager dm = Meter();
        Load(dm, Me, "와플", Cleric);
        IReadOnlyList<int> all = PrefilledCodes(dm);
        Snapshot(dm, all[0]);
        Assert.DoesNotContain(all[1], PrefilledCodes(dm));

        dm.SaveCooldown(all[1], 30_000, _now, actorId: 0);

        Assert.Contains(all[1], PrefilledCodes(dm));
    }
}
