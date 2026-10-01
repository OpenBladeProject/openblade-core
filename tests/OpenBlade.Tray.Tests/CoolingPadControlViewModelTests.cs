using OpenBlade.Contracts;
using OpenBlade.Testing;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class CoolingPadControlViewModelTests
{
    [Fact]
    public async Task VisibilityUsesConfirmedPresenceAndPreservesItOnlyWithinTheConnection()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        var stopped = Pad() with
        {
            Requested = CoolingPadControlSettings.Stopped,
            State = CoolingPadControlState.Stopped,
            Connection = CoolingPadConnectionState.Unavailable,
        };
        var unknown = Snapshot(stopped);
        view.Update(unknown, true);
        Assert.False(view.IsVisible);
        var present = unknown with { PadlessHyperBoostEligibility = PadlessHyperBoostEligibilityState.CoolingPadPresent };
        view.Update(present, true);
        Assert.True(view.IsVisible);
        view.Update(unknown, true);
        Assert.True(view.IsVisible);
        view.Update(present with
        {
            PadlessHyperBoostEligibility = PadlessHyperBoostEligibilityState.CoolingPadAbsent,
            CoolingPadControl = stopped with { Connection = CoolingPadConnectionState.PresentExact },
        }, true);
        Assert.False(view.IsVisible);
        view.Update(unknown, true);
        Assert.False(view.IsVisible);
        view.Update(present, true);
        Assert.True(view.IsVisible);
        view.Disconnect();
        Assert.False(view.IsVisible);
        view.Update(unknown, true);
        Assert.False(view.IsVisible);
        view.Update(Snapshot(stopped with { Connection = CoolingPadConnectionState.PresentExact }), true);
        Assert.True(view.IsVisible);
        view.Update(Snapshot(stopped with { Connection = CoolingPadConnectionState.Absent }), true);
        Assert.False(view.IsVisible);
    }

    [Fact]
    public async Task PresenceChangeInvalidatesPhysicalConfirmationWithUnchangedRestorationStatus()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        var status = Snapshot(Pad() with
        {
            Connection = CoolingPadConnectionState.Unavailable,
            Restoration = new(Guid.NewGuid(), CoolingPadRestorationStage.Medium, DateTimeOffset.UtcNow.AddMinutes(10)),
        }) with
        { PadlessHyperBoostEligibility = PadlessHyperBoostEligibilityState.CoolingPadPresent };
        view.Update(status, true);
        view.PhysicalConfirmation = true;
        Assert.True(view.RestoreQuietCommand.CanExecute(null));
        view.Update(status with { PadlessHyperBoostEligibility = PadlessHyperBoostEligibilityState.CoolingPadAbsent }, true);
        Assert.False(view.IsVisible);
        Assert.False(view.PhysicalConfirmation);
        view.Update(status, true);
        Assert.True(view.IsVisible);
        Assert.False(view.PhysicalConfirmation);
        Assert.False(view.RestoreQuietCommand.CanExecute(null));
    }

    [Fact]
    public async Task PresentationShowsOnlyRelevantActionsAndKeepsUncertainStateVisible()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.Update(Snapshot(Pad() with
        {
            Requested = CoolingPadControlSettings.Stopped,
            State = CoolingPadControlState.Stopped,
            PauseReason = CoolingPadControlPauseReason.None,
            ResumeRequired = false,
        }), true);
        Assert.Equal("Updates stopped", view.SummaryText);
        Assert.False(view.IsResumeVisible);
        Assert.False(view.IsRestorationVisible);
        Assert.False(view.IsNoticeVisible);

        view.Update(Snapshot(Pad()), true);
        Assert.True(view.IsResumeVisible);
        view.Update(Snapshot(Pad() with { State = CoolingPadControlState.Running }), true);
        Assert.False(view.IsResumeVisible);
        view.Update(Snapshot(Pad() with
        {
            State = CoolingPadControlState.Running,
            RetainedEffect = true,
            RetainedSessionResumable = false,
        }), true);
        Assert.False(view.IsNoticeVisible);
        view.Update(Snapshot(Pad() with
        {
            State = CoolingPadControlState.Stopped,
            ResumeRequired = false,
            RetainedEffect = true,
            RetainedSessionResumable = true,
            QuietRestorationAvailable = true,
        }), true);
        Assert.True(view.IsResumeVisible);
        Assert.True(view.ResumeCommand.CanExecute(null));

        view.Update(Snapshot(Pad() with { RetainedEffect = true, RetainedSessionResumable = false }), true);
        Assert.True(view.IsNoticeVisible);
        Assert.Contains("attended resolution", view.NoticeText);
        Assert.False(view.IsResumeVisible);
        Assert.False(view.IsRestorationVisible);

        view.Update(Snapshot(Pad() with { SubmissionStateUnknown = true, QuietRestorationAvailable = true }), true);
        Assert.True(view.IsNoticeVisible);
        Assert.Contains("outcome is unknown", view.NoticeText);
        Assert.False(view.CanConfirmPhysical);
        Assert.False(view.IsRestorationVisible);
        view.Disconnect();
        Assert.True(view.IsNoticeVisible);
        Assert.Equal("Disconnected", view.SummaryText);
    }

    [Theory]
    [InlineData(CoolingPadRestorationStage.Medium, "Confirm Medium")]
    [InlineData(CoolingPadRestorationStage.Quiet, "Confirm quiet")]
    [InlineData(CoolingPadRestorationStage.Final, "Finish restoration")]
    public async Task RestorationPresentationTracksFreshStageWithoutAdmittingUncheckedConfirmation(
        CoolingPadRestorationStage stage, string action)
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        var notified = new List<string?>();
        view.PropertyChanged += (_, args) => notified.Add(args.PropertyName);
        view.Update(Snapshot(Pad() with { QuietRestorationAvailable = true }), true);
        Assert.False(view.IsRestorationVisible);
        view.IsRestorationGuidanceOpen = true;
        Assert.True(view.IsRestorationVisible);
        Assert.False(view.HasRestorationStage);
        Assert.Equal("Restore quiet", view.RestoreButtonText);
        view.PhysicalConfirmation = true;
        view.Update(Snapshot(Pad() with
        {
            Restoration = new(Guid.NewGuid(), stage, DateTimeOffset.UtcNow.AddMinutes(10)),
        }), true);
        Assert.True(view.HasRestorationStage);
        Assert.True(view.IsRestorationVisible);
        Assert.False(view.IsRestorationGuidanceOpen);
        Assert.Equal(action, view.RestoreButtonText);
        Assert.False(view.PhysicalConfirmation);
        Assert.False(view.RestoreQuietCommand.CanExecute(null));
        Assert.Contains(nameof(view.RestoreButtonText), notified);
        Assert.Contains(nameof(view.IsRestorationVisible), notified);
    }

    [Fact]
    public async Task HealthyRunningRestorationDisclosureIsLocalAndStagesStayVisible()
    {
        var client = new Client();
        await using var view = new CoolingPadControlViewModel(client, default);
        var healthy = Snapshot(Pad() with
        {
            State = CoolingPadControlState.Running,
            PauseReason = CoolingPadControlPauseReason.None,
            ResumeRequired = false,
            RetainedEffect = true,
            RetainedSessionResumable = false,
            QuietRestorationAvailable = true,
        }) with
        { PadlessHyperBoostEligibility = PadlessHyperBoostEligibilityState.CoolingPadPresent };
        view.Update(healthy, true);
        Assert.True(view.IsRestorationDisclosureVisible);
        Assert.False(view.IsRestorationVisible);
        Assert.False(view.IsNoticeVisible);
        view.IsRestorationGuidanceOpen = true;
        Assert.True(view.IsRestorationVisible);
        Assert.Equal(0, client.Mutations);
        Assert.Equal(0, client.Refreshes);
        view.PhysicalConfirmation = true;
        view.IsRestorationGuidanceOpen = false;
        Assert.False(view.PhysicalConfirmation);
        Assert.False(view.RestoreQuietCommand.CanExecute(null));
        view.IsRestorationGuidanceOpen = true;
        view.PhysicalConfirmation = true;
        view.Update(healthy with { CoolingPadControl = healthy.CoolingPadControl! with { QuietRestorationAvailable = false } }, true);
        Assert.False(view.IsRestorationGuidanceOpen);
        Assert.False(view.IsRestorationVisible);
        Assert.False(view.PhysicalConfirmation);
        view.Update(healthy, true);
        view.IsRestorationGuidanceOpen = true;
        view.PhysicalConfirmation = true;
        view.Update(healthy with
        {
            CoolingPadControl = healthy.CoolingPadControl! with
            {
                Restoration = new(Guid.NewGuid(), CoolingPadRestorationStage.Medium, DateTimeOffset.UtcNow.AddMinutes(10)),
            },
        }, true);
        Assert.True(view.IsRestorationVisible);
        Assert.False(view.IsRestorationDisclosureVisible);
        Assert.False(view.IsRestorationGuidanceOpen);
        Assert.False(view.PhysicalConfirmation);
        view.IsRestorationGuidanceOpen = false;
        Assert.True(view.IsRestorationVisible);
        Assert.Equal(0, client.Mutations);
        Assert.Equal(0, client.Refreshes);
        view.Disconnect();
        Assert.False(view.IsRestorationGuidanceOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialConfirmationResetsWhenPhysicalPremiseChanges(bool retainedEffect)
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        var pad = Pad() with { QuietRestorationAvailable = true };
        view.Update(Snapshot(pad), true);
        view.PhysicalConfirmation = true;
        view.Update(Snapshot(retainedEffect ? pad with { RetainedEffect = true } : pad with { LastAcknowledgedDemand = new(2200) }), true);
        Assert.True(view.CanConfirmPhysical);
        Assert.False(view.PhysicalConfirmation);
        Assert.False(view.RestoreQuietCommand.CanExecute(null));
    }

    [Fact]
    public async Task NewlyAvailableInitialPromptRequiresFreshConfirmationButUnchangedRefreshPreservesIt()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.Update(Snapshot(Pad()), true);
        Assert.False(view.CanConfirmPhysical);
        view.PhysicalConfirmation = true; // A stale binding or previous draft cannot pre-confirm a new prompt.
        view.Update(Snapshot(Pad() with { QuietRestorationAvailable = true }), true);
        Assert.True(view.CanConfirmPhysical);
        Assert.False(view.PhysicalConfirmation);
        Assert.False(view.RestoreQuietCommand.CanExecute(null));
        view.PhysicalConfirmation = true;
        view.Update(Snapshot(Pad() with { QuietRestorationAvailable = true, PlannedDemand = new(1600) }), true);
        Assert.True(view.PhysicalConfirmation);
        Assert.True(view.RestoreQuietCommand.CanExecute(null));
        view.Update(Snapshot(Pad()), true);
        Assert.False(view.PhysicalConfirmation);
        Assert.False(view.CanConfirmPhysical);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedStageCannotCarryConfirmationAcrossDisconnectOrUnknownSubmission(bool unknown)
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        var pad = Pad() with { Restoration = new(Guid.NewGuid(), CoolingPadRestorationStage.Medium, DateTimeOffset.UtcNow.AddMinutes(10)) };
        view.Update(Snapshot(pad), true);
        view.PhysicalConfirmation = true;
        view.Update(Snapshot(pad with { SubmissionStateUnknown = unknown }), unknown);
        Assert.False(view.PhysicalConfirmation);
        Assert.False(view.CanConfirmPhysical);
        view.Update(Snapshot(pad), true);
        Assert.True(view.CanConfirmPhysical);
        Assert.False(view.PhysicalConfirmation);
    }

    [Fact]
    public async Task RestorationPhysicalConfirmationIsNeverCarriedAcrossStagesOrReconnect()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        var prompt = new CoolingPadRestorationPrompt(Guid.NewGuid(), CoolingPadRestorationStage.Medium, DateTimeOffset.UtcNow.AddMinutes(10));
        view.Update(Snapshot(Pad() with { Restoration = prompt }), true);
        Assert.False(view.RestoreQuietCommand.CanExecute(null));
        view.PhysicalConfirmation = true;
        Assert.True(view.RestoreQuietCommand.CanExecute(null));
        view.Update(Snapshot(Pad() with { Restoration = prompt with { Stage = CoolingPadRestorationStage.Quiet } }), true);
        Assert.False(view.PhysicalConfirmation);
        view.PhysicalConfirmation = true;
        view.Disconnect();
        Assert.False(view.PhysicalConfirmation);
        Assert.False(view.RestoreQuietCommand.CanExecute(null));
    }

    [Fact]
    public async Task KnownRetainedWithoutLiveSessionDoesNotOfferResumeOrQuietRestoration()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.Update(Snapshot(Pad() with { RetainedEffect = true, RetainedSessionResumable = false }), true);
        Assert.False(view.ResumeCommand.CanExecute(null));
        Assert.False(view.RestoreQuietCommand.CanExecute(null));
        Assert.Contains("attended resolution", view.RestorationText);
    }

    [Fact]
    public async Task HostCurveRequestPlannedDemandAndHistoricalAcknowledgementHaveSeparateLabels()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.SelectedMode = view.ModeOptions.Single(p => p.Mode == CoolingPadControlMode.HostCurve);
        view.Update(Snapshot(Pad() with
        {
            Requested = view.CreateSettings(),
            PlannedDemand = new(1350),
            LastAcknowledgedDemand = new(1600),
        }), true);
        Assert.Contains("Temperature curve", view.RequestedText);
        Assert.DoesNotContain("1350", view.RequestedText);
        Assert.Contains("1350", view.PlannedText);
        Assert.Contains("1600", view.AcknowledgedText);
    }

    [Fact]
    public async Task ReconnectAcceptsNewServiceRevisionEpoch()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.Update(Snapshot(Pad()) with { Revision = 100 }, true);
        view.Disconnect();
        view.Update(Snapshot(Pad() with { LastAcknowledgedDemand = new(2200) }) with { Revision = 1 }, true);
        Assert.True(view.IsConnected);
        Assert.Contains("2200", view.AcknowledgedText);
        Assert.True(view.ResumeCommand.CanExecute(null));
    }

    [Fact]
    public async Task EditorBuildsIndependentImmutableCurvesAndRejectsInvalidDraft()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        Assert.Equal(CoolingPadControlSettings.Stopped, view.CreateSettings());
        view.SelectedMode = view.ModeOptions.Single(p => p.Mode == CoolingPadControlMode.HostCurve);
        var settings = view.CreateSettings();
        Assert.Equal(40, settings.CpuCurve!.Points[0].TemperatureCelsius);
        Assert.Equal(30, settings.GpuCurve!.Points[0].TemperatureCelsius);
        view.CpuPoints[0].Demand = 1500;
        Assert.Equal(1000, settings.CpuCurve.Points[0].Demand.Rpm);
        Assert.Equal(1000, view.CreateSettings().GpuCurve!.Points[0].Demand.Rpm);
        view.GpuPoints[1].Temperature = 20;
        Assert.False(view.IsEditorValid);
        Assert.Throws<ArgumentException>(() => view.CreateSettings());
    }

    [Theory]
    [InlineData(1500, true)]
    [InlineData(1510, false)]
    [InlineData(0, false)]
    public async Task FixedEditorUsesConfiguredDemandValidation(int demand, bool valid)
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.SelectedMode = view.ModeOptions.Single(p => p.Mode == CoolingPadControlMode.FixedDemand);
        view.FixedDemand = demand;
        Assert.Equal(valid, view.IsEditorValid);
    }

    [Fact]
    public async Task ResumeRequiresWritableConnectedKnownIntent()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.Update(Snapshot(Pad() with { Access = DeviceFeatureAccess.ObservedOnly }), true);
        Assert.False(view.ResumeCommand.CanExecute(null));
        view.Update(Snapshot(Pad() with { SubmissionStateUnknown = true }), true);
        Assert.False(view.ResumeCommand.CanExecute(null));
        view.Update(Snapshot(Pad() with { Requested = CoolingPadControlSettings.Stopped }), true);
        Assert.False(view.ResumeCommand.CanExecute(null));
        view.Update(Snapshot(Pad()), true);
        Assert.True(view.ResumeCommand.CanExecute(null));
        view.Disconnect();
        Assert.False(view.ResumeCommand.CanExecute(null));
        Assert.False(view.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task RequestedDemandDoesNotReplaceHistoricalAcknowledgement()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.Update(Snapshot(Pad()), true);
        Assert.Contains("1600", view.RequestedText);
        Assert.Contains("1350", view.AcknowledgedText);
        view.Update(Snapshot(Pad() with { LastAcknowledgedDemand = null }), true);
        Assert.DoesNotContain("1600", view.AcknowledgedText);
        Assert.DoesNotContain("1500", view.AcknowledgedText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMutationIsNotRetriedOrRefreshed(bool resume)
    {
        var client = new Client { Mutation = _ => Task.FromException<ControlOperationResult>(new IOException("lost response")) };
        await using var view = new CoolingPadControlViewModel(client, default);
        view.Update(Snapshot(Pad()), true);
        await (resume ? view.ResumeCommand : view.ApplyCommand).ExecuteAsync(null);
        Assert.Equal(1, client.Mutations);
        Assert.Equal(0, client.Refreshes);
        Assert.NotEmpty(view.Message);
    }

    [Fact]
    public async Task StoredMutationRefreshesAuthoritativeStatusWithoutInferringAcknowledgement()
    {
        var client = new Client { Status = Snapshot(Pad() with { LastAcknowledgedDemand = null }) };
        await using var view = new CoolingPadControlViewModel(client, default);
        view.Update(Snapshot(Pad()), true);
        view.SelectedMode = view.ModeOptions.Single(p => p.Mode == CoolingPadControlMode.FixedDemand);
        view.FixedDemand = 2200;
        await view.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(2200, client.LastSettings!.FixedDemand!.Value.Rpm);
        Assert.Equal(1, client.Mutations);
        Assert.Equal(1, client.Refreshes);
        Assert.Contains("1600", view.RequestedText);
        Assert.DoesNotContain("2200", view.AcknowledgedText);
        Assert.Equal(1600, view.FixedDemand);
    }

    [Fact]
    public async Task CleanEditorHydratesRunningIntentAndRemoteChanges()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.Update(Snapshot(Pad() with { State = CoolingPadControlState.Running }), true);
        Assert.True(view.IsFixed);
        Assert.Equal(1600, view.FixedDemand);
        var curves = new CoolingPadControlSettings(CoolingPadControlMode.HostCurve,
            cpuCurve: new([new(45, new(1100)), new(85, new(2100)), new(100, new(2300))]),
            gpuCurve: new([new(35, new(1200)), new(75, new(2200))]));
        view.Update(Snapshot(Pad() with { Requested = curves, State = CoolingPadControlState.Running }) with { Revision = 2 }, true);
        Assert.True(view.IsCurve);
        Assert.Equal(curves, view.CreateSettings());
        Assert.Equal(3, view.CpuPoints.Count);
        view.CpuPoints[0].Demand = 1300;
        view.Update(Snapshot(Pad()) with { Revision = 3 }, true);
        Assert.True(view.IsCurve);
        Assert.Equal(1300, view.CpuPoints[0].Demand);
    }

    [Fact]
    public async Task FirstRunningCurveHydratesWithoutManualSelection()
    {
        var requested = new CoolingPadControlSettings(CoolingPadControlMode.HostCurve,
            cpuCurve: new([new(42, new(1100)), new(88, new(2200))]),
            gpuCurve: new([new(32, new(1200)), new(78, new(2300))]));
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.Update(Snapshot(Pad() with { Requested = requested, State = CoolingPadControlState.Running }), true);
        Assert.Equal(requested, view.CreateSettings());
    }

    [Fact]
    public async Task PendingDraftSurvivesStatusTicksAndFailedApply()
    {
        var client = new Client { Mutation = _ => Task.FromResult(new ControlOperationResult(ControlOperationStatus.Blocked, "Blocked")) };
        await using var view = new CoolingPadControlViewModel(client, default);
        view.Update(Snapshot(Pad()), true);
        view.FixedDemand = 2200;
        view.Update(Snapshot(Pad() with { LastAcknowledgedDemand = new(1500) }) with { Revision = 2 }, true);
        await view.ApplyCommand.ExecuteAsync(null);
        view.Update(Snapshot(Pad()) with { Revision = 3 }, true);
        Assert.Equal(2200, view.FixedDemand);
    }

    [Fact]
    public async Task EditsMadeDuringSuccessfulApplyRemainPending()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ControlOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Mutation = _ => { entered.SetResult(); return release.Task; } };
        await using var view = new CoolingPadControlViewModel(client, default);
        view.Update(Snapshot(Pad()), true);
        view.FixedDemand = 2200;
        Task applying = view.ApplyCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.FixedDemand = 2300;
        release.SetResult(new(ControlOperationStatus.Applied, "Applied"));
        await applying.WaitAsync(TimeSpan.FromSeconds(5));
        view.Update(Snapshot(Pad()) with { Revision = 2 }, true);
        Assert.Equal(2200, client.LastSettings!.FixedDemand!.Value.Rpm);
        Assert.Equal(2300, view.FixedDemand);
    }

    [Theory]
    [InlineData(ControlOperationStatus.Stored)]
    [InlineData(ControlOperationStatus.Applied)]
    public async Task SuccessfulApplyReleasesUnchangedDraftOnlyAfterFreshStatus(ControlOperationStatus outcome)
    {
        var client = new Client { Mutation = _ => Task.FromResult(new ControlOperationResult(outcome, "Accepted")) };
        await using var view = new CoolingPadControlViewModel(client, default);
        view.Update(Snapshot(Pad()), true);
        view.FixedDemand = 2200;
        await view.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(1600, view.FixedDemand);
        view.Update(Snapshot(Pad() with { Requested = new(CoolingPadControlMode.FixedDemand, new(2100)) }) with { Revision = 2 }, true);
        Assert.Equal(2100, view.FixedDemand);
    }

    [Fact]
    public async Task StaleSuccessfulApplyRefreshDoesNotReleaseDraft()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.Update(Snapshot(Pad()) with { Revision = 2 }, true);
        view.FixedDemand = 2200;
        await view.ApplyCommand.ExecuteAsync(null);
        view.Update(Snapshot(Pad()) with { Revision = 3 }, true);
        Assert.Equal(2200, view.FixedDemand);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAndResumeCannotOverlap(bool resumeFirst)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ControlOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Mutation = _ => { entered.TrySetResult(); return release.Task; } };
        await using var view = new CoolingPadControlViewModel(client, default);
        view.Update(Snapshot(Pad()), true);
        var first = resumeFirst ? view.ResumeCommand : view.ApplyCommand;
        var second = resumeFirst ? view.ApplyCommand : view.ResumeCommand;
        Task running = first.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool secondEnabled = second.CanExecute(null);
        Task overlapping = second.ExecuteAsync(null);
        release.SetResult(new(ControlOperationStatus.Stored, "Stored"));
        await Task.WhenAll(running, overlapping).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondEnabled);
        Assert.Equal(1, client.Mutations);
    }

    [Fact]
    public async Task LateRefreshCannotUndoDisconnect()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ServiceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Refresh = _ => { entered.TrySetResult(); return release.Task; } };
        await using var view = new CoolingPadControlViewModel(client, default);
        view.Update(Snapshot(Pad()), true);
        Task running = view.ApplyCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.Disconnect();
        release.SetResult(Snapshot(Pad()));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(view.IsConnected);
        Assert.False(view.ResumeCommand.CanExecute(null));
    }

    [Fact]
    public async Task OlderCommandRefreshCannotReplaceNewerWatchStatus()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ServiceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Refresh = _ => { entered.TrySetResult(); return release.Task; } };
        await using var view = new CoolingPadControlViewModel(client, default);
        view.Update(Snapshot(Pad()), true);
        Task running = view.ApplyCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.Update(Snapshot(Pad() with { LastAcknowledgedDemand = new(2200) }) with { Revision = 2 }, true);
        release.SetResult(Snapshot(Pad()));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("2200", view.AcknowledgedText);
    }

    private static CoolingPadControlStatus Pad() => new(
        new(CoolingPadControlMode.FixedDemand, new(1600)), CoolingPadControlState.Paused,
        CoolingPadControlPauseReason.ExplicitResumeRequired, new(1500), new(1350))
    { Access = DeviceFeatureAccess.ReadWrite, ResumeRequired = true };

    private static ServiceStatusSnapshot Snapshot(CoolingPadControlStatus pad)
    {
        var profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(1, false, true, [], null, [], "Test",
            DeviceCapabilities.Unavailable(), null, profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn, null, null, null, null, false)
        { CoolingPadControl = pad };
    }

    private sealed class Client : UnavailableServiceClient, IServiceClient
    {
        public int Mutations { get; private set; }
        public int Refreshes { get; private set; }
        public CoolingPadControlSettings? LastSettings { get; private set; }
        public ServiceStatusSnapshot Status { get; init; } = Snapshot(Pad());
        public Func<CancellationToken, Task<ControlOperationResult>> Mutation { get; init; } =
            _ => Task.FromResult(new ControlOperationResult(ControlOperationStatus.Stored, "Stored"));
        public Func<CancellationToken, Task<ServiceStatusSnapshot>>? Refresh { get; init; }

        public Task<ControlOperationResult> SetCoolingPadControlAsync(CoolingPadControlSettings settings, CancellationToken token)
        {
            LastSettings = settings;
            Mutations++;
            return Mutation(token);
        }

        public Task<ControlOperationResult> ResumeCoolingPadControlAsync(CancellationToken token)
        {
            Mutations++;
            return Mutation(token);
        }

        public override Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token)
        {
            Refreshes++;
            return Refresh?.Invoke(token) ?? Task.FromResult(Status);
        }
    }
}
