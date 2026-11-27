use std::{future::Future, io, pin::Pin, rc::Rc};

use tokio::io::{AsyncRead, AsyncWrite};

pub(crate) type Pending<'a, T> = Pin<Box<dyn Future<Output = io::Result<T>> + 'a>>;
pub(crate) type ControlSession = Rc<dyn Fn() -> io::Result<Option<u32>>>;

// Each native connection retains its handle and supplies handle-bound
// evidence; the listener never infers identity from the pipe name.
pub(crate) trait Stream: AsyncRead + AsyncWrite + Unpin {
    fn server_pid(&self) -> io::Result<u32>;
    fn caller_after_read(&self) -> io::Result<Caller>;
    fn control_session(&self) -> io::Result<Option<u32>>;
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn user_sid_after_read(&self) -> io::Result<String> {
        Err(io::ErrorKind::PermissionDenied.into())
    }
    /// Whether the token of the request just read holds BUILTIN\Administrators
    /// enabled and not deny-only (managed `ServiceClientContext.IsAdministrator`).
    /// Evidence that cannot be read is no administrator.
    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    fn administrator_after_read(&self) -> bool {
        false
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum Logon {
    System,
    Interactive,
    RemoteInteractive,
    Other,
}

#[derive(Clone, Copy)]
pub(crate) struct Caller {
    pub(crate) pid: Option<u32>,
    pub(crate) logon: Logon,
    pub(crate) session: u32,
    pub(crate) control_session: Option<u32>,
}
