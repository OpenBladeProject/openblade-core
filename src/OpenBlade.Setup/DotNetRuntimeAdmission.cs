using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace OpenBlade.Setup;

public interface IDotNetRuntimeAdmission
{
    void EnsureAvailable();
}

internal sealed record DotNetInstallRoot(string Path, bool IsX64);

internal sealed record RegisteredDotNetRootReadResult(bool Succeeded, string? Path);

internal interface IDotNetInstallRootSource
{
    IReadOnlyList<DotNetInstallRoot> GetInstallRoots();
}

internal sealed class WindowsDotNetInstallRootSource : IDotNetInstallRootSource
{
    private const string InstalledVersionsKey = @"SOFTWARE\dotnet\Setup\InstalledVersions\x64";
    private readonly Func<RegistryView, RegisteredDotNetRootReadResult> readRegisteredX64Root;
    private readonly Func<string> readProgramFiles;

    internal WindowsDotNetInstallRootSource(
        Func<RegistryView, RegisteredDotNetRootReadResult>? readRegisteredX64Root = null,
        Func<string>? readProgramFiles = null)
    {
        this.readRegisteredX64Root = readRegisteredX64Root ?? ReadRegisteredX64Root;
        this.readProgramFiles = readProgramFiles ?? (() =>
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    }

    public IReadOnlyList<DotNetInstallRoot> GetInstallRoots()
    {
        List<DotNetInstallRoot> roots = [];
        // The .NET apphost reads architecture-specific registrations from the 32-bit view.
        RegisteredDotNetRootReadResult registered = readRegisteredX64Root(RegistryView.Registry32);
        if (!registered.Succeeded)
        {
            return roots;
        }

        if (!string.IsNullOrWhiteSpace(registered.Path))
        {
            AddRoot(roots, registered.Path);
            return roots;
        }

        string programFiles = readProgramFiles();
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            AddRoot(roots, Path.Combine(programFiles, "dotnet"));
        }

        return roots;
    }

    private static void AddRoot(List<DotNetInstallRoot> roots, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return;
        }

        string fullPath = Path.GetFullPath(path);
        if (!roots.Any(candidate => string.Equals(
            candidate.Path,
            fullPath,
            StringComparison.OrdinalIgnoreCase)))
        {
            roots.Add(new(fullPath, IsX64: true));
        }
    }

    private static RegisteredDotNetRootReadResult ReadRegisteredX64Root(RegistryView view)
    {
        try
        {
            using RegistryKey localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                view);
            using RegistryKey? installedVersions = localMachine.OpenSubKey(InstalledVersionsKey);
            return new(
                Succeeded: true,
                installedVersions?.GetValue("InstallLocation") as string);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return new(Succeeded: false, Path: null);
        }
    }
}

internal sealed class InstalledDotNetRuntimeAdmission : IDotNetRuntimeAdmission
{
    private const string CoreFramework = "Microsoft.NETCore.App";
    private const string DesktopFramework = "Microsoft.WindowsDesktop.App";
    private const string CoreRuntimeFile = "coreclr.dll";
    private const string DesktopRuntimeFile = "System.Windows.Forms.dll";
    private const string DesktopRuntimeConfigFile = "Microsoft.WindowsDesktop.App.runtimeconfig.json";
    private readonly Version minimumVersion;
    private readonly IDotNetInstallRootSource roots;

    internal InstalledDotNetRuntimeAdmission(
        Version minimumVersion,
        IDotNetInstallRootSource? roots = null)
    {
        ArgumentNullException.ThrowIfNull(minimumVersion);
        this.minimumVersion = minimumVersion;
        this.roots = roots ?? new WindowsDotNetInstallRootSource();
    }

    internal static InstalledDotNetRuntimeAdmission CreateForCurrentBuild()
    {
        string? version = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => string.Equals(
                attribute.Key,
                "OpenBladeMinimumDotNetRuntimeVersion",
                StringComparison.Ordinal))
            ?.Value;
        if (!Version.TryParse(version, out Version? minimumVersion))
        {
            throw new InvalidOperationException(
                "The OpenBlade setup runtime requirement metadata is missing or invalid.");
        }

