#!/usr/bin/env python3
"""Fixed-role staging Engine adapters. All command output is private by default."""
import copy
import base64
import hashlib
import http.client
import importlib.util
import json
import os
from pathlib import Path
import re
import secrets
import socket
import stat
import subprocess
import sys
import time
from urllib.parse import unquote, urlsplit

sys.dont_write_bytecode = True
module = importlib.util.spec_from_file_location('custody', Path(__file__).with_name('staging-paired-custody.py'))
custody = importlib.util.module_from_spec(module)
module.loader.exec_module(custody)
require = custody.require
SOCKET = '/var/run/docker.sock'
SERVICES = {'gateway': 'jeeb-staging-jeeb-gateway', 'delivery': 'jeeb-staging-delivery-service'}
REPOSITORIES = {'gateway': 'jeeb-gateway', 'delivery': 'delivery-service'}
TOKEN_PATH = '/run/secrets/delivery_service_token'
# Exact installed Ubuntu build, verified against its original source and all
# downstream patches; see the runbook evidence. Never accept failed-pull/cache
# fallback or infer another release has this native digest-present fast path.
VALIDATED_CACHE_FASTPATH_ENGINES = frozenset({('29.1.3', '29.1.3-0ubuntu3~24.04.2')})


class EngineStatus(Exception):
    def __init__(self, status):
        self.status = status


class Engine(http.client.HTTPConnection):
    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect(SOCKET)


def request(method, path, body=None):
    # Writes are only fixed-role captured-version CAS and dedicated provisioning.
    require(method == 'GET' or (method == 'POST' and (path == '/v1.52/secrets/create'
            or re.fullmatch(r'/v1\.52/services/[a-z0-9]{25}/update\?version=[1-9][0-9]*', path))))
    connection = Engine('localhost', timeout=30)
    try:
        connection.request(method, path, body=None if body is None else json.dumps(body).encode(),
                           headers={'Content-Type': 'application/json'})
        response = connection.getresponse()
        raw = response.read(1024 * 1024 + 1)
        require(len(raw) <= 1024 * 1024)
        expected_status = 201 if method == 'POST' and path == '/v1.52/secrets/create' else 200
        if response.status != expected_status:
            raise EngineStatus(response.status)
        result = json.loads(raw, object_pairs_hook=custody.unique_object)
        require(not isinstance(result, dict) or not result.get('Warnings'))
        return result
    finally:
        connection.close()


def docker(*args, stdin=None, timeout=30):
    env = dict(os.environ)
    for key in ('DOCKER_HOST', 'DOCKER_CONTEXT', 'DOCKER_TLS_VERIFY', 'DOCKER_CERT_PATH', 'DOCKER_API_VERSION'):
        env.pop(key, None)
    return subprocess.run(['docker', '--host', 'unix://' + SOCKET, *args], input=stdin,
                          capture_output=True, text=True, check=True, timeout=timeout, env=env).stdout


def env_map(rows):
    result = {}
    for row in rows:
        key, separator, value = row.partition('=')
        require(separator and key and key not in result)
        result[key] = value
    return result


def secret_metadata(secret, expected_id):
    require(secret['ID'] == expected_id and re.fullmatch(r'[a-z0-9]{25}', expected_id))
    require(secret['Spec']['Name'] == custody.SECRET_NAME)
    require(secret['Spec'].get('Labels', {}) == {'jeeb.environment': 'staging',
            'jeeb.purpose': 'delivery-service-auth', 'jeeb.version': '1'})


