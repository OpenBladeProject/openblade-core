using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData("LostSession", false)]
    [InlineData("LostSession", true)]
    [InlineData("Canceled", false)]
    [InlineData("Canceled", true)]
    [InlineData("Failure", false)]
    [InlineData("Failure", true)]
    public async Task ActivateUserAsync_InterruptedHandoffReloadsPriorUserBeforeSaving(
        string interruption, bool reactivateDuringHandoff)
    {
        string machineRoot = Path.Combine(directory, "interrupted-user-handoff");
        var repository = new UserConfigurationRepository(
            machineRoot, ProfileSetValidator.CreateUnverifiedSafeDefaults());
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        UserConfigurationContext first = await repository.LoadAsync(firstSid, CancellationToken.None);
        UserConfigurationContext second = await repository.LoadAsync(secondSid, CancellationToken.None);
        ProfileSet firstProfiles = first.Configuration.Profiles with { BatteryPolicy = BatteryPolicy.Create(75) };
        ProfileSet secondProfiles = second.Configuration.Profiles with { BatteryPolicy = BatteryPolicy.Create(65) };
        await first.Store.SaveProfilesAsync(firstProfiles, CancellationToken.None);
        await second.Store.SaveProfilesAsync(secondProfiles, CancellationToken.None);
        TestContext context = CreateContext(providerAvailable: false, userConfigurationRepository: repository);
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        int checks = 0;
        Task? reactivation = null;
        bool reactivationCompletedInsideHandoff = false;
        bool ControlsSecondSession()
        {
            if (++checks == 1)
            {
                return true;
            }
            // With no device, this check follows installation of B's context.
            // An A request arriving here must wait for the handoff's gate.
            if (reactivateDuringHandoff)
            {
                reactivation = context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
                reactivationCompletedInsideHandoff = reactivation.IsCompleted;
            }
            if (interruption == "Canceled")
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            if (interruption == "Failure")
            {
                throw new IOException("The session authority could not be read.");
            }
            return false;
        }

        Task handoff = context.Runtime.ActivateUserAsync(
            secondSid, ControlsSecondSession, cancellation.Token);
        if (interruption == "Canceled")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handoff);
        }
        else if (interruption == "Failure")
        {
            await Assert.ThrowsAsync<IOException>(() => handoff);
        }
        else
        {
            await handoff;
        }
        Assert.Equal(2, checks);
        if (reactivation is not null)
        {
            await reactivation;
            Assert.False(reactivationCompletedInsideHandoff);
        }
        else
        {
            await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        }

        ProfileSet updatedFirstProfiles = firstProfiles with { BatteryPolicy = BatteryPolicy.Create(80) };
        ControlOperationResult saved = await context.Runtime.UpdateProfilesAsync(
            updatedFirstProfiles, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, saved.Status);
        Assert.Equal(updatedFirstProfiles, (await first.Store.LoadAsync(CancellationToken.None)).Profiles);
        Assert.Equal(secondProfiles, (await second.Store.LoadAsync(CancellationToken.None)).Profiles);
        Assert.Equal(updatedFirstProfiles, context.Runtime.GetStatus().Profiles);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task ActivateUserAsync_InterruptedUserCanRetryItsOwnActivation()
    {
        string machineRoot = Path.Combine(directory, "retry-user-handoff");
        var repository = new UserConfigurationRepository(
            machineRoot, ProfileSetValidator.CreateUnverifiedSafeDefaults());
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        UserConfigurationContext second = await repository.LoadAsync(secondSid, CancellationToken.None);
        ProfileSet secondProfiles = second.Configuration.Profiles with { BatteryPolicy = BatteryPolicy.Create(65) };
        await second.Store.SaveProfilesAsync(secondProfiles, CancellationToken.None);
        TestContext context = CreateContext(providerAvailable: false, userConfigurationRepository: repository);
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        int checks = 0;
        await context.Runtime.ActivateUserAsync(secondSid, () => ++checks == 1, CancellationToken.None);

        await context.Runtime.ActivateUserAsync(secondSid, CancellationToken.None);

        Assert.Equal(secondProfiles, context.Runtime.GetStatus().Profiles);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_UserHandoffCannotReplaceAnAdmittedRequestsConfiguration(
        bool cancelWaitingRequest)
    {
        string machineRoot = Path.Combine(directory, "concurrent-user-requests");
        var repository = new UserConfigurationRepository(
            machineRoot, ProfileSetValidator.CreateUnverifiedSafeDefaults());
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        UserConfigurationContext first = await repository.LoadAsync(firstSid, CancellationToken.None);
        UserConfigurationContext second = await repository.LoadAsync(secondSid, CancellationToken.None);
        ProfileSet firstProfiles = first.Configuration.Profiles with { BatteryPolicy = BatteryPolicy.Create(75) };
        ProfileSet secondProfiles = second.Configuration.Profiles with { BatteryPolicy = BatteryPolicy.Create(65) };
        await first.Store.SaveProfilesAsync(firstProfiles, CancellationToken.None);
        await second.Store.SaveProfilesAsync(secondProfiles, CancellationToken.None);
        TestContext context = CreateContext(providerAvailable: false, userConfigurationRepository: repository);
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        var runtime = new PausedUserActivationRuntime(context.Runtime, firstSid, secondSid);
        var authority = new HandoffSessionAuthority { SessionId = 4 };
        var handler = new ControlRequestHandler(runtime, new RecoveryArchiveProvider(),
            NullServiceShutdownSignal.Instance, authority);
        var firstClient = new ServiceClientContext(firstSid, 4, false, ServiceClientLogonKind.Interactive);
        var secondClient = new ServiceClientContext(secondSid, 5, false, ServiceClientLogonKind.Interactive);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task<ServiceResponse> firstRequest = handler.HandleAsync(
            new UpdateProfilesRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(),
                firstProfiles with { BatteryPolicy = BatteryPolicy.Create(80) }),
            firstClient, timeout.Token);
        await runtime.FirstActivationCompleted.Task.WaitAsync(timeout.Token);
        authority.SessionId = 5;
        Task<ServiceResponse> secondRequest = handler.HandleAsync(
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            secondClient, waitingCancellation.Token);
        bool secondEnteredBeforeFirstDispatch = runtime.SecondActivationEntered.Task.IsCompleted;
        if (cancelWaitingRequest)
        {
            waitingCancellation.Cancel();
        }
        runtime.ResumeFirstActivation.TrySetResult();
        ServiceResponse firstResponse = await firstRequest;
        if (cancelWaitingRequest)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondRequest);
        }
        else
        {
            Assert.True((await secondRequest).Success);
        }

        Assert.False(secondEnteredBeforeFirstDispatch);
        Assert.True(firstResponse.Success);
        Assert.Equal(ControlOperationStatus.Blocked, firstResponse.Result?.Status);
        Assert.Equal(firstProfiles, (await first.Store.LoadAsync(timeout.Token)).Profiles);
        Assert.Equal(secondProfiles, (await second.Store.LoadAsync(timeout.Token)).Profiles);
        ProfileSet updatedSecondProfiles = secondProfiles with { BatteryPolicy = BatteryPolicy.Create(85) };
        ServiceResponse healthy = await handler.HandleAsync(
            new UpdateProfilesRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), updatedSecondProfiles),
            secondClient, timeout.Token);
        Assert.True(healthy.Success);
        Assert.Equal(firstProfiles, (await first.Store.LoadAsync(timeout.Token)).Profiles);
        Assert.Equal(updatedSecondProfiles, (await second.Store.LoadAsync(timeout.Token)).Profiles);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task HandleAsync_QueuedMutationRechecksSessionBeforeActivatingItsUser()
    {
        var repository = new UserConfigurationRepository(
            Path.Combine(directory, "queued-user-request"), ProfileSetValidator.CreateUnverifiedSafeDefaults());
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        UserConfigurationContext second = await repository.LoadAsync(secondSid, CancellationToken.None);
        TestContext context = CreateContext(providerAvailable: false, userConfigurationRepository: repository);
        var runtime = new PausedUserActivationRuntime(context.Runtime, firstSid, secondSid);
        var authority = new HandoffSessionAuthority { SessionId = 4 };
        var handler = new ControlRequestHandler(runtime, new RecoveryArchiveProvider(),
            NullServiceShutdownSignal.Instance, authority);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<ServiceResponse> firstRequest = handler.HandleAsync(
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            new ServiceClientContext(firstSid, 4, false, ServiceClientLogonKind.Interactive), timeout.Token);
        await runtime.FirstActivationCompleted.Task.WaitAsync(timeout.Token);
        authority.SessionId = 5;
        Task<ServiceResponse> queuedMutation = handler.HandleAsync(
            new UpdateProfilesRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(),
                second.Configuration.Profiles with { BatteryPolicy = BatteryPolicy.Create(85) }),
            new ServiceClientContext(secondSid, 5, false, ServiceClientLogonKind.Interactive), timeout.Token);
        authority.SessionId = 4;
        runtime.ResumeFirstActivation.TrySetResult();

        Assert.True((await firstRequest).Success);
        Assert.False((await queuedMutation).Success);
        Assert.False(runtime.SecondActivationEntered.Task.IsCompleted);
        Assert.Equal(second.Configuration.Profiles, (await second.Store.LoadAsync(timeout.Token)).Profiles);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task HandleAsync_LowPowerIngressBypassesTheUserRequestGate()
    {
        var repository = new UserConfigurationRepository(
            Path.Combine(directory, "user-request-power-ingress"), ProfileSetValidator.CreateUnverifiedSafeDefaults());
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        TestContext context = CreateContext(providerAvailable: false, userConfigurationRepository: repository);
        var runtime = new PausedUserActivationRuntime(context.Runtime, firstSid, secondSid);
        var gate = new LowPowerGate();
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        var handler = new ControlRequestHandler(runtime, new RecoveryArchiveProvider(),
            NullServiceShutdownSignal.Instance, new HandoffSessionAuthority { SessionId = 4 }, coordinator);
        var client = new ServiceClientContext(firstSid, 4, false, ServiceClientLogonKind.Interactive);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<ServiceResponse> firstRequest = handler.HandleAsync(
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()), client, timeout.Token);
        await runtime.FirstActivationCompleted.Task.WaitAsync(timeout.Token);
        long timestamp = TimeProvider.System.GetTimestamp();
        Task<ServiceResponse> suspend = handler.HandleAsync(
            new ReportSessionPowerTransitionRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(),
                Guid.NewGuid(), timestamp - 1, 1, timestamp, true), client, timeout.Token);
        try
        {
            Assert.True(gate.IsActive);
            Assert.True((await suspend).Success);
        }
        finally
        {
            runtime.ResumeFirstActivation.TrySetResult();
            await firstRequest;
        }
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task HandleAsync_CanceledQueuedUserAbaInvalidatesTheActiveHyperBoostRetry()
    {
        var repository = new UserConfigurationRepository(
            Path.Combine(directory, "queued-user-hyperboost"), ProfileSetValidator.CreateUnverifiedSafeDefaults());
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        await repository.LoadAsync(firstSid, CancellationToken.None);
        await repository.LoadAsync(secondSid, CancellationToken.None);
        var authority = new HandoffSessionAuthority { SessionId = 4 };
        var firstClient = new ServiceClientContext(firstSid, 4, false, ServiceClientLogonKind.Interactive);
        var secondClient = new ServiceClientContext(secondSid, 5, false, ServiceClientLogonKind.Interactive);
        using var queuedCancellation = new CancellationTokenSource();
        Task<ServiceResponse>? queuedRequest = null;
        ControlRequestHandler? handlerAtDelay = null;
        var journal = new RecordingHyperBoostOwnershipJournalStore();
        TestContext context = CreateContext(providerAvailable: false,
            userConfigurationRepository: repository,
            hyperBoostOwnershipJournalStore: journal,
            transitionRetryDelay: (_, _) =>
            {
                authority.SessionId = 5;
                queuedRequest = handlerAtDelay!.HandleAsync(
                    new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
                    secondClient, queuedCancellation.Token);
                // Returning to A and canceling B cannot restore the old epoch.
                authority.SessionId = 4;
                queuedCancellation.Cancel();
                return Task.CompletedTask;
            });
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        context.Provider.Available = true;
        ConfigurePadlessHyperBoost(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.HyperBoostFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            BladeWriteFailurePhase.HyperBoostAdapterEligibilityRead,
            "Injected preflight failure before a queued user change."));
        handlerAtDelay = new ControlRequestHandler(context.Runtime, new RecoveryArchiveProvider(),
            NullServiceShutdownSignal.Instance, authority);

        ServiceResponse response = await handlerAtDelay.HandleAsync(
            new SetHyperBoostRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), true),
            firstClient, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Assert.IsAssignableFrom<Task<ServiceResponse>>(queuedRequest));

        Assert.True(response.Success);
        Assert.NotEqual(ControlOperationStatus.Applied, response.Result?.Status);
        Assert.Equal(1, context.Device.HyperBoostSetCalls);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        // The canceled wait released its pending count, so an independent
        // later explicit request can establish fresh authority successfully.
        ServiceResponse healthy = await handlerAtDelay.HandleAsync(
            new SetHyperBoostRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), true),
            firstClient, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, healthy.Result?.Status);
        Assert.Equal(2, context.Device.HyperBoostSetCalls);
    }

    [Fact]
    public async Task ActivateUserAsync_CanceledOldRendererCleanupLeavesPriorUserRetryable()
    {
        var repository = new UserConfigurationRepository(
            Path.Combine(directory, "canceled-old-renderer"), ProfileSetValidator.CreateUnverifiedSafeDefaults());
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        TestContext context = CreateWritableContext(userConfigurationRepository: repository);
        context.Provider.Available = false;
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        context.Provider.Available = true;
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LightingEffects = new HashSet<LightingEffect> { LightingEffect.Off, LightingEffect.Static },
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        LightingSettings lighting = LightingSettingsFactory.Static(new RgbColor(10, 20, 30), 50);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetLightingOverrideAsync(
            new(lighting, OverrideLifetime.Persistent), CancellationToken.None)).Status);
        int writes = context.Device.LightingSetCalls;
        using var cancellation = new CancellationTokenSource();
        context.Provider.AfterLightingInvalidation = () =>
        {
            context.Provider.AfterLightingInvalidation = null;
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Runtime.ActivateUserAsync(
            secondSid, cancellation.Token));
        Assert.Equal(LightingRendererOwnershipState.None, context.Device.LightingRendererOwnership);
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);

        Assert.Equal(writes + 1, context.Device.LightingSetCalls);
        Assert.Equal(lighting, context.Device.ConfirmedState.Lighting);
        Assert.Equal(LightingRendererOwnershipState.Static, context.Device.LightingRendererOwnership);
    }

    [Fact]
    public async Task ActivateUserAsync_SameUserRetryDoesNotReplayCanceledSubmissionWithoutReadback()
    {
        var repository = new UserConfigurationRepository(
            Path.Combine(directory, "uncertain-user-activation"), ProfileSetValidator.CreateUnverifiedSafeDefaults());
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        UserConfigurationContext second = await repository.LoadAsync(secondSid, CancellationToken.None);
        await second.Store.SaveProfilesAsync(second.Configuration.Profiles with
        {
            PluggedIn = second.Configuration.Profiles.PluggedIn with { Performance = PerformancePreset.Performance },
        }, CancellationToken.None);
        TestContext context = CreateWritableContext(userConfigurationRepository: repository);
        context.Provider.Available = false;
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        context.Provider.Available = true;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Performance)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Runtime.ActivateUserAsync(
            secondSid, cancellation.Token));
        int submitted = context.Device.PerformanceSetCalls;
        Assert.Equal(1, submitted);
        context.Device.AfterSetter = null;
        context.Device.ReadStateFailure = new IOException("The submitted value cannot be read back.");
        int readsBeforeRetry = context.Device.StateReadCalls;
        await context.Runtime.ActivateUserAsync(secondSid, CancellationToken.None);

        Assert.Equal(submitted, context.Device.PerformanceSetCalls);
        Assert.True(context.Device.StateReadCalls > readsBeforeRetry);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
    }

    [Fact]
    public async Task HandleProtocol51ReadOnlyAsync_DoesNotWaitForAnActiveUserRequest()
    {
        var repository = new UserConfigurationRepository(
            Path.Combine(directory, "compatibility-status-request"), ProfileSetValidator.CreateUnverifiedSafeDefaults());
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        TestContext context = CreateContext(providerAvailable: false, userConfigurationRepository: repository);
        var runtime = new PausedUserActivationRuntime(context.Runtime, firstSid, secondSid);
        var handler = new ControlRequestHandler(runtime, new RecoveryArchiveProvider(),
            NullServiceShutdownSignal.Instance, new HandoffSessionAuthority { SessionId = 4 });
        var client = new ServiceClientContext(firstSid, 4, false, ServiceClientLogonKind.Interactive);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<ServiceResponse> current = handler.HandleAsync(
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()), client, timeout.Token);
        await runtime.FirstActivationCompleted.Task.WaitAsync(timeout.Token);
        Task<ServiceResponse> compatibility = handler.HandleProtocol51ReadOnlyAsync(
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()), client, timeout.Token);
        bool completedBeforeRelease = compatibility.IsCompleted;
        runtime.ResumeFirstActivation.TrySetResult();

        Assert.True((await compatibility).Success);
        Assert.True((await current).Success);
        Assert.True(completedBeforeRelease);
    }

    [Fact]
    public async Task HandleAsync_CompletedCurrentUserReadsCachedStatusWhileAnotherRequestIsBusy()
    {
        var repository = new UserConfigurationRepository(
            Path.Combine(directory, "busy-user-cached-status"), ProfileSetValidator.CreateUnverifiedSafeDefaults());
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        TestContext context = CreateContext(providerAvailable: false, userConfigurationRepository: repository);
        var authority = new HandoffSessionAuthority { SessionId = 4 };
        var archive = new PausedArchiveProvider();
        var handler = new ControlRequestHandler(context.Runtime, archive, NullServiceShutdownSignal.Instance, authority);
        var firstClient = new ServiceClientContext(firstSid, 4, false, ServiceClientLogonKind.Interactive);
        var secondClient = new ServiceClientContext(secondSid, 5, false, ServiceClientLogonKind.Interactive);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await handler.HandleAsync(new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()), firstClient, timeout.Token);
        Task<ServiceResponse> busy = handler.HandleAsync(
            new ExportDiagnosticsRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), false), firstClient, timeout.Token);
        await archive.Entered.Task.WaitAsync(timeout.Token);
        Task<ServiceResponse> cached = handler.HandleAsync(
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()), firstClient, timeout.Token);
        bool cachedCompletedBeforeRelease = cached.IsCompleted;
        authority.SessionId = 5;
        Task<ServiceResponse> changedUser = handler.HandleAsync(
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()), secondClient, timeout.Token);
        bool changedUserCompletedBeforeRelease = changedUser.IsCompleted;
        archive.Resume.TrySetResult(new DiagnosticArchive("diagnostics.zip", []));

        Assert.True((await busy).Success);
        Assert.True((await cached).Success);
        Assert.True((await changedUser).Success);
        Assert.True(cachedCompletedBeforeRelease);
        Assert.False(changedUserCompletedBeforeRelease);
    }

    private sealed class PausedArchiveProvider : IDiagnosticArchiveProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DiagnosticArchive> Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DiagnosticArchive> CreateAsync(bool liveReadingsEnabled, CancellationToken token)
        {
            Entered.TrySetResult();
            return Resume.Task.WaitAsync(token);
        }
    }

    private sealed class HandoffSessionAuthority : IInteractiveSessionAuthority
    {
        public int SessionId { get; set; }

        public int? GetControlSessionId() => SessionId;
    }

    // This adapter creates a scheduler boundary between the real activation
    // and real mutation while leaving their persistence paths unchanged.
    private sealed class PausedUserActivationRuntime(
        ServiceControlRuntime inner, SecurityIdentifier firstSid, SecurityIdentifier secondSid)
        : IServiceControlRuntime
    {
        private bool firstPaused;

        public TaskCompletionSource FirstActivationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeFirstActivation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondActivationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ServiceStatusSnapshot GetStatus() => inner.GetStatus();

        public async Task ActivateUserAsync(SecurityIdentifier userSid, Func<bool> stillControlsSession,
            CancellationToken token)
        {
            if (userSid == secondSid)
            {
                SecondActivationEntered.TrySetResult();
            }
            await inner.ActivateUserAsync(userSid, stillControlsSession, token);
            if (userSid == firstSid && !firstPaused)
            {
                firstPaused = true;
                FirstActivationCompleted.TrySetResult();
                await ResumeFirstActivation.Task.WaitAsync(token);
            }
        }

        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token) =>
            inner.UpdateProfilesAsync(profiles, token);
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(ManualOverride<PerformancePreset>? value, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled, CancellationToken token) =>
            throw new NotSupportedException();
        public Task ObserveAsync(bool includeLiveReadings, CancellationToken token) => throw new NotSupportedException();
        public Task HandlePowerSourceChangedAsync(CancellationToken token) => throw new NotSupportedException();
        public Task RefreshControllerPresenceAsync(CancellationToken token) => throw new NotSupportedException();
    }
}
