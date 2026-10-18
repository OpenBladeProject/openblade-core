using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class Blade18FnHookCorrelationTests
{
    [Fact]
    public void ConsumeReturnsExactDeferredEventOnce()
    {
        var correlation = new Blade18FnHookCorrelation();
        var expected = new Blade18DefaultFnInputEvent(
            0x19,
            Blade18DefaultFnAction.Performance,
            FunctionRowInputPhase.Down);
        correlation.Record(0x19, extended: false, keyDown: true, 100, expected);

        Assert.True(correlation.TryConsume(
            0x19,
            extended: false,
            keyDown: true,
            100,
            out Blade18DefaultFnInputEvent? actual));
        Assert.Equal(expected, actual);
        Assert.False(correlation.TryConsume(0x19, false, true, 100, out _));
    }

    [Fact]
    public void ConsumeMatchesExtendedDirectionAndMessageTime()
    {
        var correlation = new Blade18FnHookCorrelation();
        correlation.Record(0x48, extended: true, keyDown: false, 100, null);

        Assert.False(correlation.TryConsume(0x48, false, false, 100, out _));
        Assert.False(correlation.TryConsume(0x48, true, true, 100, out _));
        Assert.False(correlation.TryConsume(0x48, true, false, 101, out _));
        Assert.True(correlation.TryConsume(
            0x48,
            extended: true,
            keyDown: false,
            100,
            out Blade18DefaultFnInputEvent? inputEvent));
        Assert.Null(inputEvent);
    }

    [Fact]
    public void RecordOverflowClearsUnconfirmedActionsAndReportsFailure()
    {
        var correlation = new Blade18FnHookCorrelation();
        for (uint index = 0;
             index < Blade18FnHookCorrelation.MaximumRecentEvents + 4;
             index++)
        {
            bool accepted = correlation.Record(index, false, true, index, null);
            Assert.Equal(index != Blade18FnHookCorrelation.MaximumRecentEvents, accepted);
        }

        Assert.False(correlation.TryConsume(0, false, true, 0, out _));
        Assert.False(correlation.TryConsume(4, false, true, 4, out _));
        Assert.True(correlation.TryConsume(67, false, true, 67, out _));
    }

    [Fact]
    public void ClearDropsDeferredActions()
    {
        var correlation = new Blade18FnHookCorrelation();
        correlation.Record(
            0x13,
            false,
            true,
            100,
            new(0x13, Blade18DefaultFnAction.RefreshRate, FunctionRowInputPhase.Down));

        correlation.Clear();

        Assert.False(correlation.TryConsume(0x13, false, true, 100, out _));
    }
}
