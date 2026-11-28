//! PURE allowlist: no native handle acquisition, token, process or pipe probes.
use super::*;
use std::{
    cell::{Cell, RefCell},
    rc::Rc,
};
use windows_sys::Win32::Foundation::{WAIT_ABANDONED, WAIT_FAILED};

fn fixture() -> Snapshot {
    Snapshot {
        pid: 42,
        created: 100,
        image_path: vec![65],
        process_session: 0,
        token_session: 0,
        sid: "S-1-5-18".into(),
        authentication: (0, 999),
        primary: true,
    }
}

fn is_denied<T>(result: Result<T>) {
    assert!(matches!(result, Err(InstanceError::Denied(_))));
}

#[derive(Debug)]
struct Resource(Rc<Cell<u32>>);
impl Drop for Resource {
    fn drop(&mut self) {
        self.0.set(self.0.get() + 1);
    }
}

#[test]
fn capture_validation_failure_never_duplicates() {
    // Synthetic failures at native original-handle validation, not claims about
    // Windows behavior. No fabricated raw handle is passed to an OS function.
    for code in [6, 5, 87] {
        let duplicates = Cell::new(0);
        let owned_closes = Rc::new(Cell::new(0));
        let result = capture_checked(
            || Err(io::Error::from_raw_os_error(code)),
            || {
                duplicates.set(duplicates.get() + 1);
                Ok(Resource(owned_closes.clone()))
            },
            |_| panic!("invalid original must not be waited on"),
            |_| panic!("invalid original must not be queried"),
        );
        is_denied(result);
        assert_eq!(duplicates.get(), 0);
        assert_eq!(owned_closes.get(), 0);
    }
}

#[test]
fn capture_requires_full_query_and_synchronize_rights() {
    for access in [
        0,
        PROCESS_QUERY_INFORMATION,
        PROCESS_SYNCHRONIZE,
        0x1000 | PROCESS_SYNCHRONIZE,
    ] {
        let duplicates = Cell::new(0);
        is_denied(capture_checked(
            || require_rights(access),
            || {
                duplicates.set(duplicates.get() + 1);
                Ok(())
            },
            |_| Ok(()),
            |_| Ok(fixture()),
        ));
        assert_eq!(duplicates.get(), 0);
    }
    assert!(require_rights(REQUIRED_ACCESS).is_ok());
}

#[test]
fn capture_duplicates_once_and_closes_returned_owner() {
    let owned_closes = Rc::new(Cell::new(0));
    let duplicates = Cell::new(0);
    let waits = Cell::new(0);
    let (owned, snapshot) = capture_checked(
        || Ok(()),
        || {
            duplicates.set(duplicates.get() + 1);
            Ok(Resource(owned_closes.clone()))
        },
        |_| {
            waits.set(waits.get() + 1);
            Ok(())
        },
        |_| Ok(fixture()),
    )
    .unwrap();
    assert_eq!(snapshot, fixture());
    assert_eq!(duplicates.get(), 1);
    assert_eq!(waits.get(), 2);
    assert_eq!(owned_closes.get(), 0);
    drop(owned);
    assert_eq!(owned_closes.get(), 1);
}

#[test]
fn capture_duplicate_failure_never_waits_or_queries() {
    is_denied(capture_checked::<Resource>(
        || Ok(()),
        || Err(denied("duplicate failed")),
        |_| panic!("no owned handle"),
        |_| panic!("no owned handle"),
    ));
}

#[test]
fn capture_snapshot_failures_close_new_owner_once_after_final_wait() {
    // Each native snapshot subquery has the same fallible return path.
    for stage in [
        "PID",
        "times",
        "image path",
        "process session",
        "open token",
        "token identity",
    ] {
        let closes = Rc::new(Cell::new(0));
        let waits = Cell::new(0);
        is_denied(capture_checked(
            || Ok(()),
            || Ok(Resource(closes.clone())),
            |_| {
                waits.set(waits.get() + 1);
                Ok(())
            },
            |_| Err(denied(stage).into()),
        ));
        assert_eq!(waits.get(), 2);
        assert_eq!(closes.get(), 1);
    }
}

#[test]
fn capture_exit_or_wait_failure_closes_new_owner_once() {
    for failure_at in [1, 2] {
        for exited in [false, true] {
            let closes = Rc::new(Cell::new(0));
            let waits = Cell::new(0);
            let result = capture_checked(
                || Ok(()),
                || Ok(Resource(closes.clone())),
                |_| {
                    waits.set(waits.get() + 1);
                    if waits.get() == failure_at {
                        if exited {
                            Err(InstanceError::Exited)
                        } else {
                            Err(denied("wait failed").into())
                        }
                    } else {
                        Ok(())
                    }
                },
                |_| Ok(fixture()),
            );
            assert_eq!(matches!(result, Err(InstanceError::Exited)), exited);
            assert!(result.is_err());
            assert_eq!(closes.get(), 1);
        }
    }
}

#[test]
fn capture_invalid_snapshot_closes_new_owner_once() {
    let closes = Rc::new(Cell::new(0));
    is_denied(capture_checked(
        || Ok(()),
        || Ok(Resource(closes.clone())),
        |_| Ok(()),
        |_| {
            let mut value = fixture();
            value.pid = 0;
            Ok(value)
        },
    ));
    assert_eq!(closes.get(), 1);
}

#[test]
fn signaled_exit_259_is_exit_and_unknown_waits_are_denial() {
    // Exit code 259 never enters classification: only the original wait decides.
    let _actual_exit_code = 259;
    assert!(matches!(
        classify_wait(WAIT_OBJECT_0),
        Err(InstanceError::Exited)
    ));
    assert!(classify_wait(WAIT_TIMEOUT).is_ok());
    for result in [WAIT_FAILED, WAIT_ABANDONED, 259, 7] {
        is_denied(classify_wait(result));
    }
}

