//! How the installed Agent ends, as managed does. A deliberate stop exits after it
//! unregisters Windows Application Restart, and nothing that happens while the
//! roles drain changes that: a drain error is reported but the exit stays
//! ordinary. A launch that is not admitted (origin, token, working directory), an
//! origin retired under the Agent and a drain after a deliberate stop are
//! ordinary failure exits. A pipe or stop endpoint that another Agent already
//! owns is a quiet exit 0, as in managed. A role fault after the Agent served
//! finishes draining, releases Origin and custody in the normal order, reports
//! its diagnostic and only then fails fast, so Windows Error Reporting sees a
//! crash and, as expected pending attended validation, restarts it. A panic is
//! caught after its unwind has released what the entry owned; the roles polled
//! by the panicking future are dropped rather than drained. The fail-fast is a
//! seam so a test can observe it without ending the test process.
use std::{
    io,
    panic::{AssertUnwindSafe, catch_unwind},
    process::ExitCode,
};

/// What another Agent owns, with the OS error that said so.
#[derive(Debug, Clone, Copy, Eq, PartialEq)]
pub(crate) enum Owned {
    Pipe(Option<i32>),
    StopEndpoint(Option<i32>),
}

/// Why `entry_installed` did not end in a deliberate stop.
#[derive(Debug)]
pub(crate) enum Failure {
    /// The launch was not admitted, the singleton was lost or the origin was
    /// retired. Nothing was served that Windows should restart.
    Denied(io::Error),
    /// A selected role, the status watch or a supplier failed after admission.
    Fault(io::Error),
    /// Another Agent already owns the stop event or a pipe. Managed returns 0
    /// and writes one trace line, kept here as a non-terminal note.
    Quiet(Owned),
}

pub(crate) enum Outcome {
    Stopped,
    Failed(Failure),
    Panicked,
}

/// Run the entry. Everything it owns, Origin included, is released before this
/// returns, and a panic is caught after the unwind has released it.
pub(crate) fn guarded(entry: impl FnOnce() -> Result<(), Failure>) -> Outcome {
    match catch_unwind(AssertUnwindSafe(entry)) {
        Ok(Ok(())) => Outcome::Stopped,
        Ok(Err(failure)) => Outcome::Failed(failure),
        Err(_) => Outcome::Panicked,
    }
}

/// `report` runs before `fail_fast` so the diagnostic is persisted first.
pub(crate) fn conclude(
    outcome: Outcome,
    report: impl FnOnce(&io::Error),
    note: impl FnOnce(Owned),
    fail_fast: impl FnOnce(),
) -> ExitCode {
    match outcome {
        Outcome::Stopped => ExitCode::SUCCESS,
        Outcome::Failed(Failure::Quiet(owned)) => {
            note(owned);
            ExitCode::SUCCESS
        }
        Outcome::Failed(Failure::Denied(error)) => {
            report(&error);
            ExitCode::FAILURE
        }
        Outcome::Failed(Failure::Fault(error)) => {
            report(&error);
            fail_fast();
            ExitCode::FAILURE
        }
        Outcome::Panicked => {
            report(&io::Error::other("native Agent panicked"));
            fail_fast();
            ExitCode::FAILURE
        }
    }
}

/// The Agent's stop event is first-instance: another Agent already owns it.
/// `existing` is the OS error that said the event already existed.
pub(crate) fn singleton(existing: Option<i32>) -> Result<(), Failure> {
    match existing {
        Some(code) => Err(Failure::Quiet(Owned::StopEndpoint(Some(code)))),
        None => Ok(()),
    }
}

type Check = fn(&io::Error) -> bool;
type Conflict = fn(&io::Error) -> Option<i32>;

/// Classify the result of the running roles. After a deliberate stop any
/// failure is a drain error: it is reported, but the exit stays ordinary and is
/// never raised as a crash. Otherwise another Agent owning a pipe is quiet, an
/// origin retired under the Agent (an update, for one) is an ordinary exit, and
/// anything else after admission is a fault.
pub(crate) fn classify_run(result: io::Result<()>, deliberate: bool) -> Result<(), Failure> {
    #[cfg(openblade_agent_endpoint)]
    let (is_origin, is_conflict): (Check, Conflict) = (
        super::diagnostics::is_origin,
        super::diagnostics::claim_conflict,
    );
    #[cfg(not(openblade_agent_endpoint))]
    let (is_origin, is_conflict): (Check, Conflict) = (|_| false, |_| None);
    result.map_err(|error| {
        if deliberate {
            Failure::Denied(error)
        } else if let Some(code) = is_conflict(&error) {
            Failure::Quiet(Owned::Pipe(Some(code)))
        } else if is_origin(&error) {
            Failure::Denied(error)
        } else {
            Failure::Fault(error)
        }
    })
}

#[allow(unsafe_code)]
#[cfg_attr(test, allow(dead_code))]
pub(crate) fn raise_fail_fast() {
    use windows_sys::Win32::{
        System::Diagnostics::Debug::RaiseFailFastException,
        System::WindowsProgramming::FAIL_FAST_GENERATE_EXCEPTION_ADDRESS,
    };
    // SAFETY: NULL records ask Windows to build the exception itself; the call
    // does not return.
    unsafe {
        RaiseFailFastException(
            std::ptr::null(),
            std::ptr::null(),
            FAIL_FAST_GENERATE_EXCEPTION_ADDRESS,
        );
    }
}

#[cfg(test)]
mod tests {
    use std::cell::{Cell, RefCell};

    use super::*;

    fn run(outcome: Outcome) -> (ExitCode, Vec<&'static str>) {
        let log = RefCell::new(Vec::new());
        let code = conclude(
            outcome,
            |_| log.borrow_mut().push("report"),
            |_| log.borrow_mut().push("note"),
            || log.borrow_mut().push("fail-fast"),
        );
        (code, log.into_inner())
    }

