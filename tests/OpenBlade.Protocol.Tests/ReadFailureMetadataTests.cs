using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ReadFailureMetadataTests
{
    [Theory]
    [InlineData(false, HidSessionFailureKind.NativeIoFailure)]
    [InlineData(true, HidSessionFailureKind.NativeIoFailure)]
    [InlineData(false, HidSessionFailureKind.Timeout)]
    [InlineData(true, HidSessionFailureKind.Timeout)]
    [InlineData(false, HidSessionFailureKind.ProtocolFailure)]
    [InlineData(true, HidSessionFailureKind.ProtocolFailure)]
    public async Task ProductionBatteryRead_PreservesFailureEvidenceThroughClientConversions(
        bool temporaryFullCharge, HidSessionFailureKind failure)
    {
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HidSessionFailureDiagnostic diagnostic = Diagnostic(failure);
        var session = new FailureSession(new(default, failure)
        {
            Diagnostic = diagnostic,
            NativeRetirement = retirement.Task,
        });
        await using var port = CreateProductionPort(session);

        Exception? error = await Record.ExceptionAsync(async () =>
        {
            if (temporaryFullCharge)
            {
                await port.QueryTemporaryFullChargeAsync(default);
            }
            else
            {
                await port.QueryProtectionAsync(default);
            }
        });

        DeviceStateQuery query = temporaryFullCharge
            ? DeviceStateQuery.TemporaryFullCharge : DeviceStateQuery.BatteryProtection;
        switch (failure)
        {
            case HidSessionFailureKind.Timeout:
                var timeout = Assert.IsType<NativeDeviceReadTimeoutException>(error);
                Assert.IsAssignableFrom<TimeoutException>(timeout);
                Assert.Equal(failure, timeout.Failure);
                Assert.Equal(query, timeout.StateQuery);
                Assert.Equal(diagnostic, timeout.Diagnostic);
                Assert.Same(retirement.Task, timeout.NativeRetirement);
                break;
            case HidSessionFailureKind.ProtocolFailure:
                var protocol = Assert.IsType<RazerProtocolException>(error);
                Assert.Equal(HidProtocolValidationFailure.Checksum, protocol.ValidationFailure);
                Assert.Equal(query, protocol.StateQuery);
                Assert.Equal(diagnostic, protocol.Diagnostic);
                Assert.Same(retirement.Task, protocol.NativeRetirement);
                Assert.False(DeviceStateContentRejection.IsContentRejection(protocol));
                Assert.Null(protocol.RejectedPayloadHex);
                break;
            default:
                var native = Assert.IsType<NativeDeviceReadException>(error);
                Assert.Equal(failure, native.Failure);
                Assert.Equal(query, native.StateQuery);
                Assert.Equal(diagnostic, native.Diagnostic);
                Assert.Same(retirement.Task, native.NativeRetirement);
                break;
        }

        await Assert.ThrowsAsync<IOException>(() => port.QueryProtectionAsync(default));
        Assert.Equal(1, session.Calls);
        await port.DisposeAsync();
        Assert.False(retirement.Task.IsCompleted);
        retirement.SetResult();
    }

    [Theory]
    [InlineData(HidSessionFailureKind.NativeIoFailure)]
    [InlineData(HidSessionFailureKind.Timeout)]
    [InlineData(HidSessionFailureKind.ProtocolFailure)]
    public async Task ExperimentalBatteryRead_PreservesEvidenceWithoutChangingWritePolicy(
        HidSessionFailureKind failure)
    {
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HidSessionFailureDiagnostic diagnostic = Diagnostic(failure);
        var session = new FailureSession(new(default, failure)
        {
            Diagnostic = diagnostic,
            NativeRetirement = retirement.Task,
        });
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = new ExperimentalBladePort(session, new(), model, policy,
            () => true, () => PowerSource.ExternalUnknown);

        NativeDeviceReadException error = await Assert.ThrowsAsync<NativeDeviceReadException>(
            () => port.QueryProtectionAsync(default));

        Assert.Equal(failure, error.Failure);
        Assert.Equal(DeviceStateQuery.BatteryProtection, error.StateQuery);
        Assert.Equal(diagnostic, error.Diagnostic);
        Assert.Same(retirement.Task, error.NativeRetirement);
        Assert.True(policy.WritesAllowed);
        Assert.Equal(1, session.Calls);
        await port.DisposeAsync();
        Assert.False(retirement.Task.IsCompleted);
        retirement.SetResult();
    }

    [Fact]
    public async Task AdapterRead_PreservesNativeRetirement()
    {
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HidSessionFailureDiagnostic diagnostic = Diagnostic(HidSessionFailureKind.Timeout);
        var session = new FailureSession(new(default, HidSessionFailureKind.Timeout)
        {
            Diagnostic = diagnostic,
            NativeRetirement = retirement.Task,
        });
        var client = new AdapterPowerCommandClient(session, TimeSpan.FromSeconds(1));

        HidSessionResult<AdapterPowerState> result = await client.QueryAsync(1, default);

        Assert.Equal(HidSessionFailureKind.Timeout, result.Failure);
        Assert.Equal(diagnostic, result.Diagnostic);
        Assert.Same(retirement.Task, result.NativeRetirement);
        Assert.False(retirement.Task.IsCompleted);
        retirement.SetResult();
    }

    [Theory]
    [InlineData(0x029F, false)]
    [InlineData(0x029F, true)]
    [InlineData(0x02A0, false)]
    [InlineData(0x02A0, true)]
    public async Task ExperimentalFanTargetFailure_IdentifiesThermalDependencyWithoutWrites(
        int productId, bool secondTarget)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        byte profile = productId == 0x02A0 ? (byte)1 : (byte)0;
        HidSessionFailureDiagnostic diagnostic = Diagnostic(HidSessionFailureKind.ProtocolFailure);
        var results = new List<HidSessionResult<ReadOnlyMemory<byte>>>
        {
            new(new byte[] { 0, 1, 0, 1 }, null),
            new(new byte[] { 0, 2, 0, 1 }, null),
        };
        if (secondTarget)
        {
            results.Add(new(new byte[] { profile, 1, 30 }, null));
        }
        results.Add(new(default, HidSessionFailureKind.ProtocolFailure) { Diagnostic = diagnostic });
        var session = new ProductionBatteryPortTests.ScriptedSession(results.ToArray());
        await using var port = new ExperimentalBladePort(session, new(), model,
            new ExperimentalFeaturesPolicy(model), () => true, () => PowerSource.ExternalUnknown);

        NativeDeviceReadException error = await Assert.ThrowsAsync<NativeDeviceReadException>(
            () => port.QueryPerformanceFanStateAsync(default));

        Assert.Equal(DeviceStateQuery.PerformanceAndFans, error.StateQuery);
        Assert.Equal(diagnostic, error.Diagnostic);
        Assert.Equal(secondTarget ? 4 : 3, session.Commands.Count);
        Assert.All(session.Commands, command => Assert.True(command.CommandId is 0x82 or 0x81));
        Assert.Equal(new byte[] { profile, secondTarget ? (byte)2 : (byte)1, 0 },
            session.Commands[^1].Arguments.ToArray());
    }

    [Theory]
    [InlineData(HidSessionFailureKind.Canceled)]
    [InlineData(HidSessionFailureKind.QueuedCanceled)]
    public async Task ProductionBatteryRead_PreservesCallerCancellation(HidSessionFailureKind failure)
    {
        using var cancellation = new CancellationTokenSource();
        var session = new FailureSession(new(default, failure), cancellation.Cancel);
        await using var port = CreateProductionPort(session);

        OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(
            () => port.QueryProtectionAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, session.Calls);
    }

    [Fact]
    public async Task ProductionBatteryRead_UnclassifiedProtocolFailureHasNoExchangeEvidence()
    {
        var session = new FailureSession(new(default, HidSessionFailureKind.ProtocolFailure));
        await using var port = CreateProductionPort(session);

        RazerProtocolException error = await Assert.ThrowsAsync<RazerProtocolException>(
            () => port.QueryProtectionAsync(default));

        Assert.Null(error.Diagnostic);
        Assert.Null(error.NativeRetirement);
        Assert.Null(error.RejectedPayloadHex);
        Assert.False(DeviceStateContentRejection.IsContentRejection(error));
    }

    private static HidSessionFailureDiagnostic Diagnostic(HidSessionFailureKind failure) =>
        new(failure == HidSessionFailureKind.ProtocolFailure
            ? HidSessionFailurePhase.ProtocolValidation : HidSessionFailurePhase.GetCompletion,
            failure, 31, 90)
        {
            ProtocolValidationFailure = failure == HidSessionFailureKind.ProtocolFailure
                ? HidProtocolValidationFailure.Checksum : null,
        };

    private static ProductionBatteryPort CreateProductionPort(IRazerCommandSession session) =>
        new(session, DeviceCommandCatalog.Empty,
            new ProductionBatteryReadEvidence(new HashSet<ProductionBatteryQueryCapability>
            {
                ProductionBatteryQueryCapability.TemporaryFullCharge,
            }), new ValidationRunContext(), TimeSpan.FromSeconds(1));

    private sealed class FailureSession(
        HidSessionResult<ReadOnlyMemory<byte>> failure,
        Action? beforeResult = null) : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        internal int Calls { get; private set; }

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            Calls++;
            beforeResult?.Invoke();
            return Task.FromResult(failure);
        }

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }
    }
}
