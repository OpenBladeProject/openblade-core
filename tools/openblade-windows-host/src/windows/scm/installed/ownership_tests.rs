//! Explicitly in-memory ownership fixtures. No OS discovery, handles or IPC.

use super::*;
use crate::{
    discovery::performance::worker_tests::{
        BlockAt, blocking_worker_with_authority, in_memory_worker,
    },
    installed::readiness::{Caller, FirmwareSource, Logon},
};
use std::sync::Mutex;
use tokio::time::{Duration, Instant, timeout};

const PID: u32 = 42;
const ADMISSION: Admission = Admission {
    epoch: 10,
    session: 1,
    full_adapter: true,
    controller_clear: true,
    awake: true,
};
const CALLER: Caller = Caller {
    pid: Some(77),
    logon: Logon::Interactive,
    session: 1,
    control_session: Some(1),
};

#[derive(Clone, Copy)]
enum Release {
    Confirmed,
    Pending,
    Failed,
    WrongEpoch,
}

struct State {
    loaded: Option<AuthorityEpoch>,
    observed: AuthorityEpoch,
    admission: Admission,
    occupied: bool,
    lost: bool,
    release: Release,
    events: Vec<&'static str>,
}

#[derive(Clone)]
struct InMemoryAuthority(Arc<Mutex<State>>);

fn epoch(pid: u32) -> AuthorityEpoch {
    AuthorityEpoch {
        instance: Arc::new(()),
        launch_pid: pid,
    }
}

impl InMemoryAuthority {
    fn new() -> Self {
        let loaded = epoch(PID);
        Self(Arc::new(Mutex::new(State {
            loaded: Some(loaded.clone()),
            observed: loaded,
            admission: ADMISSION,
            occupied: false,
            lost: false,
            release: Release::Confirmed,
            events: vec![],
        })))
    }

    fn change(&self, update: impl FnOnce(&mut State)) {
        update(&mut self.0.lock().unwrap());
    }
}

impl Authority for InMemoryAuthority {
    fn loaded_generation(&self) -> io::Result<AuthorityEpoch> {
        self.0
            .lock()
            .unwrap()
            .loaded
            .clone()
            .ok_or_else(|| denied("in-memory loaded generation unavailable"))
    }

    fn acquire(&self, expected: &AuthorityEpoch) -> io::Result<Arc<dyn OwnerLease>> {
        let mut state = self.0.lock().unwrap();
        if state.occupied || state.loaded.as_ref() != Some(expected) {
            return Err(denied("in-memory predecessor remains fenced"));
        }
        state.occupied = true;
        state.events.push("acquired");
        Ok(Arc::new(self.clone()))
    }
}

impl OwnerLease for InMemoryAuthority {
    fn observe(&self) -> Ownership {
        let state = self.0.lock().unwrap();
        if state.lost {
            Ownership::Lost
        } else {
            Ownership::Acquired(state.observed.clone(), state.admission)
        }
    }

    fn release(&self) -> io::Result<Ownership> {
        let mut state = self.0.lock().unwrap();
        state.events.push("release");
        match state.release {
            Release::Confirmed => {
                state.occupied = false;
                Ok(Ownership::Retired(state.observed.clone()))
            }
            Release::Pending => Ok(Ownership::Releasing),
            Release::Failed => Err(io::Error::other("in-memory release failed")),
            Release::WrongEpoch => Ok(Ownership::Retired(epoch(PID))),
        }
    }
}

fn idle_source(authority: Option<&dyn Authority>) -> io::Result<FirmwareSource> {
    FirmwareSource::bind(PID, authority, |_| in_memory_worker(|| Ok(())), |_| None)
}

