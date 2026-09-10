using WaffleMeter.App.Core;
using WaffleMeter.Capture;
using WaffleMeter.Data;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

/// <summary>
/// Locks the '명중(컷 기준)' row: its exact label, and its position after the four rows that really do appear
/// in the game's stat window.
///
/// <para><b>Why the label is a contract.</b> The stats site quotes it verbatim when it tells a player which
/// number to compare against a published 명중컷 ("미터 설정 › 스탯의 '명중(컷 기준)'"). Renaming it here breaks
/// nothing that a compiler or a test in that repository can see — the site keeps rendering its sentence, the
/// meter keeps rendering its row, and the player simply cannot find the thing they were told to look for.</para>
///
/// <para><b>Why the position is a contract.</b> The settings card's own description says "맨 위 네 값은 인게임
/// 스탯창에 뜨는 숫자입니다". This row is the one value that is NOT in the game's stat window — it is a
/// meter-side sum — so putting it inside those four makes that sentence false and sends people looking for a
/// row the game never shows. A sentence that makes a promise about a list turns the list's ORDER into part of
/// the contract, which is easy to miss when adding a row.</para>
/// </summary>
public sealed class StatSheetJudgmentRowTests
{
    private const string CutLabel = "명중(컷 기준)";

    private static PlayerStatSheet Sheet() => new(
        new Dictionary<int, int>
        {
            // 인게임 스탯창 네 값의 재료
            [PlayerStatIds.Attack] = 3_778,
            [PlayerStatIds.AdditionalAttack] = 1_083,
            [PlayerStatIds.MinimumAttack] = 973,
            [PlayerStatIds.MaximumAttack] = 1_563,
            [PlayerStatIds.AttackIncreasePercent] = 12_301,
            [PlayerStatIds.Defense] = 10_666,
            [PlayerStatIds.ArmorDefense] = 16_393,
            [PlayerStatIds.DefenseIncreasePercent] = 2_400,
            [PlayerStatIds.Accuracy] = 1_807,
            [PlayerStatIds.WeaponAccuracy] = 391,
            [PlayerStatIds.AccuracyIncreasePercent] = 5_350,
            [PlayerStatIds.Critical] = 2_278,
            [PlayerStatIds.CriticalIncreasePercent] = 5_950,
            // 컷 기준에만 들어가는 두 항
            [PlayerStatIds.PveAccuracy] = 320,
            [PlayerStatIds.BlockPierce] = 210,
        },
        UpdatedAt: 1L,
        FullSnapshotSeen: true);

    private static StatSheetGroup DerivedGroup() =>
        StatSheetExport.Groups(Sheet()).First(g => g.Title.Contains("인게임 스탯창"));

    [Fact]
    public void CutRowUsesTheExactLabelTheStatsSiteQuotes()
    {
        Assert.Contains(DerivedGroup().Rows, r => r.Label == CutLabel);

        // Spelled out so a stray space or a full-width bracket fails here rather than in a player's search.
        Assert.Equal("명중(컷 기준)", CutLabel);
        Assert.Contains('(', CutLabel);
        Assert.DoesNotContain("명중 (", CutLabel);
    }

    [Fact]
    public void CutRowComesAfterTheFourInGameStatWindowRows()
    {
        List<StatSheetRow> rows = DerivedGroup().Rows;

        Assert.Equal(["공격력", "방어력", "명중", "치명타", CutLabel], rows.Select(r => r.Label).ToArray());
    }

    /// <summary>명중 + PvE 명중 + 막기 관통, on top of the stat-window 명중 the meter already reconciled against
    /// the game. The site converts a published cut with the same shape (its D in place of our 104), so the two
    /// numbers are directly comparable — that comparability is the entire reason this row exists.</summary>
    [Fact]
    public void CutRowIsStatWindowAccuracyPlusPveAccuracyPlusBlockPierce()
    {
        List<StatSheetRow> rows = DerivedGroup().Rows;
        double accuracy = double.Parse(rows.Single(r => r.Label == "명중").Value, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture);
        double cut = double.Parse(rows.Single(r => r.Label == CutLabel).Value, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(accuracy + 320 + 210, cut);

        // (1,807 + 391) × 1.535 = 3,373.93 → 3,374, then + 320 + 210.
        Assert.Equal(3_374d, accuracy);
        Assert.Equal(3_904d, cut);
    }

    /// <summary>막기 관통 is 256, NOT 449 (철벽 관통). 449 was tested as a term in the block calculation and
    /// rejected (chi2 50.9); it is a different stat with a different mechanic, and using it here would publish
    /// a cut basis that does not match the one the site estimates against.</summary>
    [Fact]
    public void CutRowUsesBlockPierceNotIronWallPenetration()
    {
        var withIronWall = new PlayerStatSheet(
            new Dictionary<int, int>(Sheet().Values) { [PlayerStatIds.IronWallPenetrationPercent] = 2_640 },
            UpdatedAt: 1L,
            FullSnapshotSeen: true);

        StatSheetGroup group = StatSheetExport.Groups(withIronWall).First(g => g.Title.Contains("인게임 스탯창"));
        Assert.Equal("3,904", group.Rows.Single(r => r.Label == CutLabel).Value);
    }
}
