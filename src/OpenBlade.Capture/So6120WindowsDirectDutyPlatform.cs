using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// Only the fixed CLI constructs this after verifying the launch inputs and
// obtaining attended baseline consent. This is no authority for mapped A1 writes.
internal sealed class So6120AttendedBaselineConsent(So6120JournalIdentity identity, uint consoleSession,
    TimeProvider clock, So6120CallerChallengeContext challenge,
    Func<So6120SupervisedMetadata, So6120ChallengeScope> observeCurrentScope)
{
    private int consumed;
    internal So6120JournalIdentity Identity { get; } = identity ?? throw new ArgumentNullException(nameof(identity));
    internal uint ConsoleSession { get; } = consoleSession;
    internal TimeProvider Clock { get; } = clock ?? throw new ArgumentNullException(nameof(clock));
    internal So6120CallerChallengeContext Challenge { get; } = challenge ?? throw new ArgumentNullException(nameof(challenge));
    internal Func<So6120SupervisedMetadata, So6120ChallengeScope> ObserveCurrentScope { get; } =
        observeCurrentScope ?? throw new ArgumentNullException(nameof(observeCurrentScope));

    internal void Consume()
    {
        if (Interlocked.Exchange(ref consumed, 1) != 0) { throw new InvalidOperationException("Baseline consent is already consumed."); }
        if (!So6120DutyWorkflow.ValidOwner(Identity.Owner) || !So6120SilentTrialPlan.SupportsProfile(Identity.CapturedProfile)
            || Identity.RequestedDuty1 is not null || Identity.RequestedDuty2 is not null
            || Identity.ConsentId == Guid.Empty || !IsDigest(Identity.ConsentSha256)
            || !IsDigest(Identity.DeviceEvidenceSha256) || !IsDigest(Identity.FirmwareEvidenceSha256)
            || string.IsNullOrWhiteSpace(Identity.BootIdentity) || ConsoleSession is 0 or uint.MaxValue
            || Challenge.ReviewedScope != Challenge.ObservedScope
            || Challenge.Stamp.RunId != Identity.Owner.OperationId || Challenge.Stamp.ConsoleSession != ConsoleSession
            || !ReferenceEquals(Challenge.Stamp.Clock, Clock))
        { throw new InvalidOperationException("Invalid attended SO6120 baseline consent binding."); }
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

internal enum So6120NativeCloseState { NotAttempted, Closed, Failed }
internal sealed record So6120NativeCloseResult(bool Succeeded, int NativeError);
internal sealed class So6120NativeCloseEvidence
{
    private So6120NativeCloseResult? result;
    public So6120NativeCloseState State => Volatile.Read(ref result) switch
    {
        null => So6120NativeCloseState.NotAttempted,
        { Succeeded: true } => So6120NativeCloseState.Closed,
        _ => So6120NativeCloseState.Failed,
    };
    public int? NativeError => Volatile.Read(ref result)?.NativeError;
    internal void Record(So6120NativeCloseResult value) => Volatile.Write(ref result, value);
}

internal sealed class So6120OwnedInpOutHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Func<nint, So6120NativeCloseResult>? closeForTest;
    public So6120OwnedInpOutHandle() : base(ownsHandle: true) { }
    internal So6120OwnedInpOutHandle(nint value, Func<nint, So6120NativeCloseResult> close) : this()
    {
        closeForTest = close;
        SetHandle(value);
    }

    internal So6120NativeCloseEvidence Evidence { get; } = new();
    protected override bool ReleaseHandle()
    {
        So6120NativeCloseResult result;
        if (closeForTest is not null) { result = closeForTest(handle); }
        else
        {
            bool succeeded = CloseHandle(handle);
            result = new(succeeded, succeeded ? 0 : Marshal.GetLastWin32Error());
        }
        Evidence.Record(result);
        return result.Succeeded;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint value);
}

internal sealed class So6120WindowsDutyAcquisition(So6120SessionBinding binding, So6120InpOutBytePort port,
    ISo6120SessionEvidenceSource evidenceSource, So6120NativeCloseEvidence closeEvidence, IDisposable ownership) : IDisposable
{
    private int disposed;
    internal So6120SessionBinding Binding { get; } = binding;
    internal So6120InpOutBytePort Port { get; } = port;
    internal ISo6120SessionEvidenceSource EvidenceSource { get; } = evidenceSource;
    internal So6120NativeCloseEvidence CloseEvidence { get; } = closeEvidence;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        try { Port.Dispose(); }
        finally
        {
            // Preserve live ownership when CloseHandle failed or was not
            // observed. The process supervisor retains this acquisition.
            if (CloseEvidence.State == So6120NativeCloseState.Closed) { ownership.Dispose(); }
        }
    }
}

