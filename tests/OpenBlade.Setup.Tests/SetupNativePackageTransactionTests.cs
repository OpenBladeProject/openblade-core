#if OPENBLADE_NATIVE_PACKAGE_TESTS
using OpenBlade.Deployment;
using OpenBlade.Deployment.Tests;

namespace OpenBlade.Setup.Tests;

public sealed partial class SetupCommandRunnerTests
{
    [Theory]
    [InlineData((int)NativeEpochBoundary.Reserved)]
    [InlineData((int)NativeEpochBoundary.FrontierWritten)]
    [InlineData((int)NativeEpochBoundary.SelectionPublished)]
    [InlineData((int)NativeEpochBoundary.EvidenceRetaining)]
    [InlineData((int)NativeEpochBoundary.EvidenceRetained)]
    public async Task NativePreparationFailureRecoversPriorLifecycleAndDisposesWithoutActivation(int boundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        var payload = new NativePayload();
        var native = new SetupNativePackageTransaction(payload, files, package, Guid.NewGuid(), reached =>
        { if ((int)reached == boundary) { throw new IOException("prepare failed"); } });
        var platform = new RecordingSetupPlatform
        {
            PayloadOverride = native,
            ServiceExists = true,
            Transaction = new RecordingPayloadTransaction(true)
        };
        await Assert.ThrowsAsync<IOException>(() => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));
        Assert.False(native.IsActivated);
        Assert.False(native.RequiresExplicitNativeRecovery);
        Assert.Equal(0, payload.Activations);
        Assert.Equal(1, payload.Rollbacks);
        Assert.Equal(1, payload.Disposals);
        Assert.Contains("service-restore", platform.Operations);
        Assert.DoesNotContain("service-install", platform.Operations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => native.ActivateAsync(CancellationToken.None));
        await native.DisposeAsync();
        Assert.Equal(1, payload.Disposals);
        // This is only metadata recovery. No service/process or readiness ran.
        if (boundary != (int)NativeEpochBoundary.Reserved)
        {
            string frontier = Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).Last();
            WindowsNativeEpochTransaction.RecoverUnresolvedPublication(files, NativeCanonical.Digest(File.ReadAllBytes(frontier)));
            Assert.Equal(NativeEpochPhase.Unresolved, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePreparationCancellationNeverActivatesOrReplaysAndAlwaysDisposes(bool afterPublication)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        using var cancel = new CancellationTokenSource();
        var payload = new NativePayload();
        var native = new SetupNativePackageTransaction(payload, files, package, Guid.NewGuid(), reached =>
        { if (afterPublication && reached == NativeEpochBoundary.EvidenceRetained) { cancel.Cancel(); } });
        if (!afterPublication) { cancel.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => native.ActivateAsync(cancel.Token));
        Assert.False(native.RequiresExplicitNativeRecovery);
        await native.RollbackAsync(CancellationToken.None);
        await native.DisposeAsync();
        Assert.Equal(0, payload.Activations);
        Assert.Equal(1, payload.Disposals);
        await Assert.ThrowsAsync<InvalidOperationException>(() => native.ActivateAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PayloadFailureBeforeNativeStartupRecoversButPossibleServiceStartupForbidsFallback(bool possibleStartup)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        var payload = new NativePayload { FailActivation = !possibleStartup };
        var native = new SetupNativePackageTransaction(payload, files, package, Guid.NewGuid());
        var platform = new RecordingSetupPlatform { PayloadOverride = native, FailurePoint = possibleStartup ? "service-install" : null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));
        Assert.Equal(possibleStartup, native.RequiresExplicitNativeRecovery);
        Assert.Equal(possibleStartup ? 0 : 1, payload.Rollbacks);
        Assert.Equal(possibleStartup ? 0 : 1, payload.Disposals);
        Assert.Equal(possibleStartup ? NativeEpochPhase.Unresolved : NativeEpochPhase.Provisional, NativeEpochRecord.Parse(files.ReadCurrent()!).Phase);
        if (possibleStartup) { Assert.DoesNotContain("tray-startup-restore:Shortcut", platform.Operations); }
    }

    private sealed class NativePayload : ISetupPayloadTransaction
    {
        internal int Activations, Rollbacks, Disposals;
        internal bool FailActivation;
        public string StagedPayloadDirectory => "inert";
        public bool HadExistingInstall => true;
        public bool IsActivated { get; private set; }
        public string? CommitWarning => null;
        public Task ActivateAsync(CancellationToken cancellationToken)
        {
            Activations++; IsActivated = true;
            if (FailActivation) { throw new InvalidOperationException("payload activation ambiguous"); }
            return Task.CompletedTask;
        }
        public Task CommitAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("no readiness fixture");
        public Task RollbackAsync(CancellationToken cancellationToken) { Rollbacks++; IsActivated = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
#endif
