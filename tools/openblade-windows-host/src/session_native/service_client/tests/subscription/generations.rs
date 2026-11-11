//! Synthetic generation proofs and duplex streams only; no Windows authority claim.
use super::*;
use std::rc::Rc;

#[derive(Clone, Copy)]
enum ProofState {
    Live,
    Retired,
    Unavailable,
    Denied,
}

struct Proof {
    pid: NonZeroU32,
    state: Cell<ProofState>,
}

impl Proof {
    fn live(pid: u32) -> Rc<Self> {
        Rc::new(Self {
            pid: NonZeroU32::new(pid).unwrap(),
            state: Cell::new(ProofState::Live),
        })
    }
}

impl InstalledServiceGeneration for Rc<Proof> {
    fn verify_live_process(&self) -> Result<NonZeroU32, ServiceAuthorityError> {
        match self.state.get() {
            ProofState::Live => Ok(self.pid),
            ProofState::Retired => Err(ServiceAuthorityError::Retired),
            ProofState::Unavailable => Err(ServiceAuthorityError::Unavailable),
            // Even a seemingly transient error in a denied proof stays fatal.
            ProofState::Denied => Err(ServiceAuthorityError::Denied(
                io::ErrorKind::NotFound.into(),
            )),
        }
    }
}

struct Factory {
    current: RefCell<Option<Rc<Proof>>>,
    acquisitions: Cell<usize>,
}

impl Factory {
    fn new(proof: Rc<Proof>) -> Self {
        Self {
            current: RefCell::new(Some(proof)),
            acquisitions: Cell::new(0),
        }
    }
}

impl InstalledServiceAuthority for Factory {
    fn acquire_generation(
        &self,
    ) -> Result<Box<dyn InstalledServiceGeneration + '_>, ServiceAuthorityError> {
        self.acquisitions.set(self.acquisitions.get() + 1);
        let proof = self
            .current
            .borrow()
            .clone()
            .ok_or(ServiceAuthorityError::Unavailable)?;
        proof.verify_live_process()?;
        Ok(Box::new(proof))
    }
}

#[test]
fn in_memory_peer_lookup_exit_race_requires_trusted_retirement() {
    let proof = Proof::live(PID);
    let result = verify_peer(&proof, proof.pid, &(), &|_| {
        proof.state.set(ProofState::Retired);
        Err(io::ErrorKind::NotFound.into())
    });
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::ConnectionAborted);
    proof.state.set(ProofState::Live);
    let result = verify_peer(&proof, proof.pid, &(), &|_| {
        proof.state.set(ProofState::Retired);
        Ok((PID + 1, 0))
    });
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
}

#[tokio::test(start_paused = true)]
async fn in_memory_temporary_absence_acquires_new_proof_after_backoff() {
    let authority = Factory {
        current: RefCell::new(None),
        acquisitions: Cell::new(0),
    };
    let (client, mut server) = duplex(16_384);
    let mut client = Some(client);
    let start = Instant::now();
    let watch = watch_on(
        Some(&authority),
        pending(),
        || Ok(REQUEST_ID.into()),
        async || Ok(client.take().unwrap()),
        |_| Ok((PID, 0)),
        |update| match update {
            StatusUpdate::Reconnecting => {
                assert_eq!(start.elapsed(), Duration::ZERO);
                authority.current.replace(Some(Proof::live(PID)));
                Ok(())
            }
            StatusUpdate::Opened(_) => {
                assert_eq!(start.elapsed(), INITIAL_RECONNECT_DELAY);
                Err(io::ErrorKind::Interrupted.into())
            }
            StatusUpdate::Event(_) => panic!("no event"),
        },
    );
    let serve = async {
        accept(&mut server, REQUEST_ID).await;
        server.write_all(frames()[1]).await.unwrap();
    };
    assert_eq!(
        tokio::join!(watch, serve).0.unwrap_err().kind(),
        io::ErrorKind::Interrupted
    );
    assert_eq!(authority.acquisitions.get(), 2);
}

