"""Strict bounded parsers for firmware containers, never executable loaders."""

from dataclasses import dataclass
import io
import lzma
import re
import struct

from . import FirmwareError

MAX_INPUT_SIZE = 64 * 1024 * 1024
MAX_IMAGE_SIZE = 16 * 1024 * 1024
MAX_DICTIONARY_SIZE = 64 * 1024 * 1024
NSIS_SIGNATURE = b"\xef\xbe\xad\xdeNullsoftInst"


@dataclass(frozen=True)
class Image:
    data: bytes
    base_address: int
    populated_bytes: int


def decode_intel_hex(data: bytes) -> Image:
    """Decode checked records, rejecting even identical overlapping writes."""
    if not data or len(data) > MAX_INPUT_SIZE:
        raise FirmwareError("Intel HEX input is empty or exceeds the input limit")
    # First pass validates shape and finds bounds without a dictionary per byte
    # or a splitlines allocation proportional to the number of tiny records.
    upper = 0
    eof = False
    starts = set()
    low = None
    high = 0
    for line_number, address, kind, payload in _hex_records(data):
        if eof:
            raise FirmwareError(f"Intel HEX record after EOF at line {line_number}")
        count = len(payload)
        if kind == 0:
            if not count or address + count > 65536:
                raise FirmwareError("Empty or wrapping Intel HEX data record")
            first, end = upper + address, upper + address + count
            low = first if low is None else min(low, first)
            high = max(high, end)
            if high > 0x100000000 or high - low > MAX_IMAGE_SIZE:
                raise FirmwareError("Intel HEX address span exceeds the image limit")
        elif kind == 1:
            if count or address:
                raise FirmwareError("Malformed Intel HEX EOF record")
            eof = True
        elif kind in (2, 4):
            if count != 2 or address:
                raise FirmwareError("Malformed Intel HEX extended address record")
            upper = int.from_bytes(payload, "big") << (4 if kind == 2 else 16)
        elif kind in (3, 5):
            if count != 4 or address or kind in starts:
                raise FirmwareError("Malformed or duplicate Intel HEX start address record")
            starts.add(kind)
        else:
            raise FirmwareError(f"Unsupported Intel HEX record type {kind}")
    if not eof or low is None:
        raise FirmwareError("Intel HEX requires data records and a final EOF")
    image = bytearray(b"\xff" * (high - low))
    populated = bytearray(high - low)
    upper, total = 0, 0
    for _, address, kind, payload in _hex_records(data):
        if kind == 0:
            start = upper + address - low
            end = start + len(payload)
            if any(populated[start:end]):
                raise FirmwareError("Duplicate/overlapping Intel HEX address")
            image[start:end] = payload
            populated[start:end] = b"\x01" * len(payload)
            total += len(payload)
        elif kind in (2, 4):
            upper = int.from_bytes(payload, "big") << (4 if kind == 2 else 16)
    return Image(bytes(image), low, total)


def _hex_records(data):
    stream = io.BytesIO(data)
    number = 0
    while raw := stream.readline(524):
        number += 1
        line = raw[:-1] if raw.endswith(b"\n") else raw
        line = line[:-1] if line.endswith(b"\r") else line
        if not re.fullmatch(rb":[0-9a-fA-F]{10,520}", line) or len(line) % 2 != 1:
            raise FirmwareError(f"Malformed Intel HEX record at line {number}")
        record = bytes.fromhex(line[1:].decode("ascii"))
        if len(record) != record[0] + 5 or sum(record) & 255:
            raise FirmwareError(f"Intel HEX length/checksum failure at line {number}")
        yield number, int.from_bytes(record[1:3], "big"), record[3], record[4:-1]


def extract_nsis_block(data: bytes, block_index: int) -> bytes:
    """Parse only the reviewed block container; caller must first verify its hash."""
    if len(data) > MAX_INPUT_SIZE or isinstance(block_index, bool) or not isinstance(block_index, int) or not 0 <= block_index < 4096:
        raise FirmwareError("NSIS input or block index exceeds its limit")
    signature = data.find(NSIS_SIGNATURE)
    if signature < 4 or data.find(NSIS_SIGNATURE, signature + 1) != -1:
        raise FirmwareError("NSIS signature is missing or ambiguous")
    start = signature - 4
    if start + 28 > len(data):
        raise FirmwareError("Truncated NSIS header")
    flags, = struct.unpack_from("<I", data, start)
    header_size, total_size = struct.unpack_from("<II", data, start + 20)
    if flags & ~15 or not 0 < header_size <= MAX_IMAGE_SIZE or total_size < 36 or start + total_size > len(data):
        raise FirmwareError("Invalid NSIS header bounds or flags")
    position, end, selected, index = start + 28, start + total_size - 4, None, 0
    while position < end:
        if position + 4 > end or index >= 4096:
            raise FirmwareError("Truncated or excessive NSIS blocks")
        raw_size, = struct.unpack_from("<I", data, position)
        position += 4
        size = raw_size & 0x7fffffff
        if not size or position + size > end:
            raise FirmwareError("Invalid NSIS block bounds")
        if index == block_index:
            selected = data[position:position + size]
            if raw_size & 0x80000000:
                selected = _decompress_nsis(selected)
            elif size > MAX_IMAGE_SIZE:
                raise FirmwareError("NSIS payload exceeds the image limit")
        position += size
        index += 1
    if position != end or selected is None:
        raise FirmwareError("NSIS block is missing or archive is incomplete")
    return selected


def _decompress_nsis(data: bytes) -> bytes:
    if len(data) < 6 or data[0] >= 9 * 5 * 5:
        raise FirmwareError("Invalid NSIS LZMA properties")
    properties = data[0]
    lc, lp, pb = properties % 9, (properties // 9) % 5, properties // 45
    dictionary = int.from_bytes(data[1:5], "little")
    if lc + lp > 4 or not 4096 <= dictionary <= MAX_DICTIONARY_SIZE:
        raise FirmwareError("NSIS LZMA dictionary or properties exceed limits")
    try:
        decoder = lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[{
            "id": lzma.FILTER_LZMA1, "dict_size": dictionary, "lc": lc, "lp": lp, "pb": pb,
        }])
        result = decoder.decompress(data[5:], max_length=MAX_IMAGE_SIZE + 1)
        if len(result) > MAX_IMAGE_SIZE or not decoder.eof or decoder.unused_data:
            raise FirmwareError("NSIS LZMA payload is oversized, truncated or has trailing bytes")
        return result
    except lzma.LZMAError as error:
        raise FirmwareError("Invalid NSIS LZMA payload") from error
