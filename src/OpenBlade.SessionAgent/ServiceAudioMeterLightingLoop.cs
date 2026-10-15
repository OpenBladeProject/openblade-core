using System.Runtime.InteropServices;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface IAudioMeterLightingSampler : IDisposable
{
    AmbientLightingFrame Capture(double colorBoost);

    UniformLightingFrame CaptureUniform(double colorBoost);
}

public interface IAudioMeterLightingSamplerFactory
{
    IAudioMeterLightingSampler Create();
}

public sealed class WindowsAudioMeterLightingSamplerFactory
    : IAudioMeterLightingSamplerFactory
{
    public static WindowsAudioMeterLightingSamplerFactory Instance { get; } = new();

    private WindowsAudioMeterLightingSamplerFactory()
    {
    }

    public IAudioMeterLightingSampler Create() =>
        new WindowsAudioMeterLightingSampler();
}

public sealed class WindowsAudioMeterLightingSampler : IAudioMeterLightingSampler
{
    private readonly IAudioMeterNativeApi nativeApi;
    private readonly object enumeratorObject;
    private readonly object deviceObject;
    private readonly object meterObject;
    private int disposed;

    public WindowsAudioMeterLightingSampler()
        : this(WindowsAudioMeterNativeApi.Instance)
    {
    }

    internal WindowsAudioMeterLightingSampler(IAudioMeterNativeApi nativeApi)
    {
        this.nativeApi = nativeApi
            ?? throw new ArgumentNullException(nameof(nativeApi));
        object? acquiredEnumerator = null;
        object? acquiredDevice = null;
        object? acquiredMeter = null;
        try
        {
            acquiredEnumerator = nativeApi.CreateEnumerator();
            acquiredDevice = nativeApi.GetDefaultAudioEndpoint(acquiredEnumerator);
            acquiredMeter = nativeApi.ActivateMeter(acquiredDevice);
            nativeApi.ValidateMeter(acquiredMeter);

            enumeratorObject = acquiredEnumerator;
            deviceObject = acquiredDevice;
            meterObject = acquiredMeter;
            acquiredEnumerator = null;
            acquiredDevice = null;
            acquiredMeter = null;
        }
        finally
        {
            ReleaseIfAcquired(nativeApi, acquiredMeter);
            ReleaseIfAcquired(nativeApi, acquiredDevice);
            ReleaseIfAcquired(nativeApi, acquiredEnumerator);
        }
    }

    public AmbientLightingFrame Capture(double colorBoost)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ValidateColorBoost(colorBoost);

        float peak = nativeApi.GetPeakValue(meterObject);
        return RenderFrame(peak, colorBoost);
    }

    public UniformLightingFrame CaptureUniform(double colorBoost)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ValidateColorBoost(colorBoost);

        float peak = nativeApi.GetPeakValue(meterObject);
        return RenderUniformFrame(peak, colorBoost);
    }

    internal static AmbientLightingFrame RenderFrame(float peak, double colorBoost)
    {
        if (float.IsNaN(peak) || peak is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(peak));
        }
        if (!double.IsFinite(colorBoost)
            || colorBoost is < 1 or > 4
            || colorBoost * 4 != Math.Truncate(colorBoost * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(colorBoost));
        }
        double level = Math.Clamp(peak * colorBoost, 0, 1);
        int litColumns = (int)Math.Ceiling(
            level * AmbientLightingFrame.Columns);
        return RenderColumns(litColumns);
    }

    internal static UniformLightingFrame RenderUniformFrame(
        float peak,
        double colorBoost)
    {
        if (float.IsNaN(peak) || peak is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(peak));
        }
        ValidateColorBoost(colorBoost);
        byte intensity = checked((byte)Math.Round(
            Math.Clamp(peak * colorBoost, 0, 1) * byte.MaxValue));
        return new UniformLightingFrame(new RgbColor(0, intensity, 0));
    }

    private static void ValidateColorBoost(double colorBoost)
    {
        if (!double.IsFinite(colorBoost)
            || colorBoost is < 1 or > 4
            || colorBoost * 4 != Math.Truncate(colorBoost * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(colorBoost));
        }
    }

    internal static AmbientLightingFrame RenderColumns(int litColumns)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(litColumns);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            litColumns,
            AmbientLightingFrame.Columns);
        RgbColor[] slots = new RgbColor[AmbientLightingFrame.SlotCount];
        for (int row = 0; row < AmbientLightingFrame.Rows; row++)
        {
            for (int column = 0; column < AmbientLightingFrame.Columns; column++)
            {
                int slot = (row * AmbientLightingFrame.Columns) + column;
                if (AmbientLightingFrame.IsHole(slot) || column >= litColumns)
                {
                    continue;
                }

                double position = column /
                    (double)(AmbientLightingFrame.Columns - 1);
                slots[slot] = position switch
                {
                    < 0.5 => new(
                        checked((byte)Math.Round(position * 2 * 255)),
                        255,
                        0),
                    _ => new(
                        255,
                        checked((byte)Math.Round((1 - position) * 2 * 255)),
                        0),
                };
            }
        }
        return new(slots);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        nativeApi.Release(meterObject);
        nativeApi.Release(deviceObject);
        nativeApi.Release(enumeratorObject);
    }

    private static void ReleaseIfAcquired(
        IAudioMeterNativeApi nativeApi,
        object? value)
    {
        if (value is not null)
        {
            nativeApi.Release(value);
        }
    }
}

