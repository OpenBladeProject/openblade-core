//! Read-worker lifetime across mutation entry points that return without a
//! mutation. Every entry point stops the reader before its admission checks;
//! a refusal that submitted nothing must leave the next read running, while a
//! fenced or integrity-failed source stays sealed. Only the injected
//! dispatcher, Reader and fixture port run.
use super::*;
use crate::discovery::performance::{
    ObservedPerformance,
    mutation::{Permit, Settlement},
};
use openblade_domain::thermal_mutation::{Destination, Progress};

/// Whether an observeState request runs a read job on the port and confirms
/// state. The retained observation is expired first, so only a running reader
/// can confirm it.
async fn reads(rig: &Rig) -> bool {
    rig.reader.expire_for_tests();
    let opened = rig.port.state.lock().unwrap().opened;
    let observed = fresh_observe(rig).await;
    rig.port.state.lock().unwrap().opened > opened
        && !observed["status"]["confirmedState"].is_null()
}

/// The writes so far, once every opened port has retired.
fn retired_writes(rig: &Rig) -> usize {
    let state = rig.port.state.lock().unwrap();
    assert_eq!(state.opened, state.retired);
    state.writes
}

/// Starts an observation whose read job is held on the port, then drops the
/// observation: the job stays abandoned on the reader until it is released.
async fn abandon_held_read(rig: &Rig, held: &Arc<AtomicBool>) {
    rig.reader.expire_for_tests();
    let holding = {
        let mut state = rig.port.state.lock().unwrap();
        state.held_read = Some(held.clone());
        state.holding.clone()
    };
    let observation = rig
        .source
        .lock()
        .await
        .observe_performance(USER, Instant::now() + Duration::from_secs(3));
    tokio::pin!(observation);
    // The port opening does not show the job reached the hold: under load it
    // can still fail at its deadline first. The fixture notifies once the read
    // is parked, so wait for that.
    tokio::select! {
        _ = &mut observation => panic!("the held read returned"),
        () = holding.notified() => {}
    }
}

/// Lets the abandoned read job finish.
fn release_held_read(rig: &Rig, held: &AtomicBool) {
    held.store(true, Ordering::Release);
    rig.port.state.lock().unwrap().held_read = None;
}

/// One framed request whose connection may end in a runtime error, which
/// `Rig::request` refuses. Returns the server's result and the reply bytes.
#[cfg(feature = "service-fan-mutation-v2")]
async fn exchange(rig: &Rig, request: Value) -> (Result<(), ExchangeError>, io::Result<Vec<u8>>) {
    let (mut client, server) = duplex(65536);
    let publications = rig.source.lock().await.publications();
    let server = serve_connection(
        Box::new(SidStream {
            inner: test_stream(server, USER, Fault::None),
            sid: SID.into(),
        }),
        rig.source.clone(),
        publications,
        Rc::new(Subscriptions::new(2)),
    );
    let client = async {
        crate::service_frame::write_payload(&mut client, &serde_json::to_vec(&request).unwrap())
            .await
            .unwrap();
        crate::service_frame::read_payload(&mut client).await
    };
    tokio::join!(server, client)
}

/// A permit refused at the Reader's own admission, before any submission.
struct Refused(Option<ReadAdmission>);
impl Permit for Refused {
    fn authorize(&self) -> Option<ReadAdmission> {
        self.0
    }
    fn resolve(
        &self,
        _: i32,
        _: ObservedPerformance,
        _: &AtomicBool,
    ) -> io::Result<Option<Destination>> {
        panic!("a refused permit never reaches a mutation worker")
    }
    fn record(&self, _: Progress) -> io::Result<()> {
        panic!("a refused permit never reaches a mutation worker")
    }
    fn retired(&self) -> io::Result<Settlement> {
        panic!("a refused permit never registers a mutation worker")
    }
}