    #[test]
    fn a_deliberate_stop_and_an_admission_denial_never_fail_fast() {
        let (code, log) = run(Outcome::Stopped);
        assert_eq!(code, ExitCode::SUCCESS);
        assert!(log.is_empty());
        let denied = Outcome::Failed(Failure::Denied(io::ErrorKind::PermissionDenied.into()));
        let (code, log) = run(denied);
        assert_eq!(code, ExitCode::FAILURE);
        assert_eq!(log, ["report"]);
    }

    #[test]
    fn a_role_fault_or_a_panic_reports_first_and_then_fails_fast() {
        let fault = Outcome::Failed(Failure::Fault(io::ErrorKind::BrokenPipe.into()));
        assert_eq!(run(fault).1, ["report", "fail-fast"]);
        assert_eq!(run(Outcome::Panicked).1, ["report", "fail-fast"]);
    }

    struct Release<'a>(&'a RefCell<Vec<&'static str>>);

    impl Drop for Release<'_> {
        fn drop(&mut self) {
            self.0.borrow_mut().push("released");
        }
    }

    #[test]
    fn everything_the_entry_owns_is_released_before_the_report_and_the_fail_fast() {
        let log = RefCell::new(Vec::new());
        for panics in [false, true] {
            log.borrow_mut().clear();
            let outcome = guarded(|| {
                let _origin = Release(&log);
                if panics {
                    panic!("role bug");
                }
                Err(Failure::Fault(io::ErrorKind::TimedOut.into()))
            });
            conclude(
                outcome,
                |_| log.borrow_mut().push("report"),
                |_| log.borrow_mut().push("note"),
                || log.borrow_mut().push("fail-fast"),
            );
            assert_eq!(*log.borrow(), ["released", "report", "fail-fast"]);
        }
    }

    #[test]
    fn a_clean_stop_is_passed_through() {
        let ran = Cell::new(false);
        assert!(matches!(
            guarded(|| {
                ran.set(true);
                Ok(())
            }),
            Outcome::Stopped
        ));
        assert!(ran.get());
    }

    #[test]
    fn another_agent_owning_the_stop_event_is_a_quiet_exit_and_the_first_is_not() {
        assert!(singleton(None).is_ok());
        let failure = singleton(Some(183)).unwrap_err();
        assert!(matches!(
            failure,
            Failure::Quiet(Owned::StopEndpoint(Some(183)))
        ));
        // One non-terminal note, no failure report and no crash.
        let (code, log) = run(Outcome::Failed(failure));
        assert_eq!(code, ExitCode::SUCCESS);
        assert_eq!(log, ["note"]);
    }

    #[test]
    fn a_clean_run_is_a_clean_exit() {
        assert!(classify_run(Ok(()), false).is_ok());
        assert!(classify_run(Ok(()), true).is_ok());
    }

    #[test]
    fn a_failure_after_a_deliberate_stop_is_reported_but_never_a_crash() {
        for error in [
            io::Error::from(io::ErrorKind::TimedOut),
            io::Error::from(io::ErrorKind::BrokenPipe),
        ] {
            let outcome = Outcome::Failed(classify_run(Err(error), true).unwrap_err());
            let (code, log) = run(outcome);
            assert_eq!(code, ExitCode::FAILURE);
            assert_eq!(log, ["report"]);
        }
    }

    #[test]
    fn an_unclassified_failure_without_a_stop_is_a_fault() {
        assert!(matches!(
            classify_run(Err(io::ErrorKind::BrokenPipe.into()), false),
            Err(Failure::Fault(_))
        ));
    }

    #[cfg(openblade_agent_endpoint)]
    mod through_the_roles {
        use std::future::pending;

        use super::*;
        use crate::session_native::{
            diagnostics,
            feature_lifetime::{feature, run_all},
        };

        /// What `run_all` returns when this one role fails, with no stop.
        async fn failing(error: io::Error) -> io::Result<()> {
            run_all(
                pending(),
                pending(),
                vec![feature(move |_stop| async move { Err(error) })],
            )
            .await
        }

        #[tokio::test]
        async fn a_retired_origin_flowing_out_of_the_roles_is_an_ordinary_exit() {
            let error = diagnostics::origin(io::Error::from_raw_os_error(5));
            let result = failing(error).await;
            assert!(matches!(
                classify_run(result, false),
                Err(Failure::Denied(_))
            ));
        }

        #[tokio::test]
        async fn a_pipe_claim_conflict_flowing_out_of_the_roles_is_quiet() {
            for code in [5, 231] {
                let result = failing(diagnostics::claim(io::Error::from_raw_os_error(code))).await;
                assert!(matches!(
                    classify_run(result, false),
                    Err(Failure::Quiet(Owned::Pipe(Some(c)))) if c == code
                ));
            }
            // Another claim failure is a listener fault, not a conflict.
            let result = failing(diagnostics::claim(io::Error::from_raw_os_error(2))).await;
            assert!(matches!(
                classify_run(result, false),
                Err(Failure::Fault(_))
            ));
        }

        #[tokio::test]
        async fn a_drain_error_after_a_deliberate_stop_flowing_out_of_run_all_is_reported() {
            // The stop wins, then a role fails while it drains.
            let result = run_all(
                async { Ok(()) },
                pending(),
                vec![feature(|_stop| async {
                    Err(io::Error::other("startup did not retire"))
                })],
            )
            .await;
            assert!(result.is_err());
            assert!(matches!(
                classify_run(result, true),
                Err(Failure::Denied(_))
            ));
        }
    }
}
