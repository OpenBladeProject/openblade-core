using System.IO.Pipes;
using System.Security.Principal;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionAgentPipeOwner : IAsyncDisposable
{
    private const int MaximumPipeInstances = 2;
    private readonly SecurityIdentifier userSid;
    private NamedPipeServerStream? initialPipe;

    private SessionAgentPipeOwner(
        SessionAgentPipeEndpoint endpoint,
        SecurityIdentifier userSid,
        NamedPipeServerStream initialPipe)
    {
        Endpoint = endpoint;
        this.userSid = userSid;
        this.initialPipe = initialPipe;
    }

    public SessionAgentPipeEndpoint Endpoint { get; }

    public static bool TryAcquire(
        SessionAgentPipeEndpoint endpoint,
        SecurityIdentifier userSid,
        out SessionAgentPipeOwner? owner)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(userSid);
        try
        {
            NamedPipeServerStream pipe = CreatePipe(
                endpoint.PipeName,
                userSid,
                firstInstance: true);
            owner = new SessionAgentPipeOwner(endpoint, userSid, pipe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            owner = null;
            return false;
        }
    }

    internal NamedPipeServerStream TakeInitialPipe() =>
        Interlocked.Exchange(ref initialPipe, null)
        ?? throw new InvalidOperationException("The session-agent pipe owner is already in use.");

    internal NamedPipeServerStream CreateReplacementPipe() =>
        CreatePipe(Endpoint.PipeName, userSid, firstInstance: false);

    internal static async Task<bool> WaitForConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            IsExpectedWaitCancellation(exception, cancellationToken))
        {
            // Windows can surface a canceled named-pipe accept as either
            // OperationCanceledException or ERROR_PIPE_NOT_CONNECTED/IOException.
            return false;
        }
    }

    internal static bool IsExpectedWaitCancellation(
        Exception exception,
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
        && exception is OperationCanceledException or IOException;

    public async ValueTask DisposeAsync()
    {
        NamedPipeServerStream? pipe = Interlocked.Exchange(ref initialPipe, null);
        if (pipe is not null)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static NamedPipeServerStream CreatePipe(
        string pipeName,
        SecurityIdentifier userSid,
        bool firstInstance)
    {
        PipeOptions options = PipeOptions.Asynchronous;
        if (firstInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        // .NET rejects combining PipeOptions.CurrentUserOnly with an explicit
        // PipeSecurity. The protected one-SID DACL below is the auditable
        // equivalent and is present from the instant the pipe is created.
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            MaximumPipeInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            SessionAgentPipeSecurity.Create(userSid));
    }
}