        return new(minimumVersion);
    }

    public void EnsureAvailable()
    {
        foreach (DotNetInstallRoot root in roots.GetInstallRoots())
        {
            if (root.IsX64
                && HasCompatibleRuntime(root.Path))
            {
                return;
            }
        }

        throw new InvalidOperationException(SetupStrings.Format(
            "OpenBlade requires the 64-bit .NET 10 Desktop Runtime ({0} or a newer 10.0 patch). Install or repair .NET 10 Desktop Runtime, then try again.",
            minimumVersion.ToString(3)));
    }

    private bool HasCompatibleRuntime(string root)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root))
            {
                return false;
            }

            string fullRoot = Path.GetFullPath(root);
            if (!TrySelectLatestFramework(
                    fullRoot,
                    DesktopFramework,
                    minimumVersion,
                    out string? desktopDirectory)
                || !HasRequiredFile(desktopDirectory, DesktopRuntimeFile)
                || !TryReadCoreDependency(desktopDirectory, out Version? coreDependency)
                || coreDependency.Major != minimumVersion.Major
                || coreDependency.Minor != minimumVersion.Minor)
            {
                return false;
            }

            Version requiredCoreVersion = coreDependency > minimumVersion
                ? coreDependency
                : minimumVersion;
            return TrySelectLatestFramework(
                    fullRoot,
                    CoreFramework,
                    requiredCoreVersion,
                    out string? coreDirectory)
                && HasRequiredFile(coreDirectory, CoreRuntimeFile);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or JsonException)
        {
            return false;
        }
    }

    private static bool TrySelectLatestFramework(
        string root,
        string framework,
        Version requiredVersion,
        [NotNullWhen(true)] out string? selectedDirectory)
    {
        selectedDirectory = null;
        Version? selectedVersion = null;
        string frameworkRoot = Path.GetFullPath(Path.Combine(root, "shared", framework));
        if (!Directory.Exists(frameworkRoot))
        {
            return false;
        }

        foreach (string versionDirectory in Directory.EnumerateDirectories(frameworkRoot))
        {
            string fullVersionDirectory = Path.GetFullPath(versionDirectory);
            if (!string.Equals(
                Path.GetDirectoryName(fullVersionDirectory),
                frameworkRoot,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string versionName = Path.GetFileName(fullVersionDirectory);
            if (!Version.TryParse(versionName, out Version? installedVersion)
                || installedVersion.Major != requiredVersion.Major
                || installedVersion.Minor != requiredVersion.Minor
                || installedVersion < requiredVersion
                || selectedVersion is not null && installedVersion <= selectedVersion)
            {
                continue;
            }

            selectedVersion = installedVersion;
            selectedDirectory = fullVersionDirectory;
        }

        return selectedDirectory is not null;
    }

    private static bool HasRequiredFile(string frameworkDirectory, string requiredFile)
    {
        string runtimeFile = Path.GetFullPath(Path.Combine(frameworkDirectory, requiredFile));
        return string.Equals(
            Path.GetDirectoryName(runtimeFile),
            frameworkDirectory,
            StringComparison.OrdinalIgnoreCase)
            && File.Exists(runtimeFile);
    }

    private static bool TryReadCoreDependency(
        string desktopDirectory,
        [NotNullWhen(true)] out Version? requiredVersion)
    {
        requiredVersion = null;
        string configPath = Path.GetFullPath(Path.Combine(desktopDirectory, DesktopRuntimeConfigFile));
        if (!string.Equals(
            Path.GetDirectoryName(configPath),
            desktopDirectory,
            StringComparison.OrdinalIgnoreCase)
            || !File.Exists(configPath))
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("runtimeOptions", out JsonElement runtimeOptions)
            || runtimeOptions.ValueKind != JsonValueKind.Object
            || !runtimeOptions.TryGetProperty("rollForward", out JsonElement rollForward)
            || rollForward.ValueKind != JsonValueKind.String
            || !string.Equals(rollForward.GetString(), "LatestPatch", StringComparison.Ordinal)
            || !runtimeOptions.TryGetProperty("framework", out JsonElement framework)
            || framework.ValueKind != JsonValueKind.Object
            || !framework.TryGetProperty("name", out JsonElement name)
            || name.ValueKind != JsonValueKind.String
            || !string.Equals(name.GetString(), CoreFramework, StringComparison.Ordinal)
            || !framework.TryGetProperty("version", out JsonElement version)
            || version.ValueKind != JsonValueKind.String
            || !Version.TryParse(version.GetString(), out requiredVersion))
        {
            requiredVersion = null;
            return false;
        }

        return true;
    }
}