internal sealed class So6120NativeAcquisitionException(Exception failure, So6120NativeCloseEvidence closeEvidence,
    IDisposable? unresolvedOwnership) : IOException("InpOut was opened but the platform could not attach it.", failure)
{
    internal So6120NativeCloseEvidence CloseEvidence { get; } = closeEvidence;
    // The supervisor retains the exception through its terminal report.
    internal bool OwnershipUnresolved => unresolvedOwnership is not null;
}

internal interface ISo6120WindowsDutyNative
{
    IDisposable ReserveRun();
    So6120MetadataRead Capture(Guid runId, TimeProvider clock);
    void VerifyUserOwnersStopped();
    IDisposable PinDriverFile(So6120DriverMetadata driver);
    IDisposable CreateAttemptLatch(Guid runId, DateTimeOffset startedUtc);
    So6120OwnedInpOutHandle OpenDevice();
}

internal static class So6120WindowsDirectDutyPlatform
{
    internal static So6120WindowsDutyAcquisition Acquire(So6120AttendedBaselineConsent consent, CancellationToken cancellationToken = default) =>
        Acquire(consent, new WindowsNative(), cancellationToken);

    internal static So6120WindowsDutyAcquisition Acquire(So6120AttendedBaselineConsent consent, ISo6120WindowsDutyNative native,
        CancellationToken cancellationToken = default)
    {
        consent.Consume();
        cancellationToken.ThrowIfCancellationRequested();
        IDisposable? run = native.ReserveRun(), pin = null, latch = null;
        So6120OwnedInpOutHandle? handle = null;
        try
        {
            So6120SupervisedMetadata stopped = ReadMetadata(consent.Identity.Owner.OperationId, consent.ConsoleSession,
                consent.Clock, native, cancellationToken);
            if (stopped.OpenBladeStatus != ServiceControllerStatus.Stopped || stopped.OpenBladeWriterPids.Count != 0)
            { throw new InvalidOperationException("OpenBlade must already have completed its attended shutdown."); }
            var handoff = new So6120OpenBladeHandoff(stopped.Stamp with { Timestamp = consent.Clock.GetTimestamp() },
                So6120HandoffOrigin.AlreadyStopped, null);
            var source = new EvidenceSource(consent, handoff, native);
            var binding = new So6120SessionBinding(consent.Identity, consent.ConsoleSession, consent.Challenge, handoff);
            So6120SupervisedMetadata first = source.Capture(cancellationToken).Metadata;
            cancellationToken.ThrowIfCancellationRequested();
            pin = native.PinDriverFile(first.Driver);
            cancellationToken.ThrowIfCancellationRequested();
            latch = native.CreateAttemptLatch(consent.Identity.Owner.OperationId, consent.Clock.GetUtcNow());
            // Recheck the stopped owners, pinned configuration and current
            // metadata after both disk operations and before the open.
            _ = source.Capture(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            native.VerifyUserOwnersStopped();
            cancellationToken.ThrowIfCancellationRequested();
            // The call is synchronous. Once entered, return any resulting
            // owned handle even if cancellation arrives during the open.
            handle = native.OpenDevice();
            if (handle.IsInvalid || handle.IsClosed) { throw new InvalidOperationException("InpOut did not return a usable handle."); }
            var port = So6120InpOutBytePort.AttachWindowsHandle(handle);
            var acquisition = new So6120WindowsDutyAcquisition(binding, port, source, handle.Evidence,
                new RunOwnership(pin, latch, run));
            handle = null;
            pin = null;
            latch = null;
            run = null;
            return acquisition;
        }
        catch (Exception failure) when (handle is { IsInvalid: false })
        {
            var close = handle.Evidence;
            try { handle.Dispose(); }
            catch (Exception closeFailure) { failure = new AggregateException(failure, closeFailure); }
            handle = null;
            RunOwnership? unresolved = null;
            if (close.State != So6120NativeCloseState.Closed)
            {
                unresolved = new(pin, latch, run);
                pin = null;
                latch = null;
                run = null;
            }
            throw new So6120NativeAcquisitionException(failure, close, unresolved);
        }
        finally
        {
            try { handle?.Dispose(); }
            finally { ReleaseOwnership(pin, latch, run); }
        }
    }

    private sealed class RunOwnership(IDisposable? pin, IDisposable? latch, IDisposable? run) : IDisposable
    {
        public void Dispose() => ReleaseOwnership(pin, latch, run);
    }

    private static void ReleaseOwnership(IDisposable? pin, IDisposable? latch, IDisposable? run)
    {
        try { pin?.Dispose(); }
        finally
        {
            try { latch?.Dispose(); }
            finally { run?.Dispose(); }
        }
    }

    internal static So6120SupervisedMetadata ReadMetadata(Guid runId, uint? consoleSession, TimeProvider clock,
        ISo6120WindowsDutyNative native, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = clock.GetTimestamp();
        native.VerifyUserOwnersStopped();
        cancellationToken.ThrowIfCancellationRequested();
        So6120MetadataRead read = native.Capture(runId, clock);
        if (read.Metadata is not { } metadata)
        { throw new InvalidOperationException($"SO6120 metadata unavailable ({read.FailedStage}, {read.Error}, {read.HostFailure?.HostIdentityFailure?.Reason})."); }
        if (metadata.Stamp.RunId != runId || (consoleSession.HasValue && metadata.Stamp.ConsoleSession != consoleSession.Value)
            || !ReferenceEquals(metadata.Stamp.Clock, clock) || metadata.StartedTimestamp < started)
        { throw new InvalidOperationException("SO6120 metadata does not bind to the current attended run."); }
        return metadata;
    }

    private sealed class EvidenceSource(So6120AttendedBaselineConsent consent, So6120OpenBladeHandoff handoff,
        ISo6120WindowsDutyNative native) : ISo6120SessionEvidenceSource
    {
        public So6120SessionEvidence Capture() => Capture(CancellationToken.None);

        internal So6120SessionEvidence Capture(CancellationToken cancellationToken)
        {
            So6120SupervisedMetadata metadata = ReadMetadata(consent.Identity.Owner.OperationId, consent.ConsoleSession,
                consent.Clock, native, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var challenge = consent.Challenge with { ObservedScope = consent.ObserveCurrentScope(metadata) };
            cancellationToken.ThrowIfCancellationRequested();
            So6120AdmissionAssessment assessment = So6120SupervisedAdmission.Assess(consent.Clock, metadata, challenge, handoff);
            if (assessment != So6120AdmissionAssessment.InputsMatched)
            { throw new InvalidOperationException($"SO6120 live admission refused ({assessment})."); }
            return new(consent.Identity, metadata, challenge, handoff);
        }
    }

    internal sealed class WindowsNative : ISo6120WindowsDutyNative
    {
        public IDisposable ReserveRun()
        {
            // A semaphore can be released by the disposal thread even when it
            // differs from the acquiring thread. It serializes this tool only.
            var semaphore = new Semaphore(1, 1, @"Global\OpenBlade.SO6120.SupervisedDirectDuty");
            try
            {
                if (!semaphore.WaitOne(0)) { throw new InvalidOperationException("An SO6120 runner is already active."); }
                return new RunReservation(semaphore);
            }
            catch { semaphore.Dispose(); throw; }
        }

        internal static string AttemptDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenBlade", "DeveloperValidation", "SO6120");

        public IDisposable CreateAttemptLatch(Guid runId, DateTimeOffset startedUtc) =>
            So6120WindowsDirectDutyPlatform.CreateAttemptLatch(AttemptDirectory, runId, startedUtc);

        public So6120MetadataRead Capture(Guid runId, TimeProvider clock) => So6120WindowsSupervisedMetadata.Capture(runId, clock);

        public void VerifyUserOwnersStopped()
        {
            foreach (string name in new[] { "OpenBlade.Tray", "OpenBlade.SessionAgent" })
            {
                Process[] processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Length != 0) { throw new InvalidOperationException($"{name} is still running."); }
                }
                finally { foreach (Process process in processes) { process.Dispose(); } }
            }
        }

        public IDisposable PinDriverFile(So6120DriverMetadata driver)
        {
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                So6120SupervisedAdmission.DriverRelativePath);
            if (!Path.IsPathFullyQualified(expected) || !string.Equals(driver.FilePath, expected, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0)
            { throw new InvalidOperationException("The reviewed InpOut disk path is unavailable."); }
            var file = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (file.Length != So6120SupervisedAdmission.DriverBytes
                    || Convert.ToHexString(SHA256.HashData(file)) != So6120SupervisedAdmission.DriverHash)
                { throw new InvalidOperationException("The locked InpOut disk file differs from the reviewed driver."); }
                return file;
            }
            catch { file.Dispose(); throw; }
        }

