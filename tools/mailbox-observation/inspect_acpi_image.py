#!/usr/bin/env python3
"""Bounded, standard-library-only inspection of a pinned ACPI.sys *file*.

Example (no elevation, imports of native libraries, or execution of the image):
  python tools/mailbox-observation/inspect_acpi_image.py --input COPY/ACPI.sys \
    --metadata artifacts/mailbox-runtime-20261105/acpi/kernel-acpi-provider.txt \
    --output NEW-existing-parent/acpi-image.json

Metadata accepts the complete saved English `wevtutil gp /ge:true /gm:true`
text, or JSON with exactly these required descriptor keys (extra keys ignored):
  {"provider_guid":"c514638f-7723-485b-bcfc-96565d735d4a", "event_id":7,
   "version":0,"channel":16,"level":4,"opcode":0,"task":105,
   "keywords":"0x8000200000000001"}
The earlier acpi-event-templates.json omits channel/opcode and is rejected.
All descriptor fields must match the reviewed metadata. No missing defaults,
keyword-mask stripping, instruction decoder, or automatic emitter proof.

JSON schema_version=1 reports image identity, validated PE sections and mapping,
normal imports, x64 exception-directory function intervals, descriptor byte
candidates, limitations and an unresolved manual emitter-audit checklist.
Addresses are numeric: file_offset, rva, and preferred_va (not runtime VA).
Descriptor byte matches are NOT instruction references or invocation evidence.
No signature validation, live memory, loaded module, AML or namespace query.

Output is exclusively published after a complete same-directory write+fsync:
Windows rename (which refuses existing targets), or a hard link on other OSes.
Existing destinations are never replaced. Unsupported publication fails closed.
Parent directory must already exist. No input/system modification.

Format references (inspection does not access the network):
https://learn.microsoft.com/en-us/windows/win32/debug/pe-format
https://learn.microsoft.com/en-us/windows/win32/api/evntprov/ns-evntprov-event_descriptor
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import struct
import sys
import tempfile

PINNED_SHA256 = "5de94c7819c690178fef3d656e4f743b2c46bfd58d6fd981e8bdfe7efa3d3022"
MAX_IMAGE_BYTES = 16 * 1024 * 1024
MAX_METADATA_BYTES = 256 * 1024
MAX_MATCHES = 256
PROVIDER_GUID = "c514638f-7723-485b-bcfc-96565d735d4a"
EXPECTED = dict(event_id=7, version=0, channel=16, level=4, opcode=0,
                task=105, keywords=0x8000200000000001)


class InspectionError(ValueError):
    """Malformed, unrecognized or out-of-scope data; no report published."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise InspectionError(message)


def require_local_path(path: Path) -> None:
    # Refuse explicit UNC/network and device namespaces; no discovery queries.
    spelling = str(path).replace("/", "\\")
    require(not spelling.startswith("\\\\"), "UNC/device/extended namespace paths are not supported")


def read_regular(path: Path, limit: int) -> bytes:
    require_local_path(path)
    require(stat.S_ISREG(path.stat().st_mode), "Input must be a regular file")
    with path.open("rb") as stream:
        info = os.fstat(stream.fileno())
        require(stat.S_ISREG(info.st_mode) and info.st_size <= limit,
                "Input is not a bounded regular file")
        data = stream.read(limit + 1)
    require(len(data) <= limit, "Input exceeds size limit")
    return data


def metadata_descriptor(data: bytes) -> dict:
    require(len(data) <= MAX_METADATA_BYTES, "Metadata exceeds size limit")
    try:
        text = data.decode("utf-16" if data.startswith((b"\xff\xfe", b"\xfe\xff"))
                           else "utf-8-sig")
        if text.lstrip().startswith("{"):
            values = json.loads(text)
            require(isinstance(values, dict), "Metadata must be an object")
            require("provider_guid" in values, "Metadata missing provider_guid")
        else:
            guid = re.search(r"^guid:\s*([^\r\n]+)", text, re.M)
            require(guid is not None, "Metadata missing provider GUID")
            candidates = []
            for block in re.split(r"^  event:\s*$", text, flags=re.M)[1:]:
                fields = dict(re.findall(r"^    (value|version|channel|level|opcode|task|keywords):\s*(\S+)", block, re.M))
                if fields.get("value") == "7":
                    fields["event_id"] = fields.pop("value")
                    candidates.append(fields)
            require(len(candidates) == 1, "Expected exactly one Event7 metadata record")
            values = dict(candidates[0], provider_guid=guid.group(1))
        require(str(values["provider_guid"]).strip().strip("{}").lower() == PROVIDER_GUID,
                "Unexpected metadata provider")
        result = {}
        for field, expected in EXPECTED.items():
            require(field in values, f"Metadata missing {field}; no defaults allowed")
            value = values[field]
            require(type(value) in (int, str), f"Invalid metadata {field}")
            number = int(value, 0) if isinstance(value, str) else value
            require(number == expected, f"Metadata {field} differs from reviewed Event7")
            result[field] = number
        return dict(result, provider_guid=PROVIDER_GUID)
    except (UnicodeError, json.JSONDecodeError, TypeError, KeyError) as error:
        raise InspectionError(f"Invalid descriptor metadata: {error}") from error


