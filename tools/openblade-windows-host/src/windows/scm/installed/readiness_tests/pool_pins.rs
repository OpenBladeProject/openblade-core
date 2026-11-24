//! Transaction-ID pins of fan and temporary-charge records, through the
//! installed runtime: a record's pins return once it settles, so repeated
//! fan changes and temporary-charge cycles no longer exhaust the pool, while
//! an unresolved record keeps its pins, a sent ID without a correlated reply
//! is never issued again, and the fenced source-cancel rollback still finds
//! its reserved IDs unsent.
use super::*;

/// The IDs of every request sent from `since` on.
fn sent_ids(r: &Rig, since: usize) -> Vec<u8> {
    r.port.state.lock().unwrap().sent[since..]
        .iter()
        .map(|(_, _, _, id)| *id)
        .collect()
}
fn sent_len(r: &Rig) -> usize {
    r.port.state.lock().unwrap().sent.len()
}
/// Reads until the pool has issued every ID at least once more since now,
/// and returns the IDs those reads sent.
async fn read_every_round(r: &Rig) -> Vec<u8> {
    let since = sent_len(r);
    for _ in 0..400 {
        assert_eq!(fresh_observe(r).await["success"], true);
        if sent_len(r) - since > 2 * 254 {
            return sent_ids(r, since);
        }
    }
    panic!("reads did not cycle the pool");
}

/// Every ID a fan record names, from its journal rows.
#[cfg(feature = "service-fan-mutation-v2")]
fn fan_record_ids(r: &Rig, request: u32) -> Vec<u8> {
    let id = format!("aaaaaaaa-bbbb-cccc-dddd-{request:012}");
    let rows = fan_rows(r);
    let fan = &rows
        .iter()
        .rfind(|row| row["request"] == id.as_str())
        .unwrap()["fan"];
    ["preflight", "ids", "cleanup"]
        .iter()
        .flat_map(|key| fan[*key].as_array().unwrap())
        .flat_map(|pair| pair.as_array().unwrap())
        .map(|id| id.as_u64().unwrap() as u8)
        .collect()
}

/// IDs the tests hold out of the pool, so that a few leaked records exhaust
/// what is left: 114 free for fans, 54 for temporary charge.
const FAN_HELD_PAIRS: usize = 70;
#[cfg(feature = "service-temporary-charge-v1")]
const TEMPORARY_HELD_PAIRS: usize = 100;

// About 12 manual set-point changes used to exhaust a full pool, and about 5
// exhaust what is left here: each owner pinned its IDs for the process.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_changes_return_each_replaced_owners_pins() {
    let r = fan_rig(0, 0);
    r.port.reserve_for_tests(FAN_HELD_PAIRS);
    r.get().await;
    let mut lowest = usize::MAX;
    for n in 0..15u32 {
        // Manual 3700, manual 4000 and a return to Automatic, in turn.
        let request = match n % 3 {
            0 => fan_request(200 + n, Some(1), 3700),
            1 => fan_request(200 + n, Some(1), 4000),
            _ => fan_request(200 + n, Some(0), 3700),
        };
        let outcome = r.request(request).await;
        assert_eq!(outcome["result"]["status"], 1, "change {n}: {outcome}");
        lowest = lowest.min(r.port.free_transactions());
    }
    // Only the live owner's pins and IDs waiting for their command's next
    // answer are out of the pool.
    assert!(lowest >= 50, "{lowest}");
    assert_eq!(fan_rows(&r).last().unwrap()["progress"], 50);
    r.origin.stop();
    r.stop().await;
}

