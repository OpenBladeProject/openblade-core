using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class FanStopControllerTests
{
    private static readonly FanControlSettings ManualIntent = FanControlSettings.FromLegacy(FanMode.Maximum);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EntryOrCallerLossDuringAdmissionPreventsInitialObservation(bool callerLoss, bool automatic)
    {
        bool entry = true;
        bool caller = true;
        bool changeAdmission = false;
        var backend = new Backend
        {
            DuringAdmission = () =>
            {
                if (!changeAdmission)
                {
                    return;
                }
                if (callerLoss)
                {
                    caller = false;
                }
                else
                {
                    entry = false;
                }
            },
        };
        var controller = new FanStopController(backend);
        if (automatic)
        {
            await Stop(controller);
            backend.Calls.Clear();
        }
        changeAdmission = true;
        ControlOperationResult result = await controller.SetModeAsync(automatic ? FanStopMode.Automatic : FanStopMode.Stop,
            true, ManualIntent, () => true, default, () => entry, () => caller);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(["admission"], backend.Calls);
        Assert.False(controller.RequiresRecovery);
    }

    [Fact]
    public async Task ExplicitAutoRechecksCallerBeforeSubmissionWithoutLosingOwnedStop()
    {
        bool caller = true;
        bool changeCaller = false;
        var backend = new Backend
        {
            DuringStateRead = () =>
            {
                if (changeCaller)
                {
                    caller = false;
                }
            },
        };
        var controller = new FanStopController(backend);
        await Stop(controller);
        changeCaller = true;
        ControlOperationResult result = await controller.SetModeAsync(FanStopMode.Automatic,
            true, ManualIntent, () => true, default, requestAdmitted: () => caller);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.True(controller.OwnsStop);
        Assert.False(controller.RequiresRecovery);
        Assert.DoesNotContain("Automatic", backend.Calls);
        await controller.ReconcileAsync(true, () => true, default);
        Assert.Equal(FanStopState.Automatic, controller.GetStatus(true).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntryBoundaryAfterResolvedStop_ConfirmsThenHandsBackInSameCall(bool canceled)
    {
        using var cancellation = new CancellationTokenSource();
        bool entry = true;
        var backend = new Backend
        {
            AfterSubmission = () =>
            {
                entry = false;
                if (canceled)
                {
                    cancellation.Cancel();
                }
            },
        };
        var controller = new FanStopController(backend);
        if (canceled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.SetModeAsync(
                FanStopMode.Stop, true, ManualIntent, () => true, cancellation.Token, () => entry));
        }
        else
        {
            ControlOperationResult result = await controller.SetModeAsync(
                FanStopMode.Stop, true, ManualIntent, () => true, default, () => entry);
            Assert.Equal(ControlOperationStatus.Applied, result.Status);
        }
        Assert.Equal(1, controller.ConfirmedStopGeneration);
        Assert.False(controller.RequiresRecovery);
        Assert.Equal(FanStopState.Automatic, controller.GetStatus(true).State);
        Assert.Equal(["Stop", "Automatic"], backend.Calls.Where(value => value is "Stop" or "Automatic"));
    }

    [Fact]
    public async Task ClosedEntryPreventsStopButDoesNotPreventOwnedAutomatic()
    {
        var backend = new Backend();
        var controller = new FanStopController(backend);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await controller.SetModeAsync(FanStopMode.Stop, true, ManualIntent, () => true, default, () => false)).Status);
        Assert.Empty(backend.Calls);
        await Stop(controller);
        Assert.Equal(ControlOperationStatus.Applied,
            (await controller.SetModeAsync(FanStopMode.Automatic, true, ManualIntent, () => true, default, () => false)).Status);
        Assert.Equal(FanStopState.Automatic, controller.GetStatus(true).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresolvedStartupFencesBeforeAnyBackendCommand(bool unresolved)
    {
        var backend = new Backend
        {
            StartupDisposition = unresolved ? FanStopStartupDisposition.Unresolved : FanStopStartupDisposition.Unknown,
        };
        var controller = new FanStopController(backend);
        Assert.True(controller.RequiresRecovery);
        await controller.RefreshAsync(true, () => true, default);
        await Stop(controller);
        await controller.ReconcileAsync(true, () => true, default);
        Assert.Empty(backend.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InitialMappedReadFailure_FencesWithoutOwnershipAndNeverDispatchesAgain(bool refresh, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var backend = new Backend
        {
            DuringStateRead = () =>
            {
                if (cancel)
                {
                    cancellation.Cancel();
                }
                else
                {
                    throw new IOException("Simulated unresolved A0 command.");
                }
            },
        };
        var controller = new FanStopController(backend);
        async Task RunAsync()
        {
            if (refresh)
            {
                await controller.RefreshAsync(true, () => true, cancellation.Token);
            }
            else
            {
                await controller.SetModeAsync(FanStopMode.Stop, true, ManualIntent, () => true, cancellation.Token);
            }
        }
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(RunAsync);
        }
        else
        {
            await RunAsync();
        }
        Assert.True(controller.RequiresRecovery);
        Assert.Equal(["admission", "read"], backend.Calls);
        await controller.RefreshAsync(true, () => true, default);
        await controller.ReconcileAsync(true, () => true, default);
        await controller.SetModeAsync(FanStopMode.Stop, true, ManualIntent, () => true, default);
        await controller.SetModeAsync(FanStopMode.Automatic, true, ManualIntent, () => true, default);
        Assert.Equal(["admission", "read"], backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvedUnknownInitialRead_IsUnavailableWithoutInventingSubmissionUncertainty(bool refresh)
    {
        var backend = new Backend { Observed = FanStopObservedState.Unknown };
        var controller = new FanStopController(backend);
        if (refresh)
        {
            await controller.RefreshAsync(true, () => true, default);
        }
        else
        {
            await Stop(controller);
        }
        Assert.False(controller.RequiresRecovery);
        Assert.False(controller.GetStatus(true).CanStop);
        Assert.Equal(["admission", "read"], backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringCommandFreeAdmission_DoesNotStartReadOrInventUncertainty(bool refresh)
    {
        using var cancellation = new CancellationTokenSource();
        var backend = new Backend { DuringAdmission = cancellation.Cancel };
        var controller = new FanStopController(backend);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (refresh)
            {
                await controller.RefreshAsync(true, () => true, cancellation.Token);
            }
            else
            {
                await controller.SetModeAsync(FanStopMode.Stop, true, ManualIntent, () => true, cancellation.Token);
            }
        });
        Assert.False(controller.RequiresRecovery);
        Assert.Equal(["admission"], backend.Calls);
    }

    [Fact]
    public async Task ProductionBackend_IsUnavailableAndNeverClaimsAutomaticOrSubmits()
    {
        var controller = new FanStopController(UnavailableFanStopBackend.Instance);
        Assert.Equal(FanStopUnavailableReason.UnsupportedDevice, controller.GetStatus(false).UnavailableReason);
        Assert.Equal(FanStopUnavailableReason.RecoveryNotValidated, controller.GetStatus(true).UnavailableReason);
        foreach (FanStopMode mode in Enum.GetValues<FanStopMode>())
        {
            Assert.Equal(ControlOperationStatus.Blocked,
                (await controller.SetModeAsync(mode, true, ManualIntent, () => true, default)).Status);
        }

        Assert.Equal(FanStopState.Unavailable, controller.GetStatus(true).State);
        Assert.False(controller.GetStatus(true).CanStop);
        Assert.False(controller.GetStatus(true).CanReturnToAutomatic);
    }

    [Fact]
    public async Task StopAndAuto_RequireIndependentReadbackAndPreserveManualIntent()
    {
        var backend = new Backend();
        var controller = new FanStopController(backend);
        Assert.Equal(FanStopState.Inactive, controller.GetStatus(true).State);
        Assert.Equal(ControlOperationStatus.Applied, (await Stop(controller)).Status);
        Assert.Equal(["admission", "read", "Stop", "admission", "read"], backend.Calls);
        Assert.Equal(FanStopState.Stopped, controller.GetStatus(true).State);
        Assert.Equal(ManualIntent, controller.GetStatus(true).NormalFanControl);
        Assert.True(controller.GetStatus(true).CanReturnToAutomatic);
        backend.Calls.Clear();
        Assert.Equal(ControlOperationStatus.Applied,
            (await controller.SetModeAsync(FanStopMode.Automatic, true, ManualIntent, () => true, default)).Status);
        Assert.Equal(["admission", "read", "Automatic", "admission", "read"], backend.Calls);
        Assert.Equal(FanStopState.Automatic, controller.GetStatus(true).State);
        Assert.False(controller.OwnsStop);
        Assert.Equal(FanMode.Maximum, ManualIntent.Mode);
    }

    [Theory]
    [InlineData("temperature")]
    [InlineData("profile")]
    [InlineData("powerScope")]
    [InlineData("lowPower")]
    [InlineData("shutdown")]
    public async Task LeavingStopScope_HandsBackDirectlyToGenericAuto(string condition)
    {
        var backend = new Backend();
        var controller = new FanStopController(backend);
        await Stop(controller);
        backend.Calls.Clear();
        backend.Admission = condition switch
        {
            "temperature" => backend.Admission with { TemperatureSafe = false },
            "profile" => backend.Admission with { ProfileInScope = false },
            "powerScope" => backend.Admission with { PowerInScope = false },
            "lowPower" => backend.Admission with { SystemActive = false },
            _ => backend.Admission,
        };
        ControlOperationResult? result = await controller.ReconcileAsync(condition == "shutdown", () => true, default);
        Assert.Equal(ControlOperationStatus.Applied, result!.Status);
        Assert.Equal(["Automatic"], backend.Calls.Where(value => value is "Automatic" or "Stop"));
        Assert.Equal(FanStopState.Automatic, controller.GetStatus(true).State);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("controller")]
    [InlineData("power")]
    [InlineData("session")]
    public async Task AutoRetainsFreshSafetyAdmissionAndDoesNotWriteWhenItFails(string condition)
    {
        var backend = new Backend();
        var controller = new FanStopController(backend);
        await Stop(controller);
        backend.Calls.Clear();
        backend.Admission = condition switch
        {
            "identity" => backend.Admission with { ExactDeviceVerified = false },
            "controller" => backend.Admission with { ControllersAbsent = false },
            "power" => backend.Admission with { PowerKnown = false },
            _ => backend.Admission with { SessionAdmitted = false },
        };
        Assert.Equal(ControlOperationStatus.Blocked,
            (await controller.ReconcileAsync(true, () => true, default))!.Status);
        Assert.DoesNotContain("Automatic", backend.Calls);
        Assert.DoesNotContain("read", backend.Calls);
        Assert.True(controller.OwnsStop);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("controller")]
    [InlineData("power")]
    [InlineData("session")]
    [InlineData("caller")]
    public async Task FreshBasicAdmissionMustPrecedeEvenReadCommands(string failure)
    {
        var backend = new Backend();
        backend.Admission = failure switch
        {
            "identity" => backend.Admission with { ExactDeviceVerified = false },
            "controller" => backend.Admission with { ControllersAbsent = false },
            "power" => backend.Admission with { PowerKnown = false },
            "session" => backend.Admission with { SessionAdmitted = false },
            _ => backend.Admission,
        };
        var controller = new FanStopController(backend);
        bool StillAdmitted() => failure != "caller" || backend.Calls.Count == 0;
        await controller.RefreshAsync(true, StillAdmitted, default);
        Assert.False(controller.GetStatus(true).CanStop);
        backend.Calls.Clear();
        await controller.SetModeAsync(FanStopMode.Stop, true, ManualIntent, StillAdmitted, default);
        Assert.DoesNotContain("read", backend.Calls);
        Assert.DoesNotContain("Stop", backend.Calls);
        Assert.False(controller.RequiresRecovery);
    }

    [Fact]
    public async Task AdmissionLostAfterResolvedSubmission_FencesWithoutDispatchingConfirmationCommand()
    {
        var backend = new Backend();
        backend.AfterSubmission = () => backend.Admission = backend.Admission with { ControllersAbsent = false };
        var controller = new FanStopController(backend);
        await Stop(controller);
        Assert.Equal(["admission", "read", "Stop", "admission"], backend.Calls);
        Assert.True(controller.RequiresRecovery);
        Assert.False(controller.GetStatus(true).CanReturnToAutomatic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationRequiresOwnershipForStopButPermitsConfirmedAutoRelease(bool returningToAuto)
    {
        var backend = new Backend();
        var controller = new FanStopController(backend);
        if (returningToAuto)
        {
            await Stop(controller);
        }
        backend.AfterSubmission = () => backend.Admission = backend.Admission with { OwnershipConfirmed = false };
        await controller.SetModeAsync(returningToAuto ? FanStopMode.Automatic : FanStopMode.Stop,
            true, ManualIntent, () => true, default);
        Assert.Equal(returningToAuto ? FanStopState.Automatic : FanStopState.RecoveryRequired,
            controller.GetStatus(true).State);
        Assert.False(controller.GetStatus(true).CanReturnToAutomatic);
    }

    [Fact]
    public async Task ObservedStopWithoutOwnedWrite_DoesNotEstablishOwnershipOrAuthorizeAuto()
    {
        var backend = new Backend { Observed = FanStopObservedState.Stopped };
        var controller = new FanStopController(backend);
        Assert.Equal(ControlOperationStatus.Blocked, (await Stop(controller)).Status);
        await controller.SetModeAsync(FanStopMode.Automatic, true, ManualIntent, () => true, default);
        Assert.False(controller.OwnsStop);
        Assert.True(controller.RequiresRecovery);
        Assert.DoesNotContain("Automatic", backend.Calls);
        Assert.DoesNotContain("Stop", backend.Calls);
    }

    [Fact]
    public async Task ResolvedTransientAdmissionFailure_ReenablesOwnedAutoAfterFreshConfirmation()
    {
        var backend = new Backend();
        var controller = new FanStopController(backend);
        await Stop(controller);
        backend.Admission = backend.Admission with { ControllersAbsent = false };
        await controller.ReconcileAsync(false, () => true, default);
        Assert.False(controller.GetStatus(true).CanReturnToAutomatic);
        backend.Admission = backend.Admission with { ControllersAbsent = true };
        await controller.ReconcileAsync(false, () => true, default);
        Assert.True(controller.GetStatus(true).CanReturnToAutomatic);
        Assert.Equal(["Stop"], backend.Calls.Where(value => value is "Stop" or "Automatic"));
    }

    [Theory]
    [InlineData("uncertain")]
    [InlineData("exception")]
    [InlineData("readback")]
    public async Task PossibleSubmissionWithoutConfirmation_FencesAndNeverTriesAuto(string failure)
    {
        var backend = new Backend { Failure = failure };
        var controller = new FanStopController(backend);
        Assert.Equal(ControlOperationStatus.Blocked, (await Stop(controller)).Status);
        Assert.True(controller.RequiresRecovery);
        Assert.Equal(ManualIntent, controller.GetStatus(true).NormalFanControl);
        int calls = backend.Calls.Count;
        await controller.ReconcileAsync(true, () => true, default);
        await controller.SetModeAsync(FanStopMode.Automatic, true, ManualIntent, () => true, default);
        Assert.Equal(calls, backend.Calls.Count);
        Assert.False(controller.GetStatus(true).CanReturnToAutomatic);
    }

    [Fact]
    public async Task CanceledSubmission_PreservesFenceAndObservableCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var backend = new Backend { DuringSubmission = () => cancellation.Cancel() };
        var controller = new FanStopController(backend);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.SetModeAsync(FanStopMode.Stop, true, ManualIntent, () => true, cancellation.Token));
        Assert.True(controller.RequiresRecovery);
        Assert.DoesNotContain("Automatic", backend.Calls);
    }

    [Fact]
    public async Task KnownNotSubmittedFailure_DoesNotFabricateOwnershipOrRecovery()
    {
        var backend = new Backend { Failure = "notSubmitted" };
        var controller = new FanStopController(backend);
        Assert.Equal(ControlOperationStatus.Blocked, (await Stop(controller)).Status);
        Assert.False(controller.OwnsStop);
        Assert.False(controller.RequiresRecovery);
        Assert.Equal(FanStopState.Inactive, controller.GetStatus(true).State);
    }

    [Fact]
    public async Task CancellationAfterResolvedStop_FinishesReadbackAndOwnedAutoBeforePropagating()
    {
        using var cancellation = new CancellationTokenSource();
        var backend = new Backend { AfterSubmission = () => cancellation.Cancel() };
        var controller = new FanStopController(backend);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.SetModeAsync(FanStopMode.Stop, true, ManualIntent, () => true, cancellation.Token));
        Assert.False(controller.RequiresRecovery);
        Assert.Equal(FanStopState.Automatic, controller.GetStatus(true).State);
        Assert.Equal(["Stop", "Automatic"], backend.Calls.Where(value => value is "Stop" or "Automatic"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainSubmission_PreservesPreviouslyAcceptedToggle(bool alreadyStopped)
    {
        var backend = new Backend();
        var controller = new FanStopController(backend);
        if (alreadyStopped)
        {
            await Stop(controller);
        }
        backend.Failure = "uncertain";
        await controller.SetModeAsync(alreadyStopped ? FanStopMode.Automatic : FanStopMode.Stop,
            true, ManualIntent, () => true, default);
        Assert.Equal(FanStopState.RecoveryRequired, controller.GetStatus(true).State);
        Assert.Equal(alreadyStopped ? FanStopMode.Stop : FanStopMode.Automatic,
            controller.GetStatus(true).RequestedMode);
    }

    [Fact]
    public async Task SessionChangesDuringAdmission_RefusesBeforeSubmission()
    {
        var backend = new Backend();
        var controller = new FanStopController(backend);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await controller.SetModeAsync(FanStopMode.Stop, true, ManualIntent,
                () => backend.Calls.Count == 0, default)).Status);
        Assert.DoesNotContain("Stop", backend.Calls);
    }

    [Fact]
    public async Task OwnedStateDrift_FencesWithoutReplayingStopOrAuto()
    {
        var backend = new Backend();
        var controller = new FanStopController(backend);
        await Stop(controller);
        backend.Calls.Clear();
        backend.Observed = FanStopObservedState.Unknown;
        await controller.ReconcileAsync(false, () => true, default);
        Assert.True(controller.RequiresRecovery);
        Assert.DoesNotContain("Stop", backend.Calls);
        Assert.DoesNotContain("Automatic", backend.Calls);
    }

    private static Task<ControlOperationResult> Stop(FanStopController controller) =>
        controller.SetModeAsync(FanStopMode.Stop, true, ManualIntent, () => true, default);

    internal sealed class Backend : IFanStopBackend
    {
        public FanStopUnavailableReason UnavailableReason => FanStopUnavailableReason.None;
        public FanStopStartupDisposition StartupDisposition { get; init; } = FanStopStartupDisposition.Clear;
        public List<string> Calls { get; } = [];
        public FanStopAdmission Admission { get; set; } = new(true, true, true, true, true, true, true, true, true, true);
        public FanStopObservedState Observed { get; set; } = FanStopObservedState.Ordinary;
        public string? Failure { get; set; }
        public Action? DuringSubmission { get; init; }
        public Action? AfterSubmission { get; set; }
        public Action? DuringStateRead { get; init; }
        public Action? DuringAdmission { get; init; }
        public Task<FanStopAdmission> ReadAdmissionAsync(CancellationToken token)
        {
            Calls.Add("admission");
            DuringAdmission?.Invoke();
            return Task.FromResult(Admission);
        }
        public Task<FanStopObservedState> ReadStateAsync(CancellationToken token)
        {
            Calls.Add("read");
            DuringStateRead?.Invoke();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Observed);
        }
        public Task<FanStopSubmission> SubmitAsync(FanStopMode mode, CancellationToken token)
        {
            Calls.Add(mode.ToString());
            DuringSubmission?.Invoke();
            token.ThrowIfCancellationRequested();
            AfterSubmission?.Invoke();
            if (Failure == "exception")
            {
                throw new IOException("Simulated native failure.");
            }

            if (Failure == "notSubmitted")
            {
                return Task.FromResult(FanStopSubmission.NotSubmitted);
            }

            if (Failure != "readback")
            {
                Observed = mode == FanStopMode.Stop ? FanStopObservedState.Stopped : FanStopObservedState.Automatic;
            }

            return Task.FromResult(Failure == "uncertain" ? FanStopSubmission.Uncertain : FanStopSubmission.Submitted);
        }
    }
}
