using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Capture;

internal sealed record WindowsBatteryObservationOptions(string OutputPath, int DurationSeconds, int IntervalSeconds, string Workload);

// Developer-only, OS battery queries. Deliberately does not contact the control service:
// ordinary GetStatus can activate user configuration, and cached RPM has no sample timestamp.
internal static class WindowsBatteryObservationCommand
{
    internal const string Name = "observe-windows-battery";
    private const string WorkerName = "--windows-battery-observation-worker";
    private const int QueryTimeoutSeconds = 8;

    public static bool Recognizes(string command) => command is Name or WorkerName;

    internal static WindowsBatteryObservationOptions Parse(string[] args)
    {
        if (args.Length is < 2 or > 5 || args[0] != Name || string.IsNullOrWhiteSpace(args[1]))
        {
            throw new ArgumentException("Usage: observe-windows-battery <new.jsonl> [seconds:1..900] [interval:1..60] [idle-light|uncontrolled]");
        }
        int duration = args.Length > 2 ? ParseBounded(args[2], 1, 900) : 300;
        int interval = args.Length > 3 ? ParseBounded(args[3], 1, 60) : 5;
        string workload = args.Length > 4 ? args[4] : "uncontrolled";
        if (workload is not ("idle-light" or "uncontrolled"))
        {
            throw new ArgumentException("Workload must be idle-light or uncontrolled; this is an operator label, not a measurement.");
        }
        return new(Path.GetFullPath(args[1]), duration, interval, workload);
    }

    private static int ParseBounded(string value, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number >= minimum && number <= maximum
            ? number : throw new ArgumentException($"Expected an integer from {minimum} through {maximum}.");

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && args[0] == WorkerName)
        {
            Console.WriteLine(JsonSerializer.Serialize(WindowsBatteryObservationReader.Capture()));
            return 0;
        }

        WindowsBatteryObservationOptions options;
        try { options = Parse(args); }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler onCancel = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            // CreateNew preserves earlier evidence; caller chooses/creates its private output directory.
            await using FileStream file = new(options.OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using StreamWriter writer = new(file, new UTF8Encoding(false)) { AutoFlush = true };
            long origin = Stopwatch.GetTimestamp();
            await WriteAsync(writer, new
            {
                Kind = "metadata",
                SchemaVersion = 1,
                StartedUtc = DateTimeOffset.UtcNow,
                options.DurationSeconds,
                options.IntervalSeconds,
                options.Workload,
                WorkloadSource = "operator-label",
                QueryTimeoutSeconds,
                Source = "Windows battery class read queries; no service, HID or raw EC access",
                SensorSampleTime = "not exposed; timestamps bound host queries only",
                FanRpm = "not collected",
                PerformanceMode = "not collected",
            });

            int count = 0;
            string endReason = "duration-complete";
            while (Stopwatch.GetElapsedTime(origin).TotalSeconds < options.DurationSeconds)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
                double startedSeconds = Stopwatch.GetElapsedTime(origin).TotalSeconds;
                var query = await QueryIsolatedAsync(cancellation.Token);
                await WriteAsync(writer, new
                {
                    Kind = "observation",
                    Sequence = ++count,
                    StartedUtc = startedUtc,
                    FinishedUtc = DateTimeOffset.UtcNow,
                    StartedElapsedSeconds = startedSeconds,
                    FinishedElapsedSeconds = Stopwatch.GetElapsedTime(origin).TotalSeconds,
                    query.Outcome,
                    query.Snapshot,
                });
                Console.WriteLine($"Observation {count}: {query.Outcome}");
                if (query.Snapshot is null || !HasTemperature(query.Snapshot))
                {
                    // Repeating an unsupported/malformed query cannot resolve the thermal question.
                    endReason = query.Snapshot is null ? query.Outcome : "temperature-unavailable";
                    break;
                }
                double remaining = options.DurationSeconds - Stopwatch.GetElapsedTime(origin).TotalSeconds;
                if (remaining <= options.IntervalSeconds)
                {
                    break;
                }
                if (remaining > 0)
                {
                    // No catch-up burst after a slow query, and no overlapping native calls.
                    await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), cancellation.Token);
                }
            }
            await WriteAsync(writer, new { Kind = "end", EndedUtc = DateTimeOffset.UtcNow, Count = count, Reason = endReason });
            return endReason == "duration-complete" ? 0 : 3;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Recording canceled; completed JSONL observations remain available.");
            return 130;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Recording failed: {exception.GetType().Name}.");
            return 1;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    internal static bool HasTemperature(WindowsBatteryObservationSnapshot snapshot) =>
        snapshot.Enumeration.Outcome == "Read"
        && snapshot.Batteries.Any(battery => battery.IsSystemBattery == true
            && battery.Temperature.Result.Outcome == "Read" && battery.Temperature.Celsius.HasValue);

    private static Task WriteAsync(StreamWriter writer, object value) => writer.WriteLineAsync(JsonSerializer.Serialize(value));

    private static async Task<(string Outcome, WindowsBatteryObservationSnapshot? Snapshot)> QueryIsolatedAsync(CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(Environment.ProcessPath ?? throw new IOException("Process path unavailable."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(WindowsBatteryObservationCommand).Assembly.Location);
        }
        start.ArgumentList.Add(WorkerName);
        using Process process = Process.Start(start) ?? throw new IOException("Battery reader could not start.");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(QueryTimeoutSeconds));
        Task? pending = null;
        try
        {
            Task<string> output = ReadBoundedAsync(process.StandardOutput, 65536, deadline.Token);
            Task<string> error = ReadBoundedAsync(process.StandardError, 4096, deadline.Token);
            pending = Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            await pending.WaitAsync(deadline.Token);
            if (process.ExitCode != 0 || error.Result.Length != 0)
            {
                return ("worker-failed", null);
            }
            return ("read", ParseSnapshot(output.Result));
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidDataException or JsonException)
        {
            if (!await TerminateAsync(process))
            {
                return ("worker-termination-unconfirmed-stop", null);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return (exception is OperationCanceledException ? "query-timeout-stop" : "invalid-worker-output-stop", null);
        }
        finally
        {
            if (pending is not null)
            {
                // A stream can complete after the deadline. Observe a late aggregate fault.
                _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    internal static WindowsBatteryObservationSnapshot ParseSnapshot(string json)
    {
        var snapshot = JsonSerializer.Deserialize<WindowsBatteryObservationSnapshot>(json);
        if (snapshot?.Enumeration is null || snapshot.Batteries is null || snapshot.Batteries.Count > 16
            || snapshot.Batteries.Any(battery => battery?.Identity is null
                || battery.Temperature?.Result is null || battery.Status?.Result is null))
        {
            throw new InvalidDataException("Worker output lacks required observation fields.");
        }
        return snapshot;
    }

    private static async Task<bool> TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(stop.Token);
            return true;
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static async Task<string> ReadBoundedAsync(TextReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        char[] buffer = new char[1024];
        StringBuilder text = new();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (read > maximumCharacters - text.Length) { throw new InvalidDataException("Worker output limit exceeded."); }
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }
}
