//! A ZIP container as managed `ZipArchive` writes one in Create mode: local
//! headers, the entries, the central directory and its end record, with no
//! ZIP64, data descriptor or extra field. Each entry is raw DEFLATE (fixed
//! Huffman codes over an LZ77 window), or stored when that is not smaller.
//! Managed `ZipArchive` reads both methods.

use super::{Cancel, Failure};

const LOCAL_HEADER: u32 = 0x0403_4b50;
const CENTRAL_HEADER: u32 = 0x0201_4b50;
const END_OF_CENTRAL_DIRECTORY: u32 = 0x0605_4b50;
const VERSION: u16 = 20;
const STORED: u16 = 0;
const DEFLATED: u16 = 8;

/// The managed size refusal (`DiagnosticArchiveProvider.CreateAsync`).
pub(crate) const OVERSIZED: &str = "Diagnostic archive exceeds the 16 MiB limit.";

struct Central {
    name: &'static str,
    method: u16,
    crc: u32,
    compressed: u32,
    size: u32,
    offset: u32,
}

pub(crate) struct Writer {
    bytes: Vec<u8>,
    entries: Vec<Central>,
    time: u16,
    date: u16,
    limit: usize,
}

impl Writer {
    /// `dos` is the (time, date) stamped on every entry; `limit` is the
    /// archive bound (`MAXIMUM_ARCHIVE_BYTES` in production).
    pub(crate) fn bounded((time, date): (u16, u16), limit: usize) -> Self {
        Self {
            bytes: Vec::new(),
            entries: Vec::new(),
            time,
            date,
            limit,
        }
    }

    /// Appends one entry. The archive is refused as soon as it passes the
    /// bound, so a large history never holds more than the bound plus one
    /// entry.
    pub(crate) fn add(
        &mut self,
        name: &'static str,
        content: &[u8],
        cancel: &Cancel,
    ) -> Result<(), Failure> {
        cancel.check()?;
        let crc = crc32(content);
        let deflated = deflate(content, cancel)?;
        let (method, data) = if !content.is_empty() && deflated.len() < content.len() {
            (DEFLATED, deflated.as_slice())
        } else {
            (STORED, content)
        };
        let oversized = || Failure::new(OVERSIZED);
        let entry = Central {
            name,
            method,
            crc,
            compressed: u32::try_from(data.len()).map_err(|_| oversized())?,
            size: u32::try_from(content.len()).map_err(|_| oversized())?,
            offset: u32::try_from(self.bytes.len()).map_err(|_| oversized())?,
        };
        self.reserve(30 + name.len() + data.len())?;
        self.u32(LOCAL_HEADER);
        self.u16(VERSION);
        self.u16(0);
        self.header_fields(&entry);
        self.u16(0);
        self.bytes.extend_from_slice(name.as_bytes());
        self.bytes.extend_from_slice(data);
        self.entries.push(entry);
        Ok(())
    }

    pub(crate) fn finish(mut self) -> Result<Vec<u8>, Failure> {
        let start = self.bytes.len();
        let entries = std::mem::take(&mut self.entries);
        for entry in &entries {
            self.reserve(46 + entry.name.len())?;
            self.u32(CENTRAL_HEADER);
            self.u16(VERSION);
            self.u16(VERSION);
            self.u16(0);
            self.header_fields(entry);
            self.u16(0); // extra
            self.u16(0); // comment
            self.u16(0); // disk
            self.u16(0); // internal attributes
            self.u32(0); // external attributes
            self.u32(entry.offset);
            self.bytes.extend_from_slice(entry.name.as_bytes());
        }
        let size = self.bytes.len() - start;
        self.reserve(22)?;
        self.u32(END_OF_CENTRAL_DIRECTORY);
        self.u16(0);
        self.u16(0);
        let count = u16::try_from(entries.len()).map_err(|_| Failure::new(OVERSIZED))?;
        self.u16(count);
        self.u16(count);
        self.u32(size as u32);
        self.u32(start as u32);
        self.u16(0);
        Ok(self.bytes)
    }

