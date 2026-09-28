using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture;

internal static class Rz090528PadlessLoadedHyperBoostValidationCommand
{
    internal const string Name = "validate-rz09-0528-02c6-hyperboost-padless-loaded";
    internal const string Confirmation = "RZ09-0528:1532:02C6:BIOS-2.02:PERFORMANCE-AUTO:GPU-ZERO:APPLY-ONCE:RESTORE:10S:EXPERIMENTAL-75C";
    internal const string AttendedConfirmation = "ATTENDED:PAD-USB-AND-POWER-DISCONNECTED:STEEL-NOMAD-DX12";

    internal static bool ValidArguments(string[] args) => args.Length == 12 && args.Take(6).SequenceEqual(new[]
    {
        "--confirm-target", Confirmation, "--confirm-pad-disconnected", AttendedConfirmation,
        "--plan-sha256", Rz090528PadlessLoadedValidation.PlanSha256,
    }) && args[6] == "--workload-exe" && ExactPath(args[7], StockSteelNomadWorkload.ExpectedExecutableName)
        && args[8] == "--hwinfo-exe" && ExactPath(args[9], "HWiNFO64.exe")
        && args[10] == "--hwinfo-log" && LocalPath(args[11])
        && Path.GetExtension(args[11]).Equals(".json", StringComparison.OrdinalIgnoreCase);

    private static bool LocalPath(string path) => Path.IsPathFullyQualified(path)
        && !Path.GetPathRoot(path)!.StartsWith("\\\\", StringComparison.Ordinal);

