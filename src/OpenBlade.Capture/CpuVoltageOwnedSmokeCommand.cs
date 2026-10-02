using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// One attended workload on the existing exact owned-driver lifecycle. No PM
// observation child, CPU getter, setter, retry or replacement worker lives here.
internal sealed class CpuVoltageOwnedSmokeCommand
{
    internal const string Name = "cpu-voltage-owned-smoke";
    internal const string Confirmation = "RZ09-0528-OWNED-V31-CO-AND-OPENBLADE-SHUTDOWN";
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(600);
    private static CpuVoltageOwnedSmokeCommand? root;
    private static int claimed;
    private readonly Options options;
    private readonly TextWriter output;
    private readonly WindowsCpuPmLaunchFiles pins = new(voltageValidation: true);
    private readonly CancellationTokenSource deadline = new(Budget);
    private readonly Guid run = Guid.NewGuid();
    private IReadOnlyList<CpuPmLaunchFile> manifest = [];
    private CpuPmOwnedDriverLifecycle? owner;
    private WindowsCpuVoltageServiceProcess? service;
    private Process? actualChild;
    private CpuPmRetainedLaunchProcess? child;
    private Timer? watchdog;
    private bool mutationPossible, serviceTerminal;
    private int uncertain;
    private CpuVoltageOwnedSmokeCommand(Options options, TextWriter output) { this.options = options; this.output = output; }
    internal sealed record Options(string Payload, string Source, string Manifest, string ServiceVersion);
    internal static Options Parse(string[] args)
    {
        string[] names = ["--payload", "--source", "--manifest-sha256", "--expected-service-version", "--confirm-target"];
        if (args.Length != 10) { throw new ArgumentException("Five exact options required; use --help."); }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        { if (!names.Contains(args[i], StringComparer.Ordinal) || !values.TryAdd(args[i], args[i + 1])) { throw new ArgumentException("Unknown or duplicate option."); } }
        string source = values["--source"], hash = values["--manifest-sha256"], version = values["--expected-service-version"], path = values["--payload"];
        string suffix = "+voltage-validation." + (source.Length == 40 ? source[..8] : string.Empty);
        if (values["--confirm-target"] != Confirmation || !CpuPmLaunchSchema.IsHex(source, 40, true)
            || !CpuPmLaunchSchema.IsHex(hash, 64, false) || version.Length > 64 || version.Any(char.IsWhiteSpace) || !version.EndsWith(suffix, StringComparison.Ordinal)
            || !Version.TryParse(version[..^suffix.Length], out var parsed) || parsed.Build < 0
            || path.Length > 350 || !path.StartsWith(@"C:\", StringComparison.Ordinal) || path.Contains('/') || path != Path.GetFullPath(path))
        { throw new ArgumentException("Exact reviewed package, full source, validation Service version and shutdown confirmation required."); }
        return new(path, source, hash, version);
    }
    internal static int Run(string[] args, TextWriter output)
    {
        if (args.Length == 1 && args[0] == "--help")
        {
            output.WriteLine($"{Name} --payload <private reviewed closure> --source <40hex> --manifest-sha256 <64HEX> --expected-service-version <version+voltage-validation.8hex> --confirm-target {Confirmation}");
            output.WriteLine("Attended only: temporary owned V31, one -1/0 cycle, exact original restoration, then typed OpenBlade shutdown (including normal shutdown cleanup), then verified owned V31 removal. Fixed 600-second budget; uncertainty retains resources. No automatic elevation.");
            return 0;
        }
        Options options;
        try { options = Parse(args); } catch (ArgumentException exception) { output.WriteLine(exception.Message); return 2; }
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || Interlocked.CompareExchange(ref claimed, 1, 0) != 0) { return 2; }
        root = new(options, output);
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; root.MarkUncertain(); };
        Console.CancelKeyPress += cancel;
        try { return root.Execute(); } finally { Console.CancelKeyPress -= cancel; }
    }
    private int Execute()
    {
        try
        {
            PinPackage();
            WindowsCpuPmFreshRunTree.Create(run);
            string runPath = Path.Combine(WindowsCpuPmCaptureEvidenceFiles.FixedRoot, run.ToString("N"));
            CpuPmAuthenticatedChildRun.PinAncestors(pins, runPath); pins.PinDirectory(runPath, true, true);
            pins.PinDirectory(Path.Combine(runPath, "control"), true, true);
            string evidence = Path.Combine(runPath, "evidence"); pins.PinDirectory(evidence, true, true);
            if (pins.Enumerate(evidence).Any()) { throw new IOException("Fresh evidence required."); }
            service = WindowsCpuVoltageServiceProcess.Bind(pins, options.ServiceVersion, deadline.Token);
            var safety = CpuPmParentDriverSafety.CreateForVoltageValidation(pins, () =>
            { CpuPmAuthenticatedChildRun.ValidatePayload(pins, options.Payload, manifest, deadline.Token, voltageValidation: true); AssertContext(); }, deadline.Token)
                ?? throw new IOException("Passive owner safety unavailable.");
            var tag = new CpuPmDriverOwnerTag(run, Guid.NewGuid(), options.Manifest, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
            owner = new(tag, new WindowsCpuPmDriverLifecyclePlatform(), safety,
                WindowsCpuPmDriverJournalStorage.CreateForFixedRun(tag), new CpuPmAuthorityClock());
            WriteContext(Path.Combine(runPath, "control", "voltage-context.json"), new
            { run, options.Source, options.Manifest, options.ServiceVersion, service.Identity, BudgetSeconds = 600 });
            watchdog = new Timer(_ => MarkUncertain(), null, RemainingBudget(), Timeout.InfiniteTimeSpan);
            mutationPossible = true;
            if (!owner.PrepareAndStart(deadline.Token) || !owner.BeginChildLaunch(deadline.Token)) { return Park("DriverOrLaunchAdmissionUnknown"); }
            var start = new ProcessStartInfo(Path.Combine(options.Payload, "firmware-smoke", "OpenBlade.FirmwareSmoke.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.Combine(options.Payload, "firmware-smoke") };
            foreach (string arg in new[] { "cpu-voltage-optimizer", "--confirm-target", "RZ09-0528-CPU-VOLTAGE-OPTIMIZER-SMOKE",
                "--expected-service-version", options.ServiceVersion, "--output", evidence }) { start.ArgumentList.Add(arg); }
            AssertContext();
            DateTimeOffset launched = DateTimeOffset.UtcNow;
            actualChild = Process.Start(start) ?? throw new IOException("Smoke launch unknown.");
            child = CpuPmRetainedLaunchProcess.Retain(WindowsCpuPmProcessProbe.FromLaunchedHandle(actualChild.SafeHandle), pins,
                start.FileName, manifest.Single(f => f.Name == "firmware-smoke/OpenBlade.FirmwareSmoke.exe").Sha256);
            if (!owner.BindStartedChild(child, deadline.Token)) { return Park("ChildBindingUnknown"); }
            WriteContext(Path.Combine(runPath, "control", "voltage-child.json"), child.Identity);
            bool cleaned = Complete(child, () => Volatile.Read(ref uncertain) != 0 || deadline.IsCancellationRequested,
                AssertContext, owner.RevokeAuthority,
                () =>
                {
                    if (actualChild.ExitCode != 0) { throw new IOException("Smoke failed; no shutdown or driver cleanup."); }
                    string[] expected = [CpuVoltageOwnedSmokeEvidence.BaselineFile, CpuVoltageOwnedSmokeEvidence.ResultFile];
                    var entries = pins.Enumerate(evidence).ToArray();
                    if (entries.Length != 2 || entries.Any(e => e.Directory || !expected.Contains(e.Name, StringComparer.Ordinal)))
                    { throw new IOException("Smoke evidence closure differs."); }
                    var baseline = pins.PinFile(Path.Combine(evidence, expected[0]), CpuPmLaunchSchema.ManifestLimit, true);
                    var result = pins.PinFile(Path.Combine(evidence, expected[1]), CpuPmLaunchSchema.ManifestLimit, true);
                    CpuVoltageOwnedSmokeEvidence.Verify(baseline.ReadBounded(CpuPmLaunchSchema.ManifestLimit),
                        result.ReadBounded(CpuPmLaunchSchema.ManifestLimit), options.ServiceVersion, launched, DateTimeOffset.UtcNow);
                    pins.AssertAllPins();
                    WriteContext(Path.Combine(runPath, "control", "voltage-restoration-verified.json"), new
                    { BaselineSha256 = baseline.Sha256, ResultSha256 = result.Sha256, options.ServiceVersion, OriginalVerified = true });
                },
                () => service.ShutdownOnce(deadline.Token),
                () =>
                {
                    while (service.ReadState() == CpuPmChildState.Running)
                    { deadline.Token.ThrowIfCancellationRequested(); pins.AssertAllPins(); Thread.Sleep(100); }
                    service.AssertState(CpuPmChildState.Terminal); serviceTerminal = true;
                },
                () => owner.Cleanup(deadline.Token), () => Thread.Sleep(100));
            if (!cleaned) { return Park("CompletionOrCleanupUnknown"); }
            watchdog.Dispose(); watchdog = null;
            child.ReleaseAfterTerminal(); service.ReleaseAfterTerminal(); pins.Dispose(); deadline.Dispose();
            Log("OriginalVerified_ServiceTerminal_OwnedDriverAbsent"); return 0;
        }
        catch (Exception exception)
        {
            if (mutationPossible) { return Park("Unknown_" + exception.GetType().Name); }
            Log("BlockedBeforeDriverMutation_" + exception.GetType().Name); return 3;
        }
        finally { GC.KeepAlive(root); }
    }
    // Retains the existing terminal-child/cleanup reducer; the sole workload
    // extension verifies immutable restoration evidence and drains Service first.
    internal static bool Complete(ICpuPmRetainedChild child, Func<bool> uncertain, Action context, Action revoke,
        Action verifyRestoration, Action shutdown, Action waitServiceTerminal, Func<CpuPmDriverCleanup> cleanup, Action delay)
        => CpuPmSupervisorCompletion.Finish(child, uncertain, context, revoke, () =>
        {
            verifyRestoration(); context(); if (uncertain()) { throw new IOException("Deadline before shutdown."); }
            shutdown(); waitServiceTerminal(); context(); if (uncertain()) { throw new IOException("Deadline before cleanup."); }
        }, cleanup, delay);
    private readonly long started = Stopwatch.GetTimestamp();
    private TimeSpan RemainingBudget()
    { var remaining = Budget - Stopwatch.GetElapsedTime(started); return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero; }
    private void AssertContext()
    {
        deadline.Token.ThrowIfCancellationRequested(); pins.AssertAllPins();
        if (service is null) { throw new IOException("Service not bound."); }
        service.AssertState(serviceTerminal ? CpuPmChildState.Terminal : CpuPmChildState.Running);
        deadline.Token.ThrowIfCancellationRequested();
    }
    private void PinPackage()
    {
        CpuPmAuthenticatedChildRun.PinAncestors(pins, options.Payload); pins.PinDirectory(options.Payload, true, true);
        var manifestPin = pins.PinFile(Path.Combine(options.Payload, "manifest.json"), CpuPmLaunchSchema.VoltageManifestLimit, true);
        manifest = CpuPmLaunchSchema.ParseManifest(manifestPin.ReadBounded(CpuPmLaunchSchema.VoltageManifestLimit), options.Source, options.Manifest,
            ["OpenBlade.Capture.exe", "OpenBlade.Capture.dll", "OpenBlade.Capture.deps.json", "OpenBlade.Capture.runtimeconfig.json",
                "firmware-smoke/OpenBlade.FirmwareSmoke.exe", "firmware-smoke/OpenBlade.FirmwareSmoke.dll",
                "firmware-smoke/OpenBlade.FirmwareSmoke.deps.json", "firmware-smoke/OpenBlade.FirmwareSmoke.runtimeconfig.json",
                "service/OpenBlade.Service.exe", "service/OpenBlade.Service.dll", "service/OpenBlade.Service.deps.json", "service/OpenBlade.Service.runtimeconfig.json"], voltageValidation: true);
        CpuPmAuthenticatedChildRun.ValidatePayload(pins, options.Payload, manifest, deadline.Token, voltageValidation: true);
        using var self = WindowsCpuPmProcessProbe.Current(); var observed = self.Read(true);
        if (observed.State != CpuPmChildState.Running || observed.Image != Path.Combine(options.Payload, "OpenBlade.Capture.exe"))
        { throw new IOException("Owner must execute from reviewed pinned package."); }
        string installed = Path.GetDirectoryName(WindowsCpuVoltageServiceProcess.Image)!;
        CpuPmAuthenticatedChildRun.PinAncestors(pins, installed); pins.PinDirectory(installed, false, false);
        foreach (var file in manifest.Where(f => f.Name.StartsWith("service/", StringComparison.Ordinal)))
        {
            string path = Path.Combine(installed, file.Name[8..].Replace('/', '\\'));
            CpuPmAuthenticatedChildRun.PinAncestors(pins, path);
            var pin = pins.PinFile(path, CpuPmLaunchSchema.MaximumFileBytes, false);
            if (pin.Length != file.Length || pin.Sha256 != file.Sha256) { throw new IOException("Installed Service closure differs from reviewed package."); }
        }
        foreach (string file in manifest.Select(entry => entry.Name).Where(name =>
            Path.GetFileName(name).StartsWith("OpenBlade.", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.Ordinal)))
        {
            string version = FileVersionInfo.GetVersionInfo(Path.Combine(options.Payload, file.Replace('/', '\\'))).ProductVersion ?? "";
            if (!SourceVersion(version, options.Source)
                || file == "service/OpenBlade.Service.dll" && version != options.ServiceVersion)
            { throw new IOException("Reviewed source-qualified assembly version mismatch."); }
        }
        pins.AssertAllPins();
    }
    internal static bool SourceVersion(string value, string source)
    {
        string[] parts = value.Split('+');
        return CpuPmLaunchSchema.IsHex(source, 40, true) && value.Length <= 64 && !value.Any(char.IsWhiteSpace) && parts.Length == 2 && Version.TryParse(parts[0], out var version) && version.Build >= 0
            && (parts[1] == source || parts[1] == "voltage-validation." + source[..8]);
    }
    private void WriteContext(string path, object value)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
        _ = pins.PinFile(path, CpuPmLaunchSchema.ManifestLimit, true);
    }
    private void MarkUncertain()
    {
        if (Interlocked.Exchange(ref uncertain, 1) != 0) { return; }
        try { owner?.MarkUncertain(); } catch { }
        try { deadline.Cancel(); } catch { }
        Log("ResourcesRetained_NoRetry_NoCleanup");
    }
    private int Park(string reason)
    {
        MarkUncertain(); Log(reason);
        // Never terminate the child/Service, dispose retained resources or infer
        // restoration. The original kernel objects and callbacks remain rooted.
        using var parked = new ManualResetEventSlim(false); parked.Wait(); return 4;
    }
    private void Log(string value)
    { try { lock (output) { output.WriteLine("CpuVoltageOwner=" + value); output.Flush(); } } catch { } }
}
