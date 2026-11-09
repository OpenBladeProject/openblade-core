using System.Runtime.InteropServices;
using OpenBlade.Contracts;
using OpenBlade.FanStop;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class FanStopFirmwareAvailabilityTests
{
    [Fact]
    public void ReviewedVersionsRemainAvailableIncluding0581AndEveryFirmwareVariant()
    {
        foreach (FanStopModelContract contract in FanStopModelContract.All)
        {
            var probe = new FanStopFirmwareAvailability(() => FanStopModelContract.ForModel(contract.Model),
                _ => Metadata(contract, contract.EcVersion));
            Assert.Equal(FanStopUnavailableReason.None, probe.Read().Reason);
            Assert.Null(probe.Read().Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreviewedAndUnknownVersionsRefuseBeforeControllerDispatch(bool unknown)
    {
        var contract = FanStopModelContract.ForModel("RZ09-0483")!;
        var probe = new FanStopFirmwareAvailability(() => contract,
            _ => Metadata(contract, unknown ? null : new Version(2, 2)));
        var client = new GuardianFanStopBackendTests.Client { FirmwareAvailability = () => probe.Read() };
        var controller = new FanStopController(new GuardianFanStopBackend(client));
        Assert.False(controller.BackendAvailable);
        FanStopStatus status = controller.GetStatus(true);
        Assert.Equal(FanStopState.Unavailable, status.State);
        Assert.False(status.CanStop);
        Assert.Contains(unknown ? "metadata could not be read" : "EC firmware 2.2", status.Message);
        Assert.Equal(0, client.StartCalls);
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("unknown")]
    [InlineData("host")]
    [InlineData("missing-family")]
    public void TransientPreflightFailureRetriesAndRecoversWithoutRestart(string failure)
    {
        var clock = new Clock();
        var contract = FanStopModelContract.ForModel("RZ09-0581")!;
        bool failed = true;
        int reads = 0;
        var probe = new FanStopFirmwareAvailability(() => failed && failure == "missing-family" ? null : contract,
            _ =>
            {
                reads++;
                if (failed && failure == "exception") { throw new IOException("Transient SMBIOS failure"); }
                var metadata = Metadata(contract, failed && failure == "unknown" ? null : contract.EcVersion);
                return failed && failure == "host"
                    ? metadata with { Host = new(new("Razer", "Unknown Blade", "", ""), null) } : metadata;
            }, clock);
        Assert.NotEqual(FanStopUnavailableReason.None, probe.Read().Reason);
        failed = false;
        int before = reads;
        Assert.NotEqual(FanStopUnavailableReason.None, probe.Read().Reason);
        Assert.Equal(before, reads);
        clock.Advance(FanStopFirmwareAvailability.RefreshInterval);
        Assert.Equal(FanStopUnavailableReason.None, probe.Read().Reason);
        Assert.True(reads > before);
    }

    [Fact]
    public void EntryRefreshDoesNotTrustCachedSupportedVersion()
    {
        var contract = FanStopModelContract.ForModel("RZ09-0581")!;
        Version? reported = contract.EcVersion;
        var probe = new FanStopFirmwareAvailability(() => contract, _ => Metadata(contract, reported), new Clock());
        Assert.Equal(FanStopUnavailableReason.None, probe.Read().Reason);
        reported = null;
        Assert.Equal(FanStopUnavailableReason.None, probe.Read().Reason);
        Assert.Equal(FanStopUnavailableReason.RecoveryNotValidated, probe.Read(refresh: true).Reason);
        reported = contract.EcVersion;
        Assert.Equal(FanStopUnavailableReason.None, probe.Read(refresh: true).Reason);
    }

    [Fact]
    public void LightweightPreflightReadsOnlyHostAndBootVersionAndNeverTreatsVersionAsBoardProof()
    {
        var family = FanStopModelContract.ForModel("RZ09-0483")!;
        List<string> calls = [];
        var metadata = So6120WindowsSupervisedMetadata.CaptureFirmwareForService(family,
            () => { calls.Add("host"); return Metadata(family, null).Host; },
            () => { calls.Add("SMBIOS"); return new Version(1, 1); }, Architecture.X64);
        Assert.Equal(new[] { "host", "SMBIOS" }, calls);
        var candidate = FanStopModelContract.ForFirmware(family.Model, metadata.ReportedEcVersion)!;
        Assert.Equal((ushort)0x3009, candidate.BoardSsid); // Still required by the native owner's journaled reads.
        calls.Clear();
        var rejected = So6120WindowsSupervisedMetadata.CaptureFirmwareForService(family,
            () => { calls.Add("host"); return new(new("Razer", "Wrong model", "", ""), null); },
            () => throw new InvalidOperationException("Wrong host must not request more metadata."), Architecture.X64);
        Assert.Null(rejected.ReportedEcVersion);
        Assert.Equal(new[] { "host" }, calls);
    }

    [Fact]
    public async Task ExistingOwnerRetainsStatusAndExplicitAutoWhenPreflightLaterFails()
    {
        var contract = FanStopModelContract.ForModel("RZ09-0581")!;
        Version? reported = contract.EcVersion;
        var probe = new FanStopFirmwareAvailability(() => contract, _ => Metadata(contract, reported));
        var client = new GuardianFanStopBackendTests.Client { FirmwareAvailability = () => probe.Read() };
        var backend = new GuardianFanStopBackend(client);
        var lease = new GuardianFanStopBackendTests.Lease();
        backend.ConfigureHardwareDelegation(_ => Task.FromResult<IDisposable>(lease));
        var controller = new FanStopController(backend);
        var normal = FanControlSettings.FromLegacy(FanMode.Automatic);
        Assert.Equal(ControlOperationStatus.Applied,
            (await controller.SetModeAsync(FanStopMode.Stop, true, normal, () => true, default)).Status);
        reported = null;
        Assert.Equal(FanStopUnavailableReason.RecoveryNotValidated, probe.Read(refresh: true).Reason);
        Assert.True(controller.BackendAvailable);
        Assert.Equal(FanStopState.Stopped, controller.GetStatus(true).State);
        Assert.True(controller.GetStatus(true).CanReturnToAutomatic);
        Assert.Equal(ControlOperationStatus.Applied,
            (await controller.SetModeAsync(FanStopMode.Automatic, true, normal, () => true, default)).Status);
        Assert.Equal(1, client.AutoCalls);
        Assert.Equal(1, lease.Disposals);
        Assert.False(controller.BackendAvailable);
    }

    internal static So6120ServiceFirmwareMetadata Metadata(FanStopModelContract contract, Version? version) =>
        new(Architecture.X64, new(new(contract.Host.Manufacturer, contract.Host.ProductName, "", ""), null), version);

    private sealed class Clock : TimeProvider
    {
        private long ticks = TimeSpan.TicksPerHour;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
}