// A fan change whose setter acknowledgement is uncorrelated stays
// unresolved. Its record and the owner it did not replace keep every pin,
// including the setter's ID, which was sent without a terminal reply.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn an_unresolved_fan_change_keeps_its_pins_and_its_owners() {
    let r = fan_rig(0, 0);
    r.get().await;
    let owner = r.request(fan_request(300, Some(1), 3700)).await;
    assert_eq!(owner["result"]["status"], 1, "{owner}");
    let writes = r.port.state.lock().unwrap().writes;
    r.port.state.lock().unwrap().corrupt_ack = Some(writes + 1);
    let unresolved = r.request(fan_request(301, Some(1), 4000)).await;
    assert_eq!(unresolved["result"]["status"], 2, "{unresolved}");
    assert!(r.storage.pending());
    let mut pinned = fan_record_ids(&r, 300);
    pinned.extend(fan_record_ids(&r, 301));
    let queued = r.runtime.queued_pins_for_tests();
    assert!(queued.iter().all(|id| !pinned.contains(id)), "{queued:?}");
    let issued = read_every_round(&r).await;
    assert!(
        issued.iter().all(|id| !pinned.contains(id)),
        "{pinned:?} {issued:?}"
    );
    r.origin.stop();
    assert!(r.source.lock().await.drain().await.is_err());
}

#[cfg(feature = "service-temporary-charge-v1")]
fn source_battery(r: &Rig) {
    r.port.state.lock().unwrap().actual = 0;
    r.metadata.store(1, Ordering::Release);
    r.custody.lifecycle.ingress(
        windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
        10,
    );
}
#[cfg(feature = "service-temporary-charge-v1")]
async fn origin_tick(r: &Rig) {
    r.source.lock().await.process_power_source().await.unwrap();
}
#[cfg(feature = "service-temporary-charge-v1")]
async fn temporary(r: &Rig, n: u32, enabled: bool) {
    let response = r
        .request(battery_request(n, "setFullChargeOverride", json!(enabled)))
        .await;
    assert_eq!(response["result"]["status"], 1, "{n} {enabled}: {response}");
}
#[cfg(feature = "service-temporary-charge-v1")]
fn cancel_ids(r: &Rig) -> Vec<u8> {
    temporary_charge_ids(&r.files, true)
        .1
        .as_array()
        .unwrap()
        .iter()
        .map(|id| id.as_u64().unwrap() as u8)
        .collect()
}

// About 30 enable and cancel cycles used to exhaust a full pool, and about 5
// exhaust what is left here: each enable pinned its source-cancel IDs for the
// process.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn temporary_charge_cycles_return_each_enables_cancel_pins() {
    let r = Rig::new(MemoryFiles::default());
    r.port.reserve_for_tests(TEMPORARY_HELD_PAIRS);
    r.get().await;
    let mut lowest = usize::MAX;
    for n in 0..12u32 {
        temporary(&r, 400 + 2 * n, true).await;
        temporary(&r, 401 + 2 * n, false).await;
        lowest = lowest.min(r.port.free_transactions());
    }
    // A cancel settles its enable's pins, which the next enable's Reader
    // unpins, so at most two enables' pins are out at once.
    assert!(lowest >= 30, "{lowest}");
    r.stop().await;
}

// A source cancel whose flags read times out sends two of the enable's
// reserved IDs, one without a reply, and claims the obligation. The enable
// then settles: its unsent cancel IDs return, its quarantined one never does.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn a_failed_source_cancel_returns_only_its_unsent_ids() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    temporary(&r, 500, true).await;
    let reserved = cancel_ids(&r);
    let since = sent_len(&r);
    r.port.state.lock().unwrap().temporary_fail_read = true;
    source_battery(&r);
    origin_tick(&r).await;
    r.port.state.lock().unwrap().temporary_fail_read = false;
    let spent = sent_ids(&r, since);
    assert!(
        reserved[..2].iter().all(|id| spent.contains(id)),
        "{spent:?}"
    );
    assert!(
        reserved[2..].iter().all(|id| !spent.contains(id)),
        "{spent:?}"
    );
    assert!(!r.runtime.temporary_owned_for_tests());
    // The next mutation's Reader unpins the settled record's IDs.
    temporary(&r, 501, false).await;
    let issued = read_every_round(&r).await;
    assert!(!issued.contains(&reserved[1]), "{issued:?}");
    assert!(reserved[2..].iter().all(|id| issued.contains(id)));
    r.stop().await;
}

