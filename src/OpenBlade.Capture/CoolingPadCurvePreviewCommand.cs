using System.Globalization;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture;

// Finite temperature acquisition and pure planning. No pad transport is composed.
internal sealed class CoolingPadCurvePreviewCommand(
    Func<HostIdentityReadResult> readHost,
    Func<IFanTemperatureSource> createSource,
    TextWriter output,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<Action, IDisposable>? registerCancellation = null,
    PendingDisposalRegistry? pendingOperations = null)
{
    internal const string Name = "preview-rz09-0528-cooling-pad-curve";
    internal const string ExactTargetConfirmation = "RZ09-05289EN4:ReadOnlyTemperature-CoolingPadCurvePlanning";
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan Cadence = TimeSpan.FromSeconds(2);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly PendingDisposalRegistry pending = pendingOperations ?? PendingDisposalRegistry.Shared;
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static CoolingPadDemandCurve CreateCpuPreset() => new([
        new CoolingPadCurvePoint(40, new CoolingPadConfiguredRpmDemand(1000)),
        new CoolingPadCurvePoint(90, new CoolingPadConfiguredRpmDemand(2200)),
        new CoolingPadCurvePoint(100, new CoolingPadConfiguredRpmDemand(3200))]);
    internal static CoolingPadDemandCurve CreateGpuPreset() => new([
        new CoolingPadCurvePoint(30, new CoolingPadConfiguredRpmDemand(1000)),
        new CoolingPadCurvePoint(80, new CoolingPadConfiguredRpmDemand(2200)),
        new CoolingPadCurvePoint(90, new CoolingPadConfiguredRpmDemand(3200))]);

    internal static HostIdentityReadResult ReadWindowsHost()
    {
        var registry = new WindowsRegistryValueSource();
        return new WindowsHostIdentitySource(registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            WindowsHostIdentitySource.Rz090528Identity).ReadExactIdentity();
    }

    internal async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count != 4 || arguments[0] != "--confirm-target" || arguments[1] != ExactTargetConfirmation
            || arguments[2] != "--samples" || !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            || count is < 1 or > 30)
        {
            await output.WriteLineAsync($"Usage: OpenBlade.Capture {Name} --confirm-target {ExactTargetConfirmation} --samples 1..30").ConfigureAwait(false);
            return 1;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = (registerCancellation ?? ConsoleCancellation.Register)(() => ConsoleCancellation.Cancel(cancellation));
        var sourceCancellation = new CancellationTokenSource();
        var cpuCurve = CreateCpuPreset();
        var gpuCurve = CreateGpuPreset();
        var samples = new List<Sample>();
        HostIdentityReadResult host = default;
        Task<IFanTemperatureSource>? opening = null;
        Task<FanTemperatureReadings>? reading = null;
        string phase = "hostIdentity";
        string? failure = null;
        bool sourceDisposed = false;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            Task<HostIdentityReadResult> identity = Task.Run(readHost, CancellationToken.None);
            pending.Retain(identity);
            host = await identity.WaitAsync(MaximumAge, clock, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (host.Failure is not null || host.Evidence is not
                { Manufacturer: "Razer", ProductName: "Blade 16 - RZ09-0528", Sku: "RZ09-05289EN4" })
            {
                failure = "HostIdentityBlocked";
            }
            else
            {
                phase = "sourceOpen";
                opening = Task.Run(() =>
                {
                    sourceCancellation.Token.ThrowIfCancellationRequested();
                    return createSource() ?? throw new InvalidOperationException();
                }, CancellationToken.None);
                IFanTemperatureSource source = await opening.WaitAsync(MaximumAge, clock, cancellation.Token).ConfigureAwait(false);
                if (source is not IDisposable) { throw new InvalidOperationException("Disposable temperature source required."); }
                for (int index = 1; index <= count; index++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    phase = "temperatureRead";
                    long started = clock.GetTimestamp();
                    reading = Task.Run(async () =>
                    {
                        sourceCancellation.Token.ThrowIfCancellationRequested();
                        return await source.ReadAsync(sourceCancellation.Token).ConfigureAwait(false);
                    }, CancellationToken.None);
                    FanTemperatureReadings temperatures = await reading.WaitAsync(MaximumAge, clock, cancellation.Token).ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    TimeSpan age = clock.GetElapsedTime(started);
                    CoolingPadCurveDecision plan = CoolingPadCurvePlanner.Plan(cpuCurve, gpuCurve, temperatures, age, MaximumAge);
                    samples.Add(new(index, temperatures.CpuCelsius, temperatures.GpuCelsius, temperatures.GpuInactive,
                        age.TotalMilliseconds, plan.Demand?.Rpm, plan.Reason.ToString()));
                    if (plan.Demand is null)
                    {
                        failure = plan.Reason.ToString();
                        break;
                    }
                    if (index < count)
                    {
                        phase = "cadence";
                        await (delay is null ? Task.Delay(Cadence, clock, cancellation.Token) : delay(Cadence, cancellation.Token)).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) { failure = "Canceled"; }
        catch (TimeoutException) { failure = "Timeout"; }
        catch { failure = "ObservationFailed"; }
        finally
        {
            // A timed-out read may still own NVML. Close only after it completes;
            // retain late cleanup without allowing any further preview reads.
            Task cleanup = Task.Run(async () =>
            {
                Task notification = sourceCancellation.CancelAsync();
                try
                {
                    if (reading is not null) { try { await reading.ConfigureAwait(false); } catch { } }
                    if (opening is not null)
                    {
                        IFanTemperatureSource source = await opening.ConfigureAwait(false);
                        if (source is IDisposable disposable) { disposable.Dispose(); }
                        else { throw new InvalidOperationException("Disposable temperature source required."); }
                    }
                }
                finally
                {
                    try { await notification.ConfigureAwait(false); }
                    finally { sourceCancellation.Dispose(); }
                }
            }, CancellationToken.None);
            try
            {
                await cleanup.WaitAsync(MaximumAge, clock, CancellationToken.None).ConfigureAwait(false);
                sourceDisposed = opening is not null;
            }
            catch
            {
                pending.Retain(cleanup);
                failure ??= "SourceCleanupIncomplete";
                if (failure == "SourceCleanupIncomplete") { phase = "sourceCleanup"; }
            }
        }

        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            command = Name,
            readOnly = true,
            padControlReportsSent = false,
            padPresenceChecked = false,
            productionAdmitted = false,
            measuredRpmConfirmed = false,
            actualTemperatureControlPerformed = false,
            safeHandbackPerformed = false,
            preset = "illustrative-OpenBlade-planning-policy",
            synapsePreset = false,
            savedUserIntent = false,
            recommendedForProduction = false,
            cpuCurve = cpuCurve.Points,
            gpuCurve = gpuCurve.Points,
            expectedHost = "RZ09-05289EN4",
            biosVersion = host.Evidence?.BiosVersion,
            biosIsCaptureProvenanceOnly = true,
            sourceOriginTimestampAvailable = false,
            sampleAgeMeaning = "host-acquisition-age-including-read-duration-only",
            maximumAgeMilliseconds = MaximumAge.TotalMilliseconds,
            intervalBetweenReadsMilliseconds = Cadence.TotalMilliseconds,
            requestedSamples = count,
            temperatureSourceConstructionStarted = opening is not null,
            temperatureSourceDisposed = sourceDisposed,
            samples,
            completed = failure is null && samples.Count == count,
            failurePhase = failure is null ? null : phase,
            failure,
        }, JsonOptions)).ConfigureAwait(false);
        return failure is null ? 0 : failure == "Canceled" ? 4 : 2;
    }

    private sealed record Sample(int Index, int? CpuCelsius, int? GpuCelsius, bool GpuInactive,
        double HostAcquisitionAgeMilliseconds, int? PlannedConfiguredDemandRpm, string Reason);
}
