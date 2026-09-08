"""Exercise the actual workflow wrapper through OpenSSH's remote-shell join.

No network, Docker daemon, login or real credentials. Both POSIX sh and Bash
execute the joined command, and a fake Docker checks the received argv/stdin.
"""
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / '.github/workflows/jeeb-staging-paired-activation.yml'
OVERRIDES = ('DOCKER_HOST', 'DOCKER_CONTEXT', 'DOCKER_TLS_VERIFY', 'DOCKER_CERT_PATH', 'DOCKER_API_VERSION')
SOCKET = 'unix:///var/run/docker.sock'
REGISTRY = '.jeeb-deploy/paired-runtime-123-1'
IMAGE = 'ghcr.io/olivium-dev/jeeb-gateway@sha256:' + 'a' * 64


def wrapper():
    matches = re.findall(r'(?ms)^          local_docker\(\) \{\n.*?^          \}', WORKFLOW.read_text())
    if len(matches) != 1:
        raise AssertionError('expected exactly one workflow Docker wrapper')
    return textwrap.dedent(matches[0])


class SSHArgumentTests(unittest.TestCase):
    def invoke(self, arguments, *, shell='/bin/sh', function=None, registry=REGISTRY, stdin=''):
        with tempfile.TemporaryDirectory(prefix='paired-ssh-argv-') as directory:
            target = Path(directory)
            # OpenSSH does not preserve the local argument array. Its remote
            # command is a space-joined string interpreted by the login shell.
            ssh = target / 'ssh'
            ssh.write_text('#!' + sys.executable + '\n' + textwrap.dedent('''
                import os, subprocess, sys
                if sys.argv[1] != 'jeeb-staging': sys.exit(70)
                command = ' '.join(sys.argv[2:])
                sys.exit(subprocess.call([os.environ['FIXTURE_SHELL'], '-c', command]))
            '''))
            docker = target / 'docker'
            docker.write_text('#!' + sys.executable + '\n' + textwrap.dedent(r'''
                import json, os, sys
                argv = sys.argv[1:]
                args = argv[4:]
                if args and args[0] == 'info' and args != ['info', '--format', '{{json .}}']:
                    sys.stderr.write('docker info accepts no arguments\n')
                    sys.exit(64)
                private_input = sys.stdin.read() if '--password-stdin' in args else ''
                overrides = ['DOCKER_HOST', 'DOCKER_CONTEXT', 'DOCKER_TLS_VERIFY', 'DOCKER_CERT_PATH', 'DOCKER_API_VERSION']
                print(json.dumps({'argv': argv, 'overrides_absent': all(key not in os.environ for key in overrides),
                                  'stdin_matches': private_input == 'synthetic-only-fixture-input'}))
            '''))
            ssh.chmod(0o700); docker.chmod(0o700)
            environment = dict(os.environ, PATH=directory + os.pathsep + os.environ['PATH'],
                               REMOTE_PAIRED_ROOT=registry, FIXTURE_SHELL=shell)
            environment.update({key: 'must-be-unset' for key in OVERRIDES})
            script = 'set -euo pipefail\n' + (wrapper() if function is None else function) + '\nlocal_docker "$@"\n'
            return subprocess.run(['bash', '-c', script, 'fixture', *arguments], input=stdin,
                                  capture_output=True, text=True, env=environment, timeout=10)

    def test_actual_workflow_info_inspect_digest_registry_and_stdin(self):
        for shell in ('/bin/sh', '/bin/bash'):
            for arguments, stdin in ((['info', '--format', '{{json .}}'], ''),
                                     (['image', 'inspect', IMAGE, '--format', '{{json .}}'], ''),
                                     (['image', 'pull', IMAGE], ''),
                                     (['login', 'ghcr.io', '-u', 'oudaykhaled', '--password-stdin'], 'synthetic-only-fixture-input')):
                with self.subTest(shell=shell, command=arguments[0]):
                    result = self.invoke(arguments, shell=shell, stdin=stdin)
                    self.assertEqual(0, result.returncode, result.stderr)
                    value = json.loads(result.stdout)
                    self.assertEqual(['--host', SOCKET, '--config', REGISTRY, *arguments], value['argv'])
                    self.assertTrue(value['overrides_absent'])
                    self.assertEqual(bool(stdin), value['stdin_matches'])
                    self.assertNotIn('synthetic-only-fixture-input', result.stdout + result.stderr)

    def test_printable_shell_metacharacters_roundtrip_without_evaluation(self):
        # Stress serialization without widening production's fixed path gate.
        arguments = ['image', 'inspect', IMAGE, '--format', '{{json .Config.Labels}}', "literal ' $HOME ; & ( ) [ ] * ? \\"]
        registry = "fixture path ' $HOME ; $(false)"
        for shell in ('/bin/sh', '/bin/bash'):
            result = self.invoke(arguments, shell=shell, registry=registry)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(['--host', SOCKET, '--config', registry, *arguments], json.loads(result.stdout)['argv'])

    def test_old_array_forwarding_reproduces_actual_failure(self):
        old = '''local_docker() {
          ssh jeeb-staging env -u DOCKER_HOST -u DOCKER_CONTEXT -u DOCKER_TLS_VERIFY -u DOCKER_CERT_PATH -u DOCKER_API_VERSION docker --host ''' + SOCKET + ''' --config "$REMOTE_PAIRED_ROOT" "$@"
        }'''
        for shell in ('/bin/sh', '/bin/bash'):
            result = self.invoke(['info', '--format', '{{json .}}'], shell=shell, function=old)
            self.assertEqual(64, result.returncode)
            self.assertEqual('docker info accepts no arguments\n', result.stderr)
            self.assertEqual('', result.stdout)


if __name__ == '__main__':
    unittest.main()
