//! Developer adapter fixture for the RZ09-0581 standard adapter-power query.
//! This module has no Windows device opener or native authority provider.

use crate::registry_identity::{RegistryIdentity, RegistryMatch};
use std::time::{Duration, Instant};

pub(crate) mod io;
pub(crate) mod session;
#[cfg(windows)]
#[allow(unsafe_code)]
mod windows;

use io::{FRAME_LENGTH, FeatureIo, Limit, Stop};
use session::{AdapterPowerLevels, Failure as ExchangeFailure, Session};

pub(super) const EXACT_PRODUCT: &str = "Blade 16 - RZ09-0581";
const SETTLE_DELAY: Duration = Duration::from_millis(30);
const EXCHANGE_TIMEOUT: Duration = Duration::from_secs(3);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct HandleIdentity {
    pub(crate) vendor: u16,
    pub(crate) product: u16,
    pub(crate) usage_page: u16,
    pub(crate) usage: u16,
    pub(crate) feature_bytes: u16,
}

pub(super) const CONTROL_COLLECTION: HandleIdentity = HandleIdentity {
    vendor: 0x1532,
    product: 0x02E0,
    usage_page: 1,
    usage: 2,
    feature_bytes: FRAME_LENGTH as u16,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum AdmissionFailure {
    Stopped(Stop),
    Host,
    ExecutionBlocked,
    ControllerConflict,
    ControllerDetectionFailed,
    OwnershipUnavailable,
    CandidatesUnavailable,
    HandleIdentityUnavailable,
    NoExactHandle,
    MultipleExactHandles,
    SelectedHandleChanged,
}

#[derive(Debug, PartialEq, Eq)]
pub(crate) enum QueryFailure {
    Admission(AdmissionFailure),
    Exchange(ExchangeFailure),
}

pub(crate) trait OpenedHandle: FeatureIo {
    /// Read attributes and capabilities from this very handle.
    fn identity(&self) -> Option<HandleIdentity>;
}

/// Owns the simulated exclusive lease until dropped. Only offline fixtures
/// implement this contract. An operational provider would need the handoff
/// and continuous ownership described in the runbook.
pub(crate) trait FixtureLease {
    type Handle: OpenedHandle;

    /// Must represent a fresh platform check and both registry sources.
    fn host(&mut self) -> (bool, RegistryIdentity<'_>);

    /// Check execution approval, competing controllers and the exclusive
    /// owner lease. A permissive or unavailable observation cannot proceed.
    fn check_authority(&mut self) -> Result<(), AdmissionFailure>;

    /// A complete fixture scan returning distinct owned handles. Unavailable
    /// enumeration or unreadable candidates cannot establish uniqueness.
    fn open_candidates(&mut self, limit: Limit<'_>) -> Result<Vec<Self::Handle>, AdmissionFailure>;
}

/// Consume one fixture lease for one typed query, closing the selected handle
/// before returning. There is no reopen or reconciliation path in this slice.
pub(crate) fn query_once(
    mut lease: impl FixtureLease,
    limit: Limit<'_>,
) -> Result<AdapterPowerLevels, QueryFailure> {
    let handle = select_handle(&mut lease, limit).map_err(QueryFailure::Admission)?;
    let limit = Limit {
        deadline: limit.deadline.min(Instant::now() + EXCHANGE_TIMEOUT),
        ..limit
    };
    let session = Session::new(handle, SETTLE_DELAY);
    let result = session
        .query_adapter_power(limit)
        .map_err(QueryFailure::Exchange);
    // Keep the lease through both terminal I/O drain and handle disposal.
    drop(session);
    result
}

fn select_handle<S: FixtureLease>(
    source: &mut S,
    limit: Limit<'_>,
) -> Result<S::Handle, AdmissionFailure> {
    check_host_and_authority(source, limit)?;

    let mut exact = None;
    for handle in source.open_candidates(limit)? {
        check_stop(limit)?;
        if read_identity(&handle)? != CONTROL_COLLECTION {
            continue;
        }
        if exact.is_some() {
            return Err(AdmissionFailure::MultipleExactHandles);
        }
        exact = Some(handle);
    }
    let handle = exact.ok_or(AdmissionFailure::NoExactHandle)?;

    // Refresh authority after the scan, then validate the same handle as the
    // last admission step. Cardinality is scoped to the complete scan above.
    check_host_and_authority(source, limit)?;
    check_stop(limit)?;
    if read_identity(&handle)? != CONTROL_COLLECTION {
        return Err(AdmissionFailure::SelectedHandleChanged);
    }
    Ok(handle)
}

fn check_host_and_authority(
    source: &mut impl FixtureLease,
    limit: Limit<'_>,
) -> Result<(), AdmissionFailure> {
    check_stop(limit)?;
    let (supported, registry) = source.host();
    if !supported
        || !matches!(
            registry.resolve(),
            RegistryMatch::RazerProduct(EXACT_PRODUCT)
        )
    {
        return Err(AdmissionFailure::Host);
    }
    check_stop(limit)?;
    source.check_authority()
}

fn read_identity(handle: &impl OpenedHandle) -> Result<HandleIdentity, AdmissionFailure> {
    handle
        .identity()
        .ok_or(AdmissionFailure::HandleIdentityUnavailable)
}

fn check_stop(limit: Limit<'_>) -> Result<(), AdmissionFailure> {
    limit
        .stop()
        .map_or(Ok(()), |reason| Err(AdmissionFailure::Stopped(reason)))
}
