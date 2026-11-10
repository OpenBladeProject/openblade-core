use std::sync::{Mutex, mpsc};

use serde_json::{Value, json, to_value};
use tokio::sync::oneshot;

use super::*;
use crate::installed::readiness::Logon;

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
    let supported = source.snapshot(SYSTEM).await.unwrap();
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

    let unsupported = to_value(source.snapshot(SYSTEM).await.unwrap()).unwrap();
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
    assert!(source.snapshot(SYSTEM).await.is_err());
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
    let initial = to_value(source.snapshot(SYSTEM).await.unwrap()).unwrap();
    assert_eq!(initial["potentialControllers"], json!(["RazerAppEngine"]));
    let partial = to_value(source.snapshot(SYSTEM).await.unwrap()).unwrap();
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

    let inactive = source.snapshot(INACTIVE).await.unwrap();
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

    let ambiguous = to_value(source.snapshot(SYSTEM).await.unwrap()).unwrap();
    assert_eq!(ambiguous["revision"], 2);
    assert!(ambiguous["message"].as_str().unwrap().contains("ambiguous"));
    let unavailable = to_value(source.snapshot(SYSTEM).await.unwrap()).unwrap();
    assert_eq!(unavailable["revision"], 3);
    assert_eq!(
        unavailable["message"],
        "Device identity is unavailable. Control ownership is unverified."
    );
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
        .project(
            0,
            Some(Evidence {
                controllers: vec!["RazerAppEngine".to_owned()],
                ..evidence(matched(HidMetadata::Candidate), Power::Battery)
            }),
        )
        .unwrap();
    let (status, ()) = tokio::join!(source.snapshot(SYSTEM), async {
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
    assert_eq!(
        to_value(source.snapshot(SYSTEM).await.unwrap()).unwrap(),
        stale
    );

    release.send(()).unwrap();
    while !source.refresh.as_ref().unwrap().worker.is_finished() {
        tokio::task::yield_now().await;
    }
    let recovered = to_value(source.snapshot(SYSTEM).await.unwrap()).unwrap();
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
        status = source.snapshot(SYSTEM) => panic!("snapshot completed before cancellation: {}", status.is_ok()),
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
    assert!(source.snapshot(SYSTEM).await.is_err());
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
    assert!(source.snapshot(SYSTEM).await.is_err());
    source.drain().await.unwrap();
}
