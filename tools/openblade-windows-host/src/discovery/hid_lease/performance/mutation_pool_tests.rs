//! The transaction-ID pool through the mutation paths of the Reader, on the
//! in-memory port: recipes, fan no-ops and refused fan requests free their
//! IDs; an uncertain setter's ID, and the IDs a record names once a setter
//! may be sent, are never issued again; and an exhausted pool is named.
use super::super::mutation::{Permit, Refusal};
use super::*;
use openblade_domain::{
    battery_history::{Desired, Request as BatteryRequest},
    fan_recipe::FanIntent,
    temporary_charge_history::{CONTRACT, TemporaryChargeRecord},
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
    // A temporary full charge target, and the IDs its cancellation spends.
    tfc: Option<bool>,
    reserved: Option<[u8; 8]>,
    // The reserved IDs a fresh-ID cancel replaces, and the IDs it drew.
    superseded: Option<[u8; 8]>,
    ids: Mutex<Option<[u8; 8]>>,
    pins: Mutex<Vec<u8>>,
    cancel_ids: Mutex<Option<[u8; 8]>>,
    rows: Mutex<Vec<Progress>>,
    battery_rows: Mutex<Vec<u8>>,
    fan_rows: Mutex<Vec<u8>>,
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
    fn temporary_target(&self) -> Option<bool> {
        self.tfc
    }
    fn temporary_reserved_ids(&self) -> Option<[u8; 8]> {
        self.reserved
    }
    fn temporary_fresh_ids(&self) -> bool {
        self.superseded.is_some()
    }
    fn retained_pins(&self, ids: &[u8]) {
        self.pins.lock().unwrap().extend_from_slice(ids);
    }
    fn resolve_temporary_charge(
        &self,
        source: u8,
        flags: u8,
        policy: Option<u8>,
        ids: [u8; 8],
        cancel_ids: Option<[u8; 8]>,
    ) -> io::Result<Option<TemporaryChargeRecord>> {
        let enabled = self.tfc.unwrap();
        let requested = BatteryRequest::SetFullChargeOverride { enabled };
        let configured = Desired {
            limit: 80,
            protection: true,
            temporary: !enabled,
        };
        let record = TemporaryChargeRecord {
            contract: CONTRACT.into(),
            desired: configured.requested(&requested),
            requested,
            configured,
            source,
            baseline: flags,
            target: if enabled { 4 } else { 0 },
            policy,
            ids,
            cancel_ids,
            superseded_ids: self.superseded,
            origin: self
                .superseded
                .map(|_| openblade_domain::temporary_charge_history::Origin {
                    request: "aaaaaaaa-bbbb-cccc-dddd-000000000002".into(),
                    intent: "5".repeat(64),
                }),
            fan_owner: None,
        };
        record.validate()?;
        *self.cancel_ids.lock().unwrap() = cancel_ids;
        *self.ids.lock().unwrap() = Some(ids);
        Ok(Some(record))
    }
    fn record_battery(&self, progress: u8) -> io::Result<()> {
        self.battery_rows.lock().unwrap().push(progress);
        Ok(())
    }
    fn resolve_fan(
        &self,
        _: i32,
        _: ObservedPerformance,
    ) -> io::Result<Option<(FanIntent, Option<Destination>)>> {
        Ok(self.fan.map(|intent| (intent, None)))
    }
    fn resolve(
        &self,
        _: i32,
        _: ObservedPerformance,
        _: &AtomicBool,
    ) -> io::Result<Option<Destination>> {
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
    }
    fixture
}

const BUDGET: Duration = Duration::from_secs(10);

fn limit(cancelled: &AtomicBool, budget: Duration) -> Limit<'_> {
    Limit {
        deadline: Instant::now() + budget,
        cancelled,
    }
}

async fn apply(fixture: &Fixture, request: &Request) -> Option<ObservedPerformance> {
    apply_within(fixture, request, BUDGET).await
}

async fn apply_within(
    fixture: &Fixture,
    request: &Request,
    budget: Duration,
) -> Option<ObservedPerformance> {
    let cancelled = AtomicBool::new(false);
    let mut reader = Reader::new(fixture.port(), ADMITTED);
    let observed = reader
        .apply_preset(request, limit(&cancelled, budget))
        .await;
    reader.retire(None).await.unwrap();
    observed
}

