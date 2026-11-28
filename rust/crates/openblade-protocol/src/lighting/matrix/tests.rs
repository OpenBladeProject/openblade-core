extern crate std;
use super::*;
use crate::calculate_checksum;
use serde_json::Value;
use std::{format, string::String, vec::Vec};

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02X}")).collect()
}

fn notation(report: &RazerReport) -> String {
    let bytes = report.encode();
    let length = usize::from(bytes[5]);
    format!(
        "{:02X}/{:02X}:{}",
        bytes[6],
        bytes[7],
        hex(&bytes[8..8 + length])
    )
}

fn reply(request: &RazerReport, payload: &[u8]) -> Vec<u8> {
    let bytes = request.encode();
    let mut reply = RazerReport::create(bytes[1], bytes[6], bytes[7], payload)
        .unwrap()
        .encode();
    reply[0] = 2;
    reply[88] = calculate_checksum(&reply).unwrap();
    reply.to_vec()
}

fn fixture(path: &str) -> Value {
    serde_json::from_str(path).unwrap()
}

fn admission() -> Value {
    fixture(include_str!(
        "../../../../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_LightingSemanticAdmission.json"
    ))
}

fn oracle() -> Value {
    fixture(include_str!(
        "../../../../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_LightingProduction.json"
    ))
}

/// The reviewed source maps, read as the host's loader reads them.
struct Maps {
    canvas: [u8; SLOT_COUNT],
    tables: Vec<[u8; SLOT_COUNT]>,
    selector: [u8; 256],
}

impl Maps {
    fn load() -> Self {
        let root = fixture(include_str!(
            "../../../../../../src/OpenBlade.Contracts/Fixtures/Blade16KeyboardLightingLayout.json"
        ));
        let mut tables = Vec::new();
        let mut offsets = Vec::new();
        let mut selector = [u8::MAX; 256];
        for table in root["tables"].as_array().unwrap() {
            let mut usages = [0; SLOT_COUNT];
            let values = table["hidUsages"].as_array().unwrap();
            assert_eq!(values.len(), SLOT_COUNT);
            for (slot, value) in values.iter().enumerate() {
                usages[slot] = u8::try_from(value.as_u64().unwrap()).unwrap();
            }
            for id in table["layoutIds"].as_array().unwrap() {
                selector[usize::try_from(id.as_u64().unwrap()).unwrap()] =
                    u8::try_from(tables.len()).unwrap();
            }
            offsets.push(table["offset"].as_u64().unwrap());
            tables.push(usages);
        }
        assert!(selector.iter().all(|&index| index != u8::MAX));
        let canvas = tables[offsets.iter().position(|&offset| offset == 0xb345).unwrap()];
        Self {
            canvas,
            tables,
            selector,
        }
    }

    fn layout(&self) -> KeyboardLayout<'_> {
        KeyboardLayout {
            canvas: &self.canvas,
            tables: &self.tables,
            selector: &self.selector,
        }
    }
}

fn color(text: &str) -> Rgb {
    let byte = |index: usize| u8::from_str_radix(&text[index..index + 2], 16).unwrap();
    Rgb {
        red: byte(0),
        green: byte(2),
        blue: byte(4),
    }
}

/// The geometry, command and holes are the reviewed admission's `matrix`
/// section and the oracle's `matrixCommand`.
#[test]
fn the_geometry_is_the_reviewed_matrix_admission() {
    let matrix = &admission()["matrix"];
    assert_eq!(
        matrix["command"],
        format!("{MATRIX_CLASS:02X}/{MATRIX_COMMAND:02X}")
    );
    assert_eq!(matrix["envelopeLength"], ENVELOPE_LENGTH);
    assert_eq!(matrix["featureReportLength"], FEATURE_REPORT_LENGTH);
    assert_eq!(matrix["slotCount"], SLOT_COUNT);
    let holes: Vec<usize> = matrix["holeSlots"]
        .as_array()
        .unwrap()
        .iter()
        .map(|slot| usize::try_from(slot.as_u64().unwrap()).unwrap())
        .collect();
    assert_eq!(holes, CANVAS_HOLES);
    let protocol = &oracle()["protocol"];
    assert_eq!(protocol["matrixCommand"], matrix["command"]);
    assert_eq!(protocol["matrixEnvelopeLength"], ENVELOPE_LENGTH);
    assert_eq!(protocol["matrixFeatureReportLength"], FEATURE_REPORT_LENGTH);
    assert_eq!(protocol["matrixSlotCount"], SLOT_COUNT);
}

