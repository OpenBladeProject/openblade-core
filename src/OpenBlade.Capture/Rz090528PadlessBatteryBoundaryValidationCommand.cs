using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture;

internal static class Rz090528PadlessBatteryBoundaryValidationCommand
{
    internal const string Name = "validate-rz09-0528-02c6-hyperboost-padless-battery-boundary";
    internal const string Confirmation = "RZ09-0528:1532:02C6:BIOS-2.02:PERFORMANCE-AUTO:GPU-ZERO:BATTERY-ONLY:EXPERIMENTAL-75C";
    internal const string AttendedConfirmation = "ATTENDED:PAD-USB-AND-POWER-OFF:USB-C-DISCONNECTED:ORIGINAL-AC:NO-WORKLOAD";

    internal static bool ValidArguments(string[] args) => args.Length == 10 && args.Take(6).SequenceEqual(new[]
    {
        "--confirm-target", Confirmation, "--confirm-boundary", AttendedConfirmation,
        "--plan-sha256", Rz090528PadlessBatteryBoundaryValidation.PlanSha256,
    }) && args[6] == "--hwinfo-exe" && LocalPath(args[7])
        && Path.GetFileName(args[7]).Equals("HWiNFO64.exe", StringComparison.OrdinalIgnoreCase)
        && args[8] == "--hwinfo-log" && LocalPath(args[9])
        && Path.GetExtension(args[9]).Equals(".json", StringComparison.OrdinalIgnoreCase);

    private static bool LocalPath(string path) => Path.IsPathFullyQualified(path)
        && !Path.GetPathRoot(path)!.StartsWith("\\\\", StringComparison.Ordinal);

    internal static bool? CoherentWindowsPower(bool? before, bool? after) =>
        before.HasValue && before == after ? before : null;

    internal static async ValueTask<bool> VerifyFinalGpuZeroAsync<T>(OwnedReadOnlyLane<T> lane,
        Func<T> query, Func<T, bool> confirmed, bool hardwareDisposed, bool snapshotQuiesced,
        CancellationToken token) where T : class
    {
        if (!hardwareDisposed || !snapshotQuiesced || !lane.OperationQuiesced) { return false; }
        try { return confirmed(await lane.InvokeAsync(query, null, token)); }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    internal static string SerializeReport(object report) => JsonSerializer.Serialize(report, new JsonSerializerOptions
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    });

    internal static bool NoBenchmarkProcesses(IEnumerable<string> names) =>
        !names.Any(name => name.StartsWith("3DMark", StringComparison.OrdinalIgnoreCase));

