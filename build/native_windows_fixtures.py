"""Windows test fixtures only. Exact allowlist, offline builds, byte-preserving receipts."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

TARGET = 'openblade-windows-service-candidate'
TUPLES = ('service-battery-policy-v1', 'service-temporary-charge-v1')
EMITTERS = {'peer_response', 'peer_subscription', 'publisher_projection', 'synthetic_projection'}
# OpenBladeIpc.ProtocolVersion: the native peer speaks the combined protocol.
FRAME_VERSION = 91
PREFIX = b'OPENBLADE_FIXTURE_FRAME '
PURE_ZERO_CHECKS = {'installed::readiness::thermal_storage::release_read_tests::tfc_release_only_exact_read_operations_tag_retryable_errors'}
MAX_LOG = 16 * 1024 * 1024
ENV_NAMES = ('OPENBLADE_NATIVE_THERMAL_RESPONSES', 'OPENBLADE_NATIVE_BATTERY_RESPONSES',
             'OPENBLADE_NATIVE_BATTERY_PUBLICATIONS', 'OPENBLADE_NATIVE_TFC_CORRECTION_FRAMES',
             'OPENBLADE_NATIVE_EDGE_FRAMES')


def developer_disk_gate_applies(environ=None):
    # The 25 GiB gate protects the developer host, whose C: also holds the
    # shared Cargo cache. GitHub-hosted runners are discarded after each job;
    # a self-hosted runner reports 'self-hosted' and keeps the gate. Mirrors
    # Test-OpenBladeDisposableHostedRunner in NativeWindowsFixtures.ps1.
    environ = os.environ if environ is None else environ
    return not (environ.get('GITHUB_ACTIONS') == 'true' and environ.get('RUNNER_ENVIRONMENT') == 'github-hosted')


def require_supported_python():
    if sys.version_info < (3, 11) or not callable(getattr(hashlib, 'file_digest', None)):
        raise RuntimeError('Python 3.11 or newer with hashlib.file_digest is required for native fixture checks')
    return {'executable': sys.executable, 'versionInfo': list(sys.version_info[:3]), 'fileDigest': True}


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')


def load_manifest(repo):
    m = json.loads((repo / 'build/NativeWindowsFixtureManifest.json').read_text(encoding='utf-8-sig'))
    if m['schemaVersion'] != 2 or m['target'] != TARGET or tuple(m['tuples']) != TUPLES:
        raise ValueError('Unreviewed target or feature tuple')
    if {c['env'] for c in m['corpora']} != set(ENV_NAMES) or len(m['corpora']) != 5:
        raise ValueError('Required five corpus scopes differ')
    if m['managedProjects'] != ['tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj',
                                 'tests/OpenBlade.Contracts.Tests/OpenBlade.Contracts.Tests.csproj',
                                 'tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj']:
        raise ValueError('Unreviewed managed prerequisite scope')
    seen = set()
    for c in m['corpora']:
        if c['tuple'] not in TUPLES or not c['cases'] or not set(c['emitters']) <= EMITTERS:
            raise ValueError('Invalid corpus tuple/emitter scope')
        if ('TFC' in c['env']) != (c['tuple'] == TUPLES[1]):
            raise ValueError('TFC/basic tuple separation changed')
        for declaration in c['cases']:
            case = declaration['name']
            minimum = declaration['minimumRecords']
            if type(minimum) is not int or minimum != (0 if case in PURE_ZERO_CHECKS else 1):
                raise ValueError('Unreviewed case contribution requirement')
            if minimum == 0 and not declaration.get('zeroRecordsReason'):
                raise ValueError('Pure zero-record check requires an explicit reason')
            if not re.fullmatch(r'installed::readiness::[a-zA-Z0-9_:]+', case) or case in seen:
                raise ValueError('Invalid or duplicated exact test')
            seen.add(case)
        if not re.fullmatch(r'OpenBlade.Contracts.Tests.NativeThermal\w+Tests\.ActualNative\w+', c['fact']):
            raise ValueError('Unreviewed managed consumer')
    return m


def source_manifest(repo):
    paths = subprocess.check_output(['git', 'ls-files', '-z'], cwd=repo).decode().split('\0')
    paths += ['build/NativeWindowsFixtureManifest.json', 'build/NativeWindowsFixtures.ps1',
              'build/Test-NativeWindowsFixtures.ps1', 'build/native_windows_fixtures.py',
              'tests/Build.Tests/NativeWindowsFixtures.Tests.ps1', 'build/test_native_windows_fixtures.py']
    # Includes the actual managed producer/codec, Rust source, locks and test inputs.
    return {p: digest(repo / p) for p in sorted(set(paths)) if p and (repo / p).is_file()
            and not p.startswith(('.trail/', 'graft/'))}


def checked_run(command, cwd, out, label, receipts, timeout=600, env=None):
    log = out / (label + '.log')
    started = time.time()
    receipt = {'command': command, 'cwd': str(cwd), 'log': str(log), 'startedUtcEpoch': started}
    receipts.append(receipt)
    with log.open('xb') as stream:
        process = subprocess.Popen(command, cwd=cwd, stdout=stream, stderr=subprocess.STDOUT, env=env)
        try:
            while process.poll() is None:
                if time.time() - started > timeout or log.stat().st_size > MAX_LOG:
                    raise TimeoutError(f'{label}: timeout or bounded log exceeded')
                time.sleep(.1)
        except BaseException:
            # Kill only this runner-owned command tree, never an image-name sweep.
            subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], capture_output=True)
            process.wait()
            raise
    receipt.update(exitCode=process.returncode, seconds=round(time.time() - started, 3), sha256=digest(log))
    text = log.read_text(encoding='utf-8-sig', errors='strict')
    if process.returncode:
        raise RuntimeError(f'{label} exited {process.returncode}; {log}\n{text[-6000:]}')
    return text


def check_native_result(text):
    if not re.search(r'test result: ok\. 1 passed; 0 failed; 0 ignored;', text):
        raise ValueError('Selected native test did not execute exactly one non-ignored passing test')


def collect(data, case, allowed, minimum_records=1):
    if type(minimum_records) is not int or minimum_records != (0 if case in PURE_ZERO_CHECKS else 1):
        raise ValueError('Unreviewed case contribution requirement')
    records = []
    offset = 0
    for line_number, line in enumerate(data.splitlines(keepends=True), 1):
        # Legacy frames lack emitter custody. TRACE copies corroborate an
        # already classified delivery and never contribute another record.
        if line.startswith(b'NATIVE_THERMAL_') and not line.startswith(b'NATIVE_THERMAL_EDGE_TRACE '):
            raise ValueError('Unclassified legacy frame emission')
        if PREFIX in line and not line.startswith(PREFIX):
            raise ValueError('Unanchored fixture record')
        if line.startswith(PREFIX):
            if not line.endswith(b'\n'):
                raise ValueError('Truncated fixture record')
            e = json.loads(line[len(PREFIX):])
            if set(e) != {'schemaVersion', 'emitter', 'method', 'operation', 'raw'}:
                raise ValueError('Unknown envelope shape')
            if e['schemaVersion'] != 1 or e['emitter'] not in allowed or e['emitter'] not in EMITTERS:
                raise ValueError('Unknown or out-of-scope emitter')
            if e['method'] not in ('response', 'statusEvent') or not e['operation'] or not isinstance(e['raw'], str):
                raise ValueError('Invalid frame scope')
            raw = e['raw'].encode('utf-8')
            frame = json.loads(raw)
            if not raw or frame.get('frame') != e['method'] or frame.get('version') != FRAME_VERSION:
                raise ValueError('Frame method/version mismatch')
            if e['emitter'] == 'peer_response' and e['method'] != 'response':
                raise ValueError('Response emitter method mismatch')
            if e['emitter'] == 'peer_subscription' and e['method'] != 'statusEvent':
                raise ValueError('Subscription emitter method mismatch')
            e.update(test=case, stdoutLine=line_number, stdoutByteOffset=offset,
                     rawSha256=hashlib.sha256(raw).hexdigest())
            records.append(e)
        offset += len(line)
    if len(records) < minimum_records:
        raise ValueError('Selected frame producer contributed no classified records: ' + case)
    return records


def write_corpus(path, records, wrapper):
    if not records:
        raise ValueError('Empty required corpus')
    if wrapper:
        # Only envelope is serialized. raw remains the exact original UTF8 string.
        payload = json.dumps([{'method': r['test'], 'operation': r['operation'], 'raw': r['raw']}
                              for r in records], ensure_ascii=False).encode('utf-8')
    else:
        payload = b'[' + b',\n'.join(r['raw'].encode('utf-8') for r in records) + b']\n'
    with path.open('xb') as stream:
        stream.write(payload)
    return {'path': str(path), 'sha256': digest(path), 'count': len(records)}


def verify_inputs(receipt, expected_repository, expected_root):
    if receipt.get('status') != 'PREPARED' or not receipt.get('runId') or not receipt.get('source'):
        raise ValueError('Missing fresh prepared receipt')
    root = Path(receipt['root']).resolve(strict=True)
    repository = Path(receipt['repository']).resolve(strict=True)
    if root != Path(expected_root).resolve(strict=True):
        raise ValueError('Receipt root does not match caller')
    if repository != Path(expected_repository).resolve(strict=True):
        raise ValueError('Receipt repository does not match caller')
    if root.name != receipt['runId']:
        raise ValueError('Stale run binding')
    for item in receipt['inputs'].values():
        path = Path(item['path'])
        if not path.is_absolute() or not path.resolve(strict=True).is_relative_to(root) or item['count'] < 1:
            raise ValueError('Invalid or empty input scope')
        if not isinstance(json.loads(path.read_bytes()), list) or len(json.loads(path.read_bytes())) != item['count']:
            raise ValueError('Empty or invalid corpus count')
        if digest(path) != item['sha256']:
            raise ValueError('Mutated corpus input')
    if set(receipt['inputs']) != set(ENV_NAMES):
        raise ValueError('Missing required corpus input')
    if source_manifest(repository) != receipt['source']:
        raise ValueError('Source changed after preparation')


def check_trx(path, fact):
    root = ET.parse(path).getroot()
    results = [n for n in root.iter() if n.tag.endswith('UnitTestResult')]
    if len(results) != 1 or results[0].get('outcome') != 'Passed' or results[0].get('testName') != fact:
        raise ValueError(f'Expected one passing, non-skipped exact managed Fact: {fact}')


def managed_fact(repo, out, fact, receipts, index, build=False):
    project = 'OpenBlade.' + fact.split('.')[1] + '.Tests'
    arguments = ['dotnet', 'test', str(repo / 'tests' / project / (project + '.csproj')),
                 '--configuration', os.environ.get('OPENBLADE_FIXTURE_CONFIGURATION', 'Debug'),
                 '--no-restore', '--no-build', '-p:NuGetAudit=false', '-p:OpenBladeNativePackageTests=true',
                 '--filter', 'FullyQualifiedName=' + fact, '--logger', f'trx;LogFileName=managed-{index}.trx',
                 '--results-directory', str(out)]
    checked_run(arguments, repo, out, f'managed-{index}', receipts)
    check_trx(out / f'managed-{index}.trx', fact)


def compare_generated(repo, out):
    pinned = repo / 'tools/openblade-windows-host/tests/fixtures'
    inventory = json.loads((pinned / 'native-thermal-edge-fixtures.json').read_text())
    for f in inventory['originalLegacyInputs']:
        if digest(pinned / f['name']) != f['sha256']:
            raise ValueError('Pinned legacy input drift: ' + f['name'])
    # Persisted inputs frozen at the schemas the native readers admit. The
    # producer still writes current managed documents; only pins are checked.
    for f in inventory.get('frozenPersisted', []):
        if digest(pinned / f['name']) != f['sha256']:
            raise ValueError('Pinned persisted input drift: ' + f['name'])
    for f in inventory['generated']:
        generated = out / 'edge-producer/generated' / f['name']
        if generated.read_bytes() != (pinned / f['name']).read_bytes() or digest(generated) != f['sha256']:
            raise ValueError('Fresh managed include_bytes semantic drift: ' + f['name'])


def native_build_command(repo, features):
    if features not in TUPLES:
        raise ValueError('Unknown feature tuple')
    return ['cargo', 'test', '--manifest-path', str(repo / 'tools/openblade-windows-host/Cargo.toml'),
            '--locked', '--offline', '--no-default-features', '--features', features,
            '--bin', TARGET, '--no-run', '--message-format=json']


def native_test_command(binary, case, manifest):
    if case not in {t['name'] for c in manifest['corpora'] for t in c['cases']}:
        raise ValueError('Unreviewed native test')
    return [str(binary), case, '--exact', '--nocapture', '--test-threads=1', '--quiet']


def prepare_managed_dependency(repo, out, configuration, receipts):
    # Separate compile/data-only prerequisite. Never execute the resulting DLL
    # here, and never invoke the ordinary per-output-target development builder.
    payload = out / 'managed-ffi-payload'
    payload.mkdir()
    env = os.environ.copy()
    env['RUSTFLAGS'] = '-C target-feature=+crt-static'
    env.pop('CARGO_ENCODED_RUSTFLAGS', None)
    env['CARGO_PROFILE_DEV_PANIC'] = 'unwind'
    env['CARGO_PROFILE_RELEASE_PANIC'] = 'unwind'
    command = ['cargo', 'build', '--manifest-path', str(repo / 'rust/Cargo.toml'),
               '--locked', '--offline', '-p', 'openblade-ffi', '--lib', '--target', 'x86_64-pc-windows-msvc']
    if configuration == 'Release':
        command.append('--release')
    checked_run(command, repo, out, 'managed-ffi-dependency', receipts, 900, env)
    profile = configuration.lower() if configuration == 'Release' else 'debug'
    binary = Path(env['CARGO_TARGET_DIR']) / 'x86_64-pc-windows-msvc' / profile / 'openblade_ffi.dll'
    destination = payload / 'openblade_ffi.dll'
    shutil.copyfile(binary, destination)
    inputs = [{'path': str(p.relative_to(repo)).replace('\\', '/'), 'sha256': digest(p)}
              for p in sorted((repo / 'rust').rglob('*')) if p.is_file() and 'target' not in p.parts]
    revision = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo).decode().strip()
    props = (repo / 'Directory.Build.props').read_text(encoding='utf-8-sig')
    version = re.search(r'<OpenBladeVersion>([^<]+)</OpenBladeVersion>', props).group(1)
    metadata = dict(schemaVersion=1, abiVersion='0x00010000', fileName='openblade_ffi.dll',
                    target='x86_64-pc-windows-msvc', productVersion=version, sourceRevision=revision,
                    sha256=digest(destination), inputs=inputs)
    write_json(payload / 'OpenBlade.Native.provenance.json', metadata)
    return payload, {'classification': 'Separate required managed-FFI compile/data-only dependency, not candidate tests or product-role execution',
                     'source': inputs, 'features': [], 'target': metadata['target'], 'binaryPath': str(binary),
                     'binarySha256': metadata['sha256'], 'payloadManifestSha256': digest(payload / 'OpenBlade.Native.provenance.json'),
                     'environment': {k: env.get(k) for k in ('CARGO_TARGET_DIR', 'RUSTFLAGS', 'CARGO_ENCODED_RUSTFLAGS', 'CARGO_PROFILE_DEV_PANIC', 'CARGO_PROFILE_RELEASE_PANIC')}}


def require_dotnet_10(repo, out, receipts):
    # The managed prerequisites target net10.0. They build with the SDK the host
    # selects, exactly like the solution build, so a newer default SDK (hosted
    # images ship one beside setup-dotnet's 10.x) is accepted. Their tests run
    # only on the .NET 10 shared runtime, which must be installed.
    sdk = checked_run(['dotnet', '--version'], repo, out, 'dotnet-version', receipts).strip()
    major = re.match(r'(\d+)\.', sdk)
    if not major or int(major.group(1)) < 10:
        raise ValueError('The managed fixture runner requires a .NET 10 or newer SDK, not ' + sdk)
    runtimes = checked_run(['dotnet', '--list-runtimes'], repo, out, 'dotnet-runtimes', receipts)
    netcore = re.findall(r'^Microsoft\.NETCore\.App (10\.\d+\.\d+\S*) ', runtimes, re.M)
    if not netcore:
        raise ValueError('The managed fixture runner requires an installed .NET 10 runtime')
    return {'sdk': sdk, 'netCoreRuntimes': netcore}


def prepare(repo, out, mode):
    manifest = load_manifest(repo)
    receipts = []
    receipt = {'pythonRuntime': require_supported_python(), 'schemaVersion': 1, 'mode': mode, 'status': 'FAILED', 'runId': out.name, 'root': str(out), 'repository': str(repo),
               'source': source_manifest(repo), 'commands': receipts, 'inputs': {}, 'binaries': [],
               'boundary': 'Test-only current-process SID lookup and protected own-SID NTFS fixtures; injected peers, no real peer authentication or product roles.'}
    try:
        for name in ENV_NAMES:
            if os.environ.get(name):
                raise ValueError('Inherited corpus is forbidden: ' + name)
        if os.environ.get('CARGO_TARGET_DIR') != r'C:\build-cache\cargo\openblade-core':
            raise ValueError('Unexpected shared Cargo target')
        if developer_disk_gate_applies() and shutil.disk_usage('C:\\').free < 25 * 1024**3:
            raise ValueError('C: has less than 25 GiB free')
        # Resolve shims without installing a toolchain. Receipts bind actual
        # compiler/Cargo bytes, including on hosted runners whose PATH has rustup.
        rustup = shutil.which('rustup')
        resolved_tools = {}
        if rustup:
            for tool in ('cargo', 'rustc'):
                output = checked_run([rustup, 'which', '--toolchain', '1.99.1', tool],
                                     repo, out, 'resolve-' + tool, receipts, 30)
                resolved_tools[tool] = str(Path(output.strip()).resolve(strict=True))
            tool_bin = str(Path(resolved_tools['rustc']).parent)
            os.environ['PATH'] = tool_bin + os.pathsep + os.environ['PATH']
        for tool in ('cargo', 'rustc', 'dotnet'):
            path = resolved_tools.get(tool) or shutil.which(tool)
            if not path:
                raise ValueError('Required installed tool unavailable: ' + tool)
            receipt.setdefault('tools', {})[tool] = {'path': path, 'sha256': digest(path)}
        version = checked_run(['rustc', '--version'], repo, out, 'rust-version', receipts)
        if not version.startswith('rustc 1.99.1 '):
            raise ValueError('Installed Rust differs from reviewed pin')
        receipt['dotnet'] = require_dotnet_10(repo, out, receipts)
        receipt['buildEnvironment'] = {k: os.environ.get(k) for k in ('CARGO_HOME', 'RUSTUP_HOME', 'CARGO_TARGET_DIR', 'RUSTFLAGS', 'CARGO_ENCODED_RUSTFLAGS', 'RUSTUP_TOOLCHAIN')}
        payload, receipt['managedDependency'] = prepare_managed_dependency(repo, out, os.environ['OPENBLADE_FIXTURE_CONFIGURATION'], receipts)
        # Empty package sources ensure restores use only the installed package cache.
        nuget = out / 'offline-nuget.config'
        nuget.write_text('<configuration><packageSources><clear /></packageSources></configuration>')
        for index, project in enumerate(manifest['managedProjects']):
            path = str(repo / project)
            common = ['-p:NuGetAudit=false', '-p:OpenBladeNativePackageTests=true',
                      '-p:OpenBladeNativeDevelopmentBuild=false', '-p:OpenBladeNativePayloadDirectory=' + str(payload)]
            checked_run(['dotnet', 'restore', path, '--configfile', str(nuget)] + common, repo, out, f'restore-{index}', receipts)
            checked_run(['dotnet', 'build', path, '--configuration', os.environ['OPENBLADE_FIXTURE_CONFIGURATION'],
                         '--no-restore', '-m:1'] + common, repo, out, f'build-{index}', receipts)
        os.environ['OPENBLADE_EDGE_MANAGED_ROOT'] = str(out / 'edge-producer')
        managed_fact(repo, out, manifest['producer'], receipts, 0)
        compare_generated(repo, out)
        all_records = []
        for tuple_index, features in enumerate(TUPLES):
            text = checked_run(native_build_command(repo, features), repo, out, f'cargo-{tuple_index}', receipts, 900)
            artifacts = [json.loads(line) for line in text.splitlines() if line.startswith('{')]
            matches = [a for a in artifacts if a.get('reason') == 'compiler-artifact' and a.get('target', {}).get('name') == TARGET
                       and a.get('target', {}).get('kind') == ['bin'] and a.get('profile', {}).get('test') is True and a.get('executable')]
            if len(matches) != 1:
                raise ValueError('Cargo did not identify exactly one selected test executable')
            artifact = matches[0]
            if features not in artifact['features'] or (features == TUPLES[0] and TUPLES[1] in artifact['features']):
                raise ValueError('Cargo feature mismatch')
            if Path(artifact['target']['src_path']).resolve() != repo / 'tools/openblade-windows-host/src/installed_main.rs':
                raise ValueError('Cargo selected an unexpected source target')
            binary = Path(artifact['executable']).resolve(strict=True)
            if not binary.is_relative_to(Path(os.environ['CARGO_TARGET_DIR']).resolve()) or binary.suffix != '.exe':
                raise ValueError('Cargo executable escaped shared target')
            binding = {'path': str(binary), 'sha256': digest(binary), 'tuple': features,
                       'cargoFeatures': artifact['features'], 'target': artifact['target']}
            receipt['binaries'].append(binding)
            for corpus in [c for c in manifest['corpora'] if c['tuple'] == features]:
                records = []
                for declaration in corpus['cases']:
                    case = declaration['name']
                    label = f'native-{len(receipts):02}'
                    if digest(binary) != binding['sha256']:
                        raise ValueError('Selected native executable mutated')
                    text = checked_run(native_test_command(binary, case, manifest),
                                       repo, out, label, receipts, 90)
                    check_native_result(text)
                    emitted = collect((out / (label + '.log')).read_bytes(), case, set(corpus['emitters']), declaration['minimumRecords'])
                    for r in emitted:
                        r.update(runId=out.name, tuple=features, executableSha256=binding['sha256'],
                                 log=label + '.log', index=len(all_records))
                        all_records.append(r)
                    records.extend(emitted)
                receipt['inputs'][corpus['env']] = write_corpus(out / (corpus['env'] + '.json'), records, corpus['wrapper'])
                os.environ[corpus['env']] = receipt['inputs'][corpus['env']]['path']
        write_json(out / 'frame-provenance.json', all_records)
        receipt['status'] = 'PREPARED'
        verify_inputs(receipt, repo, out)
        for index, corpus in enumerate(manifest['corpora'], 1):
            verify_inputs(receipt, repo, out)
            managed_fact(repo, out, corpus['fact'], receipts, index)
            verify_inputs(receipt, repo, out)
        if mode == 'VerifySelected':
            managed_fact(repo, out, manifest['history'], receipts, 6)
        verify_inputs(receipt, repo, out)
        # Standard CI tests own a separate fresh generator root.
        receipt['environment'] = {**{n: receipt['inputs'][n]['path'] for n in ENV_NAMES},
            'OPENBLADE_NATIVE_EDGE_FIXTURES': '1', 'OPENBLADE_EDGE_MANAGED_ROOT': str(out / 'edge-suite'),
            'OPENBLADE_EDGE_V79_INPUT_ROOT': str(repo / 'tools/openblade-windows-host/tests/fixtures'),
            'OPENBLADE_HISTORY_MANAGED_FIXTURE_ROOT': str(out / 'history'),
            'OPENBLADE_THERMAL_FIXTURE_ROOT': str(out / 'native-history'),
            'OPENBLADE_THERMAL_FIXTURE_SID': os.environ['OPENBLADE_THERMAL_FIXTURE_SID']}
    except BaseException as error:
        receipt.update(status='FAILED', error=str(error))
        raise
    finally:
        write_json(out / 'receipt.json', receipt)


def main():
    # Validate before resolving fixture paths or preparing any roots/builds.
    require_supported_python()
    parser = argparse.ArgumentParser()
    parser.add_argument('--repository', type=Path, required=True)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--mode', choices=('PrepareOnly', 'VerifySelected', 'Check'), required=True)
    args = parser.parse_args()
    if sys.platform != 'win32':
        raise ValueError('Windows-only fixture runner')
    repo, out = args.repository.resolve(strict=True), args.root.resolve(strict=True)
    if args.mode == 'Check':
        verify_inputs(json.loads((out / 'receipt.json').read_text()), repo, out)
    else:
        prepare(repo, out, args.mode)


if __name__ == '__main__':
    main()
