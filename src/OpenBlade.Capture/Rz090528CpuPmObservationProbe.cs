namespace OpenBlade.Capture;

internal sealed record CpuPmCaptureArguments(Guid RunId);

// Parsing is separate from authority. This command cannot reach a native provider
// until an authenticated live lifecycle owner and pinned output sink are supplied.
internal static class Rz090528CpuPmObservationProbe
{
    internal const string Name = "cpu-pm-observe-rz09-0528";
    internal const string TransportApproval = "--approve-attended-pm-pci-transport-and-kernel-copy";
    internal const string LifetimeApproval = "--acknowledge-fresh-run-owned-pinned-driver";
    internal const string RunIdFlag = "--run-id";
    internal const string PrivateRoot = @"C:\OpenBlade\validation-archive\cpu-tuning-re\pm-observation-runs";
    internal const string Usage = Name + " " + TransportApproval + " " + LifetimeApproval + " " + RunIdFlag + " <lowercase-guid-N>";

    internal static CpuPmCaptureArguments? Parse(string[]? args)
    {
        if (args is not { Length: 4 }) { return null; }
        bool transport = false, lifetime = false;
        Guid run = Guid.Empty;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case TransportApproval when !transport: transport = true; break;
                case LifetimeApproval when !lifetime: lifetime = true; break;
                case RunIdFlag when run == Guid.Empty && index + 1 < args.Length:
                    string text = args[++index];
                    if (!Guid.TryParseExact(text, "N", out run) || run == Guid.Empty || run.ToString("N") != text) { return null; }
                    break;
                default: return null;
            }
        }
        return transport && lifetime && run != Guid.Empty ? new(run) : null;
    }

    internal static string GetPrivateRunDirectory(CpuPmCaptureArguments args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.RunId == Guid.Empty) { throw new ArgumentException("A fresh nonempty run ID is required.", nameof(args)); }
        return Path.Combine(PrivateRoot, args.RunId.ToString("N"));
    }

    internal static int RunUnavailable(string[] args, TextWriter output, TextWriter error)
    {
        if (Parse(args) is null) { error.WriteLine($"Usage: {Usage}"); return 2; }
        output.WriteLine("PmObservation=Unavailable; authenticated live-parent lifetime authority is not implemented");
        output.WriteLine("NativeExecution=NotStarted; no lifecycle or output files created");
        output.WriteLine("LivePptTarget=Unknown; IndependentRestore=Unproven");
        return 3;
    }
}
