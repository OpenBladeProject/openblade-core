namespace OpenBlade.DeploymentClient.Tests;

public sealed class BrokerServerIdentityPolicyTests
{
    private const string ServiceName = "OpenBlade Local Dev Deployment Broker";
    private const string Expected =
        @"C:\Program Files\OpenBlade Local Dev Deployment Broker\OpenBlade.DeploymentBroker.exe";
    private const string ExpectedCommandLine =
        "\"C:\\Program Files\\OpenBlade Local Dev Deployment Broker\\OpenBlade.DeploymentBroker.exe\"";

    [Fact]
    public void Policy_RequiresExactRunningScmServiceIdentity()
    {
        BrokerServerIdentity identity = ValidIdentity();

        Assert.True(BrokerServerIdentityPolicy.IsTrusted(identity, ServiceName, Expected));
        Assert.False(BrokerServerIdentityPolicy.IsTrusted(
            identity with { PipeServerProcessId = 0 },
            ServiceName,
            Expected));
        Assert.False(BrokerServerIdentityPolicy.IsTrusted(
            identity with { ServiceProcessId = 5678 },
            ServiceName,
            Expected));
        Assert.False(BrokerServerIdentityPolicy.IsTrusted(
            identity with { ServiceIsRunning = false },
            ServiceName,
            Expected));
        Assert.False(BrokerServerIdentityPolicy.IsTrusted(
            identity with { ServiceName = "OpenBlade" },
            ServiceName,
            Expected));
        Assert.False(BrokerServerIdentityPolicy.IsTrusted(
            identity with { ServiceType = 0x20 },
            ServiceName,
            Expected));
        Assert.False(BrokerServerIdentityPolicy.IsTrusted(
            identity with { StartType = 2 },
            ServiceName,
            Expected));
        Assert.False(BrokerServerIdentityPolicy.IsTrusted(
            identity with { StartName = "LocalService" },
            ServiceName,
            Expected));
    }

    [Theory]
    [InlineData(@"C:\Program Files\OpenBlade Local Dev Deployment Broker\OpenBlade.DeploymentBroker.exe")]
    [InlineData("\"C:\\Program Files\\OpenBlade Local Dev Deployment Broker\\OpenBlade.DeploymentBroker.exe\" --arg")]
    [InlineData("\"C:\\Users\\Public\\OpenBlade.DeploymentBroker.exe\"")]
    [InlineData("\"C:\\Program Files\\OpenBlade Local Dev Deployment Broker\\OpenBlade.DeploymentBroker.exe\\\"")]
    public void Policy_RejectsUnquotedDifferentOrParameterizedServiceCommandLine(string commandLine)
    {
        Assert.False(BrokerServerIdentityPolicy.IsTrusted(
            ValidIdentity() with { BinaryPath = commandLine },
            ServiceName,
            Expected));
    }

    private static BrokerServerIdentity ValidIdentity() => new(
        PipeServerProcessId: 1234,
        ServiceProcessId: 1234,
        ServiceIsRunning: true,
        ServiceName,
        BrokerServerIdentityPolicy.ServiceWin32OwnProcess,
        BrokerServerIdentityPolicy.ServiceDemandStart,
        ExpectedCommandLine,
        StartName: "LocalSystem");
}
