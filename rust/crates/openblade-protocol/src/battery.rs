//! Exact RZ09-0581 policy and full Power Mode Control codecs.
//! BatteryValidationProtocol/TemporaryFullChargeProtocol and pinned 0581 fixture.
//! Captured BIOS is provenance, never a production admission gate.
use crate::RazerReport;
use openblade_types::ProtocolValidationFailure;
pub const CONFIRMATION_READS: usize = 9;
pub const CONFIRMATION_INTERVAL_MS: u64 = 250;
pub fn admitted_limit(limit: u8) -> bool {
    (50..=80).contains(&limit) && limit.is_multiple_of(5)
}
pub fn valid_policy(raw: u8) -> bool {
    raw == 0x50 || raw & 0x80 != 0 && admitted_limit(raw & 0x7f)
}
pub fn target(enabled: bool, limit: u8) -> Option<u8> {
    admitted_limit(limit).then_some(if enabled { 0x80 | limit } else { 0x50 })
}
pub fn policy_query(id: u8) -> RazerReport {
    RazerReport::create(id, 7, 0x92, &[0]).expect("fixed policy query")
}
pub fn temporary_query(id: u8) -> RazerReport {
    RazerReport::create(id, 7, 0x8f, &[0]).expect("fixed temporary query")
}
pub fn setter(id: u8, raw: u8) -> Option<RazerReport> {
    valid_policy(raw)
        .then(|| RazerReport::create(id, 7, 0x12, &[raw]).expect("fixed policy setter"))
}
pub fn policy(query: &RazerReport, reply: &[u8]) -> Result<u8, ProtocolValidationFailure> {
    let p = query.validate_response(reply, Some(1))?;
    let raw = p.as_slice()[0];
    if valid_policy(raw) {
        Ok(raw)
    } else {
        Err(ProtocolValidationFailure::PayloadValidator)
    }
}
pub fn power_flags(query: &RazerReport, reply: &[u8]) -> Result<u8, ProtocolValidationFailure> {
    let p = query.validate_response(reply, Some(1))?;
    let raw = p.as_slice()[0];
    if raw & !0x0f == 0 {
        Ok(raw)
    } else {
        Err(ProtocolValidationFailure::PayloadValidator)
    }
}
pub fn temporary(query: &RazerReport, reply: &[u8]) -> Result<bool, ProtocolValidationFailure> {
    power_flags(query, reply).map(|raw| raw & 4 != 0)
}
/// SET replaces the whole byte. This typed writer admits only TFC, never a merge.
pub fn temporary_setter(id: u8, enabled: bool) -> RazerReport {
    RazerReport::create(id, 7, 0x0f, &[if enabled { 4 } else { 0 }])
        .expect("fixed temporary setter")
}
pub fn temporary_write_admitted(flags: u8) -> bool {
    flags & !4 == 0
}
pub fn acknowledge(
    command: &RazerReport,
    reply: &[u8],
    target: u8,
) -> Result<(), ProtocolValidationFailure> {
    if command.validate_response(reply, Some(1))?.as_slice() == [target] {
        Ok(())
    } else {
        Err(ProtocolValidationFailure::PayloadValidator)
    }
}
