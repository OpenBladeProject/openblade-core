using OpenBlade.Contracts;

namespace OpenBlade.Setup.Tests;

public sealed class SessionAgentShutdownEndpointTests
{
    [Fact]
    public void Endpoint_IsCaseInsensitiveForSameInstalledPathAndDistinctFromTrayEndpoint()
    {
        const string agent = @"C:\Program Files\OpenBlade\OpenBlade.SessionAgent.exe";

        string first = SessionAgentShutdownEndpoint.ForExecutablePath(agent);
        string second = SessionAgentShutdownEndpoint.ForExecutablePath(agent.ToUpperInvariant());
        string tray = TrayShutdownEndpoint.ForExecutablePath(
            @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe");

        Assert.Equal(first, second);
        Assert.StartsWith(@"Local\OpenBlade.SessionAgent.Shutdown.", first, StringComparison.Ordinal);
        Assert.NotEqual(tray, first);
    }

    [Fact]
    public void Endpoint_DiffersForDevelopmentAndInstalledAgents()
    {
        Assert.NotEqual(
            SessionAgentShutdownEndpoint.ForExecutablePath(
                @"C:\Program Files\OpenBlade\OpenBlade.SessionAgent.exe"),
            SessionAgentShutdownEndpoint.ForExecutablePath(
                @"C:\worktree\OpenBlade.SessionAgent.exe"));
    }

    [Fact]
    public void Endpoint_RejectsRelativePath() =>
        Assert.Throws<ArgumentException>(() =>
            SessionAgentShutdownEndpoint.ForExecutablePath("OpenBlade.SessionAgent.exe"));
}
