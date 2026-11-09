using System.Runtime.InteropServices;
using System.ServiceProcess;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.FanStop;

internal sealed record So6120AdmissionStamp(Guid RunId, uint ConsoleSession, TimeProvider Clock, long Timestamp);
internal sealed record So6120HidMetadata(string Path, HidHandleIdentity? Identity, int? OpenError = null);
internal sealed record So6120SessionMetadata(bool Interactive, int ProcessSession, uint ConsoleSession, byte AcLineStatus);
internal sealed record So6120DriverMetadata(string WindowsDirectory, string ConfiguredPath, string? FilePath,
    ServiceType ServiceType, ServiceControllerStatus ServiceStatus, long? Bytes, string? Sha256, string? DosTarget);
internal sealed record So6120SupervisedMetadata(So6120AdmissionStamp Stamp, long StartedTimestamp,
    Architecture ProcessArchitecture, HostIdentityReadResult Host, Version? ReportedEcVersion,
    IReadOnlyList<So6120HidMetadata> HidCandidates, So6120SessionMetadata Session, So6120DriverMetadata Driver,
    IReadOnlyList<string> DetectedControllers, ServiceControllerStatus? OpenBladeStatus, IReadOnlyList<int> OpenBladeWriterPids);

// Supplied by the outer verifier. Digests bind its evidence and observed scope;
// comparing them here neither authenticates that evidence nor grants consent.
internal sealed record So6120ChallengeScope(string HidPath, string BiosVersion, Version EcVersion, string DriverSha256,
    string PeripheralConfigurationSha256, string ControllerSoftwareSha256);
[Flags]
internal enum So6120AcceptedResiduals { None = 0, UnnamedFileHandles = 1, UncoveredAcpiPaths = 2, UnobservedWriters = 4 }
internal sealed record So6120CallerChallengeContext(So6120AdmissionStamp Stamp, Guid DecisionId, string EvidenceSha256,
    So6120ChallengeScope ReviewedScope, So6120ChallengeScope ObservedScope,
    So6120AcceptedResiduals ResidualsRequiringAcceptance, So6120AcceptedResiduals AcceptedResiduals);
internal enum So6120HandoffOrigin { AlreadyStopped, StoppedByThisRun, ServiceOwned }
internal sealed record So6120OriginalProcessExit(int Pid, long StartUtcTicks, long ObservedExitTimestamp);
internal sealed record So6120OpenBladeHandoff(So6120AdmissionStamp Stamp, So6120HandoffOrigin Origin,
    So6120OriginalProcessExit? OriginalExit);

internal enum So6120AdmissionAssessment
{
    InputsMatched, StaleMetadata, HostMismatch, EcVersionMismatch, HidIdentityUnavailable, SessionOrPowerMismatch,
    DriverMismatch, ControllerDetected, CallerChallengeMissingOrMismatched, OpenBladeHandoffMissingOrMismatched,
}

internal static class So6120SupervisedAdmission
{
    internal const string Manufacturer = "Razer";
    internal const string Product = "Blade 16 - RZ09-0581";
    internal const string Sku = "RZ09-05819EN4";
    internal const string Bios = "4.01";
    internal const string DriverName = "inpoutx64";
    internal const string DriverRelativePath = @"System32\Drivers\inpoutx64.sys";
    internal const string DriverTarget = @"\Device\inpoutx64";
    internal const long DriverBytes = 15008;
    internal const string DriverHash = "F8965FDCE668692C3785AFA3559159F9A18287BC0D53ABB21902895A8ECF221B";
    internal static readonly Version EcVersion = new(1, 4);
    private static readonly TimeSpan MetadataAge = TimeSpan.FromSeconds(5);
    // Applies to verification in the current run, independent of the age of
    // the source and caller-challenge artifacts referenced by that verification.
    private static readonly TimeSpan ContextAge = TimeSpan.FromMinutes(15);

