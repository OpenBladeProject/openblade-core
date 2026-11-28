//! The lifecycle operations over the installed runtime's own gate, storage
//! and fixture device: a shutdown waits for the mutation in flight and
//! leaves an uncertain write to the existing Stop cleanup, and a relayed
//! report is answered without touching the mutation in flight. No SCM, pipe
//! or lifecycle supplier is reached.
use super::*;
use crate::installed::readiness::session_lifecycle::Ports;
#[cfg(feature = "service-session-power-relay-v1")]
use crate::installed::readiness::session_lifecycle::relay::{ACCEPTED, Relay};
#[cfg(feature = "service-lifecycle-shutdown-v1")]
use crate::installed::readiness::session_lifecycle::shutdown::{SHUTTING_DOWN, ShutdownSignal};

/// The relay's QPC clock: 1 kHz, its reports stamped now.
#[cfg(feature = "service-session-power-relay-v1")]
const NOW: i64 = 10_000;

/// What the Rig's lifecycle ports record.
pub(super) struct LifecycleProbes {
    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    stops: Arc<AtomicUsize>,
    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    signal: Rc<ShutdownSignal>,
}

impl LifecycleProbes {
    pub(super) fn new() -> (Self, Ports) {
        #[cfg(feature = "service-lifecycle-shutdown-v1")]
        let stops = Arc::new(AtomicUsize::new(0));
        // No delay: the managed delay has its own test.
        #[cfg(feature = "service-lifecycle-shutdown-v1")]
        let signal = {
            let counted = stops.clone();
            Rc::new(ShutdownSignal::new(
                std::time::Duration::ZERO,
                Arc::new(move || {
                    counted.fetch_add(1, Ordering::AcqRel);
                }),
            ))
        };
        #[cfg(feature = "service-session-power-relay-v1")]
        let built = tokio::time::Instant::now();
        let ports = Ports {
            #[cfg(feature = "service-lifecycle-shutdown-v1")]
            shutdown: Some(signal.clone()),
            // A clear gate SCM never moved, and a clock at NOW when the Rig
            // is built that runs on with the runtime's, as QPC runs on with
            // the receipt a report is aged from.
            #[cfg(feature = "service-session-power-relay-v1")]
            relay: Some(Rc::new(Relay::new(
                || {
                    Some(openblade_domain::low_power_gate::View {
                        latest: 0,
                        active: false,
                    })
                },
                move |timestamp| {
                    let elapsed = u64::try_from(NOW.checked_sub(timestamp)?).ok()?;
                    Some(std::time::Duration::from_millis(elapsed) + built.elapsed())
                },
            ))),
        };
        (
            Self {
                #[cfg(feature = "service-lifecycle-shutdown-v1")]
                stops,
                #[cfg(feature = "service-lifecycle-shutdown-v1")]
                signal,
            },
            ports,
        )
    }
}

#[cfg(feature = "service-lifecycle-shutdown-v1")]
impl Rig {
    async fn shutdown(&self, n: u8) -> Value {
        self.request_as(
            json!({"operation":"shutdownOpenBlade","version":91,"requestId":format!("aaaaaaaa-bbbb-cccc-eeef-{n:012}")}),
            SYSTEM,
            SID,
        )
        .await
    }
    /// The stops once a requested one has run on its own task.
    async fn stops(&self) -> usize {
        self.lifecycle.signal.settled().await;
        self.lifecycle.stops.load(Ordering::Acquire)
    }
}

/// Starts `rig.set(n, value)` with its first write held, and returns it
/// once that write has been submitted.
async fn hold_first_write<'a>(
    rig: &'a Rig,
    release: &Arc<AtomicBool>,
    n: u8,
    value: Value,
) -> Pin<Box<dyn std::future::Future<Output = Value> + 'a>> {
    rig.port.state.lock().unwrap().held_write = Some((1, release.clone()));
    let mut set = Box::pin(rig.set(n, value));
    let entered = async {
        while rig.port.state.lock().unwrap().writes != 1 {
            tokio::task::yield_now().await;
        }
    };
    tokio::select! {
        _ = &mut set => panic!("held mutation completed"),
        entered = tokio::time::timeout(HANG_GUARD, entered) => entered.unwrap(),
    }
    set
}

