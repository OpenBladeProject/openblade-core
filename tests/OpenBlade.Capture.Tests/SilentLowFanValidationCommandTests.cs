using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class SilentLowFanValidationCommandTests
{
    [Theory]
    [InlineData(40, 55, true, false, true)]
    [InlineData(40, 10, true, false, true)]
    [InlineData(40, null, true, true, true)]
    [InlineData(40, 85, true, false, false)]
    [InlineData(40, 9, true, false, false)]
    [InlineData(40, null, true, false, false)]
    [InlineData(40, 40, false, false, false)]
    [InlineData(null, null, true, true, false)]
    [InlineData(9, null, true, true, false)]
    [InlineData(56, null, true, true, false)]
    public void ThermalAdmissionRequiresCoolCpuAndInactiveGpuWithThermalEvidence(
        int? cpu, int? gpu, bool inactive, bool poweredDown, bool expected) =>
        Assert.Equal(expected, SilentLowFanValidationCommand.IsThermallySafe(
            new(cpu, gpu, inactive, GpuPoweredDown: poweredDown)));

    [Fact]
    public void NamedExperimentRequiresExactIdentityAndNewJournalArgument()
    {
        string path = Path.Combine(Path.GetTempPath(), "low-fan-test.jsonl");
        Assert.True(SilentLowFanValidationCommand.TryParseArguments(
            ["--confirm-target", "RZ09-0581:1532:02E0:4.01", "--journal", path],
            "4.01", out string? actual));
        Assert.Equal(path, actual);
        Assert.False(SilentLowFanValidationCommand.TryParseArguments(
            ["--confirm-target", "RZ09-0528:1532:02C6:4.01", "--journal", path],
            "4.01", out _));
        Assert.False(SilentLowFanValidationCommand.TryParseArguments(
            ["--confirm-target", "RZ09-0581:1532:02E0:4.00", "--journal", path],
            "4.01", out _));
        Assert.False(SilentLowFanValidationCommand.TryParseArguments(
            ["--confirm-target", "RZ09-0581:1532:02E0:4.01", "--journal", "relative.jsonl"],
            "4.01", out _));
        Assert.False(SilentLowFanValidationCommand.TryParseArguments(
            ["--confirm-target", "RZ09-0581:1532:02E0:4.01", "--journal", path, "--rpm", "0"],
            "4.01", out _));
    }
}
