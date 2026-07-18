using OpenBlade.Contracts;

namespace OpenBlade.Session;

public readonly record struct DisplayPowerContext(PowerSource PowerSource, long Generation)
{
    public bool IsConfirmed => PowerSource is
        PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC;
}

public interface IDisplayPowerContextSource
{
    DisplayPowerContext Capture();
}

public sealed class DisplayPowerContextState : IDisplayPowerContextSource
{
    private readonly object sync = new();
    private DisplayPowerContext current = new(PowerSource.ExternalUnknown, 0);

    public DisplayPowerContext Capture()
    {
        lock (sync)
        {
            return current;
        }
    }

    public void Update(PowerSource powerSource, long generation)
    {
        if (!Enum.IsDefined(powerSource))
        {
            throw new ArgumentOutOfRangeException(nameof(powerSource));
        }
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        lock (sync)
        {
            current = new DisplayPowerContext(powerSource, generation);
        }
    }
}
