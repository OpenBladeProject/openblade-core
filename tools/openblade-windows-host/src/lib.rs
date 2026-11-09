//! Pure development contracts for the future Windows host.

pub mod configuration;

#[cfg(all(test, windows))]
mod configuration_writer;
