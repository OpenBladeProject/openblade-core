use std::io;
#[cfg(test)]
use std::{
    ffi::OsString,
    path::{Path, PathBuf},
};

#[cfg(feature = "agent-application-reporter-v1")]
#[cfg_attr(test, allow(dead_code, unused_imports))]
// The production transport is built only by the non-test installed monitor.
mod application;
#[cfg(feature = "agent-battery-report-v1")]
#[cfg_attr(test, allow(dead_code))]
// The production supplier is built only by the non-test installed monitor.
mod battery;
#[cfg(openblade_agent_endpoint)]
pub(crate) mod diagnostics;
#[cfg(feature = "agent-display-query-v1")]
#[cfg_attr(test, allow(dead_code))]
// The production suppliers are built only by the non-test installed monitor.
mod display_query;
pub(crate) mod exit;
#[cfg(openblade_agent_endpoint)]
mod feature_lifetime;
#[cfg(feature = "agent-function-row-v1")]
#[cfg_attr(test, allow(dead_code))]
// The production hooks are built only by the non-test installed monitor.
mod function_row;
#[cfg(feature = "agent-gpu-v1")]
mod gpu;
#[cfg(not(test))]
mod installed_monitor;
#[cfg(openblade_display_write)]
mod power_admission;
#[cfg(feature = "agent-power-mode-automation-v1")]
#[cfg_attr(test, allow(dead_code))]
// The production supplier is built only by the non-test installed monitor.
mod power_mode;
#[cfg_attr(test, allow(dead_code))]
// Only the non-test installed entry registers a restart.
mod restart;
#[allow(dead_code)]
mod scm_peer;
pub(crate) mod service_client;
mod token;
#[cfg(any(openblade_display_write, feature = "agent-function-row-v1"))]
#[cfg_attr(test, allow(dead_code))]
// Only the non-test installed monitor resolves the user environment.
mod user_environment;

// Preserve the source of installed-origin failures across the monitor and
// Service-authority wrappers. Default/unselected entry error behavior is unchanged.
fn verify_origin(origin: &crate::loaded_origin::Origin) -> io::Result<()> {
    origin.verify().map_err(origin_error)
}

pub(crate) fn origin_error(error: io::Error) -> io::Error {
    #[cfg(openblade_agent_endpoint)]
    let error = diagnostics::origin(error);
    error
}
/// Bind the launch to the installed image directory. Setup's sign-in action
/// starts the Agent there, but Windows Application Restart cannot set a working
/// directory, so a restarted Agent starts elsewhere. The directory is therefore
/// not an admission rule: Origin has already proved the image, package, process
/// and session, and nothing before this point resolves a relative path (every
/// store is an absolute known-folder path, and `SetDefaultDllDirectories` in
/// `Origin::acquire` already removed the working directory from DLL search).
/// What is left is to make the directory what the sign-in action would have made
/// it, so later code that resolves a path against it sees the install directory
/// and never a user-writable one.
#[cfg_attr(test, allow(dead_code))]
fn bind_working_directory(
    current: io::Result<std::path::PathBuf>,
    image_parent: Option<&std::path::Path>,
    set: impl FnOnce(&std::path::Path) -> io::Result<()>,
) -> io::Result<()> {
    let parent = image_parent.ok_or_else(|| denied("agent image parent missing"))?;
    if current.is_ok_and(|current| current == parent) {
        return Ok(());
    }
    set(parent).map_err(|_| denied("native Agent working directory could not be bound"))
}

#[cfg(not(test))]
pub(crate) fn entry_installed(
    origin: std::sync::Arc<crate::loaded_origin::Origin>,
) -> Result<(), exit::Failure> {
    use exit::Failure::Denied;
    let token = token::process_token_evidence().map_err(Denied)?;
    if !token.primary
        || token.elevated
        || token.token_session == 0
        || token.token_session != token.process_session
        || token.process_session != origin.session
        || matches!(token.sid.as_str(), "S-1-5-18" | "S-1-5-19" | "S-1-5-20")
    {
        return Err(Denied(denied("unexpected native Agent process token")));
    }
    bind_working_directory(std::env::current_dir(), origin.image.parent(), |parent| {
        std::env::set_current_dir(parent)
    })
    .map_err(Denied)?;
    // This standalone Agent owns process-wide COM security before any selected
    // supplier starts. Cache failures too; only brightness becomes unavailable.
    #[cfg(feature = "agent-display-query-v1")]
    let _com_security = openblade_platform_windows::brightness::initialize_process_security();
    // Managed registers the same restart, best effort, before it serves.
    let mut restart = restart::Registration::new(restart::Windows);
    let _ = restart.try_register();
    installed_monitor::run(origin, restart)
}

#[cfg(test)]
use service_client::SubscriptionInitial;
#[cfg(test)]
use token::ProcessTokenEvidence;

