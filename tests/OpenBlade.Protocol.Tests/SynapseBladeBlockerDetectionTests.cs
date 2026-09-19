using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class SynapseBladeBlockerDetectionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "openblade-blocker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset appliedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

    [Fact]
    public void Observe_VerifiedInstallationClearsOnlyAppEngineAndHardwareGuard()
    {
        RunningControllerProcess engine = CreateInstallation("any-version");
        var monitor = new WindowsControllerPresenceMonitor(new EmptyProcesses(), new EmptyServices(), () => [engine]);

        Assert.Empty(monitor.Observe().PotentialControllers);
        Assert.True(new WindowsHardwareAccessGuard(monitor).Check().Safe);
    }

    [Theory]
    [InlineData("razerwdl")]
    [InlineData("RGS")]
    [InlineData("RazerSynapseService")]
    [InlineData("Razer Synapse Service")]
    public void Observe_VerifiedEngineDoesNotWaiveOtherControllerProcesses(string processName)
    {
        RunningControllerProcess engine = CreateInstallation("independent-process");
        var monitor = new WindowsControllerPresenceMonitor(new EmptyProcesses(), new EmptyServices(),
            () => [engine, new(processName, null, null)]);

        Assert.Equal(new[] { processName }, monitor.Observe().PotentialControllers);
        Assert.False(new WindowsHardwareAccessGuard(monitor).Check().Safe);
    }

    [Fact]
    public void Observe_VerifiedEngineDoesNotWaiveIndependentServices()
    {
        RunningControllerProcess engine = CreateInstallation("independent-services");
        WindowsControllerPresenceMonitor monitor = Monitor([engine]);

        Assert.Equal(new[] { "RGS", "Razer Chroma SDK Service", "Razer Synapse Service" },
            monitor.Observe().PotentialControllers);
        Assert.False(new WindowsHardwareAccessGuard(monitor).Check().Safe);
    }

    [Fact]
    public void Observe_MissingMarkerRetainsAllControllerAliases()
    {
        RunningControllerProcess engine = CreateInstallation("missing");
        File.Delete(Manifest(engine));

        AssertConflicts(Monitor([engine, new("razerwdl", null, null)]));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("executable")]
    [InlineData("marker")]
    public void Observe_CachedInstallationIsInvalidatedWhenAFileChanges(string changed)
    {
        RunningControllerProcess engine = CreateInstallation("updated");
        WindowsControllerPresenceMonitor monitor = Monitor([engine, new("razerwdl", null, null)]);
        Assert.DoesNotContain("RazerAppEngine", monitor.Observe().PotentialControllers);

        string path = changed switch
        {
            "archive" => Archive(engine),
            "executable" => engine.ImagePath!,
            _ => Manifest(engine),
        };
        File.WriteAllText(path, "restored or updated original payload");

        AssertConflicts(monitor);
    }

    [Fact]
    public void Observe_SameLengthPayloadChangeInvalidatesCachedHash()
    {
        RunningControllerProcess engine = CreateInstallation("same-length");
        WindowsControllerPresenceMonitor monitor = Monitor([engine]);
        Assert.DoesNotContain("RazerAppEngine", monitor.Observe().PotentialControllers);
        DateTime timestamp = File.GetLastWriteTimeUtc(Archive(engine));
        File.WriteAllText(Archive(engine), new string('x', (int)new FileInfo(Archive(engine)).Length));
        File.SetLastWriteTimeUtc(Archive(engine), timestamp.AddSeconds(1));

        Assert.Contains("RazerAppEngine", monitor.Observe().PotentialControllers);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Observe_MalformedMarkerRetainsConflicts(string content)
    {
        RunningControllerProcess engine = CreateInstallation("malformed");
        File.WriteAllText(Manifest(engine), content);

        AssertConflicts(Monitor([engine, new("razerwdl", null, null)]));
    }

    [Fact]
    public void Observe_OversizedMarkerRetainsConflicts()
    {
        RunningControllerProcess engine = CreateInstallation("oversized");
        File.WriteAllText(Manifest(engine), new string(' ', 65537));

        AssertConflicts(Monitor([engine, new("razerwdl", null, null)]));
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("patchId")]
    [InlineData("archiveSha256")]
    [InlineData("executableSha256")]
    [InlineData("bladeProductIds")]
    [InlineData("appliedAtUtc")]
    public void Observe_InvalidMarkerFieldRetainsConflicts(string field)
    {
        RunningControllerProcess engine = CreateInstallation("invalid-field");
        Dictionary<string, object?> marker = Marker(engine);
        marker[field] = field switch
        {
            "schemaVersion" => 2,
            "bladeProductIds" => new[] { 0x02E0 },
            "appliedAtUtc" => DateTimeOffset.UtcNow.AddDays(1),
            _ => "unrecognized",
        };
        File.WriteAllText(Manifest(engine), JsonSerializer.Serialize(marker));

        AssertConflicts(Monitor([engine, new("razerwdl", null, null)]));
    }

    [Fact]
    public void Observe_OneUnpatchedEngineAmongPatchedEnginesRetainsConflicts()
    {
        RunningControllerProcess patched = CreateInstallation("patched");
        RunningControllerProcess original = CreateInstallation("original");
        File.Delete(Manifest(original));

        AssertConflicts(Monitor([patched, original, new("razerwdl", null, null)]));
    }

    [Fact]
    public void Observe_RootLauncherWithoutItsOwnVerifiedArchiveRetainsConflicts()
    {
        RunningControllerProcess patched = CreateInstallation("versioned");
        var launcher = new RunningControllerProcess("RazerAppEngine", Path.Combine(directory, "RazerAppEngine.exe"), DateTimeOffset.UtcNow);

        AssertConflicts(Monitor([patched, launcher, new("razerwdl", null, null)]));
    }

    [Fact]
    public void Observe_InaccessibleEngineRetainsConflicts()
    {
        RunningControllerProcess engine = CreateInstallation("accessible");

        AssertConflicts(Monitor([engine, new("RazerAppEngine", null, null), new("razerwdl", null, null)]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Observe_EngineStartedBeforeOrAtApplyCannotUseMarker(int minutes)
    {
        RunningControllerProcess engine = CreateInstallation("old-process") with { StartedAtUtc = appliedAt.AddMinutes(minutes) };

        AssertConflicts(Monitor([engine, new("razerwdl", null, null)]));
    }

    [Fact]
    public void Observe_InstalledMarkerWithoutLiveEngineCannotSuppressServices()
    {
        CreateInstallation("stopped");

        Assert.Contains("RGS", Monitor([]).Observe().PotentialControllers);
    }

    [Fact]
    public void Observe_ProcessEnumerationFailureRemainsUnsafe()
    {
        var monitor = new WindowsControllerPresenceMonitor(new EmptyProcesses(), new Services(),
            () => throw new InvalidOperationException());

        HardwareAccessDecision result = new WindowsHardwareAccessGuard(monitor).Check();
        Assert.False(result.Safe);
        Assert.Equal("Process enumeration failed.", result.DetectionFailure);
        Assert.Contains("RGS", result.Conflicts);
    }

    [Fact]
    public void Observe_VerifiedEngineDoesNotHideServiceEnumerationFailure()
    {
        RunningControllerProcess engine = CreateInstallation("service-failure");
        var monitor = new WindowsControllerPresenceMonitor(new EmptyProcesses(), new ThrowingServices(), () => [engine]);

        HardwareAccessDecision result = new WindowsHardwareAccessGuard(monitor).Check();
        Assert.False(result.Safe);
        Assert.Equal("Windows service enumeration failed.", result.DetectionFailure);
    }

    private static void AssertConflicts(WindowsControllerPresenceMonitor monitor)
    {
        ControllerPresenceObservation result = monitor.Observe();
        Assert.Contains("RazerAppEngine", result.PotentialControllers);
        Assert.Contains("razerwdl", result.PotentialControllers);
        Assert.Contains("RGS", result.PotentialControllers);
        Assert.False(new WindowsHardwareAccessGuard(monitor).Check().Safe);
        Assert.Null(result.DetectionWarning);
    }

    private RunningControllerProcess CreateInstallation(string name)
    {
        string installation = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.Combine(installation, "resources"));
        var engine = new RunningControllerProcess("RazerAppEngine", Path.Combine(installation, "RazerAppEngine.exe"), DateTimeOffset.UtcNow);
        File.WriteAllText(engine.ImagePath!, "cooperative fixture executable");
        File.WriteAllText(Archive(engine), "cooperative fixture archive");
        File.WriteAllText(Manifest(engine), JsonSerializer.Serialize(Marker(engine)));
        return engine;
    }

    private Dictionary<string, object?> Marker(RunningControllerProcess engine) => new()
    {
        ["schemaVersion"] = 1,
        ["patchId"] = "OSSBlade/synapse-blade-blocker",
        ["archiveSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Archive(engine)))),
        ["executableSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(engine.ImagePath!))),
        ["appliedAtUtc"] = appliedAt,
        ["bladeProductIds"] = new[] { 0x02E0, 0x02C6 },
    };

    private static string Archive(RunningControllerProcess engine) => Path.Combine(Path.GetDirectoryName(engine.ImagePath!)!, "resources", "app.asar");
    private static string Manifest(RunningControllerProcess engine) => Path.Combine(Path.GetDirectoryName(engine.ImagePath!)!, "resources", "synapse-blade-blocker.json");
    private static WindowsControllerPresenceMonitor Monitor(IReadOnlyList<RunningControllerProcess> processes) =>
        new(new EmptyProcesses(), new Services(), () => processes);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class EmptyProcesses : IRunningProcessSource
    {
        public IReadOnlyList<string> GetProcessNames() => [];
    }

    private sealed class EmptyServices : IWindowsServiceSource
    {
        public IReadOnlyList<WindowsServiceSnapshot> GetServices() => [];
    }

    private sealed class Services : IWindowsServiceSource
    {
        public IReadOnlyList<WindowsServiceSnapshot> GetServices() =>
        [
            new("RGS", "RGS", WindowsServiceState.Running),
            new("RazerSynapseService", "Razer Synapse Service", WindowsServiceState.Running),
            new("Chroma", "Razer Chroma SDK Service", WindowsServiceState.Running),
        ];
    }

    private sealed class ThrowingServices : IWindowsServiceSource
    {
        public IReadOnlyList<WindowsServiceSnapshot> GetServices() => throw new InvalidOperationException();
    }
}