    private static bool ExactPath(string path, string name) => LocalPath(path)
        && Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase);

    internal static Rz090528LoadedReading ComposeReading(HwinfoCpuLogObservation cpu,
        StockGpuTelemetry gpu, StockLoadWorkloadObservation workload, string? workloadIdentity,
        DateTimeOffset observedAt)
    {
        double? effectiveCpu = cpu.Celsius is { } current
            ? Math.Max(current, cpu.NewSamplesMaximumCelsius ?? current) : null;
        double age = cpu.SourceTimestampUtc is { } source ? (observedAt - source).TotalMilliseconds : double.NaN;
        string? failure = cpu.Failure ?? gpu.Failure ?? workload.Failure;
        if (cpu.SourceTimestampUtc is null || !double.IsFinite(age) || age < 0 || age > 4000)
        {
            failure ??= "CpuSourceNotFreshAfterExternalReads";
        }
        if (!gpu.ExactDeviceConfirmed || gpu.GpuCelsius is not > 0 || gpu.GpuUtilizationPercent is not <= 100)
        {
            failure ??= "ExactGpuTelemetryUnavailable";
        }
        return new(cpu.SourceTimestampUtc ?? default, age, effectiveCpu, gpu.GpuCelsius,
            workload.RunningExact, workloadIdentity, gpu.GpuUtilizationPercent, failure);
    }

    internal static async Task<int> RunWindowsAsync(string[] args, TextWriter output, TextWriter progress, CancellationToken token)
    {
        if (!ValidArguments(args))
        {
            await output.WriteLineAsync("Exact target, attended workload, reviewed plan and explicit HWiNFO/workload paths required.");
            return 2;
        }
        bool runnerEntered = false;
        Rz090528PadlessHyperBoostValidation? runner = null;
        PerformanceFanSetterValidationResult? result = null;
        var observations = new List<object>();
        var gpuOffsets = new GpuOffsetEvidenceLog();
        object[] frozenGpuOffsets = [];
        object? providerProvenance = null;
        string? outerFailure = null;
        bool finalGpuZero = false;
        StockWorkloadProcess? acceptedWorkload = null;
        OwnedReadOnlyLane<LoadedExternalSnapshot>? lane = null;
        VerifiedHwinfoProcess? provider = null;
        HwinfoTimestampedCpuLog? cpu = null;
        bool externalTelemetryQuiesced = true;
        try
        {
            provider = await VerifiedHwinfoProcess.OpenAsync(args[9], token);
            providerProvenance = new
            {
                provider.Identity.ProcessId,
                provider.Identity.StartedAtUtc,
                ExecutableName = "HWiNFO64.exe",
                provider.Identity.Sha256,
                provider.Identity.Version,
                provider.Identity.SignatureVerified,
                Signer = "REALiX"
            };
            cpu = new HwinfoTimestampedCpuLog(args[11], provider.Identity, provider.Observe);
            StockGpuTelemetry? currentGpu = null;
            var workload = new StockSteelNomadWorkload(args[7], StockSteelNomadWorkload.EnumerateWindows,
                () => currentGpu?.GpuUtilizationPercent);
            bool armed = false;
            lane = new(() => { cpu.Dispose(); provider.Dispose(); });
            async ValueTask<Rz090528LoadedReading> Read(CancellationToken cancellation)
            {
                LoadedExternalSnapshot snapshot = await lane.InvokeAsync(() =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    HwinfoCpuLogObservation cpuValue = cpu.ReadFresh(cancellation);
                    currentGpu = NvidiaNvmlGpuClockOffsetValidationApi.ReadExactRz090528GpuTelemetry();
                    StockLoadWorkloadObservation observedWorkload = armed ? workload.Observe() : new(false, null, null);
                    cancellation.ThrowIfCancellationRequested();
                    string? binding = workload.Bound is { } bound ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        $"{bound.ProcessId}|{bound.StartedAtUtc:O}|{Path.GetFullPath(bound.Path)}|{bound.Sha256}|{bound.Version}"))) : null;
                    Rz090528LoadedReading combined = ComposeReading(cpuValue, currentGpu, observedWorkload, binding, DateTimeOffset.UtcNow);
                    return new(cpuValue, currentGpu, combined, workload.Bound, null);
                }, accepted =>
                {
                    observations.Add(new { Cpu = accepted.Cpu, Gpu = accepted.Gpu, Combined = accepted.Reading });
                    acceptedWorkload = accepted.Workload;
                }, cancellation);
                return snapshot.Reading!;
            }
            var loaded = new Rz090528PadlessLoadedValidation(Read, at =>
            {
                return lane.InvokeAsync(() =>
                {
                    string? failure = workload.Arm(at);
                    armed = failure is null;
                    return new(null, null, null, null, failure);
                }, null, token).AsTask().GetAwaiter().GetResult().ArmFailure;
            }, at =>
            {
                progress.WriteLine($"ARMED {at:O} - Start one Steel Nomad DX12 run. Experimental 10-second observation; 180-second arming timeout.");
                progress.Flush();
            });
            var nvml = new NvidiaNvmlGpuClockOffsetProbe();
            var nvapi = new NvidiaNvapiGpuClockOffsetProbe();
            bool Eligible()
            {
                try
                {
                    if (!Rz090528HyperBoostValidationCommand.IsIsolated()
                        || !GpuClockOffsetValidationEnvironment.AreGpuTuningToolsInactive()
                        || NvidiaNvmlGpuClockOffsetValidationApi.ReadDriverVersion() != "617.14")
                    {
                        return false;
                    }
                    var values = new[] { nvml.Query(), nvapi.Query() };
                    bool zero = values.All(value => value.DeviceName == GpuClockOffsetValidationPlan.Rz090528UiStep.ExpectedDeviceName
                        && value.Core.CurrentMegahertz == 0 && value.Memory.CurrentMegahertz == 0);
                    gpuOffsets.Add(new { Nvml = values[0], Nvapi = values[1], ZeroConfirmed = zero });
                    return zero && Rz090528HyperBoostValidationCommand.IsIsolated();
                }
                catch { return false; }
            }
            runner = Rz090528PadlessHyperBoostValidation.CreateWindows(Eligible, loaded: loaded);
            runnerEntered = true;
            result = await runner.RunAsync(PerformanceFanValidationTarget.HyperBoostFromPerformance, token);
            if (runner.DisposalConfirmed && runner.LoadedResult?.SnapshotQuiesced != false)
            {
                finalGpuZero = Eligible();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            outerFailure = error is OperationCanceledException ? "Canceled" : error.GetType().Name + ":ExternalValidationFailed";
        }
        finally
        {
            if (lane is not null) { externalTelemetryQuiesced = await lane.CloseAsync(); }
            else { cpu?.Dispose(); provider?.Dispose(); }
            frozenGpuOffsets = gpuOffsets.Close();
        }
        bool success = outerFailure is null && result is { Success: true, Restored: true }
            && runner?.LoadedResult is { Completed: true, SnapshotQuiesced: true } && runner.DisposalConfirmed
            && finalGpuZero && externalTelemetryQuiesced && !token.IsCancellationRequested;
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            Target = "RZ09-05289EN4 / 1532:02C6 / BIOS 2.02 / RTX 5090 Laptop / driver 617.14",
            Rz090528PadlessLoadedValidation.PlanSha256,
            Plan = JsonSerializer.Deserialize<JsonElement>(Rz090528PadlessLoadedValidation.ReadPlan()),
            Hwinfo = providerProvenance,
            Workload = acceptedWorkload is { } bound ? new
            {
                bound.ProcessId,
                bound.StartedAtUtc,
                ExecutableName = StockSteelNomadWorkload.ExpectedExecutableName,
                bound.Sha256,
                bound.Version
            } : null,
            Result = result,
            LoadedObservation = runner?.LoadedResult,
            ExternalTelemetry = observations,
            IndependentGpuOffsets = frozenGpuOffsets,
            GpuOffsetEvidenceTruncated = gpuOffsets.Truncated,
            FinalGpuZeroConfirmed = finalGpuZero,
            FailureReconciliation = runner?.FailureReconciliation,
            HardwareOpened = runner?.HardwareOpened ?? false,
            DisposalConfirmed = runner?.DisposalConfirmed ?? !runnerEntered,
            ExternalTelemetryQuiesced = externalTelemetryQuiesced,
            SnapshotQuiesced = runner?.LoadedResult?.SnapshotQuiesced ?? (runner?.DisposalConfirmed ?? !runnerEntered),
            OuterFailure = outerFailure,
            PossibleSubmission = result?.ApplyAttempted ?? runnerEntered,
            ManualRecoveryRequired = result is null ? runnerEntered
                : result.ManualRecoveryRequired || result.IndeterminatePartialWrite
                    || result.ApplyAttempted && !result.Restored
                    || outerFailure is not null && runner?.HardwareOpened == true && runner.DisposalConfirmed != true,
            HardwareRoundTripConfirmed = result is { ApplyAcknowledged: true, CandidateConfirmed: true, Restored: true } && finalGpuZero,
            ProductionPadlessAdmitted = false,
            ThermalSafetyEstablished = false,
            GpuWritesPerformed = false,
            CoolingPadFeatureWritesPerformed = false,
            WorkloadLaunchedOrStopped = false,
            Success = success,
        }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        return success ? 0 : 3;
    }
}

