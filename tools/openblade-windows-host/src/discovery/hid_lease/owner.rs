//! Serialized ownership for the development service's read-only HID lease.

use super::{LeaseIdentity, LeaseSource, Selection, blocked};
use std::{
    io,
    sync::{Arc, atomic::AtomicBool},
    task::Poll,
};

pub(super) trait ServiceLeaseSource: LeaseSource {
    // Pending keeps the same handle fenced. An error leaves retirement
    // uncertain and must never be treated as permission to retry or reopen.
    fn poll_retirement(&self, handle: &mut Self::Handle) -> Poll<io::Result<()>>;
}

// An owned token avoids reuse across acquisitions and across owner instances,
// even if the next handle has identical path and descriptor metadata.
#[derive(Clone)]
pub(super) struct Epoch(Arc<()>);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Retirement {
    Complete,
    Pending,
    Uncertain,
}

enum Slot<H> {
    Vacant,
    Active {
        epoch: Epoch,
        identity: LeaseIdentity,
        handle: H,
    },
    Retiring {
        handle: H,
        uncertain: bool,
    },
}

/// Keep this owner in one service driver. Feed it only serialized events and
/// keep it alive through stop/drain. A successful check is passive identity
/// evidence scoped to this epoch and confers no mutation authority.
pub(super) struct HidLeaseOwner<S: ServiceLeaseSource> {
    source: S,
    slot: Slot<S::Handle>,
    stopped: bool,
}

impl<S: ServiceLeaseSource> HidLeaseOwner<S> {
    pub(super) fn new(source: S) -> Self {
        Self {
            source,
            slot: Slot::Vacant,
            stopped: false,
        }
    }

    pub(super) fn acquire(&mut self, cancelled: &AtomicBool) -> io::Result<Epoch> {
        if self.stopped || !matches!(self.slot, Slot::Vacant) {
            return Err(blocked("HID lease owner is occupied or stopped"));
        }

        let (selection, handle) = Selection::open(&self.source, cancelled)?;
        match LeaseIdentity::verify(selection, &self.source, &handle, cancelled) {
            Ok(identity) => {
                let epoch = Epoch(Arc::new(()));
                self.slot = Slot::Active {
                    epoch: epoch.clone(),
                    identity,
                    handle,
                };
                Ok(epoch)
            }
            Err(error) => {
                // An open can fail admission after obtaining a handle. That
                // handle must pass the same retirement fence as an active one.
                self.retire(handle);
                Err(error)
            }
        }
    }

    pub(super) fn revalidate(&mut self, epoch: &Epoch, cancelled: &AtomicBool) -> io::Result<()> {
        let result = match &self.slot {
            Slot::Active {
                epoch: current,
                identity,
                handle,
            } if Arc::ptr_eq(&current.0, &epoch.0) => {
                identity.revalidate(&self.source, handle, cancelled)
            }
            _ => return Err(blocked("stale HID lease epoch")),
        };
        if result.is_err() {
            self.retire_active();
        }
        result
    }

    // Removal and lifecycle callbacks must carry the epoch captured when
    // their work was registered. A stale callback cannot retire its successor.
    pub(super) fn invalidate(&mut self, epoch: &Epoch) -> Option<Retirement> {
        if matches!(&self.slot, Slot::Active { epoch: current, .. }
            if Arc::ptr_eq(&current.0, &epoch.0))
        {
            self.retire_active();
            return Some(self.retirement());
        }
        None
    }

    pub(super) fn stop(&mut self) -> Retirement {
        self.stopped = true;
        self.retire_active();
        self.retirement()
    }

    pub(super) fn retirement(&self) -> Retirement {
        match self.slot {
            Slot::Retiring {
                uncertain: true, ..
            } => Retirement::Uncertain,
            Slot::Retiring {
                uncertain: false, ..
            } => Retirement::Pending,
            _ => Retirement::Complete,
        }
    }

    // Called by the serialized driver to observe drain progress on this slot.
    // It is independent of the ordinary work cancellation token.
    pub(super) fn poll_retirement(&mut self) -> Retirement {
        if let Slot::Retiring { handle, uncertain } = &mut self.slot
            && !*uncertain
        {
            match self.source.poll_retirement(handle) {
                Poll::Ready(Ok(())) => self.slot = Slot::Vacant,
                Poll::Pending => {}
                Poll::Ready(Err(_)) => *uncertain = true,
            }
        }
        self.retirement()
    }

    fn retire_active(&mut self) {
        match std::mem::replace(&mut self.slot, Slot::Vacant) {
            Slot::Active { handle, .. } => self.retire(handle),
            other => self.slot = other,
        }
    }

    fn retire(&mut self, handle: S::Handle) {
        self.slot = Slot::Retiring {
            handle,
            uncertain: false,
        };
        self.poll_retirement();
    }
}
