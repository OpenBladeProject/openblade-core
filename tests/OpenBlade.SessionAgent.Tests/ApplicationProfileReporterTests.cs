using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.Session;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ApplicationProfileReporterTests
{
    [Fact]
    public void SelectActivePathPrefersForegroundThenNewestActivatedRunningProfile()
    {
        var first = new ApplicationProfileDefinition(
            Guid.NewGuid(),
            "First",
            @"C:\Games\first.exe",
            ApplicationActivationMode.WhileRunningAfterActivation,
            new ApplicationProfileOverrideSet());
        var second = new ApplicationProfileDefinition(
            Guid.NewGuid(),
            "Second",
            @"C:\Games\second.exe",
            ApplicationActivationMode.WhileRunningAfterActivation,
            new ApplicationProfileOverrideSet());
        ApplicationProfileDefinition[] profiles = [second, first];
        (Guid ProfileId, string ExecutablePath, long ActivationSequence)[] activated =
        [
            (first.Id, first.ExecutablePath, 7),
            (second.Id, second.ExecutablePath, 3),
        ];

        Assert.Equal(
            first.ExecutablePath,
            ApplicationProfileReporter.SelectActivePath(profiles, null, activated));
        Assert.Equal(
            second.ExecutablePath,
            ApplicationProfileReporter.SelectActivePath(
                profiles,
                second.ExecutablePath,
                activated));
        Assert.Equal(
            first.ExecutablePath,
            ApplicationProfileReporter.SelectActivePath(
                [second with { Enabled = false }, first],
                null,
                activated));
    }

    [Fact]
    public async Task ReactivationUpdatesRecencyWithoutReplacingExitSubscription()
    {
        var first = new ApplicationProfileDefinition(Guid.NewGuid(), "First", @"C:\Games\first.exe",
            ApplicationActivationMode.WhileRunningAfterActivation, new ApplicationProfileOverrideSet());
        var second = new ApplicationProfileDefinition(Guid.NewGuid(), "Second", @"C:\Games\second.exe",
            ApplicationActivationMode.WhileRunningAfterActivation, new ApplicationProfileOverrideSet());
        var foregroundOnly = new ApplicationProfileDefinition(Guid.NewGuid(), "Foreground", @"C:\Games\foreground.exe",
            ApplicationActivationMode.Foreground, new ApplicationProfileOverrideSet());
        var firstProcess = new FakeProcessHandle(first.ExecutablePath);
        var secondProcess = new FakeProcessHandle(second.ExecutablePath);
        int openCount = 0;
        Channel<string?> reports = Channel.CreateUnbounded<string?>();
        await using var reporter = new ApplicationProfileReporter(
            (request, _) =>
            {
                reports.Writer.TryWrite(request.ExecutablePath);
                return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Applied, "Applied"));
            },
            processId =>
            {
                openCount++;
                return processId == 1 ? firstProcess : secondProcess;
            },
            TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        Task running = reporter.RunAsync(cancellation.Token);
        ApplicationProfileDefinition[] profiles = [first, second, foregroundOnly];
        try
        {
            reporter.Observe(Status(profiles));
            await ObserveAndExpectAsync(first, 1);
            Action staleFirstExit = firstProcess.CaptureExitNotification();
            await ObserveAndExpectAsync(second, 2);
            await ObserveAndExpectAsync(first, 1);

            // Repeated status/audio-output observations of the same foreground
            // must neither replace the process subscription nor report again.
            reporter.Observe(Status(profiles));
            reporter.ObserveForeground(Observation(first, 1));
            await ObserveAndExpectAsync(foregroundOnly, 3);
            reporter.ObserveForeground(ThxForegroundObservation.Unknown);
            await ExpectAsync(first.ExecutablePath);
            Assert.Equal(2, openCount);

            secondProcess.RaiseExit();
            await ObserveAndExpectAsync(foregroundOnly, 3);
            reporter.ObserveForeground(ThxForegroundObservation.Unknown);
            await ExpectAsync(first.ExecutablePath);
            Assert.True(secondProcess.Disposed);

            // The original first-process callback must still clear its record
            // after its activation sequence changed; no timer pruning is involved.
            firstProcess.RaiseExit();
            await ExpectAsync(null);
            Assert.True(firstProcess.Disposed);

            // A delayed exit notification for the old lifetime cannot retire a
            // new process that Windows assigned the same PID and executable.
            firstProcess = new FakeProcessHandle(first.ExecutablePath);
            await ObserveAndExpectAsync(first, 1);
            staleFirstExit();
            await ObserveAndExpectAsync(foregroundOnly, 3);
            reporter.ObserveForeground(ThxForegroundObservation.Unknown);
            await ExpectAsync(first.ExecutablePath);
            Assert.False(firstProcess.Disposed);
            Assert.Equal(3, openCount);
            Assert.False(reports.Reader.TryRead(out _));
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }

        static ThxForegroundObservation Observation(ApplicationProfileDefinition profile, int id) =>
            new(Path.GetFileName(profile.ExecutablePath), ExecutablePath: profile.ExecutablePath, ProcessId: id);

        async Task ObserveAndExpectAsync(ApplicationProfileDefinition profile, int id)
        {
            reporter.ObserveForeground(Observation(profile, id));
            await ExpectAsync(profile.ExecutablePath);
        }

        async Task ExpectAsync(string? path) =>
            Assert.Equal(path, await reports.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task DroppedExitEventIsPrunedBeforeLeaseCanRefreshExitedProcess()
    {
        var profile = new ApplicationProfileDefinition(
            Guid.NewGuid(),
            "Game",
            @"C:\Games\game.exe",
            ApplicationActivationMode.WhileRunningAfterActivation,
            new ApplicationProfileOverrideSet());
        var process = new FakeProcessHandle(profile.ExecutablePath);
        var requests = new ConcurrentQueue<ReportApplicationContextRequest>();
        var activated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleared = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reporter = new ApplicationProfileReporter(
            (request, _) =>
            {
                requests.Enqueue(request);
                if (request.ExecutablePath == profile.ExecutablePath)
                {
                    activated.TrySetResult();
                }
                else if (request.ExecutablePath is null && activated.Task.IsCompleted)
                {
                    cleared.TrySetResult();
                }
                return Task.FromResult(new ControlOperationResult(
                    ControlOperationStatus.Applied,
                    "Applied"));
            },
            processId => processId == 42
                ? process
                : throw new ArgumentException("Unexpected process."),
            TimeSpan.FromMilliseconds(15));
        using var cancellation = new CancellationTokenSource();
        Task running = reporter.RunAsync(cancellation.Token);
        reporter.Observe(Status([profile]));
        reporter.ObserveForeground(new ThxForegroundObservation(
            "game.exe",
            ExecutablePath: profile.ExecutablePath,
            ProcessId: 42));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        process.MarkExitedWithoutRaisingEvent();
        for (int index = 0; index < 32; index++)
        {
            reporter.ObserveForeground(ThxForegroundObservation.Unknown);
        }
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(2));
        int countAfterClear = requests.Count;
        await Task.Delay(60);

        Assert.DoesNotContain(
            requests.Skip(countAfterClear),
            request => request.ExecutablePath == profile.ExecutablePath);
        Assert.True(process.Disposed);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task AuthoritativeStatusCannotBeEvictedByForegroundBurstWhileReportIsBlocked()
    {
        var profile = new ApplicationProfileDefinition(
            Guid.NewGuid(),
            "Game",
            @"C:\Games\game.exe",
            ApplicationActivationMode.WhileRunningAfterActivation,
            new ApplicationProfileOverrideSet());
        var process = new FakeProcessHandle(profile.ExecutablePath);
        var firstReportStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstReport = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReport = new TaskCompletionSource<ReportApplicationContextRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int reportCount = 0;
        await using var reporter = new ApplicationProfileReporter(
            async (request, cancellationToken) =>
            {
                int ordinal = Interlocked.Increment(ref reportCount);
                if (ordinal == 1)
                {
                    firstReportStarted.TrySetResult();
                    await releaseFirstReport.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    secondReport.TrySetResult(request);
                }
                return new ControlOperationResult(
                    ControlOperationStatus.Applied,
                    "Applied");
            },
            processId => processId == 42
                ? process
                : throw new ArgumentException("Unexpected process."),
            TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        Task running = reporter.RunAsync(cancellation.Token);
        try
        {
            reporter.Observe(Status([profile]));
            reporter.ObserveForeground(new ThxForegroundObservation(
                "game.exe",
                ExecutablePath: profile.ExecutablePath,
                ProcessId: 42));
            await firstReportStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            reporter.Observe(Status(
            [
                profile with
                {
                    ActivationMode = ApplicationActivationMode.Foreground,
                },
            ]));
            for (int index = 0; index < 16; index++)
            {
                reporter.ObserveForeground(ThxForegroundObservation.Unknown);
            }
            releaseFirstReport.TrySetResult();

            ReportApplicationContextRequest next = await secondReport.Task
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(next.ExecutablePath);
        }
        finally
        {
            releaseFirstReport.TrySetResult();
            cancellation.Cancel();
            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static ServiceStatusSnapshot Status(
        IReadOnlyList<ApplicationProfileDefinition> profiles)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults() with
        {
            ApplicationProfiles = profiles,
        };
        return new ServiceStatusSnapshot(
            Revision: 1,
            DeviceVerified: true,
            WritesBlocked: false,
            PotentialControllers: [],
            ControllerDetectionWarning: null,
            ExternalChanges: [],
            Message: "test",
            Capabilities: null,
            ConfirmedState: null,
            Profiles: defaults,
            DesiredState: new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    defaults.PluggedIn.Performance,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(
                    defaults.PluggedIn.Fans,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(
                    defaults.PluggedIn.Lighting,
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(defaults.BatteryPolicy, true, false)),
            ActivePowerSource: PowerSource.PluggedIn,
            LiveReadings: null,
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false,
            Firmware: null,
            ServiceVersion: "test");
    }

    private sealed class FakeProcessHandle(string executablePath) : IApplicationProcessHandle
    {
        private EventHandler? exited;

        public string? ExecutablePath { get; } = executablePath;
        public bool HasExited { get; private set; }
        public bool Disposed { get; private set; }
        public event EventHandler? Exited
        {
            add => exited += value;
            remove => exited -= value;
        }

        public void MarkExitedWithoutRaisingEvent() => HasExited = true;

        public void RaiseExit()
        {
            HasExited = true;
            exited?.Invoke(this, EventArgs.Empty);
        }

        public Action CaptureExitNotification()
        {
            EventHandler? notification = exited;
            return () => notification?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Disposed = true;
            exited = null;
        }
    }
}
