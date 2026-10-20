using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace OpenBlade.Platform;

public sealed record RazerDriverApiPackageEvidence(bool Installed, bool Trusted, string? Identity, string? Failure = null)
{
    public static RazerDriverApiPackageEvidence Missing { get; } = new(false, false, null);
    public static RazerDriverApiPackageEvidence Rejected { get; } = new(true, false, null);
}

/// <summary>Read-only binding of a Razer API endpoint to its installed, catalog-trusted package.</summary>
public static class WindowsRazerDriverApiDiscovery
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, long> TrustCache = new(StringComparer.Ordinal);

    public static RazerDriverApiPackageEvidence Capture(string? instanceId, string service, string hardwareId)
    {
        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? serviceKey = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service);
            if (serviceKey is null)
            {
                return RazerDriverApiPackageEvidence.Missing;
            }
            if (serviceKey.GetValue("Type") is not int serviceType || serviceType != 1)
            {
                return RazerDriverApiPackageEvidence.Rejected;
            }
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string image = ResolveServiceImage(serviceKey.GetValue("ImagePath", null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string, windows, service);
            if (!File.Exists(image))
            {
                return RazerDriverApiPackageEvidence.Missing;
            }
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return RazerDriverApiPackageEvidence.Rejected;
            }
            using RegistryKey? device = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId);
            string driverKey = device?.GetValue("Driver") as string
                ?? throw new InvalidDataException("The endpoint has no installed driver package.");
            if (driverKey.Contains("..", StringComparison.Ordinal) || driverKey.StartsWith('\\'))
            {
                throw new InvalidDataException("Invalid installed driver key.");
            }
            using RegistryKey? driver = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
            string infName = driver?.GetValue("InfPath") as string
                ?? throw new InvalidDataException("The installed driver INF is unavailable.");
            RequireFileName(infName);
            string publishedInf = Path.Combine(windows, "INF", infName);
            var buffer = new StringBuilder(32768);
            if (!SetupGetInfDriverStoreLocation(publishedInf, 0, null, buffer, buffer.Capacity, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            string storeInf = Path.GetFullPath(buffer.ToString());
            string storeRoot = Path.Combine(windows, "System32", "DriverStore", "FileRepository");
            RequireUnder(storeInf, storeRoot);
            string root = Path.GetDirectoryName(storeInf)!;
            // Lock the published and store INFs while selecting their catalog and declarations.
            using var published = OpenProtectedFile(publishedInf);
            using var inf = OpenProtectedFile(storeInf);
            string publishedIdentity = Hash(published);
            if (!string.Equals(publishedIdentity, Hash(inf), StringComparison.Ordinal))
            {
                throw new InvalidDataException("The installed INF differs from its driver-store package.");
            }
            inf.Position = 0;
            using var reader = new StreamReader(inf, Encoding.UTF8, true, 4096, leaveOpen: true);
            string catalogName = ValidateInfContract(reader.ReadToEnd(), service, hardwareId);
            string catalogPath = Path.Combine(root, catalogName);
            string storeImage = Path.Combine(root, service + ".sys");
            using var catalog = OpenProtectedFile(catalogPath);
            using var stored = OpenProtectedFile(storeImage);
            using var active = OpenProtectedFile(image);
            string activeIdentity = Hash(active);
            if (!string.Equals(activeIdentity, Hash(stored), StringComparison.Ordinal))
            {
                throw new InvalidDataException("The active driver image differs from its installed package.");
            }
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
                instanceId.ToUpperInvariant(), service.ToUpperInvariant(), hardwareId.ToUpperInvariant(),
                image.ToUpperInvariant(), storeInf.ToUpperInvariant(), publishedIdentity, activeIdentity, Hash(catalog)))));
            lock (CacheLock)
            {
                long now = Environment.TickCount64;
                if (!TrustCache.TryGetValue(identity, out long verifiedAt) || now - verifiedAt >= 60_000)
                {
                    _ = WindowsDriverApiTrust.VerifyCatalogMembers(catalogPath, [storeInf, storeImage, image],
                        [WindowsDriverApiTrust.MicrosoftHardwareCompatibilityPublisher]);
                    if (TrustCache.Count >= 16)
                    {
                        TrustCache.Clear();
                    }
                    TrustCache[identity] = now;
                }
            }
            return new(true, true, identity);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or Win32Exception or CryptographicException)
        {
            return new(true, false, null, exception.Message);
        }
    }

    internal static string ResolveServiceImage(string? value, string windows, string service)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("The driver service image is unavailable.");
        }
        string path = value.Trim();
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(windows, path[12..]);
        }
        else if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(windows, path);
        }
        else if (path.StartsWith(@"%SystemRoot%\", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(windows, path[13..]);
        }
        path = Path.GetFullPath(path);
        RequireUnder(path, Path.Combine(windows, "System32", "drivers"));
        if (!string.Equals(Path.GetFileName(path), service + ".sys", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The service image is not the selected Razer API driver.");
        }
        return path;
    }

    // Parse only the installed x64 model's explicit service declaration. Unsupported INF
    // indirection fails closed; a filename, provider string or unrelated signed model is insufficient.
    internal static string ValidateInfContract(string text, string service, string hardwareId)
    {
        if (text.Length > 1_048_576)
        {
            throw new InvalidDataException("The driver INF exceeds the inspection bound.");
        }
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? section = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Split(';', 2)[0].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string name = line[1..^1];
                if (!sections.TryAdd(name, section = []))
                {
                    throw new InvalidDataException("Duplicate driver INF section.");
                }
            }
            else if (line.Length > 0)
            {
                section?.Add(line);
            }
        }
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in sections.GetValueOrDefault("Strings") ?? [])
        {
            string[] pair = line.Split('=', 2);
            if (pair.Length == 2)
            {
                strings[pair[0].Trim()] = pair[1].Trim().Trim('"');
            }
        }
        string Expand(string value)
        {
            value = value.Trim().Trim('"');
            if (value.StartsWith('%') && value.EndsWith('%') && strings.TryGetValue(value[1..^1], out string? expanded))
            {
                return expanded;
            }
            return value;
        }
        string[] Values(string line) => line.Split('=', 2) is { Length: 2 } pair
            ? pair[1].Split(',').Select(Expand).ToArray() : [];
        bool Key(string line, string name) => string.Equals(line.Split('=', 2)[0].Trim(), name, StringComparison.OrdinalIgnoreCase);
        List<string> version = sections.GetValueOrDefault("Version") ?? [];
        if (!version.Any(entry => Key(entry, "Provider") && Values(entry) is [string provider]
            && provider is "Razer Inc" or "Razer Inc." or "Razer"))
        {
            throw new InvalidDataException("The signed INF does not identify the Razer provider.");
        }
        var models = new List<string>();
        foreach (string line in sections.GetValueOrDefault("Manufacturer") ?? [])
        {
            string[] values = Values(line);
            if (values.Length < 2)
            {
                continue;
            }
            foreach (string decoration in values.Skip(1).Where(value =>
                value.Equals("NTamd64", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("NTamd64.", StringComparison.OrdinalIgnoreCase)))
            {
                models.Add(values[0] + "." + decoration);
            }
        }
        foreach (string line in models.SelectMany(model => sections.GetValueOrDefault(model) ?? []))
        {
            string[] values = Values(line);
            if (values.Length < 2 || !values.Skip(1).Contains(hardwareId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (string serviceLine in new[] { ".NTamd64.Services", ".NT.Services", ".Services" }
                .SelectMany(suffix => sections.GetValueOrDefault(values[0] + suffix) ?? []))
            {
                string[] declaration = Values(serviceLine);
                if (!Key(serviceLine, "AddService") || declaration.Length < 3
                    || !string.Equals(declaration[0], service, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                List<string> serviceSection = sections.GetValueOrDefault(declaration[2]) ?? [];
                bool kernel = serviceSection.Any(entry => Key(entry, "ServiceType")
                    && Values(entry) is ["1"]);
                bool binary = serviceSection.Any(entry =>
                    Key(entry, "ServiceBinary") && Values(entry) is [string path]
                    && string.Equals(path, @"%12%\" + service + ".sys", StringComparison.OrdinalIgnoreCase));
                if (!kernel || !binary)
                {
                    continue;
                }
                string? catalog = version.Where(entry => Key(entry, "CatalogFile.NTamd64") || Key(entry, "CatalogFile"))
                    .SelectMany(Values).Distinct(StringComparer.OrdinalIgnoreCase).SingleOrDefault();
                if (catalog is null)
                {
                    throw new InvalidDataException("The installed package catalog is ambiguous or missing.");
                }
                RequireFileName(catalog);
                if (!catalog.EndsWith(".cat", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The package catalog has an invalid extension.");
                }
                return catalog;
            }
        }
        throw new InvalidDataException("The installed INF does not bind the exact hardware and Razer API service.");
    }

    private static FileStream OpenProtectedFile(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Driver package paths may not contain reparse points.");
            }
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 64 * 1024 * 1024)
        {
            stream.Dispose();
            throw new InvalidDataException("The driver package file exceeds the inspection bound.");
        }
        return stream;
    }

    private static string Hash(FileStream file)
    {
        file.Position = 0;
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static void RequireFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains(':'))
        {
            throw new InvalidDataException("Invalid installed package filename.");
        }
    }

    private static void RequireUnder(string path, string root)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The driver path is outside its protected Windows directory.");
        }
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupGetInfDriverStoreLocation(string fileName, nint alternatePlatform,
        string? localeName, StringBuilder returnBuffer, int returnBufferSize, out uint requiredSize);
}
