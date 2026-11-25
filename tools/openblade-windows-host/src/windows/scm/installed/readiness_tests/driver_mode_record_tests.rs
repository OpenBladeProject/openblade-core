//! The keyboard Driver-mode owner over its durable record
//! (`owner\keyboard-driver-mode.bin`, the rig's `RecordModel`): the claim is
//! written before the setter can be sent, an acquire's under the ordinary
//! admission and a release's under its boundary's cleanup scope, settled
//! after the job, closed by any failed write, and a later process over the
//! same files resolves an intent read-only.
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

/// A record write slower than a whole job costs only the job it slowed: the
/// next claim is charged the floor, not that write, so after a settle that
/// took longer than the acquire's budget a keyboard found Normal is claimed
/// and written again at the next step.
#[tokio::test]
async fn a_slow_record_write_never_refuses_a_later_claim() {
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
    assert_eq!(sent_from(&rig, 3), [0x84, 0x04, 0x84]);
    assert_eq!((sets(&rig), mode(&rig)), (2, DRIVER));
    assert_eq!(rig.custody.driver_mode.writes().len(), 4);
    assert_eq!(held(&rig).unwrap().0, State::Owned);
    assert_eq!(rig.runtime.driver_mode_claims_for_tests(), 2);
    rig.stop().await;
}

/// The record writes since the `from`th, by the scope they held.
fn scopes_from(rig: &Rig, from: usize) -> Vec<Option<CleanupScope>> {
    rig.custody.driver_mode.writes()[from..]
        .iter()
        .map(|(scope, _)| *scope)
        .collect()
}

/// A release claims before its setter can be sent, as the acquire does: a
/// `ReleaseIntent` over the owned record, naming the release job's IDs, is
/// on disk when 00/04:0000 is sent, and the confirmed readback settles it
/// `Clear`. Each write holds the boundary's own cleanup scope: Owned Stop at
/// shutdown, the suspend generation in low power.
#[tokio::test]
async fn a_release_claims_under_its_boundary_scope_before_its_setter() {
    for boundary in ["shutdown", "low power"] {
        let (rig, _) = owner_rig(NORMAL).await;
        tick(&rig).await;
        let acquired = rig.custody.driver_mode.writes().len();
        let sent = keyboard(&rig).len();
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
        let scope = if boundary == "shutdown" {
            assert_eq!(release(&rig).await, Some(Release::Restored));
            CleanupScope::OwnedStop
        } else {
            suspend(&rig);
            tick(&rig).await;
            assert_eq!(prepared(&rig), Some(Release::Restored));
            let generation = rig.custody.lifecycle.low_power().unwrap().generation;
            CleanupScope::LowPower { generation }
        };
        assert_eq!(mode(&rig), NORMAL, "{boundary}");
        let job = &keyboard(&rig)[sent..];
        let (setter, target, record) = at_setter.lock().unwrap().take().unwrap();
        assert_eq!((setter, target), (job[1].2, NORMAL), "{boundary}");
        let record = record.unwrap();
        assert_eq!(record.state(), State::ReleaseIntent, "{boundary}");
        assert_eq!(record.prior(), Settled::Owned, "{boundary}");
        let ids = record.ids();
        assert_eq!([ids[0], ids[1], ids[2]], [job[0].2, job[1].2, job[2].2]);
        assert_eq!(held(&rig), Some((State::Clear, ids)), "{boundary}");
        assert_eq!(owner(&rig), (false, false, false), "{boundary}");
        assert_eq!(scopes_from(&rig, acquired), [Some(scope); 2], "{boundary}");
        if boundary == "low power" {
            rig.custody.lifecycle.ingress(POWER, RESUME);
        }
        rig.stop().await;
    }
}

