using System.Windows.Input;
using OpenSynapse.Contracts;

namespace OpenSynapse.Tray;

public enum TrayMenuGroup
{
    Performance,
    Fans,
    Battery,
    Application,
}

public sealed class TrayMenuItem
{
    private readonly ICommand? command;
    private readonly object? parameter;
    private readonly Action? action;

    internal TrayMenuItem(
        string label,
        TrayMenuGroup group,
        bool isMutation,
        ICommand? command = null,
        object? parameter = null,
        Action? action = null)
    {
        Label = label;
        Group = group;
        IsMutation = isMutation;
        this.command = command;
        this.parameter = parameter;
        this.action = action;
    }

    public string Label { get; }

    public TrayMenuGroup Group { get; }

    public bool IsMutation { get; }

    public bool IsEnabled => action is not null || command?.CanExecute(parameter) == true;

    public void Invoke()
    {
        if (!IsEnabled)
        {
            return;
        }

        if (action is not null)
        {
            action();
        }
        else
        {
            command!.Execute(parameter);
        }
    }
}

public sealed class TrayMenuController
{
    public TrayMenuController(
        MainViewModel viewModel,
        Action openTray,
        Action exitTray)
        : this(
            viewModel?.SetPerformanceCommand
                ?? throw new ArgumentNullException(nameof(viewModel)),
            viewModel.SetFanModeCommand,
            viewModel.ToggleFullChargeCommand,
            openTray,
            exitTray)
    {
    }

    public TrayMenuController(
        ICommand setPerformance,
        ICommand setFans,
        ICommand toggleFullCharge,
        Action openTray,
        Action exitTray)
    {
        ArgumentNullException.ThrowIfNull(setPerformance);
        ArgumentNullException.ThrowIfNull(setFans);
        ArgumentNullException.ThrowIfNull(toggleFullCharge);
        ArgumentNullException.ThrowIfNull(openTray);
        ArgumentNullException.ThrowIfNull(exitTray);

        Items =
        [
            Mutation("Silent", TrayMenuGroup.Performance, setPerformance, PerformancePreset.Silent),
            Mutation("Balanced", TrayMenuGroup.Performance, setPerformance, PerformancePreset.Balanced),
            Mutation("Performance", TrayMenuGroup.Performance, setPerformance, PerformancePreset.Performance),
            Mutation("Automatic", TrayMenuGroup.Fans, setFans, FanMode.Automatic),
            Mutation("Maximum", TrayMenuGroup.Fans, setFans, FanMode.Maximum),
            Mutation("Toggle charge to 100%", TrayMenuGroup.Battery, toggleFullCharge, null),
            new TrayMenuItem("Open", TrayMenuGroup.Application, false, action: openTray),
            new TrayMenuItem("Exit", TrayMenuGroup.Application, false, action: exitTray),
        ];
    }

    public IReadOnlyList<TrayMenuItem> Items { get; }

    public TrayMenuItem Find(string label) => Items.Single(item => item.Label == label);

    private static TrayMenuItem Mutation(
        string label,
        TrayMenuGroup group,
        ICommand command,
        object? parameter) =>
        new(label, group, true, command, parameter);
}
