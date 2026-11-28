use std::{
    ffi::OsString,
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
use openblade_windows_host::pipe::{
    Connection as NamedPipeServer, Endpoint, Identity, stale_connection,
};
use tokio::{
    io::{AsyncRead, AsyncReadExt},
    runtime::Builder,
    sync::oneshot,
};

use crate::{
    session_audio, session_battery,
    session_brightness::{ScriptedComPolicy, WmiScenario},
    session_display::{
        self, ColorFixture, HdrFixture, RefreshFixture, Rejected, SessionDisplay,
        journal::{Journal, StartMode},
    },
    session_gpu,
};

mod integrated;

const PREFIX: &str = r"\\.\pipe\OpenBlade.Rust.Session.Display.Dev.";

const BATTERY_PREFIX: &str = r"\\.\pipe\OpenBlade.Rust.Session.Battery.Dev.";

const GPU_PREFIX: &str = r"\\.\pipe\OpenBlade.Rust.Session.Gpu.Dev.";

const AUDIO_PREFIX: &str = r"\\.\pipe\OpenBlade.Rust.Session.Audio.Dev.";

pub(super) fn entry() -> io::Result<()> {
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    run(&args)
}

fn run(args: &[OsString]) -> io::Result<()> {
    let [mode, id, scenario, brightness @ ..] = args else {
        return Err(usage());
    };
    let id = id.to_str().filter(|id| valid_id(id)).ok_or_else(usage)?;
    if mode == "integrated" {
        return run_fixture(integrated::run(id, scenario, brightness));
    }
    if mode == "battery-foreground" {
        if !brightness.is_empty() {
            return Err(usage());
        }
        let scenario = scenario
            .to_str()
            .and_then(session_battery::Scenario::parse)
            .ok_or_else(usage)?;
        return run_fixture(run_battery(id, scenario));
    }
    if mode == "audio-foreground" {
        if !brightness.is_empty() {
            return Err(usage());
        }
        let scenario = scenario
            .to_str()
            .and_then(session_audio::Scenario::parse)
            .ok_or_else(usage)?;
        return run_fixture(run_audio(id, scenario));
    }
    if mode == "gpu-foreground" {
        if !brightness.is_empty() {
            return Err(usage());
        }
        let scenario = match scenario.to_str() {
            Some("available") => GpuScenario::Available,
            #[cfg(feature = "gpu-close-fixture")]
            Some("simulated-close") => GpuScenario::SimulatedClose,
            Some("empty") => GpuScenario::Empty,
            Some("no-data") => GpuScenario::NoData,
            Some("large") => GpuScenario::Large,
            Some("stalled") => GpuScenario::Stalled,
            _ => return Err(usage()),
        };
        return run_fixture(run_gpu(id, scenario));
    }
    let mode = match mode.to_str() {
        Some("foreground") => StartMode::New,
        Some("resume-display") => StartMode::Resume,
        Some("reconcile-display") => StartMode::Reconcile,
        _ => return Err(usage()),
    };
    let refresh = match scenario.to_str() {
        Some("refresh-dynamic") => Some(RefreshFixture::Available),
        Some("refresh-stale-target") => Some(RefreshFixture::StaleTarget),
        Some("refresh-stale-power") => Some(RefreshFixture::StalePower),
        Some("refresh-inactive") => Some(RefreshFixture::Inactive),
        Some("refresh-validation-error") => Some(RefreshFixture::ValidationError),
        Some("refresh-stale-after-validation") => Some(RefreshFixture::StaleAfterValidation),
        Some("refresh-submission-error") => Some(RefreshFixture::SubmissionError),
        Some("refresh-readback-mismatch") => Some(RefreshFixture::ReadbackMismatch),
        Some("refresh-sibling-readback") => Some(RefreshFixture::SiblingReadback),
        Some("refresh-stalled-admission") => Some(RefreshFixture::StalledAdmission),
        Some("refresh-stalled-submission") => Some(RefreshFixture::StalledSubmission),
        _ => None,
    };
    let color = scenario.to_str().and_then(ColorFixture::parse);
    let (scenario, hdr) = match scenario.to_str() {
        _ if color == Some(ColorFixture::StalePower) => {
            (Scenario::Available, HdrFixture::StalePower)
        }
        _ if color == Some(ColorFixture::Inactive) => (Scenario::Available, HdrFixture::Inactive),
        _ if color.is_some() => (Scenario::Available, HdrFixture::Available),
        _ if refresh.is_some() => (Scenario::Dynamic, HdrFixture::Available),
        Some("available") => (Scenario::Available, HdrFixture::Available),
        Some("unavailable") => (Scenario::Unavailable, HdrFixture::Available),
        Some("stale-target") => (Scenario::StaleTarget, HdrFixture::Available),
        Some("native-error") => (Scenario::NativeError, HdrFixture::Available),
        Some("hdr-error") => (Scenario::HdrError, HdrFixture::Available),
        Some("hdr-unsupported") => (Scenario::HdrUnsupported, HdrFixture::Available),
        Some("hdr-stale-target") => (Scenario::Available, HdrFixture::StaleTarget),
        Some("hdr-stale-power") => (Scenario::Available, HdrFixture::StalePower),
        Some("hdr-inactive") => (Scenario::Available, HdrFixture::Inactive),
        Some("hdr-submission-error") => (Scenario::Available, HdrFixture::SubmissionError),
        Some("hdr-readback-mismatch") => (Scenario::Available, HdrFixture::ReadbackMismatch),
        Some("hdr-sibling-readback") => (Scenario::Available, HdrFixture::SiblingReadback),
        Some("hdr-stalled-admission") => (Scenario::Available, HdrFixture::StalledAdmission),
        Some("hdr-stalled-submission") => (Scenario::Available, HdrFixture::StalledSubmission),
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
            Some("starts-at-70") => WmiScenario::StartsAt70,
            Some("starts-at-10") => WmiScenario::StartsAt10,
            _ => return Err(usage()),
        },
        _ => return Err(usage()),
    };
    run_fixture(run_display(
        (id, mode),
        scenario,
        brightness,
        hdr,
        refresh.unwrap_or(RefreshFixture::Available),
        color.unwrap_or(ColorFixture::Disabled),
    ))
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
    let gpu_scenarios = if cfg!(feature = "gpu-close-fixture") {
        "available|empty|no-data|large|stalled|simulated-close"
    } else {
        "available|empty|no-data|large|stalled"
    };
    io::Error::new(
        io::ErrorKind::InvalidInput,
        format!(
            "expected foreground|resume-display|reconcile-display <32 lowercase hex digits> <display fixture> [brightness fixture] or gpu-foreground <32 lowercase hex digits> <{gpu_scenarios}> or battery-foreground <32 lowercase hex digits> <available|missing|malformed|oversized|failed|stalled> or audio-foreground <32 lowercase hex digits> <available|unavailable|stalled|modern|legacy|ambiguous> or integrated <id> <new|resume|reconcile> <gpu scenario> <service id> <service pid> <reporter id> [battery-available] [audio-available]"
        ),
    )
}