/// A release over an acquire in doubt records what its own baseline read,
/// `AcquireIntent -> Owned`, before it claims, so the record never holds an
/// intent over an intent; all three writes hold the cleanup scope.
#[tokio::test]
async fn a_release_over_an_acquire_in_doubt_records_it_owned_first() {
    let (rig, _) = fenced_readback_rig().await;
    let (state, acquire_ids) = held(&rig).unwrap();
    assert_eq!(state, State::AcquireIntent);
    let acquired = rig.custody.driver_mode.writes().len();
    assert_eq!(release(&rig).await, Some(Release::Restored));
    assert_eq!(mode(&rig), NORMAL);
    let writes = rig.custody.driver_mode.writes();
    let states: Vec<_> = writes[acquired..]
        .iter()
        .map(|(_, bytes)| Record::parse(bytes).unwrap().state())
        .collect();
    assert_eq!(states, [State::Owned, State::ReleaseIntent, State::Clear]);
    assert_eq!(
        Record::parse(&writes[acquired].1).unwrap().ids(),
        acquire_ids
    );
    assert_eq!(
        scopes_from(&rig, acquired),
        [Some(CleanupScope::OwnedStop); 3]
    );
    rig.stop().await;
}

/// A release setter refused at its checkpoint after the claim was written
/// is known not submitted: the intent reverts to its prior under the same
/// cleanup scope, on the acquire's IDs, whose 00/04 reply is still the
/// newest, so a later process seeds them. The mode is unchanged and nothing
/// is in doubt. The competing controller that refused it ended the cleanup
/// admission, so the release reports `Unadmitted`: nothing was sent.
#[tokio::test]
async fn a_release_claim_known_not_submitted_reverts_to_its_prior() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    let acquired = rig.custody.driver_mode.writes().len();
    let owned = held(&rig).unwrap().1;
    let custody = Arc::downgrade(&rig.custody);
    rig.custody.driver_mode.after_write(move || {
        custody
            .upgrade()
            .unwrap()
            .controller
            .store(true, Ordering::Release);
    });
    assert_eq!(release(&rig).await, Some(Release::Unadmitted));
    rig.custody.controller.store(false, Ordering::Release);
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    let writes = rig.custody.driver_mode.writes();
    let claim = Record::parse(&writes[acquired].1).unwrap();
    assert_eq!(claim.state(), State::ReleaseIntent);
    assert_ne!(claim.ids(), owned);
    assert_eq!(held(&rig), Some((State::Owned, owned)));
    assert_eq!(
        scopes_from(&rig, acquired),
        [Some(CleanupScope::OwnedStop); 2]
    );
    assert_eq!(owner(&rig), (true, false, false));
    assert!(!rig.runtime.driver_mode_uncertain_for_tests());
}

/// A release that may have submitted Normal unconfirmed leaves its
/// `ReleaseIntent` on disk. A later Service over the same files reports
/// its own release `Uncertain` and sends nothing. Its first step resolves
/// the intent as a loaded acquire intent is, with one read-only job that
/// activates its write lease: Driver records the intent's prior, and Normal
/// records `Clear`, after which the next tick acquires through the ordinary
/// claim on new IDs.
#[tokio::test]
async fn a_restart_over_a_release_intent_resolves_it_by_a_later_read() {
    // (case, the keyboard before the release, the firmware after the restart)
    for (case, before, firmware) in [
        ("owned, read driver", NORMAL, DRIVER),
        ("owned, read normal", NORMAL, NORMAL),
        ("unclaimed, read driver", DRIVER, DRIVER),
    ] {
        let (rig, _) = owner_rig(before).await;
        tick(&rig).await;
        rig.port.state.lock().unwrap().device_mode_bad_ack = true;
        assert_eq!(release(&rig).await, Some(Release::Unconfirmed), "{case}");
        let (state, ids) = held(&rig).unwrap();
        assert_eq!(state, State::ReleaseIntent, "{case}");
        let prior = on_disk(&rig).unwrap().prior();
        let expected_prior = if before == NORMAL {
            Settled::Owned
        } else {
            Settled::Clear
        };
        assert_eq!(prior, expected_prior, "{case}");
        // A later Stop over the intent sends nothing.
        let stopped = restarted(&rig, firmware).await;
        assert_eq!(release(&stopped).await, Some(Release::Uncertain), "{case}");
        assert!(keyboard(&stopped).is_empty(), "{case}");
        assert_eq!(held(&stopped), Some((State::ReleaseIntent, ids)), "{case}");
        // A later step resolves it with one read-only job of its own, and
        // acquires, if at all, only at the next tick.
        let later_rig = restarted(&rig, firmware).await;
        tick(&later_rig).await;
        assert_eq!(sent_from(&later_rig, 0), [0x84], "{case}");
        assert!(
            !later_rig.runtime.driver_mode_uncertain_for_tests(),
            "{case}"
        );
        let resolved = match (firmware, prior) {
            (DRIVER, Settled::Owned) => State::Owned,
            _ => State::Clear,
        };
        assert_eq!(held(&later_rig), Some((resolved, ids)), "{case}");
        assert_eq!(owner(&later_rig).0, resolved == State::Owned, "{case}");
        assert_eq!(
            later_rig.runtime.driver_mode_claims_for_tests(),
            0,
            "{case}"
        );
        // The resolution holds the ordinary admission.
        assert_eq!(scopes_from(&later_rig, 0), [None], "{case}");
        if firmware == NORMAL {
            tick(&later_rig).await;
            assert_eq!(sent_from(&later_rig, 1), [0x84, 0x04, 0x84], "{case}");
            let (state, fresh) = held(&later_rig).unwrap();
            assert_eq!(state, State::Owned, "{case}");
            assert!(fresh.iter().all(|id| !ids.contains(id)), "{fresh:?}");
        } else {
            assert_eq!(sets(&later_rig), 0, "{case}");
        }
        later_rig.stop().await;
    }
}

