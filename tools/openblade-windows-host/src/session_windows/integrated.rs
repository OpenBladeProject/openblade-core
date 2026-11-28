use std::{
    ffi::OsString,
    future::pending,
    io::{self, Read},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering::SeqCst},
    },
    time::Duration,
};

use openblade_application_reporter::{ActivationMode, ApplicationProfile, Guid};
use openblade_platform_windows::application::watcher::replay::{
    ProcessProbe, Script, ScriptedWatcher,
};
use openblade_windows_host::{
    pipe::Identity,
    reporter_pipe::{Response, VERSION, encode, guid_text, receive, send},
};
use serde_json::json;
use tokio::{
    net::windows::named_pipe::{ClientOptions, NamedPipeClient},
    sync::{oneshot, watch},
    time::{Instant, interval, sleep, timeout},
};
use windows_sys::Win32::{
    Foundation::ERROR_PIPE_BUSY, Storage::FileSystem::SECURITY_IDENTIFICATION,
};

use super::{
    AUDIO_PREFIX, BATTERY_PREFIX, ColorFixture, Endpoint, GPU_PREFIX, GpuScenario, HdrFixture,
    PREFIX, RefreshFixture, Scenario, StartMode, WmiScenario, announce_ready, display_fixture,
    listen, serve_battery, serve_display, serve_gpu, session_audio, session_battery, usage,
    valid_id, valid_request_id,
};

const SERVICE_PREFIX: &str = r"\\.\pipe\OpenBlade.Rust.ReadOnly.Dev.";
const IO_TIMEOUT: Duration = Duration::from_secs(2);
const STATUS_ID: &str = "8f338bd0-3933-4f3b-88a0-49ff1d899a17";

