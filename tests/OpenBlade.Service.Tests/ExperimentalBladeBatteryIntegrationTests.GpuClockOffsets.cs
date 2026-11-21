using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;
using static OpenBlade.Service.Tests.VerifiedBladeProviderFixture;

namespace OpenBlade.Service.Tests;

// RZ09-0529 and RZ09-0530 through the real experimental factory, provider and
// NVML controller, with only the HID and NVML APIs faked.
public sealed partial class ExperimentalBladeBatteryIntegrationTests
{
    [Theory]
    [InlineData(0x02C7, "NVIDIA GeForce RTX 5070 Ti Laptop GPU")]
    [InlineData(0x02C7, "NVIDIA GeForce RTX 5090 Laptop GPU")]
    [InlineData(0x02C5, "NVIDIA GeForce RTX 5060 Laptop GPU")]
    [InlineData(0x02C5, "NVIDIA GeForce RTX 5070 Laptop GPU")]
    public async Task GpuOffsets_StandaloneWriteUsesNvmlOnly(int productId, string gpuName)
    {
        GpuOffsetHarness harness = CreateGpuOffsetHarness(productId, gpuName);
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        Assert.True(harness.Factory.AdmitsNvidiaLaptopGpuForClockOffsets);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        Assert.NotNull((await device.ReadCapabilitiesAsync(CancellationToken.None)).GpuOverclock);
        Assert.Null((await device.ReadStateAsync(CancellationToken.None)).GpuClockOffsets);
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyGpuClockTuningOutcomeAsync(
            new GpuClockTuningSettings(true, new GpuClockOffsetSettings(25, 100)), CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.Equal(25, harness.Api.Core);
        // NVML memory offsets are twice the Synapse/OpenBlade value.
        Assert.Equal(200, harness.Api.Memory);
        Assert.Empty(harness.Io.Setters);
    }

    // While writes are paused the capability is withdrawn and new offsets, the
    // 0/0 reset included, are refused; applied offsets stay in the driver. The
    // published envelope stays clipped to the policy, and recovery carries the
    // managed observation so a pending GPU half is judged from it.
    [Theory]
    [InlineData(0x02C7)]
    [InlineData(0x02C5)]
    public async Task GpuOffsets_PauseKeepsPolicyEnvelopeAndRecoveryCarriesManagedOffsets(int productId)
    {
        GpuOffsetHarness harness = CreateGpuOffsetHarness(productId, "NVIDIA GeForce RTX 5070 Laptop GPU");
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        var applied = new GpuClockOffsetSettings(300, 1000);
        await device.SetGpuClockOffsetsAsync(applied, CancellationToken.None);
        await PauseExperimentalWritesAsync(productId, harness, device);
        Assert.True(provider.IsExperimentalWritesPaused);

        Assert.Null((await device.ReadCapabilitiesAsync(CancellationToken.None)).GpuOverclock);
        int writes = harness.Api.Writes.Count;
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            device.SetGpuClockOffsetsAsync(GpuClockOffsetSettings.Default, CancellationToken.None));
        Assert.Equal(writes, harness.Api.Writes.Count);
        Assert.Equal((300, 2000), (harness.Api.Core, harness.Api.Memory));

        GpuClockOffsetCapabilities paused = Assert.IsType<GpuClockOffsetCapabilities>(
            (await device.ReadStateAsync(CancellationToken.None)).GpuClockOffsets);
        Assert.Equal(applied, paused.Current);
        Assert.Equal((-1000, 400), (paused.Core.MinimumMegahertz, paused.Core.MaximumMegahertz));
        Assert.Equal((-1000, 3000), (paused.Memory.MinimumMegahertz, paused.Memory.MaximumMegahertz));

        ConfirmedDeviceState? accepted = null;
        ConfirmedDeviceState recovered = await provider.ResumeExperimentalWritesAsync(state =>
        {
            accepted = state;
            return true;
        }, () => true, CancellationToken.None);

        Assert.Equal(applied, accepted?.GpuClockOffsets?.Current);
        Assert.Equal(applied, recovered.GpuClockOffsets?.Current);
        Assert.False(provider.IsExperimentalWritesPaused);
    }

