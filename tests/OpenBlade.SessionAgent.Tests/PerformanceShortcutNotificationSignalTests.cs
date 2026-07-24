using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class PerformanceShortcutNotificationSignalTests
{
    [Theory]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Balanced)]
    [InlineData(PerformancePreset.Performance)]
    [InlineData(PerformancePreset.BatterySaver)]
    [InlineData(PerformancePreset.BatteryBalanced)]
    [InlineData(PerformancePreset.Custom)]
    public void CurrentModeSignal_RoundTrips(PerformancePreset preset)
    {
        var expected = new PerformanceShortcutNotificationSignal(
            PerformanceShortcutNotificationKind.CurrentMode,
            preset);

        Assert.True(PerformanceShortcutNotificationSignal.TryDecode(
            expected.Encode(),
            out PerformanceShortcutNotificationSignal actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FailedSignal_RoundTripsWithoutPreset()
    {
        var expected = new PerformanceShortcutNotificationSignal(
            PerformanceShortcutNotificationKind.Failed,
            Preset: null);

        Assert.True(PerformanceShortcutNotificationSignal.TryDecode(
            expected.Encode(),
            out PerformanceShortcutNotificationSignal actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(0x0000FF01u)]
    [InlineData(0x00000102u)]
    [InlineData(0x00010001u)]
    public void InvalidSignal_IsRejected(uint encoded)
    {
        Assert.False(PerformanceShortcutNotificationSignal.TryDecode(
            encoded,
            out _));
    }
}
