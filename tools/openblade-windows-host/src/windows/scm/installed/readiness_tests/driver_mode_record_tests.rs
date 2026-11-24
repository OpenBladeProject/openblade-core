//! The keyboard Driver-mode owner over its durable record
//! (`owner\keyboard-driver-mode.bin`, the rig's `RecordModel`): the claim is
//! written before the setter can be sent, settled after the job, closed by
//! any failed write, and a later process over the same files resolves an
//! intent read-only.
use super::super::driver_mode_record_model::{Fault, RECORD, STAGE};
use super::*;
use openblade_domain::driver_mode_history::{Record, Settled, State};

/// The record the files hold, `None` while there is none.
fn on_disk(rig: &Rig) -> Option<Record> {
    rig.files
        .read(RECORD)
        .unwrap()
        .map(|bytes| Record::parse(&bytes).unwrap())
}
/// The held record's state and IDs.
fn held(rig: &Rig) -> Option<(State, [u8; 4])> {
    on_disk(rig).map(|record| (record.state(), record.ids()))
}
fn closed(rig: &Rig) -> bool {
    rig.runtime.driver_mode_closed_for_tests()
}
/// The keyboard commands sent from the `from`th exchange on.
fn sent_from(rig: &Rig, from: usize) -> Vec<u8> {
    keyboard(rig)[from..]
        .iter()
        .map(|(command, ..)| *command)
        .collect()
}
/// A later Service over `earlier`'s files: a fresh port and pool, a new
/// owner generation, its keyboard in `mode`.
async fn restarted(earlier: &Rig, mode: u8) -> Rig {
    wired(Rig::restarted(earlier.files.clone()), mode).await.0
}

/// Managed claims before the setter can be submitted; here the claim is the
/// durable `AcquireIntent` naming the job's baseline, setter, readback and
/// spare IDs, already on disk when the setter is sent. The confirmed
/// readback settles it as `Owned`, keeping those IDs.
#[tokio::test]
async fn the_claim_is_durable_before_the_setter_is_sent() {
    let (rig, _) = owner_rig(NORMAL).await;
    let files = rig.files.clone();
    let at_setter = Arc::new(StdMutex::new(None));
    let seen = at_setter.clone();
    rig.port.state.lock().unwrap().device_mode_on_set = Some(Arc::new(move |id, mode| {
        let record = files
            .read(RECORD)
            .unwrap()
            .map(|b| Record::parse(&b).unwrap());
        *seen.lock().unwrap() = Some((id, mode, record));
    }));
    tick(&rig).await;
    let sent = keyboard(&rig);
    let (setter, target, record) = at_setter.lock().unwrap().take().unwrap();
    assert_eq!((setter, target), (sent[1].2, DRIVER));
    let record = record.expect("the claim is on disk before the setter");
    assert_eq!(record.state(), State::AcquireIntent);
    assert_eq!(record.prior(), Settled::Clear);
    let ids = record.ids();
    assert_eq!([ids[0], ids[1], ids[2]], [sent[0].2, sent[1].2, sent[2].2]);
    assert_eq!(held(&rig), Some((State::Owned, ids)));
    assert_eq!(owner(&rig), (true, false, false));
    // The acquire holds the ordinary admission, never a cleanup scope.
    assert!(
        rig.custody
            .driver_mode
            .writes()
            .iter()
            .all(|(scope, _)| scope.is_none())
    );
    rig.stop().await;
}

/// A keyboard already in Driver mode is not claimed, so nothing is written.
#[tokio::test]
async fn a_read_of_driver_writes_no_record() {
    let (rig, _) = owner_rig(DRIVER).await;
    tick(&rig).await;
    assert_eq!(on_disk(&rig), None);
    assert!(rig.custody.driver_mode.writes().is_empty());
    rig.stop().await;
}

