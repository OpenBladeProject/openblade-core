using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;

namespace OpenBlade.Capture;

// Developer-only fixed owner entry. No firmware operands or approval cross the
// pipe: byte 1 renews caller liveness, byte 2 requests Auto, EOF/invalid data
// requests stop. Both exact consent prompts remain in the owner's own console.
internal static class So6120GuardianHost
{
    internal const string OwnerName = "run-so6120-supervised-guardian";
    internal const int IsolationRefused = 4;
    internal const int ControlRefused = 5;
    internal const int CallerUnavailable = 6;
    internal const int OwnerFailure = 7;

    internal static bool IsTrialName(string name) => name is So6120SilentPwmCommand.Name
        or So6120SilentPwmCommand.BalancedName or So6120SilentPwmCommand.HalfCycleName or So6120SilentPwmCommand.StopName;

    internal static bool TryParseOwner(IReadOnlyList<string> args, out string? trial, out string? root, out string? handle)
    {
        trial = root = handle = null;
        if (args.Count != 6 || args[0] != "--trial" || !IsTrialName(args[1]) || args[2] != "--run-root"
            || !So6120SilentPwmCommand.TryParse([args[2], args[3]], out root) || root is null
            || args[4] != "--control-handle" || !long.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            || value <= 0 || !Environment.Is64BitProcess)
        { return false; }
        trial = args[1]; handle = args[5]; return true;
    }

