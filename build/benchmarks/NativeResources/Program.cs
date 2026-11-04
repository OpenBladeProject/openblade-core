using System.Diagnostics;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Testing;

if (args.Length is < 2 or > 10)
{
    throw new ArgumentException("Usage: NativeResources <developer-harness-exe> <new-output-directory> [phase-seconds=300] [startup-runs=5] [window-kind=dashboard] [window-dwell-ms=0] [--observe-rendering] [--offscreen] [--input-desktop] [--observe-retirement]. See README for diagnostic window kinds.");
}
string executable = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
int phaseSeconds = args.Length >= 3 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 300;
int startupRuns = args.Length >= 4 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 5;
string windowKind = args.Length >= 5 ? args[4] : "dashboard";
int windowDwellMilliseconds = args.Length >= 6 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 0;
string[] switches = args.Skip(6).ToArray();
if (switches.Distinct(StringComparer.Ordinal).Count() != switches.Length
    || switches.Any(value => value is not ("--observe-rendering" or "--offscreen" or "--input-desktop" or "--observe-retirement")))
{
    throw new ArgumentException("Optional switches must be unique: --observe-rendering, --offscreen, --input-desktop, --observe-retirement.");
}
bool observeRendering = switches.Contains("--observe-rendering");
bool observeRetirement = switches.Contains("--observe-retirement");
bool offscreenWindows = switches.Contains("--offscreen");
bool inputDesktop = switches.Contains("--input-desktop");
if (inputDesktop && !offscreenWindows)
{
    throw new ArgumentException("Input-desktop validation requires --offscreen; the activating presentation path is never admitted there.");
}
if (windowDwellMilliseconds is < 0 or > 1000)
{
    throw new ArgumentException("Window dwell must be between zero and 1000 milliseconds.");
}
if (windowKind is not ("dashboard" or "dashboard-empty" or "empty" or "empty-toolwindow" or "empty-toolwindow-titlebar"
    or "empty-toolwindow-restore" or "empty-toolwindow-reset" or "localized-content"))
{
    throw new ArgumentException("Unknown window control kind.");
}
if (windowKind == "localized-content" && (!offscreenWindows || inputDesktop || observeRetirement || observeRendering))
{
    throw new ArgumentException("Localized-content validation requires --offscreen on the private desktop without other observation switches.");
}
if (!File.Exists(executable) || phaseSeconds is < 1 or > 600 || startupRuns is < 1 or > 10)
{
    throw new ArgumentException("Require a harness executable, phases of 1-600 seconds, and 1-10 startup runs.");
}
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
{
    throw new IOException("The evidence directory must be new or empty; previous runs are never overwritten.");
}
Directory.CreateDirectory(output);
string assembly = Path.ChangeExtension(executable, ".dll");
RequireDeveloperHarness(assembly, requireOffscreenSupport: offscreenWindows, requireRetirementSupport: observeRetirement,
    requireLocalizationSupport: windowKind == "localized-content");
string assemblyHash = Hash(assembly);
Dictionary<string, string> binaryHashes = HashBinaries(Path.GetDirectoryName(executable)!);
var runs = new List<NativeRun>();
for (int index = 0; index < startupRuns; index++)
{
    runs.Add(await RunAsync($"startup-{index + 1}", 0));
}
runs.Add(await RunAsync("sustained", phaseSeconds));
if (!binaryHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(
    HashBinaries(Path.GetDirectoryName(executable)!).OrderBy(pair => pair.Key, StringComparer.Ordinal)))
{
    throw new InvalidDataException("The measured assembly changed during the run.");
}
var report = new
{
    schemaVersion = 1,
    capturedAtUtc = DateTimeOffset.UtcNow,
    executable,
    executableSha256 = Hash(executable),
    assemblySha256 = assemblyHash,
    binaryHashes,
    runnerFramework = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    logicalProcessors = Environment.ProcessorCount,
    phaseSeconds,
    startupRuns,
    windowKind,
    windowDwellMilliseconds,
    observeRendering,
    observeRetirement,
    offscreenWindows,
    desktopMode = inputDesktop ? "input" : "private",
    runs,
    notes = "Fresh processes with explicit desktop/presentation mode, warm/uncontrolled OS caches, initial en-US language and per-run preferences. Compare matched offscreen modes when isolating desktop identity. Launch-to-loaded-layout is a parent-observed upper bound with 25ms polling, not first paint or installed-app startup. Process identity comes from the owned process handle, never a name search. External samples include task delays and instrumentation. The localized-content scenario cycles English, Russian and Chinese and collects at bounded retirement checkpoints, including each complete 12-change batch in longer runs; other scenarios do not force child GC during activity phases. CPU percentages are one-logical-core equivalents (CPU seconds / wall seconds * 100), not normalized to machine cores. No battery, energy, real service/driver latency or hardware behavior is measured.",
};
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), json);
Console.WriteLine(json);

