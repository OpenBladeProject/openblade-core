//! Managed's RZ09-0581 animated lighting effects (`Pid02E0LightingEffects`,
//! `Pid02E0WaveWheelEffects`, `Pid02E0TidalEffect`, `Pid02E0FireEffect`,
//! `SoftwareFirePattern`, `Pid02E0StarlightEffect` and `StarlightPattern`),
//! and the stream cadence of `ProductionLightingPort` (`NextFrameDelay`,
//! `ApplyRefreshRateToDelay` and `AdvanceFrameDeadline`), ported exactly.
//!
//! Time is in managed `TimeSpan` ticks of 100 ns, and every floating-point
//! expression keeps managed's evaluation order, so each frame is the managed
//! frame byte for byte. `lighting_effect_vectors` replays frames, delays and
//! deadlines recorded from the managed implementation.
//!
//! Each effect renders the stable logical canvas; the write boundary projects
//! it onto the session layout (`render::frame`). Wave, Wheel and Tidal
//! interpolate between the frames of the reviewed sanitized oracle fixtures,
//! embedded byte for byte and checked as managed checks them. A fixture that
//! fails its pin or its metadata leaves those effects unsupported.
use openblade_protocol::lighting::matrix::{COLUMNS, MatrixFrame, Rgb, SLOT_COUNT, is_canvas_hole};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::sync::OnceLock;

mod inflate;

/// Managed `TimeSpan.TicksPerSecond`.
pub(crate) const TICKS_PER_SECOND: i64 = 10_000_000;
/// Managed `AnimatedFrameInterval`: thirty frames per second.
pub(crate) const ANIMATED_FRAME_INTERVAL: i64 = TICKS_PER_SECOND / 30;
/// Managed `RefreshInterval(SixtyHz)`.
const SIXTY_HZ_INTERVAL: i64 = TICKS_PER_SECOND / 60;
/// Managed `BreathingPlateauStreamDuration`.
const BREATHING_PLATEAU_STREAM: i64 = TICKS_PER_SECOND;
/// Managed `SpectrumCycle`, `TimeSpan.FromMilliseconds(37_708.802)`.
pub(crate) const SPECTRUM_CYCLE: i64 = 377_088_020;
/// Managed `BreathingCycle`.
const BREATHING_CYCLE: i64 = 7 * TICKS_PER_SECOND;
/// Managed `SoftwareFirePattern`'s cycle.
const FIRE_CYCLE: i64 = 12 * TICKS_PER_SECOND;
const WAVE_WHEEL_FRAMES: usize = 25;
const WAVE_WHEEL_CYCLE: i64 = TICKS_PER_SECOND;
const WAVE_WHEEL_INTERVAL: i64 = 40 * 10_000;
const TIDAL_FRAMES: usize = 100;
pub(crate) const TIDAL_CYCLE: i64 = 5 * TICKS_PER_SECOND;
const TIDAL_INTERVAL: i64 = 50 * 10_000;

/// Managed `LightingRefreshRate`: the selectable animated cadence.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) enum RefreshRate {
    #[default]
    ThirtyHz,
    SixtyHz,
}

impl RefreshRate {
    pub(crate) fn from_wire(value: i32) -> Option<Self> {
        match value {
            0 => Some(Self::ThirtyHz),
            1 => Some(Self::SixtyHz),
            _ => None,
        }
    }

    pub(crate) fn wire(self) -> i32 {
        match self {
            Self::ThirtyHz => 0,
            Self::SixtyHz => 1,
        }
    }

    /// Managed `RefreshRateLabel`.
    pub(crate) fn label(self) -> &'static str {
        match self {
            Self::ThirtyHz => "30 Hz",
            Self::SixtyHz => "60 Hz",
        }
    }

    fn interval(self) -> i64 {
        match self {
            Self::ThirtyHz => ANIMATED_FRAME_INTERVAL,
            Self::SixtyHz => SIXTY_HZ_INTERVAL,
        }
    }
}

/// Managed `BreathingColorMode` and `StarlightColorMode`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum ColorMode {
    Single,
    Dual,
    Random,
}