/// A resolution whose settle is refused before anything is written leaves
/// the owner at the intent (`Ledger::settle` returns `false`): the record
/// keeps its `ReleaseIntent` and IDs, the release stays in doubt, and the
/// ledger stays open, so a later confirmed read resolves it. The read
/// itself is still published as confirmed. Here the read
/// admission ends once the read-only job has retired, between its read and
/// its settle, and returns before the next step.
#[tokio::test]
async fn a_refused_resolution_settle_keeps_the_intent_for_a_later_read() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode_bad_ack = true;
    assert_eq!(release(&rig).await, Some(Release::Unconfirmed));
    let (state, ids) = held(&rig).unwrap();
    assert_eq!(state, State::ReleaseIntent);
    let later_rig = restarted(&rig, DRIVER).await;
    let custody = Arc::downgrade(&later_rig.custody);
    let once = std::sync::atomic::AtomicBool::new(true);
    later_rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        if once.swap(false, Ordering::AcqRel) {
            let custody = custody.upgrade().unwrap();
            custody.live.store(false, Ordering::Release);
        }
    }));
    tick(&later_rig).await;
    later_rig.custody.live.store(true, Ordering::Release);
    assert_eq!(sent_from(&later_rig, 0), [0x84]);
    assert!(later_rig.custody.driver_mode.writes().is_empty());
    assert_eq!(held(&later_rig), Some((State::ReleaseIntent, ids)));
    assert!(later_rig.runtime.driver_mode_uncertain_for_tests());
    assert!(!closed(&later_rig));
    assert_eq!(owner(&later_rig), (true, false, false));
    // The read was confirmed under the admission the job started with, so
    // it is published; the doubt is the record's, not the read cadence's.
    assert_eq!(published(&later_rig).await, Some(true));
    // The job activated this process's write lease, so the doubt now waits,
    // as after a release of its own, for a confirmed read made after the
    // step that finds it; that step sends nothing.
    step(&later_rig).await;
    assert!(sent_from(&later_rig, 1).is_empty());
    assert!(later_rig.runtime.driver_mode_uncertain_for_tests());
    // A later read of Driver settles the intent's prior on its IDs, and the
    // step then maintains Driver as a first start does, with a read only.
    record(&later_rig, DeviceMode::Driver);
    step(&later_rig).await;
    assert_eq!(sent_from(&later_rig, 1), [0x84]);
    assert_eq!(held(&later_rig), Some((State::Owned, ids)));
    assert!(!later_rig.runtime.driver_mode_uncertain_for_tests());
    assert_eq!(owner(&later_rig), (true, false, false));
    assert_eq!(published(&later_rig).await, Some(true));
    assert_eq!(scopes_from(&later_rig, 0), [None]);
    assert_eq!(sets(&later_rig), 0);
    later_rig.stop().await;
}