def candidate(role, baseline, image, secret_id, node_id):
    require(re.fullmatch(r'[a-z0-9]{25}', secret_id))
    require(re.fullmatch(r'[a-z0-9]{25}', node_id))
    require(role in SERVICES and baseline['Name'] == SERVICES[role])
    require(baseline['Mode'] == {'Replicated': {'Replicas': 1}})
    require(baseline.get('UpdateConfig', {}).get('FailureAction') == 'pause')
    require(re.fullmatch(r'ghcr\.io/olivium-dev/' + REPOSITORIES[role] + r'@sha256:[0-9a-f]{64}', image))
    container = baseline['TaskTemplate']['ContainerSpec']
    require(re.fullmatch(r'ghcr\.io/olivium-dev/' + REPOSITORIES[role] + r'@sha256:[0-9a-f]{64}', container['Image']))
    require(not container.get('Command') and not container.get('Args') and not container.get('Dir'))
    require(not container.get('Mounts') and not container.get('Configs'))
    require(container.get('User', '') in (('', 'appuser', '65532', '65532:65532') if role == 'gateway'
                                         else ('', '0', '0:0', 'root')))
    values = env_map(container.get('Env', []))
    require(not any(key in values for key in ('PATH', 'LD_PRELOAD', 'LD_LIBRARY_PATH', 'DOTNET_ROOT',
                'DOTNET_STARTUP_HOOKS', 'ASPNETCORE_HOSTINGSTARTUPASSEMBLIES')))
    auth_aliases = {'delivery_service_token', 'delivery_service_token_file', 'delivery_service_auth_mode',
                    'services:delivery:servicetoken', 'services:delivery:servicetokenfile'}
    require(not any(k.lower().replace('__', ':') in auth_aliases for k in values))
    for mount in container.get('Secrets', []) + container.get('Configs', []):
        require(mount.get('SecretName') != custody.SECRET_NAME and
                mount.get('File', {}).get('Name') not in ('delivery_service_token', TOKEN_PATH))
    require(not any(m.get('Target') in ('/run', '/run/secrets', TOKEN_PATH) for m in container.get('Mounts', [])))
    if role == 'gateway':
        require(values.get('Services__Delivery__BaseUrl') == 'http://192.168.2.20:10055')
        # The protected staging publisher declares Staging. Both deployed
        # environments enforce the same mounted-only delivery credential loader;
        # Development/Testing must never enter this activation route. Preserve
        # the incumbent environment, including the legacy Production default.
        environment_keys = {'ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT'}
        require(not any(k.upper() in environment_keys and k not in environment_keys for k in values))
        require(values.get('ASPNETCORE_ENVIRONMENT', 'Production') in ('Staging', 'Production'))
        require(values.get('DOTNET_ENVIRONMENT', 'Production') in ('Staging', 'Production'))
        require(not any(k.lower().replace('__', ':') == 'services:delivery:baseurl' and k != 'Services__Delivery__BaseUrl' for k in values))
    else:
        require(values.get('SKIP_DB_INIT') == 'true')
        require(not container.get('Mounts'))
    result = copy.deepcopy(baseline)
    c = result['TaskTemplate']['ContainerSpec']
    c['Image'] = image
    c.setdefault('Env', []).append('DELIVERY_SERVICE_TOKEN_FILE=' + TOKEN_PATH)
    if role == 'delivery':
        c['Env'].append('DELIVERY_SERVICE_AUTH_MODE=required')
    uid = '65532' if role == 'gateway' else '0'
    c.setdefault('Secrets', []).append({'SecretID': secret_id, 'SecretName': custody.SECRET_NAME,
        'File': {'Name': 'delivery_service_token', 'UID': uid, 'GID': uid, 'Mode': 256}})
    constraints = result['TaskTemplate'].setdefault('Placement', {}).setdefault('Constraints', [])
    if 'node.id==' + node_id not in [value.replace(' ', '') for value in constraints]:
        constraints.append('node.id==' + node_id)
    # Nothing else changes: DSN, unrelated secrets/import bearer, networking,
    # rollout/resources/health/labels and all unknown fields remain byte values.
    return result


def exact_spec(spec):
    # Match existing Engine canonicalization: only registry auth is transport
    # metadata, never publish or compare its secret material.
    result = copy.deepcopy(spec)
    c = result['TaskTemplate']['ContainerSpec']
    c.get('PullOptions', {}).pop('RegistryAuth', None)
    if not c.get('PullOptions'):
        c.pop('PullOptions', None)
    return result


