namespace OpenBlade.Protocol;

internal sealed class Rz090528BatteryValidationFactory : IBatteryValidationRunFactory
{
    private static readonly RazerCommandSpec RawQueryCommand = new(
        0x07,
        0x92,
        [0x00],
        1,
        static payload => payload.Length == 1);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;

    internal Rz090528BatteryValidationFactory(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory)
    {
        this.createContext = createContext;
        this.actorFactory = actorFactory;
    }

    public async Task<HidSessionResult<IBatteryValidationPort>> OpenAsync(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(null, HidSessionFailureKind.Canceled);
        }

        ValidationRunContext context;
        try
        {
            context = createContext();
        }
        catch
        {
            return new(null, HidSessionFailureKind.NativeIoFailure);
        }

        HidSessionResult<ValidationActor> opened;
        try
        {
            opened = await actorFactory.OpenAsync(context).ConfigureAwait(false);
        }
        catch
        {
            return new(null, HidSessionFailureKind.NativeIoFailure);
        }

        if (opened.Value is not { } actor || opened.Failure is not null)
        {
            if (opened.Value is { } unexpected)
            {
                await unexpected.DisposeAsync().ConfigureAwait(false);
            }

            return new(null, opened.Failure ?? HidSessionFailureKind.NativeIoFailure)
            {
                Diagnostic = opened.Diagnostic,
            };
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await actor.DisposeAsync().ConfigureAwait(false);
            return new(null, HidSessionFailureKind.Canceled);
        }

        return new(new Rz090528BatteryQueryPort(actor), null);
    }

    internal async Task<HidSessionResult<byte>> RunRawAsync(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(default, HidSessionFailureKind.Canceled);
        }

        ValidationRunContext context;
        try
        {
            context = createContext();
        }
        catch
        {
            return new(default, HidSessionFailureKind.NativeIoFailure);
        }

        HidSessionResult<ValidationActor> opened;
        try
        {
            opened = await actorFactory.OpenAsync(context).ConfigureAwait(false);
        }
        catch
        {
            return new(default, HidSessionFailureKind.NativeIoFailure);
        }

        if (opened.Value is not { } actor || opened.Failure is not null)
        {
            if (opened.Value is { } unexpected)
            {
                await unexpected.DisposeAsync().ConfigureAwait(false);
            }

            return new(default, opened.Failure ?? HidSessionFailureKind.NativeIoFailure)
            {
                Diagnostic = opened.Diagnostic,
            };
        }

        await using (actor)
        {
            HidSessionResult<ReadOnlyMemory<byte>> response =
                await actor.Session.ExecuteAsync(
                    RawQueryCommand,
                    static () => TransactionIdAssignment.Valid(1),
                    TimeSpan.FromSeconds(3),
                    cancellationToken).ConfigureAwait(false);
            if (response.Failure is not null)
            {
                return new(default, response.Failure)
                {
                    Diagnostic = response.Diagnostic,
                };
            }

            return response.Value.Length == 1
                ? new(response.Value.Span[0], null)
                : new(default, HidSessionFailureKind.ProtocolFailure);
        }
    }
}

internal sealed class Rz090528BatteryQueryPort(ValidationActor actor)
    : IBatteryValidationPort
{
    private int nextTransaction = 1;
    private int disposed;

    public async Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return new(default, HidSessionFailureKind.Disposed);
        }

        return await actor.BatteryCommands.QueryAsync(
            AllocateTransaction(),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<HidSessionResult<HidUnit>> ApplyAsync(
        BatteryValidationSetting setting,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return Task.FromResult(
                new HidSessionResult<HidUnit>(
                    default,
                    HidSessionFailureKind.Disposed));
        }

        return actor.BatteryCommands.SetAsync(
            AllocateTransaction(),
            setting,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await actor.DisposeAsync().ConfigureAwait(false);
    }

    private byte AllocateTransaction()
    {
        int value = nextTransaction;
        nextTransaction = value == byte.MaxValue ? 1 : value + 1;
        return checked((byte)value);
    }
}
