#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace OpenBlade.DeploymentBroker;

// Private fixture transport. No inheritable endpoints exist in the launcher,
// including while unrelated same-process Process.Start callers run.
internal sealed class ScratchChildChannels : IDisposable
{
    internal const string Argument = "--scratch-channels";
    internal const PipeOptions ServerOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance;
    internal const HandleInheritability Inheritability = HandleInheritability.None;
    private const string Prefix = "OpenBlade.Scratch.Channels.";
    private readonly NamedPipeServerStream[] pipes;
    internal string Arguments { get; }
    internal Stream Input => pipes[0];
    internal Stream Output => pipes[1];
    internal Stream Error => pipes[2];

    internal ScratchChildChannels()
    {
        string id = Guid.NewGuid().ToString("N");
        Arguments = Argument + " " + id + " " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        // Acquire records ownership before any fallible initialization.
        pipes = Acquire(i => new NamedPipeServerStream(Name(id, i), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, ServerOptions, 4096, 4096), (_, _) => { });
    }

    internal static T[] Acquire<T>(Func<int, T> create, Action<T, int> initialize) where T : IDisposable
    {
        List<T> acquired = [];
        try
        {
            for (int i = 0; i < 3; i++)
            {
                T resource = create(i);
                acquired.Add(resource);
                initialize(resource, i);
            }
            return acquired.ToArray();
        }
        catch (Exception failure)
        {
            ScratchFixtureFailure.Cleanup(failure, () => ScratchReceiverClosure.DisposeRetainedResources(acquired.Cast<IDisposable>()));
            throw;
        }
    }

    internal void Connect(uint expectedChild, Action requireLive)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task.WhenAll(pipes.Select(pipe => pipe.WaitForConnectionAsync(deadline.Token)))
            .WaitAsync(deadline.Token).GetAwaiter().GetResult();
        foreach (var pipe in pipes)
        {
            requireLive();
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) || peer != expectedChild)
            {
                throw new InvalidDataException("The scratch channel client differs from the created child.");
            }
            requireLive();
        }
    }

    internal static string Name(string id, int channel) => Prefix + id + "." + channel.ToString(CultureInfo.InvariantCulture);

    internal static NamedPipeClientStream[] OpenChild(string[] args)
    {
        var (id, parent) = ParseArguments(args);
        return Acquire(i => new NamedPipeClientStream(".", Name(id, i), PipeDirection.InOut, PipeOptions.None,
                System.Security.Principal.TokenImpersonationLevel.Identification, Inheritability),
            (pipe, _) =>
            {
                pipe.Connect(10000);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint peer) || peer != parent)
                { throw new InvalidDataException("The scratch channel server differs from its launcher."); }
            });
    }

    internal static (string Id, uint Parent) ParseArguments(string[] args)
    {
        if (args.Length != 3 || args[0] != Argument || !Guid.TryParseExact(args[1], "N", out var id)
            || args[1] != id.ToString("N")
            || id == Guid.Empty || !uint.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint parent) || parent == 0)
        {
            throw new InvalidDataException("Invalid scratch channel arguments.");
        }
        return (args[1], parent);
    }

    public void Dispose() => ScratchReceiverClosure.DisposeRetainedResources(pipes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint id);
}
#endif
