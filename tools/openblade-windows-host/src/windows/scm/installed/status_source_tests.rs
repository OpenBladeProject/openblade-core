use std::sync::{Mutex, mpsc};

use serde_json::{Value, json, to_value};
use tokio::sync::oneshot;

use super::*;
use crate::installed::readiness::Logon;

#[tokio::test]
async fn private_pipe_serves_and_retires_observed_status() {
    use crate::installed::readiness::{
        DRAIN_TIMEOUT, StatusRequest, StatusResponse, read_payload, serve, verify_peer,
        windows::private_endpoint, write_frame,
    };
    use std::{rc::Rc, sync::atomic::AtomicUsize};
    use tokio::time::timeout;

    timeout(Duration::from_secs(5), async {
        let (open, _) = private_endpoint(|session| Rc::new(move || Ok(Some(session))));
        let mut endpoint = open().unwrap();
        let scans = AtomicUsize::new(0);
        let mut source = ObservedStatus::new(move |cancel| {
            match scans.fetch_add(1, Ordering::Relaxed) {
                0 => Evidence {
                    controllers: vec!["RazerAppEngine".to_owned()],
                    ..evidence(matched(HidMetadata::Candidate), Power::Battery)
                },
                1 => evidence(matched(HidMetadata::Unavailable), Power::Unknown),
                2 => {
                    while !cancel.load(Ordering::Relaxed) {
                        std::thread::park_timeout(Duration::from_millis(1));
                    }
                    // A completed but expired scan must never supply the reply.
                    evidence(DeviceMetadata::Unsupported, Power::Battery)
                }
                _ => panic!("unexpected additional scan"),
            }
        });
        let request =
            StatusRequest::get("aa40bce5-c278-4179-a620-580ac694b7d8".to_owned()).unwrap();
        let pid = std::process::id();
        for (revision, power, controllers, message) in [
            (0, 1, json!(["RazerAppEngine"]), "matching control HID"),
            (1, 3, json!([]), "metadata unavailable"),
            (2, 3, json!([]), "Device identity is unavailable"),
        ] {
            let client = async {
                let mut client = endpoint.connect_probe().await?;
                verify_peer(&*client, pid)?;
                write_frame(&mut *client, &request).await?;
                let response = read_payload(&mut *client).await?;
                verify_peer(&*client, pid)?;
                Ok::<_, io::Error>(response)
            };
            // Resolve the real request token on the server after the frame read.
            // The private exchange uses the normal interactive caller path.
            let (server, response) =
                tokio::join!(serve(&mut source, endpoint.accept(), None), client);
            drop(server.unwrap());
            let response = response.unwrap();
            StatusResponse::verify(&response, &request).unwrap();
            let frame: Value = serde_json::from_slice(&response).unwrap();
            let status = &frame["status"];
            assert_read_only(status);
            assert_eq!(status["revision"], revision);
            assert_eq!(status["controlAccess"], 0);
            assert_eq!(status["activePowerSource"], power);
            assert_eq!(status["potentialControllers"], controllers);
            assert_eq!(status["unsupportedLaptop"], false);
            assert!(status["message"].as_str().unwrap().contains(message));
            println!("PHASE9O_FRAME {}", std::str::from_utf8(&response).unwrap());
        }

        endpoint.retire().await.unwrap();
        timeout(DRAIN_TIMEOUT, source.drain())
            .await
            .unwrap()
            .unwrap();
        assert!(source.refresh.is_none());
        open().unwrap().retire().await.unwrap();
    })
    .await
    .unwrap();
}

const SYSTEM: Caller = Caller {
    pid: Some(17),
    logon: Logon::System,
    session: 0,
    control_session: None,
};

const INACTIVE: Caller = Caller {
    pid: None,
    logon: Logon::RemoteInteractive,
    session: 9,
    control_session: None,
};

fn evidence(device: DeviceMetadata, power: Power) -> Evidence {
    Evidence {
        device,
        power,
        controllers: vec![],
        controller_warning: Some(CONTROLLER_UNKNOWN),
        // A host on which native reads no pad and no journal.
        #[cfg(feature = "service-hyperboost-v1")]
        hyperboost: HyperBoostStatus {
            restore_pending: Some(false),
            ..HyperBoostStatus::UNOBSERVED
        },
    }
}

fn matched(hid: HidMetadata) -> DeviceMetadata {
    DeviceMetadata::MatchedHost {
        model: "RZ09-0581".into(),
        hid,
    }
}

pub(in crate::installed::readiness) fn in_memory_source(
    on_battery: impl Fn(&AtomicBool) -> bool + Send + Sync + 'static,
) -> Box<dyn StatusSource> {
    Box::new(ObservedStatus::new(move |cancel| {
        if on_battery(cancel) {
            evidence(matched(HidMetadata::Candidate), Power::Battery)
        } else {
            evidence(DeviceMetadata::Unsupported, Power::ExternalUnknown)
        }
    }))
}

pub(in crate::installed::readiness) fn in_memory_source_with_firmware(
    has_metadata: impl Fn() -> bool + Send + Sync + 'static,
    firmware: FirmwareSource,
) -> Box<dyn StatusSource> {
    let mut source = ObservedStatus::new(move |_| {
        let device = if has_metadata() {
            matched(HidMetadata::Candidate)
        } else {
            DeviceMetadata::Unavailable
        };
        evidence(device, Power::ExternalUnknown)
    });
    source.firmware = Some(firmware);
    Box::new(source)
}

