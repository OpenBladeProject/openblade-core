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

/// The exact RZ09-0581 preset destinations admitted by the installed slice:
/// each is one reviewed 0D/02 pair `[1, channel, selector, 0]` in the managed
/// fixture `Rz09_0581_02e0_3_01_BatteryProduction.json`. Numeric values
/// preserve the managed PerformancePreset wire contract. Custom (5) is not a
/// pair-only destination and stays outside this type.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(i32)]
pub enum InstalledThermalPreset {
    Silent = 0,
    Balanced = 1,
    Performance = 2,
    BatterySaver = 3,
    BatteryBalanced = 4,
}
impl InstalledThermalPreset {
    pub const fn from_wire(value: i32) -> Option<Self> {
        match value {
            0 => Some(Self::Silent),
            1 => Some(Self::Balanced),
            2 => Some(Self::Performance),
            3 => Some(Self::BatterySaver),
            4 => Some(Self::BatteryBalanced),
            _ => None,
        }
    }
    /// The firmware mode byte of the preset's 0D/02 pair.
    pub const fn selector(self) -> u8 {
        match self {
            Self::Silent => 5,
            Self::Balanced => 0,
            Self::Performance => 2,
            Self::BatterySaver => 3,
            Self::BatteryBalanced => 6,
        }
    }
    /// The battery selectors 3 and 6 (managed `IsBatterySelectorCommand`).
    pub const fn is_battery(self) -> bool {
        matches!(self, Self::BatterySaver | Self::BatteryBalanced)
    }
}
