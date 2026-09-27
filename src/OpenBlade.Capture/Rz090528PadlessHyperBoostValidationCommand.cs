using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture;

internal static class Rz090528PadlessHyperBoostValidationCommand
{
    internal const string Name = "validate-rz09-0528-02c6-hyperboost-padless-immediate";
    internal const string NoLoadName = "validate-rz09-0528-02c6-hyperboost-padless-no-load";
    internal const string Confirmation = "RZ09-0528:1532:02C6:BIOS-2.02:PERFORMANCE-AUTO:GPU-ZERO:APPLY-ONCE:RESTORE";
    internal const string AttendedConfirmation = "ATTENDED:PAD-USB-AND-POWER-DISCONNECTED:NO-LOAD";

    internal static async Task<int> RunWindowsAsync(string[] args, TextWriter output, CancellationToken token, bool noLoad = false)
    {
        using var temperature = new SafeWindowsFanTemperatureSource();
        var observation = noLoad ? new Rz090528PadlessNoLoadValidation(new HyperBoostThermalTelemetrySource(temperature)) : null;
        return await RunAsync(args, guard => Rz090528PadlessHyperBoostValidation.CreateWindows(guard, observation),
            new NvidiaNvmlGpuClockOffsetValidationController(), new NvidiaNvapiGpuClockOffsetProbe(),
            () => Rz090528HyperBoostValidationCommand.IsIsolated()
                && GpuClockOffsetValidationEnvironment.AreGpuTuningToolsInactive()
                && NvidiaNvmlGpuClockOffsetValidationApi.ReadDriverVersion() == "617.14",
            async cancellation => await temperature.ReadAsync(cancellation), output, token, noLoad);
    }

    internal static async Task<int> RunAsync(string[] args,
        Func<Func<bool>, Rz090528PadlessHyperBoostValidation> createRunner,
        IGpuClockOffsetValidationController gpu, IGpuClockOffsetProbe independent,
        Func<bool> isolated, Func<CancellationToken, ValueTask<FanTemperatureReadings>> telemetry,
        TextWriter output, CancellationToken token, bool noLoad = false)
    {
        string confirmation = noLoad ? Confirmation + ":60S:BASELINE-PLUS-10C" : Confirmation;
        string planHash = noLoad ? Rz090528PadlessNoLoadValidation.PlanSha256 : Rz090528PadlessHyperBoostValidation.PlanSha256;
        if (!args.SequenceEqual(new[] { "--confirm-target", confirmation,
            "--confirm-pad-disconnected", AttendedConfirmation,
            "--plan-sha256", planHash }))
        {
            await output.WriteLineAsync("Exact target, attended pad USB/power disconnection, no-load and reviewed plan confirmations are required.");
            return 2;
        }
        byte[] plan = noLoad ? Rz090528PadlessNoLoadValidation.ReadPlan() : Rz090528PadlessHyperBoostValidation.ReadPlan();
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        var gpuObservations = new List<object>();
        bool Eligible()
        {
            try
            {
                if (!isolated())
                {
                    return false;
                }

                GpuClockOffsetCapabilities nvml = gpu.Query(), nvapi = independent.Query();
                bool zero = new[] { nvml, nvapi }.All(value =>
                    value.DeviceName == GpuClockOffsetValidationPlan.Rz090528UiStep.ExpectedDeviceName
                    && value.Core.CurrentMegahertz == 0 && value.Memory.CurrentMegahertz == 0);
                gpuObservations.Add(new { Nvml = nvml, Nvapi = nvapi, ZeroConfirmed = zero });
                return zero && isolated();
            }
            catch { return false; }
        }
        async Task<FanTemperatureReadings?> Temperature()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { return await telemetry(timeout.Token); } catch { return null; }
        }
        FanTemperatureReadings? before = await Temperature();
        DateTimeOffset firstTemperatureAtUtc = DateTimeOffset.UtcNow;
        FanTemperatureReadings? second = await Temperature();
        DateTimeOffset secondTemperatureAtUtc = DateTimeOffset.UtcNow;
        static bool Sane(FanTemperatureReadings? value) => value is { CpuCelsius: > 0 } readings
            && (readings.GpuCelsius is > 0 || readings.GpuCelsius is null && readings.GpuInactive);
        if (!Sane(before) || !Sane(second))
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                PlanSha256 = planHash,
                ProductionAdmitted = false,
                PadlessProductionAdmitted = false,
                PreflightFailure = "CPU telemetry must be present and positive; GPU telemetry must be positive or explicitly inactive.",
                TemperatureBefore = before,
                FirstTemperatureAtUtc = firstTemperatureAtUtc,
                TemperatureSecond = second,
                SecondTemperatureAtUtc = secondTemperatureAtUtc,
                HardwareOpened = false,
                ApplyAttempted = false,
                ManualRecoveryRequired = false,
                SensorSanityDoesNotEstablishThermalSafety = true,
                Success = false,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 3;
        }
        var runner = createRunner(Eligible);
        PerformanceFanSetterValidationResult result = await runner.RunAsync(
            PerformanceFanValidationTarget.HyperBoostFromPerformance, token);
        bool finalGpuZero = Eligible();
        FanTemperatureReadings? after = await Temperature();
        bool hardwareRoundTripConfirmed = result.ApplyAcknowledged && result.CandidateConfirmed && result.Restored && finalGpuZero;
        bool telemetryUncertain = !Sane(after);
        bool success = result.Success && finalGpuZero && !telemetryUncertain && !token.IsCancellationRequested
            && (!noLoad || runner.NoLoadResult?.Completed == true);
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            Target = "RZ09-0528 / RZ09-05289EN4 / 1532:02C6 / BIOS 2.02 / RTX 5090 Laptop / driver 617.14",
            PlanSha256 = planHash,
            Plan = JsonSerializer.Deserialize<JsonElement>(plan),
            ProductionAdmitted = false,
            PadlessProductionAdmitted = false,
            PadlessConditionPreviouslyValidated = false,
            StartedUtc = startedUtc,
            CompletedUtc = DateTimeOffset.UtcNow,
            AttendedPadUsbAndPowerDisconnectedAcknowledged = true,
            runner.FirstAbsenceConfirmed,
            runner.SecondAbsenceConfirmed,
            runner.AbsenceSampleElapsedMilliseconds,
            runner.FirstAbsenceObservedAtUtc,
            runner.SecondAbsenceObservedAtUtc,
            runner.FailureReconciliation,
            GpuObservations = gpuObservations,
            GpuWritesPerformed = false,
            CoolingPadFeatureWritesPerformed = false,
            FinalGpuZeroConfirmed = finalGpuZero,
            TemperatureBefore = before,
            FirstTemperatureAtUtc = firstTemperatureAtUtc,
            TemperatureSecond = second,
            SecondTemperatureAtUtc = secondTemperatureAtUtc,
            TemperatureAfter = after,
            TemperatureAfterAtUtc = DateTimeOffset.UtcNow,
            TelemetryUncertain = telemetryUncertain,
            HardwareRoundTripConfirmed = hardwareRoundTripConfirmed,
            SensorSanityDoesNotEstablishThermalSafety = true,
            TelemetryEstablishesNoThermalLimit = true,
            NoLoadPerformed = true,
            NoDwellOrLoadPerformed = !noLoad,
            NoDwellPerformed = !noLoad,
            NoLoadObservation = runner.NoLoadResult,
            Result = result,
            CancellationRequested = token.IsCancellationRequested,
            Success = success,
        }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        return success ? 0 : 3;
    }
}
