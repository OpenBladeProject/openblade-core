namespace OpenBlade.Protocol;

// Fixed developer validation metadata. None of these records authorizes a CPU
// command: the independently admitted native proof must still pass in full.
internal sealed record CpuVoltageJobDeadline(Guid Session, Guid Job, int HostPid, long HostCreated,
    string HostImage, long StartedTick, long ExpiresTick, string Source, string Manifest, string ServiceVersion)
{
    internal const string Root = @"C:\ProgramData\OpenBladeCpuVoltageSessions";
    internal const long BudgetMilliseconds = 600_000;
    internal string FileName => "job-deadline-" + Job.ToString("N") + ".json";
    internal bool IsCurrent(long now) => Session != Guid.Empty && Job != Guid.Empty && HostPid > 0 && HostCreated > 0
        && StartedTick >= 0 && ExpiresTick > StartedTick && ExpiresTick - StartedTick == BudgetMilliseconds
        && now >= StartedTick && now < ExpiresTick;
}

internal sealed record CpuVoltageRunDeadline(CpuVoltageJobDeadline Job, Guid Run, int OwnerPid, long OwnerCreated,
    string OwnerImage, int ServicePid, long ServiceCreated, string DriverOwnerTag)
{
    internal string FileName => "run-deadline-" + Run.ToString("N") + ".json";
    internal bool Matches(string tag, int servicePid, long serviceCreated, string version)
    {
        string[] parts = tag.Split(':');
        return Job is not null && Run != Guid.Empty && OwnerPid > 0 && OwnerCreated > 0
            && OwnerImage == Job.HostImage && ServicePid == servicePid && ServiceCreated == serviceCreated
            && Job.ServiceVersion == version && DriverOwnerTag == tag && parts.Length == 5 && parts[0] == "1"
            && parts[1] == Run.ToString("N") && Guid.TryParseExact(parts[2], "N", out var lifetime) && lifetime != Guid.Empty
            && parts[3] == Job.Manifest && parts[3].Length == 64 && parts[3].All(Uri.IsHexDigit)
            && parts[4].Length == 64 && parts[4].All(Uri.IsHexDigit);
    }
}

// Lifetime belongs to the actual native worker, never its timed-out awaiter.
internal sealed class CpuLiveCurveWriteDeadline : IDisposable
{
    private readonly Func<long> ticks;
    private readonly Func<bool> identityCurrent;
    private readonly IDisposable? resources;
    private readonly CancellationTokenSource cancellation;
    private readonly long started, expires;
    private int revoked, disposed;
    internal CpuLiveCurveWriteDeadline(long started, long expires, Func<long> ticks, Func<bool> identityCurrent,
        CancellationToken token, IDisposable? resources = null)
    {
        this.started = started; this.expires = expires; this.ticks = ticks;
        this.identityCurrent = identityCurrent; this.resources = resources;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        long remaining = expires - ticks();
        if (remaining is <= 0 or > CpuVoltageJobDeadline.BudgetMilliseconds) { cancellation.Cancel(); }
        else { cancellation.CancelAfter(TimeSpan.FromMilliseconds(remaining)); }
    }
    internal CancellationToken Token => cancellation.Token;
    internal bool IsCurrent()
    {
        if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref revoked) != 0) { return false; }
        bool valid;
        try
        {
            long now = ticks();
            valid = !cancellation.IsCancellationRequested && now >= started && now < expires && identityCurrent();
            // Identity checks can take time. Recheck after them too.
            now = ticks(); valid &= now >= started && now < expires && !cancellation.IsCancellationRequested;
        }
        catch { valid = false; }
        if (!valid) { Interlocked.Exchange(ref revoked, 1); cancellation.Cancel(); }
        return valid;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        try { cancellation.Cancel(); } finally { try { resources?.Dispose(); } finally { cancellation.Dispose(); } }
    }
}
