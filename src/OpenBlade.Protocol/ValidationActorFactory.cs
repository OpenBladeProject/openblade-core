namespace OpenBlade.Protocol;

internal sealed class ValidationActor : IAsyncDisposable
{
    internal ValidationActor(
        IRazerCommandSession session,
        IBatteryCommandClient batteryCommands)
        : this(
            session,
            batteryCommands,
            new TemporaryFullChargeCommandClient(session, TimeSpan.FromSeconds(3)))
    {
    }

    internal ValidationActor(
        IRazerCommandSession session,
        IBatteryCommandClient batteryCommands,
        ITemporaryFullChargeCommandClient temporaryFullChargeCommands)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(batteryCommands);
        ArgumentNullException.ThrowIfNull(temporaryFullChargeCommands);
        Session = session;
        BatteryCommands = batteryCommands;
        TemporaryFullChargeCommands = temporaryFullChargeCommands;
    }

    internal IRazerCommandSession Session { get; }

    internal IBatteryCommandClient BatteryCommands { get; }

    internal ITemporaryFullChargeCommandClient TemporaryFullChargeCommands { get; }

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}

internal interface IValidationActorFactory
{
    ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context);
}

internal sealed class ValidationActorFactory : IValidationActorFactory
{
    private readonly Func<HidSessionResult<OpenedHidHandle>> openHandle;
    private readonly Func<
        OpenedHidHandle,
        ValidationRunContext.ActorLease,
        IRazerCommandSession> createSession;
    private readonly TimeSpan exchangeTimeout;

    internal ValidationActorFactory(
        WindowsRazerHidSessionOpener opener,
        IHidFeatureIo io,
        IHardwareAccessGuard hardwareAccessGuard,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        ControllerExclusivityPolicy exclusivityPolicy,
        TimeSpan featureReportSettleDelay = default,
        byte featureReportId = 0)
        : this(
            opener.Open,
            (handle, lease) => new WindowsRazerHidSession(
                handle,
                io,
                hardwareAccessGuard,
                timeProvider,
                lease,
                exclusivityPolicy,
                featureReportSettleDelay,
                featureReportId,
                FirmwareQueryDiagnostics.Process),
            exchangeTimeout)
    {
        ArgumentNullException.ThrowIfNull(opener);
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(hardwareAccessGuard);
        ArgumentNullException.ThrowIfNull(timeProvider);
    }

    internal ValidationActorFactory(
        Func<HidSessionResult<OpenedHidHandle>> openHandle,
        Func<OpenedHidHandle, ValidationRunContext.ActorLease, IRazerCommandSession> createSession,
        TimeSpan exchangeTimeout)
    {
        ArgumentNullException.ThrowIfNull(openHandle);
        ArgumentNullException.ThrowIfNull(createSession);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.openHandle = openHandle;
        this.createSession = createSession;
        this.exchangeTimeout = exchangeTimeout;
    }

    public async ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
        ValidationRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryAcquireActor(out ValidationRunContext.ActorLease lease))
        {
            return Failure(HidSessionFailureKind.ProtocolFailure);
        }

        HidSessionResult<OpenedHidHandle> opened;
        try
        {
            opened = openHandle();
        }
        catch
        {
            lease.Dispose();
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        if (opened.Failure is not null)
        {
            lease.Dispose();
            return Failure(opened.Failure.Value);
        }

        OpenedHidHandle? handle = opened.Value;
        if (handle is null)
        {
            lease.Dispose();
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        IRazerCommandSession? session = null;
        try
        {
            session = createSession(handle, lease);
            var batteryCommands = new BatteryCommandClient(session, exchangeTimeout);
            var temporaryFullChargeCommands = new TemporaryFullChargeCommandClient(
                session,
                exchangeTimeout);
            return new(new ValidationActor(
                session,
                batteryCommands,
                temporaryFullChargeCommands), null);
        }
        catch
        {
            if (session is not null)
            {
                await DisposeIgnoringFailureAsync(session).ConfigureAwait(false);
            }
            else
            {
                await DisposeIgnoringFailureAsync(handle).ConfigureAwait(false);
                lease.Dispose();
            }

            return Failure(HidSessionFailureKind.NativeIoFailure);
        }
    }

    private static async ValueTask DisposeIgnoringFailureAsync(IAsyncDisposable resource)
    {
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static HidSessionResult<ValidationActor> Failure(HidSessionFailureKind failure) =>
        new(null, failure);
}
