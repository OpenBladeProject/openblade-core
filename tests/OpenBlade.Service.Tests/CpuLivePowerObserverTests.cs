using System.Buffers.Binary;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuLivePowerObserverTests
{
    [Fact]
    public void PowerPhaseTimingsSeparateCompletedDurationsFromActiveElapsedAndFreeze()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        using var timing = new CpuPowerObservationTiming(() => elapsed);
        elapsed = TimeSpan.FromMilliseconds(125); timing.MoveTo(CpuPowerObservationPhase.FirstNative);
        elapsed = TimeSpan.FromMilliseconds(1025); timing.MoveTo(CpuPowerObservationPhase.RecoveryAdmission);
        elapsed = TimeSpan.FromMilliseconds(2025); timing.MoveTo(CpuPowerObservationPhase.ReplacementNative);
        elapsed = TimeSpan.FromMilliseconds(2725); timing.MoveTo(CpuPowerObservationPhase.PostRange);
        elapsed = TimeSpan.FromMilliseconds(2750); timing.MoveTo(CpuPowerObservationPhase.CompleteGetter);
        elapsed = TimeSpan.FromMilliseconds(2800);
        string frozen = timing.Snapshot(freeze: true);
        Assert.Equal("; power phases total 2800 ms: initial getter/context=125 ms completed, first native observation=900 ms completed, "
            + "recovery wait/admission=1000 ms completed, replacement native observation=700 ms completed, "
            + "post-range checks=25 ms completed, CompleteGetter=50 ms active", frozen);
        elapsed = TimeSpan.FromSeconds(20); timing.Dispose();
        timing.MoveTo(CpuPowerObservationPhase.FirstNative);
        Assert.Equal(frozen, timing.Snapshot());
    }

    [Fact]
    public void SuccessfulPowerPhaseSummaryIsPublishedOnceAndIndependentOfCurveProgress()
    {
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        diagnostic.BeginNativeQuery();
        var timing = diagnostic.BeginPowerObservation()!;
        timing.MoveTo(CpuPowerObservationPhase.FirstNative);
        timing.MoveTo(CpuPowerObservationPhase.PostRange);
        timing.MoveTo(CpuPowerObservationPhase.CompleteGetter);
        diagnostic.CompletePowerObservation(timing);
        string summary = Assert.IsType<string>(diagnostic.PowerTimingSummary);
        Assert.StartsWith("; power phases total ", summary);
        Assert.Contains("CompleteGetter=", summary); Assert.DoesNotContain(" active", summary);
        Assert.DoesNotContain("recovery", summary); Assert.DoesNotContain("IOCTL", summary);
        Assert.Null(diagnostic.FirstFailure); Assert.Null(diagnostic.LiveObservationTimeoutContext); Assert.Null(diagnostic.LiveObservationReason);
        diagnostic.CompletePowerObservation(timing);
        Assert.Equal(summary, diagnostic.TakePowerTimingSummary());
        diagnostic.CompletePowerObservation(timing);
        Assert.Null(diagnostic.TakePowerTimingSummary());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservationCancellationReportsPowerAndRetainsFirstReasonAfterLateCompletion(bool callerCancelled)
    {
        var store = new CpuPowerTestData.MemoryStore(); var backend = new CpuPowerTestData.FakeBackend();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CpuLivePowerBackendObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken operationToken = default;
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        CpuPowerObservationTiming? timing = null;
        backend.ObserveResult = token =>
        {
            timing = diagnostic.BeginPowerObservation(); timing!.MoveTo(CpuPowerObservationPhase.FirstNative);
            operationToken = token; entered.SetResult(); return release.Task;
        };
        using var caller = new CancellationTokenSource();
        diagnostic.BeginNativeQuery(); // Prior CO progress must not be attributed to the power timeout.
        var transaction = new CpuLivePowerObserver(backend, store,
            operationTimeout: callerCancelled ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100));
        var pending = transaction.ObserveAsync(caller.Token, diagnostic);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancelled) { caller.Cancel(); }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CpuLivePowerBlockReason.ObservationUnavailable, result.Reason);
        Assert.True(operationToken.IsCancellationRequested); Assert.False(release.Task.IsCompleted);
        string first = Assert.IsType<string>(diagnostic.FirstFailure);
        Assert.StartsWith(callerCancelled ? "power observation caller cancellation" : "power observation operation timeout", first);
        Assert.Contains("active stage power backend observation", first); Assert.DoesNotContain("IOCTL", first);
        Assert.Contains("power phases total ", first);
        Assert.Matches(@"initial getter/context=\d+ ms completed, first native observation=\d+ ms active$", first);
        string? frozen = diagnostic.LiveObservationTimeoutContext;
        release.SetResult(new(backend.Context, CpuPowerTestData.Raw(backend.Pair), backend.Cap, backend.Capability, backend.Range));
        await release.Task;
        timing!.MoveTo(CpuPowerObservationPhase.ReplacementNative);
        diagnostic.CompletePowerObservation(timing);
        diagnostic.RecordObservationCancellation(!callerCancelled, powerObservation: true);
        Assert.Null(diagnostic.BeginPowerObservation()); Assert.Null(diagnostic.LiveObservationReason);
        diagnostic.Check(false, "late native rejection");
        Assert.Equal(first, diagnostic.FirstFailure); Assert.Equal(frozen, diagnostic.LiveObservationTimeoutContext);
        Assert.Equal(1, backend.Reads); Assert.Null(store.Journal);
    }

}
