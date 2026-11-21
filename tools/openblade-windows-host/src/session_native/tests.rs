use super::*;
use std::{cell::Cell, rc::Rc};
use tokio::io::DuplexStream;

const IMAGE: &str = r"C:\Synthetic\OpenBlade\OpenBlade.SessionAgent.exe";
const DIRECTORY: &str = r"C:\Synthetic\OpenBlade";
const SID: &str = "S-1-5-21-1-2-3-1001";

fn token() -> ProcessTokenEvidence {
    ProcessTokenEvidence {
        sid: SID.into(),
        token_session: 3,
        process_session: 3,
        logon: (0, 17),
        primary: true,
        elevated: false,
    }
}

fn complete() -> Readiness {
    Readiness {
        display: true,
        gpu: true,
        audio: true,
        battery: true,
        tray_events: true,
        service_client: true,
        stop: true,
    }
}

struct Bindings {
    context: Option<SignInContext>,
    held: Rc<Cell<bool>>,
    waited: Rc<Cell<bool>>,
    ready: Readiness,
    subscription_error: Option<io::ErrorKind>,
    stop_error: bool,
}

impl Default for Bindings {
    fn default() -> Self {
        Self {
            context: Some(SignInContext {
                image: IMAGE.into(),
                sid: SID.into(),
                session: 3,
                logon: (0, 17),
            }),
            held: Rc::default(),
            waited: Rc::default(),
            ready: complete(),
            subscription_error: None,
            stop_error: false,
        }
    }
}

impl SignInBindings for Bindings {
    fn authorized_sign_in(&mut self, session: u32) -> io::Result<SignInContext> {
        assert_eq!(session, 3);
        self.context
            .take()
            .ok_or_else(|| denied("no authorized logon"))
    }

    fn claim(&mut self, _: &SignInContext) -> io::Result<Box<dyn AgentLease>> {
        if self.held.replace(true) {
            return Err(io::ErrorKind::AlreadyExists.into());
        }
        Ok(Box::new(Lease {
            held: Rc::clone(&self.held),
            waited: Rc::clone(&self.waited),
            ready: std::mem::take(&mut self.ready),
            subscription_error: self.subscription_error,
            subscription: None,
            stop_error: self.stop_error,
        }))
    }
}

struct Lease {
    held: Rc<Cell<bool>>,
    waited: Rc<Cell<bool>>,
    ready: Readiness,
    subscription_error: Option<io::ErrorKind>,
    subscription: Option<(DuplexStream, DuplexStream)>,
    stop_error: bool,
}

impl AgentLease for Lease {
    fn readiness(&self) -> Readiness {
        assert!(self.held.get());
        self.ready
    }

    fn open_service_status(&mut self) -> io::Result<SubscriptionInitial> {
        assert!(self.held.get());
        if let Some(error) = self.subscription_error {
            return Err(error.into());
        }
        let (stream, initial) = service_client::tests::fixture_initial()?;
        self.subscription = Some(stream);
        Ok(initial)
    }

    fn serve_until_stopped(&mut self) -> io::Result<()> {
        assert!(self.held.get());
        assert!(self.subscription.is_some());
        self.waited.set(true);
        if self.stop_error {
            return Err(io::ErrorKind::BrokenPipe.into());
        }
        Ok(())
    }
}

impl Drop for Lease {
    fn drop(&mut self) {
        assert!(self.held.replace(false));
    }
}

fn start(evidence: ProcessTokenEvidence, bindings: Option<&mut Bindings>) -> io::Result<()> {
    run(
        Path::new(IMAGE),
        Path::new(DIRECTORY),
        || Ok(evidence),
        bindings.map(|bindings| bindings as &mut dyn SignInBindings),
    )
}

