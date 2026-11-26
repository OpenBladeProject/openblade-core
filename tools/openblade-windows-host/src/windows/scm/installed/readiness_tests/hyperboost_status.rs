//! Managed's HyperBoost status members and `recoverHyperBoostRestore`
//! through the installed dispatcher.
//!
//! The members are the metadata observer's: the pad and managed's ownership
//! journal as the HyperBoost requests' probes read them, on the matched
//! host only. The recovery request answers from managed's journal and writes
//! nothing: no setter, no native journal row and no status.
use super::*;

/// Managed `RecoverHyperBoostRestoreAsync` replies.
const NO_RESTORE: &str = "There is no saved HyperBoost restore to retry.";
const NOT_AWAITING_RETRY: &str =
    "The HyperBoost ownership journal is not awaiting a user-authorized restore retry.";
const ACTIVE_USER: &str = "The active interactive Windows user is required.";
/// Managed `ControlOperationStatus.Stored` and `Blocked`.
const STORED: u8 = 0;
const BLOCKED: u8 = 2;
/// Managed `HyperBoostOwnershipJournalState.RestorePending`.
const RESTORE_PENDING: u8 = 2;

impl Rig {
    async fn recover_as(&self, n: u8, caller: Caller) -> Value {
        let response = self
            .request_as(
                json!({"operation":"recoverHyperBoostRestore","version":90,"requestId":format!("aaaaaaaa-bbbb-cccc-ffff-{n:012}")}),
                caller,
                SID,
            )
            .await;
        // It changes nothing, so no status follows the result.
        assert!(response["status"].is_null(), "{response}");
        assert!(!response["result"].is_null(), "{response}");
        response
    }
    async fn recover(&self, n: u8) -> Value {
        self.recover_as(n, USER).await
    }
    /// The three members of a fresh status.
    async fn members(&self) -> (Value, Value, Value) {
        let status = self.get().await;
        let member = |name: &str| status["status"][name].clone();
        (
            member("padlessHyperBoostEligibility"),
            member("hyperBoostRestorePending"),
            member("hyperBoostBlockingControllers"),
        )
    }
}
/// What the request must leave as it found it.
fn untouched(rig: &Rig) -> (usize, usize, usize) {
    (
        setters(rig).len(),
        rows(rig).len(),
        rig.port.state.lock().unwrap().writes,
    )
}

/// Managed `ResolvePadlessHyperBoostEligibility`: CoolingPadAbsent (1),
/// CoolingPadPresent (2), and Unknown (0) for a scan that failed. A status
/// takes none of the observations a request's gates make.
#[tokio::test]
async fn status_reports_the_pad_the_observer_read() {
    for (pad, eligibility) in [
        (Pad::Absent, 1),
        (Pad::Present, 2),
        (Pad::ObservationFailed, 0),
    ] {
        let rig = rig_at(0, 0, FULL, saved_files());
        *rig.hyperboost.pad.lock().unwrap() = pad;
        let (observed, pending, blockers) = rig.members().await;
        assert_eq!(observed, eligibility, "{pad:?}");
        assert_eq!(pending, false, "{pad:?}");
        // A complete observation that found no controller: an empty list,
        // not managed's null.
        assert_eq!(blockers, json!([]), "{pad:?}");
        assert_eq!(rig.hyperboost.observations.load(Ordering::Acquire), 0);
        assert_eq!(rig.hyperboost.journal_reads.load(Ordering::Acquire), 0);
        rig.stop().await;
    }
}

/// A request's pad scans run to their answer while nothing cancels their
/// job: a pad-less entry is admitted on three of them, one for the request
/// and one before each enable setter, and none ends as a cancelled scan.
#[tokio::test]
async fn an_uncancelled_pad_scan_admits_a_padless_entry() {
    let rig = rig_at(0, 0, FULL, saved_files());
    let result = rig.hyperboost(1, true).await;
    assert_eq!(result["result"]["status"], 1, "{result}");
    assert_eq!(setters(&rig).len(), 2);
    assert_eq!(rig.hyperboost.observations.load(Ordering::Acquire), 3);
    assert_eq!(rig.hyperboost.gate_cancellations.load(Ordering::Acquire), 0);
    rig.stop_in_hyperboost().await;
}