class PEImage:
    """Strict file-backed x64 PE32+ mappings, without loading the image."""

    def __init__(self, data: bytes):
        self.data = data
        require(len(data) <= MAX_IMAGE_BYTES, "Image exceeds size limit")
        require(self.span(0, 2) == b"MZ", "Missing DOS signature")
        nt = self.unpack("<I", 0x3C)[0]
        require(nt >= 64 and self.span(nt, 4) == b"PE\0\0", "Invalid PE signature/offset")
        machine, count, _, _, _, optional_size, _ = self.unpack("<HHIIIHH", nt + 4)
        require(machine == 0x8664, "Expected x64 machine")
        require(1 <= count <= 96, "Invalid section count")
        opt = nt + 24
        self.span(opt, optional_size)
        require(optional_size >= 112 and self.unpack("<H", opt)[0] == 0x20B,
                "Expected complete PE32+ optional header")
        self.image_base = self.unpack("<Q", opt + 24)[0]
        self.entry_rva = self.unpack("<I", opt + 16)[0]
        self.image_size, self.headers_size = self.unpack("<II", opt + 56)
        file_alignment = self.unpack("<I", opt + 36)[0]
        section_alignment = self.unpack("<I", opt + 32)[0]
        require(512 <= file_alignment <= 65536 and file_alignment & (file_alignment - 1) == 0,
                "Unsupported file alignment")
        require(section_alignment >= file_alignment and section_alignment & (section_alignment - 1) == 0,
                "Unsupported section alignment")
        directory_count = self.unpack("<I", opt + 108)[0]
        require(directory_count <= 16 and 112 + 8 * directory_count <= optional_size,
                "Data directories exceed optional header")
        table = opt + optional_size
        self.span(table, count * 40)
        require(table + count * 40 <= self.headers_size <= len(data), "Invalid header extent")
        require(self.headers_size <= self.image_size <= 0xFFFFFFFF, "Invalid image size")
        require(self.headers_size % file_alignment == 0 and self.image_size % section_alignment == 0,
                "Misaligned header/image size")
        require(self.image_base + self.image_size <= 0x10000000000000000,
                "Preferred VA range overflows")
        self.directories = [self.unpack("<II", opt + 112 + i * 8) for i in range(directory_count)]
        self.sections = []
        for i in range(count):
            at = table + i * 40
            name = self.span(at, 8).rstrip(b"\0").decode("ascii", "replace")
            virtual_size, rva, raw_size, raw_offset = self.unpack("<IIII", at + 8)
            characteristics = self.unpack("<I", at + 36)[0]
            extent = max(virtual_size, raw_size)
            require(rva >= self.headers_size and rva + extent <= self.image_size,
                    "Section virtual extent is outside image")
            require(rva % section_alignment == 0, "Misaligned section RVA")
            if raw_size:
                require(raw_offset >= self.headers_size and raw_offset % file_alignment == 0
                        and raw_size % file_alignment == 0,
                        "Invalid section file offset")
                self.span(raw_offset, raw_size)
            self.sections.append(dict(name=name, rva=rva, virtual_size=virtual_size,
                                      raw_size=raw_size, file_offset=raw_offset,
                                      characteristics=characteristics))
        for i, left in enumerate(self.sections):
            for right in self.sections[i + 1:]:
                for start, length in (("rva", None), ("file_offset", "raw_size")):
                    lsize = left[length] if length else max(left["raw_size"], left["virtual_size"])
                    rsize = right[length] if length else max(right["raw_size"], right["virtual_size"])
                    require(not (lsize and rsize and left[start] < right[start] + rsize
                                 and right[start] < left[start] + lsize), "Overlapping PE sections")
        if self.entry_rva:
            self.rva_offset(self.entry_rva)
            require(any(s["rva"] <= self.entry_rva < s["rva"] + s["raw_size"]
                        and s["characteristics"] & 0x20000000 for s in self.sections),
                    "Entry point is not file-backed executable code")

    def span(self, offset: int, size: int) -> bytes:
        require(offset >= 0 and size >= 0 and offset + size <= len(self.data),
                "Truncated/out-of-bounds file extent")
        return self.data[offset:offset + size]

    def unpack(self, fmt: str, offset: int) -> tuple:
        return struct.unpack(fmt, self.span(offset, struct.calcsize(fmt)))

    def rva_offset(self, rva: int, size: int = 1) -> int:
        require(rva >= 0 and size > 0 and rva + size <= self.image_size, "RVA outside image")
        if rva + size <= self.headers_size:
            return rva
        for s in self.sections:
            delta = rva - s["rva"]
            if 0 <= delta and delta + size <= s["raw_size"]:
                return s["file_offset"] + delta
        raise InspectionError("RVA is not backed by one file section (gap/zero-fill/cross-boundary)")

    def location(self, offset: int, size: int = 1) -> dict:
        self.span(offset, size)
        if offset + size <= self.headers_size:
            return dict(file_offset=offset, rva=offset, preferred_va=self.image_base + offset, section="headers")
        for s in self.sections:
            delta = offset - s["file_offset"]
            if s["raw_size"] and 0 <= delta and delta + size <= s["raw_size"]:
                rva = s["rva"] + delta
                return dict(file_offset=offset, rva=rva, preferred_va=self.image_base + rva, section=s["name"])
        return dict(file_offset=offset, rva=None, preferred_va=None, section=None)

    def directory(self, index: int) -> tuple[int, int]:
        rva, size = self.directories[index] if index < len(self.directories) else (0, 0)
        require(bool(rva) == bool(size), "Incomplete data directory")
        if size:
            self.rva_offset(rva, size)
        return rva, size

    def cstring(self, rva: int) -> str:
        data = bytearray()
        for index in range(512):
            byte = self.data[self.rva_offset(rva + index)]
            if not byte:
                try:
                    return data.decode("ascii")
                except UnicodeError as error:
                    raise InspectionError("Non-ASCII import name") from error
            data.append(byte)
        raise InspectionError("Unterminated/oversize import name")

    def imports(self) -> list[dict]:
        rva, size = self.directory(1)
        if not size:
            return []
        require(size <= 1024 * 1024, "Import directory exceeds limit")
        result = []
        total = 0
        for pos in range(0, size - 19, 20):
            values = self.unpack("<IIIII", self.rva_offset(rva + pos, 20))
            if not any(values):
                return result
            lookup, _, _, name, iat = values
            require(len(result) < 256 and name != 0 and iat != 0,
                    "Invalid/oversize import descriptor")
            dll = self.cstring(name)
            entries = []
            for n in range(4096):
                thunk_rva = (lookup or iat) + n * 8
                self.rva_offset(iat + n * 8, 8)
                thunk = self.unpack("<Q", self.rva_offset(thunk_rva, 8))[0]
                if not thunk:
                    break
                total += 1
                require(total <= 16384, "Import count exceeds limit")
                if thunk & (1 << 63):
                    require(thunk & 0x7FFFFFFFFFFF0000 == 0, "Invalid ordinal import")
                    entry = dict(ordinal=thunk & 0xFFFF)
                else:
                    require(thunk <= 0xFFFFFFFF, "Invalid import name RVA")
                    self.rva_offset(thunk, 2)
                    entry = dict(name=self.cstring(thunk + 2))
                entries.append(dict(entry, lookup_rva=thunk_rva, iat_rva=iat + n * 8))
            else:
                raise InspectionError("Unterminated import lookup table")
            result.append(dict(dll=dll, entries=entries))
        raise InspectionError("Unterminated import descriptor table")

    def functions(self) -> list[dict]:
        rva, size = self.directory(3)
        require(size % 12 == 0 and size // 12 <= 65536, "Invalid x64 exception directory size")
        result = []
        last_begin = -1
        for pos in range(0, size, 12):
            begin, end, unwind = self.unpack("<III", self.rva_offset(rva + pos, 12))
            require(last_begin <= begin < end and unwind != 0, "Invalid runtime function bounds/order")
            at = self.rva_offset(begin, end - begin)
            section = next((s for s in self.sections if s["rva"] <= begin < s["rva"] + s["raw_size"]), None)
            require(section is not None and section["characteristics"] & 0x20000000,
                    "Runtime function is not in executable section")
            self.rva_offset(unwind & ~3)
            result.append(dict(begin_rva=begin, end_rva_exclusive=end, file_offset=at,
                               preferred_va=self.image_base + begin, unwind_rva=unwind,
                               table_entry_rva=rva + pos))
            last_begin = begin
        return result


def build_report(image: bytes, metadata: bytes) -> dict:
    digest = hashlib.sha256(image).hexdigest()
    require(digest == PINNED_SHA256, "Unrecognized image SHA-256; pinned ACPI.sys required")
    descriptor = metadata_descriptor(metadata)
    pe = PEImage(image)
    pattern = struct.pack("<HBBBBHQ", *(descriptor[k] for k in
                          ("event_id", "version", "channel", "level", "opcode", "task", "keywords")))
    candidates = []
    start = 0
    while True:
        at = image.find(pattern, start)
        if at < 0:
            break
        require(len(candidates) < MAX_MATCHES, "Descriptor candidate count exceeds limit")
        candidates.append(dict(pe.location(at, len(pattern)), evidence="byte-pattern candidate only"))
        start = at + 1
    imports = pe.imports()
    functions = pe.functions()
    return dict(
        schema_version=1, kind="offline_acpi_image_inspection",
        image=dict(sha256=digest, size_bytes=len(image), identity="matches reviewed SHA-256",
                   signature_validation="not performed", runtime_admission=False),
        pe=dict(machine="x64", format="PE32+", preferred_image_base=pe.image_base,
                image_size=pe.image_size, headers_size=pe.headers_size, entry_rva=pe.entry_rva,
                sections=pe.sections),
        metadata=dict(sha256=hashlib.sha256(metadata).hexdigest(), descriptor=descriptor),
        descriptor_pattern_hex=pattern.hex(), descriptor_candidates=candidates,
        imports=imports,
        trace_related_import_name_candidates=[dict(dll=d["dll"], **e) for d in imports for e in d["entries"]
                                               if re.search(r"^(Etw|Wpp)|TraceLogging|TraceMessage|EventWrite|EventRegister",
                                                            e.get("name", ""), re.I)],
        runtime_function_intervals=functions,
        coverage=dict(external_evaluation="unresolved", nested_evaluation="unresolved",
                      emitter_identified=False, instruction_references_verified=False),
        limitations=["File data only; never loaded or executed; no runtime or firmware requests.",
                     "No match does not prove an emitter is absent: compiled descriptor keywords may differ from exported channel masks.",
                     "A byte match does not prove a live EVENT_DESCRIPTOR or a code reference.",
                     "Import names show dependencies, not calls or tracing coverage; delayed imports are not decoded.",
                     "Runtime function entries are unwind intervals, not a complete function/call graph; unwind programs are not decoded.",
                     "No instruction decoder, RIP-relative reference inference, signature trust, or live namespace evidence."],
        emitter_audit_checklist=[
            "On this exact SHA-256, verify descriptor identity and any keyword-mask differences against supplied disassembly.",
            "Verify instruction references to candidates; map verified instruction RVAs through sections/runtime intervals.",
            "Trace the emitter's callers and gates: provider enable, method-name filters, timing/thresholds, error/early exits.",
            "Determine external ordinary/EX evaluation and EC-query entry coverage, separately from nested interpreter calls.",
            "Verify start/finish semantics, name construction, async continuation and elapsed units; do not pair solely by PID/TID.",
            "Record reviewed instruction ranges and counterexamples; absence of a pattern or import never resolves coverage."])


def publish_exclusive(source: str, target: Path) -> None:
    if os.name == "nt":
        os.rename(source, target)  # Windows rename refuses an existing destination.
    else:
        os.link(source, target)  # POSIX rename would overwrite; use exclusive link.


def publish_new(path: Path, report: dict) -> None:
    require_local_path(path)
    encoded = (json.dumps(report, indent=2, ensure_ascii=True) + "\n").encode("utf-8")
    # Fail before allocating staging if the destination already exists, including dangling symlinks.
    require(not os.path.lexists(path), "Output already exists; refusing overwrite")
    fd, temporary = tempfile.mkstemp(prefix=".acpi-inspection-", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        publish_exclusive(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--input", required=True, type=Path, help="Explicit regular-file copy of pinned ACPI.sys")
    parser.add_argument("--metadata", required=True, type=Path, help="Complete saved provider text or descriptor JSON")
    parser.add_argument("--output", required=True, type=Path, help="New JSON path in an existing directory")
    args = parser.parse_args(argv)
    try:
        require_local_path(args.output)
        require(not os.path.lexists(args.output), "Output already exists; refusing overwrite")
        report = build_report(read_regular(args.input, MAX_IMAGE_BYTES),
                              read_regular(args.metadata, MAX_METADATA_BYTES))
        report["input_paths"] = dict(image=str(args.input.absolute()), metadata=str(args.metadata.absolute()))
        publish_new(args.output, report)
    except (InspectionError, OSError, ValueError) as error:
        print(f"Inspection refused: {error}", file=sys.stderr)
        return 2
    print(f"Wrote {args.output}: {len(report['descriptor_candidates'])} byte candidates; coverage unresolved.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
