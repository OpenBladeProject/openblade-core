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
    public void KnownShortcutConfirmationEmitsClosedDiagnosticAndRoutes()
    {
        (Blade18DefaultFnAction Action, Blade18FnShortcut Shortcut)[] expected =
        [
            (Blade18DefaultFnAction.Performance, Blade18FnShortcut.Performance),
            (Blade18DefaultFnAction.RefreshRate, Blade18FnShortcut.RefreshRate),
            (Blade18DefaultFnAction.Touchpad, Blade18FnShortcut.Touchpad),
        ];
        foreach ((Blade18DefaultFnAction action, Blade18FnShortcut shortcut) in expected)
        {
            var diagnostics = new List<Blade18FnDiagnostic>();
            var routed = new List<(Blade18DefaultFnInputEvent Input, bool Extended)>();
            var input = new Blade18DefaultFnInputEvent(
                0x19,
                action,
                FunctionRowInputPhase.Up);

            WindowsReactiveKeyboardSource.EmitAndRouteConfirmedBlade18Shortcut(
                input,
                extended: true,
                diagnostics.Add,
                (item, extended) => routed.Add((item, extended)));

            Assert.Equal(
                new Blade18FnDiagnostic(
                    Blade18FnDiagnosticStage.HookRawInputConfirmed,
                    shortcut,
                    FunctionRowInputPhase.Up),
                Assert.Single(diagnostics));
            Assert.Equal((input, true), Assert.Single(routed));
        }
    }

    [Fact]
    public void UnsupportedShortcutDoesNotEmitButStillRoutes()
    {
        var diagnostics = new List<Blade18FnDiagnostic>();
        var routed = new List<Blade18DefaultFnInputEvent>();
        var input = new Blade18DefaultFnInputEvent(
            0x20,
            Blade18DefaultFnAction.VolumeMute,
            FunctionRowInputPhase.Down);

        WindowsReactiveKeyboardSource.EmitAndRouteConfirmedBlade18Shortcut(
            input,
            extended: false,
            diagnostics.Add,
            (item, _) => routed.Add(item));

        Assert.Empty(diagnostics);
        Assert.Equal(input, Assert.Single(routed));
    }

    [Fact]
    public void ThrowingDiagnosticSinkDoesNotPreventConfirmedRoute()
    {
        var routed = new List<Blade18DefaultFnInputEvent>();
        var input = new Blade18DefaultFnInputEvent(
            0x19,
            Blade18DefaultFnAction.Performance,
            FunctionRowInputPhase.Down);

        WindowsReactiveKeyboardSource.EmitAndRouteConfirmedBlade18Shortcut(
            input,
            extended: false,
            _ => throw new InvalidOperationException("private"),
            (item, _) => routed.Add(item));

        Assert.Equal(input, Assert.Single(routed));
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
