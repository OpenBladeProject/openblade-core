using System.Text.Json;

namespace OpenBlade.Tray;

internal sealed record EpicApplicationManifest(
    string InstallRoot,
    string DeclaredExecutable,
    string DisplayName);

internal sealed record InstalledApplicationCandidate(
    string Name,
    string ExecutablePath,
    string Launcher,
    string InstallRoot)
{
    internal string ExecutableName => Path.GetFileNameWithoutExtension(ExecutablePath);
}

internal sealed record InstalledApplicationGroup(
    string Name,
    string Launcher,
    string InstallRoot,
    IReadOnlyList<InstalledApplicationCandidate> Executables);

internal static class ApplicationDiscoveryCatalog
{
    private const int MaximumExecutableVariants = 4;

    private static readonly string[] IgnoredExecutableTerms =
    [
        "uninstall", "unins", "setup", "installer", "updater", "updatehelper",
        "helper", "crashreport", "crashhandler", "crashpad", "bugreport", "bootstrapper",
        "prereq", "redistributable", "vcredist", "dxsetup", "easyanticheat",
        "battleye", "eosbootstrapper", "overlayrenderer", "webview", "cefsubprocess",
    ];

    private static readonly string[] IgnoredDirectoryTerms =
    [
        "_commonredist", "redistributable", "redist", "prereq", "installer",
        "support", "crashpad", "easyanticheat", "battleye",
    ];

    private static readonly string[] EquivalentSuffixes =
    [
        "win64shipping", "win32shipping", "shipping", "win64", "win32", "x64", "x86",
    ];

    internal static IReadOnlyList<string> SelectExecutableVariants(
        string installRoot,
        string installedName,
        IEnumerable<string> executablePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(installedName);
        ArgumentNullException.ThrowIfNull(executablePaths);

        string normalizedTitle = NormalizeIdentity(installedName);
        RankedExecutable[] ranked = executablePaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => IsUsefulExecutable(installRoot, path))
            .Select(path => new RankedExecutable(
                path,
                NormalizeExecutableIdentity(path),
                ScoreExecutable(installRoot, normalizedTitle, path),
                NormalizeIdentity(Path.GetFileNameWithoutExtension(path))
                    .Contains("launcher", StringComparison.Ordinal)))
            .GroupBy(candidate => candidate.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Path.Length)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool hasDirectExecutable = ranked.Any(candidate => !candidate.IsLauncher);
        return ranked
            .Where(candidate => !hasDirectExecutable || !candidate.IsLauncher)
            .Take(MaximumExecutableVariants)
            .Select(candidate => candidate.Path)
            .ToArray();
    }

    internal static IReadOnlyList<string> SelectEpicExecutableVariants(
        string installRoot,
        string installedName,
        string declaredExecutable,
        IEnumerable<string> executablePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredExecutable);
        return new[] { declaredExecutable }
            .Concat(SelectExecutableVariants(installRoot, installedName, executablePaths))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumExecutableVariants)
            .ToArray();
    }

    internal static bool TryReadEpicManifest(
        string json,
        out EpicApplicationManifest? manifest)
    {
        manifest = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object
                || !TryGetString(item, "InstallLocation", out string installRoot)
                || !TryGetString(item, "LaunchExecutable", out string declaredExecutable)
                || !TryGetString(item, "DisplayName", out string displayName))
            {
                return false;
            }

            manifest = new EpicApplicationManifest(installRoot, declaredExecutable, displayName);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<InstalledApplicationGroup> Group(
        IEnumerable<InstalledApplicationCandidate> candidates,
        string? searchText)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        string query = searchText?.Trim() ?? string.Empty;

        return candidates
            .Where(candidate => Matches(candidate, query))
            .DistinctBy(candidate => candidate.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .GroupBy(
                candidate => new GroupKey(candidate.Name,
                    Path.TrimEndingDirectorySeparator(candidate.InstallRoot)),
                GroupKeyComparer.Instance)
            .Select(group => new InstalledApplicationGroup(
                group.Key.Name,
                string.Join(", ", group.Select(candidate => candidate.Launcher)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)),
                group.Key.InstallRoot,
                group.OrderBy(candidate => candidate.ExecutableName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(candidate => candidate.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.InstallRoot, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool Matches(InstalledApplicationCandidate candidate, string query) =>
        query.Length == 0
        || candidate.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || candidate.Launcher.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || candidate.ExecutableName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || candidate.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetString(JsonElement item, string propertyName, out string value)
    {
        value = string.Empty;
        if (!item.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } text
            || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool IsUsefulExecutable(string installRoot, string path)
    {
        string file = NormalizeIdentity(Path.GetFileNameWithoutExtension(path));
        if (file.Length == 0 || IgnoredExecutableTerms.Any(file.Contains))
        {
            return false;
        }

        string? relative = Path.GetRelativePath(installRoot, path);
        return !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .TakeWhile(part => !part.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Select(NormalizeIdentity)
            .Any(part => IgnoredDirectoryTerms.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private static int ScoreExecutable(string installRoot, string normalizedTitle, string path)
    {
        string executable = NormalizeIdentity(Path.GetFileNameWithoutExtension(path));
        int score = executable == normalizedTitle ? 200 : 0;
        if (normalizedTitle.Length >= 4 && executable.Contains(normalizedTitle, StringComparison.Ordinal))
        {
            score += 80;
        }
        if (executable.Contains("launcher", StringComparison.Ordinal))
        {
            score -= 60;
        }
        if (executable.Contains("shipping", StringComparison.Ordinal))
        {
            score += 10;
        }

        string relative = Path.GetRelativePath(installRoot, path);
        score -= Math.Max(0, relative.Count(character =>
            character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar) - 1) * 8;
        return score;
    }

    private static string NormalizeExecutableIdentity(string path)
    {
        string identity = NormalizeIdentity(Path.GetFileNameWithoutExtension(path));
        bool shipping = identity.Contains("shipping", StringComparison.Ordinal);
        foreach (string suffix in EquivalentSuffixes)
        {
            if (identity.EndsWith(suffix, StringComparison.Ordinal) && identity.Length > suffix.Length)
            {
                identity = identity[..^suffix.Length];
                break;
            }
        }
        // Unreal installations commonly keep a root bootstrap executable beside
        // the actual foreground process under Binaries/*/*-Shipping.exe. Collapse
        // architecture-named shipping copies, but never merge that foreground
        // process into the root entry point: exact executable matching needs both.
        return shipping ? identity + "|shipping" : identity;
    }

    private static string NormalizeIdentity(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record RankedExecutable(string Path, string Identity, int Score, bool IsLauncher);
    private sealed record GroupKey(string Name, string InstallRoot);

    private sealed class GroupKeyComparer : IEqualityComparer<GroupKey>
    {
        internal static GroupKeyComparer Instance { get; } = new();

        public bool Equals(GroupKey? x, GroupKey? y) =>
            x is not null && y is not null
            && string.Equals(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase)
            && string.Equals(x.InstallRoot, y.InstallRoot, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(GroupKey obj) => HashCode.Combine(
            StringComparer.CurrentCultureIgnoreCase.GetHashCode(obj.Name),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.InstallRoot));
    }
}