/// This process's own release in doubt is resolved by a later confirmed
/// read, whose settle is refused when the read admission ends between the
/// read and the settle: the `ReleaseIntent` and its IDs stay, the ledger
/// stays open and the doubt stays. The next step's read settles it.
#[tokio::test]
async fn a_refused_settle_of_this_processs_release_doubt_keeps_it_for_a_later_read() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode_bad_ack = true;
    suspend(&rig);
    tick(&rig).await;
    assert_eq!(prepared(&rig), Some(Release::Unconfirmed));
    rig.port.state.lock().unwrap().device_mode_bad_ack = false;
    let (state, ids) = held(&rig).unwrap();
    assert_eq!(state, State::ReleaseIntent);
    rig.custody.lifecycle.ingress(POWER, RESUME);
    rig.port.state.lock().unwrap().device_mode = DRIVER;
    let writes = rig.custody.driver_mode.writes().len();
    record(&rig, DeviceMode::Driver);
    // The step's read of the mode is admitted, its settle is not.
    rig.custody.read_refuse_at.store(2, Ordering::Release);
    step(&rig).await;
    assert_eq!(rig.custody.read_refuse_at.load(Ordering::Acquire), 0);
    assert_eq!(rig.custody.driver_mode.writes().len(), writes);
    assert_eq!(held(&rig), Some((State::ReleaseIntent, ids)));
    assert!(rig.runtime.driver_mode_uncertain_for_tests());
    assert!(!closed(&rig));
    step(&rig).await;
    assert_eq!(held(&rig), Some((State::Owned, ids)));
    assert!(!rig.runtime.driver_mode_uncertain_for_tests());
    assert_eq!(owner(&rig), (true, false, false));
    assert_eq!(sets(&rig), 2);
    rig.stop().await;
}

/// An acquire claim whose setter a suspend refuses at its checkpoint is
/// known not submitted, but its revert holds the ordinary admission, which
/// the suspend ended too, so the `AcquireIntent` stays. The low-power
/// release then reads Normal and resolves it as the read-only resolution
/// does, `Clear` on the claim's IDs. Nothing was claimed or changed, so it
/// reports `NotDriver`, not a foreign change.
#[tokio::test]
async fn a_suspend_between_an_acquire_claim_and_its_setter_releases_as_not_driver() {
    let (rig, _) = owner_rig(NORMAL).await;
    let custody = Arc::downgrade(&rig.custody);
    rig.custody.driver_mode.after_write(move || {
        custody.upgrade().unwrap().lifecycle.ingress(POWER, SUSPEND);
    });
    tick(&rig).await;
    let (state, ids) = held(&rig).unwrap();
    assert_eq!(state, State::AcquireIntent);
    assert_eq!((sets(&rig), mode(&rig)), (0, NORMAL));
    assert_eq!(rig.custody.driver_mode.writes().len(), 1);
    tick(&rig).await;
    assert_eq!(prepared(&rig), Some(Release::NotDriver));
    assert_eq!(held(&rig), Some((State::Clear, ids)));
    assert_eq!((sets(&rig), mode(&rig)), (0, NORMAL));
    let (owns, indeterminate, _) = owner(&rig);
    assert_eq!((owns, indeterminate), (false, false));
    rig.custody.lifecycle.ingress(POWER, RESUME);
    rig.stop().await;
}