    /// Method, timestamp, CRC, sizes and name length: the run shared by the
    /// local and central headers.
    fn header_fields(&mut self, entry: &Central) {
        self.u16(entry.method);
        self.u16(self.time);
        self.u16(self.date);
        self.u32(entry.crc);
        self.u32(entry.compressed);
        self.u32(entry.size);
        self.u16(entry.name.len() as u16);
    }

    fn reserve(&self, additional: usize) -> Result<(), Failure> {
        if self.bytes.len().saturating_add(additional) > self.limit {
            return Err(Failure::new(OVERSIZED));
        }
        Ok(())
    }

    fn u16(&mut self, value: u16) {
        self.bytes.extend_from_slice(&value.to_le_bytes());
    }

    fn u32(&mut self, value: u32) {
        self.bytes.extend_from_slice(&value.to_le_bytes());
    }
}

/// CRC-32 (IEEE 802.3, reflected), as ZIP headers carry it.
pub(crate) fn crc32(bytes: &[u8]) -> u32 {
    static TABLE: std::sync::OnceLock<[u32; 256]> = std::sync::OnceLock::new();
    let table = TABLE.get_or_init(|| {
        let mut table = [0; 256];
        for (index, entry) in table.iter_mut().enumerate() {
            let mut value = index as u32;
            for _ in 0..8 {
                value = if value & 1 != 0 {
                    0xEDB8_8320 ^ (value >> 1)
                } else {
                    value >> 1
                };
            }
            *entry = value;
        }
        table
    });
    !bytes.iter().fold(!0u32, |crc, byte| {
        table[((crc ^ u32::from(*byte)) & 0xFF) as usize] ^ (crc >> 8)
    })
}

struct Bits {
    bytes: Vec<u8>,
    buffer: u64,
    count: u32,
}

impl Bits {
    /// `count` bits of `value`, least significant first.
    fn put(&mut self, value: u32, count: u32) {
        self.buffer |= u64::from(value) << self.count;
        self.count += count;
        while self.count >= 8 {
            self.bytes.push(self.buffer as u8);
            self.buffer >>= 8;
            self.count -= 8;
        }
    }

    /// A Huffman code, which DEFLATE packs most significant bit first.
    fn code(&mut self, code: u32, length: u32) {
        self.put(code.reverse_bits() >> (32 - length), length);
    }

    fn finish(mut self) -> Vec<u8> {
        if self.count > 0 {
            self.bytes.push(self.buffer as u8);
        }
        self.bytes
    }
}

/// RFC 1951 3.2.6 fixed literal/length code.
fn literal(bits: &mut Bits, symbol: u32) {
    match symbol {
        0..=143 => bits.code(0x30 + symbol, 8),
        144..=255 => bits.code(0x190 + symbol - 144, 9),
        256..=279 => bits.code(symbol - 256, 7),
        _ => bits.code(0xC0 + symbol - 280, 8),
    }
}

const LENGTH_BASE: [u16; 29] = [
    3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131,
    163, 195, 227, 258,
];
const LENGTH_EXTRA: [u8; 29] = [
    0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
];
const DISTANCE_BASE: [u16; 30] = [
    1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537,
    2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577,
];
const DISTANCE_EXTRA: [u8; 30] = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13,
    13,
];

fn matched(bits: &mut Bits, length: usize, distance: usize) {
    let code = LENGTH_BASE
        .iter()
        .rposition(|base| usize::from(*base) <= length)
        .expect("length >= 3");
    literal(bits, 257 + code as u32);
    bits.put(
        (length - usize::from(LENGTH_BASE[code])) as u32,
        u32::from(LENGTH_EXTRA[code]),
    );
    let code = DISTANCE_BASE
        .iter()
        .rposition(|base| usize::from(*base) <= distance)
        .expect("distance >= 1");
    bits.code(code as u32, 5);
    bits.put(
        (distance - usize::from(DISTANCE_BASE[code])) as u32,
        u32::from(DISTANCE_EXTRA[code]),
    );
}

