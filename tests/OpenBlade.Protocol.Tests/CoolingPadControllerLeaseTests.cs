using System.Diagnostics;

namespace OpenBlade.Protocol.Tests;

public sealed class CoolingPadControllerLeaseTests
{
    [Fact]
    public async Task ServiceOwnerReleaseDoesNotReleaseActiveNativeSessionReference()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new CoolingPadControllerLease(() => new Release(() => released.TrySetResult()));
        Assert.True(await owner.EnsureServiceOwnershipAsync(default));
        var session = owner.Acquire();
        owner.Dispose();
        Assert.False(released.Task.IsCompleted);
        session!.Dispose();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(owner.Acquire());
    }

    [Fact]
    public async Task DefiniteConflictAllowsOnlyASeparatelyRequestedNewAdmission()
    {
        int attempts = 0;
        using var owner = new CoolingPadControllerLease(() => ++attempts == 1 ? null : new Release(() => { }));
        Assert.False(await owner.EnsureServiceOwnershipAsync(default));
        Assert.Equal(1, attempts);
        Assert.True(await owner.EnsureServiceOwnershipAsync(default));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CanceledLateLeaseNeverOpensActorOrAllowsReplacementWhilePending()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int actors = 0;
        var factory = new CoolingPadSessionFactory(() => { actors++; throw new InvalidOperationException(); }, TimeProvider.System,
            production: true, acquireLease: () => { entered.Set(); release.Wait(); return new Release(() => disposed.TrySetResult()); });
        using var cancellation = new CancellationTokenSource();
        var opening = factory.OpenAsync(cancellation.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        cancellation.Cancel();
        Assert.Null((await opening).Session);
        Assert.Null((await factory.OpenAsync(default)).Session);
        release.Set();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, actors);
    }

    [Fact]
    public async Task CanceledServiceLeaseAcquisitionIsOwnedUntilLateRelease()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        using var owner = new CoolingPadControllerLease(() =>
        { Interlocked.Increment(ref attempts); entered.Set(); release.Wait(); return new Release(() => disposed.TrySetResult()); });
        using var cancellation = new CancellationTokenSource();
        var pending = owner.EnsureServiceOwnershipAsync(cancellation.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        cancellation.Cancel();
        Assert.False(await pending);
        owner.Dispose();
        Assert.False(await owner.EnsureServiceOwnershipAsync(default));
        release.Set();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task InertChildCannotOpenHeldLeaseFileButCanAfterRelease()
    {
        // Sharing semantics use an ordinary temp file: no ProgramData, ACL mutation,
        // service action, HID access or privileged child is involved.
        string path = Path.Combine(Path.GetTempPath(), $"OpenBlade-lease-test-{Guid.NewGuid():N}.lock");
        try
        {
            using var owner = new CoolingPadControllerLease(() => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            var reference = owner.Acquire();
            Assert.NotNull(reference);
            Assert.Equal(42, await Probe());
            reference.Dispose();
            Assert.Equal(0, await Probe());
        }
        finally { File.Delete(path); }

        async Task<int> Probe()
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("try { $f=[IO.File]::Open($env:OPENBLADE_LEASE_TEST,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $f.Dispose(); exit 0 } catch [IO.IOException] { exit 42 }");
            start.Environment["OPENBLADE_LEASE_TEST"] = path;
            using var child = Process.Start(start)!;
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return child.ExitCode;
        }
    }

    [Fact]
    public void UnacknowledgedPolicyAssignmentCannotBeReusedOrReset()
    {
        var policy = new CoolingPadTransactionPolicy();
        var command = CoolingPadHostDemandProtocol.CreateSpec(new(2200));
        Assert.Null(policy.Assign(command).Failure);
        Assert.NotNull(policy.Assign(command).Failure);
        policy.Complete(new(command.Arguments, null));
        Assert.NotNull(policy.Assign(command).Failure);
    }

    private sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
