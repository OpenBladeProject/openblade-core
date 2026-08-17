using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace OpenBlade.Installer.UI;

internal sealed class InstallerProgressPipe : IDisposable
{
    private const string PipePrefix = "OpenBlade.Setup.Progress.";
    private readonly NamedPipeServerStream server;
    private readonly Action<InstallerSetupProgress> onMessage;
    private bool disposed;

    private InstallerProgressPipe(string name, Action<InstallerSetupProgress> onMessage)
    {
        Name = name;
        this.onMessage = onMessage;
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The installer user identity is unavailable.");
        PipeSecurity security = CreateSecurity(currentUser);
        // Burn executes one OpenBlade.Setup package per plan, so a single
        // authenticated writer is the complete progress session.
        server = new NamedPipeServerStream(
            name,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    public string Name { get; }

    internal static PipeSecurity CreateSecurity(SecurityIdentifier currentUser)
    {
        if (currentUser is null)
        {
            throw new ArgumentNullException(nameof(currentUser));
        }

        PipeSecurity security = new();
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    public static InstallerProgressPipe Start(Action<InstallerSetupProgress> onMessage)
    {
        if (onMessage is null)
        {
            throw new ArgumentNullException(nameof(onMessage));
        }

        InstallerProgressPipe pipe = new(
            PipePrefix + Guid.NewGuid().ToString("N"),
            onMessage);
        _ = pipe.ReadAsync();
        return pipe;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        server.Dispose();
    }

    private async Task ReadAsync()
    {
        try
        {
            await server.WaitForConnectionAsync().ConfigureAwait(false);
            using StreamReader reader = new(
                server,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024,
                leaveOpen: true);
            await ReadMessagesAsync(reader, onMessage).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    internal static async Task ReadMessagesAsync(
        TextReader reader,
        Action<InstallerSetupProgress> messageHandler)
    {
        if (reader is null)
        {
            throw new ArgumentNullException(nameof(reader));
        }
        if (messageHandler is null)
        {
            throw new ArgumentNullException(nameof(messageHandler));
        }

        // A producer argument is bounded to 512 UTF-16 characters. Its UTF-8
        // bytes can expand to 1,536 bytes before Base64 encoding, so keep the
        // wire envelope above the resulting 2,048 characters plus fields.
        const int MaximumMessageLength = 4096;
        char[] buffer = new char[1024];
        StringBuilder line = new(MaximumMessageLength);
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            for (int index = 0; index < read; index++)
            {
                char value = buffer[index];
                if (value == '\n')
                {
                    Dispatch(line.ToString(), messageHandler);
                    line.Clear();
                }
                else if (value != '\r' && line.Length < MaximumMessageLength)
                {
                    line.Append(value);
                }
            }
        }

        if (line.Length > 0)
        {
            Dispatch(line.ToString(), messageHandler);
        }
    }

    private static void Dispatch(
        string line,
        Action<InstallerSetupProgress> messageHandler)
    {
        if (InstallerSetupProgress.TryParse(line, out InstallerSetupProgress? progress))
        {
            messageHandler(progress!);
        }
    }
}

internal enum InstallerSetupProgressCode
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

internal sealed class InstallerSetupProgress
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private InstallerSetupProgress(
        InstallerSetupProgressCode code,
        string? argument,
        string wireValue)
    {
        Code = code;
        Argument = argument;
        WireValue = wireValue;
    }

    public InstallerSetupProgressCode Code { get; }
    public string? Argument { get; }
    public string WireValue { get; }

    internal static bool TryParse(string value, out InstallerSetupProgress? progress)
    {
        progress = null;
        string[] fields = value.Split('|');
        if (fields.Length is < 2 or > 3
            || !string.Equals(fields[0], "v1", StringComparison.Ordinal)
            || !Enum.TryParse(fields[1], ignoreCase: false, out InstallerSetupProgressCode code)
            || !Enum.IsDefined(typeof(InstallerSetupProgressCode), code))
        {
            return false;
        }

        bool requiresArgument = code == InstallerSetupProgressCode.CopyFile;
        if (fields.Length != (requiresArgument ? 3 : 2))
        {
            return false;
        }

        string? argument = null;
        if (requiresArgument)
        {
            try
            {
                argument = StrictUtf8.GetString(Convert.FromBase64String(fields[2]));
            }
            catch (FormatException)
            {
                return false;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
            if (argument.Length > 512 || argument.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                return false;
            }
        }

        progress = new InstallerSetupProgress(code, argument, value);
        return true;
    }
}
