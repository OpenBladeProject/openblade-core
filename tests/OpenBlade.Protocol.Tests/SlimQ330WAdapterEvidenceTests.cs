using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class SlimQ330WAdapterEvidenceTests
{
    [Fact]
    public void EmbeddedManifest_BindsExactAdapterReadbackAndEvidence()
    {
        SlimQ330WAdapterAdmissionManifest manifest =
            SlimQ330WAdapterAdmissionSource.ReadValidated();

        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal("Blade 16 - RZ09-0581", manifest.Host.Model);
        Assert.Equal("RZ09-05819EN4", manifest.Host.Sku);
        Assert.Equal("ModelScoped", manifest.Host.FirmwareAdmission);
        Assert.Equal(0x1532, manifest.Host.VendorId);
        Assert.Equal(0x02E0, manifest.Host.ProductId);
        Assert.Equal(330, manifest.Adapter.AdvertisedPowerWatts);
        Assert.Equal("0000", manifest.Adapter.RequestPayloadHex);
        Assert.Equal("1211", manifest.Adapter.ResponsePayloadHex);
        Assert.Equal("FullPower330W", manifest.Adapter.MappedState);
        Assert.Equal(10, manifest.Evidence.IdenticalReadCount);
        Assert.Equal(4, manifest.Evidence.PowerOnlineSampleCount);
        Assert.Equal("4.00", manifest.Evidence.ValidatedBiosVersion);
        Assert.Equal(
            "7cb784d93de51bb8ef3fc6ac0a97fc80010d7b19",
            manifest.Evidence.CaptureCommit);
        Assert.Equal(
            "6402DFA16478DDB80A6E7CE0442612E34215E4FC05DC400EA83EA2EDE3C148A6",
            manifest.Evidence.AnnotationSha256);
        Assert.True(manifest.Gate.ModelScopedAdmissionApproved);
        Assert.True(SlimQ330WAdapterAdmissionSource.IsAdmitted());
    }

    [Fact]
    public void ChangedResponse_IsRejected()
    {
        SlimQ330WAdapterAdmissionManifest manifest =
            SlimQ330WAdapterAdmissionSource.ReadValidated();

        manifest = manifest with
        {
            Adapter = manifest.Adapter with { ResponsePayloadHex = "1111" },
        };

        Assert.Throws<UnvalidatedCommandException>(
            () => SlimQ330WAdapterAdmissionSource.ValidateForTest(manifest));
    }
}
