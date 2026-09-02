using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Security.Principal;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class NamedPipeControlServer
{
    internal const int MaximumServerInstances = 14;
    internal const int ReservedOrdinaryServerInstances = 4;
    internal const int MaximumLightingStreamConnections = 2;
    internal const int MaximumPersistentConnections =
        MaximumServerInstances - ReservedOrdinaryServerInstances;
    private static readonly TimeSpan FrameReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FrameWriteTimeout = TimeSpan.FromSeconds(2);
    private readonly string pipeName;
    private readonly PipeSecurity pipeSecurity;
    private readonly ControlRequestHandler handler;
    private readonly IStatusSubscriptionHub subscriptionHub;
    private readonly IServiceClientContextResolver clientContextResolver;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim firstListenerGate = new(1, 1);
    private int firstListenerEstablished;
    private int persistentConnectionCount;
    private int lightingStreamConnectionCount;

    internal NamedPipeControlServer(
        string pipeName,
        SecurityIdentifier authorizedUser,
        ControlRequestHandler handler,
        IStatusSubscriptionHub subscriptionHub,
        TimeProvider? timeProvider = null)
    {
        this.pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("Pipe name is required.", nameof(pipeName))
            : pipeName;
        pipeSecurity = PipeSecurityFactory.Create(
            authorizedUser ?? throw new ArgumentNullException(nameof(authorizedUser)));
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        this.subscriptionHub = subscriptionHub
            ?? throw new ArgumentNullException(nameof(subscriptionHub));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        clientContextResolver = new FixedServiceClientContextResolver(
            ServiceClientContext.TrustedTestClient);
    }

    public NamedPipeControlServer(
        string pipeName,
        ControlRequestHandler handler,
        IStatusSubscriptionHub subscriptionHub,
        IServiceClientContextResolver clientContextResolver,
        TimeProvider? timeProvider = null)
    {
        this.pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("Pipe name is required.", nameof(pipeName))
            : pipeName;
        pipeSecurity = PipeSecurityFactory.CreateForInteractiveUsers();
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        this.subscriptionHub = subscriptionHub
            ?? throw new ArgumentNullException(nameof(subscriptionHub));
        this.clientContextResolver = clientContextResolver
            ?? throw new ArgumentNullException(nameof(clientContextResolver));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task ServeOnceAsync(CancellationToken cancellationToken)
    {
        bool ownsFirstListenerGate = false;
        NamedPipeServerStream? pipe = null;
        try
        {
            if (Volatile.Read(ref firstListenerEstablished) == 0)
            {
                await firstListenerGate.WaitAsync(cancellationToken);
                ownsFirstListenerGate = true;
                if (Volatile.Read(ref firstListenerEstablished) == 0)
                {
                    pipe = CreatePipe(firstInstance: true);
                }
                else
                {
                    firstListenerGate.Release();
                    ownsFirstListenerGate = false;
                }
            }

            pipe ??= CreatePipe(firstInstance: false);
            await pipe.WaitForConnectionAsync(cancellationToken);
            if (ownsFirstListenerGate)
            {
                Volatile.Write(ref firstListenerEstablished, 1);
                firstListenerGate.Release();
                ownsFirstListenerGate = false;
            }
            ServiceRequest request;
            try
            {
                request = await ReadRequestAsync(pipe, cancellationToken);
            }
            catch (Exception exception) when (
                exception is InvalidDataException
                    or JsonException
                    or IpcProtocolVersionMismatchException)
            {
                await WriteFrameAsync(
                    pipe,
                    Failure(Guid.Empty, "IPC request is malformed."),
                    cancellationToken);
                return;
            }

            // Windows permits named-pipe impersonation only after the server has
            // read client data. Resolve the authenticated client context after
            // consuming the first complete request, then handle that request
            // without reading a second frame.
            ServiceClientContext client = clientContextResolver.Resolve(pipe);
            await ServeConnectionAsync(pipe, client, request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            // A disconnect or stalled initial request affects only this connection.
        }
        finally
        {
            if (ownsFirstListenerGate)
            {
                firstListenerGate.Release();
            }
            if (pipe is not null)
            {
                await pipe.DisposeAsync();
            }
        }
    }

    private NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        PipeOptions options = PipeOptions.Asynchronous;
        if (firstInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: MaximumServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity);
    }

    internal async Task ServeConnectionAsync(
        Stream connection,
        CancellationToken cancellationToken)
        => await ServeConnectionAsync(
            connection,
            ServiceClientContext.TrustedTestClient,
            cancellationToken);

    internal async Task ServeConnectionAsync(
        Stream connection,
        ServiceClientContext client,
        CancellationToken cancellationToken)
        => await ServeConnectionAsync(
            connection,
            client,
            initialRequest: null,
            cancellationToken);

    private async Task ServeConnectionAsync(
        Stream connection,
        ServiceClientContext client,
        ServiceRequest? initialRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        bool ownsPersistentSlot = false;
        bool ownsLightingStreamSlot = false;
        IDisposable? protocolCompatibilityLease = null;
        bool protocol51Compatibility = false;
        try
        {
            Type? streamingRequestType = null;
            while (true)
            {
                ServiceRequest request;
                if (initialRequest is not null)
                {
                    request = initialRequest;
                    initialRequest = null;
                }
                else
                {
                    try
                    {
                        request = await ReadRequestAsync(
                            connection,
                            cancellationToken,
                            useDeadline: streamingRequestType is null);
                    }
                    catch (Exception exception) when (
                        exception is InvalidDataException
                            or JsonException
                            or IpcProtocolVersionMismatchException)
                    {
                        await WriteFrameAsync(
                            connection,
                            Failure(Guid.Empty, "IPC request is malformed."),
                            cancellationToken);
                        return;
                    }
                }

                if (request.Version == OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion)
                {
                    if (request is not (GetStatusRequest or SubscribeStatusRequest))
                    {
                        await WriteFrameAsync(
                            connection,
                            Failure(
                                request.RequestId,
                                "This OpenBlade client is read-only because the service was updated. Update or repair OpenBlade before changing settings."),
                            cancellationToken,
                            protocol51Compatibility: true);
                        return;
                    }

                    protocolCompatibilityLease ??=
                        handler.RegisterProtocolCompatibilityClient();
                    protocol51Compatibility = true;
                    request = request switch
                    {
                        GetStatusRequest getStatus => getStatus with
                        {
                            Version = OpenBladeIpc.ProtocolVersion,
                        },
                        SubscribeStatusRequest subscribeStatus => subscribeStatus with
                        {
                            Version = OpenBladeIpc.ProtocolVersion,
                        },
                        _ => throw new InvalidOperationException(
                            "Protocol 51 compatibility admitted an unsupported request."),
                    };
                }

                if (!ownsPersistentSlot && IsPersistentRequest(request))
                {
                    bool lightingStream = IsLightingStreamRequest(request);
                    if (lightingStream
                        && !TryReserveSlot(
                            ref lightingStreamConnectionCount,
                            MaximumLightingStreamConnections))
                    {
                        await WriteFrameAsync(
                            connection,
                            Failure(
                                request.RequestId,
                                "The service has reached its lighting stream capacity.",
                                ServiceResponseErrorCode.CapacityUnavailable),
                            cancellationToken,
                            protocol51Compatibility);
                        return;
                    }
                    ownsLightingStreamSlot = lightingStream;
                    // This aggregate reservation also bounds subscriptions
                    // that are still completing initial admission and have not
                    // yet acquired one of the hub's subscriber leases.
                    if (!TryReserveSlot(
                        ref persistentConnectionCount,
                        MaximumPersistentConnections))
                    {
                        if (ownsLightingStreamSlot)
                        {
                            Interlocked.Decrement(ref lightingStreamConnectionCount);
                            ownsLightingStreamSlot = false;
                        }
                        await WriteFrameAsync(
                            connection,
                            Failure(
                                request.RequestId,
                                "The service has reached its persistent connection capacity.",
                                ServiceResponseErrorCode.CapacityUnavailable),
                            cancellationToken,
                            protocol51Compatibility);
                        return;
                    }
                    ownsPersistentSlot = true;
                }

                if (request is SubscribeStatusRequest)
                {
                    await ServeSubscriptionAsync(
                        connection,
                        request,
                        client,
                        cancellationToken,
                        protocol51Compatibility);
                    return;
                }

                if (streamingRequestType is not null
                    && request.GetType() != streamingRequestType)
                {
                    await WriteFrameAsync(
                        connection,
                        Failure(
                            request.RequestId,
                            "A lighting input stream accepts only one request type."),
                        cancellationToken);
                    return;
                }

                ServiceResponse response = protocol51Compatibility
                    ? await handler.HandleProtocol51ReadOnlyAsync(
                        request,
                        client,
                        cancellationToken)
                    : await handler.HandleAsync(
                        request,
                        client,
                        cancellationToken);
                await WriteFrameAsync(
                    connection,
                    response,
                    cancellationToken,
                    protocol51Compatibility);
                if (!response.Success)
                {
                    return;
                }

                if (request is not (PulseReactiveKeyRequest or PushAmbientLightingFrameRequest))
                {
                    return;
                }
                streamingRequestType = request.GetType();

                // High-frequency lighting inputs are already serialized by
                // their producer. Keep one request class per long-lived pipe so
                // input never pays per-frame connection setup or stale reconnects.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            // A disconnect or stalled writer affects only this connection.
        }
        finally
        {
            if (ownsPersistentSlot)
            {
                Interlocked.Decrement(ref persistentConnectionCount);
            }
            if (ownsLightingStreamSlot)
            {
                Interlocked.Decrement(ref lightingStreamConnectionCount);
            }
            protocolCompatibilityLease?.Dispose();
        }
    }

    private static bool IsPersistentRequest(ServiceRequest request) =>
        request is SubscribeStatusRequest
            or PulseReactiveKeyRequest
            or PushAmbientLightingFrameRequest;

    private static bool IsLightingStreamRequest(ServiceRequest request) =>
        request is PulseReactiveKeyRequest or PushAmbientLightingFrameRequest;

    private static bool TryReserveSlot(ref int count, int maximum)
    {
        int observed = Volatile.Read(ref count);
        while (observed < maximum)
        {
            int prior = Interlocked.CompareExchange(
                ref count,
                observed + 1,
                observed);
            if (prior == observed)
            {
                return true;
            }
            observed = prior;
        }
        return false;
    }

    private async Task ServeSubscriptionAsync(
        Stream connection,
        ServiceRequest request,
        ServiceClientContext client,
        CancellationToken cancellationToken,
        bool protocol51Compatibility)
    {
        ServiceResponse initial = protocol51Compatibility
            ? await handler.HandleProtocol51ReadOnlyAsync(
                request,
                client,
                cancellationToken)
            : await handler.HandleAsync(
                request,
                client,
                cancellationToken);
        if (!initial.Success)
        {
            await WriteFrameAsync(
                connection,
                initial,
                cancellationToken,
                protocol51Compatibility);
            return;
        }

        StatusSubscriptionLease lease;
        try
        {
            lease = subscriptionHub.Subscribe(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await WriteFrameAsync(
                connection,
                Failure(
                    request.RequestId,
                    exception.Message,
                    ServiceResponseErrorCode.CapacityUnavailable),
                cancellationToken,
                protocol51Compatibility);
            return;
        }

        await using (lease)
        {
            await WriteFrameAsync(
                connection,
                initial,
                cancellationToken,
                protocol51Compatibility);
            long initialRevision = initial.Status?.Revision ?? -1;

            using CancellationTokenSource connectionLifetime =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task streamTask = StreamSubscriptionAsync(
                connection,
                lease,
                initialRevision,
                client,
                connectionLifetime.Token,
                protocol51Compatibility);
            Task disconnectTask = MonitorDisconnectAsync(
                connection,
                connectionLifetime.Token);

            await Task.WhenAny(streamTask, disconnectTask);
            connectionLifetime.Cancel();

            Exception? failure = null;
            foreach (Task task in new[] { streamTask, disconnectTask })
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                    when (connectionLifetime.IsCancellationRequested &&
                          !cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }

    private async Task StreamSubscriptionAsync(
        Stream connection,
        StatusSubscriptionLease lease,
        long initialRevision,
        ServiceClientContext client,
        CancellationToken cancellationToken,
        bool protocol51Compatibility)
    {
        await foreach (ServiceStatusSnapshot snapshot in
            lease.ReadAllAsync(cancellationToken))
        {
            if (snapshot.Revision <= initialRevision)
            {
                continue;
            }

            ServiceStatusSnapshot projected = handler.ProjectStatus(snapshot, client);
            await WriteFrameAsync(
                connection,
                new ServiceStatusEvent(
                    OpenBladeIpc.ProtocolVersion,
                    projected),
                cancellationToken,
                protocol51Compatibility);
        }
    }

    private static async Task MonitorDisconnectAsync(
        Stream connection,
        CancellationToken cancellationToken)
    {
        byte[] probe = new byte[1];
        _ = await connection.ReadAsync(probe, cancellationToken);
    }

    private async Task WriteFrameAsync(
        Stream connection,
        ServiceServerFrame frame,
        CancellationToken cancellationToken,
        bool protocol51Compatibility = false)
    {
        using var deadline = new CancellationTokenSource(FrameWriteTimeout, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            if (protocol51Compatibility)
            {
                await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(
                    connection,
                    frame,
                    linked.Token);
            }
            else
            {
                await IpcMessageCodec.WriteServerFrameAsync(connection, frame, linked.Token);
            }
        }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("IPC frame write exceeded two seconds.", exception);
        }
    }

    private async Task<ServiceRequest> ReadRequestAsync(
        Stream connection,
        CancellationToken cancellationToken,
        bool useDeadline = true)
    {
        if (!useDeadline)
        {
            return await IpcMessageCodec.ReadRequestWithProtocol51CompatibilityAsync(
                connection,
                cancellationToken);
        }

        using var deadline = new CancellationTokenSource(FrameReadTimeout, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            return await IpcMessageCodec.ReadRequestWithProtocol51CompatibilityAsync(
                connection,
                linked.Token);
        }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("IPC frame read exceeded two seconds.", exception);
        }
    }

    private static ServiceResponse Failure(
        Guid requestId,
        string error,
        ServiceResponseErrorCode? errorCode = null) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId == Guid.Empty ? Guid.NewGuid() : requestId,
        Success: false,
        Error: error,
        Status: null,
        Result: null)
        {
            ErrorCode = errorCode,
        };

    private sealed class FixedServiceClientContextResolver(ServiceClientContext context)
        : IServiceClientContextResolver
    {
        public ServiceClientContext Resolve(NamedPipeServerStream pipe) => context;
    }
}
