//! Phase 3b offline transport target. The developer binaries cannot link these modules.

#[path = "standard_transport/io.rs"]
mod io;
#[path = "standard_transport/replay.rs"]
mod replay;
#[path = "standard_transport/session.rs"]
mod session;

#[cfg(windows)]
#[path = "standard_transport/windows.rs"]
#[allow(unsafe_code)]
mod windows;
