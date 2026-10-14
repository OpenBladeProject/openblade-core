using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class DotNetRuntimeAdmissionTests
{
    private static readonly Version MinimumVersion = new(10, 0, 12);

    [Fact]
    public void CompleteNewerPatchInX64GlobalRoot_IsAccepted()
    {
        using RuntimeTree tree = new();
        tree.WriteCompleteRuntime("10.0.13");

        CreateAdmission(new DotNetInstallRoot(tree.Root, IsX64: true)).EnsureAvailable();
    }

    [Theory]
    [InlineData("9.0.99")]
    [InlineData("11.0.12")]
    [InlineData("10.0.11")]
    [InlineData("10.0.13-preview.1")]
    public void UnsupportedRuntimeVersion_IsRejected(string version)
    {
        using RuntimeTree tree = new();
        tree.WriteCompleteRuntime(version);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => CreateAdmission(new DotNetInstallRoot(tree.Root, IsX64: true)).EnsureAvailable());

        Assert.Contains("64-bit .NET Desktop Runtime 10.0.12 or later", failure.Message);
    }

    [Theory]
    [InlineData("coreclr.dll")]
    [InlineData("System.Windows.Forms.dll")]
    public void IncompleteRuntime_IsRejected(string missingFile)
    {
        using RuntimeTree tree = new();
        tree.WriteCompleteRuntime("10.0.12");
        File.Delete(Directory.EnumerateFiles(tree.Root, missingFile, SearchOption.AllDirectories).Single());

        Assert.Throws<InvalidOperationException>(
            () => CreateAdmission(new DotNetInstallRoot(tree.Root, IsX64: true)).EnsureAvailable());
    }

    [Fact]
    public void CompleteX86Runtime_IsRejected()
    {
        using RuntimeTree tree = new();
        tree.WriteCompleteRuntime("10.0.12");

        Assert.Throws<InvalidOperationException>(
            () => CreateAdmission(new DotNetInstallRoot(tree.Root, IsX64: false)).EnsureAvailable());
    }

    [Fact]
    public void UnrootedRuntimeLocation_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => CreateAdmission(new DotNetInstallRoot(@"relative\dotnet", IsX64: true)).EnsureAvailable());
    }

    [Fact]
    public void WindowsRootSource_UsesRegisteredX64RootAsAuthoritative()
    {
        string registered = Path.GetFullPath(@"C:\registered-dotnet");
        string programFiles = Path.GetFullPath(@"C:\Program Files");
        WindowsDotNetInstallRootSource source = new(
            readRegisteredX64Root: () => new(Succeeded: true, Path: registered),
            readProgramFiles: () => programFiles);

        Assert.Equal([registered], source.GetInstallRoots().Select(root => root.Path));
        Assert.All(source.GetInstallRoots(), root => Assert.True(root.IsX64));
    }

    [Fact]
    public void WindowsRootSource_UsesDefaultProgramFilesRootWhenRegistrationIsAbsent()
    {
        string programFiles = Path.GetFullPath(@"C:\Program Files");
        WindowsDotNetInstallRootSource source = new(
            readRegisteredX64Root: () => new(Succeeded: true, Path: null),
            readProgramFiles: () => programFiles);

        Assert.Equal(
            [Path.Combine(programFiles, "dotnet")],
            source.GetInstallRoots().Select(root => root.Path));
    }

    [Fact]
    public void WindowsRootSource_RegistryReadFailureDoesNotFallBack()
    {
        WindowsDotNetInstallRootSource source = new(
            readRegisteredX64Root: () => new(Succeeded: false, Path: null),
            readProgramFiles: () => Path.GetFullPath(@"C:\Program Files"));

        Assert.Empty(source.GetInstallRoots());
    }

    [Fact]
    public void BrokenRegisteredRoot_DoesNotAcceptCompleteDefaultProgramFilesRuntime()
    {
        using RuntimeTree tree = new();
        string registered = Path.Combine(tree.Root, "registered-dotnet");
        string programFiles = Path.Combine(tree.Root, "ProgramFiles");
        WriteCompleteRuntime(Path.Combine(programFiles, "dotnet"), "10.0.12");
        Directory.CreateDirectory(registered);
        WindowsDotNetInstallRootSource source = new(
            readRegisteredX64Root: () => new(Succeeded: true, Path: registered),
            readProgramFiles: () => programFiles);

        Assert.Throws<InvalidOperationException>(
            () => new InstalledDotNetRuntimeAdmission(MinimumVersion, source).EnsureAvailable());
    }

    [Fact]
    public async Task StagePayload_MissingRuntimeFailsBeforeCreatingTransactionDirectories()
    {
        using RuntimeTree tree = new();
        string programFiles = Path.Combine(tree.Root, "ProgramFiles");
        string payload = Path.Combine(tree.Root, "payload");
        Directory.CreateDirectory(programFiles);
        Directory.CreateDirectory(payload);
        SetupPaths paths = SetupPaths.Create(
            programFiles,
            Path.Combine(tree.Root, "ProgramData"),
            Path.Combine(tree.Root, "LocalData"),
            Path.Combine(tree.Root, "Startup"));
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            paths: paths,
            payloadDirectory: payload,
            dotNetRuntimeAdmission: CreateAdmission());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => platform.StagePayloadAsync(CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(programFiles));
    }

    [Fact]
    public void ConstructionForUninstall_DoesNotProbeInstallRuntime()
    {
        CountingRuntimeAdmission runtimeAdmission = new();

        _ = WindowsSetupPlatformTestFactory.Create(dotNetRuntimeAdmission: runtimeAdmission);

        Assert.Equal(0, runtimeAdmission.CallCount);
    }

    private static InstalledDotNetRuntimeAdmission CreateAdmission(params DotNetInstallRoot[] roots) =>
        new(MinimumVersion, new FixedRootSource(roots));

    private sealed class FixedRootSource(IReadOnlyList<DotNetInstallRoot> roots) : IDotNetInstallRootSource
    {
        public IReadOnlyList<DotNetInstallRoot> GetInstallRoots() => roots;
    }

    private sealed class CountingRuntimeAdmission : IDotNetRuntimeAdmission
    {
        public int CallCount { get; private set; }

        public void EnsureAvailable()
        {
            CallCount++;
            throw new InvalidOperationException("Runtime admission must not run for uninstall.");
        }
    }

    private sealed class RuntimeTree : IDisposable
    {
        public RuntimeTree()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "OpenBlade.Setup.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void WriteCompleteRuntime(string version)
        {
            DotNetRuntimeAdmissionTests.WriteCompleteRuntime(Root, version);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

    }

    private static void WriteCompleteRuntime(string root, string version)
    {
        WriteFile(Path.Combine(root, "shared", "Microsoft.NETCore.App", version, "coreclr.dll"));
        WriteFile(Path.Combine(
            root,
                "shared",
                "Microsoft.WindowsDesktop.App",
                version,
                "System.Windows.Forms.dll"));
    }

    private static void WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
    }
}
