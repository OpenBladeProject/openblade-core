use std::{
    ffi::OsString,
    io,
    path::{Path, PathBuf},
};

#[cfg(feature = "agent-battery-report-v1")]
mod battery;
#[cfg(any(
    feature = "agent-battery-report-v1",
    feature = "agent-display-query-v1"
))]
pub(crate) mod diagnostics;
#[cfg(feature = "agent-display-query-v1")]
mod display_query;
#[cfg(any(
    feature = "agent-battery-report-v1",
    feature = "agent-display-query-v1"
))]
mod feature_lifetime;
#[cfg(not(test))]
mod installed_monitor;
#[cfg(openblade_display_write)]
mod power_admission;
#[allow(dead_code)]
mod scm_peer;
pub(crate) mod service_client;
mod token;
#[cfg(openblade_display_write)]
mod user_environment;

// Preserve the source of installed-origin failures across the monitor and
// Service-authority wrappers. Default/unselected entry error behavior is unchanged.
fn verify_origin(origin: &crate::loaded_origin::Origin) -> io::Result<()> {
    origin.verify().map_err(origin_error)
}

fn origin_error(error: io::Error) -> io::Error {
    #[cfg(any(
        feature = "agent-battery-report-v1",
        feature = "agent-display-query-v1"
    ))]
    let error = diagnostics::origin(error);
    error
}
#[cfg(not(test))]
pub(crate) fn entry_installed(
    origin: std::sync::Arc<crate::loaded_origin::Origin>,
) -> io::Result<()> {
    let token = token::process_token_evidence()?;
    if !token.primary
        || token.elevated
        || token.token_session == 0
        || token.token_session != token.process_session
        || token.process_session != origin.session
        || matches!(token.sid.as_str(), "S-1-5-18" | "S-1-5-19" | "S-1-5-20")
        || std::env::current_dir()?
            != origin
                .image
                .parent()
                .ok_or_else(|| denied("agent image parent missing"))?
    {
        return Err(denied("unexpected native Agent token or working directory"));
    }
    // This standalone Agent owns process-wide COM security before any selected
    // supplier starts. Cache failures too; only brightness becomes unavailable.
    #[cfg(feature = "agent-display-query-v1")]
    let _com_security = openblade_platform_windows::brightness::initialize_process_security();
    installed_monitor::run(origin)
}

use service_client::SubscriptionInitial;
use token::ProcessTokenEvidence;

const SIGN_IN_IMAGE: &str = "OpenBlade.SessionAgent.exe";

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

struct SignInContext {
    image: PathBuf,
    sid: String,
    session: u32,
    logon: (i32, u32),
}

trait SignInBindings {
    // Independently verified installed action and authorized interactive logon
    // for this session. Process-token evidence alone cannot supply either.
    fn authorized_sign_in(&mut self, session: u32) -> io::Result<SignInContext>;

    // Own the managed first-instance endpoint set for this SID/session for the
    // lifetime of the lease. A fixture endpoint or an unrelated lock is invalid.
    fn claim(&mut self, context: &SignInContext) -> io::Result<Box<dyn AgentLease>>;
}

trait AgentLease {
    fn readiness(&self) -> Readiness;
    // Start the retained watcher with independent Service process authority
    // and the Agent stop signal. Wait for its first verified open.
    fn open_service_status(&mut self) -> io::Result<SubscriptionInitial>;
    // Return Ok only after a confirmed stop and completion of owned work.
    // Drop must release the claim on all startup and stop exit paths.
    fn serve_until_stopped(&mut self) -> io::Result<()>;
}

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
