using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace OpenBlade.Deployment;

internal sealed record ManagedMailboxRun(string Run, string Process, string Executable, string Assembly, string Module)
{
    internal byte[] Encode() => NativeCanonical.Lines("OpenBlade.ManagedMailboxRun.v1", Run, Process, Executable, Assembly, Module);
    internal static ManagedMailboxRun Parse(byte[] bytes)
    {
        var f = NativeCanonical.ParseLines(bytes, "OpenBlade.ManagedMailboxRun.v1", 6);
        var process = f[1].Split(':');
        if (!NativeCanonical.IsHex(f[0], 32) || !NativeCanonical.IsHex(f[2], 64) || !NativeCanonical.IsHex(f[3], 64)
            || !NativeCanonical.IsHex(f[4], 32) || process.Length != 2
            || !uint.TryParse(process[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint pid) || pid == 0
            || !long.TryParse(process[1], NumberStyles.None, CultureInfo.InvariantCulture, out long created) || created <= 0
            || f[1] != pid.ToString(CultureInfo.InvariantCulture) + ":" + created.ToString(CultureInfo.InvariantCulture))
        { throw new InvalidDataException("Managed mailbox run differs."); }
        return new(f[0], f[1], f[2], f[3], f[4]);
    }
}

internal sealed record ManagedMailboxRequest(string RunDigest, string Installation, string Request, string Package)
{
    internal byte[] Encode() => NativeCanonical.Lines("OpenBlade.ManagedMailboxRequest.v1", RunDigest, Installation, Request, Package);
    internal static ManagedMailboxRequest Parse(byte[] bytes)
    {
        var f = NativeCanonical.ParseLines(bytes, "OpenBlade.ManagedMailboxRequest.v1", 5);
        if (!NativeCanonical.IsHex(f[0], 64) || !NativeCanonical.IsHex(f[1], 32) || !NativeCanonical.IsHex(f[2], 32) || !NativeCanonical.IsHex(f[3], 64))
        { throw new InvalidDataException("Managed mailbox request differs."); }
        return new(f[0], f[1], f[2], f[3]);
    }
}

internal sealed record ManagedMailboxBuildBinding(string RunDigest, string Installation, string Package, string Enrollment)
{
    internal byte[] Encode() => NativeCanonical.Lines("OpenBlade.ManagedMailboxBuildBinding.v1", RunDigest, Installation, Package, Enrollment);
    internal static ManagedMailboxBuildBinding Parse(byte[] bytes)
    {
        var f = NativeCanonical.ParseLines(bytes, "OpenBlade.ManagedMailboxBuildBinding.v1", 5);
        if (!NativeCanonical.IsHex(f[0], 64) || !NativeCanonical.IsHex(f[1], 32) || !NativeCanonical.IsHex(f[2], 64) || !NativeCanonical.IsHex(f[3], 64))
        { throw new InvalidDataException("Managed authenticated build binding differs."); }
        return new(f[0], f[1], f[2], f[3]);
    }
}

// Product entry owns the same lock later retained by the read-only native lease.
// Files are protected, non-reparse, single-link and held stable through decisions.
internal interface IManagedMailboxRun : IDisposable { void PublishQuiescent(); }

[SupportedOSPlatform("windows")]
internal sealed class WindowsManagedMailboxRun : IManagedMailboxRun
{
    private readonly WindowsNativeEpochFiles files;
    private readonly FileStream exclusion;
    private readonly IDisposable? image;
    internal ManagedMailboxRun Record { get; }
    private bool sealedRun;
    private string installation = string.Empty;
    private WindowsManagedMailboxRun(WindowsNativeEpochFiles files, FileStream exclusion, ManagedMailboxRun record, IDisposable? image)
    { this.files = files; this.exclusion = exclusion; Record = record; this.image = image; }

    internal static bool IsEnrolledImage => Environment.ProcessPath == Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenBlade", "OpenBlade.Service.exe");

