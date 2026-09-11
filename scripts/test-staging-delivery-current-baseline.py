#!/usr/bin/env python3
"""Offline current-baseline custody, no runtime mutation and retention tests."""
import ast
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile
from contextlib import redirect_stdout, redirect_stderr
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT/'scripts'/filename)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


b = load('baseline_test_subject', 'staging-delivery-current-baseline.py')
p = load('baseline_provenance_subject', 'staging-delivery-baseline-provenance.py')
f = load('baseline_audit_fixtures', 'test-staging-paired-readonly-audit.py')
OWNER = '0'*64


class BaselineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='.current-baseline-test-', dir=Path.home())
        self.addCleanup(self.temp.cleanup)
        self.home = Path(self.temp.name)
        self.root = self.home/'.jeeb-deploy'/'paired-releases'
        self.journal = self.root/b.a.SECRET_NAME
        for path in (self.home/'.jeeb-deploy', self.root, self.journal, self.root/'consumed'):
            path.mkdir(mode=0o700)
        meta = f.metadata()
        for i, phase in enumerate(b.a.PHASES[:4]):
            b.c.write_exclusive(self.journal/f'{i:02d}-{phase}.json', {'phase': phase, **meta})
        b.c.write_exclusive(self.root/'consumed'/(meta['receiptNonce']+'.json'),
                            {'activation': b.a.SECRET_NAME, 'gatewayRun': meta['gatewayRun']})
        for role in b.a.SERVICES:
            b.c.write_exclusive(self.root/'consumed'/(b.a.SECRET_NAME+'-'+role+'-post.json'),
                {'serviceId': meta[role+'ServiceId'], 'version': meta[role+'Version'],
                 'phase': role+'-submission-pending'})
        journal, metadata = b.a.read_journal(self.home)
        roles = {role: f.private_fixture(role) for role in b.a.SERVICES}
        services = {role: value['service'] for role, value in roles.items()}
        self.snapshot = {'info': {'ID': 'daemon-fixture', 'Swarm': {'NodeID': f.NODE}},
            'journal': journal, 'metadata': metadata, 'roles': roles, 'services': services,
            'after': copy.deepcopy(services), 'secret': f.secret(),
            'journal_after': copy.deepcopy(journal), 'metadata_after': copy.deepcopy(metadata)}
        self.provenance = b.authority('f'*40, '3', '1')

    def collect(self, home):
        self.assertEqual(Path(home), self.home)
        return copy.deepcopy(self.snapshot)

    def reconcile(self):
        with patch.object(b.a, 'collect', side_effect=self.collect):
            return b.reconcile(self.home, self.provenance)

    def verify(self, exact=False, seal=None):
        if seal is None:
            seal = b.digest(b.load_seal(self.home)[0])
        with b.c.held_lock(self.home, OWNER), patch.object(b.a, 'collect', side_effect=self.collect):
            return b.verify_retention(self.home, OWNER, exact=exact, expected_seal=seal)

    def old_bytes(self):
        return {str(path.relative_to(self.root)): path.read_bytes()
                for folder in (self.journal, self.root/'consumed') for path in folder.iterdir()}

    def test_preserves_every_original_and_full_private_snapshot(self):
        before = self.old_bytes()
        report = self.reconcile()
        self.assertEqual(self.old_bytes(), before)
        self.assertEqual(report['historical_completion'], 'unproven')
        self.assertNotIn(f.SENTINEL, json.dumps(report))
        seal, evidence = b.load_seal(self.home)
        self.assertEqual(evidence['snapshot'], self.snapshot)
        self.assertEqual(evidence['audit'], b.a.project(self.snapshot))
        self.assertEqual(report['seal_sha256'], b.digest(seal))
        for path in (self.root/b.NAME).iterdir():
            self.assertEqual(path.stat().st_mode & 0o777, 0o400)
        self.assertEqual(self.verify(exact=True, seal=report['seal_sha256']), f.SECRET)

    def test_partial_and_complete_record_both_reject_reentry(self):
        self.reconcile()
        before = self.old_bytes()
        with self.assertRaises(FileExistsError): self.reconcile()
        (self.root/b.NAME/'sealed.json').unlink()
        with self.assertRaises(FileExistsError): self.reconcile()
        with self.assertRaises(Exception): self.verify()
        self.assertEqual(self.old_bytes(), before)

    def test_second_read_race_never_seals(self):
        changed = copy.deepcopy(self.snapshot)
        changed['services']['gateway']['Version']['Index'] += 1
        changed['after'] = copy.deepcopy(changed['services'])
        with patch.object(b.a, 'collect', side_effect=[self.snapshot, changed]):
            with self.assertRaises(ValueError): b.reconcile(self.home, self.provenance)
        self.assertFalse((self.root/b.NAME/'sealed.json').exists())

    def test_second_read_ignores_routine_health_log_but_not_runtime_drift(self):
        original = copy.deepcopy(self.snapshot)
        original['roles']['gateway']['container']['State']['Health'] = {
            'Status': 'healthy', 'FailingStreak': 0, 'Log': [{'Start': 'old-check', 'Output': f.SENTINEL}]}
        benign = copy.deepcopy(original)
        benign['info']['SystemTime'] = 'new-clock-tick'
        benign['roles']['gateway']['container']['State']['Health']['Log'] = [{'Start': 'new-check'}]
        self.assertEqual(b.stable_runtime(original), b.stable_runtime(benign))
        with patch.object(b.a, 'collect', side_effect=[original, benign]):
            b.reconcile(self.home, self.provenance)
        self.assertTrue((self.root/b.NAME/'sealed.json').exists())
        for field in ('health_status', 'failure_streak', 'pid', 'process', 'environment', 'image', 'task', 'network', 'service'):
            changed = copy.deepcopy(benign)
            container = changed['roles']['gateway']['container']
            if field == 'health_status': container['State']['Health']['Status'] = 'unhealthy'
            elif field == 'failure_streak': container['State']['Health']['FailingStreak'] = 1
            elif field == 'pid': container['State']['Pid'] = 999
            elif field == 'process': container['Path'] = '/unexpected'
            elif field == 'environment': container['Config']['Env'].append('OTHER=value')
            elif field == 'image': changed['roles']['gateway']['image']['Id'] = 'sha256:'+'0'*64
            elif field == 'task': changed['roles']['gateway']['tasks'][0]['ID'] = 'x'*25
            elif field == 'network': container['NetworkSettings'] = {'Networks': {'unexpected': {}}}
            else: changed['services']['gateway']['Version']['Index'] += 1
            with self.subTest(field=field):
                self.assertNotEqual(b.stable_runtime(original), b.stable_runtime(changed))

    def test_every_required_current_runtime_verdict_is_enforced(self):
        report = b.a.project(self.snapshot)
        for key in b.REQUIRED:
            altered = copy.deepcopy(report)
            altered['roles']['delivery'][key] = False
            with self.subTest(key=key), patch.object(b.a, 'project', return_value=altered):
                with self.assertRaises(ValueError): b.verified(self.snapshot)

    def test_process_injection_and_shadow_mounts_are_rejected(self):
        for key in ('LD_PRELOAD', 'dotnet_environment', 'Mounts', 'Configs'):
            snapshot = copy.deepcopy(self.snapshot)
            declaration = snapshot['services']['gateway']['Spec']['TaskTemplate']['ContainerSpec']
            if key in ('Mounts', 'Configs'):
                declaration[key] = [{'Target': '/app'}]
            else:
                declaration['Env'].append(key+'=private-sentinel')
            with self.subTest(key=key):
                with self.assertRaises(ValueError): b.verified(snapshot)

    def test_normal_retention_accepts_unrelated_successor_but_first_cas_does_not(self):
        report = self.reconcile()
        role = self.snapshot['roles']['gateway']
        role['service']['Version']['Index'] += 1
        role['service']['Spec']['Labels']['new-label'] = 'unrelated'
        self.snapshot['after'] = copy.deepcopy(self.snapshot['services'])
        self.assertEqual(self.verify(), f.SECRET)
        with self.assertRaises(ValueError): self.verify(exact=True, seal=report['seal_sha256'])
        with self.assertRaises(ValueError): self.verify(exact=True, seal='0'*64)

    def test_stale_identity_version_and_credential_changes_are_rejected(self):
        self.reconcile()
        original = copy.deepcopy(self.snapshot)
        for field in ('service_id', 'version', 'same_version_drift', 'secret', 'metadata', 'peer_auth', 'daemon', 'node'):
            self.snapshot = copy.deepcopy(original)
            service = self.snapshot['services']['delivery']
            if field == 'service_id': service['ID'] = 'x'*25
            elif field == 'version': service['Version']['Index'] -= 1
            elif field == 'same_version_drift': service['Spec']['Labels']['new'] = 'unexpected'
            elif field == 'secret': self.snapshot['secret']['ID'] = 'x'*25
            elif field == 'metadata': self.snapshot['secret']['Spec']['Labels']['jeeb.version'] = '2'
            elif field == 'peer_auth': service['Spec']['TaskTemplate']['ContainerSpec']['Env'].append('DELIVERY_SERVICE_TOKEN=secret')
            elif field == 'daemon': self.snapshot['info']['ID'] = 'changed'
            else: self.snapshot['info']['Swarm']['NodeID'] = 'x'*25
            self.snapshot['after'] = copy.deepcopy(self.snapshot['services'])
            with self.subTest(field=field):
                with self.assertRaises(ValueError): self.verify()

    def test_tampered_original_even_same_json_is_rejected(self):
        self.reconcile()
        path = self.journal/'00-prepared.json'
        raw = path.read_bytes()
        path.chmod(0o600); path.write_bytes(raw+b' '); path.chmod(0o400)
        with self.assertRaises(ValueError): self.verify()

    def test_missing_or_changed_consumed_claim_is_rejected(self):
        self.reconcile()
        path = self.root/'consumed'/(b.a.SECRET_NAME+'-delivery-post.json')
        path.unlink()
        with self.assertRaises(FileNotFoundError): self.verify()

    def test_file_integrity_permissions_links_and_unknown_files_fail_closed(self):
        self.reconcile()
        path = self.root/b.NAME/'evidence-000.json'
        raw = path.read_bytes()
        for mutation in ('bytes', 'mode', 'symlink', 'hardlink', 'unknown'):
            with self.subTest(mutation=mutation):
                if mutation == 'bytes':
                    path.chmod(0o600); path.write_bytes(raw.replace(b'base64', b'changed')); path.chmod(0o400)
                elif mutation == 'mode': path.chmod(0o600)
                elif mutation == 'symlink':
                    path.unlink(); path.symlink_to(self.journal/'00-prepared.json')
                elif mutation == 'hardlink':
                    path.unlink(); os.link(self.journal/'00-prepared.json', path)
                else: (self.root/b.NAME/'unexpected').touch()
                with self.assertRaises(Exception): b.load_seal(self.home)
                path.unlink(); path.write_bytes(raw); path.chmod(0o400)
                if mutation == 'unknown': (self.root/b.NAME/'unexpected').unlink()

    def test_missing_lock_never_accepts_retention(self):
        self.reconcile()
        with patch.object(b.a, 'collect', side_effect=self.collect):
            with self.assertRaises(Exception): b.verify_retention(self.home, OWNER)

    def test_bundle_is_standalone_without_ambient_modules(self):
        with redirect_stdout(io.StringIO()) as output: b.bundle('reconcile')
        source = output.getvalue()
        with tempfile.TemporaryDirectory() as empty:
            result = subprocess.run([sys_executable(), '-I', '-', 'invalid-command'], input=source,
                                    cwd=empty, text=True, capture_output=True)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stdout, '')
        self.assertEqual(result.stderr.strip(),
            'Current delivery baseline is unverified; historical activation remains unproven.')
        with redirect_stdout(io.StringIO()) as output: b.bundle('retention', 'none')
        subprocess.run(['bash', '-n'], input=output.getvalue(), text=True, check=True)

    def test_entrypoint_sanitizes_private_failure(self):
        with patch.object(b.sys, 'argv', ['baseline', 'reconcile', 'a'*40, '3', '1']), \
             patch.object(b, 'reconcile', side_effect=ValueError(f.SENTINEL)), \
             redirect_stdout(io.StringIO()) as output, redirect_stderr(io.StringIO()) as error:
            self.assertEqual(b.main(), 1)
        self.assertEqual(output.getvalue(), '')
        self.assertNotIn(f.SENTINEL, error.getvalue())

    def test_no_engine_mutation_or_old_journal_writer(self):
        tree = ast.parse((ROOT/'scripts/staging-delivery-current-baseline.py').read_text())
        calls = [ast.unparse(node.func) for node in ast.walk(tree) if isinstance(node, ast.Call)]
        self.assertFalse(any(name.endswith(('.request', '.run', '.Popen', '.unlink', '.remove', '.replace', '.rename')) for name in calls))
        self.assertNotIn('c.Journal', calls)
        self.assertNotIn('c.write_exclusive', [ast.unparse(node.func) for function in tree.body
            if isinstance(function, ast.FunctionDef) and function.name in ('origin', 'verify_retention', 'load_seal')
            for node in ast.walk(function) if isinstance(node, ast.Call)])
        workflow = (ROOT/'.github/workflows/jeeb-staging-delivery-current-baseline.yml').read_text()
        for marker in ('[ "$GITHUB_ACTOR" = oudaykhaled ]', '[ "$GITHUB_TRIGGERING_ACTOR" = oudaykhaled ]',
                       '[ "$REVIEWED_SHA" = "$GITHUB_SHA" ]', 'staging-paired-source-guard.sh', 'environment: staging'):
            self.assertIn(marker, workflow)
        deployment = (ROOT/'.github/workflows/jeeb-staging-deploy.yml').read_text()
        self.assertIn('python3 scripts/staging-delivery-current-baseline.py bundle retention', deployment)
        self.assertIn('scripts/staging-delivery-baseline-provenance.py', deployment)
        self.assertIn('actions: read', deployment)
        self.assertIn('GH_TOKEN: ${{ github.token }}', deployment)

    def test_public_projection_is_exact_saved_artifact_without_private_evidence(self):
        report = self.reconcile()
        public = b.public_provenance(self.home)
        self.assertEqual(public, report)
        self.assertNotIn(f.SENTINEL, json.dumps(public))
        self.assertEqual(p.projection(public), self.provenance)
        with self.assertRaises(ValueError): self.verify(seal='0'*64)


class ProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.value = {'schema_version': 1, 'state': 'sealed', 'authority': b.authority('f'*40, '3', '1'),
                      'seal_sha256': 'a'*64, 'evidence_sha256': 'b'*64,
                      'historical_completion': 'unproven', 'current_posture': 'verified'}
        self.run = {'id': 3, 'run_attempt': 1, 'head_sha': 'f'*40, 'head_branch': 'main',
                    'path': p.WORKFLOW, 'event': 'workflow_dispatch', 'status': 'completed',
                    'conclusion': 'success', 'repository': {'full_name': p.REPOSITORY},
                    'head_repository': {'full_name': p.REPOSITORY},
                    'actor': {'login': 'oudaykhaled'}, 'triggering_actor': {'login': 'oudaykhaled'}}
        self.recorded = copy.deepcopy(self.value)
        self.names = [p.FILENAME]
        self.artifact_count = 1
        self.artifact_changes = {}
        self.calls = []

    def fetch(self, path, raw=False):
        self.calls.append(path)
        prefix = 'repos/'+p.REPOSITORY+'/actions/'
        if path == prefix+'runs/3/attempts/1': return self.run
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w') as archive:
            for name in self.names: archive.writestr(name, json.dumps(self.recorded))
        data = stream.getvalue()
        if path == prefix+'runs/3/artifacts?per_page=100':
            artifact = {'id': 7, 'name': 'delivery-current-baseline-public-seal-3-1', 'expired': False,
                        'size_in_bytes': len(data), 'digest': 'sha256:'+hashlib.sha256(data).hexdigest()}
            artifact.update(self.artifact_changes)
            return {'total_count': self.artifact_count, 'artifacts': [artifact]*self.artifact_count}
        if path == prefix+'artifacts/7/zip' and raw: return data
        raise AssertionError('Unexpected API path')

    def test_exact_attempt_and_immutable_artifact_match(self):
        self.assertEqual(p.verify(self.value, self.fetch), 'a'*64)
        self.assertEqual(self.calls[0], 'repos/'+p.REPOSITORY+'/actions/runs/3/attempts/1')

    def test_absent_and_complete_paths_do_not_fetch_or_require_a_seal(self):
        self.assertEqual(p.verify({'schema_version': 1, 'state': 'not-required'}, self.fetch), 'none')
        self.assertEqual(self.calls, [])

    def test_failed_pending_wrong_workflow_source_attempt_and_actor_are_rejected(self):
        original = copy.deepcopy(self.run)
        changes = {'status': 'in_progress', 'conclusion': 'failure', 'path': 'other.yml',
                   'head_sha': '0'*40, 'head_branch': 'other', 'run_attempt': 2, 'id': 4,
                   'event': 'push', 'actor': {'login': 'other'}, 'triggering_actor': {'login': 'other'},
                   'repository': {'full_name': 'other/repo'}, 'head_repository': {'full_name': 'other/repo'}}
        for field, value in changes.items():
            self.run = {**original, field: value}
            with self.subTest(field=field), self.assertRaises(ValueError): p.verify(self.value, self.fetch)

    def test_wrong_hash_attempt_missing_duplicate_or_unsafe_artifact_fails(self):
        for field in ('hash', 'attempt', 'missing', 'duplicate', 'filename', 'extra', 'oversize'):
            self.setUp()
            if field == 'hash': self.recorded['seal_sha256'] = '0'*64
            elif field == 'attempt': self.recorded['authority']['attempt'] = '2'
            elif field == 'missing': self.artifact_count = 0
            elif field == 'duplicate': self.artifact_count = 2
            elif field == 'filename': self.names = ['../'+p.FILENAME]
            elif field == 'extra': self.names.append('private.json')
            else: self.recorded['private'] = f.SENTINEL*10000
            with self.subTest(field=field), self.assertRaises(ValueError): p.verify(self.value, self.fetch)

    def test_duplicate_keys_unknown_private_fields_and_redacted_failures(self):
        with self.assertRaises(ValueError): json.loads('{"a":1,"a":2}', object_pairs_hook=p.unique_object)
        with self.assertRaises(ValueError): p.projection({**self.value, 'private': f.SENTINEL})
        with patch.object(p.sys, 'argv', ['provenance']), \
             patch.object(p.sys, 'stdin', io.TextIOWrapper(io.BytesIO(json.dumps(self.value).encode()))), \
             patch.object(p, 'fetch', side_effect=ValueError(f.SENTINEL)), \
             redirect_stdout(io.StringIO()) as output, redirect_stderr(io.StringIO()) as error:
            self.assertEqual(p.main(), 1)
        self.assertEqual(output.getvalue(), '')
        self.assertNotIn(f.SENTINEL, error.getvalue())

    def test_expired_digest_substitution_and_wrong_attempt_artifact_name_fail(self):
        for changes in ({'expired': True}, {'digest': 'sha256:'+'0'*64},
                        {'name': 'delivery-current-baseline-public-seal-3-2'},
                        {'size_in_bytes': 32769}, {'id': False}):
            self.artifact_changes = changes
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                p.verify(self.value, self.fetch)


def sys_executable():
    import sys
    return sys.executable


if __name__ == '__main__': unittest.main()
