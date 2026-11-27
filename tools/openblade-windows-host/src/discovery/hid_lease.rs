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

/// A HID collection of the exact device beside its control role, selected
/// as managed `ProductionLightingFactory` selects the matrix interface
/// (`IsExactMatrixInterface`): among the device's candidates with the role's
/// usage hint, exactly one whose opened metadata carries the role's usage
/// and feature report length.
#[cfg(any(test, feature = "service-lighting-v1"))]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Role {
    usage: (u16, u16),
    feature_bytes: u16,
}

/// The exact host model, its device and every role candidate of one scan.
#[cfg(any(test, feature = "service-lighting-v1"))]
struct RoleCandidates {
    model: String,
    device: (u16, u16),
    hints: Vec<CandidateHint>,
}

#[cfg(any(test, feature = "service-lighting-v1"))]
impl RoleCandidates {
    fn fresh(
        source: &impl DiscoverySource,
        role: Role,
        cancelled: &AtomicBool,
    ) -> io::Result<Self> {
        check_cancelled(cancelled)?;
        let inventory: Inventory<'static> = Inventory::load()?;
        let host = source.read_host(cancelled)?;
        let (_, model) = host.resolve(&inventory);
        let model = model.ok_or_else(|| blocked("no exact Windows host model"))?;
        let device = model
            .device_id()
            .ok_or_else(|| blocked("no exact HID device ID"))?;
        let hints = super::discover_role(source, model, role.usage, cancelled)?;
        Ok(Self {
            model: model.model_number.to_owned(),
            device,
            hints,
        })
    }

    fn same(&self, other: &Self) -> bool {
        self.model == other.model
            && self.device == other.device
            && self.hints.len() == other.hints.len()
            && self.hints.iter().zip(&other.hints).all(|(left, right)| {
                left.path.eq_ignore_ascii_case(&right.path) && left.facts == right.facts
            })
    }
}

/// The one exact role collection, with the candidates it was chosen from.
#[cfg(any(test, feature = "service-lighting-v1"))]
struct RoleIdentity {
    candidates: RoleCandidates,
    path: String,
    descriptor: Descriptor,
}

#[cfg(any(test, feature = "service-lighting-v1"))]
impl RoleIdentity {
    /// Describes each candidate through its own metadata handle, released
    /// before the next, and requires exactly one exact descriptor. Any open
    /// or metadata failure refuses: an unreadable candidate could be the
    /// exact one.
    fn select<S: LeaseSource>(source: &S, role: Role, cancelled: &AtomicBool) -> io::Result<Self> {
        let candidates = RoleCandidates::fresh(source, role, cancelled)?;
        let mut exact = Vec::new();
        for hint in &candidates.hints {
            check_cancelled(cancelled)?;
            let handle = source.open_metadata(&hint.path)?;
            let descriptor = source.describe(&handle)?;
            drop(handle);
            if descriptor.device == candidates.device
                && descriptor.usage == role.usage
                && descriptor.feature_bytes == role.feature_bytes
            {
                exact.push((hint.path.clone(), descriptor));
            }
        }
        let [(path, descriptor)] = <[_; 1]>::try_from(exact)
            .map_err(|_| blocked("no unique exact HID role collection"))?;
        check_cancelled(cancelled)?;
        Ok(Self {
            candidates,
            path,
            descriptor,
        })
    }

    fn model(&self) -> &str {
        &self.candidates.model
    }

    fn path(&self) -> &str {
        &self.path
    }

    fn descriptor(&self) -> Descriptor {
        self.descriptor
    }

    /// The opened handle still shows the selected descriptor, and a fresh
    /// scan finds the same model and candidates: a removed, replaced or added
    /// collection refuses.
    fn verify<S: LeaseSource>(
        &self,
        source: &S,
        handle: &S::Handle,
        cancelled: &AtomicBool,
    ) -> io::Result<()> {
        check_cancelled(cancelled)?;
        if source.describe(handle)? != self.descriptor {
            return Err(blocked("opened HID role metadata changed"));
        }
        let fresh = RoleCandidates::fresh(
            source,
            Role {
                usage: self.descriptor.usage,
                feature_bytes: self.descriptor.feature_bytes,
            },
            cancelled,
        )?;
        if !self.candidates.same(&fresh) {
            return Err(blocked("HID role candidates changed"));
        }
        check_cancelled(cancelled)
    }
}
fn blocked(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, message)
}
