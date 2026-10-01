using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;
using PadWrite = OpenBlade.Protocol.CoolingPadWriteResult;
using Raw = OpenBlade.Protocol.CoolingPadRawControlState;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadServiceCurveTrialTests
{
    private static string[] Arguments => ["--confirm-target", CoolingPadServiceCurveTrialCommand.Target, "--confirm-recovery", "ManualPowerCycleOnly"];

    [Fact]
    public async Task ExactArgumentsAndInitialConfirmationPrecedeRunner()
    {
        int runs = 0, observations = 0;
        Task<CoolingPadServiceCurveTrialResult> Run(Func<string, CancellationToken, Task<bool>> _, CancellationToken token)
        { runs++; throw new InvalidOperationException(); }
        Task<bool> Observe(string stage, CancellationToken token) { observations++; return Task.FromResult(false); }
        using var output = new StringWriter();
        Assert.Equal(2, await CoolingPadServiceCurveTrialCommand.RunAsync([], Run, Observe, output, default));
        Assert.Equal(0, observations);
        Assert.Equal(2, await CoolingPadServiceCurveTrialCommand.RunAsync(Arguments, Run, Observe, output, default));
        Assert.Equal(1, observations);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task ThrownRunnerPreservesUnknownSubmissionAndManualAttention()
    {
        using var output = new StringWriter();
        int exit = await CoolingPadServiceCurveTrialCommand.RunAsync(Arguments,
            (_, _) => throw new InvalidOperationException(), (_, _) => Task.FromResult(true), output, default);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(2, exit);
        Assert.True(json.RootElement.GetProperty("runnerResultUnavailable").GetBoolean());
        Assert.True(json.RootElement.GetProperty("submissionStateUnknown").GetBoolean());
        Assert.True(json.RootElement.GetProperty("manualAttentionRequired").GetBoolean());
        Assert.False(json.RootElement.GetProperty("outerServiceAdmissionExercised").GetBoolean());
        Assert.False(json.RootElement.GetProperty("productionAdmission").GetBoolean());
    }

    [Fact]
    public async Task SixtyTicksUseOneCoordinatorPortAndRestoreInPhysicalOrder()
    {
        var source = new Source();
        var session = new Session();
        int opens = 0, delays = 0;
        var stages = new List<string>();
        var runner = CoolingPadServiceCurveTrialRunner.ForTesting(() => source,
            _ => { opens++; return Task.FromResult(new CoolingPadSessionOpenResult(session, null)); },
            _ => { delays++; return Task.CompletedTask; });
        var result = await runner.RunAsync((stage, _) =>
        {
            stages.Add(stage);
            if (stage == "coolPhaseReady") { source.Value = new(45, 40); }
            if (stage == "mediumPhysicalConfirmation") { Assert.Equal("medium", session.Calls.Last()); }
            if (stage == "quietPhysicalConfirmation") { Assert.Equal("quiet", session.Calls.Last()); }
            if (stage == "finalPhysicalConfirmation") { Assert.Equal("rawRestoreAndQuery", session.Calls.Last()); }
            return Task.FromResult(true);
        }, default);
        Assert.True(result.Completed);
        Assert.True(result.SourceDisposed);
        Assert.True(result.ResourceCleanupCompleted);
        Assert.False(result.ManualAttentionRequired);
        Assert.Equal(1, opens);
        Assert.Equal(60, delays);
        Assert.Equal(62, result.Readings.Count); // preflight, first fresh demand, sixty maintenance reads
        Assert.Equal(61, result.Decisions.Count);
        Assert.Equal(59, result.Decisions.Count(x => x.UnchangedDemandSuppressed));
        Assert.Equal(new[] { "warmLoadReady", "firstDemandPhysicalConfirmation", "coolPhaseReady", "intervalPhysicalConfirmation", "mediumPhysicalConfirmation", "quietPhysicalConfirmation", "finalPhysicalConfirmation" }, stages);
        Assert.Equal(new[] { "query", "acquireAndInternalQuery", "query", "entryAndDemand", "updateDemand", "medium", "quiet", "rawRestoreAndQuery", "dispose" }, session.Calls);
        Assert.Equal(11, result.InferredLogicalExchangeCount); // includes both query0300 exchanges
        Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData("firstDemandPhysicalConfirmation", "entryAndDemand")]
    [InlineData("coolPhaseReady", "entryAndDemand")]
    [InlineData("intervalPhysicalConfirmation", "entryAndDemand")]
    [InlineData("mediumPhysicalConfirmation", "medium")]
    [InlineData("quietPhysicalConfirmation", "quiet")]
    [InlineData("finalPhysicalConfirmation", "rawRestoreAndQuery")]
    public async Task RefusedPhysicalGateNeverWritesLaterStage(string refusal, string lastOperation)
    {
        var source = new Source();
        var session = new Session();
        var runner = Create(source, session);
        var result = await runner.RunAsync((stage, _) => Task.FromResult(stage != refusal), default);
        Assert.False(result.Completed);
        Assert.True(result.ManualAttentionRequired);
        Assert.Equal(lastOperation, session.Calls[^2]);
        Assert.Equal("dispose", session.Calls.Last());
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task UncertainInactiveSamplePausesAndDoesNotRestoreOrReadAgain()
    {
        var source = new Source();
        var session = new Session();
        var runner = Create(source, session);
        var result = await runner.RunAsync((stage, _) =>
        {
            if (stage == "firstDemandPhysicalConfirmation") { source.Value = new(45, null, true); }
            return Task.FromResult(true);
        }, default);
        Assert.False(result.Completed);
        Assert.Equal("TelemetryUnavailable", result.Failure);
        Assert.Equal(3, source.Reads);
        Assert.DoesNotContain("medium", session.Calls);
        Assert.DoesNotContain("updateDemand", session.Calls);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task AmbiguousMediumStopsBeforeQuietAndKeepsUnknownFlag()
    {
        var session = new Session { FailMedium = true };
        var result = await Create(new Source(), session).RunAsync((_, _) => Task.FromResult(true), default);
        Assert.False(result.Completed);
        Assert.True(result.SubmissionStateUnknown);
        Assert.DoesNotContain("quiet", session.Calls);
        Assert.DoesNotContain("rawRestoreAndQuery", session.Calls);
        Assert.Null(result.InferredLogicalExchangeCount);
    }

    [Fact]
    public async Task WarmPromptRefusalDoesNotConstructSourceOrFactory()
    {
        int constructions = 0;
        var runner = new CoolingPadServiceCurveTrialRunner(
            () => { constructions++; throw new InvalidOperationException(); },
            () => { constructions++; throw new InvalidOperationException(); });
        var result = await runner.RunAsync((_, _) => Task.FromResult(false), default);
        Assert.False(result.Completed);
        Assert.False(result.ManualAttentionRequired);
        Assert.Equal(0, constructions);
    }

    [Fact]
    public async Task TimedOutPhysicalGateCannotResumeOnLateYes()
    {
        var yes = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session();
        var result = await CoolingPadServiceCurveTrialRunner.ForTesting(() => new Source(),
            _ => Task.FromResult(new CoolingPadSessionOpenResult(session, null)), operatorTimeout: TimeSpan.FromMilliseconds(40))
            .RunAsync((stage, _) => stage == "firstDemandPhysicalConfirmation" ? yes.Task : Task.FromResult(true), default);
        Assert.False(result.Completed);
        int count = session.Calls.Count;
        Assert.Null(result.InferredLogicalExchangeCount);
        yes.SetResult(true);
        await Task.Yield();
        Assert.Equal(count, session.Calls.Count);
        Assert.DoesNotContain("medium", session.Calls);
    }

    [Fact]
    public async Task CancellationDuringReadClosesPortBeforeOwningSourceDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        var blocked = new TaskCompletionSource<FanTemperatureReadings>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source();
        var session = new Session();
        var runner = Create(source, session);
        var run = runner.RunAsync((stage, _) =>
        {
            if (stage == "firstDemandPhysicalConfirmation")
            { source.OnRead = () => { cancellation.Cancel(); return new ValueTask<FanTemperatureReadings>(blocked.Task); }; }
            return Task.FromResult(true);
        }, cancellation.Token);
        await session.Disposal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(source.Disposed);
        blocked.SetResult(new(45, 50));
        var result = await run;
        Assert.True(source.Disposed);
        Assert.False(result.Completed);
        Assert.DoesNotContain("medium", session.Calls);
    }

    private static CoolingPadServiceCurveTrialRunner Create(Source source, Session session) =>
        CoolingPadServiceCurveTrialRunner.ForTesting(() => source, _ => Task.FromResult(new CoolingPadSessionOpenResult(session, null)));

    private sealed class Source : IFanTemperatureSource, IDisposable
    {
        internal FanTemperatureReadings Value = new(45, 60);
        internal int Reads;
        internal bool Disposed;
        internal Func<ValueTask<FanTemperatureReadings>>? OnRead;
        public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken token)
        { Reads++; return OnRead?.Invoke() ?? ValueTask.FromResult(Value); }
        public void Dispose() => Disposed = true;
    }

    private sealed class Session : ICoolingPadDemandSession
    {
        internal List<string> Calls { get; } = [];
        internal bool Disposed, FailMedium;
        internal TaskCompletionSource Disposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool acquired;
        public Task<CoolingPadRawQueryResult> QueryRawControlStateAsync(CancellationToken token)
        { Calls.Add("query"); return Task.FromResult(new CoolingPadRawQueryResult(acquired ? Raw.Raw0300 : Raw.Raw0000, null)); }
        private Task<PadWrite> Write(string name, Func<bool> allowed)
        {
            Assert.True(allowed());
            Calls.Add(name);
            return Task.FromResult(new PadWrite(true, !(FailMedium && name == "medium"), FailMedium && name == "medium" ? CoolingPadSessionFault.SubmissionUnknown : null));
        }
        public Task<PadWrite> AcquireAsync(Func<bool> allowed, CancellationToken token)
        { acquired = true; return Write("acquireAndInternalQuery", allowed); }
        public async Task<CoolingPadDemandEntryResult> EnterDemandAsync(CoolingPadConfiguredRpmDemand demand, Func<bool> allowed, CancellationToken token)
        { var result = await Write("entryAndDemand", allowed); return new(result, result); }
        public Task<PadWrite> UpdateDemandAsync(CoolingPadConfiguredRpmDemand demand, Func<bool> allowed, CancellationToken token) => Write("updateDemand", allowed);
        public Task<PadWrite> ReassertMediumAsync(Func<bool> allowed, CancellationToken token) => Write("medium", allowed);
        public Task<PadWrite> ReassertQuietAsync(Func<bool> allowed, CancellationToken token) => Write("quiet", allowed);
        public Task<PadWrite> RestoreRawControlStateAsync(Func<bool> allowed, CancellationToken token) => Write("rawRestoreAndQuery", allowed);
        public ValueTask DisposeAsync() { Disposed = true; Calls.Add("dispose"); Disposal.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