fn assert_read_only(status: &Value) {
    assert_eq!(status["deviceVerified"], false);
    assert_eq!(status["writesBlocked"], true);
    assert_eq!(status["serviceVersion"], env!("CARGO_PKG_VERSION"));
    // No identity, no record: managed's Unavailable record.
    assert_eq!(status["capabilities"], capability_record(false));
    for field in [
        "confirmedState",
        "liveReadings",
        "performanceOverride",
        "fanOverride",
        "lightingOverride",
    ] {
        assert!(status[field].is_null(), "{field}: {status}");
    }
    assert_eq!(status["fullChargeOverride"], false);
    assert_eq!(status["externalChanges"], json!([]));
}

#[tokio::test]
async fn supported_and_unsupported_metadata_never_admit_control() {
    let observations = Mutex::new(
        [
            evidence(matched(HidMetadata::Candidate), Power::Battery),
            evidence(DeviceMetadata::Unsupported, Power::ExternalUnknown),
        ]
        .into_iter(),
    );
    let mut source = ObservedStatus::new(move |_| observations.lock().unwrap().next().unwrap());
    let supported = source.snapshot(SYSTEM, Instant::now()).await.unwrap();
    supported.verify(0).unwrap();
    let supported = to_value(supported).unwrap();
    assert_read_only(&supported);
    assert_eq!(supported["revision"], 0);
    assert_eq!(supported["unsupportedLaptop"], false);
    assert_eq!(supported["activePowerSource"], 1);
    assert_eq!(
        supported["desiredState"]["performance"],
        json!({"value": 3, "source": 1})
    );
    assert!(supported["message"].as_str().unwrap().contains("RZ09-0581"));
    assert!(supported["controllerDetectionWarning"].is_string());

    let unsupported = to_value(source.snapshot(SYSTEM, Instant::now()).await.unwrap()).unwrap();
    assert_read_only(&unsupported);
    assert_eq!(unsupported["revision"], 1);
    assert_eq!(unsupported["unsupportedLaptop"], true);
    assert_eq!(unsupported["activePowerSource"], 3);
    assert_eq!(
        unsupported["message"],
        "No verified supported device is available."
    );
    assert_eq!(
        unsupported["desiredState"]["performance"],
        json!({"value": 1, "source": 1})
    );
    source.drain().await.unwrap();
    assert!(source.snapshot(SYSTEM, Instant::now()).await.is_err());
}

#[tokio::test]
async fn partial_observations_replace_prior_fields_and_caller_projection_keeps_revision() {
    let initial = Evidence {
        controllers: vec!["RazerAppEngine".to_owned()],
        ..evidence(matched(HidMetadata::NoCandidate), Power::Battery)
    };
    let observations = Mutex::new(
        [
            initial,
            evidence(matched(HidMetadata::Unavailable), Power::Unknown),
            evidence(matched(HidMetadata::Unavailable), Power::ExternalUnknown),
            evidence(matched(HidMetadata::Ambiguous), Power::ExternalUnknown),
            evidence(DeviceMetadata::Unavailable, Power::Unknown),
        ]
        .into_iter(),
    );
    let mut source = ObservedStatus::new(move |_| observations.lock().unwrap().next().unwrap());
    let initial = to_value(source.snapshot(SYSTEM, Instant::now()).await.unwrap()).unwrap();
    assert_eq!(initial["potentialControllers"], json!(["RazerAppEngine"]));
    let partial = to_value(source.snapshot(SYSTEM, Instant::now()).await.unwrap()).unwrap();
    assert_eq!(partial["revision"], 1);
    assert_eq!(partial["potentialControllers"], json!([]));
    assert_eq!(partial["controllerDetectionWarning"], CONTROLLER_UNKNOWN);
    assert_eq!(partial["activePowerSource"], 3);
    assert!(
        partial["message"]
            .as_str()
            .unwrap()
            .contains("metadata unavailable")
    );

    let inactive = source.snapshot(INACTIVE, Instant::now()).await.unwrap();
    inactive.verify(1).unwrap();
    let inactive = to_value(inactive).unwrap();
    assert_read_only(&inactive);
    assert_eq!(inactive["revision"], partial["revision"]);
    assert_eq!(inactive["profiles"], partial["profiles"]);
    assert_eq!(inactive["desiredState"], partial["desiredState"]);
    assert_eq!(
        inactive["message"],
        "Another Windows session currently controls OpenBlade hardware."
    );

    let ambiguous = to_value(source.snapshot(SYSTEM, Instant::now()).await.unwrap()).unwrap();
    assert_eq!(ambiguous["revision"], 2);
    assert!(ambiguous["message"].as_str().unwrap().contains("ambiguous"));
    let unavailable = to_value(source.snapshot(SYSTEM, Instant::now()).await.unwrap()).unwrap();
    assert_eq!(unavailable["revision"], 3);
    // Mailbox compositions serve explicit thermal reads, so they report the missing
    // fresh read instead of the read-only composition's denied observation.
    assert_eq!(
        unavailable["message"],
        if cfg!(feature = "service-mailbox-observation-v1") {
            "Device identity is unavailable. No fresh thermal observation is confirmed."
        } else {
            "Device identity is unavailable. Firmware observation denied: adapter, activity and managed drain admission are unavailable."
        }
    );
}

