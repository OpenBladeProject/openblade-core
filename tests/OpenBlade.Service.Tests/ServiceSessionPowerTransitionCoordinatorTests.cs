using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceSessionPowerTransitionCoordinatorTests
{
    private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SuspendExpiredBehindActiveRunner_StillReachesWriteFreePreparation()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime { BlockPrepare = true, IgnorePrepareCancellation = true };
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        ReportSessionPowerTransitionRequest first = Request(time, true);
        Task<ControlOperationResult> active = Report(coordinator, first);
        await runtime.PrepareStarted.Task.WaitAsync(DeadlockGuard);
        time.Advance(1);
        Task<ControlOperationResult> supersededResume = Report(coordinator, first with
        {
            Sequence = 2,
            TimestampQpc = time.GetTimestamp(),
            Suspending = false,
        });
        time.Advance(1);
        Task<ControlOperationResult> pendingSuspend = Report(coordinator, first with
        {
            Sequence = 3,
            TimestampQpc = time.GetTimestamp(),
        });
        time.Advance(SessionPowerTransition.MaximumAgeMilliseconds + 1);
        runtime.PrepareRelease.TrySetResult();

        await Task.WhenAll(active, supersededResume, pendingSuspend).WaitAsync(DeadlockGuard);

        Assert.True(gate.IsActive);
        Assert.Equal(2, runtime.PrepareCalls);
        Assert.Equal(0, runtime.PreparedWrites);
        Assert.Equal(0, runtime.RestoreCalls);
        Assert.Equal(1, runtime.MaximumConcurrentOperations);
    }

    [Fact]
    public async Task Handler_AdmitsRelayBeforeUserActivation()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime();
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        var handler = new ControlRequestHandler(runtime, new RejectArchive(),
            NullServiceShutdownSignal.Instance, new Authority(), coordinator);

        ServiceResponse response = await handler.HandleAsync(Request(time, true),
            ServiceClientContext.TrustedTestClient, default);

        Assert.True(response.Success);
        Assert.Equal(ControlOperationStatus.Applied, response.Result?.Status);
        Assert.True(gate.IsActive);
        Assert.Equal(0, runtime.ActivationCalls);
        Assert.Equal(1, runtime.PrepareCalls);
        Assert.Equal(0, runtime.GenericInvalidations);
    }

    [Theory]
    [InlineData(ServiceClientLogonKind.RemoteInteractive, 1)]
    [InlineData(ServiceClientLogonKind.Interactive, 2)]
    [InlineData(ServiceClientLogonKind.LocalSystem, 0)]
    public void AccessPolicy_RequiresActiveLocalInteractiveCaller(
        ServiceClientLogonKind logonKind, int sessionId)
    {
        ServiceClientContext caller = ServiceClientContext.TrustedTestClient with
        {
            LogonKind = logonKind,
            SessionId = sessionId,
        };

        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            Request(new Clock(), true), caller, new Authority()));
    }

    [Fact]
    public async Task NativeAndRelayedDuplicates_ProcessEachBoundaryOnce()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime();
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        ReportSessionPowerTransitionRequest suspend = Request(time, true);
        gate.Enter();
        await coordinator.ProcessNativeAsync(true, default);
        await Report(coordinator, suspend);

        time.Advance(1);
        gate.Exit();
        await coordinator.ProcessNativeAsync(false, default);
        await Report(coordinator, suspend with
        {
            RequestId = Guid.NewGuid(),
            Sequence = 2,
            TimestampQpc = time.GetTimestamp(),
            Suspending = false,
        });

        Assert.Equal(1, runtime.PrepareCalls);
        Assert.Equal(1, runtime.NoWriteInvalidations);
        Assert.Equal(1, runtime.RestoreCalls);
        Assert.Equal(0, runtime.GenericInvalidations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RelayedResume_AuthorityLossDuringClose_RequiresNativeCorroboration(
        bool nativeCorroboration, bool replaceInstance)
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime { BlockNoWriteInvalidation = true };
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        var backend = new Backend();
        using var source = new WindowsServiceLifecycleEventSource(backend, gate);
        bool authorized = true;
        gate.Enter();
        time.Advance(1);
        ReportSessionPowerTransitionRequest first = Request(time, false);
        Task<ControlOperationResult> relayed = coordinator.ReportAsync(first,
            ServiceClientContext.TrustedTestClient, () => authorized, default);
        await runtime.NoWriteInvalidationStarted.Task.WaitAsync(DeadlockGuard);

        Task<ControlOperationResult>? native = null;
        if (nativeCorroboration)
        {
            time.Advance(1);
            backend.Publish!(ServiceLifecycleTrigger.Resumed);
            native = coordinator.ProcessNativeAsync(false, default);
        }
        Task<ControlOperationResult>? replacement = null;
        if (replaceInstance)
        {
            time.Advance(1);
            replacement = Report(coordinator, first with
            {
                RequestId = Guid.NewGuid(),
                InstanceId = Guid.NewGuid(),
                InstanceStartedQpc = time.GetTimestamp(),
                TimestampQpc = time.GetTimestamp(),
            });
        }
        else
        {
            authorized = false;
        }
        runtime.NoWriteInvalidationRelease.TrySetResult();

        await relayed.WaitAsync(DeadlockGuard);
        if (native is not null)
        {
            await native.WaitAsync(DeadlockGuard);
        }
        if (replacement is not null)
        {
            await replacement.WaitAsync(DeadlockGuard);
        }
        Assert.Equal(1, runtime.NoWriteInvalidations);
        Assert.Equal(nativeCorroboration ? 1 : 0, runtime.BeginResumeCalls);
        Assert.Equal(nativeCorroboration ? 1 : 0, runtime.RestoreCalls);
        Assert.Equal(nativeCorroboration ? 1 : 0, runtime.CompleteResumeCalls);
        Assert.Equal(0, runtime.GenericInvalidations);
    }

    [Fact]
    public async Task PendingResume_CoalescesLateNoticesAndRetainsNativeCorroboration()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime { BlockPrepare = true, IgnorePrepareCancellation = true };
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        var backend = new Backend();
        using var source = new WindowsServiceLifecycleEventSource(backend, gate);
        ReportSessionPowerTransitionRequest first = Request(time, true);
        Task<ControlOperationResult> preparing = Report(coordinator, first);
        await runtime.PrepareStarted.Task.WaitAsync(DeadlockGuard);
        time.Advance(1);
        Task<ControlOperationResult> resuming = Report(coordinator, first with
        {
            Sequence = 2,
            TimestampQpc = time.GetTimestamp(),
            Suspending = false,
        });
        long generation = gate.Generation;

        time.Advance(2001);
        backend.Publish!(ServiceLifecycleTrigger.Resumed);
        Task<ControlOperationResult> native = coordinator.ProcessNativeAsync(false, default);
        Assert.Equal(generation, gate.Generation);
        time.Advance(1);
        Task<ControlOperationResult> replacement = Report(coordinator, first with
        {
            RequestId = Guid.NewGuid(),
            InstanceId = Guid.NewGuid(),
            InstanceStartedQpc = time.GetTimestamp(),
            TimestampQpc = time.GetTimestamp(),
            Suspending = false,
        });
        Assert.Equal(generation, gate.Generation);
        runtime.PrepareRelease.TrySetResult();
        await Task.WhenAll(preparing, resuming, native, replacement).WaitAsync(DeadlockGuard);

        Assert.Equal(0, runtime.PreparedWrites);
        Assert.Equal(1, runtime.NoWriteInvalidations);
        Assert.Equal(1, runtime.RestoreCalls);
        Assert.Equal(1, runtime.CompleteResumeCalls);
        Assert.Equal(1, runtime.MaximumConcurrentOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateNativeResume_DoesNotCancelAnOngoingRestore(bool displayOnFirst)
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime { BlockRestore = true };
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        var backend = new Backend();
        using var source = new WindowsServiceLifecycleEventSource(backend, gate,
            getTickCount: time.GetTimestamp);
        var loop = new ServiceLifecycleRefreshLoop(runtime, source, gate,
            NullLogger<ServiceLifecycleRefreshLoop>.Instance, coordinator);
        using var stopping = new CancellationTokenSource(DeadlockGuard);
        Task listening = loop.RunAsync(stopping.Token);
        Task<ControlOperationResult>? relayed = null;
        try
        {
            gate.Enter();
            time.Advance(1);
            if (displayOnFirst)
            {
                // Exercise the real event source's synthesized resume and the
                // serial lifecycle consumer, not only a direct gate change.
                backend.Publish!(ServiceLifecycleTrigger.DisplayOn);
            }
            else
            {
                relayed = Report(coordinator, Request(time, false));
            }
            await runtime.RestoreStarted.Task.WaitAsync(DeadlockGuard);
            long generation = gate.Generation;

            time.Advance(2001);
            backend.Publish!(ServiceLifecycleTrigger.Resumed);
            Assert.Equal(generation, gate.Generation);
            Assert.False(runtime.RestoreCancellation.IsCancellationRequested);
            runtime.RestoreRelease.TrySetResult();
            await coordinator.ProcessNativeAsync(false, default).WaitAsync(DeadlockGuard);
            if (relayed is not null)
            {
                await relayed.WaitAsync(DeadlockGuard);
            }

            Assert.Equal(1, runtime.NoWriteInvalidations);
            Assert.Equal(1, runtime.RestoreCalls);
            Assert.Equal(1, runtime.CompleteResumeCalls);
            Assert.Equal(0, runtime.CancelResumeCalls);
            Assert.False(runtime.RestoreCancellation.IsCancellationRequested);
        }
        finally
        {
            runtime.RestoreRelease.TrySetResult();
            await stopping.CancelAsync();
            await listening.WaitAsync(DeadlockGuard);
        }
    }

    [Fact]
    public async Task SettledResume_StillAllowsRecoveryWhenTheNextSuspendWasMissed()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime();
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        var backend = new Backend();
        using var source = new WindowsServiceLifecycleEventSource(backend, gate,
            getTickCount: time.GetTimestamp);
        backend.Publish!(ServiceLifecycleTrigger.Resumed);
        await coordinator.ProcessNativeAsync(false, default).WaitAsync(DeadlockGuard);
        long firstGeneration = gate.Generation;

        time.Advance(2001);
        backend.Publish!(ServiceLifecycleTrigger.Resumed);
        Assert.True(gate.Generation > firstGeneration);
        await coordinator.ProcessNativeAsync(false, default).WaitAsync(DeadlockGuard);

        Assert.Equal(2, runtime.NoWriteInvalidations);
        Assert.Equal(2, runtime.RestoreCalls);
        Assert.Equal(2, runtime.CompleteResumeCalls);
        Assert.False(gate.IsActive);
    }

    [Fact]
    public async Task ActualNativeSuspend_StillCancelsAnOngoingRestore()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime { BlockRestore = true };
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        var backend = new Backend();
        using var source = new WindowsServiceLifecycleEventSource(backend, gate);
        Task<ControlOperationResult> resuming = Report(coordinator, Request(time, false));
        await runtime.RestoreStarted.Task.WaitAsync(DeadlockGuard);
        long generation = gate.Generation;

        time.Advance(2001);
        backend.Publish!(ServiceLifecycleTrigger.Suspending);
        Assert.True(gate.IsActive);
        Assert.True(gate.Generation > generation);
        await resuming.WaitAsync(DeadlockGuard);

        Assert.True(runtime.RestoreCancellation.IsCancellationRequested);
        Assert.Equal(1, runtime.CancelResumeCalls);
        Assert.Equal(0, runtime.CompleteResumeCalls);
        await coordinator.ProcessNativeAsync(true, default).WaitAsync(DeadlockGuard);
        Assert.Equal(1, runtime.PreparedWrites);
    }

    [Fact]
    public async Task NativeDisplayOnWatermark_RejectsAnOlderRelaySuspend()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime();
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        var backend = new Backend();
        using var source = new WindowsServiceLifecycleEventSource(backend, gate);
        ReportSessionPowerTransitionRequest stale = Request(time, true);
        time.Advance(1);
        backend.Publish!(ServiceLifecycleTrigger.DisplayOn);

        ControlOperationResult result = await Report(coordinator, stale);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.False(gate.IsActive);
        Assert.Equal(0, runtime.PrepareCalls);
    }

    [Fact]
    public async Task StaleInstanceOrSequence_CannotReplaceTheCurrentAgent()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime();
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        ReportSessionPowerTransitionRequest old = Request(time, true);
        await Report(coordinator, old);
        time.Advance(1);
        ReportSessionPowerTransitionRequest current = old with
        {
            RequestId = Guid.NewGuid(),
            InstanceId = Guid.NewGuid(),
            InstanceStartedQpc = old.InstanceStartedQpc + 1,
            TimestampQpc = time.GetTimestamp(),
            Suspending = false,
        };
        await Report(coordinator, current);
        time.Advance(1);

        ControlOperationResult staleInstance = await Report(coordinator, old with
        {
            RequestId = Guid.NewGuid(),
            Sequence = 99,
            TimestampQpc = time.GetTimestamp(),
        });
        ControlOperationResult duplicate = await Report(coordinator, current with
        {
            RequestId = Guid.NewGuid(),
            TimestampQpc = time.GetTimestamp(),
            Suspending = true,
        });

        Assert.Equal(ControlOperationStatus.Blocked, staleInstance.Status);
        Assert.Equal(ControlOperationStatus.Blocked, duplicate.Status);
        Assert.False(gate.IsActive);
        Assert.Equal(1, runtime.PrepareCalls);
    }

    [Fact]
    public async Task ExpiredOrFutureTimestamp_DoesNotLatchTheGate()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime();
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        ReportSessionPowerTransitionRequest expired = Request(time, true);
        time.Advance(SessionPowerTransition.MaximumAgeMilliseconds + 1);

        Assert.Equal(ControlOperationStatus.Blocked, (await Report(coordinator, expired)).Status);
        Assert.Equal(ControlOperationStatus.Blocked, (await Report(coordinator, expired with
        {
            TimestampQpc = time.GetTimestamp() + 1,
        })).Status);
        Assert.False(gate.IsActive);
        Assert.Equal(0, runtime.PrepareCalls);
    }

    [Fact]
    public async Task QpcDeadline_RejectsLateWriteEvenWhenCancellationTimerHasNotFired()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime { BlockPrepare = true, IgnorePrepareCancellation = true };
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        Task<ControlOperationResult> preparing = Report(coordinator, Request(time, true));
        await runtime.PrepareStarted.Task.WaitAsync(DeadlockGuard);
        Assert.True(gate.IsActive);

        time.Advance(SessionPowerTransition.MaximumAgeMilliseconds + 1);
        runtime.PrepareRelease.TrySetResult();
        ControlOperationResult result = await preparing.WaitAsync(DeadlockGuard);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(0, runtime.PreparedWrites);
        Assert.True(gate.IsActive);
    }

    [Fact]
    public async Task Resume_ClearsGateAndCancelsBlockedSuspendBeforeWaiting()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime { BlockPrepare = true };
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        ReportSessionPowerTransitionRequest first = Request(time, true);
        Task<ControlOperationResult> preparing = Report(coordinator, first);
        await runtime.PrepareStarted.Task.WaitAsync(DeadlockGuard);
        time.Advance(1);

        Task<ControlOperationResult> resuming = Report(coordinator, first with
        {
            Sequence = 2,
            TimestampQpc = time.GetTimestamp(),
            Suspending = false,
        });
        Assert.False(gate.IsActive);
        await Task.WhenAll(preparing, resuming).WaitAsync(DeadlockGuard);

        Assert.Equal(0, runtime.PreparedWrites);
        Assert.Equal(1, runtime.RestoreCalls);
        Assert.Equal(1, runtime.MaximumConcurrentOperations);
    }

    [Fact]
    public async Task AcceptedResume_CompletesAfterCallerStopsWaiting()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime { BlockResume = true };
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        gate.Enter();
        time.Advance(1);
        using var callerCancellation = new CancellationTokenSource();
        Task<ControlOperationResult> response = coordinator.ReportAsync(Request(time, false),
            ServiceClientContext.TrustedTestClient, static () => true, callerCancellation.Token);
        await runtime.ResumeStarted.Task.WaitAsync(DeadlockGuard);

        await callerCancellation.CancelAsync();
        Assert.Equal(ControlOperationStatus.Stored, (await response.WaitAsync(DeadlockGuard)).Status);
        time.Advance(SessionPowerTransition.MaximumAgeMilliseconds + 1);
        runtime.ResumeRelease.TrySetResult();
        await runtime.ResumeCompleted.Task.WaitAsync(DeadlockGuard);

        Assert.Equal(1, runtime.RestoreCalls);
        Assert.False(gate.IsActive);
    }

    [Fact]
    public async Task DisplayOn_DoesNotOrphanAnOngoingResume()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime { BlockResume = true };
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        var backend = new Backend();
        using var source = new WindowsServiceLifecycleEventSource(backend, gate);
        Task<ControlOperationResult> resuming = Report(coordinator, Request(time, false));
        await runtime.ResumeStarted.Task.WaitAsync(DeadlockGuard);
        long generation = gate.Generation;

        time.Advance(2001);
        backend.Publish!(ServiceLifecycleTrigger.DisplayOn);
        Assert.Equal(generation, gate.Generation);
        runtime.ResumeRelease.TrySetResult();
        await runtime.ResumeCompleted.Task.WaitAsync(DeadlockGuard);
        await resuming.WaitAsync(DeadlockGuard);

        Assert.Equal(1, runtime.RestoreCalls);
        Assert.False(gate.IsActive);
    }

    [Fact]
    public async Task NativeSuspend_CancelsResumeBeforeTheLifecycleConsumerDequeuesIt()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime { BlockResume = true };
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        var backend = new Backend();
        using var source = new WindowsServiceLifecycleEventSource(backend, gate);
        Task<ControlOperationResult> resuming = Report(coordinator, Request(time, false));
        await runtime.ResumeStarted.Task.WaitAsync(DeadlockGuard);
        time.Advance(1);

        backend.Publish!(ServiceLifecycleTrigger.Suspending);
        Assert.True(gate.IsActive);
        Assert.Equal(ControlOperationStatus.Stored, (await resuming.WaitAsync(DeadlockGuard)).Status);
        Assert.Equal(0, runtime.RestoreCalls);
        Assert.Equal(0, runtime.PrepareCalls);

        await coordinator.ProcessNativeAsync(true, default).WaitAsync(DeadlockGuard);
        Assert.Equal(1, runtime.PreparedWrites);
        Assert.Equal(1, runtime.MaximumConcurrentOperations);
    }

    [Fact]
    public async Task NewSuspend_CancelsActiveResumeAndRunsWithinItsOwnBoundary()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime { BlockResume = true };
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        ReportSessionPowerTransitionRequest resume = Request(time, false);
        Task<ControlOperationResult> resuming = Report(coordinator, resume);
        await runtime.ResumeStarted.Task.WaitAsync(DeadlockGuard);
        time.Advance(1);

        Task<ControlOperationResult> suspending = Report(coordinator, resume with
        {
            Sequence = 2,
            TimestampQpc = time.GetTimestamp(),
            Suspending = true,
        });
        Assert.True(gate.IsActive);
        await Task.WhenAll(resuming, suspending).WaitAsync(DeadlockGuard);

        Assert.Equal(0, runtime.RestoreCalls);
        Assert.Equal(1, runtime.PreparedWrites);
        Assert.Equal(1, runtime.MaximumConcurrentOperations);
    }

    [Fact]
    public async Task BlockedRunner_KeepsOnlyTheLatestPendingTransition()
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime { BlockPrepare = true, IgnorePrepareCancellation = true };
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        ReportSessionPowerTransitionRequest first = Request(time, true);
        Task<ControlOperationResult> active = Report(coordinator, first);
        await runtime.PrepareStarted.Task.WaitAsync(DeadlockGuard);
        time.Advance(1);
        Task<ControlOperationResult> replacedResume = Report(coordinator, first with
        {
            Sequence = 2,
            TimestampQpc = time.GetTimestamp(),
            Suspending = false,
        });
        time.Advance(1);
        Task<ControlOperationResult> replacedSuspend = Report(coordinator, first with
        {
            Sequence = 3,
            TimestampQpc = time.GetTimestamp(),
        });
        time.Advance(1);
        Task<ControlOperationResult> latest = Report(coordinator, first with
        {
            Sequence = 4,
            TimestampQpc = time.GetTimestamp(),
            Suspending = false,
        });

        long latestGeneration = gate.Generation;
        time.Advance(2001);
        Task<ControlOperationResult> duplicateLatest = Report(coordinator, first with
        {
            Sequence = 5,
            TimestampQpc = time.GetTimestamp(),
            Suspending = false,
        });
        Assert.Equal(latestGeneration, gate.Generation);

        Assert.Equal(ControlOperationStatus.Stored, (await replacedResume).Status);
        Assert.Equal(ControlOperationStatus.Stored, (await replacedSuspend).Status);
        runtime.PrepareRelease.TrySetResult();
        await Task.WhenAll(active, latest, duplicateLatest).WaitAsync(DeadlockGuard);

        Assert.Equal(1, runtime.PrepareCalls);
        Assert.Equal(0, runtime.PreparedWrites);
        Assert.Equal(1, runtime.RestoreCalls);
        Assert.Equal(1, runtime.MaximumConcurrentOperations);
    }

    private static Task<ControlOperationResult> Report(
        ServiceSessionPowerTransitionCoordinator coordinator,
        ReportSessionPowerTransitionRequest request) => coordinator.ReportAsync(request,
            ServiceClientContext.TrustedTestClient, static () => true, default);

    private static ReportSessionPowerTransitionRequest Request(Clock time, bool suspending) =>
        new(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), Guid.NewGuid(),
            1000, 1, time.GetTimestamp(), suspending);

    private sealed class Clock : TimeProvider
    {
        private long timestamp = 10000;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Volatile.Read(ref timestamp);
        internal void Advance(long milliseconds) => Interlocked.Add(ref timestamp, milliseconds);
    }

    private sealed class Authority : IInteractiveSessionAuthority
    {
        public int? GetControlSessionId() => 1;
    }

    private sealed class RejectArchive : IDiagnosticArchiveProvider
    {
        public Task<DiagnosticArchive> CreateAsync(bool liveReadingsEnabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Backend : IWindowsLifecycleNotificationBackend, IDisposable
    {
        internal Action<ServiceLifecycleTrigger>? Publish { get; private set; }
        public IDisposable Subscribe(Action<ServiceLifecycleTrigger> publish)
        {
            Publish = publish;
            return this;
        }
        public void Dispose() => Publish = null;
    }

    private sealed class Runtime : IServiceControlRuntime
    {
        private int concurrentOperations;
        internal bool BlockPrepare { get; init; }
        internal bool IgnorePrepareCancellation { get; init; }
        internal bool BlockResume { get; init; }
        internal bool BlockNoWriteInvalidation { get; init; }
        internal bool BlockRestore { get; init; }
        internal int ActivationCalls { get; private set; }
        internal int PrepareCalls { get; private set; }
        internal int PreparedWrites { get; private set; }
        internal int NoWriteInvalidations { get; private set; }
        internal int GenericInvalidations { get; private set; }
        internal int RestoreCalls { get; private set; }
        internal int BeginResumeCalls { get; private set; }
        internal int CompleteResumeCalls { get; private set; }
        internal int CancelResumeCalls { get; private set; }
        internal CancellationToken RestoreCancellation { get; private set; }
        internal int MaximumConcurrentOperations { get; private set; }
        internal TaskCompletionSource PrepareStarted { get; } = NewSignal();
        internal TaskCompletionSource PrepareRelease { get; } = NewSignal();
        internal TaskCompletionSource ResumeStarted { get; } = NewSignal();
        internal TaskCompletionSource ResumeRelease { get; } = NewSignal();
        internal TaskCompletionSource ResumeCompleted { get; } = NewSignal();
        internal TaskCompletionSource NoWriteInvalidationStarted { get; } = NewSignal();
        internal TaskCompletionSource NoWriteInvalidationRelease { get; } = NewSignal();
        internal TaskCompletionSource RestoreStarted { get; } = NewSignal();
        internal TaskCompletionSource RestoreRelease { get; } = NewSignal();

        public Task ActivateUserAsync(SecurityIdentifier userSid, Func<bool> stillControlsSession,
            CancellationToken cancellationToken)
        {
            ActivationCalls++;
            throw new InvalidOperationException("Relay must not activate user configuration.");
        }

        public async Task<ControlOperationResult> PrepareForLowPowerAsync(long expectedGeneration,
            Func<bool> stillAuthorized, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                PrepareCalls++;
                PrepareStarted.TrySetResult();
                if (BlockPrepare)
                {
                    await PrepareRelease.Task.WaitAsync(IgnorePrepareCancellation
                        ? CancellationToken.None : cancellationToken);
                }
                if (!stillAuthorized())
                {
                    return new(ControlOperationStatus.Blocked, "Preparation is no longer authorized.");
                }
                PreparedWrites++;
                return new(ControlOperationStatus.Applied, "Synthetic Off confirmed.");
            }
            finally
            {
                Interlocked.Decrement(ref concurrentOperations);
            }
        }

        public async Task InvalidateLightingRendererForLowPowerAsync(CancellationToken cancellationToken)
        {
            NoWriteInvalidations++;
            NoWriteInvalidationStarted.TrySetResult();
            if (BlockNoWriteInvalidation)
            {
                await NoWriteInvalidationRelease.Task.WaitAsync(cancellationToken);
            }
        }

        public Task InvalidateLightingRendererAsync(CancellationToken cancellationToken)
        {
            GenericInvalidations++;
            throw new InvalidOperationException("Generic close is not allowed on this path.");
        }

        public async Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                ResumeStarted.TrySetResult();
                if (BlockResume)
                {
                    await ResumeRelease.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref concurrentOperations);
            }
        }

        public async Task RestoreLightingRendererAsync(CancellationToken cancellationToken)
        {
            RestoreCalls++;
            RestoreCancellation = cancellationToken;
            RestoreStarted.TrySetResult();
            if (BlockRestore)
            {
                await RestoreRelease.Task.WaitAsync(cancellationToken);
            }
        }

        public Task BeginResumeValidationAsync(CancellationToken cancellationToken)
        {
            BeginResumeCalls++;
            return Task.CompletedTask;
        }

        public Task CancelResumeValidationAsync(CancellationToken cancellationToken)
        {
            CancelResumeCalls++;
            return Task.CompletedTask;
        }

        public Task CompleteResumeValidationAsync(CancellationToken cancellationToken)
        {
            CompleteResumeCalls++;
            ResumeCompleted.TrySetResult();
            return Task.CompletedTask;
        }

        private void Enter() => MaximumConcurrentOperations = Math.Max(
            MaximumConcurrentOperations, Interlocked.Increment(ref concurrentOperations));
        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ServiceStatusSnapshot GetStatus() => throw new NotSupportedException();
        public Task ObserveAsync(bool includeLiveReadings, CancellationToken token) =>
            throw new NotSupportedException();
        public Task RefreshControllerPresenceAsync(CancellationToken token) =>
            throw new NotSupportedException();
        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(
            ManualOverride<PerformancePreset>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled,
            CancellationToken token) => throw new NotSupportedException();
    }
}
