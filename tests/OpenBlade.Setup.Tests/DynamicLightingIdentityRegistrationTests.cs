using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class DynamicLightingIdentityRegistrationTests
{
    private const string Publisher = "CN=OpenBlade Identity Test";

    [Fact]
    public async Task AbsentArtifactPair_IsAnIdempotentNoOp()
    {
        using TemporaryInstallDirectory install = new();
        RecordingPackageManager packages = new();
        DynamicLightingIdentityRegistration registration = new(packages);

        bool registered = await registration.IsRegisteredAsync(
            install.Path,
            "S-1-5-21-test",
            CancellationToken.None);
        await registration.InstallIfPresentAsync(
            install.Path,
            "S-1-5-21-test",
            CancellationToken.None);
        await registration.RemoveIfPresentAsync(
            install.Path,
            "S-1-5-21-test",
            CancellationToken.None);

        Assert.False(registered);
        Assert.Empty(packages.FindRequests);
        Assert.Empty(packages.AddRequests);
        Assert.Empty(packages.RemovedPackageFullNames);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialArtifactPair_FailsBeforePackageManagerMutation(bool packageOnly)
    {
        using TemporaryInstallDirectory install = new();
        if (packageOnly)
        {
            File.WriteAllBytes(
                System.IO.Path.Combine(install.Path, "OpenBlade.DynamicLighting.msix"),
                []);
        }
        else
        {
            File.WriteAllText(
                System.IO.Path.Combine(
                    install.Path,
                    "OpenBlade.DynamicLighting.identity.json"),
                "{}",
                Encoding.UTF8);
        }
        RecordingPackageManager packages = new();
        DynamicLightingIdentityRegistration registration = new(packages);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => registration.InstallIfPresentAsync(
                install.Path,
                "S-1-5-21-test",
                CancellationToken.None));

        Assert.Contains("both be present", failure.Message, StringComparison.Ordinal);
        Assert.Empty(packages.FindRequests);
    }

    [Fact]
    public async Task Install_ValidPairReplacesOnlyExactPublisherScopeAndUsesExternalLocation()
    {
        using TemporaryInstallDirectory install = new();
        WriteArtifacts(install.Path);
        RecordingPackageManager packages = new();
        packages.PackageFullNames.Add("OSSBlade.OpenBlade.DynamicLighting_0.0.1.0_x64_test");
        DynamicLightingIdentityRegistration registration = new(packages);

        await registration.InstallIfPresentAsync(
            install.Path,
            "S-1-5-21-test",
            CancellationToken.None);

        Assert.Equal(
            ["OSSBlade.OpenBlade.DynamicLighting_0.0.1.0_x64_test"],
            packages.RemovedPackageFullNames);
        Assert.All(
            packages.FindRequests,
            request =>
            {
                Assert.Equal("S-1-5-21-test", request.UserSid);
                Assert.Equal("OSSBlade.OpenBlade.DynamicLighting", request.PackageName);
                Assert.Equal(Publisher, request.Publisher);
            });
        PackageAddRequest add = Assert.Single(packages.AddRequests);
        Assert.Equal(
            System.IO.Path.Combine(install.Path, "OpenBlade.DynamicLighting.msix"),
            add.PackageUri.LocalPath,
            ignoreCase: true);
        Assert.Equal(
            System.IO.Path.TrimEndingDirectorySeparator(install.Path),
            System.IO.Path.TrimEndingDirectorySeparator(add.ExternalLocationUri.LocalPath),
            ignoreCase: true);
        Assert.Single(packages.PackageFullNames);
    }

    [Fact]
    public async Task Remove_DeletesEveryExactPublisherMatchAndVerifiesAbsence()
    {
        using TemporaryInstallDirectory install = new();
        WriteArtifacts(install.Path);
        RecordingPackageManager packages = new();
        packages.PackageFullNames.Add("exact-old");
        packages.PackageFullNames.Add("exact-new");
        DynamicLightingIdentityRegistration registration = new(packages);

        await registration.RemoveIfPresentAsync(
            install.Path,
            "S-1-5-21-test",
            CancellationToken.None);

        Assert.Equal(["exact-old", "exact-new"], packages.RemovedPackageFullNames);
        Assert.Empty(packages.PackageFullNames);
        Assert.Equal(2, packages.FindRequests.Count);
    }

    [Fact]
    public async Task ManifestPublisherMismatch_FailsBeforeLookingUpOrRemovingPackages()
    {
        using TemporaryInstallDirectory install = new();
        WriteArtifacts(install.Path, manifestPublisher: "CN=Different Publisher");
        RecordingPackageManager packages = new();
        DynamicLightingIdentityRegistration registration = new(packages);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => registration.RemoveIfPresentAsync(
                install.Path,
                "S-1-5-21-test",
                CancellationToken.None));

        Assert.Contains("reviewed descriptor", failure.Message, StringComparison.Ordinal);
        Assert.Empty(packages.FindRequests);
    }

    [Fact]
    public async Task DescriptorWithUnreviewedProperty_FailsClosed()
    {
        using TemporaryInstallDirectory install = new();
        WriteArtifacts(install.Path);
        string descriptorPath = System.IO.Path.Combine(
            install.Path,
            "OpenBlade.DynamicLighting.identity.json");
        File.WriteAllText(
            descriptorPath,
            """
            {
              "schemaVersion": 1,
              "packageName": "OSSBlade.OpenBlade.DynamicLighting",
              "publisher": "CN=OpenBlade Identity Test",
              "applicationId": "SessionAgent",
              "packageFile": "OpenBlade.DynamicLighting.msix",
              "externalLocation": "OpenBlade install directory",
              "allowUnsigned": true
            }
            """,
            Encoding.UTF8);
        RecordingPackageManager packages = new();
        DynamicLightingIdentityRegistration registration = new(packages);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => registration.IsRegisteredAsync(
                install.Path,
                "S-1-5-21-test",
                CancellationToken.None));

        Assert.Empty(packages.FindRequests);
    }

    [Fact]
    public async Task ManifestDtd_IsRejectedBeforePackageManagerAccess()
    {
        using TemporaryInstallDirectory install = new();
        WriteArtifacts(
            install.Path,
            manifestPrefix: "<!DOCTYPE Package [<!ENTITY xxe SYSTEM \"file:///private\">]>");
        RecordingPackageManager packages = new();
        DynamicLightingIdentityRegistration registration = new(packages);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => registration.InstallIfPresentAsync(
                install.Path,
                "S-1-5-21-test",
                CancellationToken.None));

        Assert.Empty(packages.FindRequests);
    }

    [Fact]
    public async Task DeploymentFailure_ReportsOnlyBoundedOperationAndHResult()
    {
        using TemporaryInstallDirectory install = new();
        WriteArtifacts(install.Path);
        RecordingPackageManager packages = new()
        {
            AddResult = new(unchecked((int)0x80073CF9), IsRegistered: false),
        };
        DynamicLightingIdentityRegistration registration = new(packages);

        InvalidOperationException failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => registration.InstallIfPresentAsync(
                install.Path,
                "S-1-5-21-test",
                CancellationToken.None));

        Assert.Contains("0x80073CF9", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(install.Path, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Publisher, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageManagerException_IsReducedToTypeAndHResult()
    {
        using TemporaryInstallDirectory install = new();
        WriteArtifacts(install.Path);
        RecordingPackageManager packages = new()
        {
            AddFailure = new IOException($"private deployment detail: {install.Path}"),
        };
        DynamicLightingIdentityRegistration registration = new(packages);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registration.InstallIfPresentAsync(
                install.Path,
                "S-1-5-21-test",
                CancellationToken.None));

        Assert.Contains("IOException", failure.Message, StringComparison.Ordinal);
        Assert.Contains("HRESULT", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(install.Path, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task DeploymentTimeout_IsBoundedAndDoesNotExposeIdentityData()
    {
        using TemporaryInstallDirectory install = new();
        WriteArtifacts(install.Path);
        RecordingPackageManager packages = new() { NeverCompleteAdd = true };
        DynamicLightingIdentityRegistration registration = new(
            packages,
            TimeSpan.FromMilliseconds(50));

        TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(
            () => registration.InstallIfPresentAsync(
                install.Path,
                "S-1-5-21-test",
                CancellationToken.None));

        Assert.Contains("bounded timeout", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(install.Path, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Publisher, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellation_IsNotConvertedToTimeoutOrDeploymentFailure()
    {
        using TemporaryInstallDirectory install = new();
        WriteArtifacts(install.Path);
        RecordingPackageManager packages = new() { NeverCompleteAdd = true };
        DynamicLightingIdentityRegistration registration = new(
            packages,
            TimeSpan.FromSeconds(5));
        using CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registration.InstallIfPresentAsync(
                install.Path,
                "S-1-5-21-test",
                cancellation.Token));
    }

    [Fact]
    public async Task RelativeExternalLocation_IsRejected()
    {
        DynamicLightingIdentityRegistration registration =
            new(new RecordingPackageManager());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => registration.IsRegisteredAsync(
                "OpenBlade",
                "S-1-5-21-test",
                CancellationToken.None));
    }

    internal static void WriteArtifacts(
        string installDirectory,
        string manifestPublisher = Publisher,
        string manifestPrefix = "")
    {
        string descriptor = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                packageName = "OSSBlade.OpenBlade.DynamicLighting",
                publisher = Publisher,
                applicationId = "SessionAgent",
                packageFile = "OpenBlade.DynamicLighting.msix",
                externalLocation = "OpenBlade install directory",
            });
        File.WriteAllText(
            System.IO.Path.Combine(
                installDirectory,
                "OpenBlade.DynamicLighting.identity.json"),
            descriptor,
            Encoding.UTF8);

        string manifest =
            $$"""
            <?xml version="1.0" encoding="utf-8"?>
            {{manifestPrefix}}
            <Package
              xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
              xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
              xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
              xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
              <Identity
                Name="OSSBlade.OpenBlade.DynamicLighting"
                Publisher="{{manifestPublisher}}"
                Version="0.1.0.0"
                ProcessorArchitecture="x64" />
              <Properties>
                <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
              </Properties>
              <Applications>
                <Application Id="SessionAgent" Executable="OpenBlade.SessionAgent.exe">
                  <Extensions>
                    <uap3:Extension Category="windows.appExtension">
                      <uap3:AppExtension
                        Name="com.microsoft.windows.lighting"
                        Id="OpenBladeDynamicLighting"
                        PublicFolder="public" />
                    </uap3:Extension>
                  </Extensions>
                </Application>
              </Applications>
              <Capabilities>
                <rescap:Capability Name="runFullTrust" />
              </Capabilities>
            </Package>
            """;
        string packagePath = System.IO.Path.Combine(
            installDirectory,
            "OpenBlade.DynamicLighting.msix");
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        ZipArchiveEntry manifestEntry = archive.CreateEntry(
            "AppxManifest.xml",
            CompressionLevel.NoCompression);
        using StreamWriter writer = new(
            manifestEntry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(manifest);
    }

    private sealed class RecordingPackageManager : IDynamicLightingPackageManager
    {
        public List<PackageFindRequest> FindRequests { get; } = [];
        public List<PackageAddRequest> AddRequests { get; } = [];
        public List<string> RemovedPackageFullNames { get; } = [];
        public List<string> PackageFullNames { get; } = [];
        public DynamicLightingDeploymentResult AddResult { get; init; } =
            new(0, IsRegistered: true);
        public DynamicLightingDeploymentResult RemoveResult { get; init; } =
            new(0, IsRegistered: false);
        public bool NeverCompleteAdd { get; init; }
        public Exception? AddFailure { get; init; }

        public IReadOnlyList<string> FindPackageFullNamesForUser(
            string userSid,
            string packageName,
            string publisher)
        {
            FindRequests.Add(new(userSid, packageName, publisher));
            return [.. PackageFullNames];
        }

        public async Task<DynamicLightingDeploymentResult> AddPackageAsync(
            Uri packageUri,
            Uri externalLocationUri,
            CancellationToken cancellationToken)
        {
            AddRequests.Add(new(packageUri, externalLocationUri));
            if (AddFailure is not null)
            {
                throw AddFailure;
            }
            if (NeverCompleteAdd)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (AddResult.HResult >= 0 && AddResult.IsRegistered)
            {
                PackageFullNames.Clear();
                PackageFullNames.Add("OSSBlade.OpenBlade.DynamicLighting_0.1.0.0_x64_test");
            }
            return AddResult;
        }

        public Task<DynamicLightingDeploymentResult> RemovePackageAsync(
            string packageFullName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemovedPackageFullNames.Add(packageFullName);
            if (RemoveResult.HResult >= 0)
            {
                PackageFullNames.Remove(packageFullName);
            }
            return Task.FromResult(RemoveResult);
        }
    }

    private sealed record PackageFindRequest(
        string UserSid,
        string PackageName,
        string Publisher);

    private sealed record PackageAddRequest(
        Uri PackageUri,
        Uri ExternalLocationUri);

    private sealed class TemporaryInstallDirectory : IDisposable
    {
        private const string Prefix = "OpenBlade.DynamicLighting.Tests.";

        public TemporaryInstallDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            string fullPath = System.IO.Path.GetFullPath(Path);
            string expectedParent = System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (Directory.Exists(fullPath)
                && string.Equals(
                    System.IO.Path.GetDirectoryName(fullPath),
                    expectedParent,
                    StringComparison.OrdinalIgnoreCase)
                && System.IO.Path.GetFileName(fullPath).StartsWith(
                    Prefix,
                    StringComparison.Ordinal))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
    }
}
