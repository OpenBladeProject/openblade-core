using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ManagedOwnerHandoffTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task QuiesceDrainsBothOwnersAndHandbackRequiresExactExitAndFreshMatchingRead()
    {
        using var deadline = new CancellationTokenSource(Timeout);
        using var fixture = new Fixture(holdMatrixClose: true);
        using Process child = StartWaitingChild();
        using WindowsVerifiedBladeDeviceProvider owner = fixture.CreateOwner();
        try
        {
            Assert.True(owner.TryGetVerifiedDevice(out IBladeDevice retained));
            Assert.True(await retained.ReadDriverModeAsync(deadline.Token));
            Assert.True(fixture.LightingControl!.HasOpenHandle);
            Assert.Throws<IOException>(() => fixture.OpenMatrixFile().Dispose());

            fixture.HoldNextControlRead();
            Task<AdapterPowerState> inFlight = retained.ReadAdapterPowerStateAsync(deadline.Token);
            await fixture.ControlReadStarted.Task.WaitAsync(deadline.Token);
            Task<AdapterPowerState> queued = retained.ReadAdapterPowerStateAsync(deadline.Token);
            Task<ManagedOwnerHandoff.Lease> transfer = owner.QuiesceForOwnerHandoffAsync(
                child.SafeHandle, AdapterPowerState.FullPower280W, deadline.Token);

            Assert.True(File.Exists(fixture.Marker));
            Assert.False(transfer.IsCompleted);
            await AssertFencedAsync(owner, retained, fixture, deadline.Token);
            Assert.Throws<IOException>(() => fixture.OpenControlFile().Dispose());

            fixture.ReleaseControlRead.TrySetResult();
            Assert.Equal(AdapterPowerState.FullPower280W, await inFlight);
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => queued);
            await fixture.MatrixCloseStarted.Task.WaitAsync(deadline.Token);
            Assert.False(transfer.IsCompleted);
            Assert.Throws<IOException>(() => fixture.OpenMatrixFile().Dispose());
            await AssertFencedAsync(owner, retained, fixture, deadline.Token);

            fixture.ReleaseMatrixClose.TrySetResult();
            ManagedOwnerHandoff.Lease lease = await transfer;
            Assert.False(fixture.LightingControl!.HasOpenHandle);
            using (fixture.OpenControlFile())
            using (fixture.OpenMatrixFile())
            {
                Assert.False(await owner.CompleteOwnerHandoffAsync(lease, deadline.Token));
                await AssertFencedAsync(owner, retained, fixture, deadline.Token);
            }

            await ExitChildAsync(child, deadline.Token);
            Assert.False(await owner.CompleteOwnerHandoffAsync(lease with { }, deadline.Token));
            int beforeHandback = fixture.ControlOpens;
            fixture.AdapterPowerLevel = 0x00;
            Assert.False(await owner.CompleteOwnerHandoffAsync(lease, deadline.Token));
            Assert.True(File.Exists(fixture.Marker));
            Assert.Equal(beforeHandback + 1, fixture.ControlOpens);
            Assert.Equal(1, fixture.MatrixOpens);

            fixture.AdapterPowerLevel = 0x11;
            Assert.True(await owner.CompleteOwnerHandoffAsync(lease, deadline.Token));
            Assert.False(File.Exists(fixture.Marker));
            Assert.False(await owner.CompleteOwnerHandoffAsync(lease, deadline.Token));
            Assert.Equal(beforeHandback + 2, fixture.ControlOpens);
            Assert.True(owner.TryGetVerifiedDevice(out IBladeDevice resumed));
            Assert.Equal(AdapterPowerState.FullPower280W, await resumed.ReadAdapterPowerStateAsync(deadline.Token));
            Assert.True(await resumed.ReadDriverModeAsync(deadline.Token));
            Assert.Equal(2, fixture.MatrixOpens);
        }
        finally
        {
            fixture.ReleaseControlRead.TrySetResult();
            fixture.ReleaseMatrixClose.TrySetResult();
            StopChild(child);
        }
    }

    [Fact]
    public async Task CanceledDrainDoesNotGrantAndRestartWithItsMarkerRemainsFenced()
    {
        using var deadline = new CancellationTokenSource(Timeout);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var fixture = new Fixture(holdMatrixClose: true);
        using Process child = StartWaitingChild();
        try
        {
            using (WindowsVerifiedBladeDeviceProvider owner = fixture.CreateOwner())
            {
                try
                {
                    Assert.True(owner.TryGetVerifiedDevice(out IBladeDevice retained));
                    Assert.True(await retained.ReadDriverModeAsync(deadline.Token));
                    Task<ManagedOwnerHandoff.Lease> transfer = owner.QuiesceForOwnerHandoffAsync(
                        child.SafeHandle, AdapterPowerState.FullPower280W, canceled.Token);
                    await fixture.MatrixCloseStarted.Task.WaitAsync(deadline.Token);
                    canceled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
                    Assert.True(File.Exists(fixture.Marker));
                    await AssertFencedAsync(owner, retained, fixture, deadline.Token);
                }
                finally
                {
                    fixture.ReleaseMatrixClose.TrySetResult();
                }
            }

            await ExitChildAsync(child, deadline.Token);
            using WindowsVerifiedBladeDeviceProvider restarted = fixture.CreateOwner();
            Assert.False(restarted.TryGetRecentlyVerifiedReadOnlyDevice(out _));
            await Assert.ThrowsAsync<CapabilityUnavailableException>(async () =>
                await restarted.AcquireFanStopHardwareLeaseAsync(deadline.Token));
            Assert.True(File.Exists(fixture.Marker));
            Assert.Equal(0, fixture.ControlOpens);
        }
        finally
        {
            StopChild(child);
        }
    }

    [Fact]
    public async Task CanceledReconciliationAfterChildExitLeavesEvidenceForRestart()
    {
        using var deadline = new CancellationTokenSource(Timeout);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var fixture = new Fixture();
        using Process child = StartWaitingChild();
        try
        {
            using (WindowsVerifiedBladeDeviceProvider owner = fixture.CreateOwner())
            {
                ManagedOwnerHandoff.Lease lease = await owner.QuiesceForOwnerHandoffAsync(
                    child.SafeHandle, AdapterPowerState.FullPower280W, deadline.Token);
                await ExitChildAsync(child, deadline.Token);
                fixture.HoldNextControlRead();
                Task<bool> handback = owner.CompleteOwnerHandoffAsync(lease, canceled.Token);
                await fixture.ControlReadStarted.Task.WaitAsync(deadline.Token);
                canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handback);
                Assert.True(File.Exists(fixture.Marker));
                Assert.False(owner.TryGetVerifiedDevice(out _));
            }

            using WindowsVerifiedBladeDeviceProvider restarted = fixture.CreateOwner();
            Assert.False(restarted.TryGetVerifiedDevice(out _));
            Assert.True(File.Exists(fixture.Marker));
            using var closedControl = fixture.OpenControlFile();
        }
        finally
        {
            StopChild(child);
        }
    }

    [Fact]
    public async Task MatrixCloseFailureNeverGrantsEvenAfterTheChildExits()
    {
        using var deadline = new CancellationTokenSource(Timeout);
        using var fixture = new Fixture(failMatrixClose: true);
        using Process child = StartWaitingChild();
        try
        {
            using (WindowsVerifiedBladeDeviceProvider owner = fixture.CreateOwner())
            {
                Assert.True(owner.TryGetVerifiedDevice(out IBladeDevice retained));
                Assert.True(await retained.ReadDriverModeAsync(deadline.Token));
                await Assert.ThrowsAsync<IOException>(() => owner.QuiesceForOwnerHandoffAsync(
                    child.SafeHandle, AdapterPowerState.FullPower280W, deadline.Token));
                await AssertFencedAsync(owner, retained, fixture, deadline.Token);
            }

            await ExitChildAsync(child, deadline.Token);
            using WindowsVerifiedBladeDeviceProvider restarted = fixture.CreateOwner();
            Assert.False(restarted.TryGetVerifiedDevice(out _));
            Assert.True(File.Exists(fixture.Marker));
        }
        finally
        {
            StopChild(child);
        }
    }

    private static async Task AssertFencedAsync(
        WindowsVerifiedBladeDeviceProvider owner, IBladeDevice retained, Fixture fixture,
        CancellationToken cancellationToken)
    {
        int controls = fixture.ControlOpens;
        int matrix = fixture.MatrixOpens;
        int probes = fixture.Probes;
        Assert.False(owner.TryGetVerifiedDevice(out _));
        Assert.False(owner.TryGetRecentlyVerifiedReadOnlyDevice(out _));
        Assert.False(owner.TryGetOwnedAmbientLightingDevice(out _));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            retained.ReadAdapterPowerStateAsync(cancellationToken));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            retained.ReadDriverModeAsync(cancellationToken));
        Assert.Equal(controls, fixture.ControlOpens);
        Assert.Equal(matrix, fixture.MatrixOpens);
        Assert.Equal(probes, fixture.Probes);
    }

    private static Process StartWaitingChild()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/q");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("set /p handoff=");
        return Process.Start(start) ?? throw new InvalidOperationException("The isolated child did not start.");
    }

    private static async Task ExitChildAsync(Process child, CancellationToken token)
    {
        await child.StandardInput.WriteLineAsync("done".AsMemory(), token);
        await child.StandardInput.FlushAsync(token);
        await child.WaitForExitAsync(token);
    }

    private static void StopChild(Process child)
    {
        if (!child.HasExited)
        {
            child.Kill();
            Assert.True(child.WaitForExit((int)Timeout.TotalMilliseconds));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("openblade-handoff-");
        private readonly bool holdMatrixClose;
        private readonly bool failMatrixClose;
        private int holdNextRead;

        internal Fixture(bool holdMatrixClose = false, bool failMatrixClose = false)
        {
            this.holdMatrixClose = holdMatrixClose;
            this.failMatrixClose = failMatrixClose;
        }

        internal string Marker => Path.Combine(directory.FullName, "pending");
        internal int Probes { get; private set; }
        internal int ControlOpens { get; private set; }
        internal int MatrixOpens { get; private set; }
        internal byte AdapterPowerLevel { get; set; } = 0x11;
        internal FileCommandSession? LightingControl { get; private set; }
        internal TaskCompletionSource ControlReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseControlRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MatrixCloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseMatrixClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void HoldNextControlRead() => Interlocked.Exchange(ref holdNextRead, 1);

        internal FileStream OpenControlFile() => OpenFile("control");
        internal FileStream OpenMatrixFile() => OpenFile("matrix");

        internal WindowsVerifiedBladeDeviceProvider CreateOwner()
        {
            var pending = new PendingDisposalRegistry();
            var battery = new ProductionBatteryFactory(
                DeviceCommandCatalog.Empty, ProductionBatteryReadEvidence.Production,
                () => new ValidationRunContext(), _ => new ActorFactory(OpenControlSession),
                TimeProvider.System, Timeout, Timeout, pending,
                sharedCommandGate: Rz090581FeatureCommandGate.Shared);
            var lighting = new ProductionLightingFactory(
                admitted: true, () => new ValidationRunContext(),
                _ => new ActorFactory(() => LightingControl = new FileCommandSession(
                    OpenFile("lighting-control"), (_, _) => Task.FromResult<byte[]>([0x03, 0x00]))),
                OpenMatrixWriter, TimeProvider.System, Timeout, Timeout, pending,
                sharedCommandGate: Rz090581FeatureCommandGate.Shared);
            return new WindowsVerifiedBladeDeviceProvider(
                () =>
                {
                    Probes++;
                    return VerifiedBladeProviderFixture.Probe(BatteryValidationProbeClassification.Ready);
                },
                battery, VerifiedBladeProviderFixture.UnavailableFanRpmFactory(), lighting,
                ownerHandoff: new ManagedOwnerHandoff(Marker, "isolated-build", "isolated-boot"));
        }

        private FileCommandSession OpenControlSession()
        {
            ControlOpens++;
            return new FileCommandSession(OpenControlFile(), async (command, token) =>
            {
                Assert.Equal((AdapterPowerProtocol.CommandClass, AdapterPowerProtocol.CommandId),
                    (command.ClassId, command.CommandId));
                if (Interlocked.Exchange(ref holdNextRead, 0) == 1)
                {
                    ControlReadStarted.TrySetResult();
                    await ReleaseControlRead.Task.WaitAsync(token);
                }
                return [AdapterPowerLevel, 0x11];
            });
        }

        private HidSessionResult<IPid02E0MatrixFeatureWriter> OpenMatrixWriter()
        {
            MatrixOpens++;
            var file = File.OpenHandle(Path.Combine(directory.FullName, "matrix"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous);
            // The transport is a fake. File.OpenHandle already prepares the
            // async file for managed I/O, so the native HID binding is simulated.
            var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
            var handle = new OpenedHidHandle(file, bound, () =>
            {
                MatrixCloseStarted.TrySetResult();
                if (holdMatrixClose)
                {
                    ReleaseMatrixClose.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
                }
                if (failMatrixClose)
                {
                    throw new IOException("Fake matrix closure could not be confirmed.");
                }
            }, file.Dispose);
            return new(new WindowsPid02E0MatrixFeatureWriter(
                handle, new UnusedMatrixIo(), new SafeGuard(), TimeProvider.System, Timeout,
                ControllerExclusivityPolicy.Strict, sharedCommandGate: Rz090581FeatureCommandGate.Shared), null);
        }

        private FileStream OpenFile(string name) => new(
            Path.Combine(directory.FullName, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        public void Dispose() => directory.Delete(recursive: true);
    }

    private sealed class ActorFactory(Func<FileCommandSession> createSession) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            FileCommandSession session = createSession();
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(
                new ValidationActor(session, new BatteryCommandClient(session, Timeout)), null));
        }
    }

    private sealed class FileCommandSession(
        FileStream file, Func<RazerCommandSpec, CancellationToken, Task<byte[]>> respond) : IRazerCommandSession
    {
        internal bool HasOpenHandle => file.CanRead;
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;

        public async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command, Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                return new(default, HidSessionFailureKind.ConflictBlocked);
            }
            return new(await respond(command, cancellationToken), null);
        }

        public ValueTask DisposeAsync() => file.DisposeAsync();
    }

    private sealed class SafeGuard : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => new([], null);
    }

    private sealed class UnusedMatrixIo : IHidFeatureIo
    {
        public Task<HidFeatureIoResult> ExecuteAsync(
            OpenedHidHandle handle, uint controlCode, byte[] buffer,
            CancellationToken callerCancellation, CancellationToken deadlineCancellation) =>
            throw new InvalidOperationException("Handing off must not submit a matrix report.");
    }
}
