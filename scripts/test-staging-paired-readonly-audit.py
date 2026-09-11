#!/usr/bin/env python3
"""Offline read-only/redaction boundaries; no live daemon, SSH or credentials."""
import ast
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import tempfile
import unittest
from contextlib import redirect_stdout
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT/'scripts'/filename)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


a = load('readonly_audit', 'staging-paired-readonly-audit.py')
policy = load('paired_policy', 'check-staging-paired-activation.py')
SENTINEL = 'private-value-never-emit-fixture'
NODE = 'n'*25
SECRET = 's'*25


def metadata():
    value = {'secretId': SECRET, 'receiptNonce': 'c'*64, 'gatewayBuildRun': '1', 'gatewayBuildAttempt': '1'}
    for role in a.SERVICES:
        value.update({role+'Source': 'a'*40, role+'Tree': 'b'*40, role+'Run': '2', role+'Attempt': '1',
                      role+'Image': 'ghcr.io/olivium-dev/'+a.REPOSITORIES[role]+'@sha256:'+'d'*64,
                      role+'ServiceId': ('g' if role == 'gateway' else 'd')*25, role+'Version': 7})
    return value


def private_fixture(role):
    meta = metadata()
    uid = '65532' if role == 'gateway' else '0'
    env = ['UNRELATED='+SENTINEL]
    env += ['Services__Delivery__BaseUrl=http://192.168.2.20:10055', 'ASPNETCORE_ENVIRONMENT=Staging'] if role == 'gateway' else ['SKIP_DB_INIT=true']
    old = {'Name': a.SERVICES[role], 'Mode': {'Replicated': {'Replicas': 1}}, 'UpdateConfig': {'FailureAction': 'pause'},
           'TaskTemplate': {'ContainerSpec': {'Image': 'ghcr.io/olivium-dev/'+a.REPOSITORIES[role]+'@sha256:'+'e'*64,
                                            'Env': env, 'Secrets': []}, 'Placement': {'Constraints': []}},
           'Labels': {'unknown': SENTINEL}}
    current = copy.deepcopy(old)
    declaration = current['TaskTemplate']['ContainerSpec']
    declaration['Image'] = meta[role+'Image']
    declaration['Env'].append('DELIVERY_SERVICE_TOKEN_FILE='+a.TOKEN_PATH)
    if role == 'delivery': declaration['Env'].append('DELIVERY_SERVICE_AUTH_MODE=required')
    declaration['Secrets'] = [{'SecretID': SECRET, 'SecretName': a.SECRET_NAME,
                               'File': {'Name': 'delivery_service_token', 'UID': uid, 'GID': uid, 'Mode': 256}}]
    current['TaskTemplate']['Placement']['Constraints'].append('node.id=='+NODE)
    service = {'ID': meta[role+'ServiceId'], 'Version': {'Index': 8}, 'Spec': current,
               'PreviousSpec': old, 'UpdateStatus': {'State': 'completed'}}
    config = {'Cmd': [], 'WorkingDir': '/app', 'Env': ['IMAGE_DEFAULT='+SENTINEL],
              'User': 'appuser' if role == 'gateway' else 'root',
              'Labels': {'org.opencontainers.image.revision': meta[role+'Source'], 'jeeb.source.tree': meta[role+'Tree']}}
    if role == 'gateway': config['Entrypoint'] = ['dotnet', 'JeebGateway.dll']
    else: config['Cmd'] = ['/app/delivery-service']
    image = {'Id': 'sha256:'+'f'*64, 'Config': config, 'RepoDigests': [meta[role+'Image']]}
    task = {'ID': 't'*25, 'ServiceID': service['ID'], 'NodeID': NODE, 'DesiredState': 'running',
            'Spec': {'ContainerSpec': copy.deepcopy(declaration)},
            'Status': {'State': 'running', 'ContainerStatus': {'ContainerID': '1'*64}}}
    actual = copy.deepcopy(config)
    actual['Entrypoint'] = config.get('Entrypoint')
    actual['Env'] += declaration['Env']
    actual['Labels'].update({'com.docker.swarm.service.id': service['ID'], 'com.docker.swarm.task.id': task['ID']})
    argv = (config.get('Entrypoint') or []) + config['Cmd']
    container = {'Id': '1'*64, 'Image': image['Id'], 'Config': actual, 'Path': argv[0], 'Args': argv[1:], 'State': {'Running': True}}
    return {'service': service, 'image': image, 'tasks': [task], 'container': container}


