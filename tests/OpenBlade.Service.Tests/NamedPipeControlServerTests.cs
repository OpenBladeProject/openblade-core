using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class NamedPipeControlServerTests
{
    [Fact]
    public async Task ServeOnceAsync_RoundTripsTypedRequestOverAclProtectedPipe()
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        await using NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
        var request = new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());

        await IpcMessageCodec.WriteRequestAsync(client, request, timeout.Token);
        ServiceResponse response = Assert.IsType<ServiceResponse>(
            await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
        await serverTask;

        Assert.True(response.Success);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.NotNull(response.Status);
    }

    [Fact]
    public async Task Protocol51GetStatus_ReturnsFrozenReadOnlyProjectionAndReleasesLease()
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        (Stream serverConnection, Stream client) =
            await CreateDuplexPairAsync(timeout.Token);
        await using (serverConnection)
        await using (client)
        {
            Task serverTask = context.Server.ServeConnectionAsync(
                serverConnection,
                timeout.Token);
            Guid requestId = Guid.NewGuid();
            await WriteProtocol51RequestAsync(client, "getStatus", requestId, timeout.Token);

            JsonObject response = await ReadJsonFrameAsync(client, timeout.Token);
            await serverTask;

            Assert.Equal(51, response["version"]!.GetValue<int>());
            Assert.Equal(requestId, response["requestId"]!.GetValue<Guid>());
            Assert.True(response["success"]!.GetValue<bool>());
            JsonObject status = Assert.IsType<JsonObject>(response["status"]);
            Assert.True(status["writesBlocked"]!.GetValue<bool>());
            Assert.Null(status["thermalConvergence"]);
            Assert.Equal(0, context.Runtime.ActivationCalls);
            Assert.Equal(0, context.Runtime.ProtocolCompatibilityClients);
        }
    }

    [Fact]
    public async Task Protocol51Mutation_IsRejectedBeforeHandler()
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        (Stream serverConnection, Stream client) =
            await CreateDuplexPairAsync(timeout.Token);
        await using (serverConnection)
        await using (client)
        {
            Task serverTask = context.Server.ServeConnectionAsync(
                serverConnection,
                timeout.Token);
            Guid requestId = Guid.NewGuid();
            await WriteProtocol51RequestAsync(
                client,
                "setFullChargeOverride",
                requestId,
                timeout.Token,
                "\"enabled\":true");

            JsonObject response = await ReadJsonFrameAsync(client, timeout.Token);
            await serverTask;

            Assert.Equal(51, response["version"]!.GetValue<int>());
            Assert.False(response["success"]!.GetValue<bool>());
            Assert.Contains(
                "Update or repair",
                response["error"]!.GetValue<string>(),
                StringComparison.Ordinal);
            Assert.Equal(0, context.Runtime.FullChargeOverrideCalls);
            Assert.Equal(0, context.Runtime.ProtocolCompatibilityClients);
        }
    }

    [Fact]
    public async Task Protocol51Subscription_HoldsCompatibilityLeaseForConnectionLifetime()
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        (Stream serverConnection, Stream client) =
            await CreateDuplexPairAsync(timeout.Token);
        await using (serverConnection)
        {
            Task serverTask = context.Server.ServeConnectionAsync(
                serverConnection,
                timeout.Token);
            await WriteProtocol51RequestAsync(
                client,
                "subscribeStatus",
                Guid.NewGuid(),
                timeout.Token);

            _ = await ReadJsonFrameAsync(client, timeout.Token);
            Assert.Equal(0, context.Runtime.ActivationCalls);
            Assert.Equal(1, context.Runtime.ProtocolCompatibilityClients);
            context.Hub.Publish(CreateStatus(1));
            JsonObject update = await ReadJsonFrameAsync(client, timeout.Token);
            Assert.Equal("statusEvent", update["frame"]!.GetValue<string>());
            Assert.Equal(51, update["version"]!.GetValue<int>());

            await client.DisposeAsync();
            await serverTask;
            Assert.Equal(0, context.Runtime.ProtocolCompatibilityClients);
        }
    }

    [Fact]
    public async Task ServeOnceAsync_ReadsRequestBeforeResolvingWindowsClientContext()
    {
        string pipeName = $"OpenBlade.Tests.{Guid.NewGuid():N}";
        var runtime = new StubRuntime(CreateStatus(0));
        var hub = new StatusSubscriptionHub(runtime.Status, TimeProvider.System);
        var server = new NamedPipeControlServer(
            pipeName,
            new ControlRequestHandler(runtime, new StubArchiveProvider()),
            hub,
            new WindowsServiceClientContextResolver());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = server.ServeOnceAsync(timeout.Token);
        await using NamedPipeClientStream client = await ConnectAsync(pipeName, timeout.Token);
        var request = new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());

        await IpcMessageCodec.WriteRequestAsync(client, request, timeout.Token);
        ServiceResponse response = Assert.IsType<ServiceResponse>(
            await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
        await serverTask;

        Assert.Equal(request.RequestId, response.RequestId);
    }

    [Fact]
    public async Task PulseBurst_AcceptsMultipleOrderedRequestsOnOneConnection()
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
        try
        {
            foreach (byte slot in new byte[] { 19, 53 })
            {
                var request = new PulseReactiveKeyRequest(
                    OpenBladeIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    new ReactiveKeyPulse(slot));
                await IpcMessageCodec.WriteRequestAsync(client, request, timeout.Token);
                ServiceResponse response = Assert.IsType<ServiceResponse>(
                    await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
                Assert.True(response.Success);
                Assert.Equal(request.RequestId, response.RequestId);
            }
        }
        finally
        {
            await client.DisposeAsync();
        }

        await serverTask;
    }

    [Fact]
    public async Task PulseBurst_RemainsConnectedAfterOrdinaryFrameReadDeadline()
    {
        var time = new ManualTimeProvider();
        TestServer context = CreateServer(time);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
        try
        {
            var first = new PulseReactiveKeyRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                new ReactiveKeyPulse(19));
            await IpcMessageCodec.WriteRequestAsync(client, first, timeout.Token);
            ServiceResponse firstResponse = Assert.IsType<ServiceResponse>(
                await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
            Assert.True(firstResponse.Success);

            time.Advance(TimeSpan.FromSeconds(3));
            await Task.Yield();
            Assert.False(serverTask.IsCompleted);

            var second = new PulseReactiveKeyRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                new ReactiveKeyPulse(53));
            await IpcMessageCodec.WriteRequestAsync(client, second, timeout.Token);
            ServiceResponse secondResponse = Assert.IsType<ServiceResponse>(
                await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
            Assert.True(secondResponse.Success);
            Assert.Equal(second.RequestId, secondResponse.RequestId);
        }
        finally
        {
            await client.DisposeAsync();
        }

        await serverTask;
    }

    [Fact]
    public async Task AmbientFrameBurst_AcceptsMultipleFramesOnOneConnection()
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
        try
        {
            for (int index = 0; index < 2; index++)
            {
                var colors = new RgbColor[AmbientLightingFrame.SlotCount];
                colors[1] = new RgbColor((byte)index, 2, 3);
                var request = new PushAmbientLightingFrameRequest(
                    OpenBladeIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    new AmbientLightingFrame(colors).ToRgbBytes());
                await IpcMessageCodec.WriteRequestAsync(client, request, timeout.Token);
                ServiceResponse response = Assert.IsType<ServiceResponse>(
                    await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
                Assert.True(response.Success);
                Assert.Equal(request.RequestId, response.RequestId);
            }
        }
        finally
        {
            await client.DisposeAsync();
        }

        await serverTask;
    }

    [Fact]
    public async Task PersistentConnectionCapacity_RejectsOverflowAndPreservesOrdinaryRequests()
    {
        TestServer context = CreateServer();
        TimeSpan assertionBound = TimeSpan.FromSeconds(5);
        using CancellationTokenSource assertionTimeout = new(assertionBound);
        using CancellationTokenSource persistentLifetime = new();
        var persistentConnections = new List<PersistentTestConnection>();
        string phase = "start";
        try
        {
            for (int index = 0; index < StatusSubscriptionHub.MaximumSubscribers; index++)
            {
                phase = $"admit status subscriber {index + 1}/{StatusSubscriptionHub.MaximumSubscribers}";
                PersistentTestConnection connection =
                    await StartPersistentConnectionAsync(
                        context.Server,
                        persistentLifetime.Token,
                        assertionTimeout.Token);
                persistentConnections.Add(connection);
                var request = new SubscribeStatusRequest(
                    OpenBladeIpc.ProtocolVersion,
                    Guid.NewGuid());
                await IpcMessageCodec.WriteRequestAsync(
                    connection.Client,
                    request,
                    assertionTimeout.Token);
                ServiceResponse admitted = Assert.IsType<ServiceResponse>(
                    await IpcMessageCodec.ReadServerFrameAsync(
                        connection.Client,
                        assertionTimeout.Token));
                Assert.True(admitted.Success);
            }

            foreach (ServiceRequest request in new ServiceRequest[]
            {
                new PulseReactiveKeyRequest(
                    OpenBladeIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    new ReactiveKeyPulse(19)),
                new PushAmbientLightingFrameRequest(
                    OpenBladeIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    new AmbientLightingFrame(new RgbColor[AmbientLightingFrame.SlotCount])
                        .ToRgbBytes()),
            })
            {
                phase = $"admit persistent {request.GetType().Name}";
                PersistentTestConnection connection =
                    await StartPersistentConnectionAsync(
                        context.Server,
                        persistentLifetime.Token,
                        assertionTimeout.Token);
                persistentConnections.Add(connection);
                await IpcMessageCodec.WriteRequestAsync(
                    connection.Client,
                    request,
                    assertionTimeout.Token);
                ServiceResponse admitted = Assert.IsType<ServiceResponse>(
                    await IpcMessageCodec.ReadServerFrameAsync(
                        connection.Client,
                        assertionTimeout.Token));
                Assert.True(admitted.Success);
            }

            phase = "reject overflow persistent request at capacity";
            (Stream overflowServer, Stream overflowClient) =
                await CreateDuplexPairAsync(assertionTimeout.Token);
            await using (overflowServer)
            await using (overflowClient)
            {
                Task overflowTask = context.Server.ServeConnectionAsync(
                    overflowServer,
                    assertionTimeout.Token);
                var overflow = new PulseReactiveKeyRequest(
                    OpenBladeIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    new ReactiveKeyPulse(53));
                await IpcMessageCodec.WriteRequestAsync(
                    overflowClient,
                    overflow,
                    assertionTimeout.Token);
                ServiceResponse rejected = Assert.IsType<ServiceResponse>(
                    await IpcMessageCodec.ReadServerFrameAsync(
                        overflowClient,
                        assertionTimeout.Token));
                await overflowTask;

                Assert.False(rejected.Success);
                Assert.Equal(ServiceResponseErrorCode.CapacityUnavailable, rejected.ErrorCode);
            }

            phase = "serve ordinary GetStatus request at capacity";
            (Stream ordinaryServer, Stream ordinaryClient) =
                await CreateDuplexPairAsync(assertionTimeout.Token);
            await using (ordinaryServer)
            await using (ordinaryClient)
            {
                Task ordinaryTask = context.Server.ServeConnectionAsync(
                    ordinaryServer,
                    assertionTimeout.Token);
                var request = new GetStatusRequest(
                    OpenBladeIpc.ProtocolVersion,
                    Guid.NewGuid());
                await IpcMessageCodec.WriteRequestAsync(
                    ordinaryClient,
                    request,
                    assertionTimeout.Token);
                ServiceResponse response = Assert.IsType<ServiceResponse>(
                    await IpcMessageCodec.ReadServerFrameAsync(
                        ordinaryClient,
                        assertionTimeout.Token));
                await ordinaryTask;

                Assert.True(response.Success);
            }

            phase = "release first status subscriber";
            PersistentTestConnection released = persistentConnections[0];
            await released.Client.DisposeAsync();
            await released.ServerTask.WaitAsync(assertionTimeout.Token);
            await released.Server.DisposeAsync();
            persistentConnections.RemoveAt(0);

            phase = "admit replacement status subscriber";
            PersistentTestConnection replacement =
                await StartPersistentConnectionAsync(
                    context.Server,
                    persistentLifetime.Token,
                    assertionTimeout.Token);
            persistentConnections.Add(replacement);
            var replacementRequest = new SubscribeStatusRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid());
            await IpcMessageCodec.WriteRequestAsync(
                replacement.Client,
                replacementRequest,
                assertionTimeout.Token);
            ServiceResponse replacementResponse = Assert.IsType<ServiceResponse>(
                await IpcMessageCodec.ReadServerFrameAsync(
                    replacement.Client,
                    assertionTimeout.Token));
            Assert.True(replacementResponse.Success);

            phase = "completed";
            assertionTimeout.Cancel();
            Assert.All(
                persistentConnections,
                connection => Assert.False(connection.ServerTask.IsCompleted));
        }
        catch (OperationCanceledException exception)
            when (assertionTimeout.IsCancellationRequested && phase != "completed")
        {
            throw new TimeoutException(
                $"Persistent-connection capacity test stalled in phase '{phase}' "
                + $"after the {assertionBound.TotalSeconds:0}-second assertion bound; "
                + DescribePersistentConnections(persistentConnections),
                exception);
        }
        finally
        {
            persistentLifetime.Cancel();
            foreach (PersistentTestConnection connection in persistentConnections)
            {
                await connection.Client.DisposeAsync();
            }

            TimeSpan teardownBound = TimeSpan.FromSeconds(5);
            using CancellationTokenSource teardownTimeout = new(teardownBound);
            try
            {
                await Task.WhenAll(persistentConnections.Select(
                    connection => AwaitPersistentCleanupAsync(
                        connection,
                        persistentLifetime.Token,
                        teardownTimeout.Token)));
            }
            catch (OperationCanceledException exception)
                when (teardownTimeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Persistent-connection capacity test stalled in phase 'teardown' "
                    + $"after the {teardownBound.TotalSeconds:0}-second teardown bound "
                    + $"(last assertion phase '{phase}'); "
                    + DescribePersistentConnections(persistentConnections),
                    exception);
            }
            finally
            {
                foreach (PersistentTestConnection connection in persistentConnections)
                {
                    await connection.Server.DisposeAsync();
                }
            }
        }
    }

    [Fact]
    public async Task ServeOnceAsync_ClaimsFirstPipeInstanceBeforeAcceptingClients()
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        await Task.Delay(50, timeout.Token);
        Assert.False(serverTask.IsFaulted, serverTask.Exception?.ToString());

        Exception startupClaimFailure = Assert.ThrowsAny<Exception>(() => NamedPipeServerStreamAcl.Create(
            context.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: NamedPipeControlServer.MaximumServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 0,
            outBufferSize: 0,
            PipeSecurityFactory.Create(WindowsIdentity.GetCurrent().User!)));
        Assert.True(startupClaimFailure is IOException or UnauthorizedAccessException);

        await using NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
        var request = new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());
        await IpcMessageCodec.WriteRequestAsync(client, request, timeout.Token);
        _ = await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token);
        await serverTask;
    }

    [Fact]
    public async Task ServeOnceAsync_RoundTripsBoundedDiagnosticArchiveOverAclProtectedPipe()
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        await using NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
        var request = new ExportDiagnosticsRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            LiveReadingsEnabled: true);

        await IpcMessageCodec.WriteRequestAsync(client, request, timeout.Token);
        ServiceResponse response = Assert.IsType<ServiceResponse>(
            await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
        await serverTask;

        Assert.True(response.Success);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Null(response.Status);
        Assert.Null(response.Result);
        Assert.Equal("OpenBlade-diagnostics.zip", response.Archive!.SuggestedFileName);
        Assert.Equal([0x50, 0x4B, 0x03, 0x04], response.Archive.ZipBytes);
    }

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public async Task ServeOnceAsync_ReturnsFailureForMalformedFrame(byte[] message)
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        await using NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);

        await client.WriteAsync(message, timeout.Token);
        await client.FlushAsync(timeout.Token);
        ServiceResponse response = Assert.IsType<ServiceResponse>(
            await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
        await serverTask;

        Assert.False(response.Success);
        Assert.Equal(OpenBladeIpc.ProtocolVersion, response.Version);
        Assert.NotEqual(Guid.Empty, response.RequestId);
    }

    [Fact]
    public async Task Subscribe_WritesInitialResponseThenNewStatusEvents()
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        await using NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
        var request = new SubscribeStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());
        await IpcMessageCodec.WriteRequestAsync(client, request, timeout.Token);

        ServiceResponse initial = Assert.IsType<ServiceResponse>(
            await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
        context.Hub.Publish(CreateStatus(1));
        ServiceStatusEvent update = Assert.IsType<ServiceStatusEvent>(
            await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));

        Assert.True(initial.Success);
        Assert.Equal(0, initial.Status!.Revision);
        Assert.Equal(1, update.Status.Revision);
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serverTask);
    }

    [Fact]
    public async Task Subscribe_BackgroundSessionStaysConnectedAndRegainsControl()
    {
        var runtime = new StubRuntime(CreateStatus(0));
        var hub = new StatusSubscriptionHub(runtime.Status, TimeProvider.System);
        var authority = new MutableSessionAuthority(4);
        var clientContext = new ServiceClientContext(
            new SecurityIdentifier("S-1-5-21-1-2-3-1001"),
            SessionId: 3,
            IsAdministrator: false,
            ServiceClientLogonKind.Interactive);
        var server = new NamedPipeControlServer(
            $"OpenBlade.Tests.{Guid.NewGuid():N}",
            WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Current user has no SID."),
            new ControlRequestHandler(
                runtime,
                new StubArchiveProvider(),
                NullServiceShutdownSignal.Instance,
                authority),
            hub);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        (Stream serverConnection, Stream client) =
            await CreateDuplexPairAsync(timeout.Token);
        await using (serverConnection)
        await using (client)
        {
            Task serverTask = server.ServeConnectionAsync(
                serverConnection,
                clientContext,
                timeout.Token);
            await IpcMessageCodec.WriteRequestAsync(
                client,
                new SubscribeStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
                timeout.Token);

            ServiceResponse initial = Assert.IsType<ServiceResponse>(
                await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
            hub.Publish(CreateStatus(1));
            ServiceStatusEvent inactive = Assert.IsType<ServiceStatusEvent>(
                await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
            authority.SessionId = 3;
            hub.Publish(CreateStatus(2));
            ServiceStatusEvent active = Assert.IsType<ServiceStatusEvent>(
                await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));

            Assert.True(initial.Success);
            Assert.Equal(SessionControlAccess.InactiveSession, initial.Status!.ControlAccess);
            Assert.Equal(SessionControlAccess.InactiveSession, inactive.Status.ControlAccess);
            Assert.Equal(SessionControlAccess.Active, active.Status.ControlAccess);
            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serverTask);
        }
    }

    [Fact]
    public async Task Subscribe_RejectedClientDoesNotAcquireLease()
    {
        var runtime = new StubRuntime(CreateStatus(0));
        var hub = new StatusSubscriptionHub(runtime.Status, TimeProvider.System);
        var clientContext = new ServiceClientContext(
            new SecurityIdentifier("S-1-5-21-1-2-3-1001"),
            SessionId: 3,
            IsAdministrator: false,
            ServiceClientLogonKind.Other);
        var server = new NamedPipeControlServer(
            $"OpenBlade.Tests.{Guid.NewGuid():N}",
            WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Current user has no SID."),
            new ControlRequestHandler(
                runtime,
                new StubArchiveProvider(),
                NullServiceShutdownSignal.Instance,
                new MutableSessionAuthority(3)),
            hub);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        (Stream serverConnection, Stream client) =
            await CreateDuplexPairAsync(timeout.Token);
        await using (serverConnection)
        await using (client)
        {
            Task serverTask = server.ServeConnectionAsync(
                serverConnection,
                clientContext,
                timeout.Token);
            await IpcMessageCodec.WriteRequestAsync(
                client,
                new SubscribeStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
                timeout.Token);

            ServiceResponse response = Assert.IsType<ServiceResponse>(
                await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
            await serverTask.WaitAsync(timeout.Token);

            Assert.False(response.Success);
            StatusSubscriptionLease[] leases = Enumerable
                .Range(0, StatusSubscriptionHub.MaximumSubscribers)
                .Select(_ => hub.Subscribe(CancellationToken.None))
                .ToArray();
            foreach (StatusSubscriptionLease lease in leases)
            {
                await lease.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Subscribe_WhenCapacityIsFull_ReturnsFailureWithoutLeakingSlot()
    {
        TestServer context = CreateServer();
        StatusSubscriptionLease[] leases = Enumerable
            .Range(0, StatusSubscriptionHub.MaximumSubscribers)
            .Select(_ => context.Hub.Subscribe(CancellationToken.None))
            .ToArray();
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
            await using NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
            await IpcMessageCodec.WriteRequestAsync(
                client,
                new SubscribeStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
                timeout.Token);

            ServiceResponse response = Assert.IsType<ServiceResponse>(
                await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
            await serverTask;

            Assert.False(response.Success);
            Assert.Contains("maximum", response.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ServiceResponseErrorCode.CapacityUnavailable, response.ErrorCode);
        }
        finally
        {
            foreach (StatusSubscriptionLease lease in leases)
            {
                await lease.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ServeConnectionAsync_CancelsBlockedFrameWriteAtTwoSeconds()
    {
        var time = new ManualTimeProvider();
        TestServer context = CreateServer(time);
        await using var stream = new BlockingWriteStream(
            await EncodeAsync(new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid())));

        Task serving = context.Server.ServeConnectionAsync(stream, CancellationToken.None);
        await stream.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(2));

        await serving.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stream.WriteCancellationObserved);
    }

    [Fact]
    public async Task ServeConnectionAsync_CancelsIncompleteFrameReadAtTwoSeconds()
    {
        var time = new ManualTimeProvider();
        TestServer context = CreateServer(time);
        await using var stream = new BlockingReadStream();

        Task serving = context.Server.ServeConnectionAsync(stream, CancellationToken.None);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(2));

        await serving.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stream.ReadCancellationObserved);
    }

    [Fact]
    public async Task ServeOnceAsync_StalledInitialClientDoesNotPreventNextConnection()
    {
        var time = new ManualTimeProvider();
        TestServer context = CreateServer(time);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        Task stalledServer = context.Server.ServeOnceAsync(timeout.Token);
        await using NamedPipeClientStream stalledClient = await ConnectAsync(
            context.PipeName,
            timeout.Token);
        await time.TimerCreated.Task.WaitAsync(timeout.Token);

        time.Advance(TimeSpan.FromSeconds(2));

        await stalledServer.WaitAsync(timeout.Token);
        Task nextServer = context.Server.ServeOnceAsync(timeout.Token);
        await using NamedPipeClientStream nextClient = await ConnectAsync(
            context.PipeName,
            timeout.Token);
        await IpcMessageCodec.WriteRequestAsync(
            nextClient,
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            timeout.Token);
        ServiceResponse response = Assert.IsType<ServiceResponse>(
            await IpcMessageCodec.ReadServerFrameAsync(nextClient, timeout.Token));
        await nextServer.WaitAsync(timeout.Token);

        Assert.True(response.Success);
    }

    [Fact]
    public async Task Subscribe_DisconnectDisposesLeaseAndReleasesCapacity()
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
        await IpcMessageCodec.WriteRequestAsync(
            client,
            new SubscribeStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            timeout.Token);
        _ = await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token);
        await client.DisposeAsync();
        await serverTask.WaitAsync(timeout.Token);

        StatusSubscriptionLease[] leases = Enumerable
            .Range(0, StatusSubscriptionHub.MaximumSubscribers)
            .Select(_ => context.Hub.Subscribe(CancellationToken.None))
            .ToArray();
        Assert.Throws<InvalidOperationException>(() => context.Hub.Subscribe(CancellationToken.None));
        foreach (StatusSubscriptionLease lease in leases)
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task Subscribe_CompletesWhenFifteenMinuteLeaseExpires()
    {
        var time = new ManualTimeProvider();
        TestServer context = CreateServer(time);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        await using NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
        await IpcMessageCodec.WriteRequestAsync(
            client,
            new SubscribeStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            timeout.Token);
        _ = await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token);

        time.Advance(TimeSpan.FromMinutes(15));

        await serverTask.WaitAsync(timeout.Token);
    }

    public static TheoryData<byte[]> InvalidMessages()
    {
        TheoryData<byte[]> data = [];
        data.Add(Frame(Encoding.UTF8.GetBytes("null")));
        data.Add(Frame(Encoding.UTF8.GetBytes(
            "{\"version\":3,\"requestId\":\"23c09fe2-cbb8-4b74-a175-78dfb7831cf1\",\"operation\":\"unknown\"}")));
        data.Add(Frame(Encoding.UTF8.GetBytes(
            "{\"version\":1,\"requestId\":\"23c09fe2-cbb8-4b74-a175-78dfb7831cf1\",\"operation\":\"getStatus\"}")));
        data.Add(Frame(Encoding.UTF8.GetBytes(
            "{\"version\":3,\"requestId\":\"00000000-0000-0000-0000-000000000000\",\"operation\":\"getStatus\"}")));
        byte[] oversized = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(oversized, OpenBladeIpc.MaximumMessageBytes + 1);
        data.Add(oversized);
        return data;
    }

    private static TestServer CreateServer(TimeProvider? timeProvider = null)
    {
        string pipeName = $"OpenBlade.Tests.{Guid.NewGuid():N}";
        var runtime = new StubRuntime(CreateStatus(0));
        TimeProvider time = timeProvider ?? TimeProvider.System;
        var hub = new StatusSubscriptionHub(runtime.Status, time);
        var server = new NamedPipeControlServer(
            pipeName,
            WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Current user has no SID."),
            new ControlRequestHandler(runtime, new StubArchiveProvider()),
            hub,
            time);
        return new TestServer(pipeName, server, hub, runtime);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(
        string pipeName,
        CancellationToken token)
    {
        var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(token);
        return client;
    }

    private static async Task<byte[]> EncodeAsync(ServiceRequest request)
    {
        await using var stream = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None);
        return stream.ToArray();
    }

    private static async Task WriteProtocol51RequestAsync(
        Stream stream,
        string operation,
        Guid requestId,
        CancellationToken token,
        string? additionalMembers = null)
    {
        string suffix = string.IsNullOrEmpty(additionalMembers)
            ? string.Empty
            : $",{additionalMembers}";
        byte[] payload = Encoding.UTF8.GetBytes(
            $"{{\"operation\":\"{operation}\",\"version\":51,\"requestId\":\"{requestId}\"{suffix}}}");
        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    private static async Task<JsonObject> ReadJsonFrameAsync(
        Stream stream,
        CancellationToken token)
    {
        byte[] prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        Assert.InRange(length, 1, OpenBladeIpc.MaximumMessageBytes);
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        return Assert.IsType<JsonObject>(JsonNode.Parse(payload));
    }

    private static async Task<PersistentTestConnection> StartPersistentConnectionAsync(
        NamedPipeControlServer server,
        CancellationToken persistentLifetime,
        CancellationToken assertionToken)
    {
        (Stream serverConnection, Stream client) =
            await CreateDuplexPairAsync(assertionToken);
        return new PersistentTestConnection(
            serverConnection,
            client,
            server.ServeConnectionAsync(serverConnection, persistentLifetime));
    }

    private static string DescribePersistentConnections(
        IReadOnlyList<PersistentTestConnection> connections)
    {
        int completed = connections.Count(
            connection => connection.ServerTask.IsCompleted);
        int faulted = connections.Count(
            connection => connection.ServerTask.IsFaulted);
        return $"persistent connections={connections.Count}, "
            + $"server tasks completed={completed}, faulted={faulted}";
    }

    private static async Task AwaitPersistentCleanupAsync(
        PersistentTestConnection connection,
        CancellationToken persistentLifetime,
        CancellationToken teardownToken)
    {
        try
        {
            await connection.ServerTask.WaitAsync(teardownToken);
        }
        catch (OperationCanceledException exception)
            when (persistentLifetime.IsCancellationRequested &&
                  !teardownToken.IsCancellationRequested &&
                  exception.CancellationToken == persistentLifetime)
        {
        }
    }

    private static async Task<(Stream Server, Stream Client)> CreateDuplexPairAsync(
        CancellationToken cancellationToken)
    {
        string pipeName = $"OpenBlade.Tests.Duplex.{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            Task accepting = server.WaitForConnectionAsync(cancellationToken);
            await client.ConnectAsync(cancellationToken);
            await accepting;
            return (server, client);
        }
        catch
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            throw;
        }
    }

    private static byte[] Frame(byte[] payload)
    {
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));
        return frame;
    }

    private static ServiceStatusSnapshot CreateStatus(long revision)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            revision, false, true, [], null, [], "Unavailable",
            DeviceCapabilities.Unavailable(), null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn,
            null,
            null, null, null, false,
            Firmware: null,
            ServiceVersion: "0.1.0-test");
    }

    private sealed record TestServer(
        string PipeName,
        NamedPipeControlServer Server,
        StatusSubscriptionHub Hub,
        StubRuntime Runtime);

    private sealed record PersistentTestConnection(
        Stream Server,
        Stream Client,
        Task ServerTask);

    private sealed class StubRuntime(ServiceStatusSnapshot status) : IServiceControlRuntime
    {
        private int protocolCompatibilityClients;

        public ServiceStatusSnapshot Status { get; } = status;
        public int FullChargeOverrideCalls { get; private set; }
        public int ActivationCalls { get; private set; }
        public int ProtocolCompatibilityClients =>
            Volatile.Read(ref protocolCompatibilityClients);
        public ServiceStatusSnapshot GetStatus() => Status;
        public Task ActivateUserAsync(
            SecurityIdentifier userSid,
            Func<bool> stillControlsSession,
            CancellationToken cancellationToken)
        {
            ActivationCalls++;
            return Task.CompletedTask;
        }
        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(ManualOverride<PerformancePreset>? value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled, CancellationToken token)
        {
            FullChargeOverrideCalls++;
            return Result();
        }
        public IDisposable RegisterProtocolCompatibilityClient()
        {
            Interlocked.Increment(ref protocolCompatibilityClients);
            return new CallbackDisposable(() =>
                Interlocked.Decrement(ref protocolCompatibilityClients));
        }
        public Task ObserveAsync(bool includeLiveReadings, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshControllerPresenceAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        private static Task<ControlOperationResult> Result() => Task.FromResult(
            new ControlOperationResult(ControlOperationStatus.Stored, "Stored"));
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? callback = callback;

        public void Dispose() => Interlocked.Exchange(ref callback, null)?.Invoke();
    }

    private sealed class StubArchiveProvider : IDiagnosticArchiveProvider
    {
        public Task<DiagnosticArchive> CreateAsync(
            bool liveReadingsEnabled,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DiagnosticArchive(
                "OpenBlade-diagnostics.zip",
                [0x50, 0x4B, 0x03, 0x04]));
    }

    private sealed class MutableSessionAuthority(int? sessionId)
        : IInteractiveSessionAuthority
    {
        public int? SessionId { get; set; } = sessionId;

        public int? GetControlSessionId() => SessionId;
    }

    private sealed class BlockingWriteStream(byte[] input) : Stream
    {
        private readonly MemoryStream reader = new(input, writable: false);
        public TaskCompletionSource WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WriteCancellationObserved { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            reader.ReadAsync(buffer, token);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            WriteStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                WriteCancellationObserved = true;
                throw;
            }
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { reader.Dispose(); } base.Dispose(disposing); }
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReadCancellationObserved { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken token = default)
        {
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            }
            catch (OperationCanceledException)
            {
                ReadCancellationObserved = true;
                throw;
            }
        }
        public override void Flush()
        {
        }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public TaskCompletionSource TimerCreated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, now + dueTime, period);
            timers.Add(timer);
            TimerCreated.TrySetResult();
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            now += amount;
            foreach (ManualTimer timer in timers.ToArray())
            {
                timer.FireIfDue(now);
            }
        }
        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period) : ITimer
        {
            private bool disposed;
            private DateTimeOffset next = due;
            public void FireIfDue(DateTimeOffset now)
            {
                if (disposed || now < next)
                {
                    return;
                }

                callback(state);
                if (period == Timeout.InfiniteTimeSpan)
                {
                    disposed = true;
                }
                else
                {
                    next = now + period;
                }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
