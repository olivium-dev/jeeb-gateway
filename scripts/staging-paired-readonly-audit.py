#!/usr/bin/env python3
"""Fixed staging GET-only inspection. Output is an allowlist of booleans/enums.

This does not authorize resubmission, finalization, or deletion of custody state.
No inspected environment value, command, full Spec, or exception is serialized.
"""
import copy
import http.client
import json
import os
from pathlib import Path
import re
import socket
import stat
import sys
from urllib.parse import quote

SOCKET = '/var/run/docker.sock'
PREFIX = '/v1.52'
SERVICES = {'gateway': 'jeeb-staging-jeeb-gateway', 'delivery': 'jeeb-staging-delivery-service'}
REPOSITORIES = {'gateway': 'jeeb-gateway', 'delivery': 'delivery-service'}
SECRET_NAME = 'jeeb-staging-delivery-service-auth-v1'
TOKEN_PATH = '/run/secrets/delivery_service_token'
PHASES = ('prepared', 'gateway-submission-pending', 'gateway-verified',
          'delivery-submission-pending', 'delivery-verified', 'complete')
UPDATE_STATES = ('updating', 'paused', 'completed', 'rollback_started', 'rollback_paused', 'rollback_completed')
AUTH_KEYS = {'delivery_service_token', 'delivery_service_token_file', 'delivery_service_auth_mode',
             'services:delivery:servicetoken', 'services:delivery:servicetokenfile'}
MAX_BYTES = 1024 * 1024


def require(value):
    if not value:
        raise ValueError('inspection guard')


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value)
        value[key] = item
    return value


def identifier(value):
    return isinstance(value, str) and re.fullmatch(r'[a-z0-9]{25}', value) is not None


def image_reference(role, value):
    return isinstance(value, str) and re.fullmatch(
        r'ghcr\.io/olivium-dev/' + REPOSITORIES[role] + r'@sha256:[0-9a-f]{64}', value) is not None


def tasks_path(role):
    require(role in SERVICES)
    filters = {'service': [SERVICES[role]], 'desired-state': ['running']}
    return PREFIX + '/tasks?filters=' + quote(json.dumps(filters, separators=(',', ':')), safe='')


class LocalEngine(http.client.HTTPConnection):
    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect(SOCKET)


def get(path):
    # Fixed names, digest-only own images and validated identifiers; no arbitrary
    # query or caller-supplied method/body can reach the socket.
    allowed = {'/version', PREFIX + '/info', PREFIX + '/secrets/' + SECRET_NAME}
    allowed.update(PREFIX + '/services/' + value for value in SERVICES.values())
    allowed.update(tasks_path(role) for role in SERVICES)
    patterns = (r'/v1\.52/images/ghcr\.io/olivium-dev/(?:jeeb-gateway|delivery-service)@sha256:[0-9a-f]{64}/json',
                r'/v1\.52/containers/[0-9a-f]{64}/json')
    require(path in allowed or any(re.fullmatch(pattern, path) for pattern in patterns))
    connection = LocalEngine('localhost', timeout=10)
    try:
        connection.request('GET', path)
        response = connection.getresponse()
        raw = response.read(MAX_BYTES + 1)
        require(response.status == 200 and len(raw) <= MAX_BYTES)
        value = json.loads(raw, object_pairs_hook=unique_object)
        require(isinstance(value, (dict, list)))
        return value
    finally:
        connection.close()


def metadata_valid(value):
    keys = {'secretId', 'receiptNonce', 'gatewayBuildRun', 'gatewayBuildAttempt'}
    for role in SERVICES:
        keys.update(role + suffix for suffix in ('Source', 'Tree', 'Image', 'Run', 'Attempt', 'ServiceId', 'Version'))
    if not isinstance(value, dict) or set(value) != keys:
        return False
    if not identifier(value['secretId']) or not re.fullmatch(r'[0-9a-f]{64}', str(value['receiptNonce'])):
        return False
    for key in ('gatewayBuildRun', 'gatewayBuildAttempt'):
        if not isinstance(value[key], str) or not re.fullmatch(r'[1-9][0-9]*', value[key]): return False
    for role in SERVICES:
        if not image_reference(role, value[role + 'Image']) or not identifier(value[role + 'ServiceId']): return False
        if type(value[role + 'Version']) is not int or value[role + 'Version'] <= 0: return False
        for suffix in ('Source', 'Tree'):
            if not isinstance(value[role + suffix], str) or not re.fullmatch(r'[0-9a-f]{40}', value[role + suffix]): return False
        for suffix in ('Run', 'Attempt'):
            if not isinstance(value[role + suffix], str) or not re.fullmatch(r'[1-9][0-9]*', value[role + suffix]): return False
    return True