/// A closed record cannot hold a release's claim, so the release sends no
/// setter: a corrupt record found at Stop is kept byte for byte, and a
/// claim that cannot be written closes the record after the baseline read
/// with the mode unchanged.
#[tokio::test]
async fn a_release_without_a_written_claim_sends_no_setter() {
    // A record refused at load.
    let files = MemoryFiles::default();
    files.profiles(1);
    files
        .bytes
        .lock()
        .unwrap()
        .insert(RECORD.into(), b"not a record".to_vec());
    let (corrupt, _) = wired(Rig::restarted(files.clone()), DRIVER).await;
    assert_eq!(release(&corrupt).await, Some(Release::Unrecorded));
    assert!(keyboard(&corrupt).is_empty());
    assert!(corrupt.custody.driver_mode.writes().is_empty());
    corrupt.stop().await;
    assert_eq!(files.read(RECORD).unwrap(), Some(b"not a record".to_vec()));
    // A claim that cannot be written.
    for fault in [Fault::Refused, Fault::Written] {
        let (rig, _) = owner_rig(NORMAL).await;
        tick(&rig).await;
        let sent = keyboard(&rig).len();
        rig.custody.driver_mode.fail(1, fault);
        assert_eq!(release(&rig).await, Some(Release::Unrecorded), "{fault:?}");
        assert_eq!(sent_from(&rig, sent), [0x84], "{fault:?}");
        assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER), "{fault:?}");
        assert!(closed(&rig), "{fault:?}");
    }
}

/// Owned -> Clear after a release reads Normal is written only while the
/// share has room for it: a worker slow to retire past the share reports
/// the foreign change and leaves the record `Owned`.
#[tokio::test]
async fn a_foreign_change_is_recorded_only_within_the_share() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    let (_, ids) = held(&rig).unwrap();
    let acquired = rig.custody.driver_mode.writes().len();
    {
        let mut port = rig.port.state.lock().unwrap();
        port.device_mode = NORMAL;
        port.device_mode_retire_delay = Some(Duration::from_millis(2400));
    }
    assert_eq!(release(&rig).await, Some(Release::ForeignChange));
    assert_eq!(held(&rig), Some((State::Owned, ids)));
    assert_eq!(rig.custody.driver_mode.writes().len(), acquired);
    rig.stop().await;
}

/// A low-power release its caller dropped leaves the record its job wrote:
/// the intent, whatever the job then confirmed, since nothing settled it.
/// After the resume a read made after the step that found it resolves it,
/// and the step acquires again; the
/// Stop then reads the dropped job without touching the newer record and
/// releases the new claim itself.
#[tokio::test]
async fn a_dropped_low_power_release_leaves_its_intent_for_a_read() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode_retire_delay = Some(Duration::from_millis(300));
    suspend(&rig);
    let dropped = tokio::time::timeout(Duration::from_millis(100), tick(&rig)).await;
    assert!(dropped.is_err(), "the release was still running");
    rig.custody.lifecycle.ingress(POWER, RESUME);
    std::thread::sleep(Duration::from_millis(400));
    rig.port.state.lock().unwrap().device_mode_retire_delay = None;
    assert_eq!(mode(&rig), NORMAL);
    assert_eq!(held(&rig).unwrap().0, State::ReleaseIntent);
    assert!(rig.runtime.driver_mode_uncertain_for_tests());
    // The doubt dates from the first step that finds it, so only a read
    // made after that step resolves it.
    step(&rig).await;
    assert_eq!(sets(&rig), 2);
    record(&rig, DeviceMode::Normal);
    step(&rig).await;
    assert!(!rig.runtime.driver_mode_uncertain_for_tests());
    assert_eq!((sets(&rig), mode(&rig)), (3, DRIVER));
    let (state, ids) = held(&rig).unwrap();
    assert_eq!(state, State::Owned);
    assert_eq!(release(&rig).await, Some(Release::Restored));
    assert_eq!((sets(&rig), mode(&rig)), (4, NORMAL));
    assert_eq!(held(&rig).unwrap().0, State::Clear);
    assert_ne!(held(&rig).unwrap().1, ids);
    rig.stop().await;
}

