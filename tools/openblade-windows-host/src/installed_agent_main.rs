//! Standalone installed sign-in entry. Fixture modules are outside this binary.

#[cfg(windows)]
pub(crate) mod origin_lifetime;

include!(concat!(env!("OUT_DIR"), "/agent_identity.rs"));

#[cfg(windows)]
mod loaded_origin;
#[cfg(windows)]
mod native_package;
#[cfg(windows)]
mod pipe_server_identity;
#[cfg(windows)]
mod retained_process;
#[cfg(windows)]
#[path = "windows/scm/identity.rs"]
mod scm_identity;
#[cfg(windows)]
mod service_frame;
#[cfg(windows)]
mod service_install_identity;
#[cfg(windows)]
#[allow(dead_code)] // Also contains producer helpers when both candidates are enabled.
mod service_status;
#[cfg(windows)]
mod session_native;
#[cfg(windows)]
mod windows_protected_path;
#[cfg(windows)]
mod windows_token;

#[cfg(not(test))]
fn main() -> std::process::ExitCode {
    std::hint::black_box(&NATIVE_ARTIFACT_IDENTITY);
    #[cfg(windows)]
    let result = loaded_origin::Origin::acquire("agent", &NATIVE_ARTIFACT_IDENTITY)
        .and_then(session_native::entry_installed);
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