#[tokio::test]
async fn in_memory_observer_publishes_one_revision_for_both_views_and_closes_on_failure() {
    let observations = Mutex::new(
        [
            evidence(matched(HidMetadata::Candidate), Power::Unknown),
            evidence(matched(HidMetadata::Candidate), Power::ExternalUnknown),
            evidence(matched(HidMetadata::Candidate), Power::Battery),
        ]
        .into_iter(),
    );
    let mut source = ObservedStatus::new(move |_| observations.lock().unwrap().next().unwrap());
    assert!(source.publications().is_none());
    let initial = source.snapshot(SYSTEM, Instant::now()).await.unwrap();
    let mut feed = source.publications().unwrap();
    let seeded = Arc::clone(&feed.borrow_and_update());
    assert_eq!(
        to_value(initial).unwrap(),
        to_value(&seeded.active).unwrap()
    );
    seeded.inactive.verify(1).unwrap();

    source.observe_since(Instant::now()).await.unwrap();
    assert!(!feed.has_changed().unwrap());
    assert!(Arc::ptr_eq(&seeded, &feed.borrow()));

    let requested = Instant::now();
    let changed = source.snapshot(SYSTEM, requested).await.unwrap();
    assert!(feed.has_changed().unwrap());
    let latest = Arc::clone(&feed.borrow_and_update());
    assert_eq!(changed.revision(), seeded.active.revision() + 1);
    assert_eq!(
        to_value(changed).unwrap(),
        to_value(&latest.active).unwrap()
    );
    let inactive = source.snapshot(INACTIVE, requested).await.unwrap();
    assert_eq!(inactive.revision(), latest.active.revision());
    assert_eq!(
        to_value(inactive).unwrap(),
        to_value(&latest.inactive).unwrap()
    );

    // The fake panics on exhaustion, exercising the native worker failure path.
    assert!(source.observe_since(Instant::now()).await.is_err());
    assert!(feed.has_changed().is_err());
    assert!(source.publications().is_none());
    assert!(source.snapshot(SYSTEM, requested).await.is_err());
    source.drain().await.unwrap();
}

#[tokio::test(start_paused = true)]
async fn in_memory_publication_deadline_uses_shared_observation_without_heartbeat() {
    let mut source = ObservedStatus::new(|_| panic!("fresh cache must avoid an observer thread"));
    source
        .publish(Some(evidence(DeviceMetadata::Unsupported, Power::Unknown)))
        .unwrap();
    let mut feed = source.publications().unwrap();
    let initial = Arc::clone(&feed.borrow_and_update());
    let observed = Instant::now();
    source.observed_at = Some(observed);
    let due = observed + REFRESH_INTERVAL;
    assert_eq!(source.refresh_publications().await.unwrap(), due);

    tokio::time::advance(REFRESH_INTERVAL - Duration::from_millis(1)).await;
    let shared = source
        .snapshot(SYSTEM, observed - Duration::from_millis(1))
        .await
        .unwrap();
    assert_eq!(shared.revision(), initial.active.revision());
    assert_eq!(source.refresh_publications().await.unwrap(), due);
    assert!(!feed.has_changed().unwrap());
    assert!(Arc::ptr_eq(&initial, &feed.borrow()));

    // A GetStatus observation is recorded by this same owner; the scheduler
    // must return that observation's due time, even near an earlier deadline.
    let newest = Instant::now();
    source.observed_at = Some(newest);
    tokio::time::advance(Duration::from_millis(1)).await;
    assert_eq!(
        source.refresh_publications().await.unwrap(),
        newest + REFRESH_INTERVAL
    );
    assert!(!feed.has_changed().unwrap());
    source.drain().await.unwrap();
    assert!(feed.has_changed().is_err());
    assert!(source.refresh_publications().await.is_err());
}

#[tokio::test]
async fn expired_refresh_discards_old_evidence_and_prevents_overlapping_reads() {
    let (entered, running) = oneshot::channel();
    let (release, wait) = mpsc::channel();
    let pending = Mutex::new(Some((entered, wait)));
    let mut source = ObservedStatus::new(move |cancel| {
        if let Some((entered, wait)) = pending.lock().unwrap().take() {
            entered.send(()).unwrap();
            wait.recv().unwrap();
            assert!(cancel.load(Ordering::Relaxed));
        } else {
            return evidence(DeviceMetadata::Unsupported, Power::ExternalUnknown);
        }
        evidence(matched(HidMetadata::Candidate), Power::Battery)
    });
    source
        .publish(Some(Evidence {
            controllers: vec!["RazerAppEngine".to_owned()],
            ..evidence(matched(HidMetadata::Candidate), Power::Battery)
        }))
        .unwrap();
    let mut feed = source.publications().unwrap();
    feed.borrow_and_update();
    let (status, ()) = tokio::join!(source.snapshot(SYSTEM, Instant::now()), async {
        running.await.unwrap();
    });
    let stale = to_value(status.unwrap()).unwrap();
    assert_read_only(&stale);
    assert_eq!(stale["revision"], 1);
    assert_eq!(stale["unsupportedLaptop"], false);
    assert_eq!(stale["activePowerSource"], 3);
    assert_eq!(stale["potentialControllers"], json!([]));
    assert_eq!(stale["controllerDetectionWarning"], CONTROLLER_UNKNOWN);
    assert!(!stale["message"].as_str().unwrap().contains("RZ09"));
    assert!(feed.has_changed().unwrap());
    assert_eq!(to_value(&feed.borrow_and_update().active).unwrap(), stale);
    assert_eq!(
        to_value(source.snapshot(SYSTEM, Instant::now()).await.unwrap()).unwrap(),
        stale
    );
    assert!(!feed.has_changed().unwrap());

    release.send(()).unwrap();
    while !source.refresh.as_ref().unwrap().worker.is_finished() {
        tokio::task::yield_now().await;
    }
    let recovered = to_value(source.snapshot(SYSTEM, Instant::now()).await.unwrap()).unwrap();
    assert_read_only(&recovered);
    assert_eq!(recovered["revision"], 2);
    assert_eq!(recovered["activePowerSource"], 3);
    assert_eq!(
        recovered["message"],
        "No verified supported device is available."
    );
    source.drain().await.unwrap();
    assert!(source.refresh.is_none());
}