/// The effect a renderer draws, with every parameter managed's
/// `CanRetainRenderer` compares: two requests retain one renderer only when
/// their effects are equal and their settings colors are too.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Effect {
    /// The settings color on every canvas key, with the 5 s keepalive.
    Static,
    Spectrum,
    Breathing {
        mode: ColorMode,
        primary: Option<Rgb>,
        secondary: Option<Rgb>,
    },
    Wave {
        rightward: bool,
    },
    Wheel {
        counterclockwise: bool,
    },
    Tidal {
        primary: Rgb,
        secondary: Rgb,
        random: bool,
        rightward: bool,
    },
    Starlight {
        mode: ColorMode,
        primary: Option<Rgb>,
        secondary: Option<Rgb>,
        /// The twinkle duration in ticks: 500, 1500 or 2000 ms.
        duration: i64,
    },
    Fire,
}

impl Effect {
    /// Whether the dedicated stream draws it; Static keeps its keepalive.
    pub(crate) fn animated(self) -> bool {
        self != Self::Static
    }

    /// Managed `GetNextFrameDelay` before the refresh rate applies.
    pub(crate) fn delay(self, elapsed: i64) -> i64 {
        match self {
            Self::Static => 5 * TICKS_PER_SECOND,
            Self::Breathing { .. } => breathing_delay(elapsed, BREATHING_CYCLE),
            _ => ANIMATED_FRAME_INTERVAL,
        }
    }

    /// Managed `ApplyRefreshRateToDelay`: the selected interval replaces the
    /// animated one, and Breathing keeps its plateau with only the trailing
    /// interval replaced. Every effect this module animates uses the
    /// selectable cadence (`UsesSelectableCadence`); Static does not.
    pub(crate) fn cadence(self, delay: i64, rate: RefreshRate) -> i64 {
        if !self.animated() || rate == RefreshRate::ThirtyHz {
            return delay;
        }
        if delay == ANIMATED_FRAME_INTERVAL {
            return rate.interval();
        }
        if matches!(self, Self::Breathing { .. }) && delay > ANIMATED_FRAME_INTERVAL {
            return delay - ANIMATED_FRAME_INTERVAL + rate.interval();
        }
        delay
    }

    /// Managed `NextFrameDelay`.
    pub(crate) fn next_delay(self, elapsed: i64, rate: RefreshRate) -> i64 {
        self.cadence(self.delay(elapsed), rate)
    }

    /// Managed `AdvanceFrameDeadline`: the first deadline on the effect's
    /// own cadence after `observed`, so a late frame skips the deadlines it
    /// missed.
    pub(crate) fn advance(self, deadline: i64, observed: i64, rate: RefreshRate) -> i64 {
        let mut cursor = deadline;
        while cursor <= observed {
            cursor += self.next_delay(cursor, rate);
        }
        cursor
    }
}

/// Where a random effect's colors come from: managed's
/// `RandomNumberGenerator`, behind an injected source so tests and vectors
/// are deterministic. `None` is a source that failed.
pub(crate) trait Random: Send {
    /// A uniform index below `bound` (managed `GetInt32(bound)`).
    fn index(&mut self, bound: u32) -> Option<u32>;
    /// A 32-byte seed (managed `GetBytes(32)`).
    fn seed(&mut self) -> Option<[u8; 32]>;
}

/// The system-preferred RNG through `BCryptGenRandom`.
pub(crate) struct SystemRandom;

impl SystemRandom {
    #[allow(unsafe_code)]
    fn fill(buffer: &mut [u8]) -> Option<()> {
        use windows_sys::Win32::Security::Cryptography::{
            BCRYPT_USE_SYSTEM_PREFERRED_RNG, BCryptGenRandom,
        };
        // SAFETY: a writable buffer of the given length and no algorithm
        // handle, as BCRYPT_USE_SYSTEM_PREFERRED_RNG requires.
        let status = unsafe {
            BCryptGenRandom(
                std::ptr::null_mut(),
                buffer.as_mut_ptr(),
                u32::try_from(buffer.len()).ok()?,
                BCRYPT_USE_SYSTEM_PREFERRED_RNG,
            )
        };
        (status == 0).then_some(())
    }
}

impl Random for SystemRandom {
    fn index(&mut self, bound: u32) -> Option<u32> {
        if bound == 0 {
            return None;
        }
        // Rejection sampling keeps the index uniform.
        let zone = u32::MAX - u32::MAX % bound;
        for _ in 0..64 {
            let mut bytes = [0u8; 4];
            Self::fill(&mut bytes)?;
            let value = u32::from_le_bytes(bytes);
            if value < zone {
                return Some(value % bound);
            }
        }
        None
    }

