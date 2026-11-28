//! The managed `ProfileSet` contract for the native Windows Service: the
//! typed model, the `System.Text.Json` codec, the validator and the stored
//! document readers. Pure data rules only; storage, device admission and
//! IPC dispatch stay in the host.
//!
//! Managed C# is the reference. Every rule here is pinned against it by the
//! shared vectors under `tools/openblade-windows-host/tests/data/profile-*`.

pub mod application;
pub mod decode;
pub mod document;
pub mod encode;
pub mod json;
pub mod lighting;
pub mod migrate;
pub mod model;
pub mod normalize;
mod ordinal_case;
pub mod policy;
pub mod preferences;
pub mod resolve;
pub mod text;
pub mod update;
pub mod validate;

pub use decode::Dialect;
pub use document::{CURRENT_SCHEMA, StoredProfiles, read_profile_document};
pub use json::{Json, Malformed};
pub use migrate::{Migration, Refusal, Stored, migrate};
pub use model::*;
pub use normalize::Invalid;
pub use policy::{Capabilities, ValidationPolicy};
pub use preferences::{Aggregate, Preferences, read_aggregate};
pub use validate::{SamePath, Validator};
