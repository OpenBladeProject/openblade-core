//! Managed `LightingSettingsValidation`, `LightingSettingsFactory`
//! normalisation and the keyboard sequence timeline check.

use super::model::*;
use std::vec::Vec;

/// Managed `KeyboardLightingLayoutIds.Rz09_0581`.
pub const RZ09_0581_LAYOUT: &str = "openblade.razer.rz09-0581.keyboard.6x17.v1";
/// The other identifier managed checks against the same six by seventeen
/// grid. It is recognised only so a set naming it gets managed's "different
/// keyboard layout" reply; the RZ09-0581 policy never admits it.
const SHARED_GRID_LAYOUT: &str = "openblade.razer.rz09-0528.keyboard.6x17.v1";

const DEFAULT_VISIBLE_COLOR: RgbColor = RgbColor {
    red: 0x47,
    green: 0xE1,
    blue: 0x0C,
};
const DEFAULT_SECONDARY_COLOR: RgbColor = RgbColor {
    red: 255,
    green: 0,
    blue: 170,
};
const BLACK: RgbColor = RgbColor {
    red: 0,
    green: 0,
    blue: 0,
};
const MAX_DECAY_MILLISECONDS: i32 = 2000;
const DECAY_STEP_MILLISECONDS: i32 = 250;

/// Managed `LightingSettingsValidation.IsWellFormed`.
pub fn is_well_formed(settings: &LightingSettings) -> bool {
    if !settings.effect.is_defined()
        || settings
            .independent_controls
            .is_some_and(|controls| !controls.is_well_formed())
        || settings.logo_mode.is_some_and(|mode| !mode.is_defined())
        || settings.brightness > 100
        || settings.effect != LightingEffect::OFF && settings.brightness == 0
    {
        return false;
    }
    let color = settings.color;
    let Some(parameters) = &settings.parameters else {
        // Documents older than effect parameters carry the effect alone.
        return match settings.effect {
            LightingEffect::OFF => settings.brightness == 0 && color == BLACK,
            LightingEffect::SPECTRUM => color == BLACK,
            LightingEffect::STATIC | LightingEffect::BREATHING | LightingEffect::REACTIVE => true,
            _ => false,
        };
    };
    // Single and Dual carry the effect colour as primary; Random carries none.
    let palette =
        |mode: ColorMode, primary: &Option<RgbColor>, secondary: &Option<RgbColor>| match mode {
            ColorMode::SINGLE => *primary == Some(color) && secondary.is_none(),
            ColorMode::DUAL => *primary == Some(color) && secondary.is_some(),
            ColorMode::RANDOM => primary.is_none() && secondary.is_none() && color == BLACK,
            _ => false,
        };
    match (settings.effect, parameters) {
        (LightingEffect::OFF, LightingParameters::Off) => {
            settings.brightness == 0 && color == BLACK
        }
        (LightingEffect::STATIC, LightingParameters::Static { color: fixed }) => color == *fixed,
        (LightingEffect::SPECTRUM, LightingParameters::SpectrumCycling) => color == BLACK,
        (
            LightingEffect::BREATHING,
            LightingParameters::Breathing {
                color_mode,
                primary_color,
                secondary_color,
            },
        ) => palette(*color_mode, primary_color, secondary_color),
        (
            LightingEffect::REACTIVE,
            LightingParameters::Reactive {
                color: fixed,
                duration,
            },
        ) => duration.is_defined() && color == *fixed,
        (
            LightingEffect::TIDAL,
            LightingParameters::Tidal {
                primary_color,
                random_colors,
                direction,
                ..
            },
        ) => {
            direction.is_defined()
                && color
                    == if *random_colors {
                        BLACK
                    } else {
                        *primary_color
                    }
        }
        (
            LightingEffect::STARLIGHT,
            LightingParameters::Starlight {
                color_mode,
                primary_color,
                secondary_color,
                duration,
            },
        ) => duration.is_defined() && palette(*color_mode, primary_color, secondary_color),
        (LightingEffect::WAVE, LightingParameters::Wave { direction })
        | (LightingEffect::WHEEL, LightingParameters::Wheel { direction }) => {
            direction.is_defined() && color == BLACK
        }
        (LightingEffect::RIPPLE, LightingParameters::Ripple { color: fixed }) => color == *fixed,
        (LightingEffect::AMBIENT_AWARENESS, LightingParameters::AmbientAwareness { region }) => {
            region.is_defined() && color == BLACK
        }
        (
            LightingEffect::AUDIO_METER,
            LightingParameters::AudioMeter {
                color_boost,
                decay_milliseconds,
            },
        ) => {
            color_boost.is_finite()
                && (1.0..=4.0).contains(color_boost)
                && color_boost * 4.0 == (color_boost * 4.0).trunc()
                && (0..=MAX_DECAY_MILLISECONDS).contains(decay_milliseconds)
                && decay_milliseconds % DECAY_STEP_MILLISECONDS == 0
                && color == BLACK
        }
        (LightingEffect::FIRE, LightingParameters::Fire) => color == BLACK,
        (
            LightingEffect::SEQUENCE,
            LightingParameters::Sequence {
                layout_id,
                timeline_base64,
                ..
            },
        ) => {
            // Managed also knows three layouts with their own key maps, which
            // this composition does not carry; a set naming one is refused
            // here as malformed rather than as a different layout.
            color == BLACK
                && matches!(
                    layout_id.as_deref(),
                    Some(RZ09_0581_LAYOUT | SHARED_GRID_LAYOUT)
                )
                && timeline_base64
                    .as_deref()
                    .is_some_and(timeline_is_well_formed)
        }
        _ => false,
    }
}