    fn seed(&mut self) -> Option<[u8; 32]> {
        let mut seed = [0u8; 32];
        Self::fill(&mut seed)?;
        Some(seed)
    }
}

/// Managed `Pid02E0RandomPaletteColorSource.CapturedPalette`.
const PALETTE: [Rgb; 24] = {
    const fn c(red: u8, green: u8, blue: u8) -> Rgb {
        Rgb { red, green, blue }
    }
    [
        c(0x00, 0x00, 0x80),
        c(0x00, 0x00, 0xFF),
        c(0x00, 0x80, 0x00),
        c(0x00, 0x80, 0x80),
        c(0x00, 0x80, 0xFF),
        c(0x00, 0xFF, 0x00),
        c(0x00, 0xFF, 0x80),
        c(0x00, 0xFF, 0xFF),
        c(0x80, 0x00, 0x00),
        c(0x80, 0x00, 0x80),
        c(0x80, 0x00, 0xFF),
        c(0x80, 0x80, 0x00),
        c(0x80, 0x80, 0xFF),
        c(0x80, 0xFF, 0x00),
        c(0x80, 0xFF, 0x80),
        c(0x80, 0xFF, 0xFF),
        c(0xFF, 0x00, 0x00),
        c(0xFF, 0x00, 0x80),
        c(0xFF, 0x00, 0xFF),
        c(0xFF, 0x80, 0x00),
        c(0xFF, 0x80, 0x80),
        c(0xFF, 0x80, 0xFF),
        c(0xFF, 0xFF, 0x00),
        c(0xFF, 0xFF, 0x80),
    ]
};

/// Managed `SpectrumAnchors`.
const SPECTRUM_ANCHORS: [Rgb; 51] = {
    const fn c(red: u8, green: u8, blue: u8) -> Rgb {
        Rgb { red, green, blue }
    }
    [
        c(0xFF, 0x10, 0x10),
        c(0xFF, 0x25, 0x0C),
        c(0xFF, 0x3B, 0x09),
        c(0xFF, 0x52, 0x06),
        c(0xFF, 0x68, 0x03),
        c(0xFF, 0x7E, 0x00),
        c(0xFA, 0xA1, 0x00),
        c(0xF6, 0xC7, 0x00),
        c(0xF1, 0xEC, 0x00),
        c(0xED, 0xFF, 0x00),
        c(0xE8, 0xFF, 0x00),
        c(0xE3, 0xFF, 0x00),
        c(0xDF, 0xFF, 0x00),
        c(0xA4, 0xFF, 0x00),
        c(0x63, 0xFF, 0x00),
        c(0x21, 0xFF, 0x00),
        c(0x00, 0xFF, 0x4E),
        c(0x00, 0xFF, 0xFF),
        c(0x03, 0xE4, 0xFF),
        c(0x07, 0xC8, 0xFF),
        c(0x0B, 0xAC, 0xFF),
        c(0x0F, 0x90, 0xFF),
        c(0x13, 0x74, 0xFF),
        c(0x18, 0x58, 0xFF),
        c(0x18, 0x58, 0xFF),
        c(0x18, 0x58, 0xFF),
        c(0x18, 0x58, 0xFF),
        c(0x18, 0x58, 0xFF),
        c(0x19, 0x57, 0xFF),
        c(0x19, 0x57, 0xFF),
        c(0x1F, 0x55, 0xFF),
        c(0x34, 0x50, 0xFF),
        c(0x49, 0x4A, 0xFF),
        c(0x5C, 0x45, 0xFF),
        c(0x70, 0x3F, 0xFF),
        c(0x85, 0x39, 0xFF),
        c(0x99, 0x34, 0xFF),
        c(0xAC, 0x2E, 0xFF),
        c(0xC1, 0x29, 0xFF),
        c(0xD6, 0x23, 0xFF),
        c(0xEA, 0x1D, 0xFF),
        c(0xFD, 0x18, 0xFF),
        c(0xFF, 0x17, 0xE7),
        c(0xFF, 0x16, 0xCB),
        c(0xFF, 0x15, 0xB1),
        c(0xFF, 0x14, 0x95),
        c(0xFF, 0x13, 0x7B),
        c(0xFF, 0x12, 0x60),
        c(0xFF, 0x11, 0x46),
        c(0xFF, 0x10, 0x2B),
        c(0xFF, 0x10, 0x10),
    ]
};

