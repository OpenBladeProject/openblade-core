using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsControllerPresenceMonitorTests
{
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
    public void Observe_ReturnsEachExactProcessAlias(string alias)
    {
        WindowsControllerPresenceMonitor monitor = CreateMonitor([alias], []);

        ControllerPresenceObservation result = monitor.Observe();

        Assert.Equal([alias], result.PotentialControllers);
        Assert.Null(result.DetectionWarning);
    }

    [Theory]
    [MemberData(nameof(KnownServiceAliases))]
    public void Observe_ReturnsEachExactActiveServiceAlias(string alias)
    {
        WindowsControllerPresenceMonitor monitor = CreateMonitor(
            [],
            [new WindowsServiceSnapshot(
                alias == "RGS" ? alias : "unmatched",
                alias,
                WindowsServiceState.Running)]);

        ControllerPresenceObservation result = monitor.Observe();

        Assert.Equal([alias], result.PotentialControllers);
    }

    [Theory]
    [MemberData(nameof(NonControllerRazerServices))]
    public void Observe_IgnoresRazerSupportServicesThatDoNotControlTheLaptop(string alias)
    {
        WindowsControllerPresenceMonitor monitor = CreateMonitor(
            [],
            [new WindowsServiceSnapshot(alias, alias, WindowsServiceState.Running)]);

        ControllerPresenceObservation result = monitor.Observe();

        Assert.Empty(result.PotentialControllers);
        Assert.Null(result.DetectionWarning);
    }

    [Theory]
    [InlineData(WindowsServiceState.Stopped)]
    [InlineData(WindowsServiceState.Other)]
    public void Observe_IgnoresInactiveServices(WindowsServiceState state)
    {
        WindowsControllerPresenceMonitor monitor = CreateMonitor(
            [],
            [new WindowsServiceSnapshot("RGS", "RGS", state)]);

        Assert.Empty(monitor.Observe().PotentialControllers);
    }

    [Fact]
    public void Observe_UsesExactNamesAndRejectsPrefixesAndSuffixes()
    {
        WindowsControllerPresenceMonitor monitor = CreateMonitor(
            ["RazerApp", "RazerAppEngineHelper"],
            [new WindowsServiceSnapshot(
                "RGSHelper",
                "Razer Synapse Service Helper",
                WindowsServiceState.Running)]);

        Assert.Empty(monitor.Observe().PotentialControllers);
    }

    [Fact]
    public void Observe_DoesNotTreatRgsAsADisplayNameAlias()
    {
        WindowsControllerPresenceMonitor monitor = CreateMonitor(
            [],
            [new WindowsServiceSnapshot("unexpected", "RGS", WindowsServiceState.Running)]);

        Assert.Empty(monitor.Observe().PotentialControllers);
    }

    [Fact]
    public void Observe_DeduplicatesCaseInsensitivelyAndOrdersOrdinally()
    {
        WindowsControllerPresenceMonitor monitor = CreateMonitor(
            ["razerwdl", "RazerAppEngine", "RAZERAPPENGINE"],
            [
                new WindowsServiceSnapshot(
                    "RGS",
                    "unknown",
                    WindowsServiceState.Running),
                new WindowsServiceSnapshot(
                    "unknown",
                    "rGs",
                    WindowsServiceState.StartPending),
            ]);

        Assert.Equal(
            ["RGS", "RazerAppEngine", "razerwdl"],
            monitor.Observe().PotentialControllers);
    }

    [Theory]
    [InlineData(true, false, "Process enumeration failed.")]
    [InlineData(false, true, "Windows service enumeration failed.")]
    [InlineData(true, true, "Process and Windows service enumeration failed.")]
    public void Observe_ReportsStableSourceDetectionWarnings(
        bool processFailure,
        bool serviceFailure,
        string expectedWarning)
    {
        IRunningProcessSource processes = processFailure
            ? new ThrowingProcessSource()
            : new FakeProcessSource(["RazerAppEngine"]);
        IWindowsServiceSource services = serviceFailure
            ? new ThrowingServiceSource()
            : new FakeServiceSource([]);
        var monitor = new WindowsControllerPresenceMonitor(processes, services);

        ControllerPresenceObservation result = monitor.Observe();

        Assert.Equal(expectedWarning, result.DetectionWarning);
        Assert.Equal(
            processFailure ? [] : ["RazerAppEngine"],
            result.PotentialControllers);
    }

    private static WindowsControllerPresenceMonitor CreateMonitor(
        IReadOnlyList<string> processes,
        IReadOnlyList<WindowsServiceSnapshot> services) =>
        new(new FakeProcessSource(processes), new FakeServiceSource(services));

    private sealed class FakeProcessSource(IReadOnlyList<string> names) : IRunningProcessSource
    {
        public IReadOnlyList<string> GetProcessNames() => names;
    }

    private sealed class ThrowingProcessSource : IRunningProcessSource
    {
        public IReadOnlyList<string> GetProcessNames() => throw new InvalidOperationException();
    }

    private sealed class FakeServiceSource(
        IReadOnlyList<WindowsServiceSnapshot> services) : IWindowsServiceSource
    {
        public IReadOnlyList<WindowsServiceSnapshot> GetServices() => services;
    }

    private sealed class ThrowingServiceSource : IWindowsServiceSource
    {
        public IReadOnlyList<WindowsServiceSnapshot> GetServices() =>
            throw new InvalidOperationException();
    }
}
