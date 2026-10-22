using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace OpenBlade.Tray;

/// <summary>Bounded, read-only suggestions. A candidate has no authority until explicitly saved.</summary>
internal static class InstalledApplicationDiscovery
{
    private const int MaximumCandidates = 1000;
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;

    internal static Task<IReadOnlyList<InstalledApplicationCandidate>> DiscoverAsync(CancellationToken token) =>
        Task.Run<IReadOnlyList<InstalledApplicationCandidate>>(() => Discover(token), token);

    private static IReadOnlyList<InstalledApplicationCandidate> Discover(CancellationToken token)
    {
        var candidates = new Dictionary<string, InstalledApplicationCandidate>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using RegistryKey? steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("SteamPath") is string steamPath)
            {
                var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamPath };
                string folders = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (ReadBounded(folders, token) is { } text)
                {
                    foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"(?<path>(?:\\\\.|[^\"])*)\"",
                                 RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                    {
                        libraries.Add(match.Groups["path"].Value.Replace("\\\\", "\\", StringComparison.Ordinal));
                    }
                }
                foreach (string library in libraries.Take(32))
                {
                    string apps = Path.Combine(library, "steamapps");
                    foreach (string manifest in EnumerateFiles(
                        apps, "appmanifest_*.acf", 512, token))
                    {
                        token.ThrowIfCancellationRequested();
                        if (ReadBounded(manifest, token) is not { } acf)
                        {
                            continue;
                        }
                        string? directory = VdfValue(acf, "installdir");
                        string? name = VdfValue(acf, "name");
                        if (directory is not null && name is not null
                            && TryChild(Path.Combine(apps, "common"), directory, out string root))
                        {
                            AddExecutables(root, name, "Steam");
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (IsDiscoveryFailure(exception)) { }

        string epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        foreach (string manifest in EnumerateFiles(epic, "*.item", 512, token))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (ReadBounded(manifest, token) is not { } json)
                {
                    continue;
                }
                if (ApplicationDiscoveryCatalog.TryReadEpicManifest(
                        json, out EpicApplicationManifest? item)
                    && item is not null
                    && TryChild(item.InstallRoot, item.DeclaredExecutable, out string path))
                {
                    AddExecutables(
                        item.InstallRoot,
                        item.DisplayName,
                        "Epic Games",
                        declaredExecutable: path);
                }
            }
            catch (Exception exception) when (IsDiscoveryFailure(exception)) { }
        }

        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
        {
            foreach (string game in EnumerateDirectories(
                Path.Combine(drive.RootDirectory.FullName, "XboxGames"), 256, token))
            {
                token.ThrowIfCancellationRequested();
                string root = Path.Combine(game, "Content");
                string config = Path.Combine(root, "MicrosoftGame.config");
                try
                {
                    if (ReadBounded(config, token) is not { } xml)
                    {
                        continue;
                    }
                    using XmlReader reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = MaximumMetadataBytes,
                    });
                    XDocument document = XDocument.Load(reader);
                    foreach (XElement executable in document.Descendants().Where(element => element.Name.LocalName == "Executable"))
                    {
                        if (executable.Attribute("Name")?.Value is { } relative
                            && TryChild(root, relative, out string path))
                        {
                            Add(Path.GetFileName(game), path, "Xbox", root);
                        }
                    }
                }
                catch (Exception exception) when (IsDiscoveryFailure(exception)) { }
            }
        }

        // Battle.net does not expose a stable public discovery API. Installed
        // game roots from Windows' uninstall inventory are suggestions only.
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                    foreach (string keyName in uninstall?.GetSubKeyNames().Take(1024) ?? [])
                    {
                        token.ThrowIfCancellationRequested();
                        using RegistryKey? game = uninstall?.OpenSubKey(keyName);
                        if (game?.GetValue("InstallLocation") is string root
                            && game.GetValue("DisplayName") is string name
                            && File.Exists(Path.Combine(root, ".build.info")))
                        {
                            AddExecutables(root, name, "Battle.net");
                        }
                    }
                }
                catch (Exception exception) when (IsDiscoveryFailure(exception)) { }
            }
        }
        return candidates.Values.OrderBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.ExecutablePath, StringComparer.OrdinalIgnoreCase).ToArray();

        void Add(string name, string path, string launcher, string installRoot)
        {
            if (candidates.Count >= MaximumCandidates || name.Any(char.IsControl))
            {
                return;
            }
            try
            {
                string normalized = OpenBlade.Contracts.ApplicationProfileDefinition.NormalizeExecutablePath(path);
                if (File.Exists(normalized))
                {
                    candidates.TryAdd(normalized, new(name, normalized, launcher,
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot))));
                }
            }
            catch (ArgumentException) { }
        }

        void AddExecutables(
            string root,
            string name,
            string launcher,
            string? declaredExecutable = null)
        {
            var executablePaths = new List<string>();
            var directories = new Queue<(string Path, int Depth)>();
            directories.Enqueue((root, 0));
            int visited = 0;
            while (directories.TryDequeue(out var current) && visited++ < 128
                && candidates.Count < MaximumCandidates)
            {
                token.ThrowIfCancellationRequested();
                foreach (string path in EnumerateFiles(current.Path, "*.exe", 64, token))
                {
                    executablePaths.Add(path);
                }
                if (current.Depth < 3)
                {
                    foreach (string directory in EnumerateDirectories(
                        current.Path, 64, token))
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        {
                            directories.Enqueue((directory, current.Depth + 1));
                        }
                    }
                }
            }

            IReadOnlyList<string> selected = declaredExecutable is null
                ? ApplicationDiscoveryCatalog.SelectExecutableVariants(root, name, executablePaths)
                : ApplicationDiscoveryCatalog.SelectEpicExecutableVariants(
                    root, name, declaredExecutable, executablePaths);
            foreach (string path in selected)
            {
                Add(name, path, launcher, root);
            }
        }
    }

    private static string? ReadBounded(string path, CancellationToken token)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumMetadataBytes)
            {
                return null;
            }

            using var content = new MemoryStream(
                capacity: checked((int)Math.Min(stream.Length, MaximumMetadataBytes)));
            byte[] buffer = new byte[16 * 1024];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                if (content.Length > MaximumMetadataBytes - read)
                {
                    return null;
                }
                content.Write(buffer, 0, read);
            }

            content.Position = 0;
            using var reader = new StreamReader(
                content,
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (IsDiscoveryFailure(exception)) { return null; }
    }

    private static string? VdfValue(string text, string key)
    {
        Match match = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s+\"(?<value>[^\"]*)\"",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static bool TryChild(string root, string relative, out string path)
    {
        path = string.Empty;
        try
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
            if (Path.IsPathFullyQualified(root) && candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                path = candidate;
                return true;
            }
        }
        catch (ArgumentException) { }
        return false;
    }

    private static IReadOnlyList<string> EnumerateFiles(
        string path,
        string pattern,
        int maximum,
        CancellationToken token)
    {
        try
        {
            var files = new List<string>(maximum);
            using IEnumerator<string> enumerator = Directory.EnumerateFiles(path, pattern).GetEnumerator();
            while (files.Count < maximum && enumerator.MoveNext())
            {
                token.ThrowIfCancellationRequested();
                files.Add(enumerator.Current);
            }
            return files;
        }
        catch (Exception exception) when (IsDiscoveryFailure(exception)) { return []; }
    }

    private static IReadOnlyList<string> EnumerateDirectories(
        string path,
        int maximum,
        CancellationToken token)
    {
        try
        {
            var directories = new List<string>(maximum);
            using IEnumerator<string> enumerator = Directory.EnumerateDirectories(path).GetEnumerator();
            while (directories.Count < maximum && enumerator.MoveNext())
            {
                token.ThrowIfCancellationRequested();
                directories.Add(enumerator.Current);
            }
            return directories;
        }
        catch (Exception exception) when (IsDiscoveryFailure(exception)) { return []; }
    }

    private static bool IsDiscoveryFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException
        or JsonException or XmlException or RegexMatchTimeoutException;
}
