//! Managed `Blade16KeyboardLightingLayout`: the exact RZ09-0581 MCU source
//! maps the matrix projection reads, loaded from the managed embedded
//! resource `Blade16KeyboardLightingLayout.json` once per process and refused
//! unless its pin and every reviewed source identity managed requires hold.
//! A refused fixture leaves the matrix inadmissible (`lighting_admission`),
//! as managed's static constructor fails its lighting factory.
use super::MODEL;
use openblade_protocol::lighting::matrix::{CANVAS_HOLES, COLUMNS, KeyboardLayout, SLOT_COUNT};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::{collections::BTreeMap, sync::OnceLock};

/// The managed embedded resource, exact bytes.
const FIXTURE: &[u8] = include_bytes!(
    "../../../../../../src/OpenBlade.Contracts/Fixtures/Blade16KeyboardLightingLayout.json"
);
/// Managed `FixtureSha256`, which the semantic admission's
/// `matrix.sourceLayoutSha256` also names.
pub(super) const FIXTURE_SHA256: &str =
    "187711408FC0667273CABB19A1CB73B57CA53F98688993BA76E7352A8F2AE0FB";
const IMAGE_SHA256: &str = "90bbe0d8dd1ca31614a2465c7c8821347b2a667606c39bedadbd59b98886344f";
const CANVAS_TABLE: u64 = 0xb345;
const TABLE_OFFSETS: [u64; 6] = [0xb345, 0xb915, 0xbdb5, 0xc7bd, 0xcce5, 0xd1b5];
const UNION_HOLES: [usize; 9] = [0, 17, 34, 51, 65, 68, 81, 85, 92];

/// The verified maps: the six physical tables, the table each layout byte
/// selects, and the canvas table.
pub(crate) struct SourceMaps {
    tables: Vec<[u8; SLOT_COUNT]>,
    selector: [u8; 256],
    canvas: usize,
}

impl SourceMaps {
    pub(crate) fn layout(&self) -> KeyboardLayout<'_> {
        KeyboardLayout {
            canvas: &self.tables[self.canvas],
            tables: &self.tables,
            selector: &self.selector,
        }
    }
}

/// The verified maps, or `None` when the fixture fails any check.
pub(crate) fn source_maps() -> Option<&'static SourceMaps> {
    static MAPS: OnceLock<Option<SourceMaps>> = OnceLock::new();
    MAPS.get_or_init(|| load(FIXTURE)).as_ref()
}

/// Managed `ExpectedSelection`: the reviewed firmware branch, table,
/// auxiliary table and record count of each layout byte.
fn expected_selection(id: u8) -> [u64; 4] {
    match id {
        0 | 1 | 5 | 11 => [0x5cd8, 0xb345, 0xb1cd, 47],
        8 => [0x5d00, 0xbdb5, 0xbc45, 46],
        9 => [0x5ce8, 0xb915, 0xb675, 84],
        7 => [0x5d46, 0xcce5, 0xcaed, 63],
        12 => [0x5d10, 0xd1b5, 0xd015, 52],
        3 => [0x5d3a, 0xc7bd, 0xc605, 55],
        4 => [0x5d2e, 0xc7bd, 0xc42d, 59],
        14 | 16 => [0x5d22, 0xc7bd, 0xc26d, 56],
        _ => [0x5d56, 0xc7bd, 0xc0e5, 49],
    }
}

fn number(value: &Value) -> Option<u64> {
    value.as_u64()
}

fn byte(value: &Value) -> Option<u8> {
    number(value).and_then(|value| u8::try_from(value).ok())
}

fn slots(value: &Value) -> Option<Vec<usize>> {
    value
        .as_array()?
        .iter()
        .map(|slot| number(slot).and_then(|slot| usize::try_from(slot).ok()))
        .collect()
}

/// Managed's static constructor, each `Require` in its order.
fn load(bytes: &[u8]) -> Option<SourceMaps> {
    let digest: String = Sha256::digest(bytes)
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect();
    if digest != FIXTURE_SHA256 {
        return None;
    }
    verify(&serde_json::from_slice(bytes).ok()?)
}

