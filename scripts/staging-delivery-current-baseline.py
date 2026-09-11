#!/usr/bin/env python3
"""Reconcile verified CURRENT posture; never complete/retry historical activation.

Engine access comes only from the existing GET-only paired audit. Private evidence
uses existing exclusive custody primitives. No credential contents are inspected.
"""
import base64
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import secrets
import stat
import sys
import time

sys.dont_write_bytecode = True
MODULES = {'baseline_audit': 'staging-paired-readonly-audit.py',
           'baseline_custody': 'staging-paired-custody.py'}


def module(name):
    if name in sys.modules:
        return sys.modules[name]
    # Local reviewed checkout only. Streamed bundles preload BOTH modules and
    # categorically disallow a missing-module fallback on the remote host.
    if globals().get('BASELINE_BUNDLED'):
        raise ValueError('missing reviewed module')
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(MODULES[name]))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


a, c = module('baseline_audit'), module('baseline_custody')
NAME = 'delivery-current-baseline-v1'
MAX_EVIDENCE = 4 * 1024 * 1024
CHUNK = 16384
REQUIRED = ('inspected', 'token_file_env_matches', 'auth_env_shape_matches',
            'required_mode_matches', 'secret_mount_matches', 'secret_matches_journal',
            'image_digest_matches', 'single_running_task', 'normalized_entrypoint_matches',
            'normalized_cmd_matches', 'normalized_working_directory_matches',
            'effective_path_args_match', 'no_service_command_overrides', 'container_user_matches',
            'runtime_user_allowed', 'environment_matches_spec_and_image', 'task_secret_mount_matches',
            'task_on_expected_node', 'task_running', 'task_image_matches',
            'container_identity_matches', 'container_running')


def require(value):
    if not value:
        raise ValueError('current baseline guard')


def encoded(value):
    return (json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False) + '\n').encode()


def digest(value):
    return hashlib.sha256(encoded(value)).hexdigest()


def private_raw(path):
    # Reuse the ancestor-pinning custody primitive; preserve original bytes too.
    c.directory(path.parent)
    with c.open_directory(path.parent) as parent:
        fd = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=parent)
    try:
        before = os.fstat(fd)
        require(stat.S_ISREG(before.st_mode) and before.st_uid == os.getuid()
                and stat.S_IMODE(before.st_mode) == 0o400 and before.st_nlink == 1
                and 0 < before.st_size <= 32768)
        raw = os.read(fd, 32769)
        after = os.fstat(fd)
        require(len(raw) == before.st_size and
                (before.st_size, before.st_mtime_ns, before.st_ctime_ns) ==
                (after.st_size, after.st_mtime_ns, after.st_ctime_ns))
        json.loads(raw, object_pairs_hook=a.unique_object)
        return raw
    finally:
        os.close(fd)


def origin(home, metadata):
    journal, actual = a.read_journal(home)
    require(actual == metadata and journal == {'state': 'incomplete',
        'phases': list(a.PHASES[:4]), 'prefix_valid': True, 'metadata_consistent': True})
    root = Path(home)/'.jeeb-deploy'/'paired-releases'
    paths = {f'journal-{i}': root/a.SECRET_NAME/f'{i:02d}-{phase}.json'
             for i, phase in enumerate(a.PHASES[:4])}
    paths.update({'nonce-claim': root/'consumed'/(metadata['receiptNonce']+'.json'),
        **{role+'-claim': root/'consumed'/(a.SECRET_NAME+'-'+role+'-post.json') for role in a.SERVICES}})
    result = {}
    for key, path in paths.items():
        raw = private_raw(path)
        result[key] = {'sha256': hashlib.sha256(raw).hexdigest(),
                       'bytes': base64.b64encode(raw).decode()}
    nonce = json.loads(base64.b64decode(result['nonce-claim']['bytes']))
    require(nonce == {'activation': a.SECRET_NAME, 'gatewayRun': metadata['gatewayRun']})
    for role in a.SERVICES:
        value = json.loads(base64.b64decode(result[role+'-claim']['bytes']))
        require(value == {'serviceId': metadata[role+'ServiceId'], 'version': metadata[role+'Version'],
                          'phase': role+'-submission-pending'})
    return result


