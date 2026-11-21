using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal static class Rz090528LampArrayGeometry
{
    internal const ushort ProductId = 0x02C6;
    internal const int LampCount = 85;
    internal const string Kind = "Keyboard";
    internal const string Sha256 =
        "C82E71D69DCAADDCB8530C0744B7B3240CC2E9C8FEF6CDAF1AB16B76F7A6A450";
}

internal sealed record LampArrayObservation(
    int EnumeratedDeviceCount,
    int ExactDeviceCount,
    string? Kind,
    int? LampCount,
    bool? IsConnected,
    bool? IsEnabled,
    bool? IsAvailable,
    bool? SupportsVirtualKeys,
    long? MinimumUpdateIntervalMicroseconds,
    string? GeometrySha256)
{
    internal bool MatchesApprovedGeometry =>
        ExactDeviceCount == 1
        && string.Equals(Kind, Rz090528LampArrayGeometry.Kind, StringComparison.Ordinal)
        && LampCount == Rz090528LampArrayGeometry.LampCount
        && SupportsVirtualKeys == true
        && string.Equals(
            GeometrySha256,
            Rz090528LampArrayGeometry.Sha256,
            StringComparison.Ordinal);

    internal string ToSanitizedSummary(string modelNumber) => ExactDeviceCount switch
    {
        0 => $"The exact {modelNumber} LampArray is not currently present.",
        > 1 => $"More than one exact {modelNumber} LampArray is present; the advisory stays unknown.",
        _ =>
            $"The exact {modelNumber} LampArray was observed (lamps={LampCount}, " +
            $"connected={IsConnected}, enabled={IsEnabled}, available={IsAvailable}); " +
            "this path is read-only.",
    };
}

/// <summary>
/// The current user's Windows setting "Use Dynamic Lighting on my devices".
/// NotConfigured is the Windows default, which leaves Dynamic Lighting on.
/// </summary>
internal enum DynamicLightingUserSetting
{
    NotConfigured,
    Enabled,
    Disabled,
}

internal interface IDynamicLightingUserSettingSource
{
    /// <summary>
    /// Reads the setting without changing it. A failed read throws, so the
    /// caller sends no advisory instead of guessing.
    /// </summary>
    DynamicLightingUserSetting Read();
}

internal sealed class WindowsDynamicLightingUserSettingSource : IDynamicLightingUserSettingSource
{
    private const string KeyPath = @"Software\Microsoft\Lighting";
    private const string ValueName = "AmbientLightingEnabled";

    public DynamicLightingUserSetting Read()
    {
        using Microsoft.Win32.RegistryKey? key =
            Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        object? value = key?.GetValue(ValueName);
        return value switch
        {
            null => DynamicLightingUserSetting.NotConfigured,
            int enabled => enabled == 0
                ? DynamicLightingUserSetting.Disabled
                : DynamicLightingUserSetting.Enabled,
            _ => throw new InvalidDataException(
                "The Windows Dynamic Lighting setting has an unexpected type."),
        };
    }
}

internal static class WindowsDynamicLightingAdvisoryClassifier
{
    /// <summary>
    /// Classifies one clean observation. Null means the observation cannot
    /// support either advisory, so no report is sent.
    /// </summary>
    /// <remarks>
    /// <see cref="LampArrayObservation.IsEnabled"/> is deliberately ignored:
    /// WinRT documents it as the calling app's own update gate, which is true
    /// by default, not as a Windows ownership signal. Whether Windows actually
    /// drives the keyboard is not observable here, so the result only says
    /// that the global switch is on and the exact array is present.
    /// </remarks>
    internal static WindowsDynamicLightingAdvisory? Classify(
        LampArrayObservation observation,
        DynamicLightingUserSetting setting)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (setting == DynamicLightingUserSetting.Disabled)
        {
            return WindowsDynamicLightingAdvisory.Off;
        }

