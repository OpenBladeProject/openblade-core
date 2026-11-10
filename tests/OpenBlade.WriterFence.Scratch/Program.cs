using System.ComponentModel;
using System.Diagnostics;
using OpenBlade.WriterFence.Scratch;

if (args is ["hold", string root, string rootId, string lockId, string generation] &&
    Guid.TryParseExact(generation, "N", out _))
{
    try
    {
        using (WriterLease.Acquire(root, rootId, lockId))
        {
            Console.WriteLine("held");
            Console.Out.Flush();
            if (Console.ReadLine() != "retire")
            {
                return 3;
            }
        }
        Console.WriteLine($"retired:managed:{generation}");
        Console.Out.Flush();
        return Console.ReadLine() == "exit" ? 0 : 3;
    }
    catch (Win32Exception e) when (e.NativeErrorCode == 32)
    {
        return 2;
    }
    catch (Exception e) when (e is Win32Exception or IOException or ArgumentException)
    {
        Console.Error.WriteLine(e.Message);
        return 3;
    }
}

if (args is ["rehearse", string rustExe, string parent] &&
    Path.IsPathFullyQualified(rustExe) &&
    Path.GetFileName(rustExe) == "openblade-scratch-rust-writer.exe")
{
    await Rehearsal.Run(rustExe, Path.GetFullPath(parent));
    return 0;
}

return 3;

internal sealed class Rehearsal(string rustExe, string root, string rootId, string lockId)
{
    private enum Backend { Managed, Candidate }

    private readonly string lockPath = Path.Combine(root, WriterLease.Name);

    internal static async Task Run(string rustExe, string parent)
    {
        using var parentPin = PinnedRoot.OpenPath(parent);
        string name = $"openblade-writer-fence-{Guid.NewGuid():N}";
        string root = Path.Combine(parent, name);
        try
        {
            string rootId;
            string lockId;
            using (var directory = Native.OpenRelative(parentPin.Handle, name, directory: true, create: true))
            {
                rootId = Native.Inspect(directory, directory: true).Identity;
                using var file = Native.OpenRelative(directory, WriterLease.Name, directory: false, create: true);
                lockId = Native.Inspect(file, directory: false).Identity;
            }
            var fixture = new Rehearsal(rustExe, root, rootId, lockId);
            await fixture.Transfer(Backend.Managed, Backend.Candidate);
            await fixture.Transfer(Backend.Candidate, Backend.Managed);
            await fixture.RejectChangedIdentity();
            await fixture.Crash(Backend.Managed, Backend.Candidate);
            await fixture.Crash(Backend.Candidate, Backend.Managed);
            using var cleanup = Native.OpenRelative(parentPin.Handle, name, directory: true, delete: true);
            Check(Native.Inspect(cleanup, directory: true).Identity == rootId, "cleanup root was replaced");
            using (var file = Native.OpenRelative(cleanup, WriterLease.Name, directory: false, delete: true))
            {
                Native.DeleteAdmitted(file, lockId, directory: false);
            }
            Native.DeleteAdmitted(cleanup, rootId, directory: true);
        }
        catch
        {
            Console.Error.WriteLine($"Scratch evidence retained at {root}");
            throw;
        }
    }

    private async Task Transfer(Backend outgoing, Backend incoming)
    {
        using var writer = Start(outgoing);
        await writer.RequireHeld();
        using (var contender = Start(incoming))
        {
            await contender.RequireExit(2);
            Check(string.IsNullOrEmpty(await contender.Output()), "contender reported ownership");
        }
        using (var invalid = Start(incoming, expectedLock: lockId[..9] + "0000000000000000"))
        {
            await invalid.RequireExit(3);
        }
        await writer.Release();
        using var successor = Start(incoming);
        await successor.RequireHeld();
        Check(!writer.Process.HasExited, "original writer exited before explicit release was proved");
        await writer.Finish();
        await successor.Retire();
        Console.WriteLine($"PASS {outgoing} -> {incoming}: live contention and clean release");
    }

    private async Task RejectChangedIdentity()
    {
        foreach (Backend backend in Enum.GetValues<Backend>())
        {
            using var malformed = Start(backend, expectedRoot: "malformed");
            await malformed.RequireExit(3);
            using var malformedLock = Start(backend, expectedLock: "malformed");
            await malformedLock.RequireExit(3);
        }
        File.WriteAllText(lockPath, "unexpected bytes");
        await RejectBoth();
        Check(File.ReadAllText(lockPath) == "unexpected bytes", "lease bytes were changed");
        File.WriteAllBytes(lockPath, []);

        // Keep the old file alive so the filesystem cannot recycle its ID.
        string displaced = lockPath + ".original";
        File.Move(lockPath, displaced);
        try
        {
            await RejectBoth(); // Missing leaf must never create a new fence.
            Check(!File.Exists(lockPath), "missing lease was recreated");
            File.WriteAllBytes(lockPath, []);
            await RejectBoth();
        }
        finally
        {
            File.Delete(lockPath);
            File.Move(displaced, lockPath);
        }

        // Move the actual root aside and create a same-named decoy.
        string previousRoot = root + ".original";
        Directory.Move(root, previousRoot);
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(lockPath, []);
            await RejectBoth();
        }
        finally
        {
            File.Delete(lockPath);
            Directory.Delete(root);
            Directory.Move(previousRoot, root);
        }

