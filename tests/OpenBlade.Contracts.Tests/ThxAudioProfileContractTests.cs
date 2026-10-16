using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class ThxAudioProfileContractTests
{
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
