using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using OpenBlade.Contracts;
using Windows.Management.Deployment;

namespace OpenBlade.Setup;

public sealed record DynamicLightingDeploymentResult(int HResult, bool IsRegistered);

public interface IDynamicLightingPackageManager
{
    IReadOnlyList<string> FindPackageFullNamesForUser(
        string userSid,
        string packageName,
        string publisher);

    Task<DynamicLightingDeploymentResult> AddPackageAsync(
        Uri packageUri,
        Uri externalLocationUri,
        CancellationToken cancellationToken);

    Task<DynamicLightingDeploymentResult> RemovePackageAsync(
        string packageFullName,
        CancellationToken cancellationToken);
}

public interface IDynamicLightingIdentityRegistration
{
    Task<bool> IsRegisteredAsync(
        string installDirectory,
        string userSid,
        CancellationToken cancellationToken);

    Task InstallIfPresentAsync(
        string installDirectory,
        string userSid,
        CancellationToken cancellationToken);

    Task RemoveIfPresentAsync(
        string installDirectory,
        string userSid,
        CancellationToken cancellationToken);
}

public sealed class WindowsDynamicLightingPackageManager : IDynamicLightingPackageManager
{
    private readonly PackageManager packageManager = new();

    public IReadOnlyList<string> FindPackageFullNamesForUser(
        string userSid,
        string packageName,
        string publisher) =>
        packageManager
            .FindPackagesForUser(userSid, packageName, publisher)
            .Select(package => package.Id.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public async Task<DynamicLightingDeploymentResult> AddPackageAsync(
        Uri packageUri,
        Uri externalLocationUri,
        CancellationToken cancellationToken)
    {
        AddPackageOptions options = new()
        {
            ExternalLocationUri = externalLocationUri,
        };
        DeploymentResult result = await packageManager
            .AddPackageByUriAsync(packageUri, options)
            .AsTask(cancellationToken);
        return ToResult(result);
    }

    public async Task<DynamicLightingDeploymentResult> RemovePackageAsync(
        string packageFullName,
        CancellationToken cancellationToken)
    {
        DeploymentResult result = await packageManager
            .RemovePackageAsync(packageFullName, RemovalOptions.None)
            .AsTask(cancellationToken);
        return ToResult(result);
    }

    private static DynamicLightingDeploymentResult ToResult(DeploymentResult result) =>
        new(result.ExtendedErrorCode?.HResult ?? 0, result.IsRegistered);
}

public sealed class DynamicLightingIdentityRegistration(
    IDynamicLightingPackageManager? packageManager = null,
    TimeSpan? deploymentTimeout = null)
    : IDynamicLightingIdentityRegistration
{
    internal const string PackageName = "OSSBlade.OpenBlade.DynamicLighting";
    internal const string ApplicationId = "SessionAgent";
    internal const string PackageFileName = "OpenBlade.DynamicLighting.msix";
    internal const string DescriptorFileName = "OpenBlade.DynamicLighting.identity.json";
    private const string ExternalLocation = "OpenBlade install directory";
    private const string SessionAgentExecutable = "OpenBlade.SessionAgent.exe";
    private const string LightingExtensionName = "com.microsoft.windows.lighting";
    private const string LightingExtensionId = "OpenBladeDynamicLighting";
    private const string PublicFolder = "public";
    private const string FoundationNamespace =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private const string Uap3Namespace =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
    private const string Uap10Namespace =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private const string RestrictedCapabilitiesNamespace =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
    private const int MaximumDescriptorBytes = 16 * 1024;
    private const int MaximumManifestBytes = 256 * 1024;
    private static readonly TimeSpan DefaultDeploymentTimeout = TimeSpan.FromSeconds(30);
    private readonly IDynamicLightingPackageManager packageManager =
        packageManager ?? new WindowsDynamicLightingPackageManager();
    private readonly TimeSpan deploymentTimeout = ValidateTimeout(deploymentTimeout);

    public Task<bool> IsRegisteredAsync(
        string installDirectory,
        string userSid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DynamicLightingIdentityArtifacts? artifacts = LoadArtifactsIfPresent(installDirectory);
        if (artifacts is null)
        {
            return Task.FromResult(false);
        }

        IReadOnlyList<string> packages = FindPackages(userSid, artifacts.Publisher);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(packages.Count > 0);
    }

    public async Task InstallIfPresentAsync(
        string installDirectory,
        string userSid,
        CancellationToken cancellationToken)
    {
        DynamicLightingIdentityArtifacts? artifacts = LoadArtifactsIfPresent(installDirectory);
        if (artifacts is null)
        {
            return;
        }

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deploymentTimeout);
        try
        {
            await RemovePackagesAsync(userSid, artifacts.Publisher, timeout.Token);
            DynamicLightingDeploymentResult result = await packageManager.AddPackageAsync(
                new Uri(artifacts.PackagePath),
                new Uri(artifacts.InstallDirectory + Path.DirectorySeparatorChar),
                timeout.Token);
            ThrowIfDeploymentFailed(result, "registration");
            if (!result.IsRegistered
                || FindPackages(userSid, artifacts.Publisher).Count == 0)
            {
                throw new InvalidOperationException(
                    "The Dynamic Lighting identity registration did not become active.");
            }
        }
        catch (DynamicLightingIdentityDeploymentException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The Dynamic Lighting identity registration exceeded its bounded timeout.",
                exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(nameof(DynamicLightingIdentityRegistration), nameof(InstallIfPresentAsync), exception);
            throw new InvalidOperationException(
                "The Dynamic Lighting identity registration failed "
                + $"({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}).");
        }
    }

