//! Standalone installed SCM entry. Fixture modules are outside this binary.

#[cfg(windows)]
pub(crate) mod origin_lifetime;

include!(concat!(env!("OUT_DIR"), "/service_identity.rs"));

#[cfg(windows)]
mod candidate_receipt;
#[cfg(windows)]
mod console_session;
#[cfg(all(windows, feature = "service-diagnostics-export-v1"))]
mod diagnostic_export;
#[cfg(windows)]
#[allow(dead_code)] // Installed firmware access requires an admitted runtime owner.
mod discovery;
#[cfg(all(windows, feature = "driver-api-trust"))]
mod driver_trust;
#[cfg(windows)]
#[path = "windows/scm/installed.rs"]
mod installed;
#[cfg(windows)]
mod loaded_origin;
#[cfg(windows)]
mod native_package;
#[cfg(windows)]
#[allow(dead_code)] // The Service retains no monitor; the Session Agent's Service client does.
mod pipe_server_identity;
#[cfg(windows)]
mod retained_process;
#[cfg(windows)]
#[cfg_attr(test, allow(dead_code))]
// Only the non-test native entry dispatches through the SCM host.
mod scm_host;
#[cfg(windows)]
#[cfg_attr(test, allow(dead_code))] // Only the non-test native entry reads the SCM identity.
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
#[cfg(all(windows, any(test, feature = "scratch-candidate-receipt")))]
mod service_image;
#[cfg(windows)]
#[cfg_attr(test, allow(dead_code))] // Only the non-test native entry resolves Program Files.
mod service_install_identity;
#[cfg(windows)]
mod service_status;
#[cfg(all(windows, test, feature = "native-agent-entry-compile-check"))]
mod session_native;
#[cfg(windows)]
#[allow(dead_code)] // Shared with the development writer, which is not linked here.
mod windows_protected_path;
#[cfg(windows)]
#[allow(dead_code)] // Shared with the development host, which opens the process token.
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