/// Managed `EncodeFeatureReportBuffer` of the known-answer frame, byte by
/// byte; the host compares its SHA-256 with the admission.
#[test]
fn the_known_answer_report_has_the_managed_layout() {
    let matrix = &admission()["matrix"];
    let transaction = u8::try_from(matrix["knownAnswerTransactionId"].as_u64().unwrap()).unwrap();
    let lit = color(matrix["knownAnswerColor"].as_str().unwrap());
    let buffer = encode_feature_report(transaction, &MatrixFrame::uniform(lit));
    assert_eq!(buffer.len(), FEATURE_REPORT_LENGTH);
    assert_eq!(
        buffer[..13],
        [0, 0, 0x5a, 0, 0, 0x01, 0x36, 0x03, 0x0b, 0, 0, 0, 0x65]
    );
    for slot in 0..SLOT_COUNT {
        let offset = 13 + slot * 3;
        let expected = if is_canvas_hole(slot) {
            [0; 3]
        } else {
            [0x47, 0xe1, 0x0c]
        };
        assert_eq!(buffer[offset..offset + 3], expected, "slot {slot}");
    }
    assert!(buffer[319..373].iter().all(|&byte| byte == 0));
    let sum = buffer[3..373].iter().fold(0, |sum, byte| sum ^ byte);
    assert_eq!(buffer[373], sum);
    assert_eq!(buffer[374], 0);
}

#[test]
fn a_valid_report_round_trips() {
    let mut slots = [Rgb::default(); SLOT_COUNT];
    for (slot, value) in slots.iter_mut().enumerate() {
        let slot = slot as u8;
        *value = Rgb {
            red: slot,
            green: !slot,
            blue: slot ^ 0x5a,
        };
    }
    let frame = MatrixFrame::new(slots, false);
    let buffer = encode_feature_report(0xff, &frame);
    assert_eq!(decode_feature_report(&buffer), Some((0xff, frame)));
}

/// Managed `DecodeFeatureReportBuffer` refuses every malformed buffer.
#[test]
fn every_malformed_report_is_refused() {
    let valid = encode_feature_report(7, &MatrixFrame::uniform(color("102030")));
    assert!(decode_feature_report(&valid[..FEATURE_REPORT_LENGTH - 1]).is_none());
    let mut longer = valid.to_vec();
    longer.push(0);
    assert!(decode_feature_report(&longer).is_none());
    // Report ID, status, the reserved bytes, the length, class, command,
    // slot header, a padding byte, the checksum and the terminator.
    for index in [0, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 319, 372, 373, 374] {
        let mut broken = valid;
        broken[index] ^= 0x01;
        assert!(decode_feature_report(&broken).is_none(), "byte {index}");
    }
    // A color change without its checksum.
    let mut broken = valid;
    broken[14] ^= 0x01;
    assert!(decode_feature_report(&broken).is_none());
}

#[test]
fn a_uniform_frame_keeps_every_hole_black() {
    let lit = color("FFFFFF");
    let frame = MatrixFrame::uniform(lit);
    assert!(!frame.logical_key_colors());
    for (slot, value) in frame.slots().iter().enumerate() {
        let expected = if is_canvas_hole(slot) {
            Rgb::default()
        } else {
            lit
        };
        assert_eq!(*value, expected, "slot {slot}");
    }
}

/// The canvas holes are the reviewed source fixture's own canvas table holes.
#[test]
fn the_canvas_holes_are_the_source_canvas_table() {
    let maps = Maps::load();
    let holes: Vec<usize> = (0..SLOT_COUNT)
        .filter(|&slot| maps.canvas[slot] == 0)
        .collect();
    assert_eq!(holes, CANVAS_HOLES);
}

/// On the ANSI canvas layout every canvas key maps to itself, so a spatial
/// frame projects to itself with the holes black.
#[test]
fn the_canvas_layout_projects_a_frame_onto_itself() {
    let maps = Maps::load();
    let layout = maps.layout();
    let projection = RegionalProjection::new(&layout, 0);
    let mut slots = [Rgb::default(); SLOT_COUNT];
    for (slot, value) in slots.iter_mut().enumerate() {
        if !is_canvas_hole(slot) {
            *value = Rgb {
                red: slot as u8 + 1,
                green: 0,
                blue: 0,
            };
        }
    }
    for logical in [false, true] {
        let frame = MatrixFrame::new(slots, logical);
        assert_eq!(projection.project(&frame), frame);
    }
}

