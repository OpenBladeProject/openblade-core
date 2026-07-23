using System.Diagnostics;

namespace OpenBlade.Tray;

internal static class InstalledComponentVersion
{
    internal static string ReadSibling(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path))
            {
                return "Unavailable";
            }

            return Format(FileVersionInfo.GetVersionInfo(path).ProductVersion);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return "Unavailable";
        }
    }

    internal static string Format(string? productVersion)
    {
        if (string.IsNullOrWhiteSpace(productVersion))
        {
            return "Unavailable";
        }

        string value = productVersion.Trim();
        int metadataSeparator = value.IndexOf('+');
        if (metadataSeparator < 0)
        {
            return value;
        }

        string metadata = value[(metadataSeparator + 1)..];
        return metadata.Length > 8 && metadata.All(Uri.IsHexDigit)
            ? $"{value[..metadataSeparator]}+{metadata[..8].ToLowerInvariant()}"
            : value;
    }
}