        Console.WriteLine("PASS both backends reject malformed, nonempty, missing, or replaced identity");
    }

    private async Task RejectBoth()
    {
        foreach (Backend backend in Enum.GetValues<Backend>())
        {
            using var child = Start(backend);
            await child.RequireExit(3);
            Check(string.IsNullOrEmpty(await child.Output()), "unadmitted writer reported ownership");
        }
    }

    private async Task Crash(Backend outgoing, Backend incoming)
    {
        string pending = Path.Combine(root, "battery-configuration.commit.json.recovery-required");
        byte[] evidence = "opaque interrupted transaction"u8.ToArray();
        File.WriteAllBytes(pending, evidence);
        using var writer = Start(outgoing);
        await writer.RequireHeld();
        writer.Process.Kill();
        await writer.WaitForExit();
        Check(writer.Process.ExitCode != 0, "abrupt exit was reported as clean");
        Check(string.IsNullOrEmpty(await writer.Output()), "abrupt exit emitted a retirement receipt");

        // An exclusive handle becomes available after termination, even while
        // retirement and recovery are unresolved.
        using var successor = Start(incoming);
        await successor.RequireHeld();
        await successor.Retire();
        Check(File.ReadAllBytes(pending).SequenceEqual(evidence), "recovery evidence was changed");
        File.Delete(pending);
        Console.WriteLine($"PASS {outgoing} abrupt exit: {incoming} reacquired; no receipt and recovery bytes preserved");
    }

    private Child Start(Backend backend, string? expectedRoot = null, string? expectedLock = null)
    {
        Guid generation = Guid.NewGuid();
        var info = new ProcessStartInfo
        {
            FileName = backend == Backend.Managed
                ? Environment.ProcessPath ?? throw new InvalidOperationException("Managed apphost required.")
                : rustExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (backend == Backend.Managed)
        {
            Check(Path.GetFileName(info.FileName) == "OpenBlade.WriterFence.Scratch.exe", "managed apphost required");
            info.ArgumentList.Add("hold");
        }
        info.ArgumentList.Add(root);
        info.ArgumentList.Add(expectedRoot ?? rootId);
        info.ArgumentList.Add(expectedLock ?? lockId);
        info.ArgumentList.Add(generation.ToString("N"));
        return new Child(Process.Start(info) ?? throw new InvalidOperationException("Child failed to start."), backend, generation);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class Child(Process process, Backend backend, Guid generation) : IDisposable
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
        public Process Process { get; } = process;

        public async Task RequireHeld()
        {
            string? line = await Process.StandardOutput.ReadLineAsync().WaitAsync(Deadline);
            if (line != "held")
            {
                throw new InvalidOperationException($"Expected {backend} ownership. {await Error()}");
            }
        }

        public async Task Retire()
        {
            await Release();
            await Finish();
        }

        public async Task Release()
        {
            await Send("retire");
            string role = backend == Backend.Managed ? "managed" : "candidate";
            string? line = await Process.StandardOutput.ReadLineAsync().WaitAsync(Deadline);
            Check(line == $"retired:{role}:{generation:N}",
                "missing or incorrect child retirement receipt");
        }

        public async Task Finish()
        {
            await Send("exit");
            await RequireExit(0);
            Check(string.IsNullOrEmpty(await Output()), "unexpected output after retirement");
        }

        private async Task Send(string command)
        {
            await Process.StandardInput.WriteLineAsync(command).WaitAsync(Deadline);
            await Process.StandardInput.FlushAsync().WaitAsync(Deadline);
        }

        public async Task RequireExit(int code)
        {
            await WaitForExit();
            if (Process.ExitCode != code)
            {
                throw new InvalidOperationException(
                    $"{backend} exited {Process.ExitCode}, expected {code}. {await Error()}");
            }
        }

        public Task WaitForExit() => Process.WaitForExitAsync().WaitAsync(Deadline);
        public Task<string> Output() => Process.StandardOutput.ReadToEndAsync().WaitAsync(Deadline);
        private Task<string> Error() => Process.StandardError.ReadToEndAsync().WaitAsync(Deadline);

        public void Dispose()
        {
            using (Process)
            {
                if (!Process.HasExited)
                {
                    Process.Kill();
                    if (!Process.WaitForExit((int)Deadline.TotalMilliseconds))
                    {
                        throw new TimeoutException("Scratch child did not exit.");
                    }
                }
            }
        }
    }
}