#[tokio::test]
async fn dropping_snapshot_preserves_worker_until_drain_acknowledges_completion() {
    let (entered, running) = oneshot::channel();
    let (canceled, saw_cancel) = oneshot::channel();
    let (release, wait) = mpsc::channel();
    let pending = Mutex::new(Some((entered, canceled, wait)));
    let mut source = ObservedStatus::new(move |cancel| {
        let (entered, canceled, wait) = pending.lock().unwrap().take().unwrap();
        entered.send(()).unwrap();
        while !cancel.load(Ordering::Relaxed) {
            std::thread::park_timeout(Duration::from_millis(1));
        }
        canceled.send(()).unwrap();
        wait.recv().unwrap();
        evidence(matched(HidMetadata::Candidate), Power::Battery)
    });
    tokio::select! {
        status = source.snapshot(SYSTEM, Instant::now()) => panic!("snapshot completed before cancellation: {}", status.is_ok()),
        result = running => result.unwrap(),
    }

    let mut drain = source.drain();
    tokio::select! {
        result = &mut drain => panic!("drain completed before worker retirement: {result:?}"),
        result = saw_cancel => result.unwrap(),
    }
    assert!(
        tokio::time::timeout(Duration::from_millis(20), &mut drain)
            .await
            .is_err()
    );
    drop(drain);
    assert!(source.refresh.is_some());
    release.send(()).unwrap();
    source.drain().await.unwrap();
    assert!(source.refresh.is_none());
    assert!(source.snapshot(SYSTEM, Instant::now()).await.is_err());
}

#[tokio::test]
async fn failed_prior_refresh_retires_without_polling_its_result_twice() {
    let mut source = ObservedStatus::new(|_| unreachable!("unexpected replacement scan"));
    let worker = std::thread::spawn(|| panic!("injected observer failure"));
    while !worker.is_finished() {
        tokio::task::yield_now().await;
    }
    source.refresh = Some(Refresh {
        cancel: Arc::new(AtomicBool::new(true)),
        worker,
    });
    assert!(source.snapshot(SYSTEM, Instant::now()).await.is_err());
    source.drain().await.unwrap();
}

#[cfg(feature = "service-mailbox-observation-v1")]
pub(in crate::installed::readiness) struct InjectedStatusOrigin {
    pub(in crate::installed::readiness) live: AtomicBool,
    pub(in crate::installed::readiness) transport_drains: std::sync::atomic::AtomicUsize,
    metadata_drains: std::sync::atomic::AtomicUsize,
}
#[cfg(feature = "service-mailbox-observation-v1")]
impl InjectedStatusOrigin {
    fn new() -> Arc<Self> {
        Arc::new(Self {
            live: AtomicBool::new(true),
            transport_drains: 0.into(),
            metadata_drains: 0.into(),
        })
    }
}
#[cfg(feature = "service-mailbox-observation-v1")]
impl StatusOrigin for InjectedStatusOrigin {
    fn verify(&self) -> io::Result<()> {
        if self.live.load(Ordering::Acquire) {
            Ok(())
        } else {
            Err(invalid("injected origin lost"))
        }
    }
    fn revoke(&self) {
        self.live.store(false, Ordering::Release);
    }
    fn confirm_metadata_drain(&self) -> io::Result<()> {
        self.metadata_drains.fetch_add(1, Ordering::AcqRel);
        Ok(())
    }
    fn confirm_transport_drain(&self) -> io::Result<()> {
        self.transport_drains.fetch_add(1, Ordering::AcqRel);
        Ok(())
    }
}
#[cfg(feature = "service-mailbox-observation-v1")]
pub(in crate::installed::readiness) fn in_memory_guarded_no_reader()
-> (Box<dyn StatusSource>, Arc<InjectedStatusOrigin>) {
    let origin = InjectedStatusOrigin::new();
    let mailbox = super::super::mailbox_read::MailboxSource::compose(origin.clone(), || {
        Err(invalid("prior reader already consumed"))
    })
    .unwrap();
    (
        compose_guarded_status(
            ObservedStatus::new(|_| evidence(matched(HidMetadata::Candidate), Power::Battery)),
            origin.clone(),
            mailbox,
        ),
        origin,
    )
}

