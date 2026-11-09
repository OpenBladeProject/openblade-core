using Microsoft.Win32.SafeHandles;
using OpenBlade.FanStop;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

public sealed class FanStopServiceMetadataTests
{
    [Fact]
    public void ServiceEnumerationUsesOnlyItsExactModelAndRetainsUnreadableCandidates()
    {
        foreach (FanStopModelContract contract in FanStopModelContract.All)
        {
            string control = PathFor(contract, "control");
            string unreadable = PathFor(contract, "unreadable");
            string[] paths = [.. FanStopModelContract.All.Select(item => PathFor(item, "control")).Distinct(), unreadable,
                $@"\\?\hid#vid_{contract.VendorId:x4}&pid_{contract.ProductId:x4}0#suffix-collision"];
            var native = new MetadataNative(control, contract);
            IReadOnlyList<So6120HidMetadata> observed = So6120WindowsSupervisedMetadata.ReadHidMetadata(paths, native, contract);
            Assert.Equal(new[] { control, unreadable }, native.Opened);
            Assert.Equal(2, observed.Count);
            Assert.NotNull(observed[0].Identity);
            Assert.Null(observed[1].Identity);
            Assert.Equal(5, observed[1].OpenError);
        }
    }

    [Fact]
    public void DefaultDeveloperEnumerationStillSelectsOnly0581()
    {
        FanStopModelContract contract = FanStopModelContract.ForModel("RZ09-0581")!;
        string control = PathFor(contract, "control");
        var native = new MetadataNative(control, contract);
        var observed = So6120WindowsSupervisedMetadata.ReadHidMetadata(
            FanStopModelContract.All.Select(item => PathFor(item, "control")).ToArray(), native);
        Assert.Single(observed);
        Assert.Equal(new[] { control }, native.Opened);
    }

    private static string PathFor(FanStopModelContract contract, string suffix) =>
        $@"\\?\hid#vid_{contract.VendorId:x4}&pid_{contract.ProductId:x4}&mi_03&col01#{suffix}";

    private sealed class MetadataNative(string readable, FanStopModelContract contract) : IHidCandidateNative
    {
        public List<string> Opened { get; } = [];
        public IReadOnlyList<string> EnumerateDevicePaths() => throw new NotSupportedException();
        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters)
        {
            Opened.Add(path);
            Assert.Equal(new HidOpenParameters(0, 3, 3, 0, false), parameters);
            return path == readable
                ? new(new OpenedCandidateHandle(new SafeFileHandle(new IntPtr(1234), ownsHandle: false)), null)
                : new(null, 5);
        }
        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle) =>
            new(contract.VendorId, contract.ProductId, 1, 2, 91);
        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle) => throw new NotSupportedException();
    }
}
