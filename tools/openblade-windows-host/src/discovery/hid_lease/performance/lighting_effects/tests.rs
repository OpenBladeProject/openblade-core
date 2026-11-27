//! Replays `lighting-effect-vectors.json`, recorded from the managed effects
//! by `NativeLightingEffectVectorTests`: every frame, delay, cadence and
//! deadline matches managed, with no deviation.
use super::*;
use openblade_protocol::lighting::matrix::{RegionalProjection, encode_feature_report};
use std::collections::VecDeque;

const VECTORS: &str = include_str!("../../../../../tests/data/lighting-effect-vectors.json");

/// A palette selector that returns the row's indices in order, and the
/// row's seed, counting each draw.
pub(crate) struct Scripted {
    pub(crate) indices: VecDeque<u32>,
    pub(crate) seed: [u8; 32],
    pub(crate) draws: std::sync::Arc<std::sync::atomic::AtomicUsize>,
}

impl Random for Scripted {
    fn index(&mut self, bound: u32) -> Option<u32> {
        self.draws
            .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        self.indices.pop_front().filter(|&index| index < bound)
    }

    fn seed(&mut self) -> Option<[u8; 32]> {
        Some(self.seed)
    }
}

/// A source that always fails, as a failed `BCryptGenRandom` would.
pub(crate) struct Failing;

impl Random for Failing {
    fn index(&mut self, _: u32) -> Option<u32> {
        None
    }

    fn seed(&mut self) -> Option<[u8; 32]> {
        None
    }
}

fn rgb(value: &Value) -> Option<Rgb> {
    let text = value.as_str()?;
    let byte = |at: usize| u8::from_str_radix(text.get(at..at + 2)?, 16).ok();
    Some(Rgb {
        red: byte(0)?,
        green: byte(2)?,
        blue: byte(4)?,
    })
}

fn mode(value: &Value) -> ColorMode {
    match value.as_str() {
        Some("Single") => ColorMode::Single,
        Some("Dual") => ColorMode::Dual,
        Some("Random") => ColorMode::Random,
        other => panic!("mode {other:?}"),
    }
}

fn effect(spec: &Value) -> Effect {
    let direction = |name: &str| spec["direction"] == name;
    match spec["kind"].as_str().unwrap() {
        "Spectrum" => Effect::Spectrum,
        "Breathing" => Effect::Breathing {
            mode: mode(&spec["mode"]),
            primary: rgb(&spec["primary"]),
            secondary: rgb(&spec["secondary"]),
        },
        "Wave" => Effect::Wave {
            rightward: direction("Rightward"),
        },
        "Wheel" => Effect::Wheel {
            counterclockwise: direction("Counterclockwise"),
        },
        "Tidal" => Effect::Tidal {
            primary: rgb(&spec["primary"]).unwrap(),
            secondary: rgb(&spec["secondary"]).unwrap(),
            random: spec["random"].as_bool().unwrap(),
            rightward: direction("Rightward"),
        },
        "Starlight" => Effect::Starlight {
            mode: mode(&spec["mode"]),
            primary: rgb(&spec["primary"]),
            secondary: rgb(&spec["secondary"]),
            duration: match spec["duration"].as_str().unwrap() {
                "Short" => 500 * 10_000,
                "Medium" => 1500 * 10_000,
                "Long" => 2000 * 10_000,
                other => panic!("duration {other}"),
            },
        },
        "Fire" => Effect::Fire,
        other => panic!("effect {other}"),
    }
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02X}")).collect()
}

fn rows() -> Vec<Value> {
    let root: Value = serde_json::from_str(VECTORS).unwrap();
    let rows = root["rows"].as_array().unwrap().clone();
    assert!(!rows.is_empty());
    rows
}

fn rate(value: &Value) -> RefreshRate {
    match value.as_str() {
        Some("ThirtyHz") => RefreshRate::ThirtyHz,
        Some("SixtyHz") => RefreshRate::SixtyHz,
        other => panic!("rate {other:?}"),
    }
}

#[test]
fn lighting_effect_vectors_constants_match_managed() {
    let row = rows()
        .into_iter()
        .find(|row| row["kind"] == "constants")
        .unwrap();
    let expect = &row["expect"];
    assert_eq!(expect["animatedFrameInterval"], ANIMATED_FRAME_INTERVAL);
    assert_eq!(expect["sixtyHzInterval"], SIXTY_HZ_INTERVAL);
    assert_eq!(expect["idleKeepaliveInterval"], Effect::Static.delay(0));
    assert_eq!(expect["spectrumCycle"], SPECTRUM_CYCLE);
    assert_eq!(expect["breathingCycle"], BREATHING_CYCLE);
    assert_eq!(expect["tidalCycle"], TIDAL_CYCLE);
    assert_eq!(
        expect["starlightDurations"],
        serde_json::json!([5_000_000, 15_000_000, 20_000_000])
    );
}