    // Development/custom-location managed builds retain their existing behavior.
    // If installed migration custody already exists, they must respect it too,
    // but they cannot publish an authenticated installed-run receipt.
    internal static IDisposable? AcquireUnenrolledExclusion()
    {
        if (!WindowsNativeEpochFiles.IsPresentDirectory(WindowsNativeEpochFiles.ManagedHandoffRoot)) { return null; }
        var files = WindowsNativeEpochFiles.OpenProtected(WindowsNativeEpochFiles.ManagedHandoffRoot);
        FileStream? exclusion = null;
        try
        {
            exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
            WindowsNativeEpochFiles.RequireAbsent(Path.Combine(files.Root, "request.bin"));
            WindowsNativeEpochFiles.RequireAbsent(Path.Combine(files.Root, "receipt.bin"));
            WindowsNativeEpochFiles.RequireAbsent(Path.Combine(files.Root, "mapping.bin"));
            return new UnenrolledExclusion(files, exclusion);
        }
        catch { exclusion?.Dispose(); files.Dispose(); throw; }
    }
    private sealed class UnenrolledExclusion(IDisposable files, IDisposable exclusion) : IDisposable
    { public void Dispose() { try { exclusion.Dispose(); } finally { files.Dispose(); } } }

    internal static WindowsManagedMailboxRun? Start(WindowsManagedMailboxPackage? incoming)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenBlade");
        if (!IsEnrolledImage) { throw new IOException("Managed handoff requires its installed Service image."); }
        var files = WindowsNativeEpochFiles.CreateOrOpenManagedHandoff();
        FileStream? exclusion = null;
        WindowsManagedMailboxPackage? authenticated = null;
        try
        {
            exclusion = AcquireRunExclusion(files);
            if (incoming is not null)
            {
                _ = incoming.AuthenticateInstalled(directory, requireLoadedEntry: true);
                incoming.Preserve(files);
            }
            string selection = Path.Combine(files.Root, "build-current.bin");
            try { _ = File.GetAttributes(selection); }
            catch (FileNotFoundException) { exclusion.Dispose(); files.Dispose(); return null; }
            using var selected = files.OpenFile(selection);
            var f = NativeCanonical.ParseLines(WindowsNativeEpochFiles.ReadBounded(selected), "OpenBlade.ManagedMailboxBuildSelection.v1", 2);
            authenticated = WindowsManagedMailboxPackage.OpenCached(files, f[0]);
            var identity = authenticated.AuthenticateInstalled(directory, requireLoadedEntry: true);
            using var process = Process.GetCurrentProcess();
            var record = new ManagedMailboxRun(Guid.NewGuid().ToString("N"), process.Id.ToString(CultureInfo.InvariantCulture) + ":" + process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), identity.Executable, identity.Closure, identity.Module);
            var binding = new ManagedMailboxBuildBinding(NativeCanonical.Digest(record.Encode()), authenticated.Installation,
                authenticated.PackageDigest, authenticated.EnrollmentDigest);
            return BeginRun(files, exclusion, record, binding, authenticated);
        }
        catch { authenticated?.Dispose(); exclusion?.Dispose(); files.Dispose(); throw; }
    }

    private static FileStream AcquireRunExclusion(WindowsNativeEpochFiles files)
    {
        var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
        try
        {
            WindowsNativeEpochFiles.RequireAbsent(Path.Combine(files.Root, "request.bin"));
            WindowsNativeEpochFiles.RequireAbsent(Path.Combine(files.Root, "receipt.bin"));
            WindowsNativeEpochFiles.RequireAbsent(Path.Combine(files.Root, "mapping.bin"));
            return exclusion;
        }
        catch { exclusion.Dispose(); throw; }
    }

    private static WindowsManagedMailboxRun BeginRun(WindowsNativeEpochFiles files, FileStream exclusion,
        ManagedMailboxRun record, ManagedMailboxBuildBinding binding, IDisposable? authenticated)
    {
        files.PublishCustody(Path.Combine(files.Root, "run-binding-" + record.Run + ".bin"), binding.Encode());
        files.PublishCustody(Path.Combine(files.Root, "run-" + record.Run + ".bin"), record.Encode());
        files.PublishCurrent(record.Encode());
        return new(files, exclusion, record, authenticated) { installation = binding.Installation };
    }

#if OPENBLADE_NATIVE_PACKAGE_TESTS
    internal static WindowsManagedMailboxRun StartForTests(WindowsNativeEpochFiles files, ManagedMailboxRun record)
    {
        var exclusion = AcquireRunExclusion(files);
        return BeginRun(files, exclusion, record, new(NativeCanonical.Digest(record.Encode()), new string('5', 32), new string('7', 64), new string('8', 64)), null);
    }
