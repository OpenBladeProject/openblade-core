using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProductionBatteryPortTests
{
    [Fact]
    public async Task EmptyCatalog_RemainsProtectionReadableAndRejectsEverySetter()
    {
        var session = new ScriptedSession(
            Success([BatteryValidationProtocol.Encode(BatteryValidationSetting.Limit80)]));
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        Assert.Empty(port.Capabilities.ChargeLimits);
        Assert.False(port.Capabilities.SupportsBatteryProtectionOff);
        Assert.False(port.Capabilities.SupportsTemporaryFullCharge);
        Assert.Equal(new ConfirmedBatteryState(true, 80, null), await port.QueryProtectionAsync(default));
        Assert.Null(await port.QueryTemporaryFullChargeAsync(default));

        BladeWriteException limit = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(80, default));
        BladeWriteException protection = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetProtectionOffAsync(default));
        BladeWriteException temporary = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetTemporaryFullChargeAsync(true, default));

        Assert.All(
            [limit, protection, temporary],
            error => Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState));
        Assert.Single(session.Commands);
        Assert.Equal((0x07, 0x92), (session.Commands[0].ClassId, session.Commands[0].CommandId));
    }

    [Theory]
    [InlineData(50, "battery-limit-50", 0xB2)]
    [InlineData(55, "battery-limit-55", 0xB7)]
    [InlineData(60, "battery-limit-60", 0xBC)]
    [InlineData(65, "battery-limit-65", 0xC1)]
    [InlineData(70, "battery-limit-70", 0xC6)]
    [InlineData(75, "battery-limit-75", 0xCB)]
    [InlineData(80, "battery-limit-80", 0xD0)]
    public async Task AdmittedLimitCatalog_ExposesAndResolvesExactTrustedAction(
        int limit,
        string action,
        int payload)
    {
        DeviceCommandCatalog catalog = Catalog(
            Fixture(action, 0x07, 0x12, [(byte)payload]));
        var session = new ScriptedSession(Success([(byte)payload]));
        await using var port = CreatePort(session, catalog);

        Assert.Equal([limit], port.Capabilities.ChargeLimits);

        await port.SetChargeLimitAsync(limit, default);

        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal((0x07, 0x12), (command.ClassId, command.CommandId));
        Assert.Equal([(byte)payload], command.Arguments.ToArray());
    }

    [Fact]
    public async Task ProtectionOff_RequiresBothTrustedActionAndTemporaryReadEvidence()
    {
        DeviceCommandCatalog catalog = Catalog(
            Fixture(ProductionBatteryPort.Limit80Action, 0x07, 0x12, [0xD0]),
            Fixture(ProductionBatteryPort.ProtectionOffAction, 0x07, 0x12, [0x50]));
        var withoutEvidenceSession = new ScriptedSession();
        await using var withoutEvidence = CreatePort(withoutEvidenceSession, catalog);

        Assert.False(withoutEvidence.Capabilities.SupportsBatteryProtectionOff);
        await Assert.ThrowsAsync<BladeWriteException>(
            () => withoutEvidence.SetProtectionOffAsync(default));
        Assert.Empty(withoutEvidenceSession.Commands);

        var withEvidenceSession = new ScriptedSession(Success([0x00]), Success([0x50]));
        await using var withEvidence = CreatePort(
            withEvidenceSession,
            catalog,
            AcceptedTemporaryReadEvidence());

        Assert.True(withEvidence.Capabilities.SupportsBatteryProtectionOff);
        Assert.False(await withEvidence.QueryTemporaryFullChargeAsync(default));
        await withEvidence.SetProtectionOffAsync(default);
        Assert.Equal([(0x07, 0x8F), (0x07, 0x12)], withEvidenceSession.Commands
            .Select(command => (command.ClassId, command.CommandId))
            .ToArray());
    }

    [Theory]
    [InlineData(50)]
    [InlineData(55)]
    [InlineData(60)]
    [InlineData(65)]
    [InlineData(70)]
    [InlineData(75)]
    [InlineData(80)]
    public async Task MissingAdmittedLimitActionRejectsWithoutSubmission(int limit)
    {
        var session = new ScriptedSession();
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(limit, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Empty(session.Commands);
        Assert.Empty(port.Capabilities.ChargeLimits);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(85)]
    public async Task InvalidLimitRejectsWithoutSubmissionEvenIfNamedActionIsInjected(int limit)
    {
        var session = new ScriptedSession();
        await using var port = CreatePort(
            session,
            Catalog(Fixture($"battery-limit-{limit}", 0x07, 0x12, [0x01])));

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetChargeLimitAsync(limit, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Empty(session.Commands);
        Assert.Empty(port.Capabilities.ChargeLimits);
    }

    [Theory]
    [InlineData(false, 0x00)]
    [InlineData(true, 0x04)]
    public async Task AdmittedTemporarySetterResolvesExactTrustedAction(
        bool enabled,
        byte payload)
    {
        DeviceCommandCatalog catalog = Catalog(
            Fixture(ProductionBatteryPort.TemporaryEnableAction, 0x07, 0x0F, [0x04]),
            Fixture(ProductionBatteryPort.TemporaryCancelAction, 0x07, 0x0F, [0x00]));
        var session = new ScriptedSession(Success([payload]));
        await using var port = CreatePort(
            session,
            catalog,
            AcceptedTemporaryReadEvidence());

        Assert.True(port.Capabilities.SupportsTemporaryFullCharge);
        await port.SetTemporaryFullChargeAsync(enabled, default);

        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal((0x07, 0x0F), (command.ClassId, command.CommandId));
        Assert.Equal([payload], command.Arguments.ToArray());
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task TemporarySetterRequiresReadEvidenceAndBothDirections(
        bool hasReadEvidence,
        bool hasEnable,
        bool hasCancel)
    {
        var fixtures = new List<CapturedCommandFixture>();
        if (hasEnable)
        {
            fixtures.Add(Fixture(
                ProductionBatteryPort.TemporaryEnableAction,
                0x07,
                0x0F,
                [0x04]));
        }

        if (hasCancel)
        {
            fixtures.Add(Fixture(
                ProductionBatteryPort.TemporaryCancelAction,
                0x07,
                0x0F,
                [0x00]));
        }

        var session = new ScriptedSession();
        await using var port = CreatePort(
            session,
            Catalog(fixtures.ToArray()),
            hasReadEvidence ? AcceptedTemporaryReadEvidence() : NoTemporaryReadEvidence());

        Assert.False(port.Capabilities.SupportsTemporaryFullCharge);
        foreach (bool enabled in new[] { false, true })
        {
            BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
                () => port.SetTemporaryFullChargeAsync(enabled, default));
            Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        }

        Assert.Empty(session.Commands);
    }

    [Fact]
    public void PublicBoundaryContainsNoRawPayloadPathOrSettingSurface()
    {
        string[] memberNames = typeof(IProductionBatteryPort)
            .GetMembers()
            .Select(member => member.Name)
            .ToArray();
        Type[] parameterTypes = typeof(IProductionBatteryPort)
            .GetMethods()
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(memberNames, name =>
            name.Contains("Raw", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Path", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Setting", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parameterTypes, type =>
            type == typeof(byte[])
            || type == typeof(ReadOnlyMemory<byte>)
            || type == typeof(BatteryValidationSetting));
        Assert.DoesNotContain(
            typeof(IProductionBatteryPort).GetMethods(),
            method => method.ReturnType.ToString().Contains(
                nameof(BatteryValidationSetting),
                StringComparison.Ordinal));
    }

    private static ProductionBatteryPort CreatePort(
        IRazerCommandSession session,
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence? evidence = null) => new(
            session,
            catalog,
            evidence ?? NoTemporaryReadEvidence(),
            new ValidationRunContext(),
            TimeSpan.FromSeconds(1));

    private static DeviceCommandCatalog Catalog(params CapturedCommandFixture[] fixtures) =>
        DeviceCommandCatalog.Create(CapturedFixtureFile.TargetDevice, fixtures);

    private static ProductionBatteryReadEvidence AcceptedTemporaryReadEvidence() => new(
        new HashSet<ProductionBatteryQueryCapability>
        {
            ProductionBatteryQueryCapability.TemporaryFullCharge,
        });

    private static ProductionBatteryReadEvidence NoTemporaryReadEvidence() => new(
        new HashSet<ProductionBatteryQueryCapability>());

    private static CapturedCommandFixture Fixture(
        string action,
        byte commandClass,
        byte commandId,
        byte[] payload) => new(
            CapturedFixtureFile.TargetDevice,
            action,
            commandClass,
            commandId,
            payload,
            Captured: true,
            Decoded: true,
            Applied: true,
            ReadBack: true,
            Restored: true);

    private static HidSessionResult<ReadOnlyMemory<byte>> Success(byte[] payload) =>
        new(payload, null);

    internal sealed class ScriptedSession(
        params HidSessionResult<ReadOnlyMemory<byte>>[] results) : IRazerCommandSession
    {
        private readonly Queue<HidSessionResult<ReadOnlyMemory<byte>>> results = new(results);

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public List<RazerCommandSpec> Commands { get; } = [];

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = assignTransactionId();
            Commands.Add(command);
            return Task.FromResult(results.Count == 0
                ? new HidSessionResult<ReadOnlyMemory<byte>>(
                    default,
                    HidSessionFailureKind.NativeIoFailure)
                : results.Dequeue());
        }

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }
    }
}
