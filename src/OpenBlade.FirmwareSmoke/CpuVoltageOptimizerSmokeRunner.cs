using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.FirmwareSmoke;

internal sealed record CpuVoltageOptimizerSmokeResult(string Outcome, string Phase, bool ApplyAttempted,
    bool RestoreAttempted, bool OriginalVerified, string ExpectedServiceVersion, CpuLiveState? Baseline,
    CpuLiveState? Forward, CpuLiveState? Final, string Message);

internal sealed class CpuVoltageOptimizerSmokeRunner(IFirmwareSmokeClient client)
{
    internal const string Confirmation = "RZ09-0528-CPU-VOLTAGE-OPTIMIZER-SMOKE";
    internal const string BaselineFile = "cpu-voltage-optimizer-baseline.json";
    internal const string ResultFile = "cpu-voltage-optimizer.json";
    private static readonly int[] CoreIndices = [0, 2, 4, 6, 8, 9, 10, 11, 12, 13, 14, 15];

    internal static void ValidateAuthorization(string? confirmation, string? expectedServiceVersion)
    {
        string[] parts = expectedServiceVersion?.Split('+') ?? [];
        if (confirmation != Confirmation || string.IsNullOrWhiteSpace(expectedServiceVersion)
            || expectedServiceVersion.Any(char.IsWhiteSpace)
            || parts.Length != 2 || !Version.TryParse(parts[0], out Version? version) || version.Build < 0
            || parts[1].Length is not (8 or 40) || !parts[1].All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Exact attended CPU optimizer confirmation and --expected-service-version are required.");
        }
    }

