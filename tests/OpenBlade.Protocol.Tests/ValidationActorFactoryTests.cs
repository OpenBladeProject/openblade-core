using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ValidationActorFactoryTests
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task OpenAsync_AcquiresLeaseBeforeOpeningHandle()
    {
        var context = new ValidationRunContext();
        var session = new LeaseOwningSession();
        OpenedHidHandle handle = Handle();
        bool leaseWasActive = false;
        var factory = Factory(
            () =>
            {
                leaseWasActive = !context.TryAcquireActor(out _);
                return Success(handle);
            },
            (_, lease) => session.Attach(lease));

        HidSessionResult<ValidationActor> result = await factory.OpenAsync(context);

        Assert.Null(result.Failure);
        Assert.True(leaseWasActive);
        await result.Value!.DisposeAsync();
    }

    [Theory]
    [InlineData(HidSessionFailureKind.IdentityBlocked)]
    [InlineData(HidSessionFailureKind.ConflictBlocked)]
    [InlineData(HidSessionFailureKind.ConflictDetectionFailed)]
    [InlineData(HidSessionFailureKind.NativeIoFailure)]
    public async Task OpenAsync_PropagatesExactOpenerFailureAndReleasesLease(
        HidSessionFailureKind failure)
    {
        var context = new ValidationRunContext();
        var factory = Factory(
            () => new(null, failure),
            (handle, lease) => throw new InvalidOperationException("session construction must not run"));

        HidSessionResult<ValidationActor> result = await factory.OpenAsync(context);

        Assert.Equal(failure, result.Failure);
        Assert.Null(result.Value);
        Assert.True(context.TryAcquireActor(out ValidationRunContext.ActorLease replacement));
        replacement.Dispose();
    }

    [Fact]
    public async Task OpenAsync_ReturnsAggregateWithTypedClientAndOwnedSession()
    {
        var context = new ValidationRunContext();
        var session = new LeaseOwningSession();
        var factory = Factory(() => Success(Handle()), (_, lease) => session.Attach(lease));

        HidSessionResult<ValidationActor> result = await factory.OpenAsync(context);

        ValidationActor actor = Assert.IsType<ValidationActor>(result.Value);
        Assert.Same(session, actor.Session);
        Assert.IsAssignableFrom<IBatteryCommandClient>(actor.BatteryCommands);
        Assert.IsAssignableFrom<ITemporaryFullChargeCommandClient>(actor.TemporaryFullChargeCommands);
        await actor.DisposeAsync();
        Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, session.Disposition);
    }

    [Fact]
    public async Task OpenAsync_RejectsSecondActorBeforeCallingOpener()
    {
        var context = new ValidationRunContext();
        int openerCalls = 0;
        var sessions = new Queue<LeaseOwningSession>([new(), new()]);
        var factory = Factory(
            () =>
            {
                openerCalls++;
                return Success(Handle());
            },
            (_, lease) => sessions.Dequeue().Attach(lease));

        HidSessionResult<ValidationActor> first = await factory.OpenAsync(context);
        HidSessionResult<ValidationActor> second = await factory.OpenAsync(context);

        Assert.Null(first.Failure);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure, second.Failure);
        Assert.Null(second.Value);
        Assert.Equal(1, openerCalls);
        await first.Value!.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_WhenSessionConstructionFails_DisposesHandleThenReleasesLeaseOnce()
    {
        var context = new ValidationRunContext();
        List<string> events = [];
        OpenedHidHandle handle = Handle(events);
        var factory = Factory(
            () => Success(handle),
            (openedHandle, lease) =>
            {
                events.Add("construct");
                Assert.False(context.TryAcquireActor(out _));
                throw new InvalidOperationException("scripted construction failure");
            });

        HidSessionResult<ValidationActor> result = await factory.OpenAsync(context);

        Assert.Equal(HidSessionFailureKind.NativeIoFailure, result.Failure);
        Assert.Equal(["construct", "bound", "file"], events);
        Assert.True(context.TryAcquireActor(out ValidationRunContext.ActorLease replacement));
        Assert.False(context.TryAcquireActor(out _));
        replacement.Dispose();
    }

    [Fact]
    public async Task OpenAsync_AfterFirstActorFullyDisposes_AllowsRecoveryActor()
    {
        var context = new ValidationRunContext();
        int openerCalls = 0;
        var sessions = new Queue<LeaseOwningSession>([new(), new()]);
        var factory = Factory(
            () =>
            {
                openerCalls++;
                return Success(Handle());
            },
            (_, lease) => sessions.Dequeue().Attach(lease));

        HidSessionResult<ValidationActor> first = await factory.OpenAsync(context);
        await first.Value!.DisposeAsync();
        HidSessionResult<ValidationActor> second = await factory.OpenAsync(context);

        Assert.Null(second.Failure);
        Assert.Equal(2, openerCalls);
        await second.Value!.DisposeAsync();
    }

    private static ValidationActorFactory Factory(
        Func<HidSessionResult<OpenedHidHandle>> open,
        Func<OpenedHidHandle, ValidationRunContext.ActorLease, IRazerCommandSession> createSession) =>
        new(open, createSession, ExchangeTimeout);

    private static HidSessionResult<OpenedHidHandle> Success(OpenedHidHandle handle) =>
        new(handle, null);

    private static OpenedHidHandle Handle(List<string>? events = null)
    {
        events ??= [];
        var file = (SafeFileHandle)RuntimeHelpers.GetUninitializedObject(typeof(SafeFileHandle));
        var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(typeof(ThreadPoolBoundHandle));
        return new OpenedHidHandle(
            file,
            bound,
            () => events.Add("bound"),
            () => events.Add("file"));
    }

    private sealed class LeaseOwningSession : IRazerCommandSession
    {
        private ValidationRunContext.ActorLease? lease;

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        internal LeaseOwningSession Attach(ValidationRunContext.ActorLease actorLease)
        {
            lease = actorLease;
            return this;
        }

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            if (Disposition != RazerCommandSessionDisposition.FullyDisposed)
            {
                Disposition = RazerCommandSessionDisposition.Disposing;
                lease!.Dispose();
                Disposition = RazerCommandSessionDisposition.FullyDisposed;
            }

            return ValueTask.CompletedTask;
        }
    }
}
