using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class MicrophoneMuteStateProbeCommandTests
{
    [Fact]
    public void IsRequested_RequiresTheExactStandaloneCommand()
    {
        Assert.True(MicrophoneMuteStateProbeCommand.IsRequested(
            [MicrophoneMuteStateProbeCommand.CommandName]));
        Assert.False(MicrophoneMuteStateProbeCommand.IsRequested([]));
        Assert.False(MicrophoneMuteStateProbeCommand.IsRequested(
            [MicrophoneMuteStateProbeCommand.CommandName, "extra"]));
    }

    [Fact]
    public void Run_ReportsUnmutedWithoutEndpointIdentifiers()
    {
        using var output = new StringWriter();
        var command = new MicrophoneMuteStateProbeCommand(
            () => [false, false],
            output);

        int result = command.Run();

        Assert.Equal(0, result);
        Assert.Equal(
            """
            DefaultCaptureEndpointCount=2
            MuteState=Unmuted
            Success=True

            """.ReplaceLineEndings(),
            output.ToString());
    }

    [Fact]
    public void Run_PreservesMixedAndUnavailableStates()
    {
        using var mixedOutput = new StringWriter();
        Assert.Equal(
            0,
            new MicrophoneMuteStateProbeCommand(
                () => [true, false],
                mixedOutput).Run());
        Assert.Contains("MuteState=Mixed", mixedOutput.ToString());

        using var unavailableOutput = new StringWriter();
        Assert.Equal(
            1,
            new MicrophoneMuteStateProbeCommand(
                () => [],
                unavailableOutput).Run());
        Assert.Contains("MuteState=Unavailable", unavailableOutput.ToString());
        Assert.Contains("Success=False", unavailableOutput.ToString());
    }

    [Fact]
    public void WindowsQuerySource_IsReadOnlyAndDoesNotEmitEndpointIds()
    {
        string source = File.ReadAllText(RepositoryFile(
            "src",
            "OpenBlade.SessionAgent",
            "MicrophoneMuteStateProbeCommand.cs"));

        Assert.Contains("GetMute(out bool muted)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".SetMute(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "output.WriteLine($\"Device",
            source,
            StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file {Path.Combine(relativeSegments)}.");
    }
}
