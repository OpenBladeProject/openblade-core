using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace OpenBlade.Setup;

public enum SetupProgressCode
{
    InspectPayload,
    ReadInstallState,
    RemoveLegacyRegistration,
    StopServiceForReplacement,
    StopTrayAndSessionAgent,
    RemoveDynamicLighting,
    ActivateFiles,
    RegisterDynamicLighting,
    WriteStandaloneUninstaller,
    InstallService,
    VerifyServiceHealth,
    CreateShortcuts,
    WriteBundleOwnership,
    RestorePreviousState,
    ReadUninstallState,
    RemoveShortcuts,
    RemoveService,
    RemoveDynamicLightingAndOwnership,
    RemoveProgramFiles,
    RestoreRegistrationState,
    RemoveSavedData,
    CopyFile,
    CommitFiles,
    RestoreFiles,
}

public sealed record SetupProgressEvent
{
    private const int MaximumArgumentLength = 512;

    private SetupProgressEvent(SetupProgressCode code, IReadOnlyList<string> arguments)
    {
        Code = code;
        Arguments = arguments;
    }

    public SetupProgressCode Code { get; }
    public IReadOnlyList<string> Arguments { get; }

    public static SetupProgressEvent Create(
        SetupProgressCode code,
        params string[] arguments)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        ArgumentNullException.ThrowIfNull(arguments);
        int expectedArgumentCount = code == SetupProgressCode.CopyFile ? 1 : 0;
        if (arguments.Length != expectedArgumentCount)
        {
            throw new ArgumentException(
                $"Progress code {code} requires {expectedArgumentCount} argument(s).",
                nameof(arguments));
        }

        string[] boundedArguments = new string[arguments.Length];
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index]
                ?? throw new ArgumentException("Progress arguments cannot be null.", nameof(arguments));
            boundedArguments[index] = argument.Length <= MaximumArgumentLength
                ? argument
                : argument[..MaximumArgumentLength];
        }

        return new SetupProgressEvent(code, boundedArguments);
    }

    public string ToInvariantDiagnostic() => Code switch
    {
        SetupProgressCode.InspectPayload => "Inspecting and staging the OpenBlade payload",
        SetupProgressCode.ReadInstallState => "Reading the current service, shortcut, and registry state",
        SetupProgressCode.RemoveLegacyRegistration =>
            "Removing superseded OpenBlade.lnk, OpenBlade.SessionAgent.lnk, and uninstall registry entries",
        SetupProgressCode.StopServiceForReplacement =>
            "Stopping Windows service OpenBlade for replacement",
        SetupProgressCode.StopTrayAndSessionAgent =>
            "Stopping the OpenBlade tray and Session Agent",
        SetupProgressCode.RemoveDynamicLighting =>
            "Removing Dynamic Lighting package OSSBlade.OpenBlade.DynamicLighting",
        SetupProgressCode.ActivateFiles => "Activating the staged OpenBlade files",
        SetupProgressCode.RegisterDynamicLighting =>
            "Registering Dynamic Lighting package OSSBlade.OpenBlade.DynamicLighting",
        SetupProgressCode.WriteStandaloneUninstaller => "Writing the standalone uninstaller",
        SetupProgressCode.InstallService =>
            "Creating and starting Windows service OpenBlade as LocalSystem",
        SetupProgressCode.VerifyServiceHealth => "Verifying the OpenBlade service health check",
        SetupProgressCode.CreateShortcuts =>
            "Creating all-users OpenBlade.lnk and OpenBlade.SessionAgent.lnk shortcuts",
        SetupProgressCode.WriteBundleOwnership =>
            @"Writing HKLM\SOFTWARE\OSSBlade\OpenBlade\Installer registry values",
        SetupProgressCode.RestorePreviousState =>
            "Setup failed; restoring the previous installation state",
        SetupProgressCode.ReadUninstallState => "Reading the installed OpenBlade state",
        SetupProgressCode.RemoveShortcuts =>
            "Removing all-users OpenBlade.lnk and OpenBlade.SessionAgent.lnk shortcuts",
        SetupProgressCode.RemoveService => "Stopping and deleting Windows service OpenBlade",
        SetupProgressCode.RemoveDynamicLightingAndOwnership =>
            @"Removing Dynamic Lighting package OSSBlade.OpenBlade.DynamicLighting and HKLM\SOFTWARE\OSSBlade\OpenBlade\Installer",
        SetupProgressCode.RemoveProgramFiles => @"Removing Program Files\OpenBlade",
        SetupProgressCode.RestoreRegistrationState =>
            "Removal failed; restoring the previous registration state",
        SetupProgressCode.RemoveSavedData =>
            @"Removing ProgramData\OpenBlade and saved per-user OpenBlade settings",
        SetupProgressCode.CopyFile => string.Format(
            CultureInfo.InvariantCulture,
            "Copying {0}",
            Arguments[0]),
        SetupProgressCode.CommitFiles => "Committing the OpenBlade file transaction",
        SetupProgressCode.RestoreFiles => "Restoring the previous OpenBlade files",
        _ => Code.ToString(),
    };
}