internal sealed record LoadedExternalSnapshot(HwinfoCpuLogObservation? Cpu, StockGpuTelemetry? Gpu,
    Rz090528LoadedReading? Reading, StockWorkloadProcess? Workload, string? ArmFailure);

internal sealed class GpuOffsetEvidenceLog
{
    private readonly object gate = new();
    private readonly List<object> values = [];
    private bool closed;
    internal bool Truncated { get; private set; }

    internal void Add(object value)
    {
        lock (gate)
        {
            if (closed) { return; }
            if (values.Count < 512) { values.Add(value); }
            else { Truncated = true; }
        }
    }

    internal object[] Close()
    {
        lock (gate) { closed = true; return values.ToArray(); }
    }
}

// One owned read-only worker: no HID, setters, retries after faults, or disposal beneath a pending read.
internal sealed class OwnedReadOnlyLane<T> where T : class
{
    private sealed record Request(Func<T> Read, TaskCompletionSource<T> Completion);
    private readonly Channel<Request> requests = Channel.CreateBounded<Request>(1);
    private readonly object gate = new();
    private readonly Task worker;
    private readonly TimeSpan timeout;
    private bool closed;
    private bool pending;

    internal OwnedReadOnlyLane(Action dispose, TimeSpan? timeout = null)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(2);
        worker = Task.Run(async () =>
        {
            try
            {
                await foreach (Request request in requests.Reader.ReadAllAsync())
                {
                    try { request.Completion.TrySetResult(request.Read()); }
                    catch (Exception error) { request.Completion.TrySetException(error); }
                }
            }
            finally { dispose(); }
        });
    }

    internal async ValueTask<T> InvokeAsync(Func<T> read, Action<T>? publish, CancellationToken token)
    {
        long start = Stopwatch.GetTimestamp();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (closed || pending) { throw new InvalidOperationException("ExternalTelemetryLaneUnavailable"); }
            pending = true;
            if (!requests.Writer.TryWrite(new(read, completion))) { throw new InvalidOperationException("ExternalTelemetryQueueUnavailable"); }
        }
        using CancellationTokenRegistration registration = token.Register(Close);
        try
        {
            T result = await completion.Task.WaitAsync(timeout, token).ConfigureAwait(false);
            lock (gate)
            {
                if (closed || token.IsCancellationRequested || Stopwatch.GetElapsedTime(start) > timeout)
                {
                    throw new TimeoutException("ExternalTelemetryDeadline");
                }
                publish?.Invoke(result);
                pending = false;
                return result;
            }
        }
        catch { Close(); throw; }
    }

    private void Close()
    {
        lock (gate) { closed = true; requests.Writer.TryComplete(); }
    }

    internal async Task<bool> CloseAsync()
    {
        Close();
        try { await worker.WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch { return false; }
    }
}

