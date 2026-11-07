using System.Buffers.Binary;
using System.Diagnostics;
using OpenBlade.Platform;

namespace OpenBlade.Platform.Tests;

public sealed class WindowsCpuTemperatureReaderTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromMilliseconds(100);

    [Theory]
    [InlineData(10_000, 10)]
    [InlineData(62_001, 63)]
    [InlineData(125_000, 125)]
    public void ProtocolRoundsUpAndBindsTheReplyToTheRequest(int milliCelsius, int expected)
    {
        byte[] request = CpuTemperatureProtocol.EncodeRequest(7);
        Assert.True(CpuTemperatureProtocol.TryDecodeRequest(request, out long sequence));
        Assert.Equal(7, sequence);
        byte[] reply = CpuTemperatureProtocol.EncodeReply(sequence, milliCelsius);
        Assert.True(CpuTemperatureProtocol.TryDecodeReply(reply, sequence, out int? value));
        Assert.Equal(expected, value);
        Assert.False(CpuTemperatureProtocol.TryDecodeReply(reply, sequence + 1, out _));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 60_000)]
    [InlineData(1, 9_999)]
    [InlineData(1, 125_001)]
    public void ProtocolRejectsMalformedStatusOrRange(int status, int value)
    {
        byte[] frame = CpuTemperatureProtocol.EncodeReply(1, null);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), status);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(16), value);
        Assert.False(CpuTemperatureProtocol.TryDecodeReply(frame, 1, out _));
    }

    [Fact]
    public void ProtocolRejectsInvalidGeometryMagicAndNonpositiveSequences()
    {
        Assert.False(CpuTemperatureProtocol.TryDecodeRequest(new byte[8], out _));
        Assert.False(CpuTemperatureProtocol.TryDecodeRequest(new byte[9], out _));
        byte[] reply = CpuTemperatureProtocol.EncodeReply(1, 60_000);
        Assert.False(CpuTemperatureProtocol.TryDecodeReply(reply.AsSpan(1), 1, out _));
        reply[0] ^= 1;
        Assert.False(CpuTemperatureProtocol.TryDecodeReply(reply, 1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuTemperatureProtocol.EncodeRequest(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuTemperatureProtocol.EncodeReply(1, 125_001));
    }

    [Fact]
    public async Task EachReadRequestsANewSampleAndUnavailableDoesNotReuseSuccess()
    {
        var child = new FakeChild();
        int starts = 0;
        child.Exchange = request => Task.FromResult(Reply(request, child.Requests.Count == 1 ? 62_001 : null));
        using var reader = Create(() => { starts++; return child; });
        Assert.Equal(63, await reader.ReadAsync());
        Assert.Null(await reader.ReadAsync());
        Assert.Equal(1, starts);
        Assert.Equal([1L, 2L], child.Requests);
    }

    [Fact]
    public async Task TelemetryTraceReportsOnlyStateChangesAndRecovery()
    {
        var child = new FakeChild();
        int reads = 0;
        child.Exchange = request => Task.FromResult(Reply(request, ++reads <= 2 ? null : 60_000));
        using var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            using var reader = Create(() => child, TimeSpan.FromSeconds(3));
            Assert.Null(await reader.ReadAsync());
            Assert.Null(await reader.ReadAsync());
            Assert.Equal(60, await reader.ReadAsync());
            Assert.Equal(60, await reader.ReadAsync());
            Assert.Equal(2, listener.Messages.Count);
            Assert.Contains("telemetry unavailable", listener.Messages[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("telemetry recovered", listener.Messages[1], StringComparison.OrdinalIgnoreCase);
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task ThrowingTraceListenerCannotChangeTelemetryAvailability()
    {
        var child = new FakeChild();
        int reads = 0;
        child.Exchange = request => Task.FromResult(Reply(request, ++reads == 1 ? null : 60_000));
        using var listener = new ThrowingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            using var reader = Create(() => child, TimeSpan.FromSeconds(3));
            Assert.Null(await reader.ReadAsync());
            Assert.Equal(60, await reader.ReadAsync());
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task NonCooperativeReadTimesOutWithoutWaitingForItsCompletion()
    {
        var never = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new FakeChild { Exchange = _ => never.Task };
        using var reader = Create(() => child);
        var elapsed = Stopwatch.StartNew();
        Assert.Null(await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
        await child.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(never.Task.IsCompleted);
        never.SetResult(CpuTemperatureProtocol.EncodeReply(1, 60_000));
    }

    [Fact]
    public async Task CancellationIsObservableAndPoisonsTheWorker()
    {
        var child = new FakeChild { Exchange = _ => new TaskCompletionSource<byte[]>().Task };
        using var reader = Create(() => child, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        Task<int?> reading = reader.ReadAsync(cancellation.Token).AsTask();
        await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        await child.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("magic")]
    [InlineData("status")]
    [InlineData("range")]
    [InlineData("short")]
    [InlineData("eof")]
    public async Task InvalidResponsesStopTheWorker(string defect)
    {
        var child = new FakeChild();
        child.Exchange = request =>
        {
            if (defect == "eof") { throw new EndOfStreamException(); }
            byte[] frame = Reply(request, 60_000);
            switch (defect)
            {
                case "sequence": frame[4]++; break;
                case "magic": frame[0]++; break;
                case "status": frame[12] = 2; break;
                case "range": BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(16), 130_000); break;
                case "short": return Task.FromResult(frame[..^1]);
            }
            return Task.FromResult(frame);
        };
        using var reader = Create(() => child);
        Assert.Null(await reader.ReadAsync());
        await child.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task UnconfirmedExitFencesReplacementEvenAfterTheJoinDeadline()
    {
        var child = new FakeChild
        {
            Exchange = _ => Task.FromResult(Array.Empty<byte>()),
            Exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        int starts = 0;
        using var reader = Create(() => { starts++; return child; });
        Assert.Null(await reader.ReadAsync());
        await child.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(150);
        Assert.Null(await reader.ReadAsync());
        Assert.Equal(1, starts);
        Assert.False(child.Disposed.Task.IsCompleted);
        child.Exit.SetResult();
    }

    [Fact]
    public async Task ConfirmedExitAllowsAReplacementWithANewSequence()
    {
        var first = new FakeChild { Exchange = _ => Task.FromResult(Array.Empty<byte>()) };
        var second = new FakeChild();
        int starts = 0;
        using var reader = Create(() => ++starts == 1 ? first : second);
        Assert.Null(await reader.ReadAsync());
        await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // Disposal happens immediately before the cleanup task publishes its result.
        int? result = null;
        for (int attempt = 0; attempt < 20 && result is null; attempt++)
        {
            result = await reader.ReadAsync();
            if (result is null) { await Task.Delay(10); }
        }
        Assert.Equal(60, result);
        Assert.Equal(2, starts);
        Assert.Equal([2L], second.Requests);
    }

    [Fact]
    public async Task StartupTimeoutRetainsAndStopsTheLateChildBeforeAnyReplacement()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new FakeChild();
        int starts = 0;
        using var reader = Create(() =>
        {
            Interlocked.Increment(ref starts);
            entered.SetResult();
            release.Wait();
            return child;
        });
        Task<int?> reading = reader.ReadAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.Null(await reading.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Null(await reader.ReadAsync());
            Assert.Equal(1, starts);
            Assert.Empty(child.Requests);
        }
        finally { release.Set(); }
        await child.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task DisposeDoesNotWaitForBlockedReadOrExitAndPreventsRestart()
    {
        var child = new FakeChild
        {
            Exchange = _ => new TaskCompletionSource<byte[]>().Task,
            Exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var reader = Create(() => child, TimeSpan.FromSeconds(5));
        Task<int?> reading = reader.ReadAsync().AsTask();
        await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var elapsed = Stopwatch.StartNew();
        reader.Dispose();
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Null(await reading.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(await reader.ReadAsync());
        await child.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(child.Disposed.Task.IsCompleted);
        child.Exit.SetResult();
    }

    [Fact]
    public async Task ConcurrentReadsAreSerializedAndQueuedCancellationDoesNotStopTheOwner()
    {
        var result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new FakeChild { Exchange = _ => result.Task };
        using var reader = Create(() => child, TimeSpan.FromSeconds(5));
        Task<int?> first = reader.ReadAsync().AsTask();
        await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource();
        Task<int?> queued = reader.ReadAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(child.Stopped.Task.IsCompleted);
        Assert.Single(child.Requests);
        result.SetResult(CpuTemperatureProtocol.EncodeReply(1, 60_000));
        Assert.Equal(60, await first);
    }

    private static WindowsCpuTemperatureReader Create(Func<ICpuTemperatureChild> factory, TimeSpan? deadline = null) =>
        new(factory, deadline ?? TestDeadline, TimeSpan.FromMilliseconds(40));

    private static byte[] Reply(byte[] request, int? temperature)
    {
        Assert.True(CpuTemperatureProtocol.TryDecodeRequest(request, out long sequence));
        return CpuTemperatureProtocol.EncodeReply(sequence, temperature);
    }

    private sealed class FakeChild : ICpuTemperatureChild
    {
        internal Func<byte[], Task<byte[]>> Exchange { get; set; } = request => Task.FromResult(Reply(request, 60_000));
        internal List<long> Requests { get; } = [];
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? Exit { get; set; }

        public Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken)
        {
            Requests.Add(BinaryPrimitives.ReadInt64LittleEndian(request));
            Entered.TrySetResult();
            return Exchange(request);
        }

        public void RequestStop() => Stopped.TrySetResult();
        public Task WaitForExitAsync() => Exit?.Task ?? Task.CompletedTask;
        public void Dispose() => Disposed.TrySetResult();
    }

    private sealed class CapturingTraceListener : TraceListener
    {
        private readonly List<string> messages = [];
        internal IReadOnlyList<string> Messages { get { lock (messages) { return messages.ToArray(); } } }
        public override void Write(string? message) => Capture(message);
        public override void WriteLine(string? message) => Capture(message);

        private void Capture(string? message)
        {
            if (message?.Contains("CPU temperature telemetry", StringComparison.Ordinal) != true) { return; }
            lock (messages) { messages.Add(message); }
        }
    }

    private sealed class ThrowingTraceListener : TraceListener
    {
        public override void Write(string? message)
        {
            if (message?.Contains("CPU temperature telemetry", StringComparison.Ordinal) == true)
            {
                throw new InvalidOperationException("Test listener failure.");
            }
        }

        public override void WriteLine(string? message) => Write(message);
    }
}