class Runtime:
    def __init__(self, root):
        self.root = custody.directory(Path(root))
        self.state = custody.read_private(self.root / 'runtime.json', 0o600)
        self.home = Path.home()
        self.journal = custody.Journal(self.home)

    def lock(self):
        custody.assert_shared_lock(self.home, self.state['lockOwner'])

    def daemon(self):
        self.lock()
        require(stat.S_ISSOCK(os.lstat(SOCKET).st_mode))
        version = request('GET', '/version')
        def api(value):
            require(re.fullmatch(r'[0-9]+\.[0-9]+', value))
            return tuple(map(int, value.split('.')))
        require(api(version['MinAPIVersion']) <= (1, 52) <= api(version['ApiVersion']))
        require((version['Version'], version['GitCommit']) in VALIDATED_CACHE_FASTPATH_ENGINES)
        info = request('GET', '/v1.52/info')
        require(info['Name'] == 'olivium-ephemerals' and info['ID'] == self.state['daemonId'])
        require(info['Swarm']['ControlAvailable'] is True and info['Swarm']['NodeAddr'] == '192.168.2.20'
                and info['Swarm']['LocalNodeState'] == 'active')
        require(info['Swarm']['NodeID'] == self.state['nodeId'])

    def image(self, role):
        image = self.state[role]['image']
        require(re.fullmatch(r'ghcr\.io/olivium-dev/' + REPOSITORIES[role] + r'@sha256:[0-9a-f]{64}', image))
        return request('GET', '/v1.52/images/' + image + '/json')

    def authority(self):
        self.daemon()
        custody.directory(self.home / '.jeeb-deploy/paired-releases/delivery')
        receipt = custody.read_private(self.home / '.jeeb-deploy/paired-releases/delivery' /
            (self.state['delivery']['runId'] + '-' + self.state['delivery']['attempt'] + '.json'), 0o400)
        delivery_image = self.image('delivery')
        custody.validate_receipt(receipt, self.state['delivery'], delivery_image, self.state['daemonId'], self.home, time.time())
        require(delivery_image['Config'].get('User', '') in ('', '0', '0:0', 'root'))
        require(receipt == self.state['receipt'])
        gateway = self.image('gateway')
        expected = self.state['gateway']
        require(gateway['Id'] == expected['imageId'] and expected['image'] in gateway['RepoDigests'])
        labels = gateway['Config']['Labels']
        require(labels['org.opencontainers.image.revision'] == expected['sourceCommit']
                and labels['jeeb.source.tree'] == expected['sourceTree']
                and labels['org.opencontainers.image.source'] == 'https://github.com/olivium-dev/jeeb-gateway')
        require(gateway['Config']['User'] == 'appuser' and gateway['Config']['Entrypoint'] == ['dotnet', 'JeebGateway.dll'])

    def existing_secret(self):
        secret_metadata(request('GET', '/v1.52/secrets/' + self.state['secretId']), self.state['secretId'])

    def provision_secret(self):
        self.authority()
        expected_id = self.state['secretId']
        def inspect_existing():
            try:
                value = request('GET', '/v1.52/secrets/' + custody.SECRET_NAME)
            except EngineStatus as error:
                if error.status == 404: return None
                raise
            secret_metadata(value, value['ID'])
            require(not expected_id or expected_id == value['ID'])
            return value
        existing = inspect_existing()
        if existing is not None:
            return existing['ID']
        require(not expected_id)
        provision = self.journal.root / (custody.SECRET_NAME + '-provision')
        # No prior intent (including an empty directory) may authorize another
        # create. If the object appeared after lost acknowledgement, a future
        # invocation can only reuse that exact fixed-name/label object above.
        with custody.open_directory(self.journal.root) as parent:
            os.mkdir(provision.name, 0o700, dir_fd=parent)
            os.fsync(parent)
        labels = {'jeeb.environment': 'staging', 'jeeb.purpose': 'delivery-service-auth', 'jeeb.version': '1'}
        custody.write_exclusive(provision/'00-intent.json', {'schemaVersion': 1, 'secretName': custody.SECRET_NAME,
            'labels': labels, 'gatewaySource': self.state['gateway']['sourceCommit'],
            'gatewayTree': self.state['gateway']['sourceTree'], 'gatewayRun': self.state['gateway']['runId'],
            'gatewayAttempt': self.state['gateway']['attempt'], 'daemonId': self.state['daemonId'], 'nodeId': self.state['nodeId']})
        self.lock()
        # Token content exists only in process memory and the private Unix POST.
        # No token file, stdout, log, artifact, or content-derived hash is made.
        data = base64.b64encode(secrets.token_urlsafe(48).encode()).decode()
        try:
            result = request('POST', '/v1.52/secrets/create', {'Name': custody.SECRET_NAME, 'Labels': labels, 'Data': data})
            require(re.fullmatch(r'[a-z0-9]{25}', result['ID']))
            created = inspect_existing()
            require(created is not None and created['ID'] == result['ID'])
        except Exception:
            # One bounded read-only reconciliation, never a second create.
            created = inspect_existing()
            require(created is not None)
        finally:
            data = None
        custody.write_exclusive(provision/'01-object.json', {'secretName': custody.SECRET_NAME, 'secretId': created['ID'], 'labels': labels})
        return created['ID']

    def captured(self, role):
        require(role in SERVICES)
        item = request('GET', '/v1.52/services/' + SERVICES[role])
        require(re.fullmatch(r'[a-z0-9]{25}', item['ID']) and type(item['Version']['Index']) is int and item['Version']['Index'] > 0)
        return item

    def baseline(self, role):
        return custody.read_private(self.root / (role + '-baseline.json'), 0o600)

    def candidate(self, role):
        return custody.read_private(self.root / (role + '-candidate.json'), 0o600)

    def validate_candidates(self):
        self.authority()
        self.existing_secret()
        for role in SERVICES:
            baseline = self.baseline(role)
            require(self.captured(role) == baseline and baseline.get('UpdateStatus', {}).get('State') == 'completed')
            require(self.candidate(role) == exact_spec(candidate(role, baseline['Spec'], self.state[role]['image'], self.state['secretId'], self.state['nodeId'])))
        delivery = self.baseline('delivery')
        receipt = self.state['receipt']
        require(delivery['ID'] == receipt['deliveryServiceId'] and delivery['Version']['Index'] == receipt['deliveryVersion'])
        self.network()

    def network(self):
        network = request('GET', '/v1.52/networks/jeeb-staging-net')
        receipt = self.state['receipt']
        require(network['Id'] == receipt['networkId'] and network.get('Version') == receipt['networkVersion'])
        require(network['Name'] == 'jeeb-staging-net' and network['Driver'] == 'overlay'
                and network['Attachable'] is True and 'encrypted' in network.get('Options', {}))
        for role in SERVICES:
            task = self.baseline(role)['Spec']['TaskTemplate']
            require([n['Target'] for n in task['Networks']] == [network['Id']])
            constraints = [value.replace(' ', '') for value in task.get('Placement', {}).get('Constraints', [])]
            require(not any(value.startswith('node.id==') and value != 'node.id==' + self.state['nodeId'] for value in constraints))
            require(not any(value.startswith('node.id!=') and value == 'node.id!=' + self.state['nodeId'] for value in constraints))
            require(not any(value.startswith('node.hostname==') and value != 'node.hostname==olivium-ephemerals' for value in constraints))
            require('node.hostname!=olivium-ephemerals' not in constraints)
            require('node.role==worker' not in constraints and 'node.role!=manager' not in constraints)
            expected_port = {'Protocol': 'tcp', 'TargetPort': 8080, 'PublishedPort': 10000 if role == 'gateway' else 10055,
                             'PublishMode': 'ingress' if role == 'gateway' else 'host'}
            require(self.baseline(role)['Spec']['EndpointSpec']['Ports'] == [expected_port])
        return network

    def schema(self):
        self.authority()
        baseline = self.baseline('delivery')
        require(self.captured('delivery') == baseline)
        before_network = self.network()
        receipt = self.state['receipt']
        helper = self.home / '.jeeb-deploy/paired-releases/delivery' / (receipt['runId'] + '-' + receipt['attempt'] + '.probe.py')
        fd = os.open(helper, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        try:
            info = os.fstat(fd)
            require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid() and stat.S_IMODE(info.st_mode) == 0o400
                    and info.st_nlink == 1 and info.st_size <= 131072)
            data = os.read(fd, 131073)
        finally:
            os.close(fd)
        require(hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest() == receipt['probeHelperBlobSha'])
        namespace = {'__name__': 'verified_delivery_probe', '__file__': str(helper)}
        exec(compile(data, str(helper), 'exec'), namespace)
        # Publisher proved these protected credentials for this exact immutable
        # service ID/version. Parsing them here is not a new protected comparison.
        values = env_map(baseline['Spec']['TaskTemplate']['ContainerSpec']['Env'])
        parsed = urlsplit(values['DATABASE_URL'])
        expected = {'host': parsed.hostname, 'port': str(parsed.port),
                    'username': unquote(parsed.username or ''), 'password': unquote(parsed.password or '')}
        namespace['probe']({'image': self.state['delivery']['image'], 'run_id': self.state['gateway']['runId'],
                            'attempt': self.state['gateway']['attempt'],
                            'registry': '.jeeb-deploy/delivery-schema-ghcr-' + self.state['gateway']['runId'] + '-' + self.state['gateway']['attempt'],
                            'expected': expected}, local_image={'imageId': receipt['imageId'],
                            'sourceCommit': receipt['sourceCommit'], 'sourceTree': receipt['sourceTree']})
        self.lock()
        require(self.captured('delivery') == baseline)
        require(self.network() == before_network)

    def verify(self, role):
        self.lock()
        self.network()
        baseline = self.baseline(role)
        current = self.captured(role)
        require(current['ID'] == baseline['ID'] and current['Version']['Index'] > baseline['Version']['Index'])
        require(exact_spec(current['Spec']) == exact_spec(self.candidate(role)))
        require(current.get('UpdateStatus', {}).get('State') == 'completed')
        image = self.image(role)
        ids = docker('service', 'ps', SERVICES[role], '--no-trunc', '--filter', 'desired-state=running', '--format', '{{.ID}}').split()
        require(len(ids) == 1 and re.fullmatch(r'[a-z0-9]{25}', ids[0]))
        task = json.loads(docker('inspect', ids[0]))[0]
        require(task['ServiceID'] == current['ID'] and task['Status']['State'] == 'running'
                and task['DesiredState'] == 'running' and task['Spec']['ContainerSpec']['Image'] == self.state[role]['image'])
        require(task['NodeID'] == self.state['nodeId'])
        cid = task['Status']['ContainerStatus']['ContainerID']
        require(re.fullmatch(r'[0-9a-f]{64}', cid))
        container = json.loads(docker('container', 'inspect', cid))[0]
        require(container['Id'] == cid and container['Image'] == image['Id'] and container['State']['Running'] is True)
        require(container['Config']['Labels']['com.docker.swarm.service.id'] == current['ID'])
        require(container['Config']['Labels']['com.docker.swarm.task.id'] == ids[0])
        require(task['Spec']['ContainerSpec'].get('Secrets') == self.candidate(role)['TaskTemplate']['ContainerSpec'].get('Secrets'))
        expected_env = env_map(image['Config'].get('Env', []))
        expected_env.update(env_map(self.candidate(role)['TaskTemplate']['ContainerSpec'].get('Env', [])))
        require(env_map(container['Config'].get('Env', [])) == expected_env)
        require(container['Config']['Entrypoint'] == image['Config']['Entrypoint'])
        expected_user = self.candidate(role)['TaskTemplate']['ContainerSpec'].get('User') or image['Config'].get('User', '')
        require(expected_user in (('appuser', '65532', '65532:65532') if role == 'gateway' else ('', '0', '0:0', 'root')))
        require(container['Config']['User'] == expected_user)
        mounts = [m for m in task['Spec']['ContainerSpec'].get('Secrets', []) if m.get('SecretID') == self.state['secretId']]
        uid = '65532' if role == 'gateway' else '0'
        require(len(mounts) == 1 and mounts[0]['File'] == {'Name': 'delivery_service_token', 'UID': uid, 'GID': uid, 'Mode': 256})
        require(docker('container', 'exec', '--user', uid + ':' + uid, cid, 'stat', '-c', '%u:%g:%a', TOKEN_PATH).strip() == uid + ':' + uid + ':400')
        return cid

    def wait_ready(self, role):
        for _ in range(40):
            self.lock()
            current = self.captured(role)
            require(current['ID'] == self.baseline(role)['ID'] and exact_spec(current['Spec']) == self.candidate(role))
            require(current.get('UpdateStatus', {}).get('State') not in ('paused', 'rollback_started', 'rollback_paused', 'rollback_completed'))
            try:
                cid = self.verify(role)
                path = '/health/ready' if role == 'gateway' else '/health'
                docker('container', 'exec', cid, 'wget', '-q', '-T', '5', '-O', '/dev/null', 'http://127.0.0.1:8080' + path, timeout=8)
                return
            except (ValueError, subprocess.SubprocessError, KeyError):
                time.sleep(2)
        raise ValueError('readiness deadline')

    def gateway_proof(self, mode):
        require(mode in ('credential', 'wire'))
        cid = self.verify('gateway')
        output = docker('container', 'exec', '--user', '65532:65532', cid, 'dotnet', '/app/JeebGateway.dll',
                        '--staging-delivery-auth-probe', mode, timeout=15)
        require(output == ('delivery credential ready\n' if mode == 'credential' else 'delivery authenticated wire ready\n'))
        self.verify('gateway')

    def final(self):
        self.existing_secret()
        self.verify('gateway')
        self.verify('delivery')
        self.gateway_proof('wire')
        for mode in ('missing', 'invalid', 'duplicate'):
            cid = self.verify('gateway')
            require(docker('container', 'exec', '--user', '65532:65532', cid, 'dotnet', '/app/JeebGateway.dll',
                           '--staging-delivery-auth-probe', mode, timeout=15) == 'delivery unauthorized request rejected\n')
        self.verify('gateway')
        self.verify('delivery')
        self.lock()

    def capture(self, role, paths):
        require(role in SERVICES and len(paths) == 3)
        self.lock()
        item = self.captured(role)
        values = (json.dumps(exact_spec(item['Spec'])), str(item['Version']['Index']), item['ID'])
        for path, value in zip(paths, values):
            path = Path(path)
            require(path.parent == self.root and re.fullmatch(r'[a-z-]+(?:\.json)?', path.name))
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, 'w') as stream:
                stream.write(value + '\n')

    def submit(self, role, service_id, version, path):
        require(role in SERVICES and Path(path) == self.root / (role + '-candidate.json'))
        self.authority()
        self.existing_secret()
        baseline = self.baseline(role)
        require(service_id == baseline['ID'] and version == str(baseline['Version']['Index']))
        require(self.captured(role) == baseline)
        self.network()
        phase_index = 1 if role == 'gateway' else 3
        phase = custody.read_private(self.journal.path / f'{phase_index:02d}-{role}-submission-pending.json', 0o400)
        require(phase['phase'] == role + '-submission-pending' and phase['secretId'] == self.state['secretId'])
        if role == 'delivery': self.verify('gateway')
        self.lock()
        # Burn a persistent fixed-role POST attempt before touching the socket.
        # The same pending phase cannot authorize a second submission after a
        # lost acknowledgement, process crash, or direct command repetition.
        custody.write_exclusive(self.journal.claims / (custody.SECRET_NAME + '-' + role + '-post.json'),
            {'serviceId': service_id, 'version': int(version), 'phase': role + '-submission-pending'})
        request('POST', f'/v1.52/services/{service_id}/update?version={version}', self.candidate(role))
        print('200')

    def begin(self):
        self.validate_candidates()
        metadata = {'secretId': self.state['secretId'], 'receiptNonce': self.state['receipt']['receiptNonce'],
                    'gatewayBuildRun': self.state['gateway']['buildRunId'], 'gatewayBuildAttempt': self.state['gateway']['buildAttempt']}
        for role in SERVICES:
            source = self.state[role]
            baseline = self.baseline(role)
            metadata.update({role + 'Source': source['sourceCommit'], role + 'Tree': source['sourceTree'],
                role + 'Image': source['image'], role + 'Run': source['runId'], role + 'Attempt': source['attempt'],
                role + 'ServiceId': baseline['ID'], role + 'Version': baseline['Version']['Index']})
        self.journal.begin(metadata)