// The rollback of a fenced source cancel trusts `Transactions::unsent` on the
// enable's reserved IDs. Pins settled around it never reach those IDs: after
// the rollback no other request bears them, and the quiet tick spends exactly
// them once.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn a_fenced_source_cancel_still_finds_its_restored_ids_unsent() {
    let r = Rig::new(MemoryFiles::default());
    r.custody.scoped_fresh.store(true, Ordering::Release);
    r.get().await;
    // A settled enable whose pins the next enable's Reader unpins.
    temporary(&r, 600, true).await;
    temporary(&r, 601, false).await;
    temporary(&r, 602, true).await;
    let reserved = cancel_ids(&r);
    let writes = r.port.state.lock().unwrap().writes;
    source_battery(&r);
    // A second unplug lands after the claim, in the cancel's own worker,
    // before any request.
    let since = sent_len(&r);
    r.custody.notice_skip.store(5, Ordering::Release);
    *r.custody.notice_during_authorize.lock().unwrap() = Some((
        windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
        10,
        None,
    ));
    origin_tick(&r).await;
    assert!(r.custody.notice_during_authorize.lock().unwrap().is_none());
    r.custody.notice_skip.store(0, Ordering::Release);
    assert_eq!(sent_len(&r), since, "the fenced attempt sent nothing");
    assert!(
        r.runtime.temporary_owned_for_tests(),
        "the claim rolled back"
    );
    let queued = r.runtime.queued_pins_for_tests();
    assert!(queued.iter().all(|id| !reserved.contains(id)), "{queued:?}");
    let issued = read_every_round(&r).await;
    assert!(issued.iter().all(|id| !reserved.contains(id)), "{issued:?}");
    let since = sent_len(&r);
    origin_tick(&r).await;
    assert_eq!(r.port.state.lock().unwrap().writes, writes + 1);
    assert_eq!(r.port.state.lock().unwrap().temporary_raw, 0);
    assert!(sent_ids(&r, since).iter().any(|id| reserved.contains(id)));
    assert_eq!(temporary_charge_ids(&r.files, false).0, json!(reserved));
    assert!(!r.storage.pending());
    // Only now, with the cancel joined, are the enable's pins queued.
    let queued = r.runtime.queued_pins_for_tests();
    assert!(reserved.iter().all(|id| queued.contains(id)), "{queued:?}");
    r.stop().await;
}

// An enable whose acknowledgement is uncorrelated leaves the journal
// unsettled. Every later request and source tick reconciles it first, and it
// keeps its cancel pins through each one.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn an_unsettled_enable_keeps_its_cancel_pins() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    r.port.state.lock().unwrap().temporary_bad_ack = true;
    let response = r
        .request(battery_request(700, "setFullChargeOverride", json!(true)))
        .await;
    assert_eq!(response["result"]["status"], 2, "{response}");
    r.port.state.lock().unwrap().temporary_bad_ack = false;
    assert!(r.storage.pending());
    let reserved = cancel_ids(&r);
    let retry = r
        .request(battery_request(701, "setFullChargeOverride", json!(false)))
        .await;
    assert_eq!(retry["result"]["status"], 2, "{retry}");
    source_battery(&r);
    origin_tick(&r).await;
    let queued = r.runtime.queued_pins_for_tests();
    assert!(queued.iter().all(|id| !reserved.contains(id)), "{queued:?}");
    let issued = read_every_round(&r).await;
    assert!(issued.iter().all(|id| !reserved.contains(id)), "{issued:?}");
    assert!(r.storage.pending());
    r.stop().await;
}

// An owner that firmware already reports Automatic closes with 51 and no
// setter, and its pins return: from an explicit request, from Stop, and from
// Stop's settle-only permit after an unplug moved the owner's epoch.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn an_owner_closed_without_a_setter_returns_its_pins() {
    for case in 0..3 {
        let r = if case == 2 {
            power_change_rig(15, 0, [0; 2], [0; 2]).await
        } else {
            foreign_selector_rig(0, [2; 2], [0; 2], 15).await
        };
        let owner = fan_record_ids(&r, 230);
        assert_eq!(r.port.pinned_transactions(&owner).len(), owner.len());
        if case == 0 {
            let out = r.request(fan_request(232, Some(0), 3700)).await;
            assert_eq!(out["result"]["status"], 1, "{out}");
            assert_eq!(fan_rows(&r).last().unwrap()["progress"], 51);
            assert!(r.port.pinned_transactions(&owner).is_empty());
        }
        r.origin.stop();
        r.stop().await;
        assert_eq!(fan_rows(&r).last().unwrap()["progress"], 51, "{case}");
        assert!(r.port.pinned_transactions(&owner).is_empty(), "{case}");
    }
}

