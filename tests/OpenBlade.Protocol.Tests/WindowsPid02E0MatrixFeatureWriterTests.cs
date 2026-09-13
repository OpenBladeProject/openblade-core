using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsPid02E0MatrixFeatureWriterTests
{
    private static readonly HostIdentityReadResult ExactHost = new(
        new HostIdentityEvidence(
            "Razer",
            "Blade 16 - RZ09-0581",
            "RZ09-05819EN4",
            "3.01"),
        null,
        HostFirmwareCompatibility.Tested);

    private static readonly HidHandleIdentity ExactMatrix = new(
        0x1532,
        0x02E0,
        0x000C,
        0x0001,
        375);

    [Fact]
    public async Task Opener_SelectsOnlySoleExactMatrixInterfaceAndOwnsBoundHandle()
    {
        var native = new ScriptedCandidateNative();
        OpenedCandidateHandle command = native.Add(
            "mi02",
            ExactMatrix with { UsagePage = 1, Usage = 2, FeatureReportByteLength = 91 });
        OpenedCandidateHandle matrix = native.Add("mi03", ExactMatrix);
        var opener = CreateOpener(native);

        HidSessionResult<OpenedHidHandle> result = opener.Open();

        Assert.Null(result.Failure);
        Assert.NotNull(result.Value);
        Assert.Equal(["mi02", "mi03"], native.ReadPaths);
        Assert.Equal("mi03", native.BoundPath);
        Assert.True(command.FileHandle.IsClosed);
        Assert.False(matrix.FileHandle.IsClosed);
        Assert.All(native.OpenRequests, request => Assert.Equal(
            WindowsPid02E0MatrixFeatureOpener.OperationalOpenParameters,
            request.Parameters));

        await result.Value.DisposeAsync();
        Assert.True(matrix.FileHandle.IsClosed);
        Assert.Equal(1, native.BoundDisposeCount);
    }

    [Theory]
    [InlineData("3.01", true)]
    [InlineData("4.00", true)]
    [InlineData("4.01", true)]
    [InlineData("4.02", false)]
    public async Task Opener_RequiresSupportedFirmwareWhenIdentityIsMarkedTested(
        string biosVersion,
        bool expectedToOpen)
    {
        var native = new ScriptedCandidateNative();
        OpenedCandidateHandle candidate = native.Add("mi03", ExactMatrix);
        var opener = new WindowsPid02E0MatrixFeatureOpener(
            new ScriptedHost(ExactHost with
            {
                Evidence = ExactHost.Evidence!.Value with { BiosVersion = biosVersion },
            }),
            new ScriptedGuard(),
            native,
            ControllerExclusivityPolicy.Strict);

        HidSessionResult<OpenedHidHandle> result = opener.Open();

        if (expectedToOpen)
        {
            Assert.Null(result.Failure);
            Assert.NotNull(result.Value);
            await result.Value.DisposeAsync();
            Assert.True(candidate.FileHandle.IsClosed);
        }
        else
        {
            Assert.Equal(HidSessionFailureKind.IdentityBlocked, result.Failure);
            Assert.Null(result.Value);
            Assert.Equal(0, native.EnumerateCalls);
            Assert.False(candidate.FileHandle.IsClosed);
            candidate.Dispose();
            Assert.True(candidate.FileHandle.IsClosed);
        }
    }

    [Fact]
    public async Task Opener_AcceptsUntestedFirmwareOnlyWhenExplicitlyAllowed()
    {
        var allowedNative = new ScriptedCandidateNative();
        OpenedCandidateHandle allowedCandidate = allowedNative.Add("mi03", ExactMatrix);
        var allowed = new WindowsPid02E0MatrixFeatureOpener(
            new ScriptedHost(ExactHost with
            {
                Evidence = ExactHost.Evidence!.Value with { BiosVersion = "4.02" },
                FirmwareCompatibility = HostFirmwareCompatibility.UntestedAllowed,
            }),
            new ScriptedGuard(),
            allowedNative,
            ControllerExclusivityPolicy.Strict);

        HidSessionResult<OpenedHidHandle> allowedResult = allowed.Open();

        Assert.Null(allowedResult.Failure);
        Assert.NotNull(allowedResult.Value);
        await allowedResult.Value.DisposeAsync();
        Assert.True(allowedCandidate.FileHandle.IsClosed);

        var blockedNative = new ScriptedCandidateNative();
        OpenedCandidateHandle blockedCandidate = blockedNative.Add("mi03", ExactMatrix);
        var blocked = new WindowsPid02E0MatrixFeatureOpener(
            new ScriptedHost(ExactHost with
            {
                Evidence = ExactHost.Evidence!.Value with { BiosVersion = "4.02" },
                FirmwareCompatibility = HostFirmwareCompatibility.UntestedBlocked,
            }),
            new ScriptedGuard(),
            blockedNative,
            ControllerExclusivityPolicy.Strict);

        HidSessionResult<OpenedHidHandle> blockedResult = blocked.Open();

        Assert.Equal(HidSessionFailureKind.IdentityBlocked, blockedResult.Failure);
        Assert.Null(blockedResult.Value);
        Assert.Equal(0, blockedNative.EnumerateCalls);
        Assert.False(blockedCandidate.FileHandle.IsClosed);
        blockedCandidate.Dispose();
        Assert.True(blockedCandidate.FileHandle.IsClosed);
    }

    [Fact]
    public async Task Opener_AcceptsModelScopedFirmwareWithoutOverride()
    {
        var native = new ScriptedCandidateNative();
        OpenedCandidateHandle candidate = native.Add("mi03", ExactMatrix);
        var opener = new WindowsPid02E0MatrixFeatureOpener(
            new ScriptedHost(ExactHost with
            {
                Evidence = ExactHost.Evidence!.Value with { BiosVersion = "99.99" },
                FirmwareCompatibility = HostFirmwareCompatibility.ModelScoped,
            }),
            new ScriptedGuard(),
            native,
            ControllerExclusivityPolicy.Strict);

        HidSessionResult<OpenedHidHandle> result = opener.Open();

        Assert.Null(result.Failure);
        Assert.NotNull(result.Value);
        await result.Value.DisposeAsync();
        Assert.True(candidate.FileHandle.IsClosed);
    }

    [Fact]
    public void Opener_RejectsWrongModelBeforeGuardOrNativeCalls()
    {
        var native = new ScriptedCandidateNative();
        native.Add("mi03", ExactMatrix);
        var guard = new ScriptedGuard();
        var opener = new WindowsPid02E0MatrixFeatureOpener(
            new ScriptedHost(ExactHost with
            {
                Evidence = ExactHost.Evidence!.Value with
                {
                    ProductName = "Blade 16 - RZ09-9999",
                },
            }),
            guard,
            native,
            ControllerExclusivityPolicy.Strict);

        HidSessionResult<OpenedHidHandle> result = opener.Open();

        Assert.Equal(HidSessionFailureKind.IdentityBlocked, result.Failure);
        Assert.Null(result.Value);
        Assert.Equal(0, guard.Calls);
        Assert.Equal(0, native.EnumerateCalls);
    }

    [Theory]
    [InlineData(0x1234, 0x02E0, 0x000C, 0x0001, 375)]
    [InlineData(0x1532, 0x029F, 0x000C, 0x0001, 375)]
    [InlineData(0x1532, 0x02E0, 0x0001, 0x0001, 375)]
    [InlineData(0x1532, 0x02E0, 0x000C, 0x0002, 375)]
    [InlineData(0x1532, 0x02E0, 0x000C, 0x0001, 374)]
    public void Opener_RejectsEveryInexactMatrixDescriptor(
        ushort vendor,
        ushort product,
        ushort usagePage,
        ushort usage,
        ushort length)
    {
        var native = new ScriptedCandidateNative();
        OpenedCandidateHandle candidate = native.Add(
            "candidate",
            new HidHandleIdentity(vendor, product, usagePage, usage, length));

        HidSessionResult<OpenedHidHandle> result = CreateOpener(native).Open();

        Assert.Equal(HidSessionFailureKind.IdentityBlocked, result.Failure);
        Assert.Null(result.Value);
        Assert.Null(native.BoundPath);
        Assert.True(candidate.FileHandle.IsClosed);
    }

    [Fact]
    public void Opener_MapsAccessDeniedExactInterfaceToNativeFailureWithoutBindingMetadataHandle()
    {
        var native = new ScriptedCandidateNative { AccessDeniedPath = "mi03" };
        OpenedCandidateHandle candidate = native.Add("mi03", ExactMatrix);

        HidSessionResult<OpenedHidHandle> result = CreateOpener(native).Open();

        Assert.Equal(HidSessionFailureKind.NativeIoFailure, result.Failure);
        Assert.Null(result.Value);
        Assert.Null(native.BoundPath);
        Assert.Equal([0xC0000000u, 0u],
            native.OpenRequests.Select(request => request.Parameters.DesiredAccess));
        Assert.True(candidate.FileHandle.IsClosed);
    }

    [Fact]
    public void Opener_RejectsAmbiguousExactInterfacesAndDisposesBoth()
    {
        var native = new ScriptedCandidateNative();
        OpenedCandidateHandle first = native.Add("first", ExactMatrix);
        OpenedCandidateHandle second = native.Add("second", ExactMatrix);

        HidSessionResult<OpenedHidHandle> result = CreateOpener(native).Open();

        Assert.Equal(HidSessionFailureKind.IdentityBlocked, result.Failure);
        Assert.True(first.FileHandle.IsClosed);
        Assert.True(second.FileHandle.IsClosed);
        Assert.Null(native.BoundPath);
    }

    [Fact]
    public async Task Writer_UsesSetFeatureOnlyWithExactEncoded375ByteSnapshot()
    {
        var io = new ScriptedIo();
        var owned = CreateOwnedHandle();
        await using var writer = CreateWriter(owned.Handle, io);
        Pid02E0MatrixReport report = Report(transaction: 0x29, color: new MatrixRgb(1, 2, 3));

        HidSessionResult<HidUnit> result = await writer.WriteAsync(report, CancellationToken.None);

        Assert.Null(result.Failure);
        IoCall call = Assert.Single(io.Calls);
        Assert.Equal(0x000B0191u, call.ControlCode);
        Assert.Equal(375, call.Buffer.Length);
        Assert.Equal(0, call.Buffer[0]);
        Assert.Equal(
            Pid02E0MatrixReportCodec.EncodeFeatureReportBuffer(report),
            call.Buffer);
    }

    [Fact]
    public async Task Writer_SerializesConcurrentWritesAndPreservesEachBuffer()
    {
        var firstGate = new TaskCompletionSource<HidFeatureIoResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new ScriptedIo { FirstResult = firstGate.Task };
        var owned = CreateOwnedHandle();
        await using var writer = CreateWriter(owned.Handle, io);

        Task<HidSessionResult<HidUnit>> first = writer.WriteAsync(
            Report(1, new MatrixRgb(1, 0, 0)),
            CancellationToken.None);
        await io.FirstCallObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<HidSessionResult<HidUnit>> second = writer.WriteAsync(
            Report(2, new MatrixRgb(0, 1, 0)),
            CancellationToken.None);

        Assert.Single(io.Calls);
        Assert.False(second.IsCompleted);
        firstGate.SetResult(Success());

        Assert.Null((await first).Failure);
        Assert.Null((await second).Failure);
        Assert.Equal(2, io.Calls.Count);
        Assert.Equal(1, Pid02E0MatrixReportCodec.DecodeFeatureReportBuffer(io.Calls[0].Buffer).TransactionId);
        Assert.Equal(2, Pid02E0MatrixReportCodec.DecodeFeatureReportBuffer(io.Calls[1].Buffer).TransactionId);
        Assert.Equal(1, io.MaximumConcurrentCalls);
    }

    [Fact]
    public async Task Writer_QueuedCancellationDoesNotReachNativeIo()
    {
        var firstGate = new TaskCompletionSource<HidFeatureIoResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new ScriptedIo { FirstResult = firstGate.Task };
        var owned = CreateOwnedHandle();
        await using var writer = CreateWriter(owned.Handle, io);
        Task<HidSessionResult<HidUnit>> first = writer.WriteAsync(
            Report(1, new MatrixRgb(1, 1, 1)),
            CancellationToken.None);
        await io.FirstCallObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        Task<HidSessionResult<HidUnit>> canceled = writer.WriteAsync(
            Report(2, new MatrixRgb(2, 2, 2)),
            cancellation.Token);

        cancellation.Cancel();
        Assert.Equal(HidSessionFailureKind.QueuedCanceled, (await canceled).Failure);
        firstGate.SetResult(Success());
        Assert.Null((await first).Failure);
        Assert.Single(io.Calls);
    }

    [Fact]
    public async Task Writer_SubmittedTimeoutPoisonsSessionAndMapsDiagnostic()
    {
        var io = new ScriptedIo
        {
            DefaultResult = new HidFeatureIoResult(
                new NativeIoCompletion(false, 0, 995),
                true,
                HidFeatureTermination.DeadlineExpired),
        };
        var owned = CreateOwnedHandle();
        await using var writer = CreateWriter(owned.Handle, io);

        HidSessionResult<HidUnit> result = await writer.WriteAsync(
            Report(1, MatrixRgb.Black),
            CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(
            () => writer.Disposition == RazerCommandSessionDisposition.FullyDisposed,
            TimeSpan.FromSeconds(2)));
        HidSessionResult<HidUnit> after = await writer.WriteAsync(
            Report(2, MatrixRgb.Black),
            CancellationToken.None);

        Assert.Equal(HidSessionFailureKind.Timeout, result.Failure);
        Assert.Equal(HidSessionFailurePhase.SetCompletion, result.Diagnostic?.Phase);
        Assert.Equal(995, result.Diagnostic?.NativeWin32Error);
        Assert.Equal(HidSessionFailureKind.Disposed, after.Failure);
        Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, writer.Disposition);
        Assert.Single(io.Calls);
    }

    [Fact]
    public async Task Writer_FinalizationFailurePreservesOriginalNativeError()
    {
        var nativeResult = new HidFeatureIoResult(
            new NativeIoCompletion(false, 0, 87),
            true,
            HidFeatureTermination.Completed);
        var io = new ScriptedIo
        {
            FirstResult = Task.FromException<HidFeatureIoResult>(
                new NativeOperationFinalizationException(
                    nativeResult,
                    new ArgumentException("cleanup"))),
        };
        var owned = CreateOwnedHandle();
        await using var writer = CreateWriter(owned.Handle, io);

        HidSessionResult<HidUnit> result = await writer.WriteAsync(
            Report(1, MatrixRgb.Black),
            CancellationToken.None);

        Assert.Equal(HidSessionFailureKind.NativeIoFailure, result.Failure);
        Assert.Equal(HidSessionFailurePhase.SetCompletion, result.Diagnostic?.Phase);
        Assert.Equal(87, result.Diagnostic?.NativeWin32Error);
        Assert.Equal(0, result.Diagnostic?.TransferredByteCount);
    }

    [Fact]
    public async Task Writer_StrictConflictStopsBeforeIoButAdvisoryDoesNot()
    {
        var strictIo = new ScriptedIo();
        var strictOwned = CreateOwnedHandle();
        await using (var strict = CreateWriter(
            strictOwned.Handle,
            strictIo,
            new ScriptedGuard(new HardwareAccessDecision(["Razer Synapse"], null)),
            ControllerExclusivityPolicy.Strict))
        {
            HidSessionResult<HidUnit> result = await strict.WriteAsync(
                Report(1, MatrixRgb.Black),
                CancellationToken.None);
            Assert.Equal(HidSessionFailureKind.ConflictBlocked, result.Failure);
            Assert.Empty(strictIo.Calls);
        }

        var advisoryIo = new ScriptedIo();
        var advisoryOwned = CreateOwnedHandle();
        var advisoryGuard = new ScriptedGuard(
            new HardwareAccessDecision(["Razer Synapse"], null));
        await using (var advisory = CreateWriter(
            advisoryOwned.Handle,
            advisoryIo,
            advisoryGuard,
            ControllerExclusivityPolicy.Advisory))
        {
            Assert.Null((await advisory.WriteAsync(
                Report(1, MatrixRgb.Black),
                CancellationToken.None)).Failure);
            Assert.Equal(0, advisoryGuard.Calls);
            Assert.Single(advisoryIo.Calls);
        }
    }

    [Fact]
    public async Task Writer_DisposeDrainsQueueAndDisposesOwnedHandleExactlyOnce()
    {
        var io = new ScriptedIo();
        var owned = CreateOwnedHandle();
        var writer = CreateWriter(owned.Handle, io);
        Assert.Null((await writer.WriteAsync(
            Report(1, MatrixRgb.Black),
            CancellationToken.None)).Failure);

        await writer.DisposeAsync();
        await writer.DisposeAsync();

        Assert.Equal(1, owned.DisposeCount());
        Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, writer.Disposition);
        Assert.Equal(HidSessionFailureKind.Disposed, (await writer.WriteAsync(
            Report(2, MatrixRgb.Black),
            CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task Writer_RecordsOptInQueueGuardAndIoPhaseTiming()
    {
        var time = new ManualTimeProvider();
        var sink = new TimingSink();
        var guard = new ScriptedGuard(
            new HardwareAccessDecision([], null),
            () => time.Advance(TimeSpan.FromMilliseconds(2)));
        var io = new ScriptedIo
        {
            DefaultResult = new HidFeatureIoResult(
                new NativeIoCompletion(true, 0, 0),
                true,
                HidFeatureTermination.Completed,
                new NativeIoTiming(
                    TimeSpan.FromMilliseconds(3),
                    TimeSpan.FromMilliseconds(4))),
            OnExecute = () => time.Advance(TimeSpan.FromMilliseconds(7)),
        };
        var owned = CreateOwnedHandle();
        await using var writer = CreateWriter(
            owned.Handle,
            io,
            guard,
            timeProvider: time,
            timingSink: sink);

        HidSessionResult<HidUnit> result = await writer.WriteAsync(
            Report(1, MatrixRgb.Black),
            CancellationToken.None);

        Assert.Null(result.Failure);
        MatrixWritePhaseTiming timing = Assert.Single(sink.Timings);
        Assert.Equal(TimeSpan.Zero, timing.QueueWait);
        Assert.Equal(TimeSpan.FromMilliseconds(2), timing.ControllerGuard);
        Assert.Equal(TimeSpan.FromMilliseconds(3), timing.NativeSubmitToCompletion);
        Assert.Equal(TimeSpan.FromMilliseconds(4), timing.ManagedCompletion);
    }

    private static WindowsPid02E0MatrixFeatureOpener CreateOpener(
        ScriptedCandidateNative native) =>
        new(
            new ScriptedHost(ExactHost),
            new ScriptedGuard(),
            native,
            ControllerExclusivityPolicy.Strict);

    private static WindowsPid02E0MatrixFeatureWriter CreateWriter(
        OpenedHidHandle handle,
        ScriptedIo io,
        ScriptedGuard? guard = null,
        ControllerExclusivityPolicy policy = ControllerExclusivityPolicy.Strict,
        TimeProvider? timeProvider = null,
        IMatrixWriteTimingSink? timingSink = null) =>
        new(
            handle,
            io,
            guard ?? new ScriptedGuard(),
            timeProvider ?? TimeProvider.System,
            TimeSpan.FromSeconds(3),
            policy,
            timingSink);

    private static Pid02E0MatrixReport Report(byte transaction, MatrixRgb color) =>
        new(transaction, Pid02E0MatrixRenderers.RenderStatic(color));

    private static HidFeatureIoResult Success() =>
        new(new NativeIoCompletion(true, 0, 0), true, HidFeatureTermination.Completed);

    private static (OpenedHidHandle Handle, Func<int> DisposeCount) CreateOwnedHandle()
    {
        int disposeCount = 0;
        var file = new SafeFileHandle(new IntPtr(900), ownsHandle: false);
        var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(
            typeof(ThreadPoolBoundHandle));
        return (
            new OpenedHidHandle(
                file,
                bound,
                () => { },
                () =>
                {
                    disposeCount++;
                    file.Dispose();
                }),
            () => disposeCount);
    }

    private sealed class ScriptedHost(HostIdentityReadResult result) : IHostIdentitySource
    {
        public HostIdentityReadResult ReadExactIdentity() => result;
    }

    private sealed class ScriptedGuard : IHardwareAccessGuard
    {
        private readonly HardwareAccessDecision decision;
        private readonly Action? onCheck;

        internal ScriptedGuard()
            : this(new HardwareAccessDecision([], null))
        {
        }

        internal ScriptedGuard(
            HardwareAccessDecision decision,
            Action? onCheck = null)
        {
            this.decision = decision;
            this.onCheck = onCheck;
        }

        internal int Calls { get; private set; }

        public HardwareAccessDecision Check()
        {
            Calls++;
            onCheck?.Invoke();
            return decision;
        }
    }

    private sealed class ScriptedIo : IHidFeatureIo
    {
        private int concurrentCalls;

        internal List<IoCall> Calls { get; } = [];

        internal TaskCompletionSource FirstCallObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<HidFeatureIoResult>? FirstResult { get; init; }

        internal HidFeatureIoResult DefaultResult { get; init; } = Success();

        internal Action? OnExecute { get; init; }

        internal int MaximumConcurrentCalls { get; private set; }

        public async Task<HidFeatureIoResult> ExecuteAsync(
            OpenedHidHandle handle,
            uint controlCode,
            byte[] buffer,
            CancellationToken callerCancellation,
            CancellationToken deadlineCancellation)
        {
            int concurrent = Interlocked.Increment(ref concurrentCalls);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, concurrent);
            int index;
            lock (Calls)
            {
                index = Calls.Count;
                Calls.Add(new IoCall(controlCode, (byte[])buffer.Clone()));
            }

            FirstCallObserved.TrySetResult();
            try
            {
                OnExecute?.Invoke();
                return index == 0 && FirstResult is not null
                    ? await FirstResult.ConfigureAwait(false)
                    : DefaultResult;
            }
            finally
            {
                Interlocked.Decrement(ref concurrentCalls);
            }
        }
    }

    private sealed class TimingSink : IMatrixWriteTimingSink
    {
        internal List<MatrixWritePhaseTiming> Timings { get; } = [];

        public void Record(MatrixWritePhaseTiming timing) => Timings.Add(timing);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }

    private readonly record struct IoCall(uint ControlCode, byte[] Buffer);

    private sealed class ScriptedCandidateNative : IHidCandidateNative
    {
        private readonly Dictionary<string, (OpenedCandidateHandle Handle, HidHandleIdentity Identity)>
            candidates = [];

        internal string? AccessDeniedPath { get; init; }

        internal int EnumerateCalls { get; private set; }

        internal List<(string Path, HidOpenParameters Parameters)> OpenRequests { get; } = [];

        internal List<string> ReadPaths { get; } = [];

        internal string? BoundPath { get; private set; }

        internal int BoundDisposeCount { get; private set; }

        internal OpenedCandidateHandle Add(string path, HidHandleIdentity identity)
        {
            var file = new SafeFileHandle(new IntPtr(1000 + candidates.Count), ownsHandle: false);
            var handle = new OpenedCandidateHandle(file, file.Dispose);
            candidates.Add(path, (handle, identity));
            return handle;
        }

        public IReadOnlyList<string> EnumerateDevicePaths()
        {
            EnumerateCalls++;
            return candidates.Keys.ToArray();
        }

        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters)
        {
            OpenRequests.Add((path, parameters));
            if (path == AccessDeniedPath && parameters.DesiredAccess != 0)
            {
                return new(null, 5);
            }

            return new(candidates[path].Handle, null);
        }

        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle)
        {
            KeyValuePair<string, (OpenedCandidateHandle Handle, HidHandleIdentity Identity)> entry =
                candidates.Single(pair => ReferenceEquals(pair.Value.Handle, handle));
            ReadPaths.Add(entry.Key);
            return entry.Value.Identity;
        }

        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle)
        {
            KeyValuePair<string, (OpenedCandidateHandle Handle, HidHandleIdentity Identity)> entry =
                candidates.Single(pair => ReferenceEquals(pair.Value.Handle, handle));
            BoundPath = entry.Key;
            SafeFileHandle file = handle.TransferOwnership();
            var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(
                typeof(ThreadPoolBoundHandle));
            return new OpenedHidHandle(
                file,
                bound,
                () => { },
                () =>
                {
                    BoundDisposeCount++;
                    file.Dispose();
                });
        }
    }
}

