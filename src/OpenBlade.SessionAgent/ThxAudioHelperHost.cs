using System.Diagnostics;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

internal sealed record ThxAudioHelperInvocation(string PipeName, int ParentProcessId)
{
    internal static bool HasCommandSwitch(string[] args) =>
        args.Length > 0
        && string.Equals(
            args[0],
            ThxAudioHelperProtocol.CommandSwitch,
            StringComparison.Ordinal);

    internal static bool TryParse(string[] args, out ThxAudioHelperInvocation? invocation)
    {
        invocation = null;
        if (args.Length != 3
            || !string.Equals(
                args[0],
                ThxAudioHelperProtocol.CommandSwitch,
                StringComparison.Ordinal)
            || !int.TryParse(
                args[2],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int parentProcessId)
            || parentProcessId <= 0
            || !ThxAudioHelperProtocol.IsExpectedPipeName(args[1], parentProcessId))
        {
            return false;
        }

        invocation = new ThxAudioHelperInvocation(args[1], parentProcessId);
        return true;
    }
}

internal interface IThxAudioHelperClientChannel : IAsyncDisposable
{
    TextReader Input { get; }

    TextWriter Output { get; }
}

internal interface IThxAudioHelperChannelConnector
{
    Task<IThxAudioHelperClientChannel> ConnectAsync(
        ThxAudioHelperInvocation invocation,
        CancellationToken cancellationToken);
}

internal interface IThxAudioHelperParentVerifier
{
    bool IsTrustedParent(
        IThxAudioHelperClientChannel channel,
        int expectedParentProcessId);
}

internal sealed class ThxAudioHelperHost(
    IThxAudioHelperChannelConnector connector,
    IThxAudioHelperParentVerifier parentVerifier,
    Func<IThxAudioNativeAdapter> createNative,
    IThxAudioAdmissionSource admission,
    Func<string, ThxAudioEndpoint, bool?>? readSystemEffectsDisabled = null)
{
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(15);

    internal async Task<int> RunAsync(
        ThxAudioHelperInvocation invocation,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(HostTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        await using IThxAudioHelperClientChannel channel = await connector.ConnectAsync(
            invocation,
            linked.Token).ConfigureAwait(false);
        if (!parentVerifier.IsTrustedParent(channel, invocation.ParentProcessId))
        {
            return 2;
        }

        return await new ThxAudioHelperCommand(
                createNative,
                admission,
                channel.Input,
                channel.Output,
                readSystemEffectsDisabled)
            .RunAsync(linked.Token)
            .ConfigureAwait(false);
    }
}

internal sealed class WindowsThxAudioHelperChannelConnector : IThxAudioHelperChannelConnector
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public async Task<IThxAudioHelperClientChannel> ConnectAsync(
        ThxAudioHelperInvocation invocation,
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            invocation.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            using var deadline = new CancellationTokenSource(ConnectTimeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            return new WindowsThxAudioHelperClientChannel(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class WindowsThxAudioHelperClientChannel : IThxAudioHelperClientChannel
{
    private readonly NamedPipeClientStream pipe;
    private readonly StreamReader input;
    private readonly StreamWriter output;

    internal WindowsThxAudioHelperClientChannel(NamedPipeClientStream pipe)
    {
        this.pipe = pipe;
        input = new StreamReader(
            pipe,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        output = new StreamWriter(
            pipe,
            new UTF8Encoding(false, true),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
        };
    }

    internal SafePipeHandle PipeHandle => pipe.SafePipeHandle;

    public TextReader Input => input;

    public TextWriter Output => output;

    public async ValueTask DisposeAsync()
    {
        await output.DisposeAsync().ConfigureAwait(false);
        input.Dispose();
        await pipe.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class WindowsThxAudioHelperParentVerifier : IThxAudioHelperParentVerifier
{
    public bool IsTrustedParent(
        IThxAudioHelperClientChannel channel,
        int expectedParentProcessId)
    {
        try
        {
            if (channel is not WindowsThxAudioHelperClientChannel windowsChannel
                || !GetNamedPipeServerProcessId(
                    windowsChannel.PipeHandle,
                    out uint serverProcessId)
                || serverProcessId != (uint)expectedParentProcessId
                || GetActualParentProcessId() != expectedParentProcessId)
            {
                return false;
            }

            string? currentPath = Environment.ProcessPath;
            using Process parent = Process.GetProcessById(expectedParentProcessId);
            string? parentPath = parent.MainModule?.FileName;
            return currentPath is not null
                && parentPath is not null
                && string.Equals(
                    Path.GetFullPath(currentPath),
                    Path.GetFullPath(parentPath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static int GetActualParentProcessId()
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {Environment.ProcessId}");
        searcher.Options.Timeout = TimeSpan.FromSeconds(2);
        searcher.Options.ReturnImmediately = true;
        searcher.Options.Rewindable = false;
        using ManagementObjectCollection processes = searcher.Get();
        foreach (ManagementBaseObject process in processes)
        {
            using (process)
            {
                if (process["ParentProcessId"] is uint parentProcessId
                    && parentProcessId <= int.MaxValue)
                {
                    return (int)parentProcessId;
                }
            }
        }
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);
}