/// Every ID the pool keeps pinned, sorted.
fn pinned_ids(r: &Rig) -> Vec<u8> {
    r.port.pinned_transactions(&(1..=254).collect::<Vec<u8>>())
}
fn queued(r: &Rig) -> Vec<u8> {
    let mut queued = r.runtime.queued_pins_for_tests();
    queued.sort_unstable();
    queued
}

// A competing controller that appears at any fresh authorization refuses the
// request, before or after its record named IDs. Whatever it pinned is then
// queued for release: a record known not to have submitted anything (row 6,
// or no row at all) settles.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_refused_request_queues_every_pin_it_kept() {
    let mut retained = false;
    // From the 30th on, a refusal follows a setter.
    for at in 1..30 {
        let r = fan_rig(0, 0);
        r.get().await;
        r.custody.refuse_at.store(at, Ordering::Release);
        let out = r.request(fan_request(300, Some(1), 3700)).await;
        assert_eq!(out["result"]["status"], 2, "{at}: {out}");
        assert!(!r.storage.pending(), "{at}");
        assert_eq!(pinned_ids(&r), queued(&r), "{at}");
        retained |= !queued(&r).is_empty();
    }
    assert!(retained, "no refusal came after the record named its IDs");
}
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn a_refused_enable_queues_every_pin_it_kept() {
    let mut retained = false;
    for at in 1..30 {
        let r = Rig::new(MemoryFiles::default());
        r.get().await;
        r.custody.refuse_at.store(at, Ordering::Release);
        let out = r
            .request(battery_request(700, "setFullChargeOverride", json!(true)))
            .await;
        assert_eq!(out["result"]["status"], 2, "{at}: {out}");
        assert!(!r.storage.pending(), "{at}");
        assert_eq!(pinned_ids(&r), queued(&r), "{at}");
        retained |= !queued(&r).is_empty();
    }
    assert!(retained, "no refusal came after the enable named its IDs");
}

// The origin tick reconciles while an enable's setter is still held. The
// enable has not joined, so the tick leaves its pins alone, and once it
// confirms they are still pinned for its source cancel.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn a_tick_during_an_enable_leaves_its_pins_pinned() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    let release = Arc::new(AtomicBool::new(false));
    let writes = r.port.state.lock().unwrap().writes;
    r.port.state.lock().unwrap().held_write = Some((writes + 1, release.clone()));
    let enable = r.request(battery_request(800, "setFullChargeOverride", json!(true)));
    let tick = async {
        while r.port.state.lock().unwrap().writes == writes {
            tokio::task::yield_now().await;
        }
        let released = async {
            tokio::time::sleep(Duration::from_millis(50)).await;
            release.store(true, Ordering::Release);
        };
        tokio::join!(origin_tick(&r), released);
    };
    let (response, ()) = tokio::time::timeout(HANG_GUARD, async { tokio::join!(enable, tick) })
        .await
        .unwrap();
    assert_eq!(response["result"]["status"], 1, "{response}");
    let reserved = cancel_ids(&r);
    assert_eq!(r.port.pinned_transactions(&reserved), reserved);
    assert!(queued(&r).iter().all(|id| !reserved.contains(id)));
    r.stop().await;
}

// A source cancel whose worker never confirms its stop has not joined, and
// may still send the enable's reserved IDs: the enable keeps them.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn an_unjoined_source_cancel_keeps_the_enables_pins() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    temporary(&r, 810, true).await;
    let reserved = cancel_ids(&r);
    r.port.state.lock().unwrap().mutation_worker_error = Some(Arc::new(|| {
        io::Error::other("injected unconfirmed worker stop")
    }));
    source_battery(&r);
    let _ = r.source.lock().await.process_power_source().await;
    r.port.state.lock().unwrap().mutation_worker_error = None;
    assert_eq!(r.port.pinned_transactions(&reserved), reserved);
    assert!(queued(&r).iter().all(|id| !reserved.contains(id)));
}

