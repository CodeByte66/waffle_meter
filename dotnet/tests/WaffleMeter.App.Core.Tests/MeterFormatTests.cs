using WaffleMeter.App.Core;
using Xunit;

namespace WaffleMeter.App.Core.Tests;

public sealed class MeterFormatTests
{
    [Theory]
    [InlineData(999, "999")]
    [InlineData(1500, "1.5K")]
    [InlineData(1_999_999, "1.99M")]   // truncates, not rounds
    [InlineData(2_000_000, "2M")]      // no trailing zeros
    [InlineData(2_500_000_000, "2.5B")]
    [InlineData(0, "0")]
    public void FormatAmount_matches_react(double amount, string expected)
    {
        Assert.Equal(expected, MeterFormat.FormatAmount(amount));
    }

    [Fact]
    public void FormatPower_is_thousands_with_one_decimal()
    {
        Assert.Equal("12.3k", MeterFormat.FormatPower(12345));
        Assert.Equal("5.0k", MeterFormat.FormatPower(5000));
    }

    [Theory]
    [InlineData(999_949, "999.9k")]
    [InlineData(999_950, "1.000M")]    // would otherwise round to "1000.0k"
    [InlineData(1_000_000, "1.000M")]
    [InlineData(1_234_567, "1.235M")]  // rounds, like the k form
    [InlineData(2_050_000, "2.050M")]  // keeps trailing zeros so the width is fixed
    public void FormatPower_switches_to_millions_at_1000k(int power, string expected)
    {
        Assert.Equal(expected, MeterFormat.FormatPower(power));
    }

    [Fact]
    public void FormatDps_and_percent()
    {
        Assert.Equal("50,000/s", MeterFormat.FormatDps(50000));
        Assert.Equal("60.0%", MeterFormat.FormatPercent(60.0));
        Assert.Equal("3.3%", MeterFormat.FormatPercent(3.33));
    }

    [Theory]
    [InlineData("Hero", NameDisplay.All, false, "Hero")]
    [InlineData("Hero", NameDisplay.MeOnly, false, "H***")]
    [InlineData("Hero", NameDisplay.MeOnly, true, "Hero")]
    [InlineData("Hero", NameDisplay.Hidden, true, "H***")]
    [InlineData(null, NameDisplay.Hidden, false, "***")]
    public void DisplayName_masking(string? name, NameDisplay mode, bool isUser, string expected)
    {
        Assert.Equal(expected, MeterFormat.DisplayName(name, mode, isUser));
    }

    [Theory]
    [InlineData(1010, ServerColorTier.A)]
    [InlineData(2010, ServerColorTier.B)]
    [InlineData(0, ServerColorTier.Default)]
    [InlineData(3000, ServerColorTier.Default)]
    public void ServerTier_ranges(int server, ServerColorTier expected)
    {
        Assert.Equal(expected, MeterFormat.ServerTier(server));
    }
}
