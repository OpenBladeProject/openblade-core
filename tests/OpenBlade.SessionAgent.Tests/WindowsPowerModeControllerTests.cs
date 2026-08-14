using OpenBlade.Contracts;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsPowerModeControllerTests
{
    [Theory]
    [InlineData(WindowsPowerMode.BestPowerEfficiency, "961cc777-2547-4f9d-8174-7d86181b8a7a")]
    [InlineData(WindowsPowerMode.Balanced, "00000000-0000-0000-0000-000000000000")]
    [InlineData(WindowsPowerMode.BestPerformance, "ded574b5-45a0-4f42-8737-46345c09c238")]
    public void ModeGuidMapping_UsesDocumentedWindowsValues(
        WindowsPowerMode mode,
        string expectedGuid)
    {
        Guid guid = WindowsPowerModeController.ToGuid(mode);

        Assert.Equal(Guid.Parse(expectedGuid), guid);
        Assert.Equal(mode, WindowsPowerModeController.FromGuid(guid));
    }

    [Fact]
    public void UnknownGuid_IsNotMisreportedAsSupportedMode()
    {
        Assert.Null(WindowsPowerModeController.FromGuid(Guid.NewGuid()));
    }
}