public sealed class WindowsPid02E0ExtendedFeatureNativeTests
{
    [Fact]
    public void NativeSubmit_AllowsOnlySetFeatureForExact375ByteBuffer()
    {
        var kernel = new ScriptedKernel();
        var native = new WindowsHidNative(kernel);
        byte[] buffer = new byte[375];
        using var token = new NativeOperationToken(buffer, _ => { });

        NativeSubmissionResult result = native.Submit(
            CreateHandle(),
            0x000B0191u,
            buffer,
            token,
            _ => { });

        Assert.Equal(NativeSubmissionKind.Submitted, result.Kind);
        KernelCall call = Assert.Single(kernel.Calls);
        Assert.Equal(0x000B0191u, call.ControlCode);
        Assert.Equal(token.PinnedAddress, call.Input);
        Assert.Equal(375u, call.InputLength);
        Assert.Equal(IntPtr.Zero, call.Output);
        Assert.Equal(0u, call.OutputLength);
    }

    [Theory]
    [InlineData(374, 0x000B0191u)]
    [InlineData(376, 0x000B0191u)]
    [InlineData(375, 0x000B0192u)]
    public void NativeSubmit_RejectsWrongExtendedLengthAndAllExtendedGetFeature(
        int length,
        uint controlCode)
    {
        var kernel = new ScriptedKernel();
        var native = new WindowsHidNative(kernel);
        byte[] buffer = new byte[length];
        using var token = new NativeOperationToken(buffer, _ => { });

        NativeSubmissionResult result = native.Submit(
            CreateHandle(),
            controlCode,
            buffer,
            token,
            _ => { });

        Assert.Equal(NativeSubmissionKind.ImmediateFailure, result.Kind);
        Assert.Equal(87, result.ErrorCode);
        Assert.Equal(0, kernel.AllocateCalls);
        Assert.Empty(kernel.Calls);
    }

