using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsThxActiveOutputTests
{
    [Theory]
    [InlineData(1u, ThxAudioEndpoint.Speakers)]
    [InlineData(3u, ThxAudioEndpoint.Headphones)]
    [InlineData(5u, ThxAudioEndpoint.Headphones)]
    [InlineData(2u, null)]
    [InlineData(8u, null)]
    [InlineData(9u, null)]
    [InlineData(null, null)]
    public void LegacyOutputRequiresPhysicalSpeakerOrHeadphoneFormFactor(uint? formFactor, ThxAudioEndpoint? expected) =>
        Assert.Equal(expected, WindowsThxActiveOutput.ClassifyLegacyFormFactor(formFactor));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, null)]
    public void SystemEffectsFlagOnlyAcceptsDocumentedDwordValues(int value, bool? expected)
    {
        Assert.Equal(expected, WindowsThxActiveOutput.ParseSystemEffectsDisabled(value));
        Assert.Null(WindowsThxActiveOutput.ParseSystemEffectsDisabled("1"));
        Assert.Null(WindowsThxActiveOutput.ParseSystemEffectsDisabled(null));
    }

    [Fact]
    public void EffectsLookupUsesOnlyExactRenderEndpointGuid()
    {
        const string id = "{0.0.0.00000000}.{12345678-1234-1234-1234-123456789abc}";
        Assert.True(WindowsThxActiveOutput.TryGetRenderEndpointId(id, out Guid actual));
        Assert.Equal(Guid.Parse("12345678-1234-1234-1234-123456789abc"), actual);
        Assert.False(WindowsThxActiveOutput.TryGetRenderEndpointId(id + "\\other", out _));
        Assert.False(WindowsThxActiveOutput.TryGetRenderEndpointId(id.Replace("0.0.0", "0.0.1"), out _));
        Assert.False(WindowsThxActiveOutput.TryGetRenderEndpointId("unknown", out _));
    }

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