/// One running effect and the random state managed's effect object keeps:
/// Breathing Random's color for its last cycle and Tidal Random's seed. A
/// request's first frame and the stream's later frames render from the same
/// animation, so a random color never changes within a cycle.
pub(crate) struct Animation {
    effect: Effect,
    color: Rgb,
    cycle: Option<(i64, Rgb)>,
    seed: [u8; 32],
    random: Box<dyn Random>,
}

impl std::fmt::Debug for Animation {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("Animation")
            .field("effect", &self.effect)
            .finish_non_exhaustive()
    }
}

impl Animation {
    /// `None` when an oracle effect's fixture did not load, or a random
    /// Tidal could not draw its seed (managed's effect constructor throws).
    pub(crate) fn new(effect: Effect, color: Rgb, mut random: Box<dyn Random>) -> Option<Self> {
        match effect {
            Effect::Wave { .. } | Effect::Wheel { .. } => {
                wave_wheel()?;
            }
            Effect::Tidal { .. } => {
                tidal()?;
            }
            _ => {}
        }
        let seed = match effect {
            Effect::Tidal { random: true, .. } => random.seed()?,
            _ => [0; 32],
        };
        Some(Self {
            effect,
            color,
            cycle: None,
            seed,
            random,
        })
    }

    pub(crate) fn effect(&self) -> Effect {
        self.effect
    }

    /// Managed `RenderFrame(elapsed)`. `None` when a random source failed.
    pub(crate) fn render(&mut self, elapsed: i64) -> Option<MatrixFrame> {
        let elapsed = elapsed.max(0);
        Some(match self.effect {
            Effect::Static => MatrixFrame::uniform(self.color),
            Effect::Spectrum => {
                MatrixFrame::uniform(spectrum_color(phase(elapsed, SPECTRUM_CYCLE)))
            }
            Effect::Breathing {
                mode,
                primary,
                secondary,
            } => {
                let cycle = elapsed / BREATHING_CYCLE;
                let intensity = breathing_intensity(phase(elapsed, BREATHING_CYCLE));
                let color = match mode {
                    ColorMode::Single => primary?,
                    ColorMode::Dual if cycle & 1 == 0 => primary?,
                    ColorMode::Dual => secondary?,
                    ColorMode::Random => self.cycle_color(cycle)?,
                };
                MatrixFrame::uniform(scale(color, intensity))
            }
            Effect::Wave { rightward } => {
                let oracle = wave_wheel()?;
                let template = if rightward {
                    &oracle.wave_rightward
                } else {
                    &oracle.wave_leftward
                };
                interpolate_rgb(template, elapsed)
            }
            Effect::Wheel { counterclockwise } => {
                let oracle = wave_wheel()?;
                let template = if counterclockwise {
                    &oracle.wheel_counterclockwise
                } else {
                    &oracle.wheel_clockwise
                };
                interpolate_rgb(template, elapsed)
            }
            Effect::Tidal {
                primary,
                secondary,
                random,
                rightward,
            } => {
                let (first, second) = if random {
                    tidal_pair(&self.seed, elapsed / TIDAL_CYCLE)
                } else {
                    (primary, secondary)
                };
                let oracle = tidal()?;
                let template = if rightward {
                    &oracle.rightward
                } else {
                    &oracle.leftward
                };
                interpolate_tidal(template, elapsed, first, second)
            }
            Effect::Starlight {
                mode,
                primary,
                secondary,
                duration,
            } => {
                let mut slots = [Rgb::default(); SLOT_COUNT];
                for (slot, value) in slots.iter_mut().enumerate() {
                    if !is_canvas_hole(slot) {
                        *value = starlight(slot, elapsed, mode, primary, secondary, duration);
                    }
                }
                MatrixFrame::new(slots, false)
            }
            Effect::Fire => {
                let mut slots = [Rgb::default(); SLOT_COUNT];
                for (slot, value) in slots.iter_mut().enumerate() {
                    if !is_canvas_hole(slot) {
                        *value = fire(slot / COLUMNS, slot % COLUMNS, elapsed);
                    }
                }
                MatrixFrame::new(slots, false)
            }
        })
    }

