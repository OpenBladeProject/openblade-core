use std::{
    io::{self, BufRead, Write},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering::SeqCst},
    },
    time::Duration,
};

use openblade_platform_windows::{
    display::scripted::Scenario, gpu_apps::scripted::Scenario as GpuScenario,
};
use openblade_windows_host::pipe::{Endpoint, Identity, stale_connection};
use tokio::{net::windows::named_pipe::NamedPipeServer, runtime::Builder, sync::oneshot};

use crate::{
    session_brightness::{ScriptedComPolicy, WmiScenario},
    session_display::{self, Rejected, SessionDisplay},
    session_gpu,
};

const PREFIX: &str = r"\\.\pipe\OpenBlade.Rust.Session.Display.Dev.";

const GPU_PREFIX: &str = r"\\.\pipe\OpenBlade.Rust.Session.Gpu.Dev.";

pub(super) fn entry() -> io::Result<()> {
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    let [mode, id, scenario, brightness @ ..] = args.as_slice() else {
        return Err(usage());
    };
    let id = id.to_str().filter(|id| valid_id(id)).ok_or_else(usage)?;
    if mode == "gpu-foreground" {
        if !brightness.is_empty() {
            return Err(usage());
        }
        let scenario = match scenario.to_str() {
            Some("available") => GpuScenario::Available,
            Some("empty") => GpuScenario::Empty,
            Some("no-data") => GpuScenario::NoData,
            Some("large") => GpuScenario::Large,
            Some("stalled") => GpuScenario::Stalled,
            _ => return Err(usage()),
        };
        return run_fixture(run_gpu(id, scenario));
    }
    if mode != "foreground" {
        return Err(usage());
    }
    let scenario = match scenario.to_str() {
        Some("available") => Scenario::Available,
        Some("unavailable") => Scenario::Unavailable,
        Some("stale-target") => Scenario::StaleTarget,
        Some("native-error") => Scenario::NativeError,
        Some("hdr-error") => Scenario::HdrError,
        _ => return Err(usage()),
    };
    let brightness = match brightness {
        [] => WmiScenario::Available,
        [value] => match value.to_str() {
            Some("missing-wmi") => WmiScenario::Unavailable,
            Some("replaced-panel") => WmiScenario::ReplacedPanel,
            Some("sibling-readback") => WmiScenario::SiblingReadback,
            Some("stalled-write") => WmiScenario::StalledWrite,
            Some("stalled-admission") => WmiScenario::StalledAdmission,
            _ => return Err(usage()),
        },
        _ => return Err(usage()),
    };
    run_fixture(run_display(id, scenario, brightness))
}

fn run_fixture(fixture: impl Future<Output = io::Result<()>>) -> io::Result<()> {
    let runtime = Builder::new_current_thread().enable_all().build()?;
    let result = runtime.block_on(fixture);
    // A dropped pipe future is not completion of the worker's request.
    // The fixture process exits without granting a replacement worker authority.
    runtime.shutdown_timeout(Duration::from_millis(250));
    result
}

fn usage() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidInput,
        "expected foreground <32 lowercase hex digits> <available|unavailable|stale-target|native-error|hdr-error> [brightness fixture] or gpu-foreground <32 lowercase hex digits> <available|empty|no-data|large|stalled>",
    )
}

fn valid_id(id: &str) -> bool {
    id.len() == 32
        && id
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

async fn run_display(id: &str, scenario: Scenario, brightness: WmiScenario) -> io::Result<()> {
    // One reserved claim and one data connection. This endpoint has no
    // subscribers or concurrent dispatch, so partial frames can occupy one slot.
    let endpoint = Endpoint::new(format!("{PREFIX}{id}"), 2)?;
    let _claim = endpoint.claim()?;
    let listener = endpoint.listen()?;
    let worker = ScriptedComPolicy::start(scenario, brightness)?;
    let session = Arc::new(Mutex::new(SessionDisplay::scripted(scenario, worker)));

    let stopping = Arc::new(AtomicBool::new(false));
    let stop = Arc::clone(&stopping);
    serve_connections(&endpoint, listener, stop, async |pipe, identity| {
        if let Err(Rejected::InvalidResponse | Rejected::WorkerFailed) =
            session_display::serve(pipe, identity, &session, &stopping).await
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Session Display response cannot be framed",
            ));
        }
        Ok(())
    })
    .await
}

async fn run_gpu(id: &str, scenario: GpuScenario) -> io::Result<()> {
    let endpoint = Endpoint::new(format!("{GPU_PREFIX}{id}"), 2)?;
    let _claim = endpoint.claim()?;
    let listener = endpoint.listen()?;
    serve_connections(
        &endpoint,
        listener,
        Arc::new(AtomicBool::new(false)),
        async |pipe, identity| {
            // A bad client or timed-out frame only retires this connection.
            match session_gpu::serve(pipe, identity, scenario).await {
                Err(error) if error.kind() == io::ErrorKind::Other => Err(error),
                _ => Ok(()),
            }
        },
    )
    .await
}

async fn serve_connections(
    endpoint: &Endpoint,
    listener: NamedPipeServer,
    stopping: Arc<AtomicBool>,
    mut serve_one: impl AsyncFnMut(&mut NamedPipeServer, &Identity) -> io::Result<()>,
) -> io::Result<()> {
    let (stop, stopped) = oneshot::channel();
    let stop_signal = Arc::clone(&stopping);
    std::thread::Builder::new()
        .name("session-development-stop".into())
        .spawn(move || {
            let _ = io::stdin().lock().fill_buf();
            stop_signal.store(true, SeqCst);
            let _ = stop.send(());
        })?;
    {
        let mut output = io::stdout().lock();
        writeln!(output, "READY")?;
        output.flush()?;
    }

    let serve = async {
        let mut listener = listener;
        loop {
            match listener.connect().await {
                Ok(()) => serve_one(&mut listener, &endpoint.identity).await?,
                Err(error) if stale_connection(&error) => {}
                Err(error) => return Err(error),
            }
            // Drop the old stream, including any buffered bytes. Keep the
            // synchronous claim until shutdown so the name never goes unowned.
            drop(listener);
            listener = endpoint.next_listener().await?;
        }
    };
    tokio::select! {
        biased;
        _ = stopped => Ok(()),
        result = serve => result,
    }
}

pub(super) fn valid_request_id(id: &str) -> bool {
    id.len() == 36
        && id.bytes().enumerate().all(|(position, byte)| {
            if matches!(position, 8 | 13 | 18 | 23) {
                byte == b'-'
            } else {
                byte.is_ascii_hexdigit()
            }
        })
        && id != "00000000-0000-0000-0000-000000000000"
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_only_fixed_width_lowercase_hex_for_private_names() {
        assert!(valid_id("11111111222243338444555555555555"));
        for invalid in [
            "OpenBlade.Session.Display.v1",
            r"\\.\pipe\OpenBlade.Session.Display.v1",
            "1111111122224333844455555555555A",
            "1111111122224333844455555555555",
        ] {
            assert!(!valid_id(invalid));
        }
    }
}
