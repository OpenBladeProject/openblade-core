using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture;

// Explicitly authorized named experiments; no production range override or raw command tool.
internal static class BatterySaverLowFanValidationCommand
{
    internal const string Name = "validate-battery-saver-1500";
    internal const string SpinDownName = "validate-battery-saver-spin-down-1500";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static bool TryParseArguments(
        IReadOnlyList<string> arguments, string biosVersion, out string? journalPath)
    {
        journalPath = null;
        if (arguments.Count != 4
            || arguments[0] != "--confirm-target"
            || arguments[1] != $"RZ09-0581:1532:02E0:{biosVersion}"
            || arguments[2] != "--journal"
            || !Path.IsPathFullyQualified(arguments[3]))
        {
            return false;
        }

        journalPath = Path.GetFullPath(arguments[3]);
        return true;
    }

    internal static async Task<int> RunWindowsAsync(
        IReadOnlyList<string> arguments, TextWriter output,
        bool confirmSpinningAt2000 = false)
    {
        var registry = new WindowsRegistryValueSource();
        var identity = new WindowsHostIdentitySource(
            registry, new RuntimeWindowsPlatformEvidenceSource(registry));
        HostIdentityReadResult initial = identity.ReadExactIdentity();
        if (initial.Failure is not null || initial.Evidence?.BiosVersion is not { Length: > 0 } bios)
        {
            await output.WriteLineAsync("The exact RZ09-0581 identity is unavailable.");
            return 2;
        }

        if (!TryParseArguments(arguments, bios, out string? journalPath))
        {
            string name = confirmSpinningAt2000 ? SpinDownName : Name;
            await output.WriteLineAsync($"Usage: OpenBlade.Capture {name} --confirm-target "
                + $"RZ09-0581:1532:02E0:{bios} --journal <absolute-new-jsonl-path>");
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        using IDisposable registration = ConsoleCancellation.Register(
            () => ConsoleCancellation.Cancel(cancellation));
        using var temperatures = new SafeWindowsFanTemperatureSource();
        using var stream = new FileStream(journalPath!, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        void Append(object entry)
        {
            writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        long temperatureTimestamp = 0;
        int? confirmedCpuTemperature = null;

        string? CoreAdmissionFailure()
        {
            HostIdentityReadResult current = identity.ReadExactIdentity();
            if (current.Failure is not null || current.Evidence?.BiosVersion != bios)
            {
                return "ExactHostOrRecordedFirmwareChanged";
            }

            uint consoleSession = WTSGetActiveConsoleSessionId();
            using Process currentProcess = Process.GetCurrentProcess();
            if (!Environment.UserInteractive || consoleSession is 0 or uint.MaxValue
                || currentProcess.SessionId != consoleSession)
            {
                return "ActiveLocalConsoleSessionRequired";
            }

            if (!GetSystemPowerStatus(out SystemPowerStatus power)
                || power.AcLineStatus != 0 || (power.BatteryFlag & 128) != 0
                || power.BatteryLifePercent is < 20 or > 100)
            {
                return "ConfirmedBatteryPowerAndTwentyPercentChargeRequired";
            }

            return null;
        }

        bool SubmissionAllowed(bool restoring)
        {
            try
            {
                return CoreAdmissionFailure() is null
                    && (restoring || confirmedCpuTemperature is >= 10 and <= 55
                        && temperatureTimestamp != 0
                        && Stopwatch.GetElapsedTime(temperatureTimestamp) <= TimeSpan.FromSeconds(2));
            }
            catch
            {
                return false;
            }
        }

        async Task<string?> AdmitAsync(bool restoring, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (CoreAdmissionFailure() is { } failure)
            {
                return failure;
            }

            // The temperature ceiling bounds this unvalidated low-speed experiment.
            // Restoring the saved firmware control must not be blocked by rising heat.
            if (!restoring)
            {
                FanTemperatureReadings reading = await temperatures.ReadAsync(
                    includeCpu: true, includeGpu: false, token).ConfigureAwait(false);
                confirmedCpuTemperature = reading.CpuCelsius;
                temperatureTimestamp = Stopwatch.GetTimestamp();
                Append(new { phase = "temperatureAdmission", cpuCelsius = reading.CpuCelsius });
                if (reading.CpuCelsius is not (>= 10 and <= 55))
                {
                    return "ConfirmedCpuTemperatureAtMost55CRequired";
                }
            }

            return null;
        }

        Append(new
        {
            schemaVersion = confirmSpinningAt2000 ? 2 : 1,
            phase = "experiment",
            model = "RZ09-0581",
            vendorId = "1532",
            productId = "02E0",
            biosVersion = bios,
            requestedRpm = 1500,
            preparationRpm = confirmSpinningAt2000 ? 2000 : (int?)null,
            preparationMinimumMeasuredRpm = confirmSpinningAt2000 ? 1800 : (int?)null,
            preparationMaximumMeasuredRpm = confirmSpinningAt2000 ? 2200 : (int?)null,
            preparationRequiredConsecutiveSamples = confirmSpinningAt2000 ? 3 : (int?)null,
            scenario = confirmSpinningAt2000 ? "2000-to-1500-spin-down" : "automaticTo1500",
            performance = "BatterySaver",
            productionAdmitted = false,
            sourceBaseSha256 = "BC7DC47E18261AB38393589D336C11F321CC431D700ED32147997144F64628CA",
            sourcePolicySha256 = "CD29E8D89135EA967AB13B4607A6524087A6B1DDBF76F7C73A39C7E4C6EC404D",
        });
        BatterySaverLowFanValidationRunner runner = BatteryValidationFactory
            .CreateWindowsBatterySaverLowFanValidation(
                AdmitAsync, progress => Append(progress), SubmissionAllowed, confirmSpinningAt2000);
        BatterySaverLowFanValidationResult result = await runner.RunAsync(cancellation.Token)
            .ConfigureAwait(false);
        Append(new { phase = "completed", result });
        await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
        return result.Success ? 0 : 2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        internal byte AcLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal uint BatteryLifeTime;
        internal uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