/// A setter refused before submission reverts the claim to its prior,
/// keeping the IDs the claim named.
#[tokio::test]
async fn a_claim_known_not_submitted_reverts_to_its_prior() {
    let (rig, _) = owner_rig(NORMAL).await;
    refuse_after_first_query(&rig, 5);
    tick(&rig).await;
    assert_eq!(sets(&rig), 0);
    let writes = rig.custody.driver_mode.writes();
    assert_eq!(writes.len(), 2);
    let claim = Record::parse(&writes[0].1).unwrap();
    assert_eq!(held(&rig), Some((State::Clear, claim.ids())));
    rig.custody.fresh_refuse.store(false, Ordering::Release);
}

/// A setter that may have been submitted leaves the intent on disk, which
/// keeps the owner Indeterminate; a later read of Driver settles it.
#[tokio::test]
async fn a_possibly_submitted_setter_keeps_the_intent_until_a_read() {
    let (rig, _) = fenced_readback_rig().await;
    let ids = held(&rig).unwrap().1;
    assert_eq!(held(&rig), Some((State::AcquireIntent, ids)));
    later(&rig).await;
    assert_eq!(held(&rig), Some((State::Owned, ids)));
    assert_eq!(owner(&rig), (true, false, false));
    rig.stop().await;
}

/// A claim that cannot be written sends no setter. The record is closed
/// for the process: no recovery is spent on it, no later step runs a job,
/// and nothing is ever written again, whatever the failure left on disk.
#[tokio::test]
async fn a_claim_that_cannot_be_written_sends_no_setter_and_closes_the_record() {
    for fault in [Fault::Refused, Fault::Written] {
        let (rig, _) = owner_rig(NORMAL).await;
        rig.custody.driver_mode.fail(1, fault);
        let start = captures(&rig);
        tick(&rig).await;
        assert_eq!(sent_from(&rig, 0), [0x84], "{fault:?}");
        assert_eq!(sets(&rig), 0, "{fault:?}");
        assert!(closed(&rig), "{fault:?}");
        assert_eq!(captures(&rig), start + 1, "{fault:?}: no recovery");
        assert_eq!(published(&rig).await, None, "{fault:?}");
        let left = held(&rig);
        match fault {
            Fault::Written => assert_eq!(left.unwrap().0, State::AcquireIntent),
            _ => assert_eq!(left, None),
        }
        for _ in 0..3 {
            later(&rig).await;
        }
        assert_eq!(keyboard(&rig).len(), 1, "{fault:?}");
        assert_eq!(captures(&rig), start + 1, "{fault:?}");
        assert_eq!(rig.custody.driver_mode.writes().len(), 1, "{fault:?}");
        // The status still projects confirmed reads.
        record(&rig, DeviceMode::Driver);
        assert_eq!(published(&rig).await, Some(true), "{fault:?}");
    }
}

/// A settle that fails after a confirmed acquire closes the record, which
/// still holds the intent: memory never moves past it, so a later Normal
/// is neither resolved nor acquired again in this process.
#[tokio::test]
async fn a_failed_settle_closes_the_record_at_its_intent() {
    for fault in [Fault::Refused, Fault::Fatal] {
        let (rig, _) = owner_rig(NORMAL).await;
        rig.custody.driver_mode.fail(2, fault);
        let ticked = try_tick(&rig).await;
        assert_eq!(ticked.is_err(), fault == Fault::Fatal, "{fault:?}");
        assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER), "{fault:?}");
        assert_eq!(held(&rig).unwrap().0, State::AcquireIntent, "{fault:?}");
        assert!(closed(&rig), "{fault:?}");
        if fault == Fault::Fatal {
            // Fatal evidence is retained and fails every later step.
            rig.runtime.driver_mode_due_for_tests();
            assert!(try_tick(&rig).await.is_err());
            continue;
        }
        assert_eq!(published(&rig).await, Some(true));
        rig.port.state.lock().unwrap().device_mode = NORMAL;
        let sent = keyboard(&rig).len();
        later(&rig).await;
        later(&rig).await;
        assert_eq!(keyboard(&rig).len(), sent);
        assert_eq!(rig.custody.driver_mode.writes().len(), 2);
    }
}

