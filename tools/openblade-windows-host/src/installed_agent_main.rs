//! Standalone installed sign-in entry. Fixture modules are outside this binary.

#[cfg(windows)]
#[allow(dead_code)] // Shared with the Service entry, which owns the lifetime ingress.
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
#[allow(dead_code)] // Shared with the Service entry, which checks its SCM identity.
#[path = "windows/scm/identity.rs"]
mod scm_identity;
#[cfg(windows)]
mod service_frame;
#[cfg(windows)]
#[allow(dead_code)] // Shared with the Service entry, which resolves its install location.
mod service_install_identity;
#[cfg(windows)]
#[allow(dead_code)] // Also contains producer helpers when both candidates are enabled.
mod service_status;
#[cfg(windows)]
mod session_native;
#[cfg(windows)]
#[allow(dead_code)] // Shared with the Service entry and the development writer.
mod windows_protected_path;
#[cfg(windows)]
mod windows_token;

#[cfg(not(test))]
fn main() -> std::process::ExitCode {
    std::hint::black_box(&NATIVE_ARTIFACT_IDENTITY);
    #[cfg(all(windows, openblade_agent_endpoint))]
    let _diagnostics = openblade_platform_windows::agent_diagnostics::register();
    #[cfg(windows)]
    {
        use session_native::exit::{Failure, conclude, guarded, raise_fail_fast};
        // Everything the entry owns, Origin included, is released when it
        // returns; only then is the failure reported and, for a fault or a
        // panic, raised so Windows Error Reporting sees a crash.
        let outcome = guarded(|| {
            let origin = loaded_origin::Origin::acquire("agent", &NATIVE_ARTIFACT_IDENTITY)
                .map_err(|error| Failure::Denied(session_native::origin_error(error)))?;
            session_native::entry_installed(origin)
        });
        conclude(
            outcome,
            |error| {
                #[cfg(not(openblade_agent_endpoint))]
                eprintln!("OpenBlade installed Session Agent candidate: {error}");
                #[cfg(openblade_agent_endpoint)]
                session_native::diagnostics::terminal(
                    error,
                    openblade_platform_windows::agent_diagnostics::record,
                );
            },
            raise_fail_fast,
        )
    }
    #[cfg(not(windows))]
    {
        eprintln!(
            "OpenBlade installed Session Agent candidate: {}",
            std::io::Error::new(
                std::io::ErrorKind::Unsupported,
                "the installed Session Agent candidate requires Windows",
            )
        );
        std::process::ExitCode::FAILURE
    }
}
