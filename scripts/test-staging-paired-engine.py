import copy
import importlib.util
import json
import os
from pathlib import Path
import socketserver
import tempfile
import threading
import unittest
import subprocess
from unittest.mock import patch

SOURCE = Path(__file__).with_name('staging-paired-engine.py')
spec = importlib.util.spec_from_file_location('paired_engine', SOURCE)
e = importlib.util.module_from_spec(spec)
spec.loader.exec_module(e)


def baseline(role):
    return {'Name': e.SERVICES[role], 'Mode': {'Replicated': {'Replicas': 1}},
            'UpdateConfig': {'FailureAction': 'pause', 'Order': 'stop-first'},
            'TaskTemplate': {'ContainerSpec': {'Image': 'ghcr.io/olivium-dev/' + e.REPOSITORIES[role] + '@sha256:' + 'b' * 64, 'Env':
                ['Services__Delivery__BaseUrl=http://192.168.2.20:10055',
                 'ASPNETCORE_ENVIRONMENT=Staging', 'UNRELATED=preserved'] if role == 'gateway'
                else ['SKIP_DB_INIT=true', 'DATABASE_URL=opaque-fixture', 'DB_PASSWORD=opaque-fixture'],
                'Secrets': [{'SecretID': 'other', 'SecretName': 'unrelated', 'File': {'Name': 'other'}}]},
                'Placement': {'Constraints': ['node.role==manager']}},
            'Labels': {'unknown': 'retained'}}


