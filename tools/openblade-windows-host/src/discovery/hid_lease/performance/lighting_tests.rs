//! The lighting recipe on the Reader directly, against the in-memory keyboard:
//! what it sends, in which transfer, and what each stop leaves in doubt.
use super::super::mutation::Permit;
use super::*;
use crate::discovery::performance::lighting::OFF_CONFIRMATION_DELAY;
use crate::discovery::performance::{
    LightingJob, LightingOutcome, LightingReading, LightingTarget,
};
use openblade_domain::thermal_mutation::{Destination, Progress};
use openblade_protocol::lighting::LogoMode;
use std::sync::atomic::AtomicUsize;

pub(super) const ADMITTED: ReadAdmission = ReadAdmission {
    lifecycle: 1,
    generation: [9; 32],
};

/// A permit carrying one job whose admission refuses from the 1-based
/// `refuse_from` authorization on.
pub(super) struct Lit {
    pub(super) job: LightingJob,
    pub(super) refuse_from: Option<usize>,
    calls: AtomicUsize,
}
impl Lit {
    /// A write of `target` whose zero brightness one sample confirms, or the
    /// read-only resolution for `None`: the recipe without the Off
    /// confirmation.
    fn new(target: Option<LightingTarget>) -> Self {
        Self::with(target.map_or_else(LightingJob::resolution, LightingJob::write_single_sample))
    }
    /// The write the runtime runs, a zero brightness confirmed twice.
    fn write(target: LightingTarget) -> Self {
        Self::with(LightingJob::write(target))
    }
    pub(super) fn with(job: LightingJob) -> Self {
        Self {
            job,
            refuse_from: None,
            calls: AtomicUsize::new(0),
        }
    }
    /// How many authorizations the job asked for.
    pub(super) fn authorizations(&self) -> usize {
        self.calls.load(Ordering::Acquire)
    }
}
impl Permit for Lit {
    fn lighting(&self) -> Option<&LightingJob> {
        Some(&self.job)
    }
    // Windows battery is the battery class with no adapter read, so these
    // tests pin the lighting reports alone; the power read has its own.
    fn windows_on_battery(&self) -> Option<bool> {
        Some(true)
    }
    fn store_lighting(&self, power: i32, performance: Option<ObservedPerformance>) -> bool {
        assert_eq!((power, performance), (1, None));
        true
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        let call = self.calls.fetch_add(1, Ordering::AcqRel) + 1;
        self.refuse_from
            .is_none_or(|from| call < from)
            .then_some(ADMITTED)
    }
    fn resolve(
        &self,
        _: i32,
        _: ObservedPerformance,
        _: &AtomicBool,
    ) -> io::Result<Option<Destination>> {
        unreachable!("a lighting job resolves no thermal destination")
    }
    fn record(&self, _: Progress) -> io::Result<()> {
        unreachable!("a lighting job records no thermal progress")
    }
}

const OFF: Option<LightingTarget> = Some(LightingTarget {
    brightness: 0,
    logo: None,
});

fn off_with(logo: LogoMode) -> Option<LightingTarget> {
    Some(LightingTarget {
        brightness: 0,
        logo: Some(logo),
    })
}

pub(super) fn keyboard(lighting: [u8; 3]) -> Fixture {
    let fixture = Fixture::new();
    fixture.state.lock().unwrap().lighting = lighting;
    fixture
}

pub(super) async fn run(fixture: &Fixture, permit: &Lit) {
    run_within(fixture, permit, Duration::from_secs(6)).await;
}

async fn run_within(fixture: &Fixture, permit: &Lit, budget: Duration) {
    let cancelled = AtomicBool::new(false);
    let limit = Limit {
        deadline: Instant::now() + budget,
        cancelled: &cancelled,
    };
    let mut reader = Reader::new(fixture.port(), ADMITTED);
    assert_eq!(reader.apply_preset(permit, limit).await, None);
    reader.retire(None).await.unwrap();
}

/// The lighting reports sent, as `CC/II:payload`, with an `!` on each
/// mutation transfer.
fn sent(fixture: &Fixture) -> Vec<String> {
    let s = fixture.state.lock().unwrap();
    assert_eq!(s.writes, 0, "no thermal setter");
    assert!(
        s.sent.iter().all(|sent| sent.0 == 3),
        "only lighting reports"
    );
    s.lighting_sent
        .iter()
        .map(|(command, payload, mutation)| {
            let payload: String = payload.iter().map(|b| format!("{b:02X}")).collect();
            format!(
                "03/{command:02X}:{payload}{}",
                if *mutation { "!" } else { "" }
            )
        })
        .collect()
}

