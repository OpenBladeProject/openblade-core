using System.Collections.Concurrent;
using System.Diagnostics;
using OpenBlade.Contracts;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionPowerTransitionRelayTests
{
    private static readonly ControlOperationResult Applied =
        new(ControlOperationStatus.Applied, "completed");

    [Fact]
    public void NativeRegistrationAcceptsTheDocumentedCallbackFlag()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // Register and immediately unregister only. The sink never contacts
        // hardware or the service, and this test does not initiate sleep.
        using var relay = new SessionPowerTransitionRelay(Guid.NewGuid(), Stopwatch.GetTimestamp(),
            (_, _) => Task.FromResult(Applied));
        using var registration = new WindowsSessionPowerNotificationRegistration(relay);
    }

    [Fact]
    public void FailedRegistrationLeavesOtherAgentStartupAvailable()
    {
        using var relay = new SessionPowerTransitionRelay(Guid.NewGuid(), Stopwatch.GetTimestamp(),
            (_, _) => Task.FromResult(Applied));
        var api = new FakeApi { RegisterResult = 87 };

        using var registration = WindowsSessionPowerNotificationRegistration.TryCreate(relay, api);

        Assert.Null(registration);
        Assert.Equal(1, api.UnregisterCalls);
    }

    [Fact]
    public async Task ResumeCallbackReturnsBeforeItsBoundedReportCompletes()
    {
        var completion = new TaskCompletionSource<ControlOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        using var relay = new SessionPowerTransitionRelay(Guid.NewGuid(), Stopwatch.GetTimestamp(), (_, token) =>
        {
            observed = token;
            return completion.Task;
        });

        var stopwatch = Stopwatch.StartNew();
        relay.OnPowerNotification(18);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(completion.Task.IsCompleted);
        Assert.False(observed.IsCancellationRequested);
        await WaitUntilAsync(() => observed.IsCancellationRequested);
        completion.SetResult(Applied);
    }

    [Fact]
    public void NativeCallbackStartsReportInlineAndDeduplicatesResumeVariants()
    {
        var requests = new List<ReportSessionPowerTransitionRequest>();
        int callbackThread = Environment.CurrentManagedThreadId;
        Guid instance = Guid.NewGuid();
        using var relay = new SessionPowerTransitionRelay(instance, 100, (request, _) =>
        {
            Assert.Equal(callbackThread, Environment.CurrentManagedThreadId);
            requests.Add(request);
            return Task.FromResult(Applied);
        }, () => 100, 1000);
        var api = new FakeApi();
        using var registration = new WindowsSessionPowerNotificationRegistration(relay, api);

        foreach (uint type in new uint[] { 0, 10, 0x8013, 4, 4, 18, 7, 4 })
        {
            Assert.Equal(0u, api.Notify(type));
        }

        Assert.Equal(new[] { true, false, true }, requests.Select(item => item.Suspending));
        Assert.Equal(new long[] { 1, 2, 3 }, requests.Select(item => item.Sequence));
        Assert.All(requests, item =>
        {
            Assert.Equal(instance, item.InstanceId);
            Assert.Equal(100, item.InstanceStartedQpc);
            Assert.Equal(100, item.TimestampQpc);
            Assert.Equal(OpenBladeIpc.ProtocolVersion, item.Version);
            Assert.NotEqual(Guid.Empty, item.RequestId);
        });
    }

    [Fact]
    public async Task NewTransitionsKeepOnlyLatestPendingAndCancelInFlightSuspend()
    {
        var requests = new ConcurrentQueue<ReportSessionPowerTransitionRequest>();
        var firstStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource<ControlOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        long now = 100;
        using var relay = new SessionPowerTransitionRelay(Guid.NewGuid(), 100, (request, token) =>
        {
            requests.Enqueue(request);
            if (request.Sequence == 1)
            {
                firstStarted.SetResult(token);
                return finishFirst.Task;
            }
            return Task.FromResult(Applied);
        }, () => Volatile.Read(ref now), 1000, TimeSpan.FromMilliseconds(40));
        Task suspend = Task.Run(() => relay.OnPowerNotification(4));
        CancellationToken firstCancellation = await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        relay.OnPowerNotification(18);
        await WaitUntilAsync(() => firstCancellation.IsCancellationRequested);
        relay.OnPowerNotification(4);
        relay.OnPowerNotification(7);
        finishFirst.SetResult(Applied);
        await suspend.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => requests.Count == 2);

        Assert.Equal(new long[] { 1, 4 }, requests.Select(item => item.Sequence));
        Assert.False(requests.Last().Suspending);
    }

    [Fact]
    public async Task PendingTransitionExpiresByQpcEvenWhenCancellationTimerHasNotRun()
    {
        var requests = new ConcurrentQueue<ReportSessionPowerTransitionRequest>();
        var firstStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource<ControlOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        long now = 100;
        using var relay = new SessionPowerTransitionRelay(Guid.NewGuid(), 100, (request, token) =>
        {
            requests.Enqueue(request);
            firstStarted.TrySetResult(token);
            return finishFirst.Task;
        }, () => Volatile.Read(ref now), 1000);
        Task suspend = Task.Run(() => relay.OnPowerNotification(4));
        CancellationToken firstCancellation = await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task resume = Task.Run(() => relay.OnPowerNotification(18));
        await WaitUntilAsync(() => firstCancellation.IsCancellationRequested);

        // Advance only the absolute clock, without waiting for a real timeout.
        Volatile.Write(ref now, 100 + SessionPowerTransition.MaximumAgeMilliseconds + 1);
        finishFirst.SetResult(Applied);
        await Task.WhenAll(suspend, resume).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(requests);
    }

    [Fact]
    public void NoncooperativeAsynchronousSinkCannotHoldCallbackBeyondBudget()
    {
        var completion = new TaskCompletionSource<ControlOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        int calls = 0;
        using var relay = new SessionPowerTransitionRelay(Guid.NewGuid(), Stopwatch.GetTimestamp(), (_, token) =>
        {
            calls++;
            observed = token;
            return completion.Task;
        }, callbackBudget: TimeSpan.FromMilliseconds(40));
        var stopwatch = Stopwatch.StartNew();
        relay.OnPowerNotification(4);
        relay.OnPowerNotification(18);
        relay.OnPowerNotification(4);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(observed.IsCancellationRequested);
        Assert.Equal(1, calls);
        completion.SetResult(Applied);
    }

    [Fact]
    public async Task DisposalCancelsActiveAndReleasesPendingWithoutLateDispatch()
    {
        var firstStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<ControlOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var relay = new SessionPowerTransitionRelay(Guid.NewGuid(), 100, (_, token) =>
        {
            Interlocked.Increment(ref calls);
            firstStarted.TrySetResult(token);
            return completion.Task;
        }, () => 100, 1000);
        Task first = Task.Run(() => relay.OnPowerNotification(4));
        CancellationToken token = await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = Task.Run(() => relay.OnPowerNotification(18));
        await WaitUntilAsync(() => token.IsCancellationRequested);

        relay.Dispose();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        completion.SetResult(Applied);
        relay.OnPowerNotification(4);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void SinkExceptionsCannotEscapeNativeCallback()
    {
        using var relay = new SessionPowerTransitionRelay(Guid.NewGuid(), 100,
            (_, _) => throw new InvalidOperationException("test"), () => 100, 1000);
        var api = new FakeApi();
        using var registration = new WindowsSessionPowerNotificationRegistration(relay, api);

        Assert.Equal(0u, api.Notify(4));
        Assert.Equal(0u, api.Notify(18));
    }

    [Fact]
    public void FailedNativeUnregisterRetainsCallbackAndSuppressesFurtherReports()
    {
        int calls = 0;
        using var relay = new SessionPowerTransitionRelay(Guid.NewGuid(), 100, (_, _) =>
        {
            calls++;
            return Task.FromResult(Applied);
        }, () => 100, 1000);
        var api = new FakeApi { UnregisterResult = 5 };
        var registration = new WindowsSessionPowerNotificationRegistration(relay, api);
        var weakRegistration = new WeakReference(registration);
        api.Notify(4);
        registration.Dispose();
        registration.Dispose();
        registration = null!;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        api.Notify(18);

        Assert.True(weakRegistration.IsAlive);
        Assert.Equal(1, api.UnregisterCalls);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NoncooperativeCancellationCannotAccumulateAcrossReports()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<ControlOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var relay = new SessionPowerTransitionRelay(Guid.NewGuid(), Stopwatch.GetTimestamp(), (_, token) =>
        {
            Interlocked.Increment(ref calls);
            token.Register(() =>
            {
                cancellationStarted.TrySetResult();
                releaseCancellation.Task.GetAwaiter().GetResult();
                cancellationFinished.TrySetResult();
            });
            started.TrySetResult();
            return completion.Task;
        }, callbackBudget: TimeSpan.FromMilliseconds(40));
        try
        {
            Task suspend = Task.Run(() => relay.OnPowerNotification(4));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            relay.OnPowerNotification(18);
            await cancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            completion.SetResult(Applied);
            await suspend.WaitAsync(TimeSpan.FromSeconds(5));
            relay.OnPowerNotification(4);
            relay.OnPowerNotification(18);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            relay.Dispose();
            releaseCancellation.TrySetResult();
            await cancellationFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class FakeApi : ISessionPowerNotificationApi
    {
        private WeakReference<SessionPowerNotificationCallback>? callback;
        internal uint RegisterResult { get; init; }
        internal uint UnregisterResult { get; init; }
        internal int UnregisterCalls { get; private set; }

        public uint Register(SessionPowerNotificationCallback callback, out IntPtr registration)
        {
            this.callback = new WeakReference<SessionPowerNotificationCallback>(callback);
            registration = new IntPtr(42);
            return RegisterResult;
        }

        public uint Unregister(IntPtr registration)
        {
            Assert.Equal(new IntPtr(42), registration);
            UnregisterCalls++;
            return UnregisterResult;
        }

        internal uint Notify(uint type)
        {
            Assert.True(callback!.TryGetTarget(out SessionPowerNotificationCallback? target));
            return target!(IntPtr.Zero, type, IntPtr.Zero);
        }
    }
}
