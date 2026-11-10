//! Fixed private CPU worker stdio frames. See CpuTemperatureProtocol.cs.

pub const REQUEST_LENGTH: usize = 8;
pub const REPLY_LENGTH: usize = 20;
const MAGIC: u32 = 0x3154_5043;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Sequence(i64);

impl Sequence {
    pub fn new(value: i64) -> Option<Self> {
        (value > 0).then_some(Self(value))
    }

    pub fn encode(self) -> [u8; REQUEST_LENGTH] {
        self.0.to_le_bytes()
    }

    pub fn decode(frame: &[u8]) -> Option<Self> {
        Self::new(i64::from_le_bytes(frame.try_into().ok()?))
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Reply {
    Unavailable,
    Value(MilliCelsius),
    Ready,
    TerminalUnavailable,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MilliCelsius(i32);

impl MilliCelsius {
    pub fn new(value: i32) -> Option<Self> {
        (10_000..=125_000).contains(&value).then_some(Self(value))
    }

    pub fn rounded_celsius(self) -> u8 {
        // Never round a safety input down.
        ((self.0 + 999) / 1000) as u8
    }
}

impl Reply {
    pub fn encode(self, sequence: Sequence) -> [u8; REPLY_LENGTH] {
        let (status, milli_celsius) = match self {
            Self::Unavailable => (0_i32, 0),
            Self::Value(value) => (1, value.0),
            Self::Ready => (2, 0),
            Self::TerminalUnavailable => (3, 0),
        };
        let mut frame = [0; REPLY_LENGTH];
        frame[..4].copy_from_slice(&MAGIC.to_le_bytes());
        frame[4..12].copy_from_slice(&sequence.encode());
        frame[12..16].copy_from_slice(&status.to_le_bytes());
        frame[16..].copy_from_slice(&milli_celsius.to_le_bytes());
        frame
    }
}

pub fn decode_reply(frame: &[u8], sequence: Sequence) -> Option<Reply> {
    let frame: &[u8; REPLY_LENGTH] = frame.try_into().ok()?;
    if frame[..4] != MAGIC.to_le_bytes() || frame[4..12] != sequence.encode() {
        return None;
    }
    let status = i32::from_le_bytes(frame[12..16].try_into().unwrap());
    let milli_celsius = i32::from_le_bytes(frame[16..20].try_into().unwrap());
    match (status, milli_celsius) {
        (0, 0) => Some(Reply::Unavailable),
        (1, value) => MilliCelsius::new(value).map(Reply::Value),
        (2, 0) => Some(Reply::Ready),
        (3, 0) => Some(Reply::TerminalUnavailable),
        _ => None,
    }
}
