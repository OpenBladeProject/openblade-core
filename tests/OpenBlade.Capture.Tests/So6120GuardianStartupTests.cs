using System.ComponentModel;
using System.Text.Json;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class So6120GuardianStartupTests
{
    [Fact]
    public async Task RefusalSurvivesAClosedConsoleWithoutCreatingATrialRoot()
    {
        using var fixture = new Fixture();
        int result = await So6120GuardianStartup.RefuseAsync(So6120SilentPwmCommand.StopName,
            fixture.RunRoot, "isolation", So6120GuardianHost.IsolationRefused,
            new Win32Exception(5, "Synthetic job query failure"), new ClosedConsole());

        Assert.Equal(So6120GuardianHost.IsolationRefused, result);
        Assert.False(Directory.Exists(fixture.RunRoot));
        using var document = JsonDocument.Parse(File.ReadAllText(So6120GuardianStartup.GetPath(fixture.RunRoot)));
        JsonElement record = document.RootElement;
        Assert.Equal("guardian-startup-admission", record.GetProperty("recordKind").GetString());
        Assert.Equal("isolation", record.GetProperty("stage").GetString());
        Assert.False(record.GetProperty("admitted").GetBoolean());
        Assert.False(record.GetProperty("acquisitionStartedAtRecord").GetBoolean());
        Assert.Equal(5, record.GetProperty("nativeErrorCode").GetInt32());
        Assert.Equal(fixture.RunRoot, record.GetProperty("runRoot").GetString());
        Assert.Equal(So6120SilentPwmCommand.StopName, record.GetProperty("trial").GetString());
    }

    [Fact]
    public void AdmissionRecordsCallerScopeAndAmbientMembershipWithoutClaimingATrialOutcome()
    {
        using var fixture = new Fixture();
        So6120GuardianStartup.Write(So6120SilentPwmCommand.StopName, fixture.RunRoot, "admitted", true,
            new So6120GuardianIsolationEvidence(123, true));
        Assert.False(Directory.Exists(fixture.RunRoot));
        using var document = JsonDocument.Parse(File.ReadAllText(So6120GuardianStartup.GetPath(fixture.RunRoot)));
        JsonElement record = document.RootElement;
        Assert.True(record.GetProperty("admitted").GetBoolean());
        Assert.Equal("CallerProcessAndOwnedJob", record.GetProperty("recoveryScope").GetString());
        Assert.True(record.GetProperty("ambientJobMembership").GetBoolean());
        Assert.Equal(123, record.GetProperty("callerProcessId").GetInt32());
        Assert.False(record.TryGetProperty("autoConfirmed", out _));
        Assert.False(record.TryGetProperty("resolved", out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ConsoleFailurePersistsRefusalInsteadOfAnEarlierAdmission(int failureAtWrite)
    {
        using var fixture = new Fixture();
        bool admitted = await So6120GuardianStartup.CompleteAdmissionAsync(So6120SilentPwmCommand.StopName,
            fixture.RunRoot, new So6120GuardianIsolationEvidence(123, true), new FailingConsole(failureAtWrite));

        Assert.False(admitted);
        Assert.False(Directory.Exists(fixture.RunRoot));
        using var document = JsonDocument.Parse(File.ReadAllText(So6120GuardianStartup.GetPath(fixture.RunRoot)));
        JsonElement record = document.RootElement;
        Assert.False(record.GetProperty("admitted").GetBoolean());
        Assert.Equal("startup-report", record.GetProperty("stage").GetString());
        Assert.Equal("Synthetic startup output failure", record.GetProperty("errorMessage").GetString());
        Assert.Equal(123, record.GetProperty("callerProcessId").GetInt32());
    }

    [Fact]
    public async Task ReportingFailureCannotOverwritePriorOrPartialEvidence()
    {
        using var fixture = new Fixture();
        string sidecar = So6120GuardianStartup.GetPath(fixture.RunRoot);
        byte[] prior = "partial prior startup"u8.ToArray();
        File.WriteAllBytes(sidecar, prior);
        using var output = new StringWriter();
        int result = await So6120GuardianStartup.RefuseAsync(So6120SilentPwmCommand.StopName,
            fixture.RunRoot, "control", So6120GuardianHost.ControlRefused,
            new IOException("Synthetic control failure"), output);

        Assert.Equal(So6120GuardianHost.ControlRefused, result);
        Assert.Equal(prior, File.ReadAllBytes(sidecar));
        Assert.Contains("could not be saved", output.ToString());
        Assert.False(Directory.Exists(fixture.RunRoot));
    }

    [Fact]
    public void DiagnosticTextIsBoundedAndMissingParentIsNotCreated()
    {
        using var fixture = new Fixture();
        So6120GuardianStartup.Write(So6120SilentPwmCommand.StopName, fixture.RunRoot, "launch", false,
            error: new IOException(new string('X', 10000)));
        using var document = JsonDocument.Parse(File.ReadAllText(So6120GuardianStartup.GetPath(fixture.RunRoot)));
        Assert.Equal(4096, document.RootElement.GetProperty("errorMessage").GetString()!.Length);

        string missing = Path.Combine(fixture.RunRoot, "missing", "trial");
        Assert.Throws<DirectoryNotFoundException>(() => So6120GuardianStartup.Write(
            So6120SilentPwmCommand.StopName, missing, "launch", false));
        Assert.False(Directory.Exists(fixture.RunRoot));
    }

    [Theory]
    [InlineData(So6120GuardianHost.IsolationRefused)]
    [InlineData(So6120GuardianHost.ControlRefused)]
    [InlineData(So6120GuardianHost.CallerUnavailable)]
    public void CallerShowsPersistentStartupPathForPreAcquisitionFailures(int exitCode)
    {
        using var fixture = new Fixture();
        string report = So6120GuardianHost.FormatOwnerExit(exitCode, fixture.RunRoot);
        Assert.Contains(So6120GuardianStartup.GetPath(fixture.RunRoot), report);
        Assert.Contains("admission only", report);
        Assert.Contains("No hardware acquisition occurred", report);
        Assert.DoesNotContain("restored", report, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ClosedConsole : StringWriter
    {
        public override Task WriteLineAsync(string? value) => throw new IOException("Synthetic closed console");
    }

    private sealed class FailingConsole(int failureAtWrite) : StringWriter
    {
        private int writes;
        public override Task WriteLineAsync(string? value) => ++writes >= failureAtWrite
            ? throw new IOException("Synthetic startup output failure") : base.WriteLineAsync(value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "ob-guardian-startup-" + Guid.NewGuid().ToString("N"));
        internal Fixture() { Directory.CreateDirectory(directory); }
        internal string RunRoot => Path.Combine(directory, "trial");
        public void Dispose() { Directory.Delete(directory, recursive: true); }
    }
}