public interface ISetupProgressReporter : IDisposable
{
    void Report(SetupProgressEvent progress);
}

public static class SetupProgressReporterExtensions
{
    public static void Report(
        this ISetupProgressReporter reporter,
        SetupProgressCode code,
        params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        reporter.Report(SetupProgressEvent.Create(code, arguments));
    }
}

internal sealed class NullSetupProgressReporter : ISetupProgressReporter
{
    public static NullSetupProgressReporter Instance { get; } = new();

    public void Report(SetupProgressEvent progress)
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class TextWriterSetupProgressReporter(
    TextWriter writer,
    IDisposable? ownedResource = null) : ISetupProgressReporter
{
    private readonly Lock sync = new();
    private bool available = true;

    public void Report(SetupProgressEvent progress)
    {
        if (!available)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(progress);
        string serialized = SetupProgressWireProtocol.Serialize(progress);

        lock (sync)
        {
            if (!available)
            {
                return;
            }

            try
            {
                writer.WriteLine(serialized);
                writer.Flush();
            }
            catch (IOException)
            {
                available = false;
            }
            catch (ObjectDisposedException)
            {
                available = false;
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            available = false;
            TryDispose(writer);
            TryDispose(ownedResource);
        }
    }

    private static void TryDispose(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal static class SetupProgressWireProtocol
{
    internal const string Version = "v1";

    internal static string Serialize(SetupProgressEvent progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        StringBuilder message = new StringBuilder(Version)
            .Append('|')
            .Append(progress.Code);
        foreach (string argument in progress.Arguments)
        {
            message.Append('|')
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(argument)));
        }
        return message.ToString();
    }
}

internal static class SetupProgressReporter
{
    private const string PipePrefix = "OpenBlade.Setup.Progress.";

    public static ISetupProgressReporter Create(string? pipeName)
    {
        if (!IsValidPipeName(pipeName))
        {
            return NullSetupProgressReporter.Instance;
        }

        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(
                ".",
                pipeName!,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            pipe.Connect(2000);
            StreamWriter writer = new(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true);
            return new TextWriterSetupProgressReporter(writer, pipe);
        }
        catch (IOException)
        {
            pipe?.Dispose();
            return NullSetupProgressReporter.Instance;
        }
        catch (TimeoutException)
        {
            pipe?.Dispose();
            return NullSetupProgressReporter.Instance;
        }
        catch (UnauthorizedAccessException)
        {
            pipe?.Dispose();
            return NullSetupProgressReporter.Instance;
        }
        catch (InvalidOperationException)
        {
            pipe?.Dispose();
            return NullSetupProgressReporter.Instance;
        }
    }

    internal static bool IsValidPipeName(string? pipeName)
    {
        if (pipeName is null
            || !pipeName.StartsWith(PipePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string suffix = pipeName[PipePrefix.Length..];
        return Guid.TryParseExact(suffix, "N", out _);
    }
}
