using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed class ServiceClient : IServiceClient, IAsyncDisposable
{
    private const int ConnectionTimeoutMilliseconds = 2000;
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(5);
    private readonly Func<CancellationToken, ValueTask<Stream>> connectAsync;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim reactivePulseGate = new(1, 1);
    private readonly SemaphoreSlim ambientFrameGate = new(1, 1);
    private Stream? reactivePulseConnection;
    private Stream? ambientFrameConnection;
    private int disposed;

    public ServiceClient()
        : this(OpenBladeIpc.PipeName)
    {
    }

    public ServiceClient(string pipeName, TimeProvider? timeProvider = null)
        : this(
            token => ConnectNamedPipeAsync(
                string.IsNullOrWhiteSpace(pipeName)
                    ? throw new ArgumentException("Pipe name is required.", nameof(pipeName))
                    : pipeName,
                token),
            timeProvider ?? TimeProvider.System)
    {
    }

    public ServiceClient(
        Func<CancellationToken, ValueTask<Stream>> connectAsync,
        TimeProvider timeProvider)
    {
        this.connectAsync = connectAsync ?? throw new ArgumentNullException(nameof(connectAsync));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token)
    {
        var request = new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());
        ServiceResponse response = await SendOnceAsync(request, token).ConfigureAwait(false);
        return response.Status
            ?? throw new InvalidDataException("Service status response is missing its status body.");
    }

    public async Task<ServiceStatusSnapshot> ObserveStateAsync(
        bool includeLiveReadings,
        CancellationToken cancellationToken) => await ObserveStateAsync(
                includeLiveReadings ? LiveReadingSensors.All : LiveReadingSensors.None,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<ServiceStatusSnapshot> ObserveStateAsync(
        LiveReadingSensors requestedLiveReadings,
        CancellationToken cancellationToken)
    {
        var request = new ObserveStateRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            requestedLiveReadings != LiveReadingSensors.None)
        {
            RequestedLiveReadings = requestedLiveReadings,
        };
        ServiceResponse response = await SendOnceAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return response.Status
            ?? throw new InvalidDataException(
                "Service observation response is missing its status body.");
    }

    public async Task<DiagnosticArchive> ExportDiagnosticsAsync(
        bool liveReadingsEnabled,
        CancellationToken token)
    {
        var request = new ExportDiagnosticsRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            liveReadingsEnabled);
        ServiceResponse response = await SendOnceAsync(request, token).ConfigureAwait(false);
        return response.Archive
            ?? throw new InvalidDataException(
                "Service diagnostic response is missing its archive body.");
    }

    public Task<ControlOperationResult> UpdateProfilesAsync(
        ProfileSet profiles,
        CancellationToken token) => SendMutationAsync(
            new UpdateProfilesRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), profiles),
            token);

    public Task<ControlOperationResult> SetPerformanceOverrideAsync(
        ManualOverride<PerformancePreset>? value,
        CancellationToken token) => SetPerformanceConfigurationOverrideAsync(
            value is null
                ? null
                : new PerformanceOverride(value.Value, value.Lifetime),
            token);

    public Task<ControlOperationResult> SetPerformanceConfigurationOverrideAsync(
        PerformanceOverride? value,
        CancellationToken token) => SendMutationAsync(
            new SetPerformanceOverrideRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                value),
            token);

    public Task<ControlOperationResult> CyclePerformanceShortcutAsync(
        PerformanceShortcutSource source,
        CancellationToken token) => SendMutationAsync(
            new CyclePerformanceShortcutRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                source),
            token);

    public Task<ControlOperationResult> SetPerformanceShortcutPreviewAsync(
        Guid sessionId,
        long generation,
        PerformancePreset? preset,
        CancellationToken token) => SendMutationAsync(
            new SetPerformanceShortcutPreviewRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                sessionId,
                generation,
                preset),
            token);

    public Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        CancellationToken token) => SetFanControlOverrideAsync(value, null, token);

    public Task<ControlOperationResult> SetFanControlOverrideAsync(
        ManualOverride<FanMode>? value,
        FanControlSettings? settings,
        CancellationToken token) => SendMutationAsync(
            new SetFanOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), value)
            {
                Settings = settings,
            },
            token);

    public Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken token) => SendMutationAsync(
            new SetLightingOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), value),
            token);

    public Task<ControlOperationResult> StepKeyboardBrightnessAsync(
        KeyboardBrightnessDirection direction,
        OverrideLifetime lifetime,
        CancellationToken token) => SendMutationAsync(
            new StepKeyboardBrightnessRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                direction,
                lifetime),
            token);

    public Task<ControlOperationResult> SetKeyboardLightingControlModeAsync(
        KeyboardLightingControlMode mode,
        CancellationToken token) => SendMutationAsync(
            new SetKeyboardLightingControlModeRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                mode),
            token);

    public Task<ControlOperationResult> SetExternalLightingOverridePolicyAsync(
        bool enabled,
        CancellationToken token) => SendMutationAsync(
            new SetExternalLightingOverridePolicyRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                enabled),
            token);

    public Task<ControlOperationResult> SetFirmwareCompatibilityOverrideAsync(
        bool enabled,
        CancellationToken token) => SendMutationAsync(
            new SetFirmwareCompatibilityOverrideRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                enabled),
            token);

    public Task<ControlOperationResult> ReapplyChangedSettingsAsync(
        CancellationToken token) => SendMutationAsync(
            new ReapplyChangedSettingsRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid()),
            token);

    public Task<ControlOperationResult> SetLightingSuppressionAsync(
        LightingSuppressionReason reason,
        bool suppressed,
        CancellationToken token) => SendMutationAsync(
            new SetLightingSuppressionRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                reason,
                suppressed),
            token);

    public async Task<ControlOperationResult> PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var request = new PulseReactiveKeyRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            pulse);
        await reactivePulseGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            for (int attempt = 0; ; attempt++)
            {
                Stream connection = reactivePulseConnection ??=
                    await connectAsync(token).ConfigureAwait(false);
                try
                {
                    ServiceResponse response = await SendOverConnectionAsync(
                            connection,
                            request,
                            token)
                        .ConfigureAwait(false);
                    return response.Result
                        ?? throw new InvalidDataException(
                            "Service mutation response is missing its result body.");
                }
                catch (Exception exception) when (
                    attempt == 0
                    && !token.IsCancellationRequested
                    && IsRetryableTransportFailure(exception))
                {
                    await ResetReactivePulseConnectionCoreAsync().ConfigureAwait(false);
                    await DelayAfterCapacityRejectionAsync(exception, token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // A canceled or malformed exchange can leave a partial
                    // request or a late response in this byte stream. It must
                    // never be reused by the next pulse, whose request ID would
                    // otherwise be matched against stale framing.
                    await ResetReactivePulseConnectionCoreAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            reactivePulseGate.Release();
        }
    }

    public async Task<ControlOperationResult> PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var request = new PushAmbientLightingFrameRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            frame.ToRgbBytes());
        await ambientFrameGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            for (int attempt = 0; ; attempt++)
            {
                Stream connection = ambientFrameConnection ??=
                    await connectAsync(token).ConfigureAwait(false);
                try
                {
                    ServiceResponse response = await SendOverConnectionAsync(
                            connection,
                            request,
                            token)
                        .ConfigureAwait(false);
                    return response.Result
                        ?? throw new InvalidDataException(
                            "Service mutation response is missing its result body.");
                }
                catch (Exception exception) when (
                    attempt == 0
                    && !token.IsCancellationRequested
                    && IsRetryableTransportFailure(exception))
                {
                    await ResetAmbientFrameConnectionCoreAsync().ConfigureAwait(false);
                    await DelayAfterCapacityRejectionAsync(exception, token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    await ResetAmbientFrameConnectionCoreAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            ambientFrameGate.Release();
        }
    }

    public Task<ControlOperationResult> SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken token) => SendMutationAsync(
            new SetPlaybackMuteIndicatorRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                muted),
            token);

    public Task<ControlOperationResult> SetBatteryPolicyAsync(
        BatteryPolicy value,
        CancellationToken token) => SendMutationAsync(
            new SetBatteryPolicyRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), value),
            token);

    public Task<ControlOperationResult> SetBatteryProtectionAsync(
        bool enabled,
        CancellationToken token) => SendMutationAsync(
            new SetBatteryProtectionRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                enabled),
            token);

    public Task<ControlOperationResult> SetFullChargeOverrideAsync(
        bool enabled,
        CancellationToken token) => SendMutationAsync(
            new SetFullChargeOverrideRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                enabled),
            token);

    public Task<ControlOperationResult> ApplyOneShotLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken token) => SendMutationAsync(
            new ApplyOneShotLaptopControlRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                value),
            token);

    public Task<ControlOperationResult> ShutdownOpenBladeAsync(CancellationToken token) =>
        SendMutationAsync(
            new ShutdownOpenBladeRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            token);

    public async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
        [EnumeratorCancellation] CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        TimeSpan reconnectDelay = InitialReconnectDelay;
        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            token.ThrowIfCancellationRequested();
            SubscriptionOpenResult openResult = await OpenSubscriptionAsync(token)
                .ConfigureAwait(false);
            if (openResult.RetryableFailure)
            {
                yield return new ServiceWatchUpdate.Reconnecting();
                await Task.Delay(reconnectDelay, timeProvider, token).ConfigureAwait(false);
                reconnectDelay = NextDelay(reconnectDelay);
                continue;
            }

            Stream connection = openResult.Connection!;
            await using (connection.ConfigureAwait(false))
            {
                reconnectDelay = InitialReconnectDelay;
                yield return new ServiceWatchUpdate.Connected(openResult.InitialStatus!);

                while (true)
                {
                    SubscriptionReadResult readResult = await ReadSubscriptionFrameAsync(
                            connection,
                            token)
                        .ConfigureAwait(false);
                    if (readResult.TransportFailed)
                    {
                        break;
                    }

                    if (readResult.Frame is not ServiceStatusEvent statusEvent)
                    {
                        throw new InvalidDataException(
                            "Service returned an unexpected subscription frame.");
                    }

                    if (statusEvent.Version != OpenBladeIpc.ProtocolVersion)
                    {
                        throw new InvalidDataException(
                            "Service status event version does not match the client.");
                    }

                    yield return new ServiceWatchUpdate.Connected(statusEvent.Status);
                }
            }

            // Healthy subscriptions are intentionally rotated and transient
            // disconnects are often immediately recoverable. Reopen first; only
            // the failed-open path above reports Reconnecting to consumers.
        }
    }

    private async Task<ControlOperationResult> SendMutationAsync(
        ServiceRequest request,
        CancellationToken token)
    {
        ServiceResponse response = await SendOnceAsync(request, token).ConfigureAwait(false);
        return response.Result
            ?? throw new InvalidDataException("Service mutation response is missing its result body.");
    }

    private async Task<ServiceResponse> SendOnceAsync(
        ServiceRequest request,
        CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await reactivePulseGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            // Only consecutive key-triggered pulses share a connection. Any
            // ordinary request ends the burst so request classes can never be
            // accidentally multiplexed onto the latency-sensitive channel.
            await ResetReactivePulseConnectionCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            reactivePulseGate.Release();
        }

        await ambientFrameGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            await ResetAmbientFrameConnectionCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            ambientFrameGate.Release();
        }

        Stream connection = await connectAsync(token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await SendOverConnectionAsync(connection, request, token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await reactivePulseGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await ResetReactivePulseConnectionCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            reactivePulseGate.Release();
        }

        await ambientFrameGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await ResetAmbientFrameConnectionCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            ambientFrameGate.Release();
        }
    }

    private static async Task<ServiceResponse> SendOverConnectionAsync(
        Stream connection,
        ServiceRequest request,
        CancellationToken token)
    {
        await IpcMessageCodec.WriteRequestAsync(connection, request, token).ConfigureAwait(false);
        ServiceServerFrame frame = await IpcMessageCodec.ReadServerFrameAsync(connection, token)
            .ConfigureAwait(false);
        return ValidateResponse(frame, request);
    }

    private async ValueTask ResetReactivePulseConnectionCoreAsync()
    {
        Stream? connection = reactivePulseConnection;
        reactivePulseConnection = null;
        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async ValueTask ResetAmbientFrameConnectionCoreAsync()
    {
        Stream? connection = ambientFrameConnection;
        ambientFrameConnection = null;
        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static ServiceResponse ValidateResponse(
        ServiceServerFrame frame,
        ServiceRequest request)
    {
        ServiceResponse response = ValidateResponseEnvelope(frame, request);
        if (!response.Success)
        {
            if (response.ErrorCode == ServiceResponseErrorCode.CapacityUnavailable)
            {
                throw new ServiceCapacityUnavailableException(
                    response.Error ?? "Service capacity is temporarily unavailable.");
            }
            throw new InvalidDataException(response.Error ?? "Service rejected the request.");
        }
        return response;
    }

    private static ServiceResponse ValidateResponseEnvelope(
        ServiceServerFrame frame,
        ServiceRequest request)
    {
        if (frame is not ServiceResponse response)
        {
            throw new InvalidDataException("Service returned an unexpected server frame.");
        }

        if (response.Version != OpenBladeIpc.ProtocolVersion)
        {
            throw new InvalidDataException("Service response version does not match the client.");
        }

        if (response.RequestId != request.RequestId)
        {
            throw new InvalidDataException("Service response request ID does not match the request.");
        }

        return response;
    }

    private static TimeSpan NextDelay(TimeSpan current) => TimeSpan.FromMilliseconds(
        Math.Min(current.TotalMilliseconds * 2, MaximumReconnectDelay.TotalMilliseconds));

    private async Task<SubscriptionOpenResult> OpenSubscriptionAsync(
        CancellationToken token)
    {
        Stream? connection = null;
        try
        {
            connection = await connectAsync(token).ConfigureAwait(false);
            var request = new SubscribeStatusRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid());
            await IpcMessageCodec.WriteRequestAsync(connection, request, token)
                .ConfigureAwait(false);
            ServiceServerFrame frame = await IpcMessageCodec.ReadServerFrameAsync(
                    connection,
                    token)
                .ConfigureAwait(false);
            ServiceResponse response = ValidateResponse(frame, request);
            ServiceStatusSnapshot initial = response.Status
                ?? throw new InvalidDataException(
                    "Service subscription response is missing its status body.");
            return new SubscriptionOpenResult(connection, initial, false);
        }
        catch (Exception exception) when (
            !token.IsCancellationRequested
            && IsRetryableTransportFailure(exception))
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            return new SubscriptionOpenResult(null, null, true);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private static async Task<SubscriptionReadResult> ReadSubscriptionFrameAsync(
        Stream connection,
        CancellationToken token)
    {
        try
        {
            return new SubscriptionReadResult(
                await IpcMessageCodec.ReadServerFrameAsync(connection, token).ConfigureAwait(false),
                false);
        }
        catch (Exception exception) when (
            !token.IsCancellationRequested
            && IsRetryableTransportFailure(exception))
        {
            return new SubscriptionReadResult(null, true);
        }
    }

    private static bool IsRetryableTransportFailure(Exception exception) =>
        exception is IOException or TimeoutException;

    private async Task DelayAfterCapacityRejectionAsync(
        Exception exception,
        CancellationToken token)
    {
        if (exception is ServiceCapacityUnavailableException)
        {
            // The peer can reject a reconnect before its deadline-free read
            // observes EOF from the disposed stream and releases the slot.
            await Task.Delay(InitialReconnectDelay, timeProvider, token)
                .ConfigureAwait(false);
        }
    }

    private sealed class ServiceCapacityUnavailableException(string message)
        : IOException(message);

    private sealed record SubscriptionOpenResult(
        Stream? Connection,
        ServiceStatusSnapshot? InitialStatus,
        bool RetryableFailure);

    private sealed record SubscriptionReadResult(
        ServiceServerFrame? Frame,
        bool TransportFailed);

    private static async ValueTask<Stream> ConnectNamedPipeAsync(
        string pipeName,
        CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ConnectionTimeoutMilliseconds, token).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
