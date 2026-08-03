using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class LampArrayGeometryFingerprintCacheTests
{
    [Fact]
    public void UnchangedDeviceSetReusesFingerprintAndDeviceSetChangeInvalidatesIt()
    {
        var cache = new LampArrayGeometryFingerprintCache();
        int computations = 0;

        cache.SynchronizeDeviceSet(["lamp-a"]);
        Assert.Equal(
            "fingerprint-1",
            cache.GetOrCreate("lamp-a", ComputeFingerprint));

        cache.SynchronizeDeviceSet(["lamp-a"]);
        Assert.Equal(
            "fingerprint-1",
            cache.GetOrCreate("lamp-a", ComputeFingerprint));
        Assert.Equal(1, computations);

        cache.SynchronizeDeviceSet(["lamp-b", "lamp-a"]);
        Assert.Equal(
            "fingerprint-2",
            cache.GetOrCreate("lamp-a", ComputeFingerprint));
        Assert.Equal(2, computations);

        cache.SynchronizeDeviceSet(["lamp-a", "lamp-b"]);
        Assert.Equal(
            "fingerprint-2",
            cache.GetOrCreate("lamp-a", ComputeFingerprint));
        Assert.Equal(2, computations);

        cache.SynchronizeDeviceSet(["lamp-a"]);
        Assert.Equal(
            "fingerprint-3",
            cache.GetOrCreate("lamp-a", ComputeFingerprint));
        Assert.Equal(3, computations);

        string ComputeFingerprint() =>
            $"fingerprint-{Interlocked.Increment(ref computations)}";
    }

    [Fact]
    public void RemovedDeviceCannotReuseCachedFingerprint()
    {
        var cache = new LampArrayGeometryFingerprintCache();
        cache.SynchronizeDeviceSet(["lamp-a"]);
        _ = cache.GetOrCreate("lamp-a", () => "fingerprint");

        cache.SynchronizeDeviceSet(["lamp-b"]);

        Assert.Throws<InvalidOperationException>(
            () => cache.GetOrCreate("lamp-a", () => "stale"));
    }
}