/// Managed `LightingSettingsFactory.NormalizeLegacyOff`: an Off setting
/// without parameters, saved before effect parameters existed, gains the
/// Off parameters.
pub fn normalize_legacy_off(settings: &mut LightingSettings) {
    if settings.parameters.is_none()
        && settings.effect == LightingEffect::OFF
        && settings.color == BLACK
        && settings.brightness == 0
    {
        settings.parameters = Some(LightingParameters::Off);
    }
}

/// Managed `LightingSettingsFactory.NormalizeAudioMeterDecay`, which begins
/// with `NormalizeLegacyOff`.
fn normalize_audio_meter_decay(settings: &mut LightingSettings) {
    normalize_legacy_off(settings);
    if let Some(LightingParameters::AudioMeter {
        decay_milliseconds, ..
    }) = &mut settings.parameters
    {
        let clamped = (*decay_milliseconds).clamp(0, MAX_DECAY_MILLISECONDS);
        // Midpoint away from zero on a non-negative value.
        *decay_milliseconds = (clamped + DECAY_STEP_MILLISECONDS / 2) / DECAY_STEP_MILLISECONDS
            * DECAY_STEP_MILLISECONDS;
    }
}

/// Managed `LegacyLightingVisibilityNormalizer.Normalize`: an effect saved at
/// zero brightness becomes visible again at the recovery brightness.
pub fn normalize_legacy_visibility(settings: &mut LightingSettings) {
    const FULL: u8 = 100;
    const RECOVERY: u8 = 50;
    normalize_audio_meter_decay(settings);
    if settings.effect == LightingEffect::OFF || settings.brightness != 0 {
        return;
    }
    let visible = if settings.color == BLACK {
        DEFAULT_VISIBLE_COLOR
    } else {
        settings.color
    };
    let or = |color: RgbColor, fallback: RgbColor| if color == BLACK { fallback } else { color };
    use LightingEffect as Effect;
    use LightingParameters as P;
    // The factory result for the saved parameters: effect, colour, parameters.
    let rebuilt = match settings.parameters.clone() {
        Some(P::Static { .. }) => Some((Effect::STATIC, visible, P::Static { color: visible })),
        Some(P::SpectrumCycling) => Some((Effect::SPECTRUM, BLACK, P::SpectrumCycling)),
        Some(P::Breathing {
            color_mode,
            secondary_color,
            ..
        }) => match (color_mode, secondary_color) {
            (ColorMode::SINGLE, _) => Some((visible, Some(visible), None)),
            (ColorMode::DUAL, Some(secondary)) => Some((
                visible,
                Some(visible),
                Some(or(secondary, DEFAULT_SECONDARY_COLOR)),
            )),
            (ColorMode::RANDOM, _) => Some((BLACK, None, None)),
            _ => None,
        }
        .map(|(color, primary_color, secondary_color)| {
            (
                Effect::BREATHING,
                color,
                P::Breathing {
                    color_mode,
                    primary_color,
                    secondary_color,
                },
            )
        }),
        Some(P::Reactive { duration, .. }) => Some((
            Effect::REACTIVE,
            visible,
            P::Reactive {
                color: visible,
                duration,
            },
        )),
        Some(P::Tidal {
            primary_color,
            secondary_color,
            random_colors,
            direction,
        }) => {
            let primary_color = or(primary_color, DEFAULT_VISIBLE_COLOR);
            Some((
                Effect::TIDAL,
                if random_colors { BLACK } else { primary_color },
                P::Tidal {
                    primary_color,
                    secondary_color: or(secondary_color, DEFAULT_SECONDARY_COLOR),
                    random_colors,
                    direction,
                },
            ))
        }
        Some(P::Starlight {
            color_mode,
            secondary_color,
            duration,
            ..
        }) => match (color_mode, secondary_color) {
            (ColorMode::SINGLE, _) => Some((visible, Some(visible), None)),
            (ColorMode::DUAL, Some(secondary)) => Some((
                visible,
                Some(visible),
                Some(or(secondary, DEFAULT_SECONDARY_COLOR)),
            )),
            (ColorMode::RANDOM, _) => Some((BLACK, None, None)),
            _ => None,
        }
        .map(|(color, primary_color, secondary_color)| {
            (
                Effect::STARLIGHT,
                color,
                P::Starlight {
                    color_mode,
                    primary_color,
                    secondary_color,
                    duration,
                },
            )
        }),
        Some(parameters @ P::Wave { .. }) => Some((Effect::WAVE, BLACK, parameters)),
        Some(parameters @ P::Wheel { .. }) => Some((Effect::WHEEL, BLACK, parameters)),
        Some(P::Ripple { .. }) => Some((Effect::RIPPLE, visible, P::Ripple { color: visible })),
        Some(parameters @ P::AmbientAwareness { .. }) => {
            Some((Effect::AMBIENT_AWARENESS, BLACK, parameters))
        }
        Some(parameters @ P::AudioMeter { .. }) => Some((Effect::AUDIO_METER, BLACK, parameters)),
        Some(P::Fire) => Some((Effect::FIRE, BLACK, P::Fire)),
        Some(parameters @ P::Sequence { .. }) => Some((Effect::SEQUENCE, BLACK, parameters)),
        Some(P::Off) => None,
        None => match settings.effect {
            Effect::STATIC => Some((Effect::STATIC, visible, P::Static { color: visible })),
            Effect::SPECTRUM => Some((Effect::SPECTRUM, BLACK, P::SpectrumCycling)),
            Effect::BREATHING => Some((
                Effect::BREATHING,
                visible,
                P::Breathing {
                    color_mode: ColorMode::SINGLE,
                    primary_color: Some(visible),
                    secondary_color: None,
                },
            )),
            Effect::REACTIVE => Some((
                Effect::REACTIVE,
                visible,
                P::Reactive {
                    color: visible,
                    duration: LightingDuration::MEDIUM,
                },
            )),
            _ => None,
        },
    };
    if let Some((effect, color, parameters)) = rebuilt {
        settings.effect = effect;
        settings.color = color;
        settings.parameters = Some(parameters);
        // The factory brightness is FULL; the Service then lowers the recovery.
        settings.brightness = if FULL == 0 { 0 } else { RECOVERY };
    }
}

