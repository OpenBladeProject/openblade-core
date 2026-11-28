use super::*;
use std::{cell::Cell, cell::RefCell, collections::VecDeque};

struct Script {
    resume: Result<(), Failure>,
    observations: RefCell<VecDeque<Result<Option<u32>, Failure>>>,
    terminated: Cell<bool>,
    resumed: Cell<bool>,
}

impl Script {
    fn new(observations: impl IntoIterator<Item = Result<Option<u32>, Failure>>) -> Self {
        Self {
            resume: Ok(()),
            observations: RefCell::new(observations.into_iter().collect()),
            terminated: Cell::new(false),
            resumed: Cell::new(false),
        }
    }
}
impl ChildControl for Script {
    fn resume(&self) -> Result<(), Failure> {
        self.resumed.set(true);
        self.resume
    }
    fn exited(&self) -> Result<Option<u32>, Failure> {
        self.observations
            .borrow_mut()
            .pop_front()
            .unwrap_or(Ok(None))
    }
    fn terminate(&self) {
        self.terminated.set(true);
    }
}

fn exercise(child: &Script, cancelled: bool) -> Result<(), Failure> {
    run(
        child,
        &AtomicBool::new(cancelled),
        Instant::now() + Duration::from_secs(2),
        Duration::ZERO,
    )
}

#[test]
fn successful_or_nonzero_exit_requires_whole_job_retirement() {
    for (code, outcome) in [(0, Ok(())), (7, Err(Failure::ProcessExit))] {
        let child = Script::new([Ok(None), Ok(Some(code))]);
        assert_eq!(exercise(&child, false), outcome);
        assert!(child.resumed.get());
        assert!(!child.terminated.get());
    }
}
#[test]
fn cancellation_before_resume_retires_suspended_child() {
    let child = Script::new([Ok(Some(1))]);
    assert_eq!(exercise(&child, true), Err(Failure::Cancelled));
    assert!(!child.resumed.get());
    assert!(child.terminated.get());
}
#[test]
fn deadline_resume_and_wait_failures_require_independent_retirement() {
    let child = Script::new([Ok(Some(1))]);
    assert_eq!(
        run(
            &child,
            &AtomicBool::new(false),
            Instant::now(),
            Duration::ZERO
        ),
        Err(Failure::Timeout)
    );
    assert!(child.terminated.get());
    let mut child = Script::new([Ok(Some(1))]);
    child.resume = Err(Failure::Start);
    assert_eq!(exercise(&child, false), Err(Failure::Start));
    assert!(child.terminated.get());
    let child = Script::new([Err(Failure::Wait), Ok(Some(1))]);
    assert_eq!(exercise(&child, false), Err(Failure::Wait));
    assert!(child.terminated.get());
}
#[test]
fn unterminated_or_unobservable_tree_cannot_yield_clean_cancellation() {
    for observation in [Ok(None), Err(Failure::Wait)] {
        let child = Script::new([observation]);
        assert_eq!(exercise(&child, true), Err(Failure::StopUnconfirmed));
        assert!(child.terminated.get());
    }
}

#[test]
fn cleanup_failure_preserves_valid_bytes_and_primary_start_error() {
    let xml = b"<BatteryReport/>".to_vec();
    for primary in [Ok(xml), Err(Failure::Start)] {
        let mut recorded = None;
        let result = finish_acquisition(primary.clone(), Err(Failure::Cleanup), |event| {
            recorded = Some(event);
        });
        assert_eq!(result, primary);
        assert_eq!(
            recorded,
            Some(crate::agent_diagnostics::Event::BatteryCleanup)
        );
    }
}
