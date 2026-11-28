//! The two decoders the reviewed oracle fixtures need, with no new
//! dependency: RFC 4648 base64 (managed `Convert.FromBase64String`) and RFC
//! 1951 raw deflate (managed `DeflateStream`), each refusing anything it does
//! not decode exactly.

/// The standard base64 alphabet with `=` padding. Any other byte, a
/// misplaced pad or a length that is not a multiple of four refuses.
pub(super) fn base64(text: &str) -> Option<Vec<u8>> {
    let bytes = text.as_bytes();
    if !bytes.len().is_multiple_of(4) {
        return None;
    }
    let value = |byte: u8| -> Option<u32> {
        Some(match byte {
            b'A'..=b'Z' => byte - b'A',
            b'a'..=b'z' => byte - b'a' + 26,
            b'0'..=b'9' => byte - b'0' + 52,
            b'+' => 62,
            b'/' => 63,
            _ => return None,
        } as u32)
    };
    let mut out = Vec::with_capacity(bytes.len() / 4 * 3);
    for (index, chunk) in bytes.chunks(4).enumerate() {
        let last = index + 1 == bytes.len() / 4;
        let pads = chunk.iter().rev().take_while(|&&byte| byte == b'=').count();
        if pads > 2 || (pads > 0 && !last) {
            return None;
        }
        let mut word = 0u32;
        for &byte in &chunk[..4 - pads] {
            word = (word << 6) | value(byte)?;
        }
        word <<= 6 * pads as u32;
        let decoded = [(word >> 16) as u8, (word >> 8) as u8, word as u8];
        out.extend_from_slice(&decoded[..3 - pads]);
    }
    Some(out)
}

const MAX_BITS: usize = 15;
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
const CODE_LENGTH_ORDER: [usize; 19] = [
    16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15,
];

struct Bits<'a> {
    data: &'a [u8],
    position: usize,
    buffer: u32,
    count: u32,
}

impl Bits<'_> {
    fn take(&mut self, bits: u32) -> Option<u32> {
        while self.count < bits {
            let byte = *self.data.get(self.position)?;
            self.position += 1;
            self.buffer |= u32::from(byte) << self.count;
            self.count += 8;
        }
        let value = self.buffer & ((1u32 << bits) - 1);
        self.buffer = self.buffer.checked_shr(bits).unwrap_or(0);
        self.count -= bits;
        Some(value)
    }
}

/// A canonical Huffman code: how many codes of each length, and the
/// symbols in code order.
struct Huffman {
    count: [u16; MAX_BITS + 1],
    symbol: Vec<u16>,
}

impl Huffman {
    /// An over-subscribed code refuses; an incomplete one is kept, as zlib
    /// keeps a single distance code.
    fn new(lengths: &[u8]) -> Option<Self> {
        let mut count = [0u16; MAX_BITS + 1];
        for &length in lengths {
            count[usize::from(length)] += 1;
        }
        let mut left = 1i32;
        for &codes in &count[1..] {
            left = (left << 1) - i32::from(codes);
            if left < 0 {
                return None;
            }
        }
        let mut offsets = [0u16; MAX_BITS + 1];
        for length in 1..MAX_BITS {
            offsets[length + 1] = offsets[length] + count[length];
        }
        let mut symbol = vec![0u16; lengths.len()];
        for (value, &length) in lengths.iter().enumerate() {
            if length != 0 {
                let slot = &mut offsets[usize::from(length)];
                symbol[usize::from(*slot)] = value as u16;
                *slot += 1;
            }
        }
        count[0] = 0;
        Some(Self { count, symbol })
    }

    fn decode(&self, bits: &mut Bits<'_>) -> Option<u16> {
        let (mut code, mut first, mut index) = (0i32, 0i32, 0i32);
        for &count in &self.count[1..] {
            code |= bits.take(1)? as i32;
            let count = i32::from(count);
            if code < first + count {
                return self.symbol.get((index + code - first) as usize).copied();
            }
            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }
        None
    }
}

/// One raw deflate stream that decodes to exactly `expected` bytes. Output
/// past `expected`, a stream that ends early, or any malformed block refuses.
pub(super) fn inflate(data: &[u8], expected: usize) -> Option<Vec<u8>> {
    let mut bits = Bits {
        data,
        position: 0,
        buffer: 0,
        count: 0,
    };
    let mut out = Vec::with_capacity(expected);
    loop {
        let last = bits.take(1)? == 1;
        match bits.take(2)? {
            0 => {
                bits.buffer = 0;
                bits.count = 0;
                let header = data.get(bits.position..bits.position + 4)?;
                let length = u16::from_le_bytes([header[0], header[1]]);
                if length != !u16::from_le_bytes([header[2], header[3]]) {
                    return None;
                }
                bits.position += 4;
                let block = data.get(bits.position..bits.position + usize::from(length))?;
                bits.position += usize::from(length);
                if out.len() + block.len() > expected {
                    return None;
                }
                out.extend_from_slice(block);
            }
            1 => {
                let mut lengths = [0u8; 288];
                lengths[..144].fill(8);
                lengths[144..256].fill(9);
                lengths[256..280].fill(7);
                lengths[280..].fill(8);
                let literal = Huffman::new(&lengths)?;
                let distance = Huffman::new(&[5u8; 30])?;
                codes(&mut bits, &literal, &distance, &mut out, expected)?;
            }
            2 => {
                let (literal, distance) = dynamic(&mut bits)?;
                codes(&mut bits, &literal, &distance, &mut out, expected)?;
            }
            _ => return None,
        }
        if last {
            break;
        }
    }
    (out.len() == expected).then_some(out)
}

