//! A restarted Service's transaction pool, seeded from the journal an earlier
//! process left. The firmware reply mailbox outlives the process and reading
//! a reply does not clear it, so the IDs of each family's newest request and
//! of the live owners are never issued again, while older settled requests
//! and an unreadable or absent journal leave the rest of the pool. Each
//! restart is a fresh port and pool over the earlier rig's files; both pools
//! issue from ID 1, so without the seed the restarted reads would reissue
//! the earlier IDs first.
use super::*;

/// Every ID a journal row's `family` record names under `keys`, ascending.
#[cfg(feature = "service-fan-mutation-v2")]
fn named(row: &Value, family: &str, keys: &[&str]) -> Vec<u8> {
    let mut ids: Vec<u8> = keys
        .iter()
        .flat_map(|key| row[family][*key].as_array().unwrap())
        .flat_map(|id| id.as_array().cloned().unwrap_or_else(|| vec![id.clone()]))
        .map(|id| id.as_u64().unwrap() as u8)
        .collect();
    ids.sort_unstable();
    ids.dedup();
    ids
}

/// Every ID a fan journal row names.
#[cfg(feature = "service-fan-mutation-v2")]
fn fan_named(row: &Value) -> Vec<u8> {
    named(row, "fan", &["preflight", "ids", "cleanup"])
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

/// The restarted `r` holds back exactly `seeded`, and reads that cycle its
/// pool twice never send one of them.
#[cfg(feature = "service-fan-mutation-v2")]
async fn withholds(r: Rig, seeded: &[u8]) -> (Rig, Vec<u8>) {
    assert_eq!(r.port.free_transactions(), 254 - seeded.len());
    let sent = reads_over_two_rounds(&r).await;
    assert!(!sent.iter().any(|id| seeded.contains(id)), "{seeded:?}");
    (r, sent)
}

/// Restarts over `earlier`'s files, which must withhold exactly `seeded`.
#[cfg(feature = "service-fan-mutation-v2")]
async fn restart_withholds(earlier: &Rig, seeded: &[u8]) -> (Rig, Vec<u8>) {
    withholds(Rig::restarted(earlier.files.clone()), seeded).await
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
    let ids = fan_named(&last);
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
    let named = fan_named(&last);
    assert!(named.len() > 10, "{named:?}");
    let (restarted, _) = restart_withholds(&r, &named).await;
    restarted.origin.stop();
    r.origin.stop();
}

/// A battery write whose acknowledgement was malformed (60).
#[cfg(feature = "service-battery-policy-v1")]
async fn possibly_submitted_battery_write(n: u32) -> (Rig, Vec<u8>) {
    let files = MemoryFiles::default();
    files.profiles(1);
    let r = Rig::new(files);
    r.get().await;
    r.port.state.lock().unwrap().battery_bad_ack = true;
    assert_eq!(
        r.request(battery_request(n, "setBatteryProtection", json!(false)))
            .await["result"]["status"],
        2
    );
    let last = fan_rows(&r).pop().unwrap();
    assert_eq!(last["progress"], 60);
    let ids = named(&last, "battery", &["ids"]);
    (r, ids)
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn a_restart_never_reissues_a_possibly_submitted_battery_writes_ids() {
    let (r, ids) = possibly_submitted_battery_write(402).await;
    restart_withholds(&r, &ids).await;
}

/// A confirmed history: the newest fan request and the confirmed battery
/// write are withheld, their replies being the newest of their commands,
/// and the earlier manual fan request is issued again.
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn a_confirmed_history_seeds_only_each_familys_newest_request() {
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
    let manual = rows.iter().find(|row| row["fan"]["intent"]["mode"] == 1);
    let fan = rows.iter().rfind(|row| row.get("fan").is_some()).unwrap();
    let battery = rows.last().unwrap();
    assert_eq!(battery["progress"], 62);
    let mut seeded = fan_named(fan);
    seeded.extend(named(battery, "battery", &["ids"]));
    seeded.sort_unstable();
    seeded.dedup();
    let earlier: Vec<u8> = fan_named(manual.unwrap())
        .into_iter()
        .filter(|id| !seeded.contains(id))
        .collect();
    assert!(!earlier.is_empty());
    let (restarted, sent) = restart_withholds(&r, &seeded).await;
    assert!(earlier.iter().all(|id| sent.contains(id)), "{earlier:?}");
    restarted.origin.stop();
    r.origin.stop();
}

/// A transient read failure before the first job is retried once, so the
/// pool is still seeded; two leave it whole, as before the seed.
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn a_failed_journal_read_is_retried_once_before_the_first_job() {
    let (r, ids) = possibly_submitted_battery_write(406).await;
    let transient = || io::Error::from(io::ErrorKind::Interrupted);
    let retried = Rig::restarted_failing(r.files.clone(), vec![transient()]);
    withholds(retried, &ids).await;
    let failed = Rig::restarted_failing(r.files.clone(), vec![transient(), transient()]);
    assert_eq!(failed.port.free_transactions(), 254);
    assert_eq!(fresh_observe(&failed).await["success"], true);
}

/// The same possibly submitted write, restarted over a custody that cannot
/// read the journal: the pool is whole, as before the seed, and reads run.
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn an_unreadable_journal_seeds_nothing_and_reads_still_run() {
    let (r, _) = possibly_submitted_battery_write(407).await;
    let restarted = Rig::new(r.files.clone());
    assert!(ReadCustody::journal_read_release(&*restarted.custody, false).is_err());
    assert_eq!(restarted.port.free_transactions(), 254);
    assert_eq!(fresh_observe(&restarted).await["success"], true);
}

#[tokio::test]
async fn an_absent_journal_seeds_nothing_and_does_not_block_startup() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let r = Rig::restarted(files);
    assert_eq!(
        ReadCustody::journal_read_release(&*r.custody, false).unwrap(),
        None
    );
    assert_eq!(r.port.free_transactions(), 254);
    assert_eq!(fresh_observe(&r).await["success"], true);
    r.stop().await;
}

/// A journal that fails fatal admission at the seed fails the restarted
/// source's first exchange with that failure, as its own reads would.
#[tokio::test]
async fn a_fatal_journal_admission_failure_is_kept_by_the_restarted_source() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let message = "injected journal owner binding changed";
    let fatal = crate::installed::readiness::thermal_mutation::fatal_admission(message);
    let r = Rig::restarted_failing(files, vec![fatal]);
    assert_eq!(r.port.free_transactions(), 254);
    let request = json!({"operation":"getStatus","version":90,"requestId":"bbbbbbbb-cccc-dddd-eeee-000000000098"});
    let (mut client, server) = duplex(65536);
    let publications = r.source.lock().await.publications();
    let server = serve_connection(
        Box::new(SidStream {
            inner: test_stream(server, USER, Fault::None),
            sid: SID.into(),
        }),
        r.source.clone(),
        publications,
        Rc::new(Subscriptions::new(2)),
    );
    let client = async {
        crate::service_frame::write_payload(&mut client, &serde_json::to_vec(&request).unwrap())
            .await
            .unwrap();
        crate::service_frame::read_payload(&mut client).await
    };
    let (server, reply) = tokio::join!(server, client);
    assert!(
        matches!(&server, Err(ExchangeError::Runtime(error)) if crate::installed::readiness::thermal_mutation::is_fatal_admission(error) && error.to_string() == message),
        "{:?}",
        server.err()
    );
    assert!(reply.is_err());
    assert_eq!(r.port.state.lock().unwrap().writes, 0);
}
