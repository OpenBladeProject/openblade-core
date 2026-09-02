namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionAgentTaskSupervisionContractTests
{
    [Fact]
    public void ShutdownJoinsTheSameTasksUsedForTerminalSupervision()
    {
        string program = ReadRepositoryFile(
            "src",
            "OpenBlade.SessionAgent",
            "Program.cs");
        string supervisedTasks = ExtractSupervisedTasks(program);

        Assert.Contains("microphoneMuteTask,", supervisedTasks, StringComparison.Ordinal);
        Assert.Contains(
            "Task.WhenAny(supervisedTasks)",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "Task.WhenAll(supervisedTasks)",
            program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StartupDoesNotRegisterRetiredDynamicLightingIdentity()
    {
        string program = ReadRepositoryFile(
            "src",
            "OpenBlade.SessionAgent",
            "Program.cs");
        string supervisedTasks = ExtractSupervisedTasks(program);

        Assert.DoesNotContain(
            "PerUserDynamicLightingRegistration",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "identity-registration-relaunch",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "identityRegistrationTask",
            supervisedTasks,
            StringComparison.Ordinal);
    }

    private static string ExtractSupervisedTasks(string program)
    {
        const string declaration = "Task[] supervisedTasks =";
        int start = program.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = program.IndexOf("];", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return program[start..end];
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
