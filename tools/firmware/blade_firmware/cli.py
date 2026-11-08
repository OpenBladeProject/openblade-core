"""Command-line entry point; input reads and newly created local outputs only."""

import argparse
import errno
import json
import os
from pathlib import Path
import sys
import tempfile

from . import FirmwareError
from .core import diff_reports, inspect_file, validate_report
from .profiles import PROFILE_FIELDS, load_profiles, read_json

MAX_REPORT_SIZE = 32 * 1024 * 1024


def json_text(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2, allow_nan=False) + "\n"


def atomic_new_file(path, data):
    """Publish a complete file atomically without replacing an existing target."""
    path = Path(path)
    if path.exists() or path.is_symlink():
        raise FirmwareError(f"Output already exists: {path}")
    descriptor, temporary = tempfile.mkstemp(prefix=".firmware-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        _publish_new(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def _publish_new(temporary, destination):
    if os.name == "nt":
        # Windows rename is atomic and fails if the destination exists. It also
        # works where hard links are denied (AppContainer, FAT and network disks).
        os.rename(temporary, destination)
    else:
        # POSIX rename replaces files, so use an exclusive link on that platform.
        try:
            os.link(temporary, destination)
        except OSError as error:
            if error.errno in (errno.EPERM, errno.EOPNOTSUPP, errno.ENOSYS, errno.EXDEV):
                raise FirmwareError("Cannot publish output atomically without replacement: the POSIX output filesystem must allow hard links") from error
            raise


def _new_output(path, sources=()):
    output = Path(path)
    if any(output.resolve() == Path(source).resolve() for source in sources):
        raise FirmwareError("Output would overwrite an input")
    if output.exists() or output.is_symlink():
        raise FirmwareError(f"Output already exists: {output}")
    return output


def _render(report):
    from .explorer import render_html
    return render_html(report).encode("utf-8")


def _write_inspection(output, result):
    # Render and serialize before creating anything, so validation failures leave
    # no output directory. Each published file is complete and never replaced.
    payloads = {"report.json": json_text(result.report).encode("utf-8"), "index.html": _render(result.report)}
    if result.extracted_image is not None:
        payloads["image.bin"] = result.extracted_image
    output.mkdir()
    created = []
    try:
        for name, data in payloads.items():
            destination = output / name
            atomic_new_file(destination, data)
            created.append(destination)
    except BaseException:
        for destination in created:
            destination.unlink()
        try:
            output.rmdir()
        except OSError:
            pass
        raise


def make_parser():
    parser = argparse.ArgumentParser(description="Offline firmware inspection. No live readings or hardware access.")
    subcommands = parser.add_subparsers(dest="command", required=True)
    profiles = subcommands.add_parser("profiles", help="List exact-image declarative profiles")
    profiles.add_argument("--profile-file", action="append", default=[], metavar="JSON")
    inspect = subcommands.add_parser("inspect", help="Inspect a raw binary, Intel HEX or reviewed carrier")
    inspect.add_argument("input", metavar="INPUT")
    inspect.add_argument("--output", required=True, metavar="NEW_DIRECTORY")
    inspect.add_argument("--profile", metavar="ID")
    inspect.add_argument("--profile-file", action="append", default=[], metavar="JSON")
    render = subcommands.add_parser("render", help="Render a schema-1 inspection report as self-contained HTML")
    render.add_argument("report", metavar="REPORT_JSON")
    render.add_argument("--output", required=True, metavar="FILE")
    diff = subcommands.add_parser("diff", help="Compare two schema-1 inspection reports")
    diff.add_argument("before", metavar="BEFORE_JSON")
    diff.add_argument("after", metavar="AFTER_JSON")
    diff.add_argument("--output", required=True, metavar="FILE")
    return parser


def main(argv=None):
    parser = make_parser()
    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "profiles":
            profiles = load_profiles(arguments.profile_file)
            print(json_text({"schema_version": 1, "profiles": [
                {**{key: profile[key] for key in PROFILE_FIELDS}, "image": profile["image"],
                 "carrier_count": len(profile["carriers"])} for profile in profiles]}), end="")
        elif arguments.command == "inspect":
            output = _new_output(arguments.output, [arguments.input, *arguments.profile_file])
            result = inspect_file(arguments.input, load_profiles(arguments.profile_file), arguments.profile)
            _write_inspection(output, result)
            names = ["report.json", "index.html"]
            if result.extracted_image is not None:
                names.append("image.bin")
            print("Created " + ", ".join(str(output / name) for name in names))
        elif arguments.command == "render":
            output = _new_output(arguments.output, [arguments.report])
            report = validate_report(read_json(arguments.report, MAX_REPORT_SIZE))
            atomic_new_file(output, _render(report))
            print(f"Created {output}")
        elif arguments.command == "diff":
            output = _new_output(arguments.output, [arguments.before, arguments.after])
            before, after = read_json(arguments.before, MAX_REPORT_SIZE), read_json(arguments.after, MAX_REPORT_SIZE)
            atomic_new_file(output, json_text(diff_reports(before, after)).encode("utf-8"))
            print(f"Created {output}")
        return 0
    except (FirmwareError, OSError, ValueError, OverflowError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
