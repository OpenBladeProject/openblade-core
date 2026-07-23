using System.Management;

namespace OpenBlade.SessionAgent;

/// <summary>
/// Adjusts the built-in panel through Windows' monitor-brightness WMI provider.
/// The provider supplies the display's discrete supported levels, so OpenBlade
/// does not hardcode a percentage step that the panel may reject.
/// </summary>
public sealed class WmiDisplayBrightnessController : IDisplayBrightnessController
{
    private const string WmiNamespace = @"root\wmi";

    public Task<bool> AdjustAsync(
        int direction,
        CancellationToken cancellationToken)
    {
        if (direction is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        return Task.Run(
            () => AdjustCore(direction, cancellationToken),
            cancellationToken);
    }

    public static byte? SelectAdjacentLevel(
        byte current,
        IEnumerable<byte> supportedLevels,
        int direction)
    {
        if (supportedLevels is null)
        {
            throw new ArgumentNullException(nameof(supportedLevels));
        }
        if (direction is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        byte[] levels = supportedLevels
            .Distinct()
            .Order()
            .ToArray();
        return direction < 0
            ? levels.Where(level => level < current).Select<byte, byte?>(level => level).LastOrDefault()
            : levels.Where(level => level > current).Select<byte, byte?>(level => level).FirstOrDefault();
    }

    private static bool AdjustCore(
        int direction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var brightnessSearch = new ManagementObjectSearcher(
            WmiNamespace,
            "SELECT InstanceName, CurrentBrightness, Level FROM WmiMonitorBrightness WHERE Active = TRUE");
        using ManagementObjectCollection brightnessResults = brightnessSearch.Get();
        foreach (ManagementBaseObject result in brightnessResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result["InstanceName"] is not string instanceName
                || result["CurrentBrightness"] is not byte current
                || result["Level"] is not byte[] levels
                || SelectAdjacentLevel(current, levels, direction) is not { } target)
            {
                continue;
            }

            using var methodsSearch = new ManagementObjectSearcher(
                WmiNamespace,
                "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
            using ManagementObjectCollection methodResults = methodsSearch.Get();
            foreach (ManagementObject methods in methodResults.Cast<ManagementObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(
                    methods["InstanceName"] as string,
                    instanceName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using ManagementBaseObject input =
                    methods.GetMethodParameters("WmiSetBrightness");
                input["Timeout"] = 0u;
                input["Brightness"] = target;
                using ManagementBaseObject? output =
                    methods.InvokeMethod("WmiSetBrightness", input, null);
                uint returnValue = output?["ReturnValue"] is uint value ? value : 0u;
                if (returnValue != 0)
                {
                    throw new ManagementException(
                        $"Windows rejected display brightness {target}% with status {returnValue}.");
                }
                return true;
            }
        }

        return false;
    }
}
