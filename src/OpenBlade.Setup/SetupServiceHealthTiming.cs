using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.Setup;

internal sealed class SetupServiceHealthTiming(string purpose, TimeSpan budget)
{
    internal readonly IpcRequestWriteTiming RequestWrite = new();
    internal IpcStageTiming Connect;
    internal IpcStageTiming ResponseRead;
    internal long DeadlineArmedQpc;
    private readonly long probeBeginQpc = Stopwatch.GetTimestamp();
    internal uint? ServerProcessId { get; private set; }
    internal bool StagesObserved { get; set; }

    internal void IdentifyPeer(Stream connection)
    {
        try
        {
            if (connection is NamedPipeClientStream pipe
                && GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId))
            {
                ServerProcessId = processId;
            }
        }
        catch (Exception)
        {
            // The connection may already be broken. This ID is for correlation only.
        }
    }

    internal void Report(string outcome, string? cause, bool callerCanceled, bool probeCanceled)
    {
        long endQpc = Stopwatch.GetTimestamp();
        try
        {
            long nominalDeadlineQpc = DeadlineArmedQpc
                + (long)(budget.TotalSeconds * Stopwatch.Frequency);
            string identity = FormattableString.Invariant(
                $"Purpose={purpose} ProcessId={Environment.ProcessId} ServerProcessId={ServerProcessId} QpcFrequency={Stopwatch.Frequency}");
            string deadline = FormattableString.Invariant(
                $"ProbeBeginQpc={probeBeginQpc} DeadlineArmedQpc={DeadlineArmedQpc} NominalDeadlineQpc={nominalDeadlineQpc} ProbeEndQpc={endQpc} BudgetMs={budget.TotalMilliseconds}");
            string result = $"Outcome={outcome} Cause={cause ?? "none"} CallerCanceled={callerCanceled} ProbeCanceled={probeCanceled}";
            Trace.WriteLine($"OpenBlade setup service health timing. {identity} {deadline} {result} {FormatStages(nominalDeadlineQpc)}");
        }
        catch (Exception)
        {
            // Trace listeners are best effort and cannot change setup or rollback.
        }
    }

    private string FormatStages(long nominalDeadlineQpc)
    {
        if (!StagesObserved)
        {
            return "StageTiming=unavailable";
        }

        string remaining = FormattableString.Invariant(
            $"ConnectEndRemainingMs={RemainingMs(Connect.EndQpc, nominalDeadlineQpc):F3} HeaderBeginRemainingMs={RemainingMs(RequestWrite.Header.BeginQpc, nominalDeadlineQpc):F3}");
        return $"{remaining} {FormatStage("Connect", Connect)} {FormatStage("HeaderWrite", RequestWrite.Header)} "
            + $"{FormatStage("BodyWrite", RequestWrite.Body)} {FormatStage("Flush", RequestWrite.Flush)} "
            + FormatStage("ResponseRead", ResponseRead);
    }

    private static double? RemainingMs(long? qpc, long deadlineQpc)
        => (deadlineQpc - qpc) * 1000d / Stopwatch.Frequency;

    private static string FormatStage(string name, IpcStageTiming stage)
        => stage.BeginQpc is null
            ? $"{name}=not-entered"
            : FormattableString.Invariant(
                $"{name}=[BeginQpc={stage.BeginQpc} EndQpc={stage.EndQpc} Succeeded={stage.Succeeded} CanceledAtEntry={stage.CanceledAtEntry} CanceledAtExit={stage.CanceledAtExit}]");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
