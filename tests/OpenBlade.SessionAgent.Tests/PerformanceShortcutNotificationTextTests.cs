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
    [InlineData("Fn+P selected Turbo.", "Current mode: Turbo")]
    [InlineData("Fn+P selected Gaming.", "Current mode: Gaming")]
    [InlineData("Fn+P selected Creator.", "Current mode: Creator")]
    [InlineData("Fn+P selected Maximum Power.", "Current mode: Maximum Power")]
    [InlineData("Fn+P selected MaximumPower.", "Current mode: Maximum Power")]
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
    [InlineData("Fn+P selected Turbo.", PerformancePreset.Turbo)]
    [InlineData("Fn+P selected Gaming.", PerformancePreset.Gaming)]
    [InlineData("Fn+P selected Creator.", PerformancePreset.Creator)]
    [InlineData("Fn+P selected Maximum Power.", PerformancePreset.MaximumPower)]
    [InlineData("Fn+P selected MaximumPower.", PerformancePreset.MaximumPower)]
    public void SelectedPresetCanBeComparedWithOptimisticFeedback(
        string serviceMessage,
        PerformancePreset expected)
    {
        Assert.True(PerformanceShortcutNotificationText.TryGetSelectedPreset(
            serviceMessage,
            out PerformancePreset actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Fn+P selected Maximum  Power.")]
    [InlineData("Fn+P selected maximum power.")]
    [InlineData("Fn+P selected Max imumPower.")]
    public void UnrecognizedPresetSpellingIsNotNormalized(string message)
    {
        Assert.False(PerformanceShortcutNotificationText.TryGetSelectedPreset(message, out _));
        Assert.Equal(message, PerformanceShortcutNotificationText.FormatAppliedBody(message));
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
