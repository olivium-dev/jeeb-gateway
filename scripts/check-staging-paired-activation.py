#!/usr/bin/env python3
"""Explicit inventory for the new Python Engine surface; existing policy unchanged."""
import ast
from pathlib import Path
import sys
import yaml

ROOT = Path(__file__).resolve().parents[1]


def check_engine(source):
    tree = ast.parse(source)
    assignments = {node.targets[0].id: node.value for node in tree.body if isinstance(node, ast.Assign)
                   and len(node.targets) == 1 and isinstance(node.targets[0], ast.Name)}
    assert ast.literal_eval(assignments['SOCKET']) == '/var/run/docker.sock'
    assert ast.literal_eval(assignments['SERVICES']) == {'gateway': 'jeeb-staging-jeeb-gateway', 'delivery': 'jeeb-staging-delivery-service'}
    calls = [node for node in ast.walk(tree) if isinstance(node, ast.Call) and isinstance(node.func, ast.Name)
             and node.func.id == 'request' and node.args and isinstance(node.args[0], ast.Constant)
             and node.args[0].value != 'GET']
    assert len(calls) == 2 and all(call.args[0].value == 'POST' for call in calls)
    assert {ast.unparse(call.args[1]) for call in calls} == {"f'/v1.52/services/{service_id}/update?version={version}'", "'/v1.52/secrets/create'"}
    submit = next(node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == 'submit')
    text = ast.get_source_segment(source, submit)
    assert text.index('self.captured(role) == baseline') < text.index('custody.write_exclusive(') < text.index("request('POST'")
    assert 'self.authority()' in text and 'self.lock()' in text and 'self.existing_secret()' in text


def check_readonly_audit(source):
    """Keep the separate diagnostic outside all mutation/process authority."""
    tree = ast.parse(source)
    imports = {node.module if isinstance(node, ast.ImportFrom) else alias.name
               for node in ast.walk(tree) if isinstance(node, (ast.Import, ast.ImportFrom))
               for alias in node.names}
    assert imports <= {'copy', 'http.client', 'json', 'os', 'pathlib', 're', 'socket', 'stat', 'sys', 'urllib.parse'}
    requests = []
    for node in ast.walk(tree):
        if not isinstance(node, ast.Call):
            continue
        assert not (isinstance(node.func, ast.Name) and node.func.id in {'open', 'exec', 'eval', 'compile', '__import__'})
        if isinstance(node.func, ast.Attribute):
            assert node.func.attr not in {'write', 'write_text', 'write_bytes', 'mkdir', 'makedirs', 'unlink', 'remove',
                                          'rmdir', 'rename', 'system', 'popen', 'spawn', 'execv', 'execve'}
            assert ast.unparse(node.func) != 'os.replace'
            if node.func.attr == 'request': requests.append(node)
            if ast.unparse(node.func) == 'os.open':
                flags = ast.unparse(node.args[1])
                assert 'os.O_RDONLY' in flags
                assert not any(flag in flags for flag in ('O_WRONLY', 'O_RDWR', 'O_CREAT', 'O_TRUNC', 'O_APPEND'))
    assert len(requests) == 1 and ast.unparse(requests[0]) == "connection.request('GET', path)"


def check_current_baseline(source):
    """The new evidence writer has no runtime or historical-journal writer."""
    tree = ast.parse(source)
    assignments = {node.targets[0].id: node.value for node in tree.body if isinstance(node, ast.Assign)
                   and len(node.targets) == 1 and isinstance(node.targets[0], ast.Name)}
    assert ast.literal_eval(assignments['NAME']) == 'delivery-current-baseline-v1'
    assert ast.literal_eval(assignments['MODULES']) == {
        'baseline_audit': 'staging-paired-readonly-audit.py',
        'baseline_custody': 'staging-paired-custody.py'}
    for node in ast.walk(tree):
        if not isinstance(node, ast.Call): continue
        call = ast.unparse(node.func)
        assert call not in {'open', 'exec', 'eval', 'compile', '__import__', 'c.Journal'}
        if isinstance(node.func, ast.Attribute):
            assert node.func.attr not in {'request', 'run', 'Popen', 'unlink', 'remove', 'rmdir',
                                          'rename', 'replace', 'system', 'popen', 'execv', 'execve',
                                          'write_text', 'write_bytes'}
        if call == 'os.open':
            assert 'O_RDONLY' in ast.unparse(node.args[1])
        if call == 'os.mkdir':
            assert ast.unparse(node.args[0]) == 'NAME'
    for name in ('origin', 'load_seal', 'verify_retention'):
        function = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == name)
        assert not any(isinstance(node, ast.Call) and ast.unparse(node.func) in
                       {'c.write_exclusive', 'os.mkdir'} for node in ast.walk(function))


def main():
    inventory = {str(path.relative_to(ROOT)) for path in (ROOT/'scripts').glob('*.py')
                 if '/var/run/docker.sock' in path.read_text() and path.name != Path(__file__).name}
    # Account explicitly for the offline argv fixture's expected socket string.
    # It substitutes both SSH and Docker with temporary executables; do not hide
    # the literal through concatenation or exclude every test from this inventory.
    assert inventory == {'scripts/staging-paired-engine.py', 'scripts/test-staging-paired-ssh-argv.py',
                         'scripts/staging-paired-readonly-audit.py'}
    source = (ROOT/'scripts/staging-paired-engine.py').read_text()
    check_engine(source)
    check_readonly_audit((ROOT/'scripts/staging-paired-readonly-audit.py').read_text())
    check_current_baseline((ROOT/'scripts/staging-delivery-current-baseline.py').read_text())
    for changed in (source.replace("'gateway': 'jeeb-staging-jeeb-gateway'", "'gateway': 'another-service'"),
                    source + "\nrequest('POST', '/unreviewed', {})\n"):
        try: check_engine(changed)
        except AssertionError: pass
        else: raise AssertionError('unsafe Engine inventory mutation accepted')
    template = ROOT/'.github/workflows/jeeb-staging-paired-activation.yml'
    document = yaml.safe_load(template.read_text())
    assert document['permissions'] == {'contents': 'read', 'actions': 'read', 'packages': 'write'}
    assert document['concurrency'] == {'group': 'jeeb-staging-jeeb-gateway', 'cancel-in-progress': False}
    assert document['jobs']['paired']['timeout-minutes'] == 20
    assert document['jobs']['paired']['environment'] == 'staging'
    steps = document['jobs']['paired']['steps']
    build = [step for step in steps if step.get('id') == 'build']
    assert len(build) == 1 and build[0]['if'] == "inputs.operation == 'prepare'"
    assert 'run-staging-paired-activation.sh' in str(steps)
    assert not (ROOT/'docs/runbooks/jeeb-staging-paired-activation.workflow.yml').exists()
    assert document['on']['workflow_dispatch']['inputs']['operation']['options'] == ['prepare', 'activate']
    assert 'staging-paired-source-guard.sh' in str(steps)
    assert 'GITHUB_TRIGGERING_ACTOR' in str(steps)
    print('Paired Python Engine inventory and fixed registered workflow validated.')


if __name__ == '__main__':
    try: main()
    except Exception:
        print('Paired activation source policy failed closed.', file=sys.stderr)
        sys.exit(1)