/// `drain_active` is how the battery, temporary-charge and fan entry points
/// (and the fan steps of a profile reconciliation) join active work. Each may
/// then refuse without calling `mutate`; the next read restarts the reader.
#[tokio::test]
async fn a_drain_without_a_mutation_leaves_the_next_read_running() {
    let rig = Rig::new(MemoryFiles::default());
    assert!(reads(&rig).await);
    rig.reader.drain_active().await.unwrap();
    assert!(reads(&rig).await);
    // A second drain from the restarted reader restarts it again.
    rig.reader.drain_active().await.unwrap();
    rig.reader.drain_active().await.unwrap();
    assert!(reads(&rig).await);
    assert!(reads(&rig).await);
    assert_eq!(retired_writes(&rig), 0);
    rig.stop().await;
}

/// A drain canceled while it waits for an abandoned read job keeps the
/// restart: the phase is retained before the await, and the next read
/// restarts the reader only after that job has actually retired.
#[tokio::test]
async fn a_canceled_drain_still_restarts_the_next_read() {
    let rig = Rig::new(MemoryFiles::default());
    assert!(reads(&rig).await);
    let held = Arc::new(AtomicBool::new(false));
    abandon_held_read(&rig, &held).await;
    {
        let drain = rig.reader.drain_active();
        tokio::pin!(drain);
        assert!(
            tokio::time::timeout(Duration::from_millis(50), &mut drain)
                .await
                .is_err(),
            "the drain waits for the abandoned read job"
        );
    }
    // The abandoned job still holds the port: no read restarts beside it.
    assert!(!reads(&rig).await);
    release_held_read(&rig, &held);
    assert!(reads(&rig).await);
    retired_writes(&rig);
    rig.stop().await;
}

