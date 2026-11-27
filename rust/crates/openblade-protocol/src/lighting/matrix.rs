//! Exact RZ09-0581 host-rendered keyboard matrix, C# `Pid02E0MatrixReportCodec`,
//! `Pid02E0MatrixLayout`, `RegionalKeyboardFrameProjection` and
//! `KeyboardLayoutProtocol`, with the transition handshake of
//! `ProductionLightingPort.ExecuteTransitionHandshakeCoreAsync`:
//!
//! - the 03/0B matrix frame, a 374-byte envelope carried in a 375-byte
//!   feature report on the keyboard's second HID collection, 102 RGB slots
//!   in protocol order, with the reviewed canvas hole mask kept black;
//! - the projection of the stable logical canvas onto the physical map the
//!   device's layout byte selects (`RegionalProjection`), over source tables
//!   the caller loads and verifies;
//! - the 00/86 keyboard layout query and the 03/0A:0500 handshake setter.
//!
//! The matrix command is admitted by the reviewed semantic admission
//! `Rz09_0581_02e0_3_01_LightingSemanticAdmission.json` (`matrix`), and the
//! 00/86 and 03/0A commands by the hash-pinned embedded oracle
//! `Rz09_0581_02e0_3_01_LightingProduction.json` (`protocol`); the host
//! compares both with this codec before it sends anything. This codec grants
//! no I/O authority. The frame encoder and the handshake setter exist only
//! with the `lighting-setters` feature and in this module's tests.
use crate::RazerReport;
use openblade_types::ProtocolValidationFailure;

/// Managed `Pid02E0MatrixFrame.SlotCount`: six rows of seventeen.
pub const SLOT_COUNT: usize = 102;
pub const COLUMNS: usize = 17;
/// Managed `Pid02E0MatrixReportCodec.EnvelopeLength`.
pub const ENVELOPE_LENGTH: usize = 374;
/// Managed `FeatureReportBufferLength`: the report ID, then the envelope.
pub const FEATURE_REPORT_LENGTH: usize = ENVELOPE_LENGTH + 1;
/// Managed `CommandClass` and `CommandId`.
pub const MATRIX_CLASS: u8 = 0x03;
pub const MATRIX_COMMAND: u8 = 0x0B;
/// Managed `Blade16KeyboardLightingLayout.CanvasHoleSlots`: the canvas slots
/// that carry no key and are always black on the wire.
pub const CANVAS_HOLES: [usize; 16] = [
    0, 17, 31, 34, 48, 51, 64, 65, 68, 70, 81, 82, 85, 89, 92, 93,
];

const PAYLOAD_LENGTH: [u8; 2] = [0x01, 0x36];
const SLOT_HEADER: [u8; 4] = [0x00, 0x00, 0x00, 0x65];
const RGB_OFFSET: usize = 12;
const CHECKSUM_OFFSET: usize = 372;
const TERMINATOR_OFFSET: usize = 373;

/// Whether `slot` is a canvas hole (`Pid02E0MatrixLayout.IsHole`).
pub fn is_canvas_hole(slot: usize) -> bool {
    CANVAS_HOLES.contains(&slot)
}

/// One matrix RGB value (`MatrixRgb`).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Rgb {
    pub red: u8,
    pub green: u8,
    pub blue: u8,
}

/// One bounded 102-slot frame (`Pid02E0MatrixFrame`). Renderers draw on the
/// logical canvas; `RegionalProjection` maps it to the physical slots.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MatrixFrame {
    slots: [Rgb; SLOT_COUNT],
    /// Per-key content keeps regional keys absent from the canvas black;
    /// spatial content samples them from nearby canvas colors.
    logical_key_colors: bool,
}

impl MatrixFrame {
    pub fn new(slots: [Rgb; SLOT_COUNT], logical_key_colors: bool) -> Self {
        Self {
            slots,
            logical_key_colors,
        }
    }

    /// Managed `Pid02E0MatrixLayout.RenderUniform`, which is also
    /// `Pid02E0MatrixRenderers.RenderStatic`: `color` on every canvas slot
    /// and black on every hole.
    pub fn uniform(color: Rgb) -> Self {
        let mut slots = [color; SLOT_COUNT];
        for hole in CANVAS_HOLES {
            slots[hole] = Rgb::default();
        }
        Self::new(slots, false)
    }

    pub fn slots(&self) -> &[Rgb; SLOT_COUNT] {
        &self.slots
    }

    pub fn logical_key_colors(&self) -> bool {
        self.logical_key_colors
    }
}