#[test]
fn sticky_exit_and_denial_never_query_or_revive() {
    for exited in [false, true] {
        let mut state = State::Live;
        let failure = if exited {
            InstanceError::Exited
        } else {
            denied("lost evidence").into()
        };
        assert!(state.remember::<()>(Err(failure)).is_err());
        for _ in 0..2 {
            let result: Result<()> = state.bracket(
                || panic!("sticky loss must not wait/reacquire"),
                || panic!("sticky loss must not query"),
            );
            assert_eq!(matches!(result, Err(InstanceError::Exited)), exited);
        }
    }
}

#[test]
fn same_pid_replacement_and_changed_metadata_are_denied() {
    let original = fixture();
    assert!(compare_snapshot(&original, &fixture()).is_ok());
    for field in 0..6 {
        let mut replacement = fixture();
        match field {
            0 => replacement.created += 1,
            1 => replacement.image_path.push(66),
            2 => replacement.authentication.1 += 1,
            3 => replacement.sid = "S-1-5-19".into(),
            4 => {
                replacement.process_session = 1;
                replacement.token_session = 1;
            }
            _ => replacement.pid += 1,
        }
        assert!(replacement.pid == original.pid || field == 5);
        let mut state = State::Live;
        is_denied(state.bracket(|| Ok(()), || compare_snapshot(&original, &replacement)));
        is_denied(state.bracket(|| panic!("replacement cannot revive"), || Ok(())));
    }
}

#[test]
fn service_requires_actual_system_primary_token_and_both_zero_sessions() {
    assert!(fixture().require_service().is_ok());
    for field in 0..4 {
        let mut wrong = fixture();
        match field {
            0 => wrong.sid = "S-1-5-19".into(),
            1 => wrong.process_session = 1,
            2 => wrong.token_session = 1,
            _ => wrong.primary = false,
        }
        let mut state = State::Live;
        is_denied(verify_service_peer(
            &mut state,
            &wrong,
            || Ok(()),
            || panic!("wrong service must not query a pipe"),
        ));
        is_denied(verify_service_peer(
            &mut state,
            &fixture(),
            || panic!("denial is sticky"),
            || Ok((42, 0)),
        ));
    }
}

#[test]
fn peer_checks_original_before_and_after_actual_lookup() {
    let events = RefCell::new(Vec::new());
    assert!(
        verify_service_peer(
            &mut State::Live,
            &fixture(),
            || {
                events.borrow_mut().push("original");
                Ok(())
            },
            || {
                events.borrow_mut().push("pipe");
                Ok((42, 0))
            },
        )
        .is_ok()
    );
    assert_eq!(*events.borrow(), ["original", "pipe", "original"]);
}

#[test]
fn wrong_or_unknown_pipe_peer_is_sticky_denial() {
    for peer in [(0, 0), (43, 0), (42, 1)] {
        let mut state = State::Live;
        is_denied(verify_service_peer(
            &mut state,
            &fixture(),
            || Ok(()),
            || Ok(peer),
        ));
        is_denied(verify_service_peer(
            &mut state,
            &fixture(),
            || panic!("no old-stream upgrade"),
            || Ok((42, 0)),
        ));
    }
}

#[test]
fn peer_query_failure_or_cancellation_rechecks_then_denies() {
    for kind in [io::ErrorKind::BrokenPipe, io::ErrorKind::Interrupted] {
        let checks = Cell::new(0);
        let mut state = State::Live;
        is_denied(verify_service_peer(
            &mut state,
            &fixture(),
            || {
                checks.set(checks.get() + 1);
                Ok(())
            },
            || Err(kind.into()),
        ));
        assert_eq!(checks.get(), 2);
        is_denied(verify_service_peer(
            &mut state,
            &fixture(),
            || panic!("sticky failure"),
            || Ok((42, 0)),
        ));
    }
}

#[test]
fn exit_during_peer_lookup_retires_but_failed_check_denies() {
    for exited in [true, false] {
        for query_failed in [true, false] {
            let checks = Cell::new(0);
            let mut state = State::Live;
            let result = verify_service_peer(
                &mut state,
                &fixture(),
                || {
                    checks.set(checks.get() + 1);
                    if checks.get() == 1 {
                        Ok(())
                    } else if exited {
                        Err(InstanceError::Exited)
                    } else {
                        Err(denied("original query/wait failed").into())
                    }
                },
                || {
                    if query_failed {
                        Err(io::ErrorKind::BrokenPipe.into())
                    } else {
                        Ok((42, 0))
                    }
                },
            );
            assert_eq!(matches!(result, Err(InstanceError::Exited)), exited);
            assert_eq!(checks.get(), 2);
            let next = verify_service_peer(
                &mut state,
                &fixture(),
                || panic!("no revival"),
                || Ok((42, 0)),
            );
            assert_eq!(matches!(next, Err(InstanceError::Exited)), exited);
        }
    }
}

#[test]
fn lost_original_before_pipe_lookup_never_queries_peer() {
    for exited in [true, false] {
        let result = verify_service_peer(
            &mut State::Live,
            &fixture(),
            || {
                if exited {
                    Err(InstanceError::Exited)
                } else {
                    Err(denied("unknown original").into())
                }
            },
            || panic!("untrusted original must not query a peer"),
        );
        assert_eq!(matches!(result, Err(InstanceError::Exited)), exited);
    }
}
