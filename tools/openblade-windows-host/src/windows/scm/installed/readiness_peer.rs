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
    /// Whether the token of the request just read is LocalSystem's or a
    /// member of BUILTIN\Administrators, as managed admits a shutdown
    /// (`system_or_administrator`). Every stream answers for itself.
    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    fn system_or_administrator_after_read(&self) -> bool;
    /// Whether the client has closed its end, read now without waiting.
    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    fn peer_closed(&mut self) -> bool;
}

/// Managed admits a shutdown from LocalSystem or an administrator
/// (`ServiceClientContext.IsLocalSystem`, `IsAdministrator`). Evidence that
/// cannot be read admits nobody: membership is asked only of a token whose
/// user was read and is not SYSTEM, and a failed membership read is no.
#[cfg(feature = "service-lifecycle-shutdown-v1")]
pub(crate) fn system_or_administrator(
    system: io::Result<bool>,
    administrator: impl FnOnce() -> io::Result<bool>,
) -> bool {
    match system {
        Ok(true) => true,
        Ok(false) => matches!(administrator(), Ok(true)),
        Err(_) => false,
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

#[cfg(all(test, feature = "service-lifecycle-shutdown-v1"))]
mod tests {
    use std::cell::Cell;

    use super::*;

    /// SYSTEM is admitted without a membership read, a readable member
    /// is admitted, and every unreadable answer is no. Membership is never
    /// asked of a token whose user could not be read.
    #[test]
    fn shutdown_admission_fails_closed_on_every_unreadable_answer() {
        let unreadable = || io::Error::from(io::ErrorKind::PermissionDenied);
        for (system, member, admitted, asked) in [
            (Ok(true), Some(Err(unreadable())), true, false),
            (Ok(false), Some(Ok(true)), true, true),
            (Ok(false), Some(Ok(false)), false, true),
            (Ok(false), Some(Err(unreadable())), false, true),
            (Err(unreadable()), Some(Ok(true)), false, false),
            (Err(unreadable()), Some(Err(unreadable())), false, false),
        ] {
            let read = Cell::new(false);
            let member = Cell::new(member);
            let answer = system_or_administrator(system, || {
                read.set(true);
                member.take().unwrap()
            });
            assert_eq!((answer, read.get()), (admitted, asked));
        }
    }
}