#[tokio::test(start_paused = true)]
async fn in_memory_retired_final_event_reacquires_independent_generation_even_with_same_pid() {
    for replacement_pid in [PID, PID + 1] {
        let original = Proof::live(PID);
        let replacement = Proof::live(replacement_pid);
        let authority = Factory::new(original.clone());
        let (client, mut first) = duplex(16_384);
        let (client2, mut second) = duplex(16_384);
        let mut connections = VecDeque::from([client, client2]);
        let (updates, mut received) = tokio::sync::mpsc::unbounded_channel();
        let (stop, stopped) = oneshot::channel();
        let peer_pid = Cell::new(PID);
        let watch = watch_on(
            Some(&authority),
            async { stopped.await.unwrap() },
            || Ok(REQUEST_ID.into()),
            async || {
                Ok(connections
                    .pop_front()
                    .expect("unexpected third connection"))
            },
            |_| Ok((peer_pid.get(), 0)),
            |update| {
                updates.send(update).unwrap();
                Ok(())
            },
        );
        let drive = async {
            accept(&mut first, REQUEST_ID).await;
            first
                .write_all(&edit_frame(frames()[1], |f| {
                    f["status"]["revision"] = 99.into()
                }))
                .await
                .unwrap();
            let Some(StatusUpdate::Opened(initial)) = received.recv().await else {
                panic!("first baseline")
            };
            assert_eq!(revision(&initial.into_observation()), 99);
            assert_eq!(
                Rc::strong_count(&original),
                3,
                "stream retains original proof"
            );
            original.state.set(ProofState::Retired);
            authority.current.replace(Some(replacement.clone()));
            // Valid framing from a verifiably retired producer is discarded.
            first.write_all(frames()[2]).await.unwrap();
            assert!(matches!(
                received.recv().await,
                Some(StatusUpdate::Reconnecting)
            ));
            assert_eq!(first.read(&mut [0]).await.unwrap(), 0);
            assert_eq!(authority.acquisitions.get(), 1);
            assert_eq!(
                Rc::strong_count(&original),
                1,
                "old proof released before reconnect"
            );
            peer_pid.set(replacement_pid);
            accept(&mut second, REQUEST_ID).await;
            second.write_all(frames()[1]).await.unwrap();
            let Some(StatusUpdate::Opened(initial)) = received.recv().await else {
                panic!("new baseline")
            };
            assert_eq!(revision(&initial.into_observation()), 1);
            assert_eq!(authority.acquisitions.get(), 2);
            stop.send(()).unwrap();
        };
        assert_eq!(
            tokio::join!(watch, drive).0.unwrap_err().kind(),
            io::ErrorKind::Interrupted
        );
    }
}

#[tokio::test(start_paused = true)]
async fn in_memory_unavailable_proof_retries_but_denial_and_peer_errors_are_fatal() {
    let original = Proof::live(PID);
    let authority = Factory::new(original.clone());
    original.state.set(ProofState::Unavailable);
    let (stop, stopped) = oneshot::channel();
    let (retry, waiting) = oneshot::channel();
    let mut retry = Some(retry);
    let start = Instant::now();
    let watch = watch_on(
        Some(&authority),
        async { stopped.await.unwrap() },
        || panic!("no ID without proof"),
        async || -> io::Result<DuplexStream> { panic!("no stream without proof") },
        |_| unreachable!(),
        |update| {
            assert!(matches!(update, StatusUpdate::Reconnecting));
            retry.take().unwrap().send(()).unwrap();
            Ok(())
        },
    );
    let drive = async {
        waiting.await.unwrap();
        stop.send(()).unwrap();
    };
    assert_eq!(
        tokio::join!(watch, drive).0.unwrap_err().kind(),
        io::ErrorKind::Interrupted
    );
    assert_eq!(
        start.elapsed(),
        Duration::ZERO,
        "canceled while backoff is pending"
    );
    assert_eq!(authority.acquisitions.get(), 1);

    original.state.set(ProofState::Denied);
    let result = watch_on(
        Some(&authority),
        pending(),
        || panic!("denied proof"),
        async || -> io::Result<DuplexStream> { panic!("denied proof") },
        |_| unreachable!(),
        |_| unreachable!(),
    )
    .await;
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
    original.state.set(ProofState::Live);
    let (client, _server) = duplex(16_384);
    let result = read_on(
        Some(&authority),
        pending(),
        || Ok(REQUEST_ID.into()),
        async || Ok(client),
        |_| Err(io::ErrorKind::NotFound.into()),
    )
    .await;
    assert_eq!(error_kind(result), io::ErrorKind::PermissionDenied);
}