internal interface IAudioMeterNativeApi
{
    object CreateEnumerator();

    object GetDefaultAudioEndpoint(object enumeratorObject);

    object ActivateMeter(object deviceObject);

    void ValidateMeter(object meterObject);

    float GetPeakValue(object meterObject);

    void Release(object value);
}

internal sealed class WindowsAudioMeterNativeApi : IAudioMeterNativeApi
{
    private const uint ClsctxInprocServer = 0x1;

    internal static WindowsAudioMeterNativeApi Instance { get; } = new();

    private WindowsAudioMeterNativeApi()
    {
    }

    public object CreateEnumerator() => CoreAudioDeviceEnumerator.Create();

    public object GetDefaultAudioEndpoint(object enumeratorObject)
    {
        var enumerator = (IMMDeviceEnumerator)enumeratorObject;
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(
            dataFlow: 0,
            role: 1,
            out IMMDevice device));
        return device;
    }

    public object ActivateMeter(object deviceObject)
    {
        var device = (IMMDevice)deviceObject;
        Guid iid = typeof(IAudioMeterInformation).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(
            ref iid,
            ClsctxInprocServer,
            nint.Zero,
            out object activated));
        return activated;
    }

    public void ValidateMeter(object meterObject) =>
        _ = (IAudioMeterInformation)meterObject;

    public float GetPeakValue(object meterObject)
    {
        var meter = (IAudioMeterInformation)meterObject;
        Marshal.ThrowExceptionForHR(meter.GetPeakValue(out float peak));
        return peak;
    }

    public void Release(object value) => _ = Marshal.ReleaseComObject(value);

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid iid,
            uint classContext,
            nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);

        [PreserveSig]
        int GetMeteringChannelCount(out int channelCount);

        [PreserveSig]
        int GetChannelsPeakValues(int channelCount, nint peakValues);

        [PreserveSig]
        int QueryHardwareSupport(out uint hardwareSupportMask);
    }
}

internal sealed class AudioMeterDecaySmoother
{
    private double? displayedColumns;

    internal AmbientLightingFrame Apply(
        AmbientLightingFrame frame,
        TimeSpan elapsed,
        int decayMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.IsWellFormed())
        {
            throw new ArgumentException("The Audio Meter frame is invalid.", nameof(frame));
        }
        if (decayMilliseconds is
                < LightingSettingsFactory.MinAudioMeterDecayMilliseconds
                or > LightingSettingsFactory.MaxAudioMeterDecayMilliseconds
            || decayMilliseconds
                % LightingSettingsFactory.AudioMeterDecayStepMilliseconds != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decayMilliseconds));
        }

        int targetColumns = CountLitColumns(frame);
        if (decayMilliseconds == 0)
        {
            displayedColumns = targetColumns == 0 ? null : targetColumns;
            return frame;
        }
        if (displayedColumns is null)
        {
            displayedColumns = targetColumns == 0 ? null : targetColumns;
            return frame;
        }
        if (targetColumns >= displayedColumns.Value)
        {
            displayedColumns = targetColumns;
            return frame;
        }

        double elapsedMilliseconds = Math.Max(0, elapsed.TotalMilliseconds);
        double decayedColumns = displayedColumns.Value
            - (AmbientLightingFrame.Columns
                * elapsedMilliseconds
                / decayMilliseconds);
        displayedColumns = Math.Max(targetColumns, decayedColumns);
        if (displayedColumns.Value <= 0)
        {
            Reset();
            return frame;
        }

        int litColumns = (int)Math.Ceiling(displayedColumns.Value);
        return litColumns == targetColumns
            ? frame
            : WindowsAudioMeterLightingSampler.RenderColumns(litColumns);
    }

    internal void Reset() => displayedColumns = null;

    private static int CountLitColumns(AmbientLightingFrame frame)
    {
        int litColumns = 0;
        for (int column = 0; column < AmbientLightingFrame.Columns; column++)
        {
            bool lit = false;
            for (int row = 0; row < AmbientLightingFrame.Rows; row++)
            {
                int slot = (row * AmbientLightingFrame.Columns) + column;
                if (!AmbientLightingFrame.IsHole(slot) && frame.Slots[slot] != default)
                {
                    lit = true;
                    break;
                }
            }
            if (lit)
            {
                litColumns = column + 1;
            }
        }
        return litColumns;
    }
}