/// The exact-model source maps (`Blade16KeyboardLightingLayout`): the canvas
/// table's HID usage per slot, the six physical tables, and which table each
/// of the 256 layout bytes selects. The caller loads them from the reviewed
/// source fixture and verifies its pin; zero is a slot without a key.
#[derive(Clone, Copy)]
pub struct KeyboardLayout<'a> {
    pub canvas: &'a [u8; SLOT_COUNT],
    pub tables: &'a [[u8; SLOT_COUNT]],
    pub selector: &'a [u8; 256],
}

impl KeyboardLayout<'_> {
    fn table(&self, layout: u8) -> Option<&[u8; SLOT_COUNT]> {
        self.tables
            .get(usize::from(self.selector[usize::from(layout)]))
    }

    /// Managed `IsKeySlot(slot, layoutId)`.
    pub fn is_key_slot(&self, slot: usize, layout: u8) -> bool {
        slot < SLOT_COUNT && self.table(layout).is_some_and(|table| table[slot] != 0)
    }

    /// Managed `TryMapCanvasSlot`: the physical slot carrying a canvas
    /// slot's key under `layout`. The supplemental ISO and Japanese usages
    /// map transient pulse slots; ANSI backslash and the ISO key beside
    /// Enter share one canvas identity.
    pub fn map_canvas_slot(&self, canvas_slot: usize, layout: u8) -> Option<usize> {
        if canvas_slot >= SLOT_COUNT {
            return None;
        }
        let usage = match canvas_slot {
            31 => 0x89,
            48 => 0x8b,
            64 => 0x88,
            70 => 0x64,
            82 => 0x87,
            93 => 0x8a,
            _ => self.canvas[canvas_slot],
        };
        if usage == 0 {
            return None;
        }
        let selected = self.table(layout)?;
        if selected[canvas_slot] == usage {
            return Some(canvas_slot);
        }
        let position = |usage| selected.iter().position(|&value| value == usage);
        position(usage).or_else(|| if usage == 0x31 { position(0x32) } else { None })
    }
}

/// One layout's projection (`RegionalKeyboardFrameProjection.Projection`):
/// for each physical slot, the canvas slot whose color it shows, if any.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RegionalProjection {
    canvas: [Option<u8>; SLOT_COUNT],
    keys: [Option<u8>; SLOT_COUNT],
    samples: [Option<u8>; SLOT_COUNT],
}

impl RegionalProjection {
    /// Managed `CreateProjection` for one layout byte. A later canvas slot
    /// mapped to the same physical slot replaces an earlier one, and a key
    /// slot no canvas color reaches samples the nearest that one does, the
    /// first at equal distance, as managed iterates.
    pub fn new(layout: &KeyboardLayout<'_>, id: u8) -> Self {
        let mut canvas = [None; SLOT_COUNT];
        let mut keys = [None; SLOT_COUNT];
        for slot in 0..SLOT_COUNT {
            if let Some(physical) = layout.map_canvas_slot(slot, id) {
                keys[physical] = Some(slot as u8);
                if !is_canvas_hole(slot) {
                    canvas[physical] = Some(slot as u8);
                }
            }
        }
        let mut samples = [None; SLOT_COUNT];
        for (target, sample) in samples.iter_mut().enumerate() {
            if !layout.is_key_slot(target, id) || canvas[target].is_some() {
                continue;
            }
            let mut nearest = i32::MAX;
            for (candidate, source) in canvas.iter().enumerate() {
                let Some(source) = source else {
                    continue;
                };
                let row = (target / COLUMNS) as i32 - (candidate / COLUMNS) as i32;
                let column = (target % COLUMNS) as i32 - (candidate % COLUMNS) as i32;
                let distance = row * row + column * column;
                if distance < nearest {
                    nearest = distance;
                    *sample = Some(*source);
                }
            }
        }
        Self {
            canvas,
            keys,
            samples,
        }
    }

    /// Managed `Project`: per-key content reads only the key map; spatial
    /// content reads the canvas map and samples the rest.
    pub fn project(&self, frame: &MatrixFrame) -> MatrixFrame {
        let mut slots = [Rgb::default(); SLOT_COUNT];
        for (slot, color) in slots.iter_mut().enumerate() {
            let source = if frame.logical_key_colors {
                self.keys[slot]
            } else {
                self.canvas[slot].or(self.samples[slot])
            };
            if let Some(source) = source {
                *color = frame.slots[usize::from(source)];
            }
        }
        MatrixFrame::new(slots, frame.logical_key_colors)
    }
}