    [Fact]
    public async Task OverlappedOperation_EnforcesConfigured375ByteBoundary()
    {
        var native = new ImmediateNative();
        var operation = new OverlappedFeatureOperation(native, 375);

        await Assert.ThrowsAsync<ArgumentException>(() => operation.ExecuteAsync(
            CreateHandle(),
            0x000B0191u,
            new byte[91],
            CancellationToken.None,
            CancellationToken.None));

        HidFeatureIoResult result = await operation.ExecuteAsync(
            CreateHandle(),
            0x000B0191u,
            new byte[375],
            CancellationToken.None,
            CancellationToken.None);

        Assert.Equal(HidFeatureTermination.Completed, result.Termination);
        Assert.True(result.WasSubmitted);
        Assert.Equal(375, native.BufferLength);
    }

    private static OpenedHidHandle CreateHandle()
    {
        var file = new SafeFileHandle(new IntPtr(1234), ownsHandle: false);
        var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(
            typeof(ThreadPoolBoundHandle));
        return new OpenedHidHandle(file, bound, () => { }, file.Dispose);
    }

    private readonly record struct KernelCall(
        uint ControlCode,
        IntPtr Input,
        uint InputLength,
        IntPtr Output,
        uint OutputLength);

    private sealed class ScriptedKernel : IWindowsHidKernel
    {
        private readonly IntPtr overlapped = new(5678);

