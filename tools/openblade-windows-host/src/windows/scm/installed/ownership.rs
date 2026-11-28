//! Independent Service-generation and sole-owner evidence for firmware observation.

use std::{
    io,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU8, Ordering},
    },
};

use super::denied;
#[cfg(any(not(test), feature = "service-mailbox-observation-v1"))]
#[path = "durable_owner.rs"]
pub(in crate::installed) mod durable;
use crate::discovery::performance::Admission;

// Minted by an independently verified loaded-generation source. Keep its
// retained process/image evidence alive in Authority/OwnerLease. PID, disk
// receipts and metadata handles cannot mint this token. There is deliberately
// no installed implementation or public constructor until that proof exists.
#[derive(Clone)]
pub(in crate::installed) struct AuthorityEpoch {
    pub(super) instance: Arc<()>,
    pub(super) launch_pid: u32,
}

impl PartialEq for AuthorityEpoch {
    fn eq(&self, other: &Self) -> bool {
        self.launch_pid == other.launch_pid && Arc::ptr_eq(&self.instance, &other.instance)
    }
}

#[allow(dead_code)] // Installed independent evidence has no production source yet.
pub(in crate::installed) enum Ownership {
    Acquired(AuthorityEpoch, Admission),
    Lost,
    Releasing,
    Retired(AuthorityEpoch),
}

// This is a trusted internal evidence port, never request data. Implementors
// must retain the original loaded process/image binding, independently confirm
// predecessor retirement and serialize all owners across replacement/rollback.
// A process-local mutex or controller absence cannot implement this contract.
// Failed acquisition must preserve any predecessor or partial acquisition
// fence at the authority source, including when its caller goes away.
pub(in crate::installed) trait Authority: Send + Sync {
    fn loaded_generation(&self) -> io::Result<AuthorityEpoch>;
    fn acquire(&self, epoch: &AuthorityEpoch) -> io::Result<Arc<dyn OwnerLease>>;
}

pub(in crate::installed) trait OwnerLease: Send + Sync {
    // Fresh exact-device, active session, controller, full-adapter and awake
    // evidence must share the owner epoch. Loss must be sticky at the source,
    // even if state returns to its previous value between observations.
    fn observe(&self) -> Ownership;
    // Called only after the firmware worker has confirmed native retirement.
    // Releasing/error retains the old owner fence and all original evidence.
    // Repeated calls may only poll the same release attempt, never replay it.
    fn release(&self) -> io::Result<Ownership>;
}

const ACQUIRED: u8 = 0;
const LOST: u8 = 1;
const RETIRED: u8 = 2;
const QUARANTINED: u8 = 3;

pub(in crate::installed) struct Binding {
    lease: Option<Arc<dyn OwnerLease>>,
    epoch: AuthorityEpoch,
    admission: Admission,
    state: AtomicU8,
    package: Mutex<Option<crate::candidate_receipt::CommittedServiceImage>>,
}

impl Binding {
    pub(in crate::installed) fn acquire(
        launch_pid: u32,
        authority: Option<&dyn Authority>,
    ) -> io::Result<Arc<Self>> {
        let authority =
            authority.ok_or_else(|| denied("independent Service authority is unavailable"))?;
        let epoch = authority.loaded_generation()?;
        if launch_pid == 0 || epoch.launch_pid != launch_pid {
            return Err(denied("loaded Service authority belongs to another launch"));
        }
        let lease = authority.acquire(&epoch)?;
        let observed = lease.observe();
        let valid = matches!(&observed, Ownership::Acquired(current, admission)
            if *current == epoch && admission.is_clear());
        if !valid {
            // Acquisition may have reserved ownership. Release through the same
            // evidence source, retaining its fence if retirement is unresolved.
            if !matches!(lease.release(), Ok(Ownership::Retired(current)) if current == epoch) {
                std::mem::forget(lease);
            }
            return Err(denied("sole HID owner acquisition is unconfirmed"));
        }
        let Ownership::Acquired(_, admission) = observed else {
            unreachable!()
        };
        Ok(Arc::new(Self {
            lease: Some(lease),
            epoch,
            admission,
            state: AtomicU8::new(ACQUIRED),
            package: Mutex::new(None),
        }))
    }

    pub(in crate::installed) fn retain_package(
        &self,
        package: crate::candidate_receipt::CommittedServiceImage,
    ) {
        let mut held = self.package.lock().unwrap_or_else(|e| e.into_inner());
        assert!(
            held.is_none(),
            "a native binding cannot upgrade its original package"
        );
        *held = Some(package);
    }

    pub(in crate::installed) fn authorize(&self) -> Option<Admission> {
        if self.state.load(Ordering::Acquire) != ACQUIRED {
            return None;
        }
        let package_current = self.package.lock().ok().is_some_and(|mut held| {
            held.as_mut()
                .is_none_or(|package| package.require_current().is_ok())
        });
        if !package_current {
            self.revoke();
            return None;
        }
        let current = self.lease.as_ref()?.observe();
        if matches!(current, Ownership::Acquired(epoch, admission)
            if epoch == self.epoch && admission == self.admission)
            && self.state.load(Ordering::Acquire) == ACQUIRED
        {
            Some(self.admission)
        } else {
            self.revoke();
            None
        }
    }

    pub(in crate::installed) fn revoke(&self) {
        let _ = self
            .state
            .compare_exchange(ACQUIRED, LOST, Ordering::AcqRel, Ordering::Acquire);
    }

    pub(in crate::installed) fn retire(&self) -> io::Result<()> {
        self.revoke();
        if self.state.load(Ordering::Acquire) == RETIRED {
            return Ok(());
        }
        if self.state.load(Ordering::Acquire) == QUARANTINED {
            return Err(denied("sole HID owner release remains quarantined"));
        }
        match self.lease.as_ref().expect("retained owner lease").release() {
            Ok(Ownership::Retired(epoch)) if epoch == self.epoch => {
                self.state.store(RETIRED, Ordering::Release);
                Ok(())
            }
            Ok(Ownership::Releasing) => Err(denied("sole HID owner retirement is pending")),
            result => {
                self.state.store(QUARANTINED, Ordering::Release);
                Err(result
                    .err()
                    .unwrap_or_else(|| denied("sole HID owner retirement is unconfirmed")))
            }
        }
    }
}

impl Drop for Binding {
    fn drop(&mut self) {
        if self.state.load(Ordering::Acquire) != RETIRED {
            // Dropping a source, a canceled drain, or a failed native cleanup
            // cannot release ownership. Preserve the source's original fence.
            std::mem::forget(self.lease.take());
            let held = self.package.get_mut().unwrap_or_else(|e| e.into_inner());
            std::mem::forget(held.take());
        }
    }
}

#[cfg(test)]
#[path = "ownership_tests.rs"]
mod tests;
