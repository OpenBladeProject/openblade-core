namespace OpenBlade.Protocol;

// One Service run owns this scope. Failure remains sticky after tasks detach.
internal sealed class MailboxLifetime
{
    private readonly object sync = new();
    private readonly TaskCompletionSource terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int pending;
    private bool sealedAdmission;
    private Exception? failure;
    private bool admitted;
    internal MailboxLifetime() : this(admitted: true) { }
    // A production Service run reserves nothing until it holds mailbox custody.
    internal MailboxLifetime(bool admitted) { this.admitted = admitted; }
    internal void Admit() { lock (sync) { admitted = true; } }
    internal Participation Reserve()
    {
        lock (sync)
        {
            if (sealedAdmission) { throw new IOException("Service mailbox acquisition/submission is sealed."); }
            if (!admitted) { throw new IOException("Service mailbox custody has not been acquired."); }
            return Add();
        }
    }
    private Participation Add() { checked { pending++; } return new(this); }
    internal void Seal() { lock (sync) { sealedAdmission = true; Complete(); } }
    internal Task WhenTerminal => terminal.Task;
    internal bool SuccessfullyTerminal
    {
        get { lock (sync) { return sealedAdmission && pending == 0 && failure is null; } }
    }
    internal void Fail(Exception error) { lock (sync) { failure ??= error; Complete(); } }
    private void Complete()
    {
        if (!sealedAdmission || pending != 0) { return; }
        if (failure is null) { terminal.TrySetResult(); }
        else { terminal.TrySetException(new IOException("Service transport retirement failed.", failure)); }
    }
    internal sealed class Participation : IDisposable
    {
        private readonly MailboxLifetime scope;
        private bool ended;
        internal Participation(MailboxLifetime scope) { this.scope = scope; }
        internal MailboxLifetime Scope => scope;
        // Late callbacks derive from admitted work, even after external sealing.
        internal Participation Child()
        {
            lock (scope.sync)
            {
                if (ended) { throw new ObjectDisposedException(nameof(Participation)); }
                return scope.Add();
            }
        }
        internal void Fail(Exception error) => scope.Fail(error);
        internal async Task RunAsync(Func<Task> run)
        {
            try { await run().ConfigureAwait(false); }
            catch (Exception error) { Fail(error); throw; }
            finally { Dispose(); }
        }
        public void Dispose()
        {
            lock (scope.sync)
            {
                if (ended) { return; }
                ended = true; scope.pending--; scope.Complete();
            }
        }
    }
}
