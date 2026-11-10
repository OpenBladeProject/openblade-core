use std::{
    io,
    path::{Path, PathBuf},
};

use openblade_windows_host::pipe::{Identity, ProcessTokenEvidence};

const SIGN_IN_IMAGE: &str = "OpenBlade.SessionAgent.exe";

pub(super) fn entry() -> io::Result<()> {
    let image = std::env::current_exe()?;
    let working_directory = std::env::current_dir()?;
    // This compile-only build has no trusted installed launch binding or
    // production endpoint/stop adapter. Never use the private fixture here.
    run(
        &image,
        &working_directory,
        Identity::process_token_evidence,
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
    service_client: bool,
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
    lease.serve_until_stopped()
}

fn denied(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, message)
}

#[cfg(test)]
mod tests;
