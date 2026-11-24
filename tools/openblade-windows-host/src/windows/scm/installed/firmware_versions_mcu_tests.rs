//! The MCU version through the real ReadSource, Worker and Reader with the
//! fake controller behind the Port: when the read job sends the 00/87 query,
//! when the record carries its version, and the managed read budget.
use super::*;

const VERSION: (u8, [u8; 4]) = (2, [1, 5, 0, 0]);

fn answer(rig: &Rig, reply: Option<(u8, [u8; 4])>) {
    *rig.io.mcu.lock().unwrap() = reply.map(|(status, payload)| (status, payload.to_vec()));
}

fn with_mcu(version: &str) -> Json {
    let mut record = record_json();
    record["mcuVersion"] = json!(version);
    record
}

/// A session or power notice, such as a lock, an unlock or an AC change: it
/// advances the lifecycle epoch and leaves the low-power gate.
fn fence(rig: &Rig) {
    rig.custody.lifecycle.fetch_add(1, Ordering::AcqRel);
}

/// A resume: the low-power gate leaves a suspend for a new active
/// generation, and the notices that carried it advance the lifecycle epoch.
fn resume(rig: &Rig) {
    rig.custody.resume.fetch_add(2, Ordering::AcqRel);
    fence(rig);
}

/// Waits for the job to send the query, and fails rather than hangs when it
/// sends none.
async fn until_queried(rig: &Rig) {
    tokio::time::timeout(Duration::from_secs(5), async {
        while rig.io.mcu_queries() == 0 {
            tokio::time::sleep(Duration::from_millis(1)).await;
        }
    })
    .await
    .expect("the job sends the MCU version query");
}

/// A version read after the record was published arrives in the reply of the
/// observation that read it and in a new publication of both projections, as
/// managed publishes the changed record; it is then never read again.
#[tokio::test]
async fn a_later_version_is_published_by_the_read_that_confirms_it() {
    let rig = Rig::new(Some(Arc::new(Fake::healthy())));
    for published in rig.await_firmware() {
        assert_eq!(published["firmware"], record_json());
    }
    let before = rig.publisher.borrow().active.revision();
    answer(&rig, Some(VERSION));
    let observed = rig.observe(ALL).await;
    assert_eq!(observed["firmware"], with_mcu("1.5.0.0"));
    // Every other reading of the job stands.
    assert_eq!(observed["liveReadings"]["fan1Rpm"], 3200);
    assert_eq!(observed["liveReadings"]["fan2Rpm"], 3000);
    for published in rig.published() {
        assert_eq!(published["firmware"], with_mcu("1.5.0.0"));
    }
    assert!(rig.publisher.borrow().active.revision() > before);
    for mask in [0, ALL] {
        assert_eq!(rig.observe(mask).await["firmware"], with_mcu("1.5.0.0"));
    }
    assert_eq!(rig.io.mcu_queries(), 1);
}

/// A version read before the capture finished waits for the record: no
/// status or reply has a firmware member until then, and the record carries
/// the version when it is published, without another query.
#[tokio::test]
async fn a_version_read_before_the_record_rides_its_first_publication() {
    let (fake, hold) = Fake::healthy().held(Stage::Video);
    let rig = Rig::new(Some(Arc::new(fake)));
    answer(&rig, Some(VERSION));
    rig.metadata();
    hold.entered.recv_timeout(Duration::from_secs(5)).unwrap();
    let observed = rig.observe(0).await;
    assert!(observed.get("firmware").is_none());
    for published in rig.published() {
        assert!(published.get("firmware").is_none());
    }
    assert_eq!(rig.io.mcu_queries(), 1);
    hold.release.send(()).unwrap();
    for published in rig.await_firmware() {
        assert_eq!(published["firmware"], with_mcu("1.5.0.0"));
    }
    assert_eq!(rig.io.mcu_queries(), 1);
}

