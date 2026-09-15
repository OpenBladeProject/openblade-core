using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public sealed class ProductionLightingFactory : IProductionLightingPortFactory
{
    private static readonly TimeSpan DefaultExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultCleanupDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LightingFeatureReportSettleDelay =
        TimeSpan.FromMilliseconds(30);

    private readonly bool admitted;
    private readonly Func<ValidationRunContext> createContext;
    private readonly Func<TimeSpan, IValidationActorFactory> createActorFactory;
    private readonly Func<HidSessionResult<IPid02E0MatrixFeatureWriter>> openMatrixWriter;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;

    internal ProductionLightingFactory(
        bool admitted,
        Func<ValidationRunContext> createContext,
        Func<TimeSpan, IValidationActorFactory> createActorFactory,
        Func<HidSessionResult<IPid02E0MatrixFeatureWriter>> openMatrixWriter,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout,
        PendingDisposalRegistry? pendingDisposals = null,
        bool matrixWriteTimingEnabled = false)
    {
        this.admitted = admitted;
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        this.createActorFactory = createActorFactory
            ?? throw new ArgumentNullException(nameof(createActorFactory));
        this.openMatrixWriter = openMatrixWriter
            ?? throw new ArgumentNullException(nameof(openMatrixWriter));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
        MatrixWriteTimingEnabled = matrixWriteTimingEnabled;
        Capabilities = ProductionLightingPort.CreateCapabilities(admitted);
    }

    public DeviceCapabilities Capabilities { get; }

    internal bool MatrixWriteTimingEnabled { get; }

    public static IProductionLightingPortFactory CreateWindows()
        => CreateWindowsCore(compatibilityPolicy: null);

    internal static IProductionLightingPortFactory CreateWindows(
        FirmwareCompatibilityPolicy compatibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        return CreateWindowsCore(compatibilityPolicy);
    }

    private static IProductionLightingPortFactory CreateWindowsCore(
        FirmwareCompatibilityPolicy? compatibilityPolicy)
    {
        _ = ProductionLightingEvidenceSource.ReadValidatedOracle();
        return CreateWindows(
            admitted:
                ProductionLightingEvidenceSource
                    .IsProductionLightingImplementationApproved(),
            ControllerExclusivityPolicy.Advisory,
            compatibilityPolicy);
    }

    public static IProductionLightingPortFactory CreateWindowsReadOnly() =>
        CreateWindows(
            admitted: false,
            ControllerExclusivityPolicy.Advisory,
            compatibilityPolicy: null);

    internal static IProductionLightingPortFactory CreateWindowsReadOnly(
        FirmwareCompatibilityPolicy compatibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        return CreateWindows(
            admitted: false,
            ControllerExclusivityPolicy.Advisory,
            compatibilityPolicy);
    }

    /// <summary>
    /// Developer-only exact-build validation entry point. The Capture assembly
    /// still requires an explicit exact-target confirmation before opening it.
    /// No raw report or matrix-buffer surface is exposed.
    /// </summary>
    internal static IProductionLightingPortFactory CreateWindowsForExactBuildValidation(
        IMatrixWriteTimingSink? timingSink = null)
    {
        _ = ProductionLightingEvidenceSource.ReadValidatedOracle();
        return CreateWindows(
            admitted: true,
            ControllerExclusivityPolicy.Strict,
            compatibilityPolicy: null,
            timingSink);
    }

    public Task<HidSessionResult<IProductionLightingPort>> OpenAsync(
        CancellationToken cancellationToken) => OpenCoreAsync(null, cancellationToken);

    public Task<HidSessionResult<IProductionLightingPort>> OpenAsync(
        Func<bool> driverModeWriteAllowed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(driverModeWriteAllowed);
        return OpenCoreAsync(driverModeWriteAllowed, cancellationToken);
    }

    private async Task<HidSessionResult<IProductionLightingPort>> OpenCoreAsync(
        Func<bool>? driverModeWriteAllowed,
        CancellationToken cancellationToken)
    {
        if (driverModeWriteAllowed?.Invoke() == false)
        {
            return Failure(HidSessionFailureKind.ConflictBlocked);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(HidSessionFailureKind.Canceled);
        }

        ValidationRunContext context;
        IValidationActorFactory actorFactory;
        try
        {
            context = createContext() ?? throw new InvalidOperationException();
            actorFactory = createActorFactory(LightingFeatureReportSettleDelay)
                ?? throw new InvalidOperationException();
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        HidSessionResult<ValidationActor> actorResult;
        try
        {
            actorResult = await actorFactory.OpenAsync(context).ConfigureAwait(false);
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        ValidationActor? actor = actorResult.Value;
        if (actor is null || actorResult.Failure is not null)
        {
            if (actor is not null)
            {
                await DisposeActorBoundedAsync(actor).ConfigureAwait(false);
            }

            return Failure(
                actorResult.Failure ?? HidSessionFailureKind.NativeIoFailure,
                actorResult.Diagnostic);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await DisposeActorBoundedAsync(actor).ConfigureAwait(false);
            return Failure(HidSessionFailureKind.Canceled);
        }

        IPid02E0MatrixFeatureWriter? matrixWriter = null;
        if (admitted)
        {
            HidSessionResult<IPid02E0MatrixFeatureWriter> matrixResult;
            try
            {
                matrixResult = openMatrixWriter();
            }
            catch
            {
                await DisposeActorBoundedAsync(actor).ConfigureAwait(false);
                return Failure(HidSessionFailureKind.NativeIoFailure);
            }

            matrixWriter = matrixResult.Value;
            if (matrixWriter is null || matrixResult.Failure is not null)
            {
                if (matrixWriter is not null)
                {
                    await DisposeMatrixWriterAsync(matrixWriter).ConfigureAwait(false);
                }

                await DisposeActorBoundedAsync(actor).ConfigureAwait(false);
                return Failure(
                    matrixResult.Failure ?? HidSessionFailureKind.NativeIoFailure,
                    matrixResult.Diagnostic);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            if (matrixWriter is not null)
            {
                await DisposeMatrixWriterAsync(matrixWriter).ConfigureAwait(false);
            }

            await DisposeActorBoundedAsync(actor).ConfigureAwait(false);
            return Failure(HidSessionFailureKind.Canceled);
        }

        var port = new ProductionLightingPort(
            actor,
            matrixWriter,
            admitted,
            timeProvider,
            exchangeTimeout,
            cleanupDrainTimeout,
            pendingDisposals,
            driverModeWriteAllowed);
        if (admitted)
        {
            try
            {
                await port.EnsureDriverModeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await port.DisposeAsync().ConfigureAwait(false);
                return Failure(HidSessionFailureKind.Canceled);
            }
            catch
            {
                await port.DisposeAsync().ConfigureAwait(false);
                return Failure(HidSessionFailureKind.ProtocolFailure);
            }
        }

        return new(port, null);
    }

    private static IProductionLightingPortFactory CreateWindows(
        bool admitted,
        ControllerExclusivityPolicy exclusivityPolicy,
        FirmwareCompatibilityPolicy? compatibilityPolicy,
        IMatrixWriteTimingSink? timingSink = null)
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var processSource = new RunningProcessSource();
        var serviceSource = new WindowsServiceSource();
        var guard = new WindowsHardwareAccessGuard(processSource, serviceSource);
        var hostIdentity = compatibilityPolicy is null
            ? new WindowsHostIdentitySource(
                registry,
                new RuntimeWindowsPlatformEvidenceSource(registry))
            : new WindowsHostIdentitySource(
                registry,
                new RuntimeWindowsPlatformEvidenceSource(registry),
                compatibilityPolicy);
        var sessionOpener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            guard,
            native,
            exclusivityPolicy);
        var matrixOpener = new WindowsPid02E0MatrixFeatureOpener(
            hostIdentity,
            guard,
            native,
            exclusivityPolicy);
        var controlIo = new OverlappedFeatureOperation(native);
        IHidFeatureIo matrixIo = timingSink is null
            ? new OverlappedFeatureOperation(
                native,
                TargetBladeDevice.MatrixFeatureReportByteLength)
            : new OverlappedFeatureOperation(
                native,
                TargetBladeDevice.MatrixFeatureReportByteLength,
                TimeProvider.System,
                captureTiming: true);

        return new ProductionLightingFactory(
            admitted,
            static () => new ValidationRunContext(),
            settleDelay => new ValidationActorFactory(
                sessionOpener,
                controlIo,
                guard,
                TimeProvider.System,
                DefaultExchangeTimeout,
                exclusivityPolicy,
                settleDelay),
            () =>
            {
                HidSessionResult<OpenedHidHandle> opened = matrixOpener.Open();
                return opened.Value is { } handle && opened.Failure is null
                    ? new HidSessionResult<IPid02E0MatrixFeatureWriter>(
                        new WindowsPid02E0MatrixFeatureWriter(
                            handle,
                            matrixIo,
                            guard,
                            TimeProvider.System,
                            DefaultExchangeTimeout,
                            exclusivityPolicy,
                            timingSink),
                        null)
                    : new HidSessionResult<IPid02E0MatrixFeatureWriter>(
                        null,
                        opened.Failure ?? HidSessionFailureKind.NativeIoFailure)
                    {
                        Diagnostic = opened.Diagnostic,
                    };
            },
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout,
            PendingDisposalRegistry.Shared,
            matrixWriteTimingEnabled: timingSink is not null);
    }

    private async Task DisposeActorBoundedAsync(ValidationActor actor) =>
        await BoundedValidationActorDisposal.DisposeAsync(
            actor,
            timeProvider,
            cleanupDrainTimeout,
            pendingDisposals).ConfigureAwait(false);

    private static async Task DisposeMatrixWriterAsync(IPid02E0MatrixFeatureWriter writer)
    {
        try
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static HidSessionResult<IProductionLightingPort> Failure(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic = null) => new(null, failure)
        {
            Diagnostic = diagnostic,
        };
}
