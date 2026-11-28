//! `Reader::job`, the one job body of the native worker and the fixture
//! workers: it reads only on a bound port, always retires the Reader, and
//! discards a reading whose retirement ended past the deadline or after its
//! admission was lost.
use super::{
    device_mode_tests::{ADMISSION, KeyboardIo, KeyboardPort},
    worker::{Job, Reading},
    *,
};
use std::sync::{Arc, atomic::Ordering};

const BUDGET: Duration = Duration::from_secs(2);

struct Ran {
    reading: Option<Reading<ReadAdmission>>,
    sealed: bool,
}

/// One job on a fresh Reader over `io`, on a paused job clock, with a
/// two-second deadline. Admission holds until retirement completes, and after
/// that only while `kept` is set.
fn run(io: &Arc<KeyboardIo>, bound: bool, kept: bool) -> Ran {
    let mut reader = Reader::new(KeyboardPort::new(io.clone()), ADMISSION);
    let device = io.clone();
    let mut authorize =
        move || (kept || !device.retired.load(Ordering::Acquire)).then_some(ADMISSION);
    tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .start_paused(true)
        .build()
        .unwrap()
        .block_on(async {
            let job = Job::for_tests(Instant::now() + BUDGET);
            let reading = reader.job(&mut authorize, &job, bound).await.unwrap();
            Ran {
                reading,
                sealed: reader.poisoned,
            }
        })
}

fn keyboard() -> Arc<KeyboardIo> {
    KeyboardIo::new(None)
}

#[test]
fn a_bound_job_reads_then_retires_the_reader() {
    let io = keyboard();
    let ran = run(&io, true, true);
    let reading = ran.reading.expect("a fresh reading");
    assert!(reading.admission == ADMISSION);
    assert_eq!(io.sent.lock().unwrap().len(), 2);
    // Retirement seals the Reader even after a complete read, so the job's
    // native resources end with it.
    assert!(ran.sealed);
    assert!(io.retired.load(Ordering::Acquire));
}

#[test]
fn a_job_on_an_unbound_port_sends_nothing_and_still_retires() {
    let io = keyboard();
    let ran = run(&io, false, true);
    assert!(ran.reading.is_none());
    assert!(io.sent.lock().unwrap().is_empty());
    assert_eq!(io.verifies.load(Ordering::Acquire), 0);
    assert!(ran.sealed);
    assert!(io.retired.load(Ordering::Acquire));
}

/// Retirement has no deadline of its own: the worker owns the native resources
/// until it ends. A reading whose retirement ends past the job deadline is
/// discarded, as before `Reader::job` was shared.
#[test]
fn a_reading_whose_retirement_ends_past_the_deadline_is_discarded() {
    let io = keyboard();
    *io.retirement.lock().unwrap() = BUDGET;
    let ran = run(&io, true, true);
    assert!(io.retired.load(Ordering::Acquire));
    assert!(ran.reading.is_none());
    // Retirement that ends in time keeps it.
    let io = keyboard();
    *io.retirement.lock().unwrap() = BUDGET / 2;
    assert!(run(&io, true, true).reading.is_some());
}

#[test]
fn a_reading_whose_admission_is_lost_by_retirement_is_discarded() {
    let io = keyboard();
    let ran = run(&io, true, false);
    assert_eq!(io.sent.lock().unwrap().len(), 2);
    assert!(io.retired.load(Ordering::Acquire));
    assert!(ran.reading.is_none());
}