/// The Reader's own admission refusals in `mutate` and `mutate_cleanup` stop
/// the reader and submit nothing; the next read restarts it.
#[tokio::test]
async fn reader_admission_refusals_leave_the_next_read_running() {
    let rig = Rig::new(MemoryFiles::default());
    assert!(reads(&rig).await);
    let current = ReadCustody::authorize(rig.custody.as_ref()).unwrap();
    let stale = ReadAdmission {
        generation: [8; 32],
        ..current
    };
    let deadline = Instant::now() + Duration::from_secs(3);
    for permit in [None, Some(stale)] {
        let refused = rig.reader.mutate(Arc::new(Refused(permit)), deadline).await;
        assert_eq!(refused.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
        assert!(reads(&rig).await);
    }
    // Owned-Stop cleanup without an activated owner, then with a permit that
    // is not a cleanup permit.
    for permit in [Some(current), None] {
        let refused = rig
            .reader
            .mutate_cleanup(Arc::new(Refused(permit)), deadline)
            .await;
        assert_eq!(refused.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
        assert!(reads(&rig).await);
    }
    assert_eq!(rig.set(91, value(1, 1)).await["result"]["status"], 1);
    assert!(reads(&rig).await);
    let refused = rig
        .reader
        .mutate_cleanup(Arc::new(Refused(Some(current))), deadline)
        .await;
    assert_eq!(refused.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
    assert!(reads(&rig).await);
    assert_eq!(rig.port.state.lock().unwrap().writes, 2);
    assert_eq!(rig.custody.activations.load(Ordering::Acquire), 1);
    rig.stop().await;
}

/// An uncertain activation fences the source. A later drain, as the battery
/// and fan entry points run before their own admission, never unseals it, and
/// no later mutation activates again, before or after that drain.
#[tokio::test]
async fn a_drain_of_a_fenced_source_keeps_reads_sealed() {
    let rig = Rig::new(MemoryFiles::default());
    assert!(reads(&rig).await);
    rig.custody.activation_refusal.store(2, Ordering::Release);
    assert_eq!(rig.set(91, value(1, 1)).await["result"]["status"], 2);
    assert!(!reads(&rig).await);
    rig.custody.activation_refusal.store(0, Ordering::Release);
    assert_eq!(rig.set(92, value(1, 1)).await["result"]["status"], 2);
    rig.reader.drain_active().await.unwrap();
    assert!(!reads(&rig).await);
    assert_eq!(rig.set(93, value(1, 1)).await["result"]["status"], 2);
    let deadline = Instant::now() + Duration::from_secs(3);
    let current = ReadCustody::authorize(rig.custody.as_ref());
    assert!(
        rig.reader
            .mutate_cleanup(Arc::new(Refused(current)), deadline)
            .await
            .is_err()
    );
    assert!(!reads(&rig).await);
    assert_eq!(retired_writes(&rig), 0);
    assert_eq!(rig.custody.activations.load(Ordering::Acquire), 0);
    rig.stop().await;
}

/// A drain whose reader stop is unconfirmed stays sealed. An abandoned read
/// job fails at retirement while a fan request's drain waits for it. No read
/// restarts over that worker, even once the fault clears, and the next
/// mutation is refused before any activation or write.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn an_unconfirmed_reader_stop_at_a_drain_keeps_reads_sealed() {
    let rig = fan_rig(2, 0);
    assert!(reads(&rig).await);
    rig.port.state.lock().unwrap().read_worker_error = Some(Arc::new(|| {
        io::Error::other("injected read retirement failure")
    }));
    let held = Arc::new(AtomicBool::new(false));
    abandon_held_read(&rig, &held).await;
    // Released before or after the drain reaches it, the job's failure is
    // what the drain's stop reaps.
    let release = async {
        tokio::time::sleep(Duration::from_millis(50)).await;
        release_held_read(&rig, &held);
    };
    let (refused, ()) = tokio::join!(exchange(&rig, fan_request(1, Some(1), 3700)), release);
    // The unconfirmed stop ends the request with its own runtime error.
    assert!(
        matches!(&refused.0, Err(ExchangeError::Runtime(error)) if error.to_string() == "injected read retirement failure"),
        "{:?}",
        refused.0.err()
    );
    assert!(refused.1.is_err());
    rig.port.state.lock().unwrap().read_worker_error = None;
    let opened = rig.port.state.lock().unwrap().opened;
    assert!(!reads(&rig).await);
    assert_eq!(rig.set(2, value(1, 1)).await["result"]["status"], 2);
    assert!(!reads(&rig).await);
    assert_eq!(rig.port.state.lock().unwrap().opened, opened);
    assert_eq!(retired_writes(&rig), 0);
    assert_eq!(rig.custody.activations.load(Ordering::Acquire), 0);
}

/// Integrity loss after a drain is terminal: no read restarts, even once the
/// fixture reports integrity again.
#[tokio::test]
async fn integrity_loss_after_a_drain_keeps_reads_sealed() {
    let rig = Rig::new(MemoryFiles::default());
    assert!(reads(&rig).await);
    rig.reader.drain_active().await.unwrap();
    rig.reader.expire_for_tests();
    let opened = rig.port.state.lock().unwrap().opened;
    for lost in [true, false] {
        rig.custody.integrity_lost.store(lost, Ordering::Release);
        let observed = rig
            .source
            .lock()
            .await
            .observe_performance(USER, Instant::now() + Duration::from_secs(3))
            .await;
        assert!(observed.is_err_and(|error| {
            crate::installed::readiness::thermal_mutation::is_fatal_admission(&error)
        }));
        rig.reader.drain_active().await.unwrap();
        assert_eq!(rig.port.state.lock().unwrap().opened, opened);
    }
    assert!(rig.reader.custody().verify_integrity().is_err());
}

/// A battery request refused at fresh admission, after `apply_battery`
/// drained the reader, leaves reads running. Read custody is independent of
/// the refused write admission.
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn a_refused_battery_request_leaves_the_next_read_running() {
    let rig = Rig::new(MemoryFiles::default());
    assert!(reads(&rig).await);
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    let refused = rig
        .request(battery_request(
            1,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":70}),
        ))
        .await;
    assert_eq!(refused["result"]["status"], 2, "{refused}");
    assert_eq!(
        refused["result"]["message"],
        "Fresh battery admission is unavailable."
    );
    assert!(reads(&rig).await);
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    rig.stop().await;
}

/// A fan request refused at fresh admission, after `apply_fan_locked`
/// drained the reader, leaves reads running.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_refused_fan_request_leaves_the_next_read_running() {
    let rig = fan_rig(2, 0);
    assert!(reads(&rig).await);
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    let refused = rig.request(fan_request(1, Some(1), 3700)).await;
    assert_eq!(refused["result"]["status"], 2, "{refused}");
    assert_eq!(
        refused["result"]["message"],
        "Current fan admission is unavailable."
    );
    assert!(reads(&rig).await);
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    rig.origin.stop();
    rig.stop().await;
}

/// A fan request refused while the system is suspended submits nothing and
/// reads nothing. After resume, reads run again without a mutation.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_request_refused_in_low_power_reads_again_after_resume() {
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;
    let rig = fan_rig(2, 0);
    assert!(reads(&rig).await);
    rig.custody.lifecycle.ingress(POWER, 4);
    assert!(rig.custody.lifecycle.low_power_or_unknown());
    let refused = rig.request(fan_request(1, Some(1), 3700)).await;
    assert_eq!(refused["result"]["status"], 2, "{refused}");
    assert!(!reads(&rig).await);
    rig.custody.lifecycle.ingress(POWER, 18);
    assert!(!rig.custody.lifecycle.low_power_or_unknown());
    assert!(reads(&rig).await);
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    rig.origin.stop();
    rig.stop().await;
}