/// Managed `hyperBoostRestorePending` starts from its journal's state
/// (`load.Journal?.State == RestorePending`). Every other state, an absent
/// journal and a document managed's store reads as invalid report none, as
/// managed does (10505-10518). A journal that cannot be read establishes
/// neither answer: with none before it, it is pending, as managed's recovery
/// treats a journal it cannot inspect (4057-4069).
#[tokio::test]
async fn status_reports_a_restore_managed_left_pending() {
    let foreign = String::from_utf8(journal_in(RESTORE_PENDING))
        .unwrap()
        .replacen('{', "{\"native\": true,", 1)
        .into_bytes();
    for (journal, pending) in [
        (Some(Some(journal_in(RESTORE_PENDING))), true),
        (Some(None), false),
        (Some(Some(relinquished())), false),
        (Some(Some(journal_in(0))), false),
        (Some(Some(journal_in(1))), false),
        (Some(Some(b"{".to_vec())), false),
        (Some(Some(foreign)), false),
        (Some(Some(journal_in(7))), false),
        (None, true),
    ] {
        let rig = rig_at(0, 0, FULL, saved_files());
        *rig.hyperboost.journal.lock().unwrap() = journal.clone();
        let (pad, observed, _) = rig.members().await;
        assert_eq!(observed, pending, "{journal:?}");
        assert_eq!(pad, 1, "{journal:?}");
        rig.stop().await;
    }
}

/// A journal read that fails keeps the pending restore the last status
/// reported, as managed keeps its presentation when its durable state cannot
/// be read (10133-10152): one failed read neither hides a pending restore
/// nor invents one.
#[tokio::test]
async fn status_keeps_the_pending_restore_through_a_failed_journal_read() {
    let rig = rig_at(0, 0, FULL, saved_files());
    let journal = |value: Option<Option<Vec<u8>>>| *rig.hyperboost.journal.lock().unwrap() = value;
    for (read, pending) in [
        (Some(None), false),
        (None, false),
        (Some(Some(journal_in(RESTORE_PENDING))), true),
        (None, true),
        (Some(Some(relinquished())), false),
        (None, false),
    ] {
        journal(read.clone());
        assert_eq!(rig.members().await.1, pending, "{read:?}");
    }
    rig.stop().await;
}

/// The pad and the journal are read only on the host of the model whose
/// HyperBoost requests native serves. Without that identity the members
/// are what managed's absent ones decode to, Unknown and no pending restore,
/// whatever the probes would read.
#[tokio::test]
async fn status_reads_neither_probe_without_the_matched_host() {
    let rig = rig_at(0, 0, FULL, saved_files());
    *rig.hyperboost.journal.lock().unwrap() = Some(Some(journal_in(RESTORE_PENDING)));
    assert_eq!(rig.members().await, (json!(1), json!(true), json!([])));
    let reads = rig.hyperboost.status_reads.load(Ordering::Acquire);
    assert!(reads >= 2);
    // The injected source's unavailable device identity.
    rig.metadata.store(3, Ordering::Release);
    assert_eq!(rig.members().await, (json!(0), json!(false), json!([])));
    assert_eq!(rig.hyperboost.status_reads.load(Ordering::Acquire), reads);
    rig.stop().await;
}

/// Managed 4053-4104, by the journal managed left: no journal has nothing
/// to retry, and a journal in any state but RestorePending is not awaiting a
/// retry. A pending restore is managed's to finish, since native never
/// writes that journal, and so is a journal that cannot be read or names no
/// state. No answer sends a setter, appends a native row or reads firmware
/// for a write.
#[tokio::test]
async fn recovery_answers_from_the_managed_journal_and_writes_nothing() {
    for (journal, status, message) in [
        (Some(None), STORED, NO_RESTORE),
        (Some(Some(relinquished())), BLOCKED, NOT_AWAITING_RETRY),
        (
            Some(Some(journal_in(RESTORE_PENDING))),
            BLOCKED,
            JOURNAL_PENDING,
        ),
        (Some(Some(journal_in(0))), BLOCKED, NOT_AWAITING_RETRY),
        (Some(Some(journal_in(1))), BLOCKED, NOT_AWAITING_RETRY),
        (Some(Some(journal_in(7))), BLOCKED, JOURNAL_PENDING),
        (Some(Some(b"{".to_vec())), BLOCKED, JOURNAL_PENDING),
        (None, BLOCKED, JOURNAL_PENDING),
    ] {
        // From HyperBoost and from Balanced: the answer reads no firmware
        // state, so neither changes it.
        for from in [7, 0] {
            let rig = rig_at(from, 0, FULL, saved_files());
            *rig.hyperboost.journal.lock().unwrap() = journal.clone();
            let before = untouched(&rig);
            let result = rig.recover(1).await;
            let case = format!("{journal:?} from {from}: {result}");
            assert_eq!(
                result["result"],
                json!({"status":status,"message":message}),
                "{case}"
            );
            assert_eq!(untouched(&rig), before, "{case}");
            assert_eq!(rig.hyperboost.journal_reads.load(Ordering::Acquire), 1);
            assert_eq!(rig.hyperboost.observations.load(Ordering::Acquire), 0);
            // Production order: SCM STOP revokes the origin before the drain.
            rig.origin.stop();
            rig.stop().await;
        }
    }
}