fn verify(root: &Value) -> Option<SourceMaps> {
    let identity = number(&root["schemaVersion"]) == Some(1)
        && root["model"] == MODEL
        && number(&root["vendorId"]) == Some(u64::from(super::DEVICE.0))
        && number(&root["productId"]) == Some(u64::from(super::DEVICE.1))
        && number(&root["rows"]) == Some((SLOT_COUNT / COLUMNS) as u64)
        && number(&root["columns"]) == Some(COLUMNS as u64)
        && number(&root["recordSize"]) == Some(8)
        && number(&root["source"]["imageLength"]) == Some(57764)
        && root["source"]["imageSha256"] == IMAGE_SHA256;
    if !identity {
        return None;
    }
    let mut tables = Vec::new();
    let mut offsets = Vec::new();
    let mut declared = BTreeMap::new();
    let mut union = [false; SLOT_COUNT];
    for table in root["tables"].as_array()? {
        let offset = number(&table["offset"]).filter(|offset| TABLE_OFFSETS.contains(offset))?;
        let usages: Vec<u8> = table["hidUsages"]
            .as_array()?
            .iter()
            .map(byte)
            .collect::<Option<_>>()?;
        let usages: [u8; SLOT_COUNT] = usages.try_into().ok()?;
        if number(&table["length"]) != Some((SLOT_COUNT * 8) as u64) || offsets.contains(&offset) {
            return None;
        }
        let holes: Vec<usize> = (0..SLOT_COUNT).filter(|&slot| usages[slot] == 0).collect();
        let mut keys: Vec<u8> = usages.iter().copied().filter(|&usage| usage != 0).collect();
        keys.sort_unstable();
        keys.dedup();
        if slots(&table["holes"])? != holes
            || number(&table["nonzeroCount"]) != Some((SLOT_COUNT - holes.len()) as u64)
            || keys.len() != SLOT_COUNT - holes.len()
        {
            return None;
        }
        for id in table["layoutIds"].as_array()? {
            if declared.insert(byte(id)?, offset).is_some() {
                return None;
            }
        }
        for (slot, key) in union.iter_mut().enumerate() {
            *key |= usages[slot] != 0;
        }
        offsets.push(offset);
        tables.push(usages);
    }
    if tables.len() != TABLE_OFFSETS.len() || declared.len() != 256 {
        return None;
    }
    let selector = &root["selector"];
    let entries = selector["allByteValues"].as_array()?;
    if number(&selector["storedByteAddress"]) != Some(0x1000_1990)
        || selector["query"] != "00/86"
        || number(&selector["queryPayloadIndex"]) != Some(0)
        || entries.len() != 256
    {
        return None;
    }
    let mut selection = [None; 256];
    for entry in entries {
        let id = byte(&entry["layoutId"])?;
        let actual = [
            "branchOffset",
            "tableOffset",
            "auxiliaryTableOffset",
            "auxiliaryRecordCount",
        ]
        .map(|member| number(&entry[member]));
        let expected = expected_selection(id);
        if selection[usize::from(id)].is_some()
            || actual != expected.map(Some)
            || declared.get(&id) != Some(&expected[1])
        {
            return None;
        }
        let table = offsets.iter().position(|&offset| offset == expected[1])?;
        selection[usize::from(id)] = Some(u8::try_from(table).ok()?);
    }
    let selector = selection.map(|table| table.unwrap_or(u8::MAX));
    let canvas = offsets.iter().position(|&offset| offset == CANVAS_TABLE)?;
    let canvas_holes: Vec<usize> = (0..SLOT_COUNT)
        .filter(|&slot| tables[canvas][slot] == 0)
        .collect();
    let union_holes: Vec<usize> = (0..SLOT_COUNT).filter(|&slot| !union[slot]).collect();
    if selector.contains(&u8::MAX)
        || canvas_holes != CANVAS_HOLES
        || union_holes != UNION_HOLES
        || slots(&root["unionHoles"])? != union_holes
    {
        return None;
    }
    Some(SourceMaps {
        tables,
        selector,
        canvas,
    })
}

#[cfg(test)]
mod tests;
