using System.Reflection;
using System.Security.Cryptography;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CoolingPadAccessoryAdmissionSourceTests
{
    private const string ResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0581_0f43_CoolingPadSemanticAdmission.json";
    private const string ExpectedSha256 =
        "B7D4537E62314572D40B508E248DBE0DB5E98395FD11A6215641ECF2C90FCF1A";

    [Fact]
    public void EmbeddedManifest_IsHashPinnedAndExactMachineScoped()
    {
        Assembly assembly = typeof(CoolingPadAccessoryAdmissionSource).Assembly;
        using Stream stream = Assert.IsAssignableFrom<Stream>(
            assembly.GetManifestResourceStream(ResourceName));
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        Assert.Equal(
            ExpectedSha256,
            Convert.ToHexString(SHA256.HashData(memory.ToArray())));
        CoolingPadAccessoryAdmissionManifest manifest =
            CoolingPadAccessoryAdmissionSource.ReadValidated();
        Assert.Equal("Razer", manifest.Host.Manufacturer);
        Assert.Equal("Blade 16 - RZ09-0581", manifest.Host.Model);
        Assert.Equal("RZ09-05819EN4", manifest.Host.Sku);
        Assert.Equal(0x1532, manifest.Device.VendorId);
        Assert.Equal(0x0F43, manifest.Device.ProductId);
        Assert.Equal(0x0200, manifest.Device.ReleaseNumber);
        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal(
            "captures/annotations/2026-08-09-rz09-0581-cooling-pad-host-control-physical-positive.json",
            manifest.Fan.HostControl2200PairPhysicallyValidated.Evidence?.Artifact);
        Assert.Equal(
            "captures/decoded/rz09-0581-pid-02e0-bios-4.00-cooling-pad-protocol-observations.json",
            manifest.Lighting.WholeStripProjectionResolved.Evidence?.Artifact);
        Assert.DoesNotContain(
            typeof(CoolingPadAdmissionHost).GetProperties(),
            property => property.Name.Contains(
                "bios",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidatedAccessoryGateWithoutBoundEvidence_IsRejected()
    {
        CoolingPadAccessoryAdmissionManifest manifest =
            CoolingPadAccessoryAdmissionSource.ReadValidated();
        manifest = manifest with
        {
            Lighting = manifest.Lighting with
            {
                ControllerHandoffValidated = new ReviewedAdmissionGate(true, null),
            },
        };

        Assert.Throws<UnvalidatedCommandException>(
            () => CoolingPadAccessoryAdmissionSource.ValidateForTest(manifest));
    }

    [Fact]
    public void CurrentGates_AreDerivedFromReviewedButIncompleteManifest()
    {
        CoolingPadFanControlEvidenceGates fan =
            CoolingPadFanControlEvidenceGates.Current;
        Assert.True(fan.ExactIdentityAndTransportReviewed);
        Assert.True(fan.HostControl2200PairPhysicallyValidated);
        Assert.False(fan.OpenBladeHandbackValidated);
        Assert.False(fan.WriteAdmitted);

        CoolingPadLightingEvidenceGates lighting =
            CoolingPadLightingEvidenceGates.CurrentObservedWholeStrip;
        Assert.True(lighting.ExactIdentityAndGeometryReviewed);
        Assert.True(lighting.WholeStripProjectionResolved);
        Assert.True(lighting.DisplayOffOracleObserved);
        Assert.False(lighting.OpenBladeReversibleFrameLifecycleValidated);
        Assert.False(lighting.WriteAdmitted);
    }

    [Fact]
    public void KnownAnswerFrame_MatchesManifestHash()
    {
        CoolingPadAccessoryAdmissionManifest manifest =
            CoolingPadAccessoryAdmissionSource.ReadValidated();
        var frame = new CoolingPadLightingFrame(
            Enumerable.Repeat(
                new OpenBlade.Contracts.RgbColor(0x12, 0x34, 0x56),
                CoolingPadLightingFrame.SlotCount)
            .ToArray());

        string hash = Convert.ToHexString(SHA256.HashData(
            CoolingPadLightingProtocol.CreateFrame(0x5A, frame).Encode()));

        Assert.Equal(manifest.Lighting.UniformKnownAnswerReportSha256, hash);
    }
}