/// The production-shaped variant of the low-power case: with the scoped
/// capture (`CapturedFresh`, as `WindowsGuard::capture`), fan and battery
/// requests while suspended are refused at capture, after their drain. Read
/// custody refuses too, so no read job runs until resume; then reads run
/// without a mutation.
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn requests_refused_at_capture_in_low_power_read_again_after_resume() {
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;
    let rig = fan_rig(2, 0);
    assert!(reads(&rig).await);
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    rig.custody.lifecycle.ingress(POWER, 4);
    let opened = rig.port.state.lock().unwrap().opened;
    let fan = rig.request(fan_request(1, Some(1), 3700)).await;
    assert_eq!(fan["result"]["status"], 2, "{fan}");
    assert!(
        fan["result"]["message"]
            .as_str()
            .unwrap()
            .starts_with("Fan configuration admission failed"),
        "{fan}"
    );
    let battery = rig
        .request(battery_request(
            2,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":70}),
        ))
        .await;
    assert_eq!(battery["result"]["status"], 2, "{battery}");
    assert!(
        battery["result"]["message"]
            .as_str()
            .unwrap()
            .starts_with("Battery configuration admission failed"),
        "{battery}"
    );
    assert!(!reads(&rig).await);
    assert_eq!(rig.port.state.lock().unwrap().opened, opened);
    rig.custody.lifecycle.ingress(POWER, 18);
    assert!(reads(&rig).await);
    assert_eq!(retired_writes(&rig), 0);
    rig.origin.stop();
    rig.stop().await;
}

/// A Battery source event whose temporary-charge cancellation is paused
/// (`battery_source_event` returns after its drain without a mutation) leaves
/// reads running.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn a_paused_battery_source_event_leaves_the_next_read_running() {
    for lose_logon in [true, false] {
        let rig = Rig::new(MemoryFiles::default());
        assert!(reads(&rig).await);
        let enabled = rig
            .request(battery_request(2, "setFullChargeOverride", json!(true)))
            .await;
        assert_eq!(enabled["result"]["status"], 1, "{enabled}");
        rig.port.state.lock().unwrap().actual = 0;
        rig.metadata.store(1, Ordering::Release);
        if lose_logon {
            rig.custody.authentication.store(2, Ordering::Release);
        } else {
            rig.custody.fresh_refuse.store(true, Ordering::Release);
        }
        rig.custody.lifecycle.ingress(
            windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
            10,
        );
        rig.source
            .lock()
            .await
            .process_power_source()
            .await
            .unwrap();
        assert!(!rig.storage.temporary_desired());
        assert_eq!(rig.port.state.lock().unwrap().writes, 1);
        assert!(reads(&rig).await);
        rig.stop().await;
    }
}
