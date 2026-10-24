using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using Windows.ApplicationModel;

namespace OpenBlade.SessionAgent;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum DynamicLightingPackageIdentityState
{
    Unavailable,
    Exact,
    Mismatch,
}

internal sealed record DynamicLightingPackageIdentityObservation(
    DynamicLightingPackageIdentityState Package,
    DynamicLightingPackageIdentityState Application)
{
    internal bool IsExact =>
        Package == DynamicLightingPackageIdentityState.Exact
        && Application == DynamicLightingPackageIdentityState.Exact;
}

internal interface IDynamicLightingPackageIdentitySource
{
    DynamicLightingPackageIdentityObservation Observe();
}

internal sealed class WindowsDynamicLightingPackageIdentitySource
    : IDynamicLightingPackageIdentitySource
{
    private const string PackageName = "OSSBlade.OpenBlade.DynamicLighting";
    private const string ApplicationIdSuffix = "!SessionAgent";

    public DynamicLightingPackageIdentityObservation Observe()
    {
        try
        {
            string packageName = Package.Current.Id.Name;
            string appUserModelId = AppInfo.Current.AppUserModelId;
            return new(
                string.Equals(packageName, PackageName, StringComparison.Ordinal)
                    ? DynamicLightingPackageIdentityState.Exact
                    : DynamicLightingPackageIdentityState.Mismatch,
                appUserModelId.EndsWith(
                    ApplicationIdSuffix,
                    StringComparison.Ordinal)
                    ? DynamicLightingPackageIdentityState.Exact
                    : DynamicLightingPackageIdentityState.Mismatch);
        }
        catch (Exception exception) when (
            exception is COMException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            return new(
                DynamicLightingPackageIdentityState.Unavailable,
                DynamicLightingPackageIdentityState.Unavailable);
        }
    }
}

internal sealed record DynamicLightingIdentityProbeReport(
    int SchemaVersion,
    string Probe,
    DynamicLightingPackageIdentityState PackageIdentity,
    DynamicLightingPackageIdentityState ApplicationIdentity,
    int ExactArrayCount,
    bool Connected,
    bool Enabled,
    bool Available,
    bool GeometryApproved,
    string? FailureType,
    string? FailureHResult,
    bool Success);

internal sealed class DynamicLightingIdentityProbeCommand(
    IDynamicLightingPackageIdentitySource identitySource,
    ILampArrayObservationSource lampArraySource)
{
    internal const string Argument = "--probe-dynamic-lighting-identity";
    private const string ProbeName = "OpenBlade.DynamicLightingIdentity";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    internal static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Count > 0
        && string.Equals(arguments[0], Argument, StringComparison.OrdinalIgnoreCase);

    internal async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 2
            || !string.Equals(
                arguments[0],
                Argument,
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(arguments[1]))
        {
            return 2;
        }

        string outputPath;
        try
        {
            outputPath = ValidateOutputPath(arguments[1]);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return 2;
        }

        DynamicLightingPackageIdentityObservation identity = new(
            DynamicLightingPackageIdentityState.Unavailable,
            DynamicLightingPackageIdentityState.Unavailable);
        LampArrayObservation? lampArray = null;
        Exception? failure = null;
        try
        {
            identity = identitySource.Observe();
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.DynamicLightingIdentityProbe",
                "ObserveIdentity",
                exception);
            failure = exception;
        }

        try
        {
            lampArray = await lampArraySource
                .ObserveAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.DynamicLightingIdentityProbe",
                "ObserveLampArray",
                exception);
            failure ??= exception;
        }

        bool geometryApproved = lampArray?.MatchesApprovedGeometry ?? false;
        bool success = identity.IsExact
            && lampArray is
            {
                ExactDeviceCount: 1,
                IsConnected: true,
                IsEnabled: true,
                IsAvailable: true,
            }
            && geometryApproved
            && failure is null;
        DynamicLightingIdentityProbeReport report = new(
            SchemaVersion: 1,
            Probe: ProbeName,
            PackageIdentity: identity.Package,
            ApplicationIdentity: identity.Application,
            ExactArrayCount: lampArray?.ExactDeviceCount ?? 0,
            Connected: lampArray?.IsConnected ?? false,
            Enabled: lampArray?.IsEnabled ?? false,
            Available: lampArray?.IsAvailable ?? false,
            GeometryApproved: geometryApproved,
            FailureType: failure?.GetType().Name,
            FailureHResult: failure is null
                ? null
                : $"0x{failure.HResult:X8}",
            Success: success);

        try
        {
            WriteReport(outputPath, report);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.DynamicLightingIdentityProbe",
                "WriteReport",
                exception);
            return 2;
        }

        return success ? 0 : 2;
    }

    private static string ValidateOutputPath(string outputPath)
    {
        if (!Path.IsPathFullyQualified(outputPath))
        {
            throw new ArgumentException(
                "The Dynamic Lighting identity probe output path must be absolute.",
                nameof(outputPath));
        }

        string fullPath = Path.GetFullPath(outputPath);
        if (!string.Equals(
            Path.GetExtension(fullPath),
            ".json",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The Dynamic Lighting identity probe output must be a JSON file.",
                nameof(outputPath));
        }

        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            throw new ArgumentException(
                "The Dynamic Lighting identity probe output directory must exist.",
                nameof(outputPath));
        }

        return fullPath;
    }

    private static void WriteReport(
        string outputPath,
        DynamicLightingIdentityProbeReport report)
    {
        using FileStream stream = new(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, report, SerializerOptions);
        stream.Flush(flushToDisk: true);
    }
}
