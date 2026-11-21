using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeFactoryTests
{
    // A pending record also marks a possible custom selection, which the
    // legacy-setter reviews clear with 03/0A on release.
    // The four 2026-11-19 MCU frame reviews and 02C7 load the lighting baseline
    // journal, so a pending record from an earlier run reaches the port as a
    // fault. Other frame backends keep their own restoration semantics.
    [Theory]
    [InlineData(0x0245, true, true)]
    [InlineData(0x028A, true, true)]
    [InlineData(0x028B, true, true)]
    [InlineData(0x028C, true, true)]
    [InlineData(0x02C7, true, false)]
    [InlineData(0x0255, false, false)]
    public async Task LightingAdapter_AttachesThePendingBaselineJournalToReviewedFramePorts(int productId, bool attached, bool customFrameMayRemain)
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
            // Only the legacy-setter reviews can leave a [0,5,8] selection behind.
            Assert.Equal(customFrameMayRemain, factory.LightingOwnership.McuCustomFrameMayRemain);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // Without a pending record there is no earlier selection to clear.
    [Theory]
    [InlineData(0x0245)]
    [InlineData(0x028A)]
    [InlineData(0x028B)]
    [InlineData(0x028C)]
    public async Task LightingAdapter_EmptyJournalLeavesNoPossibleCustomSelection(int productId)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        DirectoryInfo directory = Directory.CreateTempSubdirectory("openblade-factory-lighting-journal-");
        try
        {
            var journal = new ExperimentalLightingBaselineJournal(directory.FullName, model.ModelNumber,
                model.ProductId, _ => { });
            var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName),
                new Native(1) { ProductId = model.ProductId }, new Guard(), new Io(),
                () => PowerSource.ExternalUnknown, lightingJournal: journal);

            HidSessionResult<IProductionLightingPort> result =
                await factory.CreateLightingFactory().OpenAsync(CancellationToken.None);

            Assert.Null(result.Failure);
            await using ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>(result.Value);
            Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
            Assert.False(factory.LightingOwnership.McuCustomFrameMayRemain);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
