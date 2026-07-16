namespace OpenBlade.Protocol;

public static class BatteryValidationFactory
{
    private static readonly TimeSpan DefaultExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultCleanupDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BatteryFeatureReportSettleDelay =
        TimeSpan.FromMilliseconds(20);

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

    public static IBatteryQuerySequenceRunFactory CreateWindowsQuerySequence()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsQuerySequence(
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

    public static ITemporaryFullChargeQueryRunFactory CreateWindowsTemporaryFullChargeQuery()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsTemporaryFullChargeQuery(
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

    public static IProtectionOffValidationRunFactory CreateWindowsProtectionOffValidation()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsProtectionOffValidation(
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

    public static IBatteryQuerySequenceRunFactory CreateWindowsQuerySequenceForService()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsQuerySequenceForService(
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

    public static BatteryValidationProbeResult ProbeWindowsForService()
    {
        var registry = new WindowsRegistryValueSource();
        return ProbeWindowsForService(
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
        IHidCandidateNative native) =>
        ProbeWindows(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            ControllerExclusivityPolicy.Strict);

    internal static BatteryValidationProbeResult ProbeWindowsForService(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native) =>
        ProbeWindows(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            ControllerExclusivityPolicy.Advisory);

    private static BatteryValidationProbeResult ProbeWindows(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        ControllerExclusivityPolicy exclusivityPolicy)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(processSource);
        ArgumentNullException.ThrowIfNull(serviceSource);
        ArgumentNullException.ThrowIfNull(native);

        var guard = new WindowsHardwareAccessGuard(processSource, serviceSource);
        var hostIdentity = new WindowsHostIdentitySource(registry, platform);
        var evaluator = new WindowsRazerHidEvidenceEvaluator(
            hostIdentity,
            guard,
            native,
            exclusivityPolicy);
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
            native,
            ControllerExclusivityPolicy.Strict);
        var actorFactory = new ValidationActorFactory(
            opener,
            featureIo,
            hardwareAccessGuard,
            timeProvider,
            exchangeTimeout,
            ControllerExclusivityPolicy.Strict,
            BatteryFeatureReportSettleDelay);

        return new BatteryValidationRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            cleanupDrainTimeout);
    }

    internal static IBatteryQuerySequenceRunFactory CreateWindowsQuerySequence(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout) =>
        CreateWindowsQuerySequence(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            featureIo,
            timeProvider,
            exchangeTimeout,
            cleanupDrainTimeout,
            ControllerExclusivityPolicy.Strict);

    internal static ITemporaryFullChargeQueryRunFactory CreateWindowsTemporaryFullChargeQuery(
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
        ValidationActorFactory actorFactory = CreateActorFactory(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            featureIo,
            timeProvider,
            exchangeTimeout,
            ControllerExclusivityPolicy.Strict);
        return new TemporaryFullChargeQueryRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            cleanupDrainTimeout);
    }

    internal static IProtectionOffValidationRunFactory CreateWindowsProtectionOffValidation(
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
        ValidationActorFactory actorFactory = CreateActorFactory(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            featureIo,
            timeProvider,
            exchangeTimeout,
            ControllerExclusivityPolicy.Strict);
        return new ProtectionOffValidationRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            cleanupDrainTimeout);
    }

    internal static IBatteryQuerySequenceRunFactory CreateWindowsQuerySequenceForService(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout) =>
        CreateWindowsQuerySequence(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            featureIo,
            timeProvider,
            exchangeTimeout,
            cleanupDrainTimeout,
            ControllerExclusivityPolicy.Advisory);

    private static IBatteryQuerySequenceRunFactory CreateWindowsQuerySequence(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout,
        ControllerExclusivityPolicy exclusivityPolicy)
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
            native,
            exclusivityPolicy);
        var actorFactory = new ValidationActorFactory(
            opener,
            featureIo,
            hardwareAccessGuard,
            timeProvider,
            exchangeTimeout,
            exclusivityPolicy,
            BatteryFeatureReportSettleDelay);

        return new BatteryQuerySequenceRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            cleanupDrainTimeout);
    }

    private static ValidationActorFactory CreateActorFactory(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        ControllerExclusivityPolicy exclusivityPolicy)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(processSource);
        ArgumentNullException.ThrowIfNull(serviceSource);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(featureIo);
        ArgumentNullException.ThrowIfNull(timeProvider);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);

        var hardwareAccessGuard = new WindowsHardwareAccessGuard(processSource, serviceSource);
        var hostIdentity = new WindowsHostIdentitySource(registry, platform);
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            hardwareAccessGuard,
            native,
            exclusivityPolicy);
        return new ValidationActorFactory(
            opener,
            featureIo,
            hardwareAccessGuard,
            timeProvider,
            exchangeTimeout,
            exclusivityPolicy,
            BatteryFeatureReportSettleDelay);
    }
}
