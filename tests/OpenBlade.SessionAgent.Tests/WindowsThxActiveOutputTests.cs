using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsThxActiveOutputTests
{
    [Theory]
    [InlineData("Speakers (Realtek Audio Jack)", "00000000-0000-0000-ffff-ffffffffffff", ThxAudioEndpoint.Speakers)]
    [InlineData("Headphones (2- Realtek Audio Jack)", "0e21a4ec-46c8-4244-8d94-1c562346c32b", ThxAudioEndpoint.Headphones)]
    public void PinnedProductPatternAndContainerChooseOnlyOneOutput(string name, string container, ThxAudioEndpoint expected)
    {
        Assert.Equal(expected, WindowsThxActiveOutput.Classify(name, Guid.Parse(container)));
    }

    [Theory]
    [InlineData("Speakers (USB Audio)")]
    [InlineData("Headphones (Bluetooth Audio)")]
    [InlineData("Realtek Audio Jack")]
    [InlineData("Speakers (Realtek(R) Audio)")]
    [InlineData("Speakers (realtek audio jack)")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownOrOtherProductDefaultDoesNotSelectBuiltInFallback(string? name)
    {
        Assert.Null(WindowsThxActiveOutput.Classify(name, Guid.Parse("00000000-0000-0000-ffff-ffffffffffff")));
    }

    [Fact]
    public void MissingOrEmptyContainerDoesNotGuessHeadphones()
    {
        Assert.Null(WindowsThxActiveOutput.Classify("Speakers (Realtek Audio Jack)", null));
        Assert.Null(WindowsThxActiveOutput.Classify("Speakers (Realtek Audio Jack)", Guid.Empty));
    }
}
