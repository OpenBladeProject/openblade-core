//! A Service transport's own thread. Acquiring and verifying the installed
//! Service generation blocks (it re-reads and hashes the installed package and
//! queries the SCM), and the generation is not `Send`, so a transport that
//! holds one runs on a dedicated thread with its own current-thread runtime
//! instead of the Agent's shared runtime.
use std::{future::Future, io, thread};

use tokio::sync::oneshot;

/// Run `work` on a thread named `name` with its own current-thread runtime and
/// wait for it without blocking the caller. The thread is joined before this
/// returns.
pub(super) async fn host<F: Future<Output = io::Result<()>>>(
    name: &'static str,
    work: impl FnOnce() -> F + Send + 'static,
) -> io::Result<()> {
    let panicked = || io::Error::other(format!("{name} panicked"));
    let (done_tx, done) = oneshot::channel();
    let thread = thread::Builder::new().name(name.into()).spawn(move || {
        let result = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .and_then(|runtime| runtime.block_on(work()));
        let _ = done_tx.send(result);
    })?;
    let result = done.await.unwrap_or_else(|_| Err(panicked()));
    thread.join().map_err(|_| panicked())?;
    result
}
