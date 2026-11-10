//! The EDID formats used by the managed refresh read: DisplayID Type 7
//! detailed timings and the base-block vertical range descriptor.

use super::VerticalRange;

const BLOCK_LENGTH: usize = 128;
const HEADER: [u8; 8] = [0, 255, 255, 255, 255, 255, 255, 0];

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) struct DetailedTiming {
    pub(super) width: u32,
    pub(super) height: u32,
    pub(super) hertz: i32,
    pixel_rate: u64,
    total_width: u32,
    total_height: u32,
}

impl DetailedTiming {
    pub(super) fn ratios_fit(self) -> bool {
        ratio_fits(self.pixel_rate, u64::from(self.total_width))
            && ratio_fits(
                self.pixel_rate,
                u64::from(self.total_width) * u64::from(self.total_height),
            )
    }
}

fn ratio_fits(numerator: u64, denominator: u64) -> bool {
    // Match DisplayConfigRational.FromRatio, including reduction before the
    // native u32 numerator/denominator bounds are checked.
    let (mut a, mut b) = (numerator, denominator);
    while b != 0 {
        (a, b) = (b, a % b);
    }
    u32::try_from(numerator / a).is_ok() && u32::try_from(denominator / a).is_ok()
}

fn checksum(block: &[u8]) -> bool {
    block.iter().copied().fold(0_u8, u8::wrapping_add) == 0
}

pub(super) fn vertical_range(edid: &[u8]) -> Option<VerticalRange> {
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

pub(super) fn detailed_timings(edid: &[u8]) -> Vec<DetailedTiming> {
    let mut blocks = edid
        .as_chunks::<BLOCK_LENGTH>()
        .0
        .iter()
        .map(<[_; BLOCK_LENGTH]>::as_slice);
    let Some(base) = blocks.next().filter(|block| checksum(block)) else {
        return Vec::new();
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
                timings.extend(data.as_chunks::<20>().0.iter().filter_map(parse_timing));
            }
            payload = next;
        }
    }
    timings
}

fn parse_timing(bytes: &[u8; 20]) -> Option<DetailedTiming> {
    if bytes[3] & 0x10 != 0 {
        return None;
    }
    let word = |offset| u32::from(u16::from_le_bytes([bytes[offset], bytes[offset + 1]])) + 1;
    let pixel_rate = (u64::from(u32::from_le_bytes([bytes[0], bytes[1], bytes[2], 0])) + 1) * 1000;
    let width = word(4);
    let height = word(12);
    let total_width = width + word(6);
    let total_height = height + word(14);
    let total_pixels = u64::from(total_width) * u64::from(total_height);
    let hertz = (pixel_rate as f64 / total_pixels as f64).round() as i32;
    (20..=1000).contains(&hertz).then_some(DetailedTiming {
        width,
        height,
        hertz,
        pixel_rate,
        total_width,
        total_height,
    })
}