    internal static async Task<int> RunClientAsync(string trial, string root, TextWriter output)
    {
        if (!IsTrialName(trial) || !OperatingSystem.IsWindows() || Console.IsInputRedirected
            || Console.IsOutputRedirected || !Environment.UserInteractive)
        { await output.WriteLineAsync("An attended elevated Windows console is required."); return 2; }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        { await output.WriteLineAsync("Start the reviewed runner elevated; it does not request UAC itself."); return 2; }
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Capture executable path unavailable.");
        if (!string.Equals(Path.GetFileName(executable), "OpenBlade.Capture.exe", StringComparison.OrdinalIgnoreCase))
        { await output.WriteLineAsync("Use the reviewed Capture apphost executable, not a shared dotnet host."); return 2; }
        if (Directory.Exists(root) || File.Exists(root))
        { await output.WriteLineAsync("Run root already exists; never reuse an attempt directory."); return 2; }

        using var control = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var stopPump = new CancellationTokenSource();
        int autoRequested = 0;
        using var registration = ConsoleCancellation.Register(() => Interlocked.Exchange(ref autoRequested, 1));
        try
        {
            So6120GuardianChild launched;
            try
            {
                launched = So6120GuardianProcess.Start(executable,
                    [OwnerName, "--trial", trial, "--run-root", root, "--control-handle", control.GetClientHandleAsString()],
                    control.ClientSafePipeHandle, showConsole: true);
            }
            finally { control.DisposeLocalCopyOfClientHandle(); }
            using var owner = launched;
            // The control pump is independent of console output/backpressure.
            var pump = Task.Run(() => PumpControlAsync(control, () => Volatile.Read(ref autoRequested) != 0, stopPump.Token));
            try
            {
                try
                {
                    await output.WriteLineAsync($"Guardian PID {owner.Id} owns the attended trial in its separate console. Complete both approvals there. Ctrl+C here requests Auto; closing this client requests cleanup. Never close or kill the guardian.");
                    await output.WriteLineAsync($"Reports: {root}. A process exit alone does not prove Auto or native release; retain the closed row and journal.");
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                { Interlocked.Exchange(ref autoRequested, 1); }
                int exitCode = await owner.WaitForExitAsync(); // No timeout kill, reconnect or second writer.
                try { await output.WriteLineAsync(FormatOwnerExit(exitCode, root)); }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { }
                return exitCode;
            }
            finally
            {
                await stopPump.CancelAsync();
                control.Dispose();
                try { await pump.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { _ = pump.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await output.WriteLineAsync($"Guardian launch/control failed: {error.GetType().Name}: {error.Message}. No fallback, retry or owner termination was attempted.");
            return 2;
        }
    }

    internal static string FormatOwnerExit(int exitCode, string root)
    {
        string manifest = Path.Combine(root, "attended-run.jsonl");
        string preserveEvidence = $"Leave OpenBlade stopped. Preserve the run folder and inspect its durable evidence "
            + $"before any new hardware command. Manifest: {manifest}";
        return exitCode switch
        {
            0 => $"Guardian reported a completed trial (exit 0). Manifest: {manifest}",
            3 => $"Guardian reported that the trial was declined without setting writes (exit 3). Manifest: {manifest}",
            IsolationRefused => $"Guardian isolation refused (exit {exitCode}). No hardware acquisition occurred.",
            ControlRefused => $"Guardian inherited control refused (exit {exitCode}). No hardware acquisition occurred.",
            CallerUnavailable => $"Guardian caller connection unavailable (exit {exitCode}). No hardware acquisition occurred.",
            OwnerFailure => $"Guardian setup or reporting failed (exit {exitCode}). {preserveEvidence}",
            _ => $"Guardian exited without a confirmed successful trial (exit {exitCode}). {preserveEvidence}",
        };
    }

    internal static async Task PumpControlAsync(Stream control, Func<bool> autoRequested, CancellationToken cancellationToken)
    {
        byte[] command = new byte[1];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool auto = autoRequested();
                command[0] = auto ? (byte)2 : (byte)1;
                await control.WriteAsync(command, cancellationToken);
                if (auto) { return; }
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { control.Dispose(); } // EOF requests cleanup; never replace the owner.
    }

    internal static async Task<int> RunOwnerAsync(IReadOnlyList<string> args, TextReader input, TextWriter output)
    {
        if (!OperatingSystem.IsWindows() || !TryParseOwner(args, out string? trial, out string? root, out string? handle))
        { await output.WriteLineAsync("Invalid supervised guardian entry."); return 2; }
        // These checks precede metadata, consent and every acquisition path.
        try { So6120GuardianProcess.VerifyOwnerIsolation(); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { await output.WriteLineAsync($"Guardian isolation refused before acquisition: {error.Message}"); return IsolationRefused; }
        AnonymousPipeClientStream connected;
        try
        {
            connected = new AnonymousPipeClientStream(PipeDirection.In, handle!);
            try { So6120GuardianProcess.VerifyInheritedControl(connected.SafePipeHandle); }
            catch { connected.Dispose(); throw; }
        }
        catch (Exception error) when (error is ArgumentException or IOException or System.ComponentModel.Win32Exception)
        { await output.WriteLineAsync($"Guardian control refused before acquisition: {error.Message}"); return ControlRefused; }
        using var control = connected;
        await using var signals = new So6120GuardianSignals();
        using var stopReader = new CancellationTokenSource();
        var reader = ReadControlAsync(control, signals, stopReader.Token);
        try
        {
            try
            {
                await signals.FirstHeartbeat.WaitAsync(TimeSpan.FromSeconds(5), signals.StopToken);
                signals.StopToken.ThrowIfCancellationRequested();
            }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException)
            { await output.WriteLineAsync("Caller connection unavailable; no new trial may start."); return CallerUnavailable; }

            try { return await So6120SilentPwmCommand.RunOwnedWindowsAsync(trial!, root!, input, output, signals); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                || error is OperationCanceledException && signals.StopToken.IsCancellationRequested)
            {
                try { await output.WriteLineAsync($"Guardian setup or reporting failed: {error.GetType().Name}: {error.Message}"); }
                catch (Exception outputError) when (outputError is IOException or ObjectDisposedException) { }
                return OwnerFailure;
            }
        }
        finally
        {
            await stopReader.CancelAsync();
            await reader; // Polling never leaves an outstanding blocking pipe read.
        }
    }

    internal static async Task ReadControlAsync(AnonymousPipeClientStream control, So6120GuardianSignals signals,
        CancellationToken cancellationToken)
    {
        byte[] commands = new byte[64];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!So6120GuardianProcess.TryGetAvailableControlBytes(control.SafePipeHandle, out uint available))
                { signals.CallerDisconnected(); return; }
                if (available != 0)
                {
                    // This inherited endpoint has one reader; consume only
                    // already available data, including after a latched stop.
                    int read = control.Read(commands, 0, (int)Math.Min(available, (uint)commands.Length));
                    if (read == 0) { signals.CallerDisconnected(); return; }
                    for (int index = 0; index < read; index++)
                    {
                        if (!signals.Receive(commands[index])) { signals.CallerDisconnected(); }
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        { signals.CallerDisconnected(); }
    }
}
