using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapWindowsHookLoopTests
{
    [Fact]
    public void NativeStructuresMatchWindowsAbi()
    {
        Assert.Equal(IntPtr.Size == 8 ? (24, 48, 40) : (20, 32, 28),
            SnapTapWindowsHookLoop.NativeStructureSizes);
    }

    [Fact]
    public void ExactReviewedHostNeedsNoDriverPackageOrCollection()
    {
        foreach (RazerSnapTapDriverProfile profile in RazerSnapTapDriverProfiles.All)
        {
            var snapshot = new SnapTapWindowsHookLoop.AdmissionSnapshot(
                "Razer", profile.ProductName, "Razer", profile.ProductName, true, true, false);
            Assert.Null(SnapTapWindowsHookLoop.AdmissionRejection(snapshot));
            Assert.Equal("UnsupportedModel", SnapTapWindowsHookLoop.AdmissionRejection(
                snapshot with { SystemProduct = profile.ProductName + " variant" }));
            Assert.Equal("UnsupportedModel", SnapTapWindowsHookLoop.AdmissionRejection(
                snapshot with { BiosManufacturer = "razer" }));
            Assert.Equal("InactiveConsoleSession", SnapTapWindowsHookLoop.AdmissionRejection(
                snapshot with { ActiveConsole = false }));
            Assert.Equal("IncompleteControllerObservation", SnapTapWindowsHookLoop.AdmissionRejection(
                snapshot with { ControllersComplete = false }));
            Assert.Equal("CompetingController", SnapTapWindowsHookLoop.AdmissionRejection(
                snapshot with { CompetingController = true }));
        }
    }

    [Theory]
    [InlineData(1)] // mouse
    [InlineData(0x10)] // shift
    [InlineData(0x41)]
    [InlineData(0xFF)]
    public void InitialReleaseGateIncludesAllKeysAndMouse(int held)
    {
        Assert.False(SnapTapWindowsHookLoop.AllReleased(key => key == held ? unchecked((short)0x8000) : (short)0));
        Assert.True(SnapTapWindowsHookLoop.AllReleased(_ => 1)); // transition bit is not a hold
    }

    [Fact]
    public void WaitingForReleaseTracksPairBeforeArming()
    {
        var state = new SnapTapWindowsHookLoop.InputState(_ => throw new InvalidOperationException());
        state.Reconcile(1, 2, false);
        Assert.Equal(SnapTapRuntimeState.WaitingForRelease, state.Status);
        Assert.False(state.Handle(0x1E, 0, 0x100, 1, 2).Handled);
        state.Reconcile(1, 2, true);
        Assert.Equal(SnapTapRuntimeState.WaitingForRelease, state.Status);
        Assert.False(state.Handle(0x1E, 0, 0x101, 0, 0).Handled);
        state.Reconcile(1, 2, true);
        Assert.Equal(SnapTapRuntimeState.Armed, state.Status);
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x02)]
    [InlineData(0x12)]
    [InlineData(0x01)]
    public void InjectedAndExtendedKeysDoNotChangePhysicalState(uint flags)
    {
        var state = new SnapTapWindowsHookLoop.InputState(_ => throw new InvalidOperationException());
        state.Reconcile(1, 2, true);
        Assert.False(state.Handle(0x1E, flags, 0x100, 1, 2).Handled);
        Assert.Equal((ushort)0, state.Winner);
        Assert.Equal(SnapTapRuntimeState.Armed, state.Status);
        Assert.False(state.Faulted);
    }

    [Fact]
    public void WindowsPathForwardsPhysicalPressesAndRestoresPreviousWinner()
    {
        List<SnapTapOutputBatch> batches = [];
        var state = new SnapTapWindowsHookLoop.InputState(batch => { batches.Add(batch); return batch.Count; });
        state.Reconcile(1, 2, true);
        Assert.Equal(new SnapTapInputResult(true, false), state.Handle(0x1E, 0, 0x100, 1, 2));
        Assert.Empty(batches); // physical down is passed through, not reinjected
        Assert.Equal(new SnapTapInputResult(true, false), state.Handle(0x20, 0, 0x100, 1, 2));
        Assert.Equal(new SnapTapKeyStroke(0x1E, false), Assert.Single(batches).First);
        Assert.True(state.Handle(0x1E, 0, 0x100, 1, 2).Suppress); // losing repeat
        Assert.True(state.Handle(0x20, 0, 0x101, 1, 2).Suppress);
        Assert.Equal(new SnapTapOutputBatch(new(0x20, false), new(0x1E, true), 2), batches[1]);
        Assert.Equal((ushort)0x1E, state.Winner);
        Assert.Equal(SnapTapRuntimeState.Active, state.Status);
        state.Stop(1, 2, true);
        Assert.Equal(new SnapTapKeyStroke(0x1E, false), batches[2].First);
        Assert.False(state.Faulted);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(1, 3)]
    [InlineData(0, 0)]
    public void HeldWinnerContextLossStopsWithoutSendingIntoNewFocus(int window, int layout)
    {
        int sent = 0;
        var state = new SnapTapWindowsHookLoop.InputState(batch => { sent++; return batch.Count; });
        state.Reconcile(1, 2, true);
        state.Handle(0x1E, 0, 0x100, 1, 2);
        state.Reconcile(window, layout, false);
        Assert.True(state.Faulted);
        Assert.True(state.Failure?.MayHaveSubmitted);
        Assert.Equal("ForegroundOrLayoutChanged", state.Failure?.Reason);
        state.Stop(window, layout, true);
        Assert.Equal(0, sent);
    }

    [Fact]
    public void CleanFocusChangeRearmsOnlyAtConfirmedRelease()
    {
        var state = new SnapTapWindowsHookLoop.InputState(batch => batch.Count);
        state.Reconcile(1, 2, true);
        state.Reconcile(3, 4, false);
        Assert.Equal(SnapTapRuntimeState.WaitingForRelease, state.Status);
        state.Reconcile(3, 4, true);
        Assert.Equal(SnapTapRuntimeState.Armed, state.Status);
        Assert.False(state.Faulted);
    }

    [Fact]
    public void PartialSendPoisonsWithoutReplayOrCleanup()
    {
        int calls = 0;
        var state = new SnapTapWindowsHookLoop.InputState(batch => ++calls == 1 ? batch.Count : 1);
        state.Reconcile(1, 2, true);
        state.Handle(0x1E, 0, 0x100, 1, 2);
        state.Handle(0x20, 0, 0x100, 1, 2);
        state.Handle(0x20, 0, 0x101, 1, 2); // one of two inputs accepted
        Assert.True(state.Faulted);
        Assert.Equal("OutputCountMismatch", state.Failure?.Reason);
        Assert.True(state.Failure?.MayHaveSubmitted);
        state.Handle(0x1E, 0, 0x101, 1, 2);
        state.Repeat();
        state.Stop(1, 2, true);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedReleaseSuppressesNewPhysicalDownAndRetainsFailure(bool throws)
    {
        int calls = 0;
        var state = new SnapTapWindowsHookLoop.InputState(_ =>
        {
            calls++;
            if (throws) { throw new InvalidOperationException("uncertain native result"); }
            return 0;
        });
        state.Reconcile(1, 2, true);
        state.Handle(0x1E, 0, 0x100, 1, 2);
        SnapTapInputResult result = state.Handle(0x20, 0, 0x100, 1, 2);
        Assert.True(result.Suppress);
        Assert.False(result.Handled);
        Assert.True(state.Faulted);
        Assert.Equal(throws ? "OutputFailed" : "OutputCountMismatch", state.Failure?.Reason);
        Assert.True(state.Failure?.MayHaveSubmitted);
        state.Stop(1, 2, true);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AdmissionLossWithHeldWinnerDoesNotGuessCleanup()
    {
        int calls = 0;
        var state = new SnapTapWindowsHookLoop.InputState(batch => { calls++; return batch.Count; });
        state.Reconcile(1, 2, true);
        state.Handle(0x1E, 0, 0x100, 1, 2);
        state.Stop(1, 2, false);
        Assert.True(state.Faulted);
        Assert.Equal("HeldOutputUncertain", state.Failure?.Reason);
        Assert.True(state.Failure?.MayHaveSubmitted);
        Assert.Equal(0, calls);
    }
}
