using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalGpuClockOffsetCapabilityTests
{
    [Theory]
    [InlineData((ushort)0x02C7, "RZ09-0529")]
    [InlineData((ushort)0x02C5, "RZ09-0530")]
    public void ExactModel_AdmitsStepOneNvmlEnvelopeWhenWritesAreAllowed(ushort product, string modelNumber)
    {
        ExperimentalBladeModel model = Model(product);
        Assert.Equal(modelNumber, model.ModelNumber);

        GpuOverclockCapabilities? gpu = ExperimentalBladePort.CreateCapabilities(model, enabled: true).GpuOverclock;

        Assert.NotNull(gpu);
        Assert.True(gpu.IsWellFormed);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, gpu.Access);
        Assert.True(gpu.SupportsEnableDisable);
        Assert.Equal(new DeviceNumericRange(-1000, 400, 1), gpu.CoreOffsetMegahertz);
        Assert.Equal(new DeviceNumericRange(-1000, 3000, 1), gpu.MemoryOffsetMegahertz);
        // A null step makes DeviceNumericRange.Supports reject every value.
        Assert.True(gpu.CoreOffsetMegahertz.Supports(-1000));
        Assert.True(gpu.CoreOffsetMegahertz.Supports(0));
        Assert.True(gpu.CoreOffsetMegahertz.Supports(400));
        Assert.False(gpu.CoreOffsetMegahertz.Supports(401));
        Assert.False(gpu.CoreOffsetMegahertz.Supports(-1001));
        Assert.True(gpu.MemoryOffsetMegahertz.Supports(3000));
        Assert.False(gpu.MemoryOffsetMegahertz.Supports(3001));
        Assert.Equal(gpu, ExperimentalBladePort.CreateCapabilities(model, enabled: true).GpuOverclock);
    }

    [Theory]
    [InlineData((ushort)0x02C7)]
    [InlineData((ushort)0x02C5)]
    public void PausedOrDisabledWrites_DoNotAdvertiseGpuTuning(ushort product) =>
        Assert.Null(ExperimentalBladePort.CreateCapabilities(Model(product), enabled: false).GpuOverclock);

    [Fact]
    public void OtherSourceReviewedModels_RemainWithoutGpuTuning()
    {
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All.Where(
            model => model.ProductId is not (0x02C7 or 0x02C5)))
        {
            Assert.False(ExperimentalBladePort.SupportsExperimentalGpuClockOffsets(model), model.ModelNumber);
            Assert.Null(ExperimentalBladePort.CreateCapabilities(model, enabled: true).GpuOverclock);
            Assert.Empty(ExperimentalBladePort.GetExperimentalGpuClockOffsetDeviceNames(model));
            Assert.DoesNotContain(new ExperimentalFeaturesPolicy(model).Snapshot().FeatureEvidence,
                feature => feature.Feature == "GpuClockOffsets");
        }
    }

    [Fact]
    public void ExactGpuNames_AreModelScopedLaptopGpus()
    {
        IReadOnlySet<string> blade18 = ExperimentalBladePort.GetExperimentalGpuClockOffsetDeviceNames(Model(0x02C7));
        IReadOnlySet<string> blade14 = ExperimentalBladePort.GetExperimentalGpuClockOffsetDeviceNames(Model(0x02C5));

        Assert.Contains("NVIDIA GeForce RTX 5090 Laptop GPU", blade18);
        Assert.DoesNotContain("NVIDIA GeForce RTX 5090 Laptop GPU", blade14);
        Assert.DoesNotContain("NVIDIA GeForce RTX 5060 Laptop GPU", blade18);
        Assert.All(blade18.Concat(blade14), name =>
        {
            Assert.StartsWith("NVIDIA GeForce RTX 50", name, StringComparison.Ordinal);
            Assert.EndsWith(" Laptop GPU", name, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData((ushort)0x02C7)]
    [InlineData((ushort)0x02C5)]
    public void Evidence_RecordsSourceBackedNotPhysicallyValidated(ushort product)
    {
        DiagnosticExperimentalSupport snapshot = new ExperimentalFeaturesPolicy(Model(product)).Snapshot();

        ExperimentalFeatureEvidence feature = Assert.Single(snapshot.FeatureEvidence,
            evidence => evidence.Feature == "GpuClockOffsets");
        Assert.Equal("SynapseSourceBackedNvmlWithoutPhysicalValidation", feature.Basis);
        Assert.Equal("Pending", snapshot.PhysicalValidation);
    }

    private static ExperimentalBladeModel Model(ushort product) =>
        ExperimentalBladeModels.All.Single(model => model.ProductId == product);
}
