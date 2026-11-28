// Run in the pinned managed service test project; see manual-fan-stop-replay.md.
using System.Globalization;
using OpenBlade.Contracts;
using OpenBlade.FanStop;
using OpenBlade.FanStop.Guardian;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

public sealed class ManualFanStopEntryOracle
{
    [Fact]
    public void ExactContractAndDriverPinsMatch()
    {
        var contract = FanStopModelContract.ForFirmware("RZ09-0581", new Version(1, 4))!;
        var pins = File.ReadAllLines(RequiredPath("OPENBLADE_FAN_STOP_REPLAY_PINS"));
        Assert.Equal(pins, new[] { contract.DeviceIdentity, contract.FirmwareIdentity,
            contract.FirmwareSha256, contract.ContractSha256, So6120SupervisedAdmission.DriverHash });
    }

    public static IEnumerable<object[]> ThermalCases() =>
        File.ReadLines(RequiredPath("OPENBLADE_FAN_STOP_REPLAY_CASES"))
            .Where(line => !line.StartsWith('#')).Select(line => new object[] { line });

    [Theory]
    [MemberData(nameof(ThermalCases))]
    public void EntryThermalDecisionMatchesFixture(string line)
    {
        var f = line.Split('\t');
        var settings = f[1] == "Auto" ? FanStopSettings.Default : FanStopSettings.Default with
        {
            EndMode = FanStopEndMode.Custom,
            CpuLimitCelsius = 60,
            GpuLimitCelsius = 65,
            CpuImmediateCutoffCelsius = 80,
            GpuImmediateCutoffCelsius = 75,
        };
        var reading = new FanTemperatureReadings(Value(f[2]), Value(f[3]), bool.Parse(f[4]), bool.Parse(f[5]));
        var age = TimeSpan.FromMilliseconds(int.Parse(f[6], CultureInfo.InvariantCulture));
        bool canceled = bool.Parse(f[7]);
        var policy = new FanStopThermalPolicy(settings, TimeProvider.System);
        bool admitted = policy.AdmitsEntry(reading.CpuCelsius, reading.GpuCelsius, reading.GpuInactive,
            reading.GpuPoweredDown, age, canceled);
        var decision = policy.EntryThermalDecision;
        Assert.Equal(f[8], admitted ? "Admitted" : decision?.Rule ?? "SafetyCondition");
        Assert.Equal(f[9], Trigger(decision?.Cpu));
        Assert.Equal(f[10], Trigger(decision?.Gpu));
        if (!admitted)
        {
            string diagnostic = So6120ServiceInitialAdmissionDiagnostics.ThermalRefusal(settings, reading,
                age, canceled, decision);
            Assert.Contains($"stage=ThermalEntry;", diagnostic);
            Assert.Contains($"mode={settings.EndMode}; cpuC={Display(reading.CpuCelsius)}; gpuC={Display(reading.GpuCelsius)};", diagnostic);
            Assert.Contains($"gpuInactive={reading.GpuInactive}; gpuPoweredDown={reading.GpuPoweredDown};", diagnostic);
            Assert.Contains(FormattableString.Invariant($"sampleAgeMs={age.TotalMilliseconds:F1}; sampleCanceled={canceled};"), diagnostic);
            Assert.Contains(f[1] == "Auto" ? "cpuEntryLimitC=55; gpuEntryLimitC=inactive-required"
                : "cpuEntryLimitC=80; gpuEntryLimitC=75", diagnostic);
        }
    }

    private static string RequiredPath(string variable) =>
        Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException($"Set {variable}.");

    private static int? Value(string value) => value == "-" ? null : int.Parse(value, CultureInfo.InvariantCulture);
    private static string Display(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
    private static string Trigger(FanStopThermalTrigger? trigger) => trigger is null ? "-"
        : FormattableString.Invariant($"{trigger.Celsius}:{trigger.LimitCelsius}");
}
