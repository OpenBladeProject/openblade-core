namespace OpenSynapse.Protocol;

internal sealed class ValidationActor : IAsyncDisposable
{
    internal ValidationActor(
        IRazerCommandSession session,
        IBatteryCommandClient batteryCommands)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(batteryCommands);
        Session = session;
        BatteryCommands = batteryCommands;
    }

    internal IRazerCommandSession Session { get; }

    internal IBatteryCommandClient BatteryCommands { get; }

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
        TimeSpan exchangeTimeout)
        : this(
            opener.Open,
            (handle, lease) => new WindowsRazerHidSession(
                handle,
                io,
                hardwareAccessGuard,
                timeProvider,
                lease),
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
            var commands = new BatteryCommandClient(session, exchangeTimeout);
            return new(new ValidationActor(session, commands), null);
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
