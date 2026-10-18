using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(16)]
    public async Task Blade18RegionalLayout_ReadsExactEditionGetterWithoutAnySetter(byte layout)
    {
        var session = new Session { EditionReply = [layout, 128, 42] };
        var policy = Enabled();
        await using var port = Create(session, policy);
        await port.InitializeBlade18KeyboardLayoutAsync(CancellationToken.None);
        Assert.Equal(layout, port.Capabilities.Blade18KeyboardLayoutId);
        RazerCommandSpec query = Assert.Single(session.Commands);
        Assert.Equal((byte)0, query.ClassId);
        Assert.Equal((byte)0x86, query.CommandId);
        Assert.Equal(3, query.ExpectedPayloadLength);
        Assert.NotNull(query.PayloadValidator);
        Assert.Null(query.SubmissionAllowed);
        Assert.True(policy.WritesAllowed);
        Assert.Contains(policy.Snapshot().Observations,
            observation => observation.Operation == "KeyboardLayout" && observation.Outcome == "Confirmed");
    }

    [Theory]
    [InlineData(5, 3, false)]
    [InlineData(11, 3, false)]
    [InlineData(255, 3, false)]
    [InlineData(12, 2, false)]
    [InlineData(12, 4, false)]
    [InlineData(12, 3, true)]
    public async Task Blade18RegionalLayout_UnknownMalformedOrUnavailableReadKeepsUnrelatedControls(byte layout, int length, bool fail)
    {
        byte[] reply = new byte[length];
        reply[0] = layout;
        var session = new Session { EditionReply = reply, FailEditionRead = fail };
        var policy = Enabled();
        await using var port = Create(session, policy);
        await port.InitializeBlade18KeyboardLayoutAsync(CancellationToken.None);
        Assert.Null(port.Capabilities.Blade18KeyboardLayoutId);
        Assert.Single(session.Commands);
        Assert.True(policy.WritesAllowed);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Fact]
    public async Task Blade18RegionalLayout_DoesNotSwallowCancellationAsUnavailableMetadata()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session { CancelEditionRead = cancellation };
        await using var port = Create(session, Enabled());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            port.InitializeBlade18KeyboardLayoutAsync(cancellation.Token));
        Assert.Null(port.Capabilities.Blade18KeyboardLayoutId);
    }

    [Fact]
    public async Task Blade18RegionalLayout_DoesNotProbeAnotherModel()
    {
        var session = new Session();
        ExperimentalBladeModel other = ModelFor(0x029F);
        await using var port = Create(session, new ExperimentalFeaturesPolicy(other), other);
        await port.InitializeBlade18KeyboardLayoutAsync(CancellationToken.None);
        Assert.Empty(session.Commands);
        Assert.Null(port.Capabilities.Blade18KeyboardLayoutId);
    }
}