/// Managed `EncodeFeatureReportBuffer`: report ID zero, then the 03/0B
/// envelope with `transaction`, the slot colors and the XOR checksum of
/// bytes 2 to 371.
#[cfg(any(test, feature = "lighting-setters"))]
pub fn encode_feature_report(transaction: u8, frame: &MatrixFrame) -> [u8; FEATURE_REPORT_LENGTH] {
    let mut buffer = [0; FEATURE_REPORT_LENGTH];
    let envelope = &mut buffer[1..];
    envelope[1] = transaction;
    envelope[4..6].copy_from_slice(&PAYLOAD_LENGTH);
    envelope[6] = MATRIX_CLASS;
    envelope[7] = MATRIX_COMMAND;
    envelope[8..12].copy_from_slice(&SLOT_HEADER);
    for (slot, color) in frame.slots.iter().enumerate() {
        let offset = RGB_OFFSET + slot * 3;
        envelope[offset..offset + 3].copy_from_slice(&[color.red, color.green, color.blue]);
    }
    envelope[CHECKSUM_OFFSET] = checksum(envelope);
    buffer
}

/// Managed `DecodeFeatureReportBuffer`: the transaction and frame of a valid
/// feature buffer, `None` for any header, padding, checksum or terminator
/// fault. The decoded frame is spatial (managed decodes without the flag).
pub fn decode_feature_report(buffer: &[u8]) -> Option<(u8, MatrixFrame)> {
    let [0, envelope @ ..] = buffer else {
        return None;
    };
    let padding = RGB_OFFSET + SLOT_COUNT * 3;
    let valid = envelope.len() == ENVELOPE_LENGTH
        && envelope[0] == 0
        && envelope[2..4] == [0, 0]
        && envelope[4..6] == PAYLOAD_LENGTH
        && envelope[6] == MATRIX_CLASS
        && envelope[7] == MATRIX_COMMAND
        && envelope[8..12] == SLOT_HEADER
        && envelope[padding..CHECKSUM_OFFSET]
            .iter()
            .all(|&byte| byte == 0)
        && envelope[TERMINATOR_OFFSET] == 0
        && envelope[CHECKSUM_OFFSET] == checksum(envelope);
    if !valid {
        return None;
    }
    let mut slots = [Rgb::default(); SLOT_COUNT];
    for (slot, color) in slots.iter_mut().enumerate() {
        let offset = RGB_OFFSET + slot * 3;
        *color = Rgb {
            red: envelope[offset],
            green: envelope[offset + 1],
            blue: envelope[offset + 2],
        };
    }
    Some((envelope[1], MatrixFrame::new(slots, false)))
}

fn checksum(envelope: &[u8]) -> u8 {
    envelope[2..CHECKSUM_OFFSET]
        .iter()
        .fold(0, |sum, byte| sum ^ byte)
}

/// The 00/86 keyboard layout getter (`KeyboardLayoutProtocol.CreateQuery`):
/// three zero bytes, and a three-byte reply whose first byte selects the
/// physical map. The adjacent bytes are opaque metadata, never a gate.
pub struct LayoutQuery {
    request: RazerReport,
}

impl LayoutQuery {
    const CLASS: u8 = 0x00;
    const ID: u8 = 0x86;

    pub fn new(transaction: u8) -> Self {
        Self {
            request: RazerReport::create(transaction, Self::CLASS, Self::ID, &[0; 3])
                .expect("fixed three-byte layout query"),
        }
    }

    pub fn request(&self) -> &RazerReport {
        &self.request
    }

    /// The layout byte of a correlated success with exactly three bytes.
    pub fn decode(&self, response: &[u8]) -> Result<u8, ProtocolValidationFailure> {
        let payload = self.request.validate_response(response, Some(3))?;
        Ok(payload.as_slice()[0])
    }
}

/// The 03/0A:0500 transition handshake setter, whose acknowledgement must
/// echo `05 00` exactly (managed `Spec(0x03, 0x0A, [05 00], [05 00])`).
#[cfg(any(test, feature = "lighting-setters"))]
pub struct TransitionSet {
    request: RazerReport,
}

#[cfg(any(test, feature = "lighting-setters"))]
impl TransitionSet {
    const CLASS: u8 = 0x03;
    const ID: u8 = 0x0a;
    const ARGUMENTS: [u8; 2] = [0x05, 0x00];

    pub fn new(transaction: u8) -> Self {
        Self {
            request: RazerReport::create(transaction, Self::CLASS, Self::ID, &Self::ARGUMENTS)
                .expect("fixed two-byte transition setter"),
        }
    }

    pub fn request(&self) -> &RazerReport {
        &self.request
    }

    pub fn acknowledge(&self, response: &[u8]) -> Result<(), ProtocolValidationFailure> {
        let payload = self.request.validate_response(response, Some(2))?;
        if payload.as_slice() == Self::ARGUMENTS {
            Ok(())
        } else {
            Err(ProtocolValidationFailure::PayloadValidator)
        }
    }
}

#[cfg(test)]
mod tests;