class EngineTests(unittest.TestCase):
    def verify_delivery_runtime(self, image_change=None, container_change=None, wait=False):
        # API1.52 image response for the real delivery Dockerfile: CMD ./main,
        # WORKDIR /root/, no ENTRYPOINT or USER. Container inspect retains null
        # Entrypoint and empty User. Every other runtime predicate must still pass.
        sid, tid, nid, secret, cid = 's'*25, 't'*25, 'n'*25, 'x'*25, 'c'*64
        ref = 'ghcr.io/olivium-dev/delivery-service@sha256:' + 'a'*64
        before = baseline('delivery')
        proposed = e.candidate('delivery', before, ref, secret, nid)
        cs = proposed['TaskTemplate']['ContainerSpec']
        image = {'Id':'sha256:'+'b'*64, 'Config':{'Env':['PATH=/usr/bin'],
                 'Cmd':['./main'], 'WorkingDir':'/root/'}}
        current = {'ID':sid, 'Version':{'Index':2}, 'Spec':proposed,
                   'UpdateStatus':{'State':'completed'}}
        task = {'ServiceID':sid, 'Status':{'State':'running','ContainerStatus':{'ContainerID':cid}},
                'DesiredState':'running', 'Spec':{'ContainerSpec':cs}, 'NodeID':nid}
        container = {'Id':cid, 'Image':image['Id'], 'State':{'Running':True},
            'Path':'./main', 'Args':[], 'Config':{'Labels':{'com.docker.swarm.service.id':sid,
                'com.docker.swarm.task.id':tid}, 'Env':['PATH=/usr/bin']+cs['Env'],
                'Entrypoint':None, 'Cmd':['./main'], 'WorkingDir':'/root/', 'User':''}}
        if image_change: image_change(image)
        if container_change: container_change(container)
        runtime = object.__new__(e.Runtime)
        runtime.state = {'delivery':{'image':ref}, 'nodeId':nid, 'secretId':secret}
        calls = []
        def docker(*args, **kwargs):
            calls.append(args)
            if args[:2] == ('service','ps'): return tid
            if args[0] == 'inspect': return json.dumps([task])
            if args[:2] == ('container','inspect'): return json.dumps([container])
            if args[:2] == ('container','exec'):
                if 'stat' in args: return '0:0:400'
                self.assertEqual(('container','exec',cid,'wget','-q','-T','5','-O','/dev/null',
                                  'http://127.0.0.1:8080/health'), args)
                return ''
            raise AssertionError(args)
        with patch.object(runtime,'lock'), patch.object(runtime,'network'), \
             patch.object(runtime,'baseline',return_value={'ID':sid,'Version':{'Index':1}}), \
             patch.object(runtime,'captured',return_value=current), \
             patch.object(runtime,'candidate',return_value=proposed), \
             patch.object(runtime,'image',return_value=image), patch.object(e,'docker',side_effect=docker), \
             patch.object(e.time,'sleep',side_effect=AssertionError('healthy delivery must not enter retry loop')):
            if wait:
                runtime.wait_ready('delivery')
                self.assertTrue(any('wget' in call for call in calls))
            else:
                self.assertEqual(cid, runtime.verify('delivery'))

    def test_api152_command_only_delivery_passes_full_runtime_readiness(self):
        self.verify_delivery_runtime(wait=True)
        for entrypoint in (None, []):
            with self.subTest(entrypoint=entrypoint):
                self.verify_delivery_runtime(
                    image_change=lambda image: image['Config'].update(Entrypoint=entrypoint))

    def test_command_only_delivery_rejects_changed_process_or_working_directory(self):
        for key, value in (('Cmd',['/bin/sh']), ('Entrypoint',['/bin/sh']),
                           ('Entrypoint',''), ('Cmd',False), ('WorkingDir','/tmp')):
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                self.verify_delivery_runtime(container_change=lambda c: c['Config'].update({key:value}))
        for key, value in (('Path','/bin/sh'), ('Args',['--unexpected'])):
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.verify_delivery_runtime(container_change=lambda c: c.update({key:value}))

    def test_gateway_explicit_entrypoint_keeps_exact_process_identity(self):
        image = {'Entrypoint':['dotnet','JeebGateway.dll'], 'WorkingDir':'/app'}
        container = {'Config':{**image,'Cmd':None}, 'Path':'dotnet', 'Args':['JeebGateway.dll']}
        e.verify_container_process(image, container)
        for mutate in (lambda c: c['Config'].update(Entrypoint=['dotnet','other.dll']),
                       lambda c: c['Config'].update(Cmd=['--unexpected']),
                       lambda c: c.update(Args=['other.dll'])):
            changed = copy.deepcopy(container)
            mutate(changed)
            with self.assertRaises(ValueError): e.verify_container_process(image, changed)

    def test_gateway_deployed_environment_is_preserved(self):
        image = 'ghcr.io/olivium-dev/jeeb-gateway@sha256:' + 'a' * 64
        for environment in ('Staging', 'Production', None):
            with self.subTest(environment=environment):
                before = baseline('gateway')
                c = before['TaskTemplate']['ContainerSpec']
                c['Env'] = [row for row in c['Env'] if not row.startswith('ASPNETCORE_ENVIRONMENT=')]
                if environment is not None:
                    c['Env'].append('ASPNETCORE_ENVIRONMENT=' + environment)
                after = e.candidate('gateway', before, image, 's'*25, 'n'*25)
                self.assertEqual(c['Env'] + ['DELIVERY_SERVICE_TOKEN_FILE=' + e.TOKEN_PATH],
                                 after['TaskTemplate']['ContainerSpec']['Env'])

    def test_gateway_development_testing_unknown_and_environment_aliases_rejected(self):
        image = 'ghcr.io/olivium-dev/jeeb-gateway@sha256:' + 'a' * 64
        for key in ('ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT'):
            for environment in ('Development', 'Testing', 'Preview', '', 'staging'):
                with self.subTest(key=key, environment=environment):
                    before = baseline('gateway')
                    c = before['TaskTemplate']['ContainerSpec']
                    c['Env'] = [row for row in c['Env'] if not row.startswith(key + '=')]
                    c['Env'].append(key + '=' + environment)
                    with self.assertRaises(ValueError):
                        e.candidate('gateway', before, image, 's'*25, 'n'*25)
        for key in ('aspnetcore_environment', 'dotnet_environment'):
            before = baseline('gateway')
            before['TaskTemplate']['ContainerSpec']['Env'].append(key + '=Development')
            with self.assertRaises(ValueError):
                e.candidate('gateway', before, image, 's'*25, 'n'*25)

    def test_image_auth_only_delta_and_all_unrelated_fields_preserved(self):
        for role in e.SERVICES:
            before = baseline(role)
            image = 'ghcr.io/olivium-dev/' + e.REPOSITORIES[role] + '@sha256:' + 'a' * 64
            after = e.candidate(role, before, image, 's' * 25, 'n'*25)
            self.assertEqual('node.id=='+'n'*25, after['TaskTemplate']['Placement']['Constraints'].pop())
            after['TaskTemplate']['ContainerSpec']['Image'] = before['TaskTemplate']['ContainerSpec']['Image']
            after['TaskTemplate']['ContainerSpec']['Env'] = [x for x in after['TaskTemplate']['ContainerSpec']['Env']
                if not x.startswith(('DELIVERY_SERVICE_TOKEN_FILE=', 'DELIVERY_SERVICE_AUTH_MODE='))]
            after['TaskTemplate']['ContainerSpec']['Secrets'].pop()
            self.assertEqual(before, after)

    def test_alias_duplicate_mount_wrong_base_and_active_state_rejected(self):
        image = 'ghcr.io/olivium-dev/jeeb-gateway@sha256:' + 'a' * 64
        mutations = [lambda c: c['Env'].append('delivery_service_token=forbidden'),
            lambda c: c['Env'].append('DELIVERY_SERVICE_TOKEN_FILE=' + e.TOKEN_PATH),
            lambda c: c['Env'].append('Services__Delivery__ServiceTokenFile=/other'),
            lambda c: c['Env'].append(c['Env'][0]),
            lambda c: c.update(Mounts=[{'Target': '/run'}]),
            lambda c: c['Env'].__setitem__(0, 'Services__Delivery__BaseUrl=http://elsewhere'),
            lambda c: c['Secrets'].append({'SecretName': e.custody.SECRET_NAME})]
        for mutate in mutations:
            value = baseline('gateway')
            mutate(value['TaskTemplate']['ContainerSpec'])
            with self.assertRaises(ValueError): e.candidate('gateway', value, image, 's'*25, 'n'*25)

    def test_secret_metadata_exact_not_similar_or_replaced(self):
        secret = {'ID': 's'*25, 'Spec': {'Name': e.custody.SECRET_NAME, 'Labels':
            {'jeeb.environment': 'staging', 'jeeb.purpose': 'delivery-service-auth', 'jeeb.version': '1'}}}
        e.secret_metadata(secret, 's'*25)
        for mutated in ({**secret, 'ID': 'r'*25}, {**secret, 'Spec': {**secret['Spec'], 'Name': 'other'}}):
            with self.assertRaises(ValueError): e.secret_metadata(mutated, 's'*25)

    def test_actual_unix_http_single_post_conflict_lost_and_exact_body(self):
        for status in (200, 409, 500, 0):
            with self.subTest(status=status), tempfile.TemporaryDirectory() as directory:
                calls = []
                class Handler(socketserver.StreamRequestHandler):
                    def handle(self):
                        first = self.rfile.readline().decode().strip()
                        length = 0
                        while True:
                            line = self.rfile.readline().decode()
                            if line == '\r\n': break
                            if line.lower().startswith('content-length:'): length = int(line.split(':', 1)[1])
                        body = self.rfile.read(length)
                        calls.append((first, json.loads(body)))
                        if status:
                            self.wfile.write(f'HTTP/1.1 {status} Status\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{}}'.encode())
                path = str(Path(directory)/'engine.sock')
                server = socketserver.UnixStreamServer(path, Handler)
                thread = threading.Thread(target=server.serve_forever, daemon=True)
                thread.start()
                try:
                    with patch.object(e, 'SOCKET', path):
                        if status == 200: self.assertEqual({}, e.request('POST', '/v1.52/services/'+'s'*25+'/update?version=7', {'exact': True}))
                        else:
                            with self.assertRaises(Exception): e.request('POST', '/v1.52/services/'+'s'*25+'/update?version=7', {'exact': True})
                    self.assertEqual(1, len(calls))
                    self.assertEqual({'exact': True}, calls[0][1])
                finally:
                    server.shutdown()
                    server.server_close()
                    thread.join()

    def test_nonfixed_engine_write_routes_never_open_socket(self):
        for method, path in [('DELETE', '/v1.52/services/'+'s'*25), ('POST', '/v1.52/services/create'),
                             ('POST', '/v1.52/services/'+'s'*25+'/update?version=0')]:
            with self.assertRaises(ValueError): e.request(method, path, {})

    def test_submit_burns_post_claim_and_cannot_repeat_after_lost_ack(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runtime = object.__new__(e.Runtime)
            runtime.root = root
            runtime.state = {'secretId': 's'*25}
            class Journal:
                path = root / 'journal'
                claims = root / 'claims'
            runtime.journal = Journal()
            Journal.path.mkdir(mode=0o700); Journal.claims.mkdir(mode=0o700)
            e.custody.write_exclusive(Journal.path/'01-gateway-submission-pending.json',
                {'phase': 'gateway-submission-pending', 'secretId': 's'*25})
            before = {'ID': 'g'*25, 'Version': {'Index': 7}, 'Spec': baseline('gateway')}
            calls = []
            def lost(*args):
                calls.append(args)
                raise ConnectionError()
            with patch.object(runtime, 'authority'), patch.object(runtime, 'existing_secret'), \
                 patch.object(runtime, 'baseline', return_value=before), patch.object(runtime, 'captured', return_value=before), \
                 patch.object(runtime, 'network'), patch.object(runtime, 'lock'), \
                 patch.object(runtime, 'candidate', return_value={'exact': True}), patch.object(e, 'request', side_effect=lost):
                with self.assertRaises(ConnectionError): runtime.submit('gateway', 'g'*25, '7', root/'gateway-candidate.json')
                with self.assertRaises(FileExistsError): runtime.submit('gateway', 'g'*25, '7', root/'gateway-candidate.json')
                self.assertEqual(1, len(calls))
                self.assertTrue((Journal.claims/(e.custody.SECRET_NAME+'-gateway-post.json')).exists())

    def test_actual_runner_source_guards_precede_first_ssh_positive_and_negatives(self):
        source = SOURCE.with_name('run-staging-paired-activation.sh')
        defaults = {'GITHUB_REPOSITORY': 'olivium-dev/jeeb-gateway', 'GITHUB_ACTOR': 'oudaykhaled',
            'GITHUB_TRIGGERING_ACTOR': 'oudaykhaled', 'GITHUB_EVENT_NAME': 'workflow_dispatch',
            'GITHUB_REF': 'refs/heads/main', 'GITHUB_REF_PROTECTED': 'true', 'GITHUB_SHA': 'a'*40,
            'GITHUB_RUN_ID': '123', 'GITHUB_RUN_ATTEMPT': '1', 'GH_TOKEN': 'fixture'}
        cases = [({}, 1)] + [({key: value}, 0) for key, value in (
            ('GITHUB_REPOSITORY','other/repo'), ('GITHUB_ACTOR','other'), ('GITHUB_TRIGGERING_ACTOR','other'),
            ('GITHUB_EVENT_NAME','push'), ('GITHUB_REF','refs/heads/other'), ('GITHUB_REF_PROTECTED','false'),
            ('GITHUB_SHA','b'*40), ('FIXTURE_GH_RESULT','false'))]
        for changes, expected in cases:
            with self.subTest(changes=changes), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                for name, body in {'git': 'echo '+ 'a'*40, 'gh': 'echo "${FIXTURE_GH_RESULT:-true}"',
                                   'ssh': 'echo ssh >> "$FIXTURE_LOG"; exit 1'}.items():
                    path = root/name
                    path.write_text('#!/usr/bin/env bash\n'+body+'\n'); path.chmod(0o700)
                env = {**os.environ, **defaults, **changes, 'PATH': str(root)+':'+os.environ['PATH'], 'FIXTURE_LOG': str(root/'events')}
                result = subprocess.run(['bash', str(source)], capture_output=True, text=True, env=env, timeout=20)
                self.assertNotEqual(0, result.returncode)
                events = (root/'events').read_text().splitlines() if (root/'events').exists() else []
                self.assertEqual(expected, len(events), result.stderr)

    def test_fixed_secret_provision_intent_existing_reuse_and_lost_ack_no_retry(self):
        for outcome in ('success', 'lost-visible', 'lost-absent', 'existing'):
            with self.subTest(outcome=outcome), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                runtime = object.__new__(e.Runtime)
                runtime.state = {'secretId': '', 'daemonId': 'daemon', 'nodeId': 'n'*25,
                    'gateway': {'sourceCommit': 'a'*40, 'sourceTree': 'b'*40, 'runId': '123', 'attempt': '1'}}
                class Journal: pass
                runtime.journal = Journal(); runtime.journal.root = root
                labels = {'jeeb.environment':'staging','jeeb.purpose':'delivery-service-auth','jeeb.version':'1'}
                visible = [outcome == 'existing']
                posts = []
                def request(method, path, body=None):
                    if method == 'GET':
                        self.assertEqual('/v1.52/secrets/'+e.custody.SECRET_NAME, path)
                        if not visible[0]: raise e.EngineStatus(404)
                        return {'ID':'s'*25,'Spec':{'Name':e.custody.SECRET_NAME,'Labels':labels}}
                    self.assertEqual('/v1.52/secrets/create', path)
                    self.assertTrue((root/(e.custody.SECRET_NAME+'-provision')/'00-intent.json').exists())
                    self.assertEqual({'Name','Labels','Data'}, set(body))
                    self.assertEqual(labels, body['Labels'])
                    posts.append(body['Data'])
                    visible[0] = outcome != 'lost-absent'
                    if outcome.startswith('lost'): raise ConnectionError()
                    return {'ID':'s'*25}
                with patch.object(runtime, 'authority'), patch.object(runtime, 'lock'), patch.object(e, 'request', side_effect=request):
                    if outcome == 'lost-absent':
                        with self.assertRaises(ValueError): runtime.provision_secret()
                        with self.assertRaises(FileExistsError): runtime.provision_secret()
                    else:
                        self.assertEqual('s'*25, runtime.provision_secret())
                        self.assertEqual('s'*25, runtime.provision_secret())
                self.assertEqual(0 if outcome == 'existing' else 1, len(posts))
                for path in root.rglob('*.json'):
                    self.assertFalse(any(value in path.read_text() for value in posts))

    def test_network_preflight_rejects_conflicting_preserved_placement(self):
        runtime = object.__new__(e.Runtime)
        runtime.state = {'nodeId': 'n'*25, 'receipt': {'networkId': 'w'*25, 'networkVersion': None}}
        network = {'Id': 'w'*25, 'Name': 'jeeb-staging-net', 'Driver': 'overlay', 'Attachable': True, 'Options': {'encrypted': ''}}
        def document(role, constraint):
            value = baseline(role)
            value['TaskTemplate']['Networks'] = [{'Target': 'w'*25}]
            value['TaskTemplate']['Placement']['Constraints'].append(constraint)
            value['EndpointSpec'] = {'Ports': [{'Protocol':'tcp','TargetPort':8080,
                'PublishedPort':10000 if role == 'gateway' else 10055,'PublishMode':'ingress' if role == 'gateway' else 'host'}]}
            return {'Spec':value}
        for constraint in ('node.id=='+'x'*25, 'node.id!='+'n'*25, 'node.hostname==another', 'node.role==worker'):
            with self.subTest(constraint=constraint), patch.object(e, 'request', return_value=network), \
                 patch.object(runtime, 'baseline', side_effect=lambda role: document(role, constraint)):
                with self.assertRaises(ValueError): runtime.network()
        with patch.object(e,'request',return_value=network), patch.object(runtime,'baseline',side_effect=lambda role:document(role,'node.id=='+'n'*25)):
            self.assertEqual(network, runtime.network())

    def test_exact_engine_api_identity_and_no_unverified_cache_fastpath(self):
        runtime = object.__new__(e.Runtime)
        runtime.state = {'daemonId':'fixture-daemon','nodeId':'n'*25}
        version = {'MinAPIVersion':'1.44','ApiVersion':'1.52','Version':'fixture-version','GitCommit':'fixture-commit'}
        info = {'ID':'fixture-daemon','Name':'olivium-ephemerals','Swarm':
                {'NodeID':'n'*25,'NodeAddr':'192.168.2.20','ControlAvailable':True,'LocalNodeState':'active'}}
        def check(v=version, i=info, allow=True):
            def get(method,path,body=None):
                self.assertEqual('GET',method)
                return v if path == '/version' else i
            with patch.object(runtime,'lock'), patch.object(e.os,'lstat') as inode, patch.object(e,'request',side_effect=get), \
                 patch.object(e,'VALIDATED_CACHE_FASTPATH_ENGINES',{('fixture-version','fixture-commit')} if allow else set()):
                inode.return_value.st_mode = e.stat.S_IFSOCK
                runtime.daemon()
        check()
        for field, value in (('MinAPIVersion','1.53'),('ApiVersion','1.51'),('GitCommit','other')):
            with self.assertRaises(ValueError): check(v={**version,field:value})
        with self.assertRaises(ValueError): check(allow=False)
        with self.assertRaises(ValueError): check(i={**info,'Name':'other'})
        with self.assertRaises(ValueError): check(i={**info,'Swarm':{**info['Swarm'],'NodeID':'x'*25}})


if __name__ == '__main__': unittest.main()
