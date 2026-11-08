using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Core.Native;

namespace OpenBlade.Core.Native.Tests;

public sealed class ProductNativeRuntimeTests
{
    [Fact]
    public void ProductionRuntimeUsesCopiedArtifactAndRetainsOneLifetime()
    {
        ProductNativeRuntime.EnsureAvailable();
        Assert.Same(ProductNativeRuntime.Codec, ProductNativeRuntime.Codec);
        Assert.Same(ProductNativeRuntime.Policy, ProductNativeRuntime.Policy);
        Assert.Same(ProductNativeRuntime.Automation, ProductNativeRuntime.Automation);
    }

    [Theory]
    [InlineData("target", "unreviewed-target")]
    [InlineData("abiVersion", "0x00020000")]
    [InlineData("productVersion", "999.0.0")]
    [InlineData("fileName", "untrusted.dll")]
    [InlineData("sourceRevision", "invalid")]
    public void IncompatibleIdentityFailsBeforeAnyLibraryIsLoaded(string key, string value)
    {
        JsonObject manifest = ReadManifest();
        manifest[key] = value;
        using var stream = ManifestStream(manifest);
        NativeComponentException failure = Assert.Throws<NativeComponentException>(() =>
            ProductNativeRuntime.LoadFromManifest(Path.GetTempPath(), stream));
        Assert.Equal(107, failure.Category);
        AssertRedacted(failure);
    }

    [Fact]
    public void MalformedNumericManifestIsRedactedBeforeLoad()
    {
        JsonObject manifest = ReadManifest();
        manifest["schemaVersion"] = long.MaxValue;
        using var stream = ManifestStream(manifest);
        NativeComponentException failure = Assert.Throws<NativeComponentException>(() =>
            ProductNativeRuntime.LoadFromManifest(Path.GetTempPath(), stream));
        Assert.Equal(108, failure.Category);
        AssertRedacted(failure);
    }

    [Fact]
    public void MissingAndTamperedArtifactsFailWithoutFallbackOrPaths()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.NativeAdmission." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            JsonObject manifest = ReadManifest();
            using (var missing = ManifestStream(manifest))
            {
                NativeComponentException failure = Assert.Throws<NativeComponentException>(() =>
                    ProductNativeRuntime.LoadFromManifest(directory, missing));
                Assert.Equal(108, failure.Category);
                AssertRedacted(failure);
            }
            // This data-only file is deliberately invalid native code. Its hash
            // must reject it before NativeLibrary.Load can execute anything.
            File.WriteAllText(Path.Combine(directory, manifest["fileName"]!.GetValue<string>()), "tampered artifact");
            using var tampered = ManifestStream(manifest);
            NativeComponentException rejected = Assert.Throws<NativeComponentException>(() =>
                ProductNativeRuntime.LoadFromManifest(directory, tampered));
            Assert.Equal(109, rejected.Category);
            AssertRedacted(rejected);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void WindowsAdmissionKeepsArtifactLockedThroughRuntimeLifetime()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        JsonObject manifest = ReadManifest();
        string source = Path.Combine(AppContext.BaseDirectory, manifest["fileName"]!.GetValue<string>());
        using var stream = ManifestStream(manifest);
        using ProductNativeRuntime.RuntimeState admitted = ProductNativeRuntime.LoadFromManifest(AppContext.BaseDirectory, stream);
        Assert.Throws<IOException>(() => File.Open(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
    }

    private static JsonObject ReadManifest()
    {
        using Stream stream = typeof(ProductNativeRuntime).Assembly.GetManifestResourceStream("OpenBlade.Native.provenance.json")!;
        return JsonNode.Parse(stream)!.AsObject();
    }

    private static MemoryStream ManifestStream(JsonObject manifest) => new(Encoding.UTF8.GetBytes(manifest.ToJsonString()));

    private static void AssertRedacted(NativeComponentException failure)
    {
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(Path.GetTempPath(), failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tampered artifact", failure.Message);
        Assert.InRange(failure.Message.Length, 1, 180);
    }
}
