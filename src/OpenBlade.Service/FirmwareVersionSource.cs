using System.ComponentModel;
using System.Globalization;
using System.Security;
using Microsoft.Win32;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public interface IFirmwareVersionSource
{
    FirmwareVersions Capture();
}

internal sealed record VideoFirmwareCandidate(string AdapterName, string BiosVersion);

public sealed class WindowsFirmwareVersionSource : IFirmwareVersionSource
{
    private const string BiosRegistryPath = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string VideoRegistryPath = @"SYSTEM\CurrentControlSet\Control\Video";
    private const string Unavailable = "Unavailable";

    private readonly Func<string, string, object?> readRegistryValue;
    private readonly Func<IReadOnlyList<VideoFirmwareCandidate>> readVideoFirmwareCandidates;
    private readonly Func<string?> readThunderboltFirmwareVersion;
    private readonly Func<string?> readTouchpadFirmwareVersion;

    public WindowsFirmwareVersionSource()
        : this(
            ReadRegistryValue,
            ReadVideoFirmwareCandidates,
            WindowsDeviceFirmwareVersionReader.ReadThunderboltFirmwareVersion,
            WindowsDeviceFirmwareVersionReader.ReadTouchpadFirmwareVersion)
    {
    }

    internal WindowsFirmwareVersionSource(
        Func<string, string, object?> readRegistryValue,
        Func<IReadOnlyList<VideoFirmwareCandidate>> readVideoFirmwareCandidates)
        : this(readRegistryValue, readVideoFirmwareCandidates, () => null, () => null)
    {
    }

    internal WindowsFirmwareVersionSource(
        Func<string, string, object?> readRegistryValue,
        Func<IReadOnlyList<VideoFirmwareCandidate>> readVideoFirmwareCandidates,
        Func<string?> readThunderboltFirmwareVersion,
        Func<string?> readTouchpadFirmwareVersion)
    {
        this.readRegistryValue = readRegistryValue
            ?? throw new ArgumentNullException(nameof(readRegistryValue));
        this.readVideoFirmwareCandidates = readVideoFirmwareCandidates
            ?? throw new ArgumentNullException(nameof(readVideoFirmwareCandidates));
        this.readThunderboltFirmwareVersion = readThunderboltFirmwareVersion
            ?? throw new ArgumentNullException(nameof(readThunderboltFirmwareVersion));
        this.readTouchpadFirmwareVersion = readTouchpadFirmwareVersion
            ?? throw new ArgumentNullException(nameof(readTouchpadFirmwareVersion));
    }

    public FirmwareVersions Capture()
    {
        VideoFirmwareCandidate? vbios = ReadVbiosFirmware();
        string modelNumber = ReadModelNumber();
        string biosVersion = ReadBiosVersion();
        string ecVersion = ReadEcVersion();
        bool isRz090581 = string.Equals(
            RazerBladeModelCatalog.Find(modelNumber)?.ModelNumber,
            RazerBladeModelCatalog.OpenBladeTargetModelNumber,
            StringComparison.OrdinalIgnoreCase);
        string thunderboltFirmwareVersion = isRz090581
            ? ReadFirmwareVersion(readThunderboltFirmwareVersion)
            : Unavailable;
        string touchpadFirmwareVersion = isRz090581
            ? ReadFirmwareVersion(readTouchpadFirmwareVersion)
            : Unavailable;
        return new(
            biosVersion,
            ecVersion,
            Unavailable,
            vbios?.BiosVersion ?? Unavailable,
            modelNumber,
            vbios?.AdapterName ?? Unavailable,
            thunderboltFirmwareVersion,
            touchpadFirmwareVersion,
            isRz090581
                ? DeviceFeatureAccess.ReadOnly
                : null,
            isRz090581
                ? DeviceFeatureAccess.ReadOnly
                : null);
    }

    private static string ReadFirmwareVersion(Func<string?> readVersion)
    {
        try
        {
            string? value = readVersion();
            if (string.IsNullOrWhiteSpace(value))
            {
                return Unavailable;
            }

            string normalized = value.Trim();
            return Version.TryParse(normalized, out _)
                ? normalized
                : Unavailable;
        }
        catch (Exception exception) when (IsSafeMetadataFailure(exception))
        {
            return Unavailable;
        }
    }