#endif

    // Only called after the tracked scope sealed and successfully drained and the
    // real guardian delegation was independently excluded. No firmware claim.
    internal void PublishQuiescent()
    {
        if (sealedRun) { throw new InvalidOperationException("Managed transport handoff is never replayed."); }
        sealedRun = true;
        string requestPath = Path.Combine(files.Root, "request.bin");
        try { _ = File.GetAttributes(requestPath); }
        catch (FileNotFoundException) { return; }
        using var requestFile = files.OpenFile(requestPath);
        byte[] bytes = WindowsNativeEpochFiles.ReadBounded(requestFile);
        var request = ManagedMailboxRequest.Parse(bytes);
        if (request.Installation != installation || request.RunDigest != NativeCanonical.Digest(Record.Encode()) || !(files.ReadCurrent() ?? []).AsSpan().SequenceEqual(Record.Encode()))
        { throw new IOException("Managed handoff request belongs to another original run/build."); }
        files.PublishCustody(Path.Combine(files.Root, "receipt.bin"), NativeCanonical.Lines("OpenBlade.ManagedMailboxQuiescent.v1",
            NativeCanonical.Digest(bytes), Convert.ToBase64String(Record.Encode()), "transport-quiescent-no-mutation"));
        // Keep exclusion until this original process's host disposes. A restart
        // encounters immutable request.bin and cannot reacquire the mailbox.
    }
    void IManagedMailboxRun.PublishQuiescent() => PublishQuiescent();
    public void Dispose() { exclusion.Dispose(); image?.Dispose(); files.Dispose(); }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsManagedMailboxTransfer : IDisposable, IManagedMailboxReceipt
{
    private readonly WindowsNativeEpochFiles files;
    private readonly byte[] request;
    private readonly byte[] run;
    private byte[]? receipt;
    private readonly IDisposable? predecessor;
    internal WindowsManagedMailboxTransfer(WindowsNativeEpochFiles files, byte[] run, ManagedMailboxRequest request,
        IDisposable? predecessor = null)
    {
        this.files = files; this.run = run; this.request = request.Encode();
        this.predecessor = predecessor;
        _ = ManagedMailboxRun.Parse(run);
        if (request.RunDigest != NativeCanonical.Digest(run)) { throw new IOException("Managed original run differs."); }
        using var current = files.OpenFile(Path.Combine(files.Root, "run-" + ManagedMailboxRun.Parse(run).Run + ".bin"));
        WindowsNativeEpochFiles.RequireExact(current, run);
        if (!(files.ReadCurrent() ?? []).AsSpan().SequenceEqual(run)) { throw new IOException("Managed run changed before handoff request."); }
        files.PublishCustody(Path.Combine(files.Root, "request.bin"), this.request);
    }
    internal void ConfirmOriginalExited()
    {
        // Caller retains and checks the original process object; SCM Stopped is insufficient.
        using var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
        using var held = files.OpenFile(Path.Combine(files.Root, "receipt.bin"));
        receipt = WindowsNativeEpochFiles.ReadBounded(held);
        byte[] expected = NativeCanonical.Lines("OpenBlade.ManagedMailboxQuiescent.v1", NativeCanonical.Digest(request), Convert.ToBase64String(run), "transport-quiescent-no-mutation");
        if (!receipt.AsSpan().SequenceEqual(expected)) { throw new IOException("Managed transport quiescence is unconfirmed."); }
        // Setup must now be able to rename the predecessor installation. Keep
        // its original signed package evidence, but release installed-tree pins.
        if (predecessor is WindowsManagedMailboxPackage package) { package.ReleaseInstalledPins(); }
    }
    void IManagedMailboxReceipt.ConfirmOriginalExited() => ConfirmOriginalExited();
    internal void BindEpoch(WindowsNativeEpochFiles epochs, NativeEpochRecord epoch)
    {
        if (receipt is null) { throw new IOException("Managed handoff was not confirmed against the original process."); }
        var value = ManagedMailboxRequest.Parse(request);
        if (value.Installation != epoch.Installation || value.Request != epoch.Request || value.Package != epoch.Package)
        { throw new IOException("Managed handoff incoming request/package changed."); }
        using var exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
        byte[] mapping = NativeCanonical.Lines("OpenBlade.ManagedMailboxEpoch.v1", NativeCanonical.Digest(receipt), NativeCanonical.Digest(request), epoch.Epoch, epoch.Installation, epoch.Request, epoch.Package);
        files.PublishCustody(Path.Combine(files.Root, "mapping.bin"), mapping);
        // The create-once global mapping claims the receipt before any startup.
        // A partial second publication remains fenced and cannot select a new epoch.
        epochs.Publish(Path.Combine(epochs.Root, epoch.Epoch, "managed-handoff.bin"), mapping);
    }
    public void Dispose() { try { predecessor?.Dispose(); } finally { files.Dispose(); } }
}

internal interface IManagedMailboxReceipt { void ConfirmOriginalExited(); }