/// Managed `LightingSettingsFactory.PreserveLegacyIndependentIntent`.
pub fn preserve_legacy_independent_intent(settings: &mut LightingSettings) {
    if settings.independent_controls.is_some() {
        settings.controls_keyboard_effect = false;
    }
}

const BASE64: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/// Canonical padded Base64 only, as managed re-encodes and compares.
fn base64_decode(text: &str) -> Option<Vec<u8>> {
    let bytes = text.as_bytes();
    if bytes.is_empty() || !bytes.len().is_multiple_of(4) {
        return None;
    }
    let padding = bytes.iter().rev().take_while(|byte| **byte == b'=').count();
    if padding > 2 {
        return None;
    }
    let mut out = Vec::with_capacity(bytes.len() / 4 * 3);
    for (index, chunk) in bytes.chunks(4).enumerate() {
        let last = index + 1 == bytes.len() / 4;
        let used = if last { 4 - padding } else { 4 };
        let mut group = 0u32;
        for (position, byte) in chunk.iter().enumerate() {
            let value = if position < used {
                BASE64.iter().position(|symbol| symbol == byte)? as u32
            } else {
                0
            };
            group = group << 6 | value;
        }
        // Bits past the encoded bytes must be zero in the canonical form.
        if used == 2 && group & 0xFFFF != 0 || used == 3 && group & 0xFF != 0 {
            return None;
        }
        out.extend_from_slice(&group.to_be_bytes()[1..used]);
    }
    Some(out)
}

