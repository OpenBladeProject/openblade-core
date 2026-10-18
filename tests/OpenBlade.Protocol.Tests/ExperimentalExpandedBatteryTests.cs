using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalExpandedBatteryTests
{
    private static readonly ushort[] ExpandedProductIds = [0x029F, 0x029D, 0x02C5, 0x02A0];

    public static TheoryData<ushort, int> BoundaryLimits
    {
        get
        {
            var data = new TheoryData<ushort, int>();
            foreach (ushort productId in ExpandedProductIds)
            {
                data.Add(productId, 50);
                data.Add(productId, 99);
                data.Add(productId, 100);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(BoundaryLimits))]
    public async Task ExpandedModel_BoundaryLimitUsesOneSetterAndIndependentReadback(
        ushort productId,
        int percent)
    {
        ExperimentalBladeModel model = Model(productId);
        var session = new BatterySession { Raw = percent == 99 ? (byte)0x50 : (byte)0xE3 };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(model, session, policy);

        await port.SetChargeLimitAsync(percent, CancellationToken.None);

        byte expected = percent == 100 ? (byte)0x50 : (byte)(0x80 | percent);
        Assert.Equal(productId == 0x02A0 ? new byte[] { 0x92, 0x8F, 0x12, 0x92 } : [0x92, 0x12, 0x92],
            session.Commands.Select(command => command.CommandId));
        Assert.Equal(expected, Assert.Single(session.Commands, command => command.CommandId == 0x12).Arguments.Span[0]);
        Assert.Equal(expected, session.Raw);
        Assert.Single(session.Commands, command => command.CommandId == 0x12);
        Assert.Contains(policy.Snapshot().Observations, observation =>
            observation.Stage == "Readback"
            && observation.ConfirmedValue == (percent == 100 ? "Off" : percent.ToString()));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x50)]
    [InlineData(0x64)]
    [InlineData(0x7F)]
    [InlineData(0xE4)]
    public async Task ExpandedModels_DecodeEveryAdmittedOffBaselineWithoutNormalizingRawState(int raw)
    {
        foreach (ushort productId in ExpandedProductIds)
        {
            ExperimentalBladeModel model = Model(productId);
            var session = new BatterySession { Raw = (byte)raw };
            var policy = new ExperimentalFeaturesPolicy(model);
            await using var port = Create(model, session, policy);

            ConfirmedBatteryState state = await port.QueryProtectionAsync(CancellationToken.None);

            Assert.False(state.ProtectionEnabled);
            Assert.Null(state.ChargeLimitPercent);
            Assert.Single(session.Commands);
            Assert.Equal(0x92, session.Commands[0].CommandId);
            Assert.Contains(policy.Snapshot().Observations, observation =>
                observation.ConfirmedValue?.Contains($"Raw={raw:X2}", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public async Task ExpandedModelsRemainExactAndLegacyModelStaysExcluded()
    {
        foreach (ushort productId in ExpandedProductIds)
        {
            ExperimentalBladeModel model = Model(productId);
            Assert.Equal(Enumerable.Range(50, 51),
                ExperimentalBladePort.GetExperimentalChargeLimits(model).Order());
        }

        ExperimentalBladeModel excluded = Model(0x0255);
        var session = new BatterySession();
        var policy = new ExperimentalFeaturesPolicy(excluded);
        await using var port = Create(excluded, session, policy);

        Assert.Empty(ExperimentalBladePort.GetExperimentalChargeLimits(excluded));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetChargeLimitAsync(50, CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Blade16StaleGetConfirmationNeverReplaysSetter()
    {
        ExperimentalBladeModel model = Model(0x029F);
        var session = new BatterySession { Raw = 0x50 };
        session.PostSetterReads.Enqueue(0x50);
        session.PostSetterReads.Enqueue(0x50);
        session.PostSetterReads.Enqueue(0xE3);
        var policy = new ExperimentalFeaturesPolicy(model);
        int delays = 0;
        await using var port = Create(model, session, policy, (delay, token) =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(250), delay);
            token.ThrowIfCancellationRequested();
            delays++;
            return Task.CompletedTask;
        });

        await port.SetChargeLimitAsync(99, CancellationToken.None);

        Assert.Equal(2, delays);
        Assert.Single(session.Commands, command => command.CommandId == 0x12);
        Assert.Equal(4, session.Commands.Count(command => command.CommandId == 0x92));
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(0x029D)]
    [InlineData(0x02C5)]
    [InlineData(0x02A0)]
    public async Task WrongIndependentReadbackPausesWithoutRetryOrSetterReplay(ushort productId)
    {
        ExperimentalBladeModel model = Model(productId);
        var session = new BatterySession { Raw = 0x50, IgnoreSetter = true };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(model, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetChargeLimitAsync(99, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId == 0x12);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x92));
    }

    [Fact]
    public async Task CancellationAtSetterSubmissionPausesWithoutConfirmationOrReplay()
    {
        ExperimentalBladeModel model = Model(0x029D);
        using var cancellation = new CancellationTokenSource();
        var session = new BatterySession
        {
            Raw = 0x50,
            CancelAtSetterSubmission = cancellation,
        };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(model, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetChargeLimitAsync(99, cancellation.Token));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal([0x92, 0x12], session.Commands.Select(command => command.CommandId));
        Assert.Single(session.Commands, command => command.CommandId == 0x12);
    }

    [Fact]
    public async Task NativeAdmissionIsFreshAtSetterSubmission()
    {
        ExperimentalBladeModel model = Model(0x02C5);
        var session = new BatterySession { Raw = 0x50 };
        var policy = new ExperimentalFeaturesPolicy(model);
        int admissionChecks = 0;
        await using var port = new ExperimentalBladePort(session, new(), model, policy,
            () => ++admissionChecks < 3, () => PowerSource.ExternalUnknown);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetChargeLimitAsync(99, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.True(policy.WritesAllowed);
        Assert.Single(session.Commands);
        Assert.Equal(0x92, session.Commands[0].CommandId);
        Assert.Equal(3, admissionChecks);
    }

    private static ExperimentalBladeModel Model(ushort productId) =>
        ExperimentalBladeModels.All.Single(model => model.ProductId == productId);

    private static ExperimentalBladePort Create(
        ExperimentalBladeModel model,
        BatterySession session,
        ExperimentalFeaturesPolicy policy,
        Func<TimeSpan, CancellationToken, Task>? confirmationDelay = null) =>
        new(session, new(), model, policy, () => true, () => PowerSource.ExternalUnknown,
            confirmationDelay);

    private sealed class BatterySession : IRazerCommandSession
    {
        internal byte Raw { get; set; } = 0x50;
        internal bool IgnoreSetter { get; init; }
        internal CancellationTokenSource? CancelAtSetterSubmission { get; init; }
        internal Queue<byte> PostSetterReads { get; } = new();
        internal List<RazerCommandSpec> Commands { get; } = [];
        private bool setterObserved;

        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assign,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                    default, HidSessionFailureKind.IdentityBlocked)
                {
                    Diagnostic = new(HidSessionFailurePhase.Guard,
                        HidSessionFailureKind.IdentityBlocked, null, null),
                });
            }

            Assert.Equal(0x07, command.ClassId);
            Commands.Add(command);
            _ = assign();
            if (command.CommandId == 0x92)
            {
                byte current = setterObserved && PostSetterReads.TryDequeue(out byte scripted)
                    ? scripted
                    : Raw;
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                    new byte[] { current }, null));
            }

            if (command.CommandId == 0x8F)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(new byte[] { 0 }, null));
            }
            Assert.Equal(0x12, command.CommandId);
            setterObserved = true;
            if (CancelAtSetterSubmission is not null)
            {
                CancelAtSetterSubmission.Cancel();
                throw new OperationCanceledException(cancellationToken);
            }

            byte value = command.Arguments.Span[0];
            if (!IgnoreSetter)
            {
                Raw = value;
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                new byte[] { value }, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
