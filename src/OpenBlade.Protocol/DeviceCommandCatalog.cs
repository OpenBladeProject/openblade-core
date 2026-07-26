using System.Collections.Immutable;

namespace OpenBlade.Protocol;

internal sealed record DeviceIdentity(
    string Model,
    ushort VendorId,
    ushort ProductId,
    string BiosVersion);

internal sealed record CapturedCommandFixture(
    DeviceIdentity Device,
    string Action,
    byte CommandClass,
    byte CommandId,
    byte[] Payload,
    bool Captured,
    bool Decoded,
    bool Applied,
    bool ReadBack,
    bool Restored)
{
    /// <summary>
    /// Marks a command that has a validated response and interactive restoration,
    /// but no independent firmware query. These commands may only be exposed as
    /// explicit one-shot actions and must never be treated as confirmed state.
    /// </summary>
    public bool OneShotOnly { get; init; }

    public byte[]? ExpectedResponsePayload { get; init; }
}

internal sealed record RazerCommandDefinition
{
    public RazerCommandDefinition(
        byte commandClass,
        byte commandId,
        IEnumerable<byte> payload,
        IEnumerable<byte>? expectedResponsePayload = null,
        bool oneShotOnly = false)
    {
        CommandClass = commandClass;
        CommandId = commandId;
        Payload = payload.ToImmutableArray();
        ExpectedResponsePayload = (expectedResponsePayload ?? payload).ToImmutableArray();
        OneShotOnly = oneShotOnly;
    }

    public byte CommandClass { get; }

    public byte CommandId { get; }

    public ImmutableArray<byte> Payload { get; }

    public ImmutableArray<byte> ExpectedResponsePayload { get; }

    public bool OneShotOnly { get; }
}

public sealed class UnvalidatedCommandException : Exception
{
    public UnvalidatedCommandException(string message)
        : base(message)
    {
    }
}

internal sealed class DeviceCommandCatalog
{
    private readonly IReadOnlyDictionary<string, RazerCommandDefinition> commandDefinitions;

    private DeviceCommandCatalog(IReadOnlyDictionary<string, RazerCommandDefinition> commands)
    {
        commandDefinitions = commands;
    }

    public static DeviceCommandCatalog Empty { get; } = new(
        new Dictionary<string, RazerCommandDefinition>(StringComparer.Ordinal));

    public static DeviceCommandCatalog Create(
        DeviceIdentity target,
        IEnumerable<CapturedCommandFixture> fixtures)
    {
        Dictionary<string, RazerCommandDefinition> commands = new(StringComparer.Ordinal);

        foreach (CapturedCommandFixture fixture in fixtures)
        {
            if (fixture is null)
            {
                throw new UnvalidatedCommandException(
                    "The captured command set contains a null entry.");
            }

            if (string.IsNullOrWhiteSpace(fixture.Action))
            {
                throw new UnvalidatedCommandException(
                    "A captured command has a null or blank action.");
            }

            bool independentlyReadable = fixture.ReadBack && !fixture.OneShotOnly;
            bool validatedOneShot = !fixture.ReadBack
                && fixture.OneShotOnly
                && fixture.ExpectedResponsePayload is not null;
            bool allGatesPassed = fixture.Captured
                && fixture.Decoded
                && fixture.Applied
                && (independentlyReadable || validatedOneShot)
                && fixture.Restored;

            if (fixture.Device != target || !allGatesPassed)
            {
                throw new UnvalidatedCommandException(
                    $"Command '{fixture.Action}' has not passed every validation gate for the exact target device.");
            }

            if (fixture.Payload is null)
            {
                throw new UnvalidatedCommandException(
                    $"Command '{fixture.Action}' has a null payload.");
            }

            if (fixture.Payload.Length > RazerReport.MaximumPayloadLength)
            {
                throw new UnvalidatedCommandException(
                    $"Command '{fixture.Action}' has an oversized payload.");
            }

            byte[] expectedResponse = fixture.ExpectedResponsePayload ?? fixture.Payload;
            if (expectedResponse.Length > RazerReport.MaximumPayloadLength)
            {
                throw new UnvalidatedCommandException(
                    $"Command '{fixture.Action}' has an oversized expected response payload.");
            }

            if (!commands.TryAdd(
                    fixture.Action,
                    new RazerCommandDefinition(
                        fixture.CommandClass,
                        fixture.CommandId,
                        fixture.Payload,
                        expectedResponse,
                        fixture.OneShotOnly)))
            {
                throw new UnvalidatedCommandException(
                    $"Command action '{fixture.Action}' is duplicated.");
            }
        }

        return new DeviceCommandCatalog(commands);
    }

    public bool TryGet(string action, out RazerCommandDefinition? command) =>
        commandDefinitions.TryGetValue(action, out command);
}
