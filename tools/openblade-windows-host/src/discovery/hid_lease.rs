//! Opened-handle metadata and lifetime only. Feature I/O and service admission are separate.
#![cfg_attr(not(test), allow(dead_code))] // Compiled for review without an entry point.

use super::{CandidateHint, DiscoverySource, Inventory, check_cancelled, discover_hid};
use std::{io, sync::atomic::AtomicBool};

#[cfg(all(
    windows,
    feature = "hid-identity-compile-check",
    any(not(test), feature = "firmware-performance-compile-check")
))]
mod windows;

#[cfg(any(test, feature = "firmware-performance-compile-check"))]
pub(crate) mod performance;

mod owner;
#[cfg(test)]
pub(super) mod tests;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Descriptor {
    device: (u16, u16),
    usage: (u16, u16),
    feature_bytes: u16,
    version: u16,
}

trait LeaseSource: DiscoverySource {
    type Handle;

    fn open_metadata(&self, path: &str) -> io::Result<Self::Handle>;
    // Read from the retained handle and fail if its removal was observed.
    fn describe(&self, handle: &Self::Handle) -> io::Result<Descriptor>;
}

struct Selection {
    model: String,
    device: (u16, u16),
    usage: Option<(u16, u16)>,
    hint: CandidateHint,
}

impl Selection {
    fn open<S: LeaseSource>(source: &S, cancelled: &AtomicBool) -> io::Result<(Self, S::Handle)> {
        let selection = Self::fresh(source, cancelled)?;
        check_cancelled(cancelled)?;
        let handle = source.open_metadata(&selection.hint.path)?;
        Ok((selection, handle))
    }

    fn fresh(source: &impl DiscoverySource, cancelled: &AtomicBool) -> io::Result<Self> {
        check_cancelled(cancelled)?;
        let inventory: Inventory<'static> = Inventory::load()?;
        let host = source.read_host(cancelled)?;
        let (_, model) = host.resolve(&inventory);
        let model = model.ok_or_else(|| blocked("no exact Windows host model"))?;
        let device = model
            .device_id()
            .ok_or_else(|| blocked("no exact HID device ID"))?;
        let (_, hint) = discover_hid(source, Some(model), cancelled)?;
        Ok(Self {
            model: model.model_number.to_owned(),
            device,
            usage: model.windows_control_usage,
            hint: hint.ok_or_else(|| blocked("no unique, complete HID candidate"))?,
        })
    }

    fn matches(&self, descriptor: Descriptor) -> bool {
        // Managed physical selectors pin usage; source-reviewed selectors
        // require functional admission separately and do not prescribe usage.
        descriptor.device == self.device
            && descriptor.feature_bytes == 91
            && self.usage.is_none_or(|usage| descriptor.usage == usage)
            && self
                .hint
                .facts
                .usage
                .is_none_or(|usage| descriptor.usage == usage)
    }

    fn same_candidate(&self, other: &Self) -> bool {
        self.model == other.model
            && self.hint.path.eq_ignore_ascii_case(&other.hint.path)
            && self.hint.facts == other.hint.facts
    }
}

struct LeaseIdentity {
    selection: Selection,
    descriptor: Descriptor,
}

impl LeaseIdentity {
    fn verify<S: LeaseSource>(
        selection: Selection,
        source: &S,
        handle: &S::Handle,
        cancelled: &AtomicBool,
    ) -> io::Result<Self> {
        check_cancelled(cancelled)?;
        let descriptor = source.describe(handle)?;
        if !selection.matches(descriptor) {
            return Err(blocked("opened HID metadata contradicts the candidate"));
        }
        let identity = Self {
            selection,
            descriptor,
        };
        // Recheck the same handle after a fresh scan to reject opening races.
        identity.revalidate(source, handle, cancelled)?;
        Ok(identity)
    }

    fn revalidate<S: LeaseSource>(
        &self,
        source: &S,
        handle: &S::Handle,
        cancelled: &AtomicBool,
    ) -> io::Result<()> {
        let fresh = Selection::fresh(source, cancelled)?;
        if !self.selection.same_candidate(&fresh) {
            return Err(blocked("HID candidate changed"));
        }
        if source.describe(handle)? != self.descriptor {
            return Err(blocked("opened HID metadata changed"));
        }
        check_cancelled(cancelled)
    }
}

fn blocked(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, message)
}