    public async Task RemoveIfPresentAsync(
        string installDirectory,
        string userSid,
        CancellationToken cancellationToken)
    {
        DynamicLightingIdentityArtifacts? artifacts = LoadArtifactsIfPresent(installDirectory);
        if (artifacts is null)
        {
            return;
        }

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deploymentTimeout);
        try
        {
            await RemovePackagesAsync(userSid, artifacts.Publisher, timeout.Token);
            if (FindPackages(userSid, artifacts.Publisher).Count != 0)
            {
                throw new InvalidOperationException(
                    "The Dynamic Lighting identity registration remained active after removal.");
            }
        }
        catch (DynamicLightingIdentityDeploymentException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The Dynamic Lighting identity removal exceeded its bounded timeout.",
                exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(nameof(DynamicLightingIdentityRegistration), nameof(RemoveIfPresentAsync), exception);
            throw new InvalidOperationException(
                "The Dynamic Lighting identity removal failed "
                + $"({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}).");
        }
    }

    private async Task RemovePackagesAsync(
        string userSid,
        string publisher,
        CancellationToken cancellationToken)
    {
        foreach (string packageFullName in FindPackages(userSid, publisher))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DynamicLightingDeploymentResult result = await packageManager.RemovePackageAsync(
                packageFullName,
                cancellationToken);
            ThrowIfDeploymentFailed(result, "removal");
        }
    }

    private IReadOnlyList<string> FindPackages(string userSid, string publisher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        try
        {
            return packageManager.FindPackageFullNamesForUser(
                userSid,
                PackageName,
                publisher);
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(nameof(DynamicLightingIdentityRegistration), nameof(FindPackages), exception);
            throw new InvalidOperationException(
                "The Dynamic Lighting identity registration state could not be queried "
                + $"({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}).");
        }
    }

    private static void ThrowIfDeploymentFailed(
        DynamicLightingDeploymentResult result,
        string operation)
    {
        if (result.HResult < 0)
        {
            throw new DynamicLightingIdentityDeploymentException(
                $"The Dynamic Lighting identity {operation} failed "
                + $"(HRESULT 0x{result.HResult:X8}).",
                result.HResult);
        }
    }

    internal static DynamicLightingIdentityArtifacts? LoadArtifactsIfPresent(
        string installDirectory) =>
        LoadArtifactsIfPresent(
            installDirectory,
            File.Exists,
            path => new FileInfo(path).Length,
            path => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));

    internal static void RejectRetiredPayloadArtifacts(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        HashSet<string> manifest = new(relativePaths, StringComparer.OrdinalIgnoreCase);
        if (manifest.Contains(PackageFileName) || manifest.Contains(DescriptorFileName))
        {
            throw new InvalidDataException(
                "The retired Dynamic Lighting identity package and descriptor must not "
                + "be present in an OpenBlade setup payload.");
        }
    }

    private static DynamicLightingIdentityArtifacts? LoadArtifactsIfPresent(
        string installDirectory,
        Func<string, bool> fileExists,
        Func<string, long> getFileLength,
        Func<string, Stream> openRead)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        string normalizedInstallDirectory = NormalizeDirectory(installDirectory);
        string packagePath = ResolveContainedFile(
            normalizedInstallDirectory,
            PackageFileName);
        string descriptorPath = ResolveContainedFile(
            normalizedInstallDirectory,
            DescriptorFileName);
        bool packageExists = fileExists(packagePath);
        bool descriptorExists = fileExists(descriptorPath);
        if (!packageExists && !descriptorExists)
        {
            return null;
        }
        if (packageExists != descriptorExists)
        {
            throw new InvalidDataException(
                "The Dynamic Lighting identity package and descriptor must either "
                + "both be present or both be absent.");
        }

        DynamicLightingIdentityDescriptor descriptor = LoadDescriptor(
            descriptorPath,
            getFileLength,
            openRead);
        ValidatePackage(packagePath, descriptor, openRead);
        return new(
            normalizedInstallDirectory,
            packagePath,
            descriptor.Publisher);
    }

    private static DynamicLightingIdentityDescriptor LoadDescriptor(
        string descriptorPath,
        Func<string, long> getFileLength,
        Func<string, Stream> openRead)
    {
        try
        {
            long descriptorLength = getFileLength(descriptorPath);
            if (descriptorLength is <= 0 or > MaximumDescriptorBytes)
            {
                throw new InvalidDataException(
                    "The Dynamic Lighting identity descriptor has an invalid size.");
            }

            using Stream stream = openRead(descriptorPath);
            using JsonDocument document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The Dynamic Lighting identity descriptor must be a JSON object.");
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "The Dynamic Lighting identity descriptor contains duplicate properties.");
                }
            }

            string[] expectedProperties =
            [
                "schemaVersion",
                "packageName",
                "publisher",
                "applicationId",
                "packageFile",
                "externalLocation",
            ];
            if (names.Count != expectedProperties.Length
                || expectedProperties.Any(property => !names.Contains(property)))
            {
                throw new InvalidDataException(
                    "The Dynamic Lighting identity descriptor does not match the reviewed schema.");
            }

            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out JsonElement schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out int version)
                || version != 1)
            {
                throw new InvalidDataException(
                    "The Dynamic Lighting identity descriptor does not match the reviewed schema.");
            }

            string packageName = RequireString(root, "packageName");
            string publisher = RequireString(root, "publisher");
            string applicationId = RequireString(root, "applicationId");
            string packageFile = RequireString(root, "packageFile");
            string externalLocation = RequireString(root, "externalLocation");
            if (!string.Equals(packageName, PackageName, StringComparison.Ordinal)
                || !string.Equals(applicationId, ApplicationId, StringComparison.Ordinal)
                || !string.Equals(packageFile, PackageFileName, StringComparison.Ordinal)
                || !string.Equals(externalLocation, ExternalLocation, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(publisher)
                || publisher.Length > 1024
                || publisher.Any(char.IsControl))
            {
                throw new InvalidDataException(
                    "The Dynamic Lighting identity descriptor does not match the reviewed schema.");
            }

            return new(publisher);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The Dynamic Lighting identity descriptor is invalid.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "The Dynamic Lighting identity descriptor could not be read "
                + $"({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}).");
        }
    }

    private static string RequireString(JsonElement root, string propertyName)
    {
        JsonElement value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                "The Dynamic Lighting identity descriptor does not match the reviewed schema.");
        }

        return value.GetString() ?? string.Empty;
    }

    private static void ValidatePackage(
        string packagePath,
        DynamicLightingIdentityDescriptor descriptor,
        Func<string, Stream> openRead)
    {
        try
        {
            using Stream packageStream = openRead(packagePath);
            using ZipArchive archive = new(
                packageStream,
                ZipArchiveMode.Read,
                leaveOpen: false);
            ZipArchiveEntry[] manifestEntries = archive.Entries
                .Where(entry => string.Equals(
                    entry.FullName,
                    "AppxManifest.xml",
                    StringComparison.Ordinal))
                .ToArray();
            if (manifestEntries.Length != 1
                || manifestEntries[0].Length is <= 0 or > MaximumManifestBytes)
            {
                throw new InvalidDataException(
                    "The Dynamic Lighting identity package must contain exactly "
                    + "one bounded AppxManifest.xml.");
            }

            using Stream manifestStream = manifestEntries[0].Open();
            using XmlReader reader = XmlReader.Create(
                manifestStream,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersFromEntities = 0,
                    MaxCharactersInDocument = MaximumManifestBytes,
                    XmlResolver = null,
                });
            XDocument document = XDocument.Load(reader, LoadOptions.None);
            ValidateManifest(document, descriptor);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or XmlException
                or NotSupportedException)
        {
            throw new InvalidDataException(
                "The Dynamic Lighting identity package is invalid "
                + $"({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}).");
        }
    }

    private static void ValidateManifest(
        XDocument document,
        DynamicLightingIdentityDescriptor descriptor)
    {
        XNamespace foundation = FoundationNamespace;
        XNamespace uap3 = Uap3Namespace;
        XNamespace uap10 = Uap10Namespace;
        XNamespace rescap = RestrictedCapabilitiesNamespace;
        XElement? package = document.Root;
        XElement[] identities = package?.Elements(foundation + "Identity").ToArray() ?? [];
        XElement[] applications = package?
            .Element(foundation + "Applications")?
            .Elements(foundation + "Application")
            .ToArray() ?? [];
        XElement[] allowExternalContent = package?
            .Element(foundation + "Properties")?
            .Elements(uap10 + "AllowExternalContent")
            .ToArray() ?? [];
        XElement[] extensions = applications
            .SelectMany(application =>
                application
                    .Element(foundation + "Extensions")?
                    .Elements(uap3 + "Extension") ?? [])
            .ToArray();
        XElement[] appExtensions = extensions
            .SelectMany(extension => extension.Elements(uap3 + "AppExtension"))
            .ToArray();
        XElement[] runFullTrust = package?
            .Element(foundation + "Capabilities")?
            .Elements(rescap + "Capability")
            .Where(capability =>
                string.Equals(
                    (string?)capability.Attribute("Name"),
                    "runFullTrust",
                    StringComparison.Ordinal))
            .ToArray() ?? [];

        if (package?.Name != foundation + "Package"
            || identities.Length != 1
            || !HasAttribute(identities[0], "Name", PackageName)
            || !HasAttribute(identities[0], "Publisher", descriptor.Publisher)
            || applications.Length != 1
            || !HasAttribute(applications[0], "Id", ApplicationId)
            || !HasAttribute(applications[0], "Executable", SessionAgentExecutable)
            || allowExternalContent.Length != 1
            || !string.Equals(
                allowExternalContent[0].Value,
                "true",
                StringComparison.Ordinal)
            || extensions.Length != 1
            || !HasAttribute(extensions[0], "Category", "windows.appExtension")
            || appExtensions.Length != 1
            || !HasAttribute(appExtensions[0], "Name", LightingExtensionName)
            || !HasAttribute(appExtensions[0], "Id", LightingExtensionId)
            || !HasAttribute(appExtensions[0], "PublicFolder", PublicFolder)
            || runFullTrust.Length != 1)
        {
            throw new InvalidDataException(
                "The Dynamic Lighting identity package manifest differs "
                + "from its reviewed descriptor.");
        }
    }

    private static bool HasAttribute(
        XElement element,
        string attributeName,
        string expectedValue) =>
        string.Equals(
            (string?)element.Attribute(attributeName),
            expectedValue,
            StringComparison.Ordinal);

    private static string NormalizeDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new InvalidDataException(
                "The Dynamic Lighting external location must be an absolute path.");
        }

        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.IsNullOrWhiteSpace(Path.GetFileName(normalized)))
        {
            throw new InvalidDataException(
                "The Dynamic Lighting external location must name an installation directory.");
        }

        return normalized;
    }

    private static TimeSpan ValidateTimeout(TimeSpan? timeout)
    {
        TimeSpan value = timeout ?? DefaultDeploymentTimeout;
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The Dynamic Lighting deployment timeout must be greater than zero "
                + "and no more than five minutes.");
        }

        return value;
    }

    private static string ResolveContainedFile(string root, string fileName)
    {
        string candidate = Path.GetFullPath(Path.Combine(root, fileName));
        if (!string.Equals(
            Path.GetDirectoryName(candidate),
            root,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The Dynamic Lighting identity artifact path escaped "
                + "the installation directory.");
        }

        return candidate;
    }

    internal sealed record DynamicLightingIdentityArtifacts(
        string InstallDirectory,
        string PackagePath,
        string Publisher);

    private sealed record DynamicLightingIdentityDescriptor(string Publisher);

    private sealed class DynamicLightingIdentityDeploymentException
        : InvalidOperationException
    {
        public DynamicLightingIdentityDeploymentException(string message, int hResult)
            : base(message)
        {
            HResult = hResult;
        }
    }
}