fn reading(brightness: u8, logo: LogoMode) -> Option<LightingReading> {
    Some(LightingReading {
        brightness: Some(brightness),
        logo: Some(logo),
    })
}

pub(super) const BRIGHTNESS: &str = "03/83:010500";
pub(super) const STATE: &str = "03/80:010400";
pub(super) const EFFECT: &str = "03/82:010400";

#[tokio::test]
async fn off_writes_brightness_zero_reads_it_back_and_reads_the_aggregate() {
    let fixture = keyboard([0xc5, 1, 0]);
    let permit = Lit::new(OFF);
    run(&fixture, &permit).await;
    assert_eq!(permit.job.outcome(), LightingOutcome::Written);
    assert_eq!(
        permit.job.baseline(),
        Some(LightingReading {
            brightness: Some(0xc5),
            logo: None
        })
    );
    assert_eq!(permit.job.final_reading(), reading(0, LogoMode::Static));
    assert_eq!(
        sent(&fixture),
        [
            BRIGHTNESS,
            "03/03:010500!",
            BRIGHTNESS,
            BRIGHTNESS,
            STATE,
            EFFECT
        ]
    );
    assert_eq!(fixture.state.lock().unwrap().lighting, [0, 1, 0]);
}

#[tokio::test]
async fn a_confirmed_off_is_unchanged_and_writes_nothing() {
    for (lighting, target, sends) in [
        ([0, 1, 0], OFF, vec![BRIGHTNESS]),
        (
            [0, 0, 2],
            off_with(LogoMode::Off),
            vec![BRIGHTNESS, STATE, EFFECT],
        ),
    ] {
        let fixture = keyboard(lighting);
        let permit = Lit::new(target);
        run(&fixture, &permit).await;
        assert_eq!(permit.job.outcome(), LightingOutcome::Observed);
        assert_eq!(permit.job.final_reading(), permit.job.baseline());
        assert_eq!(sent(&fixture), sends);
    }
}

#[tokio::test]
async fn the_logo_writes_its_effect_only_for_a_lit_mode_then_its_state() {
    for (lighting, mode, sets) in [
        ([0, 1, 2], LogoMode::Off, vec!["03/00:010400!"]),
        (
            [0, 0, 0],
            LogoMode::Breathing,
            vec!["03/02:010402!", "03/00:010401!"],
        ),
        (
            [0, 1, 2],
            LogoMode::Static,
            vec!["03/02:010400!", "03/00:010401!"],
        ),
    ] {
        let fixture = keyboard(lighting);
        let permit = Lit::new(off_with(mode));
        run(&fixture, &permit).await;
        assert_eq!(permit.job.outcome(), LightingOutcome::Written, "{mode:?}");
        assert_eq!(permit.job.final_reading(), reading(0, mode));
        let mut expected = vec![BRIGHTNESS, STATE, EFFECT];
        expected.extend(sets);
        expected.extend([STATE, EFFECT, BRIGHTNESS, STATE, EFFECT]);
        assert_eq!(sent(&fixture), expected, "{mode:?}");
    }
}

/// A setter that may have been submitted, unacknowledged or without a
/// confirmed readback, ends the job: nothing more is sent, nothing restores
/// and nothing is replayed.
#[tokio::test]
async fn an_unconfirmed_setter_stops_the_job_in_doubt() {
    // (fault, reports sent, brightness left)
    for (fault, count, left) in [("fail", 2, 0xc5), ("ack timeout", 2, 0), ("readback", 3, 0)] {
        let fixture = keyboard([0xc5, 1, 0]);
        {
            let mut s = fixture.state.lock().unwrap();
            match fault {
                "fail" => s.lighting_fail = Some(1),
                "ack timeout" => s.lighting_timeout = Some(1),
                _ => s.lighting_query_timeout = Some(2),
            }
        }
        let permit = Lit::new(OFF);
        run(&fixture, &permit).await;
        assert!(
            matches!(permit.job.outcome(), LightingOutcome::InDoubt(_)),
            "{fault}: {:?}",
            permit.job.outcome()
        );
        assert_eq!(permit.job.final_reading(), None, "{fault}");
        assert_eq!(sent(&fixture).len(), count, "{fault}");
        assert_eq!(fixture.state.lock().unwrap().lighting[0], left, "{fault}");
    }
    // A logo setter in doubt ends the job the same way.
    let fixture = keyboard([0, 1, 0]);
    fixture.state.lock().unwrap().lighting_timeout = Some(1);
    let permit = Lit::new(off_with(LogoMode::Off));
    run(&fixture, &permit).await;
    assert!(matches!(
        permit.job.outcome(),
        LightingOutcome::InDoubt(message) if message.contains("logo")
    ));
    assert_eq!(sent(&fixture).len(), 4);
}