#[cfg(feature = "service-mailbox-observation-v1")]
#[tokio::test]
async fn guarded_no_reader_opens_metadata_but_certifies_only_own_transport_attempt_after_drain() {
    let (mut source, origin) = in_memory_guarded_no_reader();
    assert!(source.confirm_drained().is_err());
    let status = source.snapshot(SYSTEM, Instant::now()).await.unwrap();
    let status = to_value(status).unwrap();
    assert_read_only(&status);
    assert!(
        status["message"]
            .as_str()
            .unwrap()
            .contains("before native I/O")
    );
    assert!(
        source
            .observe_performance(SYSTEM, Instant::now() + Duration::from_secs(1))
            .await
            .unwrap()
            .is_none()
    );
    assert_eq!(origin.transport_drains.load(Ordering::Acquire), 0);
    source.drain().await.unwrap();
    source.confirm_drained().unwrap();
    assert_eq!(origin.transport_drains.load(Ordering::Acquire), 1);
    assert_eq!(origin.metadata_drains.load(Ordering::Acquire), 0);
}
#[cfg(feature = "service-mailbox-observation-v1")]
#[tokio::test]
async fn guarded_origin_loss_is_fatal_even_for_no_reader_status() {
    let (mut source, origin) = in_memory_guarded_no_reader();
    source.snapshot(SYSTEM, Instant::now()).await.unwrap();
    origin.live.store(false, Ordering::Release);
    assert!((source.publication_guard())().is_err());
    assert!(source.snapshot(SYSTEM, Instant::now()).await.is_err());
    assert!(source.refresh_publications().await.is_err());
    let denied = super::super::mailbox_read::MailboxSource::compose(origin, || {
        panic!("invalid origin cannot enter reader factory")
    });
    assert!(denied.is_err());
}
#[cfg(feature = "service-mailbox-observation-v1")]
#[tokio::test]
async fn guarded_sticky_read_loss_retains_current_metadata_and_expires_both_projections() {
    use super::super::mailbox_read::{MailboxSource, ReadCustody, ReadSource};
    use crate::discovery::performance::{
        ReadAdmission,
        worker_tests::{BlockAt, blocking_worker_with_authority},
    };
    use std::sync::atomic::AtomicU8;
    struct Custody {
        mapping: Arc<AtomicBool>,
        state: AtomicU8,
    }
    impl ReadCustody for Custody {
        fn authorize(&self) -> Option<ReadAdmission> {
            if !self.mapping.load(Ordering::Acquire) {
                self.revoke();
            }
            (self.state.load(Ordering::Acquire) == 0).then_some(ReadAdmission {
                lifecycle: 1,
                generation: [7; 32],
            })
        }
        fn revoke(&self) {
            let _ = self
                .state
                .compare_exchange(0, 1, Ordering::AcqRel, Ordering::Acquire);
        }
        fn retire(&self) -> io::Result<()> {
            self.state.store(2, Ordering::Release);
            Ok(())
        }
        fn confirm_drained(&self) -> io::Result<()> {
            if self.state.load(Ordering::Acquire) == 2 {
                Ok(())
            } else {
                Err(invalid("not retired"))
            }
        }
    }
    let origin = InjectedStatusOrigin::new();
    let mapping = Arc::new(AtomicBool::new(true));
    let custody = Arc::new(Custody {
        mapping: mapping.clone(),
        state: AtomicU8::new(0),
    });
    let mut held = None;
    let reader = ReadSource::compose(custody, |lease| {
        let (worker, control) =
            blocking_worker_with_authority(BlockAt::Verify, None, move || lease.authorize());
        held = Some(control);
        worker
    });
    let held = held.unwrap();
    let metadata_available = Arc::new(AtomicBool::new(true));
    let flag = metadata_available.clone();
    let mut source = GuardedStatus {
        #[cfg(feature = "service-thermal-mutation-v1")]
        mutation: None,
        inner: ObservedStatus::new(move |_| {
            if flag.load(Ordering::Acquire) {
                evidence(matched(HidMetadata::Candidate), Power::Battery)
            } else {
                Evidence {
                    controllers: vec!["current-controller".into()],
                    ..evidence(DeviceMetadata::Unavailable, Power::Unknown)
                }
            }
        }),
        origin: origin.clone(),
        power_epoch: None,
        #[cfg(any(
            feature = "service-lifecycle-shutdown-v1",
            feature = "service-session-power-relay-v1"
        ))]
        lifecycle: Default::default(),
        mailbox: MailboxSource::compose(origin, || Ok(reader.clone())).unwrap(),
    };
    source.snapshot(SYSTEM, Instant::now()).await.unwrap();
    held.release.send(()).unwrap();
    let fresh = source
        .observe_performance(SYSTEM, Instant::now() + Duration::from_secs(2))
        .await
        .unwrap()
        .unwrap();
    assert!(fresh.has_fresh_mailbox_read());
    reader.expire_for_tests();
    source.refresh_publications().await.unwrap();
    assert!(
        to_value(source.publications().unwrap().borrow().active.clone()).unwrap()["message"]
            .as_str()
            .unwrap()
            .contains("expired")
    );
    let again = source
        .observe_performance(SYSTEM, Instant::now() + Duration::from_secs(2))
        .await
        .unwrap()
        .unwrap();
    assert!(again.has_fresh_mailbox_read());
    let clean_success = to_value(source.publications().unwrap().borrow().active.clone()).unwrap();
    let message = clean_success["message"].as_str().unwrap();
    assert!(!message.contains("expired"));
    assert_eq!(
        message
            .matches("Last confirmed thermal settings retained")
            .count(),
        1
    );
    let deadline = reader.next_refresh().unwrap();
    mapping.store(false, Ordering::Release);
    metadata_available.store(false, Ordering::Release);
    let retained = source.snapshot(SYSTEM, Instant::now()).await.unwrap();
    assert!(!retained.has_fresh_mailbox_read());
    let retained = to_value(retained).unwrap();
    assert!(!retained["confirmedState"].is_null());
    assert_eq!(retained["deviceVerified"], false);
    assert_eq!(retained["activePowerSource"], 3);
    assert_eq!(
        retained["potentialControllers"],
        json!(["current-controller"])
    );
    assert!(retained["message"].as_str().unwrap().contains("revoked"));
    assert_eq!(reader.next_refresh(), Some(deadline));
    mapping.store(true, Ordering::Release);
    assert!(
        source
            .observe_performance(SYSTEM, Instant::now() + Duration::from_secs(1))
            .await
            .unwrap()
            .is_none()
    );
    assert_eq!(held.submitted.load(Ordering::Acquire), 8);
    let active_and_inactive = source.publications().unwrap().borrow().clone();
    for status in [&active_and_inactive.active, &active_and_inactive.inactive] {
        let value = to_value(status).unwrap();
        assert!(value["message"].as_str().unwrap().contains("retained"));
        assert_eq!(value["deviceVerified"], false);
        // A retained confirmed state keeps the identified record (managed
        // retains its record beside the state it retains).
        assert!(!value["confirmedState"].is_null());
        assert_eq!(value["capabilities"], capability_record(true));
    }
    reader.expire_for_tests();
    source.refresh_publications().await.unwrap();
    let publication = source.publications().unwrap().borrow().clone();
    for status in [&publication.active, &publication.inactive] {
        let value = to_value(status).unwrap();
        assert!(value["confirmedState"].is_null());
        assert_eq!(value["capabilities"], capability_record(false));
        assert_eq!(value["writesBlocked"], true);
        assert!(value["message"].as_str().unwrap().contains("expired"));
    }
    source.drain().await.unwrap();
}

