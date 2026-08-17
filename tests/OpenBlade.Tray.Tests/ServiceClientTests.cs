using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ServiceClientTests
{
    [Fact]
    public async Task GetStatusAsync_ValidatesVersionRequestIdAndResponseSubtype()
    {
        string pipeName = NewPipeName();
        ServiceStatusSnapshot expected = CreateStatus(3);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunServerAsync(
            pipeName,
            request =>
            {
                Assert.IsType<GetStatusRequest>(request);
                Assert.Equal(OpenBladeIpc.ProtocolVersion, request.Version);
                Assert.NotEqual(Guid.Empty, request.RequestId);
                return new ServiceResponse(
                    OpenBladeIpc.ProtocolVersion,
                    request.RequestId,
                    true,
                    null,
                    expected,
                    null);
            },
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);

        ServiceStatusSnapshot actual = await client.GetStatusAsync(timeout.Token);
        await server;

        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.DeviceVerified, actual.DeviceVerified);
        Assert.Equal(expected.WritesBlocked, actual.WritesBlocked);
        Assert.Equal(expected.PotentialControllers, actual.PotentialControllers);
        Assert.Equal(expected.ControllerDetectionWarning, actual.ControllerDetectionWarning);
        Assert.Equal(expected.ExternalChanges, actual.ExternalChanges);
        Assert.Equal(expected.UnconfirmedWrites, actual.UnconfirmedWrites);
        Assert.Equal(expected.Message, actual.Message);
    }

    [Fact]
    public async Task GetStatusAsync_RejectsMismatchedResponseId()
    {
        await AssertInvalidStatusResponseAsync(request => new ServiceResponse(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            true,
            null,
            CreateStatus(0),
            null));
    }

    [Fact]
    public async Task GetStatusAsync_RejectsWrongServerFrameSubtype()
    {
        await AssertInvalidStatusResponseAsync(_ =>
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, CreateStatus(1)));
    }

    [Fact]
    public async Task GetStatusAsync_RejectsNonSuccessResponse()
    {
        await AssertInvalidStatusResponseAsync(request => new ServiceResponse(
            OpenBladeIpc.ProtocolVersion,
            request.RequestId,
            false,
            "Unavailable",
            null,
            null));
    }

    [Fact]
    public async Task GetStatusAsync_RejectsVersionMismatch()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunRawServerAsync(
            pipeName,
            request => Frame(Encoding.UTF8.GetBytes(
                $"{{\"frame\":\"response\",\"version\":1,\"requestId\":\"{request.RequestId}\",\"success\":false,\"error\":\"bad version\",\"status\":null,\"result\":null}}")),
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetStatusAsync(timeout.Token));
        await server;
    }

    [Fact]
    public async Task ObserveStateAsync_SendsOneTypedV3RequestAndReturnsStatus()
    {
        string pipeName = NewPipeName();
        ServiceStatusSnapshot expected = CreateStatus(7);
        int requests = 0;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunServerAsync(
            pipeName,
            request =>
            {
                requests++;
                ObserveStateRequest observe = Assert.IsType<ObserveStateRequest>(request);
                Assert.Equal(OpenBladeIpc.ProtocolVersion, request.Version);
                Assert.NotEqual(Guid.Empty, request.RequestId);
                Assert.True(observe.IncludeLiveReadings);
                return new ServiceResponse(
                    OpenBladeIpc.ProtocolVersion,
                    request.RequestId,
                    true,
                    null,
                    expected,
                    null);
            },
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);

        ServiceStatusSnapshot actual = await client.ObserveStateAsync(true, timeout.Token);
        await server;

        Assert.Equal(7, actual.Revision);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task ObserveStateAsync_RejectsSuccessfulResponseWithoutStatusBody()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunRawServerAsync(
            pipeName,
            request => Frame(Encoding.UTF8.GetBytes(
                $"{{\"frame\":\"response\",\"version\":{OpenBladeIpc.ProtocolVersion},\"requestId\":\"{request.RequestId}\",\"success\":true,\"error\":null,\"status\":null,\"result\":null,\"archive\":null}}")),
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.ObserveStateAsync(true, timeout.Token));
        await server;
    }

    [Fact]
    public async Task ObserveStateAsync_RejectsMismatchedResponseId()
    {
        await AssertInvalidObserveResponseAsync(request => new ServiceResponse(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            true,
            null,
            CreateStatus(0),
            null));
    }

    [Fact]
    public async Task ObserveStateAsync_RejectsVersionMismatch()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunRawServerAsync(
            pipeName,
            request => Frame(Encoding.UTF8.GetBytes(
                $"{{\"frame\":\"response\",\"version\":{OpenBladeIpc.ProtocolVersion - 1},\"requestId\":\"{request.RequestId}\",\"success\":false,\"error\":\"bad version\",\"status\":null,\"result\":null,\"archive\":null}}")),
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.ObserveStateAsync(true, timeout.Token));
        await server;
    }

    [Fact]
    public async Task ExportDiagnosticsAsync_SendsTypedRequestAndReturnsArchiveBody()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        var expected = new DiagnosticArchive(
            "OpenBlade-diagnostics.zip",
            [0x50, 0x4B, 0x03, 0x04]);
        Task server = RunServerAsync(
            pipeName,
            request =>
            {
                ExportDiagnosticsRequest export =
                    Assert.IsType<ExportDiagnosticsRequest>(request);
                Assert.Equal(OpenBladeIpc.ProtocolVersion, request.Version);
                Assert.NotEqual(Guid.Empty, request.RequestId);
                Assert.True(export.LiveReadingsEnabled);
                return new ServiceResponse(
                    OpenBladeIpc.ProtocolVersion,
                    request.RequestId,
                    true,
                    null,
                    null,
                    null,
                    expected);
            },
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);

        DiagnosticArchive actual = await client.ExportDiagnosticsAsync(
            liveReadingsEnabled: true,
            timeout.Token);
        await server;

        Assert.Equal(expected.SuggestedFileName, actual.SuggestedFileName);
        Assert.Equal(expected.ZipBytes, actual.ZipBytes);
    }

    [Fact]
    public async Task ExportDiagnosticsAsync_RejectsSuccessfulResponseWithoutArchiveBody()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunRawServerAsync(
            pipeName,
            request => Frame(Encoding.UTF8.GetBytes(
                $"{{\"frame\":\"response\",\"version\":{OpenBladeIpc.ProtocolVersion},\"requestId\":\"{request.RequestId}\",\"success\":true,\"error\":null,\"status\":null,\"result\":null,\"archive\":null}}")),
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.ExportDiagnosticsAsync(
                liveReadingsEnabled: false,
                timeout.Token));
        await server;
    }

    [Fact]
    public async Task TypedMutations_SendEveryClosedMutationRequestOnFreshConnections()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        var observed = new List<ServiceRequest>();
        Task server = RunMutationServersAsync(pipeName, observed, timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        Guid shortcutSessionId = Guid.NewGuid();

        ControlOperationResult[] results =
        [
            await client.UpdateProfilesAsync(profiles, timeout.Token),
            await client.SetPerformanceOverrideAsync(
                new ManualOverride<PerformancePreset>(PerformancePreset.Performance, OverrideLifetime.Persistent),
                timeout.Token),
            await client.CyclePerformanceShortcutAsync(
                PerformanceShortcutSource.DedicatedButton,
                timeout.Token),
            await client.SetPerformanceShortcutPreviewAsync(
                shortcutSessionId,
                generation: 3,
                PerformancePreset.Performance,
                timeout.Token),
            await client.SetFanOverrideAsync(
                new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.UntilPowerSourceChange),
                timeout.Token),
            await client.SetLightingOverrideAsync(null, timeout.Token),
            await client.StepKeyboardBrightnessAsync(
                KeyboardBrightnessDirection.Increase,
                OverrideLifetime.UntilPowerSourceChange,
                timeout.Token),
            await client.SetDisplayBrightnessHidUsageAsync(
                DisplayBrightnessHidUsage.Decrement,
                timeout.Token),
            await client.SetExternalLightingOverridePolicyAsync(true, timeout.Token),
            await client.SetLightingRefreshRateAsync(
                LightingRefreshRate.SixtyHz,
                timeout.Token),
            await client.SetFirmwareCompatibilityOverrideAsync(true, timeout.Token),
            await client.ReapplyChangedSettingsAsync(timeout.Token),
            await client.SetLightingSuppressionAsync(
                LightingSuppressionReason.DisplayOff,
                suppressed: true,
                timeout.Token),
            await client.PulseReactiveKeyAsync(new ReactiveKeyPulse(77), timeout.Token),
            await client.SetBatteryPolicyAsync(BatteryPolicy.Create(75), timeout.Token),
            await client.SetBatteryProtectionAsync(false, timeout.Token),
            await client.SetFullChargeOverrideAsync(true, timeout.Token),
            await client.ApplyOneShotLaptopControlAsync(
                new ApplyOneShotLaptopControl(
                    OneShotLaptopControl.GamingMode,
                    Enabled: true),
                timeout.Token),
            await client.ShutdownOpenBladeAsync(timeout.Token),
        ];
        await server;

        Assert.All(results, result => Assert.Equal(ControlOperationStatus.Stored, result.Status));
        Assert.Collection(
            observed,
            request => Assert.IsType<UpdateProfilesRequest>(request),
            request => Assert.IsType<SetPerformanceOverrideRequest>(request),
            request => Assert.Equal(
                PerformanceShortcutSource.DedicatedButton,
                Assert.IsType<CyclePerformanceShortcutRequest>(request).Source),
            request =>
            {
                SetPerformanceShortcutPreviewRequest preview =
                    Assert.IsType<SetPerformanceShortcutPreviewRequest>(request);
                Assert.Equal(shortcutSessionId, preview.SessionId);
                Assert.Equal(3, preview.Generation);
                Assert.Equal(PerformancePreset.Performance, preview.Preset);
            },
            request => Assert.IsType<SetFanOverrideRequest>(request),
            request => Assert.IsType<SetLightingOverrideRequest>(request),
            request =>
            {
                StepKeyboardBrightnessRequest step =
                    Assert.IsType<StepKeyboardBrightnessRequest>(request);
                Assert.Equal(KeyboardBrightnessDirection.Increase, step.Direction);
                Assert.Equal(OverrideLifetime.UntilPowerSourceChange, step.Lifetime);
            },
            request => Assert.Equal(
                DisplayBrightnessHidUsage.Decrement,
                Assert.IsType<SetDisplayBrightnessHidUsageRequest>(request).Usage),
            request => Assert.True(
                Assert.IsType<SetExternalLightingOverridePolicyRequest>(request).Enabled),
            request => Assert.Equal(
                LightingRefreshRate.SixtyHz,
                Assert.IsType<SetLightingRefreshRateRequest>(request).RefreshRate),
            request => Assert.True(
                Assert.IsType<SetFirmwareCompatibilityOverrideRequest>(request).Enabled),
            request => Assert.IsType<ReapplyChangedSettingsRequest>(request),
            request =>
            {
                SetLightingSuppressionRequest suppression =
                    Assert.IsType<SetLightingSuppressionRequest>(request);
                Assert.Equal(LightingSuppressionReason.DisplayOff, suppression.Reason);
                Assert.True(suppression.Suppressed);
            },
            request => Assert.Equal(
                77,
                Assert.IsType<PulseReactiveKeyRequest>(request).Pulse.MatrixSlot),
            request => Assert.IsType<SetBatteryPolicyRequest>(request),
            request => Assert.False(
                Assert.IsType<SetBatteryProtectionRequest>(request).Enabled),
            request => Assert.IsType<SetFullChargeOverrideRequest>(request),
            request =>
            {
                ApplyOneShotLaptopControlRequest apply =
                    Assert.IsType<ApplyOneShotLaptopControlRequest>(request);
                Assert.Equal(OneShotLaptopControl.GamingMode, apply.Value.Control);
                Assert.True(apply.Value.Enabled);
            },
            request => Assert.IsType<ShutdownOpenBladeRequest>(request));
        Assert.All(observed, request => Assert.NotEqual(Guid.Empty, request.RequestId));
        Assert.Equal(19, observed.Select(request => request.RequestId).Distinct().Count());
    }

    [Fact]
    public async Task ReactivePulseBurst_ReusesOneOrderedConnection()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        var observed = new List<ReactiveKeyPulse>();
        Task server = RunReactivePulseBurstServerAsync(
            pipeName,
            expectedCount: 2,
            observed,
            timeout.Token);
        int connectionAttempts = 0;
        var client = new ServiceClient(
            async token =>
            {
                connectionAttempts++;
                var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(token);
                return pipe;
            },
            TimeProvider.System);

        await client.PulseReactiveKeyAsync(new ReactiveKeyPulse(19), timeout.Token);
        await client.PulseReactiveKeyAsync(new ReactiveKeyPulse(53), timeout.Token);
        await server;

        Assert.Equal(1, connectionAttempts);
        Assert.Equal([19, 53], observed.Select(pulse => (int)pulse.MatrixSlot));
    }

    [Fact]
    public async Task AmbientFrameBurst_ReusesOneOrderedConnection()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        var observed = new List<AmbientLightingFrame>();
        Task server = RunAmbientFrameBurstServerAsync(
            pipeName,
            expectedCount: 2,
            observed,
            timeout.Token);
        int connectionAttempts = 0;
        var client = new ServiceClient(
            async token =>
            {
                connectionAttempts++;
                var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(token);
                return pipe;
            },
            TimeProvider.System);
        var first = new RgbColor[AmbientLightingFrame.SlotCount];
        var second = new RgbColor[AmbientLightingFrame.SlotCount];
        first[1] = new RgbColor(1, 2, 3);
        second[1] = new RgbColor(4, 5, 6);

        await client.PushAmbientLightingFrameAsync(
            new AmbientLightingFrame(first),
            timeout.Token);
        await client.PushAmbientLightingFrameAsync(
            new AmbientLightingFrame(second),
            timeout.Token);
        await server;

        Assert.Equal(1, connectionAttempts);
        Assert.Equal(new RgbColor(1, 2, 3), observed[0].Slots[1]);
        Assert.Equal(new RgbColor(4, 5, 6), observed[1].Slots[1]);
    }

    [Fact]
    public async Task ReactivePulse_CapacityRejectionBacksOffAndRecovers()
    {
        string pipeName = NewPipeName();
        var time = new FakeTimeProvider();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task rejectedServer = RunServerAsync(
            pipeName,
            request => CapacityUnavailable(request.RequestId),
            timeout.Token);
        var client = new ServiceClient(pipeName, time);

        Task<ControlOperationResult> send = client.PulseReactiveKeyAsync(
            new ReactiveKeyPulse(19),
            timeout.Token);
        await rejectedServer;
        await WaitUntilAsync(() => time.Delays.Count == 1);

        Task acceptedServer = RunServerAsync(
            pipeName,
            request => Applied(request.RequestId),
            timeout.Token);
        Assert.Equal(TimeSpan.FromMilliseconds(250), time.Delays[0]);
        time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.Equal(ControlOperationStatus.Applied, (await send).Status);
        await acceptedServer;
    }

    [Fact]
    public async Task AmbientFrame_CapacityRejectionBacksOffAndRecovers()
    {
        string pipeName = NewPipeName();
        var time = new FakeTimeProvider();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task rejectedServer = RunServerAsync(
            pipeName,
            request => CapacityUnavailable(request.RequestId),
            timeout.Token);
        var client = new ServiceClient(pipeName, time);
        var slots = new RgbColor[AmbientLightingFrame.SlotCount];

        Task<ControlOperationResult> send = client.PushAmbientLightingFrameAsync(
            new AmbientLightingFrame(slots),
            timeout.Token);
        await rejectedServer;
        await WaitUntilAsync(() => time.Delays.Count == 1);

        Task acceptedServer = RunServerAsync(
            pipeName,
            request => Applied(request.RequestId),
            timeout.Token);
        Assert.Equal(TimeSpan.FromMilliseconds(250), time.Delays[0]);
        time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.Equal(ControlOperationStatus.Applied, (await send).Status);
        await acceptedServer;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LightingStream_NonCapacityRejectionDoesNotRetry(bool ambient)
    {
        string pipeName = NewPipeName();
        var time = new FakeTimeProvider();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunServerAsync(
            pipeName,
            request => new ServiceResponse(
                OpenBladeIpc.ProtocolVersion,
                request.RequestId,
                Success: false,
                Error: "Rejected",
                Status: null,
                Result: null),
            timeout.Token);
        int connectionAttempts = 0;
        var client = new ServiceClient(
            async token =>
            {
                connectionAttempts++;
                var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(token);
                return pipe;
            },
            time);
        var slots = new RgbColor[AmbientLightingFrame.SlotCount];

        Task<ControlOperationResult> send = ambient
            ? client.PushAmbientLightingFrameAsync(
                new AmbientLightingFrame(slots),
                timeout.Token)
            : client.PulseReactiveKeyAsync(new ReactiveKeyPulse(19), timeout.Token);

        await Assert.ThrowsAsync<InvalidDataException>(() => send);
        await server;
        Assert.Equal(1, connectionAttempts);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task DisposeAsync_ClosesPersistentStreamingConnections()
    {
        string reactivePipeName = NewPipeName();
        string ambientPipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task firstServer = RunPersistentStreamingServerAsync(
            reactivePipeName,
            timeout.Token);
        Task secondServer = RunPersistentStreamingServerAsync(
            ambientPipeName,
            timeout.Token);
        int connections = 0;
        var client = new ServiceClient(
            async token =>
            {
                string pipeName = Interlocked.Increment(ref connections) == 1
                    ? reactivePipeName
                    : ambientPipeName;
                var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(token);
                return pipe;
            },
            TimeProvider.System);
        var slots = new RgbColor[AmbientLightingFrame.SlotCount];

        await client.PulseReactiveKeyAsync(new ReactiveKeyPulse(19), timeout.Token);
        await client.PushAmbientLightingFrameAsync(
            new AmbientLightingFrame(slots),
            timeout.Token);

        await client.DisposeAsync();
        await Task.WhenAll(firstServer, secondServer);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.GetStatusAsync(timeout.Token));
    }

    [Fact]
    public async Task CanceledReactivePulse_DiscardsConnectionBeforeNextPulse()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        var blocked = new BlockingHandshakeStream();
        Task server = RunServerAsync(
            pipeName,
            request => new ServiceResponse(
                OpenBladeIpc.ProtocolVersion,
                request.RequestId,
                true,
                null,
                null,
                new ControlOperationResult(ControlOperationStatus.Applied, "Applied")),
            timeout.Token);
        int connectionAttempts = 0;
        var client = new ServiceClient(
            async token =>
            {
                if (connectionAttempts++ == 0)
                {
                    return blocked;
                }

                var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(token);
                return pipe;
            },
            TimeProvider.System);
        using var canceled = new CancellationTokenSource();
        Task first = client.PulseReactiveKeyAsync(new ReactiveKeyPulse(19), canceled.Token);
        await blocked.ReadStarted.Task.WaitAsync(timeout.Token);

        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(blocked.Disposed);

        ControlOperationResult second = await client.PulseReactiveKeyAsync(
            new ReactiveKeyPulse(53),
            timeout.Token);
        await server;

        Assert.Equal(ControlOperationStatus.Applied, second.Status);
        Assert.Equal(2, connectionAttempts);
    }

    [Fact]
    public async Task MutationIoFailure_IsNeverRetried()
    {
        int attempts = 0;
        var client = new ServiceClient(
            _ =>
            {
                attempts++;
                throw new IOException("offline");
            },
            new FakeTimeProvider());

        await Assert.ThrowsAsync<IOException>(() =>
            client.SetFullChargeOverrideAsync(true, CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WatchStatusAsync_YieldsInitialResponseThenStatusEvents()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunSubscriptionServerAsync(
            pipeName,
            CreateStatus(4),
            [CreateStatus(5), CreateStatus(6)],
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(timeout.Token).GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(4, Assert.IsType<ServiceWatchUpdate.Connected>(reader.Current).Status.Revision);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(5, Assert.IsType<ServiceWatchUpdate.Connected>(reader.Current).Status.Revision);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(6, Assert.IsType<ServiceWatchUpdate.Connected>(reader.Current).Status.Revision);
        await server;
    }

    [Fact]
    public async Task WatchStatusAsync_InactiveSessionRemainsConnectedUntilControlReturns()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunSubscriptionServerAsync(
            pipeName,
            CreateStatus(4) with { ControlAccess = SessionControlAccess.InactiveSession },
            [CreateStatus(5) with { ControlAccess = SessionControlAccess.Active }],
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(timeout.Token).GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync());
        ServiceStatusSnapshot inactive =
            Assert.IsType<ServiceWatchUpdate.Connected>(reader.Current).Status;
        Assert.Equal(SessionControlAccess.InactiveSession, inactive.ControlAccess);
        Assert.True(await reader.MoveNextAsync());
        ServiceStatusSnapshot active =
            Assert.IsType<ServiceWatchUpdate.Connected>(reader.Current).Status;
        Assert.Equal(SessionControlAccess.Active, active.ControlAccess);
        await server;
    }

    [Fact]
    public async Task GetStatusAsync_RejectsOversizedFramePrefix()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunRawServerAsync(
            pipeName,
            _ =>
            {
                byte[] prefix = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(
                    prefix,
                    OpenBladeIpc.MaximumMessageBytes + 1);
                return prefix;
            },
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetStatusAsync(timeout.Token));
        await server;
    }

    [Fact]
    public async Task CancellationDuringConnect_ExitsImmediatelyWithoutRetryDelay()
    {
        var time = new FakeTimeProvider();
        int attempts = 0;
        var client = new ServiceClient(
            async token =>
            {
                attempts++;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable.");
            },
            time);
        using CancellationTokenSource cancellation = new();
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(cancellation.Token).GetAsyncEnumerator();
        Task<bool> move = reader.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => attempts == 1);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Empty(time.Delays);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WatchStatusAsync_MissingPipeYieldsReconnectingWithinBoundedTime()
    {
        var time = new FakeTimeProvider();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        var client = new ServiceClient(NewPipeName(), time);
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(timeout.Token).GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync());
        Assert.IsType<ServiceWatchUpdate.Reconnecting>(reader.Current);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task WatchStatusAsync_ConnectTimeoutIsRetryable()
    {
        var time = new FakeTimeProvider();
        int attempts = 0;
        var client = new ServiceClient(
            _ =>
            {
                attempts++;
                throw new TimeoutException("offline");
            },
            time);
        using CancellationTokenSource cancellation = new();
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(cancellation.Token).GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync());
        Assert.IsType<ServiceWatchUpdate.Reconnecting>(reader.Current);
        Task<bool> next = reader.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => time.Delays.Count == 1);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CancellationDuringSubscriptionHandshake_DisposesAcquiredStream()
    {
        var stream = new BlockingHandshakeStream();
        int attempts = 0;
        var client = new ServiceClient(
            _ =>
            {
                attempts++;
                return ValueTask.FromResult<Stream>(stream);
            },
            new FakeTimeProvider());
        using CancellationTokenSource cancellation = new();
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(cancellation.Token).GetAsyncEnumerator();
        Task<bool> move = reader.MoveNextAsync().AsTask();
        await stream.ReadStarted.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.True(stream.Disposed);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WatchStatusAsync_ImmediatelyReopensHealthyRotatedSubscription()
    {
        string pipeName = NewPipeName();
        var time = new FakeTimeProvider();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        Task firstServer = RunSubscriptionServerAsync(
            pipeName, CreateStatus(1), [], cancellation.Token);
        IServiceClient client = new ServiceClient(pipeName, time);
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(1, Assert.IsType<ServiceWatchUpdate.Connected>(reader.Current).Status.Revision);
        await firstServer;
        Task secondServer = RunSubscriptionServerAsync(
            pipeName, CreateStatus(2), [], cancellation.Token);

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(2, Assert.IsType<ServiceWatchUpdate.Connected>(reader.Current).Status.Revision);
        Assert.Empty(time.Delays);
        await secondServer;
    }

    [Fact]
    public async Task WatchStatusAsync_UsesExponentialReconnectBackoffCappedAtFiveSeconds()
    {
        var time = new FakeTimeProvider();
        int attempts = 0;
        var client = new ServiceClient(
            _ =>
            {
                attempts++;
                throw new IOException("offline");
            },
            time);
        using CancellationTokenSource cancellation = new();
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(cancellation.Token).GetAsyncEnumerator();
        Task<bool> move = reader.MoveNextAsync().AsTask();
        TimeSpan[] expected =
        [
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
        ];

        for (int index = 0; index < expected.Length; index++)
        {
            Assert.True(await move);
            Assert.IsType<ServiceWatchUpdate.Reconnecting>(reader.Current);
            move = reader.MoveNextAsync().AsTask();
            await WaitUntilAsync(() => time.Delays.Count == index + 1);
            Assert.Equal(expected[index], time.Delays[index]);
            time.Advance(expected[index]);
        }

        Assert.True(await move);
        Assert.IsType<ServiceWatchUpdate.Reconnecting>(reader.Current);
        move = reader.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(expected.Length + 1, attempts);
    }

    [Fact]
    public async Task WatchStatusAsync_CapacityRejectionBacksOffAndRecovers()
    {
        string pipeName = NewPipeName();
        var time = new FakeTimeProvider();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        Task rejectedServer = RunServerAsync(
            pipeName,
            request => new ServiceResponse(
                OpenBladeIpc.ProtocolVersion,
                request.RequestId,
                Success: false,
                Error: "Busy",
                Status: null,
                Result: null)
            {
                ErrorCode = ServiceResponseErrorCode.CapacityUnavailable,
            },
            cancellation.Token);
        IServiceClient client = new ServiceClient(pipeName, time);
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(cancellation.Token).GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync());
        Assert.IsType<ServiceWatchUpdate.Reconnecting>(reader.Current);
        await rejectedServer;

        Task acceptedServer = RunSubscriptionServerAsync(
            pipeName,
            CreateStatus(7),
            [],
            cancellation.Token);
        Task<bool> reconnect = reader.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => time.Delays.Count == 1);
        Assert.Equal(TimeSpan.FromMilliseconds(250), time.Delays[0]);
        time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.True(await reconnect);
        Assert.Equal(
            7,
            Assert.IsType<ServiceWatchUpdate.Connected>(reader.Current).Status.Revision);
        await acceptedServer;
    }

    [Fact]
    public async Task WatchStatusAsync_ProtocolFailureTerminatesWithoutReconnect()
    {
        int attempts = 0;
        var stream = new ScriptedStream(Frame(Encoding.UTF8.GetBytes("not-json")));
        var client = new ServiceClient(
            _ =>
            {
                attempts++;
                return ValueTask.FromResult<Stream>(stream);
            },
            new FakeTimeProvider());
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(CancellationToken.None).GetAsyncEnumerator();

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.MoveNextAsync().AsTask());

        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WatchStatusAsync_VersionOrNonSuccessFailureTerminatesWithoutReconnect(
        bool wrongVersion)
    {
        string pipeName = NewPipeName();
        int attempts = 0;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = wrongVersion
            ? RunRawServerAsync(
                pipeName,
                request => Frame(Encoding.UTF8.GetBytes(
                    $"{{\"frame\":\"response\",\"version\":1,\"requestId\":\"{request.RequestId}\",\"success\":false,\"error\":\"bad version\",\"status\":null,\"result\":null}}")),
                timeout.Token)
            : RunServerAsync(
                pipeName,
                request => new ServiceResponse(
                    OpenBladeIpc.ProtocolVersion,
                    request.RequestId,
                    false,
                    "Rejected",
                    null,
                    null),
                timeout.Token);
        var client = new ServiceClient(
            async token =>
            {
                attempts++;
                var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(token);
                return pipe;
            },
            new FakeTimeProvider());
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(timeout.Token).GetAsyncEnumerator();

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.MoveNextAsync().AsTask());
        await server;

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WatchStatusAsync_OversizedFrameTerminatesWithoutReconnect()
    {
        int attempts = 0;
        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            prefix,
            OpenBladeIpc.MaximumMessageBytes + 1);
        var client = new ServiceClient(
            _ =>
            {
                attempts++;
                return ValueTask.FromResult<Stream>(new ScriptedStream(prefix));
            },
            new FakeTimeProvider());
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(CancellationToken.None).GetAsyncEnumerator();

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.MoveNextAsync().AsTask());

        Assert.Equal(1, attempts);
    }

    private static async Task AssertInvalidStatusResponseAsync(
        Func<ServiceRequest, ServiceServerFrame> response)
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunServerAsync(pipeName, response, timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetStatusAsync(timeout.Token));
        await server;
    }

    private static async Task AssertInvalidObserveResponseAsync(
        Func<ServiceRequest, ServiceServerFrame> response)
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunServerAsync(pipeName, response, timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.ObserveStateAsync(true, timeout.Token));
        await server;
    }

    private static async Task RunMutationServersAsync(
        string pipeName,
        List<ServiceRequest> observed,
        CancellationToken token)
    {
        for (int index = 0; index < 19; index++)
        {
            await using NamedPipeServerStream server = CreateServer(pipeName);
            await server.WaitForConnectionAsync(token);
            ServiceRequest request = await IpcMessageCodec.ReadRequestAsync(server, token);
            observed.Add(request);
            await IpcMessageCodec.WriteServerFrameAsync(
                server,
                new ServiceResponse(
                    OpenBladeIpc.ProtocolVersion,
                    request.RequestId,
                    true,
                    null,
                    null,
                    new ControlOperationResult(ControlOperationStatus.Stored, "Stored")),
                token);
        }
    }

    private static async Task RunReactivePulseBurstServerAsync(
        string pipeName,
        int expectedCount,
        List<ReactiveKeyPulse> observed,
        CancellationToken token)
    {
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await server.WaitForConnectionAsync(token);
        for (int index = 0; index < expectedCount; index++)
        {
            PulseReactiveKeyRequest request = Assert.IsType<PulseReactiveKeyRequest>(
                await IpcMessageCodec.ReadRequestAsync(server, token));
            observed.Add(request.Pulse);
            await IpcMessageCodec.WriteServerFrameAsync(
                server,
                new ServiceResponse(
                    OpenBladeIpc.ProtocolVersion,
                    request.RequestId,
                    true,
                    null,
                    null,
                    new ControlOperationResult(ControlOperationStatus.Applied, "Applied")),
                token);
        }
    }

    private static async Task RunAmbientFrameBurstServerAsync(
        string pipeName,
        int expectedCount,
        List<AmbientLightingFrame> observed,
        CancellationToken token)
    {
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await server.WaitForConnectionAsync(token);
        for (int index = 0; index < expectedCount; index++)
        {
            PushAmbientLightingFrameRequest request =
                Assert.IsType<PushAmbientLightingFrameRequest>(
                    await IpcMessageCodec.ReadRequestAsync(server, token));
            observed.Add(AmbientLightingFrame.FromRgbBytes(request.RgbBytes));
            await IpcMessageCodec.WriteServerFrameAsync(
                server,
                new ServiceResponse(
                    OpenBladeIpc.ProtocolVersion,
                    request.RequestId,
                    true,
                    null,
                    null,
                    new ControlOperationResult(ControlOperationStatus.Applied, "Applied")),
                token);
        }
    }

    private static async Task RunPersistentStreamingServerAsync(
        string pipeName,
        CancellationToken token)
    {
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await server.WaitForConnectionAsync(token);
        ServiceRequest request = await IpcMessageCodec.ReadRequestAsync(server, token);
        Assert.True(request is PulseReactiveKeyRequest or PushAmbientLightingFrameRequest);
        await IpcMessageCodec.WriteServerFrameAsync(
            server,
            new ServiceResponse(
                OpenBladeIpc.ProtocolVersion,
                request.RequestId,
                true,
                null,
                null,
                new ControlOperationResult(ControlOperationStatus.Applied, "Applied")),
            token);

        byte[] buffer = new byte[1];
        Assert.Equal(0, await server.ReadAsync(buffer, token));
    }

    private static async Task RunServerAsync(
        string pipeName,
        Func<ServiceRequest, ServiceServerFrame> response,
        CancellationToken token)
    {
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await server.WaitForConnectionAsync(token);
        ServiceRequest request = await IpcMessageCodec.ReadRequestAsync(server, token);
        await IpcMessageCodec.WriteServerFrameAsync(server, response(request), token);
    }

    private static ServiceResponse CapacityUnavailable(Guid requestId) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId,
        Success: false,
        Error: "Busy",
        Status: null,
        Result: null)
    {
        ErrorCode = ServiceResponseErrorCode.CapacityUnavailable,
    };

    private static ServiceResponse Applied(Guid requestId) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId,
        Success: true,
        Error: null,
        Status: null,
        Result: new ControlOperationResult(ControlOperationStatus.Applied, "Applied"));

    private static async Task RunRawServerAsync(
        string pipeName,
        Func<ServiceRequest, byte[]> response,
        CancellationToken token)
    {
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await server.WaitForConnectionAsync(token);
        ServiceRequest request = await IpcMessageCodec.ReadRequestAsync(server, token);
        await server.WriteAsync(response(request), token);
        await server.FlushAsync(token);
    }

    private static async Task RunSubscriptionServerAsync(
        string pipeName,
        ServiceStatusSnapshot initial,
        IReadOnlyList<ServiceStatusSnapshot> events,
        CancellationToken token)
    {
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await server.WaitForConnectionAsync(token);
        SubscribeStatusRequest request = Assert.IsType<SubscribeStatusRequest>(
            await IpcMessageCodec.ReadRequestAsync(server, token));
        await IpcMessageCodec.WriteServerFrameAsync(
            server,
            new ServiceResponse(
                OpenBladeIpc.ProtocolVersion,
                request.RequestId,
                true,
                null,
                initial,
                null),
            token);
        foreach (ServiceStatusSnapshot status in events)
        {
            await IpcMessageCodec.WriteServerFrameAsync(
                server,
                new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status),
                token);
        }
    }

    private static NamedPipeServerStream CreateServer(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    private static string NewPipeName() => $"OpenBlade.Tray.Tests.{Guid.NewGuid():N}";

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
            revision,
            false,
            true,
            ["RazerAppEngine"],
            null,
            [],
            "Blocked",
            DeviceCapabilities.Unavailable(),
            null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn,
            null,
            null,
            null,
            null,
            false,
            Firmware: null,
            ServiceVersion: "0.1.0-test");
    }

    private sealed class ScriptedStream(byte[] response) : Stream
    {
        private readonly MemoryStream input = new(response, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            input.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class BlockingHandshakeStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<FakeTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public List<TimeSpan> Delays { get; } = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(callback, state, now + dueTime, period);
            lock (sync)
            {
                timers.Add(timer);
                Delays.Add(dueTime);
            }
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            FakeTimer[] due;
            lock (sync)
            {
                now += amount;
                due = timers.Where(timer => timer.IsDue(now)).ToArray();
            }
            foreach (FakeTimer timer in due)
            {
                timer.Fire(now);
            }
        }
        private sealed class FakeTimer(TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period) : ITimer
        {
            private int disposed;
            private DateTimeOffset next = due;
            public bool IsDue(DateTimeOffset current) => Volatile.Read(ref disposed) == 0 && current >= next;
            public void Fire(DateTimeOffset current)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                callback(state);
                if (period == Timeout.InfiniteTimeSpan)
                {
                    Dispose();
                }
                else
                {
                    next = current + period;
                }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