    /// Managed `ColorForCycle`: one draw per new cycle, kept for that cycle.
    fn cycle_color(&mut self, cycle: i64) -> Option<Rgb> {
        if let Some((selected, color)) = self.cycle
            && selected == cycle
        {
            return Some(color);
        }
        let index = self.random.index(PALETTE.len() as u32)?;
        let color = *PALETTE.get(index as usize)?;
        self.cycle = Some((cycle, color));
        Some(color)
    }
}

/// Managed `CyclePhase`.
fn phase(elapsed: i64, cycle: i64) -> f64 {
    (elapsed % cycle) as f64 / cycle as f64
}

/// Managed `Pid02E0LightingMath.ToByte`: rounded half away from zero after
/// a 1e-9 nudge, then clamped.
fn to_byte(value: f64) -> u8 {
    (value + 1e-9).round().clamp(0.0, 255.0) as u8
}

/// Managed `MatrixRgb.Scale`.
fn scale(color: Rgb, intensity: u8) -> Rgb {
    let channel = |value: u8| ((u32::from(value) * u32::from(intensity) + 127) / 255) as u8;
    Rgb {
        red: channel(color.red),
        green: channel(color.green),
        blue: channel(color.blue),
    }
}

/// Managed `InterpolateColor`.
fn interpolate(from: Rgb, to: Rgb, fraction: f64) -> Rgb {
    let channel = |from: u8, to: u8| {
        to_byte(f64::from(from) + (f64::from(i32::from(to) - i32::from(from)) * fraction))
    };
    Rgb {
        red: channel(from.red, to.red),
        green: channel(from.green, to.green),
        blue: channel(from.blue, to.blue),
    }
}

/// Managed `SpectrumColor`.
fn spectrum_color(phase: f64) -> Rgb {
    let phase = phase - phase.floor();
    let position = phase * (SPECTRUM_ANCHORS.len() - 1) as f64;
    let lower = position.floor() as usize;
    let fraction = position - lower as f64;
    interpolate(
        SPECTRUM_ANCHORS[lower],
        SPECTRUM_ANCHORS[lower + 1],
        fraction,
    )
}

/// Managed `BreathingIntensity`: black, rise, full color and fall, a
/// quarter each.
fn breathing_intensity(phase: f64) -> u8 {
    let phase = phase - phase.floor();
    if phase < 0.25 {
        0
    } else if phase < 0.50 {
        to_byte(((phase - 0.25) * 4.0) * 255.0)
    } else if phase < 0.75 {
        255
    } else {
        to_byte(((1.0 - phase) * 4.0) * 255.0)
    }
}

/// Managed `BreathingFrameDelay`: the animated interval on the ramps and
/// the first second of each plateau, then one jump to one interval into the
/// next ramp.
fn breathing_delay(elapsed: i64, cycle: i64) -> i64 {
    let quarter = cycle / 4;
    if quarter <= 0 {
        return ANIMATED_FRAME_INTERVAL;
    }
    let ticks = elapsed % cycle;
    let index = (ticks / quarter).min(3);
    if matches!(index, 1 | 3) {
        return ANIMATED_FRAME_INTERVAL;
    }
    let offset = ticks - index * quarter;
    if offset < BREATHING_PLATEAU_STREAM.min(quarter) {
        return ANIMATED_FRAME_INTERVAL;
    }
    quarter - offset + ANIMATED_FRAME_INTERVAL
}

/// Managed `Pid02E0WaveWheelOracle.Render`.
fn interpolate_rgb(template: &[Rgb], elapsed: i64) -> MatrixFrame {
    let position = (elapsed % WAVE_WHEEL_CYCLE) as f64 / WAVE_WHEEL_INTERVAL as f64;
    let lower = position.floor() as usize;
    let upper = (lower + 1) % WAVE_WHEEL_FRAMES;
    let fraction = position - lower as f64;
    let mut slots = [Rgb::default(); SLOT_COUNT];
    for (slot, value) in slots.iter_mut().enumerate() {
        *value = interpolate(
            template[lower * SLOT_COUNT + slot],
            template[upper * SLOT_COUNT + slot],
            fraction,
        );
    }
    MatrixFrame::new(slots, false)
}

