namespace WaffleMeter.Capture;

/// <summary>
/// The stat ids the server uses in its character stat dictionary (0x364A / 0x3649), named.
///
/// <para>Only the ids we can name are listed. The full sheet carries ~85-87 entries and 112 distinct ids were
/// observed across one session; the rest stay unnamed and are still carried through as raw pairs, because an
/// id we cannot label today is not an id we want to silently drop — the stat window comparison that names it
/// later needs the value to have been captured.</para>
///
/// <para><b>Units.</b> Values are signed 32-bit. The percent-ish stats are basis points (value / 100 = %), the
/// flat ones are plain integers. Which is which is per-id, NOT derivable from the value, so it is declared
/// here — reading 6425 as 64.25% when it is a flat 6,425 attack is the exact failure this table exists to
/// prevent. ⚠️ The percent/flat split is from cross-reading a live capture against a client whose stat window
/// shows the same numbers; treat any id you add without that comparison as unverified.</para>
/// </summary>
public static class PlayerStatIds
{
    // ---- 공격 (flat) ----
    public const int Attack = 317;                 // 기본 공격력 (스탯창 공격력의 한 항. PlayerStatSheet.AttackPower 참조)
    public const int AdditionalAttack = 19;        // 추가 공격력
    public const int MaximumAttack = 31;           // 무기 최대 공격력
    public const int MinimumAttack = 33;           // 무기 최소 공격력
    public const int CriticalAttackPower = 38;     // 치명타 공격력
    public const int Penetration = 284;            // 관통
    public const int Accuracy = 104;               // 기본 명중 (스탯창 명중의 한 항)
    public const int WeaponAccuracy = 318;         // 무기 명중
    public const int PveAccuracy = 110;            // PvE 명중
    public const int Critical = 128;               // 기본 치명타 (스탯창 치명타의 한 항)
    public const int Defense = 52;                 // 기본 방어력 (스탯창 방어력의 한 항)
    public const int ArmorDefense = 307;           // 방어구 방어력
    public const int PveAttack = 56;               // PvE 공격력
    public const int BossAttack = 50;              // 보스 공격력
    public const int FrontAttack = 587;            // 전방 공격력
    public const int BackAttack = 98;              // 후방 공격력
    public const int FrontCritical = 591;          // 전방 치명타
    public const int BackCritical = 100;           // 후방 치명타
    public const int SealstoneAdditionalDamage = 69; // 봉혼석 추가 피해

    // ---- 막기 (flat) ----
    // 🔑 철벽(445)·철벽 관통(449)과 <b>다른 계열</b>이다. usmap 의 EStat 열거형(594 id)이 둘을 따로 선언하고,
    // 클라 DaevanionNode 의 노드 제목↔스탯 토큰 매핑이 '막기'→Block, '막기 관통'→BlockPierce, '철벽'→IronWall,
    // '철벽 관통'→IgnoreIronWall 로 1:1 확정한다. 실측으로도 갈린다: 같은 보스를 철벽 관통만 +100 올려 때린
    // 대조에서 막기 발동률이 움직이지 않아 철벽 관통은 막기 판정의 항으로 기각됐다(chi2=50.9).
    // 막힌 타격은 피해가 정상의 0.42~0.50배로 깎이며, 와이어에서는 <c>SpecialDamage.PARRY</c> 로 나타난다.
    public const int Block = 255;                  // 막기
    public const int BlockPierce = 256;            // 막기 관통

    // ---- 주신/기본 스탯 (flat, 포인트) ----
    public const int Might = 1;        // 위력
    public const int Agility = 2;      // 민첩
    public const int Knowledge = 3;    // 지식
    public const int Vitality = 4;     // 활력
    public const int Precision = 5;    // 정밀
    public const int Will = 6;         // 의지
    public const int Justice = 7;      // 정의
    public const int Freedom = 8;      // 자유
    public const int Illusion = 9;     // 환상
    public const int Life = 10;        // 생명
    public const int Time = 11;        // 시간
    public const int Destruction = 13; // 파괴
    public const int Death = 14;       // 죽음
    public const int Wisdom = 15;      // 지혜
    public const int Destiny = 16;     // 운명
    public const int Space = 17;       // 공간

