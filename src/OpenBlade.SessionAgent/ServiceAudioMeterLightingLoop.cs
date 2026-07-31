using System.Runtime.InteropServices;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface IAudioMeterLightingSampler : IDisposable
{
    AmbientLightingFrame Capture(double colorBoost);
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
    private const uint ClsctxInprocServer = 0x1;
    private readonly object enumeratorObject;
    private readonly object deviceObject;
    private readonly object meterObject;
    private readonly IAudioMeterInformation meter;
    private int disposed;

    public WindowsAudioMeterLightingSampler()
    {
        Type enumeratorType = Type.GetTypeFromCLSID(
            new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"),
            throwOnError: true)!;
        enumeratorObject = Activator.CreateInstance(enumeratorType)
            ?? throw new InvalidOperationException("Core Audio enumerator is unavailable.");
        var enumerator = (IMMDeviceEnumerator)enumeratorObject;
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(
            dataFlow: 0,
            role: 1,
            out IMMDevice device));
        deviceObject = device;
        Guid iid = typeof(IAudioMeterInformation).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(
            ref iid,
            ClsctxInprocServer,
            nint.Zero,
            out object activated));
        meterObject = activated;
        meter = (IAudioMeterInformation)activated;
    }

    public AmbientLightingFrame Capture(double colorBoost)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!double.IsFinite(colorBoost)
            || colorBoost is < 1 or > 4
            || colorBoost * 4 != Math.Truncate(colorBoost * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(colorBoost));
        }

        Marshal.ThrowExceptionForHR(meter.GetPeakValue(out float peak));
        return RenderFrame(peak, colorBoost);
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
        _ = Marshal.FinalReleaseComObject(meterObject);
        _ = Marshal.FinalReleaseComObject(deviceObject);
        _ = Marshal.FinalReleaseComObject(enumeratorObject);
    }

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

public sealed class ServiceAudioMeterLightingLoop : IAsyncDisposable
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(1);
    private readonly IServiceClient serviceClient;
    private readonly IServiceStatusWatcher statusWatcher;
    private readonly IAudioMeterLightingSamplerFactory samplerFactory;
    private readonly Action<Exception>? errorHandler;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeProvider timeProvider;
    private CancellationTokenSource? rendererCancellation;
    private Task? rendererTask;
    private double? activeBoost;
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
                double? boost = update switch
                {
                    ServiceWatchUpdate.Connected connected
                        when connected.Status.LightingRendererOwnership ==
                            LightingRendererOwnershipState.AudioMeter
                        && connected.Status.DesiredState.Lighting.Value.Parameters is
                            AudioMeterLightingParameters parameters =>
                        parameters.ColorBoost,
                    _ => null,
                };
                await SetBoostAsync(boost, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await SetBoostAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            await SetBoostAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task SetBoostAsync(double? boost, CancellationToken cancellationToken)
    {
        if (activeBoost == boost
            && (boost is null || rendererTask is { IsCompleted: false }))
        {
            return;
        }

        CancellationTokenSource? previousCancellation = rendererCancellation;
        Task? previousTask = rendererTask;
        rendererCancellation = null;
        rendererTask = null;
        activeBoost = null;
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
        if (boost is null || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        rendererCancellation = linked;
        rendererTask = RenderAsync(boost.Value, linked.Token);
        activeBoost = boost;
    }

    private async Task RenderAsync(double boost, CancellationToken cancellationToken)
    {
        IAudioMeterLightingSampler? sampler = null;
        var cadence = new SessionLightingFrameCadence(timeProvider);
        try
        {
            while (true)
            {
                AmbientLightingFrame frame;
                try
                {
                    sampler ??= samplerFactory.Create();
                    frame = sampler.Capture(boost);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ResetSampler(ref sampler);
                    ReportFailure(exception);
                    await delay(FailureRetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    SessionLightingFrameDecision decision = cadence.Evaluate(frame);
                    if (decision.ShouldSend)
                    {
                        ControlOperationResult result = await serviceClient
                            .PushAmbientLightingFrameAsync(frame, cancellationToken)
                            .ConfigureAwait(false);
                        if (result.Status != ControlOperationStatus.Applied)
                        {
                            throw new InvalidOperationException(result.Message);
                        }

                        cadence.RecordSent(frame);
                    }

                    await delay(decision.NextInterval, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
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
