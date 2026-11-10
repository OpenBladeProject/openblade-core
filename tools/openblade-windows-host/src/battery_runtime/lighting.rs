use super::{Result, Scenario, ScenarioState};
use openblade_domain::recovery::ScopedStatus;
use openblade_protocol::{REPORT_LENGTH, RazerReport};
use serde::Deserialize;
use serde_json::{Value, json};
use std::io;
use tokio::sync::mpsc;

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
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Color {
    red: u8,
    green: u8,
    blue: u8,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Parameters {
    kind: String,
}

impl LightingOverride {
    fn off_lifetime(self) -> Option<Lifetime> {
        let settings = self.value;
        if settings.effect != 0
            || settings.brightness != 0
            || settings.color.red != 0
            || settings.color.green != 0
            || settings.color.blue != 0
            || settings.parameters.is_some_and(|p| p.kind != "off")
            || settings.logo_mode.is_some()
            || settings.independent_controls.is_some()
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
    // The fixture has no producer or renderer port to retire. Its private lane
    // never admits an effect that can create one.
    device: Keyboard,
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
    raw: u8,
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
                        self.raw = LIT;
                        self.after_set_reads = None;
                        return None;
                    } else {
                        self.after_set_reads = Some(reads - 1);
                    }
                }
            }
            (SET_BRIGHTNESS, 0) => {
                self.raw = 0;
                if std::mem::take(&mut self.lose_final_read) {
                    self.after_set_reads = Some(1);
                }
            }
            _ => return None,
        }
        let mut reply = report(bytes[1], operation, self.raw)?.encode();
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
                raw: LIT,
                transaction: 0,
                controller_present: false,
                lose_final_read: false,
                after_set_reads: None,
                #[cfg(test)]
                trace: Vec::new(),
            },
            temporary: false,
            saved: SavedIntent::default(),
            confirmed_off: false,
            pending: false,
        })
    }

    fn apply(&mut self, value: Option<LightingOverride>, exact: bool) -> Result {
        if self.pending {
            return result(
                ScopedStatus::Blocked,
                "Keyboard Off awaits independent readback.",
            );
        }
        let lifetime = match value {
            Some(value) => match value.off_lifetime() {
                Some(lifetime) => Some(lifetime),
                None => return result(ScopedStatus::Blocked, "Only keyboard Off is admitted."),
            },
            None => None,
        };
        self.saved.off = lifetime == Some(Lifetime::Persistent);
        self.temporary = lifetime == Some(Lifetime::Temporary);

        // The admitted active pipe session and this scenario's mutex bound
        // the entire baseline / submission / confirmation transaction.
        if !exact || self.device.controller_present {
            return result(
                ScopedStatus::Blocked,
                "Lighting intent stored. Device write admission is unavailable.",
            );
        }
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
            if self.device.transaction == u8::MAX || self.device.controller_present {
                return result(
                    ScopedStatus::Blocked,
                    "Fresh lighting submission admission is unavailable.",
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
        self.confirmed_off = self.device.query() == Some(0);
        if self.confirmed_off {
            self.pending = false;
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
        self.confirmed_off = self.device.query() == Some(0);
        if self.confirmed_off {
            self.pending = false;
        }
    }

    pub(super) fn project(&self, status: &mut Value) {
        status["capabilities"]["chargeLimits"] = json!([]);
        status["capabilities"]["supportsBatteryProtectionOff"] = false.into();
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
        status["potentialControllers"] = if self.device.controller_present {
            json!(["scripted lighting controller"])
        } else {
            json!([])
        };
        if self.pending {
            status["writesBlocked"] = true.into();
            if let Some(unconfirmed) = status["unconfirmedWrites"].as_array_mut() {
                unconfirmed.push(LIGHTING.into());
            }
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
        &self,
        value: Option<LightingOverride>,
    ) -> io::Result<Result> {
        self.update(|state| {
            if state.configuration_blocked {
                return Ok(super::blocked_configuration());
            }
            if state.controller.pending.is_some() {
                return Ok(result(
                    ScopedStatus::Blocked,
                    "Battery write awaits independent readback.",
                ));
            }
            let exact = state.current["deviceVerified"] == true;
            Ok(state.lighting()?.apply(value, exact))
        })
        .await
    }

    pub(crate) async fn drive_lighting(&self, mut advance: mpsc::Receiver<()>) -> io::Result<()> {
        type Step = (&'static str, fn(&mut Lighting));
        let steps: [Step; 5] = [
            (
                "A competing lighting controller was observed.",
                |lighting| {
                    lighting.device.raw = LIT;
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
                    lighting.device.raw = 0;
                    lighting.observe();
                },
            ),
            (
                "Unused final brightness getter failure expired.",
                |lighting| {
                    lighting.device.lose_final_read = false;
                },
            ),
        ];
        for (message, step) in steps {
            if advance.recv().await.is_none() {
                return Ok(());
            }
            self.update(|state| {
                step(state.lighting()?);
                Ok(result(ScopedStatus::Unchanged, message))
            })
            .await?;
        }
        while advance.recv().await.is_some() {}
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture() -> Lighting {
        let mut status: Value = serde_json::from_str(crate::wire::CURRENT_FIXTURE).unwrap();
        Lighting::new(&mut status["status"]).unwrap()
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
        assert_eq!(lighting.apply(None, true).status, ScopedStatus::Blocked);
        lighting.observe();
        assert!(lighting.pending && lighting.temporary && !lighting.confirmed_off);
        lighting.device.raw = 0;
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
