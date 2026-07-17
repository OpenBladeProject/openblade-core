using System.Windows.Input;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public enum TrayMenuGroup
{
    Performance,
    Fans,
    Battery,
    Overrides,
    Application,
}

public sealed class TrayMenuItem
{
    private readonly ICommand? command;
    private readonly object? parameter;
    private readonly Action? action;
    private readonly Func<bool>? isVisible;

    internal TrayMenuItem(
        string label,
        TrayMenuGroup group,
        bool isMutation,
        ICommand? command = null,
        object? parameter = null,
        Action? action = null,
        Func<bool>? isVisible = null)
    {
        Label = label;
        Group = group;
        IsMutation = isMutation;
        this.command = command;
        this.parameter = parameter;
        this.action = action;
        this.isVisible = isVisible;
    }

    public string Label { get; }

    public TrayMenuGroup Group { get; }

    public bool IsMutation { get; }

    public bool IsVisible => isVisible?.Invoke() ?? true;

    public bool IsEnabled => IsVisible
        && (action is not null || command?.CanExecute(parameter) == true);

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
            viewModel.ClearManualOverridesCommand,
            openTray,
            exitTray,
            () => viewModel.ActivePowerSource)
    {
    }

    public TrayMenuController(
        ICommand setPerformance,
        ICommand setFans,
        ICommand toggleFullCharge,
        ICommand clearManualOverrides,
        Action openTray,
        Action exitTray,
        Func<PowerSource>? activePowerSource = null)
    {
        ArgumentNullException.ThrowIfNull(setPerformance);
        ArgumentNullException.ThrowIfNull(setFans);
        ArgumentNullException.ThrowIfNull(toggleFullCharge);
        ArgumentNullException.ThrowIfNull(clearManualOverrides);
        ArgumentNullException.ThrowIfNull(openTray);
        ArgumentNullException.ThrowIfNull(exitTray);
        activePowerSource ??= static () => PowerSource.PluggedIn;

        Items =
        [
            Mutation(
                PerformancePresetPresentation.Label(PerformancePreset.Balanced),
                TrayMenuGroup.Performance,
                setPerformance,
                PerformancePreset.Balanced,
                () => activePowerSource() == PowerSource.PluggedIn),
            Mutation(
                PerformancePresetPresentation.Label(PerformancePreset.Silent),
                TrayMenuGroup.Performance,
                setPerformance,
                PerformancePreset.Silent,
                () => activePowerSource() == PowerSource.PluggedIn),
            Mutation(
                PerformancePresetPresentation.Label(PerformancePreset.Performance),
                TrayMenuGroup.Performance,
                setPerformance,
                PerformancePreset.Performance,
                () => activePowerSource() == PowerSource.PluggedIn),
            Mutation(
                PerformancePresetPresentation.Label(PerformancePreset.BatteryBalanced),
                TrayMenuGroup.Performance,
                setPerformance,
                PerformancePreset.BatteryBalanced,
                () => activePowerSource() == PowerSource.Battery),
            Mutation(
                PerformancePresetPresentation.Label(PerformancePreset.BatterySaver),
                TrayMenuGroup.Performance,
                setPerformance,
                PerformancePreset.BatterySaver,
                () => activePowerSource() == PowerSource.Battery),
            Mutation(
                PerformancePresetPresentation.Label(PerformancePreset.Balanced),
                TrayMenuGroup.Performance,
                setPerformance,
                PerformancePreset.Balanced,
                () => activePowerSource() == PowerSource.UsbC),
            Mutation(
                "Automatic",
                TrayMenuGroup.Fans,
                setFans,
                FanMode.Automatic,
                () => activePowerSource() == PowerSource.PluggedIn),
            Mutation(
                "Maximum",
                TrayMenuGroup.Fans,
                setFans,
                FanMode.Maximum,
                () => activePowerSource() == PowerSource.PluggedIn),
            Mutation(
                "Automatic",
                TrayMenuGroup.Fans,
                setFans,
                FanMode.Automatic,
                () => activePowerSource() == PowerSource.Battery),
            Mutation(
                "Maximum",
                TrayMenuGroup.Fans,
                setFans,
                FanMode.Maximum,
                () => activePowerSource() == PowerSource.Battery),
            Mutation(
                "Automatic",
                TrayMenuGroup.Fans,
                setFans,
                FanMode.Automatic,
                () => activePowerSource() == PowerSource.UsbC),
            Mutation("Toggle charge to 100%", TrayMenuGroup.Battery, toggleFullCharge, null),
            Mutation("Clear manual overrides", TrayMenuGroup.Overrides, clearManualOverrides, null),
            new TrayMenuItem("Open", TrayMenuGroup.Application, false, action: openTray),
            new TrayMenuItem("Exit", TrayMenuGroup.Application, false, action: exitTray),
        ];
    }

    public IReadOnlyList<TrayMenuItem> Items { get; }

    public TrayMenuItem Find(string label) =>
        Items.Single(item => item.Label == label && item.IsVisible);

    private static TrayMenuItem Mutation(
        string label,
        TrayMenuGroup group,
        ICommand command,
        object? parameter,
        Func<bool>? isVisible = null) =>
        new(label, group, true, command, parameter, isVisible: isVisible);
}