/// Managed `Pid02E0TidalOracle.Render`.
fn interpolate_tidal(template: &[i16], elapsed: i64, primary: Rgb, secondary: Rgb) -> MatrixFrame {
    let color = |value: i16| match value {
        1.. => scale(primary, value as u8),
        ..0 => scale(secondary, (-value) as u8),
        0 => Rgb::default(),
    };
    let position = (elapsed % TIDAL_CYCLE) as f64 / TIDAL_INTERVAL as f64;
    let lower = position.floor() as usize;
    let upper = (lower + 1) % TIDAL_FRAMES;
    let fraction = position - lower as f64;
    let mut slots = [Rgb::default(); SLOT_COUNT];
    for (slot, value) in slots.iter_mut().enumerate() {
        *value = interpolate(
            color(template[lower * SLOT_COUNT + slot]),
            color(template[upper * SLOT_COUNT + slot]),
            fraction,
        );
    }
    MatrixFrame::new(slots, false)
}

/// Managed `Pid02E0RandomPaletteColorPairSource.ColorsForCycle`: two
/// distinct palette colors from SHA-256 of the seed and the cycle index.
fn tidal_pair(seed: &[u8; 32], cycle: i64) -> (Rgb, Rgb) {
    let mut hasher = Sha256::new();
    hasher.update(seed);
    hasher.update(cycle.to_le_bytes());
    let digest = hasher.finalize();
    let word = |offset: usize| {
        u32::from_le_bytes([
            digest[offset],
            digest[offset + 1],
            digest[offset + 2],
            digest[offset + 3],
        ])
    };
    let count = PALETTE.len() as u32;
    let first = word(0) % count;
    let mut second = word(4) % (count - 1);
    if second >= first {
        second += 1;
    }
    (PALETTE[first as usize], PALETTE[second as usize])
}

/// Managed `SoftwareFirePattern.Render`, whose own `ToByte` rounds half to
/// even and adds no nudge.
fn fire(row: usize, column: usize, elapsed: i64) -> Rgb {
    let to_byte = |value: f64| value.round_ties_even().clamp(0.0, 255.0) as u8;
    let (row, column) = (row as f64, column as f64);
    let time = (elapsed % FIRE_CYCLE) as f64 / FIRE_CYCLE as f64 * 2.0 * std::f64::consts::PI;
    let flicker = 0.5
        + 0.25 * (column * 1.7 + row * 0.9 - time * 3.0).sin()
        + 0.25 * (column * 0.6 - row * 1.3 + time * 5.0).sin();
    let heat = (0.2 + row * 0.1 + flicker * 0.3).clamp(0.0, 1.0);
    Rgb {
        red: to_byte(heat * 255.0),
        green: to_byte(heat * heat * 140.0),
        blue: 0,
    }
}

/// Managed `StarlightPattern.Mix`.
fn mix(value: u32) -> u32 {
    let mut value = value;
    value ^= value >> 16;
    value = value.wrapping_mul(0x7FEB_352D);
    value ^= value >> 15;
    value = value.wrapping_mul(0x846C_A68B);
    value ^ (value >> 16)
}

/// Managed `StarlightPattern.RenderSlot`.
fn starlight(
    slot: usize,
    elapsed: i64,
    mode: ColorMode,
    primary: Option<Rgb>,
    secondary: Option<Rgb>,
    duration: i64,
) -> Rgb {
    let seconds = |ticks: i64| ticks as f64 / TICKS_PER_SECOND as f64;
    let normalized = seconds(elapsed) / seconds(duration);
    let slot_hash = mix(slot as u32 + 1);
    let period = 8 + i64::from(slot_hash % 5);
    let fraction = f64::from(mix(slot_hash ^ 0x9E37_79B9)) / (f64::from(u32::MAX) + 1.0);
    let first_start = fraction * period as f64;
    let since_first = normalized - first_start;
    if since_first < 0.0 {
        return Rgb::default();
    }
    let event = (since_first / period as f64).floor() as i64;
    let age = since_first - (event * period) as f64;
    if age >= 1.0 {
        return Rgb::default();
    }
    let intensity = if age <= 0.5 {
        age * 2.0
    } else {
        (1.0 - age) * 2.0
    };
    let event_hash = mix(slot_hash ^ (event as u32).wrapping_mul(0x85EB_CA6B));
    let color = match mode {
        ColorMode::Single => primary.unwrap_or_default(),
        ColorMode::Dual if event_hash & 1 == 0 => primary.unwrap_or_default(),
        ColorMode::Dual => secondary.unwrap_or_default(),
        ColorMode::Random => PALETTE[(event_hash % PALETTE.len() as u32) as usize],
    };
    scale(color, to_byte(intensity * 255.0))
}