/// A readback that confirms another value is a known mismatch: the job
/// restores the confirmed baseline once, brightness first.
#[tokio::test]
async fn a_confirmed_mismatch_restores_the_baseline() {
    // (lighting, target, ignored setters, restored, reports)
    let brightness_mismatch = (
        [0xc5, 1, 0],
        OFF,
        vec![1],
        Some(true),
        vec![
            BRIGHTNESS,
            "03/03:010500!",
            BRIGHTNESS,
            "03/03:0105C5!",
            BRIGHTNESS,
        ],
    );
    let logo_mismatch = (
        [0xc5, 1, 0],
        off_with(LogoMode::Off),
        vec![2],
        Some(true),
        vec![
            BRIGHTNESS,
            STATE,
            EFFECT,
            "03/03:010500!",
            BRIGHTNESS,
            "03/00:010400!",
            STATE,
            EFFECT,
            "03/03:0105C5!",
            BRIGHTNESS,
            "03/02:010400!",
            "03/00:010401!",
            STATE,
            EFFECT,
        ],
    );
    let unrestored = (
        [0xc5, 1, 0],
        OFF,
        vec![1],
        Some(false),
        vec![
            BRIGHTNESS,
            "03/03:010500!",
            BRIGHTNESS,
            "03/03:0105C5!",
            BRIGHTNESS,
        ],
    );
    for (n, (lighting, target, ignored, restored, reports)) in
        [brightness_mismatch, logo_mismatch, unrestored]
            .into_iter()
            .enumerate()
    {
        let fixture = keyboard(lighting);
        {
            let mut s = fixture.state.lock().unwrap();
            s.lighting_ignore = ignored;
            if restored == Some(false) {
                // Another value reads back after the setter and the restore.
                s.brightness_script = [0xc5, 0x40, 0x40].into();
            }
        }
        let permit = Lit::new(target);
        run(&fixture, &permit).await;
        let LightingOutcome::Mismatched {
            failure,
            restored: actual,
        } = permit.job.outcome()
        else {
            panic!("{n}: {:?}", permit.job.outcome());
        };
        assert_eq!(actual, restored, "{n}");
        assert!(failure.ends_with("did not match independent readback."));
        assert_eq!(sent(&fixture), reports, "{n}");
        assert_eq!(permit.job.final_reading(), None, "{n}");
    }
}

/// Every setter needs its own fresh admission. A refusal before the first
/// sends nothing after the baseline; one after a confirmed brightness change
/// keeps that change and leaves nothing in doubt.
#[tokio::test]
async fn a_refused_admission_sends_no_further_setter() {
    let fixture = keyboard([0xc5, 1, 0]);
    let mut permit = Lit::new(OFF);
    permit.refuse_from = Some(1);
    run(&fixture, &permit).await;
    assert_eq!(permit.job.outcome(), LightingOutcome::Pending);
    assert!(sent(&fixture).is_empty());

    // The baseline takes three checkpoints of two authorizations each.
    let fixture = keyboard([0xc5, 1, 0]);
    let mut permit = Lit::new(OFF);
    permit.refuse_from = Some(7);
    run(&fixture, &permit).await;
    assert_eq!(permit.job.outcome(), LightingOutcome::Refused);
    assert_eq!(sent(&fixture), [BRIGHTNESS]);

    let fixture = keyboard([0xc5, 1, 2]);
    let mut permit = Lit::new(off_with(LogoMode::Off));
    // The first checkpoint, the baseline's three queries, the brightness
    // setter and its readback take 22; the logo setter's checkpoint before
    // its transfer refuses.
    permit.refuse_from = Some(23);
    run(&fixture, &permit).await;
    assert_eq!(permit.job.outcome(), LightingOutcome::Interrupted);
    assert_eq!(
        sent(&fixture),
        [BRIGHTNESS, STATE, EFFECT, "03/03:010500!", BRIGHTNESS]
    );
    assert_eq!(fixture.state.lock().unwrap().lighting, [0, 1, 2]);
}