#[test]
fn wrong_launch_shape_never_reads_token() {
    for (image, directory) in [
        (
            r"C:\Synthetic\OpenBlade\openblade-rust-session-agent.exe",
            DIRECTORY,
        ),
        (IMAGE, r"C:\Synthetic\Other"),
        ("OpenBlade.SessionAgent.exe", ""),
    ] {
        let result = run(
            Path::new(image),
            Path::new(directory),
            || panic!("token queried"),
            None,
        );
        assert_eq!(result.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
    }
}

#[test]
fn token_failures_do_not_query_authority() {
    let cases: [fn(&mut ProcessTokenEvidence); 6] = [
        |t| t.elevated = true,
        |t| t.primary = false,
        |t| {
            t.token_session = 0;
            t.process_session = 0;
        },
        |t| t.process_session += 1,
        |t| t.sid = "S-1-5-18".into(),
        |t| t.sid.clear(),
    ];
    for change in cases {
        let mut evidence = token();
        change(&mut evidence);
        let mut bindings = Bindings::default();
        let result = start(evidence, Some(&mut bindings));
        assert_eq!(result.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
        assert!(bindings.context.is_some());
        assert!(!bindings.held.get());
    }
    let result = run(
        Path::new(IMAGE),
        Path::new(DIRECTORY),
        || Err(io::ErrorKind::NotFound.into()),
        None,
    );
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::NotFound);
}

#[test]
fn authorized_image_sid_session_and_logon_must_all_match() {
    let cases: [fn(&mut SignInContext); 4] = [
        |c| c.image = r"C:\Synthetic\Other\OpenBlade.SessionAgent.exe".into(),
        |c| c.sid.push('2'),
        |c| c.session += 1,
        |c| c.logon.1 += 1,
    ];
    for change in cases {
        let mut bindings = Bindings::default();
        change(bindings.context.as_mut().unwrap());
        let result = start(token(), Some(&mut bindings));
        assert_eq!(result.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
        assert!(!bindings.held.get());
        assert!(!bindings.waited.get());
        assert!(
            bindings.ready.display,
            "claim must not have consumed readiness"
        );
    }
    let mut bindings = Bindings {
        context: None,
        ..Bindings::default()
    };
    assert_eq!(
        start(token(), Some(&mut bindings)).unwrap_err().kind(),
        io::ErrorKind::PermissionDenied,
    );
    assert!(bindings.ready.display);
}

#[test]
fn unavailable_native_bindings_fail_closed() {
    assert_eq!(
        start(token(), None).unwrap_err().kind(),
        io::ErrorKind::Unsupported,
    );
}

#[test]
fn duplicate_owner_fails_without_serving_or_releasing_incumbent() {
    let mut bindings = Bindings::default();
    let context = bindings.authorized_sign_in(3).unwrap();
    let incumbent = bindings.claim(&context).unwrap();
    bindings.context = Some(context);
    let result = start(token(), Some(&mut bindings));
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::AlreadyExists);
    assert!(bindings.held.get());
    assert!(!bindings.waited.get());
    drop(incumbent);
    assert!(!bindings.held.get());
}

#[test]
fn any_missing_role_releases_the_claim_without_serving() {
    let cases: [fn(&mut Readiness); 7] = [
        |r| r.display = false,
        |r| r.gpu = false,
        |r| r.audio = false,
        |r| r.battery = false,
        |r| r.tray_events = false,
        |r| r.service_client = false,
        |r| r.stop = false,
    ];
    for change in cases {
        let mut bindings = Bindings::default();
        change(&mut bindings.ready);
        bindings.subscription_error = Some(io::ErrorKind::InvalidData);
        let result = start(token(), Some(&mut bindings));
        assert_eq!(result.unwrap_err().kind(), io::ErrorKind::Unsupported);
        assert!(!bindings.held.get());
        assert!(!bindings.waited.get());
    }
}

#[test]
fn subscription_open_failure_releases_claim_without_serving() {
    let mut bindings = Bindings {
        subscription_error: Some(io::ErrorKind::PermissionDenied),
        ..Bindings::default()
    };
    let result = start(token(), Some(&mut bindings));
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
    assert!(!bindings.held.get());
    assert!(!bindings.waited.get());
}

#[test]
fn confirmed_stop_and_stop_failure_both_release_the_claim() {
    for stop_error in [false, true] {
        let mut bindings = Bindings {
            stop_error,
            ..Bindings::default()
        };
        let result = start(token(), Some(&mut bindings));
        if stop_error {
            assert_eq!(result.unwrap_err().kind(), io::ErrorKind::BrokenPipe);
        } else {
            result.unwrap();
        }
        assert!(bindings.waited.get());
        assert!(!bindings.held.get());
    }
}

#[test]
fn sign_in_entry_rejects_arguments_before_reading_launch_evidence() {
    for argument in ["", "integrated", "--help"] {
        let error = entry(std::iter::once(argument.into())).unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
        assert_eq!(error.to_string(), "unexpected Session Agent arguments");
    }
}

#[test]
fn native_endpoint_readiness_reports_only_the_selected_complete_gpu_contract() {
    let ready = Readiness::native_endpoints();
    assert_eq!(ready.gpu, cfg!(feature = "agent-gpu-v1"));
    // No other role is claimed by this constructor.
    for (role, value) in [
        ("display", ready.display),
        ("audio", ready.audio),
        ("battery", ready.battery),
        ("tray events", ready.tray_events),
        ("service client", ready.service_client),
        ("stop", ready.stop),
    ] {
        assert!(!value, "{role}");
    }
    let mut bindings = Bindings {
        ready,
        ..Bindings::default()
    };
    let result = start(token(), Some(&mut bindings));
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::Unsupported);
    assert!(!bindings.held.get());
}

mod working_directory {
    use std::{cell::RefCell, path::PathBuf};

    use super::*;

    const INSTALL: &str = r"C:\Program Files\OpenBlade";

    fn bind(
        current: io::Result<PathBuf>,
        parent: Option<&str>,
        set_result: io::Result<()>,
    ) -> (io::Result<()>, Vec<PathBuf>) {
        let set = RefCell::new(Vec::new());
        let result = bind_working_directory(current, parent.map(Path::new), |path| {
            set.borrow_mut().push(path.to_owned());
            set_result
        });
        (result, set.into_inner())
    }

    #[test]
    fn the_sign_in_action_directory_is_left_alone() {
        let (result, set) = bind(Ok(INSTALL.into()), Some(INSTALL), Ok(()));
        result.unwrap();
        assert!(set.is_empty());
    }

    // Windows Application Restart cannot set a working directory, so a restarted
    // Agent starts anywhere. It is admitted by Origin, and then bound to the
    // verified install directory instead of being refused.
    #[test]
    fn a_restarted_agent_is_bound_to_the_verified_install_directory() {
        for current in [
            Ok(PathBuf::from(r"C:\Windows\System32")),
            Ok(PathBuf::from(r"C:\Users\Public")),
            Err(io::ErrorKind::NotFound.into()),
        ] {
            let (result, set) = bind(current, Some(INSTALL), Ok(()));
            result.unwrap();
            assert_eq!(set, [PathBuf::from(INSTALL)]);
        }
    }

    #[test]
    fn an_unbindable_or_unknown_install_directory_is_denied() {
        let (result, _) = bind(
            Ok(r"C:\Elsewhere".into()),
            Some(INSTALL),
            Err(io::ErrorKind::NotFound.into()),
        );
        assert_eq!(result.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
        let (result, set) = bind(Ok(INSTALL.into()), None, Ok(()));
        assert_eq!(result.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
        assert!(set.is_empty());
    }
}
