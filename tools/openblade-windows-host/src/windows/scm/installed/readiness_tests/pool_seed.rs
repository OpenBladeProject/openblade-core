//! A restarted Service's transaction pool, seeded from the journal an earlier
//! process left. The firmware reply mailbox outlives the process, so an ID
//! the earlier process may have sent without reading its reply is never
//! issued again, while a confirmed history and an unreadable or absent
//! journal leave the whole pool. Each restart is a fresh port and pool over
//! the earlier rig's files; both pools issue from ID 1, so without the seed
//! the restarted reads would reissue the earlier IDs first.
use super::*;

/// Every ID a journal row's `family` record names under `keys`.
#[cfg(feature = "service-fan-mutation-v2")]
fn named(row: &Value, family: &str, keys: &[&str]) -> Vec<u8> {
    let mut ids: Vec<u8> = keys
        .iter()
        .flat_map(|key| row[family][*key].as_array().unwrap())
        .flat_map(|id| id.as_array().cloned().unwrap_or_else(|| vec![id.clone()]))
        .map(|id| id.as_u64().unwrap() as u8)
        .collect();
    ids.sort_unstable();
    ids
}

/// The IDs `r` sends over enough reads to issue every free ID at least twice.
#[cfg(feature = "service-fan-mutation-v2")]
async fn reads_over_two_rounds(r: &Rig) -> Vec<u8> {
    let since = r.port.state.lock().unwrap().sent.len();
    loop {
        assert_eq!(fresh_observe(r).await["success"], true);
        let sent = &r.port.state.lock().unwrap().sent[since..];
        if sent.len() > 2 * 254 {
            return sent.iter().map(|(_, _, _, id)| *id).collect();
        }
    }
}

/// Restarts over `earlier`'s files: the new pool holds back exactly
/// `seeded`, and reads that cycle it twice never send one of them.
#[cfg(feature = "service-fan-mutation-v2")]
async fn restart_withholds(earlier: &Rig, seeded: &[u8]) -> Rig {
    let r = Rig::restarted(earlier.files.clone());
    assert_eq!(r.port.free_transactions(), 254 - seeded.len());
    let sent = reads_over_two_rounds(&r).await;
    assert!(!sent.iter().any(|id| seeded.contains(id)), "{seeded:?}");
    r
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_restart_never_reissues_an_unsettled_fan_chains_ids() {
    let r = fan_rig(0, 0);
    r.get().await;
    r.port.state.lock().unwrap().fail_write = Some(1);
    assert_eq!(
        r.request(fan_request(400, Some(1), 3700)).await["result"]["status"],
        2
    );
    let last = fan_rows(&r).pop().unwrap();
    assert!(
        !openblade_domain::thermal_mutation::settled(last["progress"].as_u64().unwrap() as u8),
        "{last}"
    );
    // The setter and readback IDs, which the first process issued, not the
    // preflight IDs whose replies it read or the cleanup it never began.
    let ids = named(&last, "fan", &["ids"]);
    assert!(ids.contains(&5), "{ids:?}");
    restart_withholds(&r, &ids).await;
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_restart_withholds_a_live_fan_owners_cleanup() {
    // A Stop cleanup can send these IDs and fail before its first row.
    let r = fan_rig(0, 0);
    r.get().await;
    assert_eq!(
        r.request(fan_request(401, Some(1), 3700)).await["result"]["status"],
        1
    );
    let last = fan_rows(&r).pop().unwrap();
    assert_eq!(last["progress"], 30);
    let restarted = restart_withholds(&r, &named(&last, "fan", &["cleanup"])).await;
    restarted.origin.stop();
    r.origin.stop();
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn a_restart_never_reissues_a_possibly_submitted_battery_writes_ids() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let r = Rig::new(files);
    r.get().await;
    r.port.state.lock().unwrap().battery_bad_ack = true;
    assert_eq!(
        r.request(battery_request(402, "setBatteryProtection", json!(false)))
            .await["result"]["status"],
        2
    );
    let last = fan_rows(&r).pop().unwrap();
    assert_eq!(last["progress"], 60);
    restart_withholds(&r, &named(&last, "battery", &["ids"])).await;
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn a_confirmed_history_seeds_nothing() {
    let r = fan_rig(0, 0);
    r.get().await;
    for request in [
        fan_request(403, Some(1), 3700),
        fan_request(404, Some(0), 3700),
        battery_request(405, "setBatteryPolicy", json!({"chargeLimitPercent":70})),
    ] {
        assert_eq!(r.request(request).await["result"]["status"], 1);
    }
    let rows = fan_rows(&r);
    assert!(rows.iter().any(|row| row["fan"]["intent"]["mode"] == 1));
    assert_eq!(rows.last().unwrap()["progress"], 62);
    let restarted = restart_withholds(&r, &[]).await;
    assert_eq!(restarted.get().await["success"], true);
    restarted.origin.stop();
    r.origin.stop();
}

/// The same possibly submitted write, restarted over a custody that cannot
/// read the journal: the pool is whole, as before the seed, and reads run.
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn an_unreadable_journal_seeds_nothing_and_reads_still_run() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let r = Rig::new(files);
    r.get().await;
    r.port.state.lock().unwrap().battery_bad_ack = true;
    r.request(battery_request(406, "setBatteryProtection", json!(false)))
        .await;
    assert_eq!(fan_rows(&r).pop().unwrap()["progress"], 60);
    let restarted = Rig::new(r.files.clone());
    assert!(ReadCustody::journal_read(&*restarted.custody, false).is_err());
    assert_eq!(restarted.port.free_transactions(), 254);
    assert_eq!(fresh_observe(&restarted).await["success"], true);
}

#[tokio::test]
async fn an_absent_journal_seeds_nothing_and_does_not_block_startup() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let r = Rig::restarted(files);
    assert_eq!(ReadCustody::journal_read(&*r.custody, false).unwrap(), None);
    assert_eq!(r.port.free_transactions(), 254);
    assert_eq!(fresh_observe(&r).await["success"], true);
    r.stop().await;
}