/// The four decoded Wave and Wheel templates.
struct WaveWheel {
    wave_rightward: Vec<Rgb>,
    wave_leftward: Vec<Rgb>,
    wheel_clockwise: Vec<Rgb>,
    wheel_counterclockwise: Vec<Rgb>,
}

/// The two decoded Tidal templates.
struct Tidal {
    leftward: Vec<i16>,
    rightward: Vec<i16>,
}

const WAVE_WHEEL_FIXTURE: &[u8] = include_bytes!(
    "../../../../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_WaveWheelOracle.json"
);
/// Managed `Pid02E0WaveWheelOracle.ApprovedFixtureSha256`.
const WAVE_WHEEL_SHA256: &str = "CFC9B9365CF7C55069AD2782AE510F0838865EAB0681E7A45BAD1D9B932B432E";
const TIDAL_FIXTURE: &[u8] = include_bytes!(
    "../../../../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_TidalOracle.json"
);
/// Managed `Pid02E0TidalOracle.ApprovedFixtureSha256`.
const TIDAL_SHA256: &str = "35D69EB780ED6727951E630565D2873FAECE7B47450CB4B30AFAE38AC3271C4F";

fn wave_wheel() -> Option<&'static WaveWheel> {
    static ORACLE: OnceLock<Option<WaveWheel>> = OnceLock::new();
    ORACLE
        .get_or_init(|| load_wave_wheel(WAVE_WHEEL_FIXTURE))
        .as_ref()
}

fn tidal() -> Option<&'static Tidal> {
    static ORACLE: OnceLock<Option<Tidal>> = OnceLock::new();
    ORACLE.get_or_init(|| load_tidal(TIDAL_FIXTURE)).as_ref()
}

fn pinned(bytes: &[u8], sha256: &str) -> Option<Value> {
    let digest: String = Sha256::digest(bytes)
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect();
    (digest == sha256)
        .then(|| serde_json::from_slice(bytes).ok())
        .flatten()
}

/// Managed's record deserialization with unmapped members disallowed: the
/// object has exactly `keys`.
fn exact<'a>(value: &'a Value, keys: &[&str]) -> Option<&'a serde_json::Map<String, Value>> {
    let object = value.as_object()?;
    (object.len() == keys.len() && keys.iter().all(|key| object.contains_key(*key)))
        .then_some(object)
}

fn target_matches(value: &Value) -> bool {
    exact(
        value,
        &["modelNumber", "vendorIdHex", "productIdHex", "bios"],
    )
    .is_some()
        && value["modelNumber"] == "RZ09-0581"
        && value["vendorIdHex"] == "1532"
        && value["productIdHex"] == "02E0"
        && value["bios"] == "3.01"
}

/// The decoded payload of one template with this encoding and length.
fn payload(template: &Value, encoding: &str, length: usize) -> Option<Vec<u8>> {
    if template["encoding"] != encoding || template["uncompressedBytes"].as_u64()? != length as u64
    {
        return None;
    }
    template["startSeconds"].as_f64()?;
    template["firstSourceFrame"].as_i64()?;
    template["lastSourceFrame"].as_i64()?;
    let compressed = inflate::base64(template["data"].as_str()?)?;
    inflate::inflate(&compressed, length)
}

