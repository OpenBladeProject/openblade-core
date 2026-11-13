//! Standalone installed SCM entry. Fixture modules are outside this binary.

#[cfg(windows)]
pub(crate) mod origin_lifetime;

include!(concat!(env!("OUT_DIR"), "/service_identity.rs"));

#[cfg(windows)]
mod candidate_receipt;
#[cfg(windows)]
mod console_session;
#[cfg(windows)]
#[allow(dead_code)] // Installed firmware access requires an admitted runtime owner.
mod discovery;
#[cfg(windows)]
#[path = "windows/scm/installed.rs"]
mod installed;
#[cfg(windows)]
mod loaded_origin;
#[cfg(windows)]
mod native_package;
#[cfg(windows)]
mod pipe_server_identity;
#[cfg(windows)]
mod retained_process;
#[cfg(windows)]
mod scm_host;
#[cfg(windows)]
#[path = "windows/scm/identity.rs"]
mod scm_identity;
#[cfg(all(windows, feature = "scratch-candidate-receipt"))]
#[allow(dead_code)]
mod scratch_receipt_policy;
#[cfg(all(windows, feature = "scratch-candidate-receipt"))]
#[allow(dead_code)]
mod scratch_receiver_closure;
#[cfg(windows)]
mod service_frame;
#[cfg(windows)]
mod service_image;
#[cfg(windows)]
mod service_install_identity;
#[cfg(windows)]
mod service_status;
#[cfg(all(windows, test, feature = "native-agent-entry-compile-check"))]
mod session_native;
#[cfg(windows)]
#[allow(dead_code)] // Shared with the development writer, which is not linked here.
mod windows_protected_path;
#[cfg(windows)]
mod windows_token;

#[cfg(not(test))]
fn main() -> std::process::ExitCode {
    std::hint::black_box(&NATIVE_ARTIFACT_IDENTITY);
    #[cfg(windows)]
    let result = loaded_origin::Origin::acquire("service", &NATIVE_ARTIFACT_IDENTITY)
        .and_then(installed::entry);
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

#[cfg(windows)]
mod native_owner_record;

#[cfg(all(windows, feature = "service-thermal-mutation-v1"))]
mod configuration;

#[cfg(all(windows, feature = "service-thermal-mutation-v1"))]
mod thermal_history;
