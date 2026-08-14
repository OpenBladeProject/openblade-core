using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class RazerControllerServiceAliasesTests
{
    [Theory]
    [InlineData("Razer Chroma SDK Diagnostic Service")]
    [InlineData("Razer Chroma SDK Server")]
    [InlineData("Razer Chroma SDK Service")]
    [InlineData("Razer Chroma Stream Server")]
    [InlineData("RGS")]
    [InlineData("Razer Gaming Services Updater")]
    [InlineData("RazerSynapseService")]
    [InlineData("Razer Synapse Service")]
    public void ContainsAcceptsEachReviewedControllerServiceAlias(string alias)
    {
        Assert.True(RazerControllerServiceAliases.Contains(alias));
        Assert.True(RazerControllerServiceAliases.Contains(alias.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("Razer Elevation Service")]
    [InlineData("Razer Game Manager Service 3")]
    [InlineData("Razer Synapse Service Helper")]
    [InlineData("RazerAppEngine")]
    [InlineData("razerwdl")]
    public void ContainsRejectsSupportServicesPrefixesAndProcesses(string alias)
    {
        Assert.False(RazerControllerServiceAliases.Contains(alias));
    }
}