def read_journal(home):
    report = {'state': 'unreadable', 'phases': [], 'prefix_valid': False, 'metadata_consistent': False}
    opened = []
    try:
        home = Path(home)
        require(home.is_absolute())
        directory = os.open('/', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        opened.append(directory)
        for part in home.parts[1:]:
            directory = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
            opened.append(directory)
            info = os.fstat(directory)
            require(info.st_uid in (0, os.getuid()) and not info.st_mode & 0o022)
        require(os.fstat(directory).st_uid == os.getuid())
        for part in ('.jeeb-deploy', 'paired-releases', SECRET_NAME):
            try:
                directory = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
            except FileNotFoundError:
                report['state'] = 'absent'
                return report, None
            opened.append(directory)
            info = os.fstat(directory)
            require(info.st_uid == os.getuid() and stat.S_IMODE(info.st_mode) == 0o700)
        expected = [f'{i:02d}-{phase}.json' for i, phase in enumerate(PHASES)]
        names = set(os.listdir(directory))
        report['phases'] = [phase for name, phase in zip(expected, PHASES) if name in names]
        report['prefix_valid'] = names == set(expected[:len(names)])
        require(report['prefix_valid'] and names)
        metadata = None
        for index in range(len(names)):
            fd = os.open(expected[index], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
            try:
                before = os.fstat(fd)
                require(stat.S_ISREG(before.st_mode) and before.st_uid == os.getuid()
                        and stat.S_IMODE(before.st_mode) == 0o400 and before.st_nlink == 1 and before.st_size <= 32768)
                raw = os.read(fd, 32769)
                after = os.fstat(fd)
                require(len(raw) == before.st_size and (before.st_size, before.st_mtime_ns, before.st_ctime_ns)
                        == (after.st_size, after.st_mtime_ns, after.st_ctime_ns))
                value = json.loads(raw, object_pairs_hook=unique_object)
                require(isinstance(value, dict) and value.pop('phase') == PHASES[index] and metadata_valid(value))
                if metadata is None: metadata = value
                require(value == metadata)
            finally:
                os.close(fd)
        require(set(os.listdir(directory)) == names)
        report.update(state='complete' if len(names) == len(PHASES) else 'incomplete', metadata_consistent=True)
        return report, metadata
    except Exception:
        return report, None
    finally:
        for fd in reversed(opened): os.close(fd)


def env_map(rows):
    require(isinstance(rows, list))
    values = {}
    for row in rows:
        require(isinstance(row, str))
        key, separator, value = row.partition('=')
        require(separator and key and key not in values)
        values[key] = value
    return values


def auth_rows(values):
    return [key for key in values if key.lower().replace('__', ':') in AUTH_KEYS]


def auth_mounts(container):
    return [mount for mount in container.get('Secrets', []) if mount.get('SecretName') == SECRET_NAME
            or mount.get('File', {}).get('Name') in ('delivery_service_token', TOKEN_PATH)]


def auth_inactive(container):
    return not auth_rows(env_map(container.get('Env', []))) and not auth_mounts(container)


def exact_spec(spec):
    result = copy.deepcopy(spec)
    container = result['TaskTemplate']['ContainerSpec']
    container.get('PullOptions', {}).pop('RegistryAuth', None)
    if not container.get('PullOptions'): container.pop('PullOptions', None)
    return result


def reconstructed_previous(role, previous, metadata, node_id):
    """In-memory comparison only; never an update payload or recovery authority."""
    require(metadata_valid(metadata) and identifier(node_id))
    require(previous['Name'] == SERVICES[role] and previous['Mode'] == {'Replicated': {'Replicas': 1}})
    require(previous.get('UpdateConfig', {}).get('FailureAction') == 'pause')
    container = previous['TaskTemplate']['ContainerSpec']
    require(image_reference(role, container['Image']) and auth_inactive(container))
    require(not any(container.get(key) for key in ('Command', 'Args', 'Dir', 'Mounts', 'Configs')))
    require(container.get('User', '') in (('', 'appuser', '65532', '65532:65532') if role == 'gateway' else ('', '0', '0:0', 'root')))
    values = env_map(container.get('Env', []))
    require(not any(key in values for key in ('PATH', 'LD_PRELOAD', 'LD_LIBRARY_PATH', 'DOTNET_ROOT',
                 'DOTNET_STARTUP_HOOKS', 'ASPNETCORE_HOSTINGSTARTUPASSEMBLIES')))
    if role == 'gateway':
        require(values.get('Services__Delivery__BaseUrl') == 'http://192.168.2.20:10055')
        environment_keys = {'ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT'}
        require(not any(key.upper() in environment_keys and key not in environment_keys for key in values))
        require(values.get('ASPNETCORE_ENVIRONMENT', 'Production') in ('Production', 'Staging'))
        require(values.get('DOTNET_ENVIRONMENT', 'Production') in ('Production', 'Staging'))
        require(not any(key.lower().replace('__', ':') == 'services:delivery:baseurl'
                        and key != 'Services__Delivery__BaseUrl' for key in values))
    else:
        require(values.get('SKIP_DB_INIT') == 'true')
    result = copy.deepcopy(previous)
    target = result['TaskTemplate']['ContainerSpec']
    target['Image'] = metadata[role + 'Image']
    target.setdefault('Env', []).append('DELIVERY_SERVICE_TOKEN_FILE=' + TOKEN_PATH)
    if role == 'delivery': target['Env'].append('DELIVERY_SERVICE_AUTH_MODE=required')
    uid = '65532' if role == 'gateway' else '0'
    target.setdefault('Secrets', []).append({'SecretID': metadata['secretId'], 'SecretName': SECRET_NAME,
        'File': {'Name': 'delivery_service_token', 'UID': uid, 'GID': uid, 'Mode': 256}})
    constraints = result['TaskTemplate'].setdefault('Placement', {}).setdefault('Constraints', [])
    if 'node.id==' + node_id not in [value.replace(' ', '') for value in constraints]:
        constraints.append('node.id==' + node_id)
    return exact_spec(result)


def sequence(value):
    require(value is None or (isinstance(value, list) and all(isinstance(item, str) for item in value)))
    return value or []


def collect_role(role, service):
    """Private in-memory evidence, never serialized by the entrypoint."""
    require(identifier(service['ID']) and service['Spec']['Name'] == SERVICES[role])
    declaration = service['Spec']['TaskTemplate']['ContainerSpec']
    require(image_reference(role, declaration['Image']))
    image = get(PREFIX + '/images/' + declaration['Image'] + '/json')
    tasks = get(tasks_path(role))
    require(isinstance(tasks, list))
    snapshot = {'service': service, 'image': image, 'tasks': tasks, 'container': None}
    if len(tasks) == 1:
        task = tasks[0]
        require(identifier(task['ID']) and task['ServiceID'] == service['ID'])
        cid = task['Status']['ContainerStatus']['ContainerID']
        require(isinstance(cid, str) and re.fullmatch(r'[0-9a-f]{64}', cid))
        snapshot['container'] = get(PREFIX + '/containers/' + cid + '/json')
    return snapshot


def inspect_role(role, snapshot, metadata, secret, node_id):
    """Pure projection/validation; never return any private snapshot value."""
    report = {'inspected': False}
    try:
        service = snapshot['service']
        require(identifier(service['ID']) and service['Spec']['Name'] == SERVICES[role])
        spec = service['Spec']
        declaration = spec['TaskTemplate']['ContainerSpec']
        require(image_reference(role, declaration['Image']))
        update = service.get('UpdateStatus', {}).get('State')
        report.update(update_state=update if update in UPDATE_STATES else 'absent' if update is None else 'unknown',
                      service_id_matches_journal=bool(metadata and service['ID'] == metadata[role + 'ServiceId']),
                      service_image_matches_journal=bool(metadata and declaration['Image'] == metadata[role + 'Image']),
                      service_version_after_journal=bool(metadata and type(service.get('Version', {}).get('Index')) is int
                          and service['Version']['Index'] > metadata[role + 'Version']))
        values = env_map(declaration.get('Env', []))
        mounts = auth_mounts(declaration)
        uid = '65532' if role == 'gateway' else '0'
        expected_mount = {'SecretID': secret.get('ID'), 'SecretName': SECRET_NAME,
                          'File': {'Name': 'delivery_service_token', 'UID': uid, 'GID': uid, 'Mode': 256}}
        report.update(token_file_env_present='DELIVERY_SERVICE_TOKEN_FILE' in values,
                      token_file_env_matches=values.get('DELIVERY_SERVICE_TOKEN_FILE') == TOKEN_PATH,
                      inline_credential_present=any(key.lower().replace('__', ':') in
                          ('delivery_service_token', 'services:delivery:servicetoken') for key in values),
                      auth_env_shape_matches=set(auth_rows(values)) == ({'DELIVERY_SERVICE_TOKEN_FILE'} if role == 'gateway'
                          else {'DELIVERY_SERVICE_TOKEN_FILE', 'DELIVERY_SERVICE_AUTH_MODE'}),
                      required_mode_matches=values.get('DELIVERY_SERVICE_AUTH_MODE') == 'required' if role == 'delivery'
                          else 'DELIVERY_SERVICE_AUTH_MODE' not in values,
                      secret_mount_matches=identifier(secret.get('ID')) and mounts == [expected_mount],
                      secret_matches_journal=bool(metadata and secret.get('ID') == metadata['secretId']))
        image = snapshot['image']
        config = image['Config']
        report.update(image_entrypoint_key_present='Entrypoint' in config,
                      image_entrypoint_empty=not sequence(config.get('Entrypoint')),
                      image_cmd_key_present='Cmd' in config,
                      image_working_directory_key_present='WorkingDir' in config,
                      image_digest_matches=declaration['Image'] in image.get('RepoDigests', []),
                      image_source_matches_journal=bool(metadata and config.get('Labels', {}).get('org.opencontainers.image.revision') == metadata[role + 'Source']),
                      image_tree_matches_journal=bool(metadata and config.get('Labels', {}).get('jeeb.source.tree') == metadata[role + 'Tree']))
        previous = service.get('PreviousSpec')
        report.update(previous_spec_present=isinstance(previous, dict), previous_auth_inactive=False,
                      previous_candidate_reconstructable=False, previous_candidate_matches_current=False)
        if isinstance(previous, dict):
            report['previous_auth_inactive'] = auth_inactive(previous['TaskTemplate']['ContainerSpec'])
            try:
                reconstructed = reconstructed_previous(role, previous, metadata, node_id)
                report['previous_candidate_reconstructable'] = True
                report['previous_candidate_matches_current'] = reconstructed == exact_spec(spec)
            except Exception:
                pass
        tasks = snapshot['tasks']
        require(isinstance(tasks, list))
        report['single_running_task'] = len(tasks) == 1
        if len(tasks) != 1: return report
        task = tasks[0]
        require(identifier(task['ID']) and task['ServiceID'] == service['ID'])
        cid = task['Status']['ContainerStatus']['ContainerID']
        require(isinstance(cid, str) and re.fullmatch(r'[0-9a-f]{64}', cid))
        container = snapshot['container']
        actual = container['Config']
        entrypoint = sequence(config.get('Entrypoint'))
        command = sequence(config.get('Cmd'))
        argv = entrypoint + command
        expected_env = env_map(config.get('Env', []))
        expected_env.update(values)
        expected_user = declaration.get('User') or config.get('User', '')
        report.update(container_entrypoint_key_present='Entrypoint' in actual,
                      container_entrypoint_null=actual.get('Entrypoint') is None,
                      container_entrypoint_empty=not sequence(actual.get('Entrypoint')),
                      normalized_entrypoint_matches=sequence(actual.get('Entrypoint')) == entrypoint,
                      normalized_cmd_matches=sequence(actual.get('Cmd')) == command,
                      normalized_working_directory_matches=(actual.get('WorkingDir') or '') == (config.get('WorkingDir') or ''),
                      effective_path_args_match=bool(argv) and container.get('Path') == argv[0]
                          and sequence(container.get('Args')) == argv[1:],
                      no_service_command_overrides=not any(declaration.get(key) for key in ('Command', 'Args', 'Dir')),
                      container_user_matches=actual.get('User', '') == expected_user,
                      runtime_user_allowed=expected_user in (('appuser', '65532', '65532:65532') if role == 'gateway' else ('', '0', '0:0', 'root')),
                      environment_matches_spec_and_image=env_map(actual.get('Env', [])) == expected_env,
                      task_secret_mount_matches=task['Spec']['ContainerSpec'].get('Secrets') == declaration.get('Secrets'),
                      task_on_expected_node=task.get('NodeID') == node_id,
                      task_running=task.get('DesiredState') == 'running' and task['Status'].get('State') == 'running',
                      task_image_matches=task['Spec']['ContainerSpec'].get('Image') == declaration['Image'],
                      container_identity_matches=container.get('Id') == cid and container.get('Image') == image.get('Id')
                          and actual.get('Labels', {}).get('com.docker.swarm.service.id') == service['ID']
                          and actual.get('Labels', {}).get('com.docker.swarm.task.id') == task['ID'],
                      container_running=container.get('State', {}).get('Running') is True, inspected=True)
    except Exception:
        pass
    return report


def collect(home):
    """Read-only private snapshot. Call project() before displaying anything."""
    require(socket.gethostname().split('.')[0] == 'olivium-ephemerals')
    require(stat.S_ISSOCK(os.lstat(SOCKET).st_mode))
    version = get('/version')
    require(version.get('Version') == '29.1.3' and version.get('GitCommit') == '29.1.3-0ubuntu3~24.04.2')
    def api(value):
        require(isinstance(value, str) and re.fullmatch(r'[0-9]+\.[0-9]+', value))
        return tuple(map(int, value.split('.')))
    require(api(version['MinAPIVersion']) <= (1, 52) <= api(version['ApiVersion']))
    info = get(PREFIX + '/info')
    swarm = info.get('Swarm', {})
    require(info.get('Name') == 'olivium-ephemerals' and swarm.get('NodeAddr') == '192.168.2.20'
            and swarm.get('ControlAvailable') is True and swarm.get('LocalNodeState') == 'active'
            and identifier(swarm.get('NodeID')))
    journal, metadata = read_journal(home)
    snapshot = {'info': info, 'journal': journal, 'metadata': metadata, 'roles': {}, 'services': {}, 'after': {}}
    try:
        secret = get(PREFIX + '/secrets/' + SECRET_NAME)
    except Exception:
        secret = {}
    snapshot['secret'] = secret
    for role in SERVICES:
        try:
            snapshot['services'][role] = get(PREFIX + '/services/' + SERVICES[role])
            snapshot['roles'][role] = collect_role(role, snapshot['services'][role])
        except Exception:
            pass
    for role in SERVICES:
        try: snapshot['after'][role] = get(PREFIX + '/services/' + SERVICES[role])
        except Exception: pass
    snapshot['journal_after'], snapshot['metadata_after'] = read_journal(home)
    return snapshot


def project(snapshot):
    """Only fixed keys, boolean verdicts, and known enum strings leave here."""
    secret, metadata = snapshot['secret'], snapshot['metadata']
    report = {'schema_version': 1, 'host_guard_passed': True, 'journal': snapshot['journal'], 'roles': {}}
    report['secret_metadata_matches'] = identifier(secret.get('ID')) and secret.get('Spec', {}).get('Name') == SECRET_NAME and secret['Spec'].get('Labels') == {
        'jeeb.environment': 'staging', 'jeeb.purpose': 'delivery-service-auth', 'jeeb.version': '1'}
    for role in SERVICES:
        report['roles'][role] = inspect_role(role, snapshot['roles'].get(role, {}), metadata, secret, snapshot['info']['Swarm']['NodeID'])
    report['snapshot_stable'] = (len(snapshot['services']) == len(SERVICES) and snapshot['services'] == snapshot['after']
                                and metadata is not None and snapshot['journal_after'] == snapshot['journal']
                                and snapshot['metadata_after'] == metadata)
    return report


def main():
    try:
        require(len(sys.argv) == 1)
        report = project(collect(Path.home()))
        output = json.dumps(report, sort_keys=True, separators=(',', ':'))
        require(len(output) <= 16384)
        print(output)
        return 0
    except Exception:
        print('{"schema_version":1,"host_guard_passed":false,"inspection_failed":true}')
        return 1


if __name__ == '__main__': sys.exit(main())
