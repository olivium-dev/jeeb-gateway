#!/usr/bin/env python3
"""Explicit authority inventory for the one-use private chat migration."""
import ast
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def check_source(source):
    tree = ast.parse(source)
    imports = {node.module if isinstance(node, ast.ImportFrom) else alias.name
               for node in ast.walk(tree) if isinstance(node, (ast.Import, ast.ImportFrom)) for alias in node.names}
    assert imports <= {'contextlib', 'fcntl', 'copy', 'hashlib', 'http.client', 'json', 'os', 'pathlib', 're', 'secrets', 'socket', 'stat', 'sys', 'time', 'urllib.parse'}
    requests = [node for node in ast.walk(tree) if isinstance(node, ast.Call)
                and isinstance(node.func, ast.Attribute) and node.func.attr == 'request']
    assert len(requests) == 4
    posts = [node for node in requests if ast.literal_eval(node.args[0]) == 'POST']
    assert len(posts) == 2
    # Compare syntax structure, not unparse's version-dependent quote choices.
    expected_targets = {
        "'/api/firebase/token'", 'f"/v1.52/services/{original[\'ID\']}/update?version={version}&registryAuthFrom=spec"'}
    assert {ast.dump(node.args[1], include_attributes=False) for node in posts} == {
        ast.dump(ast.parse(expression, mode='eval').body, include_attributes=False)
        for expression in expected_targets}
    functions = {node.name: ast.get_source_segment(source, node) for node in ast.walk(tree) if isinstance(node, ast.FunctionDef)}
    validation = ast.parse(functions['validate_operation'])
    expressions = [node.args[0] for node in ast.walk(validation) if isinstance(node, ast.Call)
                   and isinstance(node.func, ast.Name) and node.func.id == 'require']
    expected = ast.parse('operation in ("migrate-private", "diagnose-private", "continue-private")', mode='eval').body
    assert len(expressions) == 1 and ast.dump(expressions[0]) == ast.dump(expected)
    assert 'expected_seal=self.approved_seal' in functions['retention']
    assert functions['submit'].index('self.inspect(role) == original') < functions['submit'].index('connection.request("POST"')
    migration = functions['migrate']
    for role in ('gateway', 'chat'):
        assert migration.index(f'journal.advance("{role}-submission-pending")') < migration.index(f'runtime.submit("{role}"')
    assert migration.rindex('runtime.verify("gateway"') < migration.rindex('runtime.verify("chat"') < migration.index('journal.advance("complete"')
    assert 'with baseline.c.held_lock(home, owner):' in functions['main']
    assert 'body=b"{"' in functions['http_probe']
    # Read-only entrypoint and its dedicated helpers may not reach any mutation
    # authority, including custody primitives that create directories/claims.
    forbidden = {'submit', 'advance', 'begin', 'migrate', 'continue_private', 'Journal', 'Runtime',
                 'held_lock', 'custody_root', 'write_exclusive', 'mkdir', 'makedirs',
                 'unlink', 'remove', 'rename', 'replace', 'write', 'write_text', 'write_bytes', 'system', 'execv',
                 'request', 'http_probe', 'wait_chat_readiness'}
    for name in ('diagnose', 'diagnostic_lock', 'diagnostic_journal', 'diagnostic_failure',
                 'recorded_candidate_reconciliation'):
        fragment = ast.parse(functions[name])
        for node in ast.walk(fragment):
            if not isinstance(node, ast.Call):
                continue
            called = node.func.id if isinstance(node.func, ast.Name) else node.func.attr if isinstance(node.func, ast.Attribute) else None
            assert called not in forbidden
            assert not any(keyword.arg == 'create' for keyword in node.keywords)
            if called == 'open':
                assert any(isinstance(item, ast.Attribute) and item.attr == 'O_RDONLY' for item in ast.walk(node))
                assert not any(isinstance(item, ast.Attribute) and item.attr in ('O_CREAT', 'O_WRONLY', 'O_RDWR', 'O_TRUNC') for item in ast.walk(node))
    assert 'LOCK_SH' in functions['diagnostic_lock'] and 'LOCK_NB' in functions['diagnostic_lock']
    assert 'LOCK_EX' not in functions['diagnostic_lock']
    main_tree = ast.parse(functions['main'])
    diagnostic_test = ast.parse('operation == "diagnose-private"', mode='eval').body
    branches = [node for node in ast.walk(main_tree) if isinstance(node, ast.If)
                and ast.dump(node.test) == ast.dump(diagnostic_test)]
    assert len(branches) == 1 and any(isinstance(node, ast.Return) for node in branches[0].body)
    continuation = ast.parse(functions['continue_private'])
    calls = [node for node in ast.walk(continuation) if isinstance(node, ast.Call)]
    submits = [node for node in calls if isinstance(node.func, ast.Attribute) and node.func.attr == 'submit']
    assert len(submits) == 1 and ast.literal_eval(submits[0].args[0]) == 'chat'
    assert not any((isinstance(node.func, ast.Name) and node.func.id == 'migrate') or
                   (isinstance(node.func, ast.Attribute) and node.func.attr in ('begin', 'unlink', 'remove', 'rmdir')) for node in calls)
    assert functions['continue_private'].count('recorded_candidate_reconciliation(') == 2
    assert 'baseline.private_raw' in functions['continue_private']


def check_workflow(source):
    import yaml
    document = yaml.safe_load(source)
    assert document['on']['workflow_dispatch']['inputs']['operation']['options'] == ['diagnose-private', 'migrate-private', 'continue-private', 'activate-identity']
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
