//! Opened-handle metadata and lifetime only. Feature I/O and service admission are separate.
#![cfg_attr(not(test), allow(dead_code))] // Compiled for review without an entry point.

use super::{CandidateHint, DiscoverySource, Inventory, check_cancelled, discover_hid};
use std::{io, sync::atomic::AtomicBool};

#[cfg(all(windows, feature = "hid-identity-compile-check", not(test)))]
mod windows;

#[cfg(test)]
mod tests;

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

struct Lease<'a, S: LeaseSource> {
    source: &'a S,
    selection: Selection,
    descriptor: Descriptor,
    handle: Option<S::Handle>,
}

impl<'a, S: LeaseSource> Lease<'a, S> {
    fn open(source: &'a S, cancelled: &AtomicBool) -> io::Result<Self> {
        let selection = Selection::fresh(source, cancelled)?;
        check_cancelled(cancelled)?;
        let handle = source.open_metadata(&selection.hint.path)?;
        check_cancelled(cancelled)?;
        let descriptor = source.describe(&handle)?;
        if !selection.matches(descriptor) {
            return Err(blocked("opened HID metadata contradicts the candidate"));
        }
        let mut lease = Self {
            source,
            selection,
            descriptor,
            handle: Some(handle),
        };
        // Scan again after opening, then recheck the same handle. An opening
        // race must not return a lease based on the earlier enumeration.
        lease.revalidate(cancelled)?;
        Ok(lease)
    }

    fn revalidate(&mut self, cancelled: &AtomicBool) -> io::Result<()> {
        // Keep the handle only on success. Any uncertainty retires it
        // immediately, and this lease can never reopen a replacement.
        let handle = self
            .handle
            .take()
            .ok_or_else(|| blocked("HID metadata lease was revoked"))?;
        let fresh = Selection::fresh(self.source, cancelled)?;
        if !self.selection.same_candidate(&fresh) {
            return Err(blocked("HID candidate changed"));
        }
        if self.source.describe(&handle)? != self.descriptor {
            return Err(blocked("opened HID metadata changed"));
        }
        check_cancelled(cancelled)?;
        self.handle = Some(handle);
        Ok(())
    }
}

fn blocked(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, message)
}
