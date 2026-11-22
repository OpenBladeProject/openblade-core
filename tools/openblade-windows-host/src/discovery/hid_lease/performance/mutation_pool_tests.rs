//! The transaction-ID pool through the mutation paths of the Reader, on the
//! in-memory port: recipes and fan no-ops free their IDs, a prepared fan
//! record pins its own, and an exhausted pool is named.
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

#[derive(Default)]
struct Request {
    destination: Option<Destination>,
    fan: Option<FanIntent>,
    rows: Mutex<Vec<Progress>>,
    refusals: Mutex<Vec<Refusal>>,
}
impl Permit for Request {
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
    fn record_fan(&self, _: u8) -> io::Result<()> {
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
    }
    fixture
}

fn limit(cancelled: &AtomicBool) -> Limit<'_> {
    Limit {
        deadline: Instant::now() + Duration::from_secs(10),
        cancelled,
    }
}

async fn apply(fixture: &Fixture, request: &Request) -> Option<ObservedPerformance> {
    let cancelled = AtomicBool::new(false);
    let mut reader = Reader::new(fixture.port(), ADMITTED);
    let observed = reader.apply_preset(request, limit(&cancelled)).await;
    reader.retire(None).await.unwrap();
    observed
}

async fn read(fixture: &Fixture) -> Option<ObservedPerformance> {
    let cancelled = AtomicBool::new(false);
    let mut reader = Reader::new(fixture.port(), ADMITTED);
    let observed = reader
        .observe(&mut || Some(ADMITTED), limit(&cancelled))
        .await;
    reader.retire(None).await.unwrap();
    observed
}

fn sent_ids(fixture: &Fixture) -> Vec<u8> {
    let s = fixture.state.lock().unwrap();
    s.sent.iter().map(|(.., id)| *id).collect()
}

#[tokio::test(start_paused = true)]
async fn repeated_thermal_recipes_never_exhaust_the_pool() {
    let fixture = balanced();
    for round in 0..300 {
        let (destination, observed) = if round % 2 == 0 {
            (Preset::Silent, ObservedPerformance::SilentAutomatic)
        } else {
            (Preset::Balanced, ObservedPerformance::BalancedAutomatic)
        };
        let request = Request {
            destination: Some(Destination::Preset(destination)),
            ..Request::default()
        };
        assert_eq!(apply(&fixture, &request).await, Some(observed), "{round}");
        assert!(request.refusals.lock().unwrap().is_empty(), "{round}");
        assert_eq!(
            request.rows.lock().unwrap().last(),
            Some(&Progress::Confirmed)
        );
    }
    assert!(sent_ids(&fixture).len() > 10 * 254);
    // Only the last 0D/82, 07/8C and setter replies can still be read.
    assert!(fixture.transactions.free() >= 250);
}

#[tokio::test(start_paused = true)]
async fn repeated_fan_no_ops_free_their_unrecorded_preflight_ids() {
    let fixture = balanced();
    let automatic = || Request {
        fan: FanIntent::checked(0, 3700),
        ..Request::default()
    };
    for _ in 0..1_000 {
        assert_eq!(
            apply(&fixture, &automatic()).await,
            Some(ObservedPerformance::BalancedAutomatic)
        );
    }
    assert_eq!(fixture.state.lock().unwrap().writes, 0);
    assert!(fixture.transactions.free() >= 250);
}

#[tokio::test(start_paused = true)]
async fn a_prepared_fan_record_pins_its_ids_for_the_process() {
    let fixture = balanced();
    let fixed = Request {
        fan: FanIntent::checked(1, 3700),
        ..Request::default()
    };
    assert!(apply(&fixture, &fixed).await.is_some());
    assert_eq!(fixture.state.lock().unwrap().writes, 4);
    let recorded = sent_ids(&fixture);
    let free = fixture.transactions.free();
    // The record's two preflight pairs, five recipe pairs and five cleanup
    // pairs stay pinned.
    assert_eq!(free, 254 - 24);
    for _ in 0..300 {
        assert!(read(&fixture).await.is_some());
    }
    let reads = &sent_ids(&fixture)[recorded.len()..];
    assert!(reads.len() > 2 * 254);
    assert!(reads.iter().all(|id| !recorded.contains(id)));
    // The reads keep only their last reply's ID.
    assert_eq!(fixture.transactions.free(), free - 1);
}

#[tokio::test(start_paused = true)]
async fn an_exhausted_pool_is_named_even_when_the_observation_fails_first() {
    for fan in [None, FanIntent::checked(1, 3700)] {
        let fixture = balanced();
        fixture.exhaust();
        let request = Request {
            destination: fan.is_none().then_some(Destination::Preset(Preset::Silent)),
            fan,
            ..Request::default()
        };
        assert_eq!(apply(&fixture, &request).await, None);
        assert_eq!(
            *request.refusals.lock().unwrap(),
            [Refusal::TransactionCapacity],
            "{fan:?}"
        );
        assert!(request.rows.lock().unwrap().is_empty());
        assert!(sent_ids(&fixture).is_empty());
    }
}