async fn read(fixture: &Fixture) -> Option<ObservedPerformance> {
    let cancelled = AtomicBool::new(false);
    let mut reader = Reader::new(fixture.port(), ADMITTED);
    let observed = reader
        .observe(&mut || Some(ADMITTED), limit(&cancelled, BUDGET))
        .await;
    reader.retire(None).await.unwrap();
    observed
}

fn sent_ids(fixture: &Fixture) -> Vec<u8> {
    let s = fixture.state.lock().unwrap();
    s.sent.iter().map(|(.., id)| *id).collect()
}

fn preset(destination: Preset) -> Request {
    Request {
        destination: Some(Destination::Preset(destination)),
        ..Request::default()
    }
}

/// Wraps the pool several times with reads and confirmed preset recipes.
async fn churn(fixture: &Fixture) {
    let before = sent_ids(fixture).len();
    for round in 0..160 {
        assert!(read(fixture).await.is_some(), "{round}");
        let destination = if round % 2 == 0 {
            Preset::Silent
        } else {
            Preset::Balanced
        };
        assert!(
            apply(fixture, &preset(destination)).await.is_some(),
            "{round}"
        );
    }
    assert!(sent_ids(fixture).len() - before > 3 * 254);
}

#[tokio::test(start_paused = true)]
async fn repeated_confirmed_thermal_recipes_never_exhaust_the_pool() {
    let fixture = balanced();
    for round in 0..300 {
        let (destination, observed) = if round % 2 == 0 {
            (Preset::Silent, ObservedPerformance::SilentAutomatic)
        } else {
            (Preset::Balanced, ObservedPerformance::BalancedAutomatic)
        };
        let request = preset(destination);
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
async fn an_uncertain_setter_id_is_never_issued_again() {
    for fault in ["uncorrelated acknowledgement", "failed submission"] {
        let fixture = balanced();
        {
            let mut s = fixture.state.lock().unwrap();
            if fault == "failed submission" {
                s.fail_write = Some(1);
            } else {
                s.corrupt_ack = Some(1);
            }
        }
        assert_eq!(
            apply(&fixture, &preset(Preset::Silent)).await,
            None,
            "{fault}"
        );
        let setter = {
            let mut s = fixture.state.lock().unwrap();
            assert_eq!(s.writes, 1, "{fault}");
            s.fail_write = None;
            s.corrupt_ack = None;
            s.selectors = [0, 0];
            s.sent
                .iter()
                .find(|(class, command, ..)| (*class, *command) == (13, 2))
                .map(|(.., id)| *id)
                .unwrap()
        };
        let before = sent_ids(&fixture).len();
        churn(&fixture).await;
        assert!(
            sent_ids(&fixture)[before..].iter().all(|id| *id != setter),
            "{fault}: setter ID {setter} was issued again"
        );
    }
}

#[tokio::test(start_paused = true)]
async fn temporary_charge_cancel_ids_are_issued_to_nothing_else() {
    let fixture = balanced();
    let enable = Request {
        tfc: Some(true),
        ..Request::default()
    };
    assert_eq!(apply(&fixture, &enable).await, None);
    assert_eq!(*enable.battery_rows.lock().unwrap(), [0, 70, 71, 72]);
    let cancel_ids = enable.cancel_ids.lock().unwrap().unwrap();
    let before = sent_ids(&fixture).len();
    churn(&fixture).await;
    assert!(
        sent_ids(&fixture)[before..]
            .iter()
            .all(|id| !cancel_ids.contains(id)),
        "a cancellation ID was issued to another request"
    );
    // The cancellation still has its IDs.
    let cancel = Request {
        tfc: Some(false),
        reserved: Some(cancel_ids),
        ..Request::default()
    };
    assert_eq!(apply(&fixture, &cancel).await, None);
    assert_eq!(*cancel.battery_rows.lock().unwrap(), [0, 70, 71, 72]);
}

// A cancel that replaces its enable's reserved IDs draws fresh ones as the
// enable drew those: pinned, and handed to the record that names them, so no
// other request is issued them until that record settles.
#[tokio::test(start_paused = true)]
async fn temporary_charge_fresh_cancel_ids_are_pinned_for_their_record() {
    let fixture = balanced();
    let enable = Request {
        tfc: Some(true),
        ..Request::default()
    };
    assert_eq!(apply(&fixture, &enable).await, None);
    let reserved = enable.cancel_ids.lock().unwrap().unwrap();
    // An automatic cancel runs on Battery.
    fixture.state.lock().unwrap().actual = 0;
    let cancel = Request {
        tfc: Some(false),
        superseded: Some(reserved),
        ..Request::default()
    };
    assert_eq!(apply(&fixture, &cancel).await, None);
    assert_eq!(*cancel.battery_rows.lock().unwrap(), [0, 70, 71, 72]);
    let fresh = cancel.ids.lock().unwrap().unwrap();
    assert!(fresh.iter().all(|id| !reserved.contains(id)), "{fresh:?}");
    assert_eq!(*cancel.pins.lock().unwrap(), fresh);
    assert_eq!(fixture.pinned_transactions(&fresh), fresh);
    fixture.state.lock().unwrap().actual = 15;
    let before = sent_ids(&fixture).len();
    churn(&fixture).await;
    assert!(
        sent_ids(&fixture)[before..]
            .iter()
            .all(|id| !fresh.contains(id) && !reserved.contains(id)),
        "a pinned cancellation ID was issued to another request"
    );
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
async fn pace_refused_fan_requests_return_their_ids() {
    let fixture = balanced();
    // At this device pace the recipe cannot fit the request time, so the
    // pace check before the first setter refuses it after Prepared.
    fixture.state.lock().unwrap().latency = Some(Duration::from_millis(100));
    let mut free = Vec::new();
    for _ in 0..30 {
        let request = Request {
            fan: FanIntent::checked(1, 3700),
            ..Request::default()
        };
        let budget = Duration::from_millis(3260);
        assert_eq!(apply_within(&fixture, &request, budget).await, None);
        assert_eq!(*request.refusals.lock().unwrap(), [Refusal::Deadline]);
        assert_eq!(*request.fan_rows.lock().unwrap(), [0]);
        free.push(fixture.transactions.free());
    }
    assert_eq!(fixture.state.lock().unwrap().writes, 0);
    // Only the preflight replies still readable are kept, and each refusal
    // replaces the last one's.
    assert!(free[0] >= 250, "{free:?}");
    assert!(free.iter().all(|n| *n == free[0]), "{free:?}");
    // A request with the time it needs is then served.
    fixture.state.lock().unwrap().latency = None;
    let fixed = Request {
        fan: FanIntent::checked(1, 3700),
        ..Request::default()
    };
    assert!(apply(&fixture, &fixed).await.is_some());
}

#[tokio::test(start_paused = true)]
async fn a_fan_record_pins_its_ids_once_a_setter_may_have_been_sent() {
    // The recipe completes, or its first setter's submission fails after the
    // setter reached the device.
    for fail in [None, Some(1)] {
        let fixture = balanced();
        fixture.state.lock().unwrap().fail_write = fail;
        let fixed = Request {
            fan: FanIntent::checked(1, 3700),
            ..Request::default()
        };
        assert_eq!(apply(&fixture, &fixed).await.is_some(), fail.is_none());
        assert_eq!(
            fixture.state.lock().unwrap().writes,
            if fail.is_none() { 4 } else { 1 }
        );
        {
            // Undo the failed setter's half-applied fan state so reads agree.
            let mut s = fixture.state.lock().unwrap();
            s.fail_write = None;
            if fail.is_some() {
                s.fans = [0, 0];
            }
        }
        let recorded = sent_ids(&fixture);
        let free = fixture.transactions.free();
        // The record's two preflight pairs, five recipe pairs and five
        // cleanup pairs stay pinned, sent or not.
        assert_eq!(free, 254 - 24, "{fail:?}");
        for _ in 0..300 {
            assert!(read(&fixture).await.is_some());
        }
        let reads = &sent_ids(&fixture)[recorded.len()..];
        assert!(reads.len() > 2 * 254);
        assert!(reads.iter().all(|id| !recorded.contains(id)), "{fail:?}");
        // The reads keep only their last reply's ID.
        assert_eq!(fixture.transactions.free(), free - 1, "{fail:?}");
    }
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
