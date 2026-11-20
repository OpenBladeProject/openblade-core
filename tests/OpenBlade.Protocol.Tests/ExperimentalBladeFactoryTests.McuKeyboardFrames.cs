using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeFactoryTests
{
    // The four 2026-11-19 MCU frame reviews and 02C7 load the lighting baseline
    // journal, so a pending record from an earlier run reaches the port as a
    // fault. Other frame backends keep their own restoration semantics.
    [Theory]
    [InlineData(0x0245, true)]
    [InlineData(0x028A, true)]
    [InlineData(0x028B, true)]
    [InlineData(0x028C, true)]
    [InlineData(0x02C7, true)]
    [InlineData(0x0255, false)]
    public async Task LightingAdapter_AttachesThePendingBaselineJournalToReviewedFramePorts(int productId, bool attached)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        DirectoryInfo directory = Directory.CreateTempSubdirectory("openblade-factory-lighting-journal-");
        try
        {
            var journal = new ExperimentalLightingBaselineJournal(directory.FullName, model.ModelNumber,
                model.ProductId, _ => { });
            journal.SavePending(new(LightingSettingsFactory.BreathingSingle(new(11, 22, 33), 51), 129));
            var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName),
                new Native(1) { ProductId = model.ProductId }, new Guard(), new Io(),
                () => PowerSource.ExternalUnknown, lightingJournal: journal);

            HidSessionResult<IProductionLightingPort> result =
                await factory.CreateLightingFactory().OpenAsync(CancellationToken.None);

            Assert.Null(result.Failure);
            await using ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>(result.Value);
            Assert.Equal(attached ? LightingRendererOwnershipState.Faulted : LightingRendererOwnershipState.None,
                port.OwnershipState);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
