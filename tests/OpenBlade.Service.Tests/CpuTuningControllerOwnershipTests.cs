using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuTuningControllerOwnershipTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.CpuOwnership.Tests", Guid.NewGuid().ToString("N"));
    private static readonly DeviceIdentity Identity = new("RZ09-0528", 0x1532, 0x02C6, "2.02");

    [Fact]
    public async Task LegacyRepeatedEditsAndControllerRecreationRestoreOriginalSavedBaseline()
    {
        var source = new Source();
        byte[] original = (byte[])source.Bytes.Clone();
        var store = new CpuTuningOwnershipJournalStore(Path.Combine(directory, "ownership.json"));
        CpuTuningController Controller() => new(source, store, gate => new Backend(source, gate));
        await SeedAsync(source, store, new(null, new(false, -10)));
        CpuTuningStatus first = await Controller().ObserveAsync(false, default);
        Assert.Equal(0, first.SavedConfiguration!.CurveOptimizer!.AllCoreValue);
        Assert.Equal(CpuTuningPendingBoot.Requested, first.PendingBoot);
        Assert.True(first.RestorePending);
        Assert.False(first.Firmware.CanWrite);
        Assert.Equal(new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer), first.FirmwareRecovery);
        await SeedAsync(source, store, new(null, new(true, -2)));
        CpuTuningStatus second = await Controller().ObserveAsync(false, default);
        Assert.Equal(-2, second.SavedConfiguration!.CurveOptimizer!.AllCoreValue);
        Assert.Equal(first.FirmwareRecovery, second.FirmwareRecovery);
        ControlOperationResult restored = await Controller().RestoreSavedAsync((_, _) => true, default);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.False(restored.CpuTuning!.RestorePending);
        Assert.Equal(original, source.Bytes);
        Assert.Equal(3, source.Submissions);
    }

    [Fact]
    public async Task PossibleLegacySubmissionNeverProjectsRestorationAuthority()
    {
        var source = new Source { UnknownSubmission = true };
        var store = new CpuTuningOwnershipJournalStore(Path.Combine(directory, "ownership.json"));
        var result = await new CpuSavedTuningTransaction(new Backend(source, (_, _) => true), store)
            .ApplyAsync(Identity, new(null, new(false, -10)), default);
        Assert.Equal(CpuSavedTuningOutcome.Uncertain, result.Outcome);
        var controller = new CpuTuningController(source, store, gate => new Backend(source, gate));
        CpuTuningStatus observed = await controller.ObserveAsync(false, default);
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.CurveOptimizer), observed.FirmwareRecovery);
        Assert.Equal(ControlOperationStatus.Failed, (await controller.RestoreSavedAsync((_, _) => true, default)).Status);
        Assert.Equal(ControlOperationStatus.Blocked, (await controller.ApplySavedAsync(new(75, null), (_, _) => true, default)).Status);
        Assert.Equal(1, source.Submissions);
    }

    [Fact]
    public async Task NewSavedRequestCannotReplaceLegacyRestorationBaseline()
    {
        var source = new Source();
        byte[] original = (byte[])source.Bytes.Clone();
        var store = new CpuTuningOwnershipJournalStore(Path.Combine(directory, "ownership.json"));
        await SeedAsync(source, store, new(null, new(false, -10)));
        var controller = new CpuTuningController(source, store, gate => new Backend(source, gate));
        Assert.Equal(ControlOperationStatus.Blocked, (await controller.ApplySavedAsync(new(null, new(true, -2)), (_, _) => true, default)).Status);
        Assert.Equal(original, (await store.LoadAsync(default)).Journal!.BaselineCurrent);
        Assert.True((await controller.ObserveAsync(false, default)).RestorePending);
        Assert.Equal(1, source.Submissions);
    }

    [Fact]
    public async Task FailedLegacyMirrorCheckCannotGrantRestorationAuthority()
    {
        var source = new Source();
        var store = new CpuTuningOwnershipJournalStore(Path.Combine(directory, "ownership.json"));
        await SeedAsync(source, store, new(null, new(false, -1)));
        source.MirrorMismatch = true;
        var controller = new CpuTuningController(source, store, gate => new Backend(source, gate));
        ControlOperationResult blocked = await controller.RestoreSavedAsync((_, _) => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, blocked.Status);
        Assert.Equal(CpuTuningBackendAvailability.Failed, blocked.CpuTuning!.Firmware.Availability);
        Assert.Equal(1, source.Submissions);
    }

    [Fact]
    public async Task NewSavedRequestIsRejectedBeforeSourceBackendOrAdmission()
    {
        var source = new Source();
        var store = new CpuTuningOwnershipJournalStore(Path.Combine(directory, "ownership.json"));
        var controller = new CpuTuningController(source, store, _ => throw new InvalidOperationException("No new saved backend may be created."));
        ControlOperationResult result = await controller.ApplySavedAsync(new(null, new(false, -1)),
            (_, _) => throw new InvalidOperationException("No new saved command may be admitted."), default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, source.Submissions);
        Assert.Equal(0, source.IdentityReads);
        Assert.Null((await store.LoadAsync(default)).Journal);
    }

    private static async Task SeedAsync(Source source, ICpuTuningOwnershipJournalStore store, CpuTuningSettings settings)
    {
        // Historical fake-journal setup only; product requests cannot enter this forward path.
        var result = await new CpuSavedTuningTransaction(new Backend(source, (_, _) => true), store)
            .ApplyAsync(Identity, settings, default);
        Assert.Equal(CpuSavedTuningOutcome.Verified, result.Outcome);
    }

    private sealed class Source : ICpuTuningObservationSource
    {
        internal byte[] Bytes = CpuSavedTuningTransactionTests.Baseline();
        internal int Submissions;
        internal int IdentityReads;
        internal bool MirrorMismatch;
        internal bool UnknownSubmission;
        public DeviceIdentity? ReadIdentity() { IdentityReads++; return Identity; }
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