def prepare(root, payload):
    root = custody.directory(Path(root))
    require(set(payload) == {'gateway', 'delivery', 'secretId', 'lockOwner', 'daemonId', 'nodeId'})
    require(re.fullmatch(r'[a-z0-9]{25}', payload['nodeId']))
    require(payload['secretId'] == '' or re.fullmatch(r'[a-z0-9]{25}', payload['secretId']))
    for role in SERVICES:
        source = payload[role]
        for field in ('runId', 'attempt'): require(re.fullmatch(r'[1-9][0-9]*', source[field]))
        for field in ('sourceCommit', 'sourceTree'): require(re.fullmatch(r'[0-9a-f]{40}', source[field]))
        require(re.fullmatch(r'ghcr\.io/olivium-dev/' + REPOSITORIES[role] + r'@sha256:[0-9a-f]{64}', source['image']))
    custody.assert_shared_lock(Path.home(), payload['lockOwner'])
    receipts = custody.directory(Path.home() / '.jeeb-deploy/paired-releases/delivery')
    payload['receipt'] = custody.read_private(receipts / (payload['delivery']['runId'] + '-' + payload['delivery']['attempt'] + '.json'), 0o400)
    require(payload['delivery']['image'] == payload['delivery']['imageDigest'])
    runtime = object.__new__(Runtime)
    runtime.root, runtime.state, runtime.home = root, payload, Path.home()
    runtime.journal = custody.Journal(Path.home())
    runtime.authority()
    captured = {role: runtime.captured(role) for role in SERVICES}
    require(captured['delivery']['ID'] == payload['receipt']['deliveryServiceId']
            and captured['delivery']['Version']['Index'] == payload['receipt']['deliveryVersion'])
    for role in SERVICES:
        baseline = captured[role]
        require(baseline.get('UpdateStatus', {}).get('State') == 'completed')
        candidate(role, baseline['Spec'], payload[role]['image'], '0'*25, payload['nodeId'])
        custody.write_exclusive(root / (role + '-baseline.json'), baseline, mode=0o600)
    runtime.network()
    payload['secretId'] = runtime.provision_secret()
    custody.write_exclusive(root / 'runtime.json', payload, mode=0o600)
    runtime.existing_secret()
    for role in SERVICES:
        baseline = captured[role]
        proposed = candidate(role, baseline['Spec'], payload[role]['image'], payload['secretId'], payload['nodeId'])
        custody.write_exclusive(root / (role + '-incumbent.json'), exact_spec(baseline['Spec']), mode=0o600)
        custody.write_exclusive(root / (role + '-candidate.json'), exact_spec(proposed), mode=0o600)
        for suffix, value in (('incumbent-id', baseline['ID']), ('incumbent-version', str(baseline['Version']['Index']))):
            fd = os.open(root / (role + '-' + suffix), os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, 'w') as stream: stream.write(value + '\n')
    runtime.validate_candidates()


