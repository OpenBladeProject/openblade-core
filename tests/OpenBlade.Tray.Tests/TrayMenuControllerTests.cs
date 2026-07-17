using System.Windows.Input;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class TrayMenuControllerTests
{
    [Fact]
    public void Items_DelegateToExactSharedCommandsAndTrayActions()
    {
        var performance = new RecordingCommand();
        var fans = new RecordingCommand();
        var fullCharge = new RecordingCommand();
        var clearOverrides = new RecordingCommand();
        int opens = 0;
        int exits = 0;
        var controller = new TrayMenuController(
            performance,
            fans,
            fullCharge,
            clearOverrides,
            () => opens++,
            () => exits++);

        foreach (TrayMenuItem item in controller.Items)
        {
            item.Invoke();
        }

        Assert.Equal(
            ["Balanced", "Silent", "Performance", "Balanced", "Battery Saver", "Balanced", "Automatic", "Maximum", "Automatic", "Maximum", "Automatic", "Toggle charge to 100%", "Clear manual overrides", "Open", "Exit"],
            controller.Items.Select(item => item.Label));
        Assert.Equal(
            [PerformancePreset.Balanced, PerformancePreset.Silent, PerformancePreset.Performance],
            performance.Parameters);
        Assert.Equal([FanMode.Automatic, FanMode.Maximum], fans.Parameters);
        Assert.Equal([null], fullCharge.Parameters);
        Assert.Equal([null], clearOverrides.Parameters);
        Assert.Equal(1, opens);
        Assert.Equal(1, exits);
    }

    [Fact]
    public void PerformanceItems_TrackActivePowerSource()
    {
        PowerSource source = PowerSource.PluggedIn;
        var enabled = new RecordingCommand();
        var controller = new TrayMenuController(
            enabled,
            enabled,
            enabled,
            enabled,
            () => { },
            () => { },
            () => source);

        Assert.Equal(
            ["Balanced", "Silent", "Performance"],
            controller.Items
                .Where(item => item.Group == TrayMenuGroup.Performance && item.IsVisible)
                .Select(item => item.Label));
        Assert.Equal(
            ["Automatic", "Maximum"],
            controller.Items
                .Where(item => item.Group == TrayMenuGroup.Fans && item.IsVisible)
                .Select(item => item.Label));

        source = PowerSource.Battery;

        Assert.Equal(
            ["Balanced", "Battery Saver"],
            controller.Items
                .Where(item => item.Group == TrayMenuGroup.Performance && item.IsVisible)
                .Select(item => item.Label));
        Assert.Equal(
            ["Automatic", "Maximum"],
            controller.Items
                .Where(item => item.Group == TrayMenuGroup.Fans && item.IsVisible)
                .Select(item => item.Label));

        source = PowerSource.UsbC;

        Assert.Equal(
            ["Balanced"],
            controller.Items
                .Where(item => item.Group == TrayMenuGroup.Performance && item.IsVisible)
                .Select(item => item.Label));
        Assert.Equal(
            ["Automatic"],
            controller.Items
                .Where(item => item.Group == TrayMenuGroup.Fans && item.IsVisible)
                .Select(item => item.Label));

        source = PowerSource.ExternalUnknown;

        Assert.DoesNotContain(controller.Items, item =>
            item.Group is TrayMenuGroup.Performance or TrayMenuGroup.Fans
            && item.IsVisible);
    }

    [Fact]
    public void MutationAvailabilityTracksCommandsWhileOpenAndExitStayEnabled()
    {
        var disabled = new RecordingCommand(_ => false);
        var controller = new TrayMenuController(
            disabled,
            disabled,
            disabled,
            disabled,
            () => { },
            () => { });

        Assert.All(
            controller.Items.Where(item => item.IsMutation),
            item => Assert.False(item.IsEnabled));
        Assert.True(controller.Items.Single(item => item.Label == "Open").IsEnabled);
        Assert.True(controller.Items.Single(item => item.Label == "Exit").IsEnabled);
    }

    [Fact]
    public void ExitInvokesOnlyTrayShutdownAction()
    {
        var mutations = new RecordingCommand();
        int opens = 0;
        int exits = 0;
        var controller = new TrayMenuController(
            mutations,
            mutations,
            mutations,
            mutations,
            () => opens++,
            () => exits++);

        controller.Items.Single(item => item.Label == "Exit").Invoke();

        Assert.Equal(1, exits);
        Assert.Equal(0, opens);
        Assert.Empty(mutations.Parameters);
    }

    private sealed class RecordingCommand(Predicate<object?>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
        public List<object?> Parameters { get; } = [];
        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => Parameters.Add(parameter);
    }
}
