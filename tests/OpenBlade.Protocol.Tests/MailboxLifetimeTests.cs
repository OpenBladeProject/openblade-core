using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class MailboxLifetimeTests
{
    [Fact]
    public async Task ActualNativeOpenReservesBeforeTheOpenAndTransfersCandidateRetirement()
    {
        var scope = new MailboxLifetime();
        var native = new WindowsHidNative(scope, (_, _) =>
        {
            scope.Seal();
            Assert.False(scope.WhenTerminal.IsCompleted);
            return new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(123), ownsHandle: false);
        });
        var result = native.TryOpen("injected-no-device", new(0, 0, 0, 0, false));
        Assert.NotNull(result.Candidate);
        Assert.False(scope.WhenTerminal.IsCompleted);
        result.Candidate!.Dispose();
        await scope.WhenTerminal;
        int attempts = 0;
        var sealedNative = new WindowsHidNative(scope, (_, _) => { attempts++; throw new InvalidOperationException(); });
        Assert.Throws<IOException>(() => sealedNative.TryOpen("injected-no-device", new(0, 0, 0, 0, false)));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public void ActualNativeOpenWaitsForCustodyAdmission()
    {
        var scope = new MailboxLifetime(admitted: false);
        int attempts = 0;
        var native = new WindowsHidNative(scope, (_, _) =>
        {
            attempts++;
            return new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(123), ownsHandle: false);
        });
        Assert.Throws<IOException>(() => native.TryOpen("injected-no-device", new(0, 0, 0, 0, false)));
        Assert.Equal(0, attempts);
        scope.Admit();
        var result = native.TryOpen("injected-no-device", new(0, 0, 0, 0, false));
        Assert.NotNull(result.Candidate);
        Assert.Equal(1, attempts);
        result.Candidate!.Dispose();
    }

    [Fact]
    public async Task ActualNativeCompletionCallbackRetainsScopeAfterOperationFinalization()
    {
        var scope = new MailboxLifetime();
        var operation = scope.Reserve();
        scope.Seal();
        var callback = new NativeCompletionContext(_ =>
        {
            operation.Dispose();
            Assert.False(scope.WhenTerminal.IsCompleted);
        }, operation);
        callback.Complete(0, 0);
        await scope.WhenTerminal;
        callback.Complete(0, 0); // Duplicate OS notifications never add/replay participation.
    }

    [Fact]
    public async Task SealingPreventsAcquisitionAndWaitsForLateNativeCallbackAfterWorkerExit()
    {
        var scope = new MailboxLifetime();
        var handle = scope.Reserve();
        var worker = handle.Child();
        var native = scope.Reserve();
        scope.Seal();
        Assert.Throws<IOException>(() => scope.Reserve());
        worker.Dispose();
        handle.Dispose();
        Assert.False(scope.WhenTerminal.IsCompleted);
        // A callback may arrive after caller and worker completion. It derives
        // from the original operation and is admitted despite external sealing.
        using (var callback = native.Child())
        {
            native.Dispose();
            Assert.False(scope.WhenTerminal.IsCompleted);
        }
        await scope.WhenTerminal;
    }

    [Fact]
    public async Task DetachedFaultedDisposalNeverBecomesASuccessfulEmptyAggregate()
    {
        var scope = new MailboxLifetime();
        var resource = scope.Reserve();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task disposal = resource.RunAsync(() => finish.Task);
        scope.Seal();
        Assert.False(scope.WhenTerminal.IsCompleted);
        finish.SetException(new IOException("native free failed"));
        await Assert.ThrowsAsync<IOException>(() => disposal);
        await Assert.ThrowsAsync<IOException>(() => scope.WhenTerminal);
        Assert.Throws<IOException>(() => scope.Reserve());
    }

    [Fact]
    public async Task AcquisitionAndSealingHaveOneAtomicWinner()
    {
        var scope = new MailboxLifetime();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MailboxLifetime.Participation? admitted = null;
        Task acquire = Task.Run(async () =>
        {
            await start.Task;
            try { admitted = scope.Reserve(); }
            catch (IOException) { }
        });
        Task seal = Task.Run(async () => { await start.Task; scope.Seal(); });
        start.SetResult();
        await Task.WhenAll(acquire, seal);
        if (admitted is not null)
        {
            Assert.False(scope.WhenTerminal.IsCompleted);
            admitted.Dispose();
        }
        await scope.WhenTerminal;
        Assert.Throws<IOException>(() => scope.Reserve());
    }
}
