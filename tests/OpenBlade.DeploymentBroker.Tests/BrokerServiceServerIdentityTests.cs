using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class BrokerServiceServerIdentityTests
{
    private static readonly string ExpectedExecutable = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "OpenBlade",
        "OpenBlade.Service.exe");

    [Fact]
    public void Policy_AcceptsOnlyExactProductionServiceConfiguration()
    {
        Assert.True(BrokerServiceConfigurationPolicy.IsTrusted(
            HealthyEvidence(),
            ExpectedExecutable));
    }

    [Theory]
    [InlineData("missing-service")]
    [InlineData("not-running")]
    [InlineData("shared-process")]
    [InlineData("demand-start")]
    [InlineData("wrong-account")]
    [InlineData("wrong-image")]
    [InlineData("unquoted-image")]
    [InlineData("image-arguments")]
    [InlineData("missing-program-files")]
    [InlineData("missing-directory")]
    [InlineData("missing-executable")]
    [InlineData("program-files-reparse")]
    [InlineData("directory-reparse")]
    [InlineData("executable-reparse")]
    public void Policy_RejectsEveryServiceIdentityOrProtectedPathMismatch(string mutation)
    {
        BrokerServiceConfigurationEvidence evidence = HealthyEvidence();
        switch (mutation)
        {
            case "missing-service":
                evidence = evidence with { Exists = false };
                break;
            case "not-running":
                evidence = evidence with { IsRunning = false };
                break;
            case "shared-process":
                evidence = evidence with { ServiceType = 0x00000020 };
                break;
            case "demand-start":
                evidence = evidence with { StartType = 0x00000003 };
                break;
            case "wrong-account":
                evidence = evidence with { StartName = @"NT AUTHORITY\LocalService" };
                break;
            case "wrong-image":
                evidence = evidence with
                {
                    BinaryPath = $"\"{Path.Combine(
                        Path.GetDirectoryName(ExpectedExecutable)!,
                        "NotOpenBlade.Service.exe")}\"",
                };
                break;
            case "unquoted-image":
                evidence = evidence with { BinaryPath = ExpectedExecutable };
                break;
            case "image-arguments":
                evidence = evidence with { BinaryPath = $"\"{ExpectedExecutable}\" --service" };
                break;
            case "missing-program-files":
                evidence = evidence with { ProgramFilesDirectoryExists = false };
                break;
            case "missing-directory":
                evidence = evidence with { InstallDirectoryExists = false };
                break;
            case "missing-executable":
                evidence = evidence with { ExecutableExists = false };
                break;
            case "program-files-reparse":
                evidence = evidence with { ProgramFilesDirectoryIsReparsePoint = true };
                break;
            case "directory-reparse":
                evidence = evidence with { InstallDirectoryIsReparsePoint = true };
                break;
            case "executable-reparse":
                evidence = evidence with { ExecutableIsReparsePoint = true };
                break;
            default:
                throw new InvalidOperationException("Unknown identity mutation.");
        }

        Assert.False(BrokerServiceConfigurationPolicy.IsTrusted(
            evidence,
            ExpectedExecutable));
    }

    [Fact]
    public void WindowsProbe_UsesFixedScmKeyAndExecutablePath()
    {
        Assert.Equal("OpenBlade", WindowsBrokerServiceHealthProbe.ServiceName);
        Assert.Equal(ExpectedExecutable, WindowsBrokerServiceHealthProbe.ExpectedExecutablePath);
    }

    [Fact]
    public void ServiceExitCodes_AreDiagnosticOnlyAndRetainNumericContext()
    {
        BrokerServiceConfigurationEvidence evidence = HealthyEvidence() with
        {
            Win32ExitCode = 1067,
            ServiceSpecificExitCode = 42,
        };

        Assert.True(BrokerServiceConfigurationPolicy.IsTrusted(evidence, ExpectedExecutable));
        string context = WindowsBrokerServiceHealthProbe.FormatServiceStatusContext(evidence);
        Assert.Contains("win32ExitCode=1067 win32ExitCodeHex=0x0000042B", context, StringComparison.Ordinal);
        Assert.Contains(
            "serviceSpecificExitCode=42 serviceSpecificExitCodeHex=0x0000002A",
            context,
            StringComparison.Ordinal);
    }

    private static BrokerServiceConfigurationEvidence HealthyEvidence() => new(
        Exists: true,
        IsRunning: true,
        ServiceType: BrokerServiceConfigurationPolicy.Win32OwnProcess,
        StartType: BrokerServiceConfigurationPolicy.AutoStart,
        BinaryPath: $"\"{ExpectedExecutable.ToUpperInvariant()}\"",
        StartName: "LocalSystem",
        ProgramFilesDirectoryExists: true,
        InstallDirectoryExists: true,
        ExecutableExists: true,
        ProgramFilesDirectoryIsReparsePoint: false,
        InstallDirectoryIsReparsePoint: false,
        ExecutableIsReparsePoint: false);
}
