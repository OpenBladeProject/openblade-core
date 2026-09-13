namespace OpenBlade.Contracts;

public static class SessionPowerTransition
{
    // Windows gives applications a short shared suspend-preparation window.
    // Both processes also check QPC age because cancellation timers can pause in sleep.
    public const int MaximumAgeMilliseconds = 1500;
}

public sealed record ReportSessionPowerTransitionRequest(
    int Version,
    Guid RequestId,
    Guid InstanceId,
    long InstanceStartedQpc,
    long Sequence,
    long TimestampQpc,
    bool Suspending) : ServiceRequest(Version, RequestId);
