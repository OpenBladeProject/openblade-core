using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class HyperBoostManualFanRestorationReconcilerTests
{
    [Theory]
    [InlineData("Fans write is indeterminate: original diagnostic")]
    [InlineData("The device did not confirm submission.")]
    [InlineData("")]
    public async Task IndeterminateSubmission_ConfirmsManualReadbackRegardlessOfMessage(string message)
    {
        int reads = 0;
        ScopedApplyOutcome result = await Reconcile(
            CreateOutcome(message),
            _ =>
            {
                reads++;
                return Task.FromResult(State(FanMode.Fixed));
            });

        Assert.Equal(1, reads);
        Assert.Equal(ControlResultStatus.Applied, result.Result.Status);
        Assert.True(result.ReadbackMatched);
        Assert.Equal(FanMode.Fixed, result.Readback!.Fans);
    }

    [Theory]
    [InlineData(WriteSubmissionState.KnownNotApplied)]
    [InlineData(null)]
    public async Task DiagnosticText_CannotAdmitNonIndeterminateSubmission(WriteSubmissionState? state)
    {
        ScopedApplyOutcome original = CreateOutcome("Fans write is indeterminate") with
        {
            WriteSubmissionState = state,
        };
        ScopedApplyOutcome result = await Reconcile(
            original,
            _ => throw new InvalidOperationException("Readback must not be attempted."));

        Assert.Same(original, result);
    }

    [Fact]
    public async Task FailedReads_StopAfterTwoAttemptsAndOneDelay()
    {
        int reads = 0;
        List<TimeSpan> delays = [];
        ScopedApplyOutcome original = CreateOutcome("Unconfirmed submission");
        ScopedApplyOutcome result = await Reconcile(original, _ =>
        {
            reads++;
            throw new IOException("Unavailable");
        }, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        Assert.Same(original, result);
        Assert.Equal(2, reads);
        Assert.Equal([TimeSpan.FromMilliseconds(750)], delays);
    }

    [Fact]
    public async Task AutomaticReadback_PreservesFailureAndClearsPendingAttempt()
    {
        ScopedApplyOutcome result = await Reconcile(
            CreateOutcome("Unconfirmed submission"),
            _ => Task.FromResult(State(FanMode.Automatic)));

        Assert.Equal(ControlResultStatus.Failed, result.Result.Status);
        Assert.False(result.WriteAttempted);
        Assert.False(result.ReadbackMatched);
        Assert.Equal(FanMode.Automatic, result.Readback!.Fans);
    }

    [Fact]
    public async Task Cancellation_PreservesOriginalOutcomeWithoutRetry()
    {
        using CancellationTokenSource cancellation = new();
        ScopedApplyOutcome original = CreateOutcome("Unconfirmed submission");
        int reads = 0;
        ScopedApplyOutcome result = await Reconcile(original, _ =>
        {
            reads++;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }, token: cancellation.Token);

        Assert.Same(original, result);
        Assert.Equal(1, reads);
    }

    private static Task<ScopedApplyOutcome> Reconcile(
        ScopedApplyOutcome outcome,
        Func<CancellationToken, Task<ConfirmedDeviceState>> read,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken token = default) =>
        HyperBoostManualFanRestorationReconciler.ReconcileAsync(
            read,
            FanControlSettings.FromLegacy(FanMode.Automatic),
            FanControlSettings.FromLegacy(FanMode.Fixed),
            outcome,
            delay ?? ((_, _) => throw new InvalidOperationException("Unexpected retry.")),
            token);

    private static ConfirmedDeviceState State(FanMode mode) => new(null, mode, null, null);

    private static ScopedApplyOutcome CreateOutcome(string message) => new(
        new ControlResult(ControlResultStatus.Failed, message),
        Readback: null,
        WriteAttempted: true,
        ReadbackMatched: false,
        Capabilities: null,
        DeviceAccessFailed: false,
        WriteSubmissionState: WriteSubmissionState.Indeterminate);
}
