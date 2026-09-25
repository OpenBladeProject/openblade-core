using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class RazerThermalControllerPolicyTests
{
    [Theory]
    [InlineData("Razer Chroma SDK Diagnostic Service")]
    [InlineData("Razer Chroma SDK Server")]
    [InlineData("Razer Chroma SDK Service")]
    [InlineData("Razer Chroma Stream Server")]
    [InlineData("RGS")]
    [InlineData("Razer Gaming Services Updater")]
    public void ExactNonThermalAliasesRemainDetectedButDoNotBlockThermals(string controller)
    {
        Assert.True(RazerControllerServiceAliases.Contains(controller));
        Assert.False(RazerThermalControllerPolicy.IsBlocking(controller));
        Assert.False(RazerThermalControllerPolicy.IsBlocking(controller.ToUpperInvariant()));
    }

    [Fact]
    public void WindowsLightingHelperDoesNotBlockThermals()
    {
        Assert.False(RazerThermalControllerPolicy.IsBlocking("razerwdl"));
        Assert.False(RazerThermalControllerPolicy.IsBlocking("RAZERWDL"));
    }

    [Theory]
    [InlineData("RazerAppEngine")]
    [InlineData("RazerSynapseService")]
    [InlineData("Razer Synapse Service")]
    [InlineData("Unknown controller")]
    [InlineData("Razer Chroma SDK Service Helper")]
    [InlineData("RGS Helper")]
    [InlineData("Razer Gaming Services Updater Helper")]
    [InlineData(" Razer Chroma SDK Service")]
    [InlineData("")]
    public void OtherObservedControllersRemainBlocking(string controller)
    {
        Assert.True(RazerThermalControllerPolicy.IsBlocking(controller));
    }
}
