using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupPayloadTransactionTests
{
    [Fact]
    public async Task StagePayload_ReportsEveryCopiedRelativePathAndTransactionPhase()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        RecordingSetupProgressReporter progress = new();

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(progressReporter: progress)
                .StagePayloadAsync(CancellationToken.None);
        await transaction.ActivateAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        Assert.Contains(
            progress.Events,
            item => item.Code == SetupProgressCode.CopyFile
                && Assert.Single(item.Arguments) == "OpenBlade.Service.exe");
        Assert.Contains(
            progress.Events,
            item => item.Code == SetupProgressCode.CopyFile
                && Assert.Single(item.Arguments) == "OpenBlade.Tray.exe");
        Assert.Contains(progress.Events, item => item.Code == SetupProgressCode.ActivateFiles);
        Assert.Contains(progress.Events, item => item.Code == SetupProgressCode.CommitFiles);
        Assert.DoesNotContain(
            progress.Events.SelectMany(item => item.Arguments),
            argument => argument.Contains(tree.Root, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("OpenBlade.Service.exe")]
    [InlineData("OpenBlade.Service.dll")]
    [InlineData("OpenBlade.Service.deps.json")]
    [InlineData("OpenBlade.Service.runtimeconfig.json")]
    [InlineData("OpenBlade.Platform.dll")]
    [InlineData("OpenBlade.SessionAgent.exe")]
    [InlineData("OpenBlade.SessionAgent.dll")]
    [InlineData("OpenBlade.SessionAgent.deps.json")]
    [InlineData("OpenBlade.SessionAgent.runtimeconfig.json")]
    [InlineData("OpenBlade.Tray.exe")]
    [InlineData("OpenBlade.Tray.dll")]
    [InlineData("OpenBlade.Tray.deps.json")]
    [InlineData("OpenBlade.Tray.runtimeconfig.json")]
    [InlineData("OpenBlade.Tray.pri")]
    [InlineData("App.xbf")]
    [InlineData("MainWindow.xbf")]
    [InlineData("Microsoft.WindowsAppRuntime.dll")]
    [InlineData("Microsoft.WindowsAppRuntime.pri")]
    [InlineData("Microsoft.ui.xaml.dll")]
    [InlineData("Microsoft.UI.Xaml.Controls.dll")]
    [InlineData("Microsoft.UI.Xaml.Controls.pri")]
    [InlineData(@"Microsoft.UI.Xaml\Assets\NoiseAsset_256x256_PNG.png")]
    public async Task StagePayload_MissingRequiredFile_FailsBeforeCreatingTransactionDirectories(string missing)
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        File.Delete(Path.Combine(tree.Payload, missing));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tree.CreatePlatform().StagePayloadAsync(CancellationToken.None));

        Assert.Contains(missing, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles));
    }

    [Fact]
    public async Task StagePayload_CaptureDeveloperToolPresent_FailsBeforeCreatingTransactionDirectories()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        string nestedTool = Path.Combine(tree.Payload, "tools", "openblade.capture-helper.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedTool)!);
        File.WriteAllBytes(nestedTool, [0x43, 0x41, 0x50]);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tree.CreatePlatform().StagePayloadAsync(CancellationToken.None));

        Assert.Contains("developer tool", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openblade.capture-helper.exe", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles));
    }

    [Theory]
    [InlineData("OpenBlade.DynamicLighting.msix")]
    [InlineData("OpenBlade.DynamicLighting.identity.json")]
    public async Task StagePayload_RetiredDynamicLightingIdentityArtifactFailsBeforeCreatingTransactionDirectories(
        string fileName)
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        File.WriteAllBytes(
            Path.Combine(tree.Payload, fileName),
            [0x50, 0x4B]);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => tree.CreatePlatform().StagePayloadAsync(CancellationToken.None));

        Assert.Contains("must not be present", failure.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles));
    }

    [Fact]
    public async Task StagePayload_ReviewedDynamicLightingIdentityPairIsRejected()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        DynamicLightingIdentityRegistrationTests.WriteArtifacts(tree.Payload);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => tree.CreatePlatform().StagePayloadAsync(CancellationToken.None));

        Assert.Contains("must not be present", failure.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles));
    }

    [Fact]
    public async Task Commit_InstallsExactBytePreservingPayloadTreeAndRemovesObsoleteWpfFiles()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("PresentationCore.dll", [1]);
        tree.WriteInstall("PresentationFramework.dll", [2]);
        tree.WriteInstall(@"Themes\Light.xaml", [3]);
        tree.WriteInstall("obsolete.txt", [4]);
        tree.WriteInstall("OpenBlade.Capture.exe", [5]);
        tree.WriteInstall("hostfxr.dll", [6]);
        tree.WriteInstall(@"host\fxr\10.0.1\hostfxr.dll", [7]);
        tree.WriteInstall(@"shared\Microsoft.NETCore.App\10.0.1\coreclr.dll", [8]);
        tree.WriteInstall(@"shared\Microsoft.WindowsDesktop.App\10.0.1\System.Windows.Forms.dll", [9]);
        Assert.False(File.Exists(Path.Combine(tree.Payload, "OpenBlade.Capture.exe")));
        byte[] expectedAsset = File.ReadAllBytes(
            Path.Combine(tree.Payload, @"Microsoft.UI.Xaml\Assets\NoiseAsset_256x256_PNG.png"));

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform().StagePayloadAsync(CancellationToken.None);
        await transaction.ActivateAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        Assert.Equal(expectedAsset, File.ReadAllBytes(
            Path.Combine(tree.Install, @"Microsoft.UI.Xaml\Assets\NoiseAsset_256x256_PNG.png")));
        Assert.Equal(ReadManifest(tree.Payload), ReadManifest(tree.Install));
        Assert.False(File.Exists(Path.Combine(tree.Install, "OpenBlade.Capture.exe")));
        Assert.False(File.Exists(Path.Combine(tree.Install, "PresentationCore.dll")));
        Assert.False(File.Exists(Path.Combine(tree.Install, "hostfxr.dll")));
        Assert.False(Directory.Exists(Path.Combine(tree.Install, "host")));
        Assert.False(Directory.Exists(Path.Combine(tree.Install, "shared")));
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.installing.*"));
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.failed.*"));
    }

    [Fact]
    public async Task StagePayload_ServiceExecutableIsStreamCreatedAndInheritsDestinationAcl()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        string source = Path.Combine(tree.Payload, "OpenBlade.Service.exe");
        SecurityIdentifier sourceOnlySid = new("S-1-5-21-1111111111-2222222222-3333333333-4444");
        FileInfo sourceFile = new(source);
        FileSecurity sourceSecurity = sourceFile.GetAccessControl();
        sourceSecurity.AddAccessRule(new FileSystemAccessRule(
            sourceOnlySid,
            FileSystemRights.Modify,
            AccessControlType.Allow));
        sourceFile.SetAccessControl(sourceSecurity);
        Assert.Contains(
            sourceFile.GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(),
            rule => rule.IdentityReference == sourceOnlySid
                && !rule.IsInherited
                && (rule.FileSystemRights & FileSystemRights.Modify) != 0);

        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current user SID unavailable for ACL test.");
        DirectoryInfo destinationParent = new(tree.ProgramFiles);
        DirectorySecurity parentSecurity = destinationParent.GetAccessControl();
        parentSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        parentSecurity.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        destinationParent.SetAccessControl(parentSecurity);

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform().StagePayloadAsync(CancellationToken.None);
        await transaction.ActivateAsync(CancellationToken.None);

        string installed = Path.Combine(tree.Install, "OpenBlade.Service.exe");
        FileSecurity installedSecurity = new FileInfo(installed).GetAccessControl();
        FileSystemAccessRule[] rules = installedSecurity
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.DoesNotContain(rules, rule => rule.IdentityReference == sourceOnlySid);
        Assert.Contains(rules, rule =>
            rule.IdentityReference == currentUser
            && rule.IsInherited
            && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
        Assert.False(installedSecurity.AreAccessRulesProtected);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(installed));

        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StagePayload_CopyFailure_CleansOnlyStagingAndLeavesOldTreeByteExact()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [9, 8, 7]);
        SortedDictionary<string, string> before = ReadManifest(tree.Install);
        DelegatingPayloadFileSystem files = new()
        {
            CopyFailure = (_, destination) => destination.EndsWith("NoiseAsset_256x256_PNG.png", StringComparison.OrdinalIgnoreCase)
                ? new IOException("injected copy failure")
                : null,
        };

        await Assert.ThrowsAsync<IOException>(
            () => tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None));

        Assert.Equal(before, ReadManifest(tree.Install));
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.installing.*"));
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activate_LiveMoveTransientFailure_RetriesAndActivates(bool unauthorized)
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [9, 7, 5]);
        int liveMoveAttempts = 0;
        DelegatingPayloadFileSystem files = new()
        {
            MoveFailure = (source, destination) =>
            {
                if (!source.Equals(tree.Install, StringComparison.OrdinalIgnoreCase)
                    || !destination.Contains(".rollback.", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                liveMoveAttempts++;
                return liveMoveAttempts == 1
                    ? unauthorized
                        ? new UnauthorizedAccessException("service process still releasing the install tree")
                        : new IOException("service process still releasing the install tree")
                    : null;
            },
        };

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        await transaction.ActivateAsync(CancellationToken.None);

        Assert.Equal(2, liveMoveAttempts);
        Assert.True(transaction.IsActivated);
        Assert.Equal(ReadManifest(tree.Payload), ReadManifest(tree.Install));
    }

    [Fact]
    public async Task Activate_LiveMoveCancellationDuringRetry_StopsBeforeSecondAttempt()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [1, 6, 1, 8]);
        using CancellationTokenSource cancellation = new();
        int liveMoveAttempts = 0;
        DelegatingPayloadFileSystem files = new()
        {
            MoveFailure = (source, destination) =>
            {
                if (!source.Equals(tree.Install, StringComparison.OrdinalIgnoreCase)
                    || !destination.Contains(".rollback.", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                liveMoveAttempts++;
                cancellation.Cancel();
                return new IOException("service process still releasing the install tree");
            },
        };

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transaction.ActivateAsync(cancellation.Token));

        Assert.Equal(1, liveMoveAttempts);
        Assert.True(File.Exists(Path.Combine(tree.Install, "old.bin")));
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
    }

    [Fact]
    public async Task Activate_LiveMoveNonTransientFailure_DoesNotRetry()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [4, 2]);
        int liveMoveAttempts = 0;
        DelegatingPayloadFileSystem files = new()
        {
            MoveFailure = (source, destination) =>
            {
                if (!source.Equals(tree.Install, StringComparison.OrdinalIgnoreCase)
                    || !destination.Contains(".rollback.", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                liveMoveAttempts++;
                return new InvalidOperationException("permanent move failure");
            },
        };

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.ActivateAsync(CancellationToken.None));

        Assert.Equal("permanent move failure", failure.Message);
        Assert.Equal(1, liveMoveAttempts);
    }

    [Fact]
    public async Task Activate_LiveMoveTransientFailureWithOccupiedDestination_DoesNotRetry()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [6, 2, 6]);
        int liveMoveAttempts = 0;
        DelegatingPayloadFileSystem files = new()
        {
            MoveFailure = (source, destination) =>
            {
                if (!source.Equals(tree.Install, StringComparison.OrdinalIgnoreCase)
                    || !destination.Contains(".rollback.", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                liveMoveAttempts++;
                Directory.CreateDirectory(destination);
                return new IOException("destination became occupied");
            },
        };

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => transaction.ActivateAsync(CancellationToken.None));

        Assert.Equal("destination became occupied", failure.Message);
        Assert.Equal(1, liveMoveAttempts);
        Assert.True(File.Exists(Path.Combine(tree.Install, "old.bin")));
        Assert.Single(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
    }

    [Fact]
    public async Task Activate_LiveMovePersistentTransientFailure_IsBounded()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [8, 8]);
        int liveMoveAttempts = 0;
        DelegatingPayloadFileSystem files = new()
        {
            MoveFailure = (source, destination) =>
            {
                if (!source.Equals(tree.Install, StringComparison.OrdinalIgnoreCase)
                    || !destination.Contains(".rollback.", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                liveMoveAttempts++;
                return new IOException("persistent move failure");
            },
        };

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => transaction.ActivateAsync(CancellationToken.None));

        Assert.Equal("persistent move failure", failure.Message);
        Assert.Equal(25, liveMoveAttempts);
        Assert.True(File.Exists(Path.Combine(tree.Install, "old.bin")));
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
    }

    [Fact]
    public async Task Activate_StagingMoveFailure_RestoresOldTree()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [6, 5, 4]);
        SortedDictionary<string, string> before = ReadManifest(tree.Install);
        DelegatingPayloadFileSystem files = new()
        {
            MoveFailure = (source, destination) =>
                source.Contains(".installing.", StringComparison.OrdinalIgnoreCase)
                && destination.Equals(tree.Install, StringComparison.OrdinalIgnoreCase)
                    ? new IOException("activate move failed")
                    : null,
        };

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => transaction.ActivateAsync(CancellationToken.None));

        Assert.Equal("activate move failed", failure.Message);
        Assert.Equal(before, ReadManifest(tree.Install));
        Assert.False(transaction.IsActivated);
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
    }

    [Fact]
    public async Task Activate_TransientRestoreMoveFailure_RetriesAndPreservesOriginalActivationFailure()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [3, 1, 4]);
        SortedDictionary<string, string> before = ReadManifest(tree.Install);
        int restoreAttempts = 0;
        DelegatingPayloadFileSystem files = new()
        {
            MoveFailure = (source, destination) =>
            {
                if (source.Contains(".installing.", StringComparison.OrdinalIgnoreCase)
                    && destination.Equals(tree.Install, StringComparison.OrdinalIgnoreCase))
                {
                    return new IOException("activate move failed");
                }

                if (source.Contains(".rollback.", StringComparison.OrdinalIgnoreCase)
                    && destination.Equals(tree.Install, StringComparison.OrdinalIgnoreCase))
                {
                    restoreAttempts++;
                    return restoreAttempts == 1
                        ? new UnauthorizedAccessException("old service handle still releasing")
                        : null;
                }

                return null;
            },
        };

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => transaction.ActivateAsync(CancellationToken.None));

        Assert.Equal("activate move failed", failure.Message);
        Assert.Equal(2, restoreAttempts);
        Assert.Equal(before, ReadManifest(tree.Install));
        Assert.False(transaction.IsActivated);
    }

    [Fact]
    public async Task Activate_ActivationAndRestoreMoveFailures_RetainsBackupAndReportsBoth()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [3, 2, 1]);
        DelegatingPayloadFileSystem files = new()
        {
            MoveFailure = (source, destination) =>
            {
                if (destination.Equals(tree.Install, StringComparison.OrdinalIgnoreCase))
                {
                    return source.Contains(".installing.", StringComparison.OrdinalIgnoreCase)
                        ? new IOException("activate move failed")
                        : new IOException("restore move failed");
                }

                return null;
            },
        };

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => transaction.ActivateAsync(CancellationToken.None));

        Assert.Equal("activate move failed", failure.InnerExceptions[0].Message);
        Assert.Equal("restore move failed", failure.InnerExceptions[1].Message);
        string backup = Assert.Single(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
        Assert.True(File.Exists(Path.Combine(backup, "old.bin")));
        Assert.True(transaction.IsActivated);
    }

    [Fact]
    public async Task Activate_CancellationBetweenRenames_RestoresOldTreeAndLeavesTransactionRollbackable()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [8, 6, 4, 2]);
        SortedDictionary<string, string> before = ReadManifest(tree.Install);
        using CancellationTokenSource cancellation = new();
        DelegatingPayloadFileSystem files = new()
        {
            AfterMove = (source, destination) =>
            {
                if (source.Equals(tree.Install, StringComparison.OrdinalIgnoreCase)
                    && destination.Contains(".rollback.", StringComparison.OrdinalIgnoreCase))
                {
                    cancellation.Cancel();
                }
            },
        };

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transaction.ActivateAsync(cancellation.Token));

        Assert.Equal(before, ReadManifest(tree.Install));
        Assert.False(transaction.IsActivated);
        await transaction.RollbackAsync(CancellationToken.None);
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.installing.*"));
    }

    [Fact]
    public async Task Rollback_AfterSuccessfulActivation_RestoresExactOldTreeAndRemovesNewTree()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [5, 5, 5]);
        tree.WriteInstall("hostfxr.dll", [6, 6, 6]);
        tree.WriteInstall(@"host\fxr\10.0.1\hostfxr.dll", [7, 7, 7]);
        tree.WriteInstall(@"shared\Microsoft.NETCore.App\10.0.1\coreclr.dll", [8, 8, 8]);
        tree.WriteInstall(
            @"shared\Microsoft.WindowsDesktop.App\10.0.1\System.Windows.Forms.dll",
            [9, 9, 9]);
        SortedDictionary<string, string> before = ReadManifest(tree.Install);

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform().StagePayloadAsync(CancellationToken.None);
        await transaction.ActivateAsync(CancellationToken.None);
        await transaction.RollbackAsync(CancellationToken.None);

        Assert.Equal(before, ReadManifest(tree.Install));
        Assert.True(File.Exists(Path.Combine(tree.Install, "hostfxr.dll")));
        Assert.True(File.Exists(Path.Combine(
            tree.Install,
            @"shared\Microsoft.NETCore.App\10.0.1\coreclr.dll")));
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.failed.*"));
    }

    [Fact]
    public async Task Rollback_TransientBackupRestoreFailure_RetriesAndRestoresOldTree()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [2, 7, 1, 8]);
        SortedDictionary<string, string> before = ReadManifest(tree.Install);
        int restoreAttempts = 0;
        DelegatingPayloadFileSystem files = new()
        {
            MoveFailure = (source, destination) =>
            {
                if (!source.Contains(".rollback.", StringComparison.OrdinalIgnoreCase)
                    || !destination.Equals(tree.Install, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                restoreAttempts++;
                return restoreAttempts == 1
                    ? new IOException("restore handle still releasing")
                    : null;
            },
        };

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        await transaction.ActivateAsync(CancellationToken.None);
        await transaction.RollbackAsync(CancellationToken.None);

        Assert.Equal(2, restoreAttempts);
        Assert.Equal(before, ReadManifest(tree.Install));
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.failed.*"));
    }

    [Fact]
    public async Task Rollback_FailedTreeCleanupFailureKeepsRestoredTreeTerminal()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [9, 8, 7]);
        SortedDictionary<string, string> before = ReadManifest(tree.Install);
        DelegatingPayloadFileSystem files = new()
        {
            DeleteFailure = path => path.Contains(".failed.", StringComparison.OrdinalIgnoreCase)
                ? new IOException("injected failed-tree cleanup failure")
                : null,
        };

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        await transaction.ActivateAsync(CancellationToken.None);

        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => transaction.RollbackAsync(CancellationToken.None));

        Assert.Contains("was restored", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, ReadManifest(tree.Install));
        Assert.False(transaction.IsActivated);
        Assert.Single(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.failed.*"));
        await transaction.RollbackAsync(CancellationToken.None);
        Assert.Equal(before, ReadManifest(tree.Install));
    }

    [Fact]
    public async Task Rollback_StrandedBackupCleanupFailureKeepsRestoredTreeTerminal()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [4, 2, 4, 2]);
        SortedDictionary<string, string> before = ReadManifest(tree.Install);
        bool permitBackupRestore = false;
        DelegatingPayloadFileSystem files = new()
        {
            MoveFailure = (source, destination) =>
            {
                if (!destination.Equals(tree.Install, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (source.Contains(".installing.", StringComparison.OrdinalIgnoreCase))
                {
                    return new IOException("injected activation failure");
                }

                return source.Contains(".rollback.", StringComparison.OrdinalIgnoreCase)
                    && !permitBackupRestore
                        ? new IOException("injected initial restore failure")
                        : null;
            },
            DeleteFailure = path => path.Contains(".installing.", StringComparison.OrdinalIgnoreCase)
                ? new IOException("injected staging cleanup failure")
                : null,
        };

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        await Assert.ThrowsAsync<AggregateException>(
            () => transaction.ActivateAsync(CancellationToken.None));
        permitBackupRestore = true;

        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => transaction.RollbackAsync(CancellationToken.None));

        Assert.Contains("was restored", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, ReadManifest(tree.Install));
        Assert.False(transaction.IsActivated);
        Assert.Single(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.installing.*"));
        await transaction.RollbackAsync(CancellationToken.None);
        Assert.Equal(before, ReadManifest(tree.Install));
    }

    [Fact]
    public async Task Commit_BackupCleanupFailure_IsPointOfNoReturnAndKeepsHealthyNewTree()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [1, 3, 5]);
        SortedDictionary<string, string> expected = ReadManifest(tree.Payload);
        DelegatingPayloadFileSystem files = new()
        {
            DeleteFailure = path => path.Contains(".rollback.", StringComparison.OrdinalIgnoreCase)
                ? new IOException("injected backup cleanup failure")
                : null,
        };

        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        await transaction.ActivateAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        Assert.Equal(expected, ReadManifest(tree.Install));
        Assert.False(transaction.IsActivated);
        Assert.NotNull(transaction.CommitWarning);
        Assert.Contains("rollback", transaction.CommitWarning, StringComparison.OrdinalIgnoreCase);
        Assert.True(transaction.CommitWarning.Length <= 512);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.RollbackAsync(CancellationToken.None));
        Assert.Single(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
    }

    [Fact]
    public async Task Dispose_UnactivatedStagingCleanupFailureIsBestEffortAndRetainsSibling()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        DelegatingPayloadFileSystem files = new()
        {
            DeleteFailure = path => path.Contains(".installing.", StringComparison.OrdinalIgnoreCase)
                ? new IOException("injected disposal cleanup failure")
                : null,
        };
        ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);

        await transaction.DisposeAsync();

        Assert.Single(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.installing.*"));
    }

    [Fact]
    public async Task Runner_OriginalAndRollbackFailuresSurviveRealTransactionDisposeCleanupRetry()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        DelegatingPayloadFileSystem files = new()
        {
            DeleteFailure = path => path.Contains(".installing.", StringComparison.OrdinalIgnoreCase)
                ? new IOException("staging cleanup failed")
                : null,
        };
        ISetupPayloadTransaction transaction =
            await tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None);
        SetupCommandRunner runner = new(new CaptureFailingSetupPlatform(transaction));

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => runner.RunAsync(["install"], CancellationToken.None));

        Assert.Equal("lifecycle capture failed", failure.InnerExceptions[0].Message);
        Assert.Contains(failure.InnerExceptions, exception => exception.Message == "staging cleanup failed");
        Assert.Single(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.installing.*"));
    }

    [Fact]
    public async Task CommitAndRollback_PreCancelledBoundariesDoNotMutateAndRollbackCanRetry()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [2, 4, 6, 8]);
        SortedDictionary<string, string> before = ReadManifest(tree.Install);
        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform().StagePayloadAsync(CancellationToken.None);
        await transaction.ActivateAsync(CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transaction.CommitAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transaction.RollbackAsync(cancellation.Token));
        Assert.True(transaction.IsActivated);

        await transaction.RollbackAsync(CancellationToken.None);
        Assert.Equal(before, ReadManifest(tree.Install));
    }

    [Fact]
    public async Task StagePayload_SourceMutationDuringCopy_IsRejectedWithoutTouchingLiveTree()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [4, 4, 4]);
        string mutableSource = Path.Combine(tree.Payload, "OpenBlade.Tray.dll");
        DelegatingPayloadFileSystem files = new()
        {
            AfterCopy = (source, _) =>
            {
                if (source.Equals(mutableSource, StringComparison.OrdinalIgnoreCase))
                {
                    File.AppendAllText(source, "changed");
                }
            },
        };

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => tree.CreatePlatform(files).StagePayloadAsync(CancellationToken.None));

        Assert.Contains("changed while staging", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(tree.Install, "old.bin")));
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.installing.*"));
    }

    [Fact]
    public async Task StagePayload_CancellationDuringCopy_CleansStagingAndLeavesLiveTree()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        tree.WriteInstall("old.bin", [7]);
        using CancellationTokenSource cancellation = new();
        DelegatingPayloadFileSystem files = new()
        {
            AfterCopy = (_, _) => cancellation.Cancel(),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tree.CreatePlatform(files).StagePayloadAsync(cancellation.Token));

        Assert.True(File.Exists(Path.Combine(tree.Install, "old.bin")));
        Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.installing.*"));
    }

    [Fact]
    public async Task Transaction_EnforcesStateBoundariesAndSafeTerminalIdempotence()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        await using ISetupPayloadTransaction transaction =
            await tree.CreatePlatform().StagePayloadAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.CommitAsync(CancellationToken.None));
        await transaction.ActivateAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.ActivateAsync(CancellationToken.None));
        await transaction.RollbackAsync(CancellationToken.None);
        await transaction.RollbackAsync(CancellationToken.None);

        Assert.False(Directory.Exists(tree.Install));
    }

    [Fact]
    public async Task StagePayload_AmbiguousTransactionSibling_FailsClosedAndPreservesIt()
    {
        using TestTree tree = new();
        tree.CreateCompletePayload();
        string ambiguous = Path.Combine(tree.ProgramFiles, $"OpenBlade.rollback.{Guid.NewGuid():N}");
        Directory.CreateDirectory(ambiguous);
        File.WriteAllText(Path.Combine(ambiguous, "evidence.txt"), "preserve");

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tree.CreatePlatform().StagePayloadAsync(CancellationToken.None));

        Assert.Contains("transaction", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(ambiguous, "evidence.txt")));
    }

    private static SortedDictionary<string, string> ReadManifest(string root)
    {
        SortedDictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            result[Path.GetRelativePath(root, file)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return result;
    }

    private sealed class TestTree : IDisposable
    {
        private static readonly string[] RequiredFiles =
        [
            "OpenBlade.Service.exe",
            "OpenBlade.Service.dll",
            "OpenBlade.Service.deps.json",
            "OpenBlade.Service.runtimeconfig.json",
            "OpenBlade.Platform.dll",
            "OpenBlade.SessionAgent.exe",
            "OpenBlade.SessionAgent.dll",
            "OpenBlade.SessionAgent.deps.json",
            "OpenBlade.SessionAgent.runtimeconfig.json",
            "OpenBlade.Tray.exe",
            "OpenBlade.Tray.dll",
            "OpenBlade.Tray.deps.json",
            "OpenBlade.Tray.runtimeconfig.json",
            "OpenBlade.Tray.pri",
            "App.xbf",
            "MainWindow.xbf",
            "Microsoft.WindowsAppRuntime.dll",
            "Microsoft.WindowsAppRuntime.pri",
            "Microsoft.ui.xaml.dll",
            "Microsoft.UI.Xaml.Controls.dll",
            "Microsoft.UI.Xaml.Controls.pri",
            @"Microsoft.UI.Xaml\Assets\NoiseAsset_256x256_PNG.png",
        ];

        public TestTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "OpenBlade.Setup.Tests", Guid.NewGuid().ToString("N"));
            ProgramFiles = Path.Combine(Root, "ProgramFiles");
            Payload = Path.Combine(Root, "payload");
            Directory.CreateDirectory(ProgramFiles);
            Directory.CreateDirectory(Payload);
            Paths = SetupPaths.Create(
                ProgramFiles,
                Path.Combine(Root, "ProgramData"),
                Path.Combine(Root, "LocalData"),
                Path.Combine(Root, "Startup"));
        }

        public string Root { get; }
        public string ProgramFiles { get; }
        public string Payload { get; }
        public string Install => Paths.InstallDirectory;
        public SetupPaths Paths { get; }

        public void CreateCompletePayload()
        {
            for (int index = 0; index < RequiredFiles.Length; index++)
            {
                Write(Payload, RequiredFiles[index], [(byte)(index + 1), 0xA5, 0x5A]);
            }

            Write(Payload, @"locales\en-US\resources.pri", [0x10, 0x20]);
        }

        public void WriteInstall(string relativePath, byte[] bytes) => Write(Install, relativePath, bytes);

        public WindowsSetupPlatform CreatePlatform(
            ISetupPayloadFileSystem? files = null,
            ISetupProgressReporter? progressReporter = null) =>
            WindowsSetupPlatformTestFactory.Create(
                paths: Paths,
                payloadDirectory: Payload,
                payloadFileSystem: files,
                progressReporter: progressReporter);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static void Write(string root, string relativePath, byte[] bytes)
        {
            string path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }

    private sealed class DelegatingPayloadFileSystem : ISetupPayloadFileSystem
    {
        private readonly WindowsSetupPayloadFileSystem inner = new();

        public Func<string, string, Exception?>? CopyFailure { get; init; }
        public Func<string, string, Exception?>? MoveFailure { get; init; }
        public Func<string, Exception?>? DeleteFailure { get; init; }
        public Action<string, string>? AfterCopy { get; init; }
        public Action<string, string>? AfterMove { get; init; }

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);
        public IEnumerable<string> EnumerateFiles(string path) => inner.EnumerateFiles(path);
        public IEnumerable<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public long GetFileLength(string path) => inner.GetFileLength(path);
        public Stream OpenRead(string path) => inner.OpenRead(path);

        public void CopyFile(string source, string destination)
        {
            Exception? failure = CopyFailure?.Invoke(source, destination);
            if (failure is not null)
            {
                throw failure;
            }

            inner.CopyFile(source, destination);
            AfterCopy?.Invoke(source, destination);
        }

        public void MoveDirectory(string source, string destination)
        {
            Exception? failure = MoveFailure?.Invoke(source, destination);
            if (failure is not null)
            {
                throw failure;
            }

            inner.MoveDirectory(source, destination);
            AfterMove?.Invoke(source, destination);
        }

        public void DeleteDirectory(string path)
        {
            Exception? failure = DeleteFailure?.Invoke(path);
            if (failure is not null)
            {
                throw failure;
            }

            inner.DeleteDirectory(path);
        }
    }

    private sealed class CaptureFailingSetupPlatform(ISetupPayloadTransaction transaction) : ISetupPlatform
    {
        public string CurrentUserSid => throw new NotSupportedException();

        public Task<ISetupPayloadTransaction> StagePayloadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(transaction);

        public Task<SetupInstallLifecycleState> CaptureInstallLifecycleStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromException<SetupInstallLifecycleState>(
                new InvalidOperationException("lifecycle capture failed"));

        public Task PrepareServiceForInstallAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RestoreServiceInstallStateAsync(
            SetupServiceInstallState priorState,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task InstallUninstallerAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task WriteAuthorizedUserSidAsync(string sid, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task InstallServiceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task VerifyServiceHealthAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InstallStartupShortcutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveStartupShortcutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InstallSessionAgentStartupShortcutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveSessionAgentStartupShortcutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InstallStartMenuShortcutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveStartMenuShortcutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InstallUninstallShortcutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveUninstallShortcutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InstallUninstallRegistrationAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveUninstallRegistrationAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InstallBundleOwnershipAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RestoreBundleOwnershipAsync(
            BundleOwnershipIdentity ownership,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveBundleOwnershipAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RestoreDynamicLightingIdentityAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveDynamicLightingIdentityAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveServiceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopTrayAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopSessionAgentAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveBinariesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveDataAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
