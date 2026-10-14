using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace OpenBlade.Setup;

public static class SignInTaskNames
{
    public const string Tray = "OpenBlade Tray";
    public const string SessionAgent = "OpenBlade Session Agent";
}

public enum SignInTaskState
{
    Missing,
    Enabled,
    Disabled,
}

public sealed record SignInTaskDefinition(
    string TaskName,
    string ExecutablePath,
    string WorkingDirectory,
    string? Arguments,
    string Description);

public static class SignInTaskXml
{
    public const string UsersGroupSid = "S-1-5-32-545";

    // SYSTEM and Administrators full access; Users read-only so a standard user
    // can see and run the task but cannot change or delete it.
    public const string SecurityDescriptor = "D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;BU)";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string Build(SignInTaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        XElement exec = new(Ns + "Exec", new XElement(Ns + "Command", definition.ExecutablePath));
        if (definition.Arguments is not null)
        {
            exec.Add(new XElement(Ns + "Arguments", definition.Arguments));
        }

        exec.Add(new XElement(Ns + "WorkingDirectory", definition.WorkingDirectory));

        XElement task = new(
            Ns + "Task",
            new XAttribute("version", "1.2"),
            new XElement(
                Ns + "RegistrationInfo",
                new XElement(Ns + "Description", definition.Description)),
            new XElement(
                Ns + "Triggers",
                new XElement(Ns + "LogonTrigger", new XElement(Ns + "Enabled", "true"))),
            new XElement(
                Ns + "Principals",
                new XElement(
                    Ns + "Principal",
                    new XAttribute("id", "Users"),
                    new XElement(Ns + "GroupId", UsersGroupSid),
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(
                Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "Parallel"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "false"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(
                    Ns + "IdleSettings",
                    new XElement(Ns + "StopOnIdleEnd", "false"),
                    new XElement(Ns + "RestartOnIdle", "false")),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "true"),
                new XElement(Ns + "RunOnlyIfIdle", "false"),
                new XElement(Ns + "WakeToRun", "false"),
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                // The Task Scheduler default (7) is below normal. The keyboard hook in the
                // Session Agent has a 300 ms Windows timeout, so use normal priority (4).
                new XElement(Ns + "Priority", "4")),
            new XElement(
                Ns + "Actions",
                new XAttribute("Context", "Users"),
                exec));

        return new XDocument(task).ToString(SaveOptions.DisableFormatting);
    }
}

public interface ISignInTaskScheduler
{
    SignInTaskState GetState(string taskName);

    void Register(SignInTaskDefinition definition);

    void Delete(string taskName);
}

internal sealed class WindowsSignInTaskScheduler : ISignInTaskScheduler
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonGroup = 4;

    // The dynamic binder surfaces some COM failures as FileNotFoundException or
    // DirectoryNotFoundException instead of COMException, so match on HResult alone.
    internal static bool IsNotFound(int hresult) =>
        hresult is unchecked((int)0x80070002) or unchecked((int)0x80070003);

    public SignInTaskState GetState(string taskName)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskName);

        object service = CreateService();
        object? folder = null;
        object? task = null;
        try
        {
            dynamic dynamicService = service;
            dynamicService.Connect();
            folder = dynamicService.GetFolder("\\");
            dynamic dynamicFolder = folder;
            task = dynamicFolder.GetTask(taskName);
            dynamic dynamicTask = task;
            return (bool)dynamicTask.Enabled ? SignInTaskState.Enabled : SignInTaskState.Disabled;
        }
        catch (Exception ex) when (IsNotFound(ex.HResult))
        {
            return SignInTaskState.Missing;
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    public void Register(SignInTaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        string xml = SignInTaskXml.Build(definition);
        object service = CreateService();
        object? folder = null;
        object? registered = null;
        try
        {
            dynamic dynamicService = service;
            dynamicService.Connect();
            folder = dynamicService.GetFolder("\\");
            dynamic dynamicFolder = folder;
            registered = dynamicFolder.RegisterTask(
                definition.TaskName,
                xml,
                TaskCreateOrUpdate,
                null,
                null,
                TaskLogonGroup,
                SignInTaskXml.SecurityDescriptor);
        }
        finally
        {
            ReleaseComObject(registered);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    public void Delete(string taskName)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskName);

        object service = CreateService();
        object? folder = null;
        try
        {
            dynamic dynamicService = service;
            dynamicService.Connect();
            folder = dynamicService.GetFolder("\\");
            dynamic dynamicFolder = folder;
            dynamicFolder.DeleteTask(taskName, 0);
        }
        catch (Exception ex) when (IsNotFound(ex.HResult))
        {
        }
        finally
        {
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    private static object CreateService()
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not create the Windows Task Scheduler service.");
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
