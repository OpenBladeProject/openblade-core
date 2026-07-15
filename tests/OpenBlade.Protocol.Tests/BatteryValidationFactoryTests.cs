using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class BatteryValidationFactoryTests
{
    [Fact]
    public void CreateWindows_ReturnsTypedFactory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IBatteryValidationRunFactory factory = BatteryValidationFactory.CreateWindows();

        Assert.NotNull(factory);
    }

    [Fact]
    public void CreateWindows_DoesNotReadHostEvidenceOrOpenHidDuringConstruction()
    {
        var registry = new ThrowingRegistryValueSource();
        var platform = new ThrowingPlatformEvidenceSource();
        var processes = new ThrowingProcessSource();
        var services = new ThrowingServiceSource();
        var native = new ThrowingHidCandidateNative();
        var io = new ThrowingHidFeatureIo();

        IBatteryValidationRunFactory factory = BatteryValidationFactory.CreateWindows(
            registry,
            platform,
            processes,
            services,
            native,
            io,
            TimeProvider.System,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5));

        Assert.NotNull(factory);
        Assert.Equal(0, registry.ReadCount);
        Assert.Equal(0, platform.ReadCount);
        Assert.Equal(0, processes.ReadCount);
        Assert.Equal(0, services.ReadCount);
        Assert.Equal(0, native.EnumerationCount);
        Assert.Equal(0, io.ExecutionCount);
    }

    [Fact]
    public void CreateWindowsQuerySequence_DoesNotReadHostEvidenceOrOpenHidDuringConstruction()
    {
        var registry = new ThrowingRegistryValueSource();
        var platform = new ThrowingPlatformEvidenceSource();
        var processes = new ThrowingProcessSource();
        var services = new ThrowingServiceSource();
        var native = new ThrowingHidCandidateNative();
        var io = new ThrowingHidFeatureIo();

        IBatteryQuerySequenceRunFactory factory = BatteryValidationFactory.CreateWindowsQuerySequence(
            registry,
            platform,
            processes,
            services,
            native,
            io,
            TimeProvider.System,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5));

        Assert.NotNull(factory);
        Assert.Equal(0, registry.ReadCount);
        Assert.Equal(0, platform.ReadCount);
        Assert.Equal(0, processes.ReadCount);
        Assert.Equal(0, services.ReadCount);
        Assert.Equal(0, native.EnumerationCount);
        Assert.Equal(0, io.ExecutionCount);
    }

    [Fact]
    public async Task CreateWindowsQuerySequence_ConfiguresTwentyMillisecondFeatureReportSettle()
    {
        var time = new ManualTimeProvider();
        var native = new ExactHidCandidateNative();
        var io = new SuccessfulQueryIo();
        IBatteryQuerySequenceRunFactory factory = BatteryValidationFactory.CreateWindowsQuerySequence(
            new ExactRegistryValueSource(),
            new ExactPlatformEvidenceSource(),
            new EmptyProcessSource(),
            new EmptyServiceSource(),
            native,
            io,
            time,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5));
        HidSessionResult<IBatteryQuerySequencePort> opened = await factory.OpenAsync(default);
        await using IBatteryQuerySequencePort port = Assert.IsAssignableFrom<IBatteryQuerySequencePort>(opened.Value);

        Task<HidSessionResult<BatteryValidationSetting>> query = port.QueryAsync(default);
        await io.WaitForCallsAsync(1);
        await time.WaitForTimerCountAsync(2);
        time.Advance(TimeSpan.FromMilliseconds(19));
        await Task.Yield();
        Assert.Equal(1, io.CallCount);
        Assert.False(query.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(BatteryValidationSetting.ProtectionOff, (await query).Value);
        Assert.Equal(2, io.CallCount);
    }

    [Fact]
    public async Task CreateWindows_ConfiguresTwentyMillisecondFeatureReportSettleForQuery()
    {
        var time = new ManualTimeProvider();
        var io = new SuccessfulQueryIo();
        IBatteryValidationRunFactory factory = BatteryValidationFactory.CreateWindows(
            new ExactRegistryValueSource(),
            new ExactPlatformEvidenceSource(),
            new EmptyProcessSource(),
            new EmptyServiceSource(),
            new ExactHidCandidateNative(),
            io,
            time,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5));
        HidSessionResult<IBatteryValidationPort> opened = await factory.OpenAsync(default);
        await using IBatteryValidationPort port = Assert.IsAssignableFrom<IBatteryValidationPort>(
            opened.Value);

        Task<HidSessionResult<BatteryValidationSetting>> query = port.QueryAsync(default);
        await io.WaitForCallsAsync(1);
        await time.WaitForTimerCountAsync(2);
        time.Advance(TimeSpan.FromMilliseconds(19));
        await Task.Yield();
        Assert.Equal(1, io.CallCount);
        Assert.False(query.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(BatteryValidationSetting.ProtectionOff, (await query).Value);
        Assert.Equal(2, io.CallCount);
    }

    [Fact]
    public async Task CreateWindows_ConfiguresTwentyMillisecondFeatureReportSettleForSetter()
    {
        var time = new ManualTimeProvider();
        var io = new SuccessfulQueryIo();
        IBatteryValidationRunFactory factory = BatteryValidationFactory.CreateWindows(
            new ExactRegistryValueSource(),
            new ExactPlatformEvidenceSource(),
            new EmptyProcessSource(),
            new EmptyServiceSource(),
            new ExactHidCandidateNative(),
            io,
            time,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5));
        HidSessionResult<IBatteryValidationPort> opened = await factory.OpenAsync(default);
        await using IBatteryValidationPort port = Assert.IsAssignableFrom<IBatteryValidationPort>(
            opened.Value);

        Task<HidSessionResult<BatteryValidationSetting>> baseline = port.QueryAsync(default);
        await io.WaitForCallsAsync(1);
        await time.WaitForTimerCountAsync(2);
        time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Equal(BatteryValidationSetting.ProtectionOff, (await baseline).Value);

        Task<HidSessionResult<HidUnit>> setter = port.ApplyAsync(
            BatteryValidationSetting.Limit80,
            default);
        await io.WaitForCallsAsync(3);
        await time.WaitForTimerCountAsync(4);
        time.Advance(TimeSpan.FromMilliseconds(19));
        await Task.Yield();
        Assert.Equal(3, io.CallCount);
        Assert.False(setter.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null((await setter).Failure);
        Assert.Equal(4, io.CallCount);
        Assert.Contains(io.SetReports, report =>
            report[1] == 2 && report[6] == 0x07 && report[7] == 0x12 && report[8] == 0xD0);
    }

    [Fact]
    public async Task CreateWindows_RecoveryActorRetainsTwentyMillisecondFeatureReportSettle()
    {
        var time = new ManualTimeProvider();
        var native = new ExactHidCandidateNative();
        var io = new SuccessfulQueryIo(busyCandidateSet: true);
        IBatteryValidationRunFactory factory = BatteryValidationFactory.CreateWindows(
            new ExactRegistryValueSource(),
            new ExactPlatformEvidenceSource(),
            new EmptyProcessSource(),
            new EmptyServiceSource(),
            native,
            io,
            time,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5));
        HidSessionResult<IBatteryValidationPort> opened = await factory.OpenAsync(default);
        await using IBatteryValidationPort port = Assert.IsAssignableFrom<IBatteryValidationPort>(
            opened.Value);

        Task<HidSessionResult<BatteryValidationSetting>> baseline = port.QueryAsync(default);
        await io.WaitForCallsAsync(1);
        await time.WaitForTimerCountAsync(2);
        time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Equal(BatteryValidationSetting.ProtectionOff, (await baseline).Value);

        Task<HidSessionResult<HidUnit>> candidate = port.ApplyAsync(
            BatteryValidationSetting.Limit80,
            default);
        await io.WaitForCallsAsync(3);
        await time.WaitForTimerCountAsync(4);
        time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Equal(HidSessionFailureKind.ProtocolFailure, (await candidate).Failure);

        Task<HidSessionResult<HidUnit>> restore = port.ApplyAsync(
            BatteryValidationSetting.ProtectionOff,
            default);
        await io.WaitForCallsAsync(5);
        await time.WaitForTimerCountAsync(6);
        Assert.Equal(2, native.OpenCount);
        time.Advance(TimeSpan.FromMilliseconds(19));
        await Task.Yield();
        Assert.Equal(5, io.CallCount);
        Assert.False(restore.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null((await restore).Failure);
        Assert.Equal(6, io.CallCount);
        Assert.Contains(io.SetReports, report =>
            report[1] == 4 && report[6] == 0x07 && report[7] == 0x12 && report[8] == 0x50);

        Task<HidSessionResult<BatteryValidationSetting>> restoreQuery = port.QueryAsync(default);
        await io.WaitForCallsAsync(7);
        await time.WaitForTimerCountAsync(8);
        time.Advance(TimeSpan.FromMilliseconds(19));
        await Task.Yield();
        Assert.Equal(7, io.CallCount);
        Assert.False(restoreQuery.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(BatteryValidationSetting.ProtectionOff, (await restoreQuery).Value);
        Assert.Equal(8, io.CallCount);
        Assert.Contains(io.SetReports, report =>
            report[1] == 5 && report[6] == 0x07 && report[7] == 0x92 && report[8] == 0x00);
    }

    [Fact]
    public void ProbeWindows_ContainsHostEvidenceFailureBeforeGuardOrHid()
    {
        var registry = new ThrowingRegistryValueSource();
        var platform = new ThrowingPlatformEvidenceSource();
        var processes = new ThrowingProcessSource();
        var services = new ThrowingServiceSource();
        var native = new ThrowingHidCandidateNative();

        BatteryValidationProbeResult result = BatteryValidationFactory.ProbeWindows(
            registry,
            platform,
            processes,
            services,
            native);

        Assert.Equal(BatteryValidationProbeStage.HostIdentity, result.Stage);
        Assert.Equal(
            BatteryValidationProbeClassification.HostIdentityBlocked,
            result.Classification);
        Assert.Equal(0, registry.ReadCount);
        Assert.Equal(1, platform.ReadCount);
        Assert.Equal(0, processes.ReadCount);
        Assert.Equal(0, services.ReadCount);
        Assert.Equal(0, native.EnumerationCount);
    }

    private sealed class ThrowingRegistryValueSource : IRegistryValueSource
    {
        public int ReadCount { get; private set; }

        public object? Read(
            Microsoft.Win32.RegistryHive hive,
            Microsoft.Win32.RegistryView view,
            string key,
            string valueName)
        {
            ReadCount++;
            throw new InvalidOperationException("Registry must not be read during composition.");
        }
    }

    private sealed class ThrowingPlatformEvidenceSource : IWindowsPlatformEvidenceSource
    {
        public int ReadCount { get; private set; }

        public WindowsPlatformEvidence Read()
        {
            ReadCount++;
            throw new InvalidOperationException("Platform evidence must not be read during composition.");
        }
    }

    private sealed class ThrowingProcessSource : IRunningProcessSource
    {
        public int ReadCount { get; private set; }

        public IReadOnlyList<string> GetProcessNames()
        {
            ReadCount++;
            throw new InvalidOperationException("Processes must not be read during composition.");
        }
    }

    private sealed class ThrowingServiceSource : IWindowsServiceSource
    {
        public int ReadCount { get; private set; }

        public IReadOnlyList<WindowsServiceSnapshot> GetServices()
        {
            ReadCount++;
            throw new InvalidOperationException("Services must not be read during composition.");
        }
    }

    private sealed class ThrowingHidCandidateNative : IHidCandidateNative
    {
        public int EnumerationCount { get; private set; }

        public IReadOnlyList<string> EnumerateDevicePaths()
        {
            EnumerationCount++;
            throw new InvalidOperationException("HID must not be enumerated during composition.");
        }

        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters) =>
            throw new InvalidOperationException("HID must not be opened during composition.");

        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle) =>
            throw new InvalidOperationException("HID must not be read during composition.");

        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle) =>
            throw new InvalidOperationException("HID must not be bound during composition.");
    }

    private sealed class ThrowingHidFeatureIo : IHidFeatureIo
    {
        public int ExecutionCount { get; private set; }

        public Task<HidFeatureIoResult> ExecuteAsync(
            OpenedHidHandle handle,
            uint controlCode,
            byte[] buffer,
            CancellationToken callerCancellation,
            CancellationToken deadlineCancellation)
        {
            ExecutionCount++;
            throw new InvalidOperationException("HID I/O must not run during composition.");
        }
    }

    private sealed class ExactRegistryValueSource : IRegistryValueSource
    {
        public object? Read(
            Microsoft.Win32.RegistryHive hive,
            Microsoft.Win32.RegistryView view,
            string key,
            string valueName) => valueName switch
            {
                "SystemManufacturer" => "Razer",
                "SystemProductName" => "Blade 16 - RZ09-0581",
                "SystemSKU" => "RZ09-05819EN4",
                "BIOSVersion" => "3.01",
                _ => null,
            };
    }

    private sealed class ExactPlatformEvidenceSource : IWindowsPlatformEvidenceSource
    {
        public WindowsPlatformEvidence Read() => new(
            IsWindows: true,
            IsWorkstation: true,
            new Version(10, 0, 26200),
            Architecture.X64);
    }

    private sealed class EmptyProcessSource : IRunningProcessSource
    {
        public IReadOnlyList<string> GetProcessNames() => [];
    }

    private sealed class EmptyServiceSource : IWindowsServiceSource
    {
        public IReadOnlyList<WindowsServiceSnapshot> GetServices() => [];
    }

    private sealed class ExactHidCandidateNative : IHidCandidateNative
    {
        public int OpenCount { get; private set; }

        public IReadOnlyList<string> EnumerateDevicePaths() => ["exact"];

        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters)
        {
            OpenCount++;
            return new(new OpenedCandidateHandle(
                (SafeFileHandle)RuntimeHelpers.GetUninitializedObject(typeof(SafeFileHandle)),
                static () => { }), null);
        }

        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle) =>
            new(0x1532, 0x02E0, 0x0001, 0x0002, 91);

        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle) => new(
            handle.TransferOwnership(),
            (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(
                typeof(ThreadPoolBoundHandle)),
            static () => { },
            static () => { });
    }

    private sealed class SuccessfulQueryIo : IHidFeatureIo
    {
        private readonly TaskCompletionSource<bool> called = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool busyCandidateSet;
        private readonly object sync = new();
        private byte[]? request;

        private int callCount;

        public SuccessfulQueryIo(bool busyCandidateSet = false)
        {
            this.busyCandidateSet = busyCandidateSet;
        }

        public int CallCount => Volatile.Read(ref callCount);

        public List<byte[]> SetReports { get; } = [];

        public Task<HidFeatureIoResult> ExecuteAsync(
            OpenedHidHandle handle,
            uint controlCode,
            byte[] buffer,
            CancellationToken callerCancellation,
            CancellationToken deadlineCancellation)
        {
            int currentCall = Interlocked.Increment(ref callCount);
            called.TrySetResult(true);
            if (controlCode == 0x000B0191)
            {
                byte[] report = buffer.AsSpan(1, RazerReport.ReportLength).ToArray();
                lock (sync)
                {
                    request = report;
                    SetReports.Add(report);
                }
            }
            if (controlCode == 0x000B0192)
            {
                byte[] report;
                lock (sync)
                {
                    byte[] source = request ?? throw new InvalidOperationException();
                    report = RazerReport.Create(
                        source[1],
                        source[6],
                        source[7],
                        source[7] == 0x92 ? [0x50] : [source[8]]).Encode();
                }

                report[0] = busyCandidateSet && report[1] == 2
                    ? (byte)0x01
                    : (byte)RazerCommandStatus.Successful;
                byte checksum = 0;
                for (int index = 2; index <= 87; index++)
                {
                    checksum ^= report[index];
                }

                report[88] = checksum;
                report.CopyTo(buffer, 1);
            }

            return Task.FromResult(new HidFeatureIoResult(
                new NativeIoCompletion(true, 91, 0),
                true,
                HidFeatureTermination.Completed));
        }

        public async Task WaitForCallsAsync(int count)
        {
            while (CallCount < count)
            {
                await called.Task;
                if (CallCount < count)
                {
                    await Task.Yield();
                }
            }
        }

    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object sync = new();
        private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;
        private readonly List<Timer> timers = [];

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => utcNow.Ticks;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new Timer(this, callback, state, utcNow + dueTime);
            lock (sync)
            {
                timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            List<Timer> due;
            lock (sync)
            {
                utcNow += amount;
                due = timers.Where(timer => !timer.Disposed && timer.Due <= utcNow).ToList();
            }

            foreach (Timer timer in due)
            {
                timer.Fire();
            }
        }

        public async Task WaitForTimerCountAsync(int count)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            while (true)
            {
                lock (sync)
                {
                    if (timers.Count >= count)
                    {
                        return;
                    }
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException($"Expected {count} timers to be registered.");
                }

                await Task.Yield();
            }
        }

        private sealed class Timer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset due) : ITimer
        {
            public DateTimeOffset Due { get; private set; } = due;

            public bool Disposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = owner.utcNow + dueTime;
                return true;
            }

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                if (!Disposed)
                {
                    Disposed = true;
                    callback(state);
                }
            }
        }
    }
}