/// One slow record write does not end later releases: after a settle slower
/// than a whole release job, the next suspend's release is charged the floor
/// for its claim, restores Normal and settles `Clear`; after the resume the
/// next step acquires again, and the Stop restores Normal once more.
#[tokio::test]
async fn one_slow_record_write_leaves_the_next_release_and_acquire_whole() {
    let (rig, _) = owner_rig(NORMAL).await;
    // The settle after the acquire takes longer than a whole release job.
    rig.custody
        .driver_mode
        .delay_at(2, Duration::from_millis(800));
    tick(&rig).await;
    assert_eq!(held(&rig).unwrap().0, State::Owned);
    later(&rig).await;
    assert_eq!(rig.custody.driver_mode.writes().len(), 2);
    suspend(&rig);
    tick(&rig).await;
    assert_eq!(prepared(&rig), Some(Release::Restored));
    assert_eq!((sets(&rig), mode(&rig)), (2, NORMAL));
    assert_eq!(held(&rig).unwrap().0, State::Clear);
    rig.custody.lifecycle.ingress(POWER, RESUME);
    later(&rig).await;
    assert_eq!((sets(&rig), mode(&rig)), (3, DRIVER));
    assert_eq!(held(&rig).unwrap().0, State::Owned);
    assert_eq!(release(&rig).await, Some(Release::Restored));
    assert_eq!((sets(&rig), mode(&rig)), (4, NORMAL));
    assert_eq!(held(&rig).unwrap().0, State::Clear);
    rig.stop().await;
}

/// A release claim slower than its allowance is charged again once written:
/// the setter, its acknowledgement and the readback no longer fit the job,
/// so no setter is sent and the release reports `Exhausted`. The revert is
/// charged at the claim's own measured length, which the share no longer
/// holds, so the `ReleaseIntent` stays, a doubt a later read resolves. The
/// pace arithmetic itself, both sides of the check, is pinned on paused time
/// (`a_driver_mode_setter_is_sent_only_while_it_fits_after_the_claim`); a
/// revert with room is `a_release_claim_known_not_submitted_reverts_to_its_prior`.
/// Here every margin only grows with host load: the job's three exchanges
/// and the 640 ms claim overrun its 700 ms budget by their own lengths, and
/// the revert would need another 640 ms of a 900 ms share.
#[tokio::test]
async fn a_release_claim_slower_than_its_allowance_sends_no_setter() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    let acquired = rig.custody.driver_mode.writes().len();
    // A zero latency keeps the job on the real clock the claim's delay
    // spends, with no exchange slower than the fixture's own.
    rig.port.state.lock().unwrap().device_mode_latency = Some(Duration::ZERO);
    rig.custody
        .driver_mode
        .delay_at(1, Duration::from_millis(640));
    let sent = keyboard(&rig).len();
    assert_eq!(release(&rig).await, Some(Release::Exhausted));
    rig.port.state.lock().unwrap().device_mode_latency = None;
    assert_eq!(sent_from(&rig, sent), [0x84]);
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    let writes = rig.custody.driver_mode.writes();
    assert_eq!(writes.len(), acquired + 1, "only the claim was written");
    let claimed = Record::parse(&writes[acquired].1).unwrap();
    assert_eq!(claimed.state(), State::ReleaseIntent);
    assert!(rig.runtime.driver_mode_uncertain_for_tests());
    assert_eq!(held(&rig), Some((State::ReleaseIntent, claimed.ids())));
    // A read after the release resolves the doubt to its prior, on the
    // intent's IDs: a resolution cannot know that no setter was sent.
    record(&rig, DeviceMode::Driver);
    step(&rig).await;
    assert_eq!(held(&rig), Some((State::Owned, claimed.ids())));
    assert!(!rig.runtime.driver_mode_uncertain_for_tests());
    assert_eq!(sets(&rig), 1);
    rig.stop().await;
}