/// The two replies native gives as managed's are managed's text.
#[test]
fn recovery_replies_are_managed_literals() {
    let managed = std::fs::read_to_string(concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/../../src/OpenBlade.Service/ServiceControlRuntime.cs"
    ))
    .unwrap();
    for reply in [NO_RESTORE, NOT_AWAITING_RETRY] {
        assert!(managed.contains(&format!("\"{reply}\"")), "{reply}");
    }
}

/// Only the active interactive user is answered: the request reads no
/// journal for anyone else. (The status the dispatcher takes before every
/// mutation has read it, for any caller.) The dispatcher drops such a
/// caller's reply, so this asks the source as the dispatcher does and reads
/// the runtime's own answer.
#[tokio::test]
async fn recovery_refuses_a_caller_that_is_not_the_active_user() {
    for (case, caller) in [
        Caller {
            logon: Logon::System,
            ..USER
        },
        Caller {
            session: 8,
            control_session: Some(7),
            ..USER
        },
        Caller {
            control_session: None,
            ..USER
        },
    ]
    .into_iter()
    .enumerate()
    {
        let rig = rig_at(0, 0, FULL, saved_files());
        // The dispatcher publishes a status before it serves a request.
        rig.get().await;
        // A pending restore would otherwise be named.
        *rig.hyperboost.journal.lock().unwrap() = Some(Some(journal_in(RESTORE_PENDING)));
        let before = untouched(&rig);
        let request = crate::service_status::StatusRequest::decode(
            br#"{"operation":"recoverHyperBoostRestore","version":90,"requestId":"aaaaaaaa-bbbb-cccc-ffff-000000000001"}"#,
        )
        .unwrap();
        let mutation = rig.source.lock().await.mutate(
            caller,
            SID.into(),
            request,
            tokio::time::Instant::now() + Duration::from_secs(3),
        );
        let (outcome, status) = mutation.await.unwrap();
        assert_eq!(
            (outcome.status, outcome.message.as_str()),
            (BLOCKED, ACTIVE_USER),
            "{case}"
        );
        assert!(status.is_none(), "{case}");
        assert_eq!(untouched(&rig), before, "{case}");
        assert_eq!(rig.hyperboost.journal_reads.load(Ordering::Acquire), 0);
        rig.stop().await;
    }
}

/// A setter that may have been submitted leaves its row unsettled, and the
/// recovery request is no way around that fence: it has no restore of its
/// own to retry, so it sends nothing and settles nothing, in this process
/// and after a restart. The entry and the exit stay refused.
#[tokio::test]
async fn recovery_replays_nothing_over_a_possible_submission() {
    // An owned entry whose second enable setter the pad stopped.
    let rig = rig_at(0, 0, FULL, saved_files());
    rig.hyperboost
        .script
        .lock()
        .unwrap()
        .extend([Pad::Absent, Pad::Absent, Pad::Present]);
    let entry = rig.hyperboost(1, true).await;
    assert_eq!(entry["result"]["message"], PENDING, "{entry}");
    assert_eq!(progress(&rig), [0, 1, 2]);
    let before = untouched(&rig);
    assert_eq!(before.0, 1);

    let result = rig.recover(2).await;
    assert_eq!(
        result["result"],
        json!({"status":STORED,"message":NO_RESTORE}),
        "{result}"
    );
    assert_eq!(untouched(&rig), before);
    assert_eq!(progress(&rig), [0, 1, 2]);
    // The fence stands: nothing the recovery did admits a later write.
    let retry = rig.hyperboost(3, false).await;
    assert_eq!(retry["result"], json!({"status":2,"message":PENDING}));
    assert_eq!(untouched(&rig), before);
    rig.origin.stop();
    rig.stop().await;

    let restarted = Rig::restarted(rig.files.clone());
    let sent = setters(&restarted).len();
    let result = restarted.recover(4).await;
    assert_eq!(
        result["result"],
        json!({"status":STORED,"message":NO_RESTORE}),
        "{result}"
    );
    assert_eq!(setters(&restarted).len(), sent);
    assert_eq!(progress(&restarted), [0, 1, 2]);
    assert!(
        Storage::open(Box::new(restarted.files.clone()), SID)
            .unwrap()
            .pending()
    );
    restarted.origin.stop();
    restarted.stop().await;
}
