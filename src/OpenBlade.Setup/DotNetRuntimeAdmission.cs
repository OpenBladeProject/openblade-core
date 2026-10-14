using System.Reflection;
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
    private readonly Func<RegisteredDotNetRootReadResult> readRegisteredX64Root;
    private readonly Func<string> readProgramFiles;

    internal WindowsDotNetInstallRootSource(
        Func<RegisteredDotNetRootReadResult>? readRegisteredX64Root = null,
        Func<string>? readProgramFiles = null)
    {
        this.readRegisteredX64Root = readRegisteredX64Root ?? ReadRegisteredX64Root;
        this.readProgramFiles = readProgramFiles ?? (() =>
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    }

    public IReadOnlyList<DotNetInstallRoot> GetInstallRoots()
    {
        List<DotNetInstallRoot> roots = [];
        RegisteredDotNetRootReadResult registered = readRegisteredX64Root();
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

    private static RegisteredDotNetRootReadResult ReadRegisteredX64Root()
    {
        try
        {
            using RegistryKey localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
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
                && HasCompleteFramework(root.Path, CoreFramework, CoreRuntimeFile)
                && HasCompleteFramework(root.Path, DesktopFramework, DesktopRuntimeFile))
            {
                return;
            }
        }

        throw new InvalidOperationException(SetupStrings.Format(
            "OpenBlade requires the 64-bit .NET Desktop Runtime {0} or later. Run OpenBlade-Setup.exe to install or repair it, then try again.",
            minimumVersion.ToString(3)));
    }

    private bool HasCompleteFramework(string root, string framework, string requiredFile)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root))
            {
                return false;
            }

            string fullRoot = Path.GetFullPath(root);
            string frameworkRoot = Path.Combine(fullRoot, "shared", framework);
            if (!Directory.Exists(frameworkRoot))
            {
                return false;
            }

            foreach (string versionDirectory in Directory.EnumerateDirectories(frameworkRoot))
            {
                string fullVersionDirectory = Path.GetFullPath(versionDirectory);
                if (!string.Equals(
                    Path.GetDirectoryName(fullVersionDirectory),
                    Path.GetFullPath(frameworkRoot),
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string versionName = Path.GetFileName(fullVersionDirectory);
                if (!Version.TryParse(versionName, out Version? installedVersion)
                    || installedVersion.Major != minimumVersion.Major
                    || installedVersion.Minor != minimumVersion.Minor
                    || installedVersion < minimumVersion)
                {
                    continue;
                }

                string runtimeFile = Path.GetFullPath(Path.Combine(fullVersionDirectory, requiredFile));
                if (string.Equals(
                    Path.GetDirectoryName(runtimeFile),
                    fullVersionDirectory,
                    StringComparison.OrdinalIgnoreCase)
                    && File.Exists(runtimeFile))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return false;
        }

        return false;
    }
}
