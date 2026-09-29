using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal interface ICpuTuningObservationSource
{
    DeviceIdentity? ReadIdentity();
    SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity);
    CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend);
}

internal sealed class CpuTuningController(ICpuTuningObservationSource source,
    ICpuTuningOwnershipJournalStore? journalStore = null,
    Func<Func<DeviceIdentity, CpuSavedTuningCommand, bool>, ICpuSavedTuningBackend>? backendFactory = null)
{
    internal bool HasSavedBackend => journalStore is not null;

    internal async Task<CpuTuningStatus> ObserveAsync(bool useInstalledBackend, CancellationToken token)
    {
        var observed = await CpuFirmwareReadScheduler.RunAsync(() => Observe(useInstalledBackend, token),
            TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        if (journalStore is null || observed.Identity is null || observed.Saved is null) { return observed.Status; }
        CpuSavedTuningTransactionResult ownership = await CreateTransaction(static (_, _) => false)
            .ObserveOwnershipAsync(observed.Identity, observed.Saved, token).ConfigureAwait(false);
        return observed.Status with
        {
            PendingBoot = ownership.PendingBoot,
            RestorePending = ownership.RestorePending,
            FirmwareRecovery = ownership.Recovery,
        };
    }

    private (CpuTuningStatus Status, DeviceIdentity? Identity, SavedCpuFirmwareReadResult? Saved)
        Observe(bool useInstalledBackend, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var unavailable = new CpuTuningBackendStatus(DeviceFeatureAccess.ObservedOnly,
            CpuTuningBackendAvailability.Unavailable);
        DeviceIdentity? identity = source.ReadIdentity();
        token.ThrowIfCancellationRequested();
        if (identity is null)
        {
            return (new(null, null, unavailable, unavailable, CpuTuningPendingBoot.Unknown, false), null, null);
        }

        SavedCpuFirmwareReadResult saved = source.ReadSaved(identity);
        token.ThrowIfCancellationRequested();
        CpuSavedConfiguration? decoded = saved.Current is { } current
            ? new(current.PowerLimitMilliwatts, current.PolicyMode, current.PowerLimitMode,
                DecodeCurve(current))
            : null;
        CpuTuningBackendStatus firmware = DescribeFirmwareBackend(saved);
        CpuTuningBackendStatus live = source.InspectInstalled(identity, useInstalledBackend) with
        {
            // Passive dependency discovery is not native query/write admission.
            Access = DeviceFeatureAccess.ObservedOnly,
        };
        token.ThrowIfCancellationRequested();
        // Saved observations cannot establish active settings, ownership, a
        // pending OpenBlade write, or successful boot activation.
        return (new(decoded, null, firmware, live, CpuTuningPendingBoot.Unknown, false), identity, saved);
    }

    internal async Task<ControlOperationResult> ApplySavedAsync(CpuTuningSettings settings,
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission, CancellationToken token) =>
        await MutateSavedAsync(settings, freshRuntimeAdmission, restoring: false, token).ConfigureAwait(false);

    internal async Task<ControlOperationResult> RestoreSavedAsync(
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission, CancellationToken token) =>
        await MutateSavedAsync(null, freshRuntimeAdmission, restoring: true, token).ConfigureAwait(false);

    private CpuSavedTuningTransaction CreateTransaction(
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission) => new(
            backendFactory?.Invoke(freshRuntimeAdmission) ?? new WindowsSavedCpuTuningBackend(source,
                (identity, command) => WindowsSavedCpuTuningBackend.IsProductionCommandAdmitted(command)
                    && freshRuntimeAdmission(identity, command)),
            journalStore ?? throw new InvalidOperationException("CPU ownership storage is unavailable."));

    private async Task<ControlOperationResult> MutateSavedAsync(CpuTuningSettings? settings,
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> freshRuntimeAdmission, bool restoring,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (journalStore is null)
        { return new(ControlOperationStatus.Blocked, "CPU ownership storage is unavailable."); }
        DeviceIdentity? identity = source.ReadIdentity();
        if (!CpuSavedTuningPlan.IsExactIdentity(identity))
        { return new(ControlOperationStatus.Blocked, "The saved CPU firmware identity is not admitted."); }
        CpuSavedTuningTransaction transaction = CreateTransaction(freshRuntimeAdmission);
        CpuSavedTuningTransactionResult result = restoring
            ? await transaction.RestoreAsync(identity!, token).ConfigureAwait(false)
            : await transaction.ApplyAsync(identity!, settings!, token).ConfigureAwait(false);
        // Reload the durable journal, using only this operation's readback.
        // Missing readback must not fall back to an older UI observation.
        CpuSavedTuningTransactionResult ownership = await transaction.ObserveOwnershipAsync(identity!,
            result.Observation ?? new(SavedCpuFirmwareReadStatus.Unavailable, null, null, 0, 0),
            token).ConfigureAwait(false);
        var unavailable = new CpuTuningBackendStatus(DeviceFeatureAccess.ObservedOnly,
            CpuTuningBackendAvailability.Unavailable);
        CpuSavedConfiguration? saved = result.Observation?.Current is { } current
            ? new(current.PowerLimitMilliwatts, current.PolicyMode, current.PowerLimitMode, DecodeCurve(current)) : null;
        var status = new CpuTuningStatus(saved, null,
            DescribeFirmwareBackend(result.Observation),
            unavailable, ownership.PendingBoot, ownership.RestorePending)
        { FirmwareRecovery = ownership.Recovery };
        ControlOperationStatus operation = result.Outcome switch
        {
            CpuSavedTuningOutcome.Verified or CpuSavedTuningOutcome.Unchanged => ControlOperationStatus.Stored,
            CpuSavedTuningOutcome.Uncertain => ControlOperationStatus.Failed,
            _ => ControlOperationStatus.Blocked,
        };
        string message = result.Outcome switch
        {
            CpuSavedTuningOutcome.Verified => restoring
                ? "The original saved CPU configuration was restored and verified. Live settings remain unconfirmed."
                : "CPU settings were saved and verified for the next boot. Live settings remain unconfirmed.",
            CpuSavedTuningOutcome.Unchanged => result.RestorePending
                ? "Saved CPU settings already match; the original restoration baseline was retained."
                : "Saved CPU settings already match; no ownership was established.",
            CpuSavedTuningOutcome.Uncertain => "CPU submission or readback is uncertain. Further CPU writes are blocked.",
            _ => result.Reason switch
            {
                CpuSavedTuningBlockReason.InvalidJournal => "CPU recovery data is invalid. Further CPU writes are blocked.",
                CpuSavedTuningBlockReason.PendingSubmission => "A prior CPU submission remains unresolved. Further writes are blocked.",
                CpuSavedTuningBlockReason.UnrestorableBaseline => "The saved CPU baseline has no admitted exact restoration route.",
                CpuSavedTuningBlockReason.AdmissionDenied => "CPU commands require attended service write and restoration admission.",
                CpuSavedTuningBlockReason.JournalUnavailable => "CPU recovery data could not be saved. No further command was submitted.",
                _ => "The saved CPU state or write authority changed; further writes are blocked.",
            },
        };
        return new(operation, message) { CpuTuning = status };
    }

    private static CpuTuningBackendStatus DescribeFirmwareBackend(SavedCpuFirmwareReadResult? saved) =>
        new(DeviceFeatureAccess.ObservedOnly, saved?.Status switch
        {
            SavedCpuFirmwareReadStatus.Observed => CpuTuningBackendAvailability.Available,
            SavedCpuFirmwareReadStatus.InvalidData or SavedCpuFirmwareReadStatus.MirrorMismatch => CpuTuningBackendAvailability.Failed,
            _ => CpuTuningBackendAvailability.Unavailable,
        });

    private static CpuCurveOptimizerState? DecodeCurve(Rz090528SavedCpuConfiguration saved)
    {
        if (saved.AllCoreCurveOptimizer is { } allCore)
        {
            return new(CpuCurveOptimizerMode.AllCore, allCore, []);
        }

        // Per-core storage is retained losslessly by the private reader. A
        // public active-core summary additionally needs validated topology.
        return null;
    }
}

internal sealed class WindowsCpuTuningObservationSource : ICpuTuningObservationSource
{
    public DeviceIdentity? ReadIdentity()
    {
        var registry = new WindowsRegistryValueSource();
        HostIdentityReadResult host = new WindowsHostIdentitySource(registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            WindowsHostIdentitySource.Rz090528Identity, compatibilityPolicy: null).ReadExactIdentity();
        return host is { Failure: null, Evidence: { } evidence }
            ? new("RZ09-0528", 0x1532, 0x02C6, evidence.BiosVersion)
            : null;
    }

    public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity)
    {
        if (identity.BiosVersion != "2.02")
        {
            return new(SavedCpuFirmwareReadStatus.UnsupportedIdentity, null, null, 0, 0);
        }

        try
        {
            // Duplicate the caller's token: enable the read prerequisite only
            // on a temporary impersonation token, never on the service token.
            using WindowsIdentity current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
            if (!DuplicateTokenEx(current.AccessToken, 0x2C, 0, 2, 2, out SafeAccessTokenHandle temporary))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            using (temporary)
            {
                if (!LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out Luid privilege))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var privileges = new TokenPrivileges { Count = 1, Id = privilege, Attributes = 2 };
                if (!AdjustTokenPrivileges(temporary, false, ref privileges, 0, 0, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                int error = Marshal.GetLastWin32Error();
                if (error != 0) { throw new Win32Exception(error); }
                return WindowsIdentity.RunImpersonated(temporary,
                    () => new Rz090528SavedCpuFirmwareReader().Read(identity));
            }
        }
        catch (Win32Exception exception)
        {
            return new(SavedCpuFirmwareReadStatus.Unavailable, null, null,
                exception.NativeErrorCode, exception.NativeErrorCode);
        }
    }

    public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend) =>
        new Rz090528InstalledCpuBackendInspector().Inspect(useInstalledBackend, identity);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Id; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access,
        nint attributes, int level, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid id);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disable, ref TokenPrivileges privileges,
        uint length, nint previous, nint returned);
}
