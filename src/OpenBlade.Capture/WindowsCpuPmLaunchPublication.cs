using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Capture;

// Future supervisor composition only. The private run/control/evidence tree must
// already have been freshly created. No directories, driver calls or CLI here.
internal sealed class WindowsCpuPmLaunchPublication : ICpuPmLaunchRecordStore
{
    private static readonly List<WindowsCpuPmLaunchPublication> ChildRoots = [];
    private readonly WindowsCpuPmLaunchFiles files = new();
    private readonly CpuPmReviewedLaunchBootstrap trusted;
    private readonly string control, childHash;
    private readonly WindowsCpuPmProcessProbe parent;
    private readonly CpuPmAuthorityPeer parentIdentity;
    private readonly CpuPmLaunchPublication publication;
    private Process? originalChild;
    private CpuPmRetainedLaunchProcess? retainedChild;
    private bool attempted, released;
    private WindowsCpuPmLaunchPublication(Guid run, Guid lifetime, CpuPmReviewedLaunchBootstrap trusted, CancellationToken token)
    {
        this.trusted = trusted;
        if (run == Guid.Empty || lifetime == Guid.Empty) { throw new ArgumentException("Fresh run/lifetime required."); }
        string runPath = Path.Combine(WindowsCpuPmCaptureEvidenceFiles.FixedRoot, run.ToString("N"));
        control = Path.Combine(runPath, "control"); parent = WindowsCpuPmProcessProbe.Current();
        try
        {
            CpuPmAuthenticatedChildRun.PinAncestors(files, runPath); files.PinDirectory(runPath, true, true);
            files.PinDirectory(control, true, false); files.PinDirectory(Path.Combine(runPath, "evidence"), true, false);
            if (files.Enumerate(control).Any() || files.Enumerate(Path.Combine(runPath, "evidence")).Any()
                || files.Enumerate(runPath).Any(e => !e.Directory || e.Name is not ("control" or "evidence"))) { throw new IOException("Fresh launch tree required."); }
            CpuPmAuthenticatedChildRun.PinAncestors(files, trusted.PayloadPath); files.PinDirectory(trusted.PayloadPath, true, true);
            var manifestPin = files.PinFile(Path.Combine(trusted.PayloadPath, "manifest.json"), CpuPmLaunchSchema.ManifestLimit, true);
            var manifest = CpuPmLaunchSchema.ParseManifest(manifestPin.ReadBounded(CpuPmLaunchSchema.ManifestLimit), trusted);
            CpuPmAuthenticatedChildRun.ValidatePayload(files, trusted.PayloadPath, manifest, token);
            string parentImage = Path.Combine(trusted.PayloadPath, "OpenBlade.PmSupervisor.exe");
            string parentHash = manifest.Single(f => f.Name == "OpenBlade.PmSupervisor.exe").Sha256;
            childHash = manifest.Single(f => f.Name == "OpenBlade.Capture.exe").Sha256;
            var observed = parent.Read(true);
            parentIdentity = new(observed.Pid, observed.Created, parentImage, parentHash);
            if (!parentIdentity.IsValid || observed.Pid != Environment.ProcessId || observed.State != CpuPmChildState.Running
                || observed.Image != parentImage) { throw new IOException("Actual supervisor identity denied."); }
            publication = new(new(run, lifetime, trusted.Source, trusted.ManifestSha256, parentIdentity,
                Path.Combine(trusted.PayloadPath, "OpenBlade.Capture.exe"), childHash), this);
        }
        catch { parent.Dispose(); files.Dispose(); throw; }
    }
    internal static WindowsCpuPmLaunchPublication Create(Guid run, Guid lifetime,
        CpuPmReviewedLaunchBootstrap trusted, CancellationToken token) => new(run, lifetime, trusted, token);

