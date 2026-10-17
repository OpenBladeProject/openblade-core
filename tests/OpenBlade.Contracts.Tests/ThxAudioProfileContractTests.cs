using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class ThxAudioProfileContractTests
{
    [Theory]
    [InlineData('a')]
    [InlineData('界')]
    public void ApplicationRules_RespectEncodedByteBudget(char character)
    {
        var rules = new List<ThxApplicationAudioRule>();
        while (true)
        {
            var candidate = new ThxApplicationAudioRule(
                rules.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + new string(character, 240) + ".exe", ThxApplicationAudioOutput.Competitive);
            ThxApplicationAudioRule[] next = [.. rules, candidate];
            if (!ThxAudioProfileSettings.ApplicationRulesFitWithinByteLimit(next))
            {
                Assert.NotEmpty(rules);
                Assert.True(ThxAudioProfileSettings.ApplicationRulesFitWithinByteLimit(rules));
                Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(next).Length
                    > ThxAudioProfileSettings.MaximumApplicationRuleBytes);
                break;
            }
            rules.Add(candidate);
        }
    }

    [Fact]
    public void ApplicationRule_NormalizesExecutableBasename()
    {
        var rule = new ThxApplicationAudioRule(
            "  ExampleGame.EXE  ",
            ThxApplicationAudioOutput.Immersive);

        Assert.Equal("examplegame.exe", rule.ExecutableName);
    }

    [Theory]
    [InlineData("bad<name.exe")]
    [InlineData("bad>name.exe")]
    [InlineData("bad:name.exe")]
    [InlineData("bad\"name.exe")]
    [InlineData("folder/name.exe")]
    [InlineData("folder\\name.exe")]
    [InlineData("bad|name.exe")]
    [InlineData("bad?name.exe")]
    [InlineData("bad*name.exe")]
    [InlineData("bad\u0001name.exe")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("name.")]
    public void ApplicationRule_RejectsInvalidWindowsBasename(string executableName)
    {
        Assert.Throws<ArgumentException>(() => new ThxApplicationAudioRule(
            executableName,
            ThxApplicationAudioOutput.Game));
    }
}
