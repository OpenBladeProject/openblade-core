using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// Fixed developer experiment; it does not authorize production GPU/HyperBoost combinations.
internal static class Rz090528HyperBoostGpuValidationCommand
{
    internal const string Name = "validate-rz09-0528-02c6-hyperboost-gpu";
    internal const string Confirmation = "RZ09-0528:02C6:BIOS-2.02:RTX5090:617.14:GPU-5:RESTORE-ZERO";

    internal static Task<int> RunWindowsAsync(string[] arguments, TextWriter output, CancellationToken token) =>
        RunAsync(arguments,
            observer => Rz090528HyperBoostValidation.CreateWindows(
                () => Rz090528HyperBoostValidationCommand.IsIsolated()
                    && GpuClockOffsetValidationEnvironment.AreGpuTuningToolsInactive()
                    && ReadDriverVersion() == "617.14", observer),
            guard => new NvidiaNvmlGpuClockOffsetValidationController(guard),
            new NvidiaNvapiGpuClockOffsetProbe(), ReadDriverVersion, output, token);

    internal static async Task<int> RunAsync(string[] arguments,
        Func<ExactHyperBoostValidationObserver, IPerformanceFanSetterValidationRunner> createRunner,
        Func<Func<bool>, IGpuClockOffsetValidationController> createGpu,
        IGpuClockOffsetProbe independent, Func<string> driverVersion,
        TextWriter output, CancellationToken token)
    {
        if (!arguments.SequenceEqual(new[] { "--confirm-target", Confirmation,
            "--confirm-pad-ready", Rz090528HyperBoostValidationCommand.PadConfirmation }))
        {
            await output.WriteLineAsync("Requires the exact target and attended cooling-pad confirmations; Performance/Automatic and zero GPU offsets are required.");
            return 2;
        }

        var observations = new List<object>();
        var results = new List<PerformanceFanSetterValidationResult>();
        bool failed = false;
        bool gpuWriteAttempted = false;
        Func<CancellationToken, Task<bool>>? thermalGuard = null;
        IGpuClockOffsetValidationController? gpu = null;

        bool GuardNative()
        {
            try
            {
                bool allowed = !failed && !token.IsCancellationRequested
                    && driverVersion() == "617.14"
                    && thermalGuard is not null
                    && thermalGuard(token).GetAwaiter().GetResult();
                failed |= !allowed;
                return allowed;
            }
            catch { failed = true; return false; }
        }

        void ReadOffsets(string phase, int? expected)
        {
            GpuClockOffsetCapabilities nvml = gpu!.Query();
            GpuClockOffsetCapabilities nvapi = independent.Query();
            observations.Add(new { Phase = phase, Nvml = nvml, Nvapi = nvapi });
            if (expected is null)
            {
                return;
            }

            foreach (GpuClockOffsetCapabilities state in new[] { nvml, nvapi })
            {
                if (state.DeviceName != GpuClockOffsetValidationPlan.Rz090528UiStep.ExpectedDeviceName
                    || state.Core.MinimumMegahertz > 0 || state.Core.MaximumMegahertz < 5
                    || state.Memory.MinimumMegahertz > 0 || state.Memory.MaximumMegahertz < 5
                    || state.Core.CurrentMegahertz != expected || state.Memory.CurrentMegahertz != expected)
                {
                    throw new InvalidOperationException("The two GPU APIs did not confirm the exact GPU and expected offsets.");
                }
            }
        }

        void WriteOffsets(string phase, int value)
        {
            if (!GuardNative())
            {
                throw new InvalidOperationException("GPU prewrite guard failed.");
            }

            gpuWriteAttempted = true;
            gpu!.Set(new(value, value));
            ReadOffsets(phase, value);
        }

        try
        {
            gpu = createGpu(GuardNative);
            for (int scenario = 1; scenario <= 2; scenario++)
            {
                int currentScenario = scenario;
                async Task<bool> Observe(ExactHyperBoostValidationStage stage,
                    Func<CancellationToken, Task<bool>> guard, CancellationToken cancellation)
                {
                    thermalGuard = guard;
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!GuardNative())
                        {
                            throw new InvalidOperationException("Combined validation guard failed.");
                        }

                        string phase = $"Scenario{currentScenario}/{stage}";
                        ReadOffsets(phase, currentScenario == 2 && stage != ExactHyperBoostValidationStage.Baseline ? 5 : 0);
                        if (currentScenario == 1 && stage == ExactHyperBoostValidationStage.Candidate)
                        {
                            WriteOffsets(phase + "/ApplyFive", 5);
                            WriteOffsets(phase + "/RestoreZero", 0);
                        }
                        if (currentScenario == 2 && stage == ExactHyperBoostValidationStage.Baseline)
                        {
                            WriteOffsets(phase + "/ApplyFive", 5);
                        }

                        if (currentScenario == 2 && stage == ExactHyperBoostValidationStage.Restored)
                        {
                            WriteOffsets(phase + "/RestoreZero", 0);
                        }

                        if (!GuardNative())
                        {
                            throw new InvalidOperationException("Postwrite thermal guard failed.");
                        }
                        return true;
                    }
                    catch (Exception exception)
                    {
                        failed = true;
                        observations.Add(new { Phase = $"Scenario{currentScenario}/{stage}/Stopped", Failure = exception.GetType().Name });
                        try { ReadOffsets("ReadOnlyReconciliation", null); } catch { }
                        return false;
                    }
                    finally { thermalGuard = null; }
                }
                PerformanceFanSetterValidationResult result = await createRunner(Observe).RunAsync(
                    PerformanceFanValidationTarget.HyperBoostFromPerformance, token);
                results.Add(result);
                if (!result.Success || failed)
                {
                    failed = true;
                    try { ReadOffsets("ReadOnlyReconciliation", null); } catch { }
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            failed = true;
            observations.Add(new { Phase = "Stopped", Failure = exception.GetType().Name });
            if (gpu is not null) { try { ReadOffsets("ReadOnlyReconciliation", null); } catch { } }
        }
        bool success = !failed && results.Count == 2 && results.All(result => result.Success);
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            Target = "RZ09-0528 1532:02C6 BIOS 2.02 / RTX 5090 Laptop / driver 617.14",
            ProductionCombinationAdmitted = false,
            RequiredBaseline = "Performance/Automatic; GPU core=0, memory=0",
            AttendedCoolingPadFanReadyAcknowledged = true,
            GpuWriteAttempted = gpuWriteAttempted,
            Observations = observations,
            ThermalResults = results,
            ThermalFailureReconciliation = results.Select(result => result.ObserverFailureReconciliation).ToArray(),
            ManualRecoveryRequired = !success && (gpuWriteAttempted || results.Any(result => result.ApplyAttempted)),
            Success = success,
        }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        return success ? 0 : 3;
    }

    private static string ReadDriverVersion()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                Arguments = "--query-gpu=driver_version --format=csv,noheader",
            },
        };
        if (!process.Start())
        {
            return string.Empty;
        }

        if (!process.WaitForExit(3000)) { process.Kill(); return string.Empty; }
        return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : string.Empty;
    }
}
