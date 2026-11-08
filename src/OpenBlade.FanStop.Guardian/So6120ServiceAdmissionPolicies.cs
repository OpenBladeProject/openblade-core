using System.Runtime.InteropServices;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.FanStop.Guardian;

// The service's source-reviewed model binding is independent of the attended
// developer trial. Both use the same bounded transaction and recovery code.
internal static class So6120ServiceModelAdmission
{
    internal static FanStopModelContract SelectFirmware(FanStopModelContract family, So6120SupervisedMetadata metadata)
    {
        if (metadata.ProcessArchitecture != Architecture.X64 || !family.MatchesHost(metadata.Host))
        { throw new InvalidDataException("The firmware candidate does not match the exact host."); }
        return FanStopModelContract.ForFirmware(family.Model, metadata.ReportedEcVersion)
            ?? throw new InvalidDataException("No reviewed Fan Stop firmware candidate matches the reported EC version.");
    }

    internal static So6120JournalIdentity CreateJournal(FanStopModelContract contract, Guid operation,
        int servicePid, long serviceStartUtcTicks) =>
        new(new(operation, contract.DeviceIdentity, contract.FirmwareIdentity, Guid.NewGuid()),
            PerformancePreset.Balanced, 0, 0, contract.ContractSha256, contract.FirmwareSha256,
            operation, contract.ContractSha256, $"service:{servicePid}:{serviceStartUtcTicks}");

    internal static bool MatchesJournal(FanStopModelContract contract, So6120JournalIdentity identity,
        int servicePid, long serviceStartUtcTicks) =>
        // Only immutable catalog instances can grant model authority. A
        // journal is evidence for this owner, never a way to choose a model.
        FanStopModelContract.All.Contains(contract)
        && identity.Owner.OperationId != Guid.Empty && identity.Owner.ControllerSession != Guid.Empty
        && identity.Owner.DeviceIdentity == contract.DeviceIdentity
        && identity.Owner.FirmwareEvidence == contract.FirmwareIdentity
        && identity.DeviceEvidenceSha256 == contract.ContractSha256
        && identity.FirmwareEvidenceSha256 == contract.FirmwareSha256
        && identity.ConsentId == identity.Owner.OperationId && identity.ConsentSha256 == contract.ContractSha256
        && identity.BootIdentity == $"service:{servicePid}:{serviceStartUtcTicks}"
        && identity.CapturedProfile == PerformancePreset.Balanced
        && identity.RequestedDuty1 == 0 && identity.RequestedDuty2 == 0;

    internal static So6120AdmissionAssessment Assess(FanStopModelContract contract, So6120JournalIdentity identity,
        int servicePid, long serviceStartUtcTicks, So6120SupervisedMetadata metadata)
    {
        if (!MatchesJournal(contract, identity, servicePid, serviceStartUtcTicks))
        { return So6120AdmissionAssessment.OpenBladeHandoffMissingOrMismatched; }
        if (metadata.ProcessArchitecture != Architecture.X64 || !contract.MatchesHost(metadata.Host))
        { return So6120AdmissionAssessment.HostMismatch; }
        if (metadata.ReportedEcVersion != contract.EcVersion)
        { return So6120AdmissionAssessment.EcVersionMismatch; }
        if (metadata.HidCandidates.Count == 0 || metadata.HidCandidates.Any(candidate =>
                !contract.IsTargetHidPath(candidate.Path) || candidate.Identity is not { } hid
                || hid.VendorId != contract.VendorId || hid.ProductId != contract.ProductId)
            || metadata.HidCandidates.Count(candidate => candidate.Identity is { } hid && contract.MatchesHid(hid)) != 1)
        { return So6120AdmissionAssessment.HidIdentityUnavailable; }
        return So6120AdmissionAssessment.InputsMatched;
    }
}

internal static class So6120ServiceInitialAdmissionDiagnostics
{
    internal static string Refusal(string stage, string detail) =>
        $"Initial command-free Stop admission refused (stage={stage}; {detail}).";

    internal static string ThermalRefusal(FanStopSettings settings, FanTemperatureReadings reading,
        TimeSpan age, bool canceled, FanStopThermalDecision? decision = null) => Refusal("ThermalEntry",
            FormattableString.Invariant($"{(decision is null ? "" : decision.Diagnostic + "; ")}mode={settings.EndMode}; cpuC={Value(reading.CpuCelsius)}; gpuC={Value(reading.GpuCelsius)}; gpuInactive={reading.GpuInactive}; gpuPoweredDown={reading.GpuPoweredDown}; sampleAgeMs={Math.Clamp(age.TotalMilliseconds, -1, 60000):F1}; sampleCanceled={canceled}; cpuEntryLimitC={(settings.EndMode == FanStopEndMode.Auto ? 55 : settings.CpuImmediateCutoffCelsius)}; gpuEntryLimitC={(settings.EndMode == FanStopEndMode.Custom ? Value(settings.GpuImmediateCutoffCelsius) : "inactive-required")}; maxSampleAgeMs=2000"));

    private static string Value(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable";
}

// Stop confirmation means observed stopped tachometers, not just a zero-PWM
// command. Keep that evidence sticky and preserve the entry/coast failure even
// when the same owner successfully restores Auto afterward.
internal sealed class So6120ServiceStopProgress
{
    internal bool StopConfirmed { get; private set; }
    internal string? IncompleteReason { get; private set; }
    internal string? ThermalReason { get; private set; }
    internal string? CompletionReason => IncompleteReason ?? ThermalReason;
    internal string ReturningReason => CompletionReason ?? "Auto requested or continuation admission ended.";