    internal async Task<CpuVoltageOptimizerSmokeResult> RunAsync(string? confirmation, string? expectedServiceVersion,
        string outputDirectory, CancellationToken cancellationToken)
    {
        ValidateAuthorization(confirmation, expectedServiceVersion); // Before every client call.
        foreach (string name in new[] { BaselineFile, ResultFile })
        {
            string path = Path.Combine(outputDirectory, name);
            if (File.Exists(path) || Directory.Exists(path))
            { throw new IOException("A fresh output directory without prior CPU baseline or result evidence is required."); }
        }
        string version = expectedServiceVersion!;
        bool applyAttempted = false, restoreAttempted = false;
        CpuLiveState? baseline = null, forward = null, final = null;
        string phase = "Admission";
        try
        {
            ServiceStatusSnapshot status = await GreenStatusAsync(version, cancellationToken).ConfigureAwait(false);
            CpuTuningStatus observed = await ObserveAsync(cancellationToken).ConfigureAwait(false);
            RequireUnowned(observed);
            int original = RequireVector(observed.LiveState);
            if (original is not (-1 or 0)) { throw new InvalidDataException("Baseline must be uniformly -1 or zero."); }
            // Retain the full observed state, not a scalar or first-core fallback.
            baseline = observed.LiveState! with { CoreValues = observed.LiveState!.CoreValues.ToArray() };
            int target = original == -1 ? 0 : -1;
            var settings = new CpuTuningSettings(null, new(target != 0, -1));
            phase = "PersistBaseline";
            Directory.CreateDirectory(outputDirectory);
            await using (var stream = new FileStream(Path.Combine(outputDirectory, BaselineFile), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new
                {
                    SchemaVersion = 1,
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    ExpectedServiceVersion = version,
                    Status = status,
                    CpuTuning = observed with { LiveState = baseline },
                    CoreMask = 0xFF55,
                    RequestedSettings = settings,
                }, FirmwareSmokeFiles.JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            phase = "Apply";
            await GreenStatusAsync(version, cancellationToken).ConfigureAwait(false);
            CpuTuningStatus beforeApply = await ObserveAsync(cancellationToken).ConfigureAwait(false);
            RequireUnowned(beforeApply);
            RequireValue(beforeApply.LiveState, original);
            cancellationToken.ThrowIfCancellationRequested();
            applyAttempted = true; // A response failure may follow actual submission.
            CpuTuningStatus applied = RequireObservation(await client.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
                settings, cancellationToken).ConfigureAwait(false));
            RequireOwned(applied);
            RequireValue(applied.LiveState, target);
            phase = "ForwardReadback";
            await GreenStatusAsync(version, cancellationToken).ConfigureAwait(false);
            CpuTuningStatus independentlyObserved = await ObserveAsync(cancellationToken).ConfigureAwait(false);
            RequireOwned(independentlyObserved);
            RequireValue(independentlyObserved.LiveState, target);
            forward = independentlyObserved.LiveState;
            phase = "Restore";
            await GreenStatusAsync(version, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            restoreAttempted = true;
            CpuTuningStatus restored = RequireObservation(await client.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow,
                cancellationToken).ConfigureAwait(false));
            RequireUnowned(restored);
            RequireValue(restored.LiveState, original);
            phase = "OriginalReadback";
            await GreenStatusAsync(version, cancellationToken).ConfigureAwait(false);
            CpuTuningStatus originalObserved = await ObserveAsync(cancellationToken).ConfigureAwait(false);
            RequireUnowned(originalObserved);
            RequireValue(originalObserved.LiveState, original);
            final = originalObserved.LiveState;
            cancellationToken.ThrowIfCancellationRequested();
            return await PersistResultAsync(new("Passed", phase, true, true, true, version, baseline, forward, final,
                "One optimizer-only transition and exact original restoration were independently verified."), outputDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Never restore in finally, retry, or infer non-submission from a missing response.
            return await PersistResultAsync(new(applyAttempted || exception is OperationCanceledException ? "Unknown" : "Blocked", phase, applyAttempted,
                restoreAttempted, false, version, baseline, forward, final,
                $"Stopped without retry or guessed restoration: {exception.GetType().Name}: {exception.Message}"), outputDirectory, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<ServiceStatusSnapshot> GreenStatusAsync(string version, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ServiceStatusSnapshot status = await client.GetStatusAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!status.DeviceVerified || status.Firmware?.ModelNumber != RazerBladeModelCatalog.Rz090528ModelNumber
            || status.ServiceVersion != version || status.WritesBlocked || status.ControlAccess != SessionControlAccess.Active
            || status.ActivePowerSource != PowerSource.PluggedIn || status.ObservationReason != DeviceObservationReason.None
            || status.PotentialControllers.Count != 0 || !string.IsNullOrEmpty(status.ControllerDetectionWarning)
            || status.ExternalChanges.Count != 0 || status.UnconfirmedWrites.Count != 0
            || status.Capabilities?.CpuVoltageOptimizer is not { IsWellFormed: true, Access: DeviceFeatureAccess.ReadWrite, SupportsEnableDisable: true } curve
            || !curve.Value.Supports(-1))
        { throw new InvalidDataException("Exact model, version, full-power active control and optimizer safety/admission are required."); }
        return status;
    }

    private async Task<CpuTuningStatus> ObserveAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ControlOperationResult result = await client.ObserveCpuTuningAsync(true, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return RequireObservation(result);
    }

    private static CpuTuningStatus RequireObservation(ControlOperationResult result)
    {
        if (result.Status != ControlOperationStatus.Stored || result.CpuTuning is not { IsWellFormed: true, Live.CanWrite: true } status)
        { throw new InvalidDataException("An acknowledged typed CPU result and writable live backend are required."); }
        return status;
    }
    private static void RequireUnowned(CpuTuningStatus status)
    {
        if (status.RestorePending || status.FirmwareRecovery is not { State: CpuTuningRecoveryState.None, Features: CpuTuningFeatures.None }
            || status.LiveRecovery is not { State: CpuTuningRecoveryState.None, Features: CpuTuningFeatures.None })
        { throw new InvalidDataException("Both CPU recovery families must be explicitly resolved with no existing ownership."); }
    }
    private static void RequireOwned(CpuTuningStatus status)
    {
        if (!status.RestorePending || status.FirmwareRecovery is not { State: CpuTuningRecoveryState.None, Features: CpuTuningFeatures.None }
            || status.LiveRecovery is not { State: CpuTuningRecoveryState.RestoreReady, Features: CpuTuningFeatures.CurveOptimizer })
        { throw new InvalidDataException("Exact curve-only live restoration ownership is required."); }
    }
    private static int RequireVector(CpuLiveState? state)
    {
        if (state is not { IsWellFormed: true } || state.CoreValues.Count != 12
            || !state.CoreValues.Select(core => core.CoreIndex).Order().SequenceEqual(CoreIndices)
            || state.UniformCurveOptimizerValue is not { } value)
        { throw new InvalidDataException("Complete uniform twelve-core optimizer readback for mask FF55 is required."); }
        return value;
    }
    private static void RequireValue(CpuLiveState? state, int value)
    {
        if (RequireVector(state) != value) { throw new InvalidDataException("Independent optimizer readback differs from the expected full vector."); }
    }
    internal static async Task<CpuVoltageOptimizerSmokeResult> PersistResultAsync(CpuVoltageOptimizerSmokeResult result,
        string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, ResultFile);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, result, FirmwareSmokeFiles.JsonOptions, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        return result;
    }
}