    [Fact]
    public async Task GpuOffsets_EnvelopeAfterPauseAndResumeStaysWithinPolicy()
    {
        GpuOffsetHarness harness = CreateGpuOffsetHarness(0x02C5, "NVIDIA GeForce RTX 5070 Laptop GPU");
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetGpuClockOffsetsAsync(new GpuClockOffsetSettings(300, 1000), CancellationToken.None);
        await PauseExperimentalWritesAsync(0x02C5, harness, device);
        _ = await device.ReadStateAsync(CancellationToken.None);
        await provider.ResumeExperimentalWritesAsync(_ => true, () => true, CancellationToken.None);
        harness.Activity.Active = false;

        GpuClockOffsetCapabilities resumed = Assert.IsType<GpuClockOffsetCapabilities>(
            (await device.ReadStateAsync(CancellationToken.None)).GpuClockOffsets);

        // The raw driver range (+1000 core, +6000 memory) is never published.
        Assert.Equal(400, resumed.Core.MaximumMegahertz);
        Assert.Equal(3000, resumed.Memory.MaximumMegahertz);
    }

    // A core write lands, the memory write fails and so does the restore. The
    // offsets are unmanaged and read as null before and after, which is not
    // evidence that the prior state remained active.
    [Theory]
    [InlineData(0x02C7)]
    [InlineData(0x02C5)]
    public async Task GpuOffsets_IndeterminateNvmlWriteIsNotReportedAsUnchanged(int productId)
    {
        GpuOffsetHarness harness = CreateGpuOffsetHarness(productId, "NVIDIA GeForce RTX 5080 Laptop GPU");
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        int coreWrites = 0;
        harness.Api.SetStatus = (type, _) => type == NvmlClockType.Graphics && ++coreWrites == 1 ? 0 : 999;
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyGpuClockTuningOutcomeAsync(
            new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)), CancellationToken.None);

        Assert.Equal((200, 0), (harness.Api.Core, harness.Api.Memory));
        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.True(outcome.WriteAttempted);
        Assert.False(outcome.ReadbackMatched);
        Assert.Equal(WriteSubmissionState.Indeterminate, outcome.WriteSubmissionState);
        Assert.DoesNotContain("remained active", outcome.Result.Message, StringComparison.Ordinal);
        Assert.Null(outcome.Readback?.GpuClockOffsets);
    }

    [Theory]
    [InlineData(0x02C7, true)]
    [InlineData(0x02C5, true)]
    [InlineData(0x02A0, false)]
    public void Composition_SelectsLaptopGpuFormOnlyForTheModelsThatAdmitIt(int productId, bool admitted)
    {
        GpuOffsetHarness harness = CreateGpuOffsetHarness(productId, "NVIDIA GeForce RTX 5070 Laptop GPU");
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        var api = new FakeLaptopGpuNvml { DeviceName = "NVIDIA GeForce RTX 5070 Laptop GPU" };

        IGpuClockOffsetController controller = ServiceComposition.CreateGpuClockOffsetController(harness.Factory, api);

        Assert.Equal(admitted, controller.Query() is not null);
        // The validated exact name stays admitted on every factory.
        api.DeviceName = "NVIDIA GeForce RTX 5090 Laptop GPU";
        Assert.NotNull(controller.Query());
    }

    [Fact]
    public void Composition_ProductionFactoryKeepsTheExactRtx5090Pin()
    {
        var api = new FakeLaptopGpuNvml { DeviceName = "NVIDIA GeForce RTX 5070 Laptop GPU" };

        IGpuClockOffsetController controller = ServiceComposition.CreateGpuClockOffsetController(
            new UnavailableProductionFactory(), api);

        Assert.Null(controller.Query());
        api.DeviceName = "NVIDIA GeForce RTX 5090 Laptop GPU";
        Assert.NotNull(controller.Query());
    }

    private static async Task PauseExperimentalWritesAsync(int productId, GpuOffsetHarness harness, IBladeDevice device)
    {
        if (productId == 0x02C5)
        {
            // A lost limiter echo pauses every experimental write.
            harness.Io.FailNextSetterResponse = true;
            BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
                device.SetChargeLimitAsync(BatteryPolicy.Create(80), CancellationToken.None));
            Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
            return;
        }
        // The shared HID fake injects only the RZ09-0530 limiter fault; enter the
        // same policy state directly for RZ09-0529.
        object policy = typeof(ExperimentalBladeFactory)
            .GetField("policy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(harness.Factory)!;
        ((ExperimentalFeaturesPolicy)policy).RequireRecovery(ExperimentalRecoveryScope.LightingFieldsOnly);
    }

    private static GpuOffsetHarness CreateGpuOffsetHarness(int productId, string gpuName)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        var io = new Io { Raw = 0x50 };
        var native = new Native { ProductId = (ushort)productId };
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native, new Guard(), io,
            () => PowerSource.PluggedIn);
        var api = new FakeLaptopGpuNvml { DeviceName = gpuName };
        var activity = new SettableGpuActivity();
        var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready, BatteryValidationFirmwareCompatibility.ModelScoped),
            factory, UnavailableFanRpmFactory(), factory.CreateLightingFactory(),
            ServiceComposition.CreateGpuClockOffsetController(factory, api),
            gpuActivitySource: activity);
        return new(provider, factory, io, api, activity);
    }

    private sealed record GpuOffsetHarness(
        WindowsVerifiedBladeDeviceProvider Provider,
        ExperimentalBladeFactory Factory,
        Io Io,
        FakeLaptopGpuNvml Api,
        SettableGpuActivity Activity);

    private sealed class SettableGpuActivity : IGpuActivitySource
    {
        internal bool Active { get; set; } = true;
        public ValueTask<bool?> IsActiveAsync(CancellationToken cancellationToken = default) => new((bool?)Active);
    }

    private sealed class UnavailableProductionFactory : IProductionBatteryPortFactory
    {
        public DeviceCapabilities Capabilities { get; } = DeviceCapabilities.Unavailable();

        public Task<HidSessionResult<IProductionBatteryPort>> OpenAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HidSessionResult<IProductionBatteryPort>(null, HidSessionFailureKind.NativeIoFailure));
    }

    // Reports the raw driver range: core -1000..1000, memory -4000..12000 (NVML
    // units, twice the OpenBlade memory value).
    private sealed class FakeLaptopGpuNvml : INvidiaNvmlClockOffsetApi
    {
        public string DeviceName { get; set; } = string.Empty;
        public int Core { get; private set; }
        public int Memory { get; private set; }
        public List<(NvmlClockType Type, int Value)> Writes { get; } = [];
        public Func<NvmlClockType, int, int>? SetStatus { get; set; }

        public int Initialize() => 0;
        public int Shutdown() => 0;

        public int GetDeviceCount(out uint count)
        {
            count = 1;
            return 0;
        }

        public int GetDeviceHandleByIndex(uint index, out nint device)
        {
            device = 1;
            return 0;
        }

        public int GetDeviceName(nint device, StringBuilder name, uint length)
        {
            name.Clear();
            name.Append(DeviceName);
            return 0;
        }

        public int GetClockOffsets(nint device, ref NvmlClockOffset info)
        {
            bool core = info.Type == NvmlClockType.Graphics;
            info.MinimumMegahertz = core ? -1000 : -4000;
            info.MaximumMegahertz = core ? 1000 : 12000;
            info.CurrentMegahertz = core ? Core : Memory;
            return 0;
        }

        public int SetClockOffsets(nint device, ref NvmlClockOffset info)
        {
            int status = SetStatus?.Invoke(info.Type, info.CurrentMegahertz) ?? 0;
            Writes.Add((info.Type, info.CurrentMegahertz));
            if (status != 0)
            {
                return status;
            }
            if (info.Type == NvmlClockType.Graphics)
            {
                Core = info.CurrentMegahertz;
            }
            else
            {
                Memory = info.CurrentMegahertz;
            }
            return 0;
        }
    }
}
