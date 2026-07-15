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
    public async Task CreateWindowsQuerySequence_ConfiguresTenMillisecondFeatureReportSettle()
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
        time.Advance(TimeSpan.FromMilliseconds(9));
        await Task.Yield();
        Assert.Equal(1, io.CallCount);
        Assert.False(query.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(BatteryValidationSetting.ProtectionOff, (await query).Value);
        Assert.Equal(2, io.CallCount);
    }

    [Fact]
    public async Task CreateWindows_LeavesFeatureReportSettleAtZero()
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
        Task secondCall = io.WaitForSecondCallAsync();
        bool submittedWithoutAdvance = ReferenceEquals(
            await Task.WhenAny(secondCall, Task.Delay(TimeSpan.FromSeconds(1))),
            secondCall);
        if (!submittedWithoutAdvance)
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.True(submittedWithoutAdvance);
        Assert.Equal(BatteryValidationSetting.ProtectionOff, (await query).Value);
        Assert.Equal(2, io.CallCount);
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
        private readonly OpenedCandidateHandle candidate = new(
            (SafeFileHandle)RuntimeHelpers.GetUninitializedObject(typeof(SafeFileHandle)),
            static () => { });

        public IReadOnlyList<string> EnumerateDevicePaths() => ["exact"];

        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters) =>
            new(candidate, null);

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
        private readonly TaskCompletionSource<bool> secondCalled = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public Task<HidFeatureIoResult> ExecuteAsync(
            OpenedHidHandle handle,
            uint controlCode,
            byte[] buffer,
            CancellationToken callerCancellation,
            CancellationToken deadlineCancellation)
        {
            int currentCall = Interlocked.Increment(ref callCount);
            called.TrySetResult(true);
            if (currentCall == 2)
            {
                secondCalled.TrySetResult(true);
            }
            if (controlCode == 0x000B0192)
            {
                byte[] report = RazerReport.Create(1, 0x07, 0x92, [0x50]).Encode();
                report[0] = (byte)RazerCommandStatus.Successful;
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

        public Task WaitForSecondCallAsync() => secondCalled.Task;
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
