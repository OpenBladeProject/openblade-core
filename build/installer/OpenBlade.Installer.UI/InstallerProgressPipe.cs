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
    private readonly Action<string> onMessage;
    private bool disposed;

    private InstallerProgressPipe(string name, Action<string> onMessage)
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

    public static InstallerProgressPipe Start(Action<string> onMessage)
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
        Action<string> messageHandler)
    {
        if (reader is null)
        {
            throw new ArgumentNullException(nameof(reader));
        }
        if (messageHandler is null)
        {
            throw new ArgumentNullException(nameof(messageHandler));
        }

        const int MaximumMessageLength = 512;
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
                    messageHandler(line.ToString());
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
            messageHandler(line.ToString());
        }
    }
}