/// A Service that crashed at each point of an acquire leaves the record
/// that point implies, and a later Service over the same files starts from
/// it: an intent is resolved by one 00/84 read and no 00/04 in its first
/// step; a settled record needs no resolution.
#[tokio::test]
async fn a_restart_after_each_crash_point_finds_the_record_it_implies() {
    // (cut, the record the files hold, the restarted step's commands)
    for cut in [
        "claimed",
        "submitted",
        "acknowledged",
        "confirmed",
        "settled",
    ] {
        let (rig, _) = owner_rig(NORMAL).await;
        match cut {
            // A refused setter checkpoint, then the revert never written.
            "claimed" => {
                refuse_after_first_query(&rig, 5);
                rig.custody.driver_mode.fail(2, Fault::Refused);
            }
            "submitted" => rig.port.state.lock().unwrap().device_mode_fail_set = true,
            "acknowledged" => {
                let mut port = rig.port.state.lock().unwrap();
                port.device_mode_timeout = Some(port.device_mode_queries + 2);
            }
            // The settle never written.
            "confirmed" => rig.custody.driver_mode.fail(2, Fault::Refused),
            // The settle written, then the process stopped.
            _ => rig.custody.driver_mode.fail(2, Fault::Written),
        }
        tick(&rig).await;
        let firmware = mode(&rig);
        let (state, ids) = held(&rig).unwrap();
        let expected = if cut == "settled" {
            State::Owned
        } else {
            State::AcquireIntent
        };
        assert_eq!(state, expected, "{cut}");
        let later_rig = restarted(&rig, firmware).await;
        assert_eq!(held(&later_rig), Some((state, ids)), "{cut}");
        tick(&later_rig).await;
        assert_eq!(sent_from(&later_rig, 0), [0x84], "{cut}");
        let resolved = if firmware == DRIVER {
            State::Owned
        } else {
            State::Clear
        };
        assert_eq!(held(&later_rig), Some((resolved, ids)), "{cut}");
        assert_eq!(later_rig.runtime.driver_mode_claims_for_tests(), 0, "{cut}");
        later_rig
            .custody
            .fresh_refuse
            .store(false, Ordering::Release);
        later_rig.stop().await;
    }
}

/// An `AcquireIntent` a crashed Service left is resolved read-only by the
/// next one, as an Indeterminate acquire: Driver records `Owned`, an
/// unreadable read leaves the intent, and Normal records `Clear` and
/// acquires only at the next tick, afresh on new IDs.
#[tokio::test]
async fn a_restart_over_an_acquire_intent_resolves_it_read_only() {
    for (case, firmware) in [("driver", DRIVER), ("unread", DRIVER), ("normal", NORMAL)] {
        let (rig, _) = owner_rig(NORMAL).await;
        rig.port.state.lock().unwrap().device_mode_fail_set = true;
        tick(&rig).await;
        let (_, ids) = held(&rig).unwrap();
        let later_rig = restarted(&rig, firmware).await;
        if case == "unread" {
            later_rig.port.state.lock().unwrap().device_mode_mute = true;
        }
        tick(&later_rig).await;
        assert_eq!(sent_from(&later_rig, 0), [0x84], "{case}");
        let (state, at) = held(&later_rig).unwrap();
        assert_eq!(at, ids, "{case}");
        match case {
            "driver" => {
                assert_eq!(state, State::Owned);
                assert_eq!(owner(&later_rig), (true, false, false));
                assert_eq!(published(&later_rig).await, Some(true));
            }
            "unread" => {
                assert_eq!(state, State::AcquireIntent);
                assert_eq!(owner(&later_rig), (true, true, false));
                assert_eq!(published(&later_rig).await, None);
            }
            _ => {
                assert_eq!(state, State::Clear);
                assert_eq!(owner(&later_rig), (false, false, false));
                tick(&later_rig).await;
                assert_eq!(sent_from(&later_rig, 1), [0x84, 0x04, 0x84]);
                let (state, fresh) = held(&later_rig).unwrap();
                assert_eq!(state, State::Owned);
                assert!(fresh.iter().all(|id| !ids.contains(id)), "{fresh:?}");
                assert_eq!(later_rig.runtime.driver_mode_claims_for_tests(), 1);
            }
        }
        later_rig.port.state.lock().unwrap().device_mode_mute = false;
        later_rig.stop().await;
    }
}

