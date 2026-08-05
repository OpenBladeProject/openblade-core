using Windows.Management.Deployment;

namespace OpenBlade.SessionAgent;

internal sealed class PerUserDynamicLightingRegistration
{
    private const string PackageFileName = "OpenBlade.DynamicLighting.msix";
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(30);
    private readonly IDynamicLightingPackageIdentitySource identitySource;
    private readonly Func<Uri, Uri, CancellationToken, Task<bool>> registerPackage;
    private readonly string installDirectory;

    public PerUserDynamicLightingRegistration()
        : this(
            new WindowsDynamicLightingPackageIdentitySource(),
            RegisterPackageAsync,
            AppContext.BaseDirectory)
    {
    }

    internal PerUserDynamicLightingRegistration(
        IDynamicLightingPackageIdentitySource identitySource,
        Func<Uri, Uri, CancellationToken, Task<bool>> registerPackage,
        string installDirectory)
    {
        this.identitySource = identitySource
            ?? throw new ArgumentNullException(nameof(identitySource));
        this.registerPackage = registerPackage
            ?? throw new ArgumentNullException(nameof(registerPackage));
        this.installDirectory = string.IsNullOrWhiteSpace(installDirectory)
            ? throw new ArgumentException(
                "An install directory is required.",
                nameof(installDirectory))
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
    }

    public async Task<bool> EnsureRegisteredAsync(
        CancellationToken cancellationToken)
    {
        DynamicLightingPackageIdentityObservation identity = identitySource.Observe();
        if (identity.IsExact)
        {
            return false;
        }

        string packagePath = Path.Combine(installDirectory, PackageFileName);
        if (!File.Exists(packagePath))
        {
            return false;
        }

        return await registerPackage(
            new Uri(packagePath),
            new Uri(installDirectory + Path.DirectorySeparatorChar),
            cancellationToken);
    }

    private static async Task<bool> RegisterPackageAsync(
        Uri packageUri,
        Uri externalLocationUri,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RegistrationTimeout);
        var packageManager = new PackageManager();
        var options = new AddPackageOptions
        {
            ExternalLocationUri = externalLocationUri,
        };
        DeploymentResult result = await packageManager
            .AddPackageByUriAsync(packageUri, options)
            .AsTask(timeout.Token);
        if (result.ExtendedErrorCode is { HResult: not 0 } error)
        {
            throw new InvalidOperationException(
                $"Per-user Dynamic Lighting registration failed with HRESULT 0x{error.HResult:X8}.");
        }

        return result.IsRegistered;
    }
}
