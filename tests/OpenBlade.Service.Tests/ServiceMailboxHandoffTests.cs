using OpenBlade.Deployment;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceMailboxHandoffTests
{
#if OPENBLADE_NATIVE_PACKAGE_TESTS
    [Fact]
    public async Task ActualCoordinatorPublishesTheProtectedTransferConsumedByTheNativeReadFixture()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        files.PublishCustody(Path.Combine(files.Root, "exclusion.lock"), NativeCanonical.Lines("OpenBlade.MailboxExclusion.v1"));
        var record = new ManagedMailboxRun(new string('1', 32), "17:100", new string('2', 64), new string('3', 64), new string('4', 32));
        using var run = WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), record);
        using var coordinator = new ServiceMailboxHandoff(run);
        var request = new ManagedMailboxRequest(NativeCanonical.Digest(record.Encode()), new string('5', 32), new string('6', 32), new string('7', 64));
        using var transfer = new WindowsManagedMailboxTransfer(files.RetainFixtureView(), record.Encode(), request);
        var native = coordinator.Lifetime.Reserve();
        var callback = native.Child();
        Task completion = coordinator.CompleteCoreAsync(() => { native.Dispose(); return Task.CompletedTask; }, () => { }, true, default);
        Assert.False(File.Exists(Path.Combine(files.Root, "handoff-" + request.Request + "-receipt.bin")));
        Assert.False(completion.IsCompleted);
        callback.Dispose();
        await completion;
        Assert.ThrowsAny<IOException>(transfer.ConfirmOriginalExited);
        coordinator.Dispose();
        transfer.ConfirmOriginalExited();
        using var epochs = WindowsNativeEpochFiles.CreatePrivateFixture();
        Directory.CreateDirectory(Path.Combine(epochs.Root, new string('e', 32)));
        transfer.BindEpoch(epochs, new NativeEpochRecord(NativeEpochPhase.Provisional, new string('e', 32), new string('0', 64), 1,
            request.Installation, request.Package, request.Request, new string('a', 64) + "-service", new string('b', 64) + "-agent", new string('0', 64), new string('f', 64)));
        using var parity = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "managed-mailbox-handoff-v2.json")));
        foreach (string name in new[] { "run", "request", "receipt", "mapping" })
        {
            string file = name == "run" ? "current.bin" : "handoff-" + request.Request + "-" + name + ".bin";
            Assert.Equal(parity.RootElement.GetProperty(name).GetString(), System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(files.Root, file))));
        }
    }
#endif

    [Fact]
    public void ProductionConstructionAcquiresNothingAndRefusesTransportUntilOpened()
    {
        // DI resolution must not touch the machine store; Open does, from the startup service.
        var coordinator = new ServiceMailboxHandoff();
        Assert.Throws<IOException>(() => coordinator.Lifetime.Reserve());
    }

    [Fact]
    public async Task StoredFirmwareUncertaintyDoesNotReplaceRealResourceDrain()
    {
        var run = new InjectedRun();
        var coordinator = new ServiceMailboxHandoff(run);
        bool firmwareStored = true;
        var handle = coordinator.Lifetime.Reserve();
        var native = coordinator.Lifetime.Reserve();
        int guardianChecks = 0;
        Task closing = coordinator.CompleteCoreAsync(() => { handle.Dispose(); return Task.CompletedTask; },
            () => guardianChecks++, true, default);
        Assert.False(closing.IsCompleted);
        Assert.Equal(0, run.Receipts);
        using (var callback = native.Child())
        {
            native.Dispose();
            Assert.False(closing.IsCompleted);
        }
        await closing;
        Assert.True(firmwareStored);
        Assert.Equal(2, guardianChecks);
        Assert.Equal(1, run.Receipts);
        coordinator.Dispose();
        Assert.Equal(1, run.Disposals);
        await Assert.ThrowsAsync<IOException>(() => coordinator.CompleteCoreAsync(() => Task.CompletedTask, () => { }, true, default));
    }

    [Fact]
    public async Task CancellationAndLateCompletionQuarantineTheOriginalExclusionWithoutReplay()
    {
        var run = new InjectedRun();
        var coordinator = new ServiceMailboxHandoff(run);
        var native = coordinator.Lifetime.Reserve();
        using var cancellation = new CancellationTokenSource();
        Task closing = coordinator.CompleteCoreAsync(() => Task.CompletedTask, () => { }, true, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closing);
        coordinator.Dispose();
        Assert.Equal(0, run.Disposals);
        native.Dispose();
        await Assert.ThrowsAsync<IOException>(() => coordinator.Lifetime.WhenTerminal);
        Assert.Equal(0, run.Receipts);
        await Assert.ThrowsAsync<IOException>(() => coordinator.CompleteCoreAsync(() => Task.CompletedTask, () => { }, true, default));
    }

    [Fact]
    public async Task UnknownGuardianOrFaultedOwnerDisposalNeverPublishesTransportReceipt()
    {
        foreach (bool guardianUnknown in new[] { false, true })
        {
            var run = new InjectedRun();
            var coordinator = new ServiceMailboxHandoff(run);
            await Assert.ThrowsAsync<IOException>(() => coordinator.CompleteCoreAsync(
                () => Task.FromException(new IOException("detached disposal failed")),
                () => { if (guardianUnknown) { throw new IOException("external guardian startup custody unknown"); } }, true, default));
            coordinator.Dispose();
            Assert.Equal(0, run.Receipts);
            Assert.Equal(0, run.Disposals);
        }
    }

    [Fact]
    public async Task OwnerCloseAdmitsResidualCleanupBeforeSealing()
    {
        var run = new InjectedRun();
        using var coordinator = new ServiceMailboxHandoff(run);
        await coordinator.CompleteCoreAsync(() =>
        {
            using var cleanup = coordinator.Lifetime.Reserve();
            return Task.CompletedTask;
        }, () => { }, true, default);
        Assert.Equal(1, run.Receipts);
        Assert.Throws<IOException>(() => coordinator.Lifetime.Reserve());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerCloseUsesCallerBudgetAndLateCompletionCannotPublish(bool synchronous)
    {
        var run = new InjectedRun();
        var coordinator = new ServiceMailboxHandoff(run);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task closing = coordinator.CompleteCoreAsync(() =>
        {
            entered.SetResult();
            if (synchronous) { release.Task.GetAwaiter().GetResult(); return Task.CompletedTask; }
            return release.Task;
        }, () => { }, true, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closing.WaitAsync(TimeSpan.FromSeconds(5)));
        coordinator.Dispose();
        Assert.Equal(0, run.Disposals);
        Assert.Equal(0, run.Receipts);
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => coordinator.Lifetime.WhenTerminal);
        Assert.Equal(0, run.Receipts);
    }

    [Fact]
    public async Task ExpiredBudgetDoesNotStartOwnerCleanupOrReleaseCustody()
    {
        var run = new InjectedRun();
        var coordinator = new ServiceMailboxHandoff(run);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool closed = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.CompleteCoreAsync(
            () => { closed = true; return Task.CompletedTask; }, () => { }, true, cancellation.Token));
        coordinator.Dispose();
        Assert.False(closed);
        Assert.Equal(0, run.Receipts);
        Assert.Equal(0, run.Disposals);
    }

    private sealed class InjectedRun : IManagedMailboxRun
    {
        internal int Receipts { get; private set; }
        internal int Disposals { get; private set; }
        public void PublishQuiescent() => Receipts++;
        public void Dispose() => Disposals++;
    }
}
