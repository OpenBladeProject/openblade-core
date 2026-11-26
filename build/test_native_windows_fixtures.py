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
    def line(self, emitter='peer_response', raw='{"frame":"response","version":91,"z":1,"a":2}'):
        return f.PREFIX + json.dumps(dict(schemaVersion=1, emitter=emitter, operation='getStatus',
                                           method='response', raw=raw)).encode() + b'\n'

    def test_python_floor_precedes_fixture_path_and_prepare_work(self):
        for version in ((3, 10, 99), (3, 9, 0)):
            with self.subTest(version=version), patch.object(sys, 'version_info', version), \
                    patch.object(f, 'source_manifest') as sources, patch.object(f, 'prepare') as prepare, \
                    self.assertRaisesRegex(RuntimeError, 'Python 3.11'):
                f.main()
            sources.assert_not_called()
            prepare.assert_not_called()
        with patch.object(f.hashlib, 'file_digest', None), self.assertRaisesRegex(RuntimeError, 'file_digest'):
            f.main()
        supported = f.require_supported_python()
        self.assertGreaterEqual(tuple(supported['versionInfo']), (3, 11))
        self.assertEqual(supported['executable'], sys.executable)
        self.assertTrue(supported['fileDigest'])

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

    def test_missing_producer_and_legacy_emissions_fail(self):
        legacy = b'NATIVE_THERMAL_MUTATION_RESPONSE {"frame":"response","version":91}\n'
        for data in (b'', legacy, legacy + self.line(), self.line() + legacy):
            with self.subTest(data=data), self.assertRaises(ValueError):
                f.collect(data, 'intended_frame_producer', {'peer_response'})
        pure = next(iter(f.PURE_ZERO_CHECKS))
        self.assertEqual(f.collect(b'', pure, {'peer_response'}, minimum_records=0), [])
        with self.assertRaises(ValueError):
            f.collect(b'', 'intended_frame_producer', {'peer_response'}, minimum_records=0)
        with self.assertRaisesRegex(ValueError, 'legacy'):
            f.collect(legacy, pure, {'peer_response'}, minimum_records=0)
        manifest = f.load_manifest(REPO)
        declarations = [case for corpus in manifest['corpora'] for case in corpus['cases']]
        self.assertEqual({case['name'] for case in declarations if case['minimumRecords'] == 0}, f.PURE_ZERO_CHECKS)
        self.assertTrue(all(case['minimumRecords'] == 1 for case in declarations if case['name'] not in f.PURE_ZERO_CHECKS))

    def test_collector_rejects_scope_corruption_and_truncation(self):
        for data in (self.line('unknown'), self.line('publisher_projection'), self.line()[:-1],
                     b'prefix ' + self.line(), self.line(raw='{}'), self.line(raw=''),
                     self.line().replace(b'"schemaVersion": 1', b'"schemaVersion": 2'),
                     # Frames from the retired protocol 80 and 85 peers.
                     self.line(raw='{"frame":"response","version":80,"z":1,"a":2}'),
                     self.line(raw='{"frame":"response","version":85,"z":1,"a":2}')):
            with self.subTest(data=data), self.assertRaises((ValueError, KeyError)):
                f.collect(data, 'test', {'peer_response'})
        with tempfile.TemporaryDirectory() as d, self.assertRaises(ValueError):
            f.write_corpus(Path(d) / 'empty.json', [], False)

    def test_exact_command_recorder(self):
        manifest = f.load_manifest(REPO)
        self.assertEqual([len(c['cases']) for c in manifest['corpora']], [8, 2, 2, 10, 2])
        recorded = []
        for features in f.TUPLES:
            recorded.append(f.native_build_command(REPO, features))
        for corpus in manifest['corpora']:
            for case in corpus['cases']:
                recorded.append(f.native_test_command(Path('test.exe'), case['name'], manifest))
        self.assertEqual(len(recorded), 26)
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

    def test_frozen_persisted_inputs_ignore_fresh_output_but_not_pin_drift(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            pinned = root / 'tools/openblade-windows-host/tests/fixtures'
            pinned.mkdir(parents=True)
            generated = root / 'out/edge-producer/generated'
            generated.mkdir(parents=True)
            (pinned / 'store.json').write_bytes(b'schema 25')
            (generated / 'store.json').write_bytes(b'schema 27')
            inventory = dict(originalLegacyInputs=[], generated=[],
                             frozenPersisted=[dict(name='store.json', sha256=f.digest(pinned / 'store.json'))])
            (pinned / 'native-thermal-edge-fixtures.json').write_text(json.dumps(inventory))
            f.compare_generated(root, root / 'out')
            (pinned / 'store.json').write_bytes(b'edited')
            with self.assertRaisesRegex(ValueError, 'persisted input drift'):
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

    def test_check_entrypoint_rejects_foreign_root_and_repository(self):
        with tempfile.TemporaryDirectory() as d:
            parent = Path(d)
            expected = parent / 'expected-run'
            foreign = parent / 'foreign-run'
            foreign_repo = parent / 'foreign-repo'
            for path in (expected, foreign, foreign_repo):
                path.mkdir()
            receipt = dict(status='PREPARED', runId=foreign.name, root=str(foreign), repository=str(REPO),
                           source={'bound': 'source'}, inputs={})
            for name in f.ENV_NAMES:
                path = foreign / name
                path.write_text('[{"frame":"response"}]')
                receipt['inputs'][name] = dict(path=str(path), count=1, sha256=f.digest(path))
            def check_with(value, repository, root):
                f.write_json(root / 'receipt.json', value)
                arguments = ['fixtures', '--repository', str(repository), '--root', str(root), '--mode', 'Check']
                with patch.object(sys, 'argv', arguments), patch.object(sys, 'platform', 'win32'):
                    f.main()
            with patch.object(f, 'source_manifest', return_value=receipt['source']):
                # Positive control: every real input file/count/hash is valid.
                check_with(receipt, REPO, foreign)
                with self.assertRaisesRegex(ValueError, 'root does not match caller'):
                    check_with(receipt, REPO, expected)
                receipt['repository'] = str(foreign_repo)
                with self.assertRaisesRegex(ValueError, 'repository does not match caller'):
                    check_with(receipt, REPO, foreign)

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
                f.verify_inputs(receipt, REPO, root)
                receipt['runId'] = 'stale'
                with self.assertRaises(ValueError):
                    f.verify_inputs(receipt, REPO, root)
                receipt['runId'] = root.name
                path.write_text('[]')
                with self.assertRaises(ValueError):
                    f.verify_inputs(receipt, REPO, root)
                path.unlink()
                with self.assertRaises((ValueError, FileNotFoundError)):
                    f.verify_inputs(receipt, REPO, root)
            with self.assertRaises(ValueError):
                f.verify_inputs({}, REPO, root)


if __name__ == '__main__':
    unittest.main(verbosity=2)