/// A Service without a firmware source has no record, so, as managed reads
/// no MCU version without one, no job sends the query and no status changes.
#[tokio::test]
async fn a_source_without_a_capture_sends_no_query_and_publishes_no_member() {
    let rig = Rig::new(None);
    answer(&rig, Some(VERSION));
    rig.metadata();
    for _ in 0..4 {
        let observed = rig.observe(ALL).await;
        assert!(observed.get("firmware").is_none());
        assert_eq!(observed["liveReadings"]["fan1Rpm"], 3200);
    }
    for published in rig.published() {
        assert!(published.get("firmware").is_none());
    }
    assert_eq!(rig.io.mcu_queries(), 0);
}

/// Every reply that confirms no version (none, a timeout, a failed status, a
/// short payload, another transaction or command) leaves `Unavailable`, and
/// the job's other readings stand.
#[tokio::test]
async fn an_unconfirmed_reply_leaves_unavailable_and_costs_no_other_reading() {
    for (name, reply, edit, stall) in [
        ("no reply", None, None, false),
        ("timeout", Some(VERSION), None, true),
        ("failed status", Some((3, VERSION.1)), None, false),
        (
            "transaction mismatch",
            Some(VERSION),
            Some((2, 0xfe)),
            false,
        ),
        ("device-mode reply", Some(VERSION), Some((8, 0x84)), false),
    ] {
        let rig = Rig::new(Some(Arc::new(Fake::healthy())));
        rig.await_firmware();
        answer(&rig, reply);
        *rig.io.mcu_edit.lock().unwrap() = edit;
        rig.io.stall_mcu.store(stall, Ordering::Release);
        let observed = rig.observe(ALL).await;
        assert_eq!(observed["firmware"], record_json(), "{name}");
        assert_eq!(observed["liveReadings"]["fan1Rpm"], 3200, "{name}");
        assert_eq!(observed["liveReadings"]["fan2Rpm"], 3000, "{name}");
        assert_eq!(rig.io.mcu_queries(), 1, "{name}");
    }
    // A malformed payload cannot be scripted as four bytes.
    let rig = Rig::new(Some(Arc::new(Fake::healthy())));
    rig.await_firmware();
    *rig.io.mcu.lock().unwrap() = Some((2, vec![1, 5, 0]));
    assert_eq!(rig.observe(0).await["firmware"], record_json());
    assert_eq!(rig.io.mcu_queries(), 1);
}

/// A device change while the query is outstanding discards the job's
/// reading: its version is never kept. The next query, under the new
/// admission and within the same budget, reads it.
#[tokio::test]
async fn a_version_read_across_an_admission_change_is_discarded() {
    let rig = Rig::new(Some(Arc::new(Fake::healthy())));
    rig.await_firmware();
    answer(&rig, Some(VERSION));
    let (release, held) = channel();
    *rig.io.hold_mcu.lock().unwrap() = Some(held);
    let control = async {
        until_queried(&rig).await;
        rig.custody.generation.store(8, Ordering::Release);
        release.send(()).unwrap();
    };
    let (observed, ()) = tokio::join!(rig.snapshot(0), control);
    assert!(observed.is_none());
    for published in rig.published() {
        assert_eq!(published["firmware"], record_json());
    }
    assert_eq!(rig.observe(0).await["firmware"], with_mcu("1.5.0.0"));
    assert_eq!(rig.io.mcu_queries(), 2);
}

/// The source keeps a version only from a reading it accepts. Here the
/// worker confirms the version under the admission it was started with while
/// the device has changed: the window between the worker's last check and the
/// source's acceptance. The reading is discarded with its version, and the
/// next query, under the new admission, reads it.
#[tokio::test]
async fn a_version_the_source_does_not_accept_is_not_kept() {
    let rig = Rig::new(Some(Arc::new(Fake::healthy())));
    rig.await_firmware();
    answer(&rig, Some(VERSION));
    *rig.worker_admission.lock().unwrap() = rig.custody.authorize();
    rig.custody.generation.store(8, Ordering::Release);
    assert!(rig.snapshot(0).await.is_none());
    assert_eq!(rig.io.mcu_queries(), 1);
    *rig.worker_admission.lock().unwrap() = None;
    for published in rig.published() {
        assert_eq!(published["firmware"], record_json());
    }
    assert_eq!(rig.observe(0).await["firmware"], with_mcu("1.5.0.0"));
    assert_eq!(rig.io.mcu_queries(), 2);
}

