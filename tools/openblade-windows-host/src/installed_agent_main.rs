//! Standalone installed sign-in entry. Fixture modules are outside this binary.

#[cfg(windows)]
mod pipe_server_identity;
#[cfg(windows)]
mod service_frame;
#[cfg(windows)]
#[allow(dead_code)] // Also contains producer helpers when both candidates are enabled.
mod service_status;
#[cfg(windows)]
mod session_native;
#[cfg(windows)]
mod windows_token;

fn main() -> std::process::ExitCode {
    #[cfg(windows)]
    let result = session_native::entry(std::env::args_os().skip(1));
    #[cfg(not(windows))]
    let result: std::io::Result<()> = Err(std::io::Error::new(
        std::io::ErrorKind::Unsupported,
        "the installed Session Agent candidate requires Windows",
    ));

    match result {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("OpenBlade installed Session Agent candidate: {error}");
            std::process::ExitCode::FAILURE
        }
    }
}