    // One supervised run in the fixed AC/awake scope. A matching assessment is
    // only an input check. It grants no mailbox authority or writer exclusion.
    internal static So6120AdmissionAssessment Assess(TimeProvider clock, So6120SupervisedMetadata metadata,
        So6120CallerChallengeContext? challenge, So6120OpenBladeHandoff? handoff)
    {
        long now = clock.GetTimestamp();
        var stamp = metadata.Stamp;
        if (stamp.RunId == Guid.Empty || !ReferenceEquals(stamp.Clock, clock) || metadata.StartedTimestamp < 0 || stamp.Timestamp > now
            || metadata.StartedTimestamp > stamp.Timestamp || clock.GetElapsedTime(metadata.StartedTimestamp, now) > MetadataAge)
        { return So6120AdmissionAssessment.StaleMetadata; }
        if (metadata.ProcessArchitecture != Architecture.X64 || metadata.Host.Failure is not null
            || metadata.Host.Evidence is not { Manufacturer: Manufacturer, ProductName: Product, Sku: Sku, BiosVersion: Bios })
        { return So6120AdmissionAssessment.HostMismatch; }
        if (metadata.ReportedEcVersion != EcVersion) { return So6120AdmissionAssessment.EcVersionMismatch; }
        if (metadata.HidCandidates.Any(candidate => !IsTargetHidPath(candidate.Path)
            || candidate.Identity is not { } identity || !TargetBladeDevice.Matches(identity.VendorId, identity.ProductId)))
        { return So6120AdmissionAssessment.HidIdentityUnavailable; }
        So6120HidMetadata[] exact = metadata.HidCandidates.Where(candidate => candidate.Identity is { } identity
            && identity.UsagePage == TargetBladeDevice.UsagePage && identity.Usage == TargetBladeDevice.Usage
            && TargetBladeDevice.CanCarryProtocolReport(identity.FeatureReportByteLength)).ToArray();
        if (exact.Length != 1) { return So6120AdmissionAssessment.HidIdentityUnavailable; }
        var session = metadata.Session;
        if (!session.Interactive || session.ConsoleSession is 0 or uint.MaxValue
            || session.ProcessSession != session.ConsoleSession || session.ConsoleSession != stamp.ConsoleSession
            || session.AcLineStatus != 1)
        { return So6120AdmissionAssessment.SessionOrPowerMismatch; }
        if (!MatchesDriver(metadata.Driver)) { return So6120AdmissionAssessment.DriverMismatch; }
        if (metadata.DetectedControllers.Count != 0) { return So6120AdmissionAssessment.ControllerDetected; }

        bool MatchesContext(So6120AdmissionStamp context) => ReferenceEquals(context.Clock, clock)
            && context.RunId == stamp.RunId && context.ConsoleSession == stamp.ConsoleSession
            && context.Timestamp >= 0 && context.Timestamp <= stamp.Timestamp && clock.GetElapsedTime(context.Timestamp, now) <= ContextAge;

        if (challenge is null || !MatchesContext(challenge.Stamp) || challenge.DecisionId == Guid.Empty
            || !IsSha256(challenge.EvidenceSha256) || !MatchesChallengeScope(challenge, exact[0].Path))
        { return So6120AdmissionAssessment.CallerChallengeMissingOrMismatched; }
        if (handoff is null || !MatchesContext(handoff.Stamp) || handoff.Stamp.Timestamp > metadata.StartedTimestamp
            || metadata.OpenBladeStatus != ServiceControllerStatus.Stopped || metadata.OpenBladeWriterPids.Count != 0
            || !MatchesHandoff(handoff, clock, now))
        { return So6120AdmissionAssessment.OpenBladeHandoffMissingOrMismatched; }
        return So6120AdmissionAssessment.InputsMatched;
    }

    private static bool MatchesChallengeScope(So6120CallerChallengeContext challenge, string exactHidPath)
    {
        const So6120AcceptedResiduals known = So6120AcceptedResiduals.UnnamedFileHandles
            | So6120AcceptedResiduals.UncoveredAcpiPaths | So6120AcceptedResiduals.UnobservedWriters;
        var observed = challenge.ObservedScope;
        return challenge.ReviewedScope == observed && Equal(observed.HidPath, exactHidPath)
            && observed.BiosVersion == Bios && observed.EcVersion == EcVersion && Equal(observed.DriverSha256, DriverHash)
            && IsSha256(observed.PeripheralConfigurationSha256) && IsSha256(observed.ControllerSoftwareSha256)
            && (challenge.ResidualsRequiringAcceptance & So6120AcceptedResiduals.UnobservedWriters) != 0
            && (challenge.ResidualsRequiringAcceptance & challenge.AcceptedResiduals) == challenge.ResidualsRequiringAcceptance
            && (challenge.AcceptedResiduals & ~known) == 0;
    }

    private static bool MatchesHandoff(So6120OpenBladeHandoff handoff, TimeProvider clock, long now) =>
        handoff.Origin switch
        {
            So6120HandoffOrigin.AlreadyStopped => handoff.OriginalExit is null,
            So6120HandoffOrigin.StoppedByThisRun => handoff.OriginalExit is { Pid: > 0, StartUtcTicks: > 0, ObservedExitTimestamp: >= 0 } exit
                && exit.StartUtcTicks <= DateTime.MaxValue.Ticks && exit.ObservedExitTimestamp <= handoff.Stamp.Timestamp
                && clock.GetElapsedTime(exit.ObservedExitTimestamp, now) <= ContextAge,
            _ => false,
        };

    internal static bool IsTargetHidPath(string path)
    {
        const string prefix = @"\\?\hid#vid_1532&pid_02e0";
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && path[prefix.Length] is '&' or '#';
    }

    internal static bool IsConfiguredDriverPath(string path, string expected) => Equal(path, expected)
        || Equal(path, DriverRelativePath) || Equal(path, @"\SystemRoot\" + DriverRelativePath)
        || Equal(path, @"%SystemRoot%\" + DriverRelativePath);

    internal static bool MatchesDriver(So6120DriverMetadata driver)
    {
        string expected = Path.Combine(driver.WindowsDirectory, DriverRelativePath);
        return Path.IsPathFullyQualified(driver.WindowsDirectory) && Equal(driver.FilePath, expected)
            && IsConfiguredDriverPath(driver.ConfiguredPath, expected) && driver.ServiceType == ServiceType.KernelDriver
            && driver.ServiceStatus == ServiceControllerStatus.Running && driver.Bytes == DriverBytes
            && Equal(driver.Sha256, DriverHash) && Equal(driver.DosTarget, DriverTarget);
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Equal(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
