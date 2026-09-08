#!/usr/bin/env python3
"""Own-repository immutable build artifact custody; no cross-repository token."""
import hashlib
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import zipfile

REPO = 'olivium-dev/jeeb-gateway'
WORKFLOW = '.github/workflows/jeeb-staging-paired-activation.yml'
NAME = 'gateway-paired-build-receipt'


def require(value):
    if not value: raise ValueError('build receipt guard')


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value)
        value[key] = item
    return value


def api(path):
    return subprocess.run(['gh', 'api', 'repos/' + REPO + '/' + path], capture_output=True,
                          check=True, timeout=30).stdout


def validate(value, source, tree):
    require(set(value) == {'schemaVersion', 'repository', 'workflowPath', 'sourceCommit', 'sourceTree', 'image', 'runId', 'attempt'})
    require(value['schemaVersion'] == 1 and value['repository'] == REPO and value['workflowPath'] == WORKFLOW)
    require(value['sourceCommit'] == source and re.fullmatch(r'[0-9a-f]{40}', source))
    require(value['sourceTree'] == tree and re.fullmatch(r'[0-9a-f]{40}', tree))
    require(re.fullmatch(r'ghcr\.io/olivium-dev/jeeb-gateway@sha256:[0-9a-f]{64}', value['image']))
    for key in ('runId', 'attempt'): require(re.fullmatch(r'[1-9][0-9]*', value[key]))


def consume(run_id, source, tree):
    require(re.fullmatch(r'[1-9][0-9]*', run_id))
    run = json.loads(api('actions/runs/' + run_id), object_pairs_hook=unique_object)
    require(run['head_sha'] == source and run['head_branch'] == 'main' and run['path'] == WORKFLOW)
    require(run['event'] == 'workflow_dispatch' and run['status'] == 'completed' and run['conclusion'] == 'success')
    require(run['repository']['full_name'] == REPO and run['actor']['login'] == 'oudaykhaled'
            and run['triggering_actor']['login'] == 'oudaykhaled')
    artifacts = json.loads(api('actions/runs/' + run_id + '/artifacts?per_page=100'), object_pairs_hook=unique_object)
    require(artifacts['total_count'] <= 100)
    matching = [item for item in artifacts['artifacts'] if item['name'] == NAME]
    require(len(matching) == 1 and matching[0]['expired'] is False)
    artifact = matching[0]
    require(type(artifact['id']) is int and artifact['id'] > 0 and artifact['size_in_bytes'] < 32768)
    archive = api('actions/artifacts/' + str(artifact['id']) + '/zip')
    require(len(archive) <= 32768 and artifact['digest'] == 'sha256:' + hashlib.sha256(archive).hexdigest())
    with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
        require(bundle.namelist() == ['gateway-build-receipt.json'])
        require(bundle.getinfo('gateway-build-receipt.json').file_size <= 8192)
        value = json.loads(bundle.read('gateway-build-receipt.json'), object_pairs_hook=unique_object)
    validate(value, source, tree)
    require(value['runId'] == run_id and value['attempt'] == str(run['run_attempt']))
    return value


def main():
    try:
        source = os.environ['GITHUB_SHA']
        tree = subprocess.check_output(['git', 'rev-parse', 'HEAD^{tree}'], text=True).strip()
        if sys.argv[1:] == ['prepare']:
            value = {'schemaVersion': 1, 'repository': REPO, 'workflowPath': WORKFLOW, 'sourceCommit': source,
                     'sourceTree': tree, 'image': os.environ['BUILT_IMAGE'], 'runId': os.environ['GITHUB_RUN_ID'],
                     'attempt': os.environ['GITHUB_RUN_ATTEMPT']}
            validate(value, source, tree)
        elif sys.argv[1:] == ['consume']:
            value = consume(os.environ['GATEWAY_PREPARE_RUN'], source, tree)
        else: raise ValueError()
        # This is entirely nonsecret source/image metadata.
        with open('gateway-build-receipt.json', 'x') as stream: json.dump(value, stream, sort_keys=True)
    except Exception:
        print('Gateway build receipt failed closed.', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__': sys.exit(main())
