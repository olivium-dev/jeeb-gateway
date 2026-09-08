import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location('receipt', Path(__file__).with_name('staging-paired-build-receipt.py'))
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)


class BuildReceiptTests(unittest.TestCase):
    def test_own_exact_successful_artifact_and_negative_identity_digest_controls(self):
        value = {'schemaVersion': 1, 'repository': r.REPO, 'workflowPath': r.WORKFLOW,
                 'sourceCommit': 'a'*40, 'sourceTree': 'b'*40, 'image': 'ghcr.io/olivium-dev/jeeb-gateway@sha256:'+'c'*64,
                 'runId': '123', 'attempt': '1'}
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, 'w') as archive: archive.writestr('gateway-build-receipt.json', json.dumps(value))
        raw = buffer.getvalue()
        run = {'head_sha': 'a'*40, 'head_branch': 'main', 'path': r.WORKFLOW, 'event': 'workflow_dispatch',
               'status': 'completed', 'conclusion': 'success', 'repository': {'full_name': r.REPO},
               'actor': {'login': 'oudaykhaled'}, 'triggering_actor': {'login': 'oudaykhaled'}, 'run_attempt': 1}
        artifact = {'name': r.NAME, 'expired': False, 'id': 7, 'size_in_bytes': len(raw), 'digest': 'sha256:'+hashlib.sha256(raw).hexdigest()}
        def invoke(run_value=run, artifact_value=artifact, payload=raw):
            def api(path):
                if path == 'actions/runs/123': return json.dumps(run_value).encode()
                if path == 'actions/runs/123/artifacts?per_page=100': return json.dumps({'total_count':1, 'artifacts':[artifact_value]}).encode()
                if path == 'actions/artifacts/7/zip': return payload
                raise AssertionError('unexpected or cross-repo API')
            with patch.object(r, 'api', side_effect=api): return r.consume('123', 'a'*40, 'b'*40)
        self.assertEqual(value, invoke())
        for field, changed in (('head_sha','d'*40), ('conclusion','failure'), ('path','other'),
                               ('repository',{'full_name':'other/repo'}), ('triggering_actor',{'login':'other'}), ('run_attempt',2)):
            with self.subTest(field=field), self.assertRaises(ValueError): invoke({**run, field:changed})
        with self.assertRaises(ValueError): invoke(artifact_value={**artifact,'digest':'sha256:'+'0'*64})
        with self.assertRaises(ValueError): invoke(artifact_value={**artifact,'expired':True})


if __name__ == '__main__': unittest.main()
