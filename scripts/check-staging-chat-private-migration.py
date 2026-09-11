#!/usr/bin/env python3
"""Explicit authority inventory for the one-use private chat migration."""
import ast
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def check_source(source):
    tree = ast.parse(source)
    imports = {node.module if isinstance(node, ast.ImportFrom) else alias.name
               for node in ast.walk(tree) if isinstance(node, (ast.Import, ast.ImportFrom)) for alias in node.names}
    assert imports <= {'copy', 'hashlib', 'http.client', 'json', 'os', 'pathlib', 're', 'secrets', 'socket', 'stat', 'sys', 'time', 'urllib.parse'}
    requests = [node for node in ast.walk(tree) if isinstance(node, ast.Call)
                and isinstance(node.func, ast.Attribute) and node.func.attr == 'request']
    assert len(requests) == 4
    posts = [node for node in requests if ast.literal_eval(node.args[0]) == 'POST']
    assert len(posts) == 2
    assert {ast.unparse(node.args[1]) for node in posts} == {
        "'/api/firebase/token'", 'f"/v1.52/services/{original[\'ID\']}/update?version={version}&registryAuthFrom=spec"'}
    functions = {node.name: ast.get_source_segment(source, node) for node in ast.walk(tree) if isinstance(node, ast.FunctionDef)}
    assert 'require(operation == "migrate-private")' in functions['validate_operation']
    assert 'expected_seal=self.approved_seal' in functions['retention']
    assert functions['submit'].index('self.inspect(role) == original') < functions['submit'].index('connection.request("POST"')
    migration = functions['migrate']
    for role in ('gateway', 'chat'):
        assert migration.index(f'journal.advance("{role}-submission-pending")') < migration.index(f'runtime.submit("{role}"')
    assert migration.rindex('runtime.verify("gateway"') < migration.rindex('runtime.verify("chat"') < migration.index('journal.advance("complete"')
    assert 'with baseline.c.held_lock(home, owner):' in functions['main']
    assert 'body=b"{"' in functions['http_probe']


def check_workflow(source):
    import yaml
    document = yaml.safe_load(source)
    assert set(document['jobs']) == {'migrate'}
    assert document['permissions'] == {'contents': 'read', 'actions': 'read'}
    assert document['concurrency'] == {'group': 'jeeb-staging-jeeb-gateway', 'cancel-in-progress': False}
    job = document['jobs']['migrate']
    assert job['environment'] == 'staging' and job['timeout-minutes'] == 15
    assert all(not step.get('continue-on-error') and 'if' not in step for step in job['steps'])
    assert job['steps'][0]['run'] == 'set -euo pipefail\n[ "$GITHUB_ACTOR" = oudaykhaled ]\n[ "$GITHUB_TRIGGERING_ACTOR" = oudaykhaled ]\n'
    assert job['steps'][1]['with'] == {'ref': '${{ github.sha }}', 'persist-credentials': False}
    gate = job['steps'][2]['run']
    assert '[ "$REVIEWED_SHA" = "$GITHUB_SHA" ]' in gate
    assert 'bash scripts/staging-paired-source-guard.sh' in gate and 'validate-operation "$OPERATION"' in gate
    run = job['steps'][-1]['run']
    approval = '[ "$approved_seal" = "$BASELINE_SEAL" ]'
    assert run.index('staging-delivery-baseline-provenance.py') < run.index(approval) < run.index('staging-chat-private-migration.py bundle')
    assert 'ssh jeeb-staging python3 -I -B - "$OPERATION" "$GITHUB_SHA" "$GITHUB_RUN_ID" "$GITHUB_RUN_ATTEMPT" "$approved_seal"' in run
    assert run.count('bash scripts/staging-paired-source-guard.sh') == 2


def main():
    check_source((ROOT / 'scripts/staging-chat-private-migration.py').read_text())
    check_workflow((ROOT / '.github/workflows/jeeb-staging-chat-private-migration.yml').read_text())
    print('Private chat one-use mutation authority and fixed workflow validated.')


if __name__ == '__main__':
    main()
