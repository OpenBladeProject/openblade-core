using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsHardwareAccessGuardTests
{
    [Fact]
    public void Check_MapsControllerPresenceObservationIntoStrictDecision()
    {
        var guard = new WindowsHardwareAccessGuard(
            new ScriptedMonitor(new(["RazerAppEngine"], "source warning")));

        HardwareAccessDecision result = guard.Check();

        Assert.Equal(["RazerAppEngine"], result.Conflicts);
        Assert.Equal("source warning", result.DetectionFailure);
    }

    public static TheoryData<string> KnownProcessAliases => new()
    {
        "RazerAppEngine",
        "razerwdl",
    };

    public static TheoryData<string> KnownServiceAliases => new()
    {
        "Razer Chroma SDK Diagnostic Service",
        "Razer Chroma SDK Server",
        "Razer Chroma SDK Service",
        "Razer Chroma Stream Server",
        "RGS",
        "Razer Gaming Services Updater",
        "Razer Synapse Service",
    };

    public static TheoryData<string> NonControllerRazerServices => new()
    {
        "Razer Elevation Service",
        "Razer Game Manager Service 3",
    };

    [Theory]
    [MemberData(nameof(KnownProcessAliases))]
    public void Check_ReturnsEachKnownProcessAlias(string alias)
    {
        WindowsHardwareAccessGuard guard = CreateGuard([alias], []);

        HardwareAccessDecision result = guard.Check();

        Assert.False(result.Safe);
        Assert.Equal([alias], result.Conflicts);
        Assert.Null(result.DetectionFailure);
    }

    [Theory]
    [MemberData(nameof(KnownServiceAliases))]
    public void Check_ReturnsEachKnownActiveServiceAlias(string alias)
    {
        WindowsHardwareAccessGuard guard = CreateGuard(
            [],
            [new WindowsServiceSnapshot(
                alias == "RGS" ? alias : "unmatched-internal-name",
                alias,
                WindowsServiceState.Running)]);

        HardwareAccessDecision result = guard.Check();

        Assert.False(result.Safe);
        Assert.Equal([alias], result.Conflicts);
        Assert.Null(result.DetectionFailure);
    }

    [Theory]
    [MemberData(nameof(NonControllerRazerServices))]
    public void Check_IgnoresRazerSupportServicesThatDoNotControlTheLaptop(string alias)
    {
        WindowsHardwareAccessGuard guard = CreateGuard(
            [],
            [new WindowsServiceSnapshot(alias, alias, WindowsServiceState.Running)]);

        HardwareAccessDecision result = guard.Check();

        Assert.True(result.Safe);
        Assert.Empty(result.Conflicts);
    }

    [Theory]
    [InlineData(WindowsServiceState.Running)]
    [InlineData(WindowsServiceState.StartPending)]
    [InlineData(WindowsServiceState.ContinuePending)]
    public void Check_ReturnsKnownServiceInEachActiveState(WindowsServiceState state)
    {
        WindowsHardwareAccessGuard guard = CreateGuard(
            [],
            [new WindowsServiceSnapshot("RazerSynapseService", "Razer Synapse Service", state)]);

        HardwareAccessDecision result = guard.Check();

        Assert.False(result.Safe);
        Assert.Equal(["Razer Synapse Service"], result.Conflicts);
    }

    [Theory]
    [InlineData(WindowsServiceState.Stopped)]
    [InlineData(WindowsServiceState.Other)]
    public void Check_IgnoresKnownServiceInInactiveState(WindowsServiceState state)
    {
        WindowsHardwareAccessGuard guard = CreateGuard(
            [],
            [new WindowsServiceSnapshot("RazerSynapseService", "Razer Synapse Service", state)]);

        HardwareAccessDecision result = guard.Check();

        Assert.True(result.Safe);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Check_MatchesServiceNameWhenDisplayNameIsUnknown()
    {
        WindowsHardwareAccessGuard guard = CreateGuard(
            [],
            [new WindowsServiceSnapshot("RGS", "Unknown display name", WindowsServiceState.Running)]);

        HardwareAccessDecision result = guard.Check();

        Assert.Equal(["RGS"], result.Conflicts);
    }

    [Fact]
    public void Check_MatchesSynapseInternalServiceNameWhenDisplayNameIsUnknown()
    {
        WindowsHardwareAccessGuard guard = CreateGuard(
            [],
            [new WindowsServiceSnapshot(
                "RazerSynapseService",
                "Unknown display name",
                WindowsServiceState.Running)]);

        HardwareAccessDecision result = guard.Check();

        Assert.Equal(["RazerSynapseService"], result.Conflicts);
    }

    [Fact]
    public void Check_UsesExactAliasesAndRejectsPrefixesAndSuffixes()
    {
        WindowsHardwareAccessGuard guard = CreateGuard(
            ["RazerApp", "RazerAppEngineHelper", "notepad"],
            [
                new WindowsServiceSnapshot(
                    "RazerSynapse",
                    "Razer Synapse Service Helper",
                    WindowsServiceState.Running),
            ]);

        HardwareAccessDecision result = guard.Check();

        Assert.True(result.Safe);
        Assert.Empty(result.Conflicts);
        Assert.Null(result.DetectionFailure);
    }

    [Fact]
    public void Check_DeduplicatesAliasesCaseInsensitivelyAcrossSources()
    {
        WindowsHardwareAccessGuard guard = CreateGuard(
            ["Razer Synapse Service", "RAZER SYNAPSE SERVICE"],
            [
                new WindowsServiceSnapshot(
                    "RazerSynapseService",
                    "razer synapse service",
                    WindowsServiceState.Running),
            ]);

        HardwareAccessDecision result = guard.Check();

        Assert.False(result.Safe);
        Assert.Equal(["Razer Synapse Service"], result.Conflicts);
    }

    [Fact]
    public void Check_ReturnsDeduplicatedConflictsInDeterministicOrdinalOrder()
    {
        WindowsHardwareAccessGuard guard = CreateGuard(
            ["razerwdl", "RazerAppEngine", "RAZERAPPENGINE"],
            []);

        HardwareAccessDecision result = guard.Check();

        Assert.Equal(
            ["RazerAppEngine", "razerwdl"],
            result.Conflicts);
    }

    [Fact]
    public void Check_ProcessFailureIsUnsafeAndPreservesServiceConflict()
    {
        WindowsHardwareAccessGuard guard = new(
            new ThrowingProcessSource(),
            new FakeServiceSource([
                new WindowsServiceSnapshot(
                    "RazerSynapseService",
                    "Razer Synapse Service",
                    WindowsServiceState.Running),
            ]));

        HardwareAccessDecision result = guard.Check();

        Assert.False(result.Safe);
        Assert.Equal(["Razer Synapse Service"], result.Conflicts);
        Assert.Equal("Process enumeration failed.", result.DetectionFailure);
    }

    [Fact]
    public void Check_ServiceFailureIsUnsafeAndPreservesProcessConflict()
    {
        WindowsHardwareAccessGuard guard = new(
            new FakeProcessSource(["RazerAppEngine"]),
            new ThrowingServiceSource());

        HardwareAccessDecision result = guard.Check();

        Assert.False(result.Safe);
        Assert.Equal(["RazerAppEngine"], result.Conflicts);
        Assert.Equal("Windows service enumeration failed.", result.DetectionFailure);
    }

    [Fact]
    public void Check_BothSourceFailuresUseCombinedStableFailure()
    {
        WindowsHardwareAccessGuard guard = new(
            new ThrowingProcessSource(),
            new ThrowingServiceSource());

        HardwareAccessDecision result = guard.Check();

        Assert.False(result.Safe);
        Assert.Empty(result.Conflicts);
        Assert.Equal(
            "Process and Windows service enumeration failed.",
            result.DetectionFailure);
    }

    private static WindowsHardwareAccessGuard CreateGuard(
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

    private sealed class ScriptedMonitor(ControllerPresenceObservation observation)
        : IControllerPresenceMonitor
    {
        public ControllerPresenceObservation Observe() => observation;
    }
}