const WINDOW: usize = 32 * 1024;
const MIN_MATCH: usize = 3;
const MAX_MATCH: usize = 258;
const HASH_BITS: u32 = 15;
/// Candidates examined per position: bounds the time a repetitive input
/// can take.
const MAX_CHAIN: usize = 64;
const NONE: u32 = u32::MAX;

/// Input bytes between cancellation checks while compressing.
const CANCEL_INTERVAL: usize = 65_536;

/// One final fixed-Huffman DEFLATE block over greedy LZ77 matches.
pub(crate) fn deflate(input: &[u8], cancel: &Cancel) -> Result<Vec<u8>, Failure> {
    deflate_checked(input, || cancel.check())
}

/// `deflate` with its cancellation check supplied, called at least once per
/// `CANCEL_INTERVAL` input bytes plus one match.
pub(crate) fn deflate_checked(
    input: &[u8],
    mut check: impl FnMut() -> Result<(), Failure>,
) -> Result<Vec<u8>, Failure> {
    let mut bits = Bits {
        bytes: Vec::with_capacity(input.len() / 2),
        buffer: 0,
        count: 0,
    };
    bits.put(1, 1); // BFINAL
    bits.put(1, 2); // BTYPE fixed
    let mut head = vec![NONE; 1 << HASH_BITS];
    let mut previous = vec![NONE; WINDOW];
    let hash = |at: usize| -> usize {
        let value =
            u32::from(input[at]) << 16 | u32::from(input[at + 1]) << 8 | u32::from(input[at + 2]);
        (value.wrapping_mul(0x9E37_79B1) >> (32 - HASH_BITS)) as usize
    };
    let insert = |at: usize, head: &mut Vec<u32>, previous: &mut Vec<u32>| {
        if at + MIN_MATCH <= input.len() {
            let key = hash(at);
            previous[at % WINDOW] = head[key];
            head[key] = at as u32;
        }
    };
    let mut position = 0;
    let mut next_check = 0;
    while position < input.len() {
        // A match advances `position` by up to MAX_MATCH, so a multiple of the
        // interval can be stepped over: compare against the next due position.
        if position >= next_check {
            check()?;
            next_check = position + CANCEL_INTERVAL;
        }
        let mut best = (0, 0);
        if position + MIN_MATCH <= input.len() {
            let limit = (input.len() - position).min(MAX_MATCH);
            let mut candidate = head[hash(position)];
            let mut chain = 0;
            while candidate != NONE && chain < MAX_CHAIN {
                let start = candidate as usize;
                let distance = position - start;
                if distance == 0 || distance > WINDOW {
                    break;
                }
                let length = input[start..]
                    .iter()
                    .zip(&input[position..position + limit])
                    .take_while(|(left, right)| left == right)
                    .count();
                if length > best.0 {
                    best = (length, distance);
                    if length == limit {
                        break;
                    }
                }
                let next = previous[start % WINDOW];
                // A slot reused by a newer position ends this chain.
                if next != NONE && next as usize >= start {
                    break;
                }
                candidate = next;
                chain += 1;
            }
        }
        if best.0 >= MIN_MATCH {
            matched(&mut bits, best.0, best.1);
            for at in position..position + best.0 {
                insert(at, &mut head, &mut previous);
            }
            position += best.0;
        } else {
            literal(&mut bits, u32::from(input[position]));
            insert(position, &mut head, &mut previous);
            position += 1;
        }
    }
    literal(&mut bits, 256);
    Ok(bits.finish())
}

/// The test reader: the stored and fixed-Huffman blocks this writer emits.
#[cfg(test)]
pub(crate) mod read {
    use std::collections::BTreeMap;

