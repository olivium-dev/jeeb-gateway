#!/usr/bin/env python3
"""Regression for the accepted-Spec / not-yet-started Swarm updater window."""
import copy
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("migration_fixtures", Path(__file__).with_name("test-staging-chat-private-migration.py"))
fixtures = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixtures)
m = fixtures.m


class RolloutPendingTests(unittest.TestCase):
    def test_accepted_post_missing_status_then_completed_verifies_once_without_resubmission(self):
        original = fixtures.service("gateway")
        candidate = m.gateway_candidate(original["Spec"])
        pending = copy.deepcopy(original)
        pending["Spec"] = candidate
        pending["Version"]["Index"] += 1
        pending.pop("UpdateStatus")
        updating = copy.deepcopy(pending)
        updating["UpdateStatus"] = {"State": "updating"}
        completed = copy.deepcopy(pending)
        completed["UpdateStatus"] = {"State": "completed"}
        observations = iter([original, original, pending, updating, completed])
        latest = None
        def get(kind, value):
            nonlocal latest
            latest = next(observations)
            return copy.deepcopy(latest)
        def strict_verify(role, expected, private):
            # Reproduce the existing completed-state/retention precondition.
            m.require(latest.get("UpdateStatus", {}).get("State") == "completed")
            self.assertEqual(candidate, expected)
            return {"verified": True}
        runtime = m.Runtime(Mock(), m.manifest(), "a" * 64, Path("/unused"), "b" * 64)
        connection = Mock()
        connection.getresponse.return_value.status = 200
        connection.getresponse.return_value.read.return_value = b"{}"
        with (patch.object(m, "engine_get", side_effect=get), patch.object(m, "Engine", return_value=connection),
              patch.object(runtime, "verify", side_effect=strict_verify) as verify, patch.object(m.time, "sleep") as sleep):
            runtime.inspect("gateway")
            runtime.submit("gateway", original, candidate)
            self.assertEqual({"verified": True}, runtime.wait("gateway", candidate, True))
            self.assertEqual(2, sleep.call_count)
            verify.assert_called_once_with("gateway", candidate, True)
        self.assertEqual(1, connection.request.call_count)
        self.assertEqual("POST", connection.request.call_args.args[0])

    def test_only_missing_empty_or_updating_states_wait_with_exact_identity_and_spec(self):
        original = fixtures.service("gateway")
        expected = original["Spec"]
        for status in (None, {}, {"State": ""}, {"State": "updating"}):
            runtime = m.Runtime(Mock(), m.manifest(), "a" * 64, Path("/unused"), "b" * 64)
            current = copy.deepcopy(original)
            current["UpdateStatus"] = status
            observe = Mock()
            with (patch.object(m, "engine_get", return_value=current) as get,
                  patch.object(runtime, "verify") as verify, patch.object(m.time, "sleep") as sleep):
                with self.assertRaises(ValueError): runtime.wait("gateway", expected, True, observe=observe)
                self.assertEqual(40, get.call_count)
                self.assertEqual(40, observe.call_count)
                self.assertEqual(39, sleep.call_count)
                verify.assert_not_called()
        for failure in ("paused", "rollback_started", "rollback_completed", "failed", "unknown", "malformed", "wrong-spec", "replaced-id", "completed-guard"):
            runtime = m.Runtime(Mock(), m.manifest(), "a" * 64, Path("/unused"), "b" * 64)
            current = copy.deepcopy(original)
            current["UpdateStatus"] = {"State": failure}
            if failure == "malformed": current["UpdateStatus"] = []
            if failure == "wrong-spec": current["Spec"]["Name"] = "other"; current.pop("UpdateStatus")
            if failure == "replaced-id": current["ID"] = "r" * 25; current.pop("UpdateStatus")
            if failure == "completed-guard": current["UpdateStatus"] = {"State": "completed"}
            with (patch.object(m, "engine_get", side_effect=[original, current]) as get,
                  patch.object(runtime, "verify", side_effect=ValueError("strict guard")) as verify,
                  patch.object(m.time, "sleep") as sleep):
                runtime.inspect("gateway")
                with self.assertRaises(ValueError): runtime.wait("gateway", expected, True)
                self.assertEqual(2, get.call_count)
                self.assertEqual(1 if failure == "completed-guard" else 0, verify.call_count)
                sleep.assert_not_called()
        with patch.object(runtime, "inspect") as inspect:
            with self.assertRaises(ValueError):
                runtime.wait("gateway", expected, True, observe=Mock(side_effect=ValueError("gateway witness drift")))
            inspect.assert_not_called()


if __name__ == "__main__":
    unittest.main()
