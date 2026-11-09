using System.Globalization;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class GlobalSearchRankingTests
{
    [Theory]
    [InlineData("battery protection", "Battery protection", "Laptop › Battery", "", 0)]
    [InlineData("battery", "Battery protection", "Laptop › Battery", "", 1)]
    [InlineData("protection", "Battery protection", "Laptop › Battery", "", 2)]
    [InlineData("protection battery", "Battery protection", "Laptop › Battery", "", 3)]
    [InlineData("laptop protection", "Battery protection", "Laptop › Battery", "", 4)]
    [InlineData("charge limit", "Battery protection", "Laptop › Battery", "charge limit", 4)]
    [InlineData("A/D", "SnapTap", "Laptop › Keyboard", "A/D SOCD", 4)]
    [InlineData("nightly", "Update channel", "Settings › Updates", "nightly stable", 4)]
    [InlineData("eclairage", "Éclairage", "OpenBlade", "", 0)]
    [InlineData("яркость", "Яркость", "Экран", "", 0)]
    [InlineData("亮度", "亮度", "显示", "", 0)]
    [InlineData("battery missing", "Battery protection", "Laptop › Battery", "", null)]
    [InlineData("", "Battery protection", "Laptop › Battery", "", null)]
    public void RanksTitlesAheadOfContextAndAliases(
        string query, string title, string breadcrumb, string aliases, int? expected)
    {
        string[] terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected, GlobalSearchRanking.Score(
            terms, query, title, breadcrumb, aliases, CultureInfo.InvariantCulture.CompareInfo));
    }
}
