using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

internal interface IThxAudioHelperChild : IAsyncDisposable
{
    bool HasExited { get; }

    int? ExitCode { get; }

    bool AssignToLifetimeOwner();

    Task<ThxAudioHelperResponse> ExchangeAsync(
        ThxAudioHelperRequest request,
        CancellationToken cancellationToken);

    Task<bool> ConfirmExitAsync(CancellationToken cancellationToken);

    Task<bool> TerminateAndConfirmExitAsync();
}

internal interface IThxAudioHelperChildFactory : IAsyncDisposable
{
    IThxAudioHelperChild Start();
}

internal sealed class ThxAudioHelperProcessClient : IThxAudioWorker, IAsyncDisposable
{
    private readonly TimeSpan readTimeout;
    private readonly TimeSpan mutationTimeout;
    private IThxAudioHelperChildFactory? childFactory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IThxAudioHelperChild? unresolvedChild;
    private bool disposed;

    internal ThxAudioHelperProcessClient(
        TimeSpan? readTimeout = null,
        TimeSpan? mutationTimeout = null,
        IThxAudioHelperChildFactory? childFactory = null)
    {
        this.readTimeout = readTimeout ?? TimeSpan.FromSeconds(8);
        this.mutationTimeout = mutationTimeout ?? TimeSpan.FromSeconds(12);
        this.childFactory = childFactory;
    }

    public async Task<ThxAudioWorkerResult> ExecuteAsync(
        ThxAudioHelperRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!await ReconcileUnresolvedChildAsync().ConfigureAwait(false))
            {
                return BlockedByUnresolvedChild();
            }

            bool mutation = request is not ThxHelperReadRequest;
            IThxAudioHelperChild child;
            try
            {
                childFactory ??= new WindowsThxAudioHelperChildFactory();
                child = childFactory.Start();
            }
            catch (Exception exception)
            {
                return new ThxAudioWorkerResult(
                    ThxAudioOperationStatus.Failed,
                    ThxAudioAvailability.ComponentsUnavailable,
                    $"The THX helper could not be started: {exception.GetType().Name}.",
                    null);
            }
            if (!child.AssignToLifetimeOwner())
            {
                bool stopped = await child.TerminateAndConfirmExitAsync().ConfigureAwait(false);
                await RetainOrDisposeAsync(child, stopped).ConfigureAwait(false);
                return new ThxAudioWorkerResult(
                    ThxAudioOperationStatus.Failed,
                    ThxAudioAvailability.ComponentsUnavailable,
                    stopped
                        ? "The THX helper could not be assigned to its lifetime owner."
                        : "The THX helper lifetime could not be contained or confirmed.",
                    null);
            }