    internal void RecordThermalExit(FanStopEndReason endReason, FanStopThermalDecision? decision)
    {
        if (endReason == FanStopEndReason.ThermalThreshold && decision is not null)
        { ThermalReason ??= $"Stop ended (endReason={endReason}; {decision.Diagnostic})."; }
    }

    internal void RecordEntryFailure(So6120DutyFailure failure, string? admissionDetail)
    {
        string detail = string.IsNullOrWhiteSpace(admissionDetail) ? string.Empty : $" {admissionDetail}";
        string reason = $"Stop entry did not complete ({failure}).{detail}";
        IncompleteReason ??= reason[..Math.Min(reason.Length, 900)];
    }

    internal void RecordCoast(So6120StopCoastResult coast, FanStopEndReason endReason,
        FanStopThermalDecision? decision = null)
    {
        RecordThermalExit(endReason, decision);
        if (coast == So6120StopCoastResult.Stopped) { StopConfirmed = true; }
        else if (!StopConfirmed)
        {
            string thermalDetail = endReason == FanStopEndReason.ThermalThreshold && decision is not null
                ? "; " + decision.Diagnostic : "";
            IncompleteReason ??= $"Stopped fans were not confirmed during coast-down ({coast}; endReason={endReason}{thermalDetail}).";
        }
    }
}

internal sealed class So6120ServicePowerReadException(Exception inner)
    : IOException("The adapter read did not establish a resolved native operation.", inner)
{
    internal bool NativeOperationUnresolved => true;
}

internal sealed record So6120ServicePowerReadResult(PowerSource Source, string? AdmissionRefusal = null)
{
    internal bool QueryNotAttempted => AdmissionRefusal is not null;
}

// Runs only on the native owner's serialized worker, between resolved commands.
// It never retries a power query or treats an unresolved query as cleanup authority.
internal sealed class So6120ServiceOwnedPowerMonitor(PowerSource initial,
    Func<PowerSource> readPower, Func<bool> continuation, Func<bool> verifyOwnedStopped)
{
    private bool ended;
    internal PowerSource Current { get; private set; } = initial;
    internal FanStopEndReason EndReason { get; private set; }

    // The caller must perform its existing mandatory ownership observation
    // immediately after a successful coast boundary, including a lower source.
    internal bool BeforeCoastObservation() => Refresh(verificationFollows: true);

    internal bool BeforeHeldStop() => Refresh(verificationFollows: false);

    private bool Refresh(bool verificationFollows)
    {
        if (ended) { return false; }
        try
        {
            bool admitted = RefreshCore(verificationFollows);
            ended = !admitted;
            return admitted;
        }
        catch { ended = true; throw; }
    }

    private bool RefreshCore(bool verificationFollows)
    {
        if (!continuation()) { return false; }
        PowerSource next = readPower();
        if (!continuation()) { return false; }
        FanStopPowerTransition transition = FanStopPowerTransitionPolicy.Evaluate(Current, next);
        if (transition == FanStopPowerTransition.Automatic)
        { EndReason = FanStopEndReason.PowerChanged; return false; }
        Current = next;
        if (transition == FanStopPowerTransition.VerifyOwnedStop && !verificationFollows)
        {
            if (!verifyOwnedStopped() || !continuation()) { return false; }
            // The verification itself can take seconds. Recheck power before
            // publishing held Stop, without recursively adding verification.
            PowerSource after = readPower();
            if (!continuation()) { return false; }
            if (FanStopPowerTransitionPolicy.Evaluate(Current, after) != FanStopPowerTransition.Continue)
            { EndReason = FanStopEndReason.PowerChanged; return false; }
            Current = after;
        }
        return true;
    }
}

internal static class So6120ServicePowerReadPolicy
{
    internal static async Task<So6120ServicePowerReadResult> ReadAdmittedAsync(
        Action requireAdmission, Func<Task<PowerSource>> query)
    {
        try { requireAdmission(); }
        catch (Exception exception)
        {
            // No native query callback has been entered. End Stop admission,
            // then let the same owner attempt its normally guarded Auto path.
            // This is no authority to bypass a controller or ownership refusal.
            return new(PowerSource.ExternalUnknown,
                $"Adapter-query admission refused before native submission ({exception.GetType().Name}).");
        }
        // Once entered, query failures retain their existing ambiguity meaning.
        // Never turn them into a clean refusal or invoke the callback again.
        return new(await query().ConfigureAwait(false));
    }

    internal static PowerSource Classify(AdapterPowerState state) => state switch
    {
        AdapterPowerState.Battery => PowerSource.Battery,
        AdapterPowerState.FullPower280W or AdapterPowerState.FullPower330W
            or AdapterPowerState.SufficientExternal => PowerSource.PluggedIn,
        AdapterPowerState.UsbCPower45W or AdapterPowerState.UsbCPower60W or AdapterPowerState.UsbCPower65W
            or AdapterPowerState.UsbCPower80W or AdapterPowerState.UsbCPower90W or AdapterPowerState.UsbCPower100W
            or AdapterPowerState.LimitedExternal => PowerSource.UsbC,
        _ => PowerSource.ExternalUnknown,
    };

    internal static async Task<PowerSource> ReadAsync(Func<CancellationToken, Task<AdapterPowerState>> query,
        CancellationToken token)
    {
        try
        {
            // Unknown from this typed, completed reply ends Stop admission. It
            // does not invent ambiguity in the separate, still-owned EC path.
            return Classify(await query(token).ConfigureAwait(false));
        }
        catch (Exception exception) { throw new So6120ServicePowerReadException(exception); }
    }
}
