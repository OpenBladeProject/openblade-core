namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionAgentCrashReportingContractTests
{
    [Fact]
    public void MainWritesFatalFailuresWithoutSuppressingTheExitCode()
    {
        string program = ReadRepositoryFile(
            "src",
            "OpenBlade.SessionAgent",
            "Program.cs");

        Assert.Contains("catch (Exception exception)", program, StringComparison.Ordinal);
        Assert.Contains("CrashReportWriter.CreateDefault", program, StringComparison.Ordinal);
        Assert.Contains("\"OpenBlade.SessionAgent\"", program, StringComparison.Ordinal);
        Assert.Contains("processTerminating: true", program, StringComparison.Ordinal);
        Assert.Contains("ExceptionDispatchInfo.Capture(terminalFailure).Throw()", program,
            StringComparison.Ordinal);
        Assert.Contains("return 1;", program, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null
            && !File.Exists(Path.Combine(directory, "OpenBlade.slnx")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        Assert.False(string.IsNullOrWhiteSpace(directory));
        return File.ReadAllText(Path.Combine([directory!, .. parts]));
    }
}