def posture(role, spec, secret_id):
    declaration = spec['TaskTemplate']['ContainerSpec']
    values = a.env_map(declaration.get('Env', []))
    expected = {'DELIVERY_SERVICE_TOKEN_FILE': a.TOKEN_PATH}
    if role == 'delivery':
        expected['DELIVERY_SERVICE_AUTH_MODE'] = 'required'
        require(values.get('SKIP_DB_INIT') == 'true')
    require({key: values[key] for key in a.auth_rows(values)} == expected)
    uid = '65532' if role == 'gateway' else '0'
    mount = {'SecretID': secret_id, 'SecretName': a.SECRET_NAME,
             'File': {'Name': 'delivery_service_token', 'UID': uid, 'GID': uid, 'Mode': 256}}
    require(a.auth_mounts(declaration) == [mount])
    require(not any(item.get('Target') in ('/run', '/run/secrets', a.TOKEN_PATH)
                    for item in declaration.get('Mounts', [])))
    require(not any(item.get('File', {}).get('Name') in ('delivery_service_token', a.TOKEN_PATH)
                    for item in declaration.get('Configs', [])))
    return {'env': expected, 'mount': mount, 'skip_db_init': role == 'delivery'}


def verified(snapshot, initial=True):
    report = a.project(snapshot)
    require(report['host_guard_passed'] and report['snapshot_stable'] and report['secret_metadata_matches'])
    require(report['journal'] == {'state': 'incomplete', 'phases': list(a.PHASES[:4]),
                                 'prefix_valid': True, 'metadata_consistent': True})
    for role in a.SERVICES:
        row = report['roles'][role]
        require(all(row.get(key) is True for key in REQUIRED) and row['update_state'] == 'completed'
                and row['inline_credential_present'] is False)
        if initial:
            require(all(row.get(key) is True for key in ('service_id_matches_journal',
                'service_image_matches_journal', 'service_version_after_journal',
                'image_source_matches_journal', 'image_tree_matches_journal', 'previous_spec_present')))
        service = snapshot['services'][role]
        require(service['Spec']['Mode'] == {'Replicated': {'Replicas': 1}})
        require(service['Spec'].get('UpdateConfig', {}).get('FailureAction') == 'pause')
        require(type(service['Version']['Index']) is int and service['Version']['Index'] > 0)
        declaration = service['Spec']['TaskTemplate']['ContainerSpec']
        require(not declaration.get('Mounts') and not declaration.get('Configs'))
        values = a.env_map(declaration.get('Env', []))
        require(not any(key.upper() in {'PATH', 'LD_PRELOAD', 'LD_LIBRARY_PATH', 'DOTNET_ROOT',
            'DOTNET_STARTUP_HOOKS', 'ASPNETCORE_HOSTINGSTARTUPASSEMBLIES'} for key in values))
        if role == 'gateway':
            for key in ('ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT'):
                require(not any(name.upper() == key and name != key for name in values))
                require(values.get(key, 'Production') in ('Production', 'Staging'))
        posture(role, service['Spec'], snapshot['secret']['ID'])
        # Image defaults must not inject additional credential aliases either.
        effective = copy.deepcopy(service['Spec'])
        effective['TaskTemplate']['ContainerSpec']['Env'] = snapshot['roles'][role]['container']['Config']['Env']
        posture(role, effective, snapshot['secret']['ID'])
    return report


def authority(source, run, attempt):
    require(isinstance(source, str) and re.fullmatch(r'[0-9a-f]{40}', source))
    require(all(isinstance(value, str) and re.fullmatch(r'[1-9][0-9]*', value) for value in (run, attempt)))
    return {'repository': 'olivium-dev/jeeb-gateway',
            'workflow': '.github/workflows/jeeb-staging-delivery-current-baseline.yml',
            'source': source, 'run': run, 'attempt': attempt}


def stable_runtime(snapshot):
    """Exact execution identity/configuration, excluding health-log churn only."""
    roles = {}
    for role, value in snapshot['roles'].items():
        container = value['container']
        state = copy.deepcopy(container.get('State'))
        if isinstance(state, dict) and isinstance(state.get('Health'), dict):
            # Routine checks append timestamps/output even when status and the
            # failure streak are unchanged. They are not configuration drift.
            state['Health'].pop('Log', None)
        roles[role] = {
            'image': {key: value['image'].get(key) for key in ('Id', 'RepoDigests', 'Config')},
            'tasks': value['tasks'],
            'container': {**{key: container.get(key) for key in
                ('Id', 'Image', 'Config', 'Path', 'Args', 'Mounts', 'NetworkSettings',
                 'HostConfig', 'RestartCount')}, 'State': state}}
    return {'daemon': snapshot['info']['ID'], 'node': snapshot['info']['Swarm']['NodeID'],
            'services': snapshot['services'], 'roles': roles, 'secret': snapshot['secret'],
            'journal': snapshot['journal'], 'metadata': snapshot['metadata']}