def secret():
    return {'ID': SECRET, 'Spec': {'Name': a.SECRET_NAME, 'Labels': {
        'jeeb.environment': 'staging', 'jeeb.purpose': 'delivery-service-auth', 'jeeb.version': '1'}}}


class AuditTests(unittest.TestCase):
    def setUp(self):
        # Use the test user's home, whose ancestors have the same safe boundary
        # as deployed home; /tmp is deliberately unsafe. The checkout stays read-only.
        self.temp = tempfile.TemporaryDirectory(prefix='.paired-audit-test-', dir=Path.home())
        self.addCleanup(self.temp.cleanup)
        self.home = Path(self.temp.name)
        self.journal = self.home/'.jeeb-deploy'/'paired-releases'/a.SECRET_NAME
        for folder in (self.home/'.jeeb-deploy', self.home/'.jeeb-deploy'/'paired-releases', self.journal):
            folder.mkdir(mode=0o700)

    def phases(self, count=4):
        for i, phase in enumerate(a.PHASES[:count]):
            path = self.journal/f'{i:02d}-{phase}.json'
            path.write_text(json.dumps({'phase': phase, **metadata()}))
            path.chmod(0o400)

    def test_incomplete_phase_three_reads_without_any_write(self):
        self.phases()
        original = os.open
        flags_seen = []
        def readonly_open(path, flags, *args, **kwargs):
            flags_seen.append(flags)
            self.assertEqual(flags & os.O_ACCMODE, os.O_RDONLY)
            self.assertFalse(flags & (os.O_CREAT | os.O_TRUNC | os.O_APPEND))
            return original(path, flags, *args, **kwargs)
        with patch.object(a.os, 'open', side_effect=readonly_open):
            report, private = a.read_journal(self.home)
        self.assertTrue(flags_seen)
        self.assertEqual(report['state'], 'incomplete')
        self.assertEqual(report['phases'], list(a.PHASES[:4]))
        self.assertTrue(report['metadata_consistent'])
        self.assertEqual(private, metadata())

    def test_absent_journal_does_not_create_directories(self):
        self.journal.rmdir()
        report, private = a.read_journal(self.home)
        self.assertEqual(report['state'], 'absent')
        self.assertIsNone(private)
        self.assertFalse(self.journal.exists())

    def test_unsafe_or_inconsistent_journal_is_not_trusted(self):
        for mutation in ('symlink', 'hardlink', 'mode', 'oversize', 'inconsistent', 'duplicate', 'nonprefix', 'unknown'):
            with self.subTest(mutation=mutation):
                for path in self.journal.iterdir(): path.unlink()
                self.phases(2)
                path = self.journal/'01-gateway-submission-pending.json'
                if mutation == 'symlink':
                    path.unlink(); path.symlink_to(self.journal/'00-prepared.json')
                elif mutation == 'hardlink':
                    path.unlink(); os.link(self.journal/'00-prepared.json', path)
                elif mutation == 'mode': path.chmod(0o600)
                elif mutation == 'oversize': path.chmod(0o600); path.write_text('x'*32769); path.chmod(0o400)
                elif mutation == 'inconsistent':
                    path.chmod(0o600); path.write_text(json.dumps({'phase': a.PHASES[1], **metadata(), 'gatewayRun': '3'})); path.chmod(0o400)
                elif mutation == 'duplicate':
                    path.chmod(0o600); path.write_text('{"phase":"prepared","phase":"prepared"}'); path.chmod(0o400)
                elif mutation == 'nonprefix': (self.journal/'00-prepared.json').unlink()
                else: (self.journal/SENTINEL).write_text(SENTINEL)
                report, private = a.read_journal(self.home)
                self.assertIsNone(private)
                self.assertFalse(report['metadata_consistent'])
                self.assertNotIn(SENTINEL, json.dumps(report))

    def test_api152_omitted_entrypoint_and_command_metadata(self):
        for role in a.SERVICES:
            report = a.inspect_role(role, private_fixture(role), metadata(), secret(), NODE)
            self.assertTrue(report['inspected'])
            for field in ('normalized_entrypoint_matches', 'normalized_cmd_matches', 'normalized_working_directory_matches',
                          'effective_path_args_match', 'environment_matches_spec_and_image', 'task_secret_mount_matches',
                          'previous_auth_inactive', 'previous_candidate_matches_current', 'container_identity_matches'):
                self.assertTrue(report[field], (role, field))
            self.assertEqual(report['image_entrypoint_key_present'], role == 'gateway')
            self.assertNotIn(SENTINEL, json.dumps(report))

    def test_mismatched_runtime_and_previous_spec_are_reported_without_values(self):
        snapshot = private_fixture('delivery')
        snapshot['container']['Config']['Cmd'] = [SENTINEL]
        snapshot['container']['Args'] = [SENTINEL]
        snapshot['service']['UpdateStatus']['State'] = SENTINEL
        snapshot['service']['PreviousSpec']['Labels']['changed'] = SENTINEL
        report = a.inspect_role('delivery', snapshot, metadata(), secret(), NODE)
        self.assertFalse(report['normalized_cmd_matches'])
        self.assertFalse(report['effective_path_args_match'])
        self.assertFalse(report['previous_candidate_matches_current'])
        self.assertEqual(report['update_state'], 'unknown')
        self.assertNotIn(SENTINEL, json.dumps(report))

    def test_engine_get_rejects_other_routes_before_connecting(self):
        forbidden = ('/v1.52/services/create', '/v1.52/secrets/create', '/v1.52/containers/json',
                     '/v1.52/tasks', '/v1.52/services/another-service', '/v1.52/containers/'+'1'*64+'/exec',
                     '/v1.51/info', '/v1.52/images/ghcr.io/other/repo@sha256:'+'a'*64+'/json')
        with patch.object(a, 'LocalEngine') as engine:
            for path in forbidden:
                with self.assertRaises(ValueError): a.get(path)
            engine.assert_not_called()
        with patch.object(a, 'LocalEngine') as engine:
            response = engine.return_value.getresponse.return_value
            response.status = 200
            response.read.return_value = b'{}'
            a.get('/version')
            engine.return_value.request.assert_called_once_with('GET', '/version')

    def test_no_private_error_or_context_reaches_entrypoint(self):
        with patch.object(a, 'collect', side_effect=ValueError(SENTINEL)), patch.object(a.sys, 'argv', ['audit']), redirect_stdout(io.StringIO()) as output:
            self.assertEqual(a.main(), 1)
        self.assertNotIn(SENTINEL, output.getvalue())
        self.assertTrue(json.loads(output.getvalue())['inspection_failed'])

    def test_static_readonly_contract_rejects_mutation_and_process_execution(self):
        source = (ROOT/'scripts'/'staging-paired-readonly-audit.py').read_text()
        policy.check_readonly_audit(source)
        for mutation in (source.replace("connection.request('GET', path)", "connection.request('POST', path)"),
                         source+'\nos.open("bad", os.O_WRONLY | os.O_CREAT)\n',
                         source+'\nimport subprocess\nsubprocess.run(["docker", "exec"])\n',
                         source+'\nopen("bad", "w")\n', source+'\nos.unlink("journal")\n'):
            with self.assertRaises(AssertionError): policy.check_readonly_audit(mutation)

    def test_workflow_has_fixed_streaming_readonly_authority(self):
        import yaml
        workflow = yaml.safe_load((ROOT/'.github/workflows/jeeb-staging-paired-audit.yml').read_text())
        self.assertEqual(workflow['permissions'], {'contents': 'read', 'actions': 'read'})
        self.assertEqual(set(workflow['on']), {'workflow_dispatch'})
        self.assertEqual(set(workflow['on']['workflow_dispatch']['inputs']), {'reviewed_sha'})
        job = workflow['jobs']['inspect']
        self.assertEqual(job['environment'], 'staging')
        self.assertEqual(job['timeout-minutes'], 5)
        self.assertIn('[ "$GITHUB_ACTOR" = oudaykhaled ]', job['steps'][0]['run'])
        self.assertIn('[ "$GITHUB_TRIGGERING_ACTOR" = oudaykhaled ]', job['steps'][0]['run'])
        self.assertIn('[ "$REVIEWED_SHA" = "$GITHUB_SHA" ]', job['steps'][2]['run'])
        last = job['steps'][-1]['run']
        self.assertEqual(last.count('ssh '), 1)
        self.assertIn('ssh jeeb-staging python3 -I - < scripts/staging-paired-readonly-audit.py', last)
        self.assertEqual(last.count('bash scripts/staging-paired-source-guard.sh'), 2)


if __name__ == '__main__': unittest.main()