#[cfg(test)]
const SIGN_IN_IMAGE: &str = "OpenBlade.SessionAgent.exe";

#[cfg(test)]
pub(super) fn entry(mut args: impl Iterator<Item = OsString>) -> io::Result<()> {
    if args.next().is_some() {
        return Err(denied("unexpected Session Agent arguments"));
    }

    let image = std::env::current_exe()?;
    let working_directory = std::env::current_dir()?;
    // The installed lease, independent Service authority and stop adapter
    // are unwired. The private fixture cannot supply installed evidence.
    run(
        &image,
        &working_directory,
        token::process_token_evidence,
        None,
    )
}

#[cfg(test)]
struct SignInContext {
    image: PathBuf,
    sid: String,
    session: u32,
    logon: (i32, u32),
}

#[cfg(test)]
trait SignInBindings {
    // Independently verified installed action and authorized interactive logon
    // for this session. Process-token evidence alone cannot supply either.
    fn authorized_sign_in(&mut self, session: u32) -> io::Result<SignInContext>;

    // Own the managed first-instance endpoint set for this SID/session for the
    // lifetime of the lease. A fixture endpoint or an unrelated lock is invalid.
    fn claim(&mut self, context: &SignInContext) -> io::Result<Box<dyn AgentLease>>;
}

#[cfg(test)]
trait AgentLease {
    fn readiness(&self) -> Readiness;
    // Start the retained watcher with independent Service process authority
    // and the Agent stop signal. Wait for its first verified open.
    fn open_service_status(&mut self) -> io::Result<SubscriptionInitial>;
    // Return Ok only after a confirmed stop and completion of owned work.
    // Drop must release the claim on all startup and stop exit paths.
    fn serve_until_stopped(&mut self) -> io::Result<()>;
}

// Only the sign-in readiness check, which tests exercise, reads every role.
#[cfg_attr(not(test), allow(dead_code))]
#[derive(Clone, Copy, Default)]
struct Readiness {
    display: bool,
    gpu: bool,
    audio: bool,
    battery: bool,
    tray_events: bool,
    service_client: bool, // All required Service operations.
    stop: bool,
}

impl Readiness {
    /// Readiness set by this build's native endpoints. Only the GPU role,
    /// whose complete managed contract (query, close, force close) the
    /// selected endpoint serves, is set here; the partial display endpoint
    /// and every other role keep their own readiness decision.
    #[cfg_attr(
        not(test),
        expect(dead_code, reason = "the sign-in lease that reports it is unwired")
    )]
    fn native_endpoints() -> Self {
        Self {
            gpu: cfg!(feature = "agent-gpu-v1"),
            ..Self::default()
        }
    }

    #[cfg(test)]
    fn require_complete(&self) -> io::Result<()> {
        for (ready, role) in [
            (self.display, "display"),
            (self.gpu, "GPU"),
            (self.audio, "audio"),
            (self.battery, "battery"),
            (self.tray_events, "tray event client"),
            (self.service_client, "service client"),
            (self.stop, "stop signal"),
        ] {
            if !ready {
                return Err(io::Error::new(
                    io::ErrorKind::Unsupported,
                    format!("native Session Agent readiness is unavailable for {role}"),
                ));
            }
        }
        Ok(())
    }
}

#[cfg(test)]
fn run(
    image: &Path,
    working_directory: &Path,
    read_token: impl FnOnce() -> io::Result<ProcessTokenEvidence>,
    bindings: Option<&mut dyn SignInBindings>,
) -> io::Result<()> {
    if !image.is_absolute()
        || !image
            .file_name()
            .and_then(|name| name.to_str())
            .is_some_and(|name| name.eq_ignore_ascii_case(SIGN_IN_IMAGE))
        || image.parent() != Some(working_directory)
    {
        return Err(denied("unexpected Session Agent sign-in action"));
    }

    let token = read_token()?;
    if !token.primary
        || token.elevated
        || token.token_session == 0
        || token.token_session != token.process_session
        || token.sid.is_empty()
        || matches!(token.sid.as_str(), "S-1-5-18" | "S-1-5-19" | "S-1-5-20")
    {
        return Err(denied("unexpected Session Agent process token"));
    }

    let bindings = bindings.ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::Unsupported,
            "native Session Agent sign-in and endpoint bindings are unavailable",
        )
    })?;
    let context = bindings.authorized_sign_in(token.process_session)?;
    if context.image != image
        || context.sid != token.sid
        || context.session != token.token_session
        || context.logon != token.logon
    {
        return Err(denied(
            "Session Agent does not match the authorized interactive logon",
        ));
    }

    let mut lease = bindings.claim(&context)?;
    lease.readiness().require_complete()?;
    lease.open_service_status()?.require_fresh()?;
    lease.serve_until_stopped()
}

fn denied(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, message)
}

#[cfg(test)]
mod tests;