/// Managed `Pid02E0WaveWheelOracle.ReadTemplates`.
fn load_wave_wheel(bytes: &[u8]) -> Option<WaveWheel> {
    let document = pinned(bytes, WAVE_WHEEL_SHA256)?;
    exact(
        &document,
        &[
            "schemaVersion",
            "evidenceRole",
            "target",
            "sourceCaptureSha256",
            "cycleMilliseconds",
            "frameIntervalMilliseconds",
            "frameCount",
            "slotCount",
            "templates",
        ],
    )?;
    let templates = document["templates"].as_array()?;
    if document["schemaVersion"] != 1
        || document["evidenceRole"] != "SanitizedWaveWheelMatrixOracle"
        || document["sourceCaptureSha256"]
            != "F9E458CE3B37BCC882F6D85B97F56D22D55292F71313FFA23AF0E0A46287FBE0"
        || !target_matches(&document["target"])
        || document["cycleMilliseconds"] != 1000
        || document["frameIntervalMilliseconds"] != 40
        || document["frameCount"] != WAVE_WHEEL_FRAMES as u64
        || document["slotCount"] != SLOT_COUNT as u64
        || templates.len() != 4
    {
        return None;
    }
    let mut decoded: [Option<Vec<Rgb>>; 4] = Default::default();
    for template in templates {
        exact(
            template,
            &[
                "effect",
                "direction",
                "startSeconds",
                "firstSourceFrame",
                "lastSourceFrame",
                "encoding",
                "uncompressedBytes",
                "data",
            ],
        )?;
        let index = match (
            template["effect"].as_str()?,
            template["direction"].as_str()?,
        ) {
            ("Wave", "Rightward") => 0,
            ("Wave", "Leftward") => 1,
            ("Wheel", "Clockwise") => 2,
            ("Wheel", "Counterclockwise") => 3,
            _ => return None,
        };
        let raw = payload(
            template,
            "deflate-base64-rgb24",
            WAVE_WHEEL_FRAMES * SLOT_COUNT * 3,
        )?;
        let values: Vec<Rgb> = raw
            .chunks_exact(3)
            .map(|rgb| Rgb {
                red: rgb[0],
                green: rgb[1],
                blue: rgb[2],
            })
            .collect();
        let masked = values
            .iter()
            .enumerate()
            .all(|(index, value)| is_canvas_hole(index % SLOT_COUNT) == (*value == Rgb::default()));
        if !masked || decoded[index].replace(values).is_some() {
            return None;
        }
    }
    let [
        wave_rightward,
        wave_leftward,
        wheel_clockwise,
        wheel_counterclockwise,
    ] = decoded;
    Some(WaveWheel {
        wave_rightward: wave_rightward?,
        wave_leftward: wave_leftward?,
        wheel_clockwise: wheel_clockwise?,
        wheel_counterclockwise: wheel_counterclockwise?,
    })
}

/// Managed `Pid02E0TidalOracle.ReadTemplates`.
fn load_tidal(bytes: &[u8]) -> Option<Tidal> {
    let document = pinned(bytes, TIDAL_SHA256)?;
    exact(
        &document,
        &[
            "schemaVersion",
            "evidenceRole",
            "target",
            "sourceCaptureSha256",
            "cycleMilliseconds",
            "frameIntervalMilliseconds",
            "frameCount",
            "slotCount",
            "capturedColor1RgbHex",
            "capturedColor2RgbHex",
            "templates",
        ],
    )?;
    let templates = document["templates"].as_array()?;
    if document["schemaVersion"] != 1
        || document["evidenceRole"] != "SanitizedTidalMatrixOracle"
        || document["sourceCaptureSha256"]
            != "6BDB15583C203BA8543A3A87D578144E84C502249E0C14B3CC9F50430C51B3C2"
        || !target_matches(&document["target"])
        || document["cycleMilliseconds"] != 5000
        || document["frameIntervalMilliseconds"] != 50
        || document["frameCount"] != TIDAL_FRAMES as u64
        || document["slotCount"] != SLOT_COUNT as u64
        || document["capturedColor1RgbHex"] != "00FF00"
        || document["capturedColor2RgbHex"] != "0000FF"
        || templates.len() != 2
    {
        return None;
    }
    // Managed `Single` for each direction: exactly one of each.
    let mut decoded: [Option<Vec<i16>>; 2] = Default::default();
    for template in templates {
        exact(
            template,
            &[
                "direction",
                "startSeconds",
                "firstSourceFrame",
                "lastSourceFrame",
                "encoding",
                "uncompressedBytes",
                "data",
            ],
        )?;
        let index = match template["direction"].as_str()? {
            "Leftward" => 0,
            "Rightward" => 1,
            _ => return None,
        };
        let raw = payload(
            template,
            "deflate-base64-signed-int16-color1-positive-color2-negative",
            TIDAL_FRAMES * SLOT_COUNT * 2,
        )?;
        let values: Vec<i16> = raw
            .chunks_exact(2)
            .map(|pair| i16::from_le_bytes([pair[0], pair[1]]))
            .collect();
        let valid = values.iter().enumerate().all(|(index, &value)| {
            (-255..=255).contains(&value) && (!is_canvas_hole(index % SLOT_COUNT) || value == 0)
        });
        if !valid || decoded[index].replace(values).is_some() {
            return None;
        }
    }
    let [leftward, rightward] = decoded;
    Some(Tidal {
        leftward: leftward?,
        rightward: rightward?,
    })
}

#[cfg(test)]
pub(crate) mod tests;
