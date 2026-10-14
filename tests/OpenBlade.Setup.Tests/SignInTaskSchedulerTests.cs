using System.Xml.Linq;

namespace OpenBlade.Setup.Tests;

public sealed class SignInTaskSchedulerTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XElement Parse(SignInTaskDefinition definition) =>
        XDocument.Parse(SignInTaskXml.Build(definition)).Root!;

    private static SignInTaskDefinition Sample(string? arguments = "--startup") => new(
        SignInTaskNames.Tray,
        @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe",
        @"C:\Program Files\OpenBlade",
        arguments,
        "Starts OpenBlade");

    [Fact]
    public void Build_DescribesLogonTriggerPrincipalAndSettings()
    {
        XElement task = Parse(Sample());

        Assert.Equal(Ns + "Task", task.Name);
        Assert.Equal("1.2", (string?)task.Attribute("version"));
        Assert.Equal("Starts OpenBlade", (string?)task.Element(Ns + "RegistrationInfo")!.Element(Ns + "Description"));

        XElement trigger = Assert.Single(task.Element(Ns + "Triggers")!.Elements());
        Assert.Equal(Ns + "LogonTrigger", trigger.Name);
        Assert.Equal("true", (string?)trigger.Element(Ns + "Enabled"));
        Assert.Null(trigger.Element(Ns + "UserId"));
        Assert.Null(trigger.Element(Ns + "Delay"));

        XElement principal = Assert.Single(task.Element(Ns + "Principals")!.Elements());
        Assert.Equal("Users", (string?)principal.Attribute("id"));
        Assert.Equal(SignInTaskXml.UsersGroupSid, (string?)principal.Element(Ns + "GroupId"));
        Assert.Equal("LeastPrivilege", (string?)principal.Element(Ns + "RunLevel"));
        Assert.Null(principal.Element(Ns + "UserId"));
        Assert.Null(principal.Element(Ns + "LogonType"));

        XElement settings = task.Element(Ns + "Settings")!;
        Assert.Equal(
            new[]
            {
                ("MultipleInstancesPolicy", "Parallel"),
                ("DisallowStartIfOnBatteries", "false"),
                ("StopIfGoingOnBatteries", "false"),
                ("AllowHardTerminate", "true"),
                ("StartWhenAvailable", "false"),
                ("RunOnlyIfNetworkAvailable", "false"),
                ("AllowStartOnDemand", "true"),
                ("Enabled", "true"),
                ("Hidden", "true"),
                ("RunOnlyIfIdle", "false"),
                ("WakeToRun", "false"),
                ("ExecutionTimeLimit", "PT0S"),
                ("Priority", "4"),
            },
            settings.Elements().Where(e => !e.HasElements).Select(e => (e.Name.LocalName, e.Value)).ToArray());
        XElement idle = settings.Element(Ns + "IdleSettings")!;
        Assert.Equal("false", (string?)idle.Element(Ns + "StopOnIdleEnd"));
        Assert.Equal("false", (string?)idle.Element(Ns + "RestartOnIdle"));

        Assert.Equal(
            new[] { "RegistrationInfo", "Triggers", "Principals", "Settings", "Actions" },
            task.Elements().Select(e => e.Name.LocalName).ToArray());
    }

    [Fact]
    public void Build_RoundTripsExecutablePathArgumentsAndWorkingDirectory()
    {
        SignInTaskDefinition definition = new(
            SignInTaskNames.SessionAgent,
            @"C:\Program Files\Open & Blade\Agent.exe",
            @"C:\Program Files\Open & Blade",
            "--flag \"a & b\" <x>",
            "Desc & <more>");

        XElement task = Parse(definition);
        XElement actions = task.Element(Ns + "Actions")!;
        Assert.Equal("Users", (string?)actions.Attribute("Context"));
        XElement exec = Assert.Single(actions.Elements());
        Assert.Equal(definition.ExecutablePath, (string?)exec.Element(Ns + "Command"));
        Assert.Equal(definition.Arguments, (string?)exec.Element(Ns + "Arguments"));
        Assert.Equal(definition.WorkingDirectory, (string?)exec.Element(Ns + "WorkingDirectory"));
        Assert.Equal(definition.Description, (string?)task.Element(Ns + "RegistrationInfo")!.Element(Ns + "Description"));
    }

    [Fact]
    public void Build_OmitsArgumentsElementWhenNull()
    {
        XElement exec = Parse(Sample(arguments: null)).Element(Ns + "Actions")!.Element(Ns + "Exec")!;

        Assert.Null(exec.Element(Ns + "Arguments"));
        Assert.NotNull(exec.Element(Ns + "Command"));
        Assert.NotNull(exec.Element(Ns + "WorkingDirectory"));
    }

    [Theory]
    [InlineData(0x80070002, true)]
    [InlineData(0x80070003, true)]
    [InlineData(0x80070005, false)]
    public void IsNotFound_MatchesOnlyMissingFileAndPath(uint hresult, bool expected) =>
        Assert.Equal(expected, WindowsSignInTaskScheduler.IsNotFound(unchecked((int)hresult)));

    [Fact]
    public void GetState_ReportsMissingForUnknownTask() =>
        Assert.Equal(
            SignInTaskState.Missing,
            new WindowsSignInTaskScheduler().GetState("OpenBlade-test-" + Guid.NewGuid().ToString("N")));
}
