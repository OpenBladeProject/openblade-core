using System.Reflection;

namespace OpenBlade.Contracts;

public static class OpenBladeProductVersion
{
    public static string Read(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            string value = informational.Trim();
            int metadataSeparator = value.IndexOf('+');
            if (metadataSeparator < 0)
            {
                return value;
            }

            string metadata = value[(metadataSeparator + 1)..];
            if (metadata.Length > 8 && metadata.All(Uri.IsHexDigit))
            {
                return $"{value[..metadataSeparator]}+{metadata[..8].ToLowerInvariant()}";
            }

            return value;
        }

        return assembly.GetName().Version?.ToString(3) ?? "Unavailable";
    }
}