    // ---- 증폭·판정 (basis points: value / 100 = %) ----
    public const int AttackIncreasePercent = 425;            // 공격력 증가율
    public const int AccuracyIncreasePercent = 427;          // 명중 증가율
    public const int CriticalIncreasePercent = 429;          // 치명타 증가율
    public const int DefenseIncreasePercent = 426;           // 방어력 증가율
    public const int DamageAmplifyPercent = 28;              // 피해 증폭
    public const int WeaponDamageAmplifyPercent = 32;        // 무기 피해 증폭
    public const int PveDamageAmplifyPercent = 379;          // PvE 피해 증폭
    public const int BossDamageAmplifyPercent = 520;         // 보스 피해 증폭
    public const int CriticalDamageAmplifyPercent = 44;      // 치명타 피해 증폭
    public const int AdditionalHitAccuracyPercent = 146;     // 다단 히트 적중
    public const int PerfectPercent = 442;                   // 완벽
    public const int HardHitPercent = 443;                   // 강타
    public const int CombatSpeedPercent = 282;               // 전투 속도
    public const int FrontDamageAmplifyPercent = 589;        // 전방 피해 증폭
    public const int BackDamageAmplifyPercent = 102;         // 후방 피해 증폭

    // ---- 방어·내성 (2026-08-31 인게임 '세부 스탯' 탭과 값이 정확히 일치해 확정) ----
    // 🔑 id 배치에 규칙이 있다: 공격 쪽 id 바로 뒤(+1)나 일정 간격(+3, +42)에 그 방어 짝이 온다.
    //    98 후방 공격력 → 99 후방 방어력, 100 후방 치명타 → 101 후방 치명타 저항,
    //    38 치명타 공격력 → 41 치명타 방어력, 28 피해 증폭 → 70 피해 내성, 32 무기 피해 증폭 → 74 무기 피해 내성.
    //    값 일치만으로는 같은 숫자를 가진 다른 id와 구분이 안 되는 경우가 있어(1,500 이 두 곳), 이 간격이
    //    교차검증 역할을 한다.
    public const int CriticalDefense = 41;                   // 치명타 방어력
    public const int CriticalDamageResistPercent = 47;       // 치명타 피해 내성
    public const int DamageResistPercent = 70;               // 피해 내성
    public const int WeaponDamageResistPercent = 74;         // 무기 피해 내성
    public const int BackDefense = 99;                       // 후방 방어력
    public const int BackCriticalResist = 101;               // 후방 치명타 저항
    public const int BackDamageResistPercent = 103;          // 후방 피해 내성
    public const int AdditionalHitResistPercent = 147;       // 다단 히트 저항
    public const int IronWallPercent = 445;                  // 철벽
    public const int IronWallPenetrationPercent = 449;        // 철벽 관통
    public const int FrontDefense = 588;                     // 전방 방어력
    public const int FrontDamageResistPercent = 590;         // 전방 피해 내성
    public const int FrontCriticalResist = 592;              // 전방 치명타 저항

    /// <summary>쿨타임 감소는 두 id의 합이고, 표시할 때 <b>부호를 뒤집는다</b> — 서버는 "감소량"을 양수로
    /// 싣지만 사람이 읽는 쪽에서는 −14% 가 자연스럽다.</summary>
    public const int CooldownBasePercent = 215;

    public const int CooldownBonusPercent = 433;