/// An `Owned` record needs no resolution: Driver is maintained with no
/// write, and a release that reads Normal reports a foreign change and
/// records `Clear`. A `Clear` record acquires as a first start does.
#[tokio::test]
async fn a_restart_over_a_settled_record_starts_from_it() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    let (_, ids) = held(&rig).unwrap();
    let owned = restarted(&rig, DRIVER).await;
    tick(&owned).await;
    assert_eq!((sent_from(&owned, 0), sets(&owned)), (vec![0x84], 0));
    assert_eq!(owner(&owned), (true, false, false));
    owned.port.state.lock().unwrap().device_mode = NORMAL;
    assert_eq!(release(&owned).await, Some(Release::ForeignChange));
    assert_eq!(held(&owned), Some((State::Clear, ids)));
    let clear = restarted(&owned, NORMAL).await;
    tick(&clear).await;
    assert_eq!(sent_from(&clear, 0), [0x84, 0x04, 0x84]);
    assert_eq!(held(&clear).unwrap().0, State::Owned);
    clear.stop().await;
}

/// A record the custody refuses, corrupt, of another version or beside a
/// leftover stage, closes the keyboard owner for the process: no job runs,
/// the file is kept byte for byte, and the status still projects confirmed
/// reads. Startup does not fail.
#[tokio::test]
async fn a_refused_record_closes_the_owner_and_is_kept() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    let owned = rig.files.read(RECORD).unwrap().unwrap();
    for case in ["corrupt", "next version", "stage"] {
        let files = MemoryFiles::default();
        files.profiles(1);
        let bytes = match case {
            "corrupt" => b"not a record".to_vec(),
            "next version" => String::from_utf8(owned.clone())
                .unwrap()
                .replace("NativeDriverMode.v1", "NativeDriverMode.v2")
                .into_bytes(),
            _ => owned.clone(),
        };
        {
            let mut held = files.bytes.lock().unwrap();
            held.insert(RECORD.into(), bytes.clone());
            if case == "stage" {
                held.insert(STAGE.into(), b"leftover".to_vec());
            }
        }
        let (later_rig, _) = wired(Rig::restarted(files.clone()), NORMAL).await;
        for _ in 0..3 {
            later(&later_rig).await;
        }
        assert!(keyboard(&later_rig).is_empty(), "{case}");
        assert!(closed(&later_rig), "{case}");
        record(&later_rig, DeviceMode::Driver);
        assert_eq!(published(&later_rig).await, Some(true), "{case}");
        assert!(later_rig.custody.driver_mode.writes().is_empty(), "{case}");
        assert_eq!(files.read(RECORD).unwrap(), Some(bytes), "{case}");
    }
}

/// A claim charges its record write in the job's pace at the longest of a
/// floor and the last measured replace: after a replace slower than the
/// job's whole budget, the next claim is known not to fit, so its job reads
/// the baseline and writes nothing, neither record nor setter.
#[tokio::test]
async fn a_slow_record_write_is_charged_before_the_next_claim() {
    let (rig, _) = owner_rig(NORMAL).await;
    // The settle after the first acquire takes longer than a whole job.
    rig.custody
        .driver_mode
        .delay_at(2, Duration::from_millis(2100));
    tick(&rig).await;
    assert_eq!(sets(&rig), 1);
    assert_eq!(held(&rig).unwrap().0, State::Owned);
    rig.port.state.lock().unwrap().device_mode = NORMAL;
    later(&rig).await;
    assert_eq!(sent_from(&rig, 3), [0x84]);
    assert_eq!(sets(&rig), 1);
    assert_eq!(rig.custody.driver_mode.writes().len(), 2);
    assert_eq!(held(&rig).unwrap().0, State::Owned);
    assert_eq!(rig.runtime.driver_mode_claims_for_tests(), 1);
    rig.stop().await;
}
