using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenBlade.Protocol;

/// <summary>Protected cooperative OpenBlade exclusion; vendor software does not participate.</summary>
public sealed class CoolingPadControllerLease : IDisposable
{
    private readonly object gate = new();
    private readonly Func<IDisposable?> acquire;
    private IDisposable? held;
    private IDisposable? serviceOwnership;
    private int references;
    private volatile bool disposed;
    private Task<bool>? pendingServiceOwnership;

    public CoolingPadControllerLease() : this(() => OpenProtected(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenBlade")))
    { }
    internal CoolingPadControllerLease(Func<IDisposable?> acquire) => this.acquire = acquire;

    public async Task<bool> EnsureServiceOwnershipAsync(CancellationToken token)
    {
        Task<bool> pending;
        lock (this)
        {
            if (disposed) { return false; }
            if (pendingServiceOwnership is { IsCompletedSuccessfully: true, Result: false }) { pendingServiceOwnership = null; }
            pending = pendingServiceOwnership ??= Task.Run(EnsureServiceOwnership);
        }
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false) && !disposed && !token.IsCancellationRequested; }
        catch { return false; }
        // A timed-out acquisition stays owned. No detached callback can dispatch a write.
    }

    public bool EnsureServiceOwnership()
    {
        lock (gate)
        {
            if (disposed) { return false; }
            return serviceOwnership is not null || (serviceOwnership = Acquire()) is not null;
        }
    }

    public IDisposable? Acquire()
    {
        lock (gate)
        {
            if (disposed) { return null; }
            if (held is null)
            {
                try { held = acquire(); }
                catch { return null; }
                if (held is null) { return null; }
                if (disposed) { held.Dispose(); held = null; return null; }
            }
            references++;
            return new Reference(this);
        }
    }

    private void Release()
    {
        lock (gate)
        {
            if (--references == 0) { held?.Dispose(); held = null; }
        }
    }
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        // Never synchronously wait for a blocked native file/ACL acquisition.
        _ = Task.Run(() =>
        {
            lock (gate)
            {
                serviceOwnership?.Dispose();
                serviceOwnership = null;
                // Session references survive owner disposal until actual native release.
            }
        }).ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
    }
    private sealed class Reference(CoolingPadControllerLease owner) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) { owner.Release(); } }
    }

    internal static IDisposable OpenProtected(string directory)
    {
        VerifySecurity(directory, true);
        string path = Path.Combine(directory, "cooling-pad-controller.lock");
        bool exists = File.Exists(path);
        if (exists) { VerifySecurity(path, false); }
        var stream = new FileStream(path, exists ? FileMode.Open : FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (!exists)
            {
                var security = new FileSecurity();
                security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                security.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                { security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow)); }
                new FileInfo(path).SetAccessControl(security);
            }
            VerifySecurity(directory, true);
            VerifySecurity(path, false);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static void VerifySecurity(string path, bool directory)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        { throw new UnauthorizedAccessException("Reparse-point controller lease rejected."); }
        FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier)) != administrators
            || rules.Length != 2 || !rules.Any(r => r.IdentityReference == system) || !rules.Any(r => r.IdentityReference == administrators)
            || rules.Any(r => r.IsInherited || r.AccessControlType != AccessControlType.Allow
                || (r.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl
                || r.PropagationFlags != PropagationFlags.None
                || r.InheritanceFlags != (directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None)))
        { throw new UnauthorizedAccessException("Controller lease storage is not protected exactly."); }
    }
}
