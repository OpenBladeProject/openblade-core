using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>A detached editor. Opening or changing it never applies device settings.</summary>
public sealed class ApplicationProfileDraft
{
    private readonly Dictionary<PowerSource, ApplicationPowerProfileOverride> overrides = [];

    public ApplicationProfileDraft(ApplicationProfileDefinition? existing = null)
    {
        Id = existing?.Id ?? Guid.NewGuid();
        Name = existing?.Name ?? string.Empty;
        ExecutablePath = existing?.ExecutablePath ?? string.Empty;
        ActivationMode = existing?.ActivationMode ?? ApplicationActivationMode.Foreground;
        Enabled = existing?.Enabled ?? true;
        foreach (PowerSource source in Sources)
        {
            overrides[source] = existing?.Overrides.ForPowerSource(source) ?? new();
        }
    }

    public static IReadOnlyList<PowerSource> Sources { get; } =
        [PowerSource.PluggedIn, PowerSource.Battery, PowerSource.UsbC];

    public Guid Id { get; }
    public string Name { get; set; }
    public string ExecutablePath { get; set; }
    public ApplicationActivationMode ActivationMode { get; set; }
    public bool Enabled { get; set; }

    public ApplicationPowerProfileOverride ForSource(PowerSource source) => overrides[source];

    public void SetSource(PowerSource source, ApplicationPowerProfileOverride value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!overrides.ContainsKey(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }
        overrides[source] = value;
    }

    public ApplicationProfileDefinition Build()
    {
        string name = Name.Trim();
        if (name.Length is 0 or > ApplicationProfileDefinition.MaximumNameLength
            || name.Any(char.IsControl))
        {
            throw new ArgumentException(TrayText.Get("Enter a name of up to 80 characters."));
        }
        if (!Enum.IsDefined(ActivationMode))
        {
            throw new ArgumentException(TrayText.Get("Choose an application activation mode."));
        }

        ApplicationPowerProfileOverride? pluggedIn = ConfiguredOrNull(
            overrides[PowerSource.PluggedIn]);
        ApplicationPowerProfileOverride? battery = ConfiguredOrNull(
            overrides[PowerSource.Battery]);
        ApplicationPowerProfileOverride? usbC = ConfiguredOrNull(
            overrides[PowerSource.UsbC]);
        if (pluggedIn is null && battery is null && usbC is null)
        {
            throw new ArgumentException(
                TrayText.Get("Choose at least one application profile setting."));
        }

        return new(Id, name, ApplicationProfileDefinition.NormalizeExecutablePath(ExecutablePath),
            ActivationMode, new()
            {
                PluggedIn = pluggedIn,
                Battery = battery,
                UsbC = usbC,
            }, Enabled);
    }

    private static ApplicationPowerProfileOverride? ConfiguredOrNull(
        ApplicationPowerProfileOverride value) => value.IsConfigured ? value : null;
}
