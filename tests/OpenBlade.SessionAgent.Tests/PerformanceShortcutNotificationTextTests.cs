using OpenBlade.Contracts;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class PerformanceShortcutNotificationTextTests
{
    [Theory]
    [InlineData("The performance button selected Silent.", "Current mode: Silent")]
    [InlineData("Fn+P selected Performance.", "Current mode: Performance")]
    [InlineData("The performance button selected BatteryBalanced.", "Current mode: Balanced")]
    [InlineData("Fn+P selected BatterySaver.", "Current mode: Battery Saver")]
    [InlineData("The performance button selected Custom.", "Current mode: Custom")]
    public void AppliedShortcutMessageUsesFriendlyCurrentMode(
        string serviceMessage,
        string expected)
    {
        Assert.Equal(
            expected,
            PerformanceShortcutNotificationText.FormatAppliedBody(serviceMessage));
    }

    [Theory]
    [InlineData("The performance button selected Silent.", PerformancePreset.Silent)]
    [InlineData("Fn+P selected BatterySaver.", PerformancePreset.BatterySaver)]
    public void SelectedPresetCanBeComparedWithOptimisticFeedback(
        string serviceMessage,
        PerformancePreset expected)
    {
        Assert.True(PerformanceShortcutNotificationText.TryGetSelectedPreset(
            serviceMessage,
            out PerformancePreset actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnrecognizedMessageIsPreserved()
    {
        const string message = "Performance mode changed.";

        Assert.Equal(
            message,
            PerformanceShortcutNotificationText.FormatAppliedBody(message));
    }
}