    // Caller must keep the Process returned by its own successful Process.Start,
    // even if entry into this method fails. No supplied PID is opened or adopted.
    internal CpuPmRetainedLaunchProcess PublishActuallyLaunchedChild(Process launched,
        Action<CpuPmRetainedLaunchProcess> prepareAuthority)
    {
        if (attempted || released) { throw new InvalidOperationException("Launch publication already consumed."); }
        attempted = true; originalChild = launched ?? throw new ArgumentNullException(nameof(launched));
        // Root before any duplicate/query/pin/persistence failure. Failure must not
        // dispose the returned Process, kill the child, release pins or retry.
        lock (ChildRoots) { ChildRoots.Add(this); }
        var actual = WindowsCpuPmLaunchProcesses.RetainActuallyLaunchedChild(launched, trusted, childHash, files);
        retainedChild = actual;
        ArgumentNullException.ThrowIfNull(prepareAuthority);
        publication.PublishChild(actual, _ => prepareAuthority(actual)); return actual;
    }
    public void AssertPins()
    {
        if (released) { throw new IOException("Publication pins released."); }
        files.AssertAllPins(); var observed = parent.Read(true);
        if (observed.Pid != parentIdentity.ProcessId || observed.Created != parentIdentity.CreationFileTimeUtc
            || observed.State != CpuPmChildState.Running || observed.Image != parentIdentity.ExecutablePath) { throw new IOException("Supervisor continuity denied."); }
    }
    public void Store(string name, byte[] bytes)
    {
        if (name is not ("owner.json" or "child.json") || bytes.Length is 0 or > CpuPmLaunchSchema.RecordLimit) { throw new IOException("Launch record denied."); }
        AssertPins(); string final = Path.Combine(control, name), partial = final + ".partial";
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        using (SafeFileHandle handle = WindowsCpuPmCaptureEvidenceFiles.CreateFileW(partial, 0xC0020000, 1, 0,
            WindowsCpuPmCaptureEvidenceFiles.CreateNew, 0x00200000, 0))
        {
            WindowsCpuPmCaptureEvidenceFiles.VerifyShape(handle, partial, false);
            WindowsCpuPmCaptureEvidenceFiles.VerifyPrivateAcl(handle);
            using var stream = new FileStream(handle, FileAccess.ReadWrite);
            stream.Write(bytes); stream.Flush(true); stream.Position = 0;
            if (stream.Length != bytes.Length || Convert.ToHexString(SHA256.HashData(stream)) != hash) { throw new IOException("Launch record durability denied."); }
            WindowsCpuPmCaptureEvidenceFiles.VerifyShape(handle, partial, false); AssertPins();
        }
        AssertPins();
        if (!WindowsCpuPmCaptureEvidenceFiles.MoveFileExW(partial, final, WindowsCpuPmCaptureEvidenceFiles.MoveWriteThrough))
        { throw new IOException("Launch record publication denied."); }
        var pin = files.PinFile(final, CpuPmLaunchSchema.RecordLimit, true);
        if (pin.Length != bytes.Length || pin.Sha256 != hash || !pin.ReadBounded(CpuPmLaunchSchema.RecordLimit).SequenceEqual(bytes))
        { throw new IOException("Launch record pin denied."); }
        AssertPins();
    }
    // If strict retained binding failed, retain the original returned handle and
    // query that kernel object directly. Unknown/inaccessible never means exit.
    internal bool TryReleaseAfterActualTerminal()
    {
        if (released) { return true; }
        if (!attempted || originalChild is null) { return false; }
        try
        {
            using var probe = WindowsCpuPmProcessProbe.FromLaunchedHandle(originalChild.SafeHandle);
            if (probe.Read(false).State != CpuPmChildState.Terminal) { return false; }
            // Release an independently proven binding if available. A corrupted
            // binding remains rooted rather than weakening its release contract.
            retainedChild?.ReleaseAfterTerminal();
            originalChild.Dispose(); parent.Dispose(); files.Dispose(); released = true;
            lock (ChildRoots) { ChildRoots.Remove(this); }
            return true;
        }
        catch (Exception) { return false; }
    }
}

internal sealed class WindowsCpuPmLaunchStartupWait : ICpuPmLaunchStartupWait
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly string child;
    internal WindowsCpuPmLaunchStartupWait(Guid run)
    {
        if (run == Guid.Empty) { throw new ArgumentException("Run required."); }
        child = Path.Combine(WindowsCpuPmCaptureEvidenceFiles.FixedRoot, run.ToString("N"), "control", "child.json");
    }
    public TimeSpan Elapsed => elapsed.Elapsed;
    public bool ChildRecordPresent()
    {
        try { _ = File.GetAttributes(child); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    public Task Delay(CancellationToken token) => Task.Delay(25, token);
    internal static Task<CpuPmAuthenticatedChildRun?> LoadAsync(Guid run, CpuPmReviewedLaunchBootstrap trusted, CancellationToken token)
        => CpuPmLaunchStartupBarrier.AwaitThenLoad(new WindowsCpuPmLaunchStartupWait(run), () =>
        {
            var files = new WindowsCpuPmLaunchFiles();
            var processes = new WindowsCpuPmLaunchProcesses(trusted, files);
            return CpuPmAuthenticatedChildRun.TryLoad(run, trusted, files, processes, token);
        }, token);
}
