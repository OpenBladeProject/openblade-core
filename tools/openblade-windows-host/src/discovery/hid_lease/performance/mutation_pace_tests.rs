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
    assert_eq!(*permit.refusals.lock().unwrap(), [Refusal::Deadline]);
    assert!(permit.rows.lock().unwrap().is_empty());
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
    assert!(permit.rows.lock().unwrap().is_empty());
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
async fn a_fan_recipe_adds_its_fixed_spacing_before_the_first_row() {
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
    assert_eq!(fixture.state.lock().unwrap().writes, 0);
    // With time for it the check passes; this permit then has no fan record
    // to prepare, so it stops there, still before any setter.
    let fixture = balanced();
    let permit = fixed();
    assert_eq!(
        apply(&fixture, &permit, Duration::from_millis(3270)).await,
        None
    );
    assert!(permit.refusals.lock().unwrap().is_empty());
    assert_eq!(fixture.state.lock().unwrap().writes, 0);
}
