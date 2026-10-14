using System.IO;
using System.IO.Compression;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public static class UnsupportedLaptopReport
{
    public const string SuggestedFileName = "OpenBlade-identity-report.zip";

    private const string VersionsEntryName = "versions.json";
    private const string DeviceVerificationEntryName = "device-verification.json";

    public static Uri IssueFormUri { get; } = new(
        "https://github.com/OpenBladeProject/openblade-core/issues/new?template=device-support.yml");

    public static IReadOnlyList<string> IncludedEntries { get; } =
    [
        VersionsEntryName,
        DeviceVerificationEntryName,
    ];

    public static DiagnosticArchive Create(DiagnosticArchive diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        using var output = new MemoryStream();
        bool hasDeviceVerification = false;
        try
        {
            using var source = new MemoryStream(diagnostics.ZipBytes, writable: false);
            using var input = new ZipArchive(source, ZipArchiveMode.Read);
            using var report = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (string name in IncludedEntries)
            {
                // Every other entry is dropped on purpose: device.json carries a
                // serial-number field, and logs, configuration, and command history
                // are more than an identity report needs.
                ZipArchiveEntry? entry = input.GetEntry(name);
                if (entry is null)
                {
                    continue;
                }

                ZipArchiveEntry copy = report.CreateEntry(name, CompressionLevel.Optimal);
                using Stream from = entry.Open();
                using Stream to = copy.Open();
                from.CopyTo(to);
                hasDeviceVerification |= name == DeviceVerificationEntryName;
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            throw new InvalidDataException(
                "The diagnostic archive could not be read.",
                exception);
        }

        if (!hasDeviceVerification)
        {
            throw new InvalidDataException(
                "The diagnostic archive has no device verification entry.");
        }

        return new DiagnosticArchive(SuggestedFileName, output.ToArray());
    }
}
