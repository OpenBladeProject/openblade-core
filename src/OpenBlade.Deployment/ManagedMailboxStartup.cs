using System.Runtime.Versioning;

namespace OpenBlade.Deployment;

[SupportedOSPlatform("windows")]
internal static class ManagedMailboxStartup
{
    internal sealed class Admission(IManagedMailboxRun? run, IDisposable? unenrolled, IDisposable? legacy,
        NativeHandbackBinding? nativeHandbacks = null) : IDisposable
    {
        internal IManagedMailboxRun? Run { get; } = run;
        // Freshly derived native handback binding, kept after custody ends.
        internal NativeHandbackBinding NativeHandbacks { get; } = nativeHandbacks ?? NativeHandbackBinding.None;
        public void Dispose()
        {
            try { Run?.Dispose(); }
            finally { try { unenrolled?.Dispose(); } finally { legacy?.Dispose(); } }
        }
    }
    internal static Admission Open(Func<ManagedMailboxNativeCustody> acquireNative, Func<ManagedMailboxNativeCustody, IDisposable?> acquireLegacy,
        Func<ManagedMailboxNativeCustody, IManagedMailboxRun?> enroll, Func<ManagedMailboxNativeCustody, IDisposable?> unenrolled)
    {
        // This gate is mandatory and deliberately outside optional enrollment's
        // closed failure filter. Unknown native ownership never becomes fallback.
        using var native = acquireNative();
        IDisposable? legacy = null, ordinary = null;
        IManagedMailboxRun? run = null;
        try
        {
            legacy = acquireLegacy(native);
            try { run = enroll(native); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException or OpenBladeReleaseVerificationException
                or System.Text.Json.JsonException or FormatException)
            { System.Diagnostics.Trace.TraceWarning("Optional managed mailbox enrollment unavailable: {0}", error.Message); }
            if (run is null) { ordinary = unenrolled(native); }
            native.RequireSafe();
            // Native origins are now proven retired/never-selected. The new
            // managed scope owns handoff exclusion before these proof locks end;
            // a new native reader needs its NEW exact request/receipt and SCM
            // original. Retaining native owner locks here would incorrectly
            // prevent first enrollment of this unenrolled default run.
            return new(run, ordinary, legacy, native.HandbackBinding);
        }
        catch
        {
            try { run?.Dispose(); }
            finally { try { ordinary?.Dispose(); } finally { legacy?.Dispose(); } }
            throw;
        }
    }
}
