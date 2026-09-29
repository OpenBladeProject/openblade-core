using System.Security.Cryptography;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record StockLoadWorkloadObservation(bool RunningExact, uint? GpuUtilization, string? Failure);
internal interface IStockLoadWorkload
{
    string? Arm(DateTimeOffset armedAtUtc);
    StockLoadWorkloadObservation Observe();
}
internal sealed record StockLoadReadings(int? CpuCelsius, int? GpuCelsius, int? Fan1Rpm, int? Fan2Rpm, string? Failure)
{
    public bool PairedPerformanceAutomaticConfirmed { get; init; }
    public bool Full280WConfirmed { get; init; }
    public bool PadAbsentConfirmed { get; init; }
    public bool IndependentGpuZeroConfirmed { get; init; }
    public StockGpuTelemetry? GpuTelemetry { get; init; }
}
internal sealed record StockLoadSample(DateTimeOffset ObservedAtUtc, double ElapsedMilliseconds,
    uint GpuUtilizationPercent, StockLoadReadings Readings);
internal sealed record StockLoadArmingSample(DateTimeOffset ObservedAtUtc, double ElapsedMilliseconds,
    StockLoadWorkloadObservation Workload);
internal sealed record StockLoadBaselineResult(bool Completed, string? Failure, DateTimeOffset ArmedAtUtc,
    double ArmingElapsedMilliseconds, double CollectionElapsedMilliseconds,
    IReadOnlyList<StockLoadSample> Samples, IReadOnlyList<StockLoadArmingSample> ArmingSamples,
    StockLoadReadings? Preflight, DateTimeOffset? PreflightObservedAtUtc, double FinalVerificationElapsedMilliseconds);

