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
        PipeSecurity security = new();
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The installer user identity is unavailable.");
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.Write,
            AccessControlType.Allow));
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
            while (!disposed)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                onMessage(line);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }
}