/// A regional key that only a canvas hole's transient pulse identity reaches
/// samples its nearest canvas color for spatial content and shows that
/// hole's slot for per-key content, as managed projects; a slot without a
/// key stays black.
#[test]
fn a_regional_key_reached_only_by_a_hole_samples_spatially() {
    let maps = Maps::load();
    let layout = maps.layout();
    let mut slots = [Rgb::default(); SLOT_COUNT];
    for (slot, value) in slots.iter_mut().enumerate() {
        *value = Rgb {
            red: slot as u8 + 1,
            green: 1,
            blue: 1,
        };
    }
    let mut sampled = 0;
    for id in 0..=u8::MAX {
        let projection = RegionalProjection::new(&layout, id);
        let spatial = projection.project(&MatrixFrame::new(slots, false));
        let keys = projection.project(&MatrixFrame::new(slots, true));
        for slot in 0..SLOT_COUNT {
            let sources: Vec<usize> = (0..SLOT_COUNT)
                .filter(|&canvas| layout.map_canvas_slot(canvas, id) == Some(slot))
                .collect();
            if !layout.is_key_slot(slot, id) {
                assert!(sources.is_empty(), "layout {id} slot {slot}");
                assert_eq!(spatial.slots()[slot], Rgb::default());
                assert_eq!(keys.slots()[slot], Rgb::default());
                continue;
            }
            // The last mapped canvas slot wins for per-key content.
            let last = *sources.last().expect("every regional key is reached");
            assert_eq!(keys.slots()[slot], slots[last], "layout {id} slot {slot}");
            let spatial_source = sources
                .iter()
                .rev()
                .find(|&&canvas| !is_canvas_hole(canvas));
            match spatial_source {
                Some(&canvas) => assert_eq!(spatial.slots()[slot], slots[canvas]),
                None => {
                    let shown = usize::from(spatial.slots()[slot].red) - 1;
                    assert!(!is_canvas_hole(shown), "layout {id} slot {slot}");
                    sampled += 1;
                }
            }
        }
    }
    assert!(sampled > 0, "some regional key samples a neighbour");
}

#[test]
fn an_out_of_range_canvas_slot_maps_nowhere() {
    let maps = Maps::load();
    let layout = maps.layout();
    assert_eq!(layout.map_canvas_slot(SLOT_COUNT, 0), None);
    assert!(!layout.is_key_slot(SLOT_COUNT, 0));
    // The canvas hole at slot 0 carries no usage.
    assert_eq!(layout.map_canvas_slot(0, 0), None);
}

/// The layout query and handshake are the oracle's transition requests, and
/// their replies are its transition responses.
#[test]
fn the_layout_query_and_handshake_are_the_oracle_transition() {
    let protocol = &oracle()["protocol"];
    let requests = protocol["transitionRequests"].as_array().unwrap();
    let responses = protocol["transitionResponses"].as_array().unwrap();
    let layout = LayoutQuery::new(3);
    assert_eq!(requests[0], notation(layout.request()));
    assert_eq!(responses[0], "00/86:010000");
    assert_eq!(layout.decode(&reply(layout.request(), &[1, 0, 0])), Ok(1));
    let handshake = TransitionSet::new(4);
    assert_eq!(requests[2], notation(handshake.request()));
    assert_eq!(responses[2], "03/0A:0500");
    assert_eq!(
        handshake.acknowledge(&reply(handshake.request(), &[5, 0])),
        Ok(())
    );
}

#[test]
fn the_layout_byte_ignores_the_opaque_metadata() {
    let query = LayoutQuery::new(9);
    assert_eq!(
        query.decode(&reply(query.request(), &[12, 0xaa, 0x55])),
        Ok(12)
    );
}

#[test]
fn a_layout_reply_of_another_length_is_refused() {
    let query = LayoutQuery::new(9);
    assert_eq!(
        query.decode(&reply(query.request(), &[1, 0])),
        Err(ProtocolValidationFailure::PayloadLength)
    );
    let other = LayoutQuery::new(10);
    assert!(query.decode(&reply(other.request(), &[1, 0, 0])).is_err());
}

#[test]
fn a_handshake_without_the_exact_echo_is_refused() {
    let handshake = TransitionSet::new(4);
    for payload in [[5, 1], [4, 0], [0, 0]] {
        assert_eq!(
            handshake.acknowledge(&reply(handshake.request(), &payload)),
            Err(ProtocolValidationFailure::PayloadValidator)
        );
    }
    assert_eq!(
        handshake.acknowledge(&reply(handshake.request(), &[5, 0, 0])),
        Err(ProtocolValidationFailure::PayloadLength)
    );
}
