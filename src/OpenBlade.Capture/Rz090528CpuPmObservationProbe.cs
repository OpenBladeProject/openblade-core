using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record CpuPmCaptureArguments(Guid RunId, CpuPmObservationProfile Profile = CpuPmObservationProfile.IdleThreeSamples);

// Parsing is separate from authority. This command cannot reach a native provider
// until an authenticated live lifecycle owner and pinned output sink are supplied.
internal static class Rz090528CpuPmObservationProbe
{
    internal const string Name = "cpu-pm-observe-rz09-0528";
    internal const string RangeName = "cpu-pm-range-observe-rz09-0528";
    internal const string RangeApproval = "--approve-attended-range-capability-and-metadata";
    internal const string TransportApproval = "--approve-attended-pm-pci-transport-and-kernel-copy";
    internal const string LifetimeApproval = "--acknowledge-fresh-run-owned-pinned-driver";
    internal const string RunIdFlag = "--run-id";
    internal const string PrivateRoot = @"C:\OpenBlade\validation-archive\cpu-tuning-re\pm-observation-runs";
    internal const string Usage = Name + " " + TransportApproval + " " + LifetimeApproval + " " + RunIdFlag + " <lowercase-guid-N>";
    internal const string RangeUsage = RangeName + " " + TransportApproval + " " + LifetimeApproval + " " + RangeApproval
        + " " + RunIdFlag + " <lowercase-guid-N>";

    internal static CpuPmCaptureArguments? Parse(string[]? args, CpuPmObservationProfile profile = CpuPmObservationProfile.IdleThreeSamples)
    {
        bool range = profile == CpuPmObservationProfile.RangeSingleSample;
        if (profile is not (CpuPmObservationProfile.IdleThreeSamples or CpuPmObservationProfile.RangeSingleSample)
            || args is null || args.Length != (range ? 5 : 4)) { return null; }
        bool transport = false, lifetime = false, rangeApproved = false;
        Guid run = Guid.Empty;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case TransportApproval when !transport: transport = true; break;
                case LifetimeApproval when !lifetime: lifetime = true; break;
                case RangeApproval when range && !rangeApproved: rangeApproved = true; break;
                case RunIdFlag when run == Guid.Empty && index + 1 < args.Length:
                    string text = args[++index];
                    if (!Guid.TryParseExact(text, "N", out run) || run == Guid.Empty || run.ToString("N") != text) { return null; }
                    break;
                default: return null;
            }
        }
        return transport && lifetime && rangeApproved == range && run != Guid.Empty ? new(run, profile) : null;
    }

    internal static bool MatchesReviewedProfile(CpuPmCaptureArguments arguments, CpuPmReviewedLaunchBootstrap trusted) =>
        arguments.Profile == trusted.Profile;

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
