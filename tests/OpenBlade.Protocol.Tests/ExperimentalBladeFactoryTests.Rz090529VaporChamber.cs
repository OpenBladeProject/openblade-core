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

    // A probe refused at the shared command gate, or canceled while it waits there,
    // never reached the MCU. It settles nothing, so the next open probes again; as
    // for SharedGateAdmissionTimeout_PreservesResourceFailureInsteadOfRejectingIdentity,
    // gate contention is not evidence about the device. Cancellation returns Canceled
    // exactly like the Blade 18 layout probe.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090529VaporChamberProbe_SharedGateRefusalOrCancellationSettlesNothing(bool cancel)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02C7);
        DirectoryInfo directory = Directory.CreateTempSubdirectory("openblade-0529-probe-gate-");
        try
        {
            int admissionRequests = await CountRequestsBeforeVaporChamberProbeAsync(model, directory);
            using var sharedCommandGate = new SemaphoreSlim(1, 1);
            using var cancellation = new CancellationTokenSource();
            var native = new Native(1) { ProductId = model.ProductId };
            Task? competitor = null;
            Io io = null!;
            io = new Io
            {
                Events = native.Events,
                // Another component queues for the shared gate while the last
                // admission GET holds it, so it is served before the probe.
                AfterGet = () =>
                {
                    if (io.Requests.Count == admissionRequests && competitor is null)
                    {
                        competitor = sharedCommandGate.WaitAsync();
                        if (cancel)
                        {
                            cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));
                        }
                    }
                },
            };
            var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
                new Guard(), io, () => PowerSource.ExternalUnknown, sharedCommandGate,
                lightingJournal: new(directory.FullName, model.ModelNumber, model.ProductId, _ => { }));

            HidSessionResult<IProductionBatteryPort> first =
                await factory.OpenAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotNull(competitor);
            await competitor!;
            sharedCommandGate.Release();
            if (cancel)
            {
                Assert.Null(first.Value);
                Assert.Equal(HidSessionFailureKind.Canceled, first.Failure);
                Assert.Equal(1, native.DisposeCounts[0]);
            }
            else
            {
                Assert.Null(first.Failure);
                await using (Assert.IsType<ExperimentalBladePort>(first.Value)) { }
                Assert.Contains(factory.GetDiagnostics().Observations, item => item.Operation == "VaporChamberEffect"
                    && item.FailureCategory == "VaporChamberProbeDeferred");
            }
            Assert.Equal(1, native.BoundCount);
            Assert.Equal(0, VaporChamberQueries(io));

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

    private static async Task<int> CountRequestsBeforeVaporChamberProbeAsync(
        ExperimentalBladeModel model, DirectoryInfo directory)
    {
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new Io { Events = native.Events };
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.ExternalUnknown,
            lightingJournal: new(Path.Combine(directory.FullName, "count"), model.ModelNumber,
                model.ProductId, _ => { }));
        HidSessionResult<IProductionBatteryPort> opened = await factory.OpenAsync(CancellationToken.None);
        await using (Assert.IsType<ExperimentalBladePort>(opened.Value)) { }
        int probe = io.Requests.FindIndex(request => request is (0x0F, 0x82, 38));
        Assert.True(probe > 0);
        return probe;
    }
}