// A cancel refused before any request leaves the reservation, and a release
// that storage then defers puts the enable back in its slot with its pins.
// Once the release goes through, they are queued.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn a_deferred_release_keeps_the_enables_pins() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    temporary(&r, 820, true).await;
    let reserved = cancel_ids(&r);
    r.custody.fresh_refuse.store(true, Ordering::Release);
    r.files.release_fault.store(1, Ordering::Release);
    source_battery(&r);
    let _ = r.source.lock().await.process_power_source().await;
    assert!(r.runtime.release_only_for_tests());
    assert_eq!(r.port.pinned_transactions(&reserved), reserved);
    assert!(queued(&r).iter().all(|id| !reserved.contains(id)));
    r.custody.fresh_refuse.store(false, Ordering::Release);
    r.files.release_fault.store(0, Ordering::Release);
    let _ = r.source.lock().await.process_power_source().await;
    assert!(!r.runtime.release_only_for_tests());
    assert!(
        reserved.iter().all(|id| queued(&r).contains(id)),
        "{:?}",
        queued(&r)
    );
}

// A newer confirmed enable replaces the owner, whose pins are then queued.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn a_replaced_enable_queues_its_pins() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    temporary(&r, 830, true).await;
    let first = cancel_ids(&r);
    temporary(&r, 831, true).await;
    let second = cancel_ids(&r);
    assert!(first.iter().all(|id| !second.contains(id)));
    assert!(
        first.iter().all(|id| queued(&r).contains(id)),
        "{:?}",
        queued(&r)
    );
    assert_eq!(r.port.pinned_transactions(&second), second);
    r.stop().await;
}

// An owner whose closing 51 cannot be appended stays the owner, from an
// explicit request or from Stop, and keeps its pins.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn an_owner_whose_closing_row_fails_keeps_its_pins() {
    for explicit in [true, false] {
        let r = foreign_selector_rig(0, [2; 2], [0; 2], 15).await;
        let owner = fan_record_ids(&r, 230);
        if explicit {
            // The request saves its intent after its first read, so only the
            // journal is made to fail: from that read on it no longer holds
            // the bytes the store expects, and the 51 is the append refused.
            let files = r.files.clone();
            let at = r.port.state.lock().unwrap().power_reads + 1;
            r.port.state.lock().unwrap().on_power = Some(Arc::new(move |n| {
                if n == at {
                    let mut bytes = files.bytes.lock().unwrap();
                    let journal = bytes.get_mut("thermal-mutation.journal").unwrap();
                    journal.insert(journal.len() - 1, b' ');
                }
            }));
            let out = r.request(fan_request(232, Some(0), 3700)).await;
            assert_ne!(out["result"]["status"], 1, "{out}");
        } else {
            r.files.fail.store(true, Ordering::Release);
            r.origin.stop();
            let _ = r.source.lock().await.drain().await;
        }
        assert_eq!(fan_rows(&r).last().unwrap()["progress"], 30, "{explicit}");
        assert_eq!(r.port.pinned_transactions(&owner), owner, "{explicit}");
        assert!(
            queued(&r).iter().all(|id| !owner.contains(id)),
            "{explicit}"
        );
    }
}

