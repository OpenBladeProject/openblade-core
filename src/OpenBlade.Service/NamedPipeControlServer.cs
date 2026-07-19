using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Security.Principal;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class NamedPipeControlServer
{
    internal const int MaximumServerInstances = 12;
    private static readonly TimeSpan FrameWriteTimeout = TimeSpan.FromSeconds(2);
    private readonly string pipeName;
    private readonly SecurityIdentifier authorizedUser;
    private readonly ControlRequestHandler handler;
    private readonly IStatusSubscriptionHub subscriptionHub;
    private readonly TimeProvider timeProvider;

    public NamedPipeControlServer(
        string pipeName,
        SecurityIdentifier authorizedUser,
        ControlRequestHandler handler,
        IStatusSubscriptionHub subscriptionHub,
        TimeProvider? timeProvider = null)
    {
        this.pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("Pipe name is required.", nameof(pipeName))
            : pipeName;
        this.authorizedUser = authorizedUser
            ?? throw new ArgumentNullException(nameof(authorizedUser));
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        this.subscriptionHub = subscriptionHub
            ?? throw new ArgumentNullException(nameof(subscriptionHub));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task ServeOnceAsync(CancellationToken cancellationToken)
    {
        await using NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: MaximumServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            PipeSecurityFactory.Create(authorizedUser));

        await pipe.WaitForConnectionAsync(cancellationToken);
        await ServeConnectionAsync(pipe, cancellationToken);
    }

    public async Task ServeConnectionAsync(
        Stream connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            ServiceRequest request;
            try
            {
                request = await IpcMessageCodec.ReadRequestAsync(
                    connection,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                await WriteFrameAsync(
                    connection,
                    Failure(Guid.Empty, "IPC request is malformed."),
                    cancellationToken);
                return;
            }

            if (request is SubscribeStatusRequest)
            {
                await ServeSubscriptionAsync(connection, request, cancellationToken);
                return;
            }

            ServiceResponse response = await handler.HandleAsync(request, cancellationToken);
            await WriteFrameAsync(connection, response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            // A disconnect or stalled writer affects only this connection.
        }
    }

    private async Task ServeSubscriptionAsync(
        Stream connection,
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        StatusSubscriptionLease lease;
        try
        {
            lease = subscriptionHub.Subscribe(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await WriteFrameAsync(
                connection,
                Failure(request.RequestId, exception.Message),
                cancellationToken);
            return;
        }

        await using (lease)
        {
            ServiceResponse initial = await handler.HandleAsync(request, cancellationToken);
            await WriteFrameAsync(connection, initial, cancellationToken);
            long initialRevision = initial.Status?.Revision ?? -1;

            using CancellationTokenSource connectionLifetime =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task streamTask = StreamSubscriptionAsync(
                connection,
                lease,
                initialRevision,
                connectionLifetime.Token);
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
        CancellationToken cancellationToken)
    {
        await foreach (ServiceStatusSnapshot snapshot in
            lease.ReadAllAsync(cancellationToken))
        {
            if (snapshot.Revision <= initialRevision)
            {
                continue;
            }

            await WriteFrameAsync(
                connection,
                new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, snapshot),
                cancellationToken);
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
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(FrameWriteTimeout, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            await IpcMessageCodec.WriteServerFrameAsync(connection, frame, linked.Token);
        }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("IPC frame write exceeded two seconds.", exception);
        }
    }

    private static ServiceResponse Failure(Guid requestId, string error) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId == Guid.Empty ? Guid.NewGuid() : requestId,
        Success: false,
        Error: error,
        Status: null,
        Result: null);
}
