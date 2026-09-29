using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal static class Rz090528HyperBoostValidationCommand
{
    internal const string Name = "validate-rz09-0528-02c6-hyperboost-performance";
    internal const string Confirmation =
        "RZ09-0528:1532:02C6:BIOS-2.02:HYPERBOOST:RESTORE-PERFORMANCE";
    internal const string PadConfirmation = "ATTENDED:COOLING-PAD-FAN-READY";

    internal static async Task<int> RunAsync(
        string[] arguments, Func<IPerformanceFanSetterValidationRunner> createRunner,
        TextWriter output, CancellationToken cancellationToken)
    {
        if (!arguments.SequenceEqual(new[]
            { "--confirm-target", Confirmation, "--confirm-pad-ready", PadConfirmation }))
        {
            await output.WriteLineAsync(
                $"Requires --confirm-target {Confirmation} --confirm-pad-ready {PadConfirmation}. " +
                "A present operator must first confirm the genuine cooling pad fan is ready; " +
                "baseline must be Performance with Automatic fans. This is not production admission.");
            return 2;
        }

        PerformanceFanSetterValidationResult result = await createRunner().RunAsync(
            PerformanceFanValidationTarget.HyperBoostFromPerformance, cancellationToken);
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            Target = "RZ09-0528 1532:02C6 BIOS 2.02",
            ProductionAdmitted = false,
            AttendedCoolingPadFanReadyAcknowledged = true,
            Result = result,
        }, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        }));
        return result.Success ? 0 : 3;
    }

    internal static bool IsIsolated()
    {
        if (!GpuClockOffsetValidationEnvironment.HasOneExactProtocolCollection()
            || !GpuClockOffsetValidationEnvironment.IsSynapseInactive()
            || !GpuClockOffsetValidationEnvironment.IsOpenBladeServiceInactive())
        {
            return false;
        }
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("USBPcapCMD");
        }
        catch
        {
            return false;
        }
        try
        {
            return processes.Length == 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }
}
