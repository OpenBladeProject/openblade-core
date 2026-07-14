using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class WindowsConflictDetectorTests
{
    [Fact]
    public void Check_ReturnsOnlyExactKnownProcessAliases()
    {
        WindowsConflictDetector detector = CreateDetector(
            [
                "notepad",
                "GameManagerService3",
                "razer_elevation_service",
                "RazerAppEngine",
                "RazerAppEngineHelper",
                "razerwdl",
            ],
            []);

        ConflictCheck result = detector.Check();

        Assert.False(result.Safe);
        Assert.Equal(
            ["GameManagerService3", "razer_elevation_service", "RazerAppEngine", "razerwdl"],
            result.Conflicts);
        Assert.Null(result.DetectionFailure);
    }

    public static TheoryData<string> KnownServiceAliases => new()
    {
        "Razer Chroma SDK Diagnostic Service",
        "Razer Chroma SDK Server",
        "Razer Chroma SDK Service",
        "Razer Chroma Stream Server",
        "Razer Elevation Service",
        "Razer Game Manager Service 3",
        "RGS",
        "Razer Gaming Services Updater",
        "Razer Synapse Service",
    };

    [Theory]
    [MemberData(nameof(KnownServiceAliases))]
    public void Check_ReturnsEachVerifiedActiveServiceAlias(string alias)
    {
        WindowsConflictDetector detector = CreateDetector(
            [],
            [new WindowsServiceSnapshot("unmatched-internal-name", alias, WindowsServiceState.Running)]);

        ConflictCheck result = detector.Check();

        Assert.False(result.Safe);
        Assert.Equal([alias], result.Conflicts);
    }

    [Theory]
    [InlineData(WindowsServiceState.Running)]
    [InlineData(WindowsServiceState.StartPending)]
    [InlineData(WindowsServiceState.ContinuePending)]
    public void Check_ReturnsKnownActiveService(WindowsServiceState state)
    {
        WindowsConflictDetector detector = CreateDetector(
            [],
            [new WindowsServiceSnapshot("RazerSynapseService", "Razer Synapse Service", state)]);

        ConflictCheck result = detector.Check();

        Assert.False(result.Safe);
        Assert.Equal(["Razer Synapse Service"], result.Conflicts);
        Assert.Null(result.DetectionFailure);
    }

    [Fact]
    public void Check_IgnoresStoppedKnownService()
    {
        WindowsConflictDetector detector = CreateDetector(
            [],
            [new WindowsServiceSnapshot(
                "unmatched-internal-name",
                "Razer Chroma Stream Server",
                WindowsServiceState.Stopped)]);

        ConflictCheck result = detector.Check();

        Assert.True(result.Safe);
        Assert.Empty(result.Conflicts);
        Assert.Null(result.DetectionFailure);
    }

    [Fact]
    public void Check_IgnoresOtherKnownServiceState()
    {
        WindowsConflictDetector detector = CreateDetector(
            [],
            [new WindowsServiceSnapshot(
                "unmatched-internal-name",
                "Razer Synapse Service",
                WindowsServiceState.Other)]);

        ConflictCheck result = detector.Check();

        Assert.True(result.Safe);
        Assert.Empty(result.Conflicts);
        Assert.Null(result.DetectionFailure);
    }

    [Fact]
    public void Check_DeduplicatesProcessAndServiceAliasesCaseInsensitively()
    {
        WindowsConflictDetector detector = CreateDetector(
            ["Razer Synapse Service", "RAZER SYNAPSE SERVICE"],
            [new WindowsServiceSnapshot(
                "RazerSynapseService",
                "razer synapse service",
                WindowsServiceState.Running)]);

        ConflictCheck result = detector.Check();

        Assert.False(result.Safe);
        Assert.Equal(["Razer Synapse Service"], result.Conflicts);
    }

    [Fact]
    public void Check_ProcessFailureIsUnsafeAndPreservesServiceConflict()
    {
        WindowsConflictDetector detector = new(
            new ThrowingProcessSource(),
            new FakeServiceSource([
                new WindowsServiceSnapshot(
                    "RazerSynapseService",
                    "Razer Synapse Service",
                    WindowsServiceState.Running),
            ]));

        ConflictCheck result = detector.Check();

        Assert.False(result.Safe);
        Assert.Equal(["Razer Synapse Service"], result.Conflicts);
        Assert.Equal("Process enumeration failed.", result.DetectionFailure);
    }

    [Fact]
    public void Check_ServiceFailureIsUnsafeAndPreservesProcessConflict()
    {
        WindowsConflictDetector detector = new(
            new FakeProcessSource(["RazerAppEngine"]),
            new ThrowingServiceSource());

        ConflictCheck result = detector.Check();

        Assert.False(result.Safe);
        Assert.Equal(["RazerAppEngine"], result.Conflicts);
        Assert.Equal("Windows service enumeration failed.", result.DetectionFailure);
    }

    [Fact]
    public void Check_BothSourceFailuresUseCombinedStableFailure()
    {
        WindowsConflictDetector detector = new(
            new ThrowingProcessSource(),
            new ThrowingServiceSource());

        ConflictCheck result = detector.Check();

        Assert.False(result.Safe);
        Assert.Empty(result.Conflicts);
        Assert.Equal(
            "Process and Windows service enumeration failed.",
            result.DetectionFailure);
    }

    private static WindowsConflictDetector CreateDetector(
        IReadOnlyList<string> processes,
        IReadOnlyList<WindowsServiceSnapshot> services) =>
        new(new FakeProcessSource(processes), new FakeServiceSource(services));

    private sealed class FakeProcessSource(IReadOnlyList<string> names) : IRunningProcessSource
    {
        public IReadOnlyList<string> GetProcessNames() => names;
    }

    private sealed class ThrowingProcessSource : IRunningProcessSource
    {
        public IReadOnlyList<string> GetProcessNames() =>
            throw new Exception("unstable detail");
    }

    private sealed class FakeServiceSource(
        IReadOnlyList<WindowsServiceSnapshot> services) : IWindowsServiceSource
    {
        public IReadOnlyList<WindowsServiceSnapshot> GetServices() => services;
    }

    private sealed class ThrowingServiceSource : IWindowsServiceSource
    {
        public IReadOnlyList<WindowsServiceSnapshot> GetServices() =>
            throw new Exception("unstable detail");
    }
}