    private static bool ObserveNoBenchmarkProcesses()
    {
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return false; }
        try { return NoBenchmarkProcesses(processes.Select(process => process.ProcessName)); }
        catch { return false; }
        finally { foreach (Process process in processes) { process.Dispose(); } }
    }

    internal static Rz090528BatteryBoundaryExternal ComposeReading(HwinfoCpuLogObservation cpu,
        StockGpuTelemetry gpu, bool? windowsBefore, bool? windowsAfter, bool noWorkload, bool gpuZero,
        DateTimeOffset observedAt)
    {
        double? effectiveCpu = cpu.Celsius is { } current
            ? Math.Max(current, cpu.NewSamplesMaximumCelsius ?? current) : null;
        double age = cpu.SourceTimestampUtc is { } source ? (observedAt - source).TotalMilliseconds : double.NaN;
        string? failure = cpu.Failure ?? gpu.Failure;
        if (cpu.SourceTimestampUtc is null || !double.IsFinite(age) || age < 0 || age > 4000)
        {
            failure ??= "CpuSourceNotFreshAfterExternalReads";
        }
        if (!gpu.ExactDeviceConfirmed || gpu.GpuCelsius is not > 0 || gpu.GpuUtilizationPercent is not <= 100)
        {
            failure ??= "ExactGpuTelemetryUnavailable";
        }
        return new(cpu.SourceTimestampUtc ?? default, age, effectiveCpu, gpu.GpuCelsius,
            gpu.GpuUtilizationPercent, CoherentWindowsPower(windowsBefore, windowsAfter), noWorkload, gpuZero, failure);
    }

    internal static async Task<int> RunWindowsAsync(string[] args, TextWriter output, TextWriter progress, CancellationToken token)
    {
        if (!ValidArguments(args))
        {
            await output.WriteLineAsync("Exact target, attended battery-only boundary, reviewed plan and explicit HWiNFO paths required.");
            return 2;
        }
        bool runnerEntered = false, finalGpuZero = false, externalTelemetryQuiesced = true;
        Rz090528PadlessHyperBoostValidation? runner = null;
        PerformanceFanSetterValidationResult? result = null;
        VerifiedHwinfoProcess? provider = null;
        HwinfoTimestampedCpuLog? cpu = null;
        OwnedReadOnlyLane<BatteryExternalSnapshot>? lane = null;
        object? providerProvenance = null;
        var observations = new List<BatteryExternalSnapshot>();
        var gpuOffsets = new GpuOffsetEvidenceLog();
        object[] frozenGpuOffsets = [];
        string? outerFailure = null;
        try
        {
            provider = await VerifiedHwinfoProcess.OpenAsync(args[7], token);
            providerProvenance = new
            {
                provider.Identity.ProcessId,
                provider.Identity.StartedAtUtc,
                ExecutableName = "HWiNFO64.exe",
                provider.Identity.Sha256,
                provider.Identity.Version,
                provider.Identity.SignatureVerified,
                Signer = "REALiX"
            };
            cpu = new HwinfoTimestampedCpuLog(args[9], provider.Identity, provider.Observe);
            lane = new(() => { cpu.Dispose(); provider.Dispose(); });
            var nvml = new NvidiaNvmlGpuClockOffsetProbe();
            var nvapi = new NvidiaNvapiGpuClockOffsetProbe();
            bool Eligible()
            {
                try
                {
                    if (!Rz090528HyperBoostValidationCommand.IsIsolated()
                        || !GpuClockOffsetValidationEnvironment.AreGpuTuningToolsInactive()
                        || NvidiaNvmlGpuClockOffsetValidationApi.ReadDriverVersion() != "617.14")
                    {
                        return false;
                    }
                    var values = new[] { nvml.Query(), nvapi.Query() };
                    bool zero = values.All(value => value.DeviceName == GpuClockOffsetValidationPlan.Rz090528UiStep.ExpectedDeviceName
                        && value.Core.CurrentMegahertz == 0 && value.Memory.CurrentMegahertz == 0);
                    gpuOffsets.Add(new { Nvml = values[0], Nvapi = values[1], ZeroConfirmed = zero });
                    return zero && Rz090528HyperBoostValidationCommand.IsIsolated();
                }
                catch { return false; }
            }
            async ValueTask<Rz090528BatteryBoundaryExternal> Read(CancellationToken cancellation)
            {
                BatteryExternalSnapshot snapshot = await lane.InvokeAsync(() =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    bool? windowsBefore = ReadWindowsAcOnline();
                    HwinfoCpuLogObservation cpuValue = cpu.ReadFresh(cancellation);
                    StockGpuTelemetry gpu = NvidiaNvmlGpuClockOffsetValidationApi.ReadExactRz090528GpuTelemetry();
                    bool noWorkload = ObserveNoBenchmarkProcesses();
                    bool gpuZero = Eligible();
                    bool? windowsAfter = ReadWindowsAcOnline();
                    cancellation.ThrowIfCancellationRequested();
                    var combined = ComposeReading(cpuValue, gpu, windowsBefore, windowsAfter, noWorkload, gpuZero, DateTimeOffset.UtcNow);
                    return new(cpuValue, gpu, windowsBefore, windowsAfter, combined);
                }, accepted => observations.Add(accepted), cancellation);
                return snapshot.Combined!;
            }
            var boundary = new Rz090528PadlessBatteryBoundaryValidation(Read, signal =>
            {
                string marker = signal switch
                {
                    Rz090528BatteryBoundarySignal.ReadyToUnplug => "READY_TO_UNPLUG",
                    Rz090528BatteryBoundarySignal.BatteryObservedReconnectNow => "BATTERY_OBSERVED_RECONNECT_NOW",
                    Rz090528BatteryBoundarySignal.AbortReconnectAc => "ABORT_RECONNECT_AC_AND_WAIT",
                    _ => "RECOVERY_REQUIRED"
                };
                progress.WriteLine($"{marker} {DateTimeOffset.UtcNow:O}");
                progress.Flush();
            }, externalQuiesced: () => lane.OperationQuiesced, readWindowsAc: ReadWindowsAcOnline);
            runner = Rz090528PadlessHyperBoostValidation.CreateWindows(Eligible, batteryBoundary: boundary,
                batteryReadIsolation: () => Rz090528HyperBoostValidationCommand.IsIsolated()
                    && GpuClockOffsetValidationEnvironment.AreGpuTuningToolsInactive());
            runnerEntered = true;
            result = await runner.RunAsync(PerformanceFanValidationTarget.HyperBoostFromPerformance, token);
            finalGpuZero = await VerifyFinalGpuZeroAsync(lane,
                () => new BatteryExternalSnapshot(null, null, null, null, null, Eligible()),
                value => value.FinalGpuZero == true, runner.DisposalConfirmed,
                runner.BatteryBoundaryResult?.SnapshotQuiesced != false, token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            outerFailure = error is OperationCanceledException ? "Canceled" : error.GetType().Name + ":ExternalValidationFailed";
        }
        finally
        {
            if (lane is not null) { externalTelemetryQuiesced = await lane.CloseAsync(); }
            else { cpu?.Dispose(); provider?.Dispose(); }
            frozenGpuOffsets = gpuOffsets.Close();
        }
        bool manualRecovery = result is null ? runnerEntered
            : result.ManualRecoveryRequired || result.IndeterminatePartialWrite || result.ApplyAttempted && !result.Restored
                || runner?.BatteryBoundaryResult?.ManualRecoveryRequired == true
                || runner?.HardwareOpened == true && (runner.DisposalConfirmed != true
                    || runner.BatteryBoundaryResult?.SnapshotQuiesced == false || !externalTelemetryQuiesced)
                || result.ApplyAttempted && !finalGpuZero;
        bool success = outerFailure is null && result is { Success: true, Restored: true }
            && runner?.BatteryBoundaryResult is { Completed: true, SnapshotQuiesced: true, Restored: true }
            && runner.DisposalConfirmed && finalGpuZero && externalTelemetryQuiesced && !token.IsCancellationRequested;
        if (manualRecovery)
        {
            await progress.WriteLineAsync($"RECOVERY_REQUIRED {DateTimeOffset.UtcNow:O}");
            await progress.FlushAsync();
        }
        await output.WriteLineAsync(SerializeReport(new
        {
            Target = "RZ09-05289EN4 / 1532:02C6 / BIOS 2.02 / RTX 5090 Laptop / driver 617.14",
            Rz090528PadlessBatteryBoundaryValidation.PlanSha256,
            Plan = JsonSerializer.Deserialize<JsonElement>(Rz090528PadlessBatteryBoundaryValidation.ReadPlan()),
            Hwinfo = providerProvenance,
            Result = result,
            BatteryBoundary = runner?.BatteryBoundaryResult,
            ExternalTelemetry = observations,
            IndependentGpuOffsets = frozenGpuOffsets,
            GpuOffsetEvidenceTruncated = gpuOffsets.Truncated,
            FinalGpuZeroConfirmed = finalGpuZero,
            HardwareOpened = runner?.HardwareOpened ?? false,
            DisposalConfirmed = runner?.DisposalConfirmed ?? !runnerEntered,
            ExternalTelemetryQuiesced = externalTelemetryQuiesced,
            SnapshotQuiesced = runner?.BatteryBoundaryResult?.SnapshotQuiesced ?? (runner?.DisposalConfirmed ?? !runnerEntered),
            OuterFailure = outerFailure,
            PossibleSubmission = result?.ApplyAttempted ?? runnerEntered,
            ManualRecoveryRequired = manualRecovery,
            HardwareRestorationConfirmed = result is { CandidateConfirmed: true, Restored: true } && finalGpuZero,
            ProductionPadlessAdmitted = false,
            ThermalSafetyEstablished = false,
            CpuIdleEstablished = false,
            GpuWritesPerformed = false,
            CoolingPadFeatureWritesPerformed = false,
            WorkloadLaunchedOrStopped = false,
            Success = success,
        }));
        return success ? 0 : 3;
    }

    private sealed record BatteryExternalSnapshot(HwinfoCpuLogObservation? Cpu, StockGpuTelemetry? Gpu,
        bool? WindowsBefore, bool? WindowsAfter, Rz090528BatteryBoundaryExternal? Combined,
        bool? FinalGpuZero = null);

    private static bool? ReadWindowsAcOnline() => GetSystemPowerStatus(out SystemPowerStatus status)
        ? status.AcLineStatus switch { 0 => false, 1 => true, _ => null } : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        internal byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        internal uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
