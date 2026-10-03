using System.Management;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// One operation's first denial only; this carries no admission authority.
internal sealed class CpuSavedAdmissionDiagnostic
{
    private string? firstFailure;
    internal string? FirstFailure => Volatile.Read(ref firstFailure);
    internal string? LiveObservationReason { get; set; }

    internal bool Check(bool allowed, string stage)
    {
        if (!allowed) { Interlocked.CompareExchange(ref firstFailure, stage, null); }
        return allowed;
    }
}

internal interface ICpuSavedFirmwareCommandInvoker
{
    // A false/unknown result after invocation must never be treated as proof
    // that the provider did not receive the command.
    Task<CpuSavedTuningSubmission> SendAsync(CpuSavedTuningCommand command,
        Func<bool> freshAdmission, CancellationToken token);
}

internal sealed class WindowsSavedCpuTuningBackend : ICpuSavedTuningBackend
{
    private readonly ICpuTuningObservationSource source;
    private readonly Func<DeviceIdentity, CpuSavedTuningCommand, bool> admitted;
    private readonly ICpuSavedFirmwareCommandInvoker invoker;
    private readonly CpuSavedAdmissionDiagnostic? diagnostic;

    internal WindowsSavedCpuTuningBackend(ICpuTuningObservationSource source,
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission,
        CpuSavedAdmissionDiagnostic? diagnostic = null)
        : this(source, (identity, command) => freshRuntimeAdmission(identity, command)
            && CheckStage(Rz090528CpuFreshUsbIdentity.Read() is { Model: "RZ09-0528", VendorId: 0x1532, ProductId: 0x02C6 }, diagnostic, "fresh USB identity")
            && CheckStage(Rz090528CpuControllerPresence.ControllersAbsent(), diagnostic, "fresh controller process/service inventory"),
            new WindowsCpuSavedFirmwareCommandInvoker(), diagnostic)
    { }

    internal WindowsSavedCpuTuningBackend(ICpuTuningObservationSource source,
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission,
        ICpuSavedFirmwareCommandInvoker invoker, CpuSavedAdmissionDiagnostic? diagnostic = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        admitted = freshRuntimeAdmission ?? throw new ArgumentNullException(nameof(freshRuntimeAdmission));
        this.invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        this.diagnostic = diagnostic;
    }