fn valid_id(id: &str) -> bool {
    id.len() == 32
        && id
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

async fn run_display(
    (id, mode): (&str, StartMode),
    scenario: Scenario,
    brightness: WmiScenario,
    hdr: HdrFixture,
    refresh: RefreshFixture,
    color: ColorFixture,
) -> io::Result<()> {
    // One reserved claim and one data connection. This endpoint has no
    // subscribers or concurrent dispatch, so partial frames can occupy one slot.
    let endpoint = Endpoint::new(format!("{PREFIX}{id}"), 2)?;
    // Keep the exclusive claim until process exit. The runtime can stop
    // waiting for a blocked worker before that worker has actually stopped.
    // The OS closes this one claim and its peer when no thread can act again.
    std::mem::forget(endpoint.claim()?);
    let listener = endpoint.listen()?;
    let session = display_fixture(
        (id, mode),
        &endpoint.identity,
        scenario,
        brightness,
        hdr,
        refresh,
        color,
    )?;

    let stopping = Arc::new(AtomicBool::new(false));
    let stop = Arc::clone(&stopping);
    serve_connections(&endpoint, listener, stop, async |pipe, identity| {
        serve_display(pipe, identity, &session, &stopping).await
    })
    .await
}

async fn run_battery(id: &str, scenario: session_battery::Scenario) -> io::Result<()> {
    let endpoint = Endpoint::new(format!("{BATTERY_PREFIX}{id}"), 2)?;
    let _claim = endpoint.claim()?;
    let listener = endpoint.listen()?;
    serve_connections(
        &endpoint,
        listener,
        Arc::new(AtomicBool::new(false)),
        async |pipe, identity| serve_battery(pipe, identity, scenario).await,
    )
    .await
}

async fn serve_battery(
    pipe: &mut NamedPipeServer,
    identity: &Identity,
    scenario: session_battery::Scenario,
) -> io::Result<()> {
    // Malformed, disconnected or stalled clients retire only their own connection.
    let _ = session_battery::serve(pipe, identity, scenario).await;
    Ok(())
}

pub(super) async fn read_frame_body(
    pipe: &mut (impl AsyncRead + Unpin),
    limit: i32,
) -> io::Result<Vec<u8>> {
    let length = pipe.read_i32_le().await?;
    if !(1..=limit).contains(&length) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "invalid Session request length",
        ));
    }
    let mut body = vec![0; length as usize];
    pipe.read_exact(&mut body).await?;
    Ok(body)
}

