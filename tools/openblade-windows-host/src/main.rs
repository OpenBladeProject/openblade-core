#[cfg(windows)]
#[allow(dead_code)] // Only the installed entries bind ingress and lifetime.
pub(crate) mod origin_lifetime;

#[cfg(windows)]
mod application_runtime;
#[cfg(any(windows, test))]
mod battery_policy;
#[cfg(any(windows, test))]
mod battery_runtime;
#[cfg(all(windows, not(test), feature = "lifecycle-evidence-compile-check"))]
mod console_session;
#[cfg(all(windows, test))]
#[allow(dead_code)] // The host tests use scripted discovery only.
mod discovery;
#[cfg(any(windows, test))]
mod fan_runtime;
#[cfg(windows)]
mod fan_stop_fixture;
#[cfg(any(windows, test))]
mod performance_runtime;
#[cfg(any(windows, test))]
mod runtime;
#[cfg(windows)]
#[allow(dead_code)]
// This host binds no SCM ingress: set_ingress and installed_ingress serve installed entries.
mod scm_host;
#[cfg(all(windows, feature = "vm-scm-fixture"))]
#[path = "windows/scm/identity.rs"]
mod scm_identity;
#[cfg(windows)]
mod windows;
#[cfg(any(windows, test))]
mod wire;

fn main() -> std::process::ExitCode {
    #[cfg(windows)]
    let result = windows::entry();
    #[cfg(not(windows))]
    let result: std::io::Result<()> = Err(std::io::Error::new(
        std::io::ErrorKind::Unsupported,
        "the development host requires Windows",
    ));

    match result {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("OpenBlade development host: {error}");
            std::process::ExitCode::FAILURE
        }
    }
}