        public So6120OwnedInpOutHandle OpenDevice()
        {
            const uint shareReadWriteDelete = 7;
            const uint openExisting = 3;
            So6120OwnedInpOutHandle handle = CreateFileW(@"\\.\inpoutx64", 0, shareReadWriteDelete, 0, openExisting, 0, 0);
            int error = Marshal.GetLastWin32Error();
            if (!handle.IsInvalid) { return handle; }
            handle.Dispose();
            throw new Win32Exception(error, "The fixed zero-access InpOut open failed.");
        }

        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern So6120OwnedInpOutHandle CreateFileW(string name, uint access, uint share, nint security,
            uint disposition, uint flags, nint template);
    }

    private sealed class RunReservation(Semaphore semaphore) : IDisposable
    {
        public void Dispose()
        {
            try { semaphore.Release(); }
            finally { semaphore.Dispose(); }
        }
    }

    // One attempt across run roots. Even a partial marker survives failure.
    // This path check is not an authorization boundary against administrators.
    internal static FileStream CreateAttemptLatch(string directory, Guid runId, DateTimeOffset startedUtc)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.StartsWith("\\\\", StringComparison.Ordinal))
        { throw new IOException("SO6120 attempt directory must be a local absolute path."); }
        var root = new DirectoryInfo(Path.GetFullPath(directory));
        VerifyLatchAncestors(root);
        root.Create();
        VerifyLatchAncestors(root);
        var latch = new FileStream(Path.Combine(root.FullName, "attempt.json"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
        try
        {
            VerifyLatchAncestors(root);
            JsonSerializer.Serialize(latch, new { runId, startedUtc });
            latch.Flush(flushToDisk: true);
            return latch;
        }
        catch { latch.Dispose(); throw; }
    }

    private static void VerifyLatchAncestors(DirectoryInfo directory)
    {
        for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                { throw new IOException("SO6120 attempt directory may not traverse a reparse point."); }
            }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
        }
    }

    // These hashes cover present devnode IDs and configured service records.
    // They do not attest loaded images, activity, or universal writer exclusion.
    internal static So6120ChallengeScope ObserveCurrentScope(So6120SupervisedMetadata metadata)
    {
        var candidate = metadata.HidCandidates.Single(item => item.Identity is { } identity
            && TargetBladeDevice.Matches(identity.VendorId, identity.ProductId)
            && identity.UsagePage == TargetBladeDevice.UsagePage && identity.Usage == TargetBladeDevice.Usage
            && TargetBladeDevice.CanCarryProtocolReport(identity.FeatureReportByteLength));
        return new(candidate.Path, metadata.Host.Evidence?.BiosVersion ?? throw new InvalidOperationException("BIOS version missing."),
            metadata.ReportedEcVersion ?? throw new InvalidOperationException("Reported EC version missing."),
            metadata.Driver.Sha256 ?? throw new InvalidOperationException("Configured driver hash missing."),
            HashDeviceIds(ReadPresentDeviceIds()), HashServiceConfiguration());
    }

    internal static string HashDeviceIds(IEnumerable<string> ids) => HashInventory("so6120-present-devnode-ids-v1", writer =>
    {
        string[] sorted = ids.Select(id => id.ToUpperInvariant()).Order(StringComparer.Ordinal).ToArray();
        if (sorted.Length == 0 || sorted.Any(string.IsNullOrWhiteSpace)
            || sorted.Distinct(StringComparer.Ordinal).Count() != sorted.Length)
        { throw new InvalidDataException("Present devnode inventory is empty or ambiguous."); }
        foreach (string id in sorted) { writer.Write(id); }
    });

    private static string[] ReadPresentDeviceIds()
    {
        const uint present = 0x100;
        const uint maxCharacters = 256 * 1024;
        uint result = CM_Get_Device_ID_List_SizeW(out uint size, null, present);
        if (result != 0 || size is 0 or > maxCharacters) { throw new IOException($"Present devnode list size failed ({result})."); }
        char[] buffer = new char[size];
        result = CM_Get_Device_ID_ListW(null, buffer, size, present);
        if (result != 0) { throw new IOException($"Present devnode list failed ({result})."); }
        ReadOnlySpan<char> terminator = ['\0', '\0'];
        int end = buffer.AsSpan().IndexOf(terminator);
        if (end <= 0 || buffer.AsSpan(end).IndexOfAnyExcept('\0') >= 0) { throw new IOException("Present devnode list is incomplete."); }
        return new string(buffer, 0, end).Split('\0');
    }

    private static string HashServiceConfiguration() => HashInventory("so6120-driver-razer-openblade-config-v1", writer =>
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var services = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services") ?? throw new IOException("Service registry unavailable.");
        string[] names = services.GetSubKeyNames();
        if (names.Length is 0 or > 4096) { throw new IOException("Service registry inventory exceeds its bounds."); }
        foreach (string name in names.Order(StringComparer.OrdinalIgnoreCase))
        {
            using var service = services.OpenSubKey(name) ?? throw new IOException("Service registry inventory changed.");
            int? type = service.GetValue("Type") as int?;
            bool named = name.StartsWith("Razer", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("OpenBlade", StringComparison.OrdinalIgnoreCase)
                || RazerControllerServiceAliases.Contains(name)
                || name.Equals("GameManagerService3", StringComparison.OrdinalIgnoreCase);
            if (!named && (type.GetValueOrDefault() & 0xB) == 0) { continue; }
            object? image = service.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (type is null or <= 0 || service.GetValue("Start") is not int start || start is < 0 or > 4
                || image is not (null or string { Length: <= 4096 }))
            { throw new IOException("An in-scope service configuration is incomplete or malformed."); }
            writer.Write(name.ToUpperInvariant());
            writer.Write(type.Value);
            writer.Write(start);
            writer.Write(image is not null);
            if (image is string path) { writer.Write(path); }
            if (writer.BaseStream.Position > 1024 * 1024) { throw new IOException("Service configuration exceeds its byte bound."); }
        }
    });

    private static string HashInventory(string domain, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(domain);
        write(writer);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint size, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint size, uint flags);
}