/// Managed's complete shutdown waits for the user request in flight. The
/// native one waits on the runtime's gate: the mutation finishes, is
/// answered, and only then is the shutdown answered and the Stop requested.
#[cfg(feature = "service-lifecycle-shutdown-v1")]
#[tokio::test]
async fn installed_shutdown_waits_for_the_mutation_in_flight() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    let release = Arc::new(AtomicBool::new(false));
    let mut set = hold_first_write(&rig, &release, 1, value(2, 1)).await;
    let shutdown = rig.shutdown(1);
    tokio::pin!(shutdown);
    tokio::select! {
        _ = &mut shutdown => panic!("the shutdown overtook the mutation in flight"),
        _ = &mut set => panic!("held mutation completed"),
        () = tokio::time::sleep(std::time::Duration::from_millis(50)) => {}
    }
    assert_eq!(rig.stops().await, 0);
    release.store(true, Ordering::Release);
    let (set, shutdown) = tokio::join!(set, shutdown);
    assert_eq!(set["result"]["status"], 1, "{set}");
    assert_eq!(
        shutdown["result"],
        json!({"status": 0, "message": SHUTTING_DOWN})
    );
    assert_eq!(rig.stops().await, 1);
    assert_eq!(rig.port.state.lock().unwrap().writes, 2);
    rig.stop().await;
}

/// A write whose submission is uncertain fences the writes after it. The
/// shutdown neither waits on nor repairs it: it is answered, the Stop is
/// requested, and the existing drain retires with no further write.
#[cfg(feature = "service-lifecycle-shutdown-v1")]
#[tokio::test]
async fn installed_shutdown_after_an_uncertain_write_leaves_it_to_the_stop_cleanup() {
    let rig = Rig::new(MemoryFiles::default());
    rig.port.state.lock().unwrap().fail_write = Some(1);
    assert_eq!(rig.set(1, value(1, 1)).await["result"]["status"], 2);
    assert_eq!(rig.get().await["status"]["writesBlocked"], true);
    let shutdown = rig.shutdown(1).await;
    assert_eq!(shutdown["result"]["message"], SHUTTING_DOWN);
    assert_eq!(rig.stops().await, 1);
    // Repeated, it stops nothing more.
    assert_eq!(rig.shutdown(2).await["result"]["status"], 0);
    assert_eq!(rig.stops().await, 1);
    rig.stop().await;
    assert_eq!(rig.port.state.lock().unwrap().writes, 1);
    assert!(
        Storage::open(Box::new(rig.files.clone()), SID)
            .unwrap()
            .pending(),
        "the uncertain write stays recorded for recovery"
    );
}

/// A Session Agent report that arrives while a mutation's write is in
/// flight is answered at once and changes nothing the mutation reads: the
/// mutation then completes as it would have.
#[cfg(feature = "service-session-power-relay-v1")]
#[tokio::test]
async fn installed_report_during_a_write_is_answered_without_touching_it() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    let release = Arc::new(AtomicBool::new(false));
    let mut set = hold_first_write(&rig, &release, 1, value(2, 1)).await;
    let report = rig.request(json!({
        "operation": "reportSessionPowerTransition",
        "version": 91,
        "requestId": "aaaaaaaa-bbbb-cccc-eeef-000000000001",
        "instanceId": "aaaaaaaa-bbbb-4ccc-8ddd-000000000501",
        "instanceStartedQpc": 1000,
        "sequence": 1,
        "timestampQpc": NOW,
        "suspending": true,
    }));
    tokio::pin!(report);
    let report = tokio::select! {
        report = &mut report => report,
        _ = &mut set => panic!("held mutation completed"),
    };
    assert_eq!(report["result"], json!({"status": 0, "message": ACCEPTED}));
    assert_eq!(rig.port.state.lock().unwrap().writes, 1);
    release.store(true, Ordering::Release);
    assert_eq!(set.await["result"]["status"], 1);
    rig.stop().await;
}
