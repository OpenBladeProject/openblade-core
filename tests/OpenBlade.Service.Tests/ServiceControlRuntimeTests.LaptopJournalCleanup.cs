using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task LaptopRecovery_ComputerRenameDoesNotPreventExactModelReadback()
    {
        var store = new LaptopControlPendingStore(Path.Combine(directory, "renamed-pending.json"));
        await store.TrySaveAsync(null, new(1, "previous-computer-name", LaptopIdentity,
            [new(FunctionKeysRequest, true)]), CancellationToken.None);
        var device = new CountingBladeDevice { FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys };
        var executor = new LaptopControlExecutor(store);

        await executor.ReconcileAsync(device, LaptopIdentity, CancellationToken.None);

        Assert.False(executor.RequiresReconciliation);
        Assert.Empty(store.Load().Document!.Entries);
        Assert.Equal(0, device.OneShotSetCalls);
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("cancellation")]
    [InlineData("known-not-applied")]
    [InlineData("capability")]
    public async Task LaptopRecovery_KnownNonSubmissionRetriesOnlyJournalCleanup(string refusal)
    {
        string path = Path.Combine(directory, "cleanup-pending.json");
        var store = new LaptopControlPendingStore(path);
        var device = new CountingBladeDevice();
        var executor = new LaptopControlExecutor(store);
        using var cancellation = new CancellationTokenSource();
        FileStream? journalLock = null;
        int admissions = 0;
        bool WriteAllowed()
        {
            if (++admissions == 2 && refusal is "authority" or "cancellation")
            {
                journalLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (refusal == "cancellation")
                {
                    cancellation.Cancel();
                }
                return false;
            }
            return true;
        }
        device.AfterOneShotSet = _ =>
        {
            journalLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.OneShotFailure = refusal == "capability"
                ? new CapabilityUnavailableException("Refused before submission")
                : new BladeWriteException(WriteSubmissionState.KnownNotApplied, "Refused before submission");
        };
        try
        {
            var request = new ApplyOneShotLaptopControl(OneShotLaptopControl.MicrophoneMuteIndicator, Enabled: true);
            if (refusal == "cancellation")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(
                    device, LaptopIdentity, request, true, WriteAllowed, cancellation.Token));
            }
            else
            {
                LaptopControlApplyOutcome result = await executor.ExecuteAsync(
                    device, LaptopIdentity, request, true, WriteAllowed, CancellationToken.None);
                Assert.Equal(ControlOperationStatus.Blocked, result.Result.Status);
            }
            Assert.True(store.Load().RequiresReconciliation);
            Assert.True(executor.RequiresReconciliation);
        }
        finally
        {
            journalLock?.Dispose();
        }

        int setters = device.OneShotSetCalls;
        var restarted = new LaptopControlExecutor(store);
        await restarted.ReconcileAsync(device, LaptopIdentity, CancellationToken.None);
        Assert.True(restarted.RequiresReconciliation); // Restart cannot recover process-local proof.

        await executor.ReconcileAsync(device, LaptopIdentity, CancellationToken.None);

        Assert.False(executor.RequiresReconciliation);
        Assert.Empty(store.Load().Document!.Entries);
        Assert.Equal(setters, device.OneShotSetCalls);
        Assert.Equal(refusal is "authority" or "cancellation" ? 0 : 1, setters);
    }

    [Fact]
    public async Task LaptopRecovery_KnownNonSubmissionDoesNotClearAReplacementRecord()
    {
        string path = Path.Combine(directory, "replacement-pending.json");
        var store = new LaptopControlPendingStore(path);
        var device = new CountingBladeDevice();
        var executor = new LaptopControlExecutor(store);
        FileStream? journalLock = null;
        device.AfterOneShotSet = _ =>
        {
            journalLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.OneShotFailure = new BladeWriteException(WriteSubmissionState.KnownNotApplied, "Refused");
        };
        try
        {
            await executor.ExecuteAsync(device, LaptopIdentity,
                new(OneShotLaptopControl.MicrophoneMuteIndicator, Enabled: true),
                true, static () => true, CancellationToken.None);
        }
        finally
        {
            journalLock?.Dispose();
        }
        LaptopControlPendingLoad pending = store.Load();
        var replacement = pending.Document! with
        {
            Entries = [new(new(OneShotLaptopControl.MicrophoneMuteIndicator, Enabled: false), true)],
        };
        Assert.True(await store.TrySaveAsync(pending.Sha256, replacement, CancellationToken.None));

        await executor.ReconcileAsync(device, LaptopIdentity, CancellationToken.None);

        Assert.True(executor.RequiresReconciliation);
        Assert.False(Assert.Single(store.Load().Document!.Entries).Requested.Enabled);
        Assert.Equal(1, device.OneShotSetCalls);
    }
}