def preserve(home, snapshot, originals, provenance):
    root = c.custody_root(home)
    path = root/NAME
    # Any partial directory is a permanent stop requiring separate review.
    with c.open_directory(root) as parent:
        os.mkdir(NAME, 0o700, dir_fd=parent)
        os.fsync(parent)
    evidence = {'snapshot': snapshot, 'originals': originals, 'audit': verified(snapshot)}
    raw = encoded(evidence)
    require(len(raw) <= MAX_EVIDENCE)
    hashes = {}
    for index, start in enumerate(range(0, len(raw), CHUNK)):
        name = f'evidence-{index:03d}.json'
        part = {'base64': base64.b64encode(raw[start:start+CHUNK]).decode()}
        c.write_exclusive(path/name, part)
        hashes[name] = digest(part)
    seal = {'schema_version': 1, 'historical_completion': 'unproven',
            'current_posture': 'verified', 'authority': provenance,
            'created_at': int(time.time()), 'files': hashes, 'evidence_sha256': hashlib.sha256(raw).hexdigest()}
    return path, seal


def reconcile(home, provenance):
    with c.held_lock(home, secrets.token_hex(32)):
        snapshot = a.collect(home)
        verified(snapshot)
        originals = origin(home, snapshot['metadata'])
        # Preserve private current and PreviousSpecs BEFORE any future mutation.
        path, seal = preserve(home, snapshot, originals, provenance)
        after = a.collect(home)
        verified(after)
        require(stable_runtime(after) == stable_runtime(snapshot))
        require(origin(home, snapshot['metadata']) == originals)
        # Only this last exclusive, fsynced seal authorizes retention acceptance.
        c.write_exclusive(path/'sealed.json', seal)
        return public_seal(seal)


def public_seal(seal):
    return {'schema_version': 1, 'state': 'sealed', 'authority': seal['authority'], 'historical_completion': 'unproven',
            'current_posture': 'verified', 'seal_sha256': digest(seal),
            'evidence_sha256': seal['evidence_sha256']}


