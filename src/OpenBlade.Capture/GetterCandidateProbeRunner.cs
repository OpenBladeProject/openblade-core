using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record GetterCandidateProbeSnapshot(string ResponsePayloadHex);

internal interface IGetterCandidateProbeRunner
{
    Task<HidSessionResult<GetterCandidateProbeSnapshot>> RunAsync(
        CancellationToken cancellationToken);
}

internal static class GetterCandidateProbeFactory
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FeatureReportSettleDelay = TimeSpan.FromMilliseconds(20);

    internal static IGetterCandidateProbeRunner CreateCoolingPadControlStateWindows()
        => new CoolingPadControlStateQueryRunner(
            static () => new ValidationRunContext(), CreateCoolingPadActorFactory(),
            TimeProvider.System, ExchangeTimeout, CleanupTimeout);

    internal static CoolingPadControlStateRoundTripRunner CreateCoolingPadControlStateRoundTripWindows()
        => new(static () => new ValidationRunContext(), CreateCoolingPadActorFactory(),
            TimeProvider.System, ExchangeTimeout, CleanupTimeout);

    internal static CoolingPadFanExploratoryTrialRunner CreateCoolingPadFanExploratoryTrialWindows()
        => new(static () => new ValidationRunContext(), CreateCoolingPadActorFactory(),
            TimeProvider.System, ExchangeTimeout, CleanupTimeout, TimeSpan.FromSeconds(180));

    internal static CoolingPadControlStateRecoveryRunner CreateCoolingPadControlStateRecoveryWindows()
        => new(static () => new ValidationRunContext(), CreateCoolingPadActorFactory(),
            TimeProvider.System, ExchangeTimeout, CleanupTimeout, TimeSpan.FromSeconds(180));

    private static IValidationActorFactory CreateCoolingPadActorFactory()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var guard = new CoolingPadControlStateQueryFactory.PadAccessGuard(new RunningProcessSource(), new WindowsServiceSource());
        var opener = new WindowsRazerHidSessionOpener(
            new WindowsHostIdentitySource(registry,
                new RuntimeWindowsPlatformEvidenceSource(registry),
                WindowsHostIdentitySource.Rz090528Identity),
            guard, native, ControllerExclusivityPolicy.Strict,
            CoolingPadControlStateQueryFactory.ExactIdentity);
        return new ValidationActorFactory(opener, new OverlappedFeatureOperation(native),
                guard, TimeProvider.System, TimeSpan.FromSeconds(3),
                ControllerExclusivityPolicy.Strict, TimeSpan.FromMilliseconds(30),
                featureReportId: 0);
    }

    internal static IGetterCandidateProbeRunner CreateFunctionKeyPrimaryWindows() =>
        new FunctionKeyPrimaryGetterCandidateProbeRunner(
            static () => new ValidationRunContext(),
            CreateStrictActorFactory(),
            TimeProvider.System,
            ExchangeTimeout,
            CleanupTimeout);

    internal static IGetterCandidateProbeRunner CreateGamingModeWindows() =>
        new GamingModeGetterCandidateProbeRunner(
            static () => new ValidationRunContext(),
            CreateStrictActorFactory(),
            TimeProvider.System,
            ExchangeTimeout,
            CleanupTimeout);

    internal static IGetterCandidateProbeRunner
        CreateFunctionKeyPrimaryRz090528Windows() =>
        new FunctionKeyPrimaryGetterCandidateProbeRunner(
            static () => new ValidationRunContext(),
            CreateRz090528AdvisoryActorFactory(),
            TimeProvider.System,
            ExchangeTimeout,
            CleanupTimeout);

    internal static IGetterCandidateProbeRunner CreateGamingModeRz090528Windows() =>
        new GamingModeGetterCandidateProbeRunner(
            static () => new ValidationRunContext(),
            CreateRz090528AdvisoryActorFactory(),
            TimeProvider.System,
            ExchangeTimeout,
            CleanupTimeout);

    internal static IGetterCandidateProbeRunner CreateDeviceModeRz090528Windows() =>
        new DeviceModeQueryRunner(
            static () => new ValidationRunContext(),
            CreateRz090528AdvisoryActorFactory(),
            TimeProvider.System,
            ExchangeTimeout,
            CleanupTimeout);

    internal static IGetterCandidateProbeRunner CreateDeviceModeWindows() =>
        new DeviceModeQueryRunner(
            static () => new ValidationRunContext(),
            CreateStrictActorFactory(),
            TimeProvider.System,
            ExchangeTimeout,
            CleanupTimeout);

    internal static IDeviceModeValidationRunner CreateDeviceModeValidationWindows() =>
        new DeviceModeValidationRunner(
            static () => new ValidationRunContext(),
            CreateStrictActorFactory(),
            TimeProvider.System,
            ExchangeTimeout,
            CleanupTimeout);

    internal static IDeviceModeValidationRunner
        CreateDeviceModeValidationRz090528Windows() =>
        new DeviceModeValidationRunner(
            static () => new ValidationRunContext(),
            CreateRz090528ActorFactory(ControllerExclusivityPolicy.Strict),
            TimeProvider.System,
            ExchangeTimeout,
            CleanupTimeout);

    private static IValidationActorFactory CreateStrictActorFactory()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var guard = new WindowsHardwareAccessGuard(
            new RunningProcessSource(),
            new WindowsServiceSource());
        var opener = new WindowsRazerHidSessionOpener(
            new WindowsHostIdentitySource(
                registry,
                new RuntimeWindowsPlatformEvidenceSource(registry)),
            guard,
            native,
            ControllerExclusivityPolicy.Strict);
        return new ValidationActorFactory(
            opener,
            new OverlappedFeatureOperation(native),
            guard,
            TimeProvider.System,
            ExchangeTimeout,
            ControllerExclusivityPolicy.Strict,
            FeatureReportSettleDelay);
    }

    private static IValidationActorFactory CreateRz090528AdvisoryActorFactory()
        => CreateRz090528ActorFactory(ControllerExclusivityPolicy.Advisory);

    private static IValidationActorFactory CreateRz090528ActorFactory(
        ControllerExclusivityPolicy policy)
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var guard = new WindowsHardwareAccessGuard(
            new RunningProcessSource(),
            new WindowsServiceSource());
        var opener = new WindowsRazerHidSessionOpener(
            new WindowsHostIdentitySource(
                registry,
                new RuntimeWindowsPlatformEvidenceSource(registry),
                WindowsHostIdentitySource.Rz090528Identity),
            guard,
            native,
            policy,
            new HidHandleIdentity(
                VendorId: 0x1532,
                ProductId: 0x02C6,
                UsagePage: 0x0001,
                Usage: 0x0002,
                FeatureReportByteLength: 91));
        return new ValidationActorFactory(
            opener,
            new OverlappedFeatureOperation(native),
            guard,
            TimeProvider.System,
            ExchangeTimeout,
            policy,
            TimeSpan.FromMilliseconds(30));
    }
}

