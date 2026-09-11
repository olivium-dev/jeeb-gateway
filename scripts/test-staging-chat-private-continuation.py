#!/usr/bin/env python3
"""Forward-only continuation, using real candidate/runtime/custody code offline."""
import copy
import contextlib
import fcntl
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("migration_fixtures", Path(__file__).with_name("test-staging-chat-private-migration.py"))
f = importlib.util.module_from_spec(spec)
spec.loader.exec_module(f)
m, c = f.m, f.c
reader = f.load("continuation_baseline_reader", "staging-delivery-current-baseline.py")


class ContinuationTests(unittest.TestCase):
    def test_main_dispatches_continuation_once_only_inside_existing_owner_lock(self):
        baseline, runtime = Mock(c=c), Mock()
        locked = []
        @contextlib.contextmanager
        def held(home, owner):
            self.assertEqual("c" * 64, owner)
            locked.append(True)
            try: yield
            finally: locked.pop()
        def continued(actual_runtime, source, run, attempt, seal):
            self.assertEqual([True], locked)
            self.assertIs(runtime, actual_runtime)
            self.assertEqual(("e" * 40, "900", "1", "b" * 64), (source, run, attempt, seal))
            return {"migration": "complete"}
        output = io.StringIO()
        with (patch.object(m.sys, "argv", ["helper", "continue-private", "e" * 40, "900", "1", "b" * 64]),
              patch.dict(m.sys.modules, {"migration_baseline": baseline}), patch.object(c, "held_lock", side_effect=held) as lock,
              patch.object(m.secrets, "token_hex", return_value="c" * 64), patch.object(m, "Runtime", return_value=runtime) as constructor,
              patch.object(m, "continue_private", side_effect=continued) as continuation,
              patch.object(m, "Journal", side_effect=AssertionError("must not construct Journal in dispatch")),
              patch.object(m, "migrate", side_effect=AssertionError("must not migrate")), contextlib.redirect_stdout(output)):
            self.assertEqual(0, m.main())
        self.assertEqual(1, lock.call_count)
        self.assertEqual(1, constructor.call_count)
        self.assertEqual(1, continuation.call_count)
        self.assertEqual([], locked)
        self.assertEqual("complete", json.loads(output.getvalue())["migration"])

    @contextlib.contextmanager
    def held_fixture(self, runtime):
        root = runtime.home / ".jeeb-deploy/locks"
        owner = root / "jeeb-staging-gateway.owner"
        with open(root / "jeeb-staging-gateway.lock", "rb") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            owner.write_text(runtime.owner + "\n")
            owner.chmod(0o600)
            try: yield
            finally:
                if owner.exists(): owner.unlink()

    def fixture(self, temporary, post_failure=False):
        helper = f.DiagnosticTests()
        home, _ = helper.make_home(temporary)
        baseline, values, journal = helper.recorded_fixture(home)
        baseline.private_raw.side_effect = reader.private_raw
        runtime = m.Runtime(baseline, m.manifest(), "c" * 64, home, "b" * 64)
        connection = Mock()
        connection.getresponse.return_value.status = 200
        connection.getresponse.return_value.read.return_value = b"{}"
        original_chat = copy.deepcopy(values[("service", m.SERVICES["chat"])])
        def request(method, path, body, headers):
            self.assertEqual("POST", method)
            self.assertEqual("/v1.52/services/" + original_chat["ID"] + "/update?version=10&registryAuthFrom=spec", path)
            self.assertTrue((journal.path / "03-chat-submission-pending.json").exists())
            self.assertEqual(m.chat_candidate(original_chat["Spec"]), json.loads(body))
            if post_failure: raise ConnectionResetError(f.SECRET)
            chat = values[("service", m.SERVICES["chat"])]
            chat["PreviousSpec"] = copy.deepcopy(chat["Spec"])
            chat["Spec"] = json.loads(body)
            chat["Version"]["Index"] += 1
            chat["Endpoint"] = copy.deepcopy(chat["Spec"]["EndpointSpec"])
            task = values[("tasks", chat["ID"])][0]
            task["ID"] = "z" * 25
            task["Spec"]["ContainerSpec"] = copy.deepcopy(chat["Spec"]["TaskTemplate"]["ContainerSpec"])
        connection.request.side_effect = request
        return helper, home, baseline, values, journal, runtime, connection

    def run_continuation(self, runtime, values, connection, probe=None, source="e" * 40, run="900", attempt="1"):
        with (self.held_fixture(runtime), patch.object(m, "engine_get", side_effect=lambda kind, value: copy.deepcopy(values[(kind, value)])),
              patch.object(m, "Engine", return_value=connection), patch.object(m, "http_probe", side_effect=probe),
              patch.object(m.Journal, "begin", side_effect=AssertionError("must not begin")),
              patch.object(m, "migrate", side_effect=AssertionError("must not migrate"))):
            return m.continue_private(runtime, source, run, attempt, "b" * 64)

    def test_success_preserves_original_bytes_and_records_new_truthful_provenance(self):
        with tempfile.TemporaryDirectory() as temporary:
            _, home, baseline, values, journal, runtime, connection = self.fixture(temporary)
            original = {p.name: p.read_bytes() for p in journal.path.iterdir()}
            result = self.run_continuation(runtime, values, connection)
            self.assertEqual("complete", result["migration"])
            self.assertEqual(1, connection.request.call_count)
            for name, raw in original.items(): self.assertEqual(raw, (journal.path / name).read_bytes())
            previous = json.loads(original["01-gateway-submission-pending.json"])
            for index, phase in enumerate(m.PHASES[2:], 2):
                value = json.loads((journal.path / f"{index:02d}-{phase}.json").read_text())
                self.assertEqual(previous["metadata"], value["metadata"])
                self.assertEqual("34605416479", value["metadata"]["runId"])
                self.assertEqual(m.digest(previous), value["previousSha256"])
                provenance = value["evidence"]["continuation"]
                self.assertEqual(("e" * 40, "900", "1"), (provenance["sourceCommit"], provenance["runId"], provenance["attempt"]))
                self.assertGreater(provenance["observedAtUnix"], 0)
                self.assertFalse(provenance["identityActivationAuthorized"])
                self.assertNotIn(f.SECRET, json.dumps(value))
                previous = value
            before_retry = {p.name: p.read_bytes() for p in journal.path.iterdir()}
            with self.assertRaises(ValueError): self.run_continuation(runtime, values, connection)
            self.assertEqual(1, connection.request.call_count)
            self.assertEqual(before_retry, {p.name: p.read_bytes() for p in journal.path.iterdir()})

    def test_failed_chat_post_consumes_claim_and_never_resubmits(self):
        with tempfile.TemporaryDirectory() as temporary:
            _, _, _, values, journal, runtime, connection = self.fixture(temporary, post_failure=True)
            with self.assertRaises(ConnectionResetError): self.run_continuation(runtime, values, connection)
            self.assertTrue((journal.path / "03-chat-submission-pending.json").exists())
            self.assertFalse((journal.path / "04-chat-verified.json").exists())
            before = {p.name: p.read_bytes() for p in journal.path.iterdir()}
            with self.assertRaises(ValueError): self.run_continuation(runtime, values, connection)
            self.assertEqual(1, connection.request.call_count)
            self.assertEqual(before, {p.name: p.read_bytes() for p in journal.path.iterdir()})

    def test_post_accepted_then_readiness_or_receipt_failure_cannot_resume(self):
        for failure in ("readiness", "receipt"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temporary:
                _, _, _, values, journal, runtime, connection = self.fixture(temporary)
                originals = {p.name: p.read_bytes() for p in journal.path.iterdir()}
                real = m.Journal.advance
                def advance(instance, phase, evidence):
                    if phase == "chat-verified": raise OSError("receipt unavailable")
                    return real(instance, phase, evidence)
                if failure == "readiness":
                    with self.assertRaises(ValueError):
                        self.run_continuation(runtime, values, connection, probe=[None, ValueError("nontransient readiness")])
                else:
                    with patch.object(m.Journal, "advance", advance), self.assertRaises(OSError):
                        self.run_continuation(runtime, values, connection)
                self.assertEqual(1, connection.request.call_count)
                self.assertTrue((journal.path / "03-chat-submission-pending.json").exists())
                self.assertFalse((journal.path / "04-chat-verified.json").exists())
                self.assertFalse((journal.path / "05-complete.json").exists())
                for name, raw in originals.items(): self.assertEqual(raw, (journal.path / name).read_bytes())
                before_retry = {p.name: p.read_bytes() for p in journal.path.iterdir()}
                with self.assertRaises(ValueError): self.run_continuation(runtime, values, connection)
                self.assertEqual(1, connection.request.call_count)
                self.assertEqual(before_retry, {p.name: p.read_bytes() for p in journal.path.iterdir()})

    def test_wrong_history_or_current_proof_never_appends_or_posts(self):
        for failure in ("source", "run", "attempt", "hash", "prepared-only", "gateway-verified", "chat-claimed",
                        "gateway-spec", "previous", "chat-spec", "gateway-id", "seal", "checkpoint", "retention"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temporary:
                helper, _, baseline, values, journal, runtime, connection = self.fixture(temporary)
                if failure in ("source", "run", "attempt", "hash"):
                    def change(metadata):
                        if failure == "hash": metadata["candidateSpecSha256"]["gateway"] = "f" * 64
                        else: metadata[{"source": "sourceCommit", "run": "runId", "attempt": "attempt"}[failure]] = "2"
                    helper.rewrite_recorded_metadata(journal, change)
                if failure == "prepared-only": (journal.path / "01-gateway-submission-pending.json").unlink()
                if failure in ("gateway-verified", "chat-claimed"):
                    journal.advance("gateway-verified")
                    if failure == "chat-claimed": journal.advance("chat-submission-pending")
                gateway, chat = (values[("service", m.SERVICES[role])] for role in ("gateway", "chat"))
                if failure == "gateway-spec": gateway["Spec"]["Labels"] = {"changed": "true"}
                if failure == "previous": gateway["PreviousSpec"]["Labels"] = {"changed": "true"}
                if failure == "chat-spec": chat["Spec"]["Labels"] = {"changed": "true"}
                if failure == "gateway-id": gateway["ID"] = "r" * 25
                if failure == "seal": baseline.digest.return_value = "f" * 64
                if failure == "checkpoint": baseline.stable_runtime.side_effect = ["before", "after"]
                if failure == "retention": baseline.verify_retention.side_effect = ValueError("lock or retention failed")
                before = {p.name: p.read_bytes() for p in journal.path.iterdir()}
                with self.assertRaises((ValueError, KeyError)): self.run_continuation(runtime, values, connection)
                connection.request.assert_not_called()
                self.assertEqual(before, {p.name: p.read_bytes() for p in journal.path.iterdir()})

    def test_phase_or_runtime_drift_after_first_append_stops_before_chat_claim(self):
        for failure in ("whitespace", "gateway-task", "chat-task", "gateway-version"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temporary:
                _, _, _, values, journal, runtime, connection = self.fixture(temporary)
                real_advance = m.Journal.advance
                def advance(instance, phase, evidence):
                    value = real_advance(instance, phase, evidence)
                    if phase == "gateway-verified":
                        if failure == "whitespace":
                            path = journal.path / "00-prepared.json"
                            raw = path.read_bytes()
                            path.chmod(0o600); path.write_bytes(raw + b"\n"); path.chmod(0o400)
                        elif failure == "gateway-version":
                            values[("service", m.SERVICES["gateway"])]["Version"]["Index"] += 1
                        else:
                            role = "gateway" if failure == "gateway-task" else "chat"
                            service = values[("service", m.SERVICES[role])]
                            values[("tasks", service["ID"])][0]["ID"] = "r" * 25
                    return value
                with patch.object(m.Journal, "advance", advance), self.assertRaises(ValueError):
                    self.run_continuation(runtime, values, connection)
                connection.request.assert_not_called()
                self.assertTrue((journal.path / "02-gateway-verified.json").exists())
                self.assertFalse((journal.path / "03-chat-submission-pending.json").exists())

    def test_owner_loss_after_final_reconciliation_cannot_append_and_old_runs_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            _, home, _, values, journal, runtime, connection = self.fixture(temporary)
            real = m.recorded_candidate_reconciliation
            calls = 0
            def reconcile(*args):
                nonlocal calls
                result = real(*args)
                calls += 1
                if calls == 2: (home / ".jeeb-deploy/locks/jeeb-staging-gateway.owner").unlink()
                return result
            with patch.object(m, "recorded_candidate_reconciliation", side_effect=reconcile), self.assertRaises(FileNotFoundError):
                self.run_continuation(runtime, values, connection)
            connection.request.assert_not_called()
            self.assertFalse((journal.path / "02-gateway-verified.json").exists())
            for kwargs in ({"source": "a333ff9b96116aa4833bf0fdeec0a9720e041ce1"}, {"run": "34605416479"}, {"attempt": "2"}):
                with self.assertRaises(ValueError): self.run_continuation(runtime, values, connection, **kwargs)
            connection.request.assert_not_called()

    def test_http_failure_before_append_or_guard_drift_after_chat_claim_never_posts(self):
        for failure in ("http", "claimed-owner-loss"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temporary:
                _, home, _, values, journal, runtime, connection = self.fixture(temporary)
                real = m.Journal.advance
                def advance(instance, phase, evidence):
                    result = real(instance, phase, evidence)
                    if phase == "chat-submission-pending":
                        (home / ".jeeb-deploy/locks/jeeb-staging-gateway.owner").unlink()
                    return result
                if failure == "http":
                    with self.assertRaises(ValueError):
                        self.run_continuation(runtime, values, connection, probe=ValueError("wrong readiness"))
                    self.assertFalse((journal.path / "02-gateway-verified.json").exists())
                else:
                    with patch.object(m.Journal, "advance", advance), self.assertRaises(FileNotFoundError):
                        self.run_continuation(runtime, values, connection)
                    self.assertTrue((journal.path / "03-chat-submission-pending.json").exists())
                connection.request.assert_not_called()


if __name__ == "__main__":
    unittest.main()
