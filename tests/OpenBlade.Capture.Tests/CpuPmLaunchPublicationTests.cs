using System.Security.Cryptography;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPmLaunchPublicationTests
{
    private static readonly CpuPmAuthorityPeer Parent = new(7, 11, @"C:\payload\OpenBlade.PmSupervisor.exe", new('A', 64));
    private static readonly CpuPmAuthorityPeer Child = new(8, 12, @"C:\payload\OpenBlade.Capture.exe", new('B', 64));
    private static CpuPmLaunchOwner Owner() => new(Guid.NewGuid(), Guid.NewGuid(), new('a', 40), new('C', 64), Parent, Child.ExecutablePath, Child.ExecutableSha256);
    [Fact]
    public void OwnerPrecedesChildAndChildBindsExactEncodedOwnerAndObservedIdentity()
    {
        var store = new RecordStore(); var owner = Owner(); var publisher = new CpuPmLaunchPublication(owner, store);
        var child = new Retained(); publisher.PublishChild(child, _ => { });
        Assert.Equal(new[] { "owner.json", "child.json" }, store.Records.Keys);
        var record = CpuPmLaunchSchema.ParseChild(store.Records["child.json"]);
        Assert.Equal(Child, record.Child); Assert.Equal(Parent, record.Parent); Assert.Equal(owner.Run, record.Run);
        Assert.Equal(owner.Lifetime, record.Lifetime);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(store.Records["owner.json"])), record.OwnerSha256);
        Assert.Throws<InvalidOperationException>(() => publisher.PublishChild(child, _ => { }));
    }
    [Fact]
    public void ExactRetainedChildAuthorityPreparationPrecedesReadyRecordPublication()
    {
        var store = new RecordStore(); var publisher = new CpuPmLaunchPublication(Owner(), store); var child = new Retained();
        bool bound = false, listening = false;
        store.BeforeStore = name =>
        {
            if (name == "child.json") { Assert.True(bound); Assert.True(listening); }
        };
        publisher.PublishChild(child, actual =>
        {
            Assert.Same(child, actual); Assert.False(store.Records.ContainsKey("child.json"));
            bound = true; listening = true;
        });
        Assert.True(store.Records.ContainsKey("child.json"));
    }
    [Fact]
    public void AuthorityPreparationFailureNeverPublishesOrRetriesAndRetainsLiveChild()
    {
        var store = new RecordStore(); var publisher = new CpuPmLaunchPublication(Owner(), store); var child = new Retained(); int preparations = 0;
        Assert.Throws<IOException>(() => publisher.PublishChild(child, _ => { preparations++; throw new IOException("lifecycle/listener denied"); }));
        Assert.False(store.Records.ContainsKey("child.json")); Assert.Equal(CpuPmChildState.Running, child.ReadState());
        Assert.Throws<InvalidOperationException>(() => publisher.PublishChild(child, _ => { preparations++; }));
        Assert.Equal(1, preparations);
    }
    [Theory]
    [InlineData("identity")]
    [InlineData("Terminal")]
    [InlineData("Unknown")]
    public void ChildDriftDuringAuthorityPreparationCannotPublishReadiness(string fault)
    {
        var store = new RecordStore(); var publisher = new CpuPmLaunchPublication(Owner(), store); var child = new Retained();
        Assert.Throws<IOException>(() => publisher.PublishChild(child, _ =>
        {
            if (fault == "identity") { child.FailAtRead = 2; }
            else { child.State = Enum.Parse<CpuPmChildState>(fault); }
        }));
        Assert.False(store.Records.ContainsKey("child.json"));
        Assert.Throws<InvalidOperationException>(() => publisher.PublishChild(child, _ => { }));
    }
    [Fact]
    public void MissingAuthorityPreparationIsRejectedWithoutPublicationAndCannotRetry()
    {
        var store = new RecordStore(); var publisher = new CpuPmLaunchPublication(Owner(), store); var child = new Retained();
        Assert.Throws<ArgumentNullException>(() => publisher.PublishChild(child, null!));
        Assert.False(store.Records.ContainsKey("child.json"));
        Assert.Throws<InvalidOperationException>(() => publisher.PublishChild(child, _ => { }));
        foreach (var type in new[] { typeof(CpuPmLaunchPublication), typeof(WindowsCpuPmLaunchPublication) })
        {
            var method = Assert.Single(type.GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
                m => m.Name is "PublishChild" or "PublishActuallyLaunchedChild");
            Assert.Equal(2, method.GetParameters().Length); Assert.False(method.GetParameters()[1].IsOptional);
        }
    }
    [Theory]
    [InlineData("before-store")]
    [InlineData("after-store")]
    public void PublicationFailureIsOneShotAndLeavesActualChildAndRecordsRetained(string fault)
    {
        var store = new RecordStore(); var publisher = new CpuPmLaunchPublication(Owner(), store); var child = new Retained();
        store.Fault = fault;
        Assert.Throws<IOException>(() => publisher.PublishChild(child, _ => { }));
        Assert.Throws<InvalidOperationException>(() => publisher.PublishChild(child, _ => { }));
        Assert.Equal(CpuPmChildState.Running, child.ReadState()); Assert.True(store.Records.ContainsKey("owner.json"));
        Assert.Equal(fault == "after-store", store.Records.ContainsKey("child.json"));
    }
    [Theory]
    [InlineData("Terminal")]
    [InlineData("Unknown")]
    public void UncertainOrTerminalChildCannotProduceLaunchRecord(string state)
    {
        var store = new RecordStore(); var publisher = new CpuPmLaunchPublication(Owner(), store);
        Assert.Throws<IOException>(() => publisher.PublishChild(new Retained { State = Enum.Parse<CpuPmChildState>(state) }, _ => { }));
        Assert.False(store.Records.ContainsKey("child.json"));
    }
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void IdentityUncertaintyBeforeOrAfterStoreIsOneShotAndPreservesEvidence(int failAtRead, bool stored)
    {
        var store = new RecordStore(); var publisher = new CpuPmLaunchPublication(Owner(), store);
        var child = new Retained { FailAtRead = failAtRead };
        Assert.Throws<IOException>(() => publisher.PublishChild(child, _ => { }));
        Assert.Equal(stored, store.Records.ContainsKey("child.json"));
        Assert.Throws<InvalidOperationException>(() => publisher.PublishChild(child, _ => { }));
        Assert.Equal(CpuPmChildState.Running, child.ReadState());
    }
    [Fact]
    public async Task BarrierWaitsForPublishedNameThenRunsStrictLoaderOnce()
    {
        var wait = new Wait { ReadyAfter = 3 }; int calls = 0;
        Assert.Null(await CpuPmLaunchStartupBarrier.AwaitThenLoad<object>(wait, () => { calls++; return null; }, default));
        Assert.Equal(1, calls); Assert.Equal(3, wait.Delays);
    }
    [Fact]
    public async Task NamePresenceCannotCreateAuthority()
    {
        var wait = new Wait { ReadyAfter = 0 }; int calls = 0;
        Assert.Null(await CpuPmLaunchStartupBarrier.AwaitThenLoad<object>(wait, () => { calls++; throw new IOException("strict denial"); }, default));
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task TimeoutAndCancellationNeverInvokeLoader()
    {
        int calls = 0;
        Assert.Null(await CpuPmLaunchStartupBarrier.AwaitThenLoad<object>(new Wait(), () => { calls++; return new(); }, default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Null(await CpuPmLaunchStartupBarrier.AwaitThenLoad<object>(new Wait { ReadyAfter = 0 }, () => { calls++; return new(); }, canceled.Token));
        Assert.Equal(0, calls);
    }
    private sealed class RecordStore : ICpuPmLaunchRecordStore
    {
        internal readonly Dictionary<string, byte[]> Records = [];
        internal string? Fault;
        internal Action<string>? BeforeStore;
        public void AssertPins() { }
        public void Store(string name, byte[] bytes)
        {
            BeforeStore?.Invoke(name);
            if (Fault == "before-store") { throw new IOException(); }
            Records.Add(name, bytes);
            if (Fault == "after-store") { throw new IOException(); }
        }
    }
    private sealed class Retained : ICpuPmRetainedChild
    {
        public CpuPmAuthorityPeer Identity => Child;
        internal CpuPmChildState State = CpuPmChildState.Running;
        internal int FailAtRead = int.MaxValue, Reads;
        public CpuPmAuthorityPeer? ReadFreshIdentity() => ++Reads >= FailAtRead ? null : Identity;
        public CpuPmChildState ReadState() => State;
    }
    private sealed class Wait : ICpuPmLaunchStartupWait
    {
        internal int ReadyAfter = int.MaxValue, Delays;
        public TimeSpan Elapsed => TimeSpan.FromMilliseconds(Delays * 25);
        public bool ChildRecordPresent() => Delays >= ReadyAfter;
        public Task Delay(CancellationToken token) { token.ThrowIfCancellationRequested(); Delays++; return Task.CompletedTask; }
    }
}