internal sealed class UniformAudioMeterDecaySmoother
{
    private double? displayedIntensity;

    internal UniformLightingFrame Apply(
        UniformLightingFrame frame,
        TimeSpan elapsed,
        int decayMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (decayMilliseconds is
                < LightingSettingsFactory.MinAudioMeterDecayMilliseconds
                or > LightingSettingsFactory.MaxAudioMeterDecayMilliseconds
            || decayMilliseconds
                % LightingSettingsFactory.AudioMeterDecayStepMilliseconds != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decayMilliseconds));
        }

        double target = frame.Color.Green;
        if (decayMilliseconds == 0 || displayedIntensity is null
            || target >= displayedIntensity.Value)
        {
            displayedIntensity = target == 0 ? null : target;
            return frame;
        }

        displayedIntensity = Math.Max(
            target,
            displayedIntensity.Value
                - (byte.MaxValue * Math.Max(0, elapsed.TotalMilliseconds)
                    / decayMilliseconds));
        if (displayedIntensity.Value <= 0)
        {
            Reset();
            return frame;
        }

        return new UniformLightingFrame(new RgbColor(
            0,
            checked((byte)Math.Ceiling(displayedIntensity.Value)),
            0));
    }

    internal void Reset() => displayedIntensity = null;
}

internal readonly record struct AudioMeterRenderingConfiguration(
    AudioMeterLightingParameters Parameters,
    LightingRefreshRate RefreshRate,
    bool UniformFrames = false);

