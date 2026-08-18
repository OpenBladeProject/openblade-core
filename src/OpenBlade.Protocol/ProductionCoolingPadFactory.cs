namespace OpenBlade.Protocol;

internal sealed class ProductionCoolingPadFactory : IProductionCoolingPadPortFactory
{
    private static readonly TimeSpan DefaultExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FeatureReportSettleDelay =
        TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan DefaultCleanupDrainTimeout =
        TimeSpan.FromSeconds(5);

    private static readonly ExpectedHostIdentity TargetHost = new(
        "Razer",
        "Blade 16 - RZ09-0581",
        "RZ09-05819EN4",
        TargetBladeDevice.ValidatedBiosVersions,
        FirmwareAdmissionScope.ModelScoped);

    private static readonly HidHandleIdentity TargetPad = new(
        WindowsCoolingPadDiscoverySource.VendorId,
        WindowsCoolingPadDiscoverySource.ProductId,
        WindowsCoolingPadDiscoverySource.UsagePage,
        WindowsCoolingPadDiscoverySource.Usage,
        RazerReport.ReportLength + 1,
        WindowsCoolingPadDiscoverySource.VersionNumber);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;

    internal ProductionCoolingPadFactory(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeSpan exchangeTimeout,
        TimeProvider? timeProvider = null,
        TimeSpan? cleanupDrainTimeout = null,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        ArgumentNullException.ThrowIfNull(actorFactory);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        TimeSpan effectiveCleanup = cleanupDrainTimeout
            ?? DefaultCleanupDrainTimeout;
        RazerCommandSessionTimeout.Validate(effectiveCleanup);
        this.createContext = createContext;
        this.actorFactory = actorFactory;
        this.exchangeTimeout = exchangeTimeout;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.cleanupDrainTimeout = effectiveCleanup;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    internal static IProductionCoolingPadPortFactory CreateWindows()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var guard = new WindowsHardwareAccessGuard(
            new RunningProcessSource(),
            new WindowsServiceSource());
        var hostIdentity = new WindowsHostIdentitySource(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            TargetHost);
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            guard,
            native,
            ControllerExclusivityPolicy.Strict,
            TargetPad);
        return new ProductionCoolingPadFactory(
            static () => new ValidationRunContext(),
            new ValidationActorFactory(
                opener,
                new OverlappedFeatureOperation(native),
                guard,
                TimeProvider.System,
                DefaultExchangeTimeout,
                ControllerExclusivityPolicy.Strict,
                FeatureReportSettleDelay,
                featureReportId: 0x05),
            DefaultExchangeTimeout,
            TimeProvider.System,
            DefaultCleanupDrainTimeout,
            PendingDisposalRegistry.Shared);
    }

    public async Task<HidSessionResult<IProductionCoolingPadPort>> OpenAsync(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(HidSessionFailureKind.Canceled);
        }

        ValidationRunContext context;
        try
        {
            context = createContext() ?? throw new InvalidOperationException();
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        HidSessionResult<ValidationActor> opened;
        try
        {
            opened = await actorFactory.OpenAsync(context).ConfigureAwait(false);
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        if (opened.Failure is not null || opened.Value is null)
        {
            if (opened.Value is not null)
            {
                await DisposeActorBoundedAsync(opened.Value).ConfigureAwait(false);
            }

            return Failure(opened.Failure ?? HidSessionFailureKind.NativeIoFailure);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await DisposeActorBoundedAsync(opened.Value).ConfigureAwait(false);
            return Failure(HidSessionFailureKind.Canceled);
        }

        return new(
            new ProductionCoolingPadPort(
                opened.Value.Session,
                opened.Value,
                context,
                exchangeTimeout),
            null);
    }

    private async Task DisposeActorBoundedAsync(ValidationActor actor) =>
        await BatteryValidationRun.DisposeBoundedAsync(
            actor,
            timeProvider,
            cleanupDrainTimeout,
            pendingDisposals).ConfigureAwait(false);

    private static HidSessionResult<IProductionCoolingPadPort> Failure(
        HidSessionFailureKind failure) => new(null, failure);
}