#[test]
fn only_the_exact_matched_model_has_fan_stop_evidence() {
    assert!(has_fan_stop_evidence(Some(&matched(
        HidMetadata::Candidate
    ))));
    assert!(has_fan_stop_evidence(Some(&matched(
        HidMetadata::Unavailable
    ))));
    for model in ["RZ09-0528", "RZ09-05810", "rz09-0581", ""] {
        let other = DeviceMetadata::MatchedHost {
            model: model.into(),
            hid: HidMetadata::Candidate,
        };
        assert!(!has_fan_stop_evidence(Some(&other)), "{model}");
    }
    for device in [DeviceMetadata::Unsupported, DeviceMetadata::Unavailable] {
        assert!(!has_fan_stop_evidence(Some(&device)));
    }
    assert!(!has_fan_stop_evidence(None));
}

/// Probes that count their reads: a pad that is absent and a journal that
/// names a pending restore.
#[cfg(feature = "service-hyperboost-v1")]
fn counted_probes() -> (
    super::super::thermal_mutation::Probes,
    Arc<std::sync::atomic::AtomicUsize>,
) {
    use super::super::thermal_mutation::{Pad, Probes};
    let reads = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let (scans, journals) = (reads.clone(), reads.clone());
    let probes = Probes::new(
        move |_| {
            scans.fetch_add(1, Ordering::AcqRel);
            Pad::Absent
        },
        Arc::new(move || {
            journals.fetch_add(1, Ordering::AcqRel);
            Ok(Some(br#"{"state": 2}"#.to_vec()))
        }),
    );
    (probes, reads)
}

/// Managed 16139-16145: the blocking controllers are the observation's own
/// (`Observation::hyperboost_blockers`), in the potential controllers'
/// spelling and order. A lighting service and the verified updater service
/// are potential controllers that do not block HyperBoost. Without probes
/// the pad and the journal stay unread.
#[cfg(feature = "service-hyperboost-v1")]
#[test]
fn hyperboost_blockers_are_the_controller_observation_s_own() {
    use openblade_platform_windows::controller_presence::{
        Process, Scan, Service, ServiceState, observe,
    };
    let running = |name| Service {
        name,
        display_name: name,
        state: ServiceState::Running,
    };
    let observation = observe(
        Scan::Complete(&[Process {
            name: "RazerAppEngine",
            pid: None,
            identity: None,
            verified_image: None,
        }]),
        Scan::Complete(&[running("Razer Chroma SDK Service"), running("RGS")]),
        None,
        Scan::Complete(&[]),
        0,
    );
    assert_eq!(
        observation.potential_controllers(),
        ["RGS", "Razer Chroma SDK Service", "RazerAppEngine"]
    );
    for device in [
        matched(HidMetadata::Candidate),
        DeviceMetadata::Unsupported,
        DeviceMetadata::Unavailable,
    ] {
        assert_eq!(
            observe_hyperboost(&device, &observation, None, &AtomicBool::new(false)),
            HyperBoostStatus {
                pad: 0,
                restore_pending: Some(false),
                blockers: Some(vec!["RazerAppEngine".to_owned()]),
            }
        );
    }
    // The potential list's spelling, once each, and only its members.
    let names = |names: &[&str]| names.iter().map(|n| (*n).to_owned()).collect::<Vec<_>>();
    assert_eq!(
        HyperBoostStatus::blocking(
            &names(&["RGS", "RazerAppEngine", "razerappengine"]),
            &names(&["RAZERAPPENGINE", "Unlisted"]),
        ),
        ["RazerAppEngine"]
    );
}

/// The managed codec refuses a status whose blockers are not ordinally
/// sorted, distinct members of its potential controllers
/// (`IpcMessageCodec.cs` 497-538). Every set of the controller names as
/// processes, with and without the services, keeps that, and the sets with
/// two or more blockers pin their order.
#[cfg(feature = "service-hyperboost-v1")]
#[test]
fn hyperboost_blockers_pass_the_managed_codec_in_ordinal_order() {
    use openblade_platform_windows::controller_presence::{
        Process, Scan, Service, ServiceState, observe,
    };
    const PROCESSES: [&str; 8] = [
        "RazerAppEngine",
        "razerappengine",
        "razerwdl",
        "RGS",
        "Razer Gaming Services Updater",
        "RazerSynapseService",
        "Razer Synapse Service",
        "Razer Chroma SDK Service",
    ];
    let services = [
        ("RazerSynapseService", "Razer Synapse Service"),
        ("rgs2", "RGS"),
        ("Chroma", "Razer Chroma SDK Service"),
    ]
    .map(|(name, display_name)| Service {
        name,
        display_name,
        state: ServiceState::Running,
    });
    let ordinal = |names: &[String]| {
        names
            .windows(2)
            .all(|pair| pair[0].encode_utf16().lt(pair[1].encode_utf16()))
    };
    let mut several = 0;
    for mask in 0u32..1 << PROCESSES.len() {
        let processes: Vec<_> = PROCESSES
            .iter()
            .enumerate()
            .filter(|(index, _)| mask & (1 << index) != 0)
            .map(|(_, name)| Process {
                name,
                pid: None,
                identity: None,
                verified_image: None,
            })
            .collect();
        for services in [&services[..], &[]] {
            let observation = observe(
                Scan::Complete(&processes),
                Scan::Complete(services),
                None,
                Scan::Incomplete(&[]),
                0,
            );
            let potential = observation.potential_controllers();
            let blockers = observe_hyperboost(
                &matched(HidMetadata::Candidate),
                &observation,
                None,
                &AtomicBool::new(false),
            )
            .blockers
            .unwrap();
            assert!(potential.len() <= 16 && ordinal(potential), "{potential:?}");
            assert!(ordinal(&blockers), "{blockers:?}");
            assert!(
                blockers.iter().all(|blocker| potential.contains(blocker)),
                "{blockers:?} of {potential:?}"
            );
            several += usize::from(blockers.len() > 1);
        }
    }
    assert!(several > 0);
}

/// The pad and the journal are read only on the host of the model whose
/// HyperBoost requests native serves. Another matched model, with probes
/// composed, reads neither: Unknown and nothing pending, whatever the probes
/// would have read. A cancelled observation reads neither on that model
/// either, and establishes no answer for the pending restore.
#[cfg(feature = "service-hyperboost-v1")]
#[test]
fn hyperboost_probes_are_read_only_on_the_served_model_and_before_cancellation() {
    let nothing = no_controllers();
    let live = AtomicBool::new(false);
    let observe = |model: &str, cancelled: &AtomicBool| {
        let (probes, reads) = counted_probes();
        let device = DeviceMetadata::MatchedHost {
            model: model.into(),
            hid: HidMetadata::Candidate,
        };
        let status = observe_hyperboost(&device, &nothing, Some(&probes), cancelled);
        (
            status.pad,
            status.restore_pending,
            reads.load(Ordering::Acquire),
        )
    };
    assert_eq!(observe("RZ09-0581", &live), (1, Some(true), 2));
    for other in ["RZ09-0528", "RZ09-0483", "rz09-0581", ""] {
        assert_eq!(observe(other, &live), (0, Some(false), 0), "{other}");
    }
    assert_eq!(observe("RZ09-0581", &AtomicBool::new(true)), (0, None, 0));
}

/// The members reach every caller's projection of the published status, and
/// a source that has observed nothing publishes what managed's absent
/// members decode to: Unknown, no pending restore and no classification.
#[cfg(feature = "service-hyperboost-v1")]
#[tokio::test]
async fn hyperboost_members_are_published_only_as_observed() {
    let mut source = ObservedStatus::new(|_| panic!("the cached publication is fresh"));
    source.publish(None).unwrap();
    for active in [true, false] {
        assert_eq!(hyperboost_members(&source, active), json!([0, false, null]));
    }
    let observed = HyperBoostStatus {
        pad: HyperBoostStatus::PAD_PRESENT,
        restore_pending: Some(true),
        blockers: Some(vec!["RazerAppEngine".to_owned()]),
    };
    assert!(source.publish(Some(published(&observed))).unwrap());
    for active in [true, false] {
        assert_eq!(
            hyperboost_members(&source, active),
            json!([2, true, ["RazerAppEngine"]])
        );
    }
    // A repeated observation is no new revision; a changed member is.
    assert!(!source.publish(Some(published(&observed))).unwrap());
    let cleared = HyperBoostStatus {
        restore_pending: Some(false),
        ..observed
    };
    assert!(source.publish(Some(published(&cleared))).unwrap());
    assert_eq!(
        hyperboost_members(&source, true),
        json!([2, false, ["RazerAppEngine"]])
    );
}
#[cfg(feature = "service-hyperboost-v1")]
fn hyperboost_members(source: &ObservedStatus, active: bool) -> Value {
    let publication = source.metadata.as_ref().unwrap();
    let status = to_value(if active {
        &publication.active
    } else {
        &publication.inactive
    })
    .unwrap();
    json!([
        status["padlessHyperBoostEligibility"],
        status["hyperBoostRestorePending"],
        status["hyperBoostBlockingControllers"],
    ])
}
#[cfg(feature = "service-hyperboost-v1")]
fn published(hyperboost: &HyperBoostStatus) -> Evidence {
    Evidence {
        controllers: vec!["RazerAppEngine".to_owned()],
        hyperboost: hyperboost.clone(),
        ..evidence(matched(HidMetadata::Candidate), Power::ExternalUnknown)
    }
}

/// Managed keeps its presentation when its journal cannot be read
/// (`ServiceControlRuntime.cs` 10133-10152). A journal read that fails, and
/// an observation that yields nothing, keep the pending restore the last
/// status reported, true or false, so neither hides a pending restore nor
/// invents one. With no earlier answer an unreadable journal is pending, as
/// managed's recovery treats it (4057-4069).
#[cfg(feature = "service-hyperboost-v1")]
#[tokio::test]
async fn an_unreadable_journal_keeps_the_last_pending_restore() {
    let reading = |restore_pending| HyperBoostStatus {
        pad: HyperBoostStatus::PAD_ABSENT,
        restore_pending,
        blockers: Some(vec![]),
    };
    let pending = |source: &ObservedStatus| hyperboost_members(source, true)[1].clone();
    for established in [true, false] {
        let mut source = ObservedStatus::new(|_| panic!("the cached publication is fresh"));
        source
            .publish(Some(published(&reading(Some(established)))))
            .unwrap();
        assert_eq!(pending(&source), established);
        // The journal could not be read: no new revision, the answer stands.
        assert!(!source.publish(Some(published(&reading(None)))).unwrap());
        assert_eq!(pending(&source), established);
        // Nothing observed: the other members are unknown, this one stands.
        source.publish(None).unwrap();
        assert_eq!(
            hyperboost_members(&source, true),
            json!([0, established, null])
        );
        source
            .publish(Some(published(&reading(Some(!established)))))
            .unwrap();
        assert_eq!(pending(&source), !established);
    }
    let mut source = ObservedStatus::new(|_| panic!("the cached publication is fresh"));
    source.publish(Some(published(&reading(None)))).unwrap();
    assert_eq!(pending(&source), true);
    // A first refresh that yields nothing publishes no pending restore and
    // establishes none: the unreadable journal after it is still the first
    // answer, so it is pending.
    let mut source = ObservedStatus::new(|_| panic!("the cached publication is fresh"));
    source.publish(None).unwrap();
    assert_eq!(pending(&source), false);
    source.publish(Some(published(&reading(None)))).unwrap();
    assert_eq!(pending(&source), true);
}

/// A drain tells a running observation to stop and waits for it only until
/// its deadline. An OS call that does not return is reported at the
/// deadline, not waited for; one that honours the cancellation retires the
/// observer at once.
#[tokio::test]
async fn drain_waits_for_a_stalled_observer_only_until_its_deadline() {
    for honours_cancel in [true, false] {
        let release = Arc::new(AtomicBool::new(false));
        let (started, running) = mpsc::channel();
        let held = release.clone();
        let mut source = ObservedStatus::new(move |cancel| {
            started.send(()).unwrap();
            // The cap ends the thread if the test fails before it releases.
            let began = std::time::Instant::now();
            while !held.load(Ordering::Acquire)
                && !(honours_cancel && cancel.load(Ordering::Acquire))
                && began.elapsed() < Duration::from_secs(3)
            {
                std::thread::sleep(Duration::from_millis(1));
            }
            evidence(matched(HidMetadata::Candidate), Power::Battery)
        });
        // The refresh gives up at its own budget and keeps its worker.
        let _ = tokio::time::timeout(
            Duration::from_millis(50),
            source.snapshot(SYSTEM, Instant::now()),
        )
        .await;
        running.recv().unwrap();
        let bound = Duration::from_millis(300);
        let began = std::time::Instant::now();
        let drained = source.drain_by(Instant::now() + bound).await;
        let took = began.elapsed();
        if honours_cancel {
            assert!(drained.is_ok(), "{drained:?}");
            assert!(took < bound, "{took:?}");
        } else {
            let error = drained.unwrap_err();
            assert_eq!(error.kind(), io::ErrorKind::TimedOut);
            assert_eq!(error.to_string(), OBSERVER_RUNNING);
            assert!(took >= bound && took < bound * 4, "{took:?}");
        }
        release.store(true, Ordering::Release);
    }
}

/// This composition's capability record with or without a confirmed state.
fn capability_record(identified: bool) -> Value {
    Value::from(crate::service_status::capability_record::compiled(
        identified,
    ))
}
