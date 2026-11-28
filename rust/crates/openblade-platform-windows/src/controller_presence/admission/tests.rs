use super::*;
use crate::controller_presence::{Scan, observe};

fn observation() -> Observation {
    observe(
        Scan::Complete(&[]),
        Scan::Complete(&[]),
        None,
        Scan::Incomplete(&[]),
        1,
    )
}

#[test]
fn stale_slow_and_out_of_order_samples_cannot_confirm_absence() {
    let requested = Instant::now();
    let step = Duration::from_millis(1);
    let returned = requested + step * 3;
    for (started, completed, returned, expected) in [
        (requested, returned, returned, true),
        (requested - step, returned, returned, false),
        (requested + step * 2, requested + step, returned, false),
        (requested, returned + step, returned, false),
        (
            requested,
            returned,
            requested + MAX_OBSERVATION_TIME + step,
            false,
        ),
    ] {
        let sample = Sample {
            started,
            completed,
            observation: observation(),
        };
        assert_eq!(
            sample.confirms_absence_within(requested, returned),
            expected
        );
    }
}

#[test]
fn a_sample_from_before_the_request_cannot_be_used_by_the_consumer() {
    let old = Instant::now() - Duration::from_secs(2);
    let mut sample = Some(Sample {
        started: old,
        completed: old,
        observation: observation(),
    });
    let mut admission = ReadOnlyAdmission::from_source(move || sample.take().unwrap());
    assert!(!admission.confirms_fresh_absence());
}