/// Fences a source cancel as its flags read is sent, after its power read:
/// the attempt sent requests and submitted nothing.
#[cfg(feature = "service-temporary-charge-v1")]
async fn fenced_after_a_request(r: &Rig) {
    let custody = r.custody.clone();
    let fired = Arc::new(AtomicBool::new(false));
    let once = fired.clone();
    r.port.state.lock().unwrap().battery_getter_hook = Some(Arc::new(move |_, _| {
        if !once.swap(true, Ordering::AcqRel) {
            custody.lifecycle.ingress(
                windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
                10,
            );
        }
    }));
    origin_tick(r).await;
    r.port.state.lock().unwrap().battery_getter_hook = None;
    assert!(fired.load(Ordering::Acquire));
}
/// The IDs a journal row's `tfc` member names under `key`.
#[cfg(feature = "service-temporary-charge-v1")]
fn row_ids(row: &Value, key: &str) -> Vec<u8> {
    row["tfc"][key]
        .as_array()
        .unwrap_or_else(|| panic!("{key}: {row}"))
        .iter()
        .map(|id| id.as_u64().unwrap() as u8)
        .collect()
}
/// An enabled rig whose first source cancel was fenced after it sent some of
/// the enable's reserved IDs, and the IDs reserved.
#[cfg(feature = "service-temporary-charge-v1")]
async fn spent_rig(files: MemoryFiles) -> (Rig, Vec<u8>) {
    let r = Rig::new(files);
    r.custody.scoped_fresh.store(true, Ordering::Release);
    r.get().await;
    temporary(&r, 800, true).await;
    let reserved = cancel_ids(&r);
    let writes = r.port.state.lock().unwrap().writes;
    source_battery(&r);
    let since = sent_len(&r);
    fenced_after_a_request(&r).await;
    let spent = sent_ids(&r, since);
    assert!(
        !spent.is_empty() && spent.iter().all(|id| reserved.contains(id)),
        "{spent:?} {reserved:?}"
    );
    assert_eq!(r.port.state.lock().unwrap().writes, writes);
    assert!(
        r.runtime.temporary_owned_for_tests(),
        "the claim returns for a fresh-ID retry"
    );
    assert!(r.storage.temporary_desired());
    // The enable still owns the charge, so its pins stay.
    assert_eq!(r.port.pinned_transactions(&reserved).len(), reserved.len());
    (r, reserved)
}

// A fenced source cancel that sent some of its enable's reserved IDs never
// sends them again. The retry draws fresh IDs, names the reserved ones in its
// record, and its pins are queued for release once its attempt joined.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn a_fenced_source_cancel_that_sent_retries_once_with_fresh_pinned_ids() {
    let (r, reserved) = spent_rig(MemoryFiles::default()).await;
    let writes = r.port.state.lock().unwrap().writes;
    let since = sent_len(&r);
    origin_tick(&r).await;
    assert_eq!(r.port.state.lock().unwrap().writes, writes + 1);
    assert_eq!(r.port.state.lock().unwrap().temporary_raw, 0);
    assert!(!r.storage.pending());
    let cancel = fan_rows(&r).last().unwrap().clone();
    assert_eq!(cancel["progress"], 72, "{cancel}");
    assert_eq!(row_ids(&cancel, "superseded_ids"), reserved);
    let fresh = row_ids(&cancel, "ids");
    assert!(fresh.iter().all(|id| !reserved.contains(id)), "{fresh:?}");
    let retry = sent_ids(&r, since);
    assert!(retry.iter().all(|id| fresh.contains(id)), "{retry:?}");
    let queued = r.runtime.queued_pins_for_tests();
    assert!(fresh.iter().all(|id| queued.contains(id)), "{queued:?}");
    assert!(reserved.iter().all(|id| queued.contains(id)), "{queued:?}");
    for _ in 0..3 {
        source_battery(&r);
        origin_tick(&r).await;
    }
    assert_eq!(r.port.state.lock().unwrap().writes, writes + 1);
    r.stop().await;
}

// A fresh-ID retry fenced after it sent pauses like any other unconfirmed
// cancel: the chain is bounded at the reserved IDs and one fresh set. Its
// own pins return: its record never named them, so its retirement unpinned them.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn a_fenced_fresh_id_retry_that_sent_pauses() {
    let (r, reserved) = spent_rig(MemoryFiles::default()).await;
    let writes = r.port.state.lock().unwrap().writes;
    let since = sent_len(&r);
    fenced_after_a_request(&r).await;
    let fresh = sent_ids(&r, since);
    assert!(
        !fresh.is_empty() && fresh.iter().all(|id| !reserved.contains(id)),
        "{fresh:?}"
    );
    assert!(!r.runtime.temporary_owned_for_tests());
    assert!(!r.storage.temporary_desired());
    assert!(r.port.pinned_transactions(&fresh).is_empty(), "{fresh:?}");
    let reads = r.port.state.lock().unwrap().temporary_reads;
    for _ in 0..3 {
        source_battery(&r);
        origin_tick(&r).await;
    }
    assert_eq!(r.port.state.lock().unwrap().writes, writes);
    assert_eq!(
        r.port.state.lock().unwrap().temporary_reads,
        reads,
        "not even read again"
    );
    r.stop().await;
}

