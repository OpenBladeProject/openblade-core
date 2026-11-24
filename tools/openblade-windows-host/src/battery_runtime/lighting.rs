use super::{Result, Scenario, ScenarioState};
use openblade_domain::recovery::ScopedStatus;
use openblade_protocol::{REPORT_LENGTH, RazerReport};
use serde::Deserialize;
use serde_json::{Value, json};
use std::{
    collections::BTreeMap,
    io,
    sync::{
        Arc,
        atomic::{AtomicU8, Ordering},
    },
};
use tokio::sync::{Semaphore, mpsc, watch};

mod renderer;
use renderer::{RETIRE_LIMIT, Renderer, Script};

const LIGHTING: i32 = 3;
const CLASS: u8 = 0x03;
const GET_BRIGHTNESS: u8 = 0x83;
const SET_BRIGHTNESS: u8 = 0x03;
const LIT: u8 = 197;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Lifetime {
    Temporary,
    Persistent,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct LightingOverride {
    value: OffSettings,
    lifetime: u8,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OffSettings {
    effect: u8,
    color: Color,
    brightness: u8,
    parameters: Option<Parameters>,
    logo_mode: Option<u8>,
    independent_controls: Option<serde::de::IgnoredAny>,
    // Protocol 81 full-effect authority on hybrid backends. Managed omits the
    // default; an explicit true is not an admitted Off on this backend.
    #[serde(default)]
    controls_keyboard_effect: bool,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Color {
    red: u8,
    green: u8,
    blue: u8,
}

#[derive(Deserialize)]
struct Parameters {
    kind: String,
    #[serde(flatten)]
    fields: BTreeMap<String, serde::de::IgnoredAny>,
}

impl LightingOverride {
    fn off_lifetime(&self) -> Option<Lifetime> {
        let settings = &self.value;
        if settings.effect != 0
            || settings.brightness != 0
            || settings.color.red != 0
            || settings.color.green != 0
            || settings.color.blue != 0
            || settings
                .parameters
                .as_ref()
                .is_some_and(|p| p.kind != "off" || !p.fields.is_empty())
            || settings.logo_mode.is_some()
            || settings.independent_controls.is_some()
            || settings.controls_keyboard_effect
        {
            return None;
        }
        match self.lifetime {
            0 => Some(Lifetime::Temporary),
            1 => Some(Lifetime::Persistent),
            _ => None,
        }
    }
}

pub(super) struct Lighting {
    off: Value,
    device: Keyboard,
    native_lane: Arc<Semaphore>,
    renderer: Option<Renderer>,
    renderer_start_pending: bool,
    script: Option<Script>,
    temporary: bool,
    saved: SavedIntent,
    confirmed_off: bool,
    pending: bool,
}

#[derive(Default)]
struct SavedIntent {
    off: bool,
}

struct Keyboard {
    raw: Arc<AtomicU8>,
    transaction: u8,
    controller_present: bool,
    lose_final_read: bool,
    after_set_reads: Option<u8>,
    #[cfg(test)]
    trace: Vec<(u8, [u8; 3])>,
}

impl Keyboard {
    fn exchange(&mut self, operation: u8, value: u8) -> Option<u8> {
        let transaction = self.transaction.checked_add(1)?;
        let request = report(transaction, operation, value)?;
        self.transaction = transaction;
        let response = self.reply(&request.encode())?;
        let data = request.validate_response(&response, Some(3)).ok()?;
        let bytes = data.as_slice();
        (bytes[..2] == [1, 5]).then_some(bytes[2])
    }

    fn reply(&mut self, bytes: &[u8; REPORT_LENGTH]) -> Option<[u8; REPORT_LENGTH]> {
        let [selector, led, value] = bytes[8..11].try_into().ok()?;
        if bytes[6] != CLASS || bytes[5] != 3 || [selector, led] != [1, 5] {
            return None;
        }
        let operation = bytes[7];
        #[cfg(test)]
        self.trace.push((operation, [selector, led, value]));
        match (operation, value) {
            (GET_BRIGHTNESS, 0) => {
                if let Some(reads) = self.after_set_reads {
                    if reads == 0 {
                        // The port already observed zero. The independent
                        // scoped getter fails after an external change.
                        self.raw.store(LIT, Ordering::Relaxed);
                        self.after_set_reads = None;
                        return None;
                    } else {
                        self.after_set_reads = Some(reads - 1);
                    }
                }
            }
            (SET_BRIGHTNESS, 0) => {
                self.raw.store(0, Ordering::Relaxed);
                if std::mem::take(&mut self.lose_final_read) {
                    self.after_set_reads = Some(1);
                }
            }
            _ => return None,
        }
        let mut reply = report(bytes[1], operation, self.raw.load(Ordering::Relaxed))?.encode();
        reply[0] = 2;
        Some(reply)
    }

    fn query(&mut self) -> Option<u8> {
        self.exchange(GET_BRIGHTNESS, 0)
    }
}

fn report(transaction: u8, operation: u8, value: u8) -> Option<RazerReport> {
    RazerReport::create(transaction, CLASS, operation, &[1, 5, value]).ok()
}

impl Lighting {
    pub(super) fn new(status: &mut Value) -> io::Result<Self> {
        let off = json!({
            "effect": 0, "color": {"red": 0, "green": 0, "blue": 0},
            "brightness": 0, "parameters": {"kind": "off"}, "logoMode": null
        });
        if status["profiles"]["pluggedIn"]["lighting"] != off
            || status["desiredState"]["lighting"]["value"] != off
            || status["lightingRendererOwnership"] != 0
            || status["confirmedState"]["lighting"] != Value::Null
        {
            return Err(io::Error::other(
                "lighting fixture requires an unowned Off profile",
            ));
        }
        status["capabilities"]["lightingEffects"] = json!([0]);
        // Preserve an independent logo observation. The keyboard recipe has
        // no logo command or inferred logo readback.
        status["confirmedState"]["logoLighting"] = 1.into();
        Ok(Self {
            off,
            device: Keyboard {
                raw: Arc::new(AtomicU8::new(LIT)),
                transaction: 0,
                controller_present: false,
                lose_final_read: false,
                after_set_reads: None,
                #[cfg(test)]
                trace: Vec::new(),
            },
            native_lane: Arc::new(Semaphore::new(1)),
            renderer: None,
            renderer_start_pending: false,
            script: None,
            temporary: false,
            saved: SavedIntent::default(),
            confirmed_off: false,
            pending: false,
        })
    }

    pub(super) fn integrated(status: &mut Value) -> io::Result<Self> {
        let mut lighting = Self::new(status)?;
        lighting.renderer_start_pending = true;
        Ok(lighting)
    }

    pub(super) fn expire_power_override(&mut self) {
        self.temporary = false;
    }

    pub(super) fn start_renderer(&mut self) -> io::Result<()> {
        if self.renderer.is_some() || self.pending {
            return Err(io::Error::other(
                "Previous renderer or keyboard write remains fenced.",
            ));
        }
        let permit = Arc::clone(&self.native_lane)
            .try_acquire_owned()
            .map_err(io::Error::other)?;
        let (renderer, script) = Renderer::start(permit, Arc::clone(&self.device.raw))?;
        self.renderer = Some(renderer);
        self.renderer_start_pending = false;
        self.script = Some(script);
        Ok(())
    }

    fn stage(
        &mut self,
        value: Option<LightingOverride>,
        exact: bool,
    ) -> std::result::Result<(), Result> {
        if self.renderer_start_pending {
            return Err(result(
                ScopedStatus::Blocked,
                "Renderer startup is pending.",
            ));
        }
        if self.pending {
            return Err(result(
                ScopedStatus::Blocked,
                "Keyboard Off awaits independent readback.",
            ));
        }
        if self.renderer.as_ref().is_some_and(Renderer::closed) {
            return Err(result(
                ScopedStatus::Blocked,
                "Renderer retirement is fenced.",
            ));
        }
        let lifetime =
            match value {
                Some(value) => Some(value.off_lifetime().ok_or_else(|| {
                    result(ScopedStatus::Blocked, "Only keyboard Off is admitted.")
                })?),
                None => None,
            };
        self.saved.off = lifetime == Some(Lifetime::Persistent);
        self.temporary = lifetime == Some(Lifetime::Temporary);
        self.admit(exact)
    }

    fn admit(&self, exact: bool) -> std::result::Result<(), Result> {
        if !exact || self.device.controller_present {
            return Err(result(
                ScopedStatus::Blocked,
                "Lighting intent stored. Device write admission is unavailable.",
            ));
        }
        Ok(())
    }

    fn apply_off(&mut self, exact: bool) -> Result {
        if let Err(refused) = self.admit(exact) {
            return refused;
        }
        if self.renderer.is_some() {
            return result(ScopedStatus::Blocked, "Renderer retirement is fenced.");
        }
        let Ok(_native) = Arc::clone(&self.native_lane).try_acquire_owned() else {
            return result(
                ScopedStatus::Blocked,
                "A native lighting owner is still active.",
            );
        };
        match self.device.query() {
            Some(0) => {
                self.confirmed_off = true;
                return result(ScopedStatus::Unchanged, "Keyboard Off already confirmed.");
            }
            Some(_) => self.confirmed_off = false,
            None => {
                return result(
                    ScopedStatus::Failed,
                    "Keyboard brightness baseline is unavailable.",
                );
            }
        }

        // The port takes its own baseline after scoped preflight. A zero
        // baseline needs no setter and is still verified by the final getter.
        let Some(baseline) = self.device.query() else {
            return result(
                ScopedStatus::Failed,
                "Keyboard brightness preflight is unavailable.",
            );
        };
        if baseline != 0 {
            // Reserve the setter, two confirmation reads and the two
            // possible scripted observations before the allocator runs out.
            if self.device.transaction.checked_add(5).is_none() {
                return result(
                    ScopedStatus::Blocked,
                    "Lighting transaction space cannot cover submission and readback.",
                );
            }
            self.pending = true;
            if self.device.exchange(SET_BRIGHTNESS, 0) != Some(0) || self.device.query() != Some(0)
            {
                return result(
                    ScopedStatus::Failed,
                    "Keyboard Off submission could not be confirmed.",
                );
            }
        }
        let final_read = self.device.query();
        self.confirmed_off = final_read == Some(0);
        if final_read.is_some() {
            self.pending = false;
        }
        if self.confirmed_off {
            result(
                ScopedStatus::Applied,
                "Keyboard Off independently confirmed.",
            )
        } else {
            result(
                ScopedStatus::Failed,
                "Final keyboard brightness observation could not confirm Off.",
            )
        }
    }

    fn observe(&mut self) {
        if self.renderer.is_some() {
            return;
        }
        if let Some(raw) = self.device.query() {
            self.confirmed_off = raw == 0;
            if self.confirmed_off {
                self.pending = false;
            }
        }
    }

    pub(super) fn project(&self, status: &mut Value) {
        let (source, lifetime) = if self.temporary {
            (3, Some(0))
        } else if self.saved.off {
            (4, Some(1))
        } else {
            (1, None)
        };
        status["lightingOverride"] =
            json!(lifetime.map(|lifetime| json!({"value": self.off, "lifetime": lifetime})));
        status["desiredState"]["lighting"] = json!({"value": self.off, "source": source});
        status["confirmedState"]["lighting"] = json!(self.confirmed_off.then_some(&self.off));
        if self.device.controller_present
            && let Some(controllers) = status["potentialControllers"].as_array_mut()
        {
            controllers.push("scripted lighting controller".into());
        }
        status["lightingRendererOwnership"] = match &self.renderer {
            None => 0,                      // None
            Some(Renderer::Uncertain) => 9, // Faulted
            Some(_) => 2,                   // Spectrum, retained until retirement completes
        }
        .into();
        let uncertain = self.pending || matches!(self.renderer, Some(Renderer::Uncertain));
        if uncertain || self.renderer.as_ref().is_some_and(Renderer::closed) {
            status["writesBlocked"] = true.into();
        }
        if uncertain && let Some(unconfirmed) = status["unconfirmedWrites"].as_array_mut() {
            unconfirmed.push(LIGHTING.into());
        }
    }
}

fn result(status: ScopedStatus, message: &'static str) -> Result {
    Result { status, message }
}

impl ScenarioState {
    fn lighting(&mut self) -> io::Result<&mut Lighting> {
        self.lighting
            .as_mut()
            .ok_or_else(|| io::Error::other("missing lighting scenario"))
    }
}

impl Scenario {
    pub(crate) async fn apply_lighting(
        self: &Arc<Self>,
        value: Option<LightingOverride>,
        started_retirement: &mut bool,
    ) -> io::Result<Result> {
        let temporary =
            value.as_ref().and_then(LightingOverride::off_lifetime) == Some(Lifetime::Temporary);
        let mut owned_operation = None;
        let mut retired = None;
        let initial = self
            .update(|state| {
                if state.configuration_blocked {
                    return Ok(super::blocked_configuration());
                }
                if let Some(blocked) = state.session_block() {
                    return Ok(blocked);
                }
                let exact = state.current["deviceVerified"] == true;
                let lighting = state.lighting()?;
                // Ordinary Off serializes under the state lock as before.
                // Only the request retiring an owner retains a reservation
                // after dropping that lock.
                let Ok(operation) = self.lighting_operation.try_lock() else {
                    return Ok(result(
                        ScopedStatus::Blocked,
                        "Keyboard Off is already waiting for retirement.",
                    ));
                };
                if let Err(refused) = lighting.stage(value, exact) {
                    return Ok(refused);
                }
                if lighting.renderer.is_none() {
                    return Ok(lighting.apply_off(exact));
                }

                retired = Some(self.begin_retirement(lighting)?);
                owned_operation = Some(operation);
                *started_retirement = true;
                Ok(result(
                    ScopedStatus::Unchanged,
                    "Renderer frame admission closed.",
                ))
            })
            .await?;
        let Some(mut retired) = retired else {
            return Ok(initial);
        };
        let _owned_operation = owned_operation;
        wait_for_retirement(&mut retired).await?;
        self.update(|state| {
            let exact = state.current["deviceVerified"] == true;
            let lighting = state.lighting()?;
            if lighting.renderer.is_some() {
                return Ok(result(
                    ScopedStatus::Failed,
                    "Renderer retirement could not be verified.",
                ));
            }
            if state.configuration_blocked {
                return Ok(super::blocked_configuration());
            }
            if let Some(blocked) = state.session_block() {
                return Ok(blocked);
            }
            let lighting = state.lighting()?;
            if temporary && !lighting.temporary {
                return Ok(result(
                    ScopedStatus::Blocked,
                    "Keyboard Off intent expired on a power change.",
                ));
            }
            Ok(lighting.apply_off(exact))
        })
        .await
    }

    fn begin_retirement(
        self: &Arc<Self>,
        lighting: &mut Lighting,
    ) -> io::Result<watch::Receiver<bool>> {
        let (publish, done) = watch::channel(false);
        let worker = lighting
            .renderer
            .as_mut()
            .ok_or_else(|| io::Error::other("Missing owned renderer."))?
            .close(done.clone())?;
        let scenario = Arc::clone(self);
        tokio::spawn(async move {
            // This task belongs to the owner, even when the pipe waiter is canceled.
            let known = worker.retire(RETIRE_LIMIT).await;
            let updated = scenario
                .update(|state| {
                    let (renderer, message) = if known {
                        (None, "Renderer port retired.")
                    } else {
                        (
                            Some(Renderer::Uncertain),
                            "Renderer retirement could not be verified.",
                        )
                    };
                    state.lighting()?.renderer = renderer;
                    Ok(result(ScopedStatus::Unchanged, message))
                })
                .await;
            publish.send_replace(updated.is_ok());
        });
        Ok(done)
    }

    pub(crate) async fn drive_integrated(
        self: &Arc<Self>,
        mut advance: mpsc::Receiver<()>,
        mut shutdown: tokio::sync::oneshot::Receiver<()>,
        release: tokio::sync::oneshot::Receiver<()>,
    ) -> io::Result<()> {
        // Failed durable or endpoint admission never starts an owner.
        if !matches!(
            shutdown.try_recv(),
            Err(tokio::sync::oneshot::error::TryRecvError::Empty)
        ) {
            return Ok(());
        }
        let (stopping, mut power_stop) = watch::channel(false);
        let renderer_stop = power_stop.clone();
        let power = async {
            let result = tokio::select! {
                biased;
                _ = power_stop.wait_for(|stop| *stop) => Ok(()),
                result = async {
                    if self.accepts_application() {
                        self.drive_application_steps(&mut advance).await
                    } else {
                        self.drive_power_steps(&mut advance, false).await
                    }
                } => result,
            };
            if result.is_err() {
                stopping.send_replace(true);
            }
            result
        };
        let renderer = async {
            let result = self.drive_integrated_renderer(renderer_stop, release).await;
            if result.is_err() {
                stopping.send_replace(true);
            }
            result
        };
        let drive = async { tokio::join!(renderer, power) };
        tokio::pin!(drive);
        let (renderer, power) = tokio::select! {
            biased;
            _ = &mut shutdown => {
                stopping.send_replace(true);
                drive.await
            }
            result = &mut drive => result,
        };
        renderer.and(power)
    }

    async fn drive_integrated_renderer(
        self: &Arc<Self>,
        mut stopping: watch::Receiver<bool>,
        release: tokio::sync::oneshot::Receiver<()>,
    ) -> io::Result<()> {
        let script = {
            let mut state = self.state.lock().await;
            let lighting = state.lighting()?;
            lighting.start_renderer()?;
            lighting
                .script
                .take()
                .ok_or_else(|| io::Error::other("Missing renderer script."))?
        };
        let Script {
            started,
            complete_frame,
            frame_done,
            release_port,
        } = script;
        started.await.map_err(io::Error::other)?;
        let mut status = self.subscribe();
        self.update(|state| {
            let message = if state
                .lighting()?
                .renderer
                .as_ref()
                .is_some_and(Renderer::closed)
            {
                "Renderer frame admission closed."
            } else {
                "Renderer frame in flight."
            };
            Ok(result(ScopedStatus::Unchanged, message))
        })
        .await?;

        let mut retired = loop {
            {
                let mut state = self.state.lock().await;
                let lighting = state.lighting()?;
                match &lighting.renderer {
                    Some(Renderer::Retiring(done)) => break done.clone(),
                    Some(Renderer::Owned(_)) if *stopping.borrow() => {
                        break self.begin_retirement(lighting)?;
                    }
                    Some(Renderer::Owned(_)) => {}
                    _ => {
                        return Err(io::Error::other(
                            "Renderer ownership could not be verified.",
                        ));
                    }
                }
            }
            tokio::select! {
                biased;
                changed = stopping.changed() => changed.map_err(io::Error::other)?,
                changed = status.changed() => changed.map_err(io::Error::other)?,
            }
        };
        let released = async {
            self.drain_frame(complete_frame, frame_done).await?;
            let known = matches!(
                tokio::time::timeout(RETIRE_LIMIT / 2, release).await,
                Ok(Ok(()))
            );
            release_port
                .send(known)
                .map_err(|_| io::Error::other("Port already released."))
        }
        .await;
        wait_for_retirement(&mut retired).await?;
        released?;
        if self.state.lock().await.lighting()?.renderer.is_some() {
            return Err(io::Error::other(
                "Renderer retirement could not be verified.",
            ));
        }
        Ok(())
    }

    async fn drain_frame(
        &self,
        complete_frame: tokio::sync::oneshot::Sender<()>,
        frame_done: tokio::sync::oneshot::Receiver<()>,
    ) -> io::Result<()> {
        complete_frame
            .send(())
            .map_err(|_| io::Error::other("Frame already ended."))?;
        frame_done.await.map_err(io::Error::other)?;
        self.update(|state| {
            if state.lighting()?.native_lane.try_acquire().is_ok() {
                return Err(io::Error::other(
                    "Renderer released its port before native retirement.",
                ));
            }
            Ok(result(
                ScopedStatus::Unchanged,
                "Frame drained. Renderer port still owned.",
            ))
        })
        .await?;
        Ok(())
    }

    pub(crate) async fn lighting_wait_disconnected(&self) -> io::Result<()> {
        self.update(|_| {
            Ok(result(
                ScopedStatus::Unchanged,
                "Caller disconnected while waiting for renderer retirement.",
            ))
        })
        .await?;
        Ok(())
    }

    pub(crate) async fn drive_lighting(
        &self,
        mut advance: mpsc::Receiver<()>,
        owned: bool,
        uncertain: bool,
    ) -> io::Result<()> {
        if owned && !self.drive_owned_renderer(&mut advance, uncertain).await? {
            while advance.recv().await.is_some() {}
            return Ok(());
        }

        type Step = (&'static str, fn(&mut Lighting));
        let steps: [Step; 4] = [
            (
                "A competing lighting controller was observed.",
                |lighting| {
                    lighting.device.raw.store(LIT, Ordering::Relaxed);
                    lighting.device.controller_present = true;
                    lighting.observe();
                },
            ),
            (
                "Controller left. Final brightness getter failure armed.",
                |lighting| {
                    lighting.device.controller_present = false;
                    lighting.device.lose_final_read = true;
                    lighting.observe();
                },
            ),
            (
                "Read-only observation still sees nonzero brightness.",
                Lighting::observe,
            ),
            (
                "Read-only observation confirms external keyboard Off.",
                |lighting| {
                    lighting.device.raw.store(0, Ordering::Relaxed);
                    lighting.observe();
                },
            ),
        ];
        for (message, step) in steps {
            if advance.recv().await.is_none() {
                return Ok(());
            }
            self.update(|state| {
                let lighting = state.lighting()?;
                lighting.device.lose_final_read = false;
                lighting.device.after_set_reads = None;
                step(lighting);
                Ok(result(ScopedStatus::Unchanged, message))
            })
            .await?;
        }
        while advance.recv().await.is_some() {}
        Ok(())
    }

    async fn drive_owned_renderer(
        &self,
        advance: &mut mpsc::Receiver<()>,
        uncertain: bool,
    ) -> io::Result<bool> {
        let script = self
            .state
            .lock()
            .await
            .lighting()?
            .script
            .take()
            .ok_or_else(|| io::Error::other("Missing renderer script."))?;
        script.started.await.map_err(io::Error::other)?;
        self.update(|_| Ok(result(ScopedStatus::Unchanged, "Renderer frame in flight.")))
            .await?;

        let retired = loop {
            if advance.recv().await.is_none() {
                return Ok(false);
            }
            let mut state = self.state.lock().await;
            let lighting = state.lighting()?;
            let renderer = lighting
                .renderer
                .as_ref()
                .ok_or_else(|| io::Error::other("Missing owned renderer."))?;
            let retired = match renderer {
                Renderer::Retiring(retired) => Some(retired.clone()),
                Renderer::Uncertain => None,
                Renderer::Owned(_) => {
                    self.update_locked(&mut state, |_| {
                        Ok(result(
                            ScopedStatus::Unchanged,
                            "Waiting for a keyboard Off request.",
                        ))
                    })?;
                    continue;
                }
            };
            if renderer.submit().is_ok() || lighting.start_renderer().is_ok() {
                return Err(io::Error::other(
                    "Retiring renderer admitted a frame or replacement.",
                ));
            }
            if lighting.native_lane.try_acquire().is_ok() {
                return Err(io::Error::other(
                    "In-flight frame lost its native lighting lease.",
                ));
            }
            break retired;
        };
        self.drain_frame(script.complete_frame, script.frame_done)
            .await?;

        if advance.recv().await.is_none() {
            return Ok(false);
        }
        script
            .release_port
            .send(!uncertain)
            .map_err(|_| io::Error::other("Port already released."))?;
        if let Some(mut retired) = retired {
            wait_for_retirement(&mut retired).await?;
        }
        if self.state.lock().await.lighting()?.renderer.is_none() {
            return Ok(true);
        }
        if advance.recv().await.is_some() {
            self.update(|state| {
                let lighting = state.lighting()?;
                if lighting.start_renderer().is_ok() {
                    return Err(io::Error::other(
                        "Renderer admitted a replacement after uncertain retirement.",
                    ));
                }
                lighting.device.raw.store(0, Ordering::Relaxed);
                lighting.observe();
                Ok(result(
                    ScopedStatus::Unchanged,
                    "Unverified retirement retains the lighting fence.",
                ))
            })
            .await?;
        }
        Ok(false)
    }
}

async fn wait_for_retirement(retired: &mut watch::Receiver<bool>) -> io::Result<()> {
    retired
        .wait_for(|done| *done)
        .await
        .map_err(io::Error::other)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture() -> Lighting {
        let mut status: Value = serde_json::from_str(crate::wire::CURRENT_FIXTURE).unwrap();
        Lighting::new(&mut status["status"]).unwrap()
    }

    impl Lighting {
        fn apply(&mut self, value: Option<LightingOverride>, exact: bool) -> Result {
            match self.stage(value, exact) {
                Err(refused) => refused,
                Ok(()) => self.apply_off(exact),
            }
        }
    }

    fn request(lifetime: u8) -> Option<LightingOverride> {
        Some(
            serde_json::from_value(json!({
                "value": {
                    "effect": 0, "brightness": 0, "color": {"red": 0, "green": 0, "blue": 0},
                    "parameters": {"kind": "off"}
                },
                "lifetime": lifetime
            }))
            .unwrap(),
        )
    }

    #[tokio::test]
    async fn concurrent_unowned_requests_wait_for_state_and_run_in_order() {
        let status: Value = serde_json::from_str(crate::wire::CURRENT_FIXTURE).unwrap();
        let baseline = status["status"].clone();
        let profiles = baseline["profiles"].clone();
        let scenario = Arc::new(
            Scenario::composed(
                baseline,
                profiles,
                None,
                super::super::ProfileReplay::LightingOff {
                    owned: false,
                    uncertain: false,
                },
            )
            .unwrap(),
        );
        let busy = scenario.state.lock().await;
        let mut first_started = false;
        let mut second_started = false;
        let first = scenario.apply_lighting(request(1), &mut first_started);
        let second = scenario.apply_lighting(request(0), &mut second_started);
        tokio::pin!(first, second);
        std::future::poll_fn(|context| {
            use std::future::Future;
            assert!(first.as_mut().poll(context).is_pending());
            assert!(second.as_mut().poll(context).is_pending());
            std::task::Poll::Ready(())
        })
        .await;
        drop(busy);

        let (first, second) = tokio::join!(first, second);
        assert_eq!(first.unwrap().status, ScopedStatus::Applied);
        assert_eq!(second.unwrap().status, ScopedStatus::Unchanged);
        let mut state = scenario.state.lock().await;
        let lighting = state.lighting().unwrap();
        assert!(lighting.temporary && !lighting.saved.off && lighting.confirmed_off);
    }

    #[tokio::test]
    async fn interrupted_configuration_stays_fenced_across_renderer_retirement() {
        use openblade_windows_host::configuration_writer::fixture::Fixture;

        for known in [true, false] {
            let status: Value = serde_json::from_str(crate::wire::CURRENT_FIXTURE).unwrap();
            let (profiles, writer) = Fixture::create(true)
                .unwrap()
                .compose_battery(true)
                .unwrap();
            let scenario = Arc::new(
                Scenario::composed(
                    status["status"].clone(),
                    profiles.clone(),
                    Some(writer),
                    super::super::ProfileReplay::Integrated { application: false },
                )
                .unwrap(),
            );
            let script = {
                let mut state = scenario.state.lock().await;
                let lighting = state.lighting().unwrap();
                lighting.start_renderer().unwrap();
                lighting.script.take().unwrap()
            };
            script.started.await.unwrap();
            let mut watch = scenario.subscribe();
            let mut started = false;
            let off = scenario.apply_lighting(request(1), &mut started);
            let transaction = async {
                watch
                    .wait_for(|state| state["message"] == "Renderer frame admission closed.")
                    .await
                    .unwrap();
                let mut changed = profiles;
                changed["batteryPolicy"]["chargeLimitPercent"] = 75.into();
                assert_eq!(
                    Arc::clone(&scenario)
                        .save_profiles(changed, super::super::Limit::new(75).unwrap())
                        .await
                        .unwrap()
                        .0,
                    3
                );
                script.complete_frame.send(()).unwrap();
                script.frame_done.await.unwrap();
                script.release_port.send(known).unwrap();
            };
            let (off, ()) = tokio::time::timeout(std::time::Duration::from_secs(5), async {
                tokio::join!(off, transaction)
            })
            .await
            .unwrap();
            assert_eq!(
                off.unwrap().status,
                if known {
                    ScopedStatus::Blocked
                } else {
                    ScopedStatus::Failed
                }
            );
            assert!(started);
            let mut state = scenario.state.lock().await;
            assert!(state.configuration_blocked);
            assert_eq!(
                state.current["profiles"]["batteryPolicy"]["chargeLimitPercent"],
                77
            );
            assert_eq!(state.current["writesBlocked"], true);
            assert_eq!(
                state.current["unconfirmedWrites"],
                if known { json!([]) } else { json!([LIGHTING]) }
            );
            assert!(state.lighting().unwrap().device.trace.is_empty());
        }
    }

    #[tokio::test]
    async fn integrated_stop_without_port_acknowledgement_retains_lighting_fence() {
        use openblade_windows_host::configuration_writer::fixture::Fixture;

        let status: Value = serde_json::from_str(crate::wire::CURRENT_FIXTURE).unwrap();
        let (profiles, writer) = Fixture::create(true)
            .unwrap()
            .compose_battery(false)
            .unwrap();
        let scenario = Arc::new(
            Scenario::composed(
                status["status"].clone(),
                profiles,
                Some(writer),
                super::super::ProfileReplay::Integrated { application: false },
            )
            .unwrap(),
        );
        let (_steps, advance) = mpsc::channel(1);
        let (shutdown, wait_shutdown) = tokio::sync::oneshot::channel();
        let (release, wait_release) = tokio::sync::oneshot::channel();
        let mut watch = scenario.subscribe();
        let client = async {
            watch
                .wait_for(|state| state["message"] == "Renderer frame in flight.")
                .await
                .unwrap();
            shutdown.send(()).unwrap();
            watch
                .wait_for(|state| state["message"] == "Frame drained. Renderer port still owned.")
                .await
                .unwrap();
            drop(release);
        };
        let (outcome, ()) = tokio::time::timeout(std::time::Duration::from_secs(5), async {
            tokio::join!(
                scenario.drive_integrated(advance, wait_shutdown, wait_release),
                client
            )
        })
        .await
        .unwrap();
        assert!(outcome.is_err());
        let mut state = scenario.state.lock().await;
        assert!(!state.configuration_blocked);
        assert_eq!(state.current["lightingRendererOwnership"], 9);
        assert_eq!(state.current["unconfirmedWrites"], json!([LIGHTING]));
        assert!(state.current["confirmedState"]["lighting"].is_null());
        assert!(state.lighting().unwrap().device.trace.is_empty());
    }

    #[test]
    fn lighting_reports_match_pinned_exact_model_evidence() {
        let capture: Value = serde_json::from_str(include_str!(
            "../../../../evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-lighting-brightness-query-fixture.json"
        )).unwrap();
        let admission: Value = serde_json::from_str(include_str!(
            "../../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_LightingSemanticAdmission.json"
        )).unwrap();
        assert_eq!(capture["device"]["model"], admission["device"]["model"]);
        assert_eq!(capture["device"]["productId"], "02E0");
        assert_eq!(admission["device"]["productId"], 0x02e0);
        let query = report(1, GET_BRIGHTNESS, 0).unwrap();
        let encoded: String = query
            .encode()
            .iter()
            .map(|byte| format!("{byte:02X}"))
            .collect();
        assert_eq!(capture["exchange"]["requestEnvelopeHex"], encoded);
        assert_eq!(admission["brightness"]["query"], "03/83:010500");
        assert_eq!(admission["brightness"]["percent77Raw"], LIT);
        let set = report(3, SET_BRIGHTNESS, 0).unwrap().encode();
        assert_eq!(admission["brightness"]["setPrefix"], "03/03:0105");
        assert_eq!(&set[5..11], &[3, 3, 3, 1, 5, 0]);
        assert_eq!(set[88], 7);
    }

    #[test]
    fn lighting_off_matches_managed_port_and_scoped_reads() {
        let mut lighting = fixture();
        assert_eq!(
            lighting.apply(request(1), true).status,
            ScopedStatus::Applied
        );
        assert_eq!(
            lighting.device.trace,
            [
                (0x83, [1, 5, 0]),
                (0x83, [1, 5, 0]),
                (0x03, [1, 5, 0]),
                (0x83, [1, 5, 0]),
                (0x83, [1, 5, 0]),
            ]
        );
        assert!(lighting.saved.off && lighting.confirmed_off);
        assert_eq!(
            lighting.apply(request(0), true).status,
            ScopedStatus::Unchanged
        );
        assert!(lighting.temporary && !lighting.saved.off);
        assert_eq!(lighting.apply(None, true).status, ScopedStatus::Unchanged);
        assert!(!lighting.temporary && !lighting.saved.off);
    }

    #[test]
    fn lighting_admits_protocol_85_off_only_without_keyboard_effect_authority() {
        let off = |extra: Value| {
            let mut value = json!({
                "effect": 0, "brightness": 0, "color": {"red": 0, "green": 0, "blue": 0},
                "parameters": {"kind": "off"}
            });
            value
                .as_object_mut()
                .unwrap()
                .extend(extra.as_object().unwrap().clone());
            serde_json::from_value::<LightingOverride>(json!({"value": value, "lifetime": 1}))
        };
        // Managed writes the default by omission; an explicit false is the same Off.
        for extra in [json!({}), json!({"controlsKeyboardEffect": false})] {
            assert_eq!(
                off(extra).unwrap().off_lifetime(),
                Some(Lifetime::Persistent)
            );
        }
        // Full-effect authority and the protocol 84 zone fields are not admitted here.
        assert_eq!(
            off(json!({"controlsKeyboardEffect": true}))
                .unwrap()
                .off_lifetime(),
            None
        );
        for zone in [
            json!({"independentControls": {"vaporChamberLightingEnabled": false}}),
            json!({"independentControls": {"touchpadStatusIndicatorEnabled": true}}),
        ] {
            assert_eq!(off(zone).unwrap().off_lifetime(), None);
        }
        assert!(off(json!({"controlsKeyboardEffect": 1})).is_err());
        let mut lighting = fixture();
        let refused = off(json!({"controlsKeyboardEffect": true})).ok();
        assert_eq!(lighting.apply(refused, true).status, ScopedStatus::Blocked);
        assert!(lighting.device.trace.is_empty() && !lighting.saved.off);
    }

    #[test]
    fn lighting_refuses_a_setter_without_ids_for_confirmation_and_reconciliation() {
        let mut lighting = fixture();
        lighting.device.transaction = 249;
        assert_eq!(
            lighting.apply(request(1), true).status,
            ScopedStatus::Blocked
        );
        assert_eq!(lighting.device.trace, [(GET_BRIGHTNESS, [1, 5, 0]); 2]);
        assert_eq!(lighting.device.raw.load(Ordering::Relaxed), LIT);
        assert!(!lighting.pending);
    }

    #[test]
    fn lighting_lost_final_read_requires_read_only_confirmation_without_replay() {
        let mut lighting = fixture();
        lighting.device.controller_present = true;
        assert_eq!(
            lighting.apply(request(1), true).status,
            ScopedStatus::Blocked
        );
        assert!(lighting.saved.off && lighting.device.trace.is_empty());
        lighting.device.controller_present = false;
        lighting.device.lose_final_read = true;
        assert_eq!(
            lighting.apply(request(0), true).status,
            ScopedStatus::Failed
        );
        assert!(lighting.pending && lighting.temporary && !lighting.confirmed_off);
        assert!(lighting.start_renderer().is_err());
        assert_eq!(lighting.apply(None, true).status, ScopedStatus::Blocked);
        lighting.observe();
        assert!(lighting.pending && lighting.temporary && !lighting.confirmed_off);
        lighting.device.raw.store(0, Ordering::Relaxed);
        lighting.observe();
        assert!(!lighting.pending && lighting.temporary && lighting.confirmed_off);
        assert_eq!(
            lighting
                .device
                .trace
                .iter()
                .filter(|(op, _)| *op == SET_BRIGHTNESS)
                .count(),
            1
        );
    }
}
