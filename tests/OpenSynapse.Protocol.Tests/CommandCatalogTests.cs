using OpenSynapse.Protocol;

namespace OpenSynapse.Protocol.Tests;

public sealed class CommandCatalogTests
{
    private static readonly DeviceIdentity Target = new(
        Model: "RZ09-0581",
        VendorId: 0x1532,
        ProductId: 0x02E0,
        BiosVersion: "3.01");

    [Fact]
    public void Create_RejectsFixtureWithoutEveryValidationGate()
    {
        CapturedCommandFixture fixture = CreateFixture() with { Restored = false };

        Assert.Throws<UnvalidatedCommandException>(() =>
            DeviceCommandCatalog.Create(Target, [fixture]));
    }

    [Fact]
    public void Create_RejectsFixtureForDifferentProductId()
    {
        CapturedCommandFixture fixture = CreateFixture() with
        {
            Device = Target with { ProductId = 0x029F },
        };

        Assert.Throws<UnvalidatedCommandException>(() =>
            DeviceCommandCatalog.Create(Target, [fixture]));
    }

    [Fact]
    public void Create_AdmitsFullyValidatedFixtureForExactDevice()
    {
        DeviceCommandCatalog catalog = DeviceCommandCatalog.Create(Target, [CreateFixture()]);

        Assert.True(catalog.TryGet("performance.balanced", out RazerCommandDefinition? command));
        Assert.NotNull(command);
        Assert.Equal(0x0D, command.CommandClass);
        Assert.Equal(0x02, command.CommandId);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00, 0x00 }, command.Payload);
    }

    private static CapturedCommandFixture CreateFixture() => new(
        Device: Target,
        Action: "performance.balanced",
        CommandClass: 0x0D,
        CommandId: 0x02,
        Payload: [0x01, 0x01, 0x00, 0x00],
        Captured: true,
        Decoded: true,
        Applied: true,
        ReadBack: true,
        Restored: true);
}