#[test]
fn in_memory_candidate_refuses_missing_or_mismatched_generation_before_worker_factory() {
    let factory = |_| panic!("hardware factory must remain unopened");
    #[cfg(feature = "service-hardware-compile-check")]
    assert!(FirmwareSource::native(PID, None, |_| None).is_err());
    assert!(FirmwareSource::bind(PID, None, factory, |_| None).is_err());
    for loaded in [None, Some(epoch(PID + 1))] {
        let authority = InMemoryAuthority::new();
        authority.change(|state| state.loaded = loaded);
        assert!(FirmwareSource::bind(PID, Some(&authority), factory, |_| None).is_err());
        assert!(authority.0.lock().unwrap().events.is_empty());
    }
    let authority = InMemoryAuthority::new();
    authority.change(|state| state.observed = epoch(PID));
    assert!(FirmwareSource::bind(PID, Some(&authority), factory, |_| None).is_err());
    assert_eq!(authority.0.lock().unwrap().events, ["acquired", "release"]);
}

#[tokio::test]
async fn in_memory_exact_owner_handoff_requires_confirmed_release() {
    let authority = InMemoryAuthority::new();
    let first = idle_source(Some(&authority)).unwrap();
    assert!(idle_source(Some(&authority)).is_err());
    first.stop().await.unwrap();
    first.stop().await.unwrap();
    let next = idle_source(Some(&authority)).unwrap();
    next.stop().await.unwrap();
    assert_eq!(
        authority.0.lock().unwrap().events,
        ["acquired", "release", "acquired", "release"]
    );
}

#[tokio::test]
async fn in_memory_loss_and_stale_authority_epochs_never_revive() {
    for change in 0..4 {
        let authority = InMemoryAuthority::new();
        let binding = Binding::acquire(PID, Some(&authority)).unwrap();
        assert_eq!(binding.authorize(), Some(ADMISSION));
        authority.change(|state| match change {
            0 => state.lost = true,
            1 => state.observed = epoch(PID),
            2 => state.admission.epoch += 1,
            _ => state.admission.full_adapter = false,
        });
        assert_eq!(binding.authorize(), None);
        authority.change(|state| {
            state.lost = false;
            state.observed = state.loaded.clone().unwrap();
            state.admission = ADMISSION;
        });
        assert_eq!(binding.authorize(), None);
        assert!(idle_source(Some(&authority)).is_err());
        binding.retire().unwrap();
    }
}

#[tokio::test]
async fn in_memory_shutdown_and_rollback_preserve_unresolved_owner_fence() {
    for release in [Release::Pending, Release::Failed, Release::WrongEpoch] {
        let authority = InMemoryAuthority::new();
        let source = idle_source(Some(&authority)).unwrap();
        authority.change(|state| state.release = release);
        assert!(source.stop().await.is_err());
        authority.change(|state| state.release = Release::Confirmed);
        if !matches!(release, Release::Pending) {
            assert!(source.stop().await.is_err());
            assert_eq!(authority.0.lock().unwrap().events, ["acquired", "release"]);
        } else {
            // Keep the original pending attempt unresolved for this fixture.
            authority.change(|state| state.release = Release::Pending);
        }
        assert!(
            source
                .observe(CALLER, Instant::now() + Duration::from_secs(1))
                .await
                .unwrap()
                .is_none()
        );
        drop(source);
        // Replacement and rollback use the same independent retained fence.
        assert!(idle_source(Some(&authority)).is_err());
        assert!(idle_source(Some(&authority)).is_err());
    }
}

#[tokio::test]
async fn in_memory_pending_owner_retirement_can_confirm_without_reviving_admission() {
    let authority = InMemoryAuthority::new();
    let source = idle_source(Some(&authority)).unwrap();
    authority.change(|state| state.release = Release::Pending);
    assert!(source.stop().await.is_err());
    assert!(idle_source(Some(&authority)).is_err());
    authority.change(|state| state.release = Release::Confirmed);
    source.stop().await.unwrap();
    assert!(
        source
            .observe(CALLER, Instant::now() + Duration::from_secs(1))
            .await
            .unwrap()
            .is_none()
    );
    let replacement = idle_source(Some(&authority)).unwrap();
    replacement.stop().await.unwrap();
}