    public Task<bool> CheckWriteAdmissionAsync(DeviceIdentity identity, CpuSavedTuningCommand command,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Check(identity, command));
    }

    private bool Check(DeviceIdentity identity, CpuSavedTuningCommand command) =>
        CheckStage(identity is { Model: "RZ09-0528", VendorId: 0x1532, ProductId: 0x02C6 }, diagnostic, "saved command device identity")
        && CheckStage(IsProductionCommandAdmitted(command), diagnostic, "saved command semantic encoding")
        && CheckStage(admitted(identity, command), diagnostic, "fresh runtime admission")
        && CheckStage(CpuHardwareIdentity.Same(source.ReadIdentity(), identity), diagnostic, "fresh host identity");

    private static bool CheckStage(bool allowed, CpuSavedAdmissionDiagnostic? diagnostic, string stage) =>
        diagnostic?.Check(allowed, stage) ?? allowed;

    public Task<SavedCpuFirmwareReadResult> ReadAsync(DeviceIdentity identity, CancellationToken token) =>
        CpuFirmwareReadScheduler.RunAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!CpuHardwareIdentity.Same(source.ReadIdentity(), identity))
            {
                return new SavedCpuFirmwareReadResult(SavedCpuFirmwareReadStatus.UnsupportedIdentity,
                    null, null, 0, 0);
            }
            SavedCpuFirmwareReadResult result = source.ReadSaved(identity);
            token.ThrowIfCancellationRequested();
            return result;
        }, TimeSpan.FromSeconds(10), token);

    public Task<CpuSavedTuningSubmission> SendAsync(DeviceIdentity identity,
        CpuSavedTuningCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Check(identity, command)) { return Task.FromResult(CpuSavedTuningSubmission.NotSubmitted); }
        // The native worker checks again after discovery/version negotiation,
        // immediately before the one bounded typed RunCommand invocation.
        return invoker.SendAsync(command, () => Check(identity, command), token);
    }

    // Reviewed source admission for the exact existing model/SKU guards.
    // This does not replace version-7 negotiation, strict ACK decoding, fresh
    // runtime safety, complete paired readback or exact original restoration.
    // Installed LocalSystem/session IPC validation remains separate.
    internal static bool IsProductionCommandAdmitted(CpuSavedTuningCommand command) =>
        command is { IsWellFormed: true } && CpuSavedSemanticPolicy.MatchesKnownAnswers()
        && CpuSavedSemanticPolicy.MatchesEncoding(command, command.Encode());

    internal static bool CheckFreshAdmission(Func<bool> admission, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool allowed = admission();
        // A synchronous identity/safety probe may outlive the outer deadline.
        // It must not grant a late worker permission to invoke the provider.
        token.ThrowIfCancellationRequested();
        return allowed;
    }

    private sealed class WindowsCpuSavedFirmwareCommandInvoker : ICpuSavedFirmwareCommandInvoker
    {
        private static readonly TimeSpan OperationDeadline = TimeSpan.FromSeconds(10);
        private const bool PinnedMissingReturnValueCompatibility = true;

        public async Task<CpuSavedTuningSubmission> SendAsync(CpuSavedTuningCommand command,
            Func<bool> freshAdmission, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!command.IsWellFormed || !freshAdmission()) { return CpuSavedTuningSubmission.NotSubmitted; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(OperationDeadline);
            CancellationToken workerToken = deadline.Token;
            Task<CpuSavedTuningSubmission> worker = Task.Run(
                () => SendCore(command, freshAdmission, workerToken), CancellationToken.None);
            try
            {
                return await worker.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // COM/provider cancellation cannot prove non-submission. The
                // durable PossibleSubmission journal prevents another command,
                // including restoration, while completion remains unknown.
                deadline.Cancel();
                _ = worker.ContinueWith(static completed => { _ = completed.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted
                        | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return CpuSavedTuningSubmission.Unknown;
            }
        }

        private static CpuSavedTuningSubmission SendCore(CpuSavedTuningCommand command,
            Func<bool> freshAdmission, CancellationToken token)
        {
            bool invoking = false;
            try
            {
                token.ThrowIfCancellationRequested();
                using var search = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM AMD_ACPI");
                search.Options.Timeout = OperationDeadline;
                using ManagementObjectCollection objects = search.Get();
                ManagementObject? instance = null;
                try
                {
                    int count = 0;
                    foreach (ManagementObject candidate in objects)
                    {
                        count++;
                        if (count == 1) { instance = candidate; }
                        else { candidate.Dispose(); }
                    }
                    token.ThrowIfCancellationRequested();
                    if (count != 1 || instance is null || !CheckFreshAdmission(freshAdmission, token))
                    { return CpuSavedTuningSubmission.NotSubmitted; }
                    token.ThrowIfCancellationRequested();
                    using ManagementBaseObject? version = instance.InvokeMethod("QueryVersion", null,
                        new InvokeMethodOptions { Timeout = OperationDeadline });
                    token.ThrowIfCancellationRequested();
                    // This invoker is reachable only through the exact model,
                    // USB and exact-host admission above. Enumerated absence is
                    // distinct from a failed property read; never swallow a
                    // provider error as successful missing-return compatibility.
                    if (!WindowsSavedCpuWmiOutput.AcceptReturnValue(WindowsSavedCpuWmiOutput.Read(version, "ReturnValue"), PinnedMissingReturnValueCompatibility)
                        || !WindowsSavedCpuWmiOutput.AcceptVersion(WindowsSavedCpuWmiOutput.Read(version, "version"))
                        || !CheckFreshAdmission(freshAdmission, token))
                    { return CpuSavedTuningSubmission.NotSubmitted; }
                    using ManagementBaseObject input = instance.GetMethodParameters("RunCommand");
                    input["Inbuf"] = command.Encode();
                    token.ThrowIfCancellationRequested();
                    if (!CheckFreshAdmission(freshAdmission, token)) { return CpuSavedTuningSubmission.NotSubmitted; }
                    token.ThrowIfCancellationRequested();
                    invoking = true;
                    using ManagementBaseObject? result = instance.InvokeMethod("RunCommand", input,
                        new InvokeMethodOptions { Timeout = OperationDeadline });
                    // Acknowledgement is strictly typed, as in the attended
                    // firmware helper. Missing fields or provider errors remain
                    // ambiguous; the transaction always performs full readback.
                    if (!WindowsSavedCpuWmiOutput.AcceptReturnValue(WindowsSavedCpuWmiOutput.Read(result, "ReturnValue"), PinnedMissingReturnValueCompatibility)
                        || WindowsSavedCpuWmiOutput.Read(result, "Outbuf") is not
                        { State: CpuSavedWmiPropertyState.Present, Value: ManagementBaseObject output })
                    { return CpuSavedTuningSubmission.Unknown; }
                    using (output)
                    {
                        return WindowsSavedCpuWmiOutput.AcceptCommandResult(WindowsSavedCpuWmiOutput.Read(output, "result"))
                                ? CpuSavedTuningSubmission.Acknowledged : CpuSavedTuningSubmission.Unknown;
                    }
                }
                finally { instance?.Dispose(); }
            }
            catch (Exception exception) when (exception is ManagementException or System.Runtime.InteropServices.COMException
                or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException
                or System.Security.SecurityException)
            {
                return invoking ? CpuSavedTuningSubmission.Unknown : CpuSavedTuningSubmission.NotSubmitted;
            }
        }
    }
}