// Stock Performance observations only. No firmware or GPU setter is reachable from this coordinator.
internal sealed class Rz090528StockLoadBaseline(IStockLoadWorkload workload,
    Func<CancellationToken, Task<StockLoadReadings>> read, Func<bool> eligible,
    TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    internal const string PlanSha256 = "21B0A3D28B9F4ECB885E1AC3FD49A1579C1A44F0564C895848F67FFCD7F9A5D1";
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;

    internal static byte[] ReadPlan()
    {
        using Stream resource = typeof(Rz090528PadlessHyperBoostValidation).Assembly.GetManifestResourceStream(
            "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_StockLoadBaselinePlan.json")
            ?? throw new InvalidOperationException("Stock load plan missing.");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        byte[] value = bytes.ToArray();
        if (Convert.ToHexString(SHA256.HashData(value)) != PlanSha256)
        {
            throw new InvalidOperationException("Stock load plan hash mismatch.");
        }

        return value;
    }

    internal async Task<StockLoadBaselineResult> RunAsync(Action<DateTimeOffset> armed, CancellationToken token)
    {
        var samples = new List<StockLoadSample>();
        var armingSamples = new List<StockLoadArmingSample>();
        DateTimeOffset armedAt = time.GetUtcNow();
        long start = time.GetTimestamp();
        long? collectionStart = null;
        long? finalVerificationStart = null;
        double? observationElapsed = null;
        StockLoadReadings? preflight = null;
        DateTimeOffset? preflightAt = null;
        double armingElapsed = 0;
        double Elapsed(long stamp) => time.GetElapsedTime(stamp).TotalMilliseconds;
        StockLoadBaselineResult Finish(string? failure) => new(failure is null, failure, armedAt,
            collectionStart is null ? Elapsed(start) : armingElapsed,
            observationElapsed ?? (collectionStart is { } stamp ? Elapsed(stamp) : 0), samples.ToArray(), armingSamples.ToArray(),
            preflight, preflightAt, finalVerificationStart is { } finalStamp ? Elapsed(finalStamp) : 0);
        static bool GuardsConfirmed(StockLoadReadings value) => value.PairedPerformanceAutomaticConfirmed
            && value.Full280WConfirmed && value.PadAbsentConfirmed && value.IndependentGpuZeroConfirmed;
        async Task WaitUntil(long stamp, double elapsed)
        {
            double remaining;
            while ((remaining = elapsed - Elapsed(stamp)) > 0)
            {
                await wait(TimeSpan.FromMilliseconds(Math.Max(1, remaining)), token).ConfigureAwait(false);
            }
        }
        try
        {
            _ = ReadPlan();
            token.ThrowIfCancellationRequested();
            string? armFailure = workload.Arm(armedAt);
            if (armFailure is not null)
            {
                return Finish(armFailure);
            }

            if (!eligible())
            {
                return Finish("ExactHostIsolationOrGpuZeroNotConfirmed");
            }

            using (var preflightTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                preflightTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                preflight = await read(preflightTimeout.Token).ConfigureAwait(false);
                preflightAt = time.GetUtcNow();
                if (preflightTimeout.IsCancellationRequested)
                {
                    return Finish(token.IsCancellationRequested ? "Canceled" : "PreflightTimedOut");
                }
                if (preflight.Failure is not null)
                {
                    return Finish(preflight.Failure);
                }

                if (!GuardsConfirmed(preflight))
                {
                    return Finish("PreflightGuardsNotConfirmed");
                }
            }
            // Refuse a process that appeared during preflight; the operator starts only after ARMED.
            armedAt = time.GetUtcNow();
            start = time.GetTimestamp();
            armFailure = workload.Arm(armedAt);
            if (armFailure is not null)
            {
                return Finish(armFailure);
            }

            token.ThrowIfCancellationRequested();
            armed(armedAt);
            int qualifying = 0;
            double previous = 0;
            for (int index = 0; ; index++)
            {
                await WaitUntil(start, index == 0 ? 0 : previous + 2000).ConfigureAwait(false);
                if (Elapsed(start) >= 180000)
                {
                    return Finish("ArmingTimedOut");
                }

                if (index > 0 && Elapsed(start) - previous > 4000)
                {
                    return Finish("ArmingSampleStale");
                }

                if (!eligible())
                {
                    return Finish("ExactHostIsolationOrGpuZeroNotConfirmed");
                }

                var observed = workload.Observe();
                armingSamples.Add(new(time.GetUtcNow(), Elapsed(start), observed));
                if (observed.Failure is not null)
                {
                    return Finish(observed.Failure);
                }

                if (Elapsed(start) >= 180000 || index > 0 && Elapsed(start) - previous > 4000)
                {
                    return Finish("ArmingSampleStale");
                }

                qualifying = observed.RunningExact && observed.GpuUtilization is >= 80 and <= 100 ? qualifying + 1 : 0;
                previous = Elapsed(start);
                if (qualifying == 3)
                {
                    break;
                }
            }
            armingElapsed = Elapsed(start);
            collectionStart = time.GetTimestamp();
            for (int index = 0; index < 15; index++)
            {
                await WaitUntil(collectionStart.Value, index * 2000).ConfigureAwait(false);
                if (Elapsed(collectionStart.Value) >= 30000)
                {
                    return Finish("CollectionDeadlineMissed");
                }

                if (!eligible())
                {
                    return Finish("ExactHostIsolationOrGpuZeroNotConfirmed");
                }

                var observed = workload.Observe();
                if (observed.Failure is not null)
                {
                    return Finish(observed.Failure);
                }

                if (!observed.RunningExact || observed.GpuUtilization is not (>= 80 and <= 100))
                {
                    return Finish("QualifiedLoadLost");
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                // The native query actor owns bounded I/O. Await its completion rather than
                // abandoning actor creation or disposal when this sample's token expires.
                StockLoadReadings value = await read(timeout.Token).ConfigureAwait(false);
                if (timeout.IsCancellationRequested)
                {
                    return Finish(token.IsCancellationRequested ? "Canceled" : "ObservationTimedOut");
                }

                samples.Add(new(time.GetUtcNow(), Elapsed(collectionStart.Value), observed.GpuUtilization.Value, value));
                if (value.Failure is not null)
                {
                    return Finish(value.Failure);
                }

                if (value is not { CpuCelsius: > 0, GpuCelsius: > 0, Fan1Rpm: > 0, Fan2Rpm: > 0 })
                {
                    return Finish("PositiveActiveTemperaturesAndFansRequired");
                }
                if (value.GpuTelemetry is not { ExactDeviceConfirmed: true, Failure: null, GpuCelsius: > 0, GpuUtilizationPercent: >= 80 and <= 100 }
                    || value.GpuTelemetry.GpuCelsius != value.GpuCelsius)
                {
                    return Finish("SameDeviceGpuLoadAndTemperatureNotConfirmed");
                }
                if (!GuardsConfirmed(value))
                {
                    return Finish("RequiredGuardConfirmationMissing");
                }

                if (Elapsed(collectionStart.Value) >= 30000 || samples.Count > 1
                    && samples[^1].ElapsedMilliseconds - samples[^2].ElapsedMilliseconds > 4000)
                {
                    return Finish("CollectionSampleStale");
                }
            }
            await WaitUntil(collectionStart.Value, 30000).ConfigureAwait(false);
            observationElapsed = Elapsed(collectionStart.Value);
            if (observationElapsed > 30250 || observationElapsed - samples[^1].ElapsedMilliseconds > 4000)
            {
                return Finish("CollectionDeadlineMissed");
            }
            finalVerificationStart = time.GetTimestamp();
            var final = workload.Observe();
            if (final.Failure is not null)
            {
                return Finish(final.Failure);
            }

            if (!eligible() || !final.RunningExact || final.GpuUtilization is not (>= 80 and <= 100))
            {
                return Finish("FinalEligibilityOrLoadLost");
            }

            token.ThrowIfCancellationRequested();
            if (Elapsed(finalVerificationStart.Value) > 2000)
            {
                return Finish("FinalVerificationDeadlineMissed");
            }

            return Finish(null);
        }
        catch (OperationCanceledException) { return Finish(token.IsCancellationRequested ? "Canceled" : "ObservationTimedOut"); }
        catch { return Finish("ObservationFailed"); }
    }
}
