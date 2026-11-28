//! Development contracts for the Windows host.

pub mod configuration;
pub mod cpu_worker;
pub mod json_object;

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

#[cfg(windows)]
#[allow(dead_code)] // The library never retains a monitor; the Session Agent's Service client does.
mod pipe_server_identity;
#[cfg(windows)]
mod retained_process;

#[cfg(windows)]
mod native_package;
#[cfg(windows)]
#[allow(dead_code)]
mod service_install_identity;

#[cfg(windows)]
mod native_owner_record;
