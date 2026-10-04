using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.DeploymentClient;

internal interface IBrokerChannel
{
    Task<DeploymentBrokerResponse> ApplyFixedInboxAsync(
        Guid requestId,
        CancellationToken cancellationToken);

    Task<DeploymentBrokerResponse> ApplyCpuVoltageInboxAsync(
        Guid requestId,
        CancellationToken cancellationToken);
}

internal interface IBrokerPipeConnection : IAsyncDisposable
{
    BrokerServerIdentity ServerIdentity { get; }

    Stream Input { get; }

    Stream Output { get; }
}

internal interface IBrokerPipeConnector
{
    Task<IBrokerPipeConnection> ConnectAsync(CancellationToken cancellationToken);
}

internal interface IBrokerDeadline : IDisposable
{
    CancellationToken Token { get; }
}

internal interface IBrokerDeadlineFactory
{
    IBrokerDeadline Create(CancellationToken cancellationToken, TimeSpan timeout);
}

internal sealed class BrokerDeadlineFactory : IBrokerDeadlineFactory
{
    public IBrokerDeadline Create(CancellationToken cancellationToken, TimeSpan timeout) =>
        new BrokerDeadline(cancellationToken, timeout);
}

internal sealed class BrokerDeadline : IBrokerDeadline
{
    private readonly CancellationTokenSource source;

    internal BrokerDeadline(CancellationToken cancellationToken, TimeSpan timeout)
    {
        source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
    }

    public CancellationToken Token => source.Token;

    public void Dispose() => source.Dispose();
}

internal sealed class BrokerPipeChannel : IBrokerChannel
{
    internal static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan TerminalResponseTimeout = TimeSpan.FromMinutes(40);

    private readonly IBrokerPipeConnector connector;
    private readonly IBrokerServerIdentityVerifier identityVerifier;
    private readonly IBrokerDeadlineFactory deadlines;

    internal BrokerPipeChannel(
        IBrokerPipeConnector connector,
        IBrokerServerIdentityVerifier identityVerifier)
        : this(connector, identityVerifier, new BrokerDeadlineFactory())
    {
    }

    internal BrokerPipeChannel(
        IBrokerPipeConnector connector,
        IBrokerServerIdentityVerifier identityVerifier,
        IBrokerDeadlineFactory deadlines)
    {
        this.connector = connector;
        this.identityVerifier = identityVerifier;
        this.deadlines = deadlines;
    }

    public Task<DeploymentBrokerResponse> ApplyFixedInboxAsync(
        Guid requestId,
        CancellationToken cancellationToken) => ApplyAsync(
            requestId, DeploymentBrokerAction.ApplyFixedInbox, cancellationToken);

    public Task<DeploymentBrokerResponse> ApplyCpuVoltageInboxAsync(
        Guid requestId,
        CancellationToken cancellationToken) => ApplyAsync(
            requestId, DeploymentBrokerAction.ApplyCpuVoltageInbox, cancellationToken);

    private async Task<DeploymentBrokerResponse> ApplyAsync(
        Guid requestId,
        DeploymentBrokerAction action,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty broker request ID is required.", nameof(requestId));
        }

        IBrokerPipeConnection connection;
        using (IBrokerDeadline preflight = deadlines.Create(cancellationToken, PreflightTimeout))
        {
            connection = await connector.ConnectAsync(preflight.Token).ConfigureAwait(false);
            try
            {
                identityVerifier.Verify(connection.ServerIdentity);

                DeploymentBrokerRequest request = new(
                    DeploymentBrokerProtocol.ProtocolVersion,
                    requestId,
                    action);
                await WriteFrameAsync(
                    connection.Output,
                    DeploymentBrokerProtocol.SerializeRequest(request),
                    preflight.Token).ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        await using (connection.ConfigureAwait(false))
        using (IBrokerDeadline terminal = deadlines.Create(
            cancellationToken,
            TerminalResponseTimeout))
        {
            byte[] responseBytes = await ReadFrameAsync(connection.Input, terminal.Token)
                .ConfigureAwait(false);
            return DeploymentBrokerProtocol.ParseResponse(responseBytes);
        }
    }

    private static async Task WriteFrameAsync(
        Stream output,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        byte[] length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await output.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        byte[] lengthBytes = new byte[sizeof(int)];
        await input.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length <= 0 || length > DeploymentBrokerProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("The deployment broker response frame size is invalid.");
        }

        byte[] payload = new byte[length];
        await input.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }
}

internal sealed class WindowsBrokerPipeConnector(bool startService = true) : IBrokerPipeConnector
{
    private const string PipeName = "OpenBlade.DeploymentBroker.v1";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public async Task<IBrokerPipeConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        if (startService)
        {
            await WindowsBrokerServiceStarter.EnsureStartedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        NamedPipeClientStream pipe = new(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            BrokerServerIdentity identity = WindowsBrokerServerIdentityReader.Read(pipe);
            return new WindowsBrokerPipeConnection(pipe, identity);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal static class WindowsBrokerServiceStarter
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceStartPending = 2;
    private const uint ServiceRunning = 4;
    private const int ErrorServiceAlreadyRunning = 1056;

    internal static async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        using SafeServiceHandle manager = OpenSCManager(
            machineName: null,
            databaseName: null,
            ScManagerConnect);
        if (manager.IsInvalid)
        {
            throw Failure("The Windows Service Control Manager is unavailable.");
        }

        using SafeServiceHandle service = OpenService(
            manager,
            WindowsProtectedBrokerIdentityVerifier.ServiceName,
            ServiceQueryStatus | ServiceStart);
        if (service.IsInvalid)
        {
            throw Failure("The local-development deployment broker service is unavailable.");
        }

        if (!StartService(service, 0, null))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceAlreadyRunning)
            {
                throw Failure("The local-development deployment broker could not be started.", error);
            }
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ServiceStatusProcess status = QueryStatus(service);
            if (status.CurrentState == ServiceRunning)
            {
                return;
            }

            if (status.CurrentState != ServiceStartPending || DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    "The local-development deployment broker did not reach Running state.");
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ServiceStatusProcess QueryStatus(SafeServiceHandle service)
    {
        uint size = checked((uint)Marshal.SizeOf<ServiceStatusProcess>());
        if (!QueryServiceStatusEx(
                service,
                ScStatusProcessInfo,
                out ServiceStatusProcess status,
                size,
                out _))
        {
            throw Failure("The local-development deployment broker status is unavailable.");
        }

        return status;
    }

    private static Win32Exception Failure(string message, int? error = null) =>
        new(error ?? Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
        internal uint ProcessId;
        internal uint ServiceFlags;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(
        SafeServiceHandle service,
        uint argumentCount,
        string[]? arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        int infoLevel,
        out ServiceStatusProcess status,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint serviceHandle);
}

internal sealed class WindowsBrokerPipeConnection(
    NamedPipeClientStream pipe,
    BrokerServerIdentity serverIdentity) : IBrokerPipeConnection
{
    public BrokerServerIdentity ServerIdentity { get; } = serverIdentity;

    public Stream Input => pipe;

    public Stream Output => pipe;

    public ValueTask DisposeAsync() => pipe.DisposeAsync();
}
