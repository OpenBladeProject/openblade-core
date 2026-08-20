using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class PadlessHyperBoostLifecycleValidationBuildTests
{
    [Fact]
    public void NormalServiceBuildIsNotValidationMarked()
    {
        PadlessHyperBoostLifecycleValidationBuildIdentity identity =
            PadlessHyperBoostLifecycleValidationBuildIdentity.ReadCurrent();

        Assert.False(identity.Enabled);
        Assert.Null(identity.SourceRevision);
        Assert.Equal(64, identity.ServiceAssemblySha256.Length);
        Assert.Equal(64, identity.AdmissionManifestSha256.Length);
        Assert.False(identity.Matches(
            new string('A', 40),
            identity.ServiceAssemblySha256,
            identity.AdmissionManifestSha256));
    }
}