async Task<NativeRun> RunAsync(string scenario, int seconds)
{
    WindowsVisualTestDesktop? desktop = inputDesktop ? null : new WindowsVisualTestDesktop();
    Exception? primaryFailure = null;
    try
    {
        return await RunOnDesktopAsync(desktop, scenario, seconds);
    }
    catch (Exception exception)
    {
        primaryFailure = exception;
        throw;
    }
    finally
    {
        try { desktop?.Dispose(); }
        catch (Exception cleanupFailure) when (primaryFailure is not null)
        {
            throw new AggregateException($"Native scenario '{scenario}' failed and desktop cleanup also failed.", primaryFailure, cleanupFailure);
        }
    }
}

async Task<NativeRun> RunOnDesktopAsync(WindowsVisualTestDesktop? desktop, string scenario, int seconds)
{
    string reportPath = Path.Combine(output, scenario + ".json");
    var startInfo = new ProcessStartInfo(executable, $"--resource-performance-audit \"{reportPath}\"")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = Path.GetDirectoryName(executable)!,
    };
    foreach (string key in startInfo.Environment.Keys.Where(key => key.StartsWith("OPENBLADE_", StringComparison.OrdinalIgnoreCase)).ToArray())
    {
        startInfo.Environment.Remove(key);
    }
    startInfo.Environment["OPENBLADE_RESOURCE_PHASE_SECONDS"] = seconds.ToString(CultureInfo.InvariantCulture);
    startInfo.Environment["OPENBLADE_RESOURCE_WINDOW_KIND"] = windowKind;
    startInfo.Environment["OPENBLADE_RESOURCE_WINDOW_DWELL_MS"] = windowDwellMilliseconds.ToString(CultureInfo.InvariantCulture);
    startInfo.Environment["OPENBLADE_RESOURCE_OBSERVE_RENDERING"] = observeRendering ? "1" : "0";
    startInfo.Environment["OPENBLADE_RESOURCE_OBSERVE_RETIREMENT"] = observeRetirement ? "1" : "0";
    startInfo.Environment["OPENBLADE_RESOURCE_OFFSCREEN"] = offscreenWindows ? "1" : "0";
    startInfo.Environment["OPENBLADE_RESOURCE_INPUT_DESKTOP"] = inputDesktop ? "1" : "0";
    if (desktop is not null)
    {
        startInfo.Environment["OPENBLADE_RESOURCE_EXPECTED_DESKTOP"] = desktop.Name;
    }
    startInfo.Environment["OPENBLADE_LOCALIZATION_LANGUAGE"] = "en-US";
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds * 2 + 120));
    Task? exit = null;
    var samples = new List<ExternalSample>();
    double? loadedLayoutMilliseconds = null;
    bool readinessObservedAfterExit = false;
    long previousSample = 0;
    Exception? primaryFailure = null;
    long started = Stopwatch.GetTimestamp();
    using Process process = desktop is null
        ? Process.Start(startInfo) ?? throw new InvalidOperationException("The disconnected resource child did not start.")
        : desktop.Start(startInfo);
    try
    {
        int processId = process.Id;
        DateTime startedAtUtc = process.StartTime.ToUniversalTime();
        exit = process.WaitForExitAsync(timeout.Token);
        while (true)
        {
            if (loadedLayoutMilliseconds is null && File.Exists(reportPath + ".ready"))
            {
                loadedLayoutMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                readinessObservedAfterExit = exit.IsCompleted;
            }
            if (exit.IsCompleted)
            {
                break;
            }
            if (previousSample == 0 || Stopwatch.GetElapsedTime(previousSample) >= TimeSpan.FromSeconds(5))
            {
                try
                {
                    process.Refresh();
                    if (!process.HasExited)
                    {
                        ProcessThreadCollection threads = process.Threads;
                        int threadCount = threads.Count;
                        foreach (ProcessThread thread in threads)
                        {
                            thread.Dispose();
                        }
                        samples.Add(new ExternalSample(Stopwatch.GetElapsedTime(started).TotalSeconds,
                            ReadPhase(reportPath), process.TotalProcessorTime.TotalMilliseconds,
                            process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount, threadCount));
                    }
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                previousSample = Stopwatch.GetTimestamp();
            }
            await Task.Delay(25, timeout.Token);
        }
        await exit;
        if (process.ExitCode != 0)
        {
            string diagnostic = File.Exists(reportPath + ".error.txt")
                ? await File.ReadAllTextAsync(reportPath + ".error.txt") : "No child error artifact.";
            throw new InvalidOperationException($"{scenario} child exited {process.ExitCode}: {diagnostic}");
        }
        if (loadedLayoutMilliseconds is null)
        {
            throw new InvalidDataException("The parent did not observe the loaded-layout marker before child exit.");
        }
        using JsonDocument child = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        if (!child.RootElement.GetProperty("disconnected").GetBoolean()
            || child.RootElement.GetProperty("serviceCalls").GetInt32() != 0
            || child.RootElement.GetProperty("processId").GetInt32() != processId
            || ReadOptionalFlag(child.RootElement, "offscreenWindows") != offscreenWindows
            || ReadOptionalFlag(child.RootElement, "requireInputDesktop") != inputDesktop
            || ReadOptionalFlag(child.RootElement, "observeRetirement") != observeRetirement
            || offscreenWindows && desktop is not null
                && child.RootElement.GetProperty("windowPresentation").GetProperty("desktop")
                    .GetProperty("currentThreadDesktop").GetString() != desktop.Name)
        {
            throw new InvalidDataException("Child identity or disconnected admission did not match.");
        }
        var result = new NativeRun(scenario, processId, startedAtUtc, loadedLayoutMilliseconds.Value, readinessObservedAfterExit,
            Stopwatch.GetElapsedTime(started).TotalSeconds, samples, child.RootElement.Clone());
        await File.WriteAllTextAsync(Path.Combine(output, scenario + ".external.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return result;
    }
    catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
    {
        string phase = ReadPhase(reportPath);
        primaryFailure = new TimeoutException($"Native scenario '{scenario}' timed out during '{phase}'. Partial samples: {reportPath}.partial.json; child diagnostics: {reportPath}.error.txt", exception);
        try
        {
            await File.WriteAllTextAsync(reportPath + ".partial.json",
                JsonSerializer.Serialize(new { scenario, phase, samples }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
        catch (Exception evidenceFailure) when (evidenceFailure is IOException or UnauthorizedAccessException)
        {
            primaryFailure = new AggregateException("Timeout evidence could not be written.", primaryFailure, evidenceFailure);
        }
        throw primaryFailure;
    }
    catch (Exception exception)
    {
        primaryFailure = exception;
        throw;
    }
    finally
    {
        try
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception cleanupFailure) when (primaryFailure is not null)
        {
            throw new AggregateException($"Native scenario '{scenario}' failed and owned-process cleanup also failed.", primaryFailure, cleanupFailure);
        }
        finally
        {
            // Observe the original wait even if process cleanup fails.
            timeout.Cancel();
            try { if (exit is not null) { await exit; } }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
    }
}

static string ReadPhase(string reportPath)
{
    try
    {
        // Do not deny the child's phase publication while sampling it. A read
        // during truncation can be incomplete; label it as a transition.
        using var stream = new FileStream(reportPath + ".phase", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string phase = reader.ReadToEnd();
        return phase is "warmup" or "idle" or "activity" or "retirement" or "dispose" ? phase : "startup-or-transition";
    }
    catch (IOException) { return "startup-or-transition"; }
}

// Earlier default-mode probes predate these optional controls. Missing flags
// mean false; explicit offscreen launches still require matching metadata first.
static bool ReadOptionalFlag(JsonElement report, string name) =>
    report.TryGetProperty(name, out JsonElement value) && value.GetBoolean();

static string Hash(string path)
{
    using FileStream stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static Dictionary<string, string> HashBinaries(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
    .Where(path => Path.GetExtension(path) is ".dll" or ".exe")
    .ToDictionary(path => Path.GetRelativePath(directory, path), Hash, StringComparer.Ordinal);

static void RequireDeveloperHarness(string assemblyPath, bool requireOffscreenSupport, bool requireRetirementSupport,
    bool requireLocalizationSupport)
{
    using FileStream stream = File.OpenRead(assemblyPath);
    using var pe = new PEReader(stream);
    MetadataReader metadata = pe.GetMetadataReader();
    foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        if (metadata.GetString(type.Namespace) != "OpenBlade.Tray" || metadata.GetString(type.Name) != "App")
        {
            continue;
        }
        bool resourceProbe = false;
        bool offscreenProbe = false;
        bool retirementProbe = false;
        bool localizationProbe = false;
        foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
        {
            string name = metadata.GetString(metadata.GetMethodDefinition(methodHandle).Name);
            resourceProbe |= name == "RunResourcePerformanceAuditAsync";
            offscreenProbe |= name == "ShowOffscreenResourceWindow";
            retirementProbe |= name == "ObserveResourceRetirementAsync";
            localizationProbe |= name == "RunResourceLocalizationAuditAsync";
        }
        if (resourceProbe && (!requireOffscreenSupport || offscreenProbe) && (!requireRetirementSupport || retirementProbe)
            && (!requireLocalizationSupport || localizationProbe))
        {
            return;
        }
    }
    throw new InvalidDataException("Require an explicit developer-harness build with the requested resource/offscreen/retirement/localization probe; nothing was launched.");
}

internal sealed record ExternalSample(double ElapsedSeconds, string Phase, double CpuMilliseconds,
    long PrivateBytes, long WorkingSetBytes, int Handles, int Threads);
internal sealed record NativeRun(string Scenario, int ProcessId, DateTime StartedAtUtc, double LaunchToLoadedLayoutMilliseconds, bool ReadinessObservedAfterExit,
    double TotalSeconds, IReadOnlyList<ExternalSample> Samples, JsonElement Child);
