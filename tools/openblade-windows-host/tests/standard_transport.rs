//! Offline integration target. Neither developer executable links this adapter.

#[path = "../src/adapter_power/mod.rs"]
mod adapter_power;
#[path = "standard_transport/admission.rs"]
mod admission;
#[cfg(windows)]
#[path = "standard_transport/ownership.rs"]
mod ownership;
#[path = "../src/registry_identity.rs"]
mod registry_identity;
#[path = "standard_transport/replay.rs"]
mod replay;