internal abstract class GetterCandidateProbeRunner : IGetterCandidateProbeRunner
{
    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int started;

    protected GetterCandidateProbeRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupTimeout,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory ?? throw new ArgumentNullException(nameof(actorFactory));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupTimeout = cleanupTimeout;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    protected abstract RazerCommandSpec CreateFixedCommand();

    public async Task<HidSessionResult<GetterCandidateProbeSnapshot>> RunAsync(
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return Failure(HidSessionFailureKind.Disposed);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(HidSessionFailureKind.Canceled);
        }

        ValidationRunContext context;
        byte transactionId;
        try
        {
            context = createContext() ?? throw new InvalidOperationException();
            if (!context.TryAllocateTransaction(out transactionId))
            {
                return Failure(HidSessionFailureKind.Exhausted);
            }
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        ValidationActor? actor = null;
        try
        {
            HidSessionResult<ValidationActor> opened =
                await actorFactory.OpenAsync(context).ConfigureAwait(false);
            actor = opened.Value;
            if (opened.Failure is not null || actor is null)
            {
                return Failure(
                    opened.Failure ?? HidSessionFailureKind.NativeIoFailure,
                    opened.Diagnostic);
            }

            cancellationToken.ThrowIfCancellationRequested();
            HidSessionResult<ReadOnlyMemory<byte>> response =
                await actor.Session.ExecuteAsync(
                    CreateFixedCommand(),
                    () => TransactionIdAssignment.Valid(transactionId),
                    exchangeTimeout,
                    cancellationToken).ConfigureAwait(false);
            if (response.Failure is not null)
            {
                return Failure(response.Failure.Value, response.Diagnostic);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new(
                new GetterCandidateProbeSnapshot(Convert.ToHexString(response.Value.Span)),
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(HidSessionFailureKind.Canceled);
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }
        finally
        {
            if (actor is not null)
            {
                await BoundedValidationActorDisposal.DisposeAsync(
                    actor,
                    timeProvider,
                    cleanupTimeout,
                    pendingDisposals).ConfigureAwait(false);
            }
        }
    }

    private static HidSessionResult<GetterCandidateProbeSnapshot> Failure(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic = null) =>
        new(null, failure) { Diagnostic = diagnostic };
}

internal sealed class FunctionKeyPrimaryGetterCandidateProbeRunner
    : GetterCandidateProbeRunner
{
    internal FunctionKeyPrimaryGetterCandidateProbeRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupTimeout,
        PendingDisposalRegistry? pendingDisposals = null)
        : base(
            createContext,
            actorFactory,
            timeProvider,
            exchangeTimeout,
            cleanupTimeout,
            pendingDisposals)
    {
    }

    protected override RazerCommandSpec CreateFixedCommand() =>
        new(0x02, 0x86, [0x01, 0x00], expectedPayloadLength: 2);
}

internal sealed class GamingModeGetterCandidateProbeRunner : GetterCandidateProbeRunner
{
    internal GamingModeGetterCandidateProbeRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupTimeout,
        PendingDisposalRegistry? pendingDisposals = null)
        : base(
            createContext,
            actorFactory,
            timeProvider,
            exchangeTimeout,
            cleanupTimeout,
            pendingDisposals)
    {
    }

    protected override RazerCommandSpec CreateFixedCommand() =>
        new(0x00, 0x88, [0x00, 0x00, 0x00, 0x00], expectedPayloadLength: 4);
}

internal sealed class DeviceModeQueryRunner : GetterCandidateProbeRunner
{
    internal DeviceModeQueryRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupTimeout,
        PendingDisposalRegistry? pendingDisposals = null)
        : base(
            createContext,
            actorFactory,
            timeProvider,
            exchangeTimeout,
            cleanupTimeout,
            pendingDisposals)
    {
    }

    protected override RazerCommandSpec CreateFixedCommand() =>
        new(
            DeviceModeProtocol.OperationClass,
            DeviceModeProtocol.QueryOperationId,
            DeviceModeProtocol.QueryArguments.ToArray(),
            expectedPayloadLength: 2,
            payload => DeviceModeProtocol.TryDecodeQuery(payload.Span, out _));
}
