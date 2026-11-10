//! Development contracts for the Windows host.

pub mod configuration;
pub mod cpu_worker;

#[cfg(windows)]
pub fn valid_dev_id(id: &str) -> bool {
    id.len() == 32
        && id
            .bytes()
            .all(|byte| matches!(byte, b'0'..=b'9' | b'a'..=b'f'))
}

#[cfg(windows)]
pub mod configuration_writer;

#[cfg(windows)]
pub mod fan_stop_entry;
#[cfg(windows)]
pub mod fan_stop_guardian;

#[cfg(windows)]
pub mod pipe;

#[cfg(windows)]
pub mod reporter_pipe;

#[cfg(windows)]
mod windows_protected_path;
#[cfg(windows)]
mod windows_token;