/// Session and power notices fence every read admission but are no resume,
/// so they leave the budget spent, as managed restores its attempts only at
/// resume and device loss. Each query that gets no reply keeps its ID
/// quarantined, so a fence that restored the budget would drain the pool.
#[tokio::test]
async fn only_a_resume_restores_the_budget() {
    let rig = Rig::new(Some(Arc::new(Fake::healthy())));
    rig.await_firmware();
    answer(&rig, None);
    for observation in 0..6 {
        fence(&rig);
        assert!(rig.snapshot(0).await.is_some(), "{observation}");
    }
    assert_eq!(rig.io.mcu_queries(), 3);
    resume(&rig);
    for observation in 0..4 {
        fence(&rig);
        assert!(rig.snapshot(0).await.is_some(), "{observation}");
    }
    assert_eq!(rig.io.mcu_queries(), 6);
    answer(&rig, Some(VERSION));
    resume(&rig);
    assert_eq!(rig.observe(0).await["firmware"], with_mcu("1.5.0.0"));
    assert_eq!(rig.io.mcu_queries(), 7);
}

/// Stop while the query is outstanding completes once the held SET returns,
/// publishes no version and keeps none.
#[tokio::test]
async fn stop_with_an_outstanding_query_keeps_no_version() {
    let rig = Rig::new(Some(Arc::new(Fake::healthy())));
    rig.await_firmware();
    answer(&rig, Some(VERSION));
    let (release, held) = channel();
    *rig.io.hold_mcu.lock().unwrap() = Some(held);
    let control = async {
        until_queried(&rig).await;
        // Stop is polled first: it fences the worker before the SET returns.
        let release = async { release.send(()).unwrap() };
        let (stopped, ()) = tokio::join!(rig.source.stop(), release);
        stopped
    };
    let (observed, stopped) = tokio::time::timeout(Duration::from_secs(5), async {
        tokio::join!(rig.snapshot(0), control)
    })
    .await
    .expect("stop completes");
    stopped.unwrap();
    assert!(observed.is_none());
    for published in rig.published() {
        assert_eq!(published["firmware"], record_json());
    }
    assert_eq!(
        rig.source
            .firmware
            .poll(Instant::now())
            .unwrap()
            .mcu_version,
        UNAVAILABLE
    );
    assert_eq!(rig.io.mcu_queries(), 1);
    // The query counts from its SET, not from a reply that Stop discarded.
    assert_eq!(rig.source.firmware.mcu_sends_for_tests(), 1);
}

fn vectors() -> Json {
    serde_json::from_str(include_str!(
        "../../../../tests/data/mcu-version-vectors.json"
    ))
    .unwrap()
}

/// The managed budget rows (`NativeFirmwareVersionMcuVectors`): each Version
/// step is an observation whose query is answered 1.5.0.0, each Failed step
/// one whose query gets no reply, and Resume a resume, which advances both
/// the low-power gate and the lifecycle epoch. The published version and the
/// number of queries sent match managed's.
#[tokio::test]
async fn the_read_budget_matches_the_managed_vectors() {
    let vectors = vectors();
    let rows = vectors["budget"].as_array().unwrap();
    assert!(rows.len() >= 10);
    for row in rows {
        let name = row["name"].as_str().unwrap();
        let rig = Rig::new(Some(Arc::new(Fake::healthy())));
        for step in row["steps"].as_array().unwrap() {
            match step.as_str().unwrap() {
                "Version" => answer(&rig, Some(VERSION)),
                "Failed" => answer(&rig, None),
                "Resume" => {
                    resume(&rig);
                    continue;
                }
                other => panic!("unknown step {other}"),
            }
            assert!(rig.snapshot(0).await.is_some(), "{name}");
        }
        let published = rig.await_firmware();
        let expect = &row["expect"];
        assert_eq!(
            published[0]["firmware"]["mcuVersion"], expect["mcuVersion"],
            "{name}"
        );
        assert_eq!(
            rig.io.mcu_queries() as u64,
            expect["reads"].as_u64().unwrap(),
            "{name}"
        );
    }
}