#[tokio::test]
async fn in_memory_native_retirement_precedes_owner_release_and_replacement() {
    let authority = InMemoryAuthority::new();
    let mut held = None;
    let source = FirmwareSource::bind(
        PID,
        Some(&authority),
        |binding| {
            let (worker, control) =
                blocking_worker_with_authority(BlockAt::Retire, None, move || binding.authorize());
            held = Some(control);
            worker
        },
        |_| None,
    )
    .unwrap();
    let held = held.unwrap();
    let mut read = source.observe(CALLER, Instant::now() + Duration::from_secs(2));
    tokio::select! {
        entered = held.entered => entered.unwrap(),
        _ = &mut read => panic!("read must wait at in-memory callback drain"),
    }
    assert_eq!(held.submitted.load(Ordering::Acquire), 4);
    assert!(
        timeout(Duration::from_millis(20), source.stop())
            .await
            .is_err()
    );
    assert_eq!(authority.0.lock().unwrap().events, ["acquired"]);
    assert!(idle_source(Some(&authority)).is_err());
    drop(read);
    held.release.send(()).unwrap();
    source.stop().await.unwrap();
    assert_eq!(authority.0.lock().unwrap().events, ["acquired", "release"]);
    let replacement = idle_source(Some(&authority)).unwrap();
    replacement.stop().await.unwrap();
}

#[tokio::test]
async fn in_memory_worker_cleanup_failure_preserves_owner_across_rollback() {
    let authority = InMemoryAuthority::new();
    let source = FirmwareSource::bind(
        PID,
        Some(&authority),
        |_| in_memory_worker(|| Err(io::Error::other("in-memory native drain failed"))),
        |_| None,
    )
    .unwrap();
    assert!(
        source
            .observe(CALLER, Instant::now() + Duration::from_secs(1))
            .await
            .is_err()
    );
    assert!(source.stop().await.is_err());
    drop(source);
    assert!(idle_source(Some(&authority)).is_err());
    assert_eq!(authority.0.lock().unwrap().events, ["acquired"]);
}

#[tokio::test]
async fn in_memory_owner_loss_during_native_call_stops_traffic_and_delivery() {
    for (at, expected) in [
        (BlockAt::Verify, 0),
        (BlockAt::Submit, 1),
        (BlockAt::Retire, 4),
    ] {
        let authority = InMemoryAuthority::new();
        let mut held = None;
        let source = FirmwareSource::bind(
            PID,
            Some(&authority),
            |binding| {
                let (worker, control) =
                    blocking_worker_with_authority(at, None, move || binding.authorize());
                held = Some(control);
                worker
            },
            |_| panic!("lost owner must not project a firmware result"),
        )
        .unwrap();
        let held = held.unwrap();
        let mut read = source.observe(CALLER, Instant::now() + Duration::from_secs(2));
        tokio::select! {
            entered = held.entered => entered.unwrap(),
            _ = &mut read => panic!("expected in-memory native boundary"),
        }
        authority.change(|state| state.lost = true);
        held.release.send(()).unwrap();
        assert!(read.await.unwrap().is_none());
        assert_eq!(held.submitted.load(Ordering::Acquire), expected);
        source.stop().await.unwrap();
    }
}