async fn run_gpu(id: &str, scenario: GpuScenario) -> io::Result<()> {
    let endpoint = Endpoint::new(format!("{GPU_PREFIX}{id}"), 2)?;
    let _claim = endpoint.claim()?;
    let listener = endpoint.listen()?;
    serve_connections(
        &endpoint,
        listener,
        Arc::new(AtomicBool::new(false)),
        async |pipe, identity| serve_gpu(pipe, identity, scenario).await,
    )
    .await
}

async fn run_audio(id: &str, scenario: session_audio::Scenario) -> io::Result<()> {
    let mut fixture = session_audio::Fixture::new(scenario);
    let endpoint = Endpoint::new(format!("{AUDIO_PREFIX}{id}"), 2)?;
    let _claim = endpoint.claim()?;
    let listener = endpoint.listen()?;
    serve_connections(
        &endpoint,
        listener,
        Arc::new(AtomicBool::new(false)),
        async |pipe, identity| fixture.serve(pipe, identity).await,
    )
    .await
}

fn display_fixture(
    (id, mode): (&str, StartMode),
    identity: &Identity,
    scenario: Scenario,
    brightness: WmiScenario,
    hdr: HdrFixture,
    refresh: RefreshFixture,
    color: ColorFixture,
) -> io::Result<Arc<Mutex<SessionDisplay>>> {
    let journal = Journal::open(id, identity, mode);
    let worker = ScriptedComPolicy::start(scenario, brightness, Arc::clone(&journal))?;
    Ok(Arc::new(Mutex::new(SessionDisplay::scripted(
        scenario, worker, hdr, refresh, color, journal,
    ))))
}

async fn serve_display(
    pipe: &mut NamedPipeServer,
    identity: &Identity,
    session: &Arc<Mutex<SessionDisplay>>,
    stopping: &Arc<AtomicBool>,
) -> io::Result<()> {
    if let Err(Rejected::InvalidResponse | Rejected::WorkerFailed) =
        session_display::serve(pipe, identity, session, stopping).await
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Session Display response cannot be framed",
        ));
    }
    Ok(())
}

async fn serve_gpu(
    pipe: &mut NamedPipeServer,
    identity: &Identity,
    scenario: GpuScenario,
) -> io::Result<()> {
    // A bad client or timed-out frame only retires this connection.
    match session_gpu::serve(pipe, identity, scenario).await {
        Err(error) if error.kind() == io::ErrorKind::Other => Err(error),
        _ => Ok(()),
    }
}

fn announce_ready() -> io::Result<()> {
    let mut output = io::stdout().lock();
    writeln!(output, "READY")?;
    output.flush()
}

async fn serve_connections(
    endpoint: &Endpoint,
    listener: NamedPipeServer,
    stopping: Arc<AtomicBool>,
    serve_one: impl AsyncFnMut(&mut NamedPipeServer, &Identity) -> io::Result<()>,
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
    announce_ready()?;
    tokio::select! {
        biased;
        _ = stopped => Ok(()),
        result = listen(endpoint, listener, serve_one) => result,
    }
}

async fn listen(
    endpoint: &Endpoint,
    mut listener: NamedPipeServer,
    mut serve_one: impl AsyncFnMut(&mut NamedPipeServer, &Identity) -> io::Result<()>,
) -> io::Result<()> {
    loop {
        match listener.connect().await {
            Ok(()) => serve_one(&mut listener, &endpoint.identity).await?,
            Err(error) if stale_connection(&error) => {}
            Err(error) => return Err(error),
        }
        // Drop the old stream and any buffered bytes while retaining the claim.
        drop(listener);
        listener = endpoint.next_listener().await?;
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

#[cfg(test)]
mod launch_tests {
    #[test]
    fn no_arguments_require_an_explicit_fixture() {
        let error = super::run(&[]).unwrap_err();
        assert_eq!(error.kind(), std::io::ErrorKind::InvalidInput);
    }
}
