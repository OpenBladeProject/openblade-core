using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class DriverKeyboardOutputTests
{
    private static readonly SnapTapKeyStroke ADown = new(0x1E, true);
    private static readonly SnapTapKeyStroke AUp = new(0x1E, false);
    private static readonly SnapTapKeyStroke DDown = new(0x20, true);
    private const RazerBladeInputDeviceKind Device = RazerBladeInputDeviceKind.Rz09_0581;

    [Fact]
    public void ARefusedReleaseIsSentAgainBeforeTheStopCountsAsClean()
    {
        var rig = new Rig();
        Assert.True(rig.Snap.Handle(ADown));
        rig.Decision = DriverOutputDecision.StopRequested;
        // The user lets go of A during the stall; the stop refuses the forwarded up.
        Assert.False(rig.Snap.Handle(AUp));
        Assert.True(rig.Output.SnapStopRefused);
        Assert.Equal(1, rig.Output.DeliveredCount);

        StopRefusalExit exit = rig.Exit();
        Assert.True(exit.MayRelease);
        rig.Decision = DriverOutputDecision.Allowed;
        Assert.True(rig.Output.TryReleaseDelivered());

        Assert.Empty(rig.HeldInWindows);
        Assert.True(exit.IsClean(keysReleased: true, closed: true, functionStopped: true));
    }

    [Fact]
    public void ARefusedPriorityChangeReleasesTheEarlierWinner()
    {
        var rig = new Rig();
        Assert.True(rig.Snap.Handle(ADown));
        rig.Decision = DriverOutputDecision.StopRequested;
        Assert.False(rig.Snap.Handle(DDown));

        rig.Decision = DriverOutputDecision.Allowed;
        Assert.True(rig.Exit().MayRelease);
        Assert.True(rig.Output.TryReleaseDelivered());
        Assert.Empty(rig.HeldInWindows);
    }

    [Fact]
    public void ARefusedFirstPressNeedsNoRelease()
    {
        var rig = new Rig();
        rig.Decision = DriverOutputDecision.StopRequested;
        Assert.False(rig.Snap.Handle(ADown));

        Assert.True(rig.Exit().MayRelease);
        rig.Decision = DriverOutputDecision.Allowed;
        Assert.True(rig.Output.TryReleaseDelivered());
        Assert.Empty(rig.Submitted);
    }

    [Fact]
    public void AFailedOrRefusedReleaseKeepsTheStopFailed()
    {
        var throwing = new Rig();
        Assert.True(throwing.Snap.Handle(ADown));
        throwing.Decision = DriverOutputDecision.StopRequested;
        Assert.False(throwing.Snap.Handle(AUp));
        throwing.Decision = DriverOutputDecision.Allowed;
        throwing.FailSubmit = true;
        Assert.False(throwing.Output.TryReleaseDelivered());
        Assert.True(throwing.Output.ReleaseUncertain);
        Assert.False(throwing.Exit().IsClean(keysReleased: false, closed: true, functionStopped: true));

        var refused = new Rig();
        Assert.True(refused.Snap.Handle(ADown));
        refused.Decision = DriverOutputDecision.StopRequested;
        Assert.False(refused.Snap.Handle(AUp));
        refused.Decision = DriverOutputDecision.TargetChanged;
        Assert.False(refused.Output.TryReleaseDelivered());
        Assert.False(refused.Output.ReleaseUncertain);
        Assert.Equal(1, refused.Output.DeliveredCount);
    }

    [Fact]
    public void ARefusalNotCausedByTheStopOrAfterASubmissionIsNeverReleased()
    {
        var focus = new Rig();
        Assert.True(focus.Snap.Handle(ADown));
        focus.Decision = DriverOutputDecision.TargetChanged;
        Assert.False(focus.Snap.Handle(AUp));
        Assert.False(focus.Output.SnapStopRefused);
        Assert.False(focus.Exit().MayRelease);

        // Releasing D restores the still-held A in one batch; its second stroke is
        // refused after the first was submitted.
        var partial = new Rig();
        Assert.True(partial.Snap.Handle(ADown));
        Assert.True(partial.Snap.Handle(DDown));
        partial.RefuseAfter = partial.Submitted.Count + 1;
        Assert.False(partial.Snap.Handle(new(0x20, false)));
        Assert.False(partial.Output.SnapStopRefused);
        Assert.True(partial.Snap.Failure?.MayHaveSubmitted);
        Assert.False(partial.Exit().MayRelease);
    }

    [Fact]
    public void AFunctionRowReleaseRefusedByTheStopIsReleasedAndStaysUncertainOtherwise()
    {
        var rig = new Rig();
        Assert.True(rig.Function.Handle(new(0x3F, true), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true));
        rig.Decision = DriverOutputDecision.StopRequested;
        Assert.False(rig.Function.Handle(new(0x3F, false), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true));
        // The processor keeps its own uncertainty; only the output knows nothing was submitted.
        Assert.True(rig.Function.OutputUncertain);
        Assert.True(rig.Output.FunctionStopRefused);

        StopRefusalExit exit = rig.Exit();
        Assert.True(exit.MayRelease);
        rig.Decision = DriverOutputDecision.Allowed;
        Assert.True(rig.Output.TryReleaseDelivered());
        Assert.Empty(rig.HeldInWindows);

        var focus = new Rig();
        Assert.True(focus.Function.Handle(new(0x3F, true), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true));
        focus.Decision = DriverOutputDecision.TargetChanged;
        Assert.False(focus.Function.Handle(new(0x3F, false), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true));
        Assert.True(focus.Function.OutputUncertain);
        Assert.False(focus.Exit().MayRelease);
    }

    [Fact]
    public void AnyOtherFailureKeepsTheStopFailed()
    {
        var rig = new Rig();
        Assert.True(rig.Snap.Handle(ADown));
        rig.Decision = DriverOutputDecision.StopRequested;
        Assert.False(rig.Snap.Handle(AUp));
        StopRefusalExit exit = rig.Exit();
        DiagnosticFailure other = SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Worker, "Run", "InputLoop", "ForegroundChanged", true);

        Assert.False((exit with { PublishFailed = true }).MayRelease);
        Assert.False((exit with { SessionFailure = other }).MayRelease);
        Assert.False((exit with { LoopFailure = other }).MayRelease);
        Assert.False((exit with { SnapFailure = null }).MayRelease);
        Assert.False((exit with { SnapFailure = exit.SnapFailure! with { MayHaveSubmitted = true } }).MayRelease);
        Assert.False((exit with { FunctionFaulted = true }).MayRelease);
        Assert.False((exit with { SnapStopRefused = false, FunctionStopRefused = true }).MayRelease);
        Assert.False(exit.IsClean(keysReleased: true, closed: false, functionStopped: true));
        Assert.False(exit.IsClean(keysReleased: true, closed: true, functionStopped: false));
        Assert.False(exit.IsClean(keysReleased: false, closed: true, functionStopped: true));
    }

    [Fact]
    public void ACombinedStopReleasesBothTheWinnerAndAForwardedFunctionKey()
    {
        var rig = new Rig();
        Assert.True(rig.Snap.Handle(ADown));
        Assert.True(rig.Function.Handle(new(0x3F, true), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true));
        rig.Decision = DriverOutputDecision.StopRequested;
        // The F5 repeat is refused; F5 itself was delivered, so it is released.
        Assert.False(rig.Function.Handle(new(0x3F, true), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true));

        StopRefusalExit exit = rig.Exit();
        Assert.True(exit.FunctionKeysSettled);
        Assert.True(exit.MayRelease);
        rig.Decision = DriverOutputDecision.Allowed;
        Assert.True(rig.Output.TryReleaseDelivered());
        Assert.Empty(rig.HeldInWindows);
    }

    [Fact]
    public void AHeldMediaKeyKeepsTheStopFailed()
    {
        var rig = new Rig();
        Assert.True(rig.Function.Handle(new(0x43, true), Device, false, FunctionKeyPrimaryMode.MultimediaKeys, true));
        Assert.Empty(rig.Submitted);
        rig.Decision = DriverOutputDecision.StopRequested;
        Assert.False(rig.Snap.Handle(ADown));

        StopRefusalExit exit = rig.Exit();
        Assert.False(exit.FunctionKeysSettled);
        Assert.False(exit.MayRelease);
    }

    [Fact]
    public void ARefusedFunctionPressIsTheOnlyUndeliveredKeyAllowed()
    {
        var rig = new Rig();
        rig.Decision = DriverOutputDecision.StopRequested;
        Assert.False(rig.Function.Handle(new(0x3F, true), Device, false, FunctionKeyPrimaryMode.FunctionKeys, true));
        Assert.Equal((ushort)0x3F, rig.Output.FunctionRefusedDown);
        Assert.True(rig.Exit().MayRelease);
    }

    [Fact]
    public void TheTransportsFinalRefusalCountsOnlyWhenTheStopStillHolds()
    {
        var stop = new Rig { DeferSubmitAs = DriverOutputDecision.StopRequested };
        Assert.False(stop.Snap.Handle(ADown));
        Assert.True(stop.Output.SnapStopRefused);
        Assert.False(stop.Snap.Failure?.MayHaveSubmitted);
        Assert.Equal(0, stop.Output.DeliveredCount);
        Assert.True(stop.Exit().MayRelease);

        // The decision flipped back, so the refusal cannot be attributed to the stop.
        var flicker = new Rig { DeferSubmitAs = DriverOutputDecision.Allowed };
        Assert.False(flicker.Snap.Handle(ADown));
        Assert.False(flicker.Output.SnapStopRefused);
        Assert.False(flicker.Exit().MayRelease);

        // A release refused by the transport's final check is not an uncertain submission.
        var release = new Rig();
        Assert.True(release.Snap.Handle(ADown));
        release.DeferSubmitAs = DriverOutputDecision.TargetChanged;
        Assert.False(release.Output.TryReleaseDelivered());
        Assert.False(release.Output.ReleaseUncertain);
        Assert.Equal(1, release.Output.DeliveredCount);
    }

    [Theory]
    [InlineData(7, 9, false, false, (int)DriverOutputDecision.Allowed)]
    [InlineData(7, 9, false, true, (int)DriverOutputDecision.StopRequested)]
    [InlineData(7, 9, true, true, (int)DriverOutputDecision.Allowed)]
    [InlineData(8, 9, false, true, (int)DriverOutputDecision.TargetChanged)]
    [InlineData(7, 0, false, false, (int)DriverOutputDecision.TargetChanged)]
    public void OutputDecisionReadsOneSnapshot(int window, int layout, bool stopping, bool stopRequested,
        int expected) =>
        Assert.Equal((DriverOutputDecision)expected, SnapTapDriverLoop.OutputDecision(7, 9, window, layout, stopping, stopRequested));

    private sealed class Rig
    {
        internal Rig()
        {
            Output = new DriverKeyboardOutput(() => Submitted.Count >= RefuseAfter ? DriverOutputDecision.StopRequested : Decision, stroke =>
            {
                if (FailSubmit) { throw new IOException("uncertain"); }
                if (DeferSubmitAs is { } late)
                {
                    // The transport's own final check refuses before DeviceIoControl.
                    Decision = late;
                    throw new KeyboardSubmissionDeferredException();
                }
                Submitted.Add(stroke);
                if (stroke.IsDown) { HeldInWindows.Add(stroke.ScanCode); }
                else { HeldInWindows.Remove(stroke.ScanCode); }
            });
            Snap = new SnapTapDriverInputProcessor(Output.SendBatch);
            Assert.True(Snap.Enable());
            Function = new FunctionRowDriverInputProcessor(Output.SendFunctionRow, _ => { }, () => { });
        }

        internal DriverOutputDecision Decision { get; set; } = DriverOutputDecision.Allowed;
        internal int RefuseAfter { get; set; } = int.MaxValue;
        internal bool FailSubmit { get; set; }
        internal DriverOutputDecision? DeferSubmitAs { get; set; }
        internal List<SnapTapKeyStroke> Submitted { get; } = [];
        internal HashSet<ushort> HeldInWindows { get; } = [];
        internal DriverKeyboardOutput Output { get; }
        internal SnapTapDriverInputProcessor Snap { get; }
        internal FunctionRowDriverInputProcessor Function { get; }

        internal StopRefusalExit Exit() => new(Output.SnapStopRefused, Output.FunctionStopRefused, false, null,
            Snap.Faulted, Snap.Failure, Function.Faulted, Function.Failure, Function.Failure ?? Snap.Failure,
            Function.TracksOnly(code => Output.IsDelivered(code) || code == Output.FunctionRefusedDown));
    }
}