        internal int AllocateCalls { get; private set; }

        internal List<KernelCall> Calls { get; } = [];

        public IntPtr AllocateNativeOverlapped(
            ThreadPoolBoundHandle boundHandle,
            NativeCompletionContext completionContext,
            byte[] pinData)
        {
            AllocateCalls++;
            return overlapped;
        }

        public void FreeNativeOverlapped(
            ThreadPoolBoundHandle boundHandle,
            IntPtr nativeOverlapped)
        {
            Assert.Equal(overlapped, nativeOverlapped);
        }

        public NativeCallResult DeviceIoControl(
            SafeFileHandle fileHandle,
            uint controlCode,
            IntPtr input,
            uint inputLength,
            IntPtr output,
            uint outputLength,
            IntPtr nativeOverlapped)
        {
            Assert.Equal(overlapped, nativeOverlapped);
            Calls.Add(new KernelCall(controlCode, input, inputLength, output, outputLength));
            return new NativeCallResult(true, 0);
        }

        public NativeCallResult CancelIoEx(
            SafeFileHandle fileHandle,
            IntPtr nativeOverlapped) =>
            new(true, 0);
    }

    private sealed class ImmediateNative : IOverlappedFeatureNative
    {
        internal int BufferLength { get; private set; }

        public NativeSubmissionResult Submit(
            OpenedHidHandle handle,
            uint controlCode,
            byte[] buffer,
            NativeOperationToken operation,
            Action<NativeIoCompletion> completionCallback)
        {
            BufferLength = buffer.Length;
            completionCallback(new NativeIoCompletion(true, 0, 0));
            return new NativeSubmissionResult(NativeSubmissionKind.Submitted, 0);
        }

        public NativeCancelResult Cancel(
            OpenedHidHandle handle,
            NativeOperationToken operation) =>
            NativeCancelResult.NotFound;
    }
}
