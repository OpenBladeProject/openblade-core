using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenBlade.Core.Native;

/// <summary>The mandatory first-party component, admitted once for the process lifetime.</summary>
public static class ProductNativeRuntime
{
    private static readonly Lazy<RuntimeState> State = new(Load);

    public static NativeReportCodec Codec => State.Value.Codec;
    public static NativeProfilePolicy Policy => State.Value.Policy;

    public static void EnsureAvailable() => _ = State.Value;

    private static RuntimeState Load()
    {
        using Stream manifest = typeof(ProductNativeRuntime).Assembly
            .GetManifestResourceStream("OpenBlade.Native.provenance.json")
            ?? throw new NativeComponentException(107);
        return LoadFromManifest(AppContext.BaseDirectory, manifest);
    }

    // Tests exercise real admission with data-only rejected artifacts. Product
    // callers cannot override either the embedded manifest or the fixed directory.
    internal static RuntimeState LoadFromManifest(string directory, Stream manifestStream)
    {
        FileStream? lease = null;
        NativeReportCodec? codec = null;
        NativeProfilePolicy? policy = null;
        try
        {
            Assembly assembly = typeof(ProductNativeRuntime).Assembly;
            using JsonDocument document = JsonDocument.Parse(manifestStream, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement manifest = document.RootElement;
            string fileName = OperatingSystem.IsWindows() ? "openblade_ffi.dll"
                : OperatingSystem.IsMacOS() ? "libopenblade_ffi.dylib" : "libopenblade_ffi.so";
            string? target = CurrentTarget();
            if (target is null) { throw new NativeComponentException(100); }
            string expectedVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion.Split('+')[0];
            string hash = manifest.GetProperty("sha256").GetString() ?? "";
            if (manifest.GetProperty("schemaVersion").GetInt32() != 1
                || manifest.GetProperty("abiVersion").GetString() != "0x00010000"
                || manifest.GetProperty("fileName").GetString() != fileName
                || manifest.GetProperty("target").GetString() != target
                || manifest.GetProperty("productVersion").GetString() != expectedVersion
                || manifest.GetProperty("sourceRevision").GetString() is not { Length: 40 } revision
                || !revision.All(Uri.IsHexDigit)
                || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            {
                throw new NativeComponentException(107);
            }

            // Only the assembly-embedded build manifest is authoritative. A mutable
            // sidecar, environment variable or working directory cannot select code.
            string path = Path.GetFullPath(Path.Combine(directory, fileName));
            lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            VerifyHash(lease, hash);
            NativeCodecLoadResult codecResult = NativeReportCodec.Load(path);
            codec = codecResult.Codec;
            if (!codecResult.IsSuccess) { throw new NativeComponentException(codecResult.Error!); }
            NativePolicyLoadResult policyResult = NativeProfilePolicy.Load(path);
            policy = policyResult.Policy;
            if (!policyResult.IsSuccess) { throw new NativeComponentException(policyResult.Error!); }
            VerifyHash(lease, hash);

            // Deliberately retain both safe module handles and the no-write/no-delete
            // file lease until process exit. Host shutdown still uses codec/policy
            // during keyboard/fan restoration; DI disposal must not unload them.
            return new RuntimeState(codec!, policy!, lease);
        }
        catch (NativeComponentException)
        {
            policy?.Dispose();
            codec?.Dispose();
            lease?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or NotSupportedException or FormatException)
        {
            policy?.Dispose();
            codec?.Dispose();
            lease?.Dispose();
            // Native loader and filesystem messages can contain account paths.
            throw new NativeComponentException(108);
        }
    }

    private static void VerifyHash(FileStream stream, string expected)
    {
        stream.Position = 0;
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new NativeComponentException(109);
        }
    }

    private static string? CurrentTarget() => (OperatingSystem.IsWindows(), OperatingSystem.IsLinux(),
        OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture) switch
    {
        (true, _, _, Architecture.X64) => "x86_64-pc-windows-msvc",
        (_, true, _, Architecture.X64) => "x86_64-unknown-linux-gnu",
        (_, _, true, Architecture.X64) => "x86_64-apple-darwin",
        (_, _, true, Architecture.Arm64) => "aarch64-apple-darwin",
        _ => null,
    };

    internal sealed record RuntimeState(NativeReportCodec Codec, NativeProfilePolicy Policy, FileStream Lease) : IDisposable
    {
        public void Dispose()
        {
            Policy.Dispose();
            Codec.Dispose();
            Lease.Dispose();
        }
    }
}
