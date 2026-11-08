use crate::wire::{self, Error};
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::{
    net::windows::named_pipe::ClientOptions,
    time::{Duration, sleep},
};

const PIPE: &str = r"\\.\pipe\OpenBlade.Service.v2";

pub fn get_status() -> Result<String, Error> {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_io()
        .enable_time()
        .build()
        .map_err(|_| Error::Io)?;
    runtime.block_on(wire::bounded(
        async {
            // One process, one request. Guid correlation needs uniqueness, not secrecy.
            let stamp = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map_err(|_| Error::Io)?
                .as_nanos();
            let hex = format!(
                "{:032x}",
                stamp.max(1) ^ ((std::process::id() as u128) << 96)
            );
            let id = format!(
                "{}-{}-{}-{}-{}",
                &hex[..8],
                &hex[8..12],
                &hex[12..16],
                &hex[16..20],
                &hex[20..]
            );
            let mut pipe = loop {
                // Tokio defaults to SECURITY_IDENTIFICATION | SECURITY_SQOS_PRESENT.
                // The server reads our real token/session after reading the request.
                // Never grant full impersonation or manufacture a claimed identity.
                match ClientOptions::new().open(PIPE) {
                    Ok(pipe) => break pipe,
                    Err(error) if error.raw_os_error() == Some(231) => {
                        sleep(Duration::from_millis(25)).await
                    } // ERROR_PIPE_BUSY; no request submitted.
                    Err(_) => return Err(Error::Unavailable),
                }
            };
            // No reconnect/replay after submission. Dropping the owned Tokio client
            // safely releases its native handle and pending overlapped I/O storage.
            wire::exchange(&mut pipe, &id).await
        },
        async {
            // A signal-registration failure also stops the operation before submission.
            let _ = tokio::signal::ctrl_c().await;
        },
    ))
}
