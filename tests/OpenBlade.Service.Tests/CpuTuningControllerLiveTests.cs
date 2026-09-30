using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuTuningControllerLiveTests
{
    [Fact]
    public async Task CapturedAdmissionExpiresAndLaterRequestsCannotRenewIt()
    {
        var store = new CpuLivePowerTuningTransactionTests.MemoryStore();
        var backend = new CpuLivePowerTuningTransactionTests.FakeBackend(store);
        var controller = new CpuTuningController(new UnusedSource(), powerBackend: backend, powerStore: store);
        var outside = controller.CaptureLiveRuntimeAdmission(CpuTuningFeatures.PowerLimit);
        Func<bool>? first = null;
        backend.Admit = (_, _) =>
        {
            var current = controller.CaptureLiveRuntimeAdmission(CpuTuningFeatures.PowerLimit);
            Assert.True(current());
            if (first is null) { first = current; }
            return true;
        };
        Assert.False(outside());
        var applied = await controller.MutateLiveAsync(new(45, null), false, _ => true, default);
        Assert.Equal(ControlOperationStatus.Stored, applied.Status);
        Assert.False(first!());
        backend.Admit = (_, _) =>
        {
            Assert.False(first!());
            Assert.True(controller.CaptureLiveRuntimeAdmission(CpuTuningFeatures.PowerLimit)());
            return true;
        };
        Assert.Equal(ControlOperationStatus.Stored,
            (await controller.MutateLiveAsync(null, true, _ => true, default)).Status);
        Assert.False(first!());
    }

    [Fact]
    public async Task RuntimeRevocationAfterPendingFlushSkipsSetterAndRetainsPreparedOwnership()
    {
        var store = new CpuLivePowerTuningTransactionTests.MemoryStore();
        var backend = new CpuLivePowerTuningTransactionTests.FakeBackend(store);
        var controller = new CpuTuningController(new UnusedSource(), powerBackend: backend, powerStore: store);
        bool admitted = true;
        store.BeforeWrite = index => { if (index == 2) { admitted = false; } return Task.CompletedTask; };
        var result = await controller.MutateLiveAsync(new(45, null), false, _ => admitted, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Empty(backend.Sent);
        Assert.Equal(CpuLivePowerOwnershipState.Prepared, store.Journal!.State);
        Assert.Equal(0, store.Journal.CompletedSteps);
        Assert.Equal(CpuTuningRecoveryState.Blocked, result.CpuTuning!.LiveRecovery!.State);
    }

    [Fact]
    public async Task ExistingOwnershipInBothFamiliesPreventsRestoreBeforeAnyGetter()
    {
        var curveStore = new CpuLiveCurveTuningTransactionTests.MemoryStore();
        var curve = new CpuLiveCurveTuningTransactionTests.FakeBackend(curveStore);
        var powerStore = new CpuLivePowerTuningTransactionTests.MemoryStore();
        var power = new CpuLivePowerTuningTransactionTests.FakeBackend(powerStore);
        await new CpuLiveCurveTuningTransaction(curve, curveStore).ApplySelectiveAsync([new(0, -2)], default);
        await new CpuLivePowerTuningTransaction(power, powerStore).ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 45)], default);
        int curveReads = curve.Reads, powerReads = power.Reads;
        var controller = new CpuTuningController(new UnusedSource(), curveBackend: curve, curveStore: curveStore,
            powerBackend: power, powerStore: powerStore);
        var result = await controller.MutateLiveAsync(null, true, _ => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(CpuTuningFeatures.CurveOptimizer | CpuTuningFeatures.PowerLimit, result.CpuTuning!.LiveRecovery!.Features);
        Assert.Equal(curveReads, curve.Reads); Assert.Equal(powerReads, power.Reads);
        Assert.Single(curve.Sent); Assert.Single(power.Sent);
    }

    private sealed class UnusedSource : ICpuTuningObservationSource
    {
        public DeviceIdentity? ReadIdentity() => throw new InvalidOperationException("Live operations cannot use saved identity.");
        public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity) => throw new InvalidOperationException();
        public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend) => throw new InvalidOperationException();
    }
}
