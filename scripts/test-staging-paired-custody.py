import copy
from datetime import datetime, timezone
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SOURCE = Path(__file__).with_name('staging-paired-custody.py')
spec = importlib.util.spec_from_file_location('custody', SOURCE)
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)


class CustodyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.home = Path(self.temp.name)
        (self.home / '.jeeb-deploy').mkdir(mode=0o700)
        self.image = {'Id': 'sha256:' + 'b' * 64, 'RepoDigests': ['ghcr.io/olivium-dev/delivery-service@sha256:' + 'c' * 64],
                      'Config': {'Labels': {'org.opencontainers.image.revision': 'd' * 40, 'jeeb.source.tree': 'e' * 40,
                                           'org.opencontainers.image.source': 'https://github.com/olivium-dev/delivery-service'}}}
        self.receipt = dict(schemaVersion=1, repository='olivium-dev/delivery-service',
            workflowPath='.github/workflows/jeeb-staging-paired-prepare.yml', sourceCommit='d' * 40,
            sourceTree='e' * 40, probeHelperBlobSha='f' * 40, runId='123', attempt='1',
            imageId=self.image['Id'], imageDigest=self.image['RepoDigests'][0], daemonId='daemon-fixture',
            sshUid=os.getuid(), canonicalHome=str(self.home), receiptNonce='a' * 64,
            createdAt='2026-09-08T06:00:00Z', expiresAt='2026-09-08T06:15:00Z')
        self.now = datetime(2026, 9, 8, 6, 5, tzinfo=timezone.utc).timestamp()
        self.expected = {k: self.receipt[k] for k in ('sourceCommit', 'sourceTree', 'probeHelperBlobSha', 'runId', 'attempt', 'imageDigest')}

    def validate(self, receipt=None, image=None, now=None):
        c.validate_receipt(receipt or self.receipt, self.expected, image or self.image,
                           'daemon-fixture', self.home, self.now if now is None else now)

    def test_exact_receipt_positive_and_expiry_future_source_identity_negative(self):
        self.validate()
        cases = {'sourceCommit': 'a' * 40, 'sourceTree': 'a' * 40, 'daemonId': 'wrong',
                 'sshUid': os.getuid() + 1, 'canonicalHome': '/other', 'workflowPath': 'wrong',
                 'createdAt': '2026-09-08T06:10:00Z', 'expiresAt': '2026-09-08T06:16:00Z',
                 'runId': '0123', 'receiptNonce': '../x', 'imageId': 'sha256:' + 'a' * 64}
        for field, value in cases.items():
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.validate({**self.receipt, field: value})
        with self.assertRaises(ValueError): self.validate(now=c.utc(self.receipt['expiresAt']))
        image = copy.deepcopy(self.image)
        image['Config']['Labels']['jeeb.source.tree'] = 'a' * 40
        with self.assertRaises(ValueError): self.validate(image=image)

    def metadata(self):
        result = dict(secretId='s' * 25, receiptNonce='a' * 64, gatewayBuildRun='122', gatewayBuildAttempt='1')
        for role, repo in (('gateway', 'jeeb-gateway'), ('delivery', 'delivery-service')):
            result.update({role+'Source': 'd'*40, role+'Tree': 'e'*40,
                role+'Image': 'ghcr.io/olivium-dev/'+repo+'@sha256:'+'c'*64,
                role+'Run': '123', role+'Attempt': '1', role+'ServiceId': role[0]*25, role+'Version': 7})
        return result

    def test_append_only_journal_reentry_phase_skip_and_secret_fields_rejected(self):
        journal = c.Journal(self.home)
        with self.assertRaises(ValueError): journal.begin({**self.metadata(), 'DATABASE_URL': 'forbidden'})
        journal.begin(self.metadata())
        with self.assertRaises(FileExistsError): journal.begin(self.metadata())
        with self.assertRaises(ValueError): journal.advance('delivery-submission-pending')
        for phase in c.PHASES[1:]: journal.advance(phase)
        with self.assertRaises(ValueError): journal.advance('complete')
        self.assertEqual(6, len(list(journal.path.iterdir())))
        self.assertTrue((journal.claims / ('a' * 64 + '.json')).exists())

    def test_symlink_writable_duplicate_json_and_hardlink_rejected(self):
        root = c.custody_root(self.home)
        file = root / 'fixture.json'
        c.write_exclusive(file, {'a': 1})
        os.chmod(file, 0o600)
        with self.assertRaises(ValueError): c.read_private(file, 0o400)
        os.chmod(file, 0o400)
        link = root / 'link.json'
        link.symlink_to(file)
        with self.assertRaises(OSError): c.read_private(link, 0o400)
        os.link(file, root / 'hard.json')
        with self.assertRaises(ValueError): c.read_private(file, 0o400)
        duplicate = root / 'duplicate.json'
        duplicate.write_text('{"a":1,"a":2}')
        duplicate.chmod(0o400)
        with self.assertRaises(ValueError): c.read_private(duplicate, 0o400)

    def test_real_flock_stale_owner_and_live_holder(self):
        lockroot = self.home / '.jeeb-deploy/locks'
        lockroot.mkdir(mode=0o700)
        owner = 'd' * 64
        (lockroot/'jeeb-staging-gateway.owner').write_text(owner+'\n')
        (lockroot/'jeeb-staging-gateway.owner').chmod(0o600)
        lock = lockroot/'jeeb-staging-gateway.lock'
        lock.touch(mode=0o600)
        with self.assertRaises(ValueError): c.assert_shared_lock(self.home, owner)
        process = subprocess.Popen(['flock', str(lock), 'bash', '-c', 'echo ready; read -r stop'],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        try:
            self.assertEqual('ready\n', process.stdout.readline())
            c.assert_shared_lock(self.home, owner)
            with self.assertRaises(ValueError): c.assert_shared_lock(self.home, 'e'*64)
        finally:
            process.communicate('stop\n', timeout=5)

    def test_strict_holder_never_overwrites_stale_owner_or_symlink(self):
        with tempfile.TemporaryDirectory(dir=Path.home()) as temporary:
            home = Path(temporary)
            owner = 'f' * 64
            with c.held_lock(home, owner):
                locks = home/'.jeeb-deploy/locks'
                self.assertEqual(owner+'\n', (locks/'jeeb-staging-gateway.owner').read_text())
                inode = (locks/'jeeb-staging-gateway.lock').stat().st_ino
                with self.assertRaises(BlockingIOError):
                    with c.held_lock(home, 'e'*64): pass
            self.assertFalse((locks/'jeeb-staging-gateway.owner').exists())
            stale = locks/'jeeb-staging-gateway.owner'
            stale.write_text('stale\n'); stale.chmod(0o600)
            with self.assertRaises(FileExistsError):
                with c.held_lock(home, owner): pass
            self.assertEqual('stale\n', stale.read_text())
            self.assertEqual(inode, (locks/'jeeb-staging-gateway.lock').stat().st_ino)
            stale.unlink()
            lock = locks/'jeeb-staging-gateway.lock'
            lock.rename(home/'outside')
            lock.symlink_to(home/'outside')
            with self.assertRaises(OSError):
                with c.held_lock(home, owner): pass


if __name__ == '__main__': unittest.main()
