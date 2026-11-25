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
    for field in [
        "capabilities",
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
        assert!(value["capabilities"].is_null());
    }
    reader.expire_for_tests();
    source.refresh_publications().await.unwrap();
    let publication = source.publications().unwrap().borrow().clone();
    for status in [&publication.active, &publication.inactive] {
        let value = to_value(status).unwrap();
        assert!(value["confirmedState"].is_null());
        assert!(value["capabilities"].is_null());
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
