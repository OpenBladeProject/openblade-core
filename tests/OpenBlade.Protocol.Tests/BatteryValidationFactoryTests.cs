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
}
