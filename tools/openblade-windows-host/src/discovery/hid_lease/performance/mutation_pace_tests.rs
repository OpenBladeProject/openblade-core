//! The pace check on the Reader directly, on paused time: every transfer of
//! the in-memory port completes a fixed latency after its submission, so the
//! measured pace and the deadline arithmetic are exact.
use super::super::mutation::{Permit, Refusal};
use super::*;
use openblade_domain::{
    fan_recipe::FanIntent,
    thermal_mutation::{Destination, Preset, Progress},
};

const ADMITTED: ReadAdmission = ReadAdmission {
    lifecycle: 1,
    generation: [9; 32],
};
/// A send, the 30 ms settle and the reply: 100 + 30 + 100 ms.
const LATENCY: Duration = Duration::from_millis(100);

#[derive(Default)]
struct Paced {
    destination: Option<Destination>,
    fan: Option<FanIntent>,
    rows: Mutex<Vec<Progress>>,
    fan_rows: Mutex<Vec<u8>>,
    refusals: Mutex<Vec<Refusal>>,
}
impl Permit for Paced {
    fn authorize(&self) -> Option<ReadAdmission> {
        Some(ADMITTED)
    }
    fn windows_on_battery(&self) -> Option<bool> {
        Some(false)
    }
    fn is_fan(&self) -> bool {
        self.fan.is_some()
    }
    fn resolve_fan(
        &self,
        _: i32,
        _: ObservedPerformance,
    ) -> io::Result<Option<(FanIntent, Option<Destination>)>> {
        Ok(self.fan.map(|intent| (intent, None)))
    }
    fn resolve(&self, _: i32, _: ObservedPerformance) -> io::Result<Option<Destination>> {
        Ok(self.destination)
    }
    fn refused(&self, cause: Refusal) {
        self.refusals.lock().unwrap().push(cause);
    }
    fn record(&self, progress: Progress) -> io::Result<()> {
        self.rows.lock().unwrap().push(progress);
        Ok(())
    }
    fn fan_record(&self) -> Option<openblade_domain::fan_history::FanRecord> {
        Some(openblade_domain::fan_history::FanRecord {
            contract: openblade_domain::fan_history::CONTRACT.into(),
            requested: serde_json::json!({"value": null, "settings": null}),
            intent: FanIntent::checked(0, 3700)?,
            selector: 0,
            entry: false,
            source: 0,
            preflight: [[0; 2]; 2],
            ids: Vec::new(),
            cleanup: Vec::new(),
            custom: None,
        })
    }
    fn prepare_fan(&self, _: openblade_domain::fan_history::FanRecord) -> io::Result<()> {
        Ok(())
    }
    fn record_fan(&self, progress: u8) -> io::Result<()> {
        self.fan_rows.lock().unwrap().push(progress);
        Ok(())
    }
}

/// Balanced with Automatic fans on a full adapter.
fn balanced() -> Fixture {
    let fixture = Fixture::new();
    {
        let mut s = fixture.state.lock().unwrap();
        s.selectors = [0, 0];
        s.fans = [0, 0];
        s.latency = Some(LATENCY);
    }
    fixture
}

async fn apply(fixture: &Fixture, permit: &Paced, budget: Duration) -> Option<ObservedPerformance> {
    let cancelled = AtomicBool::new(false);
    let limit = Limit {
        deadline: Instant::now() + budget,
        cancelled: &cancelled,
    };
    let mut reader = Reader::new(fixture.port(), ADMITTED);
    let observed = reader.apply_preset(permit, limit).await;
    reader.retire(None).await.unwrap();
    observed
}

#[tokio::test(start_paused = true)]
async fn a_preset_recipe_starts_only_when_its_measured_pace_fits_the_deadline() {
    // The observation pair and the 07/8C read take 3 × 230 ms. The rest is
    // two setters with their power reads and the final pair: 6 × 230 ms, so
    // the recipe ends 2070 ms after it starts.
    let silent = || Paced {
        destination: Some(Destination::Preset(Preset::Silent)),
        ..Paced::default()
    };
    let fixture = balanced();
    let permit = silent();
    let started = Instant::now();
    assert_eq!(
        apply(&fixture, &permit, Duration::from_millis(2060)).await,
        None
    );
    // Refused after the Prepared row and before any setter.
    assert_eq!(*permit.refusals.lock().unwrap(), [Refusal::Deadline]);
    assert_eq!(*permit.rows.lock().unwrap(), [Progress::Prepared]);
    {
        let s = fixture.state.lock().unwrap();
        assert_eq!((s.writes, s.selectors), (0, [0, 0]));
    }
    assert_eq!(started.elapsed(), Duration::from_millis(690));
    // With 10 ms to spare the same recipe runs and is confirmed in 2070 ms.
    let fixture = balanced();
    let permit = silent();
    let started = Instant::now();
    assert_eq!(
        apply(&fixture, &permit, Duration::from_millis(2080)).await,
        Some(ObservedPerformance::SilentAutomatic)
    );
    assert!(permit.refusals.lock().unwrap().is_empty());
    assert_eq!(
        permit.rows.lock().unwrap().last(),
        Some(&Progress::Confirmed)
    );
    assert_eq!(started.elapsed(), Duration::from_millis(2070));
}

