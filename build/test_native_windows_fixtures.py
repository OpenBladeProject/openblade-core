"""Collector/scope negatives; no native/product command is executed here."""
import importlib.util
import json
from pathlib import Path
import tempfile
import sys
import unittest
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('fixtures', REPO / 'build/native_windows_fixtures.py')
f = importlib.util.module_from_spec(spec)
spec.loader.exec_module(f)


class FixtureContracts(unittest.TestCase):
    def line(self, emitter='peer_response', raw='{"frame":"response","version":80,"z":1,"a":2}'):
        return f.PREFIX + json.dumps(dict(schemaVersion=1, emitter=emitter, operation='getStatus',
                                           method='response', raw=raw)).encode() + b'\n'

    def test_preserves_exact_payload_and_ignores_trace_copy(self):
        data = self.line() + b'NATIVE_THERMAL_EDGE_TRACE getStatus {}\n'
        rows = f.collect(data, 'test', {'peer_response'})
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]['stdoutByteOffset'], 0)
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / 'corpus.json'
            f.write_corpus(path, rows, False)
            self.assertIn(b'"z":1,"a":2', path.read_bytes())
            edge = Path(d) / 'edge.json'
            f.write_corpus(edge, rows, True)
            self.assertEqual(json.loads(edge.read_bytes())[0]['raw'], rows[0]['raw'])

    def test_collector_rejects_scope_corruption_and_truncation(self):
        for data in (self.line('unknown'), self.line('publisher_projection'), self.line()[:-1],
                     b'prefix ' + self.line(), self.line(raw='{}'), self.line(raw=''),
                     self.line().replace(b'"schemaVersion": 1', b'"schemaVersion": 2')):
            with self.subTest(data=data), self.assertRaises((ValueError, KeyError)):
                f.collect(data, 'test', {'peer_response'})
        with tempfile.TemporaryDirectory() as d, self.assertRaises(ValueError):
            f.write_corpus(Path(d) / 'empty.json', [], False)

    def test_exact_command_recorder(self):
        manifest = f.load_manifest(REPO)
        self.assertEqual([len(c['cases']) for c in manifest['corpora']], [3, 2, 2, 10, 2])
        recorded = []
        for features in f.TUPLES:
            recorded.append(f.native_build_command(REPO, features))
        for corpus in manifest['corpora']:
            for case in corpus['cases']:
                recorded.append(f.native_test_command(Path('test.exe'), case, manifest))
        self.assertEqual(len(recorded), 21)
        for command in recorded[:2]:
            self.assertIn('--offline', command)
            self.assertIn('--locked', command)
            self.assertEqual(command[command.index('--bin') + 1], f.TARGET)
            self.assertNotIn('--all-targets', command)
        for command in recorded[2:]:
            self.assertEqual(command[2:], ['--exact', '--nocapture', '--test-threads=1', '--quiet'])
        for features in ('vm-scm-fixture', 'service-battery-policy-v1,service-temporary-charge-v1', ''):
            with self.assertRaises(ValueError):
                f.native_build_command(REPO, features)
        with self.assertRaises(ValueError):
            f.native_test_command(Path('product.exe'), 'unknown', manifest)

    def test_zero_ignored_failed_selected_native_results_fail(self):
        for text in ('', 'test result: ok. 0 passed; 0 failed; 0 ignored;',
                     'test result: ok. 0 passed; 0 failed; 1 ignored;', 'test result: FAILED. 1 passed; 1 failed; 0 ignored;'):
            with self.assertRaises(ValueError):
                f.check_native_result(text)
        f.check_native_result('test result: ok. 1 passed; 0 failed; 0 ignored; 10 filtered out;')

    def test_managed_zero_skipped_wrong_fact_fail(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / 'result.trx'
            for xml in ('<TestRun/>', '<TestRun><UnitTestResult outcome="NotExecuted" testName="A"/></TestRun>',
                        '<TestRun><UnitTestResult outcome="Passed" testName="B"/></TestRun>'):
                p.write_text(xml)
                with self.assertRaises(ValueError):
                    f.check_trx(p, 'A')

    def test_managed_fixture_semantic_drift_fails(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            pinned = root / 'tools/openblade-windows-host/tests/fixtures'
            pinned.mkdir(parents=True)
            generated = root / 'out/edge-producer/generated'
            generated.mkdir(parents=True)
            (pinned / 'fixture.frames').write_bytes(b'original')
            (generated / 'fixture.frames').write_bytes(b'original')
            inventory = dict(originalLegacyInputs=[], generated=[dict(name='fixture.frames', sha256=f.digest(pinned / 'fixture.frames'))])
            (pinned / 'native-thermal-edge-fixtures.json').write_text(json.dumps(inventory))
            f.compare_generated(root, root / 'out')
            (generated / 'fixture.frames').write_bytes(b'drift')
            with self.assertRaisesRegex(ValueError, 'semantic drift'):
                f.compare_generated(root, root / 'out')

    @unittest.skipUnless(sys.platform == 'win32', 'Windows process-tree timeout contract')
    def test_owned_command_timeout_fails_and_keeps_log(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            receipts = []
            with self.assertRaises(TimeoutError):
                f.checked_run([sys.executable, '-c', 'import time; time.sleep(30)'], root, root, 'timeout', receipts, timeout=.05)
            self.assertEqual(len(receipts), 1)
            self.assertTrue((root / 'timeout.log').is_file())

    def test_missing_stale_mutated_inputs_fail(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            receipt = dict(status='PREPARED', runId=root.name, root=d, repository=str(REPO),
                           source={'bound': 'source'}, inputs={})
            for name in f.ENV_NAMES:
                path = root / name
                path.write_text('[{"frame":"response"}]')
                receipt['inputs'][name] = dict(path=str(path), count=1, sha256=f.digest(path))
            with patch.object(f, 'source_manifest', return_value=receipt['source']):
                f.verify_inputs(receipt)
                receipt['runId'] = 'stale'
                with self.assertRaises(ValueError):
                    f.verify_inputs(receipt)
                receipt['runId'] = root.name
                path.write_text('[]')
                with self.assertRaises(ValueError):
                    f.verify_inputs(receipt)
                path.unlink()
                with self.assertRaises((ValueError, FileNotFoundError)):
                    f.verify_inputs(receipt)
            with self.assertRaises(ValueError):
                f.verify_inputs({})


if __name__ == '__main__':
    unittest.main(verbosity=2)
