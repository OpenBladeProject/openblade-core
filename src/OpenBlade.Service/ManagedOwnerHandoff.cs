using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

// Internal composition seam. Installed service composition does not create a scope.
// A record inherited on startup is evidence of uncertainty, never a resumable lease.
internal sealed class ManagedOwnerHandoff(string markerPath, string build, string boot)
{
    private readonly string path = Path.GetFullPath(markerPath);
    private int blocked = MarkerExists(markerPath) ? 1 : 0;
    private Lease? current;
    private byte[]? marker;

    internal bool IsBlocked => Volatile.Read(ref blocked) != 0;

    // The device owner's lifecycle lock serializes these transitions. The exact
    // process handle must be retained by the private supervisor through handback.
    internal Lease Begin(SafeProcessHandle child, AdapterPowerState baseline)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (IsBlocked || ObserveChild(child) != ChildRunning)
        {
            throw new InvalidOperationException("An unresolved handoff or unavailable child blocks transfer.");
        }

        Volatile.Write(ref blocked, 1);
        var lease = new Lease(Guid.NewGuid(), child, baseline);
        byte[] record = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Model = "RZ09-0581",
            ProductId = "02E0",
            Build = build,
            Boot = boot,
            Attempt = lease.Attempt,
            Baseline = baseline,
        });
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, FileOptions.WriteThrough))
        {
            stream.Write(record);
            stream.Flush(flushToDisk: true);
        }
        marker = record;
        current = lease;
        return lease;
    }

    internal bool ChildStillWaiting(Lease lease) =>
        ReferenceEquals(lease, current) && ObserveChild(lease.Child) == ChildRunning;

    internal bool ChildExited(Lease lease) =>
        ReferenceEquals(lease, current) && ObserveChild(lease.Child) == ChildExitedNormallyOrAbnormally;

    internal bool TryFinish(Lease lease)
    {
        if (!ReferenceEquals(lease, current) || marker is not { } expected)
        {
            return false;
        }

        // This directory belongs to the isolated supervisor. Read only the
        // expected bounded record and preserve a mismatch as blocked evidence.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length != expected.Length)
            {
                return false;
            }
            byte[] actual = new byte[expected.Length];
            stream.ReadExactly(actual);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                return false;
            }
        }
        File.Delete(path);
        current = null;
        marker = null;
        Volatile.Write(ref blocked, 0);
        return true;
    }

    private static bool MarkerExists(string path)
    {
        try
        {
            _ = File.GetAttributes(Path.GetFullPath(path));
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static uint ObserveChild(SafeProcessHandle child) =>
        child.IsClosed || child.IsInvalid ? uint.MaxValue : WaitForSingleObject(child, 0);

    private const uint ChildExitedNormallyOrAbnormally = 0;
    private const uint ChildRunning = 258;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    internal sealed record Lease(Guid Attempt, SafeProcessHandle Child, AdapterPowerState Baseline);
}
