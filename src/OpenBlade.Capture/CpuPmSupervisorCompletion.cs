namespace OpenBlade.Capture;

// Shared by the concrete supervisor and injected tests. A returned authority task
// is not a terminal-child observation. The SAME pending drain blocks this owner
// thread; there is no timeout replacement and cleanup cannot run concurrently.
internal static class CpuPmSupervisorCompletion
{
    internal static bool Finish(ICpuPmRetainedChild child, Func<bool> uncertain, Action validateBootstrap,
        Action revoke, Action drainSameListener, Func<CpuPmDriverCleanup> cleanup, Action delay)
    {
        var identity = child.Identity;
        while (true)
        {
            validateBootstrap();
            if (uncertain() || child.ReadFreshIdentity() != identity) { revoke(); return false; }
            CpuPmChildState state = child.ReadState();
            if (state == CpuPmChildState.Unknown) { revoke(); return false; }
            if (state == CpuPmChildState.Terminal) { break; }
            delay();
        }
        revoke(); drainSameListener();
        for (int polls = 0; polls < 64; polls++)
        {
            validateBootstrap();
            if (uncertain() || child.ReadFreshIdentity() != identity || child.ReadState() != CpuPmChildState.Terminal) { return false; }
            var result = cleanup();
            if (result == CpuPmDriverCleanup.Complete) { return true; }
            if (result != CpuPmDriverCleanup.PendingStopped) { return false; }
            delay();
        }
        return false;
    }
}