pub(super) async fn run(id: &str, mode: &OsString, arguments: &[OsString]) -> io::Result<()> {
    let [gpu, service, pid, reporter_id, extra @ ..] = arguments else {
        return Err(usage());
    };
    let (battery_requested, audio_requested) = match extra {
        [] => (false, false),
        [option] if option == "battery-available" => (true, false),
        [option] if option == "audio-available" => (false, true),
        [first, second]
            if (first == "battery-available" && second == "audio-available")
                || (first == "audio-available" && second == "battery-available") =>
        {
            (true, true)
        }
        _ => return Err(usage()),
    };
    let (mode, color) = match mode.to_str() {
        Some("new") => (StartMode::New, "icc-partial-submission-error"),
        Some("resume") => (StartMode::Resume, "icc-available"),
        Some("reconcile") => (StartMode::Reconcile, "icc-available"),
        _ => return Err(usage()),
    };
    let gpu_scenario = match gpu.to_str() {
        Some("available") => GpuScenario::Available,
        Some("stalled") => GpuScenario::Stalled,
        _ => return Err(usage()),
    };
    let service = service
        .to_str()
        .filter(|value| valid_id(value))
        .ok_or_else(usage)?;
    let pid = pid
        .to_str()
        .and_then(|value| value.parse().ok())
        .filter(|pid| *pid != 0)
        .ok_or_else(usage)?;
    let reporter_id = reporter_id
        .to_str()
        .filter(|value| valid_id(value))
        .ok_or_else(usage)?;
    let instance = u128::from_str_radix(reporter_id, 16).map_err(io::Error::other)?;
    if instance == 0 {
        return Err(usage());
    }

    let display = Endpoint::new(format!("{PREFIX}{id}"), 2)?;
    let gpu = Endpoint::new(format!("{GPU_PREFIX}{id}"), 2)?;
    let battery = battery_requested
        .then(|| Endpoint::new(format!("{BATTERY_PREFIX}{id}"), 2))
        .transpose()?;
    let audio = audio_requested
        .then(|| Endpoint::new(format!("{AUDIO_PREFIX}{id}"), 2))
        .transpose()?;
    let owner = display.identity.fixture_owner();
    if owner != gpu.identity.fixture_owner()
        || battery
            .iter()
            .chain(audio.iter())
            .any(|endpoint| owner != endpoint.identity.fixture_owner())
    {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "fixture endpoint owners differ",
        ));
    }
    let display_claim = display.claim()?;
    let _gpu_claim = gpu.claim()?;
    let _battery_claim = battery.as_ref().map(Endpoint::claim).transpose()?;
    let _audio_claim = audio.as_ref().map(Endpoint::claim).transpose()?;
    let battery_listener = battery.as_ref().map(Endpoint::listen).transpose()?;
    let audio_listener = audio.as_ref().map(Endpoint::listen).transpose()?;
    let display_listener = display.listen()?;
    let gpu_listener = gpu.listen()?;

    let stopping = Arc::new(AtomicBool::new(false));
    let (stop, mut stopped) = oneshot::channel();
    let (focus, selection) = watch::channel(false);
    let input_stopping = Arc::clone(&stopping);
    std::thread::Builder::new()
        .name("integrated-session-input".into())
        .spawn(move || {
            let mut input = io::stdin().lock();
            let mut command = [0];
            while input.read_exact(&mut command).is_ok() {
                match command[0] {
                    b'a' => focus.send_replace(true),
                    b'c' => focus.send_replace(false),
                    _ => break,
                };
            }
            input_stopping.store(true, SeqCst);
            let _ = stop.send(());
        })?;

    let service = Service {
        path: format!("{SERVICE_PREFIX}{service}"),
        pid,
        identity: &display.identity,
    };
    let profile = tokio::select! {
        biased;
        _ = &mut stopped => return Ok(()),
        profile = service.profile() => profile?,
    };
    let mut script = Script::default();
    let mut watcher = script.start(VERSION, Guid(instance.to_be_bytes()), Arc::clone(&stopping))?;
    let result = async {
        watcher.wait_ready(IO_TIMEOUT)?;
        let state = display_fixture(
            (id, mode),
            &display.identity,
            Scenario::Available,
            WmiScenario::Available,
            HdrFixture::Available,
            RefreshFixture::Available,
            ColorFixture::parse(color).expect("integrated color fixture"),
        )?;
        // The Display claim must outlive an abandoned scripted submission,
        // including the bounded runtime drain at process exit.
        std::mem::forget(display_claim);
        if stopping.load(SeqCst) {
            return Ok(());
        }
        let path = profile.executable_path.clone();
        watcher.observe_profiles(vec![profile]);
        announce_ready()?;

        tokio::select! {
            biased;
            _ = &mut stopped => Ok(()),
            result = listen(&display, display_listener, async |pipe, identity| {
                serve_display(pipe, identity, &state, &stopping).await
            }) => result,
            result = listen(&gpu, gpu_listener, async |pipe, identity| {
                serve_gpu(pipe, identity, gpu_scenario).await
            }) => result,
            result = async {
                match (battery.as_ref(), battery_listener) {
                    (Some(endpoint), Some(listener)) => listen(endpoint, listener, async |pipe, identity| {
                        serve_battery(pipe, identity, session_battery::Scenario::Available).await
                    }).await,
                    _ => pending().await,
                }
            } => result,
            result = async {
                match (audio.as_ref(), audio_listener) {
                    (Some(endpoint), Some(listener)) => {
                        // The unified fixture admits only synthetic Audio reads.
                        let mut fixture = session_audio::Fixture::new(session_audio::Scenario::Available);
                        listen(endpoint, listener, async |pipe, identity| {
                            fixture.serve(pipe, identity).await
                        }).await
                    }
                    _ => pending().await,
                }
            } => result,
            result = report(&service, &script, &mut watcher, selection, &path, instance) => result,
        }
    }
    .await;
    stopping.store(true, SeqCst);
    let retired = watcher.shutdown();
    result.and(retired)
}

