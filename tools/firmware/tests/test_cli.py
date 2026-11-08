"""CLI exercises use only synthetic inputs and in-process calls."""

from contextlib import redirect_stderr, redirect_stdout
import errno
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from blade_firmware.cli import _publish_new, atomic_new_file, main
from blade_firmware.core import inspect_bytes
from blade_firmware import FirmwareError


class CliTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / "synthetic.bin"
        self.source.write_bytes(b"synthetic, not vendor firmware")

    def run_cli(self, *arguments):
        output, error = io.StringIO(), io.StringIO()
        with redirect_stdout(output), redirect_stderr(error):
            status = main([str(argument) for argument in arguments])
        return status, output.getvalue(), error.getvalue()

    def test_profiles_lists_built_in_metadata(self):
        status, output, error = self.run_cli("profiles")
        self.assertEqual(status, 0, error)
        profiles = json.loads(output)["profiles"]
        self.assertEqual({profile["id"] for profile in profiles}, {"so6120-ec-1.04", "so6120-mcu-1.07"})

    def test_inspect_outputs_deterministic_report_and_html(self):
        source_before = self.source.read_bytes()
        first, second = self.root / "first", self.root / "second"
        for directory in (first, second):
            status, _, error = self.run_cli("inspect", self.source, "--output", directory)
            self.assertEqual(status, 0, error)
            self.assertEqual({path.name for path in directory.iterdir()}, {"report.json", "index.html"})
            self.assertIn("No live readings", (directory / "index.html").read_text(encoding="utf-8"))
        self.assertEqual((first / "report.json").read_bytes(), (second / "report.json").read_bytes())
        self.assertEqual((first / "index.html").read_bytes(), (second / "index.html").read_bytes())
        self.assertEqual(source_before, self.source.read_bytes())

    def test_inspect_existing_directory_and_source_overwrite_refused(self):
        for destination in (self.root, self.source):
            status, _, error = self.run_cli("inspect", self.source, "--output", destination)
            self.assertEqual(status, 2)
            self.assertIn("Output", error)
        self.assertEqual(self.source.read_bytes(), b"synthetic, not vendor firmware")

    def test_hex_saves_decoded_image(self):
        source = self.root / "synthetic.hex"
        source.write_bytes(b":0300000041424337\r\n:00000001FF\r\n")
        output = self.root / "hex"
        status, stdout, error = self.run_cli("inspect", source, "--output", output)
        self.assertEqual(status, 0, error)
        self.assertIn(str(output / "image.bin"), stdout)
        self.assertEqual((output / "image.bin").read_bytes(), b"ABC")
        report = json.loads((output / "report.json").read_text(encoding="utf-8"))
        self.assertEqual(report["registers"], [])

    def test_invalid_hex_extension_is_not_treated_as_raw(self):
        source = self.root / "bad.hex"
        source.write_bytes(b"garbage")
        output = self.root / "bad"
        status, _, _ = self.run_cli("inspect", source, "--output", output)
        self.assertEqual(status, 2)
        self.assertFalse(output.exists())

    def test_explicit_mismatch_leaves_no_output(self):
        output = self.root / "mismatch"
        status, _, error = self.run_cli("inspect", self.source, "--profile", "so6120-ec-1.04", "--output", output)
        self.assertEqual(status, 2)
        self.assertIn("mismatch", error)
        self.assertFalse(output.exists())

    def write_report(self, name="report.json"):
        path = self.root / name
        path.write_text(json.dumps(inspect_bytes(self.source.read_bytes(), self.source.name, []).report), encoding="utf-8")
        return path

    def test_render_and_diff_outputs_and_collisions(self):
        report = self.write_report()
        rendered, diff = self.root / "render.html", self.root / "diff.json"
        status, _, error = self.run_cli("render", report, "--output", rendered)
        self.assertEqual(status, 0, error)
        status, _, error = self.run_cli("diff", report, report, "--output", diff)
        self.assertEqual(status, 0, error)
        self.assertEqual(json.loads(diff.read_text())["changes"], {"registers": [], "thermal_tables": []})
        for command in (("render", report, "--output", rendered), ("render", report, "--output", report),
                        ("diff", report, report, "--output", diff), ("diff", report, report, "--output", report)):
            status, _, _ = self.run_cli(*command)
            self.assertEqual(status, 2)

    def test_malformed_reports_never_publish(self):
        report = self.write_report()
        for content in ('{}', '{"schema_version":1,"schema_version":1}', '[1]', 'null'):
            report.write_text(content, encoding="utf-8")
            for command in (("render", report), ("diff", report, report)):
                output = self.root / "bad-output"
                status, _, _ = self.run_cli(*command, "--output", output)
                self.assertEqual(status, 2)
                self.assertFalse(output.exists())

    def test_atomic_file_never_overwrites_even_on_collision_race(self):
        output = self.root / "file.bin"
        atomic_new_file(output, b"first")
        with self.assertRaises(ValueError):
            atomic_new_file(output, b"second")
        self.assertEqual(output.read_bytes(), b"first")
        raced = self.root / "race.bin"
        def race(temporary, destination):
            Path(destination).write_bytes(b"racing writer")
            _publish_new(temporary, destination)
        with patch("blade_firmware.cli._publish_new", side_effect=race), self.assertRaises(FileExistsError):
            atomic_new_file(raced, b"must not overwrite")
        self.assertEqual(raced.read_bytes(), b"racing writer")
        self.assertFalse(list(self.root.glob(".firmware-*")))

    def test_posix_filesystem_without_links_fails_closed_with_explicit_error(self):
        temporary = self.root / "complete-staging-file"
        temporary.write_bytes(b"complete output")
        destination = self.root / "unsupported-output"
        with patch("blade_firmware.cli.os.name", "posix"), patch("blade_firmware.cli.os.link", side_effect=OSError(errno.EOPNOTSUPP, "not supported")):
            with self.assertRaisesRegex(FirmwareError, "filesystem must allow hard links"):
                _publish_new(str(temporary), str(destination))
        self.assertFalse(destination.exists())
        self.assertEqual(temporary.read_bytes(), b"complete output")

    def test_output_rollback_on_publication_failure(self):
        directory = self.root / "rolled-back"
        original = atomic_new_file
        def fail_second(path, data):
            if Path(path).name == "index.html":
                raise OSError("synthetic output failure")
            original(path, data)
        with patch("blade_firmware.cli.atomic_new_file", side_effect=fail_second):
            status, _, error = self.run_cli("inspect", self.source, "--output", directory)
        self.assertEqual(status, 2)
        self.assertIn("synthetic output failure", error)
        self.assertFalse(directory.exists())


if __name__ == "__main__":
    unittest.main()
