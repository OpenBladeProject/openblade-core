namespace OpenSynapse.Protocol;

internal sealed class WindowsRazerHidSessionOpener
{
    private static readonly HidOpenParameters OperationalOpenParameters = new(
        DesiredAccess: 0xC0000000u,
        ShareMode: 0x00000003u,
        CreationDisposition: 3u,
        FlagsAndAttributes: 0x40000000u,
        Inheritable: false);

    private static readonly HidHandleIdentity ExactIdentity = new(
        VendorId: 0x1532,
        ProductId: 0x02E0,
        UsagePage: 0x0001,
        Usage: 0x0002,
        FeatureReportByteLength: 91);

    private readonly IHostIdentitySource hostIdentity;
    private readonly IHardwareAccessGuard hardwareAccessGuard;
    private readonly IHidCandidateNative native;

    public WindowsRazerHidSessionOpener(
        IHostIdentitySource hostIdentity,
        IHardwareAccessGuard hardwareAccessGuard,
        IHidCandidateNative native)
    {
        ArgumentNullException.ThrowIfNull(hostIdentity);
        ArgumentNullException.ThrowIfNull(hardwareAccessGuard);
        ArgumentNullException.ThrowIfNull(native);
        this.hostIdentity = hostIdentity;
        this.hardwareAccessGuard = hardwareAccessGuard;
        this.native = native;
    }

    public HidSessionResult<OpenedHidHandle> Open()
    {
        HostIdentityReadResult identity;
        try
        {
            identity = hostIdentity.ReadExactIdentity();
        }
        catch (Exception)
        {
            return Failure(HidSessionFailureKind.IdentityBlocked);
        }

        if (identity.Evidence is null || identity.Failure is not null)
        {
            return Failure(HidSessionFailureKind.IdentityBlocked);
        }

        HardwareAccessDecision access;
        try
        {
            access = hardwareAccessGuard.Check();
        }
        catch (Exception)
        {
            return Failure(HidSessionFailureKind.ConflictDetectionFailed);
        }

        if (access.DetectionFailure is not null)
        {
            return Failure(HidSessionFailureKind.ConflictDetectionFailed);
        }

        if (access.Conflicts.Count != 0)
        {
            return Failure(HidSessionFailureKind.ConflictBlocked);
        }

        List<OpenedCandidateHandle> matches = [];
        try
        {
            foreach (string path in native.EnumerateDevicePaths())
            {
                OpenedCandidateHandle? candidate = native.TryOpen(path, OperationalOpenParameters);
                if (candidate is null)
                {
                    continue;
                }

                try
                {
                    HidHandleIdentity handleIdentity = native.ReadIdentity(candidate);
                    if (handleIdentity == ExactIdentity)
                    {
                        matches.Add(candidate);
                        candidate = null;
                    }
                }
                finally
                {
                    candidate?.Dispose();
                }
            }

            if (matches.Count != 1)
            {
                DisposeAll(matches);
                return Failure(HidSessionFailureKind.IdentityBlocked);
            }

            OpenedCandidateHandle selected = matches[0];
            try
            {
                OpenedHidHandle opened = native.BindSelected(selected);
                matches.Clear();
                return new(opened, null);
            }
            catch
            {
                selected.Dispose();
                matches.Clear();
                throw;
            }
        }
        catch (Exception)
        {
            DisposeAll(matches);
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }
    }

    private static HidSessionResult<OpenedHidHandle> Failure(HidSessionFailureKind failure) =>
        new(null, failure);

    private static void DisposeAll(List<OpenedCandidateHandle> handles)
    {
        foreach (OpenedCandidateHandle handle in handles)
        {
            handle.Dispose();
        }

        handles.Clear();
    }
}
