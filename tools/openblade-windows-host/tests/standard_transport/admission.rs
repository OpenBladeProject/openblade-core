use crate::{
    adapter_power::{
        AdmissionFailure as Denied, FixtureSource, HandleIdentity, OpenedHandle, QueryFailure,
        io::{Direction, FeatureIo, Frame, Limit, Stop, Transfer},
        query_once,
        session::{AdapterPowerLevels, Failure, ReplyFailure},
    },
    registry_identity::RegistryIdentity,
    replay::{frame, limit},
};
use openblade_types::ProtocolValidationFailure;
use std::{
    cell::Cell,
    collections::VecDeque,
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicUsize, Ordering},
    },
    time::{Duration, Instant},
};

const EXACT: HandleIdentity = HandleIdentity {
    vendor: 0x1532,
    product: 0x02e0,
    usage_page: 1,
    usage: 2,
    feature_bytes: 91,
};

type RegistryPair = (Option<&'static str>, Option<&'static str>);
const EXACT_HOST: RegistryPair = (Some("Razer"), Some("Blade 16 - RZ09-0581"));

#[derive(Default)]
struct Observations {
    opens: AtomicUsize,
    drops: AtomicUsize,
    sets: AtomicUsize,
    gets: AtomicUsize,
}

struct FakeHandle {
    identities: Cell<[Option<HandleIdentity>; 2]>,
    observed: Arc<Observations>,
    set_finished: Option<Instant>,
    reply_transaction: u8,
    stop_after_set: Option<Arc<AtomicBool>>,
}

impl OpenedHandle for FakeHandle {
    fn identity(&self) -> Option<HandleIdentity> {
        let [first, next] = self.identities.replace([None, None]);
        self.identities.set([next, None]);
        first
    }
}

impl FeatureIo for FakeHandle {
    fn transfer(
        &mut self,
        direction: Direction,
        buffer: &mut Frame,
        budget: Limit<'_>,
    ) -> Transfer {
        if let Some(reason) = budget.stop() {
            return Transfer::BeforeSubmission(reason);
        }
        match direction {
            Direction::Set => {
                assert_eq!(*buffer, frame(0, 1, [0, 0]));
                assert!(self.set_finished.replace(Instant::now()).is_none());
                self.observed.sets.fetch_add(1, Ordering::Relaxed);
                if let Some(cancel) = &self.stop_after_set {
                    cancel.store(true, Ordering::Release);
                }
                Transfer::Completed(Ok(0))
            }
            Direction::Get => {
                assert_eq!(*buffer, [0; 91]);
                assert!(self.set_finished.unwrap().elapsed() >= Duration::from_millis(30));
                self.observed.gets.fetch_add(1, Ordering::Relaxed);
                *buffer = frame(2, self.reply_transaction, [0x11, 0x11]);
                Transfer::Completed(Ok(91))
            }
        }
    }
}

impl Drop for FakeHandle {
    fn drop(&mut self) {
        self.observed.drops.fetch_add(1, Ordering::Relaxed);
    }
}

struct FakeSource {
    hosts: VecDeque<(bool, RegistryPair, RegistryPair)>,
    authority: VecDeque<Result<(), Denied>>,
    candidates: Option<Vec<FakeHandle>>,
    observed: Arc<Observations>,
    cancel_on_final_check: Option<Arc<AtomicBool>>,
}

impl FixtureSource for FakeSource {
    type Handle = FakeHandle;

    fn host(&mut self) -> (bool, RegistryIdentity<'_>) {
        let (supported, bios, system) = self.hosts.pop_front().expect("unexpected host read");
        (supported, RegistryIdentity { bios, system })
    }

    fn check_authority(&mut self) -> Result<(), Denied> {
        let result = self
            .authority
            .pop_front()
            .expect("unexpected authority check");
        if self.authority.is_empty()
            && let Some(cancel) = &self.cancel_on_final_check
        {
            cancel.store(true, Ordering::Release);
        }
        result
    }

    fn open_candidates(&mut self, _: Limit<'_>) -> Result<Vec<FakeHandle>, Denied> {
        assert_eq!(self.observed.opens.fetch_add(1, Ordering::Relaxed), 0);
        self.candidates.take().ok_or(Denied::CandidatesUnavailable)
    }
}

fn fake(identities: impl IntoIterator<Item = [Option<HandleIdentity>; 2]>) -> FakeSource {
    let observed = Arc::new(Observations::default());
    let candidates = identities
        .into_iter()
        .map(|identities| FakeHandle {
            identities: Cell::new(identities),
            observed: observed.clone(),
            set_finished: None,
            reply_transaction: 1,
            stop_after_set: None,
        })
        .collect();
    FakeSource {
        hosts: [(true, EXACT_HOST, EXACT_HOST); 2].into(),
        authority: [Ok(()); 2].into(),
        candidates: Some(candidates),
        observed,
        cancel_on_final_check: None,
    }
}

fn assert_denied(source: FakeSource, expected: Denied, opens: usize) {
    let observed = source.observed.clone();
    let candidates = source.candidates.as_ref().map_or(0, Vec::len);
    assert_eq!(
        query_once(source, limit(&AtomicBool::new(false))),
        Err(QueryFailure::Admission(expected))
    );
    assert_eq!(observed.opens.load(Ordering::Relaxed), opens);
    assert_eq!(observed.drops.load(Ordering::Relaxed), candidates);
    assert_eq!(observed.sets.load(Ordering::Relaxed), 0);
    assert_eq!(observed.gets.load(Ordering::Relaxed), 0);
}