fn dynamic(bits: &mut Bits<'_>) -> Option<(Huffman, Huffman)> {
    let literals = bits.take(5)? as usize + 257;
    let distances = bits.take(5)? as usize + 1;
    let header = bits.take(4)? as usize + 4;
    if literals > 286 || distances > 30 {
        return None;
    }
    let mut lengths = [0u8; 19];
    for &index in &CODE_LENGTH_ORDER[..header] {
        lengths[index] = bits.take(3)? as u8;
    }
    let lengths_code = Huffman::new(&lengths)?;
    let mut lengths = vec![0u8; literals + distances];
    let mut index = 0;
    while index < lengths.len() {
        let symbol = lengths_code.decode(bits)?;
        let (value, repeat) = match symbol {
            0..=15 => (symbol as u8, 1),
            16 => (
                *lengths.get(index.checked_sub(1)?)?,
                3 + bits.take(2)? as usize,
            ),
            17 => (0, 3 + bits.take(3)? as usize),
            18 => (0, 11 + bits.take(7)? as usize),
            _ => return None,
        };
        if index + repeat > lengths.len() {
            return None;
        }
        lengths[index..index + repeat].fill(value);
        index += repeat;
    }
    if lengths[256] == 0 {
        return None;
    }
    Some((
        Huffman::new(&lengths[..literals])?,
        Huffman::new(&lengths[literals..])?,
    ))
}

fn codes(
    bits: &mut Bits<'_>,
    literal: &Huffman,
    distance: &Huffman,
    out: &mut Vec<u8>,
    expected: usize,
) -> Option<()> {
    loop {
        let symbol = usize::from(literal.decode(bits)?);
        match symbol {
            0..=255 => {
                if out.len() == expected {
                    return None;
                }
                out.push(symbol as u8);
            }
            256 => return Some(()),
            257..=285 => {
                let index = symbol - 257;
                let length = usize::from(LENGTH_BASE[index])
                    + bits.take(u32::from(LENGTH_EXTRA[index]))? as usize;
                let code = usize::from(distance.decode(bits)?);
                if code >= DISTANCE_BASE.len() {
                    return None;
                }
                let back = usize::from(DISTANCE_BASE[code])
                    + bits.take(u32::from(DISTANCE_EXTRA[code]))? as usize;
                if back > out.len() || out.len() + length > expected {
                    return None;
                }
                let start = out.len() - back;
                for offset in 0..length {
                    out.push(out[start + offset]);
                }
            }
            _ => return None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn base64_decodes_padding_and_refuses_the_rest() {
        assert_eq!(base64("TWFu").as_deref(), Some(&b"Man"[..]));
        assert_eq!(base64("TWE=").as_deref(), Some(&b"Ma"[..]));
        assert_eq!(base64("TQ==").as_deref(), Some(&b"M"[..]));
        assert_eq!(base64("").as_deref(), Some(&b""[..]));
        for bad in ["TWF", "TW=u", "T===", "TQ==TWFu", "TW!u"] {
            assert_eq!(base64(bad), None, "{bad}");
        }
    }

    #[test]
    fn inflate_decodes_stored_and_fixed_blocks() {
        // A stored block of "abc". The oracle fixtures cover dynamic blocks.
        assert_eq!(
            inflate(&[0x01, 0x03, 0x00, 0xFC, 0xFF, b'a', b'b', b'c'], 3).as_deref(),
            Some(&b"abc"[..])
        );
        // zlib's raw fixed-Huffman "a" repeated ten times (a back-reference).
        let fixed = [0x4B, 0x4C, 0x84, 0x01, 0x00];
        assert_eq!(inflate(&fixed, 10).as_deref(), Some(&[b'a'; 10][..]));
        // The exact length is required.
        assert_eq!(inflate(&fixed, 9), None);
        assert_eq!(inflate(&fixed, 11), None);
        // A truncated stream refuses.
        assert_eq!(inflate(&fixed[..2], 10), None);
        // A reserved block type refuses.
        assert_eq!(inflate(&[0x07], 0), None);
    }
}
