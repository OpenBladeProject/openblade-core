//! Pure 90-byte report envelopes, excluding the OS report-ID prefix.
//! This module admits no command, model, session or hardware operation.

#![no_std]

pub mod battery;
pub mod custom_performance;
pub mod device_mode;
pub mod fan;
pub mod fan_rpm;
pub mod lighting;
pub mod mcu_version;
pub mod thermal_preset;

use openblade_types::{ProtocolValidationFailure, ReportEncodingError};

pub const REPORT_LENGTH: usize = 90;
pub const MAXIMUM_PAYLOAD_LENGTH: usize = 80;

/// Bounded owned bytes; independent of the request/response caller's storage.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReportPayload {
    bytes: [u8; MAXIMUM_PAYLOAD_LENGTH],
    length: u8,
}

impl ReportPayload {
    pub fn as_slice(&self) -> &[u8] {
        &self.bytes[..usize::from(self.length)]
    }
}

/// An immutable request with a copied payload and caller-selected correlation ID.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RazerReport {
    transaction_id: u8,
    command_class: u8,
    command_id: u8,
    payload: ReportPayload,
}

impl RazerReport {
    pub fn create(
        transaction_id: u8,
        command_class: u8,
        command_id: u8,
        payload: &[u8],
    ) -> Result<Self, ReportEncodingError> {
        let length = checked_payload_length(payload)?;
        let mut bytes = [0; MAXIMUM_PAYLOAD_LENGTH];
        bytes[..payload.len()].copy_from_slice(payload);
        Ok(Self {
            transaction_id,
            command_class,
            command_id,
            payload: ReportPayload { bytes, length },
        })
    }

    pub fn encode(&self) -> [u8; REPORT_LENGTH] {
        encode_validated(
            self.transaction_id,
            self.command_class,
            self.command_id,
            self.payload.as_slice(),
            self.payload.length,
        )
    }

    /// Requires exactly 90 destination bytes, preserving the C# span contract.
    pub fn encode_into(&self, destination: &mut [u8]) -> Result<(), ReportEncodingError> {
        check_destination(destination)?;
        destination.copy_from_slice(&self.encode());
        Ok(())
    }

    /// Preserves C# fault precedence and accepts only successful (0x02) status.
    /// Signed expected lengths deliberately retain C#'s rejection of negatives.
    /// Reserved/header padding and byte 89 are not validated by the C# helper.
    pub fn validate_response(
        &self,
        response: &[u8],
        expected_payload_length: Option<i32>,
    ) -> Result<ReportPayload, ProtocolValidationFailure> {
        let checksum = calculate_checksum(response)?;
        if response[88] != checksum {
            return Err(ProtocolValidationFailure::Checksum);
        }
        if response[1] != self.transaction_id {
            return Err(ProtocolValidationFailure::TransactionMismatch);
        }
        if response[6] != self.command_class {
            return Err(ProtocolValidationFailure::CommandClassMismatch);
        }
        if response[7] != self.command_id {
            return Err(ProtocolValidationFailure::CommandIdMismatch);
        }
        if response[0] != 0x02 {
            return Err(ProtocolValidationFailure::Status);
        }
        let length = response[5];
        if usize::from(length) > MAXIMUM_PAYLOAD_LENGTH
            || expected_payload_length.is_some_and(|expected| expected != i32::from(length))
        {
            return Err(ProtocolValidationFailure::PayloadLength);
        }
        let mut bytes = [0; MAXIMUM_PAYLOAD_LENGTH];
        bytes[..usize::from(length)].copy_from_slice(&response[8..8 + usize::from(length)]);
        Ok(ReportPayload { bytes, length })
    }

    /// A correlated terminal rejection of this request: checksum, transaction,
    /// command class and command ID all match, the raw status is FAILED (3) or
    /// NOT_SUPPORTED (5), and the single-report geometry is intact. Mirrors C#
    /// `OrdinaryThermalPowerAdmission.IsCompletedAdapterRejection` and
    /// `HasSingleReportGeometry`. New, busy and timeout statuses are not
    /// completed. An error body is never payload evidence, so none is returned.
    pub fn is_completed_rejection(&self, response: &[u8]) -> bool {
        self.validate_response(response, None) == Err(ProtocolValidationFailure::Status)
            && matches!(response[0], 3 | 5)
            && has_single_report_geometry(response)
    }
}

/// One whole report: reserved bytes 2-4 and the trailing byte are zero and the
/// payload length fits it. C# `OrdinaryThermalPowerAdmission.HasSingleReportGeometry`,
/// which managed requires of a rejection and of a success reply it maps to
/// unclassified adapter power (`WindowsRazerHidSession`).
pub fn has_single_report_geometry(response: &[u8]) -> bool {
    response.len() == REPORT_LENGTH
        && response[2..5] == [0, 0, 0]
        && usize::from(response[5]) <= MAXIMUM_PAYLOAD_LENGTH
        && response[89] == 0
}

/// Static encoding validates payload size before destination size, then publishes.
pub fn encode_report(
    transaction_id: u8,
    command_class: u8,
    command_id: u8,
    payload: &[u8],
    destination: &mut [u8],
) -> Result<(), ReportEncodingError> {
    let length = checked_payload_length(payload)?;
    check_destination(destination)?;
    destination.copy_from_slice(&encode_validated(
        transaction_id,
        command_class,
        command_id,
        payload,
        length,
    ));
    Ok(())
}

/// XOR of offsets 2..=87. The complete envelope length is checked before indexing.
pub fn calculate_checksum(report: &[u8]) -> Result<u8, ProtocolValidationFailure> {
    if report.len() != REPORT_LENGTH {
        return Err(ProtocolValidationFailure::EnvelopeLength);
    }
    Ok(checksum_envelope(report))
}

fn checked_payload_length(payload: &[u8]) -> Result<u8, ReportEncodingError> {
    if payload.len() > MAXIMUM_PAYLOAD_LENGTH {
        return Err(ReportEncodingError::PayloadTooLarge {
            actual: payload.len(),
        });
    }
    // The explicit 80-byte bound above proves this conversion fits.
    Ok(payload.len() as u8)
}

fn check_destination(destination: &[u8]) -> Result<(), ReportEncodingError> {
    if destination.len() != REPORT_LENGTH {
        return Err(ReportEncodingError::DestinationLength {
            actual: destination.len(),
        });
    }
    Ok(())
}

fn encode_validated(
    transaction_id: u8,
    command_class: u8,
    command_id: u8,
    payload: &[u8],
    length: u8,
) -> [u8; REPORT_LENGTH] {
    let mut report = [0; REPORT_LENGTH];
    report[1] = transaction_id;
    report[5] = length;
    report[6] = command_class;
    report[7] = command_id;
    report[8..8 + payload.len()].copy_from_slice(payload);
    report[88] = checksum_envelope(&report);
    report
}

fn checksum_envelope(report: &[u8]) -> u8 {
    report[2..=87]
        .iter()
        .fold(0, |checksum, byte| checksum ^ byte)
}