        return observation switch
        {
            // A completed enumeration without the exact array leaves nothing
            // Windows can drive.
            { ExactDeviceCount: 0 } => WindowsDynamicLightingAdvisory.Off,
            { ExactDeviceCount: 1, IsConnected: true } =>
                WindowsDynamicLightingAdvisory.MayControl,
            _ => null,
        };
    }
}

internal interface ILampArrayObservationSource
{
    /// <summary>
    /// Performs one bounded, read-only observation of the LampArrays whose
    /// hardware identity is exactly Razer and the given product ID.
    /// Implementations must never request LampArray priority or call a
    /// LampArray mutation API.
    /// </summary>
    Task<LampArrayObservation> ObserveAsync(
        ushort productId,
        CancellationToken cancellationToken);
}

internal sealed record LampArraySessionEligibility(
    WindowsDynamicLightingLampArrayProfile Profile)
{
    internal static LampArraySessionEligibility? FromStatus(
        ServiceStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        // Only the active session's agent observes: the service accepts
        // advisory reports from that session alone, and an inactive agent
        // must not keep retrying a refused report.
        if (!status.DeviceVerified
            || status.ControlAccess != SessionControlAccess.Active
            || WindowsDynamicLightingLampArrays.Find(status.Firmware?.ModelNumber)
                is not { } profile)
        {
            return null;
        }

        return new(profile);
    }
}

/// <summary>
/// Activates read-only Windows LampArray observation only while this session
/// controls a verified laptop from the reviewed LampArray list. The loop
/// reports the advisory to the service only when it differs from the one the
/// service publishes, which also resends it after a service restart or user
/// change. Nothing it reports gates OpenBlade lighting, and the loop never
/// calls a LampArray mutation API.
/// </summary>
internal sealed class ServiceLampArrayObservationLoop
{
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(5);

    private readonly object sync = new();
    private readonly IServiceStatusWatcher statusWatcher;
    private readonly ILampArrayObservationSource observationSource;
    private readonly Action<LampArraySessionEligibility, LampArrayObservation>? observationHandler;
    private readonly Action<Exception>? errorHandler;
    private readonly IDynamicLightingUserSettingSource? settingSource;
    private readonly Func<ReportWindowsDynamicLightingAdvisoryRequest, CancellationToken,
        Task<ControlOperationResult>>? advisoryReporter;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Channel<byte> eligibilityChanges = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    private LampArraySessionEligibility? eligibility;
    private LampArrayObservation? lastObservation;
    private WindowsDynamicLightingAdvisory publishedAdvisory;
    private long eligibilityGeneration;

    internal ServiceLampArrayObservationLoop(
        IServiceStatusWatcher statusWatcher,
        ILampArrayObservationSource observationSource,
        Action<LampArraySessionEligibility, LampArrayObservation>? observationHandler = null,
        Action<Exception>? errorHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IDynamicLightingUserSettingSource? settingSource = null,
        Func<ReportWindowsDynamicLightingAdvisoryRequest, CancellationToken,
            Task<ControlOperationResult>>? advisoryReporter = null)
    {
        if ((settingSource is null) != (advisoryReporter is null))
        {
            throw new ArgumentException(
                "Advisory reporting requires both a setting source and a reporter.");
        }

        this.settingSource = settingSource;
        this.advisoryReporter = advisoryReporter;
        this.statusWatcher = statusWatcher
            ?? throw new ArgumentNullException(nameof(statusWatcher));
        this.observationSource = observationSource
            ?? throw new ArgumentNullException(nameof(observationSource));
        this.observationHandler = observationHandler;
        this.errorHandler = errorHandler;
        this.delay = delay ?? Task.Delay;
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task watch = WatchStatusAsync(linked.Token);
        Task observe = ObserveAsync(linked.Token);
        Task completed = await Task.WhenAny(watch, observe).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(watch, observe).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }

        if (completed.IsFaulted)
        {
            await completed.ConfigureAwait(false);
        }
    }

    private async Task WatchStatusAsync(CancellationToken cancellationToken)
    {
        await foreach (ServiceWatchUpdate update in statusWatcher
            .WatchStatusAsync(cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            ServiceStatusSnapshot? status =
                (update as ServiceWatchUpdate.Connected)?.Status;
            lock (sync)
            {
                publishedAdvisory = status?.WindowsDynamicLightingAdvisory
                    ?? WindowsDynamicLightingAdvisory.Unknown;
            }
            SetEligibility(status is null
                ? null
                : LampArraySessionEligibility.FromStatus(status));
        }

        throw new IOException(
            "The LampArray observation status subscription ended unexpectedly.");
    }

    private async Task ObserveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            DrainEligibilityChanges();
            (LampArraySessionEligibility? current, long generation) = ReadEligibility();
            if (current is null)
            {
                _ = await eligibilityChanges.Reader
                    .ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            TimeSpan nextDelay = ObservationInterval;
            try
            {
                LampArrayObservation observation = await observationSource
                    .ObserveAsync(current.Profile.ProductId, cancellationToken)
                    .ConfigureAwait(false);
                if (ReadEligibility().Generation == generation)
                {
                    ReportObservation(current, observation);
                    await ReportAdvisoryAsync(
                            current,
                            generation,
                            observation,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                nextDelay = FailureRetryInterval;
                ReportFailure(exception);
            }

            await WaitForDelayOrEligibilityChangeAsync(
                    nextDelay,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void SetEligibility(LampArraySessionEligibility? value)
    {
        lock (sync)
        {
            if (eligibility == value)
            {
                return;
            }

            eligibility = value;
            eligibilityGeneration++;
            lastObservation = null;
        }
        eligibilityChanges.Writer.TryWrite(0);
    }

    private (LampArraySessionEligibility? Eligibility, long Generation)
        ReadEligibility()
    {
        lock (sync)
        {
            return (eligibility, eligibilityGeneration);
        }
    }

    private async Task ReportAdvisoryAsync(
        LampArraySessionEligibility current,
        long generation,
        LampArrayObservation observation,
        CancellationToken cancellationToken)
    {
        if (settingSource is null || advisoryReporter is null)
        {
            return;
        }

        WindowsDynamicLightingAdvisory? advisory =
            WindowsDynamicLightingAdvisoryClassifier.Classify(
                observation,
                settingSource.Read());
        if (advisory is not { } observed)
        {
            return;
        }

        lock (sync)
        {
            // Report only a change. A restarted service or a user change
            // publishes Unknown, which differs, so the advisory is resent.
            if (eligibilityGeneration != generation || publishedAdvisory == observed)
            {
                return;
            }
        }

        ControlOperationResult result = await advisoryReporter(
                new ReportWindowsDynamicLightingAdvisoryRequest(
                    OpenBladeIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    current.Profile.ModelNumber,
                    observed),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Status is not ControlOperationStatus.Stored)
        {
            // A refusal is traced, not retried quickly: the next observation
            // interval tries again only if the advisory still differs.
            ReportFailure(new InvalidOperationException(
                $"The service did not record the Windows Dynamic Lighting advisory: {result.Message}"));
        }
    }

    private void ReportObservation(
        LampArraySessionEligibility current,
        LampArrayObservation observation)
    {
        bool changed;
        lock (sync)
        {
            changed = lastObservation != observation;
            lastObservation = observation;
        }
        if (!changed)
        {
            return;
        }

        try
        {
            observationHandler?.Invoke(current, observation);
        }
        catch
        {
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

    private async Task WaitForDelayOrEligibilityChangeAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task intervalTask = delay(interval, waitCancellation.Token);
        Task<bool> changedTask = eligibilityChanges.Reader
            .WaitToReadAsync(waitCancellation.Token)
            .AsTask();
        _ = await Task.WhenAny(intervalTask, changedTask).ConfigureAwait(false);
        await waitCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(intervalTask, changedTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
        {
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void DrainEligibilityChanges()
    {
        while (eligibilityChanges.Reader.TryRead(out _))
        {
        }
    }
}