#[test]
fn requires_supported_windows_and_both_ordinal_registry_values() {
    for host in [
        (false, EXACT_HOST, EXACT_HOST),
        (true, EXACT_HOST, (Some("Razer"), None)),
        (true, EXACT_HOST, (Some("RAZER"), EXACT_HOST.1)),
        (
            true,
            EXACT_HOST,
            (Some("Razer"), Some("Blade 16 - RZ09-0528")),
        ),
        (
            true,
            (Some("Razer"), Some("Blade 16")),
            (Some("Razer"), Some("Blade 16")),
        ),
    ] {
        let mut source = fake([[Some(EXACT); 2]]);
        source.hosts[0] = host;
        assert_denied(source, Denied::Host, 0);
    }
    let mut source = fake([[Some(EXACT); 2]]);
    source.hosts[1].0 = false;
    assert_denied(source, Denied::Host, 1);
}

#[test]
fn checks_execution_owner_and_controllers_before_and_after_the_scan() {
    for denial in [
        Denied::ExecutionBlocked,
        Denied::ControllerConflict,
        Denied::ControllerDetectionFailed,
        Denied::OwnershipUnavailable,
    ] {
        for stage in 0..2 {
            let mut source = fake([[Some(EXACT); 2]]);
            source.authority[stage] = Err(denial);
            assert_denied(source, denial, stage);
        }
    }
}

#[test]
fn requires_exact_opened_attributes_collection_geometry_and_cardinality() {
    for mismatch in [
        HandleIdentity {
            vendor: 0x1533,
            ..EXACT
        },
        HandleIdentity {
            product: 0x02c6,
            ..EXACT
        },
        HandleIdentity {
            usage_page: 0xff00,
            ..EXACT
        },
        HandleIdentity { usage: 1, ..EXACT },
        HandleIdentity {
            feature_bytes: 90,
            ..EXACT
        },
        HandleIdentity {
            feature_bytes: 92,
            ..EXACT
        },
    ] {
        assert_denied(fake([[Some(mismatch); 2]]), Denied::NoExactHandle, 1);
    }
    assert_denied(fake([]), Denied::NoExactHandle, 1);
    assert_denied(fake([[Some(EXACT); 2]; 2]), Denied::MultipleExactHandles, 1);
    assert_denied(
        fake([[Some(EXACT); 2], [None; 2]]),
        Denied::HandleIdentityUnavailable,
        1,
    );

    let mut source = fake([]);
    source.candidates = None;
    assert_denied(source, Denied::CandidatesUnavailable, 1);
}

#[test]
fn refuses_stale_or_disappearing_handle_evidence() {
    assert_denied(
        fake([[Some(EXACT), Some(HandleIdentity { usage: 1, ..EXACT })]]),
        Denied::SelectedHandleChanged,
        1,
    );
    assert_denied(
        fake([[Some(EXACT), None]]),
        Denied::HandleIdentityUnavailable,
        1,
    );
}

#[test]
fn queries_only_the_selected_handle_with_fixed_transaction_and_pacing() {
    let source = fake([
        [Some(HandleIdentity { usage: 1, ..EXACT }); 2],
        [Some(EXACT); 2],
    ]);
    let observed = source.observed.clone();
    assert_eq!(
        query_once(source, limit(&AtomicBool::new(false))),
        Ok(AdapterPowerLevels {
            actual: 0x11,
            recommended: 0x11
        })
    );
    assert_eq!(observed.sets.load(Ordering::Relaxed), 1);
    assert_eq!(observed.gets.load(Ordering::Relaxed), 1);
    assert_eq!(observed.drops.load(Ordering::Relaxed), 2);
}

#[test]
fn late_cancellation_blocks_submission_and_closes_the_handle() {
    let mut source = fake([[Some(EXACT); 2]]);
    let cancel = Arc::new(AtomicBool::new(false));
    source.cancel_on_final_check = Some(cancel.clone());
    let observed = source.observed.clone();
    assert_eq!(
        query_once(source, limit(&cancel)),
        Err(QueryFailure::Admission(Denied::Stopped(Stop::Caller)))
    );
    assert_eq!(observed.sets.load(Ordering::Relaxed), 0);
    assert_eq!(observed.drops.load(Ordering::Relaxed), 1);
}

#[test]
fn cancellation_after_set_retires_the_handle_without_get_or_replay() {
    let mut source = fake([[Some(EXACT); 2]]);
    let cancel = Arc::new(AtomicBool::new(false));
    source.candidates.as_mut().unwrap()[0].stop_after_set = Some(cancel.clone());
    let observed = source.observed.clone();
    assert_eq!(
        query_once(source, limit(&cancel)),
        Err(QueryFailure::Exchange(Failure::UncertainIo(
            Direction::Set,
            Transfer::CompletedAfterStop {
                reason: Stop::Caller,
                completion: Ok(0)
            },
        )))
    );
    assert_eq!(observed.opens.load(Ordering::Relaxed), 1);
    assert_eq!(observed.sets.load(Ordering::Relaxed), 1);
    assert_eq!(observed.gets.load(Ordering::Relaxed), 0);
    assert_eq!(observed.drops.load(Ordering::Relaxed), 1);
}

#[test]
fn independent_response_transaction_mismatch_retires_the_handle() {
    let mut source = fake([[Some(EXACT); 2]]);
    source.candidates.as_mut().unwrap()[0].reply_transaction = 2;
    let observed = source.observed.clone();
    assert_eq!(
        query_once(source, limit(&AtomicBool::new(false))),
        Err(QueryFailure::Exchange(Failure::UncertainProtocol {
            count: 91,
            failure: ReplyFailure::Protocol(ProtocolValidationFailure::TransactionMismatch),
        }))
    );
    assert_eq!(observed.sets.load(Ordering::Relaxed), 1);
    assert_eq!(observed.gets.load(Ordering::Relaxed), 1);
    assert_eq!(observed.drops.load(Ordering::Relaxed), 1);
}
