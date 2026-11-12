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

/// The exact-model getter vocabulary; broader saved settings may include Boost.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u8)]
pub enum CustomPerformanceLevel {
    Low = 0,
    Medium = 1,
    High = 2,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u8)]
pub enum CustomPerformanceComponent {
    Cpu = 1,
    Gpu = 2,
}

/// The two exact RZ09-0581 destinations admitted by the installed slice.
/// Numeric values preserve the managed PerformancePreset wire contract.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(i32)]
pub enum InstalledThermalPreset {
    Balanced = 1,
    Performance = 2,
}
impl InstalledThermalPreset {
    pub const fn from_wire(value: i32) -> Option<Self> {
        match value {
            1 => Some(Self::Balanced),
            2 => Some(Self::Performance),
            _ => None,
        }
    }
    pub const fn selector(self) -> u8 {
        match self {
            Self::Balanced => 0,
            Self::Performance => 2,
        }
    }
}