    struct Bits<'a> {
        bytes: &'a [u8],
        position: usize,
    }

    impl Bits<'_> {
        fn bit(&mut self) -> u32 {
            let byte = self.bytes[self.position / 8];
            let bit = (byte >> (self.position % 8)) & 1;
            self.position += 1;
            u32::from(bit)
        }

        fn take(&mut self, count: u32) -> u32 {
            (0..count).fold(0, |value, index| value | self.bit() << index)
        }

        fn code(&mut self, count: u32) -> u32 {
            (0..count).fold(0, |value, _| value << 1 | self.bit())
        }

        fn literal(&mut self) -> u32 {
            let mut code = self.code(7);
            if code <= 0x17 {
                return code + 256;
            }
            code = code << 1 | self.bit();
            match code {
                0x30..=0xBF => code - 0x30,
                0xC0..=0xC7 => code - 0xC0 + 280,
                _ => (code << 1 | self.bit()) - 0x190 + 144,
            }
        }
    }

    pub(crate) fn inflate(bytes: &[u8]) -> Vec<u8> {
        let mut bits = Bits { bytes, position: 0 };
        assert_eq!(bits.take(1), 1, "one final block");
        assert_eq!(bits.take(2), 1, "fixed Huffman codes");
        let mut output = Vec::new();
        loop {
            let symbol = bits.literal();
            match symbol {
                0..=255 => output.push(symbol as u8),
                256 => return output,
                _ => {
                    let code = (symbol - 257) as usize;
                    let length = usize::from(super::LENGTH_BASE[code])
                        + bits.take(u32::from(super::LENGTH_EXTRA[code])) as usize;
                    let code = bits.code(5) as usize;
                    let distance = usize::from(super::DISTANCE_BASE[code])
                        + bits.take(u32::from(super::DISTANCE_EXTRA[code])) as usize;
                    for _ in 0..length {
                        output.push(output[output.len() - distance]);
                    }
                }
            }
        }
    }

    fn u16_at(bytes: &[u8], at: usize) -> usize {
        usize::from(u16::from_le_bytes([bytes[at], bytes[at + 1]]))
    }

    fn u32_at(bytes: &[u8], at: usize) -> usize {
        u32::from_le_bytes(bytes[at..at + 4].try_into().unwrap()) as usize
    }

    /// Entry names and contents in central-directory order, each checked
    /// against its CRC and both headers.
    pub(crate) fn entries(zip: &[u8]) -> Vec<(String, Vec<u8>)> {
        let end = zip.len() - 22;
        assert_eq!(u32_at(zip, end), 0x0605_4b50);
        let count = u16_at(zip, end + 10);
        let mut at = u32_at(zip, end + 16);
        let mut seen = BTreeMap::new();
        let mut result = Vec::new();
        for _ in 0..count {
            assert_eq!(u32_at(zip, at), 0x0201_4b50);
            let method = u16_at(zip, at + 10);
            let crc = u32_at(zip, at + 16) as u32;
            let compressed = u32_at(zip, at + 20);
            let size = u32_at(zip, at + 24);
            let name_length = u16_at(zip, at + 28);
            let offset = u32_at(zip, at + 42);
            let name = String::from_utf8(zip[at + 46..at + 46 + name_length].to_vec()).unwrap();
            assert_eq!(zip[offset..offset + 4], [0x50, 0x4b, 0x03, 0x04]);
            assert_eq!(zip[offset + 8..offset + 28], zip[at + 10..at + 30]);
            let data = &zip[offset + 30 + name_length..offset + 30 + name_length + compressed];
            let content = match method {
                0 => data.to_vec(),
                8 => inflate(data),
                other => panic!("method {other}"),
            };
            assert_eq!(content.len(), size);
            assert_eq!(super::crc32(&content), crc);
            assert!(seen.insert(name.clone(), ()).is_none());
            result.push((name, content));
            at += 46 + name_length;
        }
        result
    }
}
