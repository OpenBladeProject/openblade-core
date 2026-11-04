using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture;

internal static class SilentLowFanValidationCommand
{
    internal const string StopName = "validate-silent-fan-stop-1500";
    internal const string LowestName = "validate-silent-lowest-spinning";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static bool TryParseArguments(IReadOnlyList<string> arguments, string biosVersion, out string? journalPath)
    {
        journalPath = null;
        if (arguments.Count != 4 || arguments[0] != "--confirm-target"
            || arguments[1] != $"RZ09-0581:1532:02E0:{biosVersion}"
            || arguments[2] != "--journal" || !Path.IsPathFullyQualified(arguments[3]))
        {
            return false;
        }

        journalPath = Path.GetFullPath(arguments[3]);
        return true;
    }

    internal static async Task<int> RunWindowsAsync(IReadOnlyList<string> arguments,
        TextWriter output, SilentLowFanScenario scenario)
    {
        var registry = new WindowsRegistryValueSource();
        var identity = new WindowsHostIdentitySource(registry, new RuntimeWindowsPlatformEvidenceSource(registry));
        var initial = identity.ReadExactIdentity();
        if (initial.Failure is not null || initial.Evidence?.BiosVersion is not { Length: > 0 } bios)
        {
            await output.WriteLineAsync("The exact RZ09-0581 identity is unavailable.");
            return 2;
        }
        if (!TryParseArguments(arguments, bios, out string? path))
        {
            await output.WriteLineAsync($"Usage: OpenBlade.Capture {(scenario == SilentLowFanScenario.FanStop1500 ? StopName : LowestName)} --confirm-target RZ09-0581:1532:02E0:{bios} --journal <absolute-new-jsonl-path>");
            return 1;
        }
        using var cancellation = new CancellationTokenSource();
        using var registration = ConsoleCancellation.Register(() => ConsoleCancellation.Cancel(cancellation));
        using var temperatures = new SafeWindowsFanTemperatureSource();
        using var stream = new FileStream(path!, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        void Append(object entry)
        {
            writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        long thermalTimestamp = 0;
        bool coolAndGpuInactive = false;
        string? CoreFailure()
        {
            var current = identity.ReadExactIdentity();
            if (current.Failure is not null || current.Evidence?.BiosVersion != bios)
            {
                return "ExactHostOrRecordedFirmwareChanged";
            }

            uint console = WTSGetActiveConsoleSessionId();
            using var process = Process.GetCurrentProcess();
            if (!Environment.UserInteractive || console is 0 or uint.MaxValue || process.SessionId != console)
            {
                return "ActiveLocalConsoleSessionRequired";
            }

            if (!GetSystemPowerStatus(out var power) || power.AcLineStatus != 1)
            {
                return "PhysicalAcPowerRequired";
            }

            return null;
        }
        bool SubmissionAllowed(bool restoring)
        {
            try
            {
                return CoreFailure() is null && (restoring || coolAndGpuInactive && thermalTimestamp != 0
                    && Stopwatch.GetElapsedTime(thermalTimestamp) <= TimeSpan.FromSeconds(2));
            }
            catch { return false; }
        }
        async Task<string?> AdmitAsync(bool restoring, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (CoreFailure() is { } failure)
            {
                return failure;
            }

            if (!restoring)
            {
                // Timestamp before the potentially asynchronous sensor read: slow reads
                // cannot manufacture a fresh thermal admission at completion.
                thermalTimestamp = Stopwatch.GetTimestamp();
                coolAndGpuInactive = false;
                var reading = await temperatures.ReadAsync(includeCpu: true, includeGpu: true, token);
                coolAndGpuInactive = reading.CpuCelsius is >= 10 and <= 55 && reading.GpuInactive;
                Append(new { phase = "thermalAdmission", reading.CpuCelsius, reading.GpuInactive });
                if (!coolAndGpuInactive || Stopwatch.GetElapsedTime(thermalTimestamp) > TimeSpan.FromSeconds(2))
                {
                    return "FreshCpu10To55CAndConfirmedGpuInactiveRequired";
                }
            }
            return null;
        }
        Append(new
        {
            schemaVersion = 1,
            phase = "experiment",
            scenario,
            model = "RZ09-0581",
            vendorId = "1532",
            productId = "02E0",
            biosVersion = bios,
            performance = "Silent",
            productionAdmitted = false,
            preparationRpm = 2000,
            targets = scenario == SilentLowFanScenario.FanStop1500 ? new[] { 1500 } : new[] { 1900, 1800, 1700, 1600, 1500 },
            sourceBaseSha256 = "BC7DC47E18261AB38393589D336C11F321CC431D700ED32147997144F64628CA",
            sourcePolicySha256 = "CD29E8D89135EA967AB13B4607A6524087A6B1DDBF76F7C73A39C7E4C6EC404D"
        });
        var runner = BatteryValidationFactory.CreateWindowsSilentLowFanValidation(AdmitAsync, SubmissionAllowed, progress => Append(progress));
        var result = await runner.RunAsync(scenario, cancellation.Token);
        Append(new { phase = "completed", result });
        await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
        return result.Success ? 0 : 2;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        internal byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        internal uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