#[tokio::test]
async fn in_memory_bound_getter_projects_only_the_live_owner_epoch() {
    use crate::service_status::Snapshot;
    use serde_json::{Value, json};

    const REPLAY: &[u8] = include_bytes!("../../../../tests/fixtures/observe-state-v91.frames");
    let length = u32::from_le_bytes(REPLAY[..4].try_into().unwrap()) as usize;
    let reply: Value = serde_json::from_slice(&REPLAY[8 + length..]).unwrap();
    let mut guards = reply["status"].clone();
    guards["confirmedState"] = Value::Null;
    let status: Snapshot = serde_json::from_value(guards).unwrap();
    let authority = InMemoryAuthority::new();
    let mut held = None;
    let source = FirmwareSource::bind(
        PID,
        Some(&authority),
        |binding| {
            let (worker, control) =
                blocking_worker_with_authority(BlockAt::Retire, None, move || binding.authorize());
            held = Some(control);
            worker
        },
        move |admission| {
            assert_eq!(admission, ADMISSION);
            Some(status.clone())
        },
    )
    .unwrap();
    let held = held.unwrap();
    let mut read = source.observe(CALLER, Instant::now() + Duration::from_secs(2));
    tokio::select! {
        entered = held.entered => entered.unwrap(),
        _ = &mut read => panic!("expected in-memory native drain"),
    }
    held.release.send(()).unwrap();
    let observed = serde_json::to_value(read.await.unwrap().unwrap()).unwrap();
    assert_eq!(
        observed["confirmedState"],
        json!({
            "performance": 2, "fans": 0, "battery": null,
            "lighting": null, "hyperBoostActive": null,
        })
    );
    source.stop().await.unwrap();
}

#[tokio::test]
async fn in_memory_checked_candidate_shutdown_waits_before_rollback_acquisition() {
    use crate::{installed::*, scm_host::Controls, scm_identity::ScmEvidence};
    use std::path::PathBuf;
    use tokio::sync::oneshot;
    use windows_sys::Win32::System::Services::{
        SERVICE_RUNNING, SERVICE_START_PENDING, SERVICE_STOPPED, SERVICE_WIN32_OWN_PROCESS,
    };

    let authority = InMemoryAuthority::new();
    let mut held = None;
    let source = FirmwareSource::bind(
        PID,
        Some(&authority),
        |binding| {
            let (worker, control) =
                blocking_worker_with_authority(BlockAt::Submit, None, move || binding.authorize());
            held = Some(control);
            worker
        },
        |_| None,
    )
    .unwrap();
    let held = held.unwrap();
    let mut read = source.observe(CALLER, Instant::now() + Duration::from_secs(2));
    tokio::select! {
        entered = held.entered => entered.unwrap(),
        _ = &mut read => panic!("expected in-memory native submission"),
    }
    let (stop, stopped) = oneshot::channel();
    let mut stop = Some(stop);
    let controls = Controls::new();
    let statuses = std::cell::RefCell::new(vec![]);
    let candidate = run_candidate(
        &controls,
        stopped,
        || {
            Ok(ProcessEvidence {
                has_arguments: false,
                selected_image: None,
                program_files: PathBuf::from(r"C:\Program Files"),
                image: PathBuf::from(r"C:\Program Files\OpenBlade\OpenBlade.Service.exe"),
                pid: PID,
                local_system: true,
                session: 0,
            })
        },
        |_| {
            Ok(ScmEvidence {
                process_id: PID,
                service_type: SERVICE_WIN32_OWN_PROCESS,
                state: SERVICE_START_PENDING,
                matches_config: true,
            })
        },
        |_| Ok(source.clone()),
        async |_, source, shutdown, _| {
            Ok((
                Readiness {
                    pipe_name: PIPE_NAME,
                    server_pid: PID,
                    protocol_version: PROTOCOL_VERSION,
                    correlated_status: true,
                },
                async move {
                    let _ = shutdown.await;
                    source.stop().await
                },
            ))
        },
        |status| {
            statuses
                .borrow_mut()
                .push((status.dwCurrentState, status.dwWin32ExitCode));
            if status.dwCurrentState == SERVICE_RUNNING {
                let _ = stop.take().unwrap().send(());
            }
            Ok(())
        },
    );
    tokio::pin!(candidate);
    assert!(
        timeout(Duration::from_millis(20), &mut candidate)
            .await
            .is_err()
    );
    assert_eq!(authority.0.lock().unwrap().events, ["acquired"]);
    assert!(idle_source(Some(&authority)).is_err());
    held.release.send(()).unwrap();
    candidate.await.unwrap();
    assert_eq!(statuses.borrow().last(), Some(&(SERVICE_STOPPED, 0)));
    let rollback = idle_source(Some(&authority)).unwrap();
    rollback.stop().await.unwrap();
}
