using System.ComponentModel;
using System.Globalization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// Developer-only attended validation. The PCI mailbox getter transport writes
// registers; installed-backend opt-in is not approval for this command.
internal static class Rz090528CpuLiveQueryProbe
{
    internal const string Name = "cpu-live-query-rz09-0528";
    internal const string PciApprovalFlag = "--approve-attended-pci-transport-writes";
    internal const string LoadedImageFlag = "--acknowledge-freshly-loaded-pinned-driver";
    internal const string Usage = Name + " " + PciApprovalFlag + " " + LoadedImageFlag;

    internal static int RunWindows(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken) => Run(args, output, error,
            Rz090528CpuFreshUsbIdentity.Read,
            (approval, freshUsb, token) => WindowsRz090528CpuQueryAdapter.QueryForAttendedValidation(
                approval, freshUsb, token), cancellationToken,
            identity => new Rz090528InstalledCpuBackendInspector().Inspect(true, identity));

    internal static int Run(string[] args, TextWriter output, TextWriter error,
        Func<DeviceIdentity?> readFreshUsbIdentity,
        Func<CpuQueryAttendedApproval, Func<DeviceIdentity?>, CancellationToken,
            Rz090528CpuLiveQueryResult> query,
        CancellationToken cancellationToken,
        Func<DeviceIdentity, CpuTuningBackendStatus>? inspectPassiveBackend = null)
    {
        // Strict parsing: no implicit permission, duplicate flags, or generic
        // driver/IOCTL/command parameters can reach the native entrypoint.
        if (args.Length != 2 || args.Distinct(StringComparer.Ordinal).Count() != 2
            || !args.Contains(PciApprovalFlag, StringComparer.Ordinal)
            || !args.Contains(LoadedImageFlag, StringComparer.Ordinal))
        {
            error.WriteLine($"Usage: {Usage}");
            error.WriteLine("Requires a present operator approving PCI transport writes and confirming "
                + "the exact driver was freshly loaded from the pinned backend files.");
            return 2;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.WriteLine("AttendedPciTransportWrites=ApprovedForOneBoundedQuery");
            output.WriteLine("LoadedKernelImageProvenance=OperatorAcknowledgedFreshLoadFromPinnedFiles");
            output.WriteLine("LoadedKernelImageMachineAttestation=Unavailable; filesystem hashes do not prove loaded image bytes");
            output.WriteLine("SavedConfiguration=NotQueried; saved configuration is separate from measured live state");
            output.WriteLine("LivePpt=Unknown; PM version and length do not establish a PPT baseline");
            var identity = readFreshUsbIdentity();
            if (identity is not { Model: "RZ09-0528", VendorId: 0x1532, ProductId: 0x02C6 })
            {
                output.WriteLine("Readiness=RefusedFreshExactUsbIdentity");
                return 3;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (inspectPassiveBackend is not null)
            {
                var backend = inspectPassiveBackend(identity);
                output.WriteLine($"PassiveInstalledBackend={backend.Availability}");
                if (backend.Availability != CpuTuningBackendAvailability.Available)
                {
                    output.WriteLine("Readiness=RefusedPassiveInstalledBackend; driver lifecycle changes require a separate attended proposal");
                    return 3;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            output.WriteLine("Readiness=FreshUsbPresent; adapter must freshly admit exact host/SKU/CPU, "
                + "running pinned backend and controller absence before device access");
            output.WriteLine("NativeQuery=StartingSingleAllowlistedCoAndPmGetter; no CO or PPT setter");
            output.Flush();
            var result = query(CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true),
                readFreshUsbIdentity, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            output.WriteLine($"NativeQueryStatus={result.Status}");
            if (result.Diagnostic is { } diagnostic)
            {
                output.WriteLine($"NativeQueryStage={diagnostic.Stage}");
                output.WriteLine($"NativeQueryFailure={diagnostic.Failure}");
                if (diagnostic.Operation is { } operation)
                {
                    output.WriteLine($"NativeQueryOperation={operation}");
                }
                if (diagnostic.NativeErrorCode is > 0)
                {
                    output.WriteLine($"NativeQueryErrorCode={diagnostic.NativeErrorCode}");
                }
            }
            if (result.Status != CpuLiveQueryStatus.Observed || result.Observation is null)
            {
                return 3;
            }
            var observation = result.Observation;
            output.WriteLine($"LiveObservedAtUtc={DateTimeOffset.UtcNow:O}");
            output.WriteLine($"LiveEnabledPhysicalCoreMask=0x{observation.EnabledCoreMask:X8}");
            output.WriteLine($"LivePmVersion=0x{observation.PmVersion:X8}");
            output.WriteLine($"LivePmTableLength=0x{observation.PmTableLength:X}");
            output.WriteLine("LiveCurveOptimizerPhysicalCoreOffsets=" + string.Join(",",
                observation.CoreOffsets.Select(core => string.Create(CultureInfo.InvariantCulture,
                    $"{core.PhysicalCoreId}:{core.Offset}"))));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            output.WriteLine("NativeQueryStatus=Cancelled; no completed live observation");
            return 130;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            error.WriteLine("NativeQueryStatus=Unavailable; passive identity or attended query failed");
            return 3;
        }
    }

    internal static bool IsExactUsbInstance(string instanceId) =>
        Rz090528CpuFreshUsbIdentity.IsExactUsbInstance(instanceId);
}
