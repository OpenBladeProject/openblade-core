using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ThermalExternalDriftValidationBuildTests
{
    [Fact]
    public void NormalServiceBuildIsNotValidationMarked()
    {
        ThermalExternalDriftValidationBuildIdentity identity =
            ThermalExternalDriftValidationBuildIdentity.ReadCurrent();

        Assert.False(identity.Enabled);
        Assert.Null(identity.SourceRevision);
        Assert.Equal(64, identity.ServiceAssemblySha256.Length);
        Assert.Equal(64, identity.ProtocolAssemblySha256.Length);
        Assert.Equal(64, identity.ProductionFixtureSha256.Length);
        Assert.False(identity.Matches(
            new string('A', 40),
            identity.ServiceAssemblySha256,
            identity.ProtocolAssemblySha256,
            identity.ProductionFixtureSha256));
    }
}
