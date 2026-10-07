using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuTuningControllerOwnershipTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.CpuOwnership.Tests", Guid.NewGuid().ToString("N"));
    private static readonly DeviceIdentity Identity = new("RZ09-0528", 0x1532, 0x02C6, "2.02");

    [Fact]
    public async Task ControllerRepeatedEditsAndRecreationRestoreOriginalSavedBaseline()
    {
        var source = new Source();
        byte[] original = (byte[])source.Bytes.Clone();
        var store = new CpuTuningOwnershipJournalStore(Path.Combine(directory, "ownership.json"));
        CpuTuningController Controller() => new(source, store, gate => new Backend(source, gate));
        ControlOperationResult first = await Controller().ApplySavedAsync(new(null, new(false, -10)),
            (_, _) => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, first.Status);
        Assert.Equal(0, first.CpuTuning!.SavedConfiguration!.CurveOptimizer!.AllCoreValue);
        Assert.Null(first.CpuTuning.LiveState);
        Assert.Equal(CpuTuningPendingBoot.Requested, first.CpuTuning.PendingBoot);
        Assert.True(first.CpuTuning.RestorePending);
        Assert.Equal(new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer), first.CpuTuning.FirmwareRecovery);
        Assert.Null(first.CpuTuning.LiveRecovery);

        ControlOperationResult second = await Controller().ApplySavedAsync(new(null, new(true, -2)),
            (_, _) => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, second.Status);
        Assert.Equal(-2, second.CpuTuning!.SavedConfiguration!.CurveOptimizer!.AllCoreValue);
        CpuTuningStatus observed = await Controller().ObserveAsync(false, CancellationToken.None);
        Assert.True(observed.RestorePending);
        Assert.Equal(CpuTuningPendingBoot.Requested, observed.PendingBoot);
        Assert.True(observed.Firmware.CanWrite);
        Assert.Equal(first.CpuTuning.FirmwareRecovery, observed.FirmwareRecovery);

        ControlOperationResult restored = await Controller().RestoreSavedAsync((_, _) => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.False(restored.CpuTuning!.RestorePending);
        Assert.Equal(new(CpuTuningRecoveryState.None, CpuTuningFeatures.None), restored.CpuTuning.FirmwareRecovery);
        Assert.Null(restored.CpuTuning.LiveState);
        Assert.Equal(original, source.Bytes);
        Assert.Equal(3, source.Submissions);
    }

    [Fact]
    public async Task PossibleSubmissionReadbackNeverProjectsRestorationAuthority()
    {
        var source = new Source { UnknownSubmission = true };
        var store = new CpuTuningOwnershipJournalStore(Path.Combine(directory, "ownership.json"));
        var controller = new CpuTuningController(source, store, gate => new Backend(source, gate));
        ControlOperationResult uncertain = await controller.ApplySavedAsync(new(null, new(false, -10)),
            (_, _) => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Failed, uncertain.Status);
        Assert.True(uncertain.CpuTuning!.RestorePending);
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.CurveOptimizer), uncertain.CpuTuning.FirmwareRecovery);
        Assert.Null(uncertain.CpuTuning.LiveRecovery);
        CpuTuningStatus observed = await controller.ObserveAsync(false, CancellationToken.None);
        Assert.Equal(uncertain.CpuTuning.FirmwareRecovery, observed.FirmwareRecovery);
        await controller.RestoreSavedAsync((_, _) => true, CancellationToken.None);
        await controller.ApplySavedAsync(new(75, null), (_, _) => true, CancellationToken.None);
        Assert.Equal(1, source.Submissions);
    }

    [Fact]
    public async Task UnchangedOwnedIntentRetainsItsBaselineAndSaysSo()
    {
        var source = new Source();
        var store = new CpuTuningOwnershipJournalStore(Path.Combine(directory, "ownership.json"));
        var controller = new CpuTuningController(source, store, gate => new Backend(source, gate));
        await controller.ApplySavedAsync(new(null, new(false, -10)), (_, _) => true, CancellationToken.None);
        ControlOperationResult unchanged = await controller.ApplySavedAsync(new(null, new(false, -12)),
            (_, _) => true, CancellationToken.None);
        Assert.True(unchanged.CpuTuning!.RestorePending);
        Assert.Contains("baseline was retained", unchanged.Message);
        Assert.Equal(1, source.Submissions);
    }

    [Fact]
    public async Task FailedMirrorCheckCannotBecomeAvailableInMutationProjection()
    {
        var source = new Source { MirrorMismatch = true };
        var store = new CpuTuningOwnershipJournalStore(Path.Combine(directory, "ownership.json"));
        var controller = new CpuTuningController(source, store, gate => new Backend(source, gate));
        ControlOperationResult blocked = await controller.ApplySavedAsync(new(null, new(false, -1)),
            (_, _) => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, blocked.Status);
        Assert.Equal(CpuTuningBackendAvailability.Failed, blocked.CpuTuning!.Firmware.Availability);
        Assert.Equal(0, source.Submissions);
    }

    [Fact]
    public async Task FreshRuntimeDenialPreventsOwnershipAndFirmwareSubmission()
    {
        var source = new Source();
        var store = new CpuTuningOwnershipJournalStore(Path.Combine(directory, "ownership.json"));
        var controller = new CpuTuningController(source, store, gate => new Backend(source, gate));
        ControlOperationResult result = await controller.ApplySavedAsync(new(null, new(false, -1)),
            (_, _) => false, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, source.Submissions);
        Assert.Null((await store.LoadAsync(CancellationToken.None)).Journal);
    }

    private sealed class Source : ICpuTuningObservationSource
    {
        internal byte[] Bytes = CpuSavedTuningTransactionTests.Baseline();
        internal int Submissions;
        internal bool MirrorMismatch;
        internal bool UnknownSubmission;
        public DeviceIdentity? ReadIdentity() => Identity;
        public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity)
        {
            byte[] last = (byte[])Bytes.Clone();
            if (MirrorMismatch) { last[799] ^= 1; }
            return new(MirrorMismatch ? SavedCpuFirmwareReadStatus.MirrorMismatch : SavedCpuFirmwareReadStatus.Observed,
                Rz090528CpuFirmwareCodec.DecodeSaved(Bytes), Rz090528CpuFirmwareCodec.DecodeSaved(last), 0, 0);
        }
        public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool enabled) =>
            new(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Unavailable);
    }

    private sealed class Backend(Source source, Func<DeviceIdentity, CpuSavedTuningCommand, bool> gate) : ICpuSavedTuningBackend
    {
        public Task<bool> CheckWriteAdmissionAsync(DeviceIdentity identity, CpuSavedTuningCommand command, CancellationToken token) =>
            Task.FromResult(gate(identity, command));
        public Task<SavedCpuFirmwareReadResult> ReadAsync(DeviceIdentity identity, CancellationToken token) =>
            Task.FromResult(source.ReadSaved(identity));
        public Task<CpuSavedTuningSubmission> SendAsync(DeviceIdentity identity, CpuSavedTuningCommand command, CancellationToken token)
        {
            if (!gate(identity, command)) { return Task.FromResult(CpuSavedTuningSubmission.NotSubmitted); }
            source.Submissions++;
            source.Bytes = CpuSavedTuningPlan.Simulate(source.Bytes, command);
            return Task.FromResult(source.UnknownSubmission ? CpuSavedTuningSubmission.Unknown : CpuSavedTuningSubmission.Acknowledged);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }
}
