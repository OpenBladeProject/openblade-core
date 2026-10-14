using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;
using static OpenBlade.Service.Tests.VerifiedBladeProviderFixture;

namespace OpenBlade.Service.Tests;

public sealed class ExperimentalBladeBatteryIntegrationTests
{
    [Fact]
    public async Task Provider_AdmitsRangeChangesFromOffAndConfirmsExactLimit()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        var io = new Io { Raw = 0x50 };
        var factory = new ExperimentalBladeFactory(model, new Identity(), new Native(), new Guard(), io,
            () => PowerSource.ExternalUnknown);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready, BatteryValidationFirmwareCompatibility.ModelScoped),
            factory, UnavailableFanRpmFactory());
        provider.SetAllowExperimentalFeatures(true);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        ConfirmedDeviceState baseline = await device.ReadStateAsync(CancellationToken.None);
        Assert.False(baseline.Battery!.ProtectionEnabled);
        Assert.False(baseline.Battery.TemporaryFullCharge);
        var coordinator = new ControlCoordinator(device);
        var desired = new DesiredBatteryState(BatteryPolicy.Create(99), true, false);
        ScopedApplyOutcome changed = await coordinator.ApplyBatteryOutcomeAsync(desired, CancellationToken.None);
        Assert.Equal(ControlResultStatus.Applied, changed.Result.Status);
        Assert.Equal(99, changed.Readback!.Battery!.ChargeLimitPercent);
        Assert.False(changed.Readback.Battery.TemporaryFullCharge);
        Assert.Equal(0xE3, io.Raw);
        Assert.Single(io.Setters);
        Assert.Equal((byte)0xE3, io.Setters[0]);
        ScopedApplyOutcome disabled = await coordinator.ApplyBatteryOutcomeAsync(
            new DesiredBatteryState(BatteryPolicy.Create(80), false, false), CancellationToken.None);
        Assert.Equal(ControlResultStatus.Applied, disabled.Result.Status);
        Assert.False(disabled.Readback!.Battery!.ProtectionEnabled);
        Assert.Null(disabled.Readback.Battery.ChargeLimitPercent);
        Assert.Equal(new byte[] { 0xE3, 0x50 }, io.Setters);
        provider.SetAllowExperimentalFeatures(false);
        int writes = io.Setters.Count;
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => device.SetChargeLimitAsync(BatteryPolicy.Create(51), CancellationToken.None));
        Assert.Equal(writes, io.Setters.Count);
    }

    private sealed class Identity : IHostIdentitySource
    {
        public HostIdentityReadResult ReadExactIdentity() => new(
            new HostIdentityEvidence("Razer", "Blade 18 - RZ09-0484", null, "test"), null);
    }
    private sealed class Guard : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => new([], null);
    }
    private sealed class Native : IHidCandidateNative
    {
        public IReadOnlyList<string> EnumerateDevicePaths() => ["test"];
        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters) =>
            new(new OpenedCandidateHandle(new SafeFileHandle(new IntPtr(501), ownsHandle: false)), null);
        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle) => new(0x1532, 0x02A0, 1, 2, 91);
        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle)
        {
            SafeFileHandle file = handle.TransferOwnership();
            var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
            return new(file, bound, () => { }, file.Dispose);
        }
    }
    private sealed class Io : IHidFeatureIo
    {
        private byte[] request = [];
        internal byte Raw { get; set; }
        internal List<byte> Setters { get; } = [];
        public Task<HidFeatureIoResult> ExecuteAsync(OpenedHidHandle handle, uint code,
            byte[] buffer, CancellationToken caller, CancellationToken deadline)
        {
            if (code == 0x000B0191)
            {
                request = buffer.ToArray();
                if (request[7] == 0x07 && request[8] == 0x12)
                {
                    Raw = request[9];
                    Setters.Add(Raw);
                }
            }
            else
            {
                request.CopyTo(buffer, 0);
                buffer[1] = 2;
                if (buffer[7] == 0x07 && buffer[8] == 0x92)
                {
                    buffer[9] = Raw;
                    buffer[89] ^= Raw;
                }
            }
            return Task.FromResult(new HidFeatureIoResult(new(true, buffer.Length, 0), true,
                HidFeatureTermination.Completed));
        }
    }
}