#[test]
fn lighting_effect_vectors_frames_match_managed() {
    let maps = super::super::lighting_layout::source_maps().unwrap();
    let mut checked = 0;
    for row in rows().into_iter().filter(|row| row["kind"] == "frames") {
        let name = row["name"].as_str().unwrap();
        let effect = effect(&row["effect"]);
        let mut seed = [0u8; 32];
        if let Some(text) = row["seed"].as_str() {
            for (index, byte) in seed.iter_mut().enumerate() {
                *byte = u8::from_str_radix(&text[index * 2..index * 2 + 2], 16).unwrap();
            }
        }
        let draws = std::sync::Arc::default();
        let random = Scripted {
            indices: row["random"]
                .as_array()
                .map(|values| values.iter().map(|v| v.as_u64().unwrap() as u32).collect())
                .unwrap_or_default(),
            seed,
            draws: std::sync::Arc::clone(&draws),
        };
        let mut animation = Animation::new(effect, Rgb::default(), Box::new(random)).unwrap();
        let samples = row["expect"]["samples"].as_array().unwrap();
        let elapsed = row["elapsed"].as_array().unwrap();
        assert_eq!(samples.len(), elapsed.len(), "{name}");
        for (tick, expect) in elapsed.iter().zip(samples) {
            let tick = tick.as_i64().unwrap();
            let delay = effect.delay(tick);
            assert_eq!(expect["delay"], delay, "{name} at {tick}");
            assert_eq!(
                expect["thirtyHz"],
                effect.cadence(delay, RefreshRate::ThirtyHz),
                "{name} at {tick}"
            );
            assert_eq!(
                expect["sixtyHz"],
                effect.cadence(delay, RefreshRate::SixtyHz),
                "{name} at {tick}"
            );
            let frame = animation.render(tick).unwrap();
            let canvas: Vec<u8> = frame
                .slots()
                .iter()
                .flat_map(|color| [color.red, color.green, color.blue])
                .collect();
            assert_eq!(expect["canvas"], hex(&canvas), "{name} at {tick}");
            for (index, layout) in [0u8, 255].into_iter().enumerate() {
                let projected = RegionalProjection::new(&maps.layout(), layout).project(&frame);
                let report = encode_feature_report(1, &projected);
                assert_eq!(
                    expect["reports"][index],
                    hex(&Sha256::digest(report)),
                    "{name} at {tick} on layout {layout}"
                );
            }
            checked += 1;
        }
        assert_eq!(
            row["expect"]["draws"],
            draws.load(std::sync::atomic::Ordering::Relaxed),
            "{name}"
        );
    }
    // Every sample the managed generator recorded.
    assert_eq!(checked, 91);
}

#[test]
fn lighting_effect_vectors_deadlines_match_managed() {
    let mut checked = 0;
    for row in rows().into_iter().filter(|row| row["kind"] == "advance") {
        let effect = effect(&row["effect"]);
        let next = effect.advance(
            row["deadline"].as_i64().unwrap(),
            row["observed"].as_i64().unwrap(),
            rate(&row["rate"]),
        );
        assert_eq!(row["expect"], next, "{}", row["name"]);
        checked += 1;
    }
    assert_eq!(checked, 8);
}

#[test]
fn oracle_fixtures_load_and_refuse_any_other_bytes() {
    assert!(wave_wheel().is_some());
    assert!(tidal().is_some());
    let mut altered = WAVE_WHEEL_FIXTURE.to_vec();
    let at = altered.len() / 2;
    altered[at] ^= 1;
    assert!(load_wave_wheel(&altered).is_none());
    let mut altered = TIDAL_FIXTURE.to_vec();
    let at = altered.len() / 2;
    altered[at] ^= 1;
    assert!(load_tidal(&altered).is_none());
}

#[test]
fn a_failed_random_source_renders_nothing() {
    let random = Effect::Breathing {
        mode: ColorMode::Random,
        primary: None,
        secondary: None,
    };
    let mut animation = Animation::new(random, Rgb::default(), Box::new(Failing)).unwrap();
    assert!(animation.render(0).is_none());
    let tidal = Effect::Tidal {
        primary: Rgb::default(),
        secondary: Rgb::default(),
        random: true,
        rightward: true,
    };
    assert!(Animation::new(tidal, Rgb::default(), Box::new(Failing)).is_none());
}

#[test]
fn refresh_rates_name_managed_wire_values_and_labels() {
    assert_eq!(RefreshRate::from_wire(0), Some(RefreshRate::ThirtyHz));
    assert_eq!(RefreshRate::from_wire(1), Some(RefreshRate::SixtyHz));
    assert_eq!(RefreshRate::from_wire(2), None);
    assert_eq!(RefreshRate::SixtyHz.wire(), 1);
    assert_eq!(RefreshRate::ThirtyHz.label(), "30 Hz");
    assert_eq!(RefreshRate::SixtyHz.label(), "60 Hz");
    // Static keeps its 5 s keepalive at either rate.
    assert_eq!(
        Effect::Static.next_delay(0, RefreshRate::SixtyHz),
        5 * TICKS_PER_SECOND
    );
}

#[test]
fn the_system_random_source_draws_in_range() {
    let mut random = SystemRandom;
    for _ in 0..64 {
        assert!(random.index(24).unwrap() < 24);
    }
    assert!(random.seed().is_some());
    assert_eq!(random.index(0), None);
}
