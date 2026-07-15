namespace OpenBlade.Protocol;

public static class BatteryValidationFactory
{
    private static readonly TimeSpan DefaultExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultCleanupDrainTimeout = TimeSpan.FromSeconds(5);

    public static IBatteryValidationRunFactory CreateWindows()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindows(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            native,
            new OverlappedFeatureOperation(native),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }

    public static BatteryValidationProbeResult ProbeWindows()
    {
        var registry = new WindowsRegistryValueSource();
        return ProbeWindows(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            new WindowsHidNative());
    }

    internal static BatteryValidationProbeResult ProbeWindows(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(processSource);
        ArgumentNullException.ThrowIfNull(serviceSource);
        ArgumentNullException.ThrowIfNull(native);

        var guard = new WindowsHardwareAccessGuard(processSource, serviceSource);
        var hostIdentity = new WindowsHostIdentitySource(registry, platform);
        var evaluator = new WindowsRazerHidEvidenceEvaluator(hostIdentity, guard, native);
        using HidEvidenceEvaluation evaluation = evaluator.Evaluate();
        return evaluation.Result;
    }

    internal static IBatteryValidationRunFactory CreateWindows(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(processSource);
        ArgumentNullException.ThrowIfNull(serviceSource);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(featureIo);
        ArgumentNullException.ThrowIfNull(timeProvider);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);

        var hardwareAccessGuard = new WindowsHardwareAccessGuard(processSource, serviceSource);
        var hostIdentity = new WindowsHostIdentitySource(registry, platform);
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            hardwareAccessGuard,
            native);
        var actorFactory = new ValidationActorFactory(
            opener,
            featureIo,
            hardwareAccessGuard,
            timeProvider,
            exchangeTimeout);

        return new BatteryValidationRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            cleanupDrainTimeout);
    }
}
