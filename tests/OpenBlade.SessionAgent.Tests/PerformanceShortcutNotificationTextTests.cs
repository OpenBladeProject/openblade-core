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

    [Fact]
    public void UnrecognizedMessageIsPreserved()
    {
        const string message = "Performance mode changed.";

        Assert.Equal(
            message,
            PerformanceShortcutNotificationText.FormatAppliedBody(message));
    }
}