/// Managed's owned-renderer Off confirmation: a zero brightness confirms Off
/// only after a second zero sample 750 ms later, at the baseline and at the
/// final read; a second non-zero sample is the reading.
#[tokio::test(start_paused = true)]
async fn a_zero_brightness_is_confirmed_by_a_second_sample_when_required() {
    let fixture = keyboard([0, 1, 0]);
    let permit = Lit::write(OFF.unwrap());
    let started = Instant::now();
    run(&fixture, &permit).await;
    assert!(started.elapsed() >= OFF_CONFIRMATION_DELAY);
    assert_eq!(permit.job.outcome(), LightingOutcome::Observed);
    assert_eq!(sent(&fixture), [BRIGHTNESS, BRIGHTNESS]);

    // A second sample that is not zero is a lit baseline, which is written.
    let fixture = keyboard([0, 1, 0]);
    fixture.state.lock().unwrap().brightness_script = [0, 0x10].into();
    let permit = Lit::write(OFF.unwrap());
    run(&fixture, &permit).await;
    assert_eq!(permit.job.outcome(), LightingOutcome::Written);
    assert_eq!(permit.job.baseline().unwrap().brightness, Some(0x10));
    assert_eq!(
        sent(&fixture),
        [
            BRIGHTNESS,
            BRIGHTNESS,
            "03/03:010500!",
            BRIGHTNESS,
            BRIGHTNESS,
            BRIGHTNESS,
            STATE,
            EFFECT
        ]
    );

    // Without the requirement one zero sample is the reading.
    let fixture = keyboard([0, 1, 0]);
    let permit = Lit::new(OFF);
    let started = Instant::now();
    run(&fixture, &permit).await;
    assert!(started.elapsed() < OFF_CONFIRMATION_DELAY);
    assert_eq!(sent(&fixture), [BRIGHTNESS]);
}

/// A final second sample that is not zero leaves the final brightness lit.
#[tokio::test(start_paused = true)]
async fn the_final_off_confirmation_reports_a_lit_second_sample() {
    let fixture = keyboard([0xc5, 1, 0]);
    // The baseline, the readback, then the final read's two samples.
    fixture.state.lock().unwrap().brightness_script = [0xc5, 0, 0, 0x20].into();
    let permit = Lit::write(OFF.unwrap());
    run(&fixture, &permit).await;
    assert_eq!(permit.job.outcome(), LightingOutcome::Written);
    assert_eq!(permit.job.final_reading().unwrap().brightness, Some(0x20));
}

#[tokio::test(start_paused = true)]
async fn too_little_time_for_the_recipe_writes_nothing() {
    let fixture = keyboard([0xc5, 1, 0]);
    fixture.state.lock().unwrap().latency = Some(Duration::from_millis(100));
    let permit = Lit::new(OFF);
    // The baseline takes 230 ms; the setter, readback and restore or final
    // read need far more than the 400 ms left.
    run_within(&fixture, &permit, Duration::from_millis(600)).await;
    assert_eq!(permit.job.outcome(), LightingOutcome::Unpaced);
    assert_eq!(sent(&fixture), [BRIGHTNESS]);
}

#[tokio::test]
async fn a_logo_pair_outside_the_contract_writes_nothing() {
    for lighting in [[0xc5, 1, 7], [0xc5, 2, 0]] {
        let fixture = keyboard(lighting);
        let permit = Lit::new(off_with(LogoMode::Off));
        run(&fixture, &permit).await;
        assert_eq!(permit.job.outcome(), LightingOutcome::Malformed);
        assert_eq!(sent(&fixture), [BRIGHTNESS, STATE, EFFECT]);
    }
}

/// The read-only resolution reads the brightness and logo and writes nothing.
#[tokio::test]
async fn a_read_only_job_reads_and_writes_nothing() {
    let fixture = keyboard([0xc5, 1, 2]);
    let permit = Lit::new(None);
    run(&fixture, &permit).await;
    assert_eq!(permit.job.outcome(), LightingOutcome::Observed);
    assert_eq!(
        permit.job.final_reading(),
        reading(0xc5, LogoMode::Breathing)
    );
    assert_eq!(sent(&fixture), [BRIGHTNESS, STATE, EFFECT]);

    let fixture = keyboard([0xc5, 1, 2]);
    fixture.state.lock().unwrap().lighting_query_timeout = Some(2);
    let permit = Lit::new(None);
    run(&fixture, &permit).await;
    assert_eq!(
        permit.job.outcome(),
        LightingOutcome::Unread("Logo lighting could not be read.")
    );
}