def load_seal(home):
    path = c.directory(Path(home)/'.jeeb-deploy'/'paired-releases'/NAME)
    seal = c.read_private(path/'sealed.json', 0o400)
    require(set(seal) == {'schema_version', 'historical_completion', 'current_posture',
                         'authority', 'created_at', 'files', 'evidence_sha256'})
    require(type(seal['schema_version']) is int and seal['schema_version'] == 1
            and seal['historical_completion'] == 'unproven' and seal['current_posture'] == 'verified')
    p = seal['authority']
    require(p == authority(p['source'], p['run'], p['attempt']))
    require(type(seal['created_at']) is int and 0 < seal['created_at'] <= time.time()+60)
    files = seal['files']
    require(isinstance(files, dict) and 1 <= len(files) <= MAX_EVIDENCE//CHUNK)
    expected = {f'evidence-{index:03d}.json' for index in range(len(files))}
    require(set(files) == expected)
    with c.open_directory(path) as fd:
        require(set(os.listdir(fd)) == expected | {'sealed.json'})
    raw = bytearray()
    for name in sorted(expected):
        part = c.read_private(path/name, 0o400)
        require(set(part) == {'base64'} and digest(part) == files[name])
        decoded = base64.b64decode(part['base64'], validate=True)
        require(0 < len(decoded) <= CHUNK)
        raw.extend(decoded)
    require(len(raw) <= MAX_EVIDENCE and hashlib.sha256(raw).hexdigest() == seal['evidence_sha256'])
    evidence = json.loads(raw, object_pairs_hook=a.unique_object)
    require(set(evidence) == {'snapshot', 'originals', 'audit'})
    require(encoded(evidence) == bytes(raw) and verified(evidence['snapshot']) == evidence['audit'])
    return seal, evidence


def public_provenance(home):
    """Fixed nonsecret metadata only; the runner verifies GitHub run success."""
    journal, metadata = a.read_journal(home)
    if journal['state'] == 'absent' or (journal['state'] == 'complete' and metadata is not None):
        return {'schema_version': 1, 'state': 'not-required'}
    require(journal == {'state': 'incomplete', 'phases': list(a.PHASES[:4]),
                       'prefix_valid': True, 'metadata_consistent': True})
    seal, evidence = load_seal(home)
    require(origin(home, metadata) == evidence['originals'])
    return public_seal(seal)


def verify_retention(home, lock_owner, exact=False, expected_seal=None):
    c.assert_shared_lock(home, lock_owner)
    seal, evidence = load_seal(home)
    # The runner verified this exact seal's successful protected workflow run.
    # Every use (not only first CAS) is bound to that approved public hash.
    require(isinstance(expected_seal, str) and re.fullmatch(r'[0-9a-f]{64}', expected_seal)
            and digest(seal) == expected_seal)
    saved = evidence['snapshot']
    current = a.collect(home)
    verified(current, initial=False)
    require(current['metadata'] == saved['metadata'])
    require(origin(home, current['metadata']) == evidence['originals'])
    require(current['info']['ID'] == saved['info']['ID']
            and current['info']['Swarm']['NodeID'] == saved['info']['Swarm']['NodeID'])
    require(current['secret'] == saved['secret'])
    for role in a.SERVICES:
        before, now = saved['services'][role], current['services'][role]
        require(now['ID'] == before['ID'] and now['Version']['Index'] >= before['Version']['Index'])
        if now['Version']['Index'] == before['Version']['Index']:
            require(now == before)
        require(posture(role, now['Spec'], current['secret']['ID']) ==
                posture(role, before['Spec'], saved['secret']['ID']))
        if exact:
            require(now == before)
    # A seal or historical file changed during verification is not accepted.
    require(load_seal(home)[0] == seal and origin(home, current['metadata']) == evidence['originals'])
    c.assert_shared_lock(home, lock_owner)
    return current['secret']['ID']


def bundle(mode, approved_seal=None):
    require(mode in ('reconcile', 'retention'))
    if mode == 'retention':
        require(approved_seal == 'none' or (isinstance(approved_seal, str)
                and re.fullmatch(r'[0-9a-f]{64}', approved_seal)))
    directory = Path(__file__).parent
    source = 'import sys,types\nBASELINE_BUNDLED=True\n'
    for name, filename in MODULES.items():
        content = (directory/filename).read_text()
        source += f'm=types.ModuleType({name!r});sys.modules[{name!r}]=m\n'
        source += f'exec(compile({content!r},{filename!r},"exec"),m.__dict__)\n'
    source += (directory/Path(__file__).name).read_text()
    if mode == 'retention':
        # Fixed executable source supplied by the reviewed publisher; no ambient
        # helper path, environment-supplied source, or remote import fallback.
        print("staging_delivery_current_baseline_secret_id() {\npython3 -I - verify-retention "
              + approved_seal + " \"${STAGING_GATEWAY_LOCK_OWNER:-${DELIVERY_LOCK_OWNER:-}}\" <<'BASELINE_PY'")
        print(source)
        print('BASELINE_PY\n}')
    else:
        print(source)


def main():
    try:
        args = sys.argv[1:]
        if len(args) == 2 and args[0] == 'bundle':
            bundle(args[1]); return 0
        if len(args) == 3 and args[:2] == ['bundle', 'retention']:
            bundle('retention', args[2]); return 0
        if len(args) == 4 and args[0] == 'reconcile':
            result = reconcile(Path.home(), authority(*args[1:]))
            print(json.dumps(result, sort_keys=True))
        elif args == ['public-seal']:
            print(json.dumps(public_provenance(Path.home()), sort_keys=True))
        elif len(args) == 3 and args[0] == 'verify-retention':
            print(verify_retention(Path.home(), args[2], expected_seal=args[1]))
        elif len(args) == 3 and args[0] == 'verify-baseline':
            print(verify_retention(Path.home(), args[2], exact=True, expected_seal=args[1]))
        else:
            raise ValueError()
        return 0
    except Exception:
        print('Current delivery baseline is unverified; historical activation remains unproven.', file=sys.stderr)
        return 1


if __name__ == '__main__': sys.exit(main())
