//! Standalone installed SCM entry. Fixture modules are outside this binary.

#[cfg(windows)]
#[path = "windows/scm/installed.rs"]
mod installed;
#[cfg(windows)]
mod scm_host;
#[cfg(windows)]
#[path = "windows/scm/identity.rs"]
mod scm_identity;

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