            TimeSpan timeout = mutation ? mutationTimeout : readTimeout;
            using var deadline = new CancellationTokenSource(timeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
            try
            {
                ThxAudioHelperResponse response = await child.ExchangeAsync(
                    request,
                    linked.Token).ConfigureAwait(false);
                bool exited = await child.ConfirmExitAsync(linked.Token).ConfigureAwait(false);
                if (!exited)
                {
                    bool stopped = await child.TerminateAndConfirmExitAsync().ConfigureAwait(false);
                    await RetainOrDisposeAsync(child, stopped).ConfigureAwait(false);
                    return stopped
                        ? TerminatedAfterUnconfirmedExit(mutation)
                        : UnconfirmedExit(mutation);
                }

                if (child.ExitCode != 0)
                {
                    throw new InvalidDataException("The THX helper exited abnormally.");
                }

                ValidateResponse(response);
                await child.DisposeAsync().ConfigureAwait(false);
                return new ThxAudioWorkerResult(
                    response.Status,
                    response.Availability,
                    response.Error,
                    response.State);
            }
            catch (OperationCanceledException)
            {
                bool stopped = await child.TerminateAndConfirmExitAsync().ConfigureAwait(false);
                await RetainOrDisposeAsync(child, stopped).ConfigureAwait(false);
                return mutation
                    ? new ThxAudioWorkerResult(
                        ThxAudioOperationStatus.Indeterminate,
                        stopped
                            ? ThxAudioAvailability.Available
                            : ThxAudioAvailability.ComponentsUnavailable,
                        "The THX helper timed out after a possible mutation. Read state before retrying.",
                        null)
                    : new ThxAudioWorkerResult(
                        ThxAudioOperationStatus.Failed,
                        stopped
                            ? ThxAudioAvailability.Available
                            : ThxAudioAvailability.ComponentsUnavailable,
                        "The THX helper timed out while reading state.",
                        null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                bool stopped = await child.TerminateAndConfirmExitAsync().ConfigureAwait(false);
                await RetainOrDisposeAsync(child, stopped).ConfigureAwait(false);
                return mutation
                    ? new ThxAudioWorkerResult(
                        ThxAudioOperationStatus.Indeterminate,
                        stopped
                            ? ThxAudioAvailability.Available
                            : ThxAudioAvailability.ComponentsUnavailable,
                        $"The THX helper ended after a possible mutation: {exception.GetType().Name}.",
                        null)
                    : new ThxAudioWorkerResult(
                        ThxAudioOperationStatus.Failed,
                        stopped
                            ? ThxAudioAvailability.Available
                            : ThxAudioAvailability.ComponentsUnavailable,
                        $"The THX helper read failed: {exception.GetType().Name}.",
                        null);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            if (unresolvedChild is not null)
            {
                _ = await unresolvedChild.TerminateAndConfirmExitAsync().ConfigureAwait(false);
                await unresolvedChild.DisposeAsync().ConfigureAwait(false);
                unresolvedChild = null;
            }
            if (childFactory is not null)
            {
                await childFactory.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> ReconcileUnresolvedChildAsync()
    {
        if (unresolvedChild is null)
        {
            return true;
        }
        if (!unresolvedChild.HasExited)
        {
            return false;
        }
        await unresolvedChild.DisposeAsync().ConfigureAwait(false);
        unresolvedChild = null;
        return true;
    }

    private async Task RetainOrDisposeAsync(IThxAudioHelperChild child, bool exited)
    {
        if (exited || child.HasExited)
        {
            await child.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            unresolvedChild = child;
        }
    }

    private static ThxAudioWorkerResult BlockedByUnresolvedChild() => new(
        ThxAudioOperationStatus.Unavailable,
        ThxAudioAvailability.ComponentsUnavailable,
        "A previous THX helper has not exited. THX audio remains blocked.",
        null);

    private static ThxAudioWorkerResult UnconfirmedExit(bool mutation) => new(
        mutation ? ThxAudioOperationStatus.Indeterminate : ThxAudioOperationStatus.Failed,
        ThxAudioAvailability.ComponentsUnavailable,
        "The THX helper exit could not be confirmed. THX audio remains blocked.",
        null);

    private static ThxAudioWorkerResult TerminatedAfterUnconfirmedExit(bool mutation) => new(
        mutation ? ThxAudioOperationStatus.Indeterminate : ThxAudioOperationStatus.Failed,
        ThxAudioAvailability.Available,
        "The THX helper did not exit normally and was terminated.",
        null);

    private static void ValidateResponse(ThxAudioHelperResponse response)
    {
        if (response.Version != ThxAudioHelperProtocol.Version
            || !Enum.IsDefined(response.Status)
            || !Enum.IsDefined(response.Availability)
            || response.Status == ThxAudioOperationStatus.Succeeded
                && (response.Availability is not (ThxAudioAvailability.Available or ThxAudioAvailability.AvailableReadOnly)
                    || response.Error is not null
                    || response.State is null)
            || response.Status != ThxAudioOperationStatus.Succeeded
                && (string.IsNullOrWhiteSpace(response.Error) || response.State is not null))
        {
            throw new InvalidDataException("The THX helper response is invalid.");
        }
    }
}

internal sealed class WindowsThxAudioHelperChildFactory : IThxAudioHelperChildFactory
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private readonly SafeJobHandle job;
    private bool disposed;

    internal WindowsThxAudioHelperChildFactory()
    {
        job = CreateJobObjectW(nint.Zero, null);
        if (job.IsInvalid)
        {
            throw new InvalidOperationException("The THX helper lifetime owner could not be created.");
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformationClass,
                    buffer,
                    (uint)size))
            {
                throw new InvalidOperationException(
                    "The THX helper lifetime policy could not be configured.");
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public IThxAudioHelperChild Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int parentProcessId = Environment.ProcessId;
        string pipeName = ThxAudioHelperProtocol.CreatePipeName(
            parentProcessId,
            Guid.NewGuid());
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.WriteThrough,
            inBufferSize: 4096,
            outBufferSize: 4096);
        Process? process = null;
        try
        {
            string executable = Path.GetFullPath(
                Environment.ProcessPath
                ?? throw new InvalidOperationException("The Session Agent path is unavailable."));
            if (!File.Exists(executable))
            {
                throw new InvalidOperationException(
                    "The fixed Session Agent executable is unavailable.");
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add(ThxAudioHelperProtocol.CommandSwitch);
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add(
                parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("The THX helper process could not be started.");
            }
            return new WindowsThxAudioHelperChild(job, process, pipe);
        }
        catch
        {
            process?.Dispose();
            pipe.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            job.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        nint information,
        uint informationLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }
}

internal sealed class WindowsThxAudioHelperChild(
    WindowsThxAudioHelperChildFactory.SafeJobHandle job,
    Process process,
    NamedPipeServerStream pipe) : IThxAudioHelperChild
{
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private bool disposed;

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return process.HasExited ? process.ExitCode : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public bool AssignToLifetimeOwner()
    {
        try
        {
            return AssignProcessToJobObject(job, process.SafeHandle);
        }
        catch
        {
            return false;
        }
    }

    public async Task<ThxAudioHelperResponse> ExchangeAsync(
        ThxAudioHelperRequest request,
        CancellationToken cancellationToken)
    {
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint childProcessId)
            || childProcessId != (uint)process.Id)
        {
            throw new InvalidDataException("The THX helper pipe client identity is invalid.");
        }

        using var input = new StreamReader(
            pipe,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        using var output = new StreamWriter(
            pipe,
            new UTF8Encoding(false, true),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
        };
        string requestJson = JsonSerializer.Serialize(request, JsonOptions);
        if (requestJson.Length > ThxAudioHelperProtocol.MaximumLineCharacters)
        {
            throw new InvalidDataException("The THX helper request exceeds 64 KiB.");
        }
        await output.WriteLineAsync(requestJson.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        string? responseJson = await ThxAudioHelperProtocol.ReadBoundedLineAsync(
            input,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(responseJson))
        {
            throw new InvalidDataException("The THX helper response is invalid.");
        }
        return JsonSerializer.Deserialize<ThxAudioHelperResponse>(responseJson, JsonOptions)
            ?? throw new InvalidDataException("The THX helper response is missing.");
    }

    public async Task<bool> ConfirmExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TerminateAndConfirmExitAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            using var timeout = new CancellationTokenSource(TerminationTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            await pipe.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        WindowsThxAudioHelperChildFactory.SafeJobHandle job,
        SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);
}