internal sealed class VerifiedHwinfoProcess : IDisposable
{
    internal const string ExpectedVersion = "8.48-5990";
    private readonly FileStream executable;
    internal HwinfoProcessIdentity Identity { get; }

    private VerifiedHwinfoProcess(FileStream executable, HwinfoProcessIdentity identity)
    {
        this.executable = executable;
        Identity = identity;
    }

    internal static bool TrustedSignature(string json)
    {
        using JsonDocument value = JsonDocument.Parse(json);
        return value.RootElement.GetProperty("Status").GetString() == "Valid"
            && value.RootElement.GetProperty("Publisher").GetString() == "REALiX, s.r.o.";
    }

    internal static async Task<VerifiedHwinfoProcess> OpenAsync(string path, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).Equals("HWiNFO64.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Explicit HWiNFO executable required.");
        }
        string canonical = Path.GetFullPath(path);
        // The signature/hash refer to this locked executable throughout the experiment.
        var locked = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            ProcessStartInfo start = CreateSignatureStartInfo(canonical);
            using Process helper = Process.Start(start) ?? throw new IOException("Signature helper unavailable.");
            Task<string> stdout = helper.StandardOutput.ReadToEndAsync(token);
            Task<string> stderr = helper.StandardError.ReadToEndAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await helper.WaitForExitAsync(timeout.Token); }
            catch
            {
                try { if (!helper.HasExited) { helper.Kill(); } } catch { }
                throw;
            }
            string signature = await stdout;
            await stderr;
            if (helper.ExitCode != 0 || !TrustedSignature(signature)) { throw new InvalidDataException("HwinfoSignatureNotVerified"); }
            string version = FileVersionInfo.GetVersionInfo(canonical).FileVersion ?? string.Empty;
            if (version != ExpectedVersion) { throw new InvalidDataException("HwinfoVersionNotReviewed"); }
            string hash = Convert.ToHexString(SHA256.HashData(locked));
            HwinfoProcessIdentity identity = ObserveExact(canonical, hash, version)
                ?? throw new InvalidDataException("ExactHwinfoProcessUnavailable");
            token.ThrowIfCancellationRequested();
            return new(locked, identity);
        }
        catch { locked.Dispose(); throw; }
    }

    internal HwinfoProcessIdentity? Observe()
    {
        if (executable.SafeFileHandle.IsClosed) { return null; }
        return ObserveExact(Identity.ExecutablePath, Identity.Sha256, Identity.Version);
    }

    internal static ProcessStartInfo CreateSignatureStartInfo(string canonical)
    {
        string inbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0");
        var start = new ProcessStartInfo(Path.Combine(inbox, "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // PS7's inherited module search path can select an incompatible Security module in Windows PowerShell.
        start.Environment["PSModulePath"] = Path.Combine(inbox, "Modules");
        start.Environment["OPENBLADE_HWINFO_SIGNATURE_PATH"] = canonical;
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; $s=Get-AuthenticodeSignature -LiteralPath $env:OPENBLADE_HWINFO_SIGNATURE_PATH; @{Status=$s.Status.ToString();Publisher=$s.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false)}|ConvertTo-Json -Compress" })
        {
            start.ArgumentList.Add(argument);
        }
        return start;
    }

    private static HwinfoProcessIdentity? ObserveExact(string path, string hash, string version)
    {
        Process[] processes = Process.GetProcessesByName("HWiNFO64");
        try
        {
            if (processes.Length != 1 || processes[0].HasExited) { return null; }
            string? actual = ReadProcessPath(processes[0].Id);
            if (actual is null || !Path.GetFullPath(actual).Equals(path, StringComparison.OrdinalIgnoreCase)) { return null; }
            return new(processes[0].Id, processes[0].StartTime.ToUniversalTime(), path, hash, version, true);
        }
        finally
        {
            foreach (Process process in processes) { process.Dispose(); }
        }
    }

    public void Dispose() => executable.Dispose();

    private static string? ReadProcessPath(int processId)
    {
        using SafeProcessHandle handle = OpenProcess(0x1000, false, processId);
        if (handle.IsInvalid) { return null; }
        var path = new StringBuilder(32768);
        int size = path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);
}
