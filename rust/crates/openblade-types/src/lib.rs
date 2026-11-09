//! Portable structured protocol errors. Numeric meanings match C# diagnostics.

#![no_std]

/// Existing `HidProtocolValidationFailure` values, independent of any future ABI.
/// Report ID and semantic payload validation remain host responsibilities.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u32)]
pub enum ProtocolValidationFailure {
    ReportId = 0,
    EnvelopeLength = 1,
    Checksum = 2,
    TransactionMismatch = 3,
    CommandClassMismatch = 4,
    CommandIdMismatch = 5,
    Status = 6,
    PayloadLength = 7,
    PayloadValidator = 8,
}

/// Rejected encoding inputs. No destination bytes are changed on these errors.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReportEncodingError {
    PayloadTooLarge { actual: usize },
    DestinationLength { actual: usize },
}