#[tokio::test(start_paused = true)]
async fn custom_counts_its_level_setters_and_level_reads() {
    // Four setters with their power reads, the final pair and its levels:
    // 12 × 230 ms after the same 690 ms, ending at 3450 ms.
    let custom = || Paced {
        destination: Destination::custom(1, 0, false),
        ..Paced::default()
    };
    let fixture = balanced();
    let permit = custom();
    assert_eq!(
        apply(&fixture, &permit, Duration::from_millis(3440)).await,
        None
    );
    assert_eq!(*permit.refusals.lock().unwrap(), [Refusal::Deadline]);
    assert_eq!(*permit.rows.lock().unwrap(), [Progress::Prepared]);
    assert_eq!(fixture.state.lock().unwrap().writes, 0);
    let fixture = balanced();
    let permit = custom();
    let started = Instant::now();
    assert!(
        apply(&fixture, &permit, Duration::from_millis(3460))
            .await
            .is_some()
    );
    assert_eq!(fixture.state.lock().unwrap().writes, 4);
    assert_eq!(started.elapsed(), Duration::from_millis(3450));
}

#[tokio::test(start_paused = true)]
async fn a_fan_recipe_adds_its_fixed_spacing_before_the_first_setter() {
    // Fixed fans from Balanced: the entry state pair and the target pair (4
    // setters with their power reads) and the final pair, 10 × 230 ms, plus
    // 210 + 65 ms of spacing after the same 690 ms: 3265 ms. Balanced has no
    // intermediate pair (selector 0).
    let fixed = || Paced {
        fan: FanIntent::checked(1, 3700),
        ..Paced::default()
    };
    let fixture = balanced();
    let permit = fixed();
    assert_eq!(
        apply(&fixture, &permit, Duration::from_millis(3260)).await,
        None
    );
    assert_eq!(*permit.refusals.lock().unwrap(), [Refusal::Deadline]);
    assert_eq!(*permit.fan_rows.lock().unwrap(), [0]);
    assert_eq!(fixture.state.lock().unwrap().writes, 0);
    // With 5 ms to spare the check passes and the four setters run.
    let fixture = balanced();
    let permit = fixed();
    apply(&fixture, &permit, Duration::from_millis(3270)).await;
    assert!(permit.refusals.lock().unwrap().is_empty());
    assert_eq!(fixture.state.lock().unwrap().writes, 4);
}

#[tokio::test(start_paused = true)]
async fn each_operation_measures_its_own_pace_and_a_refusal_leaves_the_reader_open() {
    // A 07/8C read held 1300 ms refuses the first operation after its Prepared
    // row. That refusal submitted nothing, so it does not seal the Reader as an
    // abandoned transfer would, and `Pace::start` discards its slow interval:
    // the next operation on the same Reader runs at its own pace.
    for fan in [None, FanIntent::checked(1, 3700)] {
        let operation = || Paced {
            destination: fan.is_none().then_some(Destination::Preset(Preset::Silent)),
            fan,
            ..Paced::default()
        };
        let fixture = balanced();
        fixture.state.lock().unwrap().power_latency = Some(Duration::from_millis(1300));
        let cancelled = AtomicBool::new(false);
        let limit = || Limit {
            deadline: Instant::now() + Duration::from_secs(6),
            cancelled: &cancelled,
        };
        let mut reader = Reader::new(fixture.port(), ADMITTED);
        let refused = operation();
        assert_eq!(reader.apply_preset(&refused, limit()).await, None);
        assert_eq!(
            *refused.refusals.lock().unwrap(),
            [Refusal::Deadline],
            "{fan:?}"
        );
        assert!(!reader.poisoned, "{fan:?}");
        assert_eq!(fixture.state.lock().unwrap().writes, 0);
        fixture.state.lock().unwrap().power_latency = None;
        let next = operation();
        assert!(
            reader.apply_preset(&next, limit()).await.is_some(),
            "{fan:?}"
        );
        assert!(next.refusals.lock().unwrap().is_empty(), "{fan:?}");
        assert!(fixture.state.lock().unwrap().writes > 0, "{fan:?}");
        reader.retire(None).await.unwrap();
    }
}

#[tokio::test(start_paused = true)]
async fn one_slow_power_read_paces_the_whole_recipe() {
    // The observation pair takes 2 × 230 ms and a 07/8C read held 1300 ms
    // longer takes 1530 ms: 1990 ms, a mean of 663 ms. Silent's six remaining
    // exchanges at the mean would end at 5970 ms, inside a 6 s budget; at the
    // slowest interval they end at 11170 ms, so the recipe is refused after
    // its Prepared row and before its first setter.
    let fixture = balanced();
    fixture.state.lock().unwrap().power_latency = Some(Duration::from_millis(1300));
    let permit = Paced {
        destination: Some(Destination::Preset(Preset::Silent)),
        ..Paced::default()
    };
    let started = Instant::now();
    assert_eq!(apply(&fixture, &permit, Duration::from_secs(6)).await, None);
    assert_eq!(*permit.refusals.lock().unwrap(), [Refusal::Deadline]);
    assert_eq!(*permit.rows.lock().unwrap(), [Progress::Prepared]);
    assert_eq!(fixture.state.lock().unwrap().writes, 0);
    assert_eq!(started.elapsed(), Duration::from_millis(1990));
}
