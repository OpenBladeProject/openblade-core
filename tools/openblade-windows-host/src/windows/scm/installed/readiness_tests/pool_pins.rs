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