/// Managed `KeyboardLightingSequenceCodec.IsWellFormedForLayout` for the
/// six by seventeen matrix.
pub fn timeline_is_well_formed(timeline_base64: &str) -> bool {
    const MAX_DECODED_BYTES: usize = 4096;
    const MAX_CHARACTERS: usize = MAX_DECODED_BYTES.div_ceil(3) * 4;
    const SLOTS: usize = 6 * 17;
    const HOLES: [usize; 16] = [
        0, 17, 31, 34, 48, 51, 64, 65, 68, 70, 81, 82, 85, 89, 92, 93,
    ];
    if timeline_base64.len() > MAX_CHARACTERS {
        return false;
    }
    let Some(encoded) =
        base64_decode(timeline_base64).filter(|bytes| bytes.len() <= MAX_DECODED_BYTES)
    else {
        return false;
    };
    // "OBSQ", format 1, then a little-endian frame count.
    if encoded.len() < 7 || encoded[..5] != [0x4F, 0x42, 0x53, 0x51, 1] {
        return false;
    }
    let frames = u16::from_le_bytes([encoded[5], encoded[6]]);
    if !(1..=1800).contains(&frames) {
        return false;
    }
    let mut at = 7;
    let mut total_milliseconds = 0u32;
    let mut previous = [BLACK; SLOTS];
    for _ in 0..frames {
        if encoded.len() - at < 3 {
            return false;
        }
        let duration = u32::from(u16::from_le_bytes([encoded[at], encoded[at + 1]]));
        total_milliseconds += duration;
        if !(1..=60_000).contains(&duration) || total_milliseconds > 600_000 {
            return false;
        }
        let deltas = usize::from(encoded[at + 2]);
        at += 3;
        if deltas > SLOTS || encoded.len() - at < deltas * 4 {
            return false;
        }
        let mut last_slot = None;
        for delta in encoded[at..at + deltas * 4].chunks(4) {
            let slot = usize::from(delta[0]);
            let color = RgbColor {
                red: delta[1],
                green: delta[2],
                blue: delta[3],
            };
            if slot >= SLOTS
                || HOLES.contains(&slot)
                || last_slot.is_some_and(|last| slot <= last)
                || color == previous[slot]
            {
                return false;
            }
            previous[slot] = color;
            last_slot = Some(slot);
        }
        at += deltas * 4;
    }
    at == encoded.len()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::string::String;
    use std::vec;

    fn encode(bytes: &[u8]) -> String {
        let mut out = String::new();
        for chunk in bytes.chunks(3) {
            let mut group = [0u8; 3];
            group[..chunk.len()].copy_from_slice(chunk);
            let value = u32::from_be_bytes([0, group[0], group[1], group[2]]);
            for position in 0..4 {
                out.push(if position <= chunk.len() {
                    char::from(BASE64[(value >> (18 - 6 * position) & 63) as usize])
                } else {
                    '='
                });
            }
        }
        out
    }

    fn timeline(frames: &[(u16, &[[u8; 4]])]) -> Vec<u8> {
        let mut bytes = vec![0x4F, 0x42, 0x53, 0x51, 1];
        bytes.extend((frames.len() as u16).to_le_bytes());
        for (duration, deltas) in frames {
            bytes.extend(duration.to_le_bytes());
            bytes.push(deltas.len() as u8);
            for delta in *deltas {
                bytes.extend(delta);
            }
        }
        bytes
    }

    #[test]
    fn base64_accepts_only_the_canonical_spelling() {
        for bytes in [
            &b"a"[..],
            b"ab",
            b"abc",
            b"abcd",
            &[0xFF, 0xFE, 0xFD, 0xFC, 0xFB],
        ] {
            assert_eq!(base64_decode(&encode(bytes)).as_deref(), Some(bytes));
        }
        for refused in [
            "", "A", "AA", "AAA", "AA=A", "A===", "AB==", "AAB=", "AA\nA", "AA-_", "====", "AAAA=",
        ] {
            assert_eq!(base64_decode(refused), None, "{refused:?}");
        }
    }

    #[test]
    fn timelines_follow_the_managed_sequence_grammar() {
        let ok = |bytes: &[u8]| timeline_is_well_formed(&encode(bytes));
        assert!(ok(&timeline(&[
            (100, &[[1, 9, 9, 9], [2, 1, 2, 3]]),
            (60_000, &[[1, 0, 0, 0]])
        ])));
        assert!(ok(&timeline(&[(1, &[])])));
        // Frame count, duration, order, holes, unchanged deltas and length.
        assert!(!ok(&timeline(&[])));
        assert!(!ok(&timeline(&[(0, &[])])));
        assert!(!ok(&timeline(&[(60_001, &[])])));
        assert!(!ok(&timeline(&[(100, &[[2, 1, 1, 1], [1, 1, 1, 1]])])));
        assert!(!ok(&timeline(&[(100, &[[1, 1, 1, 1], [1, 2, 2, 2]])])));
        assert!(!ok(&timeline(&[(100, &[[0, 1, 1, 1]])])));
        assert!(!ok(&timeline(&[(100, &[[102, 1, 1, 1]])])));
        assert!(!ok(&timeline(&[(100, &[[1, 0, 0, 0]])])));
        assert!(!ok(&timeline(&[
            (100, &[[1, 1, 1, 1]]),
            (100, &[[1, 1, 1, 1]])
        ])));
        let quiet: &[[u8; 4]] = &[];
        assert!(!ok(&timeline(&[(60_000, quiet); 11])));
        assert!(ok(&timeline(&[(60_000, quiet); 10])));
        let mut trailing = timeline(&[(1, &[])]);
        trailing.push(0);
        assert!(!ok(&trailing));
        let mut truncated = timeline(&[(1, &[[1, 1, 1, 1]])]);
        truncated.pop();
        assert!(!ok(&truncated));
        let mut wrong_magic = timeline(&[(1, &[])]);
        wrong_magic[0] = 0;
        assert!(!ok(&wrong_magic));
        let mut wrong_version = timeline(&[(1, &[])]);
        wrong_version[4] = 2;
        assert!(!ok(&wrong_version));
        assert!(!timeline_is_well_formed("AAAA"));
        assert!(!timeline_is_well_formed(""));
    }
}
