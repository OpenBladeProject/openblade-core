using OpenBlade.Contracts;

namespace OpenBlade.Tray;

internal static class PowerSourcePresentation
{
    public static string Label(PowerSource value) => value switch
    {
        PowerSource.PluggedIn => "AC",
        PowerSource.Battery => TrayText.Get("Battery"),
        PowerSource.UsbC => "USB-C",
        PowerSource.ExternalUnknown => TrayText.Get("External power (unclassified)"),
        _ => TrayText.Get("Power source unavailable"),
    };

    public static string ProfileLabel(PowerSource value) => value switch
    {
        PowerSource.PluggedIn => TrayText.Get("AC profile"),
        PowerSource.Battery => TrayText.Get("Battery profile"),
        PowerSource.UsbC => TrayText.Get("USB-C profile"),
        PowerSource.ExternalUnknown => Label(value),
        _ => TrayText.Get("Power source unavailable"),
    };

    public static string TroubleshootingLabel(PowerSource value) => value switch
    {
        PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC
            or PowerSource.ExternalUnknown => Label(value),
        _ => TrayText.Get("Unavailable"),
    };
}
