namespace OpenSynapse.Protocol;

public sealed record DeviceIdentity(
    string Model,
    ushort VendorId,
    ushort ProductId,
    string BiosVersion);

public sealed record CapturedCommandFixture(
    DeviceIdentity Device,
    string Action,
    byte CommandClass,
    byte CommandId,
    byte[] Payload,
    bool Captured,
    bool Decoded,
    bool Applied,
    bool ReadBack,
    bool Restored);

public sealed record RazerCommandDefinition(
    byte CommandClass,
    byte CommandId,
    byte[] Payload);

public sealed class UnvalidatedCommandException : Exception
{
    public UnvalidatedCommandException(string message)
        : base(message)
    {
    }
}

public sealed class DeviceCommandCatalog
{
    private readonly IReadOnlyDictionary<string, RazerCommandDefinition> _commands;

    private DeviceCommandCatalog(IReadOnlyDictionary<string, RazerCommandDefinition> commands)
    {
        _commands = commands;
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
            bool allGatesPassed = fixture.Captured
                && fixture.Decoded
                && fixture.Applied
                && fixture.ReadBack
                && fixture.Restored;

            if (fixture.Device != target || !allGatesPassed)
            {
                throw new UnvalidatedCommandException(
                    $"Command '{fixture.Action}' has not passed every validation gate for the exact target device.");
            }

            if (fixture.Payload.Length > RazerReport.MaximumPayloadLength)
            {
                throw new UnvalidatedCommandException(
                    $"Command '{fixture.Action}' has an oversized payload.");
            }

            commands.Add(
                fixture.Action,
                new RazerCommandDefinition(
                    fixture.CommandClass,
                    fixture.CommandId,
                    fixture.Payload.ToArray()));
        }

        return new DeviceCommandCatalog(commands);
    }

    public bool TryGet(string action, out RazerCommandDefinition? command) =>
        _commands.TryGetValue(action, out command);
}