/// Fast Startup in Windows' order: every user is logged off, then the
/// system session hibernates and the Service sees PBT_APMSUSPEND, with
/// late session notices still arriving during the release. The release
/// holds the suspend generation's cleanup scope, not a console capture:
/// it restores Normal and settles `Clear` with nobody signed in, and the
/// late notices do not end it.
#[tokio::test]
async fn a_fast_startup_order_releases_with_nobody_signed_in() {
    let (rig, named) = owner_rig(NORMAL).await;
    tick(&rig).await;
    let acquired = rig.custody.driver_mode.writes().len();
    log_everyone_off(&rig, &named);
    let captured = captures(&rig);
    let custody = Arc::downgrade(&rig.custody);
    let baseline = rig.port.state.lock().unwrap().device_mode_queries + 1;
    rig.port.state.lock().unwrap().device_mode_on_query = Some(Arc::new(move |query| {
        if query == baseline {
            let custody = custody.upgrade().unwrap();
            custody
                .lifecycle
                .ingress(SESSION_CHANGE, WTS_SESSION_LOGOFF);
            custody
                .lifecycle
                .ingress(SESSION_CHANGE, WTS_CONSOLE_DISCONNECT);
        }
    }));
    suspend(&rig);
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode_on_query = None;
    assert_eq!(prepared(&rig), Some(Release::Restored));
    assert_eq!(mode(&rig), NORMAL);
    assert_eq!(captures(&rig), captured, "no console capture");
    let generation = rig.custody.lifecycle.low_power().unwrap().generation;
    assert_eq!(
        scopes_from(&rig, acquired),
        [Some(CleanupScope::LowPower { generation }); 2]
    );
    assert_eq!(held(&rig).unwrap().0, State::Clear);
    rig.custody.lifecycle.ingress(POWER, RESUME);
    rig.stop().await;
}

/// G3, a known limit: a process whose write lease never activated, because
/// nobody signed in since it started, refuses the shutdown release over a
/// loaded `Owned` record and a keyboard in Driver mode. Nothing is sent,
/// and the record stays `Owned` and open. Only a console-captured mutation
/// activates the lease, and the record custody refuses writes until it
/// has, so a release that went ahead could not write its claim. Managed
/// would release a fresh Driver read here, whoever set it.
#[tokio::test]
async fn an_unactivated_process_refuses_the_shutdown_release() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    let (state, ids) = held(&rig).unwrap();
    assert_eq!(state, State::Owned);
    let restarted = restarted(&rig, DRIVER).await;
    shutdown(&restarted).await.unwrap();
    assert_eq!(
        restarted.runtime.driver_mode_release(),
        Some(Release::Unadmitted)
    );
    assert!(
        keyboard(&restarted).is_empty(),
        "{:?}",
        keyboard(&restarted)
    );
    assert_eq!(mode(&restarted), DRIVER);
    assert_eq!(held(&restarted), Some((State::Owned, ids)));
    assert!(!closed(&restarted));
    assert!(restarted.custody.driver_mode.writes().is_empty());
}

/// A Fast Startup release that may have applied Normal unconfirmed leaves
/// its `ReleaseIntent` through the hibernate. After the hybrid boot the
/// same process resumes, and with nobody signed in nothing resolves it: the
/// owner's steps send nothing, because only a read job a client's
/// ObserveState runs can show the mode after this process's own release,
/// and no client runs at the logon screen. A later client read resolves
/// it, and that settle needs no console user.
#[tokio::test]
async fn a_fast_startup_release_intent_waits_for_a_client_read() {
    let (rig, named) = owner_rig(NORMAL).await;
    tick(&rig).await;
    log_everyone_off(&rig, &named);
    rig.port.state.lock().unwrap().device_mode_bad_ack = true;
    suspend(&rig);
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode_bad_ack = false;
    assert_eq!(prepared(&rig), Some(Release::Unconfirmed));
    let (state, ids) = held(&rig).unwrap();
    assert_eq!(state, State::ReleaseIntent);
    // The hybrid boot resumes the same process.
    rig.custody.lifecycle.ingress(POWER, RESUME);
    let sent = keyboard(&rig).len();
    for _ in 0..3 {
        later(&rig).await;
    }
    assert_eq!(keyboard(&rig).len(), sent, "the steps send nothing");
    assert_eq!(held(&rig), Some((State::ReleaseIntent, ids)));
    assert!(rig.runtime.driver_mode_uncertain_for_tests());
    // A client's read job, still with nobody at the console.
    record(&rig, DeviceMode::Normal);
    step(&rig).await;
    assert_eq!(held(&rig), Some((State::Clear, ids)));
    assert!(!rig.runtime.driver_mode_uncertain_for_tests());
    assert_eq!(keyboard(&rig).len(), sent, "no console user, no acquire");
    rig.stop().await;
}
