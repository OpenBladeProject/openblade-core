//! How the installed Agent ends, as managed does. A deliberate stop exits 0
//! after it unregisters Windows Application Restart. A launch that is not
//! admitted (origin, token, working directory, an owned stop endpoint) is an
//! ordinary failure exit that Windows does not restart. A role fault or a
//! panic, once the Agent served, finishes draining, releases Origin and custody
//! in the normal order, reports its diagnostic and only then fails fast, so
//! Windows Error Reporting sees a crash and, for an Agent that ran at least 60
//! seconds, restarts it as the managed Agent is restarted. The fail-fast is a
//! seam so a test can observe it without ending the test process.
use std::{
    io,
    panic::{AssertUnwindSafe, catch_unwind},
    process::ExitCode,
};

/// Why `entry_installed` did not end in a deliberate stop.
#[derive(Debug)]
pub(crate) enum Failure {
    /// The launch was not admitted, the singleton was lost or the origin was
    /// retired. Nothing was served that Windows should restart.
    Denied(io::Error),
    /// A selected role, the status watch or a supplier failed after admission.
    Fault(io::Error),
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
    fail_fast: impl FnOnce(),
) -> ExitCode {
    match outcome {
        Outcome::Stopped => ExitCode::SUCCESS,
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

/// Split a result that came from the running roles: an origin that was retired
/// under the Agent (an update, for one) is an ordinary exit, anything else is a
/// fault.
pub(crate) fn classify_run(
    result: io::Result<()>,
    is_origin: impl Fn(&io::Error) -> bool,
) -> Result<(), Failure> {
    result.map_err(|error| {
        if is_origin(&error) {
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
    fn a_retired_origin_is_an_ordinary_exit_and_any_other_run_error_is_a_fault() {
        let origin = |error: &io::Error| error.kind() == io::ErrorKind::NotFound;
        assert!(classify_run(Ok(()), origin).is_ok());
        assert!(matches!(
            classify_run(Err(io::ErrorKind::NotFound.into()), origin),
            Err(Failure::Denied(_))
        ));
        assert!(matches!(
            classify_run(Err(io::ErrorKind::BrokenPipe.into()), origin),
            Err(Failure::Fault(_))
        ));
    }
}
