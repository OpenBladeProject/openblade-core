using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class BatterySaverLowFanValidationCommandTests
{
    [Fact]
    public void NamedExperimentRequiresExactIdentityAndNewJournalArgument()
    {
        string path = Path.Combine(Path.GetTempPath(), "low-fan-test.jsonl");
        Assert.True(BatterySaverLowFanValidationCommand.TryParseArguments(
            ["--confirm-target", "RZ09-0581:1532:02E0:4.01", "--journal", path],
            "4.01", out string? actual));
        Assert.Equal(path, actual);
        Assert.False(BatterySaverLowFanValidationCommand.TryParseArguments(
            ["--confirm-target", "RZ09-0528:1532:02C6:4.01", "--journal", path],
            "4.01", out _));
        Assert.False(BatterySaverLowFanValidationCommand.TryParseArguments(
            ["--confirm-target", "RZ09-0581:1532:02E0:4.00", "--journal", path],
            "4.01", out _));
        Assert.False(BatterySaverLowFanValidationCommand.TryParseArguments(
            ["--confirm-target", "RZ09-0581:1532:02E0:4.01", "--journal", "relative.jsonl"],
            "4.01", out _));
        Assert.False(BatterySaverLowFanValidationCommand.TryParseArguments(
            ["--confirm-target", "RZ09-0581:1532:02E0:4.01", "--journal", path, "--rpm", "0"],
            "4.01", out _));
    }
}
