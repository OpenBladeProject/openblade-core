using System.Text.Json;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture;

internal static class Rz090528StockLoadBaselineCommand
{
    internal const string Name = "observe-rz09-0528-02c6-stock-steel-nomad";
    internal const string Confirmation = "RZ09-0528:1532:02C6:BIOS-2.02:PERFORMANCE-AUTO:GPU-ZERO:PAD-ABSENT:READ-ONLY";

    internal static bool ValidArguments(string[] args) => args.Length == 6 && args.Take(4).SequenceEqual(new[]
    {
        "--confirm-target", Confirmation, "--plan-sha256", Rz090528StockLoadBaseline.PlanSha256,
    }) && args[4] == "--workload-exe" && Path.IsPathFullyQualified(args[5])
        && Path.GetFileName(args[5]).Equals(StockSteelNomadWorkload.ExpectedExecutableName, StringComparison.OrdinalIgnoreCase);

    internal static async Task<int> RunWindowsAsync(string[] args, TextWriter output, TextWriter progress, CancellationToken token)
    {
        if (!ValidArguments(args))
        {
            await output.WriteLineAsync("Exact read-only baseline target and reviewed plan confirmations required.");
            return 2;
        }
        var workload = new StockSteelNomadWorkload(args[5], StockSteelNomadWorkload.EnumerateWindows,
            NvidiaNvmlGpuClockOffsetValidationApi.ReadExactRz090528GpuUtilization);
        var nvml = new NvidiaNvmlGpuClockOffsetProbe();
        var nvapi = new NvidiaNvapiGpuClockOffsetProbe();
        bool Eligible()
        {
            if (!Rz090528HyperBoostValidationCommand.IsIsolated()
                || !GpuClockOffsetValidationEnvironment.AreGpuTuningToolsInactive()
                || NvidiaNvmlGpuClockOffsetValidationApi.ReadDriverVersion() != "617.14")
            {
                return false;
            }

            return new[] { nvml.Query(), nvapi.Query() }.All(value =>
                value.DeviceName == GpuClockOffsetValidationPlan.Rz090528UiStep.ExpectedDeviceName
                && value.Core.CurrentMegahertz == 0 && value.Memory.CurrentMegahertz == 0);
        }
        using var temperature = new SafeWindowsFanTemperatureSource();
        Rz090528PadlessHyperBoostValidation.StockBaselineQueries? queries = null;
        var actorFactory = Rz090528PadlessHyperBoostValidation.CreateWindows(Eligible);
        async Task<StockLoadReadings> Read(CancellationToken cancellation)
        {
            queries ??= await actorFactory.OpenStockBaselineQueriesAsync();
            var snapshot = await queries.ReadAsync(cancellation);
            if (snapshot.Failure is not null)
            {
                return new(null, null, null, null, snapshot.Failure);
            }

            var thermal = await ReadTelemetryAsync(temperature,
                NvidiaNvmlGpuClockOffsetValidationApi.ReadExactRz090528GpuTelemetry, cancellation);
            bool confirmed = actorFactory.IsStockBaselineEligible();
            return new(thermal.CpuCelsius, thermal.Gpu.GpuCelsius, snapshot.Fans?.Selector1Rpm, snapshot.Fans?.Selector2Rpm,
                thermal.Gpu.Failure ?? (confirmed ? null : "EligibilityLostAfterRead"))
            {
                PairedPerformanceAutomaticConfirmed = true,
                Full280WConfirmed = true,
                PadAbsentConfirmed = confirmed,
                IndependentGpuZeroConfirmed = confirmed,
                GpuTelemetry = thermal.Gpu,
            };
        }
        StockLoadBaselineResult? result = null;
        bool disposalConfirmed = false;
        try
        {
            var collector = new Rz090528StockLoadBaseline(workload, Read, actorFactory.IsStockBaselineEligible);
            result = await collector.RunAsync(at =>
            {
                progress.WriteLine($"ARMED {at:O} - Start one Steel Nomad DX12 run now. Read-only baseline; 180-second arming timeout.");
                progress.Flush();
            }, token);
        }
        finally
        {
            try { if (queries is not null) { await queries.DisposeAsync(); } disposalConfirmed = true; }
            catch { disposalConfirmed = false; }
        }
        bool success = result is { Completed: true } && disposalConfirmed;
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            Target = "RZ09-05289EN4 / 1532:02C6 / BIOS 2.02 / RTX 5090 Laptop / driver 617.14",
            Rz090528StockLoadBaseline.PlanSha256,
            Plan = JsonSerializer.Deserialize<JsonElement>(Rz090528StockLoadBaseline.ReadPlan()),
            Workload = workload.Bound is { } bound ? new
            {
                bound.ProcessId,
                bound.StartedAtUtc,
                ExecutableName = "3DMarkSteelNomad.exe",
                bound.Sha256,
                bound.Version
            } : null,
            Result = result,
            ReadOnly = true,
            FirmwareWritesPerformed = false,
            GpuWritesPerformed = false,
            WorkloadLaunchedOrStopped = false,
            ThermalPolicyEstablished = false,
            DisposalConfirmed = disposalConfirmed,
            Success = success,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return success ? 0 : 3;
    }

    internal static async Task<(int? CpuCelsius, StockGpuTelemetry Gpu)> ReadTelemetryAsync(
        IFanTemperatureSource cpu, Func<StockGpuTelemetry> gpu, CancellationToken token)
    {
        var reading = await cpu.ReadAsync(includeCpu: true, includeGpu: false, token);
        token.ThrowIfCancellationRequested();
        StockGpuTelemetry observed = gpu();
        token.ThrowIfCancellationRequested();
        return (reading.CpuCelsius, observed);
    }
}