    private string ReadModelNumber()
    {
        try
        {
            string systemSku = FormatRegistryString(
                readRegistryValue(BiosRegistryPath, "SystemSKU"));
            if (systemSku == Unavailable)
            {
                return Unavailable;
            }

            return RazerBladeModelCatalog.Find(systemSku)?.ModelNumber
                ?? systemSku;
        }
        catch (Exception exception) when (IsSafeMetadataFailure(exception))
        {
            return Unavailable;
        }
    }

    private string ReadBiosVersion()
    {
        try
        {
            return FormatRegistryString(readRegistryValue(BiosRegistryPath, "BIOSVersion"));
        }
        catch (Exception exception) when (IsSafeMetadataFailure(exception))
        {
            return Unavailable;
        }
    }

    private string ReadEcVersion()
    {
        try
        {
            if (!TryConvertByte(
                    readRegistryValue(BiosRegistryPath, "ECFirmwareMajorRelease"),
                    out int major)
                || !TryConvertByte(
                    readRegistryValue(BiosRegistryPath, "ECFirmwareMinorRelease"),
                    out int minor))
            {
                return Unavailable;
            }

            return $"{major}.{minor:D2}";
        }
        catch (Exception exception) when (IsSafeMetadataFailure(exception))
        {
            return Unavailable;
        }
    }

    private VideoFirmwareCandidate? ReadVbiosFirmware()
    {
        try
        {
            VideoFirmwareCandidate[] nvidiaCandidates = readVideoFirmwareCandidates()
                .Where(candidate =>
                    candidate.AdapterName.Contains(
                        "NVIDIA",
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (nvidiaCandidates.Length != 1)
            {
                return null;
            }

            VideoFirmwareCandidate nvidia = nvidiaCandidates[0];
            if (string.IsNullOrWhiteSpace(nvidia.BiosVersion))
            {
                return null;
            }

            string value = nvidia.BiosVersion.Trim();
            string normalized = value.StartsWith("Version", StringComparison.OrdinalIgnoreCase)
                ? value["Version".Length..].Trim()
                : value;
            return string.IsNullOrWhiteSpace(normalized)
                ? null
                : nvidia with
                {
                    AdapterName = nvidia.AdapterName.Trim(),
                    BiosVersion = normalized,
                };
        }
        catch (Exception exception) when (IsSafeMetadataFailure(exception))
        {
            return null;
        }
    }

    private static bool TryConvertByte(object? value, out int result)
    {
        if (value is null)
        {
            result = 0;
            return false;
        }

        try
        {
            result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return result is >= byte.MinValue and <= byte.MaxValue;
        }
        catch (Exception exception) when (exception is
            FormatException or InvalidCastException or OverflowException)
        {
            result = 0;
            return false;
        }
    }

    private static string FormatRegistryString(object? value) => value switch
    {
        string text when !string.IsNullOrWhiteSpace(text) => text.Trim(),
        string[] values when values.Any(item => !string.IsNullOrWhiteSpace(item)) =>
            string.Join(" ", values.Where(item => !string.IsNullOrWhiteSpace(item))),
        _ => Unavailable,
    };

    private static object? ReadRegistryValue(string path, string name)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable: false);
        return key?.GetValue(name);
    }

    private static IReadOnlyList<VideoFirmwareCandidate> ReadVideoFirmwareCandidates()
    {
        var candidates = new List<VideoFirmwareCandidate>();
        using RegistryKey? videoRoot = Registry.LocalMachine.OpenSubKey(
            VideoRegistryPath,
            writable: false);
        if (videoRoot is null)
        {
            return candidates;
        }

        foreach (string adapterKeyName in videoRoot.GetSubKeyNames())
        {
            using RegistryKey? adapterKey = videoRoot.OpenSubKey(adapterKeyName, writable: false);
            if (adapterKey is null)
            {
                continue;
            }

            foreach (string instanceKeyName in adapterKey.GetSubKeyNames())
            {
                using RegistryKey? instanceKey = adapterKey.OpenSubKey(
                    instanceKeyName,
                    writable: false);
                if (instanceKey?.GetValue("HardwareInformation.AdapterString") is not string adapter
                    || instanceKey.GetValue("HardwareInformation.BiosString") is not string bios)
                {
                    continue;
                }

                candidates.Add(new VideoFirmwareCandidate(adapter, bios));
            }
        }

        return candidates;
    }

    private static bool IsSafeMetadataFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or SecurityException or Win32Exception;
}
