//! The EDID formats used by the managed refresh read: DisplayID Type 7
//! detailed timings and the base-block vertical range descriptor.

use super::{DISPLAYCONFIG_RATIONAL, DisplayReadError, VerticalRange};

const BLOCK_LENGTH: usize = 128;
const HEADER: [u8; 8] = [0, 255, 255, 255, 255, 255, 255, 0];

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(in crate::display) struct DetailedTiming {
    pub(in crate::display) width: u32,
    pub(in crate::display) height: u32,
    pub(in crate::display) hertz: i32,
    pub(in crate::display) pixel_rate: u64,
    pub(in crate::display) total_width: u32,
    pub(in crate::display) total_height: u32,
}

impl DetailedTiming {
    pub(in crate::display) fn ratios_fit(self) -> bool {
        ratio_fits(self.pixel_rate, u64::from(self.total_width))
            && ratio_fits(
                self.pixel_rate,
                u64::from(self.total_width) * u64::from(self.total_height),
            )
    }
}

fn ratio_fits(numerator: u64, denominator: u64) -> bool {
    rational(numerator, denominator).is_some()
}

pub(in crate::display) fn rational(
    numerator: u64,
    denominator: u64,
) -> Option<DISPLAYCONFIG_RATIONAL> {
    if numerator == 0 || denominator == 0 {
        return None;
    }
    // Match DisplayConfigRational.FromRatio, including reduction before the
    // native u32 numerator/denominator bounds are checked.
    let (mut a, mut b) = (numerator, denominator);
    while b != 0 {
        (a, b) = (b, a % b);
    }
    Some(DISPLAYCONFIG_RATIONAL {
        Numerator: u32::try_from(numerator / a).ok()?,
        Denominator: u32::try_from(denominator / a).ok()?,
    })
}

fn checksum(block: &[u8]) -> bool {
    block.iter().copied().fold(0_u8, u8::wrapping_add) == 0
}

pub(in crate::display) fn vertical_range(edid: &[u8]) -> Option<VerticalRange> {
    let base = edid.get(..BLOCK_LENGTH)?;
    if !base.starts_with(&HEADER) || !checksum(base) {
        return None;
    }
    base[54..126]
        .as_chunks::<18>()
        .0
        .iter()
        .find_map(|descriptor| {
            let (minimum, maximum) = (descriptor[5], descriptor[6]);
            (descriptor[..4] == [0, 0, 0, 0xfd] && minimum >= 20 && maximum > minimum).then_some(
                VerticalRange {
                    minimum_hertz: minimum,
                    maximum_hertz: maximum,
                },
            )
        })
}

pub(in crate::display) fn detailed_timings(
    edid: &[u8],
) -> Result<Vec<DetailedTiming>, DisplayReadError> {
    let mut blocks = edid
        .as_chunks::<BLOCK_LENGTH>()
        .0
        .iter()
        .map(<[_; BLOCK_LENGTH]>::as_slice);
    let Some(base) = blocks.next().filter(|block| checksum(block)) else {
        return Ok(Vec::new());
    };
    let mut timings = Vec::new();
    for extension in blocks.take(usize::from(base[126])) {
        if extension[0] != 0x70 || !checksum(extension) {
            continue;
        }
        let mut payload = &extension[5..(5 + usize::from(extension[2])).min(127)];
        while let [tag, _, length, remaining @ ..] = payload {
            let length = usize::from(*length);
            if length == 0 || length > remaining.len() {
                break;
            }
            let (data, next) = remaining.split_at(length);
            if *tag == 0x22 {
                for bytes in data.as_chunks::<20>().0 {
                    timings.extend(parse_timing(bytes)?);
                }
            }
            payload = next;
        }
    }
    Ok(timings)
}

fn parse_timing(bytes: &[u8; 20]) -> Result<Option<DetailedTiming>, DisplayReadError> {
    if bytes[3] & 0x10 != 0 {
        return Ok(None);
    }
    let word = |offset| u32::from(u16::from_le_bytes([bytes[offset], bytes[offset + 1]])) + 1;
    let pixel_rate = (u64::from(u32::from_le_bytes([bytes[0], bytes[1], bytes[2], 0])) + 1) * 1000;
    let width = word(4);
    let height = word(12);
    let total_width = width + word(6);
    let total_height = height + word(14);
    let total_pixels = u64::from(total_width) * u64::from(total_height);
    let hertz = (pixel_rate as f64 / total_pixels as f64).round();
    if hertz > f64::from(i32::MAX) {
        return Err(DisplayReadError::RefreshRateOverflow);
    }
    let hertz = hertz as i32;
    Ok((20..=1000).contains(&hertz).then_some(DetailedTiming {
        width,
        height,
        hertz,
        pixel_rate,
        total_width,
        total_height,
    }))
}
