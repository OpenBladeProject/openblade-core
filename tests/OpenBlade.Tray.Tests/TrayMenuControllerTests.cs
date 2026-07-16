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
            ["Silent", "Balanced", "Performance", "Automatic", "Maximum", "Toggle charge to 100%", "Clear manual overrides", "Open", "Exit"],
            controller.Items.Select(item => item.Label));
        Assert.Equal(
            [PerformancePreset.Silent, PerformancePreset.Balanced, PerformancePreset.Performance],
            performance.Parameters);
        Assert.Equal([FanMode.Automatic, FanMode.Maximum], fans.Parameters);
        Assert.Equal([null], fullCharge.Parameters);
        Assert.Equal([null], clearOverrides.Parameters);
        Assert.Equal(1, opens);
        Assert.Equal(1, exits);
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