#[tokio::test]
async fn in_memory_old_request_never_rebinds_to_same_pid_replacement() {
    let original = Proof::live(PID);
    let authority = Factory::new(original.clone());
    let (client, mut server) = duplex(16_384);
    let read = read_on(
        Some(&authority),
        pending(),
        || Ok(REQUEST_ID.into()),
        async || Ok(client),
        |_| Ok((PID, 0)),
    );
    let serve = async {
        read_payload(&mut server).await.unwrap();
        authority.current.replace(Some(Proof::live(PID)));
        original.state.set(ProofState::Denied);
        server.write_all(super::super::frames().1).await.unwrap();
    };
    assert_eq!(
        error_kind(tokio::join!(read, serve).0),
        io::ErrorKind::PermissionDenied
    );
    assert_eq!(
        authority.acquisitions.get(),
        1,
        "an old stream cannot acquire new proof"
    );
}

#[tokio::test(start_paused = true)]
async fn in_memory_rapid_valid_baselines_and_eof_use_capped_backoff() {
    let authority = Authority::live();
    let attempts = RefCell::new(Vec::new());
    let baselines = Cell::new(0);
    let start = Instant::now();
    let watch = watch_on(
        Some(&authority),
        sleep(Duration::from_secs(20)),
        || Ok(REQUEST_ID.into()),
        async || {
            attempts.borrow_mut().push(start.elapsed().as_millis());
            let (client, mut server) = duplex(16_384);
            tokio::spawn(async move {
                accept(&mut server, REQUEST_ID).await;
                server.write_all(frames()[1]).await.unwrap();
            });
            Ok(client)
        },
        |_| Ok((PID, 0)),
        |update| {
            if matches!(update, StatusUpdate::Opened(_)) {
                baselines.set(baselines.get() + 1);
            }
            Ok(())
        },
    );
    assert_eq!(watch.await.unwrap_err().kind(), io::ErrorKind::Interrupted);
    assert_eq!(
        *attempts.borrow(),
        [0, 250, 750, 1750, 3750, 7750, 12750, 17750]
    );
    assert_eq!(baselines.get(), 8);
}

#[tokio::test(start_paused = true)]
async fn configured_initial_start_pending_reconnects_to_running_with_fresh_authority() {
    use crate::scm_identity::ScmEvidence;
    use windows_sys::Win32::System::Services::{
        SERVICE_RUNNING, SERVICE_START_PENDING, SERVICE_WIN32_OWN_PROCESS,
    };
    struct ScmFactory {
        state: Cell<u32>,
        acquisitions: Cell<usize>,
        original: Rc<Proof>,
    }
    impl InstalledServiceAuthority for ScmFactory {
        fn acquire_generation(
            &self,
        ) -> Result<Box<dyn InstalledServiceGeneration + '_>, ServiceAuthorityError> {
            self.acquisitions.set(self.acquisitions.get() + 1);
            crate::session_native::scm_peer::classify_service(
                ScmEvidence {
                    matches_config: true,
                    service_type: SERVICE_WIN32_OWN_PROCESS,
                    state: self.state.get(),
                    process_id: if self.state.get() == SERVICE_RUNNING {
                        PID
                    } else {
                        0
                    },
                },
                None,
            )?;
            Ok(Box::new(self.original.clone()))
        }
    }
    let authority = ScmFactory {
        state: Cell::new(SERVICE_START_PENDING),
        acquisitions: Cell::new(0),
        original: Proof::live(PID),
    };
    let (client, mut server) = duplex(16_384);
    let mut client = Some(client);
    let start = Instant::now();
    let watch = watch_on(
        Some(&authority),
        pending(),
        || Ok(REQUEST_ID.into()),
        async || Ok(client.take().expect("one fresh connection")),
        |_| Ok((PID, 0)),
        |update| match update {
            StatusUpdate::Reconnecting => {
                assert_eq!(start.elapsed(), Duration::ZERO);
                authority.state.set(SERVICE_RUNNING);
                Ok(())
            }
            StatusUpdate::Opened(_) => {
                assert_eq!(start.elapsed(), INITIAL_RECONNECT_DELAY);
                Err(io::ErrorKind::Interrupted.into())
            }
            StatusUpdate::Event(_) => panic!("no event"),
        },
    );
    let serve = async {
        accept(&mut server, REQUEST_ID).await;
        server.write_all(frames()[1]).await.unwrap();
    };
    assert_eq!(
        tokio::join!(watch, serve).0.unwrap_err().kind(),
        io::ErrorKind::Interrupted
    );
    assert_eq!(authority.acquisitions.get(), 2);
}
