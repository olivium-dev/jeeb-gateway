#!/usr/bin/env python3
"""Offline adversarial checks for the pinned wallet-slice guard."""

import copy
import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("wallet_contract", ROOT / "scripts/check-wallet-contract.py")
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)
BASE = json.loads((ROOT / "src/JeebGateway/contracts/wallet-service.openapi.json").read_text())


class ContractTests(unittest.TestCase):
    def test_reviewed_slice_passes(self):
        guard.validate(BASE)

    def test_placeholder_is_rejected(self):
        with self.assertRaises(ValueError):
            guard.validate({"paths": {}})

    def test_every_required_operation_is_enforced(self):
        for path, method in guard.REQUIRED.items():
            with self.subTest(path=path):
                changed = copy.deepcopy(BASE)
                del changed["paths"][path][method]
                with self.assertRaises(ValueError):
                    guard.validate(changed)

    def test_missing_responses_is_rejected(self):
        changed = copy.deepcopy(BASE)
        changed["paths"]["/Transaction/validate"]["post"]["responses"] = {}
        with self.assertRaises(ValueError):
            guard.validate(changed)

    def test_fee_default_cannot_silently_change(self):
        for value in [False, None, "true", 1]:
            with self.subTest(value=value):
                changed = copy.deepcopy(BASE)
                changed["components"]["schemas"]["TransactionRequest"]["properties"]["applyConfiguredFees"]["default"] = value
                with self.assertRaises(ValueError):
                    guard.validate(changed)

    def test_additive_paths_are_allowed(self):
        changed = copy.deepcopy(BASE)
        changed["paths"]["/new-optional-operation"] = {"get": {"responses": {"200": {"description": "OK"}}}}
        guard.validate(changed)

    def test_compatibility_checker_rejects_removal_but_allows_additions(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory) / "base.json"
            candidate = Path(directory) / "candidate.json"
            operation = {"responses": {"200": {"description": "OK"}}}
            base.write_text(json.dumps({"paths": {"/existing": {"get": operation}}}))
            for paths, expected in [
                ({"/existing": {"get": operation}}, 0),
                ({"/existing": {"get": operation}, "/added": {"post": operation}}, 0),
                ({"/existing": {"post": operation}}, 1),
                ({"/existing": {"get": False}}, 1),
                ({"/existing": {"get": {}}}, 1),
                ({"/existing": {"get": {"responses": {}}}}, 1),
                ({"/existing": {"get": {"responses": {"200": False}}}}, 1),
                ({"/existing": {"get": {"responses": {"200": {}}}}}, 1),
                ({"/existing": {"get": {"responses": {"x-metadata": False}}}}, 1),
                ({"/existing": {"get": {"responses": {"200": {"description": "OK"}, "x-metadata": False}}}}, 0),
                ({"/existing": {"get": {"responses": {"default": {"$ref": "#/components/responses/Error"}}}}}, 0),
            ]:
                candidate.write_text(json.dumps({"paths": paths}))
                result = subprocess.run(["bash", str(ROOT / "scripts/check-openapi-path-method-compatibility.sh"),
                                         str(base), str(candidate)], capture_output=True, text=True)
                if expected == 0:
                    self.assertEqual(0, result.returncode, result.stderr)
                else:
                    self.assertNotEqual(0, result.returncode, result.stderr)

    def test_empty_contracts_cannot_vacuously_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "empty.json"
            path.write_text(json.dumps({"paths": {}}))
            result = subprocess.run(["bash", str(ROOT / "scripts/check-openapi-path-method-compatibility.sh"),
                                     str(path), str(path)], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)

    def test_missing_base_is_not_a_successful_boundary_check(self):
        for base in ["", "0" * 40, "main;exit 0"]:
            result = subprocess.run(["bash", str(ROOT / "scripts/check-wallet-boundary-compatibility.sh"), base],
                                    capture_output=True, text=True)
            self.assertEqual(64, result.returncode)


if __name__ == "__main__":
    unittest.main()
