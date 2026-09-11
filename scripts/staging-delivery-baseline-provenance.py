#!/usr/bin/env python3
"""Runner-only successful-workflow provenance for a projected nonsecret seal.

No SSH, Docker, private evidence, credential reads, or remote code execution.
stdout is one approved seal hash (or `none` when no alternative is needed).
"""
import hashlib
import io
import json
import re
import subprocess
import sys
import zipfile

REPOSITORY = 'olivium-dev/jeeb-gateway'
WORKFLOW = '.github/workflows/jeeb-staging-delivery-current-baseline.yml'
FILENAME = 'delivery-current-baseline-public-seal.json'


def require(value):
    if not value:
        raise ValueError('baseline provenance guard')


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result)
        result[key] = value
    return result


def projection(value):
    require(isinstance(value, dict) and type(value.get('schema_version')) is int
            and value['schema_version'] == 1)
    if value.get('state') == 'not-required':
        require(set(value) == {'schema_version', 'state'})
        return None
    require(set(value) == {'schema_version', 'state', 'authority', 'seal_sha256', 'evidence_sha256',
                          'historical_completion', 'current_posture'} and value['state'] == 'sealed')
    require(value['historical_completion'] == 'unproven' and value['current_posture'] == 'verified')
    require(all(isinstance(value[key], str) and re.fullmatch(r'[0-9a-f]{64}', value[key])
                for key in ('seal_sha256', 'evidence_sha256')))
    p = value['authority']
    require(isinstance(p, dict) and set(p) == {'repository', 'workflow', 'source', 'run', 'attempt'})
    require(p['repository'] == REPOSITORY and p['workflow'] == WORKFLOW)
    require(isinstance(p['source'], str) and re.fullmatch(r'[0-9a-f]{40}', p['source']))
    require(all(isinstance(p[key], str) and re.fullmatch(r'[1-9][0-9]*', p[key]) for key in ('run', 'attempt')))
    return p


def validate(value, run):
    p = projection(value)
    require(p is not None and isinstance(run, dict))
    require(type(run['id']) is int and run['id'] == int(p['run']))
    require(type(run['run_attempt']) is int and run['run_attempt'] == int(p['attempt']))
    require(run['head_sha'] == p['source'] and run['head_branch'] == 'main'
            and run['path'] == WORKFLOW and run['event'] == 'workflow_dispatch'
            and run['status'] == 'completed' and run['conclusion'] == 'success')
    require(run['repository']['full_name'] == REPOSITORY and run['head_repository']['full_name'] == REPOSITORY
            and run['actor']['login'] == 'oudaykhaled' and run['triggering_actor']['login'] == 'oudaykhaled')
    return value['seal_sha256']


def verify(value, fetch):
    p = projection(value)
    if p is None:
        return 'none'
    prefix = 'repos/'+REPOSITORY+'/actions/'
    approved = validate(value, fetch(prefix+'runs/'+p['run']+'/attempts/'+p['attempt']))
    artifacts = fetch(prefix+'runs/'+p['run']+'/artifacts?per_page=100')
    require(type(artifacts['total_count']) is int and artifacts['total_count'] <= 100
            and artifacts['total_count'] == len(artifacts['artifacts']))
    name = 'delivery-current-baseline-public-seal-'+p['run']+'-'+p['attempt']
    matching = [item for item in artifacts['artifacts'] if item['name'] == name]
    require(len(matching) == 1)
    artifact = matching[0]
    require(artifact['expired'] is False and type(artifact['id']) is int and artifact['id'] > 0
            and type(artifact['size_in_bytes']) is int and 0 < artifact['size_in_bytes'] <= 32768)
    archive = fetch(prefix+'artifacts/'+str(artifact['id'])+'/zip', raw=True)
    require(len(archive) <= 32768 and artifact['digest'] == 'sha256:'+hashlib.sha256(archive).hexdigest())
    with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
        require(bundle.namelist() == [FILENAME])
        require(bundle.getinfo(FILENAME).file_size <= 16384)
        recorded = json.loads(bundle.read(FILENAME), object_pairs_hook=unique_object)
    projection(recorded)
    require(recorded == value)
    return approved


def fetch(path, raw=False):
    data = subprocess.run(['gh', 'api', path], capture_output=True, check=True, timeout=30).stdout
    require(len(data) <= (32768 if raw else 1024*1024))
    return data if raw else json.loads(data, object_pairs_hook=unique_object)


def main():
    try:
        require(sys.argv[1:] == [])
        raw = sys.stdin.buffer.read(16385)
        require(len(raw) <= 16384)
        value = json.loads(raw, object_pairs_hook=unique_object)
        print(verify(value, fetch))
        return 0
    except Exception:
        print('Current-baseline workflow provenance is not verified.', file=sys.stderr)
        return 1


if __name__ == '__main__': sys.exit(main())
