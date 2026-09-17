using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class RazerThermalControllerPolicyTests
{
    [Theory]
    [InlineData("Razer Chroma SDK Diagnostic Service")]
    [InlineData("Razer Chroma SDK Server")]
    [InlineData("Razer Chroma SDK Service")]
    [InlineData("Razer Chroma Stream Server")]
    public void ExactLightingAliasesRemainDetectedButDoNotBlockThermals(string controller)
    {
        Assert.True(RazerControllerServiceAliases.Contains(controller));
        Assert.False(RazerThermalControllerPolicy.IsBlocking(controller));
        Assert.False(RazerThermalControllerPolicy.IsBlocking(controller.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("RazerAppEngine")]
    [InlineData("razerwdl")]
    [InlineData("RazerSynapseService")]
    [InlineData("Razer Synapse Service")]
    [InlineData("RGS")]
    [InlineData("Razer Gaming Services Updater")]
    [InlineData("Unknown controller")]
    [InlineData("Razer Chroma SDK Service Helper")]
    [InlineData(" Razer Chroma SDK Service")]
    [InlineData("")]
    public void OtherObservedControllersRemainBlocking(string controller)
    {
        Assert.True(RazerThermalControllerPolicy.IsBlocking(controller));
    }
}
