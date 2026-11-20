using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeFactoryTests
{
    // The optional LED38 effect GET runs once per admitted identity. A rejection
    // poisons that session, so the factory drains it and opens one fresh session
    // without the probe; later opens reuse the failure and keep frames keyboard-only.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090529VaporChamberProbe_RunsOnceAndRejectionReopensWithoutIt(bool rejected)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02C7);
        DirectoryInfo directory = Directory.CreateTempSubdirectory("openblade-0529-probe-");
        try
        {
            var native = new Native(1) { ProductId = model.ProductId };
            var io = new Io
            {
                Events = native.Events,
                StatusFailure = rejected ? (0x0F, 0x82) : null,
                StatusFailureRegion = 38,
                StatusFailuresRemaining = rejected ? 1 : 0,
            };
            var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
                new Guard(), io, () => PowerSource.ExternalUnknown,
                lightingJournal: new(directory.FullName, model.ModelNumber, model.ProductId, _ => { }));

            HidSessionResult<IProductionBatteryPort> first = await factory.OpenAsync(CancellationToken.None);
            Assert.Null(first.Failure);
            await using (Assert.IsType<ExperimentalBladePort>(first.Value)) { }
            Assert.Equal(1, VaporChamberQueries(io));
            Assert.Equal(rejected ? 2 : 1, native.BoundCount);
            Assert.Equal(0, io.StatusFailuresRemaining);
            if (rejected)
            {
                int closed = native.Events.IndexOf("close:500");
                Assert.True(closed >= 0 && closed < native.Events.IndexOf("open:501"));
            }

            HidSessionResult<IProductionBatteryPort> second = await factory.OpenAsync(CancellationToken.None);
            Assert.Null(second.Failure);
            await using (Assert.IsType<ExperimentalBladePort>(second.Value)) { }
            Assert.Equal(1, VaporChamberQueries(io));
            Assert.All(io.Commands, command => Assert.True(command >= 0x80));
        }
        finally
        {
            directory.Delete(recursive: true);
        }

        static int VaporChamberQueries(Io io) =>
            io.Requests.Count(request => request is (0x0F, 0x82, 38));
    }
}
