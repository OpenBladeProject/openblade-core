//! Standalone installed SCM entry. Fixture modules are outside this binary.

#[cfg(windows)]
mod candidate_receipt;
#[cfg(windows)]
mod console_session;
#[cfg(all(windows, not(test)))]
#[allow(dead_code)] // Only metadata observation is used by the installed candidate.
mod discovery;
#[cfg(windows)]
#[path = "windows/scm/installed.rs"]
mod installed;
#[cfg(windows)]
mod pipe_server_identity;
#[cfg(windows)]
mod scm_host;
#[cfg(windows)]
#[path = "windows/scm/identity.rs"]
mod scm_identity;
#[cfg(windows)]
mod service_frame;
#[cfg(windows)]
mod service_image;
#[cfg(windows)]
mod service_install_identity;
#[cfg(windows)]
mod service_status;
#[cfg(windows)]
#[allow(dead_code)] // Shared with the development writer, which is not linked here.
mod windows_protected_path;

#[cfg(not(test))]
fn main() -> std::process::ExitCode {
    #[cfg(windows)]
    let result = installed::entry();
    #[cfg(not(windows))]
    let result: std::io::Result<()> = Err(std::io::Error::new(
        std::io::ErrorKind::Unsupported,
        "the installed service candidate requires Windows",
    ));

    match result {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("OpenBlade installed service candidate: {error}");
            std::process::ExitCode::FAILURE
        }
    }
}