async fn report(
    service: &Service<'_>,
    script: &Script,
    watcher: &mut ScriptedWatcher,
    mut selection: watch::Receiver<bool>,
    path: &str,
    instance: u128,
) -> io::Result<()> {
    let mut tick = interval(Duration::from_millis(25));
    tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    let started = Instant::now();
    let mut generation = 0;
    let mut process: Option<ProcessProbe> = None;
    let mut request = 0u128;
    loop {
        tokio::select! {
            changed = selection.changed() => {
                if changed.is_err() {
                    return Ok(());
                }
                let active = *selection.borrow_and_update();
                match (active, process.take()) {
                    (true, None) => {
                        generation += 1;
                        process = Some(script.process(1, 42, generation, path));
                        script.focus(1);
                    }
                    (true, previous) => process = previous,
                    (false, Some(previous)) => {
                        previous.exit();
                        script.focus(0);
                    }
                    (false, None) => {}
                }
            }
            _ = tick.tick() => {}
        }
        let request_id = Guid((instance ^ request.rotate_left(64)).to_be_bytes());
        if let Some(message) = watcher.poll(started.elapsed(), request_id) {
            request += 1;
            let response = service
                .exchange(&encode(&message)?, &guid_text(message.request_id))
                .await?;
            watcher.complete(message.sequence, response.report_outcome()?.0);
        }
    }
}

struct Service<'a> {
    path: String,
    pid: u32,
    identity: &'a Identity,
}

impl Service<'_> {
    async fn connect(&self) -> io::Result<NamedPipeClient> {
        loop {
            match ClientOptions::new()
                .security_qos_flags(SECURITY_IDENTIFICATION)
                .open(&self.path)
            {
                Err(error) if error.raw_os_error() == Some(ERROR_PIPE_BUSY as i32) => {
                    sleep(Duration::from_millis(5)).await;
                }
                result => {
                    let pipe = result?;
                    self.identity.authorize_server(&pipe, self.pid)?;
                    return Ok(pipe);
                }
            }
        }
    }

    async fn exchange(&self, bytes: &[u8], request_id: &str) -> io::Result<Response> {
        timeout(IO_TIMEOUT, async {
            let pipe = send(self.connect().await?, bytes).await?;
            receive(pipe, request_id).await
        })
        .await?
    }

    async fn profile(&self) -> io::Result<ApplicationProfile> {
        let payload = serde_json::to_vec(&json!({
            "operation": "getStatus", "version": VERSION, "requestId": STATUS_ID
        }))?;
        let response = self.exchange(&payload, STATUS_ID).await?;
        let status = response
            .status
            .filter(|status| {
                response.success
                    && response.result.is_none()
                    && response.error.is_none()
                    && response.error_code.is_none()
                    && status["controlAccess"] == 0
            })
            .ok_or_else(invalid_profile)?;
        let Some([profile]) = status["profiles"]["applicationProfiles"]
            .as_array()
            .map(Vec::as_slice)
        else {
            return Err(invalid_profile());
        };
        let id = profile["id"]
            .as_str()
            .filter(|id| valid_request_id(id))
            .ok_or_else(invalid_profile)?;
        let id = u128::from_str_radix(&id.replace('-', ""), 16).map_err(io::Error::other)?;
        let path = profile["executablePath"]
            .as_str()
            .filter(|path| !path.is_empty())
            .ok_or_else(invalid_profile)?;
        let mode = match profile["activationMode"].as_u64() {
            Some(0) => ActivationMode::Foreground,
            Some(1) => ActivationMode::WhileRunningAfterActivation,
            _ => return Err(invalid_profile()),
        };
        let enabled = profile["enabled"].as_bool().ok_or_else(invalid_profile)?;
        Ok(ApplicationProfile {
            id: Guid(id.to_be_bytes()),
            executable_path: path.to_owned(),
            mode,
            enabled,
        })
    }
}

fn invalid_profile() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        "private service application fixture unavailable",
    )
}