    /// <summary>Human-readable Korean label for a stat id, or null when we have not named it yet.</summary>
    public static string? Label(int id) => id switch
    {
        Attack => "기본 공격력",
        AdditionalAttack => "추가 공격력",
        MaximumAttack => "무기 최대 공격력",
        MinimumAttack => "무기 최소 공격력",
        CriticalAttackPower => "치명타 공격력",
        Penetration => "관통",
        Accuracy => "기본 명중",
        WeaponAccuracy => "무기 명중",
        PveAccuracy => "PvE 명중",
        Critical => "기본 치명타",
        Defense => "기본 방어력",
        ArmorDefense => "방어구 방어력",
        PveAttack => "PvE 공격력",
        BossAttack => "보스 공격력",
        FrontAttack => "전방 공격력",
        BackAttack => "후방 공격력",
        FrontCritical => "전방 치명타",
        BackCritical => "후방 치명타",
        SealstoneAdditionalDamage => "봉혼석 추가 피해",
        Block => "막기",
        BlockPierce => "막기 관통",
        Might => "위력",
        Agility => "민첩",
        Knowledge => "지식",
        Vitality => "활력",
        Precision => "정밀",
        Will => "의지",
        Justice => "정의",
        Freedom => "자유",
        Illusion => "환상",
        Life => "생명",
        Time => "시간",
        Destruction => "파괴",
        Death => "죽음",
        Wisdom => "지혜",
        Destiny => "운명",
        Space => "공간",
        AttackIncreasePercent => "공격력 증가율",
        AccuracyIncreasePercent => "명중 증가율",
        CriticalIncreasePercent => "치명타 증가율",
        DefenseIncreasePercent => "방어력 증가율",
        DamageAmplifyPercent => "피해 증폭",
        WeaponDamageAmplifyPercent => "무기 피해 증폭",
        PveDamageAmplifyPercent => "PvE 피해 증폭",
        BossDamageAmplifyPercent => "보스 피해 증폭",
        CriticalDamageAmplifyPercent => "치명타 피해 증폭",
        AdditionalHitAccuracyPercent => "다단 히트 적중",
        PerfectPercent => "완벽",
        HardHitPercent => "강타",
        CombatSpeedPercent => "전투 속도 (확인 필요)",
        FrontDamageAmplifyPercent => "전방 피해 증폭",
        BackDamageAmplifyPercent => "후방 피해 증폭",
        CriticalDefense => "치명타 방어력",
        CriticalDamageResistPercent => "치명타 피해 내성",
        DamageResistPercent => "피해 내성",
        WeaponDamageResistPercent => "무기 피해 내성",
        BackDefense => "후방 방어력",
        BackCriticalResist => "후방 치명타 저항",
        BackDamageResistPercent => "후방 피해 내성",
        AdditionalHitResistPercent => "다단 히트 저항",
        IronWallPercent => "철벽",
        IronWallPenetrationPercent => "철벽 관통",
        FrontDefense => "전방 방어력",
        FrontDamageResistPercent => "전방 피해 내성",
        FrontCriticalResist => "전방 치명타 저항",
        _ => null,
    };

    /// <summary>True when this id's value is basis points (divide by 100 for a percent).</summary>
    public static bool IsPercent(int id) => id
        is AttackIncreasePercent or AccuracyIncreasePercent or CriticalIncreasePercent or DefenseIncreasePercent
        or DamageAmplifyPercent or WeaponDamageAmplifyPercent or PveDamageAmplifyPercent
        or BossDamageAmplifyPercent or CriticalDamageAmplifyPercent or AdditionalHitAccuracyPercent
        or PerfectPercent or HardHitPercent or CombatSpeedPercent or FrontDamageAmplifyPercent
        or BackDamageAmplifyPercent or CooldownBasePercent or CooldownBonusPercent
        or CriticalDamageResistPercent or DamageResistPercent or WeaponDamageResistPercent
        or BackDamageResistPercent or AdditionalHitResistPercent or IronWallPercent
        or IronWallPenetrationPercent or FrontDamageResistPercent;
}
