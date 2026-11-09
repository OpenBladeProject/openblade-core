//! Development contracts for the Windows host.

pub mod configuration;

#[cfg(windows)]
pub fn valid_dev_id(id: &str) -> bool {
    id.len() == 32
        && id
            .bytes()
            .all(|byte| matches!(byte, b'0'..=b'9' | b'a'..=b'f'))
}

#[cfg(windows)]
pub mod configuration_writer;