// A fresh-ID retry fenced before it sent anything gives its claim back too,
// and the next tick retries with fresh IDs again, never the reserved ones.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn a_fresh_id_retry_fenced_before_any_request_is_retried_with_fresh_ids() {
    let (r, reserved) = spent_rig(MemoryFiles::default()).await;
    let writes = r.port.state.lock().unwrap().writes;
    let since = sent_len(&r);
    r.custody.notice_skip.store(5, Ordering::Release);
    *r.custody.notice_during_authorize.lock().unwrap() = Some((
        windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
        10,
        None,
    ));
    origin_tick(&r).await;
    assert!(r.custody.notice_during_authorize.lock().unwrap().is_none());
    r.custody.notice_skip.store(0, Ordering::Release);
    assert_eq!(sent_len(&r), since, "the fenced attempt sent nothing");
    assert!(r.runtime.temporary_owned_for_tests());
    assert!(r.storage.temporary_desired());
    origin_tick(&r).await;
    assert_eq!(r.port.state.lock().unwrap().writes, writes + 1);
    let cancel = fan_rows(&r).last().unwrap().clone();
    assert_eq!(cancel["progress"], 72, "{cancel}");
    assert_eq!(row_ids(&cancel, "superseded_ids"), reserved);
    assert!(sent_ids(&r, since).iter().all(|id| !reserved.contains(id)));
    r.stop().await;
}

// A restart in the middle of a fresh-ID retry, whose setter was submitted
// without a correlated acknowledgement: the reopened journal reads the row
// and stays unsettled, and a new process writes nothing.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn a_restart_during_a_fresh_id_retry_keeps_it_unsettled() {
    let files = MemoryFiles::default();
    let (r, reserved) = spent_rig(files.clone()).await;
    r.port.state.lock().unwrap().temporary_bad_ack = true;
    origin_tick(&r).await;
    assert_eq!(
        r.port.state.lock().unwrap().writes,
        2,
        "the enable and the retry"
    );
    assert!(r.storage.pending());
    let last = fan_rows(&r).last().unwrap().clone();
    assert_eq!(last["progress"], 70, "{last}");
    assert_eq!(row_ids(&last, "superseded_ids"), reserved);
    let restarted = Storage::open(Box::new(files.clone()), SID).unwrap();
    assert!(restarted.pending());
    assert_eq!(restarted.progress(), Some(70));
    let _ = r.source.lock().await.drain().await;
    let next = Rig::new(files);
    next.custody.scoped_fresh.store(true, Ordering::Release);
    next.get().await;
    source_battery(&next);
    origin_tick(&next).await;
    assert_eq!(next.port.state.lock().unwrap().writes, 0);
    assert!(next.storage.pending());
    let _ = next.source.lock().await.drain().await;
}

// A fresh-ID retry whose worker never confirms its stop has not joined, and
// may still send its own IDs: its record keeps them pinned.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn an_unjoined_fresh_id_retry_keeps_its_pins() {
    let (r, reserved) = spent_rig(MemoryFiles::default()).await;
    r.port.state.lock().unwrap().mutation_worker_error = Some(Arc::new(|| {
        io::Error::other("injected unconfirmed worker stop")
    }));
    let _ = r.source.lock().await.process_power_source().await;
    r.port.state.lock().unwrap().mutation_worker_error = None;
    let cancel = fan_rows(&r).last().unwrap().clone();
    assert_eq!(row_ids(&cancel, "superseded_ids"), reserved, "{cancel}");
    let mut fresh = row_ids(&cancel, "ids");
    fresh.sort_unstable();
    assert_eq!(r.port.pinned_transactions(&fresh), fresh);
    assert!(queued(&r).iter().all(|id| !fresh.contains(id)));
    assert_eq!(r.port.pinned_transactions(&reserved).len(), reserved.len());
}
