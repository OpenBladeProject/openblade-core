using System.Text.Json;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class DynamicLightingIdentityProbeTests
{
    [Fact]
    public void IsRequested_RecognizesOnlyExplicitProbePrefix()
    {
        Assert.True(DynamicLightingIdentityProbeCommand.IsRequested(
            [DynamicLightingIdentityProbeCommand.Argument, "report.json"]));
        Assert.True(DynamicLightingIdentityProbeCommand.IsRequested(
            ["--PROBE-DYNAMIC-LIGHTING-IDENTITY"]));
        Assert.False(DynamicLightingIdentityProbeCommand.IsRequested([]));
        Assert.False(DynamicLightingIdentityProbeCommand.IsRequested(
            ["--different-command"]));
    }

    [Fact]
    public async Task ExactIdentityAndAvailableApprovedArray_WritesSuccessfulSanitizedReport()
    {
        using TemporaryProbeDirectory temporary = new();
        string reportPath = temporary.NewReportPath();
        DynamicLightingIdentityProbeCommand command = new(
            new IdentitySource(ExactIdentity()),
            new LampArraySource(ApprovedObservation(isAvailable: true)));

        int exitCode = await command.RunAsync(
            [DynamicLightingIdentityProbeCommand.Argument, reportPath],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        DynamicLightingIdentityProbeReport report = ReadReport(reportPath);
        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal("OpenBlade.DynamicLightingIdentity", report.Probe);
        Assert.Equal(DynamicLightingPackageIdentityState.Exact, report.PackageIdentity);
        Assert.Equal(DynamicLightingPackageIdentityState.Exact, report.ApplicationIdentity);
        Assert.Equal(1, report.ExactArrayCount);
        Assert.True(report.Connected);
        Assert.True(report.Enabled);
        Assert.True(report.Available);
        Assert.True(report.GeometryApproved);
        Assert.Null(report.FailureType);
        Assert.Null(report.FailureHResult);
        Assert.True(report.Success);

        string json = File.ReadAllText(reportPath);
        Assert.Contains("\"PackageIdentity\": \"Exact\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Publisher", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UserSid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GeometrySha256", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData((int)DynamicLightingPackageIdentityState.Unavailable, true)]
    [InlineData((int)DynamicLightingPackageIdentityState.Mismatch, true)]
    [InlineData((int)DynamicLightingPackageIdentityState.Exact, false)]
    public async Task MissingIdentityOrAvailability_WritesHonestFailureReport(
        int identityStateValue,
        bool available)
    {
        DynamicLightingPackageIdentityState identityState =
            (DynamicLightingPackageIdentityState)identityStateValue;
        using TemporaryProbeDirectory temporary = new();
        string reportPath = temporary.NewReportPath();
        DynamicLightingIdentityProbeCommand command = new(
            new IdentitySource(new(identityState, identityState)),
            new LampArraySource(ApprovedObservation(available)));

        int exitCode = await command.RunAsync(
            [DynamicLightingIdentityProbeCommand.Argument, reportPath],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        DynamicLightingIdentityProbeReport report = ReadReport(reportPath);
        Assert.Equal(identityState, report.PackageIdentity);
        Assert.Equal(available, report.Available);
        Assert.False(report.Success);
    }

    [Fact]
    public async Task ObservationFailure_RetainsOnlyTypeAndHResult()
    {
        using TemporaryProbeDirectory temporary = new();
        string reportPath = temporary.NewReportPath();
        const string privateDetail = "private-device-identifier";
        DynamicLightingIdentityProbeCommand command = new(
            new IdentitySource(ExactIdentity()),
            new LampArraySource(new IOException(privateDetail)));

        int exitCode = await command.RunAsync(
            [DynamicLightingIdentityProbeCommand.Argument, reportPath],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        DynamicLightingIdentityProbeReport report = ReadReport(reportPath);
        Assert.Equal(
            DynamicLightingPackageIdentityState.Exact,
            report.PackageIdentity);
        Assert.Equal("IOException", report.FailureType);
        Assert.StartsWith("0x", report.FailureHResult, StringComparison.Ordinal);
        Assert.False(report.Success);
        Assert.DoesNotContain(
            privateDetail,
            File.ReadAllText(reportPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingOutputFile_IsNeverOverwritten()
    {
        using TemporaryProbeDirectory temporary = new();
        string reportPath = temporary.NewReportPath();
        File.WriteAllText(reportPath, "preserve");
        DynamicLightingIdentityProbeCommand command = new(
            new IdentitySource(ExactIdentity()),
            new LampArraySource(ApprovedObservation(isAvailable: true)));

        int exitCode = await command.RunAsync(
            [DynamicLightingIdentityProbeCommand.Argument, reportPath],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal("preserve", File.ReadAllText(reportPath));
    }

    [Theory]
    [InlineData()]
    [InlineData("--probe-dynamic-lighting-identity")]
    [InlineData("--probe-dynamic-lighting-identity", "relative.json")]
    [InlineData("--probe-dynamic-lighting-identity", "C:\\missing-parent\\report.json")]
    [InlineData("--probe-dynamic-lighting-identity", "C:\\report.txt")]
    public async Task InvalidInvocation_DoesNotCreateAReport(params string[] arguments)
    {
        DynamicLightingIdentityProbeCommand command = new(
            new IdentitySource(ExactIdentity()),
            new LampArraySource(ApprovedObservation(isAvailable: true)));

        int exitCode = await command.RunAsync(arguments, CancellationToken.None);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void ProgramRoutesProbeBeforeNormalSessionAgentStartup()
    {
        string program = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src",
                "OpenBlade.SessionAgent",
                "Program.cs"));

        int probeBranch = program.IndexOf(
            "DynamicLightingIdentityProbeCommand.IsRequested(args)",
            StringComparison.Ordinal);
        int startupOwnership = program.IndexOf(
            "TryAcquireStartupOwnership(",
            StringComparison.Ordinal);
        Assert.True(probeBranch >= 0);
        Assert.True(startupOwnership > probeBranch);
        Assert.Contains(
            "The explicit probe never writes a crash report",
            program,
            StringComparison.Ordinal);
    }

    private static DynamicLightingPackageIdentityObservation ExactIdentity() =>
        new(
            DynamicLightingPackageIdentityState.Exact,
            DynamicLightingPackageIdentityState.Exact);

    private static LampArrayObservation ApprovedObservation(bool isAvailable) =>
        new(
            EnumeratedDeviceCount: 1,
            ExactDeviceCount: 1,
            Kind: Rz090528LampArrayGeometry.Kind,
            LampCount: Rz090528LampArrayGeometry.LampCount,
            IsConnected: true,
            IsEnabled: true,
            IsAvailable: isAvailable,
            SupportsVirtualKeys: true,
            MinimumUpdateIntervalMicroseconds: 33333,
            GeometrySha256: Rz090528LampArrayGeometry.Sha256);

    private static DynamicLightingIdentityProbeReport ReadReport(string reportPath)
    {
        DynamicLightingIdentityProbeReport? report =
            JsonSerializer.Deserialize<DynamicLightingIdentityProbeReport>(
                File.ReadAllText(reportPath));
        return Assert.IsType<DynamicLightingIdentityProbeReport>(report);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OpenBlade.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the OpenBlade repository root.");
    }

    private sealed class IdentitySource(DynamicLightingPackageIdentityObservation observation)
        : IDynamicLightingPackageIdentitySource
    {
        public DynamicLightingPackageIdentityObservation Observe() => observation;
    }

    private sealed class LampArraySource : ILampArrayObservationSource
    {
        private readonly LampArrayObservation? observation;
        private readonly Exception? failure;

        public LampArraySource(LampArrayObservation observation)
        {
            this.observation = observation;
        }

        public LampArraySource(Exception failure)
        {
            this.failure = failure;
        }

        public Task<LampArrayObservation> ObserveAsync(
            CancellationToken cancellationToken) =>
            failure is null
                ? Task.FromResult(observation!)
                : Task.FromException<LampArrayObservation>(failure);
    }

    private sealed class TemporaryProbeDirectory : IDisposable
    {
        private const string Prefix = "OpenBlade.IdentityProbe.Tests.";

        public TemporaryProbeDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string NewReportPath() =>
            System.IO.Path.Combine(Path, Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            string fullPath = System.IO.Path.GetFullPath(Path);
            string expectedParent = System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (Directory.Exists(fullPath)
                && string.Equals(
                    System.IO.Path.GetDirectoryName(fullPath),
                    expectedParent,
                    StringComparison.OrdinalIgnoreCase)
                && System.IO.Path.GetFileName(fullPath).StartsWith(
                    Prefix,
                    StringComparison.Ordinal))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
    }
}
