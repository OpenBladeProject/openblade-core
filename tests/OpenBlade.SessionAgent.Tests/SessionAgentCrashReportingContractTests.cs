namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionAgentCrashReportingContractTests
{
    [Fact]
    public void MainWritesFatalFailuresAndPreservesUnhandledTermination()
    {
        string program = ReadRepositoryFile(
            "src",
            "OpenBlade.SessionAgent",
            "Program.cs");
        int runInvocation = program.IndexOf(
            "return RunAsync(",
            StringComparison.Ordinal);
        int catchStart = program.IndexOf(
            "catch (Exception exception)",
            runInvocation,
            StringComparison.Ordinal);
        int mainEnd = program.IndexOf(
            "private static async Task<int> RunAsync",
            catchStart,
            StringComparison.Ordinal);

        Assert.True(runInvocation >= 0);
        Assert.True(catchStart > runInvocation);
        Assert.True(mainEnd > catchStart);
        string fatalHandler = program[catchStart..mainEnd];

        Assert.Contains("CrashReportWriter.CreateDefault", fatalHandler, StringComparison.Ordinal);
        Assert.Contains("\"OpenBlade.SessionAgent\"", fatalHandler, StringComparison.Ordinal);
        Assert.Contains("processTerminating: true", fatalHandler, StringComparison.Ordinal);
        Assert.Contains("throw;", fatalHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("return 1;", fatalHandler, StringComparison.Ordinal);
        Assert.Contains("catch (Exception) when (terminalFailure is not null)", program,
            StringComparison.Ordinal);
        Assert.Contains("ExceptionDispatchInfo.Capture(terminalFailure).Throw()", program,
            StringComparison.Ordinal);
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