def main():
    try:
        require(len(sys.argv) >= 3)
        if sys.argv[2] == 'prepare':
            prepare(sys.argv[1], json.load(sys.stdin, object_pairs_hook=custody.unique_object))
            return 0
        runtime = Runtime(sys.argv[1])
        command = sys.argv[2]
        if command == 'authority': runtime.authority()
        elif command == 'daemon': runtime.daemon()
        elif command == 'lock': runtime.lock()
        elif command == 'secret': runtime.existing_secret()
        elif command == 'candidates': runtime.validate_candidates()
        elif command == 'schema': runtime.schema()
        elif command == 'gateway':
            runtime.wait_ready('gateway')
            runtime.gateway_proof('credential')
        elif command == 'delivery': runtime.wait_ready('delivery')
        elif command == 'wire': runtime.gateway_proof('wire')
        elif command == 'final': runtime.final()
        elif command == 'capture': runtime.capture(sys.argv[3], sys.argv[4:])
        elif command == 'submit': runtime.submit(*sys.argv[3:])
        elif command == 'begin': runtime.begin()
        elif command == 'advance':
            runtime.lock()
            runtime.journal.advance(sys.argv[3])
        else: raise ValueError()
    except Exception:
        print('Paired activation adapter failed; no automatic retry or compensation.', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__': sys.exit(main())