public sealed class ServiceAudioMeterLightingLoop : IAsyncDisposable
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(1);
    private readonly IServiceClient serviceClient;
    private readonly IServiceStatusWatcher statusWatcher;
    private readonly IAudioMeterLightingSamplerFactory samplerFactory;
    private readonly Action<Exception>? errorHandler;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim rendererStateGate = new(1, 1);
    private CancellationTokenSource? rendererCancellation;
    private Task? rendererTask;
    private AudioMeterRenderingConfiguration? activeConfiguration;
    private int disposed;

    public ServiceAudioMeterLightingLoop(
        IServiceClient serviceClient,
        IAudioMeterLightingSamplerFactory samplerFactory,
        Action<Exception>? errorHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IServiceStatusWatcher? statusWatcher = null,
        TimeProvider? timeProvider = null)
    {
        this.serviceClient = serviceClient
            ?? throw new ArgumentNullException(nameof(serviceClient));
        this.samplerFactory = samplerFactory
            ?? throw new ArgumentNullException(nameof(samplerFactory));
        this.statusWatcher = statusWatcher ?? serviceClient;
        this.errorHandler = errorHandler;
        this.delay = delay ?? Task.Delay;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ServiceWatchUpdate update in statusWatcher
                .WatchStatusAsync(cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                AudioMeterRenderingConfiguration? configuration = update switch
                {
                    ServiceWatchUpdate.Connected connected
                        when connected.Status.LightingRendererOwnership ==
                            LightingRendererOwnershipState.AudioMeter
                        && connected.Status.DesiredState.Lighting.Value.Parameters is
                            AudioMeterLightingParameters audioMeter =>
                        new AudioMeterRenderingConfiguration(
                            audioMeter,
                            connected.Status.LightingRefreshRate,
                            connected.Status.Capabilities?.SupportsUniformLightingFrames
                                == true),
                    _ => null,
                };
                await SetConfigurationAsync(configuration, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await SetConfigurationAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            await SetConfigurationAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task SetConfigurationAsync(
        AudioMeterRenderingConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        await rendererStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (activeConfiguration == configuration
                && (configuration is null || rendererTask is { IsCompleted: false }))
            {
                return;
            }

            CancellationTokenSource? previousCancellation = rendererCancellation;
            Task? previousTask = rendererTask;
            rendererCancellation = null;
            rendererTask = null;
            activeConfiguration = null;
            previousCancellation?.Cancel();
            if (previousTask is not null)
            {
                try
                {
                    await previousTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            previousCancellation?.Dispose();
            if (configuration is null || Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            rendererCancellation = linked;
            rendererTask = RenderAsync(configuration.Value, linked.Token);
            activeConfiguration = configuration;
        }
        finally
        {
            rendererStateGate.Release();
        }
    }

    private async Task RenderAsync(
        AudioMeterRenderingConfiguration configuration,
        CancellationToken cancellationToken)
    {
        IAudioMeterLightingSampler? sampler = null;
        AudioMeterLightingParameters parameters = configuration.Parameters;
        var cadence = new SessionLightingFrameCadence(
            timeProvider,
            configuration.RefreshRate);
        var pacer = new SessionLightingFramePacer(
            timeProvider,
            delay,
            configuration.RefreshRate);
        var smoother = new AudioMeterDecaySmoother();
        var uniformSmoother = new UniformAudioMeterDecaySmoother();
        long? lastCaptureTimestamp = null;
        try
        {
            while (true)
            {
                AmbientLightingFrame? spatialFrame = null;
                UniformLightingFrame? uniformFrame = null;
                try
                {
                    sampler ??= samplerFactory.Create();
                    if (configuration.UniformFrames)
                    {
                        uniformFrame = sampler.CaptureUniform(parameters.ColorBoost);
                    }
                    else
                    {
                        spatialFrame = sampler.Capture(parameters.ColorBoost);
                    }
                    long capturedTimestamp = timeProvider.GetTimestamp();
                    TimeSpan elapsed = lastCaptureTimestamp is { } previous
                        ? timeProvider.GetElapsedTime(previous, capturedTimestamp)
                        : TimeSpan.Zero;
                    if (uniformFrame is not null)
                    {
                        uniformFrame = uniformSmoother.Apply(
                            uniformFrame,
                            elapsed,
                            parameters.DecayMilliseconds);
                    }
                    else
                    {
                        spatialFrame = smoother.Apply(
                            spatialFrame!,
                            elapsed,
                            parameters.DecayMilliseconds);
                    }
                    lastCaptureTimestamp = capturedTimestamp;
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ResetSampler(ref sampler);
                    smoother.Reset();
                    uniformSmoother.Reset();
                    lastCaptureTimestamp = null;
                    pacer.Reset();
                    ReportFailure(exception);
                    await delay(FailureRetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    SessionLightingFrameDecision decision = uniformFrame is not null
                        ? cadence.Evaluate(uniformFrame)
                        : cadence.Evaluate(spatialFrame!);
                    if (decision.ShouldSend)
                    {
                        ControlOperationResult result = uniformFrame is not null
                            ? await serviceClient.PushUniformLightingFrameAsync(
                                uniformFrame,
                                cancellationToken).ConfigureAwait(false)
                            : await serviceClient.PushAmbientLightingFrameAsync(
                                spatialFrame!,
                                cancellationToken).ConfigureAwait(false);
                        if (result.Status != ControlOperationStatus.Applied)
                        {
                            throw new InvalidOperationException(result.Message);
                        }

                        if (uniformFrame is not null)
                        {
                            cadence.RecordSent(uniformFrame);
                        }
                        else
                        {
                            cadence.RecordSent(spatialFrame!);
                        }
                    }

                    await pacer.WaitAsync(decision.NextInterval, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    pacer.Reset();
                    ReportFailure(exception);
                    await delay(FailureRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ResetSampler(ref sampler);
        }
    }

    private void ResetSampler(ref IAudioMeterLightingSampler? sampler)
    {
        IAudioMeterLightingSampler? previous = sampler;
        sampler = null;
        if (previous is null)
        {
            return;
        }

        try
        {
            previous.Dispose();
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private void ReportFailure(Exception exception)
    {
        try
        {
            errorHandler?.Invoke(exception);
        }
        catch
        {
        }
    }
}
