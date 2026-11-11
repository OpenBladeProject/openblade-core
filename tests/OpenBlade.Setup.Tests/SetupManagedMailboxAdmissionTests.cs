using OpenBlade.Deployment;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupManagedMailboxAdmissionTests
{
    [Fact]
    public void ActualSetupAdmissionRequiresOriginalExitBeforeTheProtectedReceipt()
    {
        var order = new List<string>();
        var original = new Original(order);
        var receipt = new Receipt(order);
        SetupManagedMailboxAdmission.Confirm(original, receipt);
        Assert.Equal(new[] { "original-exited", "protected-receipt" }, order);
        order.Clear();
        original.Running = true;
        Assert.Throws<IOException>(() => SetupManagedMailboxAdmission.Confirm(original, receipt));
        Assert.Equal(new[] { "original-exited" }, order);
        order.Clear();
        original.Running = false;
        receipt.Missing = true;
        Assert.Throws<IOException>(() => SetupManagedMailboxAdmission.Confirm(original, receipt));
        Assert.Equal(new[] { "original-exited", "protected-receipt" }, order);
    }

    private sealed class Original(List<string> order) : ISetupOriginalManagedProcess
    {
        internal bool Running { get; set; }
        public SetupCandidateLaunch Launch => new(17, 100, @"C:\private-fixture\OpenBlade.Service.exe", new string('a', 64));
        public void RequireExited() { order.Add("original-exited"); if (Running) { throw new IOException("Original is still running."); } }
        public void Dispose() { }
    }
    private sealed class Receipt(List<string> order) : IManagedMailboxReceipt
    {
        internal bool Missing { get; set; }
        public void ConfirmOriginalExited() { order.Add("protected-receipt"); if (Missing) { throw new IOException("No quiescence receipt."); } }
    }
}
